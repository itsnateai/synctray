#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent

; ── Config ──────────────────────────────────────────────
global Version      := "1.4.0"
global SyncExe      := A_ScriptDir "\syncthing.exe"
global WebUI        := "http://localhost:8384"
global DblClickOpen := true
global RunOnStartup := false
global StartBrowser := false
global ApiKey       := ""
global SettingsFile := A_ScriptDir "\SyncthingTray.ini"
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

; ── Load Settings ───────────────────────────────────────
if FileExist(SettingsFile) {
    try {
        val := IniRead(SettingsFile, "Settings", "DblClickOpen", "1")
        DblClickOpen := (val = "1")

        val2 := IniRead(SettingsFile, "Settings", "RunOnStartup", "0")
        RunOnStartup := (val2 = "1")

        val3 := IniRead(SettingsFile, "Settings", "ApiKey", "")
        ApiKey := val3

        val4 := IniRead(SettingsFile, "Settings", "StartBrowser", "0")
        StartBrowser := (val4 = "1")
    }
}

; ── Launch Syncthing Hidden ─────────────────────────────
if !ProcessExist("syncthing.exe") {
    try Run(SyncExe (StartBrowser ? "" : " --no-browser"), A_ScriptDir, "Hide")
    catch {
        MsgBox("Could not launch syncthing.exe`nExpected at: " SyncExe, "SyncthingTray", "Icon!")
        ExitApp()
    }
}

; ── Tray Icon ───────────────────────────────────────────
UpdateTrayIcon()
SetTimer(UpdateTrayIcon, 5000)
SetTimer(PollSyncStatus, 10000)  ; P2-02: Poll sync status every 10s
PollSyncStatus()                  ; Initial poll

UpdateTrayIcon() {
    global g_intentionalStop, g_paused
    static lastState := -1
    static lastPaused := -1
    running := ProcessExist("syncthing.exe")
    ; Rebuild menu when state changes so Start/Stop/Pause label stays correct
    if (running != lastState || g_paused != lastPaused) {
        ; P4-01: Notify user if syncthing exited unexpectedly
        if (lastState != -1 && !running && !g_intentionalStop) {
            TrayTip("Syncthing has stopped unexpectedly!", "SyncthingTray", 3)
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
    global Version, g_syncStatus, g_syncDetail
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

    ; Only allow one instance of the settings window
    if WinExist("SyncthingTray Settings") {
        WinActivate("SyncthingTray Settings")
        return
    }

    sw := 320
    sh := 276

    sg := Gui("+AlwaysOnTop -Resize +ToolWindow", "SyncthingTray Settings")
    sg.BackColor := "1E1E2E"
    sg.SetFont("s9 cCDD6F3 bold", "Segoe UI")
    sg.Add("Text", "x16 y14 w288", "SyncthingTray v" Version)

    ; Divider
    sg.Add("Text", "x0 y34 w" sw " h1 Background404050")

    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")

    ; Checkboxes
    cbDbl := sg.Add("Checkbox", "x16 y48 w280 cCDD6F3", "Double-click tray icon opens Web UI")
    cbDbl.Value := DblClickOpen ? 1 : 0

    cbStart := sg.Add("Checkbox", "x16 y74 w280 cCDD6F3", "Run on startup")
    cbStart.Value := RunOnStartup ? 1 : 0

    cbBrowser := sg.Add("Checkbox", "x16 y100 w280 cCDD6F3", "Start browser when Syncthing launches")
    cbBrowser.Value := StartBrowser ? 1 : 0

    ; API Key for graceful shutdown
    sg.Add("Text", "x16 y126 w60 cA0A0C0", "API Key:")
    edApiKey := sg.Add("Edit", "x80 y124 w220 h22 cCDD6F3 Background2A2A3E", ApiKey)
    edApiKey.SetFont("s8", "Consolas")

    ; Divider
    sg.Add("Text", "x0 y156 w" sw " h1 Background404050")

    ; GitHub button
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")
    sg.Add("Text", "x16 y168 w60 cA0A0C0", "GitHub:")
    btnGH := sg.Add("Button", "x80 y164 w200 h22", "github.com/itsnateai/SyncthingTray")
    btnGH.SetFont("s8", "Segoe UI")
    btnGH.OnEvent("Click", (*) => Run("https://github.com/itsnateai/SyncthingTray"))

    ; Divider
    sg.Add("Text", "x0 y198 w" sw " h1 Background404050")

    ; Save / Cancel
    btnSave   := sg.Add("Button", "x16 y210 w130 h30 Default", "Save")
    btnCancel := sg.Add("Button", "x158 y210 w130 h30", "Cancel")

    btnSave.OnEvent("Click", SaveSettings)
    btnCancel.OnEvent("Click", (*) => sg.Destroy())
    sg.OnEvent("Close", (*) => sg.Destroy())

    ; Center on screen
    sg.Show("w" sw " h" sh " Center")

    SaveSettings(*) {
        global DblClickOpen, RunOnStartup, StartBrowser, ApiKey
        DblClickOpen  := cbDbl.Value = 1
        RunOnStartup  := cbStart.Value = 1
        StartBrowser  := cbBrowser.Value = 1
        ApiKey        := edApiKey.Value
        IniWrite(DblClickOpen  ? "1" : "0", SettingsFile, "Settings", "DblClickOpen")
        IniWrite(RunOnStartup  ? "1" : "0", SettingsFile, "Settings", "RunOnStartup")
        IniWrite(StartBrowser  ? "1" : "0", SettingsFile, "Settings", "StartBrowser")
        IniWrite(ApiKey, SettingsFile, "Settings", "ApiKey")
        try {
            ApplyStartup(RunOnStartup)
        } catch as e {
            MsgBox("Could not update startup shortcut:`n" e.Message, "SyncthingTray", "Icon!")
        }
        BuildMenu()
        sg.Destroy()
        TrayTip("Settings saved", "SyncthingTray")
    }
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
    TrayTip("Syncthing started", "SyncthingTray")
}

MenuStop(*) {
    global g_intentionalStop
    if ProcessExist("syncthing.exe") {
        g_intentionalStop := true
        StopSyncthing()
        UpdateTrayIcon()
        BuildMenu()
        TrayTip("Syncthing stopped", "SyncthingTray")
    } else {
        TrayTip("Syncthing is not running", "SyncthingTray")
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
    TrayTip("Syncthing restarted", "SyncthingTray")
}

MenuExit(*) {
    global g_intentionalStop
    g_intentionalStop := true
    StopSyncthing()
    ExitApp()
}
