# SyncthingTray — Project Instructions

## Overview
System tray manager for Syncthing on Windows. Launches syncthing.exe hidden, provides tray controls (start/stop/restart/pause/resume), opens the Web UI, monitors device connections and file conflicts, manages discovery settings, and checks for Syncthing updates.

## Tech Stack
- AutoHotkey v2
- Single file: `SyncthingTray.ahk` (~970 lines)
- Settings stored in `SyncthingTray.ini` (INI format)
- Syncthing REST API for status polling, pause/resume, config, and auto-updates

## Build
```bash
MSYS_NO_PATHCONV=1 "X:/_Projects/_tools/Ahk/Ahk2Exe.exe" /in SyncthingTray.ahk /out SyncthingTray.exe /icon sync.ico /compress 0 /silent
```
- Icons embedded via `@Ahk2Exe-AddResource` (sync.ico=10, pause.ico=11) — compiled .exe works standalone

## Architecture
- Config section at top with globals
- INI-based settings loaded at startup
- Tray menu rebuilt dynamically when syncthing state changes
- Settings GUI uses native AHK Gui with dark theme (sectioned layout with bold headers + dividers)
- API helper functions: `ApiGet()`, `ApiPost()`, `ApiPatch()` — centralized WinHttp boilerplate
- Graceful shutdown via Syncthing REST API (`POST /rest/system/shutdown`) with ProcessClose fallback
- 10-second polling timer: completion status + device connections + conflict detection + network auto-pause + update checks
- Anti-spam: devices seeded silently on first poll, alerts only on state changes
- Portable mode detection: disables RunOnStartup on removable drives
- First-run detection: auto-opens Settings GUI when no INI exists

## Key Files
- `SyncthingTray.ahk` — main script
- `SyncthingTray.ini` — user settings (DblClickOpen, RunOnStartup, ApiKey, StartBrowser, SyncExe, WebUI, StartupDelay, NetworkAutoPause, AutoCheckUpdates)
- `sync.ico` / `pause.ico` — tray icons (embedded in .exe; disk copies for source users running .ahk)
- `syncthing.md` — feature backlog (approved features + done table)

## Conventions
- Use `/compress 0` when compiling to avoid Defender false positives
- Never commit the INI file (may contain API key)
- All globals prefixed with `g_` for non-config state variables
- ToolTip for notifications (not TrayTip/MsgBox)

## Status

**v1.5.0 — Current release (2026-03-13)**

## Changelog

See git log for full history. Key releases:
- v1.5.0 — 14 features: middle-click toggle, device counter, synced folders submenu, configurable paths, first-run wizard, startup delay, portable mode, discovery toggles, config check, network auto-pause, auto-updater, help window, GitHub buttons, ToolTip cleanup
- v1.4.0 — Device alerts, conflict detection, pause/resume
- v1.3.0 — Start Browser setting
- v1.2.0 — Initial final release (all audit items resolved)
