using System;
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

    private sealed class PendingCommand(byte command, byte sub, TaskCompletionSource<byte[]> tcs)
    {
        public byte Command { get; } = command;
        public byte Sub { get; } = sub;
        public TaskCompletionSource<byte[]> Tcs { get; } = tcs;
    }

    private readonly ILogger? _logger;
    private readonly BluetoothLEDevice _device;
    private readonly ushort _productId;
    private readonly bool _needsHostPair;
    private readonly byte[] _hostMac;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _rumbleWake = new(0, 1);
    private readonly List<(GattCharacteristic Characteristic, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler)> _subscriptions = [];

    private PendingCommand? _pendingCommand;
    private GattCharacteristic? _inputChar;
    private GattCharacteristic? _commandChar;
    private GattCharacteristic? _responseChar;
    private GattCharacteristic? _rumbleChar;
    private GattCharacteristic? _audioOutputChar;
    private GattCharacteristic? _audioCommandChar;
    private GattDeviceService? _service;
    private CancellationTokenSource? _rumbleCts;
    private Task? _rumbleTask;
    private volatile bool _rumbleOn;
    private volatile bool _headsetConnected;
    private long _lastAudioWriteTicks;
    private int _silenceCount = 3;
    private int _rumbleCounter;
    private bool _disposed;

    public ushort ProductId => _productId;
    public byte[] Mac { get; }
    public bool IsUsb => false;
    public bool SupportsBleAudio => _audioOutputChar != null && _audioCommandChar != null;
    public bool HasHeadsetJackDetect => _productId == Ns2Protocol.PidPro2;
    public bool IsHeadsetConnected => _headsetConnected;

    public event EventHandler<byte[]>? InputReportReceived;
    public event EventHandler? LinkDropped;
    public event EventHandler<bool>? HeadsetStateChanged;

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
        if (_productId == Ns2Protocol.PidPro2)
        {
            _audioOutputChar = chars.FirstOrDefault(c => c.Uuid == Ns2Protocol.AudioOutputCharPro2);
            _audioCommandChar = chars.FirstOrDefault(c => c.Uuid == Ns2Protocol.AudioCommandCharPro2);
        }

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

        // Subscribe ONLY to the command response characteristic during handshake;
        // aux notify streams (0x000D, 0x002D) emit counter-prefixed packets that
        // can collide with command IDs if inspected as command replies.
        await SubscribeAsync(_responseChar, OnCommandResponse, ct);
        Log($"Subscribed response handle 0x{_responseChar.AttributeHandle:X4} ({_responseChar.Uuid}).");

        // Unpaired advertisement link: bond to the host first (Switch2Connect's
        // pair()). Pro 2 answers query commands unbonded but goes silent on the
        // write/config commands (0A:08 onwards) — the MAC+LTK exchange must
        // happen before the init sequence, not after it.
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

        await RunInitSequenceAsync(ct);

        // Joy-Con 2 + GameCube only (not Pro 2): raw write selects input format 3.
        if (_productId is Ns2Protocol.PidJoycon2Left or Ns2Protocol.PidJoycon2Right
            or Ns2Protocol.PidGameCubeNso or Ns2Protocol.BlePnpJoycon2Left or Ns2Protocol.BlePnpJoycon2Right)
        {
            var status = await _commandChar.WriteValueWithResultAsync(
                ((byte[])Ns2Protocol.InputFormat3Frame.Clone()).AsBuffer(),
                GetCommandWriteOption(_commandChar));
            if (status.Status != GattCommunicationStatus.Success)
            {
                Log($"Input format 3 write failed: {status.Status}");
            }
        }

        // Subscribe to input notifications after handshake. GameCube NSO may
        // deliver input on another notify characteristic in the SW2 service.
        await SubscribeAsync(_inputChar, OnInputNotify, ct);
        Log($"Subscribed input handle 0x{_inputChar.AttributeHandle:X4} ({_inputChar.Uuid}).");

        if (_productId == Ns2Protocol.PidPro2)
        {
            Log($"Pro 2 BLE audio characteristics: audioOut={_audioOutputChar != null}, audioCmd={_audioCommandChar != null}.");
        }

        if (_productId == Ns2Protocol.PidGameCubeNso)
        {
            foreach (var c in chars
                .Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    && !ReferenceEquals(c, _responseChar)
                    && !ReferenceEquals(c, _inputChar))
                .OrderBy(c => c.AttributeHandle))
            {
                await SubscribeAsync(c, OnInputNotify, ct);
                Log($"Subscribed GameCube extra notify handle 0x{c.AttributeHandle:X4} ({c.Uuid}).");
            }
        }

        // Event-driven rumble writer: silent on startup/idle, emits at ~15 ms
        // while active and sends up to 3 zero-amplitude stop frames when turned off.
        var rumbleTarget = _productId == Ns2Protocol.PidGameCubeNso
            ? _commandChar
            : (_rumbleChar ?? _commandChar);
        _rumbleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rumbleToken = _rumbleCts.Token;
        _rumbleTask = Task.Run(() => RunRumbleLoopAsync(rumbleTarget, rumbleToken), rumbleToken);

        Log("BLE link ready.");
    }

    private async Task RunRumbleLoopAsync(GattCharacteristic rumbleTarget, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_rumbleOn && Volatile.Read(ref _silenceCount) >= 3)
                {
                    await _rumbleWake.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var active = _rumbleOn;
                if (!active)
                {
                    if (Interlocked.Increment(ref _silenceCount) > 3)
                    {
                        continue;
                    }
                }
                else
                {
                    Volatile.Write(ref _silenceCount, 0);
                }

                await WriteCharNoResponseAsync(
                    rumbleTarget,
                    Ns2Protocol.BuildBleRumblePayload(_productId, _rumbleCounter++, active));

                var delayMs = Environment.TickCount64 - Volatile.Read(ref _lastAudioWriteTicks) < 200 ? 30 : 15;
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                Log($"Rumble write loop error: {e.Message}");
                try
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
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
                // controller ignores; only a run of consecutive failures means
                // the link itself is dead.
                Log($"Init step {i + 1}/{seq.Count} (cmd {command:X2}:{sub:X2}) failed: {e.Message}", LogLevel.Warning);
                if (++consecutiveFailures >= 3)
                {
                    throw new InvalidOperationException(
                        $"BLE init aborted: {consecutiveFailures} consecutive failures " +
                        $"(last: {command:X2}:{sub:X2} at step {i + 1}/{seq.Count}).", e);
                }
            }
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
        var op = characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        using var reg = ct.Register(op.Cancel);
        var status = await op;
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"CCCD subscribe failed ({status}) on {characteristic.Uuid}.");
        }

        characteristic.ValueChanged += handler;
        _subscriptions.Add((characteristic, handler));
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var data = new byte[buffer.Length];
        reader.ReadBytes(data);
        return data;
    }

    private void OnInputNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = ReadBuffer(args.CharacteristicValue);
        if (data.Length == 0)
        {
            return;
        }

        // GameCube NSO may emit short status/ack packets on extra notify chars.
        if (_productId == Ns2Protocol.PidGameCubeNso && data.Length < 30)
        {
            return;
        }

        if (_productId == Ns2Protocol.PidPro2 && data.Length >= 8)
        {
            var plugged = (BitConverter.ToUInt32(data, 4) & Ns2Protocol.BtnHeadset) != 0;
            UpdateHeadsetState(plugged);
        }

        InputReportReceived?.Invoke(this, data);
    }

    private void UpdateHeadsetState(bool plugged)
    {
        if (_headsetConnected == plugged)
        {
            return;
        }

        _headsetConnected = plugged;
        Log($"3.5mm headset {(plugged ? "connected" : "disconnected")}.", LogLevel.Info);
        HeadsetStateChanged?.Invoke(this, plugged);
    }

    private void OnCommandResponse(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = ReadBuffer(args.CharacteristicValue);
        if (data.Length == 0)
        {
            return;
        }

        var previewLen = Math.Min(8, data.Length);
        Log($"Response char: {Convert.ToHexString(data.AsSpan(0, previewLen))} ({data.Length}B)");

        var pending = Volatile.Read(ref _pendingCommand);
        if (pending != null &&
            data.Length >= 8 &&
            data[0] == pending.Command &&
            data[1] == 0x01 &&
            data[3] == pending.Sub)
        {
            Interlocked.CompareExchange(ref _pendingCommand, null, pending);
            pending.Tcs.TrySetResult(data);
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

    private static GattWriteOption GetCommandWriteOption(GattCharacteristic characteristic)
    {
        // Bleak's write_gatt_char prefers WriteWithResponse when the
        // characteristic supports it — match that (some controllers drop
        // WriteWithoutResponse frames mid-handshake).
        return characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
    }

    public async Task<byte[]> SendCommandAsync(byte command, byte subcommand, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_commandChar == null)
        {
            throw new InvalidOperationException("Transport not connected.");
        }

        await _commandLock.WaitAsync(ct);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingCommand(command, subcommand, tcs);
        Volatile.Write(ref _pendingCommand, pending);

        try
        {
            var frame = Ns2Protocol.BuildCommand(command, subcommand, data.Span, usb: false);
            var status = await _commandChar.WriteValueWithResultAsync(
                frame.AsBuffer(), GetCommandWriteOption(_commandChar));
            if (status.Status != GattCommunicationStatus.Success)
            {
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
            Interlocked.CompareExchange(ref _pendingCommand, null, pending);
            _commandLock.Release();
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
        var wasOn = _rumbleOn;
        _rumbleOn = on;
        if (on)
        {
            Volatile.Write(ref _silenceCount, 0);
        }
        else if (!wasOn)
        {
            return;
        }

        if (_rumbleWake.CurrentCount == 0)
        {
            try
            {
                _rumbleWake.Release();
            }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task SetPlayerLedAsync(int padId, CancellationToken ct)
    {
        await SendCommandAsync(0x09, 0x07, Ns2Protocol.BuildPlayerLedCommand(padId), ct);
    }

    public async Task<bool> WriteBleAudioCommandAsync(byte[] payload, CancellationToken ct)
    {
        return await WriteAudioNoResponseAsync(_audioCommandChar, payload, ct).ConfigureAwait(false);
    }

    public async Task<bool> WriteBleAudioOutputAsync(byte[] payload, CancellationToken ct)
    {
        Volatile.Write(ref _lastAudioWriteTicks, Environment.TickCount64);
        return await WriteAudioNoResponseAsync(_audioOutputChar, payload, ct).ConfigureAwait(false);
    }

    public Task ReinitializeUsbAudioAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<bool> WriteAudioNoResponseAsync(GattCharacteristic? characteristic, byte[] payload, CancellationToken ct)
    {
        if (characteristic == null || _disposed || ct.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            var writeOption = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;
            var op = characteristic.WriteValueWithResultAsync(
                payload.AsBuffer(), writeOption);
            using var reg = ct.Register(op.Cancel);
            var result = await op;
            return result.Status == GattCommunicationStatus.Success;
        }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested && !_disposed)
            {
                Log($"Audio write failed on {characteristic.Uuid}: {e.Message}");
            }
            return false;
        }
    }

    private async Task WriteCharNoResponseAsync(GattCharacteristic characteristic, byte[] data)
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
        _rumbleCts?.Cancel();

        // Give an in-flight rumble write time to finish before the
        // service/device is closed underneath it.
        if (_rumbleTask != null)
        {
            await Task.WhenAny(_rumbleTask, Task.Delay(300));
        }

        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        var connected = _device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        foreach (var (characteristic, handler) in _subscriptions)
        {
            try
            {
                characteristic.ValueChanged -= handler;
                if (connected)
                {
                    await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
            }
            catch (Exception e)
            {
                Log($"Unsubscribe failed: {e.Message}");
            }
        }
        _subscriptions.Clear();

        _service?.Dispose();
        _service = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _rumbleCts?.Cancel();
        _service?.Dispose();
        _device.Dispose();
        _commandLock.Dispose();
        _rumbleWake.Dispose();
    }
}
