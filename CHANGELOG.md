# Changelog

*LTR — Long-Term Release · one-click self-update built in.*

All notable changes to SyncthingTray are documented here.

## v2.2.27 — 2026-04-18

### Reliability
- **Native handles drop on fast-exit.** Three one-shot WinForms Timers (startup-delay, first-run-settings-open, 30-second-stability-proof) were created as local `var`s that only self-disposed inside their own Tick handlers. If the tray exited before the Tick fired (fast shutdown under 30 seconds — restart loop, Windows logoff, killed from Task Manager), the timers leaked their native timer handles until the finalizer thread eventually ran. They're now tracked in a `_oneShotTimers` field; Tick removes each one on fire, and `Dispose(bool)` stops + disposes whatever's left.
- **Syncthing launch no longer leaks a Process handle on a rare race.** `_launchedPid = p.Id` followed by a separate `p.Dispose()` left the handle leaked if `.Id` threw (process exited between `Process.Start` and the Id read — observed on slow disks with a corrupt `syncthing.exe`). The `Process` now lives in a `using` so disposal runs regardless; the PID is still captured to the int field before the scope ends.

## v2.2.26 — 2026-04-18

### Reliability
- **Inherited pause now survives a stale post-reapply snapshot.** When the tray restarts with a pause from the previous session, it re-POSTs `/rest/system/pause` to Syncthing on the first successful poll. The old shape of that code cleared the `_pauseNeedsReapply` flag as soon as the POST returned 200 — but the *next* poll could still see `allPaused == false` if its snapshot was fetched before Syncthing finished applying the pause, or if Syncthing silently rejected the request, or if an admin resumed concurrently. When that happened, the external-resume branch silently dropped the inherited pause the user hadn't touched. The flag now stays set until a subsequent poll actually observes `allPaused == true`; any unconfirmed tick just re-POSTs (idempotent server-side). Also marked the flag `volatile` — it's written from both the UI thread (Restore/MenuPause/ClearPauseState) and the poll thread (ReapplyInheritedPause) — matching the pattern already used on `_foldersLoadedSuccessfully` for cross-thread publication ordering.

## v2.2.25 — 2026-04-17

### Reliability
- **Tray no longer crashes on boot from a hand-edited INI.** `AppConfig.Load` used `int.TryParse` with no bounds on `StartupDelay`, then the constructor did `new Timer { Interval = _config.StartupDelay * 1000 }`. A value like `180000` (user typo, or muscle memory from milliseconds) overflows after `× 1000` to a negative `int`, and WinForms `Timer.Interval` throws `ArgumentOutOfRangeException` on anything `≤ 0` — silent first-line boot crash, no OSD, no log, no sign of what happened. The load path now clamps to `[0, 3600]`, matching the Settings-dialog NumericUpDown invariant.

## v2.2.24 — 2026-04-17

### Reliability
- **Tray menu no longer freezes when Syncthing is slow or dead.** Every menu click that talked to Syncthing — Pause, Resume, Rescan All, Rescan Folder, Check for Update, Upgrade Syncthing — was calling the REST API synchronously on the UI thread. With `HttpClient.Timeout = 5 s`, a dead or flaky Syncthing meant the menu stayed dismissed-but-frozen and the tray icon's tooltip stuck at "not responding" until the timeout popped. Worst offender: the pause auto-resume timer's deadline fire (user never clicked anything, yet their tray froze for up to 5 s). All seven HTTP sites now run on the thread pool via `Task.Run`; state mutations + icon/menu updates marshal back to UI via `RunOnUi`.
- **Pause always sends the POST.** Previously, switching an active 5-min pause to a 30-min pause re-used the local `_paused=true` state and skipped the REST POST — which meant if Syncthing had been resumed externally through its Web UI, the tray kept showing "paused" until the next 10-second poll reconciled it. Duration changes now always re-post; `/rest/system/pause` is idempotent server-side so this is purely an accuracy fix, not a behavior change for the happy path.

## v2.2.23 — 2026-04-17

### Reliability
- **Pause state machine is now thread-safe.** Three background-thread code paths were mutating UI-affine state directly on the thread pool: the auto-pause block (when the machine joins a public wifi), the auto-resume block (when returning to a private wifi), and the external-resume detector (when someone hits Resume in the Syncthing Web UI while the tray thinks sync is paused). Each called `_pauseTimer.Stop()` + `BuildMenu()` + `UpdateTrayIcon()` directly. WinForms `Timer.Stop()` off the UI thread is undefined — 99 runs of 100 the call silently succeeded; the 100th would throw or corrupt the timer queue, leaving a dangling auto-resume that never fires. Plus the `OnPowerModeChanged` wake handler read `_paused` and `_pauseResumeAtUtc` on the SystemEvents background thread before marshaling, racing with user-initiated MenuResume/ClearPauseState. All four sites now wrap the read/state/timer/menu writes in `RunOnUi(() => …)` so every pause-state mutation happens on the UI thread atomically.

## v2.2.22 — 2026-04-17

### Settings
- **No more jolt on Save.** The Save button used to run up to ~1200 ms of synchronous work on the UI thread: a 300 ms socket probe + up to 1500 ms discovery PATCH + 50-200 ms WScript.Shell COM call for the startup shortcut + the tray-refresh callback which itself fires three more HTTP GETs. Now `_config.Save()` (the actual INI write) runs inline — so the file is guaranteed on disk before the dialog closes — and everything after it runs on a pool thread. The dialog dismisses instantly; OSDs for any async failure still surface correctly via the existing self-marshaling path.

## v2.2.21 — 2026-04-17

### Settings
- **Button rows right-aligned with the form.** Both the top action row (GitHub / Update / Syncthing / Help / Check Config) and the bottom row (Save / Apply / Cancel) now end at x=394, giving a symmetric 16 px margin on both sides and visual alignment between the two rows. Previously the top row ended at x=384 (26 px margin) and the bottom row at x=370 (40 px margin) — enough asymmetry to read as "a bit off" on the bottom-right corner.

## v2.2.20 — 2026-04-17

### Help window
- **Backup Settings button.** Added between "Syncthing Docs" and "Close" in the Help window footer. Copies `SyncthingTray.ini` (containing the API key and every user-visible setting) to a timestamped sibling file — `SyncthingTray.ini.backup-YYYYMMDD-HHMMSS` — in the same directory. The confirmation OSD shows the new filename. This is the user-facing complement to the existing `.corrupt.bak` rotation that kicks in only on detected corruption — now users can snapshot pre-emptively before a Windows update, disk migration, or fresh-install test. All three footer buttons resized to 110 px wide with symmetric 19 px gaps.

## v2.2.19 — 2026-04-17

### Settings
- **Windows startup delay — text left-aligned.** Matches the convention every other input box in the dialog already follows (paths, Web UI, API key). NumericUpDown defaults to right-align which made it the only odd one out.

## v2.2.18 — 2026-04-17

Settings-dialog polish: clearer startup-delay control, no more jerk during auto-populate.

### Settings
- **Startup Delay → NumericUpDown.** Replaced the free-form text box with a spin-to-5 numeric control. Users can still type any value directly; the control clamps to `[0, 3600]` on both spinner and keyboard entry, so the separate range-check + error OSD in the save path is gone (value can't be invalid by construction).
- **Label clarified: "Windows startup delay".** The setting's primary purpose is the delay on Windows auto-start before SyncTray launches Syncthing — the new label makes that plain.
- **No more jerk when discovery auto-populates.** When Settings was open during a fresh Syncthing cold-start, the 2 s retry timer fired the 300 ms socket probe + up to 1500 ms HTTP GET synchronously on the UI thread — freezing the dialog at exactly the worst moment (right as Syncthing came alive). The HTTP work now runs on a pool thread via `Task.Run`; the UI update marshals back with `BeginInvoke` and runs inside `SuspendLayout` / `ResumeLayout` so the three checkboxes, their Enabled flips, and the warning-label removal repaint once at the end instead of cascading through five separate layout passes.

## v2.2.17 — 2026-04-17

No more mouse-spinner when opening a synced folder.

### Synced Folders
- **Open Folder hands off to a pool thread.** `Process.Start` was running on the UI thread, so a cold Explorer launch (100-500 ms native process fork + shell init) kept the tray's context menu and mouse cursor locked in "busy" state for ~1.5 s per click. Now the shell call runs on `Task.Run` and the menu dismisses instantly; the Explorer window still renders at whatever pace Windows manages it, but the tray stops spinning. Errors (bad path, permissions) surface as OSDs via UI-thread marshaling.
- **Uses `UseShellExecute=true` instead of spawning `explorer.exe` directly.** The shell reuses an already-running Explorer process where possible, meaningfully faster than forking a new one every click.
- **UNC paths skip the pre-check.** `Directory.Exists` on an unreachable network share could block the UI thread for the full SMB timeout (~20-30 s). Local paths still get the friendly "Folder not found" OSD; UNC paths delegate the existence check to the shell.

## v2.2.16 — 2026-04-17

Synced Folders now group by remote device (structured data), not by folder-label prefix.

### Synced Folders
- **Grouped by Remote Device.** Syncthing's `/rest/config/folders` includes a `devices` array per folder; the tray now uses that plus `/rest/config/devices` for human-readable names and `/rest/system/status` for the local myID (to filter self out). Each folder appears under every remote device it's shared with — a folder shared with 5 devices appears under all 5 device headers, which is correct and deliberate. The previous label-prefix heuristic (split on space/underscore, ≥2 folders) was fragile: it silently gave up on any naming convention that used hyphens or no separators at all.
- **"Local only" bucket.** Folders that aren't shared with any remote device fall under a "Local only" header at the bottom instead of disappearing into an unlabeled group.
- **Graceful degradation.** If the device roster fetch fails, device headers fall back to the short base32 handle (first hyphen-separated chunk of the device ID). If the myID fetch fails, the local device leaks as its own group until the next successful poll — ugly, not broken.
- **Dups are safe.** Each duplicated folder entry is an independent `ToolStripMenuItem` with its own click-handler captures; disposal cascades correctly from the parent menu on every rebuild. No shared refs, no leak, no double-fire.

## v2.2.15 — 2026-04-17

Synced Folders — singletons now render after the device groups, not before.

### Synced Folders
- **"Other" bucket sorts last.** The unlabeled singletons group (folders whose prefix doesn't appear on ≥2 folders) was rendering at the top of the Synced Folders submenu instead of at the bottom. Cause: the previous sort-sentinel trick used `\uFFFE` as a placeholder key intended to sort last, but `StringComparer.CurrentCultureIgnoreCase` is a linguistic comparer that treats reserved/non-character code points as ignorable — so the sentinel compared as empty string and pushed singletons above `s20`/`s24`/`tablet`. Replaced the sentinel trick with explicit ordering: named device groups first (alphabetical by prefix), singletons last.

## v2.2.14 — 2026-04-17

Snappier close when "Stop Syncthing when tray exits" is on.

### Exit
- **Close lag cut from ~10s to ~4s worst case.** The tray used to hold the UI frozen while `StopSyncthing` ran synchronously with a 5 s HttpClient timeout + 5 s polling wait. An in-flight poll-tick could queue a GET on the single localhost keep-alive connection that blocked the shutdown POST behind it — visible as a multi-second freeze between clicking Exit and the tray icon disappearing. Now timers are stopped first (no new polls can race), the shutdown POST is capped at 2 s, and the post-shutdown wait is capped at 2 s. If Syncthing hasn't exited by then the force-kill fallback takes over immediately.

## v2.2.13 — 2026-04-17

Network-adaptive restore for inherited auto-pauses.

### Pause
- **Auto-pause flag persisted.** `pause.dat` now stores whether the pause was triggered by network auto-pause (line 3: `0` = manual, `1` = auto). Legacy 2-line files from v2.2.12 are still accepted and default to manual.
- **Reboot-on-private-after-public-auto-pause adapts.** If the tray inherits an auto-pause from the previous session but the current network is private (or domain), the stale pause is dropped on startup instead of being re-applied to a freshly-launched Syncthing. Syncing resumes on boot automatically rather than requiring a manual Resume click. Manual pauses always re-apply regardless of network, preserving explicit user intent.

## v2.2.12 — 2026-04-17

Pause submenu polish + reboot-survival, plus two settings-flow fixes.

### Pause
- **"Until resumed" promoted to the top** of the submenu with a separator underneath, framing it as the primary action and the timed options as secondary choices.
- **Survives reboots.** SyncthingTray re-applies the inherited pause to Syncthing on the first successful poll after startup. Syncthing's own `/rest/system/pause` is runtime-only, so without the re-apply a reboot would have silently dropped the pause. Expired deadlines (pause.dat older than its timer) are skipped rather than re-applied just to auto-resume a moment later. User changes made in the Syncthing Web UI while the tray is offline still take precedence — the poll reconciliation catches that.

### Settings
- **"Start browser when Syncthing launches" now works on every tray start.** Previously the browser only popped when Syncthing itself cold-started, so closing and relaunching the tray while Syncthing kept running silently skipped the browser-open. The tray now owns browser-opening — passes `--no-browser` to Syncthing and fires OpenWebUI once per tray session on the first reachable poll.
- **Discovery section auto-refreshes.** When the dialog opens during a fresh cold start (Syncthing still binding its REST port), the three Discovery checkboxes used to be stuck disabled with "(could not read current state)" until the user closed and reopened the window. The dialog now retries `/rest/config/options` every 2 s in the background; once the read succeeds, the checkboxes populate and the warning label vanishes on its own.

## v2.2.11 — 2026-04-17

Timed pause + slimmer help text.

### Pause (MWBToggle-style submenu)
- **5 min / 30 min / Until resumed.** Right-click → Pause Syncing opens a submenu with three durations. Timed pauses auto-resume at the deadline and show the remaining time on the Resume item. "Until resumed" stays paused until you click Resume.
- **Survives sleep.** Deadlines are stored as absolute UTC time, so a 30 min pause through a 2 hr sleep resumes at wake rather than 30 min after wake.
- **Survives tray restart.** Active pause state persists to `pause.dat` in the app dir. Closing and reopening the tray mid-pause preserves the countdown.
- **External resume detected.** Hitting Resume in the Syncthing Web UI clears the local timer on the next poll, so a stale deadline can't double-fire the resume path.
- Double-click / middle-click still pause untimed — the click-path behavior from earlier versions is preserved.

### Help window
- **Content trimmed.** The verbose prose paragraphs are replaced with tight bulleted sections that still cover every tray interaction, menu item, settings group, and troubleshooting path — just faster to scan.

## v2.2.10 — 2026-04-17

Settings UI polish — tighter Tray Click Actions row.

### Settings
- **Click-action dropdowns sized to content.** Double-click and Middle-click combos were 250 px wide — wider than the path and API key fields in the same window. Shrunk to 160 px so each control's width matches its expected content, matching the convention used by Windows 11 Settings, VS Code, and JetBrains dialogs.

## v2.2.9 — 2026-04-17

Folders-by-device, proper API key masking, fuller help window, normal close button.

### Synced Folders
- **Grouped by device.** Labels that share a prefix (`s20_*`, `s24_*`, `tablet_*`) now cluster under a dimmed device header, with the prefix stripped from the child labels so the submenu reads as a true two-level list. Folders with unique names drop into an unnamed group at the bottom. Alphabetical-only sorting from v2.2.8 is replaced.

### Settings
- **API Key is masked.** The field starts hidden, matching how other apps present secrets. A Segoe MDL2 eye toggle next to the field reveals the key while pasting or verifying.
- **Normal close button.** The Settings window's tool-window chrome gave a cramped, oddly-placed X in the top right. Settings now uses the standard fixed-dialog chrome — full-size X in the usual spot.

### Help window
- **Proper help content.** The old terse bullet list is replaced with structured prose covering tray interactions, every context menu item, every Settings section, the API key workflow, troubleshooting the most common OSDs, and the diagnostic log location.
- **Section headers.** Content renders in a RichTextBox with blue section headers and wrapped body paragraphs, matching MicMute's help style but tuned for SyncTray's narrower window.

## v2.2.8 — 2026-04-17

Scannable Synced Folders menu — big readability win for users with many folders.

### Synced Folders
- **Alphabetical order.** The submenu followed Syncthing's config order, which was effectively random. Folders are now sorted case-insensitively, so device clusters like `s20_*`, `s24_*`, and `tablet_*` line up in natural groups instead of shuffling through the list.
- **Group separators.** A thin divider is drawn between letter-groups when either side has three or more folders, giving the eye a rest point on long lists while keeping isolated names grouped at the top.

## v2.2.7 — 2026-04-17

Help window fixes — no functional changes elsewhere.

### Help window
- **Status section no longer clipped.** The help text below "Status:" was painted past the window edge with no way to scroll. The help body is now inside a scrollable panel, so every line is reachable regardless of DPI.
- **Divider above buttons.** A subtle horizontal line now separates the help text from the **Syncthing Docs** and **Close** buttons, matching the divider under the title.

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
