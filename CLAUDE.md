# SyncthingTray — Project Instructions

## Overview
System tray manager for Syncthing on Windows. Launches syncthing.exe hidden, provides tray controls (start/stop/restart/pause/resume), opens the Web UI, and monitors device connections and file conflicts.

## Tech Stack
- AutoHotkey v2
- Single file: `SyncthingTray.ahk` (~450 lines)
- Settings stored in `SyncthingTray.ini` (INI format)
- Syncthing REST API for status polling, pause/resume, and graceful shutdown

## Build
```bash
MSYS_NO_PATHCONV=1 "X:/_Projects/_tools/Ahk/Ahk2Exe.exe" /in SyncthingTray.ahk /out SyncthingTray.exe /compress 0 /silent
```

## Architecture
- Config section at top with globals
- INI-based settings loaded at startup
- Tray menu rebuilt dynamically when syncthing state changes
- Settings GUI uses native AHK Gui with dark theme
- Graceful shutdown via Syncthing REST API (`POST /rest/system/shutdown`) with ProcessClose fallback
- 10-second polling timer: completion status + device connections + file conflict detection
- Anti-spam: devices seeded silently on first poll, alerts only on state changes

## Key Files
- `SyncthingTray.ahk` — main script
- `SyncthingTray.ini` — user settings (DblClickOpen, RunOnStartup, ApiKey, StartBrowser)
- `sync.ico` / `pause.ico` — tray icons (pause shown when syncing is paused via API)

## Conventions
- Use `/compress 0` when compiling to avoid Defender false positives
- Never commit the INI file (may contain API key)
- All globals prefixed with `g_` for non-config state variables

## Status

**v1.4.0 — Current release (2026-03-13)**

Features added since v1.2.0:
- v1.3.0: Start Browser setting in Settings GUI
- v1.4.0: Device connect/disconnect ToolTip alerts, file conflict detection, pause/resume syncing via REST API, reworked tray menu layout

## Changelog

See git log for full history. Key releases:
- v1.4.0 — Device alerts, conflict detection, pause/resume
- v1.3.0 — Start Browser setting
- v1.2.0 — Initial final release (all audit items resolved)
