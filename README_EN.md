![ScreenOffTray: let your screen rest while your work continues](docs/images/guide/hero-en.png)

# ScreenOffTray

[简体中文](README.md) · [Download for Windows](https://github.com/shilefumaoqu/ScreenOffTray/releases/tag/v0.2.8) · [Report an issue](https://github.com/shilefumaoqu/ScreenOffTray/issues)

**Turn off the display while your applications keep running.**

ScreenOffTray is a Windows tray utility. Turn the screen off with a shortcut or automatically after keyboard and mouse inactivity while downloads, computations and other work continue. It does not lock, sleep, hibernate or shut down Windows.

Requires Windows 10 22H2 / Windows 11 x64, one physical display and .NET Framework 4.8.

## What it does

- **Turn the display off your way:** use a shortcut immediately or set an idle timeout.
- **Keep listening:** whitelist a playing app to pause automatic display-off while it plays audio. Manual display-off remains available.
- **Wake without an accidental click:** after the guard period, the first physical key or mouse click wakes the screen instead of reaching the active app.

## See how it works

> The demonstrations use actual Settings controls on an isolated test desktop, triggered automatically and captured frame by frame. The pointer and click rings are annotations; save and playback results come from a simulated backend. No production settings or physical displays were changed.

### 1. Set an idle timeout

Adjust the minutes under General and select Save to apply ordinary settings. You can also record your own shortcut here. New users start with a 10-minute timeout and `Ctrl+Alt+F10`.

![Adjusting the idle timeout and submitting a save](docs/images/guide/idle-en.gif)

### 2. Protect a playing application

Playback protection lists detected playing applications. Add one to the whitelist to pause automatic display-off while it plays audio. Adding or removing a whitelist entry is saved immediately, independently of the ordinary Save and Cancel buttons.

![Adding a simulated playback application to the whitelist](docs/images/guide/play-en.gif)

The protection cap defaults to 60 minutes, measured from the last physical input. When it expires, playback no longer blocks display-off, although the normal idle threshold must still be met. Entries match the executable's full path; the app does not inspect webpages or silent video frames.

### 3. Wake the screen

![Illustrated display-off, guard, physical input and wake sequence](docs/images/guide/wake-en.gif)

This is an **illustrated sequence**, not a recording of a physical display. A 3-second guard follows display-off. Afterwards, release held keys or buttons, then press a physical keyboard key or mouse button. The first press and release only wake the display; the next input reaches the application normally.

Mouse movement, scrolling and input marked as injected by Windows do not wake it. Unmarked kernel-level virtual HID is outside the identification guarantee. This utility is not a privacy lock; use Windows Lock to restrict access.

### 4. Choose an appearance

Choose Use system theme, Light or Dark under About. Simplified Chinese and English are available. Appearance changes are saved with ordinary settings.

Version 0.2.4 adds real desktop Acrylic: the window takes on background color, softens underlying text, and keeps its own text clear. This is a desktop photograph of the actual Light Settings window over a separate blue background; see the [second background and validation limits](docs/原生玻璃验证.md). Unsupported systems, disabled transparency and high contrast use a solid fallback.

![Actual desktop Acrylic Settings window](docs/images/glass/blue.png)

The theme choices use Pane's System / Light / Dark naming. Reference-manual test backgrounds such as Warm white are no longer product themes. Version 0.2.6 adds in-app downloads with progress, cancellation and retry. After verification, “Save and restart to update” saves the current draft, exits normally and requests Windows installation approval. Automatic checks only notify; they never start a download or installation.

## Install and get started

1. Download `ScreenOffTray-0.2.8-Setup-x64.exe` from [v0.2.8 Release](https://github.com/shilefumaoqu/ScreenOffTray/releases/tag/v0.2.8), run it and approve the Windows administrator prompt. Setup works offline and does not download the .NET runtime.
2. During first-time setup, choose “Prepare and enable display off” in Settings. The display may flicker; avoid changing Windows display settings during preparation. If safe matching fails, display-off stays disabled and a reason is shown.
3. Open Settings from the tray menu to adjust the timeout, shortcut and language. Save applies ordinary drafts; Cancel or closing the window discards them. Display-off is deferred while Settings is open.
4. Press `Ctrl+Alt+F10` or choose “Turn off now” in the tray menu. Closing Settings leaves the background app running; use “Exit” in the tray menu to quit it.

Version 0.2.6 can upgrade from About → Check for updates. On 0.2.5 or earlier, download the installer manually and exit the previous version from the tray before running Setup. Setup preserves settings and refuses to overwrite a running app. Everyday use requires no administrator rights. The application and installer are unsigned. The driver retains its upstream signature.

## Playback protection and limits

| Situation | Automatic display-off |
| --- | --- |
| A whitelisted application is playing audio | Paused |
| Playback protection ends | The full idle timeout starts again |
| Audio state cannot be read reliably | Paused with an explanation |
| An unlisted app, silent video or system display request | Does not block automatic display-off by itself |
| Manual shortcut or tray command | Bypasses playback protection |

Explicitly paused players are filtered only when their identity is reliably matched and the state is fresh; not every player is guaranteed to be detected. The initial whitelist is empty, and the cap can be disabled. The app does not clear other applications' system requests.

The app uses a virtual display driver to keep the desktop running. Effects on automatic discrete-GPU direct-display switching on some hybrid-GPU machines remain unverified. See [validation and limitations](https://github.com/shilefumaoqu/ScreenOffTray/blob/main/docs/验证与限制.md); an isolated demonstration does not establish real-hardware or long-term stability.

## Updates, development and license

In About, check for updates, choose “Download update”, then “Save and restart to update” after verification. In-app downloads require the exact official Release asset name, HTTPS URL, length and SHA256. Missing verification data leaves manual download available. Automatic checks only notify. Safe-exit timeouts and installation failures show recovery instructions; the updater never force-kills processes, restarts Windows automatically or installs drivers. Older installers remain available in [Releases](https://github.com/shilefumaoqu/ScreenOffTray/releases).

Built with .NET Framework 4.8 / C# 5, with a WinForms background app and a separate WPF Settings process. See [build and verification](https://github.com/shilefumaoqu/ScreenOffTray/blob/main/docs/构建与验证.md) and [version validation](https://github.com/shilefumaoqu/ScreenOffTray/blob/main/docs/版本验证.md).

Project source and original assets use the [MIT License](https://github.com/shilefumaoqu/ScreenOffTray/blob/main/LICENSE). The virtual display driver is [Virtual Display Driver 25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23), retaining its original signature and [MIT license](https://github.com/shilefumaoqu/ScreenOffTray/blob/main/vendor/Licenses/Virtual-Display-Driver-MIT.txt). VDD Control, DevCon and audio drivers are not bundled.

[Open an issue](https://github.com/shilefumaoqu/ScreenOffTray/issues) with your Windows version, number of physical displays and error reason. Remove personal paths and device information before sharing logs.

Source is organized under `src/` by background app, Settings UI and setup helper, with separate resources, vendor inputs, installer and tests. See the [repository layout](docs/目录结构.md).
