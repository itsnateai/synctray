# Merge Notes — AHK→C# Conversion Branch

> **For the Claude Code session that merges `claude/ahk-to-csharp-conversion-Jlcgj` into `master`.**
> Delete this file after merge. Do NOT commit it to master.

---

## What This Branch Contains

5 commits converting SyncthingTray from AutoHotkey v2 to C# .NET 8 WinForms:

1. `feat:` Full conversion — 13 source files, ~2000 lines
2. `fix:` 4 resource leak/safety bugs caught in deep audit
3. `perf:` Hot-path allocation elimination (cached process check, tooltip dirty-check)
4. `fix:` Poll timer re-entrancy guard, dark context menu, regex cleanup, .gitignore
5. `chore:` README/CLAUDE.md rewrite, v2.0.0 bump, GitHub Actions CI

## Critical: First Build Verification

**This code has never been compiled.** The dev environment was Linux without .NET SDK. The first real build will happen either:
- On the GitHub Actions CI after merge/PR (`.github/workflows/build.yml`)
- Or manually on a Windows machine with .NET 8 SDK

Run this to verify:
```bash
cd SyncthingTray
dotnet build -c Release    # Must compile with 0 warnings (TreatWarningsAsErrors)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Likely Build Issues to Watch For

1. **`System.Management` package restore** — The .csproj references `System.Management` v8.0.0 for WMI. If NuGet restore fails, check network/feed config.

2. **`[GeneratedRegex]` source generator** — Requires .NET 8 SDK. If building on .NET 7 or earlier, these will fail. Ensure `net8.0-windows` target matches installed SDK.

3. **`LibraryImport` in NativeMethods.cs** — Uses .NET 7+ source-generated P/Invoke. If you see marshalling errors, check that the project targets `net8.0-windows` exactly.

4. **`#pragma warning disable SYSLIB0014`** in SyncthingApi.cs — Suppresses the HttpWebRequest obsolete warning. This is intentional: we need synchronous HTTP without async, and HttpWebRequest is the only built-in option. If the pragma syntax causes issues, ensure `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is in the .csproj.

5. **Embedded resources** — The .csproj uses `LogicalName` for icons:
   ```xml
   <EmbeddedResource Include="Resources\sync.ico" LogicalName="sync.ico" />
   ```
   The code loads them via `Assembly.GetManifestResourceStream("sync.ico")`. If icons don't appear, check `LogicalName` matches the string in `LoadEmbeddedIcon()`.

6. **`ApplicationConfiguration.Initialize()`** — This is a .NET 8 source-generated method. If it doesn't exist, you may need to add a `ApplicationConfiguration.cs` or switch to manual `Application.EnableVisualStyles()` + `Application.SetHighDpiMode()`.

---

## Lessons Learned (Patterns to Follow / Avoid)

### Resource Management — The #1 Source of Bugs

Every bug found in audit was a resource leak or disposal issue:

| Pattern | Rule | Why |
|---------|------|-----|
| `Process.GetProcessesByName()` | **Always** wrap every Process in `using` | Returns an array; EVERY element holds an OS handle |
| `Process.Start()` | **Always** `using var p = Process.Start(...)` | Returns a Process that holds a handle |
| `SystemIcons.Application` | **Never** return shared system icons as "owned" | Disposing a shared singleton crashes the app |
| `GetManifestResourceStream()` | **Always** dispose after reading | Returns a Stream that should be closed |
| `ContextMenuStrip` old menu | **Don't** call `Items.Clear()` before `Dispose()` | Clear detaches items from the component container so Dispose can't cascade |
| `NotifyIcon` | Set `Visible = false` **before** Dispose | Otherwise ghost icon lingers in tray until mouseover |
| COM objects | **Always** `Marshal.ReleaseComObject()` in `finally` | COM ref counts don't participate in GC |
| `Font` objects | Owner must dispose; don't let multiple objects "share" a Font without clear ownership | Font is IDisposable; GC finalizer is unreliable |

### Hot-Path Allocation — Zero-Waste Idle

This app runs 24/7 in the tray. When idle (no state changes), it should allocate nothing:

| Pattern | Fix |
|---------|-----|
| `Process.GetProcessesByName` called 4-6x per 10s cycle | Cache result with 2s TTL; invalidate on start/stop |
| Tooltip string rebuilt every poll via `$""` + `+=` | Component-level dirty check: only rebuild when status/detail/devices change |
| `$"SyncthingTray v{Version}"` rebuilt on every menu/tooltip | `static readonly` constant computed once |
| `$"{pct}% complete"` rebuilt every poll | Cache when pct hasn't changed |
| `new Regex(pattern)` inline | `[GeneratedRegex]` source-generated, `static readonly` field |
| `RegexOptions.Compiled` on `[GeneratedRegex]` | Redundant — source generator already compiles at build time. Remove the flag. |

### Timer Re-Entrancy

`System.Windows.Forms.Timer` fires on the UI thread, so ticks can't overlap with each other. But if a tick handler blocks for longer than the interval (e.g., 5 API calls x 5s timeout = 25s blocking on a 10s timer), the NEXT tick queues up and fires immediately after the handler returns. Fix: stop the timer at the start of the handler, restart in `finally`.

### WinForms Tray App Gotchas

1. **Explorer restart destroys tray icons.** You MUST handle `RegisterWindowMessage("TaskbarCreated")` in a WndProc override and re-set `trayIcon.Visible = true`.

2. **`NotifyIcon.Text` max is 127 characters.** Truncate before setting or you get an ArgumentOutOfRangeException.

3. **`ApplicationContext` already implements `IDisposable`.** Don't re-declare `IDisposable` on the class or you get a redundant interface warning (fatal with TreatWarningsAsErrors).

4. **`Application.OpenForms` collection** can change during enumeration. The current settings-single-instance check uses a foreach over it — this is safe as long as you're on the UI thread and not adding/removing forms inside the loop.

5. **`BalloonTipText`** creates Windows toast notifications that stack up and persist in Action Center. Use a borderless topmost Form instead (OsdToolTip pattern).

6. **`Assembly.Location`** returns empty string in single-file publish. Always use `Environment.ProcessPath`.

### Dark Theme for WinForms

WinForms doesn't have native dark mode. The pattern used here:
- Forms: set `BackColor` to `0x1E1E2E`, `ForeColor` to `0xCDD6F3`
- ContextMenuStrip: assign a custom `ToolStripProfessionalRenderer` subclass with overridden `ProfessionalColorTable`
- Buttons: `FlatStyle = FlatStyle.Flat` + explicit colors
- TextBoxes: `BorderStyle.FixedSingle` + explicit `BackColor`/`ForeColor`

---

## File-by-File Quick Reference

| File | Lines | Hot Path? | Key Concern |
|------|-------|-----------|-------------|
| `Program.cs` | 27 | No | Process disposal in single-instance kill |
| `TrayApplicationContext.cs` | ~950 | **YES** | Timer callbacks, polling, menu rebuild, tooltip |
| `AppConfig.cs` | 125 | No | File I/O try/catch, UTF-8 no BOM |
| `SyncthingApi.cs` | 73 | Warm | HttpWebRequest per call, SYSLIB0014 pragma |
| `SettingsForm.cs` | 480 | No | 6 Font objects disposed in Dispose(bool) |
| `HelpForm.cs` | 129 | No | Fonts stored in Tag array for disposal |
| `OsdToolTip.cs` | 110 | Warm | ShowMessage per notification, timer start/stop |
| `DarkMenuRenderer.cs` | 77 | Warm | Shared static instance, SolidBrush/Pen per render |
| `NativeMethods.cs` | 14 | No | LibraryImport source-gen P/Invoke |
| `StartupShortcut.cs` | 70 | No | COM cleanup in finally block |

---

## Feature Parity Verification

All 38 AHK features verified present in C#. All timing values match exactly:
- Tooltip durations: 3000ms / 5000ms (match AHK)
- StopSyncthing: 50 loops x 100ms graceful, 30 loops x 100ms force (match AHK)
- Poll intervals: 5000ms icon, 10000ms status (match AHK)
- Overclick: 1500ms default, 800ms pause (match AHK)
- Startup delay, first-run timer (500ms) — all match

---

## Final Deep Audit Findings (from automated review)

A comprehensive automated review of all .cs files found 2 real issues (fixed in final commit) and several theoretical concerns that are safe in practice:

**Fixed:**
- `SyncthingApi.cs` — `GetResponseStream()` can return null on edge network conditions. Added null checks on both success and error paths to prevent NullReferenceException in StreamReader constructor.

**Reviewed and determined safe (no fix needed):**
- `BuildMenu()` called while menu is open — assigning new menu before disposing old is the standard WinForms pattern. Windows handles the transition gracefully.
- `OsdToolTip` thread safety — all callers marshal to UI thread via `Invoke`/`InvokeRequired`. Timers are `System.Windows.Forms.Timer` (UI thread). No actual race.
- `Dispose()` ordering — `Visible=false` then dispose menu then dispose icon is the recommended pattern.
- `MessageWindow` handle creation — always on UI thread because `Program.Main` has `[STAThread]`.
- `Thread.Sleep` in `StopSyncthing` blocks UI — matches AHK behavior, only during shutdown. Acceptable.

---

## After Merge Checklist

- [ ] Verify GitHub Actions CI passes (first ever build)
- [ ] Fix any build warnings/errors found
- [ ] Test on Windows: tray icon appears, menu works, settings save/load
- [ ] Test with syncthing.exe: start/stop/restart, pause/resume
- [ ] Test API features: device tracking, sync status, update check
- [ ] Test edge cases: no INI file (first run), Explorer restart, double-launch
- [ ] Create GitHub Release with published .exe artifact
- [ ] Delete this MERGE_NOTES.md file
