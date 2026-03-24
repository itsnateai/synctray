# SyncthingTray Crash Report — 2026-03-23

## Context
Crash occurred during heavy system memory load (9 parallel Semgrep scans).

## Exception
```
System.OutOfMemoryException: Insufficient memory to continue the execution of the program.
   at System.Diagnostics.NtProcessInfoHelper.GetProcessInfos(Nullable`1 processIdFilter, String processNameFilter)
   at SyncthingTray.TrayApplicationContext.IsSyncthingRunning()
   at SyncthingTray.TrayApplicationContext.UpdateTrayIcon()
   at System.Windows.Forms.NativeWindow.Callback(HWND hWnd, MessageId msg, WPARAM wparam, LPARAM lparam)
```

## Analysis
- **Root cause**: `NtProcessInfoHelper.GetProcessInfos()` allocates to enumerate all running processes. Under memory pressure, this allocation fails.
- **Call chain**: `UpdateTrayIcon()` → `IsSyncthingRunning()` → `GetProcessInfos()` — this runs on every tray icon update (timer tick or system message).
- **Severity**: Low — only crashes under extreme memory pressure, but should be handled gracefully.

## Suggested Fixes
1. **Wrap `IsSyncthingRunning()` in try/catch for `OutOfMemoryException`** — return last known state on failure instead of crashing.
2. **Cache the process check result** — don't re-enumerate processes on every tick. A 10-30s cache would prevent rapid re-allocation.
3. **Consider using `Process.GetProcessesByName("syncthing")` instead of enumerating all processes** — smaller allocation footprint.

## Assembly Version
SyncthingTray v2.1.2.0, .NET 8.0
