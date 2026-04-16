# Changelog

*LTR — Long-Term Release · one-click self-update built in.*

All notable changes to SyncthingTray are documented here.

## v2.1.0 — 2026-03-18

### New Features
- **Rescan Now** — trigger a rescan for all folders or an individual folder right from the tray menu.
- **Configurable double-click and middle-click actions** — Web UI, Rescan, Pause/Resume, or Do Nothing.
- **Dark-themed dropdowns** in Settings.

### Bug Fixes
- **No more rare crash when exiting during the startup delay.**
- **Pause/Resume no longer gets stuck reporting the wrong state** — the tray now reflects the real Syncthing state after you pause or resume.
- **Conflict detection works for all your folders**, not just the one named "default".
- **Tray icon recovers after Explorer restarts** — the icon comes back automatically instead of vanishing until relaunch.
- **Stopping Syncthing only stops the instance SyncthingTray launched** — no longer kills other `syncthing` processes you may be running separately.
- **Config save errors are reported** instead of silently failing.
- **Menu no longer freezes while Syncthing is shutting down.**
- **No resource leak during startup shortcut handling.**
- **Menu opens faster when Syncthing's API is unreachable** — the tray fails fast (about 5 seconds) instead of waiting through a long timeout chain (~25 seconds).
- **Rescan is skipped when Syncthing isn't running** instead of throwing a cryptic error.

### Robustness
- **Faster update and status checks** with connection pooling.
- **More robust API parsing.**
- **Startup delay no longer freezes the tray** — you can still open menus while SyncthingTray is waiting to launch Syncthing.
- **Save / Apply / Cancel** in Settings, like standard Windows dialogs.
- **OSD notifications** replace intrusive pop-up dialogs for informational messages.
- **Smoother dark menu rendering.**

## v2.0.0 — 2026-03-17

### New Features
- **Full rewrite from AutoHotkey v2 to C# .NET 8 WinForms.**
- All v1.6.0 features preserved in the C# port.
- **Dark-themed context menu.**
- **No overlapping status polls** when Syncthing's API is slow — tray stays responsive.
- **Lower idle overhead and snappier tooltips** — the tray uses less CPU and updates only when something actually changes.
- **Embedded icons** — tray icons are built into the .exe, no external `.ico` files needed at runtime.

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
