#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent

; ── Config ──────────────────────────────────────────────
global Version      := "1.1.0"
global SyncExe      := A_ScriptDir "\syncthing.exe"
global WebUI        := "http://localhost:8384"
global DblClickOpen := true
global RunOnStartup := false
global ApiKey       := ""
global SettingsFile := A_ScriptDir "\SyncthingTray.ini"
; Track whether we intentionally stopped syncthing (vs unexpected crash)
global g_intentionalStop := false

; ── Load Settings ───────────────────────────────────────
if FileExist(SettingsFile) {
    try {
        val := IniRead(SettingsFile, "Settings", "DblClickOpen", "1")
        DblClickOpen := (val = "1")

        val2 := IniRead(SettingsFile, "Settings", "RunOnStartup", "0")
        RunOnStartup := (val2 = "1")

        val3 := IniRead(SettingsFile, "Settings", "ApiKey", "")
        ApiKey := val3
    }
}

; ── Launch Syncthing Hidden ─────────────────────────────
if !ProcessExist("syncthing.exe") {
    try Run(SyncExe " --no-browser", A_ScriptDir, "Hide")
    catch {
        MsgBox("Could not launch syncthing.exe`nExpected at: " SyncExe, "SyncthingTray", "Icon!")
        ExitApp()
    }
}

; ── Tray Icon ───────────────────────────────────────────
A_IconTip := "SyncthingTray v" Version
UpdateTrayIcon()
SetTimer(UpdateTrayIcon, 5000)

UpdateTrayIcon() {
    global g_intentionalStop
    static lastState := -1
    running := ProcessExist("syncthing.exe")
    ; Rebuild menu when state changes so Start/Stop label stays correct
    if (running != lastState) {
        ; P4-01: Notify user if syncthing exited unexpectedly
        if (lastState != -1 && !running && !g_intentionalStop) {
            TrayTip("Syncthing has stopped unexpectedly!", "SyncthingTray", 3)
            SoundBeep(300, 300)
        }
        g_intentionalStop := false
        lastState := running
        BuildMenu()
    }
    if running {
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

; ── Build Tray Menu ─────────────────────────────────────
BuildMenu() {
    global DblClickOpen, RunOnStartup, WebUI, Version

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

    ; Syncthing controls — label changes based on running state
    if ProcessExist("syncthing.exe")
        tray.Add("Stop Syncthing", MenuStop)
    else
        tray.Add("Start Syncthing", MenuStart)
    tray.Add("Restart Syncthing", MenuRestart)
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
    global DblClickOpen, RunOnStartup, ApiKey, Version, WebUI

    ; Only allow one instance of the settings window
    if WinExist("SyncthingTray Settings") {
        WinActivate("SyncthingTray Settings")
        return
    }

    sw := 320
    sh := 250

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

    ; API Key for graceful shutdown
    sg.Add("Text", "x16 y100 w60 cA0A0C0", "API Key:")
    edApiKey := sg.Add("Edit", "x80 y98 w220 h22 cCDD6F3 Background2A2A3E", ApiKey)
    edApiKey.SetFont("s8", "Consolas")

    ; Divider
    sg.Add("Text", "x0 y130 w" sw " h1 Background404050")

    ; GitHub button
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")
    sg.Add("Text", "x16 y142 w60 cA0A0C0", "GitHub:")
    btnGH := sg.Add("Button", "x80 y138 w200 h22", "github.com/itsnateai/SyncthingTray")
    btnGH.SetFont("s8", "Segoe UI")
    btnGH.OnEvent("Click", (*) => Run("https://github.com/itsnateai/SyncthingTray"))

    ; Divider
    sg.Add("Text", "x0 y172 w" sw " h1 Background404050")

    ; Save / Cancel
    btnSave   := sg.Add("Button", "x16 y184 w130 h30 Default", "Save")
    btnCancel := sg.Add("Button", "x158 y184 w130 h30", "Cancel")

    btnSave.OnEvent("Click", SaveSettings)
    btnCancel.OnEvent("Click", (*) => sg.Destroy())
    sg.OnEvent("Close", (*) => sg.Destroy())

    ; Center on screen
    sg.Show("w" sw " h" sh " Center")

    SaveSettings(*) {
        global DblClickOpen, RunOnStartup, ApiKey
        DblClickOpen  := cbDbl.Value = 1
        RunOnStartup  := cbStart.Value = 1
        ApiKey        := edApiKey.Value
        IniWrite(DblClickOpen  ? "1" : "0", SettingsFile, "Settings", "DblClickOpen")
        IniWrite(RunOnStartup  ? "1" : "0", SettingsFile, "Settings", "RunOnStartup")
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

MenuStart(*) {
    try Run(SyncExe " --no-browser", A_ScriptDir, "Hide")
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
    try Run(SyncExe " --no-browser", A_ScriptDir, "Hide")
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
