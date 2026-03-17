# SyncTray — Implementation Brief: Roadmap Items

> Generated: 2026-03-17
> Based on: Roadmap_master.md P1–P2 items for synctray
> Target version: v1.6.0

---

## Current Architecture Summary

**Version:** v1.5.0 (released 2026-03-13)
**File:** `SyncthingTray.ahk` (~978 lines, single file)
**Settings:** `SyncthingTray.ini` (INI format, never committed — may contain API key)

### Key globals (top of script)
- `WebUI` — default `"http://localhost:8384"` (already configurable via INI and Settings GUI)
- `g_paused` — tracks pause state, synced from API poll
- `g_syncStatus` / `g_syncDetail` — "idle" | "syncing" | "error" | "stopped" | "paused"
- `g_connectedCount` / `g_totalDevices` — from device poll
- `g_intentionalStop` — prevents false crash alerts

### State-change pipeline
```
UpdateTrayIcon() [5s timer]
  → rebuilds icon (sync.ico / pause.ico)
  → calls BuildMenu() on state change

PollSyncStatus() [10s timer]
  → API completion + devices + conflicts + network + updates
  → calls UpdateTooltip() at end
  → updates g_paused from API response

UpdateTooltip()
  → sets A_IconTip (hover tooltip)
  → format: "SyncthingTray v1.5.0 — {status} ({detail}) | N/M devices"
```

### Tray notification pattern (consistent throughout script)
```ahk
ToolTip("message")
SetTimer(() => ToolTip(), -3000)   ; 3s for info
SetTimer(() => ToolTip(), -5000)   ; 5s for errors
```

### Middle-click (already implemented in v1.5.0)
```ahk
OnMessage(0x404, OnTrayNotify)
OnTrayNotify(wParam, lParam, msg, hwnd) {
    event := lParam & 0xFFFF
    if (event = 0x208)   ; WM_MBUTTONUP
        TogglePause()
}
```

---

## Item Analysis: What Already Exists vs What's New

| Roadmap Item | Status | Notes |
|---|---|---|
| P1/M — Middle-click pause/unpause toggle | **Already exists** | `OnMessage(0x404)` + `TogglePause()` at lines 97–103. No settings toggle yet. |
| P1/S — Click feedback (300ms tooltip) | **Already exists** | All state-change actions already show ToolTip. MenuStart, MenuStop, MenuRestart, MenuPause, MenuResume all fire tooltips. Timing is 3000ms (not 300ms — see gotcha below). |
| P1/S — Overclick safeguard | **Missing** | No debounce or cooldown on rapid calls to MenuStart/MenuStop/MenuRestart. |
| P2/S — Configurable port | **Already exists** | `WebUI` is already in Settings GUI as editable "Web UI:" field. Saves to INI as `WebUI=`. Works fully. |
| P2/S — Paused status in hover tooltip | **Already exists** | `UpdateTooltip()` already handles `g_syncStatus = "paused"` → shows "Paused". Also shows device counts. |
| P2/S — GitHub button in settings | **Already exists** | Line 682: `btnGH := sg.Add("Button", ...)` → opens `https://github.com/itsnateai/synctray`. |
| P2/S — Verify Nate's running version | **Needs runtime check** | Script reports v1.5.0. Need to verify Nate's installed .exe matches. |

---

## What Actually Needs Building

Only two items need code changes:

### 1. Middle-click settings toggle (P1/M addition)
The middle-click itself works. The roadmap wants a **settings toggle to enable/disable** it. Currently it is always enabled with no way to turn it off.

### 2. Overclick safeguard (P1/S — new)
No debounce exists. Rapid right-click → Start → Start → Stop can desync icon from process state because:
- `ProcessExist("syncthing.exe")` can return stale results during process startup (~1–2s lag)
- Multiple `Run()` calls can spawn multiple syncthing.exe processes
- `UpdateTrayIcon()` rebuilds menu based on `ProcessExist` — if called while process is still dying, it shows wrong state

---

## Implementation Steps

### Step 1: Verify Nate's installed version

Check what's in `_.releases/` and what's deployed:

```bash
ls -la "X:/_Projects/_.releases/" | grep -i sync
```

Also check `C:/Users/nate/.xn/scripts/` or wherever SyncthingTray.exe is deployed. The script version global is `Version := "1.5.0"` — if the .exe is older it will show a different version in the tooltip.

No code change needed — just a confirmation task.

---

### Step 2: Middle-click settings toggle

**What to add:**
1. New global: `global MiddleClickEnabled := true`
2. Load from INI on startup (next to other settings)
3. Add checkbox to Settings GUI: "Middle-click tray icon toggles pause"
4. Save to INI in `SaveSettings()`
5. Guard the `OnTrayNotify` handler

**Where:**

**Config section (top of file, ~line 13):**
```ahk
global MiddleClickEnabled := true
```

**INI load block (~line 56 area):**
```ahk
MiddleClickEnabled := (IniRead(SettingsFile, "Settings", "MiddleClickEnabled", "1") = "1")
```

**`OnTrayNotify` handler (lines 99–103):**
```ahk
OnTrayNotify(wParam, lParam, msg, hwnd) {
    global MiddleClickEnabled
    event := lParam & 0xFFFF
    if (event = 0x208 && MiddleClickEnabled)
        TogglePause()
}
```

**Settings GUI — General section (~after cbBrowser, before cbNetPause):**
```ahk
cbMidClick := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Middle-click tray icon toggles pause/resume")
cbMidClick.Value := MiddleClickEnabled ? 1 : 0
y += 26
```

**`SaveSettings()` — in the globals block:**
```ahk
MiddleClickEnabled := cbMidClick.Value = 1
IniWrite(MiddleClickEnabled ? "1" : "0", SettingsFile, "Settings", "MiddleClickEnabled")
```

**Help window text (~line 888)** — already mentions middle-click. Add conditional note or leave as-is.

**Files modified:** `SyncthingTray.ahk` only.

---

### Step 3: Overclick safeguard

**Goal:** Prevent rapid consecutive clicks on Start/Stop/Restart from causing icon desync or multiple spawned processes.

**Design:** Simple static timestamp guard — refuse action if last action was within cooldown window (1500ms is appropriate: gives process state time to settle).

**Pattern:**
```ahk
; At top of MenuStart, MenuStop, MenuRestart:
static lastActionTick := 0
elapsed := A_TickCount - lastActionTick
if (elapsed < 1500) {
    ToolTip("Please wait...")
    SetTimer(() => ToolTip(), -1500)
    return
}
lastActionTick := A_TickCount
```

**Important:** Each function needs its OWN static (they don't share state). The static is per-function scope in AHK v2 — this is correct behavior.

**Alternative design — shared global cooldown:**
If you want all three actions (Start/Stop/Restart) to share a single cooldown, use a global:

```ahk
global g_lastActionTick := 0

; Helper:
IsOverclickGuarded() {
    global g_lastActionTick
    if (A_TickCount - g_lastActionTick < 1500) {
        ToolTip("Please wait...")
        SetTimer(() => ToolTip(), -1500)
        return true
    }
    g_lastActionTick := A_TickCount
    return false
}
```

Then at the top of `MenuStart`, `MenuStop`, `MenuRestart`:
```ahk
if IsOverclickGuarded()
    return
```

**Recommended:** Use the shared global approach. It prevents the worst case — Stop immediately followed by Start (which is what actually causes desync). A per-function static would allow Stop→Start in rapid succession since they're different functions.

**Also guard `TogglePause()`** — rapid middle-clicks should be debounced too. Add the same guard inside `TogglePause()` (uses API, not process control, so a shorter 800ms cooldown is fine).

**Files modified:** `SyncthingTray.ahk` only.

---

### Step 4: Update Help window text

The Help window (~line 884) lists middle-click as always-on. After adding the settings toggle, add a note:

```
Middle-click — Toggle pause/resume (if enabled in Settings)
```

---

### Step 5: README update

README.md is stale — still describes v1.2.0 era features. After implementing:
- Add middle-click toggle to feature list
- Note it's configurable in Settings
- Mention overclick safeguard implicitly (not a feature users see, just stability)

---

## Files to Modify

| File | Changes |
|---|---|
| `SyncthingTray.ahk` | All code changes (globals, INI load, Settings GUI, OnTrayNotify guard, overclick protection) |
| `README.md` | Update feature list to match v1.5.0 reality + v1.6.0 additions |
| `CLAUDE.md` | Update version line after release |

---

## Implementation Order

1. **Verify Nate's version first** (5 min, no code) — check `_.releases/` + deployed path
2. **Overclick safeguard** — most impactful for stability, straightforward to add
3. **Middle-click settings toggle** — small addition, but touches 4 places in file
4. **Update Help window text** — minor, do last before compile
5. **README update** — bring up to v1.5.0/v1.6.0 feature parity
6. **Compile + VT scan** — `MSYS_NO_PATHCONV=1 "X:/_Projects/_.claude/_tools/Ahk/Ahk2Exe.exe" /in SyncthingTray.ahk /out SyncthingTray.exe /icon sync.ico /compress 0 /silent`
7. **VT scan** — `vt scan file SyncthingTray.exe`

---

## AHK v2 Patterns Reference

**ToolTip notifications (house style):**
```ahk
ToolTip("message")
SetTimer(() => ToolTip(), -3000)   ; 3s for info/success
SetTimer(() => ToolTip(), -5000)   ; 5s for errors
```

**Middle-click detection via WM_TRAYNOTIFY:**
```ahk
OnMessage(0x404, OnTrayNotify)
; lParam & 0xFFFF extracts mouse event
; 0x208 = WM_MBUTTONUP
```

**INI read with default (note: returns string "ERROR" on missing key — pattern used here is explicit default):**
```ahk
IniRead(SettingsFile, "Settings", "MiddleClickEnabled", "1")
```

**Static per-function cooldown (AHK v2):**
```ahk
static lastTick := 0
if (A_TickCount - lastTick < 1500)
    return
lastTick := A_TickCount
```

---

## Gotchas

**"300ms" vs "3000ms" in roadmap:**
The Roadmap says "300ms tooltip" — this is almost certainly a typo or shorthand for "300-millisecond-class" response time, not a 300ms display duration. A 300ms tooltip would be invisible to the user. All existing tooltips in the codebase use 3000ms (3s) for info and 5000ms (5s) for errors. Keep 3000ms for the overclick "Please wait..." message.

**Icon desync root cause:**
The tray icon is updated by `UpdateTrayIcon()` which calls `ProcessExist("syncthing.exe")`. During the ~1–2 second window between `Run(SyncExe)` and the process actually appearing, `ProcessExist` returns 0 — so the icon stays on "stopped/paused" state. The 5s polling timer catches up quickly, but rapid clicks in that window confuse state. The overclick guard's 1500ms cooldown covers this window.

**Multiple syncthing.exe processes:**
If a user clicks Start twice rapidly before the process guard fires, two instances of syncthing.exe can spawn. Syncthing itself handles this with a port conflict (second instance fails), but the error is silent. The overclick guard prevents this entirely by blocking the second click.

**`g_intentionalStop` must be set before calling `StopSyncthing()`:**
If the overclick guard prevents a Stop action from completing, `g_intentionalStop` must NOT be set (since the stop didn't actually happen). The current code sets it before the guard check — verify this ordering in the final implementation.

**Settings GUI height is dynamic (`sh := y`):**
The GUI height is computed from the running `y` variable. Adding a checkbox increases `sh` by 26. The current `sh` value (~430) is not hardcoded — the `y += 26` pattern correctly extends the window. No manual height adjustment needed.

**Middle-click toggle in Help window:**
If a user disables middle-click in Settings, the Help window still says "Middle-click — Toggle pause/resume syncing". Either update the text to add "(if enabled in Settings)" or accept the minor inconsistency. The simpler approach is to update the static help text.

**WM_TASKBARCREATED / Explorer restart:**
SyncTray does not currently handle `TaskbarCreated` (Explorer restart recovery). Not part of this plan, but worth noting as a future P2 robustness item. If Explorer crashes/restarts, the tray icon disappears and is not re-registered. Omitting from this plan scope.

---

## Version Bump

After all changes: bump `Version := "1.5.0"` → `"1.6.0"` in the config section at line 10.

Update `CLAUDE.md` status line: `v1.6.0 — Current release (YYYY-MM-DD)`

---

## Summary of Findings

- **5 of 7 roadmap items are already fully implemented** in v1.5.0:
  - Configurable port (WebUI field in Settings)
  - Paused status in hover tooltip
  - GitHub button in settings
  - Middle-click toggle (the action itself)
  - Click feedback (ToolTip on every state change)

- **2 items need implementation:**
  - Middle-click settings toggle checkbox (small addition, ~10 lines across 4 locations)
  - Overclick safeguard (new `IsOverclickGuarded()` helper + 3 call sites)

- **1 item is a verification task:**
  - Nate's running version — check `_.releases/` and deploy path

- README.md is stale and should be updated to reflect v1.5.0 features during this pass.
