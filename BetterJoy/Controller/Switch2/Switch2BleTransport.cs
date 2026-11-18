using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BetterJoy.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// BLE GATT transport for Nintendo Switch 2 controllers (Windows.Devices.Bluetooth).
/// Implements the Switch2Connect init sequence over the command/response
/// characteristics and streams input notifications.
/// </summary>
internal sealed class Switch2BleTransport : ISwitch2Transport
{
    private const int CommandTimeoutMs = 2000;

    private readonly ILogger? _logger;
    private readonly BluetoothLEDevice _device;
    private readonly ushort _productId;
    private readonly bool _needsHostPair;
    private readonly byte[] _hostMac;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _pendingCommands = new();

    private GattCharacteristic? _inputChar;
    private GattCharacteristic? _commandChar;
    private GattCharacteristic? _responseChar;
    private GattCharacteristic? _rumbleChar;
    private GattDeviceService? _service;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;
    private volatile bool _rumbleOn;
    private int _rumbleCounter;
    private bool _disposed;

    public ushort ProductId => _productId;
    public byte[] Mac { get; }

    public event EventHandler<byte[]>? InputReportReceived;
    public event EventHandler? LinkDropped;

    public Switch2BleTransport(BluetoothLEDevice device, ushort productId, byte[] mac, ILogger? logger,
        bool needsHostPair = false, byte[]? hostMac = null)
    {
        _device = device;
        _productId = productId;
        Mac = mac;
        _logger = logger;
        _needsHostPair = needsHostPair;
        _hostMac = hostMac ?? [];
    }

    private void Log(string msg, LogLevel level = LogLevel.Debug) => _logger?.Log($"[BLE] {msg}", level);

    public async Task ConnectAsync(CancellationToken ct)
    {
        // Fast connection params (~70 Hz input vs ~20 Hz default); Win11 22H2+.
        try
        {
            _device.RequestPreferredConnectionParameters(
                BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
        }
        catch (Exception e)
        {
            Log($"PreferredConnectionParameters failed: {e.Message}");
        }

        Log($"GATT services query for {_device.BluetoothAddress:X12}...");

        var servicesResult = await _device.GetGattServicesForUuidAsync(
            Ns2Protocol.ServiceGuid, BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
        {
            throw new InvalidOperationException($"NS2 GATT service not found ({servicesResult.Status}).");
        }

        _service = servicesResult.Services[0];

        var charsResult = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"GATT characteristics failed ({charsResult.Status}).");
        }

        var chars = charsResult.Characteristics;
        _inputChar = chars.FirstOrDefault(c => c.Uuid == Ns2Protocol.InputCharGuid);
        _commandChar = chars.FirstOrDefault(c => c.Uuid == Ns2Protocol.CommandCharGuid);
        _responseChar = chars.FirstOrDefault(c => c.Uuid == Ns2Protocol.ResponseCharGuid);
        _rumbleChar = chars.FirstOrDefault(c => c.Uuid == Ns2Protocol.RumbleCharFor(_productId));

        // Fallback by handle order (Switch2Connect): 2nd WriteNoResp = command,
        // 3rd Notify = response; input = remaining lowest-handle Notify.
        if (_commandChar == null || _responseChar == null || _inputChar == null)
        {
            var writeNoResp = chars
                .Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
                .OrderBy(c => c.AttributeHandle)
                .ToList();
            var notify = chars
                .Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                .OrderBy(c => c.AttributeHandle)
                .ToList();

            _commandChar ??= writeNoResp.Count > 1 ? writeNoResp[1] : null;
            _responseChar ??= notify.Count > 2 ? notify[2] : null;
            _inputChar ??= notify.FirstOrDefault(c => c != _responseChar);

            if (_inputChar != null && _commandChar != null && _responseChar != null)
            {
                Log("Resolved GATT characteristics by handle fallback.");
            }
        }

        if (_inputChar == null || _commandChar == null || _responseChar == null)
        {
            throw new InvalidOperationException("NS2 GATT characteristics not found.");
        }

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        await SubscribeAsync(_responseChar, OnCommandResponse, ct);
        Log("Subscribed to command response characteristic.");
        await SubscribeAsync(_inputChar, OnInputReport, ct);
        Log("Subscribed to input report characteristic.");

        await RunInitSequenceAsync(ct);

        // Unpaired advertisement link: bond to the host now (Switch2Connect's
        // pair()) or the controller powers off when its pairing window ends.
        if (_needsHostPair && _hostMac.Length >= 6)
        {
            try
            {
                await PairHostAsync(_hostMac, ct);
            }
            catch (Exception e)
            {
                Log($"Host pairing failed: {e.Message}", LogLevel.Warning);
            }
        }

        // Joy-Con 2 + GameCube only (not Pro 2): raw write selects input format 3.
        if (_productId is Ns2Protocol.PidJoycon2Left or Ns2Protocol.PidJoycon2Right
            or Ns2Protocol.PidGameCubeNso or Ns2Protocol.BlePnpJoycon2Left or Ns2Protocol.BlePnpJoycon2Right)
        {
            var status = await _commandChar.WriteValueWithResultAsync(
                ((byte[])Ns2Protocol.InputFormat3Frame.Clone()).AsBuffer(),
                GattWriteOption.WriteWithoutResponse);
            if (status.Status != GattCommunicationStatus.Success)
            {
                Log($"Input format 3 write failed: {status.Status}");
            }
        }

        // Keep-alive / rumble frames at ~15 ms on the rumble characteristic
        // (falls back to the command characteristic for devices without one).
        var keepAliveChar = _rumbleChar ?? _commandChar;
        _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepAliveToken = _keepAliveCts.Token;
        _keepAliveTask = Task.Run(async () =>
        {
            while (!keepAliveToken.IsCancellationRequested)
            {
                try
                {
                    await WriteCharAsync(keepAliveChar,
                        Ns2Protocol.BuildRumbleFrame(_rumbleCounter++, _rumbleOn),
                        keepAliveToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Log($"Keep-alive failed: {e.Message}");
                }
                await Task.Delay(15, keepAliveToken).ConfigureAwait(false);
            }
        }, keepAliveToken);

        Log("BLE link ready.");
    }

    private async Task RunInitSequenceAsync(CancellationToken ct)
    {
        var seq = Ns2Protocol.GetBleInitSequence(_productId);
        var consecutiveFailures = 0;
        for (var i = 0; i < seq.Count; i++)
        {
            var (command, sub, data) = seq[i];
            Log($"Init {i + 1}/{seq.Count}: cmd {command:X2}:{sub:X2}");
            try
            {
                await SendCommandAsync(command, sub, data, ct);
                consecutiveFailures = 0;
            }
            catch (Exception e)
            {
                // Switch2Connect tolerates individual init commands the
                // controller ignores (e.g. Pro 2 and 0A:08); only a run of
                // consecutive failures means the link itself is dead.
                Log($"Init step {i + 1}/{seq.Count} (cmd {command:X2}:{sub:X2}) failed: {e.Message}", LogLevel.Warning);
                if (++consecutiveFailures >= 3)
                {
                    throw new InvalidOperationException(
                        $"BLE init aborted: {consecutiveFailures} consecutive failures " +
                        $"(last: {command:X2}:{sub:X2} at step {i + 1}/{seq.Count}).", e);
                }
            }
        }

        // Player LED off until the device is registered and assigned a pad id;
        // non-critical, so a missed ack is only a warning.
        Log("Init: player LED off (cmd 09:07)");
        try
        {
            await SendCommandAsync(0x09, 0x07, new byte[] { 0x00 }.Concat(new byte[7]).ToArray(), ct);
        }
        catch (Exception e)
        {
            Log($"Init LED off (cmd 09:07) failed: {e.Message}", LogLevel.Warning);
        }
    }

    public async Task PairHostAsync(byte[] hostMac, CancellationToken ct)
    {
        Log("Host pairing (MAC + LTK)...");
        // Order mirrors Switch2Connect: SET_MAC(15:01), LTK1(15:04), LTK2(15:02), FINISH(15:03).
        await SendCommandAsync(0x15, 0x01, Ns2Protocol.BuildPairSetMac(hostMac), ct);
        await SendCommandAsync(0x15, 0x04, Ns2Protocol.PairLtk1, ct);
        await SendCommandAsync(0x15, 0x02, Ns2Protocol.PairLtk2, ct);
        await SendCommandAsync(0x15, 0x03, new byte[] { 0x00 }, ct);
        Log("Host pairing complete.");
    }

    private async Task SubscribeAsync(
        GattCharacteristic characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler,
        CancellationToken ct)
    {
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"CCCD subscribe failed ({status}) on {characteristic.Uuid}.");
        }

        characteristic.ValueChanged += handler;
    }

    private void OnInputReport(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[args.CharacteristicValue.Length];
        reader.ReadBytes(data);

        // 0xA1 = optional HID-over-GATT prefix; 0x21 = subcommand reply.
        if (data.Length == 0 || data[0] == 0x21)
        {
            return;
        }

        InputReportReceived?.Invoke(this, data);
    }

    private void OnCommandResponse(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[args.CharacteristicValue.Length];
        reader.ReadBytes(data);

        if (data.Length == 0)
        {
            return;
        }

        var previewLen = Math.Min(8, data.Length);
        Log($"Response char: {Convert.ToHexString(data.AsSpan(0, previewLen))} ({data.Length}B)");

        if (_pendingCommands.TryRemove(data[0], out var tcs))
        {
            tcs.TrySetResult(data);
        }
        else
        {
            Log($"Unmatched response (id {data[0]:X2}).");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            LinkDropped?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<byte[]> SendCommandAsync(byte command, byte subcommand, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_commandChar == null)
        {
            throw new InvalidOperationException("Transport not connected.");
        }

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[command] = tcs;

        try
        {
            await _commandLock.WaitAsync(ct);

            var frame = Ns2Protocol.BuildCommand(command, subcommand, data.Span, usb: false);
            var status = await _commandChar.WriteValueWithResultAsync(
                frame.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            if (status.Status != GattCommunicationStatus.Success)
            {
                _pendingCommands.TryRemove(command, out _);
                throw new InvalidOperationException($"Command {command:X2}:{subcommand:X2} write failed ({status.Status}).");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CommandTimeoutMs);

            await using (timeoutCts.Token.Register(() =>
            {
                Log($"Command {command:X2}:{subcommand:X2} timed out after {CommandTimeoutMs} ms.", LogLevel.Warning);
                tcs.TrySetCanceled();
            }))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _commandLock.Release();
            _pendingCommands.TryRemove(command, out _);
        }
    }

    public async Task<byte[]> ReadMemoryAsync(int length, uint address, CancellationToken ct)
    {
        var response = await SendCommandAsync(0x02, 0x04, Ns2Protocol.BuildReadMemory(length, address), ct);

        // BLE reply: resp[0] == cmdId, payload at [8:]
        var payload = response.Length > 8 ? response[8..] : [];
        if (!Ns2Protocol.TryParseReadMemoryReply(payload, length, address, out var data))
        {
            throw new InvalidOperationException($"read_memory reply mismatch at 0x{address:X6}.");
        }

        return data.ToArray();
    }

    public void SetRumble(bool on)
    {
        _rumbleOn = on;
    }

    public async Task SetPlayerLedAsync(int padId, CancellationToken ct)
    {
        await SendCommandAsync(0x09, 0x07, Ns2Protocol.BuildPlayerLedCommand(padId), ct);
    }

    private async Task WriteCharAsync(GattCharacteristic characteristic, byte[] data, CancellationToken ct)
    {
        var status = await characteristic.WriteValueWithResultAsync(
            data.AsBuffer(), GattWriteOption.WriteWithoutResponse);
        if (status.Status != GattCommunicationStatus.Success)
        {
            Log($"Write failed ({status.Status}) on {characteristic.Uuid}.");
        }
    }

    public async Task StopAsync()
    {
        _keepAliveCts?.Cancel();

        // Give an in-flight keep-alive write time to finish before the
        // service/device is closed underneath it.
        if (_keepAliveTask != null)
        {
            await Task.WhenAny(_keepAliveTask, Task.Delay(300));
        }

        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        foreach (var c in new[] { _inputChar, _responseChar })
        {
            if (c == null)
            {
                continue;
            }

            try
            {
                c.ValueChanged -= OnInputReport;
                c.ValueChanged -= OnCommandResponse;
                await c.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch (Exception e)
            {
                Log($"Unsubscribe failed: {e.Message}");
            }
        }

        _service?.Dispose();
        _service = null;

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _keepAliveCts?.Cancel();
        _service?.Dispose();
        _device.Dispose();
        _commandLock.Dispose();
    }
}
