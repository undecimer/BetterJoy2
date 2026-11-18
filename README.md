<p align="center">
  <img src="title.png">
</p>

# BetterJoy v9.0.0 Beta
#### What's new in BetterJoy2 (v9.0.0 Beta)
- **Native Nintendo Switch 2 controller support** (Bluetooth LE & USB-C) alongside full Switch 1 controller support (`Joycon.cs` untouched):
  - **Nintendo Switch 2 Pro Controller (`0x2069`)** over BLE and USB-C
  - **Joy-Con 2 (Left `0x2067` & Right `0x2066`)** — single horizontal mode, joined dual-controller mode, and optical mouse sensor mode (place a Joy-Con 2 sensor-down on a surface; `ZL`/`ZR` = Left Click, `L`/`R` = Right Click, `L3`/`R3` = Middle Click)
  - **Nintendo Switch Online GameCube Controller (`0x2073`)** — analog `L`/`R` shoulder triggers plus digital full-press click (`ZL`/`ZR`)
  - **Switch 2 buttons & haptics:** `C` (Chat) mapped to Back/Touchpad, `GL`/`GR` grip buttons mapped to `L3`/`R3` on virtual Xbox 360 / DualShock 4 outputs, HD Rumble 2 voice-coil vibration (BLE & USB-C), and GameCube ERM rumble
  - **Automatic BLE discovery & host bonding:** connects to Switch 2 controllers in sync mode or reconnect mode directly without requiring manual Windows Bluetooth Settings pairing
  - **Calibration & 6-axis IMU motion:** reads factory and user SPI stick calibration, feeds Cemuhook UDP (`DSUController` on `127.0.0.1:26760`), and emits DualShock 4 `DS4_REPORT_EX` motion reports when `ShowAsDS4` is enabled (enabling SDL motion)
- **Upstream base ([d3xMachina/BetterJoy](https://github.com/d3xMachina/BetterJoy) v8.5 LTS):**
  - Switch 1 Pro Controller, Joy-Con (L/R), SNES, N64, NES/Famicom, and 3rd-party controllers
  - Controller calibration, deadzone, range, anti-deadzone, and stick shape (`circle`, `square`, `n64`) settings
  - .NET 10, updated `hidapi`, and HidHide driver support

> **Beta note:** Switch 2 Pro Controller over both USB-C and BLE has been verified on Windows hardware. Joy-Con 2 (single/joined/mouse) and NSO GameCube Controller support are included in this beta based on protocol specifications and welcome hardware feedback.

#### Description

Allows Nintendo Switch and Switch 2 controllers (Pro Controller, Pro Controller 2, Joy-Con, Joy-Con 2, SNES, N64, NES/Famicom, and NSO GameCube) to be used with [Cemu](http://cemu.info/) using [Cemuhook](https://sshnuke.net/cemuhook/) / `DSUController` / `SDLController`, [Citra](https://citra-emu.org/), [Dolphin](https://dolphin-emu.org/), [Yuzu](https://yuzu-emu.org/), and system-wide with generic XInput or DualShock 4 emulation via ViGEmBus.

It also allows using the gyro to control your mouse, using the Joy-Con 2 optical mouse sensor directly on a desk surface, and remapping special buttons (SL, SR, Capture, Home) to keyboard/mouse bindings.

If anyone would like to donate to the original creator (Davidobot), [you can do so here](https://www.paypal.me/DavidKhachaturov/5).

#### Original creator note
Thank you for using my software and all the constructive feedback I've been getting about it. I started writing this project a while back and have since then learnt a lot more about programming and software development in general. I don't have too much time to work on this project, but I will try to fix bugs when and if they arise. Thank you for your patience in that regard too!

It's been quite a wild ride, with nearly **590k** (!!) official download on GitHub and probably many more through the nightlies. I think this project was responsible for both software jobs I landed so far, so I am quite proud of it.

### Screenshot
![Example](https://github.com/d3xMachina/BetterJoy/assets/16732772/2cb06404-5484-480b-a733-2db72fdf043d)

# Downloads
Go to the [Releases tab](https://github.com/undecimer/BetterJoy2/releases/)!

> **Requirement:** Requires the [.NET 10.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) on Windows 10/11 (build `10.0.22621.0` or newer recommended for full Switch 2 BLE support).

# How to use
1. Install drivers
    1. Read the READMEs (they're there for a reason!)
    2. Run *Drivers/ViGEmBus_1.22.0_x64_x86_arm64.exe*
    3. Run *Drivers/HidHide_1.5.230_x64.exe* (optional but recommended to hide the default device)
    4. Restart your computer
2. Run *BetterJoy.exe*
    1. Run as Administrator if your keyboard/mouse button mappings don't work
3. Connect your controllers.
4. Start your game or emulator and configure your controller.
5. If you use Cemu:
    1. For motion controls via the built-in UDP server (`MotionServer = true`), ensure Cemu uses **DSUController** (`127.0.0.1:26760`), or enable `ShowAsDS4 = true` (with `ShowAsXInput = false`) in `BetterJoy.dll.config` (or the app configuration panel) to expose a virtual DualShock 4 controller with 6-axis IMU for **SDLController**.
    2. If using Joy-Cons, CemuHook will detect two controllers - each will give all buttons, but choosing one over the other just chooses preference for which hand to use for gyro controls.
    3. Turn rumble up to 70-80% if you want rumble.

* You can use Pro Controllers, Joy-Cons, and NSO controllers as normal Xbox 360 (XInput) or DualShock 4 controllers on your PC - try it with Steam!

# More Info
Check out the [wiki](https://github.com/Davidobot/BetterJoy/wiki)! There, you'll find all sorts of goodness such as the changelog, description of app settings, the FAQ and Problems page, and info on how to make BetterJoy work with Steam *better*.

# Connecting and Disconnecting the Controller
## Switch 2 Controllers (BLE & USB-C)
### Bluetooth LE Mode
* Ensure Windows Bluetooth is enabled and BetterJoy is running (do **not** manually pair the Switch 2 controller in Windows Bluetooth Settings).
* Hold the **Sync** button on the top of the controller until the player LEDs start flashing - BetterJoy's BLE watcher automatically discovers the controller, completes in-protocol host bonding, and initializes input/IMU/rumble.
* Once bonded to your PC, press any button to wake and reconnect automatically while BetterJoy is running.

### USB-C Mode
* Plug the Switch 2 Pro Controller or NSO GameCube Controller into your PC with a USB-C cable. BetterJoy detects the controller via USB HID hotplug and filters out duplicate/synthetic Bluetooth HID nodes automatically.

## Switch 1 Controllers
### Bluetooth Mode
 * Hold down the small button (sync) on the top of the controller for 5 seconds - this puts the controller into broadcasting mode.
 * Search for it in your bluetooth settings and pair normally.
 * To disconnect the controller - hold the home button (or capture button) down for 2 seconds (or press the sync button). To reconnect - press any button on your controller.

### USB Mode
 * Plug the controller into your computer.

## Disconnecting \[Windows 10\]
1. Go into "Bluetooth and other devices settings"
1. Under the first category "Mouse, keyboard, & pen", there should be the pro controller.
1. Click on it and a "Remove" button will be revealed.
1. Press the "Remove" button

# Building

## Visual Studio (IDE)

1. If you didn't already, install **Visual Studio Community 2026** via
   [the official guide](https://docs.microsoft.com/en-us/visualstudio/install/install-visual-studio?view=vs-2026).
   When asked about the workloads, select **.NET Desktop Development** (including the **.NET 10.0 SDK** and **Windows 11 SDK 10.0.22621.0**).
2. Get the code project via Git (`git clone --recurse-submodules https://github.com/undecimer/BetterJoy2.git`)
3. Open Visual Studio Community and open the solution file (*BetterJoy.sln*).

### For a production build :

4. Right click on "BetterJoy" in The Solution Explorer -> "Publish..." -> Click "Publish".
5. The build is in the `build\` folder

### For a standard build :

4. Open the NuGet manager via *Tools > NuGet Package Manager > Package Manager Settings*.
5. You should have a warning mentioning *restoring your packages*. Click on the **Restore** button.
6. You can now run and build BetterJoy.

## Visual Studio Build Tools / .NET CLI
1. Install **.NET 10.0 SDK** and **Visual Studio Build Tools** via
   [the official link](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2026).
2. Install **NuGet** by following
   [the official guide](https://docs.microsoft.com/en-us/nuget/install-nuget-client-tools#nugetexe-cli).
   You should follow the section for ***nuget.exe***.
   Verify that you can run `nuget` from your favourite terminal.
3. Get the code project via Git (`git clone --recurse-submodules https://github.com/undecimer/BetterJoy2.git`)

### For a production build :

4. Ensure local native submodules (`hidapi.dll`, `ViGEm.NET`, `WindowsInput`) and driver installers in `dist\Drivers\` are built/present.
5. Run `PUBLISH.bat` (or `dotnet publish BetterJoy\BetterJoy.csproj -p:PublishProfile=Properties\PublishProfiles\FolderProfile.pubxml`).
6. The build and `BetterJoy.zip` package are in the `build\` folder.

### For a standard build :

4. Open a terminal (*cmd*, *PowerShell*, ...) and enter the folder with the source code.
5. Restore and build the app:
   ```
   dotnet build BetterJoy\BetterJoy.csproj -c Release
   ```
   Or using `nuget restore` and MSBuild:
   ```
   msbuild .\BetterJoy.sln -p:Configuration=CONFIGURATION -p:Platform=PLATFORM -t:Rebuild
   ```
   The available values for **CONFIGURATION** are *Release* and *Debug*.
   The available values for **PLATFORM** are *x86* and *x64* (you want the latter 99.99% of the time).
6. You have now built the app. See the next section for locating the binaries.

## Binaries location
The built binaries are located under

*BetterJoy\bin\Release\net10.0-windows10.0.22621.0* (or *BetterJoy\bin\PLATFORM\CONFIGURATION* with MSBuild)

where `PLATFORM` and `CONFIGURATION` are the ones provided at build time.

# Acknowledgements
* Switch 2 protocol research and implementation references:
  * [TommyWabg/Switch2Connect](https://github.com/TommyWabg/Switch2Connect) — BLE initialization, host LTK pairing, input/IMU/rumble/mouse protocol documentation and implementation
  * [FingerlessCoder/joycon2pc](https://github.com/FingerlessCoder/joycon2pc) — WinRT BLE scanning and Switch 2 input/subcommand decoding in C#
  * [LeonChrome/XinHeLianSheng-Pro2-Bridge](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge) — Switch 2 Pro Controller USB/HID protocol reference
* Fork base: [d3xMachina/BetterJoy](https://github.com/d3xMachina/BetterJoy) (BetterJoy v8.5 LTS) and original creator [Davidobot/BetterJoy](https://github.com/Davidobot/BetterJoy).

A massive thanks goes out to [rajkosto](https://github.com/rajkosto/) for putting up with 17 emails and replying very quickly to my silly queries. The UDP server is also mostly taken from his [ScpToolkit](https://github.com/rajkosto/ScpToolkit) repo.

Also I am very grateful to [mfosse](https://github.com/mfosse/JoyCon-Driver) for pointing me in the right direction and to [Looking-Glass](https://github.com/Looking-Glass/JoyconLib) without whom I would not be able to figure anything out. (being honest here - the joycon code is his)

Many thanks to [nefarius](https://github.com/ViGEm/ViGEmBus) for his ViGEm project! Apologies and appreciation go out to [epigramx](https://github.com/epigramx), creator of *WiimoteHook*, for giving me the driver idea and for letting me keep using his installation batch script even though I took it without permission. Thanks go out to [MTCKC](https://github.com/MTCKC/ProconXInput) for inspiration and batch files.

A last thanks goes out to [dekuNukem](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) for his documentation, especially on the SPI calibration data and the IMU sensor notes!

Massive *thank you* to **all** code contributors!

Icons (modified): "[Switch Pro Controller](https://thenounproject.com/term/nintendo-switch/930119/)", "[
Switch Detachable Controller Left](https://thenounproject.com/remsing/uploads/?i=930115)", "[Switch Detachable Controller Right](https://thenounproject.com/remsing/uploads/?i=930121)" icons by Chad Remsing from [the Noun Project](https://thenounproject.com/). [Super Nintendo Controller](https://thenounproject.com/themizarkshow/collection/vectogram/?i=193592) icon by Mark Davis from the [the Noun Project](https://thenounproject.com/); icon modified by [Amy Alexander](https://www.linkedin.com/in/-amy-alexander/). [Switch Nintendo 64 Controller](https://thenounproject.com/icon/game-controller-193588/) icon by Mark Davis from the [the Noun Project](https://thenounproject.com/); icon modified by [d3xMachina](https://www.github.com/d3xMachina). [NES Controller](https://thenounproject.com/icon/nes-gamepad-949405/) icon by Viktor Korobkov from [the Noun Project](http://thenounproject.com/); icon modified by anon.
