#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent

; ── Config ──────────────────────────────────────────────
global Version      := "1.5.0"
global SyncExe      := A_ScriptDir "\syncthing.exe"
global WebUI        := "http://localhost:8384"
global DblClickOpen := true
global RunOnStartup := false
global StartBrowser := false
global ApiKey       := ""
global StartupDelay := 0
global SettingsFile := A_ScriptDir "\SyncthingTray.ini"
global g_isPortable := false
; Track whether we intentionally stopped syncthing (vs unexpected crash)
global g_intentionalStop := false
; P2-02: Sync status indicator — "idle", "syncing", "error", or "unknown"
global g_syncStatus := "unknown"
global g_syncDetail := ""
; v1.4.0: Device tracking, conflict detection, pause state
global g_knownDevices := Map()
global g_devicesPollSeeded := false
global g_lastConflictCount := 0
global g_paused := false
; v1.5.0: Device counter for tooltip
global g_connectedCount := 0
global g_totalDevices := 0

; ── Portable Mode Detection ─────────────────────────────
try {
    driveLetter := SubStr(A_ScriptDir, 1, 3)
    if (DriveGetType(driveLetter) = "Removable")
        g_isPortable := true
}

; ── Load Settings ───────────────────────────────────────
g_firstRun := !FileExist(SettingsFile)
if !g_firstRun {
    try {
        DblClickOpen := (IniRead(SettingsFile, "Settings", "DblClickOpen", "1") = "1")
        RunOnStartup := (IniRead(SettingsFile, "Settings", "RunOnStartup", "0") = "1")
        ApiKey       := IniRead(SettingsFile, "Settings", "ApiKey", "")
        StartBrowser := (IniRead(SettingsFile, "Settings", "StartBrowser", "0") = "1")
        StartupDelay := Number(IniRead(SettingsFile, "Settings", "StartupDelay", "0"))
        ; Load custom paths (fall back to defaults)
        v := IniRead(SettingsFile, "Settings", "SyncExe", "")
        if (v != "")
            SyncExe := v
        v := IniRead(SettingsFile, "Settings", "WebUI", "")
        if (v != "")
            WebUI := v
    }
}

; ── Launch Syncthing Hidden ─────────────────────────────
if (StartupDelay > 0)
    Sleep(StartupDelay * 1000)
if !ProcessExist("syncthing.exe") {
    try Run(SyncExe (StartBrowser ? "" : " --no-browser"), A_ScriptDir, "Hide")
    catch {
        ToolTip("Could not launch syncthing.exe — check path in Settings")
        SetTimer(() => ToolTip(), -5000)
    }
}

; ── Tray Icon ───────────────────────────────────────────
UpdateTrayIcon()
SetTimer(UpdateTrayIcon, 5000)
SetTimer(PollSyncStatus, 10000)  ; P2-02: Poll sync status every 10s
PollSyncStatus()                  ; Initial poll

; v1.5.0: First-run — open settings if no INI exists yet
if g_firstRun
    SetTimer(MenuOpenSettings, -500)

; v1.5.0: Middle-click tray icon toggles pause/resume
OnMessage(0x404, OnTrayNotify)
OnTrayNotify(wParam, lParam, msg, hwnd) {
    event := lParam & 0xFFFF
    if (event = 0x208)   ; WM_MBUTTONUP
        TogglePause()
}

UpdateTrayIcon() {
    global g_intentionalStop, g_paused
    static lastState := -1
    static lastPaused := -1
    running := ProcessExist("syncthing.exe")
    ; Rebuild menu when state changes so Start/Stop/Pause label stays correct
    if (running != lastState || g_paused != lastPaused) {
        ; P4-01: Notify user if syncthing exited unexpectedly
        if (lastState != -1 && !running && !g_intentionalStop) {
            ToolTip("Syncthing has stopped unexpectedly!")
            SetTimer(() => ToolTip(), -5000)
            SoundBeep(300, 300)
        }
        g_intentionalStop := false
        lastState := running
        lastPaused := g_paused
        BuildMenu()
    }
    if running && !g_paused {
        ico := A_ScriptDir "\sync.ico"
        if FileExist(ico)
            TraySetIcon(ico)
        else
            TraySetIcon("shell32.dll", 14)   ; sync/refresh fallback
    } else {
        ico := A_ScriptDir "\pause.ico"
        if FileExist(ico)
            TraySetIcon(ico)
        else
            TraySetIcon("shell32.dll", 28)   ; paused/stop fallback
    }
}

; ── Sync Status Polling (P2-02) ────────────────────────
PollSyncStatus() {
    global ApiKey, WebUI, g_syncStatus, g_syncDetail
    global g_knownDevices, g_devicesPollSeeded, g_lastConflictCount, g_paused
    ; Only poll when syncthing is running and API key is configured
    if (!ProcessExist("syncthing.exe") || ApiKey = "") {
        g_syncStatus := (ProcessExist("syncthing.exe")) ? "unknown" : "stopped"
        g_syncDetail := ""
        UpdateTooltip()
        return
    }

    ; ── 1. Sync completion status ──
    try {
        whr := ComObject("WinHttp.WinHttpRequest.5.1")
        whr.Open("GET", WebUI "/rest/db/completion", false)
        whr.SetRequestHeader("X-API-Key", ApiKey)
        whr.Send()
        if (whr.Status = 200) {
            body := whr.ResponseText
            if RegExMatch(body, '"completion"\s*:\s*([\d.]+)', &m) {
                pct := Round(Number(m[1]), 1)
                if (pct >= 100) {
                    g_syncStatus := g_paused ? "paused" : "idle"
                    g_syncDetail := g_paused ? "Paused" : "Up to date"
                } else {
                    g_syncStatus := "syncing"
                    g_syncDetail := pct "% complete"
                }
            } else {
                g_syncStatus := g_paused ? "paused" : "idle"
                g_syncDetail := g_paused ? "Paused" : "Connected"
            }
        } else {
            g_syncStatus := "error"
            g_syncDetail := "API HTTP " whr.Status
        }
    } catch {
        g_syncStatus := "error"
        g_syncDetail := "API unreachable"
    }

    ; ── 2. Device connect/disconnect tracking ──
    try {
        whr2 := ComObject("WinHttp.WinHttpRequest.5.1")
        whr2.Open("GET", WebUI "/rest/system/connections", false)
        whr2.SetRequestHeader("X-API-Key", ApiKey)
        whr2.Send()
        if (whr2.Status = 200) {
            connBody := whr2.ResponseText
            ; Detect pause state: check if any device is paused
            allPaused := true
            deviceCount := 0
            connCount := 0
            ; Parse each device block — find deviceId + connected + paused
            pos := 1
            while (pos := RegExMatch(connBody, '"([A-Z0-9]{7}-[^"]+)"\s*:\s*\{', &dm, pos)) {
                deviceId := dm[1]
                ; Find the connected and paused fields within this device block
                blockStart := dm.Pos + dm.Len
                connected := false
                paused := false
                if RegExMatch(connBody, '"connected"\s*:\s*(true|false)', &cm, blockStart)
                    connected := (cm[1] = "true")
                if RegExMatch(connBody, '"paused"\s*:\s*(true|false)', &pm, blockStart)
                    paused := (pm[1] = "true")

                if !paused
                    allPaused := false
                deviceCount++
                if connected
                    connCount++

                ; Alert on state change (skip first poll to avoid startup spam)
                if g_devicesPollSeeded && g_knownDevices.Has(deviceId) {
                    wasConnected := g_knownDevices[deviceId]
                    if (connected && !wasConnected) {
                        ToolTip("Device connected")
                        SetTimer(() => ToolTip(), -3000)
                    } else if (!connected && wasConnected) {
                        ToolTip("Device disconnected")
                        SetTimer(() => ToolTip(), -3000)
                    }
                }
                g_knownDevices[deviceId] := connected
                pos := blockStart
            }
            g_devicesPollSeeded := true
            g_connectedCount := connCount
            g_totalDevices := deviceCount
            ; Update pause state from API (sync with actual state)
            if (deviceCount > 0)
                g_paused := allPaused
        }
    } catch {
        ; Connection tracking is best-effort — don't fail the whole poll
    }

    ; ── 3. File conflict detection ──
    try {
        whr3 := ComObject("WinHttp.WinHttpRequest.5.1")
        whr3.Open("GET", WebUI "/rest/db/status?folder=default", false)
        whr3.SetRequestHeader("X-API-Key", ApiKey)
        whr3.Send()
        if (whr3.Status = 200) {
            statusBody := whr3.ResponseText
            if RegExMatch(statusBody, '"pullErrors"\s*:\s*(\d+)', &em) {
                errCount := Number(em[1])
                ; Only alert when count increases (avoid spam)
                if (errCount > g_lastConflictCount && g_lastConflictCount >= 0) {
                    newErrs := errCount - g_lastConflictCount
                    ToolTip(newErrs " file error(s) detected — check Web UI")
                    SetTimer(() => ToolTip(), -5000)
                }
                g_lastConflictCount := errCount
            }
        }
    } catch {
        ; Conflict check is best-effort
    }

    UpdateTooltip()
}

UpdateTooltip() {
    global Version, g_syncStatus, g_syncDetail, g_connectedCount, g_totalDevices
    tip := "SyncthingTray v" Version
    if (g_syncStatus = "paused")
        tip .= " — Paused"
    else if (g_syncStatus = "idle")
        tip .= " — Idle"
    else if (g_syncStatus = "syncing")
        tip .= " — Syncing"
    else if (g_syncStatus = "error")
        tip .= " — Error"
    else if (g_syncStatus = "stopped")
        tip .= " — Stopped"
    if (g_syncDetail != "")
        tip .= " (" g_syncDetail ")"
    if (g_totalDevices > 0)
        tip .= " | " g_connectedCount "/" g_totalDevices " devices"
    A_IconTip := tip
}

; ── Build Tray Menu ─────────────────────────────────────
BuildMenu() {
    global DblClickOpen, RunOnStartup, WebUI, Version, g_paused

    tray := A_TrayMenu
    tray.Delete()

    ; Title with version
    titleText := "SyncthingTray v" Version
    tray.Add(titleText, (*) => "")
    tray.Disable(titleText)
    tray.Add()

    ; Clickable localhost URL
    tray.Add(WebUI, MenuOpenUI)
    tray.Add()

    tray.Add("Settings...", MenuOpenSettings)
    tray.Add()

    ; Pause/Resume syncing (lightweight — keeps process running)
    if ProcessExist("syncthing.exe") {
        if g_paused
            tray.Add("Resume Syncing", MenuResume)
        else
            tray.Add("Pause Syncing", MenuPause)
        tray.Add()
    }

    ; Syncthing process controls
    tray.Add("Restart Syncthing", MenuRestart)
    if ProcessExist("syncthing.exe")
        tray.Add("Stop Syncthing", MenuStop)
    else
        tray.Add("Start Syncthing", MenuStart)
    tray.Add()
    tray.Add("Exit", MenuExit)

    ; Set default action (bold item + double-click target)
    if DblClickOpen
        tray.Default := WebUI
    else {
        try tray.Default := ""
    }
}

; ── Startup Shortcut Helpers ─────────────────────────────
StartupLnkPath() {
    return A_Startup "\SyncthingTray.lnk"
}

ApplyStartup(enable) {
    lnk := StartupLnkPath()
    if enable {
        ; Resolve the actual script path (works compiled or as .ahk)
        target := A_IsCompiled ? A_ScriptFullPath : A_AhkPath
        args   := A_IsCompiled ? "" : '"' A_ScriptFullPath '"'
        FileCreateShortcut(target, lnk, A_ScriptDir, args, "SyncthingTray", A_ScriptDir "\sync.ico")
    } else {
        if FileExist(lnk)
            FileDelete(lnk)
    }
}

; ── Menu Actions ────────────────────────────────────────
MenuOpenUI(*) {
    Run(WebUI)
}

MenuOpenSettings(*) {
    global DblClickOpen, RunOnStartup, StartBrowser, ApiKey, Version, WebUI
    global SyncExe, StartupDelay, g_isPortable

    ; Only allow one instance of the settings window
    if WinExist("SyncthingTray Settings") {
        WinActivate("SyncthingTray Settings")
        return
    }

    sw := 360
    sh := 430
    y := 14

    sg := Gui("+AlwaysOnTop -Resize +ToolWindow", "SyncthingTray Settings")
    sg.BackColor := "1E1E2E"
    sg.SetFont("s9 cCDD6F3 bold", "Segoe UI")
    sg.Add("Text", "x16 y" y " w328", "SyncthingTray v" Version)
    y += 22
    sg.Add("Text", "x0 y" y " w" sw " h1 Background404050")
    y += 10

    ; ── General ──
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")
    cbDbl := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Double-click tray icon opens Web UI")
    cbDbl.Value := DblClickOpen ? 1 : 0
    y += 26

    cbStart := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Run on startup")
    cbStart.Value := RunOnStartup ? 1 : 0
    if g_isPortable {
        cbStart.Enabled := false
        sg.Add("Text", "x36 y" (y + 18) " w300 s8 c808090", "(not available in portable mode)")
        y += 16
    }
    y += 26

    cbBrowser := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Start browser when Syncthing launches")
    cbBrowser.Value := StartBrowser ? 1 : 0
    y += 26

    sg.Add("Text", "x16 y" y " w90 cA0A0C0", "Startup Delay:")
    edDelay := sg.Add("Edit", "x110 y" (y - 2) " w50 h22 cCDD6F3 Background2A2A3E Number", String(StartupDelay))
    sg.Add("Text", "x166 y" y " w80 cA0A0C0", "seconds")
    y += 30

    ; ── Paths section ──
    sg.SetFont("s8 cA0A0C0 bold", "Segoe UI")
    sg.Add("Text", "x16 y" y " w50", "Paths")
    sg.Add("Text", "x50 y" (y + 4) " w" (sw - 60) " h1 Background404050")
    y += 16
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")

    sg.Add("Text", "x16 y" y " w90 cA0A0C0", "Syncthing:")
    edExe := sg.Add("Edit", "x80 y" (y - 2) " w210 h22 cCDD6F3 Background2A2A3E", SyncExe)
    edExe.SetFont("s8", "Consolas")
    btnBrowse := sg.Add("Button", "x294 y" (y - 3) " w50 h24", "...")
    btnBrowse.SetFont("s8", "Segoe UI")
    btnBrowse.OnEvent("Click", (*) => (f := FileSelect(3, SyncExe, "Select syncthing.exe", "Executables (*.exe)"), f != "" ? edExe.Value := f : ""))
    y += 28

    sg.Add("Text", "x16 y" y " w90 cA0A0C0", "Web UI:")
    edWebUI := sg.Add("Edit", "x80 y" (y - 2) " w210 h22 cCDD6F3 Background2A2A3E", WebUI)
    edWebUI.SetFont("s8", "Consolas")
    y += 30

    ; ── API Key ──
    sg.SetFont("s8 cA0A0C0 bold", "Segoe UI")
    sg.Add("Text", "x16 y" y " w50", "API")
    sg.Add("Text", "x40 y" (y + 4) " w" (sw - 50) " h1 Background404050")
    y += 16
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")

    sg.Add("Text", "x16 y" y " w60 cA0A0C0", "API Key:")
    edApiKey := sg.Add("Edit", "x80 y" (y - 2) " w260 h22 cCDD6F3 Background2A2A3E", ApiKey)
    edApiKey.SetFont("s8", "Consolas")
    y += 30

    ; ── Divider ──
    sg.Add("Text", "x0 y" y " w" sw " h1 Background404050")
    y += 8

    ; Link buttons row
    sg.SetFont("s8 cCDD6F3 norm", "Segoe UI")
    btnGH := sg.Add("Button", "x16 y" y " w78 h24", "GitHub")
    btnGH.OnEvent("Click", (*) => Run("https://github.com/itsnateai/synctray"))
    btnST := sg.Add("Button", "x98 y" y " w78 h24", "Syncthing")
    btnST.OnEvent("Click", (*) => Run("https://github.com/syncthing/syncthing"))
    btnHelp := sg.Add("Button", "x180 y" y " w78 h24", "Help")
    btnHelp.OnEvent("Click", (*) => ShowHelp())
    y += 34

    ; Save / Cancel
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")
    btnSave   := sg.Add("Button", "x16 y" y " w158 h30 Default", "Save")
    btnCancel := sg.Add("Button", "x182 y" y " w158 h30", "Cancel")
    y += 40
    sh := y

    btnSave.OnEvent("Click", SaveSettings)
    btnCancel.OnEvent("Click", (*) => sg.Destroy())
    sg.OnEvent("Close", (*) => sg.Destroy())

    sg.Show("w" sw " h" sh " Center")

    SaveSettings(*) {
        global DblClickOpen, RunOnStartup, StartBrowser, ApiKey
        global SyncExe, WebUI, StartupDelay
        DblClickOpen  := cbDbl.Value = 1
        RunOnStartup  := g_isPortable ? false : (cbStart.Value = 1)
        StartBrowser  := cbBrowser.Value = 1
        ApiKey        := edApiKey.Value
        SyncExe       := edExe.Value
        WebUI         := edWebUI.Value
        StartupDelay  := Number(edDelay.Value)
        IniWrite(DblClickOpen  ? "1" : "0", SettingsFile, "Settings", "DblClickOpen")
        IniWrite(RunOnStartup  ? "1" : "0", SettingsFile, "Settings", "RunOnStartup")
        IniWrite(StartBrowser  ? "1" : "0", SettingsFile, "Settings", "StartBrowser")
        IniWrite(ApiKey, SettingsFile, "Settings", "ApiKey")
        IniWrite(SyncExe, SettingsFile, "Settings", "SyncExe")
        IniWrite(WebUI, SettingsFile, "Settings", "WebUI")
        IniWrite(String(StartupDelay), SettingsFile, "Settings", "StartupDelay")
        if !g_isPortable {
            try {
                ApplyStartup(RunOnStartup)
            } catch as e {
                ToolTip("Could not update startup shortcut: " e.Message)
                SetTimer(() => ToolTip(), -5000)
            }
        }
        BuildMenu()
        sg.Destroy()
        ToolTip("Settings saved")
        SetTimer(() => ToolTip(), -3000)
    }
}

; v1.5.0: Shared toggle logic for menu + middle-click
TogglePause() {
    global g_paused
    if g_paused
        MenuResume()
    else
        MenuPause()
}

MenuPause(*) {
    global ApiKey, WebUI, g_paused
    if (ApiKey = "") {
        ToolTip("API Key required for pause — set in Settings")
        SetTimer(() => ToolTip(), -3000)
        return
    }
    try {
        whr := ComObject("WinHttp.WinHttpRequest.5.1")
        whr.Open("POST", WebUI "/rest/system/pause", false)
        whr.SetRequestHeader("X-API-Key", ApiKey)
        whr.Send()
        g_paused := true
        UpdateTrayIcon()
        BuildMenu()
        ToolTip("Syncing paused")
        SetTimer(() => ToolTip(), -3000)
    } catch {
        ToolTip("Failed to pause syncing")
        SetTimer(() => ToolTip(), -3000)
    }
}

MenuResume(*) {
    global ApiKey, WebUI, g_paused
    if (ApiKey = "") {
        ToolTip("API Key required for resume — set in Settings")
        SetTimer(() => ToolTip(), -3000)
        return
    }
    try {
        whr := ComObject("WinHttp.WinHttpRequest.5.1")
        whr.Open("POST", WebUI "/rest/system/resume", false)
        whr.SetRequestHeader("X-API-Key", ApiKey)
        whr.Send()
        g_paused := false
        UpdateTrayIcon()
        BuildMenu()
        ToolTip("Syncing resumed")
        SetTimer(() => ToolTip(), -3000)
    } catch {
        ToolTip("Failed to resume syncing")
        SetTimer(() => ToolTip(), -3000)
    }
}

MenuStart(*) {
    try Run(SyncExe (StartBrowser ? "" : " --no-browser"), A_ScriptDir, "Hide")
    catch {
        MsgBox("Could not launch syncthing.exe`nExpected at: " SyncExe, "SyncthingTray", "Icon!")
        return
    }
    UpdateTrayIcon()
    BuildMenu()
    ToolTip("Syncthing started")
    SetTimer(() => ToolTip(), -3000)
}

MenuStop(*) {
    global g_intentionalStop
    if ProcessExist("syncthing.exe") {
        g_intentionalStop := true
        StopSyncthing()
        UpdateTrayIcon()
        BuildMenu()
        ToolTip("Syncthing stopped")
        SetTimer(() => ToolTip(), -3000)
    } else {
        ToolTip("Syncthing is not running")
        SetTimer(() => ToolTip(), -3000)
    }
}

; P1-02: Graceful shutdown via REST API, falling back to ProcessClose
StopSyncthing() {
    global ApiKey, WebUI
    if (ApiKey != "") {
        try {
            whr := ComObject("WinHttp.WinHttpRequest.5.1")
            whr.Open("POST", WebUI "/rest/system/shutdown", false)
            whr.SetRequestHeader("X-API-Key", ApiKey)
            whr.Send()
            ; Wait for syncthing to exit gracefully
            loop 50 {
                if !ProcessExist("syncthing.exe")
                    return
                Sleep(100)
            }
        }
    }
    ; Fallback: force close if REST API unavailable or timed out
    if ProcessExist("syncthing.exe") {
        ProcessClose("syncthing.exe")
        loop 30 {
            if !ProcessExist("syncthing.exe")
                break
            Sleep(100)
        }
    }
}

MenuRestart(*) {
    global g_intentionalStop
    if ProcessExist("syncthing.exe") {
        g_intentionalStop := true
        StopSyncthing()
    }
    try Run(SyncExe (StartBrowser ? "" : " --no-browser"), A_ScriptDir, "Hide")
    UpdateTrayIcon()
    BuildMenu()
    ToolTip("Syncthing restarted")
    SetTimer(() => ToolTip(), -3000)
}

MenuExit(*) {
    global g_intentionalStop
    g_intentionalStop := true
    StopSyncthing()
    ExitApp()
}

; ── Help Window ──────────────────────────────────────────
ShowHelp() {
    if WinExist("SyncthingTray Help") {
        WinActivate("SyncthingTray Help")
        return
    }
    hg := Gui("+AlwaysOnTop -Resize +ToolWindow", "SyncthingTray Help")
    hg.BackColor := "1E1E2E"
    hg.SetFont("s10 cCDD6F3 bold", "Segoe UI")
    hg.Add("Text", "x16 y14 w360", "SyncthingTray v" Version)
    hg.Add("Text", "x0 y36 w400 h1 Background404050")

    hg.SetFont("s9 cCDD6F3 norm", "Segoe UI")
    helpText := ""
        . "Tray Icon Actions:`n"
        . "  Double-click — Open Syncthing Web UI`n"
        . "  Middle-click — Toggle pause/resume syncing`n"
        . "  Right-click — Open menu`n"
        . "`n"
        . "Settings:`n"
        . "  API Key — Required for pause/resume, status polling,`n"
        . "    and graceful shutdown. Find in Syncthing Web UI`n"
        . "    under Actions > Settings > API Key.`n"
        . "`n"
        . "Status Icons:`n"
        . "  Sync icon — Syncthing is running and syncing`n"
        . "  Pause icon — Syncthing is paused or stopped`n"
        . "`n"
        . "Tooltip shows: status, sync progress, and`n"
        . "connected device count (e.g. 2/3 devices).`n"
        . "`n"
        . "Syncthing docs: docs.syncthing.net"

    hg.Add("Text", "x16 y46 w360 h280 cCDD6F3", helpText)

    btnDocs := hg.Add("Button", "x16 y336 w120 h26", "Syncthing Docs")
    btnDocs.SetFont("s8", "Segoe UI")
    btnDocs.OnEvent("Click", (*) => Run("https://docs.syncthing.net"))
    btnClose := hg.Add("Button", "x260 y336 w120 h26 Default", "Close")
    btnClose.SetFont("s8", "Segoe UI")
    btnClose.OnEvent("Click", (*) => hg.Destroy())
    hg.OnEvent("Close", (*) => hg.Destroy())
    hg.Show("w400 h380 Center")
}
