using System;
using System.Numerics;
using System.Threading;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using BetterJoy.Collections;
using BetterJoy.Config;
using BetterJoy.Controller.Mapping;
using BetterJoy.Hardware.Calibration;
using BetterJoy.Hardware.Data;
using BetterJoy.Hardware.SubCommand;
using BetterJoy.Logging;
using BetterJoy.Forms;
using BetterJoy.Network;
using BetterJoy.Network.Server;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using WindowsInput.Events;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Nintendo Switch 2 controller (Joy-Con 2, Pro Controller 2, GameCube NSO)
/// over BLE GATT or USB. Implements the common controller surface so it
/// participates in the same ViGEm/UDP pipeline as the Switch 1 Joycon class.
/// </summary>
public class Switch2Device : IControllerDevice
{
    private readonly ILogger? _logger;
    private readonly MainForm _form;
    private readonly ISwitch2Transport _transport;

    public ControllerConfig Config { get; }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------
    private readonly bool[] _buttons = new bool[23];
    private readonly bool[] _buttonsRemapped = new bool[23];

    private Stick _stick;
    private Stick _stick2;
    private TwoAxisUShort _stickPrecal;
    private TwoAxisUShort _stick2Precal;

    private readonly byte[] _sliderVal = [0, 0];

    private StickLimitsCalibration _stickCal = new();
    private StickLimitsCalibration _stick2Cal = new();
    private StickDeadZoneCalibration _deadZone;
    private StickDeadZoneCalibration _deadZone2;
    private StickRangeCalibration _range;
    private StickRangeCalibration _range2;
    private bool _userCalibrated;
    private StickLimitsCalibration? _userStickCal;
    private StickLimitsCalibration? _userStick2Cal;

    private Motion _motion = new();
    private readonly MadgwickAHRS _AHRS; // filtered rotation (DCM) for motion features and UDP gyro consumers
    private readonly float[] _curRotation = [0, 0, 0, 0, 0, 0];
    private long _lastMotionTicks;
    private bool _motionCalibrated;
    private ThreeAxisShort _motionNeutralGyro;
    private Joycon.Status _state = Joycon.Status.NotAttached;
    private bool _isLeft;
    private bool _calibrateMotion;
    private bool _calibrateSticks;

    // Optical mouse mode (Joy-Con 2 resting on a surface)
    private bool _mouseActive;
    private int _prevMouseX;
    private int _prevMouseY;
    private bool _prevMouseL;
    private bool _prevMouseR;
    private bool _prevMouseM;

    private CancellationTokenSource? _cts;

    public bool ActiveGyro { get; set; } = false;
    public BatteryLevel Battery { get; private set; } = BatteryLevel.Unknown;
    public bool Charging { get; private set; }
    public bool IsThirdParty { get; }
    public bool IsUSB { get; }
    public OutputControllerDualShock4 OutDs4 { get; }
    public OutputControllerXbox360 OutXbox { get; }
    public int PacketCounter { get; set; }
    public int PadId { get; }
    public MacAddress MacAddress { get; private set; } = new();
    public string Path { get; }
    public string SerialNumber { get; }
    public string SerialOrMac { get; private set; }
    public Joycon.Status State => _state;
    public ulong Timestamp { get; private set; }
    public long TimestampCreation { get; } = DateTime.UtcNow.Ticks;
    public Joycon.ControllerType Type { get; private set; }

    public event EventHandler<Joycon.StateChangedEventArgs>? StateChanged;

    public ConcurrentList<MotionShort> CalibrationMotionDatas { get; } = [];
    public ConcurrentList<SticksData> CalibrationStickDatas { get; } = [];

    // ------------------------------------------------------------------
    // Classification
    // ------------------------------------------------------------------
    public bool IsPro => Type == Joycon.ControllerType.Pro2;
    public bool IsGameCube => Type == Joycon.ControllerType.GameCubeNSO;
    public bool IsSNES => false;
    public bool IsNES => false;
    public bool IsFamicomI => false;
    public bool IsFamicomII => false;
    public bool IsN64 => false;
    public bool IsJoycon => Ns2Protocol.IsJoycon2Type(Type);
    public bool IsLeft => _isLeft;
    public bool IsJoined => Other != null && Other != this;
    public bool IsPrimaryGyro => Other == null || IsLeft;
    public bool IsDeviceReady => _state > Joycon.Status.Dropped;
    public bool IsDeviceError => !IsDeviceReady && _state != Joycon.Status.NotAttached;

    public Switch2Device? Other { get; set; }

    IControllerDevice? IControllerDevice.Other
    {
        get => Other;
        set => Other = (Switch2Device?)value;
    }

    // ------------------------------------------------------------------

    internal Switch2Device(
        ILogger? logger,
        MainForm form,
        ISwitch2Transport transport,
        string path,
        string serial,
        bool isUsb,
        int padId,
        Joycon.ControllerType type,
        bool isThirdParty = false)
    {
        _logger = logger;
        _form = form;
        _transport = transport;
        Path = path;
        SerialNumber = serial;
        IsUSB = isUsb;
        IsThirdParty = isThirdParty;
        PadId = padId;
        Type = type;
        _isLeft = type is Joycon.ControllerType.Joycon2Left;

        Config = new ControllerConfig(logger);
        _AHRS = new MadgwickAHRS(0.014f, Config.AHRSBeta);
        SerialOrMac = serial;

        var mac = transport.Mac;
        if (mac.Length >= 6)
        {
            var macAddress = MacAddress;
            for (var i = 0; i < 6; i++)
            {
                macAddress[i] = mac[i];
            }
            MacAddress = macAddress;
            SerialOrMac = MacAddress.ToString();
        }

        OutXbox = new OutputControllerXbox360(Ns2Protocol.VendorId, _transport.ProductId);
        OutXbox.FeedbackReceived += ReceiveRumble;
        OutDs4 = new OutputControllerDualShock4(Ns2Protocol.VendorId, _transport.ProductId);
        OutDs4.FeedbackReceived += Ds4_FeedbackReceived;

        _deadZone = StickDeadZoneCalibration.FromConfigLeft(Config);
        _deadZone2 = StickDeadZoneCalibration.FromConfigRight(Config);
        _range = StickRangeCalibration.FromConfigLeft(Config);
        _range2 = StickRangeCalibration.FromConfigRight(Config);

        _transport.InputReportReceived += OnInputReport;
        _transport.LinkDropped += OnLinkDropped;
    }

    private void Log(string message, LogLevel level = LogLevel.Debug)
    {
        _logger?.Log($"{GetControllerName()} [P{PadId + 1}] {message}", level);
    }

    private void Log(string message, Exception e, LogLevel level = LogLevel.Error)
    {
        _logger?.Log($"{GetControllerName()} [P{PadId + 1}] {message}", e, level);
    }

    private void SetState(Joycon.Status state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new Joycon.StateChangedEventArgs(state));
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void Attach()
    {
        _cts ??= new CancellationTokenSource();

        try
        {
            _transport.ConnectAsync(_cts.Token).GetAwaiter().GetResult();
            SetState(Joycon.Status.Attached);

            // Read factory stick calibration (SPI, command 02:04)
            LoadStickCalibrationAsync(_cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log("Attach failed.", e);
            SetState(Joycon.Status.Dropped);
            throw;
        }
    }

    private async Task LoadStickCalibrationAsync(CancellationToken ct)
    {
        try
        {
            var leftCal = await ReadStickCalibrationAsync(
                Ns2Protocol.AddrStick1UserCal, Ns2Protocol.AddrStick1FactoryCal, true, ct);

            if (leftCal != null)
            {
                _stickCal = leftCal;
            }

            if (IsJoycon)
            {
                _stick2Cal = _stickCal;
                return;
            }

            var rightCal = await ReadStickCalibrationAsync(
                Ns2Protocol.AddrStick2UserCal, Ns2Protocol.AddrStick2FactoryCal, false, ct);

            if (rightCal != null)
            {
                _stick2Cal = rightCal;
            }
        }
        catch (Exception e)
        {
            Log($"Stick calibration read failed ({e.Message}); using defaults.", LogLevel.Debug);
        }
    }

    private async Task<StickLimitsCalibration?> ReadStickCalibrationAsync(uint userAddr, uint factoryAddr, bool isLeft, CancellationToken ct)
    {
        // User calibration (11 bytes read at +2 past the magic): a leading
        // 0xFF FF FF marker means absent -> factory data. Verified layout:
        // parse calib triplets from the first 9 bytes of the record.
        var user = await _transport.ReadMemoryAsync(Ns2Protocol.StickCalibLen, userAddr, ct);
        if (Ns2Protocol.TryParseStickCalibration(user, out var userVals))
        {
            return new StickLimitsCalibration(userVals, isLeft);
        }

        var factory = await _transport.ReadMemoryAsync(Ns2Protocol.StickCalibLen, factoryAddr, ct);
        if (Ns2Protocol.TryParseStickCalibration(factory, out var factoryVals))
        {
            return new StickLimitsCalibration(factoryVals, isLeft);
        }

        return null;
    }

    public void Begin()
    {
        Log("Ready.");
    }

    public void Detach(bool close = true)
    {
        _cts?.Cancel();

        try
        {
            _transport.StopAsync().GetAwaiter().GetResult();
            _transport.Dispose();
        }
        catch (Exception e)
        {
            Log("Detach error.", e);
        }

        SetState(Joycon.Status.NotAttached);
    }

    public void Drop(bool error = false, bool waitThreads = true)
    {
        SetState(error ? Joycon.Status.Errored : Joycon.Status.Dropped);
    }

    public bool Reset() => true;

    public bool PowerOff()
    {
        RequestPowerOff();
        return true;
    }

    public void RequestPowerOff()
    {
        Detach();
    }

    public void WaitPowerOff(int timeoutMs)
    {
    }

    private void OnLinkDropped(object? sender, EventArgs e)
    {
        Log("Link dropped.");
        Drop();
    }

    // ------------------------------------------------------------------
    // Input processing
    // ------------------------------------------------------------------

    private void OnInputReport(object? sender, byte[] report)
    {
        try
        {
            ProcessReport(report);
        }
        catch (Exception e)
        {
            Log("Input report error.", e, LogLevel.Debug);
        }
    }

    private void ProcessReport(byte[] report)
    {
        if (!IsDeviceReady)
        {
            return;
        }

        if (!Ns2Protocol.TryDecodeInputReport(report, IsGameCube, out var input))
        {
            return;
        }

        // DSU timestamps are microseconds (ticks are 100 ns each).
        Timestamp = (ulong)(DateTime.UtcNow.Ticks / 10);
        PacketCounter++;

        // First report carrying IMU data: enables the calibration countdown
        // (MainForm requires Status.MotionDataOk before collecting samples).
        if (_state == Joycon.Status.Attached && input.HasMotion)
        {
            SetState(Joycon.Status.MotionDataOk);
        }

        Array.Clear(_buttons);
        var buttonsMask = input.Buttons;
        ProcessOpticalMouse(in input, ref buttonsMask);

        ApplyButtons(buttonsMask);
        CopyInputFromJoinedController();

        if (_calibrateSticks)
        {
            CalibrationStickDatas.Add(new SticksData(_stickPrecal, _stick2Precal));
        }

        ApplySticks(input);
        ApplyTriggers(input);
        ApplyMotion(input);
        ApplyBattery(input);

        // edge tracking for remapping
        // non-analog triggers: full travel when the digital button is pressed
        if (!IsGameCube)
        {
            _sliderVal[0] = _buttons[(int)Joycon.Button.Shoulder2] ? (byte)255 : (byte)0;
            _sliderVal[1] = _buttons[(int)Joycon.Button.Shoulder22] ? (byte)255 : (byte)0;
        }

        Array.Copy(_buttons, _buttonsRemapped, _buttons.Length);
        SimulateRemappedButtons();
        ReleaseRemappedButtons();

        // When joined, the left Joy-Con 2 owns the single virtual controller
        // (same convention as Joycon's mainController.UpdateInput()); a right
        // joycon's report merges its input into the left, which pushes it.
        var main = Other != null && !IsLeft ? Other : this;
        main.UpdateInput();
        SendMotionReport(main);
    }

    // Each device reports its own motion and pad id; when joined, the input
    // is the merged state owned by the left (main) device.
    private void SendMotionReport(Switch2Device inputSource)
    {
        if (Program.Server == null || !Program.Server.HasClients)
        {
            return;
        }

        var controllerReport = new UdpControllerReport(this);
        controllerReport.AddMotion(this, 0);
        // Added last so the packet carries the remapped state.
        controllerReport.AddInput(inputSource);
        Program.Server.SendControllerReport(controllerReport);
    }

    private void ApplyButtons(uint mask)
    {
        var buttons = _buttons;

        void Set(Joycon.Button b, uint bit) { if ((mask & bit) != 0) buttons[(int)b] = true; }

        if (IsJoycon && _isLeft)
        {
            Set(Joycon.Button.DpadDown, Ns2Protocol.BtnDown);
            Set(Joycon.Button.DpadRight, Ns2Protocol.BtnRight);
            Set(Joycon.Button.DpadLeft, Ns2Protocol.BtnLeft);
            Set(Joycon.Button.DpadUp, Ns2Protocol.BtnUp);
            Set(Joycon.Button.SL, Ns2Protocol.BtnSlL);
            Set(Joycon.Button.SR, Ns2Protocol.BtnSrL);
            Set(Joycon.Button.Shoulder1, Ns2Protocol.BtnL);
            Set(Joycon.Button.Shoulder2, Ns2Protocol.BtnZl);
            Set(Joycon.Button.Minus, Ns2Protocol.BtnMinus);
            Set(Joycon.Button.Home, Ns2Protocol.BtnHome);
            Set(Joycon.Button.Plus, Ns2Protocol.BtnPlus);
            Set(Joycon.Button.Capture, Ns2Protocol.BtnCapture);
            Set(Joycon.Button.Stick, Ns2Protocol.BtnLStick);
            Set(Joycon.Button.Chat, Ns2Protocol.BtnC);
            Set(Joycon.Button.Gl, Ns2Protocol.BtnGl);
            Set(Joycon.Button.Gr, Ns2Protocol.BtnGr);
        }
        else if (IsJoycon)
        {
            // right Joy-Con 2: face buttons land on the shared d-pad indices
            // (same convention as Switch 1) so joined merging works uniformly.
            Set(Joycon.Button.DpadDown, Ns2Protocol.BtnB);
            Set(Joycon.Button.DpadRight, Ns2Protocol.BtnA);
            Set(Joycon.Button.DpadLeft, Ns2Protocol.BtnY);
            Set(Joycon.Button.DpadUp, Ns2Protocol.BtnX);
            Set(Joycon.Button.SL, Ns2Protocol.BtnSlR);
            Set(Joycon.Button.SR, Ns2Protocol.BtnSrR);
            Set(Joycon.Button.Shoulder1, Ns2Protocol.BtnR);
            Set(Joycon.Button.Shoulder2, Ns2Protocol.BtnZr);
            Set(Joycon.Button.Minus, Ns2Protocol.BtnMinus);
            Set(Joycon.Button.Home, Ns2Protocol.BtnHome);
            Set(Joycon.Button.Plus, Ns2Protocol.BtnPlus);
            Set(Joycon.Button.Capture, Ns2Protocol.BtnCapture);
            Set(Joycon.Button.Stick, Ns2Protocol.BtnRStick);
            Set(Joycon.Button.Chat, Ns2Protocol.BtnC);
            Set(Joycon.Button.Gl, Ns2Protocol.BtnGl);
            Set(Joycon.Button.Gr, Ns2Protocol.BtnGr);
        }
        else
        {
            // Pro 2 / GameCube NSO: full pad layout
            Set(Joycon.Button.B, Ns2Protocol.BtnB);
            Set(Joycon.Button.A, Ns2Protocol.BtnA);
            Set(Joycon.Button.Y, Ns2Protocol.BtnY);
            Set(Joycon.Button.X, Ns2Protocol.BtnX);
            Set(Joycon.Button.DpadDown, Ns2Protocol.BtnDown);
            Set(Joycon.Button.DpadRight, Ns2Protocol.BtnRight);
            Set(Joycon.Button.DpadLeft, Ns2Protocol.BtnLeft);
            Set(Joycon.Button.DpadUp, Ns2Protocol.BtnUp);
            Set(Joycon.Button.Shoulder1, Ns2Protocol.BtnL);
            Set(Joycon.Button.Shoulder2, Ns2Protocol.BtnZl);
            Set(Joycon.Button.Shoulder21, Ns2Protocol.BtnR);
            Set(Joycon.Button.Shoulder22, Ns2Protocol.BtnZr);
            Set(Joycon.Button.Minus, Ns2Protocol.BtnMinus);
            Set(Joycon.Button.Plus, Ns2Protocol.BtnPlus);
            Set(Joycon.Button.Home, Ns2Protocol.BtnHome);
            Set(Joycon.Button.Capture, Ns2Protocol.BtnCapture);
            Set(Joycon.Button.Stick, Ns2Protocol.BtnLStick);
            Set(Joycon.Button.Stick2, Ns2Protocol.BtnRStick);
            Set(Joycon.Button.Chat, Ns2Protocol.BtnC);
            Set(Joycon.Button.Gl, Ns2Protocol.BtnGl);
            Set(Joycon.Button.Gr, Ns2Protocol.BtnGr);
        }
    }

    // Joy-Con 2 can rest on a surface and act as an optical mouse: the sensor
    // reports a distance (<1000, on a surface) and accumulated 16-bit
    // coordinates whose deltas move the cursor. While active, the buttons used
    // as mouse clicks are masked off so they don't reach the virtual pad.
    private void ProcessOpticalMouse(in Ns2Protocol.Ns2Input input, ref uint mask)
    {
        if (!IsJoycon)
        {
            return;
        }

        var active = input.MouseDistance != 0 && input.MouseDistance < 1000 && input.MouseRoughness < 4000;
        if (!active)
        {
            // Controller lifted mid-click: release any held mouse buttons so
            // the desktop isn't left dragging.
            if (_prevMouseL)
            {
                WindowsInput.Simulate.Events().Release(ButtonCode.Left).Invoke();
            }
            if (_prevMouseR)
            {
                WindowsInput.Simulate.Events().Release(ButtonCode.Right).Invoke();
            }
            if (_prevMouseM)
            {
                WindowsInput.Simulate.Events().Release(ButtonCode.Middle).Invoke();
            }
            _prevMouseL = _prevMouseR = _prevMouseM = false;
            _mouseActive = false;
            return;
        }

        if (_mouseActive)
        {
            var dx = SignedLoopingDifference16(_prevMouseX, input.MouseX);
            var dy = SignedLoopingDifference16(_prevMouseY, input.MouseY);
            if (dx != 0 || dy != 0)
            {
                // Optical counts are not gyro degrees: GyroMouseSensitivity
                // (~1200) scales angular motion, so use a dedicated factor.
                WindowsInput.Simulate.Events()
                    .MoveBy(
                        (int)(dx * OpticalMouseSensitivity),
                        (int)(dy * OpticalMouseSensitivity))
                    .Invoke();
            }

            var left = (mask & (Ns2Protocol.BtnZl | Ns2Protocol.BtnZr)) != 0;
            var right = (mask & (Ns2Protocol.BtnL | Ns2Protocol.BtnR)) != 0;
            var middle = (mask & (Ns2Protocol.BtnLStick | Ns2Protocol.BtnRStick)) != 0;
            if (left != _prevMouseL)
            {
                (left ? WindowsInput.Simulate.Events().Hold(ButtonCode.Left) : WindowsInput.Simulate.Events().Release(ButtonCode.Left)).Invoke();
            }
            if (right != _prevMouseR)
            {
                (right ? WindowsInput.Simulate.Events().Hold(ButtonCode.Right) : WindowsInput.Simulate.Events().Release(ButtonCode.Right)).Invoke();
            }
            if (middle != _prevMouseM)
            {
                (middle ? WindowsInput.Simulate.Events().Hold(ButtonCode.Middle) : WindowsInput.Simulate.Events().Release(ButtonCode.Middle)).Invoke();
            }
            _prevMouseL = left;
            _prevMouseR = right;
            _prevMouseM = middle;

            mask &= ~(Ns2Protocol.BtnZl | Ns2Protocol.BtnZr | Ns2Protocol.BtnL | Ns2Protocol.BtnR |
                      Ns2Protocol.BtnLStick | Ns2Protocol.BtnRStick);
        }

        _prevMouseX = input.MouseX;
        _prevMouseY = input.MouseY;
        _mouseActive = true;
    }

    private const float OpticalMouseSensitivity = 1.5f;

    private static int SignedLoopingDifference16(int previous, int current)
    {
        var diff = current - previous;
        if (diff > 0x8000)
        {
            diff -= 0x10000;
        }
        else if (diff < -0x8000)
        {
            diff += 0x10000;
        }

        return diff;
    }

    private void CopyInputFromJoinedController()
    {
        if (!GetMainAndOtherController(out var mainController, out var otherController))
        {
            return;
        }

        mainController._buttons[(int)Joycon.Button.B] = otherController._buttons[(int)Joycon.Button.DpadDown];
        mainController._buttons[(int)Joycon.Button.A] = otherController._buttons[(int)Joycon.Button.DpadRight];
        mainController._buttons[(int)Joycon.Button.X] = otherController._buttons[(int)Joycon.Button.DpadUp];
        mainController._buttons[(int)Joycon.Button.Y] = otherController._buttons[(int)Joycon.Button.DpadLeft];

        mainController._buttons[(int)Joycon.Button.Stick2] = otherController._buttons[(int)Joycon.Button.Stick];
        mainController._buttons[(int)Joycon.Button.Shoulder21] = otherController._buttons[(int)Joycon.Button.Shoulder1];
        mainController._buttons[(int)Joycon.Button.Shoulder22] = otherController._buttons[(int)Joycon.Button.Shoulder2];

        // Buttons shared across both halves (the right Joy-Con 2 owns the
        // hardware C button): merge so the joined output sees them.
        mainController._buttons[(int)Joycon.Button.Chat] |= otherController._buttons[(int)Joycon.Button.Chat];
        mainController._buttons[(int)Joycon.Button.Gl] |= otherController._buttons[(int)Joycon.Button.Gl];
        mainController._buttons[(int)Joycon.Button.Gr] |= otherController._buttons[(int)Joycon.Button.Gr];

        mainController._stick2 = otherController._stick;
        mainController._sliderVal[1] = otherController._sliderVal[1];
    }

    private bool GetMainAndOtherController(out Switch2Device main, [NotNullWhen(true)] out Switch2Device? other)
    {
        main = this;
        other = Other;

        if (other == null || other == this)
        {
            other = null;
            return false;
        }

        if (!main.IsLeft)
        {
            (main, other) = (other, main);
        }

        return true;
    }

    private void ApplySticks(in Ns2Protocol.Ns2Input input)
    {
        _stickPrecal = new TwoAxisUShort(input.LeftX, input.LeftY);
        _stick2Precal = new TwoAxisUShort(input.RightX, input.RightY);

        var cal = _userCalibrated && _userStickCal != null ? _userStickCal : _stickCal;
        var dz = _deadZone;
        var range = _range;
        var antiDeadzone = Config.StickLeftAntiDeadzone;

        if (_userCalibrated)
        {
            dz = StickDeadZoneCalibration.FromConfigLeft(Config);
            range = StickRangeCalibration.FromConfigLeft(Config);
        }

        CalculateStickCenter(_stickPrecal, cal, dz, range, antiDeadzone, ref _stick);

        if (!IsJoycon)
        {
            cal = _userCalibrated && _userStick2Cal != null ? _userStick2Cal : _stick2Cal;
            dz = _deadZone2;
            range = _range2;
            antiDeadzone = Config.StickRightAntiDeadzone;

            if (_userCalibrated)
            {
                dz = StickDeadZoneCalibration.FromConfigRight(Config);
                range = StickRangeCalibration.FromConfigRight(Config);
            }

            CalculateStickCenter(_stick2Precal, cal, dz, range, antiDeadzone, ref _stick2);
        }
        else
        {
            _stick2 = Stick.Zero;
        }
    }

    private void ApplyTriggers(in Ns2Protocol.Ns2Input input)
    {
        if (!IsGameCube)
        {
            return;
        }

        _sliderVal[0] = input.TriggerLeft;
        _sliderVal[1] = input.TriggerRight;
    }

    private void ApplyMotion(in Ns2Protocol.Ns2Input input)
    {
        if (!input.HasMotion)
        {
            return;
        }

        const float accelPerG = 4096f;
        var gyroPerDps = IsPro || IsGameCube ? 14.2857f : 16.384f;

        // Gyroscope in deg/s, accelerometer in g — same convention as Joycon
        // and what the DSU/Cemuhook packet expects. Only the gyro gets a user
        // offset (rest-position median): the accel median embeds gravity, so
        // subtracting it would remove the gravity reference the orientation
        // filter and DSU clients need.
        var nG = _motionCalibrated ? _motionNeutralGyro : ThreeAxisShort.Zero;
        _motion = new Motion(
            new Vector3(
                (input.GyroX - nG.X) / gyroPerDps,
                (input.GyroY - nG.Y) / gyroPerDps,
                (input.GyroZ - nG.Z) / gyroPerDps),
            new Vector3(
                input.AccelX / accelPerG,
                input.AccelY / accelPerG,
                input.AccelZ / accelPerG));

        // Feed the Madgwick filter (gyro in rad/s, accel in g). Timestamp is
        // in microseconds, so dt must be divided by 1e6, not TicksPerSecond.
        var now = Timestamp != 0 ? (long)Timestamp : DateTime.UtcNow.Ticks / 10;
        if (_lastMotionTicks != 0)
        {
            _AHRS.SamplePeriod = Math.Clamp((now - _lastMotionTicks) / 1_000_000f, 0.001f, 0.1f);
        }
        _lastMotionTicks = now;

        const float degToRad = 0.0174533f;
        _AHRS.Update(
            _motion.Gyroscope.X * degToRad,
            _motion.Gyroscope.Y * degToRad,
            _motion.Gyroscope.Z * degToRad,
            _motion.Accelerometer.X,
            _motion.Accelerometer.Y,
            _motion.Accelerometer.Z
        );
        _AHRS.GetEulerAngles(_curRotation);

        if (_calibrateMotion)
        {
            CalibrationMotionDatas.Add(new MotionShort(
                new ThreeAxisShort(input.GyroX, input.GyroY, input.GyroZ),
                new ThreeAxisShort(input.AccelX, input.AccelY, input.AccelZ)));
        }
    }

    private void ApplyBattery(in Ns2Protocol.Ns2Input input)
    {
        if (input.BatteryMillivolts == 0)
        {
            return;
        }

        // Lithium cell ~3.3 V empty .. ~4.2 V full
        var level = (int)Math.Clamp((input.BatteryMillivolts - 3300) / (float)(4200 - 3300) * 4, 0, 4);
        Battery = (BatteryLevel)(level * 2);
    }

    // ------------------------------------------------------------------
    // Stick math (ported from Joycon)
    // ------------------------------------------------------------------

    private void CalculateStickCenter(TwoAxisUShort vals, StickLimitsCalibration cal, float deadzone, float range, float[] antiDeadzone, ref Stick stick)
    {
        float dx = vals.X - cal.XCenter;
        float dy = vals.Y - cal.YCenter;

        float normalizedX = dx / (dx > 0 ? cal.XMax : cal.XMin);
        float normalizedY = dy / (dy > 0 ? cal.YMax : cal.YMin);

        float magnitude = MathF.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY);

        if (magnitude <= deadzone || range <= deadzone)
        {
            stick.X = 0.0f;
            stick.Y = 0.0f;
        }
        else
        {
            float normalizedMagnitudeX = Math.Min(1.0f, (magnitude - deadzone) / (range - deadzone));
            float normalizedMagnitudeY = normalizedMagnitudeX;

            if (antiDeadzone[0] > 0.0f)
            {
                normalizedMagnitudeX = antiDeadzone[0] + (1.0f - antiDeadzone[0]) * normalizedMagnitudeX;
            }

            if (antiDeadzone[1] > 0.0f)
            {
                normalizedMagnitudeY = antiDeadzone[1] + (1.0f - antiDeadzone[1]) * normalizedMagnitudeY;
            }

            normalizedX *= normalizedMagnitudeX / magnitude;
            normalizedY *= normalizedMagnitudeY / magnitude;

            if (Config.SticksShape == Joycon.StickShape.Circle || normalizedX == 0f || normalizedY == 0f)
            {
                stick.X = normalizedX;
                stick.Y = normalizedY;
            }
            else if (Config.SticksShape == Joycon.StickShape.N64)
            {
                float angle = MathF.Atan2(normalizedX, normalizedY);
                float radius = GetOctagonRadius(angle, cardinalRadius: 1.0f, diagonalRadius: 1.9f);

                stick.X = normalizedX * radius;
                stick.Y = normalizedY * radius;
            }
            else
            {
                if (Math.Abs(normalizedX) > Math.Abs(normalizedY))
                {
                    stick.X = Math.Sign(normalizedX) * normalizedMagnitudeX;
                    stick.Y = stick.X * normalizedY / normalizedX;
                }
                else
                {
                    stick.Y = Math.Sign(normalizedY) * normalizedMagnitudeY;
                    stick.X = stick.Y * normalizedX / normalizedY;
                }
            }

            stick.X = Math.Clamp(stick.X, -1.0f, 1.0f);
            stick.Y = Math.Clamp(stick.Y, -1.0f, 1.0f);
        }
    }

    private static float GetOctagonRadius(float angle, float cardinalRadius, float diagonalRadius)
    {
        float cos = MathF.Abs(MathF.Cos(angle));
        float sin = MathF.Abs(MathF.Sin(angle));

        float max = MathF.Max(cos, sin);
        float min = MathF.Min(cos, sin);

        float k = (cardinalRadius / diagonalRadius) * (MathF.Sqrt(2) - 1.0f);

        float baseRadius = 1.0f / (max + k * min);
        float radius = baseRadius * cardinalRadius;

        return radius;
    }

    // ------------------------------------------------------------------
    // Button remapping (same Settings contract as Joycon)
    // ------------------------------------------------------------------

    private void SimulateRemappedButtons()
    {
        SimulateContinous(Joycon.Button.Capture, "capture");
        SimulateContinous(Joycon.Button.Home, "home");

        if (_isLeft)
        {
            SimulateContinous(Joycon.Button.SL, "sl_l");
            SimulateContinous(Joycon.Button.SR, "sr_l");
        }
        else
        {
            SimulateContinous(Joycon.Button.SL, "sl_r");
            SimulateContinous(Joycon.Button.SR, "sr_r");
        }
    }

    private void SimulateContinous(Joycon.Button origin, string key)
    {
        var s = Settings.Value(key);
        if (s == "0" || string.IsNullOrEmpty(s) || !s.StartsWith("joy_"))
        {
            return;
        }

        var button = int.Parse(s.AsSpan(4));
        _buttonsRemapped[button] |= _buttons[(int)origin];
    }

    private void ReleaseRemappedButtons()
    {
        if (Settings.Value("capture") != "0")
        {
            _buttonsRemapped[(int)Joycon.Button.Capture] = false;
        }

        if (Settings.Value("home") != "0")
        {
            _buttonsRemapped[(int)Joycon.Button.Home] = false;
        }

        if (IsLeft)
        {
            if (Settings.Value("sl_l") != "0")
            {
                _buttonsRemapped[(int)Joycon.Button.SL] = false;
            }

            if (Settings.Value("sr_l") != "0")
            {
                _buttonsRemapped[(int)Joycon.Button.SR] = false;
            }
        }
        else
        {
            if (Settings.Value("sl_r") != "0")
            {
                _buttonsRemapped[(int)Joycon.Button.SL] = false;
            }

            if (Settings.Value("sr_r") != "0")
            {
                _buttonsRemapped[(int)Joycon.Button.SR] = false;
            }
        }
    }

    // ------------------------------------------------------------------
    // Virtual output
    // ------------------------------------------------------------------

    private void UpdateInput()
    {
        try
        {
            OutDs4.UpdateInput(MapToDualShock4Input());
            OutXbox.UpdateInput(MapToXbox360Input());
        }
        catch (Exception e)
        {
            Log("Cannot update input.", e, LogLevel.Debug);
        }
    }

    public OutputControllerDualShock4InputState MapToDualShock4Input()
    {
        var output = new OutputControllerDualShock4InputState();

        var isJoycon = IsJoycon;
        var isLeft = _isLeft;
        var buttons = _buttonsRemapped;
        var stick = _stick;
        var stick2 = _stick2;

        var swapAB = Config.SwapAB;
        var swapXY = Config.SwapXY;

        if (isJoycon)
        {
            if (Other != null)
            {
                output.Cross = buttons[(int)(isLeft ? Joycon.Button.B : Joycon.Button.DpadDown)];
                output.Circle = buttons[(int)(isLeft ? Joycon.Button.A : Joycon.Button.DpadRight)];
                output.Square = buttons[(int)(isLeft ? Joycon.Button.Y : Joycon.Button.DpadLeft)];
                output.Triangle = buttons[(int)(isLeft ? Joycon.Button.X : Joycon.Button.DpadUp)];

                output.DPad = Joycon.GetDirection(
                    buttons[(int)(isLeft ? Joycon.Button.DpadUp : Joycon.Button.X)],
                    buttons[(int)(isLeft ? Joycon.Button.DpadDown : Joycon.Button.B)],
                    buttons[(int)(isLeft ? Joycon.Button.DpadLeft : Joycon.Button.Y)],
                    buttons[(int)(isLeft ? Joycon.Button.DpadRight : Joycon.Button.A)]
                );

                output.Share = buttons[(int)Joycon.Button.Capture];
                output.Options = buttons[(int)Joycon.Button.Plus];
                output.Ps = buttons[(int)Joycon.Button.Home];
                output.Touchpad = buttons[(int)Joycon.Button.Minus] | buttons[(int)Joycon.Button.Chat];

                output.ShoulderLeft = buttons[(int)(isLeft ? Joycon.Button.Shoulder1 : Joycon.Button.Shoulder21)];
                output.ShoulderRight = buttons[(int)(isLeft ? Joycon.Button.Shoulder21 : Joycon.Button.Shoulder1)];

                output.TriggerLeft = buttons[(int)(isLeft ? Joycon.Button.Shoulder2 : Joycon.Button.Shoulder22)];
                output.TriggerRight = buttons[(int)(isLeft ? Joycon.Button.Shoulder22 : Joycon.Button.Shoulder2)];

                output.ThumbLeft = buttons[(int)(isLeft ? Joycon.Button.Stick : Joycon.Button.Stick2)];
                output.ThumbRight = buttons[(int)(isLeft ? Joycon.Button.Stick2 : Joycon.Button.Stick)];
            }
            else
            {
                // single joycon held horizontally
                output.Cross = buttons[(int)(isLeft ? Joycon.Button.DpadLeft : Joycon.Button.DpadRight)];
                output.Circle = buttons[(int)(isLeft ? Joycon.Button.DpadDown : Joycon.Button.DpadUp)];
                output.Square = buttons[(int)(isLeft ? Joycon.Button.DpadUp : Joycon.Button.DpadDown)];
                output.Triangle = buttons[(int)(isLeft ? Joycon.Button.DpadRight : Joycon.Button.DpadLeft)];

                output.Ps = buttons[(int)Joycon.Button.Minus] | buttons[(int)Joycon.Button.Home];
                output.Options = buttons[(int)Joycon.Button.Plus] | buttons[(int)Joycon.Button.Capture];

                output.ShoulderLeft = buttons[(int)Joycon.Button.SL];
                output.ShoulderRight = buttons[(int)Joycon.Button.SR];

                output.ThumbLeft = buttons[(int)Joycon.Button.Stick];
            }
        }
        else
        {
            output.Cross = buttons[(int)Joycon.Button.B];
            output.Circle = buttons[(int)Joycon.Button.A];
            output.Square = buttons[(int)Joycon.Button.Y];
            output.Triangle = buttons[(int)Joycon.Button.X];

            output.DPad = Joycon.GetDirection(
                buttons[(int)Joycon.Button.DpadUp],
                buttons[(int)Joycon.Button.DpadDown],
                buttons[(int)Joycon.Button.DpadLeft],
                buttons[(int)Joycon.Button.DpadRight]
            );

            output.Share = buttons[(int)Joycon.Button.Capture];
            output.Options = buttons[(int)Joycon.Button.Plus];
            output.Ps = buttons[(int)Joycon.Button.Home];
            output.Touchpad = buttons[(int)Joycon.Button.Minus] | buttons[(int)Joycon.Button.Chat];

            output.ShoulderLeft = buttons[(int)Joycon.Button.Shoulder1];
            output.ShoulderRight = buttons[(int)Joycon.Button.Shoulder21];
            output.TriggerLeft = buttons[(int)Joycon.Button.Shoulder2];
            output.TriggerRight = buttons[(int)Joycon.Button.Shoulder22];

            // Pro 2 back paddles default to the stick clicks (remappable).
            output.ThumbLeft = buttons[(int)Joycon.Button.Stick] | buttons[(int)Joycon.Button.Gl];
            output.ThumbRight = buttons[(int)Joycon.Button.Stick2] | buttons[(int)Joycon.Button.Gr];
        }

        if (swapAB)
        {
            (output.Cross, output.Circle) = (output.Circle, output.Cross);
        }

        if (swapXY)
        {
            (output.Square, output.Triangle) = (output.Triangle, output.Square);
        }

        output.TriggerLeftValue = _sliderVal[0];
        output.TriggerRightValue = _sliderVal[1];

        output.ThumbLeftX = (byte)Math.Clamp(stick.X * 127 + 128, 0, 255);
        output.ThumbLeftY = (byte)Math.Clamp(stick.Y * 127 + 128, 0, 255);
        output.ThumbRightX = (byte)Math.Clamp(stick2.X * 127 + 128, 0, 255);
        output.ThumbRightY = (byte)Math.Clamp(stick2.Y * 127 + 128, 0, 255);

        if (isJoycon && Other != null && !isLeft)
        {
            // joined right joycon mirrors through the left one
            output.ThumbLeftX = output.ThumbRightX;
            output.ThumbLeftY = output.ThumbRightY;
        }

        return output;
    }

    private OutputControllerXbox360InputState MapToXbox360Input()
    {
        var output = new OutputControllerXbox360InputState();

        var isJoycon = IsJoycon;
        var isLeft = _isLeft;
        var buttons = _buttonsRemapped;
        var stick = _stick;
        var stick2 = _stick2;

        var swapAB = Config.SwapAB;
        var swapXY = Config.SwapXY;

        if (isJoycon)
        {
            if (Other != null)
            {
                output.A = buttons[(int)(isLeft ? Joycon.Button.B : Joycon.Button.DpadDown)];
                output.B = buttons[(int)(isLeft ? Joycon.Button.A : Joycon.Button.DpadRight)];
                output.X = buttons[(int)(isLeft ? Joycon.Button.Y : Joycon.Button.DpadLeft)];
                output.Y = buttons[(int)(isLeft ? Joycon.Button.X : Joycon.Button.DpadUp)];

                output.DpadUp = buttons[(int)(isLeft ? Joycon.Button.DpadUp : Joycon.Button.X)];
                output.DpadDown = buttons[(int)(isLeft ? Joycon.Button.DpadDown : Joycon.Button.B)];
                output.DpadLeft = buttons[(int)(isLeft ? Joycon.Button.DpadLeft : Joycon.Button.Y)];
                output.DpadRight = buttons[(int)(isLeft ? Joycon.Button.DpadRight : Joycon.Button.A)];

                output.Back = buttons[(int)Joycon.Button.Minus] | buttons[(int)Joycon.Button.Chat];
                output.Start = buttons[(int)Joycon.Button.Plus];
                output.Guide = buttons[(int)Joycon.Button.Home];

                output.ShoulderLeft = buttons[(int)(isLeft ? Joycon.Button.Shoulder1 : Joycon.Button.Shoulder21)];
                output.ShoulderRight = buttons[(int)(isLeft ? Joycon.Button.Shoulder21 : Joycon.Button.Shoulder1)];

                output.ThumbStickLeft = buttons[(int)(isLeft ? Joycon.Button.Stick : Joycon.Button.Stick2)];
                output.ThumbStickRight = buttons[(int)(isLeft ? Joycon.Button.Stick2 : Joycon.Button.Stick)];

                output.TriggerLeft = (byte)(buttons[(int)(isLeft ? Joycon.Button.Shoulder2 : Joycon.Button.Shoulder22)] ? 255 : _sliderVal[0]);
                output.TriggerRight = (byte)(buttons[(int)(isLeft ? Joycon.Button.Shoulder22 : Joycon.Button.Shoulder2)] ? 255 : _sliderVal[1]);
            }
            else
            {
                output.A = buttons[(int)(isLeft ? Joycon.Button.DpadLeft : Joycon.Button.DpadRight)];
                output.B = buttons[(int)(isLeft ? Joycon.Button.DpadDown : Joycon.Button.DpadUp)];
                output.X = buttons[(int)(isLeft ? Joycon.Button.DpadUp : Joycon.Button.DpadDown)];
                output.Y = buttons[(int)(isLeft ? Joycon.Button.DpadRight : Joycon.Button.DpadLeft)];

                output.Back = buttons[(int)Joycon.Button.Minus] | buttons[(int)Joycon.Button.Home] | buttons[(int)Joycon.Button.Chat];
                output.Start = buttons[(int)Joycon.Button.Plus] | buttons[(int)Joycon.Button.Capture];

                output.ShoulderLeft = buttons[(int)Joycon.Button.SL];
                output.ShoulderRight = buttons[(int)Joycon.Button.SR];

                output.ThumbStickLeft = buttons[(int)Joycon.Button.Stick];

                output.TriggerLeft = _sliderVal[0];
                output.TriggerRight = _sliderVal[1];
            }
        }
        else
        {
            output.A = buttons[(int)Joycon.Button.B];
            output.B = buttons[(int)Joycon.Button.A];
            output.X = buttons[(int)Joycon.Button.Y];
            output.Y = buttons[(int)Joycon.Button.X];

            output.DpadUp = buttons[(int)Joycon.Button.DpadUp];
            output.DpadDown = buttons[(int)Joycon.Button.DpadDown];
            output.DpadLeft = buttons[(int)Joycon.Button.DpadLeft];
            output.DpadRight = buttons[(int)Joycon.Button.DpadRight];

            output.Back = buttons[(int)Joycon.Button.Minus] | buttons[(int)Joycon.Button.Chat];
            output.Start = buttons[(int)Joycon.Button.Plus];
            output.Guide = buttons[(int)Joycon.Button.Home];

            output.ShoulderLeft = buttons[(int)Joycon.Button.Shoulder1];
            output.ShoulderRight = buttons[(int)Joycon.Button.Shoulder21];

            output.ThumbStickLeft = buttons[(int)Joycon.Button.Stick] | buttons[(int)Joycon.Button.Gl];
            output.ThumbStickRight = buttons[(int)Joycon.Button.Stick2] | buttons[(int)Joycon.Button.Gr];

            output.TriggerLeft = buttons[(int)Joycon.Button.Shoulder2] ? (byte)255 : _sliderVal[0];
            output.TriggerRight = buttons[(int)Joycon.Button.Shoulder22] ? (byte)255 : _sliderVal[1];
        }

        if (swapAB)
        {
            (output.A, output.B) = (output.B, output.A);
        }

        if (swapXY)
        {
            (output.X, output.Y) = (output.Y, output.X);
        }

        output.AxisLeftX = (short)Math.Clamp(stick.X * short.MaxValue, short.MinValue, short.MaxValue);
        output.AxisLeftY = (short)Math.Clamp(stick.Y * short.MaxValue, short.MinValue, short.MaxValue);
        output.AxisRightX = (short)Math.Clamp(stick2.X * short.MaxValue, short.MinValue, short.MaxValue);
        output.AxisRightY = (short)Math.Clamp(stick2.Y * short.MaxValue, short.MinValue, short.MaxValue);

        return output;
    }

    // ------------------------------------------------------------------
    // ViGEm
    // ------------------------------------------------------------------

    public bool IsViGEmSetup() => OutXbox.IsConnected() || OutDs4.IsConnected();

    public void ConnectViGEm()
    {
        if (Config.ShowAsXInput)
        {
            OutXbox.Connect();
        }

        if (Config.ShowAsDs4)
        {
            OutDs4.Connect();
        }
    }

    public void DisconnectViGEm()
    {
        OutXbox.Disconnect();
        OutDs4.Disconnect();
    }

    // ------------------------------------------------------------------
    // Rumble / LEDs
    // ------------------------------------------------------------------

    public void SetRumble(float lowFreq, float highFreq, float lowAmplitude, float highAmplitude)
    {
        _transport.SetRumble(lowAmplitude > 0.01f || highAmplitude > 0.01f);
    }

    public void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e)
    {
        _transport.SetRumble(e.LargeMotor > 0 || e.SmallMotor > 0);
    }

    public void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e)
    {
        _transport.SetRumble(e.LargeMotor > 0 || e.SmallMotor > 0);
    }

    public bool SetLEDByPlayerNum(int id)
    {
        try
        {
            _transport.SetPlayerLedAsync(id, CancellationToken.None).Wait(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetLEDByPadID()
    {
        // Pad ids are 0-based (LED pattern indexes); joined joycons share the
        // pair's lowest pad id, same as Joycon.SetLEDByPadID.
        var id = IsJoined && Other != null ? Math.Min(Other.PadId, PadId) : PadId;
        return SetLEDByPlayerNum(id);
    }

    public void RequestSetLEDByPadID()
    {
        Task.Run(() => SetLEDByPadID());
    }

    public bool SetPlayerLED(byte leds = 0x00)
    {
        try
        {
            _transport.SendCommandAsync(0x09, 0x07, new byte[] { leds, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, CancellationToken.None)
                .Wait(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void BlinkHomeLight() { }

    public bool SetHomeLight(bool on) => false;

    // ------------------------------------------------------------------
    // Motion / calibration plumbing
    // ------------------------------------------------------------------

    public Motion GetMotion() => _motion;

    public bool MotionSupported() => true;

    public bool HomeLightSupported() => false;

    public string GetControllerName() => Joycon.GetControllerName(Type);

    public void GetActiveMotionData()
    {
        var activeMotionData = _form.ActiveCalibrationMotionData(SerialOrMac);
        if (activeMotionData != null)
        {
            _motionNeutralGyro = new ThreeAxisShort(activeMotionData[0], activeMotionData[1], activeMotionData[2]);
            _motionCalibrated = true;
        }
        else
        {
            _motionCalibrated = false;
        }
    }

    public void GetActiveSticksData()
    {
        var activeSticksData = _form.ActiveCaliSticksData(SerialOrMac);
        if (activeSticksData != null)
        {
            _userStickCal = new StickLimitsCalibration(activeSticksData.AsSpan(0, 6), true);
            _userStick2Cal = new StickLimitsCalibration(activeSticksData.AsSpan(6, 6), false);
            _userCalibrated = true;
        }
    }

    public void StartMotionCalibration()
    {
        CalibrationMotionDatas.Clear();
        _calibrateMotion = true;
    }

    public void StopMotionCalibration(bool clean = false)
    {
        _calibrateMotion = false;
        if (clean)
        {
            CalibrationMotionDatas.Clear();
        }
    }

    public void StartSticksCalibration()
    {
        CalibrationStickDatas.Clear();
        _calibrateSticks = true;
    }

    public void StopSticksCalibration(bool clean = false)
    {
        _calibrateSticks = false;
        if (clean)
        {
            CalibrationStickDatas.Clear();
        }
    }

    public void SetCalibration(bool userCalibration)
    {
        if (userCalibration)
        {
            GetActiveMotionData();
            GetActiveSticksData();
        }
        else
        {
            _motionCalibrated = false;
            _userCalibrated = false;
            _userStickCal = null;
            _userStick2Cal = null;
        }
    }

    public void ApplyConfig(bool showErrors = true)
    {
        Config.Update();
        RequestSetLEDByPadID();
    }
}
