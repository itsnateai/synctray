# SyncthingTray

A lightweight system tray manager for [Syncthing](https://syncthing.net/) on Windows, built with AutoHotkey v2.

## Features

- Launches Syncthing hidden (no console window)
- Tray icon shows running state (sync/pause icons)
- Start, stop, and restart Syncthing from the tray menu
- Open the Syncthing Web UI with a click
- Settings GUI for configuration (double-click behavior, startup, API key)
- Graceful shutdown via Syncthing REST API when API key is configured
- Crash detection with sound alert when Syncthing exits unexpectedly
- Run at Windows startup option
- Periodic state sync (5-second polling)

## Requirements

- Windows 10/11
- [AutoHotkey v2](https://www.autohotkey.com/) (or use the compiled .exe)
- `syncthing.exe` in the same folder as the script

## Setup

1. Place `SyncthingTray.ahk` (or compiled `.exe`) in the same folder as `syncthing.exe`
2. Optionally place `sync.ico` and `pause.ico` for custom tray icons
3. Run the script — Syncthing will start automatically in the background

## Configuration

Right-click the tray icon and select **Settings** to configure:

- **Double-click opens Web UI** — toggle whether double-clicking the tray icon opens the Syncthing web interface
- **Run on startup** — creates/removes a Windows Startup shortcut
- **API Key** — enter your Syncthing API key to enable graceful shutdown via the REST API (recommended). Find your API key in Syncthing Web UI under Actions > Settings > API Key.

Settings are saved to `SyncthingTray.ini` in the script directory.

## Compiling

To create a standalone `.exe`:

```
Ahk2Exe.exe /in SyncthingTray.ahk /out SyncthingTray.exe /compress 0
```

Use `/compress 0` to avoid Windows Defender false positives.

## License

MIT
