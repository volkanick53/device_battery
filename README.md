# Device Battery

Battery levels of your wireless keyboard, mouse and headset, right on the Windows taskbar next to the notification area: `⌨ 65%  🎧 98%`. No windows, tiny, ~12 MB of RAM and ~0% CPU.

## Install

Download **DeviceBattery-Setup-x.y.z.exe** from the Releases page, run it and click through. It installs for your user account (no administrator rights needed), can start with Windows, and adds an uninstall entry. A portable zip (just the `.exe`) is also provided.

## Using it

**Click** the readout (or the tray icon) to open the menu:

| Menu item | What it does |
|---|---|
| *Device list* | Every detected device with its level and connection (`[Bluetooth]`, `[2.4 GHz / USB]`). Click one to show or hide it. A device that is switched off shows "off / not connected". |
| *Show on the taskbar* / *Show as tray icons* | Taskbar readout (default) or one tray icon per device. |
| *Notify me when a battery is low (15%)* | One notification below 15%; re-armed once the device is back above 20%. |
| *Check batteries every* | 1, 3 (default), 5, 10 or 30 minutes. Devices are also re-read right away when one connects or disconnects. |
| *Refresh now* | Scan immediately. |
| *Start with Windows* | Toggle autostart (same setting as the installer option). |
| *About* / *Exit* | |

Hover the readout for a tooltip with every device. Settings are saved per user in `HKCU\Software\DeviceBattery`.

The readout follows the light/dark taskbar theme and display scaling, and turns red below 20%.

## Supported devices

| Source | Devices | Status |
|---|---|---|
| `PnpBatteryProvider` | Bluetooth / Bluetooth LE devices that report a battery level to Windows (keyboards, mice, many headsets) | Working |
| `HidBatteryProvider` | SteelSeries Arctis Nova 3P / 3X Wireless (2.4 GHz USB dongle) | Working, verified on hardware |
| `HidBatteryProvider` | Logitech HID++ receivers (Unifying / Bolt / Lightspeed), other dongles | Not implemented, see below |

### Adding a dongle device

2.4 GHz dongles don't expose their battery to Windows; each vendor uses its own HID protocol. Add an entry to the `Known` table in `src/HidBatteryProvider.cs` (vendor/product ID, HID usage, reader function). The Arctis reader is a ~20-line example: write a request report, read the reply, parse the percentage. Protocols can be captured with [HidSharp](https://www.nuget.org/packages/HidSharp) or taken from [HeadsetControl](https://github.com/Sapd/HeadsetControl).

## How it works

Windows publishes `DEVPKEY_Device_BatteryLife` (`{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2`) for Bluetooth devices exposing the standard Battery Service. The app reads it through SetupAPI and merges duplicate nodes of the same physical device by ContainerId. The device kind (keyboard/mouse/...) is inferred from the classes of the device's sibling PnP nodes, with a name-based fallback.

The taskbar readout is a layered **child window of the taskbar** (`Shell_TrayWnd`), so it shares the taskbar's z-order and flyouts (volume, clock, Start, hidden icons) cannot hide it. Layered child windows only work if the exe manifest declares Windows 8/10 support, which is why `src/app.manifest` exists.

## Limitations and known issues

- Only devices Windows (or a built-in HID reader) knows the battery level of appear. Xbox controllers and Logitech/Razer/... dongle devices are not supported yet.
- The readout needs a horizontal taskbar. With a vertical taskbar (or if the taskbar refuses the window) only the tray icon is shown.
- On Windows 10 the task buttons are not narrowed to make room for the readout, so with many open apps they can slide under it. Windows 11 is unaffected.
- If `explorer.exe` restarts, the readout is re-created on the `TaskbarCreated` broadcast (not tested).
- **Quit the app from its menu (or with `DeviceBattery.exe --quit`), don't kill it.** While developing, repeatedly force-killing a process that owned a taskbar child window left Explorer refusing new child windows until Explorer was restarted. A normal exit, crash-free use and the uninstaller are fine; if the readout ever stays missing, the tray icon (in the hidden-icons flyout) still works, and restarting Explorer fixes it.
- Unexpected errors are written to `%LOCALAPPDATA%\DeviceBattery\error.log`.
- Windows only (x64).

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). To also build the installer, install [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

```powershell
installer\build-installer.ps1 -Version 1.1.0
```

This publishes a self-contained single-file exe to `dist\self-contained`, compiles `dist\DeviceBattery-Setup-1.1.0.exe` and creates a portable zip. Do **not** add `-p:EnableCompressionInSingleFile=true`: the exe becomes smaller on disk but the app then needs ~45 MB of RAM instead of ~12 MB (the installer compresses it anyway).

Just the exe, without the installer:

```bash
cd src
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../dist/self-contained
```

Framework-dependent build (~0.4 MB, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)): use `--self-contained false`.

## Project layout

```
src/
  Program.cs                  Entry point, single-instance guard, --quit
  TrayApplicationContext.cs   Menu, tray icons, scanning loop, notifications, autostart
  TaskbarWidget.cs            Layered child window of the taskbar: "glyph + %" next to the tray
  Settings.cs                 Saved settings (registry)
  IBatteryProvider.cs         Provider interface, DeviceBatteryInfo, device-kind guesser
  PnpBatteryProvider.cs       SetupAPI battery reader (Bluetooth / PnP)
  HidBatteryProvider.cs       2.4 GHz dongle readers (HidSharp)
  IconGenerator.cs            In-memory GDI+ tray icon rendering (HICON cleanup)
  NativeMethods.cs            P/Invoke declarations
  Log.cs                      Tiny error log
  app.manifest                Declares Windows 8/10 support (required for layered child windows)
  app.ico                     Application icon
installer/
  DeviceBattery.iss           Inno Setup script (English and Turkish)
  build-installer.ps1         Publish + compile installer + portable zip
```

## License

Add a license of your choice (MIT is a common default) before publishing.
