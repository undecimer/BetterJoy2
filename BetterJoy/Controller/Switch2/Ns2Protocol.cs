using System;
using System.Collections.Generic;

namespace BetterJoy.Controller.Switch2;

/// <summary>
/// Nintendo Switch 2 controller protocol: USB PIDs, GATT UUIDs, command frames,
/// input report decoding and factory stick calibration.
/// Byte layouts verified against Switch2Connect, joycon2pc, XinHeLianSheng-Pro2-Bridge
/// and NSW2-controller-enabler. See docs/SWITCH2_PROTOCOL_NOTES.md.
/// </summary>
internal static class Ns2Protocol
{
    public const ushort VendorId = 0x057E;
    public const ushort PidJoycon2Right = 0x2066;
    public const ushort PidJoycon2Left = 0x2067;
    public const ushort PidPro2 = 0x2069;
    public const ushort PidGameCubeNso = 0x2073;

    // BLE PnP ID characteristic values (service 0x180A / char 0x2A50)
    public const ushort BlePnpJoycon2Left = 0x6605;
    public const ushort BlePnpJoycon2Right = 0x6705;

    // Manufacturer ID present in the BLE advertisement while pairing
    public const ushort BleManufacturerId = 0x0553;

    public static readonly Guid ServiceGuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd0");
    public static readonly Guid InputCharGuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    public static readonly Guid CommandCharGuid = new("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    public static readonly Guid ResponseCharGuid = new("c765a961-d9d8-4d36-a20a-5315b111836a");
    public static readonly Guid PnpIdCharGuid = new("00002a50-0000-1000-8000-00805f9b34fb");

    // Rumble characteristic differs per device; GameCube has no known dedicated
    // UUID, so its rumble/keep-alive frames go over the command characteristic.
    public static readonly Guid RumbleCharJoycon2Right = new("fa19b0fb-cd1f-46a7-84a1-bbb09e00c149");
    public static readonly Guid RumbleCharJoycon2Left = new("289326cb-a471-485d-a8f4-240c14f18241");
    public static readonly Guid RumbleCharPro2 = new("cc483f51-9258-427d-a939-630c31f72b05");

    // SPI/flash memory addresses for factory stick calibration (read via command 02:04)
    public const uint AddrStick1FactoryCal = 0x0130A8;
    public const uint AddrStick2FactoryCal = 0x0130E8;
    public const uint AddrStick1UserCal = 0x1FC042;
    public const uint AddrStick2UserCal = 0x1FC062;
    public const int StickCalibLen = 0x0B;

    public const int StandardReportLen = 63;

    public static bool IsSwitch2Pid(ushort productId)
    {
        return productId is PidJoycon2Right or PidJoycon2Left or PidPro2 or PidGameCubeNso;
    }

    public static bool TryGetControllerType(ushort productId, out Joycon.ControllerType type)
    {
        type = productId switch
        {
            PidJoycon2Left or BlePnpJoycon2Left => Joycon.ControllerType.Joycon2Left,
            PidJoycon2Right or BlePnpJoycon2Right => Joycon.ControllerType.Joycon2Right,
            PidPro2 => Joycon.ControllerType.Pro2,
            PidGameCubeNso => Joycon.ControllerType.GameCubeNSO,
            _ => Joycon.ControllerType.Unknown
        };

        return type != Joycon.ControllerType.Unknown;
    }

    public static ushort PidForControllerType(Joycon.ControllerType type)
    {
        return type switch
        {
            Joycon.ControllerType.Joycon2Left => PidJoycon2Left,
            Joycon.ControllerType.Joycon2Right => PidJoycon2Right,
            Joycon.ControllerType.Pro2 => PidPro2,
            Joycon.ControllerType.GameCubeNSO => PidGameCubeNso,
            _ => 0
        };
    }

    public static bool IsJoycon2Type(Joycon.ControllerType type)
    {
        return type is Joycon.ControllerType.Joycon2Left or Joycon.ControllerType.Joycon2Right;
    }

    public static bool IsSwitch2Type(Joycon.ControllerType type)
    {
        return type is Joycon.ControllerType.Joycon2Left or Joycon.ControllerType.Joycon2Right
            or Joycon.ControllerType.Pro2 or Joycon.ControllerType.GameCubeNSO;
    }

    public static bool IsLeftJoyconType(Joycon.ControllerType type)
    {
        return type is Joycon.ControllerType.JoyconLeft or Joycon.ControllerType.Joycon2Left;
    }

    public static bool IsRightJoyconType(Joycon.ControllerType type)
    {
        return type is Joycon.ControllerType.JoyconRight or Joycon.ControllerType.Joycon2Right;
    }

    public static Guid RumbleCharFor(ushort productId)
    {
        return productId switch
        {
            PidJoycon2Left or BlePnpJoycon2Left => RumbleCharJoycon2Left,
            PidPro2 => RumbleCharPro2,
            _ => RumbleCharJoycon2Right
        };
    }

    // Command frame: [cmd, 0x91, transport, sub, 0x00, len, 0x00, 0x00, data...]
    // transport byte is 0x01 over BLE and 0x00 over USB.
    public static byte[] BuildCommand(byte command, byte subcommand, ReadOnlySpan<byte> data, bool usb)
    {
        var frame = new byte[8 + data.Length];
        frame[0] = command;
        frame[1] = 0x91;
        frame[2] = usb ? (byte)0x00 : (byte)0x01;
        frame[3] = subcommand;
        frame[5] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(8));

        return frame;
    }

    // read_memory command (02:04): [len, 0x7e, 0, 0, addr u32 LE]
    public static byte[] BuildReadMemory(int length, uint address)
    {
        var data = new byte[8];
        data[0] = (byte)length;
        data[1] = 0x7E;
        BitConverter.TryWriteBytes(data.AsSpan(4), address);

        return data;
    }

    // Verifies a read_memory reply; payload starts at offset 8.
    public static bool TryParseReadMemoryReply(ReadOnlySpan<byte> response, int length, uint address, out ReadOnlySpan<byte> payload)
    {
        payload = default;

        if (response.Length < 8 + length ||
            response[0] != length ||
            BitConverter.ToUInt32(response.Slice(4, 4)) != address)
        {
            return false;
        }

        payload = response.Slice(8, length);

        return true;
    }

    // Keep-alive / simple rumble frame sent ~every 15 ms on the rumble characteristic.
    public static byte[] BuildRumbleFrame(int counter, bool rumbleOn)
    {
        var frame = new byte[8];
        frame[0] = 0x50;
        frame[1] = (byte)(0x50 | (counter & 0x0F));
        frame[2] = rumbleOn ? (byte)0x01 : (byte)0x00;

        return frame;
    }

    // Raw write to the command characteristic to select input format 3
    // (Joy-Con 2 and GameCube only, NOT Pro 2) plus button mask 0x03FFFFFF.
    public static readonly byte[] InputFormat3Frame = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x30];

    /// <summary>
    /// BLE initialization sequence, in order. Verified against Switch2Connect.
    /// The (0x01, 0x01) step must be skipped on the Pro 2 (it returns status=4).
    /// </summary>
    public static IReadOnlyList<(byte Command, byte Sub, byte[] Data)> GetBleInitSequence(ushort productId)
    {
        var steps = new List<(byte, byte, byte[])>
        {
            (0x03, 0x0D, [0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
            (0x07, 0x01, []),
            (0x16, 0x01, []),
            (0x15, 0x03, [0x00]),
            // feature mask 0x94 = motion + mouse + magnetometer; 0xFF causes phantom ZL/ZR
            (0x0C, 0x02, [0x94, 0x00, 0x00, 0x00]),
            (0x11, 0x03, []),
            (0x0A, 0x08, [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            (0x0C, 0x04, [0x94, 0x00, 0x00, 0x00]),
            (0x03, 0x0A, [0x09, 0x00, 0x00, 0x00]),
            (0x10, 0x01, []),
            (0x01, 0x0C, []),
        };

        if (productId != PidPro2)
        {
            steps.Add((0x01, 0x01, [0x00, 0x00, 0x00, 0x00]));
        }

        return steps;
    }

    /// <summary>
    /// USB vendor-interface activation sequence (17 commands), from NSW2-controller-enabler.
    /// Sent to a bulk OUT pipe; each frame uses the USB transport byte (0x00).
    /// </summary>
    public static IReadOnlyList<byte[]> GetGameCubeUsbInitSequence()
    {
        return
        [
            BuildCommand(0x03, 0x0D, [0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], true),
            BuildCommand(0x07, 0x01, [], true),
            BuildCommand(0x16, 0x01, [], true),
            // REQUEST_CONTROLLER_MAC
            BuildCommand(0x15, 0x01, [0x00, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], true),
            // LTK_REQUEST
            BuildCommand(0x15, 0x02, [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], true),
            BuildCommand(0x15, 0x03, [0x00], true),
            BuildCommand(0x09, 0x07, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], true),
            BuildCommand(0x0C, 0x02, [0x27, 0x00, 0x00, 0x00], true),
            BuildCommand(0x11, 0x03, [], true),
            BuildCommand(0x0A, 0x08, [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], true),
            BuildCommand(0x0C, 0x04, [0x27, 0x00, 0x00, 0x00], true),
            // ENABLE_HAPTICS
            BuildCommand(0x03, 0x0A, [0x09, 0x00, 0x00, 0x00], true),
            BuildCommand(0x10, 0x01, [], true),
            BuildCommand(0x01, 0x0C, [], true),
            BuildCommand(0x03, 0x01, [0x00], true),
            BuildCommand(0x0A, 0x02, [0x03, 0x00, 0x00], true),
            // SET_PLAYER_LED
            BuildCommand(0x09, 0x07, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], true),
        ];
    }

    /// <summary>
    /// Pro Controller 2 USB initialization (XinHeLianSheng-Pro2-Bridge, verified).
    /// Replies are matched on the command channel.
    /// </summary>
    public static IReadOnlyList<(byte Command, byte Sub, byte[] Data)> GetPro2UsbInitSequence()
    {
        return
        [
            (0x07, 0x01, []),
            (0x0C, 0x02, [0x27, 0x00, 0x00, 0x00]),
            (0x11, 0x01, []),
            (0x0A, 0x08, [0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            (0x0C, 0x04, [0x27, 0x00, 0x00, 0x00]),
            (0x03, 0x0A, [0x05, 0x00, 0x00, 0x00]),
            (0x03, 0x0D, [0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),
        ];
    }

    // Player LED patterns indexed by pad id (joycon2pc map)
    private static readonly byte[] _playerLedPatterns = [0x01, 0x02, 0x04, 0x08, 0x03, 0x06, 0x0C, 0x0F];

    public static byte[] BuildPlayerLedCommand(int padId)
    {
        var data = new byte[8];
        data[0] = _playerLedPatterns[Math.Clamp(padId, 0, _playerLedPatterns.Length - 1)];

        return data;
    }

    // -----------------------------------------------------------------------
    // Input reports
    // -----------------------------------------------------------------------

    public struct Ns2Input
    {
        public uint Buttons;          // NS2 u32 button mask
        public ushort LeftX;
        public ushort LeftY;
        public ushort RightX;
        public ushort RightY;
        public byte TriggerLeft;      // GameCube analog triggers only
        public byte TriggerRight;
        public short AccelX;
        public short AccelY;
        public short AccelZ;
        public short GyroX;
        public short GyroY;
        public short GyroZ;
        public ushort BatteryMillivolts;
        public ushort MouseX;
        public ushort MouseY;
        public ushort MouseRoughness;
        public ushort MouseDistance;
        public bool HasMotion;
    }

    // NS2 button mask bits
    public const uint BtnY = 0x0000001;
    public const uint BtnX = 0x0000002;
    public const uint BtnB = 0x0000004;
    public const uint BtnA = 0x0000008;
    public const uint BtnSrR = 0x0000010;
    public const uint BtnSlR = 0x0000020;
    public const uint BtnR = 0x0000040;
    public const uint BtnZr = 0x0000080;
    public const uint BtnMinus = 0x0000100;
    public const uint BtnPlus = 0x0000200;
    public const uint BtnRStick = 0x0000400;
    public const uint BtnLStick = 0x0000800;
    public const uint BtnHome = 0x0001000;
    public const uint BtnCapture = 0x0002000;
    public const uint BtnC = 0x0004000;
    public const uint BtnDown = 0x0010000;
    public const uint BtnUp = 0x0020000;
    public const uint BtnRight = 0x0040000;
    public const uint BtnLeft = 0x0080000;
    public const uint BtnSrL = 0x0100000;
    public const uint BtnSlL = 0x0200000;
    public const uint BtnL = 0x0400000;
    public const uint BtnZl = 0x0800000;
    public const uint BtnGr = 0x1000000;
    public const uint BtnGl = 0x2000000;
    public const uint BtnHeadset = 0x10000000;
    public const uint BtnGcLClick = 0x40000000;
    public const uint BtnGcRClick = 0x80000000;

    public static ushort Unpack12X(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)(data[offset] | ((data[offset + 1] & 0x0F) << 8));
    }

    public static ushort Unpack12Y(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)(((data[offset + 1] >> 4) & 0x0F) | (data[offset + 2] << 4));
    }

    /// <summary>
    /// Decodes a standard 63-byte NS2 report (BLE notification body or the payload of
    /// USB report 0x05) and the GameCube layout. Returns false for command replies
    /// (byte 0 == 0x21/0x02) and unrecognized reports.
    /// </summary>
    public static bool TryDecodeInputReport(ReadOnlySpan<byte> report, bool gameCubeLayout, out Ns2Input input)
    {
        input = default;

        if (report.IsEmpty)
        {
            return false;
        }

        // USB reports carry a leading report ID byte; BLE notifications do
        // not, so there report[0] is just the packet counter and may hold any
        // value (a 63-byte payload must never be re-interpreted as an ID).
        if (report.Length > StandardReportLen)
        {
            if (report[0] is 0x21 or 0x02)
            {
                return false; // subcommand / SPI reply
            }

            if (report[0] is 0x30 or 0x31 or 0x05 or 0x01 or 0xA1)
            {
                report = report[1..];
            }
        }

        if (gameCubeLayout)
        {
            return TryDecodeGameCubeReport(report, ref input);
        }

        if (report.Length < 60)
        {
            return false;
        }

        input.Buttons = BitConverter.ToUInt32(report.Slice(4, 4));
        input.LeftX = Unpack12X(report, 10);
        input.LeftY = Unpack12Y(report, 10);
        input.RightX = Unpack12X(report, 13);
        input.RightY = Unpack12Y(report, 13);
        input.MouseX = BitConverter.ToUInt16(report.Slice(16, 2));
        input.MouseY = BitConverter.ToUInt16(report.Slice(18, 2));
        input.MouseRoughness = BitConverter.ToUInt16(report.Slice(20, 2));
        input.MouseDistance = BitConverter.ToUInt16(report.Slice(22, 2));
        input.BatteryMillivolts = BitConverter.ToUInt16(report.Slice(31, 2));
        input.AccelX = BitConverter.ToInt16(report.Slice(48, 2));
        input.AccelY = BitConverter.ToInt16(report.Slice(50, 2));
        input.AccelZ = BitConverter.ToInt16(report.Slice(52, 2));
        input.GyroX = BitConverter.ToInt16(report.Slice(54, 2));
        input.GyroY = BitConverter.ToInt16(report.Slice(56, 2));
        input.GyroZ = BitConverter.ToInt16(report.Slice(58, 2));
        input.HasMotion = true;

        SanitizeSticks(ref input);

        return true;
    }

    // GameCube report layout (Switch2Connect ControllerInputData):
    // [0] time, [2:5] buttons b1/b2/b3, [5:8] stick1, [8:11] stick2,
    // [12]/[13] analog triggers, [34:40] accel, [40:46] gyro.
    private static bool TryDecodeGameCubeReport(ReadOnlySpan<byte> report, ref Ns2Input input)
    {
        if (report.Length < 14)
        {
            return false;
        }

        var b1 = report[2];
        var b2 = report[3];
        var b3 = report[4];
        uint buttons = 0;

        if ((b1 & 0x01) != 0) buttons |= BtnB;
        if ((b1 & 0x02) != 0) buttons |= BtnA;
        if ((b1 & 0x04) != 0) buttons |= BtnY;
        if ((b1 & 0x08) != 0) buttons |= BtnX;
        if ((b1 & 0x10) != 0) buttons |= BtnZr | BtnGcRClick;
        if ((b1 & 0x20) != 0) buttons |= BtnR;      // GC "Z" maps to R
        if ((b1 & 0x40) != 0) buttons |= BtnPlus;   // Start
        if ((b2 & 0x01) != 0) buttons |= BtnDown;
        if ((b2 & 0x02) != 0) buttons |= BtnRight;
        if ((b2 & 0x04) != 0) buttons |= BtnLeft;
        if ((b2 & 0x08) != 0) buttons |= BtnUp;
        if ((b2 & 0x10) != 0) buttons |= BtnZl | BtnGcLClick;
        if ((b2 & 0x20) != 0) buttons |= BtnL;      // GC analog ZL = L bumper
        if ((b3 & 0x01) != 0) buttons |= BtnHome;
        if ((b3 & 0x02) != 0) buttons |= BtnCapture;
        if ((b3 & 0x10) != 0) buttons |= BtnC;

        input.Buttons = buttons;
        input.LeftX = Unpack12X(report, 5);
        input.LeftY = Unpack12Y(report, 5);
        input.RightX = Unpack12X(report, 8);
        input.RightY = Unpack12Y(report, 8);
        input.TriggerLeft = RemapGcTrigger(report[12]);
        input.TriggerRight = RemapGcTrigger(report[13]);

        if (report.Length >= 46)
        {
            input.AccelX = BitConverter.ToInt16(report.Slice(34, 2));
            input.AccelY = BitConverter.ToInt16(report.Slice(36, 2));
            input.AccelZ = BitConverter.ToInt16(report.Slice(38, 2));
            input.GyroX = BitConverter.ToInt16(report.Slice(40, 2));
            input.GyroY = BitConverter.ToInt16(report.Slice(42, 2));
            input.GyroZ = BitConverter.ToInt16(report.Slice(44, 2));
            input.HasMotion = true;
        }

        SanitizeSticks(ref input);

        return true;
    }

    /// <summary>
    /// Decodes a legacy Switch-1-style HID report (0x30/0x31) that the Pro 2 may
    /// emit over USB: buttons [3:6] (same u32 mask), sticks [6:9]/[9:12],
    /// first IMU sample at [13:25].
    /// </summary>
    public static bool TryDecodeLegacyReport(ReadOnlySpan<byte> report, out Ns2Input input)
    {
        input = default;

        if (report.Length < 25 || report[0] is not (0x30 or 0x31))
        {
            return false;
        }

        input.Buttons = (uint)(report[3] | (report[4] << 8) | (report[5] << 16));
        input.LeftX = Unpack12X(report, 6);
        input.LeftY = Unpack12Y(report, 6);
        input.RightX = Unpack12X(report, 9);
        input.RightY = Unpack12Y(report, 9);
        input.AccelX = BitConverter.ToInt16(report.Slice(13, 2));
        input.AccelY = BitConverter.ToInt16(report.Slice(15, 2));
        input.AccelZ = BitConverter.ToInt16(report.Slice(17, 2));
        input.GyroX = BitConverter.ToInt16(report.Slice(19, 2));
        input.GyroY = BitConverter.ToInt16(report.Slice(21, 2));
        input.GyroZ = BitConverter.ToInt16(report.Slice(23, 2));
        input.HasMotion = true;

        SanitizeSticks(ref input);

        return true;
    }

    // GC analog trigger raw range ~36-240 -> 0-255
    private static byte RemapGcTrigger(byte raw)
    {
        return (byte)Math.Clamp((raw - 36) * 255 / (240 - 36), 0, 255);
    }

    private static void SanitizeSticks(ref Ns2Input input)
    {
        // A disconnected/reset stick reports 0 or 2047; treat as neutral.
        const ushort Neutral = 1998;

        if (input.LeftX is 0 or 2047 || input.LeftY is 0 or 2047)
        {
            input.LeftX = Neutral;
            input.LeftY = Neutral;
        }

        if (input.RightX is 0 or 2047 || input.RightY is 0 or 2047)
        {
            input.RightX = Neutral;
            input.RightY = Neutral;
        }
    }

    /// <summary>
    /// Parses a stick calibration record (first 9 bytes, 12-bit pairs):
    /// center = [0:3], min = [3:6], max = [6:9] — verified in Switch2Connect.
    /// Returns values ordered for StickLimitsCalibration: [maxX, maxY, centerX, centerY, minX, minY].
    /// </summary>
    public static bool TryParseStickCalibration(ReadOnlySpan<byte> payload, out ushort[] values)
    {
        values = [];

        if (payload.Length < 9 || (payload[0] == 0xFF && payload[1] == 0xFF && payload[2] == 0xFF))
        {
            return false;
        }

        values =
        [
            Unpack12X(payload, 6), Unpack12Y(payload, 6), // max (absolute range above center)
            Unpack12X(payload, 0), Unpack12Y(payload, 0), // center
            Unpack12X(payload, 3), Unpack12Y(payload, 3), // min (absolute range below center)
        ];

        // Garbage reads (failed bridges, uninitialized GC records) must not
        // pin a stick to an extreme: reject implausible centers and ranges.
        var centerX = values[2];
        var centerY = values[3];
        if (centerX is < 1024 or > 3072 || centerY is < 1024 or > 3072 ||
            values[0] == 0 || values[1] == 0 || values[4] == 0 || values[5] == 0 ||
            values[0] > 4095 - centerX || values[1] > 4095 - centerY ||
            values[4] > centerX || values[5] > centerY)
        {
            values = [];
            return false;
        }

        return true;
    }
}
