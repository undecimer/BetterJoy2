using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using System;

namespace BetterJoy.Controller.Mapping;

public enum DpadDirection
{
    None,
    Northwest,
    West,
    Southwest,
    South,
    Southeast,
    East,
    Northeast,
    North
}

public struct OutputControllerDualShock4InputState
{
    public bool Triangle;
    public bool Circle;
    public bool Cross;
    public bool Square;

    public bool TriggerLeft;
    public bool TriggerRight;

    public bool ShoulderLeft;
    public bool ShoulderRight;

    public bool Options;
    public bool Share;
    public bool Ps;
    public bool Touchpad;

    public bool ThumbLeft;
    public bool ThumbRight;

    public DpadDirection DPad;

    public byte ThumbLeftX;
    public byte ThumbLeftY;
    public byte ThumbRightX;
    public byte ThumbRightY;

    public byte TriggerLeftValue;
    public byte TriggerRightValue;

    public short GyroX;
    public short GyroY;
    public short GyroZ;

    public short AccelX;
    public short AccelY;
    public short AccelZ;

    public ushort Timestamp;
    public bool HasMotion;

    public readonly bool IsEqual(OutputControllerDualShock4InputState other)
    {
        if (HasMotion || other.HasMotion)
        {
            return false;
        }

        var buttons = Triangle == other.Triangle
                      && Circle == other.Circle
                      && Cross == other.Cross
                      && Square == other.Square
                      && TriggerLeft == other.TriggerLeft
                      && TriggerRight == other.TriggerRight
                      && ShoulderLeft == other.ShoulderLeft
                      && ShoulderRight == other.ShoulderRight
                      && Options == other.Options
                      && Share == other.Share
                      && Ps == other.Ps
                      && Touchpad == other.Touchpad
                      && ThumbLeft == other.ThumbLeft
                      && ThumbRight == other.ThumbRight
                      && DPad == other.DPad;

        var axis = ThumbLeftX == other.ThumbLeftX
                   && ThumbLeftY == other.ThumbLeftY
                   && ThumbRightX == other.ThumbRightX
                   && ThumbRightY == other.ThumbRightY;

        var triggers = TriggerLeftValue == other.TriggerLeftValue
                       && TriggerRightValue == other.TriggerRightValue;

        return buttons && axis && triggers;
    }
}

public class OutputControllerDualShock4
{
    public delegate void DualShock4FeedbackReceivedEventHandler(DualShock4FeedbackReceivedEventArgs e);

    private readonly IDualShock4Controller? _controller;
    private readonly byte[] _rawReportEx = new byte[63];

    private OutputControllerDualShock4InputState _currentState;

    private byte _touchPacketCounter;
    private bool _connected = false;

    public OutputControllerDualShock4()
    {
        if (Program.EmClient == null)
        {
            return;
        }

        _controller = Program.EmClient.CreateDualShock4Controller();
        Init();
    }

    public OutputControllerDualShock4(ushort vendorId, ushort productId)
    {
        if (Program.EmClient == null)
        {
            return;
        }

        _controller = Program.EmClient.CreateDualShock4Controller(vendorId, productId);
        Init();
    }

    public event DualShock4FeedbackReceivedEventHandler? FeedbackReceived;

    private void Init()
    {
        if (_controller == null)
        {
            return;
        }

        _controller.AutoSubmitReport = false;
#pragma warning disable CS0618 // Type or member is obsolete
        _controller.FeedbackReceived += FeedbackReceivedRcv;
#pragma warning restore CS0618
    }

    private void FeedbackReceivedRcv(object sender, DualShock4FeedbackReceivedEventArgs e)
    {
        FeedbackReceived?.Invoke(e);
    }

    public bool IsConnected()
    {
        return _connected;
    }

    public void Connect()
    {
        if (_controller == null)
        {
            return;
        }

        _controller.Connect();
        _connected = true;
    }

    public void Disconnect()
    {
        _connected = false;

        try
        {
            _controller?.Disconnect();
        }
        catch { } // nothing we can do, might not be connected in the first place
    }

    public bool UpdateInput(OutputControllerDualShock4InputState newState)
    {
        if (!_connected || _currentState.IsEqual(newState))
        {
            return false;
        }

        DoUpdateInput(newState);

        return true;
    }

    private void DoUpdateInput(OutputControllerDualShock4InputState newState)
    {
        if (_controller == null)
        {
            return;
        }

        if (newState.HasMotion)
        {
            SubmitExtendedReport(newState);
            _currentState = newState;
            return;
        }

        _controller.SetButtonState(DualShock4Button.Triangle, newState.Triangle);
        _controller.SetButtonState(DualShock4Button.Circle, newState.Circle);
        _controller.SetButtonState(DualShock4Button.Cross, newState.Cross);
        _controller.SetButtonState(DualShock4Button.Square, newState.Square);

        _controller.SetButtonState(DualShock4Button.ShoulderLeft, newState.ShoulderLeft);
        _controller.SetButtonState(DualShock4Button.ShoulderRight, newState.ShoulderRight);

        _controller.SetButtonState(DualShock4Button.TriggerLeft, newState.TriggerLeft);
        _controller.SetButtonState(DualShock4Button.TriggerRight, newState.TriggerRight);

        _controller.SetButtonState(DualShock4Button.ThumbLeft, newState.ThumbLeft);
        _controller.SetButtonState(DualShock4Button.ThumbRight, newState.ThumbRight);

        _controller.SetButtonState(DualShock4Button.Share, newState.Share);
        _controller.SetButtonState(DualShock4Button.Options, newState.Options);
        _controller.SetButtonState(DualShock4SpecialButton.Ps, newState.Ps);
        _controller.SetButtonState(DualShock4SpecialButton.Touchpad, newState.Touchpad);

        _controller.SetDPadDirection(MapDPadDirection(newState.DPad));

        _controller.SetAxisValue(DualShock4Axis.LeftThumbX, newState.ThumbLeftX);
        _controller.SetAxisValue(DualShock4Axis.LeftThumbY, newState.ThumbLeftY);
        _controller.SetAxisValue(DualShock4Axis.RightThumbX, newState.ThumbRightX);
        _controller.SetAxisValue(DualShock4Axis.RightThumbY, newState.ThumbRightY);

        _controller.SetSliderValue(DualShock4Slider.LeftTrigger, newState.TriggerLeftValue);
        _controller.SetSliderValue(DualShock4Slider.RightTrigger, newState.TriggerRightValue);

        _controller.SubmitReport();

        _currentState = newState;
    }

    private void SubmitExtendedReport(OutputControllerDualShock4InputState newState)
    {
        var report = _rawReportEx.AsSpan();
        report.Clear();

        report[0] = newState.ThumbLeftX;
        report[1] = newState.ThumbLeftY;
        report[2] = newState.ThumbRightX;
        report[3] = newState.ThumbRightY;

        var buttons = MapDPadDirection(newState.DPad).Value;
        if (newState.Square) buttons |= DualShock4Button.Square.Value;
        if (newState.Cross) buttons |= DualShock4Button.Cross.Value;
        if (newState.Circle) buttons |= DualShock4Button.Circle.Value;
        if (newState.Triangle) buttons |= DualShock4Button.Triangle.Value;
        if (newState.ShoulderLeft) buttons |= DualShock4Button.ShoulderLeft.Value;
        if (newState.ShoulderRight) buttons |= DualShock4Button.ShoulderRight.Value;
        if (newState.TriggerLeft) buttons |= DualShock4Button.TriggerLeft.Value;
        if (newState.TriggerRight) buttons |= DualShock4Button.TriggerRight.Value;
        if (newState.Share) buttons |= DualShock4Button.Share.Value;
        if (newState.Options) buttons |= DualShock4Button.Options.Value;
        if (newState.ThumbLeft) buttons |= DualShock4Button.ThumbLeft.Value;
        if (newState.ThumbRight) buttons |= DualShock4Button.ThumbRight.Value;
        BitConverter.TryWriteBytes(report.Slice(4, 2), buttons);

        byte special = 0;
        if (newState.Ps) special |= (byte)DualShock4SpecialButton.Ps.Value;
        if (newState.Touchpad) special |= (byte)DualShock4SpecialButton.Touchpad.Value;
        report[6] = special;

        report[7] = newState.TriggerLeftValue;
        report[8] = newState.TriggerRightValue;

        BitConverter.TryWriteBytes(report.Slice(9, 2), newState.Timestamp);
        report[11] = 0xAF; // bBatteryLvl

        BitConverter.TryWriteBytes(report.Slice(12, 2), newState.GyroX);
        BitConverter.TryWriteBytes(report.Slice(14, 2), newState.GyroY);
        BitConverter.TryWriteBytes(report.Slice(16, 2), newState.GyroZ);
        BitConverter.TryWriteBytes(report.Slice(18, 2), newState.AccelX);
        BitConverter.TryWriteBytes(report.Slice(20, 2), newState.AccelY);
        BitConverter.TryWriteBytes(report.Slice(22, 2), newState.AccelZ);

        report[29] = 0x08; // bBatteryLvlSpecial
        report[32] = 0x01; // bTouchPacketsN
        report[33] = _touchPacketCounter++;
        report[34] = 0x80; // current touch finger 1 up
        report[38] = 0x80; // current touch finger 2 up
        report[43] = 0x80; // previous touch 0 finger 1 up
        report[47] = 0x80; // previous touch 0 finger 2 up
        report[52] = 0x80; // previous touch 1 finger 1 up
        report[56] = 0x80; // previous touch 1 finger 2 up

        _controller!.SubmitRawReport(_rawReportEx);
    }

    private static DualShock4DPadDirection MapDPadDirection(DpadDirection dPad)
    {
        return dPad switch
        {
            DpadDirection.None => DualShock4DPadDirection.None,
            DpadDirection.North => DualShock4DPadDirection.North,
            DpadDirection.Northeast => DualShock4DPadDirection.Northeast,
            DpadDirection.East => DualShock4DPadDirection.East,
            DpadDirection.Southeast => DualShock4DPadDirection.Southeast,
            DpadDirection.South => DualShock4DPadDirection.South,
            DpadDirection.Southwest => DualShock4DPadDirection.Southwest,
            DpadDirection.West => DualShock4DPadDirection.West,
            DpadDirection.Northwest => DualShock4DPadDirection.Northwest,
            _ => throw new NotImplementedException(),
        };
    }
}
