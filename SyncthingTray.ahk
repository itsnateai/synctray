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
; v1.5.0: Cached folder list for tray submenu
global g_folders := []
; v1.5.0: Network auto-pause
global NetworkAutoPause := false
global g_lastNetworkCategory := -1
global g_autoPaused := false
; v1.5.0: Auto-updater
global AutoCheckUpdates := false
global g_lastUpdateCheck := 0
global g_updateAvailable := ""
global g_updateRunning := ""

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
        NetworkAutoPause := (IniRead(SettingsFile, "Settings", "NetworkAutoPause", "0") = "1")
        AutoCheckUpdates := (IniRead(SettingsFile, "Settings", "AutoCheckUpdates", "0") = "1")
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

; v1.5.0: Load synced folders for tray submenu (once on startup)
LoadFolders()

; v1.5.0: Middle-click tray icon toggles pause/resume
OnMessage(0x404, OnTrayNotify)
OnTrayNotify(wParam, lParam, msg, hwnd) {
    event := lParam & 0xFFFF
    if (event = 0x208)   ; WM_MBUTTONUP
        TogglePause()
}

; ── API Helpers ────────────────────────────────────────
ApiGet(endpoint) {
    global ApiKey, WebUI
    whr := ComObject("WinHttp.WinHttpRequest.5.1")
    whr.Open("GET", WebUI endpoint, false)
    whr.SetRequestHeader("X-API-Key", ApiKey)
    whr.Send()
    return {status: whr.Status, body: whr.ResponseText}
}

ApiPost(endpoint, body := "") {
    global ApiKey, WebUI
    whr := ComObject("WinHttp.WinHttpRequest.5.1")
    whr.Open("POST", WebUI endpoint, false)
    whr.SetRequestHeader("X-API-Key", ApiKey)
    if (body != "") {
        whr.SetRequestHeader("Content-Type", "application/json")
        whr.Send(body)
    } else
        whr.Send()
    return {status: whr.Status, body: whr.ResponseText}
}

ApiPatch(endpoint, body) {
    global ApiKey, WebUI
    whr := ComObject("WinHttp.WinHttpRequest.5.1")
    whr.Open("PATCH", WebUI endpoint, false)
    whr.SetRequestHeader("X-API-Key", ApiKey)
    whr.SetRequestHeader("Content-Type", "application/json")
    whr.Send(body)
    return {status: whr.Status, body: whr.ResponseText}
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
        r := ApiGet("/rest/db/completion")
        if (r.status = 200) {
            if RegExMatch(r.body, '"completion"\s*:\s*([\d.]+)', &m) {
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
            g_syncDetail := "API HTTP " r.status
        }
    } catch {
        g_syncStatus := "error"
        g_syncDetail := "API unreachable"
    }

    ; ── 2. Device connect/disconnect tracking ──
    try {
        r2 := ApiGet("/rest/system/connections")
        if (r2.status = 200) {
            connBody := r2.body
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
        r3 := ApiGet("/rest/db/status?folder=default")
        if (r3.status = 200) {
            if RegExMatch(r3.body, '"pullErrors"\s*:\s*(\d+)', &em) {
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

    ; ── 4. Network auto-pause ──
    if NetworkAutoPause {
        try {
            cat := GetNetworkCategory()
            if (cat != g_lastNetworkCategory && g_lastNetworkCategory != -1) {
                if (cat = 0 && !g_paused) {
                    ; Public network detected — auto-pause
                    ApiPost("/rest/system/pause")
                    g_paused := true
                    g_autoPaused := true
                    UpdateTrayIcon()
                    BuildMenu()
                    ToolTip("Auto-paused: public network detected")
                    SetTimer(() => ToolTip(), -3000)
                } else if (cat != 0 && g_autoPaused) {
                    ; Back on private/domain — auto-resume
                    ApiPost("/rest/system/resume")
                    g_paused := false
                    g_autoPaused := false
                    UpdateTrayIcon()
                    BuildMenu()
                    ToolTip("Auto-resumed: private network detected")
                    SetTimer(() => ToolTip(), -3000)
                }
            }
            g_lastNetworkCategory := cat
        }
    }

    ; ── Section 5: Auto-update check (rate-limited to once per 24h) ──
    if (AutoCheckUpdates && ApiKey != "" && ProcessExist("syncthing.exe")) {
        elapsed := A_TickCount - g_lastUpdateCheck
        if (g_lastUpdateCheck = 0 || elapsed > 86400000) {  ; 24 hours in ms
            CheckForUpdate()
        }
    }

    UpdateTooltip()
}

; ── Update Check Functions ───────────────────────────────
CheckForUpdate() {
    global g_lastUpdateCheck, g_updateAvailable, g_updateRunning
    g_lastUpdateCheck := A_TickCount
    try {
        r := ApiGet("/rest/system/upgrade")
        if (r.status = 200) {
            newer := false
            latest := ""
            running := ""
            if RegExMatch(r.body, '"newer"\s*:\s*(true|false)', &mn)
                newer := (mn[1] = "true")
            if RegExMatch(r.body, '"latest"\s*:\s*"([^"]*)"', &ml)
                latest := ml[1]
            if RegExMatch(r.body, '"running"\s*:\s*"([^"]*)"', &mr)
                running := mr[1]
            g_updateRunning := running
            if (newer && latest != "") {
                g_updateAvailable := latest
                BuildMenu()
                ToolTip("Syncthing update available: " latest " (current: " running ")")
                SetTimer(() => ToolTip(), -5000)
            } else {
                g_updateAvailable := ""
                BuildMenu()
            }
        }
    } catch {
        ; Update check is best-effort
    }
}

MenuCheckUpdate(*) {
    global ApiKey, g_updateAvailable
    if (ApiKey = "") {
        ToolTip("API Key required — set in Settings")
        SetTimer(() => ToolTip(), -3000)
        return
    }
    ; If update already found, trigger the upgrade
    if (g_updateAvailable != "") {
        DoUpdate()
        return
    }
    ToolTip("Checking for updates...")
    SetTimer(() => ToolTip(), -2000)
    CheckForUpdate()
    if (g_updateAvailable = "") {
        ToolTip("Syncthing is up to date" (g_updateRunning != "" ? " (" g_updateRunning ")" : ""))
        SetTimer(() => ToolTip(), -3000)
    }
}

DoUpdate() {
    global g_updateAvailable
    if (g_updateAvailable = "")
        return
    try {
        r := ApiPost("/rest/system/upgrade")
        if (r.status = 200) {
            ToolTip("Syncthing upgrading to " g_updateAvailable "...")
            SetTimer(() => ToolTip(), -5000)
            g_updateAvailable := ""
            BuildMenu()
        } else {
            ToolTip("Upgrade failed (HTTP " r.status ")")
            SetTimer(() => ToolTip(), -5000)
        }
    } catch {
        ToolTip("Upgrade request failed")
        SetTimer(() => ToolTip(), -5000)
    }
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

; ── Load Synced Folders ────────────────────────────────
LoadFolders() {
    global ApiKey, WebUI, g_folders
    g_folders := []
    if (ApiKey = "")
        return
    try {
        r := ApiGet("/rest/config/folders")
        if (r.status = 200) {
            body := r.body
            ; Parse folder objects: find "id", "label", "path" in each block
            pos := 1
            while (pos := RegExMatch(body, '"id"\s*:\s*"([^"]*)"', &mid, pos)) {
                fId := mid[1]
                fLabel := ""
                fPath := ""
                searchFrom := mid.Pos + mid.Len
                ; Look for label and path nearby (within next 500 chars)
                if RegExMatch(body, '"label"\s*:\s*"([^"]*)"', &ml, searchFrom)
                    fLabel := ml[1]
                if RegExMatch(body, '"path"\s*:\s*"([^"]*)"', &mp, searchFrom)
                    fPath := mp[1]
                ; Use label if set, otherwise id
                displayName := (fLabel != "") ? fLabel : fId
                if (fPath != "")
                    g_folders.Push({id: fId, label: displayName, path: fPath})
                pos := searchFrom
            }
        }
    } catch {
        ; Folder loading is best-effort
    }
    BuildMenu()
}

; ── Build Tray Menu ─────────────────────────────────────
BuildMenu() {
    global DblClickOpen, RunOnStartup, WebUI, Version, g_paused, g_folders

    tray := A_TrayMenu
    tray.Delete()

    ; Title with version
    titleText := "SyncthingTray v" Version
    tray.Add(titleText, (*) => "")
    tray.Disable(titleText)
    tray.Add()

    ; Clickable localhost URL
    tray.Add(WebUI, MenuOpenUI)

    ; Synced folders submenu
    if (g_folders.Length > 0) {
        folderMenu := Menu()
        for f in g_folders {
            p := f.path
            folderMenu.Add(f.label, OpenFolder.Bind(p))
        }
        folderMenu.Add()
        folderMenu.Add("Refresh", (*) => LoadFolders())
        tray.Add("Synced Folders", folderMenu)
    }
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

    ; Update check
    if ProcessExist("syncthing.exe") {
        updateLabel := (g_updateAvailable != "") ? "Update Available: " g_updateAvailable : "Check for Updates"
        tray.Add(updateLabel, MenuCheckUpdate)
    }
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

OpenFolder(path, *) {
    if DirExist(path)
        Run("explorer.exe " path)
    else {
        ToolTip("Folder not found: " path)
        SetTimer(() => ToolTip(), -3000)
    }
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

    cbNetPause := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Auto-pause on public networks")
    cbNetPause.Value := NetworkAutoPause ? 1 : 0
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

    ; ── Discovery section ──
    sg.SetFont("s8 cA0A0C0 bold", "Segoe UI")
    sg.Add("Text", "x16 y" y " w70", "Discovery")
    sg.Add("Text", "x76 y" (y + 4) " w" (sw - 86) " h1 Background404050")
    y += 16
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")

    ; Load current discovery settings from API
    curGlobal := true, curLocal := true, curRelay := true
    if (ApiKey != "") {
        try {
            rd := ApiGet("/rest/config/options")
            if (rd.status = 200) {
                if RegExMatch(rd.body, '"globalAnnounceEnabled"\s*:\s*(true|false)', &gm)
                    curGlobal := (gm[1] = "true")
                if RegExMatch(rd.body, '"localAnnounceEnabled"\s*:\s*(true|false)', &lm)
                    curLocal := (lm[1] = "true")
                if RegExMatch(rd.body, '"relaysEnabled"\s*:\s*(true|false)', &rm)
                    curRelay := (rm[1] = "true")
            }
        }
    }

    cbGlobal := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Global Discovery")
    cbGlobal.Value := curGlobal ? 1 : 0
    y += 24
    cbLocal := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Local Discovery")
    cbLocal.Value := curLocal ? 1 : 0
    y += 24
    cbRelay := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "NAT Traversal (Relaying)")
    cbRelay.Value := curRelay ? 1 : 0
    y += 30

    ; ── Updates section ──
    sg.SetFont("s8 cA0A0C0 bold", "Segoe UI")
    sg.Add("Text", "x16 y" y " w60", "Updates")
    sg.Add("Text", "x66 y" (y + 4) " w" (sw - 76) " h1 Background404050")
    y += 16
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")

    cbUpdates := sg.Add("Checkbox", "x16 y" y " w320 cCDD6F3", "Check for Syncthing updates (daily)")
    cbUpdates.Value := AutoCheckUpdates ? 1 : 0
    y += 30

    ; ── Divider ──
    sg.Add("Text", "x0 y" y " w" sw " h1 Background404050")
    y += 8

    ; Link + utility buttons row
    sg.SetFont("s8 cCDD6F3 norm", "Segoe UI")
    btnGH := sg.Add("Button", "x16 y" y " w68 h24", "GitHub")
    btnGH.OnEvent("Click", (*) => Run("https://github.com/itsnateai/synctray"))
    btnST := sg.Add("Button", "x88 y" y " w68 h24", "Syncthing")
    btnST.OnEvent("Click", (*) => Run("https://github.com/syncthing/syncthing"))
    btnHelp := sg.Add("Button", "x160 y" y " w58 h24", "Help")
    btnHelp.OnEvent("Click", (*) => ShowHelp())
    btnCheck := sg.Add("Button", "x222 y" y " w120 h24", "Check Config")
    btnCheck.OnEvent("Click", (*) => CheckConfig())
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
        global SyncExe, WebUI, StartupDelay, NetworkAutoPause, AutoCheckUpdates
        DblClickOpen  := cbDbl.Value = 1
        RunOnStartup  := g_isPortable ? false : (cbStart.Value = 1)
        StartBrowser  := cbBrowser.Value = 1
        NetworkAutoPause := cbNetPause.Value = 1
        AutoCheckUpdates := cbUpdates.Value = 1
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
        IniWrite(NetworkAutoPause ? "1" : "0", SettingsFile, "Settings", "NetworkAutoPause")
        IniWrite(AutoCheckUpdates ? "1" : "0", SettingsFile, "Settings", "AutoCheckUpdates")
        ; Save discovery settings to Syncthing API
        if (ApiKey != "") {
            try {
                newGlobal := cbGlobal.Value = 1 ? "true" : "false"
                newLocal  := cbLocal.Value = 1 ? "true" : "false"
                newRelay  := cbRelay.Value = 1 ? "true" : "false"
                patchBody := '{"globalAnnounceEnabled":' newGlobal ',"localAnnounceEnabled":' newLocal ',"relaysEnabled":' newRelay '}'
                ApiPatch("/rest/config/options", patchBody)
            }
        }
        if !g_isPortable {
            try {
                ApplyStartup(RunOnStartup)
            } catch as e {
                ToolTip("Could not update startup shortcut: " e.Message)
                SetTimer(() => ToolTip(), -5000)
            }
        }
        LoadFolders()  ; Refresh folder list (also rebuilds menu)
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
    global ApiKey, g_paused
    if (ApiKey = "") {
        ToolTip("API Key required for pause — set in Settings")
        SetTimer(() => ToolTip(), -3000)
        return
    }
    try {
        ApiPost("/rest/system/pause")
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
    global ApiKey, g_paused
    if (ApiKey = "") {
        ToolTip("API Key required for resume — set in Settings")
        SetTimer(() => ToolTip(), -3000)
        return
    }
    try {
        ApiPost("/rest/system/resume")
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
        ToolTip("Could not launch syncthing.exe — check path in Settings")
        SetTimer(() => ToolTip(), -5000)
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

; Graceful shutdown via REST API, falling back to ProcessClose
StopSyncthing() {
    global ApiKey
    if (ApiKey != "") {
        try {
            ApiPost("/rest/system/shutdown")
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

; ── Network Category Detection ────────────────────────────
; Returns 0=Public, 1=Private, 2=DomainAuthenticated, -1=unknown
GetNetworkCategory() {
    try {
        wmi := ComObject("WbemScripting.SWbemLocator").ConnectServer(".", "root\StandardCimv2")
        for profile in wmi.ExecQuery("SELECT NetworkCategory FROM MSFT_NetConnectionProfile")
            return profile.NetworkCategory
    }
    return -1
}

; ── Config Check ─────────────────────────────────────────
CheckConfig() {
    global SyncExe, ApiKey
    results := ""

    ; Check syncthing.exe path
    if FileExist(SyncExe)
        results .= "✓ Syncthing exe: Found`n"
    else
        results .= "✗ Syncthing exe: NOT FOUND at " SyncExe "`n"

    ; Check if process is running
    if ProcessExist("syncthing.exe")
        results .= "✓ Process: Running`n"
    else
        results .= "✗ Process: Not running`n"

    ; Check API connectivity
    if (ApiKey = "") {
        results .= "✗ API Key: Not set`n"
    } else {
        try {
            r := ApiGet("/rest/system/status")
            if (r.status = 200)
                results .= "✓ API: Connected (HTTP 200)`n"
            else
                results .= "✗ API: HTTP " r.status "`n"
        } catch {
            results .= "✗ API: Unreachable`n"
        }

        ; Check discovery settings
        try {
            r2 := ApiGet("/rest/config/options")
            if (r2.status = 200) {
                gd := "off", ld := "off", rl := "off"
                if RegExMatch(r2.body, '"globalAnnounceEnabled"\s*:\s*true')
                    gd := "on"
                if RegExMatch(r2.body, '"localAnnounceEnabled"\s*:\s*true')
                    ld := "on"
                if RegExMatch(r2.body, '"relaysEnabled"\s*:\s*true')
                    rl := "on"
                results .= "  Discovery: Global=" gd " Local=" ld " NAT=" rl "`n"
            }
        }
    }

    ToolTip(results)
    SetTimer(() => ToolTip(), -8000)
}
