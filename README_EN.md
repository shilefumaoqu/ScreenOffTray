# ScreenOffTray

[简体中文](README.md)

**Turn off your display while applications keep running.**

A Windows tray utility for OLED users who want the screen off while downloads, calculations and background work continue. Turn it off immediately with a shortcut, or automatically after physical keyboard and mouse inactivity. It does not lock, sleep, hibernate or shut down Windows.

**[Download the Windows x64 installer · v0.1.4 pre-release](https://github.com/shilefumaoqu/ScreenOffTray/releases/tag/v0.1.4)**

Targets **Windows 10 22H2 / Windows 11 x64 with one physical display** and requires .NET Framework 4.8 already installed. A virtual display driver keeps the desktop running. Its impact on automatic direct-discrete-GPU display switching on hybrid-GPU laptops remains unverified; evaluate compatibility if your workflow requires that feature.

## Get started

1. Download `ScreenOffTray-0.1.4-Setup-x64.exe` from the Release, run it, and complete the Windows administrator prompt. Setup works offline and does not download the .NET runtime.
2. On first launch, choose “Prepare and enable display off”. Preparation matches the physical display conditions and restores the layout. The display may flicker; avoid changing Windows display settings at the same time. If safe matching fails, display-off stays paused.
3. Right-click the tray icon → Settings to change the timeout, shortcut, language and startup preference. New users start with **10 minutes** and startup disabled.
4. Press **Ctrl+Alt+F10**, or choose “Turn off now”. To customize the shortcut, focus its field in Settings, press a combination, release it and save.

Before upgrading, exit the previous version normally from the tray. Setup does not force-close it, and existing settings are retained. Everyday use does not require administrator privileges. The application and installer currently have no application code signature; the bundled driver retains its upstream signature.

## Waking the display

- A **3-second guard** follows display-off. Physical key and mouse-button presses during it neither wake the display nor reach applications.
- After the guard, release any held key or button, then press a physical keyboard key or mouse button.
- **The first press and release only wake the display.** The next press works normally in the current application.
- Mouse movement, scrolling and input marked as injected by Windows do not wake it. Unmarked kernel-level virtual HID input is outside the identification guarantee.

This is not a privacy lock. Use Windows Lock when you need to restrict other people's access to the computer.

## Automatic display-off rules

| Situation | Behavior |
| --- | --- |
| Physical input has been idle for the configured interval | Turn the display off |
| Video, a presentation or another app requests the display | Pause automatic off and show the reason in the tray |
| The display request ends | Start a full timeout again |
| Background calculations, system or execution keep-alive only | Do not block automatic off by themselves |
| Manual shortcut or “Turn off now” | Can override the automatic display-request protection |

“App requests display” is not proof of video playback. Some music apps retain display requests while paused; ScreenOffTray preserves the protection rather than ignoring an app by name or clearing its requests. An unreadable display state also pauses automatic off with an explicit reason.

## Settings and updates

Chinese and English are available. Timeout, shortcut, language, startup and automatic update-check preferences are saved. Normal startup does not repeatedly open preparation; preparation and diagnostics are available in Settings.

Settings includes the version, “Check for updates” and an automatic-check toggle. **The update source remains blank in this build**, so no update request is sent. Publishing this repository does not change installed configurations. With a valid source, checks only notify and provide a download link; they never install automatically. This pre-release is also excluded from GitHub's stable `releases/latest` endpoint.

## Development and verification

Built with **.NET Framework 4.8 / C# 5 / WinForms**, with no additional runtime libraries. Run `打包安装版.cmd` in the repository root to build the offline installer. The first build downloads and verifies pinned tools. See [Build and verification](docs/构建与验证.md) for commands and English instructions.

Version 0.1.4 has targeted isolated tests, code review, installer-content verification and an upgrade record on one computer. **Clean-system installation, all display hardware, discrete-GPU display switching and long-term operation have not been fully verified.** See [Validation and limitations](docs/验证与限制.md) and [Release notes](RELEASE_NOTES.md).

## Driver and license

Project source and original assets use the [MIT License](LICENSE). The virtual display driver comes from [VirtualDrivers/Virtual-Display-Driver · 25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23). The original INF/DLL/CAT, signature and [MIT license](Licenses/Virtual-Display-Driver-MIT.txt) are retained. VDD Control, DevCon and audio drivers are not bundled. [Third-party provenance and hashes](Licenses/第三方材料说明.md).

[Open an issue](https://github.com/shilefumaoqu/ScreenOffTray/issues) with your Windows version, display count and pause reason. Remove personal paths and device information before sharing logs.
