# Switch 2 Controller Protocol Notes

Extracted from the reference repositories (kept in `.refs/`, git-ignored):

- `FingerlessCoder/joycon2pc` (C#: WinRT BLE, NS2 parser, subcommands)
- `LeonChrome/XinHeLianSheng-Pro2-Bridge` (C#: Pro 2 USB via WinUSB, HID report parser)
- `TommyWabg/Switch2Connect` (Python: full feature set, BLE init, IMU, mouse)
- `Nadeflore/switch2-controllers` (Python: clean reference for GATT + pairing)
- `ikz87/NSW2-controller-enabler` (Python: GameCube NSO USB handshake)
- `Nohzockt/Switch2-Controllers` (Python: early BLE captures)

---

## 1. Device identification

### USB (VID `0x057E` — Nintendo)

| PID | Device |
|---|---|
| `0x2066` | Joy-Con 2 (Right) |
| `0x2067` | Joy-Con 2 (Left) |
| `0x2069` | Switch 2 Pro Controller |
| `0x2073` | NSO GameCube Controller |
| `0x2006` / `0x2007` / `0x2009` | Switch 1 Joy-Con L/R and Pro (already supported) |

### BLE

- Advertisement carries **manufacturer data ID `0x0553`** (Nintendo):
  `md[3:5]` = VID LE, `md[5:7]` = PID LE, `md[10:16]` = host MAC the pad is paired to.
  This lets us detect both already-paired pads (reconnect MAC matches ours) and pads in sync mode (no prior Windows pairing) — discovery without Settings pairing, à la Switch2Connect.
- After GATT connect, authoritative product ID comes from:
  - standard **Device Information service `0x180A` / PnP ID `0x2A50`** (`pid = data[3] | data[4]<<8`; Joy-Con 2 L = `0x6605`, R = `0x6705`), or
  - the memory-read `ControllerInfo` block (§5).
- BLE name strings seen: `Joy-Con 2 (L)`, `Joy-Con 2 (R)`, `Pro Controller 2`, `NSO GameCube Controller`.

---

## 2. GATT layout (BLE transport)

| Role | UUID |
|---|---|
| Custom service | `ab7de9be-89fe-49ad-828f-118f09df7fd0` |
| Input report (notify) | `ab7de9be-89fe-49ad-828f-118f09df7fd2` |
| Command write (WriteNoResp) | `649d4ac9-8eb7-4e6c-af44-1ea54fe5f005` |
| Command response (notify) | `c765a961-d9d8-4d36-a20a-5315b111836a` |
| Rumble write — Joy-Con 2 R | `fa19b0fb-cd1f-46a7-84a1-bbb09e00c149` |
| Rumble write — Joy-Con 2 L | `289326cb-a471-485d-a8f4-240c14f18241` |
| Rumble write — Pro Controller 2 | `cc483f51-9258-427d-a939-630c31f72b05` |

Fallback used by Switch2Connect for devices with a different characteristic set (e.g. GameCube): inside the `ab7de9be` service, pick **write char = 2nd WriteWithoutResponse char by handle**, **command-response = 3rd notify char by handle** (handles ~0x0014 / ~0x001A).

### Connection parameters
Immediately after connect, request `BluetoothLEPreferredConnectionParameters.ThroughputOptimized` (`BluetoothLEDevice.RequestPreferredConnectionParameters`). Requires Windows build ≥ 22000 and API-contract check via `ApiInformation`. Lifts the report rate from ~20 Hz to ~70 Hz on Windows 11.

---

## 3. GATT command protocol

All commands go to `COMMAND_WRITE_UUID`; replies arrive on `COMMAND_RESPONSE_UUID`.

**Request frame:** `[cmdId, 0x91, 0x01, subCmdId, 0x00, dataLen, 0x00, 0x00, data…]`
**Reply frame:** `[cmdId, 0x01, … , payload at offset 8]` — validate `resp[0] == cmdId`.

| Command | Sub | Purpose |
|---|---|---|
| `0x09` LEDs | `0x07` | Set player LED (data = LED pattern, pad to ≥4 B) |
| `0x0A` Vibration | `0x02` | Play haptic preset (1–7) |
| `0x0A` | `0x08` | Haptic init (`01 FF×8 35 00 46 00 …`) |
| `0x02` Memory | `0x04` | Read memory (see §5) |
| `0x15` Pair | `0x01` set MAC, `0x04` LTK1, `0x02` LTK2, `0x03` finish | Host pairing without Windows Settings |
| `0x0C` Feature | `0x02` init / `0x04` enable | Feature select mask (see below) |

Feature mask (use `0x94`, **not** `0xFF`): `0x04` motion, `0x10` mouse, `0x80` magnetometer. Enabling everything makes Joy-Con 2 stream phantom ZL/ZR bits.

### BLE init sequence (Switch2Connect, verified order)

```
03:0d  01 00 FF FF FF FF FF FF
07:01  —
16:01  —
15:03  00                       (pair finish; skip LTK on WinRT)
0C:02  94 00 00 00              (feature init)
11:03  —
0A:08  01 FF×8 35 00 46 00 00…  (haptic init)
0C:04  94 00 00 00              (feature enable)
03:0A  09 00 00 00
10:01  —
01:0C  —
01:01  00 00 00 00              (SKIP on Pro 2 — returns status=4)
09:07  01 00 00 00 …            (player LED)
```

Then read `ControllerInfo` + stick calibration (§5). For GameCube **and Joy-Con 2** additionally set input format `0x30` with this raw frame on the command char: `[0x01, 00×8, 0x03, 0x30]` and mask buttons with `0x03FFFFFF` (clears phantom ZL/ZR in default format). Pro 2 needs no format switch.

joycon2pc additionally sends an SPI-calibration read immediately after notify subscribe (otherwise the pad emits stub reports with buttons zeroed):
`21 01 00 10 00 18 00 00 D0 3D 06 00 …`

Keep-alive / silent rumble on the command char every ~15 ms prevents disconnect:
`[0x50, 0x50|counter&0x0F, 0x00 ×6]` (per-device rolling counter 0–15).

---

## 4. Input reports

### 4a. BLE standard report (Joy-Con 2, Pro 2) — 63 B, may carry `0xA1` HID-over-GATT prefix; `0x21` in byte 0 = subcommand reply, not input.

| Offset | Field |
|---|---|
| 0–3 | timestamp u32 |
| 4–7 | buttons u32 (mask below) |
| 8–9 | unknown |
| 10–12 | left stick, packed 12-bit X/Y |
| 13–15 | right stick, packed 12-bit X/Y |
| 16–17 | optical-mouse X u16 |
| 18–19 | optical-mouse Y u16 |
| 20–21 | mouse roughness u16 |
| 22–23 | mouse distance u16 |
| 24 | unknown |
| 25–30 | magnetometer X/Y/Z s16 |
| 31–32 | battery voltage /1000 → V |
| 33–34 | battery current /100 → mA |
| 35–45 | unknown |
| 46–47 | temperature → `25 + v/127` °C |
| 48–53 | accelerometer X/Y/Z s16 |
| 54–59 | gyroscope X/Y/Z s16 |

IMU scales: accel `4096 LSB/g`; gyro **Joy-Con 2 `16.384 LSB/dps`**, **Pro 2 `14.2857 LSB/dps`**.

Stick packing: `x = b[0] | ((b[1]&0x0F)<<8)`, `y = (b[1]>>4) | (b[2]<<4)`. Center ≈ 1998 (factory 746–3249); unused stick slot reads sentinel `0x7FF` (2047) or `0` → treat as centered.

### 4b. BLE button mask u32 (all NS2 pads)

```
0x00000001 Y        0x00000002 X        0x00000004 B        0x00000008 A
0x00000010 SR(R)    0x00000020 SL(R)    0x00000040 R        0x00000080 ZR
0x00000100 MINUS    0x00000200 PLUS     0x00000400 R-STK    0x00000800 L-STK
0x00001000 HOME     0x00002000 CAPTURE  0x00004000 C
0x00010000 D-DOWN   0x00020000 D-UP     0x00040000 D-RIGHT  0x00080000 D-LEFT
0x00100000 SR(L)    0x00200000 SL(L)    0x00400000 L        0x00800000 ZL
0x01000000 GR       0x02000000 GL
0x10000000 headset-present flag (Pro 2, not a button)
```

### 4c. NSO GameCube — BLE layout (different)

`[0]` time · `[2:5]` 3 button bytes · `[5:8]` left stick 12-bit · `[8:11]` right stick 12-bit · `[12]` L trigger analog · `[13]` R trigger analog (raw ≈36–240, remap to 0–255) · `[34:40]` accel s16×3 · `[40:46]` gyro s16×3.

GC button bytes → standard mask (Switch2Connect mapping):
- b1: `0x01` B, `0x02` A, `0x04` Y, `0x08` X, `0x10` R digital-click, `0x20` Z, `0x40` Start→PLUS
- b2: `0x01` D-down, `0x02` D-right, `0x04` D-left, `0x08` D-up, `0x10` L digital-click, `0x20` ZL→L
- b3: `0x01` Home, `0x02` Capture, `0x10` C
- Mask out MINUS/L3/R3 (not physically present). Digital click ≥50% analog also sets ZL/ZR.

### 4d. USB

- **Pro 2 (`0x2069`)**: HID input report ID `0x05`, 64 B — layout equals the BLE standard report (buttons u32 @4, sticks @10/13, IMU @48, device clock @43). Vendor command interface = MI_01 (WinUSB via MS OS 2.0 descriptor; resolve interface GUID from `HKLM\SYSTEM\CCS\Enum\USB\VID_057E&PID_2069\*\Device Parameters`). Command frame: `[cmd, 0x91, 0x00, sub, 0x00, len, 0x00, 0x00, data…]`; reply when `resp[0]==cmd && resp[1]==1 && resp[3]==sub && resp[5]∈{0xF8,0x78}`.
  USB init sequence (XinHeLianSheng):
  `(07,01) (12,02,27000000) (17,01) (0A,08,01FFFFFFFFFFFFFFFF3500460000000000000000) (12,04,27000000) (03,0A,05000000) (03,0D,0100FFFFFFFFFFFF)`
  Rumble = HID output report `0x02` (64 B); ~250 Hz input.
- **GameCube NSO (`0x2073`)**: bulk OUT on interface 1, 17-command init sequence (enable_hid.py): `03:0D`, `07:01`, `16:01`, `15:01` MAC req, `15:02` LTK, `15:03`, `09:07`, `0C:02 IMU`, `11:03`, `0A:08 haptics`, `0C:04 IMU`, `03:0A haptics`, `10:01`, `01:0C`, `03:01`, `0A:02`, `09:07 LED`. Then plain HID report: `payload[2:5]` buttons (GC layout), `[5:8]`/`[8:11]` sticks, `[12]`/`[13]` analog triggers.
- Joy-Con 2 over USB-C uses the same vendor command channel + `0x05` report as Pro 2.

---

## 5. Memory map (GATT `0x02:0x04` read; data = `[len, 0x7E, 0,0, addr u32 LE]`, reply payload at [8:])

| Address | Content |
|---|---|
| `0x00013000` | ControllerInfo (0x40 B): serial `[2:16]` ASCII, VID `[18:20]`, PID `[20:22]`, body/btn/grip colors |
| `0x0130A8` / `0x0130E8` | factory stick calibration, stick 1 / stick 2 (9 B = center/max/min 12-bit triplets) |
| `0x1FC042` / `0x1FC062` | user stick calibration (first 3 B == `FF FF FF` → absent → use factory) |

Per device type: Joy-Con L → calibration slot 1 = its stick; Joy-Con R → slot 1 = its stick; Pro 2 → slots 1+2 = left/right.

---

## 6. Rumble / HD Rumble 2

Per-device vibration characteristic (§2). Payload = `[0x00, 0x50|counter&0x0F]` + 1–3 × 5-byte `VibrationData` blocks (Joy-Con: 1 block; Pro 2: 2 blocks concatenated). Each 5-byte block: 20-bit LF (`freq` 9 b | `en_tone` 1 b | `amp` 10 b) + 20-bit HF, little-endian. Defaults `lf_freq=0x0E1, hf_freq=0x1E1`. Simple keep-alive = `0x50`-framed all-zero block (§3).

---

## 7. Optical mouse sensor (Joy-Con 2)

Fields at report offsets 16–23 (§4a). Gate used by Switch2Connect (`ir_mouse_activation.py`): sensor is "active" when `distance != 0 && distance < 1000 && roughness < 4000`; cursor delta = signed 16-bit looping diff of consecutive coords × sensitivity; stick Y can act as wheel. Optional feature — config flag `EnableSwitch2OpticalMouse` proposed.

---

## 8. BetterJoy integration design (Paso 1 — proposed class layout)

Coupling found in `d3xMachina/BetterJoy` (unchanged in this fork):

- `JoyconManager` — HIDApi hotplug → filters VID `0x057E` / PIDs `{0x2006,0x2007,0x2009,0x2017,0x2019}` → `HIDApi.Device.OpenPath` → `new Joycon(...)` → `Attach()` → `Controllers` (`List<Joycon>`) → `MainForm.AddController`. Also `Program.AddDeviceToBlocklist` (HidHide).
- `UdpServer` takes `IList<Joycon>`; `UdpControllerReport(Joycon)` reads `PadId, IsUSB, MacAddress, Battery, Timestamp, PacketCounter, MapToDualShock4Input(), GetMotion()`.
- `MainForm` consumes `Joycon` directly (icons per `ControllerType`, join/split via `Mgr.JoinOrSplitJoycon`, calibrate/reassign dialogs, `DebugType`/`Orientation`/`StickShape` enums).
- `Joycon` (3.6 kLOC) does everything: HID polling thread, report parsing, calibration, `OutputControllerXbox360`/`OutputControllerDualShock4` via ViGEm, `MadgwickAHRS` motion, WindowsInput key/mouse simulation, rumble.

**Proposal — do NOT modify `Joycon`'s internals.** Introduce:

```
IControllerDevice                    // façade: the ~30 members UdpServer/MainForm/manager actually use
├── Joycon : IControllerDevice       // Switch 1 — untouched logic, thin interface impl
└── Switch2Device : IControllerDevice// Switch 2 — new

Switch2ControllerManager             // parallel to JoyconManager; owns:
├── Switch2BleTransport              // WinRT BluetoothLEDevice + GATT (§2–§5)
├── Switch2UsbTransport              // WinUSB vendor iface (Pro2/GC) + hidapi (§4d)
├── Ns2Protocol                      // static: report parsers, command builders (§3–§6)
└── Switch2Device                    // state, calibration, joins, ViGEm outputs, motion
```

- Change `IList<Joycon>` → `IList<IControllerDevice>` in `JoyconManager.Controllers`, `UdpServer`, `UdpControllerReport`, `MainForm` internals. Members needed on the interface: `PadId, MacAddress, Battery, IsUSB, Timestamp, PacketCounter, Type, IsLeft, IsJoined, Other, State, GetMotion(), MapToDualShock4Input(), OutXbox, OutDs4, SetRumble(), SetLED…, Config, StateChanged`.
- `ControllerType` gains `Joycon2Left, Joycon2Right, Pro2, GameCubeNSO`; reuse all UI pipelines (join/split, calibrate, reassign) unchanged.
- `Switch2Device` feeds the existing `MadgwickAHRS` + `UdpServer` path with s16 IMU converted via §4a scales; sticks calibrated via §5; GC triggers mapped to L2/R2 analog + ZL/ZR digital.
- HidHide: register USB devices via the existing `Program.AddDeviceToBlocklist` (needs the HID device path — enumerate via hidapi for `0x2069/0x2073` too).
- `.csproj`: `net10.0-windows` already targets Windows; add `10.0.22621.0` TFM suffix for `Windows.Devices.Bluetooth` WinRT APIs (no extra NuGet needed on .NET 10).

Paso 2 will confirm the exact interface member list while keeping `Joycon` behaviour identical.

---

## 9. Pro Controller 2 3.5 mm Headset Audio (Firmware 2.x+)

Implemented in `Switch2AudioStreamer.cs`, `Ns2Protocol.cs`, `Switch2BleTransport.cs`, and `Switch2UsbTransport.cs` (referenced from `ndeadly/switch2_controller_research` and `notsosaelin/PicoSwitch2`):

- **USB-C (UAC 1.0 composite interfaces IF2–IF4):**
  - After HID/vendor init, Pro Controller 2 audio setup commands `18:01`, `18:03 (0x07)`, and `17:02 (80 BB 00 00 02 F0 00)` configure 48 kHz stereo PCM audio routing.
  - Windows `usbaudio.sys` binds `MI_02`..`MI_04` as a standard Stereo Headphones output (`48 kHz`, 16-bit PCM, `EP 0x03` isoch OUT) and Mono Microphone input (`48 kHz`, 16-bit PCM, `EP 0x83` isoch IN). BetterJoy mirrors default WASAPI loopback audio onto the `Switch 2 Pro Controller` endpoint unless it is already the Windows default playback device.
- **Bluetooth LE (GATT Opus/CELT):**
  - Characteristics inside Nintendo service `ab7de9be-...`:
    - `cc483f51-9258-427d-a939-630c31f72b06` (`WriteWithoutResponse`): speaker Opus halves.
    - `7492866c-ec3e-4619-8258-32755ffcc0f9` (`Read | Notify`): 112-byte extended report (byte `0x0D` = jack state: `0x05`/`0x0D` headphones, `0x07`/`0x0F` headset+mic; `0x0E` = mic len `0`/`0x32`; `0x41` = motion len). Subscribed only after BLE handshake completes.
    - `3dacbc7e-6955-40b5-8eaf-6f9809e8b380` (`WriteWithoutResponse`): setup frames `00 18 91 01 03 00 01 00 00 07` (+25 ms) and `00 17 91 01 02 00 07 00 00 80 BB 00 00 02 F0 00` (+10 ms).
  - Codec: **48 kHz stereo CELT-only Opus, 96 kbps CBR, 20 ms frames (960 samples/ch → 240 bytes, TOC `0xFC`)**, primed with 8 fixed idle packets (`FC FF FE` + 237 zero bytes).
  - Framing: split into two 123-byte GATT writes per 20 ms interval — chunk 1 (`00 04 78` + `opus[0..119]`) at `+0 ms`, chunk 2 (`00 02 78` + `opus[120..239]`) at `+5 ms`.
