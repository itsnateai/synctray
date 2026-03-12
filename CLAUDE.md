# SyncthingTray — Project Instructions

## Overview
System tray manager for Syncthing on Windows. Launches syncthing.exe hidden, provides tray controls (start/stop/restart), and opens the Web UI.

## Tech Stack
- AutoHotkey v2
- Single file: `SyncthingTray.ahk` (~280 lines)
- Settings stored in `SyncthingTray.ini` (INI format)

## Build
```
Ahk2Exe.exe /in SyncthingTray.ahk /out SyncthingTray.exe /compress 0
```

## Architecture
- Config section at top with globals
- INI-based settings loaded at startup
- Tray menu rebuilt dynamically when syncthing state changes
- Settings GUI uses native AHK Gui with dark theme
- Graceful shutdown via Syncthing REST API (`POST /rest/system/shutdown`) with ProcessClose fallback
- 5-second polling timer syncs tray icon with actual process state

## Key Files
- `SyncthingTray.ahk` — main script
- `SyncthingTray.ini` — user settings (DblClickOpen, RunOnStartup, ApiKey)
- `sync.ico` / `pause.ico` — tray icons
- `AUDIT_TASKS.md` — production readiness tracker

## Conventions
- Use `/compress 0` when compiling to avoid Defender false positives
- Never commit the INI file (may contain API key)
- All globals prefixed with `g_` for non-config state variables

## Status

**v1.2.0 — Final release (shipped 2026-03-12)**

All audit items resolved (9/9). Tracking files cleared. See FINAL_REPORT.md for summary.
