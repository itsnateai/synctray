# SyncTray — Feature Backlog

> Updated: 2026-03-13 | For: SyncTray (AHK v2) v1.4.0+
> Source: Nate's picks from 50-item research list + ad-hoc requests

---

## Approved Features

### 4. Connected Devices Counter
Show "3/5 devices connected" in tooltip from `/rest/system/connections`. Already polling this endpoint — just add the count to tooltip text.

### 16. Network-Specific Profiles
Detect current network type (Public/Private). Auto-pause on Public networks. Check if Syncthing already handles this natively first. Must be lean — no background hammering.

### 29. Quick Folder Access Menu
Right-click tray submenu listing all synced folders → click opens in Explorer. Query folders ONCE on startup via `/rest/config/folders`, cache results. Fail silently with ToolTip if folder path doesn't exist.

### 35. Discovery Toggles in Settings
Quick toggles for Global Discovery, Local Discovery, and NAT Traversal (relaying). These are buried deep in Syncthing's web UI — surface them in our Settings GUI. Uses `/rest/config/options` GET/PATCH.

### 37. Misconfiguration Check
"Check Config" button in Settings that runs diagnostics: firewall rules, port accessibility, discovery status, common misconfigs. Reports findings via ToolTip or small results panel.

### 38. Startup Delay Option
INI setting for delay (in seconds) before launching syncthing.exe after login. Prevents sync from competing with other startup programs. Huge for boot-time performance.

### 42. Portable Mode
Detect when running from removable drive or non-standard path. Store all config (INI, etc.) relative to exe location instead of AppData. No registry writes.

### 44. First-Run Setup / Syncthing Path Config
- Pop up Settings GUI on first run (no INI file exists)
- Add Syncthing executable path setting (browse dialog)
- Add Web GUI URL/port setting
- Set sensible defaults, create INI on save

### 49. Syncthing Auto-Updater
Check Syncthing GitHub releases for updates. Toggle on/off in Settings. Not spammy — check once per day max. Offer upgrade method (download + replace binary + restart). Also add Syncthing GitHub link button alongside our own.

---

## Ad-Hoc Requests

### ADHOC-1: Middle-Click Toggles Pause/Resume
Middle-click on tray icon toggles syncing pause/resume using the REST API (same as menu Pause/Resume). Quick toggle without opening menu.

### ADHOC-2: GitHub Button — No URL Display
Settings GUI GitHub button should just show as a button, not display the URL text.

### ADHOC-3: Help Button in Settings
Add a Help button to Settings GUI. Populate with useful info (keyboard shortcuts, what settings do, links to Syncthing docs).

### ADHOC-4: Syncthing GitHub Button
Add a second GitHub-style button linking to Syncthing's actual GitHub repo, alongside our own repo button.

### ADHOC-5: Single Instance Enforcement
Ensure only one instance of SyncTray runs at a time. Detect and exit if already running.

---

## Done (shipped)

| # | Feature | Version |
|---|---------|---------|
| 1 | Live Sync Status Tooltip | v1.2.0 |
| 18 | Conflict Detection Alerts | v1.4.0 |
| 19 | Device Connect/Disconnect Alerts | v1.4.0 |
| — | Pause/Resume Syncing | v1.4.0 |
| — | Start Browser Setting | v1.3.0 |
