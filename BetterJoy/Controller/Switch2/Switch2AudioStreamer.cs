using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BetterJoy.Logging;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Streams Windows desktop playback audio to a Switch 2 Pro Controller 3.5mm jack
/// and manages the USB-C UAC1 microphone input endpoint:
/// - Over USB-C: unmutes and bridges Windows default audio output to the controller
///   headphones while a 3.5mm plug is inserted (EnablePro2Audio), unmutes/gains the
///   UAC1 microphone while plugged in (mutes when empty, EnablePro2UsbMic), and
///   optionally switches/restores the default recording endpoint (AutoSwitchPro2UsbMic).
/// - Over BLE: captures Windows loopback audio, encodes 48 kHz stereo 20 ms frames as
///   96 kbps CBR CELT-only Opus (240 B) via Concentus, and streams split 120 B chunks
///   to GATT audio handle 0x002C after enabling audio on 0x0032.
/// </summary>
internal sealed class Switch2AudioStreamer : IDisposable
{
    internal readonly record struct Pro2AudioOptions(bool OutputEnabled, bool UsbMicEnabled, bool AutoSwitchUsbMic);

    private static readonly Guid MediaSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid MediaSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private static readonly Role[] CaptureRoles = [Role.Console, Role.Multimedia, Role.Communications];

    private const int PcmFrameShorts = Ns2Protocol.AudioFrameSamplesPerChannel * Ns2Protocol.AudioChannels; // 1920 shorts = 20 ms @ 48 kHz stereo
    private const int RingCapacityShorts = PcmFrameShorts * 6; // 120 ms capacity
    private const int MaxBufferedShorts = PcmFrameShorts * 2;  // 40 ms max latency cap
    private const int LoopbackBufferMs = 16;                   // ~8 ms polling cadence under timeBeginPeriod(1)
    private const int MaxConsecutiveBleFailures = 16;

    private readonly ISwitch2Transport _transport;
    private readonly Func<Pro2AudioOptions> _getOptions;
    private readonly Action<string, LogLevel> _log;
    private readonly object _lifecycleLock = new();
    private readonly AutoResetEvent _stateSignal = new(false);

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

    public Switch2AudioStreamer(ISwitch2Transport transport, Func<Pro2AudioOptions> getOptions, Action<string, LogLevel> log)
    {
        _transport = transport;
        _getOptions = getOptions;
        _log = log;
    }

    public void NotifyStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _stateSignal.Set();
        }
        catch (ObjectDisposedException) { }
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
    // USB Audio Class 1.0 bridge & microphone manager (48 kHz 16-bit PCM)
    // ------------------------------------------------------------------

    private sealed class UsbRenderState
    {
        public WasapiCapture? Capture;
        public WasapiOut? Output;
        public string? BridgedSourceId;
        public string? BridgedTargetId;
        public volatile bool CaptureStopped;
        public bool ReportedNativeDefault;
        public bool WaitingLogged;
    }

    private sealed class UsbMicState
    {
        public bool WaitingLogged;
        public bool DisabledWarningLogged;
        public bool ReadyLoggedForPlug;
        public bool MutedByUs;
        public bool AutoSwitchAppliedForPlug;
        public bool LastAutoSwitchOption;
        public string? LastPro2MicId;
        public readonly Dictionary<Role, (string Id, string FriendlyName)> SavedDefaultMics = new();
    }

    private void RunUsbAudioLoop(CancellationToken ct)
    {
        var renderState = new UsbRenderState();
        var micState = new UsbMicState();
        var waitHandles = new WaitHandle[] { ct.WaitHandle, _stateSignal };
        var lastHeadsetConnected = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var opts = _getOptions();
                    var headsetConnected = _transport.IsHeadsetConnected || !_transport.HasHeadsetJackDetect;

                    if (headsetConnected && !lastHeadsetConnected)
                    {
                        try
                        {
                            _transport.ReinitializeUsbAudioAsync(ct).GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            _log($"USB audio re-init failed: {e.Message}", LogLevel.Debug);
                        }
                    }
                    lastHeadsetConnected = headsetConnected;

                    using var enumerator = new MMDeviceEnumerator();
                    UpdateUsbRenderOutput(enumerator, renderState, opts.OutputEnabled, headsetConnected);
                    UpdateUsbCaptureMic(enumerator, micState, opts.UsbMicEnabled, opts.AutoSwitchUsbMic, headsetConnected);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        _log($"USB audio manager error: {e.Message}", LogLevel.Debug);
                    }
                    StopUsbBridge(renderState);
                }

                if (WaitHandle.WaitAny(waitHandles, 1000) == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            StopUsbBridge(renderState);
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                RestoreSavedDefaultMic(enumerator, micState);
                RestoreMicMuteIfOwned(enumerator, micState);
            }
            catch { }
            _log("USB audio streamer stopped.", LogLevel.Debug);
        }
    }

    private void UpdateUsbRenderOutput(
        MMDeviceEnumerator enumerator,
        UsbRenderState state,
        bool enabled,
        bool headsetConnected)
    {
        if (!enabled || !headsetConnected)
        {
            StopUsbBridge(state);
            state.ReportedNativeDefault = false;
            state.WaitingLogged = false;
            return;
        }

        using var pro2Endpoint = FindPro2Endpoint(enumerator, DataFlow.Render);
        if (pro2Endpoint == null)
        {
            StopUsbBridge(state);
            state.ReportedNativeDefault = false;
            if (!state.WaitingLogged)
            {
                _log("Waiting for Windows USB Audio endpoint (Switch 2 Pro Controller)...", LogLevel.Debug);
                LogAvailableEndpoints(enumerator, DataFlow.Render);
                state.WaitingLogged = true;
            }
            return;
        }

        state.WaitingLogged = false;
        EnsureRenderEndpointUnmuted(pro2Endpoint);

        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            StopUsbBridge(state);
            return;
        }

        using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Avoid loopback onto the controller itself when Windows default output
        // is already set to Switch 2 Pro Controller.
        if (string.Equals(defaultEndpoint.ID, pro2Endpoint.ID, StringComparison.OrdinalIgnoreCase))
        {
            StopUsbBridge(state);
            if (!state.ReportedNativeDefault)
            {
                _log($"USB Audio active directly via Windows default device ({pro2Endpoint.FriendlyName}).", LogLevel.Info);
                state.ReportedNativeDefault = true;
            }
            return;
        }

        state.ReportedNativeDefault = false;
        var needsRestart = state.Capture == null ||
            state.Output == null ||
            state.CaptureStopped ||
            !string.Equals(state.BridgedSourceId, defaultEndpoint.ID, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.BridgedTargetId, pro2Endpoint.ID, StringComparison.OrdinalIgnoreCase);

        if (!needsRestart)
        {
            return;
        }

        StopUsbBridge(state);
        StartUsbBridge(defaultEndpoint, pro2Endpoint, out state.Capture, out state.Output, () => state.CaptureStopped = true);
        state.BridgedSourceId = defaultEndpoint.ID;
        state.BridgedTargetId = pro2Endpoint.ID;
        _log($"USB Audio bridging '{defaultEndpoint.FriendlyName}' -> '{pro2Endpoint.FriendlyName}'.", LogLevel.Info);
    }

    private void UpdateUsbCaptureMic(
        MMDeviceEnumerator enumerator,
        UsbMicState state,
        bool enabled,
        bool autoSwitch,
        bool headsetConnected)
    {
        if (!enabled)
        {
            RestoreSavedDefaultMic(enumerator, state);
            RestoreMicMuteIfOwned(enumerator, state);
            state.ReadyLoggedForPlug = false;
            state.AutoSwitchAppliedForPlug = false;
            state.WaitingLogged = false;
            state.DisabledWarningLogged = false;
            state.LastAutoSwitchOption = false;
            return;
        }

        using var pro2Mic = FindPro2Endpoint(enumerator, DataFlow.Capture);
        if (pro2Mic == null)
        {
            HandleMissingCaptureEndpoint(enumerator, state);
            return;
        }

        state.WaitingLogged = false;
        state.DisabledWarningLogged = false;
        state.LastPro2MicId = pro2Mic.ID;

        if (!headsetConnected)
        {
            state.ReadyLoggedForPlug = false;
            state.AutoSwitchAppliedForPlug = false;
            state.LastAutoSwitchOption = autoSwitch;
            RestoreSavedDefaultMic(enumerator, state);
            MuteCaptureEndpointWhenEmpty(pro2Mic, state);
            return;
        }

        EnsureCaptureEndpointReady(pro2Mic, state);

        if (autoSwitch && (!state.AutoSwitchAppliedForPlug || !state.LastAutoSwitchOption))
        {
            SwitchDefaultMicToPro2(enumerator, pro2Mic, state);
        }
        else if (!autoSwitch && state.LastAutoSwitchOption)
        {
            RestoreSavedDefaultMic(enumerator, state);
            state.AutoSwitchAppliedForPlug = false;
        }
        state.LastAutoSwitchOption = autoSwitch;

        if (!state.ReadyLoggedForPlug)
        {
            LogMicReady(enumerator, pro2Mic);
            state.ReadyLoggedForPlug = true;
        }
    }

    private void HandleMissingCaptureEndpoint(MMDeviceEnumerator enumerator, UsbMicState state)
    {
        state.ReadyLoggedForPlug = false;
        using var disabledMic = FindPro2Endpoint(enumerator, DataFlow.Capture, DeviceState.Disabled);
        if (disabledMic != null)
        {
            if (!state.DisabledWarningLogged)
            {
                _log($"USB Microphone endpoint '{disabledMic.FriendlyName}' is disabled in Windows Sound settings (mmsys.cpl -> Recording). Re-enable it to use the controller microphone.", LogLevel.Warning);
                state.DisabledWarningLogged = true;
            }
            return;
        }

        if (!state.WaitingLogged)
        {
            _log("Waiting for Windows USB Microphone endpoint (Switch 2 Pro Controller)...", LogLevel.Debug);
            LogAvailableEndpoints(enumerator, DataFlow.Capture);
            state.WaitingLogged = true;
        }
    }

    private void EnsureCaptureEndpointReady(MMDevice endpoint, UsbMicState state)
    {
        try
        {
            var vol = endpoint.AudioEndpointVolume;
            if (vol.Mute)
            {
                vol.Mute = false;
            }
            if (vol.MasterVolumeLevelScalar < 0.15f)
            {
                vol.MasterVolumeLevelScalar = 0.85f;
            }
            state.MutedByUs = false;
        }
        catch (Exception e)
        {
            _log($"Could not prepare USB microphone endpoint: {e.Message}", LogLevel.Debug);
        }
    }

    private void MuteCaptureEndpointWhenEmpty(MMDevice endpoint, UsbMicState state)
    {
        try
        {
            var vol = endpoint.AudioEndpointVolume;
            if (!vol.Mute)
            {
                vol.Mute = true;
                state.MutedByUs = true;
                _log($"USB Microphone muted while 3.5mm jack is empty ('{endpoint.FriendlyName}').", LogLevel.Debug);
            }
        }
        catch (Exception e)
        {
            _log($"Could not mute idle USB microphone endpoint: {e.Message}", LogLevel.Debug);
        }
    }

    private void SwitchDefaultMicToPro2(MMDeviceEnumerator enumerator, MMDevice pro2Mic, UsbMicState state)
    {
        var switched = false;
        string? previousName = null;

        foreach (var role in CaptureRoles)
        {
            try
            {
                string? currentId = null;
                string? currentName = null;
                if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role))
                {
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    currentId = current.ID;
                    currentName = current.FriendlyName;
                }

                if (string.Equals(currentId, pro2Mic.ID, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (currentId != null && currentName != null && !state.SavedDefaultMics.ContainsKey(role))
                {
                    state.SavedDefaultMics[role] = (currentId, currentName);
                    previousName ??= currentName;
                }

                if (WindowsAudioPolicyConfig.TrySetDefaultEndpoint(pro2Mic.ID, role, out _))
                {
                    switched = true;
                }
            }
            catch (Exception e)
            {
                _log($"Could not switch default microphone role {role}: {e.Message}", LogLevel.Debug);
            }
        }

        state.AutoSwitchAppliedForPlug = true;
        if (switched)
        {
            var prevInfo = previousName != null ? $" (was '{previousName}')" : "";
            _log($"USB Microphone set as Windows default input: '{pro2Mic.FriendlyName}'{prevInfo}.", LogLevel.Info);
        }
    }

    private void RestoreSavedDefaultMic(MMDeviceEnumerator enumerator, UsbMicState state)
    {
        if (state.SavedDefaultMics.Count == 0)
        {
            return;
        }

        string? restoredName = null;
        foreach (var (role, saved) in state.SavedDefaultMics)
        {
            try
            {
                var shouldRestore = !enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role);
                if (!shouldRestore)
                {
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    if (string.Equals(current.ID, saved.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    shouldRestore = IsPro2Endpoint(current, state.LastPro2MicId);
                }

                if (!shouldRestore)
                {
                    continue;
                }

                using var target = enumerator.GetDevice(saved.Id);
                if (target.State != DeviceState.Active)
                {
                    continue;
                }

                if (WindowsAudioPolicyConfig.TrySetDefaultEndpoint(saved.Id, role, out _))
                {
                    restoredName ??= saved.FriendlyName;
                }
            }
            catch { }
        }

        if (restoredName != null)
        {
            _log($"USB Microphone restored Windows default input -> '{restoredName}'.", LogLevel.Info);
        }

        state.SavedDefaultMics.Clear();
    }

    private void RestoreMicMuteIfOwned(MMDeviceEnumerator enumerator, UsbMicState state)
    {
        if (!state.MutedByUs)
        {
            return;
        }

        try
        {
            using var pro2Mic = FindPro2Endpoint(enumerator, DataFlow.Capture);
            if (pro2Mic != null && pro2Mic.AudioEndpointVolume.Mute)
            {
                pro2Mic.AudioEndpointVolume.Mute = false;
            }
        }
        catch { }
        finally
        {
            state.MutedByUs = false;
        }
    }

    private void LogMicReady(MMDeviceEnumerator enumerator, MMDevice pro2Mic)
    {
        var fmt = "unknown format";
        try
        {
            var mix = pro2Mic.AudioClient.MixFormat;
            fmt = $"{mix.SampleRate} Hz, {mix.BitsPerSample}-bit, {mix.Channels} ch";
        }
        catch { }

        var volPct = 0;
        try
        {
            volPct = (int)Math.Round(pro2Mic.AudioEndpointVolume.MasterVolumeLevelScalar * 100f);
        }
        catch { }

        var isDefault = IsEndpointDefaultForRole(enumerator, pro2Mic.ID, Role.Console) ||
            IsEndpointDefaultForRole(enumerator, pro2Mic.ID, Role.Communications);
        _log($"USB Microphone ready: '{pro2Mic.FriendlyName}' ({fmt}, vol={volPct}%, default={isDefault}).", LogLevel.Info);
    }

    private static bool IsEndpointDefaultForRole(MMDeviceEnumerator enumerator, string deviceId, Role role)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role))
            {
                return false;
            }

            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            return string.Equals(current.ID, deviceId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPro2Endpoint(MMDevice device, string? knownId = null)
    {
        if (knownId != null && string.Equals(device.ID, knownId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return device.FriendlyName.Contains("Switch 2 Pro Controller", StringComparison.OrdinalIgnoreCase) ||
            device.DeviceFriendlyName.Contains("Switch 2 Pro Controller", StringComparison.OrdinalIgnoreCase);
    }

    private void LogAvailableEndpoints(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var label = flow == DataFlow.Render ? "Render" : "Capture";
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                _log($"{label} endpoint detected: '{device.FriendlyName}' (device='{device.DeviceFriendlyName}').", LogLevel.Debug);
                device.Dispose();
            }
        }
        catch { }
    }

    private static MMDevice? FindPro2Endpoint(MMDeviceEnumerator enumerator, DataFlow flow, DeviceState state = DeviceState.Active)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, state))
        {
            if (IsPro2Endpoint(device))
            {
                return device;
            }
            device.Dispose();
        }

        return null;
    }

    private void EnsureRenderEndpointUnmuted(MMDevice endpoint)
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

    private static void StopUsbBridge(UsbRenderState state)
    {
        StopLoopbackCapture(ref state.Capture);
        if (state.Output != null)
        {
            try
            {
                state.Output.Stop();
            }
            catch { }
            try
            {
                state.Output.Dispose();
            }
            catch { }
            state.Output = null;
        }
        state.BridgedSourceId = null;
        state.BridgedTargetId = null;
        state.CaptureStopped = false;
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
        _stateSignal.Dispose();
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
