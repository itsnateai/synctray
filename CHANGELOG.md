# Changelog

*LTR — Long-Term Release · one-click self-update built in.*

All notable changes to SyncthingTray are documented here.

## v2.2.5 — 2026-04-16

Post-ship audit hotfix. 22 findings closed across security, reliability, and user-visible feedback. No breaking changes — existing configs continue to work.

### Security
- Self-update now fails closed when SHA256SUMS is missing or unreachable (was silently proceeding unverified)
- Release workflow now publishes SHA256SUMS alongside the exe so verification actually runs
- Update rollback failures now surface a clear recovery instruction instead of silently leaving a broken install

### Honesty over silence
- Network auto-pause now checks the API return status before claiming "paused on public network" (previously flipped local state regardless, lying about security posture)
- Discovery checkboxes in Settings are disabled when the current state can't be read, preventing a destructive Save that would overwrite Syncthing's real config with UI-default lies
- Discovery Save reports HTTP failures instead of silently swallowing them
- `AppConfig.Load` distinguishes locked files from corrupt ones; corrupt files are backed up as `.corrupt.bak` before overwrite instead of silently wiping settings
- Device connect/disconnect polling failures now OSD once per outage (were silently ignored)
- Per-folder conflict detection preserves prior error counts across transient failures (no more fake "N new errors" storms on recovery)
- WMI unavailability one-shot OSD when auto-pause is enabled (was silently disabled forever)
- "Run on startup" OSDs a clear failure when the shortcut can't be created or deleted (was silently ignored)
- "Open Web UI" buttons OSD the failure reason instead of doing nothing
- Startup delay input validation OSDs "kept previous value" instead of silently ignoring bad input

### Reliability
- Sync polling HTTP work moved off the UI thread — cascading 5s API timeouts no longer freeze the tray menu/tooltip
- `UpdateTrayIcon`, `BuildMenu`, and `UpdateTooltip` self-marshal to the UI thread
- Syncthing "stopped unexpectedly" alert rate-limited to once per 5 minutes (no more OSD storm during crash-restart flapping)
- First-run wizard seeds a stub INI so cancelling the dialog doesn't re-trigger first-run forever
- Crash sentinel detects when an update causes the new version to crash within 30s; next launch warns the user and points them at the `.old` backup

### Diagnostics
- Optional rolling log at `%LOCALAPPDATA%\SyncthingTray\tray.log` (1 MB cap, opt-in via `DiagnosticLogging=1` in SyncthingTray.ini)
- GDI `Pen` / `SolidBrush` allocations hoisted out of paint paths (CLAUDE.md convention)

## v2.2.4 — 2026-04-15

- docs: reword LTR tag to highlight self-update as the primary differentiator
- docs: mark as LTR (Long-Term Release)

## v2.2.3 — 2026-04-15

### Security
- Self-update now verifies downloaded binaries against a SHA256SUMS file published with each release

## v2.2.2 — 2026-04-15

### Security / Distribution
- WinGet compatibility: detect `%LOCALAPPDATA%\Microsoft\WinGet\Packages` installs and route users to `winget upgrade` instead of in-app self-update
- Self-contained publish enforces `PublishSingleFile=true` so the release exe is a true single file
- Scoop install instructions removed — WinGet only

## v2.2.1 — 2026-04-15

### Bug Fixes
- Release workflow publish step failing on .NET 10 SDK runners (pinned to 8.0.x)

## v2.2.0 — 2026-03-25

### New Features
- GitHub-based self-update: one-click download + hash check + atomic swap + relaunch
- Auto-discover `syncthing.exe` when not co-located (PATH, common install dirs, `%USERPROFILE%` depth-3 walk)
- Discovery checkbox defaults fail closed (global/local/relay all off when keys absent)

### Performance
- PID-based process check (O(1)) replaces full process enumeration when we launched Syncthing ourselves
- `GOMAXPROCS=2` passed to Syncthing child to limit its Go runtime footprint

### Bug Fixes
- `OutOfMemoryException` in process enumeration no longer crashes the tray; returns last-known state
- Update checkbox label clarified — "updates for Syncthing" (not the tray app)

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
