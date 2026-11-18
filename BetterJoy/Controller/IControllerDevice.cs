using System;
using BetterJoy.Collections;
using BetterJoy.Config;
using BetterJoy.Controller.Mapping;
using BetterJoy.Hardware.Data;
using BetterJoy.Hardware.SubCommand;
using BetterJoy.Network;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BetterJoy.Controller;

/// <summary>
/// Façade over a controller managed by BetterJoy. Implemented by <see cref="Joycon"/>
/// (Switch 1 controllers over HID) and by the Switch 2 controllers (BLE/USB), so the
/// manager, the UDP server and the UI can handle both generations uniformly.
/// </summary>
public interface IControllerDevice
{
    ControllerConfig Config { get; }
    bool ActiveGyro { get; set; }
    BatteryLevel Battery { get; }
    bool Charging { get; }
    bool IsThirdParty { get; }
    bool IsUSB { get; }
    OutputControllerDualShock4 OutDs4 { get; }
    OutputControllerXbox360 OutXbox { get; }
    int PacketCounter { get; set; }
    int PadId { get; }
    MacAddress MacAddress { get; }
    string Path { get; }
    string SerialNumber { get; }
    string SerialOrMac { get; }
    Joycon.Status State { get; }
    ulong Timestamp { get; }
    long TimestampCreation { get; }
    Joycon.ControllerType Type { get; }
    event EventHandler<Joycon.StateChangedEventArgs>? StateChanged;
    ConcurrentList<MotionShort> CalibrationMotionDatas { get; }
    ConcurrentList<SticksData> CalibrationStickDatas { get; }

    bool IsPro { get; }
    bool IsSNES { get; }
    bool IsNES { get; }
    bool IsFamicomI { get; }
    bool IsFamicomII { get; }
    bool IsN64 { get; }
    bool IsJoycon { get; }
    bool IsLeft { get; }
    bool IsJoined { get; }
    bool IsPrimaryGyro { get; }
    bool IsDeviceReady { get; }
    bool IsDeviceError { get; }
    IControllerDevice? Other { get; set; }

    void Attach();
    void Begin();
    void Detach(bool close = true);
    void Drop(bool error = false, bool waitThreads = true);
    bool Reset();
    bool PowerOff();
    void RequestPowerOff();
    void WaitPowerOff(int timeoutMs);
    void ApplyConfig(bool showErrors = true);
    void SetCalibration(bool userCalibration);

    bool IsViGEmSetup();
    void ConnectViGEm();
    void DisconnectViGEm();
    void SetRumble(float lowFreq, float highFreq, float lowAmplitude, float highAmplitude);
    void ReceiveRumble(Xbox360FeedbackReceivedEventArgs e);
    void Ds4_FeedbackReceived(DualShock4FeedbackReceivedEventArgs e);

    bool SetLEDByPlayerNum(int id);
    bool SetLEDByPadID();
    void RequestSetLEDByPadID();
    bool SetPlayerLED(byte leds = 0x00);
    void BlinkHomeLight();
    bool SetHomeLight(bool on);

    Motion GetMotion();
    OutputControllerDualShock4InputState MapToDualShock4Input();
    bool MotionSupported();
    bool HomeLightSupported();
    string GetControllerName();

    void GetActiveMotionData();
    void GetActiveSticksData();
    void StartMotionCalibration();
    void StopMotionCalibration(bool clean = false);
    void StartSticksCalibration();
    void StopSticksCalibration(bool clean = false);
}
