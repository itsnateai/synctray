# Changelog

All notable changes to SyncthingTray are documented here.

## v2.1.0 — 2026-03-18

### New Features
- Rescan Now — trigger rescan for all folders or individual folders from the tray menu
- Configurable double-click and middle-click actions (Web UI, Rescan, Pause/Resume, Do Nothing)
- Dark-themed owner-draw ComboBoxes in Settings

### Bug Fixes
- Startup timer crash: guard against ObjectDisposedException when exiting during startup delay
- Pause/Resume state desync: check API return status before updating local state
- Conflict detection now checks all synced folders (was hardcoded to "default")
- Explorer restart: force icon re-assignment when taskbar is recreated
- StopSyncthing targets launched PID instead of killing all syncthing processes
- Config Save() reports failure to user instead of silently swallowing errors
- DoEvents re-entrancy: stop icon timer and guard menu actions during StopSyncthing
- COM shortcut object properly released in finally block (was leaked on exception)
- API fast-fail: skip remaining poll calls when first API call fails (reduces freeze from 25s to 5s)
- Rescan checks if Syncthing is running before making API call

### Robustness
- HttpClient with connection pooling (replaces per-request WinHttp COM objects)
- System.Text.Json for API response parsing (replaces regex)
- Non-blocking startup delay (timer-based, keeps message pump alive)
- Save/Apply/Cancel button pattern in Settings
- OSD notifications instead of MessageBox
- Cached GDI objects in DarkMenuRenderer

## v2.0.0 — 2026-03-17

### New Features
- Full rewrite from AutoHotkey v2 to C# .NET 8 WinForms
- All v1.6.0 features preserved in the C# port
- Dark-themed context menu (custom ToolStripProfessionalRenderer)
- Poll timer re-entrancy guard (prevents stacking on cascading API timeouts)
- Memory-optimized hot paths (cached process checks, dirty-check tooltips)
- Embedded icons via .NET resources (no external .ico files needed at runtime)

## v1.6.0 — 2026-03-16

### New Features
- Middle-click tray icon toggles pause/resume (configurable in Settings)
- Overclick safeguard — shared cooldown prevents rapid Start/Stop/Restart/Pause desync

## v1.5.0 — 2026-03-15

### New Features
- Middle-click tray icon toggles pause/resume
- Device counter in tooltip (e.g. "2/3 devices")
- Synced Folders submenu — open any synced folder in Explorer, rescan individual folders
- Configurable Syncthing exe path and Web UI URL
- First-run wizard — auto-opens Settings when no config exists
- Startup delay setting (seconds before launching Syncthing)
- Portable mode — auto-detected on removable drives (disables startup shortcut)
- Discovery toggles (Global, Local, NAT Traversal) in Settings
- Config Check utility (validates exe, process, API, discovery)
- Network auto-pause on public networks (WMI-based)
- Auto-update check for Syncthing (daily, rate-limited)
- Help window with usage guide
- GitHub and Syncthing buttons in Settings
- ToolTip cleanup (auto-dismiss pattern)

## v1.4.0 — 2026-03-14

### New Features
- Device connect/disconnect notifications
- File conflict (pull error) detection
- Pause/Resume syncing via tray menu

## v1.3.0

### New Features
- Start Browser setting — optionally open Web UI when Syncthing launches

## v1.2.0

### New Features
- Initial release
- Launches Syncthing hidden (no console window)
- Tray icon with sync/pause state icons
- Start, stop, restart Syncthing from tray menu
- Open Syncthing Web UI on double-click
- Graceful shutdown via REST API with process kill fallback
- Crash detection with audible alert
- Run at Windows startup (Startup folder shortcut)
- Single-instance enforcement
- Tray icon recovery after Explorer restarts
