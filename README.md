# SyncthingTray

A lightweight system tray manager for [Syncthing](https://syncthing.net/) on Windows, built with C# (.NET 8 WinForms).

## Features

- Launches Syncthing hidden (no console window)
- Tray icon shows running state (sync/pause icons) with dark-themed context menu
- Start, stop, and restart Syncthing from the tray menu
- Pause/resume syncing via menu or middle-click
- Open the Syncthing Web UI with a double-click
- Synced Folders submenu — open any synced folder in Explorer
- Device connect/disconnect notifications
- File conflict (pull error) detection
- Network auto-pause on public networks (WMI-based)
- Auto-update check for Syncthing (daily, rate-limited)
- Dark-themed Settings GUI with discovery toggles
- Config check utility (validates exe, process, API, discovery)
- Help window with usage guide
- Graceful shutdown via Syncthing REST API with process kill fallback
- Crash detection with audible alert when Syncthing exits unexpectedly
- Run at Windows startup (shortcut in Startup folder)
- Portable mode — auto-detected on removable drives (disables startup shortcut)
- First-run wizard — auto-opens Settings when no config exists
- Overclick safeguard — cooldown on rapid Start/Stop/Restart/Pause actions
- Single-instance enforcement — kills previous instances on launch
- Tray icon recovery after Explorer restarts

## Requirements

- Windows 10/11
- `syncthing.exe` in the same folder as `SyncthingTray.exe`

## Setup

1. Download `SyncthingTray.exe` from [Releases](https://github.com/itsnateai/synctray/releases)
2. Place it in the same folder as `syncthing.exe`
3. Run it — Syncthing starts automatically in the background
4. Right-click the tray icon > **Settings** to enter your API key

## Configuration

Right-click the tray icon and select **Settings** to configure:

- **Double-click opens Web UI** — toggle double-click behavior on the tray icon
- **Run on startup** — creates/removes a Windows Startup shortcut
- **Start browser** — open the Web UI when Syncthing launches
- **Middle-click toggle** — middle-click tray icon to pause/resume
- **Auto-pause on public networks** — pause syncing on public Wi-Fi
- **Startup delay** — wait N seconds before launching Syncthing
- **Syncthing path** — custom path to `syncthing.exe`
- **Web UI URL** — custom Syncthing Web UI address
- **API Key** — required for pause/resume, status polling, and graceful shutdown. Find it in the Syncthing Web UI under Actions > Settings > API Key.
- **Discovery** — toggle Global Discovery, Local Discovery, and NAT Traversal
- **Auto-update** — check for Syncthing updates daily

Settings are saved to `SyncthingTray.ini` in the application directory.

## Building from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd SyncthingTray
dotnet build -c Release
```

### Publish as single-file .exe

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `SyncthingTray/bin/Release/net8.0-windows/win-x64/publish/SyncthingTray.exe`

## Migrating from AHK Version

The C# version uses the same `SyncthingTray.ini` format. To upgrade:

1. Replace the old `.exe` (or `.ahk`) with the new `SyncthingTray.exe`
2. Keep your existing `SyncthingTray.ini` — all settings carry over
3. Keep `syncthing.exe` in the same folder

## Project Structure

```
SyncthingTray/
  Program.cs                  — Entry point, single-instance enforcement
  TrayApplicationContext.cs   — Main tray app, menu, polling, state machine
  AppConfig.cs                — INI settings read/write
  SyncthingApi.cs             — Synchronous REST client for Syncthing API
  SettingsForm.cs             — Dark-themed settings dialog
  HelpForm.cs                 — Help window
  OsdToolTip.cs               — Borderless topmost notification (replaces ToolTip)
  DarkMenuRenderer.cs         — Dark theme for tray context menu
  NativeMethods.cs            — P/Invoke (RegisterWindowMessage, Beep)
  StartupShortcut.cs          — Startup .lnk management via COM
  Resources/
    sync.ico                  — Running icon (embedded resource)
    pause.ico                 — Paused icon (embedded resource)
```

## License

MIT
