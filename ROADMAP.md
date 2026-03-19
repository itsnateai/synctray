# SyncthingTray Roadmap

Future feature ideas, primarily from competitive research against Martchus syncthingtray and SyncTrayzor v2.

## High Priority

### Per-device and per-folder pause/resume
Martchus Tray supports pausing individual devices and folders (not just global pause). Syncthing API supports `POST /rest/system/pause?device=<deviceId>` and per-folder pause via config.
**Effort:** Medium — needs device/folder submenus with pause/resume actions

### Recent changes / activity log viewer
Both Martchus Tray and SyncTrayzor v2 show recent file change history. Syncthing provides `GET /rest/events?events=ItemFinished` for tracking.
**Effort:** Medium — new form or submenu, event polling

## Medium Priority

### Sync progress / download window
SyncTrayzor v2 has a Dropbox-style file download progress window showing per-file transfer progress.
**Effort:** High — new form with progress bars, real-time API polling

## Low Priority

### Throughput / connection graphs
QSyncthingTray (dead project) had throughput and connection graphs. Would need a charting library or custom GDI+ drawing.
**Effort:** High — charting infrastructure

### Conflict resolution wizard
SyncTrayzor v2 has a dedicated conflict resolution wizard. Would walk users through resolving `.sync-conflict-*` files.
**Effort:** High — file scanning, diff display, resolution actions

## Ideas

- Explorer shell integration (overlay icons on synced folders, right-click rescan)
- Hotkey support for tray menu / web UI access
- Remote Syncthing instance connection (manage syncthing on another machine)
