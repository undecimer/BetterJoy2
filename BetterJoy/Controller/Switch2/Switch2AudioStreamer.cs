using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using BetterJoy.Logging;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Streams Windows desktop playback audio to a Switch 2 Pro Controller 3.5mm jack:
/// - Over USB-C: ensures the UAC1 "Switch 2 Pro Controller" render endpoint is unmuted
///   and bridges the current Windows default audio output into it whenever another
///   playback device is set as default.
/// - Over BLE: captures Windows loopback audio, encodes 48 kHz stereo 20 ms frames as
///   96 kbps CBR CELT-only Opus (240 B) via Concentus, and streams split 120 B chunks
///   to GATT audio handle 0x002C after enabling audio on 0x0032.
/// </summary>
internal sealed class Switch2AudioStreamer : IDisposable
{
    private static readonly Guid MediaSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid MediaSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private const int PcmFrameShorts = Ns2Protocol.AudioFrameSamplesPerChannel * Ns2Protocol.AudioChannels; // 1920 shorts = 20 ms @ 48 kHz stereo
    private const int RingCapacityShorts = PcmFrameShorts * 6; // 120 ms capacity
    private const int MaxBufferedShorts = PcmFrameShorts * 2;  // 40 ms max latency cap
    private const int LoopbackBufferMs = 16;                   // ~8 ms polling cadence under timeBeginPeriod(1)
    private const int MaxConsecutiveBleFailures = 16;

    private readonly ISwitch2Transport _transport;
    private readonly Action<string, LogLevel> _log;
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Thread? _workerThread;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _workerThread is { IsAlive: true };
            }
        }
    }

    public Switch2AudioStreamer(ISwitch2Transport transport, Action<string, LogLevel> log)
    {
        _transport = transport;
        _log = log;
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _workerThread is { IsAlive: true })
            {
                return;
            }

            if (!_transport.IsUsb && !_transport.SupportsBleAudio)
            {
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _workerThread = new Thread(() => RunWorker(ct))
            {
                IsBackground = true,
                Name = _transport.IsUsb ? "Switch2-UsbAudio" : "Switch2-BleAudio",
                Priority = ThreadPriority.AboveNormal
            };
            _workerThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            _cts?.Cancel();
            thread = _workerThread;
            _workerThread = null;
        }

        if (thread is { IsAlive: true } && thread != Thread.CurrentThread)
        {
            thread.Join(1000);
        }

        lock (_lifecycleLock)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void RunWorker(CancellationToken ct)
    {
        try
        {
            if (_transport.IsUsb)
            {
                RunUsbAudioLoop(ct);
            }
            else
            {
                RunBleAudioLoop(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested)
            {
                _log($"Pro 2 audio streamer stopped: {e.Message}", LogLevel.Warning);
            }
        }
    }

    // ------------------------------------------------------------------
    // BLE Opus CELT streaming (96 kbps CBR, 20 ms -> 240B -> 120+120B)
    // ------------------------------------------------------------------

    private void RunBleAudioLoop(CancellationToken ct)
    {
        var ring = new ShortRingBuffer(RingCapacityShorts, MaxBufferedShorts);
        var loopback = new BleLoopbackState();

        var encoder = OpusCodecFactory.CreateEncoder(
            Ns2Protocol.AudioSampleRate,
            Ns2Protocol.AudioChannels,
            OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        encoder.Bitrate = 96000;
        encoder.UseVBR = false;
        encoder.ForceMode = OpusMode.MODE_CELT_ONLY;
        encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
        encoder.ForceChannels = Ns2Protocol.AudioChannels;
        encoder.Complexity = 2;

        var pcmFrame = new short[PcmFrameShorts];
        var silenceFrame = new short[PcmFrameShorts];
        var opusPacket = new byte[Ns2Protocol.AudioOpusFrameBytes];
        var discardPacket = new byte[Ns2Protocol.AudioOpusFrameBytes];
        var idlePacket = new byte[Ns2Protocol.AudioOpusFrameBytes];
        idlePacket[0] = 0xFC;
        idlePacket[1] = 0xFF;
        idlePacket[2] = 0xFE;

        var chunk1 = new byte[3 + Ns2Protocol.AudioBleChunkBytes];
        var chunk2 = new byte[3 + Ns2Protocol.AudioBleChunkBytes];

        var timerActive = NativeMethods.timeBeginPeriod(1) == 0;
        try
        {
            if (!SendBleSetupSequence(ct))
            {
                return;
            }

            StartOrRefreshLoopbackCapture(loopback, ring);

            _log("BLE headphone Opus audio stream started (48 kHz stereo 96 kbps CELT).", LogLevel.Info);

            var tickFreq = Stopwatch.Frequency;
            var frameTicks = tickFreq / 50;      // 20 ms
            var halfTicks = frameTicks / 4;      // 5 ms
            var endpointPollTicks = tickFreq;    // 1000 ms

            var primeCount = 0;
            var idleChain = 8; // start aligned to idlePacket after priming
            var consecutiveFailures = 0;
            var nextFrameTick = Stopwatch.GetTimestamp();
            var nextEndpointCheckTick = nextFrameTick + endpointPollTicks;

            while (!ct.IsCancellationRequested)
            {
                WaitUntilTick(nextFrameTick, ct);

                var now = Stopwatch.GetTimestamp();
                if (now >= nextEndpointCheckTick)
                {
                    nextEndpointCheckTick = now + endpointPollTicks;
                    StartOrRefreshLoopbackCapture(loopback, ring);
                }

                ReadOnlySpan<byte> frameToSend;
                if (primeCount < 8)
                {
                    frameToSend = idlePacket;
                    primeCount++;
                    if (primeCount == 8)
                    {
                        ring.Clear();
                    }
                }
                else if (ring.TryPopExact(pcmFrame) && HasSignal(pcmFrame))
                {
                    var encodedLen = encoder.Encode(pcmFrame.AsSpan(), Ns2Protocol.AudioFrameSamplesPerChannel, opusPacket.AsSpan(), opusPacket.Length);
                    if (encodedLen == Ns2Protocol.AudioOpusFrameBytes && opusPacket[0] == 0xFC)
                    {
                        idleChain = 0;
                        frameToSend = opusPacket;
                    }
                    else
                    {
                        frameToSend = idlePacket;
                    }
                }
                else
                {
                    if (idleChain < 8)
                    {
                        _ = encoder.Encode(silenceFrame.AsSpan(), Ns2Protocol.AudioFrameSamplesPerChannel, discardPacket.AsSpan(), discardPacket.Length);
                        idleChain++;
                    }
                    frameToSend = idlePacket;
                }

                Ns2Protocol.BuildBleAudioChunks(frameToSend, chunk1, chunk2);

                if (!WriteBleChunkWithRetry(chunk1, ref consecutiveFailures, ct))
                {
                    break;
                }

                var afterChunk1 = Stopwatch.GetTimestamp();
                var chunk2Tick = nextFrameTick + halfTicks;
                if (chunk2Tick <= afterChunk1)
                {
                    chunk2Tick = afterChunk1 + halfTicks;
                }
                WaitUntilTick(chunk2Tick, ct);
                if (!WriteBleChunkWithRetry(chunk2, ref consecutiveFailures, ct))
                {
                    break;
                }

                nextFrameTick = AdvanceDeadline(nextFrameTick, frameTicks);
            }
        }
        finally
        {
            StopLoopbackCapture(ref loopback.Capture);
            if (timerActive)
            {
                _ = NativeMethods.timeEndPeriod(1);
            }
            _log("BLE headphone audio stream stopped.", LogLevel.Debug);
        }
    }

    private bool SendBleSetupSequence(CancellationToken ct)
    {
        if (!_transport.WriteBleAudioCommandAsync(Ns2Protocol.BleAudioSetupOne, ct).GetAwaiter().GetResult())
        {
            _log("BLE audio setup step 1 (18:03) failed.", LogLevel.Warning);
            return false;
        }

        if (ct.WaitHandle.WaitOne(25))
        {
            return false;
        }

        if (!_transport.WriteBleAudioCommandAsync(Ns2Protocol.BleAudioSetupTwo, ct).GetAwaiter().GetResult())
        {
            _log("BLE audio setup step 2 (17:02) failed.", LogLevel.Warning);
            return false;
        }

        return !ct.WaitHandle.WaitOne(10);
    }

    private bool WriteBleChunkWithRetry(byte[] chunk, ref int consecutiveFailures, CancellationToken ct)
    {
        var ok = _transport.WriteBleAudioOutputAsync(chunk, ct).GetAwaiter().GetResult();
        if (ok)
        {
            consecutiveFailures = 0;
            return true;
        }

        if (++consecutiveFailures >= MaxConsecutiveBleFailures)
        {
            _log($"BLE audio stream aborted after {consecutiveFailures} failed writes.", LogLevel.Warning);
            return false;
        }

        return true;
    }

    private sealed class LowLatencyLoopbackCapture : WasapiCapture
    {
        public LowLatencyLoopbackCapture(MMDevice captureDevice, int audioBufferMillisecondsLength = LoopbackBufferMs)
            : base(captureDevice, useEventSync: false, audioBufferMillisecondsLength: audioBufferMillisecondsLength)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
        }
    }

    private sealed class BleLoopbackState
    {
        public WasapiCapture? Capture;
        public volatile string? EndpointId;
        public volatile bool Stopped;
    }

    private void StartOrRefreshLoopbackCapture(BleLoopbackState state, ShortRingBuffer ring)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                StopLoopbackCapture(ref state.Capture);
                state.EndpointId = null;
                return;
            }

            using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (state.Capture != null && !state.Stopped && defaultEndpoint.ID == state.EndpointId)
            {
                return;
            }

            StopLoopbackCapture(ref state.Capture);
            ring.Clear();

            state.EndpointId = defaultEndpoint.ID;
            state.Stopped = false;
            var resampler = new LoopbackPcmConverter(defaultEndpoint);
            var cap = new LowLatencyLoopbackCapture(defaultEndpoint, LoopbackBufferMs);
            var mixFormat = cap.WaveFormat;
            cap.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    resampler.WriteConvertedShorts(e.Buffer, e.BytesRecorded, mixFormat, ring);
                }
            };
            cap.RecordingStopped += (_, _) => state.Stopped = true;
            cap.StartRecording();
            state.Capture = cap;

            _log($"BLE audio capturing default playback endpoint: {defaultEndpoint.FriendlyName} ({mixFormat.SampleRate} Hz, {mixFormat.Channels} ch).", LogLevel.Debug);
        }
        catch (Exception e)
        {
            _log($"Loopback capture refresh error: {e.Message}", LogLevel.Debug);
            StopLoopbackCapture(ref state.Capture);
            state.EndpointId = null;
        }
    }

    // ------------------------------------------------------------------
    // USB Audio Class 1.0 bridge (48 kHz 16-bit Stereo via WASAPI)
    // ------------------------------------------------------------------

    private void RunUsbAudioLoop(CancellationToken ct)
    {
        WasapiCapture? capture = null;
        WasapiOut? output = null;
        string? bridgedSourceId = null;
        string? bridgedTargetId = null;
        var captureStopped = false;
        var reportedNativeDefault = false;
        var waitingLogged = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var pro2Endpoint = FindPro2RenderEndpoint(enumerator);

                    if (pro2Endpoint == null)
                    {
                        StopUsbBridge(ref capture, ref output, ref bridgedSourceId, ref bridgedTargetId);
                        captureStopped = false;
                        reportedNativeDefault = false;
                        if (!waitingLogged)
                        {
                            _log("Waiting for Windows USB Audio endpoint (Switch 2 Pro Controller)...", LogLevel.Debug);
                            LogAvailableRenderEndpoints(enumerator);
                            waitingLogged = true;
                        }
                        if (ct.WaitHandle.WaitOne(1000))
                        {
                            break;
                        }
                        continue;
                    }

                    waitingLogged = false;
                    EnsureEndpointUnmuted(pro2Endpoint);

                    if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    {
                        StopUsbBridge(ref capture, ref output, ref bridgedSourceId, ref bridgedTargetId);
                        captureStopped = false;
                        if (ct.WaitHandle.WaitOne(1000))
                        {
                            break;
                        }
                        continue;
                    }

                    using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // Avoid loopback onto the controller itself when Windows default output
                    // is already set to Switch 2 Pro Controller.
                    if (string.Equals(defaultEndpoint.ID, pro2Endpoint.ID, StringComparison.OrdinalIgnoreCase))
                    {
                        StopUsbBridge(ref capture, ref output, ref bridgedSourceId, ref bridgedTargetId);
                        captureStopped = false;
                        if (!reportedNativeDefault)
                        {
                            _log($"USB Audio active directly via Windows default device ({pro2Endpoint.FriendlyName}).", LogLevel.Info);
                            reportedNativeDefault = true;
                        }
                    }
                    else
                    {
                        reportedNativeDefault = false;
                        var needsRestart = capture == null ||
                            output == null ||
                            captureStopped ||
                            !string.Equals(bridgedSourceId, defaultEndpoint.ID, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(bridgedTargetId, pro2Endpoint.ID, StringComparison.OrdinalIgnoreCase);

                        if (needsRestart)
                        {
                            StopUsbBridge(ref capture, ref output, ref bridgedSourceId, ref bridgedTargetId);
                            captureStopped = false;
                            StartUsbBridge(defaultEndpoint, pro2Endpoint, out capture, out output, () => captureStopped = true);
                            bridgedSourceId = defaultEndpoint.ID;
                            bridgedTargetId = pro2Endpoint.ID;
                            _log($"USB Audio bridging '{defaultEndpoint.FriendlyName}' -> '{pro2Endpoint.FriendlyName}'.", LogLevel.Info);
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        _log($"USB audio bridge error: {e.Message}", LogLevel.Debug);
                    }
                    StopUsbBridge(ref capture, ref output, ref bridgedSourceId, ref bridgedTargetId);
                    captureStopped = false;
                }

                if (ct.WaitHandle.WaitOne(1000))
                {
                    break;
                }
            }
        }
        finally
        {
            StopUsbBridge(ref capture, ref output, ref bridgedSourceId, ref bridgedTargetId);
            _log("USB audio streamer stopped.", LogLevel.Debug);
        }
    }

    private void LogAvailableRenderEndpoints(MMDeviceEnumerator enumerator)
    {
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _log($"Render endpoint detected: '{device.FriendlyName}' (device='{device.DeviceFriendlyName}').", LogLevel.Debug);
                device.Dispose();
            }
        }
        catch { }
    }

    private static MMDevice? FindPro2RenderEndpoint(MMDeviceEnumerator enumerator)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var friendly = device.FriendlyName;
            var devFriendly = device.DeviceFriendlyName;
            if (friendly.Contains("Switch 2 Pro Controller", StringComparison.OrdinalIgnoreCase) ||
                devFriendly.Contains("Switch 2 Pro Controller", StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
            device.Dispose();
        }

        return null;
    }

    private void EnsureEndpointUnmuted(MMDevice endpoint)
    {
        try
        {
            var vol = endpoint.AudioEndpointVolume;
            if (vol.Mute)
            {
                vol.Mute = false;
            }
            if (vol.MasterVolumeLevelScalar < 0.05f)
            {
                vol.MasterVolumeLevelScalar = 0.75f;
            }
        }
        catch (Exception e)
        {
            _log($"Could not unmute USB audio endpoint: {e.Message}", LogLevel.Debug);
        }
    }

    private void StartUsbBridge(
        MMDevice sourceEndpoint,
        MMDevice targetEndpoint,
        out WasapiCapture capture,
        out WasapiOut output,
        Action onStopped)
    {
        var outFormat = new WaveFormat(Ns2Protocol.AudioSampleRate, 16, Ns2Protocol.AudioChannels);
        var buffer = new BufferedWaveProvider(outFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(80),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        var wasapiOut = new WasapiOut(targetEndpoint, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
        wasapiOut.PlaybackStopped += (_, _) => onStopped();
        wasapiOut.Init(buffer);
        wasapiOut.Play();

        var resampler = new LoopbackPcmConverter(sourceEndpoint);
        var cap = new LowLatencyLoopbackCapture(sourceEndpoint, LoopbackBufferMs);
        var mixFormat = cap.WaveFormat;
        var scratchBytes = new byte[8192];

        cap.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0)
            {
                return;
            }

            var byteCount = resampler.ConvertTo48kStereoPcm16Bytes(e.Buffer, e.BytesRecorded, mixFormat, ref scratchBytes);
            if (byteCount > 0)
            {
                if (buffer.BufferedDuration > TimeSpan.FromMilliseconds(45))
                {
                    buffer.ClearBuffer();
                }
                buffer.AddSamples(scratchBytes, 0, byteCount);
            }
        };
        cap.RecordingStopped += (_, _) => onStopped();
        cap.StartRecording();

        capture = cap;
        output = wasapiOut;
    }

    private static void StopUsbBridge(
        ref WasapiCapture? capture,
        ref WasapiOut? output,
        ref string? sourceId,
        ref string? targetId)
    {
        StopLoopbackCapture(ref capture);
        if (output != null)
        {
            try
            {
                output.Stop();
            }
            catch { }
            try
            {
                output.Dispose();
            }
            catch { }
            output = null;
        }
        sourceId = null;
        targetId = null;
    }

    private static void StopLoopbackCapture(ref WasapiCapture? capture)
    {
        if (capture == null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch { }
        try
        {
            capture.Dispose();
        }
        catch { }
        capture = null;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void WaitUntilTick(long targetTick, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var remainingTicks = targetTick - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private static long AdvanceDeadline(long deadline, long intervalTicks)
    {
        var next = deadline + intervalTicks;
        var now = Stopwatch.GetTimestamp();
        return now - next >= intervalTicks ? now + intervalTicks : next;
    }

    private static bool HasSignal(ReadOnlySpan<short> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] != 0)
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        Stop();
    }

    // ------------------------------------------------------------------
    // Ring buffer & PCM converter (any mix format -> 48 kHz stereo s16)
    // ------------------------------------------------------------------

    private sealed class ShortRingBuffer
    {
        private readonly short[] _buf;
        private readonly int _maxBuffered;
        private readonly object _lock = new();
        private int _head;
        private int _tail;
        private int _count;

        public ShortRingBuffer(int capacity, int maxBuffered)
        {
            _buf = new short[capacity];
            _maxBuffered = maxBuffered;
        }

        public void PushStereoSample(short left, short right)
        {
            lock (_lock)
            {
                WriteUnsafe(left);
                WriteUnsafe(right);

                if (_count > _maxBuffered)
                {
                    var drop = _count - _maxBuffered;
                    drop -= drop & 1; // keep L/R alignment
                    _head = (_head + drop) % _buf.Length;
                    _count -= drop;
                }
            }
        }

        public bool TryPopExact(short[] dest)
        {
            lock (_lock)
            {
                if (_count < dest.Length)
                {
                    return false;
                }

                var first = Math.Min(dest.Length, _buf.Length - _head);
                Array.Copy(_buf, _head, dest, 0, first);
                if (first < dest.Length)
                {
                    Array.Copy(_buf, 0, dest, first, dest.Length - first);
                }

                _head = (_head + dest.Length) % _buf.Length;
                _count -= dest.Length;
                return true;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
            }
        }

        private void WriteUnsafe(short sample)
        {
            if (_count == _buf.Length)
            {
                _head = (_head + 1) % _buf.Length;
                _count--;
            }

            _buf[_tail] = sample;
            _tail = (_tail + 1) % _buf.Length;
            _count++;
        }
    }

    private sealed class LoopbackPcmConverter
    {
        private double _phase;
        private float _prevL;
        private float _prevR;
        private bool _hasPrev;

        public LoopbackPcmConverter(MMDevice _) { }

        public void WriteConvertedShorts(byte[] buffer, int bytesRecorded, WaveFormat format, ShortRingBuffer ring)
        {
            ProcessBuffer(buffer, bytesRecorded, format, (l, r) => ring.PushStereoSample(l, r));
        }

        public int ConvertTo48kStereoPcm16Bytes(byte[] buffer, int bytesRecorded, WaveFormat format, ref byte[] scratchBytes)
        {
            var outBytes = 0;
            var localScratch = scratchBytes;
            ProcessBuffer(buffer, bytesRecorded, format, (l, r) =>
            {
                if (outBytes + 4 > localScratch.Length)
                {
                    Array.Resize(ref localScratch, localScratch.Length * 2);
                }

                BinaryPrimitives.WriteInt16LittleEndian(localScratch.AsSpan(outBytes, 2), l);
                BinaryPrimitives.WriteInt16LittleEndian(localScratch.AsSpan(outBytes + 2, 2), r);
                outBytes += 4;
            });
            scratchBytes = localScratch;
            return outBytes;
        }

        private void ProcessBuffer(byte[] buffer, int bytesRecorded, WaveFormat format, Action<short, short> emit)
        {
            var frameBytes = format.BlockAlign;
            if (frameBytes <= 0 || bytesRecorded < frameBytes)
            {
                return;
            }

            var frames = bytesRecorded / frameBytes;
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                (format is WaveFormatExtensible extF && extF.SubFormat == MediaSubtypeIeeeFloat);
            var isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
                (format is WaveFormatExtensible extP && extP.SubFormat == MediaSubtypePcm);

            if (!isFloat && !isPcm)
            {
                return;
            }

            var bytesPerSample = format.BitsPerSample / 8;
            var channels = Math.Max(1, format.Channels);
            var step = (double)format.SampleRate / Ns2Protocol.AudioSampleRate;

            if (format.SampleRate == Ns2Protocol.AudioSampleRate)
            {
                for (var f = 0; f < frames; f++)
                {
                    ReadFrameFloat(buffer.AsSpan(f * frameBytes, frameBytes), channels, bytesPerSample, isFloat, out var l, out var r);
                    emit(FloatToShort(l), FloatToShort(r));
                }
                return;
            }

            for (var f = 0; f < frames; f++)
            {
                ReadFrameFloat(buffer.AsSpan(f * frameBytes, frameBytes), channels, bytesPerSample, isFloat, out var curL, out var curR);
                if (!_hasPrev)
                {
                    _prevL = curL;
                    _prevR = curR;
                    _hasPrev = true;
                }

                while (_phase < 1.0)
                {
                    var frac = (float)_phase;
                    var outL = _prevL + (curL - _prevL) * frac;
                    var outR = _prevR + (curR - _prevR) * frac;
                    emit(FloatToShort(outL), FloatToShort(outR));
                    _phase += step;
                }

                _phase -= 1.0;
                _prevL = curL;
                _prevR = curR;
            }
        }

        private static void ReadFrameFloat(ReadOnlySpan<byte> frame, int channels, int bytesPerSample, bool isFloat, out float left, out float right)
        {
            left = ReadChannelFloat(frame, 0, bytesPerSample, isFloat);
            right = channels > 1
                ? ReadChannelFloat(frame, bytesPerSample, bytesPerSample, isFloat)
                : left;
        }

        private static float ReadChannelFloat(ReadOnlySpan<byte> frame, int offset, int bytesPerSample, bool isFloat)
        {
            if (offset + bytesPerSample > frame.Length)
            {
                return 0f;
            }

            if (isFloat && bytesPerSample == 4)
            {
                return BitConverter.ToSingle(frame.Slice(offset, 4));
            }

            return bytesPerSample switch
            {
                2 => BitConverter.ToInt16(frame.Slice(offset, 2)) / 32768f,
                3 => ((sbyte)frame[offset + 2] << 16 | frame[offset + 1] << 8 | frame[offset]) / 8388608f,
                4 => BitConverter.ToInt32(frame.Slice(offset, 4)) / 2147483648f,
                _ => 0f
            };
        }

        private static short FloatToShort(float value)
        {
            var scaled = value * 32767f;
            if (scaled >= 32767f)
            {
                return short.MaxValue;
            }
            if (scaled <= -32768f)
            {
                return short.MinValue;
            }
            return (short)scaled;
        }
    }
}
