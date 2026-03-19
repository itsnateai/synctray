# SyncthingTray — Project Instructions

## Overview
System tray manager for Syncthing on Windows. Launches syncthing.exe hidden, provides tray controls (start/stop/restart/pause/resume), opens the Web UI, monitors device connections and file conflicts, manages discovery settings, and checks for Syncthing updates.

## Tech Stack
- C# / .NET 8 / Windows Forms
- No third-party NuGet packages (except System.Management for WMI)
- Settings stored in `SyncthingTray.ini` (INI format, UTF-8 no BOM)
- Syncthing REST API for status polling, pause/resume, config, and auto-updates
- P/Invoke for RegisterWindowMessage and kernel32.Beep
- COM interop for startup shortcut (.lnk) creation

## Build
```bash
cd SyncthingTray
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Architecture

### Project Structure
```
SyncthingTray/
  Program.cs                  — Entry point, single-instance kill
  TrayApplicationContext.cs   — Main app context (tray icon, menu, polling, state)
  AppConfig.cs                — INI settings read/write
  SyncthingApi.cs             — Synchronous HTTP client (HttpWebRequest)
  SettingsForm.cs             — Dark-themed settings dialog
  HelpForm.cs                 — Help window
  OsdToolTip.cs               — Borderless topmost notification form
  DarkMenuRenderer.cs         — Dark theme renderer for ContextMenuStrip
  NativeMethods.cs            — P/Invoke declarations
  StartupShortcut.cs          — Startup .lnk via WScript.Shell COM
  Resources/sync.ico          — Embedded running icon
  Resources/pause.ico         — Embedded paused icon
```

### Key Design Decisions
- **No async** — pure WinForms, single UI thread, all I/O synchronous
- **No polling for files** — monitors syncthing state via REST API, not filesystem
- **System.Windows.Forms.Timer** — fires on UI thread, safe for direct UI updates
- **IDisposable throughout** — full Dispose(bool) pattern on every class holding resources
- **Compiled regex via [GeneratedRegex]** — source-generated at compile time, zero runtime cost
- **Cached process check** — IsSyncthingRunning() has 2s TTL to avoid repeated Process.GetProcessesByName allocations within the same poll cycle
- **Tooltip dirty-check** — UpdateTooltip() only rebuilds the string when status/detail/device counts change
- **Poll timer guard** — timer stopped during PollSyncStatus to prevent re-entrancy on cascading API timeouts
- **Menu rebuild on state change only** — BuildMenu() called when running/paused state transitions, not on every timer tick

### State Machine
```
States: Stopped, Running (Idle/Syncing/Error/Unknown), Paused

Start ──→ Running ──→ Stopped (via Stop/Exit/Crash)
              ↕
           Paused (via Pause/Resume, middle-click, network auto-pause)
```

### Syncthing REST API Endpoints Used
- `GET /rest/db/completion` — sync progress percentage
- `GET /rest/system/connections` — device tracking + pause state
- `GET /rest/db/status?folder=default` — conflict/pull error detection
- `GET /rest/config/folders` — folder list for submenu
- `GET /rest/config/options` — discovery settings
- `GET /rest/system/status` — config check
- `GET /rest/system/upgrade` — update check
- `POST /rest/system/pause` / `resume` / `shutdown` / `upgrade`
- `PATCH /rest/config/options` — save discovery settings

## Key Files
- `SyncthingTray/` — C# project directory
- `SyncthingTray.ahk` — legacy AHK script (kept for reference)
- `SyncthingTray.ini` — user settings (not committed, may contain API key)
- `sync.ico` / `pause.ico` — tray icons (root copies for reference; embedded in C# project)

## Conventions
- Never commit the INI file (may contain API key)
- All notifications via OsdToolTip (borderless form), never BalloonTipText
- Use `Environment.ProcessPath` (not `Assembly.Location`) for single-file publish compatibility
- Use `nint` not `IntPtr` for handles on .NET 7+
- Dispose every Process handle from GetProcessesByName/Start with `using`
- No `async` anywhere — synchronous WinForms only
- `TreatWarningsAsErrors` enabled in .csproj

## Status

**v2.0.0 — C# rewrite (2026-03-19)**

## Changelog

- v2.0.0 — Full rewrite from AHK v2 to C# .NET 8 WinForms. All features preserved. Dark-themed context menu. Poll timer re-entrancy guard. Memory-optimized hot paths.
- v1.6.0 — Middle-click settings toggle, overclick safeguard
- v1.5.0 — 14 features: middle-click toggle, device counter, synced folders submenu, configurable paths, first-run wizard, startup delay, portable mode, discovery toggles, config check, network auto-pause, auto-updater, help window, GitHub buttons, ToolTip cleanup
- v1.4.0 — Device alerts, conflict detection, pause/resume
- v1.3.0 — Start Browser setting
- v1.2.0 — Initial final release
