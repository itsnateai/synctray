# SyncTray Feature Ideas — 50 Integration Concepts

> Research sources: [Syncthing REST API docs](https://docs.syncthing.net/dev/rest.html), [SyncTrayzor (archived)](https://github.com/canton7/SyncTrayzor), [SyncTrayzor v2](https://github.com/GermanCoding/SyncTrayzor), [Martchus/syncthingtray](https://github.com/Martchus/syncthingtray), [Syncthing issues](https://github.com/syncthing/syncthing/issues), Nate's workflow requirements
>
> Generated: 2026-03-09 | For: SyncTray (AHK v2)

---

## Category 1: Real-Time Status & Monitoring

### 1. Live Sync Status Tooltip
Poll `/rest/system/status` and `/rest/db/completion` to show real-time sync state in tray tooltip — idle, syncing (with %), error, or paused. SyncTrayzor's most-loved feature.

### 2. Per-Folder Completion Bars
Query `/rest/db/status` per folder to show individual folder sync progress. Display as a compact multi-bar in a hover popup or mini-GUI panel.

### 3. Bandwidth Monitor Overlay
Poll `/rest/system/connections` to show real-time upload/download rates. Display in tooltip or a small always-on-top floating widget (like a net meter).

### 4. Connected Devices Counter
Show "3/5 devices connected" in tooltip by querying `/rest/system/connections`. Color-code: green = all connected, yellow = partial, red = none.

### 5. Connection Quality Indicator
Distinguish between direct vs relay connections from `/rest/system/connections` (`type` field). Show a warning icon when stuck on relay — users often don't realize they're on slow relay and never fix it. (Top SyncTrayzor request #551.)

### 6. Last Sync Timestamp
Track and display "Last successful sync: 2 min ago" in tooltip. Query `/rest/events` for `FolderCompletion` events. Gives quick confidence that things are working without opening the web UI.

### 7. Error Badge with Count
Show a red badge overlay on tray icon when errors exist. Poll `/rest/system/error` to count active errors. Click badge to see error list. Clear when resolved.

### 8. Folder Health Dashboard
A mini-GUI showing all folders with status icons (synced/syncing/error/paused). One-glance view instead of opening the full web UI. Modeled after Dropbox's folder status list.

---

## Category 2: Smart Automation & Power Awareness

### 9. Battery-Aware Pause/Resume
Detect battery state via Windows WMI or `A_IsBattery`. Auto-pause Syncthing when on battery (or below a threshold like 20%). Top SyncTrayzor request (#283) — users reported 50% battery drain from sync encryption overhead.

### 10. Metered Network Detection
Use Windows Network Cost API to detect metered connections (mobile hotspots, metered WiFi). Auto-pause syncing. SyncTrayzor shipped this — it's expected table stakes.

### 11. Scheduled Sync Windows
Allow users to define sync hours (e.g., "only sync 8 PM–6 AM"). Use `/rest/system/pause` and `/rest/system/resume` at scheduled times. Highly requested in SyncTrayzor #569.

### 12. Timed Pause ("Pause for 30 min")
Right-click menu option: "Pause for 15 min / 30 min / 1 hour / Until I resume." Auto-resumes after timer expires. SyncTrayzor #369 — users wanted this for presentations, gaming, video calls.

### 13. Fullscreen/Gaming Detection
Detect when a fullscreen application is running (game, presentation, video call). Auto-pause syncing to prevent bandwidth/CPU competition. Resume when fullscreen exits.

### 14. CPU Load Throttling
Monitor system CPU usage. When CPU > 80% for sustained period, pause syncing. Resume when load drops. Prevents sync from degrading other work.

### 15. Bandwidth Limit Quick Toggle
Right-click tray menu: "Limit bandwidth" toggle. Switches between unlimited and a preconfigured limit (e.g., 1 MB/s) via `/rest/config/options` PATCH. SyncTrayzor #328 — avoids opening web UI for a common action.

### 16. Network-Specific Profiles
Remember sync settings per WiFi network (SSID). Auto-apply: "Home = unlimited, Office = limited, Hotspot = paused." Detects current network and applies the right profile.

---

## Category 3: Notifications & Alerts

### 17. File Change Notifications
Subscribe to `/rest/events` for `ItemFinished` events. Show tooltip notification: "README.md synced from Laptop2." Configurable: all files, specific folders, or specific extensions only.

### 18. Conflict Detection Alerts
Monitor for `.stconflict` files via `/rest/events` (`ItemFinished` with conflict flag) or filesystem polling. Alert immediately — conflicts left unnoticed cause data loss. SyncTrayzor shipped this.

### 19. Device Connect/Disconnect Alerts
Watch `/rest/events` for `DeviceConnected` and `DeviceDisconnected`. Notify: "Swift (Laptop 2) connected" or "Swift disconnected 5 min ago." Useful for our multi-machine workflow.

### 20. Notification Filtering
Let users filter notifications by: folder, device, file extension, event type. SyncTrayzor lacked this (#323, #657) — users complained about alert fatigue from noisy devices.

### 21. Slack/Discord Webhook Integration
Fire webhooks on important events (sync errors, device offline > 1 hour, conflicts). Our workflow already uses Discord webhooks for Claude notifications — extend pattern to Syncthing events.

### 22. Sound Alerts by Event Type
Different sounds for: error, conflict, device disconnect, sync complete. Currently only crash gets a sound. Let users assign WAV files per event type.

### 23. Stale Sync Warning
If a folder hasn't completed sync in > X hours but has pending changes, show a persistent warning. Catches stuck syncs that silent failures miss. SyncTrayzor had a stale warning bug (#498) — do it right.

---

## Category 4: File Management & Operations

### 24. Recent Files Panel
Show last 20 synced files in a right-click submenu or mini-panel. Query `/rest/events` for `ItemFinished`. Click to open file or reveal in Explorer.

### 25. Conflict Resolution Tool
Scan for `.stconflict` files across all synced folders. Present a GUI: show both versions side by side (name, date, size), let user pick which to keep. SyncTrayzor's conflict resolver was popular.

### 26. Click Notification to Reveal File
When a file sync notification appears, clicking it opens Explorer to that file's location. SyncTrayzor #636 — media professionals loved this idea. Single file = reveal, batch = open folder.

### 27. Sync Activity Log Viewer
Maintain a persistent log of sync activity (like SyncTrayzor's DownloadLog.csv). Add an in-app viewer — SyncTrayzor had the CSV but no viewer (#630), which users complained about.

### 28. Selective Folder Pause/Resume
Right-click submenu listing all folders with pause/resume toggles. Uses `/rest/config/folders/{id}` PATCH to toggle `paused`. More granular than pausing all of Syncthing.

### 29. Quick Folder Access Menu
Tray right-click shows all synced folders as menu items. Click to open in Explorer. Double-click tray icon opens primary folder. SyncTrayzor #659 — users wanted Dropbox-like "open my folder" behavior.

### 30. File Versioning Quick Access
If file versioning is enabled, provide a right-click menu to browse/restore previous versions via `/rest/folder/versions`. Surfaces a buried Syncthing feature.

---

## Category 5: Device & Network Management

### 31. One-Click Device Pause/Resume
Right-click submenu listing all devices with pause/resume toggles. Uses `/rest/system/pause?device=ID` and `/rest/system/resume?device=ID`. Faster than web UI.

### 32. Device Nickname Display
Show user-friendly device names in all menus/notifications instead of device IDs. Pull from `/rest/config/devices`. Our machines are "Asus" and "Swift" — show those names.

### 33. QR Code Device ID Display
Generate a QR code from the local device ID for easy pairing. Display in settings GUI. Avoids manual ID copy-paste. SyncTrayzor #316 considered this but rejected as complex — trivial with AHK QR libraries.

### 34. Auto-Accept Known Devices
When a new device tries to connect that matches a whitelist pattern (e.g., same user prefix), auto-accept via `/rest/config/devices` POST. Reduces friction for adding new machines.

### 35. Network Discovery Status
Show whether local discovery and global discovery are working. Query `/rest/system/status` for `discoveryEnabled`, `discoveryStatus`. Alert if discovery is failing — common cause of "can't find device" issues.

---

## Category 6: System Integration

### 36. Windows Dark/Light Theme Matching
Detect Windows theme (dark/light) and switch tray icon accordingly. White icon for dark taskbar, dark icon for light. SyncTrayzor #553 — expected on modern Windows.

### 37. Windows Firewall Auto-Configuration
Detect if Syncthing is blocked by Windows Firewall. Offer one-click rule creation. SyncTrayzor #446 — users constantly struggle with firewall setup.

### 38. Startup Delay Option
Add configurable delay before launching Syncthing after login (e.g., 30s). Prevents sync from competing with other startup programs. Reduces boot-time disk thrash.

### 39. Run as Windows Service Option
Option to install Syncthing as a Windows service instead of user-space process. Survives logoff, starts before login. Uses `nssm` or native service wrapper. Power-user feature.

### 40. Context Menu Shell Extension
Register a Windows Explorer context menu entry: right-click folder → "Add to Syncthing" or right-click file → "Show sync status." Requires registry entries — doable from AHK.

---

## Category 7: Configuration & Settings

### 41. Multi-Instance Support
Support managing multiple Syncthing instances on one machine (different ports/configs). Useful for separating personal and work sync. SyncTrayzor had basic support via custom home path.

### 42. Portable Mode
Detect when running from removable drive. Store all config relative to exe location. No registry writes, no AppData. SyncTrayzor's portable ZIP was popular for USB workflows.

### 43. Config Backup/Restore
One-click backup of Syncthing config + SyncTray settings to a ZIP. Restore from backup. Simplifies migration between machines. Export to synced folder for automatic cross-machine backup.

### 44. First-Run Setup Wizard
Guided setup for new users: locate syncthing.exe, set API key, choose startup behavior, pick primary folder. Reduces "where do I start?" friction.

### 45. Settings Profiles
Save/load named configuration profiles ("Home", "Office", "Travel"). Switch via tray menu. Each profile stores: bandwidth limits, active folders, paused devices, sync schedule.

---

## Category 8: Developer & Power-User Features

### 46. Event Scripting Hooks
Execute user-defined scripts/commands on events: sync complete, conflict detected, device connected, error occurred. SyncTrayzor #374 requested RUNONSTART/RUNONEXIT — go further with full event hooks.

### 47. CLI Control Interface
Accept command-line arguments for scripting: `SyncTray.exe --pause`, `--resume`, `--status`. Enables integration with Task Scheduler, AHK hotkeys, other automation. SyncTrayzor did this for start/stop.

### 48. REST API Pass-Through
Expose a simplified local API or hotkey system that wraps common Syncthing REST calls. Power users can bind AHK hotkeys to actions: `Win+Shift+P` = pause sync, `Win+Shift+S` = show status.

### 49. Syncthing Auto-Updater
Check for new Syncthing releases via GitHub API or `/rest/system/upgrade`. Notify user and offer one-click update. Download, replace binary, restart. SyncTrayzor handled this automatically.

### 50. Debug/Support Bundle Export
One-click collection of: Syncthing logs, SyncTray config, system info, network status, recent errors. Packages into a ZIP for troubleshooting. Syncthing has `/rest/debug/support` — wrap it with local context.

---

## Implementation Priority Suggestion

| Tier | Items | Rationale |
|------|-------|-----------|
| **Next (v1.2)** | 1, 6, 17, 29, 36 | Low effort, high daily value — status tooltip, last sync time, file notifications, folder access, dark mode icons |
| **Soon (v1.3)** | 2, 4, 7, 12, 15, 28 | Medium effort — completion bars, device counter, error badge, timed pause, bandwidth toggle, folder pause |
| **Growth (v2.0)** | 5, 8, 9, 10, 19, 20, 24, 25, 27 | Significant features — connection quality, dashboard, battery/metered, device alerts, filtering, recent files, conflicts, log viewer |
| **Power (v2.x)** | 11, 13, 16, 21, 31, 46, 47, 49 | Advanced automation — scheduling, fullscreen detect, network profiles, webhooks, device control, event hooks, CLI, auto-update |
| **Polish (v3.0)** | 22, 23, 26, 30, 33, 37, 40, 43, 44, 45, 50 | UX refinements and enterprise features |
| **Niche** | 3, 14, 34, 35, 38, 39, 41, 42, 48 | Situational value — implement based on user demand |

---

## Notes

- All REST API features require the API key configured (already supported in v1.1)
- Event-based features should use `/rest/events` long-polling instead of interval polling to reduce CPU
- SyncTrayzor's biggest mistake was embedding Chromium (CefSharp) — keep SyncTray native AHK for the lightweight advantage
- Our multi-machine workflow (Asus + Swift via Syncthing) makes device-aware features especially valuable
- AHK v2's `WebSocket` or `ComObject("WinHTTP.WinHTTPRequest")` can handle REST calls efficiently
