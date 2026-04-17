# Changelog

*LTR — Long-Term Release · one-click self-update built in.*

All notable changes to SyncthingTray are documented here.

## v2.2.6 — 2026-04-17

A steadier tray, especially when Syncthing isn't running. No breaking changes.

### Smoother tray
- **No more stall when Syncthing is off.** Clicking the tray icon or opening Settings while Syncthing was stopped could hang the menu for 5–6 seconds on every click. Now the tray checks the API with a fast probe (about 1.5 seconds) and opens immediately either way.
- **Settings window actually comes to the front.** On first open, the window occasionally appeared behind other apps and you'd have to hunt for it in the taskbar. It now activates reliably.
- **Wake-from-sleep catches up fast.** Resuming from sleep, hibernate, or Win+L now triggers an immediate status refresh instead of waiting up to a full poll cycle for the tray to notice the network came back.
- **Apply vs Save.** Clicking **Apply** in Settings no longer re-runs the full folder reload — the folder list no longer flickers when you tweak one setting at a time. **Save** continues to run the full path as before.

### Clearer errors
- **"API key rejected" is its own message.** A wrong or stale API key in Settings now surfaces as a distinct error instead of folding into a generic "could not reach Syncthing" message, so you know exactly what to fix.
- **Fewer duplicate log lines.** When multiple different warnings fire in the same minute, each type is now logged once per minute instead of every occurrence piling up in `tray.log`.

### Display & paths
- **Crisper on high-DPI displays.** The app now declares Per-Monitor V2 DPI awareness, so text and icons render sharp on 4K displays and when dragging between monitors at different scales.
- **Long paths supported.** Paths over 260 characters in folder selection now work on Windows 10/11 with long paths enabled, instead of being silently truncated.

### Safer install paths
- **Network paths rejected for `syncthing.exe`.** Pointing the tray at `\\server\share\syncthing.exe` or similar UNC paths is now refused with a clear error, rather than launching an executable across the network.

### Packaging
- **Single-file release is smaller.** Publish compression is on and native runtime libraries are embedded inside the .exe — the download drops by roughly a third and nothing loose lands next to the binary.

## v2.2.5 — 2026-04-16

A safer self-update and a lot less guessing. No breaking changes — your existing settings keep working.

### Safer updates
- **Verified downloads.** Every update is checked against a published checksum before it lands on disk. If the check can't be done for any reason, the update is aborted — no more silently installing something unverified.
- **Clear recovery when an update fails** — you get a concrete next step instead of a broken install with no explanation.
- **Crash-during-update detection.** If a new version crashes within 30 seconds of launching, the next time you start SyncthingTray you're told what happened and pointed at the `.exe.old` backup so you can roll back.

### Honest status, no more lies
- **Auto-pause tells the truth.** When you move to a public Wi-Fi, SyncthingTray only claims "paused" if Syncthing actually paused. Previously the tray could flip to "paused" locally while syncing continued in the background.
- **Discovery settings reflect reality.** If the tray can't read Syncthing's current discovery state, those checkboxes are disabled with an explanation — you can't accidentally overwrite Syncthing's real config with the wrong values.
- **Save failures are reported** for Discovery, "Run on startup", the "Open Web UI" buttons, and the startup-delay field — no more clicking Save, getting nothing back, and wondering if it took.
- **Settings file recovery.** If `SyncthingTray.ini` is corrupt or locked, the tray says so, uses safe defaults, and preserves your original file as a `.corrupt.bak` before overwriting.
- **Fewer fake alerts.** The "file error detected" notification no longer cries wolf during a brief Syncthing hiccup, and the "Syncthing stopped unexpectedly" alert is rate-limited so a crash-restart loop doesn't spam you.

### Smoother tray
- **Tray stays responsive during Syncthing slowdowns.** If Syncthing's API hangs for a few seconds, the menu, tooltip, and right-click still open immediately.
- **First-run wizard only runs once.** Closing it without saving no longer triggers it again on the next launch.
- **Device connect/disconnect notifications** now tell you when the background polling that drives them has stopped working, so you're never silently left without them.
- **Multi-user machines.** On shared PCs, launching SyncthingTray no longer silently fails when another user also has it running.

### Diagnostics
- **New log file** at `%LOCALAPPDATA%\SyncthingTray\tray.log` — attach this to any bug report. 1 MB cap, rotates once. Disable with `DiagnosticLogging=0` in `SyncthingTray.ini` if you prefer.

## v2.2.4 — 2026-04-15

- **Marked as a Long-Term Release.** The in-app Update button is the recommended way to stay current.

## v2.2.3 — 2026-04-15

- **Update integrity check.** The in-app Update button now checks the downloaded file against a checksum published with each release.

## v2.2.2 — 2026-04-15

- **Works with WinGet.** If you installed through `winget install itsnateai.SyncthingTray`, the in-app Update button sends you back to `winget upgrade` instead of trying to overwrite the managed install.
- **Release build is a true single file** — no loose `.dll` files next to the .exe anymore.

## v2.2.1 — 2026-04-15

- **Fixed a broken build pipeline** that was producing releases with missing pieces.

## v2.2.0 — 2026-03-25

### New Features
- **One-click self-update** from the Settings window — download, verify, and install the latest release without leaving the tray.
- **Auto-discover `syncthing.exe`** — if it isn't sitting next to SyncthingTray, the tray checks your PATH and the usual install locations automatically.
- **Discovery settings default to off** when the settings file is missing them, so a fresh install never quietly announces you to the network.

### Performance
- **Faster status checks** — the tray spends less time scanning running processes when Syncthing is already running.
- **Lower Syncthing CPU footprint** — Syncthing is launched with a reduced thread budget so it's gentler on the rest of your machine.

### Bug Fixes
- **No more tray crash under heavy memory pressure** — the tray keeps running and recovers on the next check instead of dying.
- **Clearer Auto-update label** in Settings — it's for Syncthing updates, not SyncthingTray updates.

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
