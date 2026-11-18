using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BetterJoy.HIDApi;
using BetterJoy.Logging;
using Windows.Devices.Enumeration;
using Windows.Devices.Usb;
using Windows.Storage.Streams;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// USB transport for Nintendo Switch 2 controllers (Pro 2, GameCube NSO).
/// Input reports are read on the HID interface via HIDApi; the command channel
/// and the activation sequence run over the vendor (WinUSB) interface via
/// Windows.Devices.Usb.
/// </summary>
internal sealed class Switch2UsbTransport : ISwitch2Transport
{
    private const int CommandTimeoutMs = 2000;

    private readonly ILogger? _logger;
    private readonly Device _device;
    private readonly ushort _productId;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _pendingCommands = new();

    private UsbDevice? _commandInterface;
    private UsbBulkOutPipe? _bulkOut;
    private UsbBulkInPipe? _bulkIn;
    private Thread? _inputThread;
    private Thread? _replyThread;
    private CancellationTokenSource? _cts;
    private int _rumbleCounter;
    private bool _disposed;

    public ushort ProductId => _productId;
    public byte[] Mac { get; } = [];

    public event EventHandler<byte[]>? InputReportReceived;
    public event EventHandler? LinkDropped;

    public Switch2UsbTransport(Device device, ushort productId, ILogger? logger)
    {
        _device = device;
        _productId = productId;
        _logger = logger;
    }

    private void Log(string msg, LogLevel level = LogLevel.Debug) => _logger?.Log($"[USB] {msg}", level);

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_productId == Ns2Protocol.PidGameCubeNso)
        {
            // GC needs the 17-command activation sequence on its vendor
            // interface before it starts streaming HID input reports.
            await OpenCommandInterfaceAsync(requireBulkIn: false, ct);
            if (_bulkOut == null)
            {
                throw new InvalidOperationException("GameCube USB vendor interface not found.");
            }

            foreach (var frame in Ns2Protocol.GetGameCubeUsbInitSequence())
            {
                await WriteBulkAsync(frame, ct);
                await Task.Delay(10, ct);
            }

            // The HID interface owns input afterwards; keep the command iface
            // alive so later commands (LEDs, rumble) still have a channel.
        }
        else if (_productId == Ns2Protocol.PidPro2)
        {
            await OpenCommandInterfaceAsync(requireBulkIn: true, ct);
            if (_bulkOut == null || _bulkIn == null)
            {
                throw new InvalidOperationException("Pro 2 USB command interface not found.");
            }

            _replyThread = new Thread(() => ReadCommandReplies(_cts.Token)) { IsBackground = true };
            _replyThread.Start();

            var seq = Ns2Protocol.GetPro2UsbInitSequence();
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
                    Log($"Init step {i + 1}/{seq.Count} (cmd {command:X2}:{sub:X2}) failed: {e.Message}", LogLevel.Warning);
                    if (++consecutiveFailures >= 3)
                    {
                        throw new InvalidOperationException(
                            $"Pro 2 USB init aborted: {consecutiveFailures} consecutive failures " +
                            $"(last: {command:X2}:{sub:X2} at step {i + 1}/{seq.Count}).", e);
                    }
                }
            }
        }

        _inputThread = new Thread(() => ReadInputLoop(_cts.Token)) { IsBackground = true };
        _inputThread.Start();

        Log("USB link ready.");
    }

    private async Task OpenCommandInterfaceAsync(bool requireBulkIn, CancellationToken ct)
    {
        var selector = UsbDevice.GetDeviceSelector(Ns2Protocol.VendorId, _productId);
        var interfaces = await DeviceInformation.FindAllAsync(selector);

        foreach (var info in interfaces)
        {
            UsbDevice? usb = null;
            try
            {
                usb = await UsbDevice.FromIdAsync(info.Id);
                if (usb == null)
                {
                    continue;
                }

                var iface = usb.DefaultInterface;
                var bulkOut = iface.BulkOutPipes.Count > 0 ? iface.BulkOutPipes[0] : null;
                var bulkIn = iface.BulkInPipes.Count > 0 ? iface.BulkInPipes[0] : null;

                if (bulkOut == null || (requireBulkIn && bulkIn == null))
                {
                    usb.Dispose();
                    continue;
                }

                _commandInterface = usb;
                _bulkOut = bulkOut;
                _bulkIn = bulkIn;

                if (_bulkIn != null)
                {
                    _bulkIn.ReadOptions |= UsbReadOptions.AllowPartialReads;
                }

                Log($"Command interface opened: {info.Id}");
                return;
            }
            catch (Exception e)
            {
                Log($"USB interface {info.Id} open failed: {e.Message}");
                usb?.Dispose();
            }
        }
    }

    private async Task WriteBulkAsync(byte[] frame, CancellationToken ct)
    {
        if (_bulkOut == null)
        {
            return;
        }

        var op = _bulkOut.OutputStream.WriteAsync(frame.AsBuffer());
        using var reg = ct.Register(op.Cancel);
        var written = await op;
        if (written != frame.Length)
        {
            throw new InvalidOperationException($"Bulk write truncated ({written}/{frame.Length}).");
        }
    }

    private void ReadInputLoop(CancellationToken ct)
    {
        var buf = new byte[512];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                if (!_device.IsValid)
                {
                    break;
                }

                read = _device.ReadTimeout(buf, buf.Length, 100);
            }
            catch (Exception e)
            {
                Log($"HID read failed: {e.Message}");
                break;
            }

            if (read <= 0)
            {
                continue;
            }

            var report = buf.AsSpan(0, read).ToArray();

            // Command replies arriving on the HID channel (0x21/0x02)
            if (report[0] is 0x21 or 0x02)
            {
                if (_pendingCommands.TryGetValue(report[0], out var tcs))
                {
                    tcs.TrySetResult(report);
                }
                continue;
            }

            // Legacy Pro reports (0x30/0x31): decode in place, then re-wrap as a
            // normalized NS2 report so Switch2Device sees a single layout.
            if (Ns2Protocol.TryDecodeLegacyReport(report, out var legacy))
            {
                InputReportReceived?.Invoke(this, WrapNormalized(legacy));
                continue;
            }

            // Strip the USB report ID byte (0x05 = FD2 input, 0x01 = GC input).
            var payload = report.Length > Ns2Protocol.StandardReportLen && report[0] is 0x05 or 0x01 or 0x30 or 0x31 or 0xA1
                ? report[1..]
                : report;

            InputReportReceived?.Invoke(this, payload);
        }

        if (!ct.IsCancellationRequested)
        {
            LinkDropped?.Invoke(this, EventArgs.Empty);
        }
    }

    private static byte[] WrapNormalized(in Ns2Protocol.Ns2Input input)
    {
        // Build a synthetic standard-layout report (counter + buttons + sticks + IMU)
        // so the device-side decoder path stays unified.
        var report = new byte[Ns2Protocol.StandardReportLen];
        BitConverter.TryWriteBytes(report.AsSpan(4, 4), input.Buttons);

        report[10] = (byte)input.LeftX;
        report[11] = (byte)((input.LeftX >> 8) | ((input.LeftY & 0x0F) << 4));
        report[12] = (byte)(input.LeftY >> 4);
        report[13] = (byte)input.RightX;
        report[14] = (byte)((input.RightX >> 8) | ((input.RightY & 0x0F) << 4));
        report[15] = (byte)(input.RightY >> 4);

        BitConverter.TryWriteBytes(report.AsSpan(48, 2), input.AccelX);
        BitConverter.TryWriteBytes(report.AsSpan(50, 2), input.AccelY);
        BitConverter.TryWriteBytes(report.AsSpan(52, 2), input.AccelZ);
        BitConverter.TryWriteBytes(report.AsSpan(54, 2), input.GyroX);
        BitConverter.TryWriteBytes(report.AsSpan(56, 2), input.GyroY);
        BitConverter.TryWriteBytes(report.AsSpan(58, 2), input.GyroZ);

        return report;
    }

    private void ReadCommandReplies(CancellationToken ct)
    {
        var buffer = new Windows.Storage.Streams.Buffer(512);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_bulkIn == null)
                {
                    break;
                }

                var op = _bulkIn.InputStream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.Partial);
                var data = op.AsTask().GetAwaiter().GetResult();
                if (data == null || data.Length == 0)
                {
                    continue;
                }

                var bytes = new byte[data.Length];
                DataReader.FromBuffer(data).ReadBytes(bytes);

                // Pro 2 ack: resp[0]==cmd && resp[1]==1 && resp[3]==sub && resp[5]∈{0xF8,0x78}
                if (bytes.Length >= 8 && bytes[1] == 0x01 && bytes[5] is 0xF8 or 0x78)
                {
                    if (_pendingCommands.TryGetValue(bytes[0], out var tcs))
                    {
                        tcs.TrySetResult(bytes);
                    }
                }
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log($"Command reply read failed: {e.Message}");
                }
                break;
            }
        }
    }

    public async Task<byte[]> SendCommandAsync(byte command, byte subcommand, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[command] = tcs;

        try
        {
            await _commandLock.WaitAsync(ct);

            var frame = Ns2Protocol.BuildCommand(command, subcommand, data.Span, usb: true);
            await WriteBulkAsync(frame, ct);

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
        var payload = response.Length > 8 ? response[8..] : [];

        if (!Ns2Protocol.TryParseReadMemoryReply(payload, length, address, out var data))
        {
            throw new InvalidOperationException($"read_memory reply mismatch at 0x{address:X6}.");
        }

        return data.ToArray();
    }

    public void SetRumble(bool on)
    {
        _ = SendRumbleAsync(on);
    }

    private async Task SendRumbleAsync(bool on)
    {
        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            if (_productId == Ns2Protocol.PidGameCubeNso)
            {
                // GC NSO takes its rumble flag on the vendor command channel.
                await SendCommandAsync(0x0A, 0x02, new byte[] { on ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00 }, ct);
            }
            else
            {
                // Joy-Con 2 / Pro 2: same 0x50 keep-alive + rumble frame used
                // on the BLE vibration characteristic, written on bulk OUT.
                await WriteBulkAsync(Ns2Protocol.BuildRumbleFrame(_rumbleCounter++, on), ct);
            }
        }
        catch (Exception e)
        {
            Log($"Rumble write failed: {e.Message}");
        }
    }

    public async Task SetPlayerLedAsync(int padId, CancellationToken ct)
    {
        await SendCommandAsync(0x09, 0x07, Ns2Protocol.BuildPlayerLedCommand(padId), ct);
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _inputThread?.Join(500);
        _replyThread?.Join(500);
        _commandInterface?.Dispose();
        _commandInterface = null;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cts?.Cancel();
        _commandInterface?.Dispose();
        _device.Dispose();
        _commandLock.Dispose();
    }
}
