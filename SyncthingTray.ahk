#Requires AutoHotkey v2.0
#SingleInstance Force
Persistent

; ── Config ──────────────────────────────────────────────
global Version      := "1.0.0"
global SyncExe      := A_ScriptDir "\syncthing.exe"
global WebUI        := "http://localhost:8384"
global DblClickOpen := true
global RunOnStartup := false
global SettingsFile := A_ScriptDir "\SyncthingTray.ini"

; ── Load Settings ───────────────────────────────────────
if FileExist(SettingsFile) {
    try {
        val := IniRead(SettingsFile, "Settings", "DblClickOpen", "1")
        DblClickOpen := (val = "1")

        val2 := IniRead(SettingsFile, "Settings", "RunOnStartup", "0")
        RunOnStartup := (val2 = "1")
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

UpdateTrayIcon() {
    running := ProcessExist("syncthing.exe")
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
BuildMenu()

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
    global DblClickOpen, RunOnStartup, Version, WebUI

    ; Only allow one instance of the settings window
    if WinExist("SyncthingTray Settings") {
        WinActivate("SyncthingTray Settings")
        return
    }

    sw := 320
    sh := 210

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

    ; Divider
    sg.Add("Text", "x0 y104 w" sw " h1 Background404050")

    ; GitHub button
    sg.SetFont("s9 cCDD6F3 norm", "Segoe UI")
    sg.Add("Text", "x16 y116 w60 cA0A0C0", "GitHub:")
    btnGH := sg.Add("Button", "x80 y112 w200 h22", "github.com/itsnateai/SyncthingTray")
    btnGH.SetFont("s8", "Segoe UI")
    btnGH.OnEvent("Click", (*) => Run("https://github.com/itsnateai/SyncthingTray"))

    ; Divider
    sg.Add("Text", "x0 y146 w" sw " h1 Background404050")

    ; Save / Cancel
    btnSave   := sg.Add("Button", "x16 y158 w130 h30 Default", "Save")
    btnCancel := sg.Add("Button", "x158 y158 w130 h30", "Cancel")

    btnSave.OnEvent("Click", SaveSettings)
    btnCancel.OnEvent("Click", (*) => sg.Destroy())
    sg.OnEvent("Close", (*) => sg.Destroy())

    ; Center on screen
    sg.Show("w" sw " h" sh " Center")

    SaveSettings(*) {
        global DblClickOpen, RunOnStartup
        DblClickOpen  := cbDbl.Value = 1
        RunOnStartup  := cbStart.Value = 1
        IniWrite(DblClickOpen  ? "1" : "0", SettingsFile, "Settings", "DblClickOpen")
        IniWrite(RunOnStartup  ? "1" : "0", SettingsFile, "Settings", "RunOnStartup")
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

MenuToggleDblClick(*) {
    global DblClickOpen
    DblClickOpen := !DblClickOpen
    IniWrite(DblClickOpen ? "1" : "0", SettingsFile, "Settings", "DblClickOpen")
    BuildMenu()
}

MenuToggleStartup(*) {
    global RunOnStartup
    RunOnStartup := !RunOnStartup
    IniWrite(RunOnStartup ? "1" : "0", SettingsFile, "Settings", "RunOnStartup")
    try {
        ApplyStartup(RunOnStartup)
    } catch as e {
        MsgBox("Could not update startup shortcut:`n" e.Message, "SyncthingTray", "Icon!")
        RunOnStartup := !RunOnStartup
        IniWrite(RunOnStartup ? "1" : "0", SettingsFile, "Settings", "RunOnStartup")
    }
    BuildMenu()
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
    if ProcessExist("syncthing.exe") {
        ProcessClose("syncthing.exe")
        loop 30 {
            if !ProcessExist("syncthing.exe")
                break
            Sleep(100)
        }
        UpdateTrayIcon()
        BuildMenu()
        TrayTip("Syncthing stopped", "SyncthingTray")
    } else {
        TrayTip("Syncthing is not running", "SyncthingTray")
    }
}

MenuRestart(*) {
    if ProcessExist("syncthing.exe") {
        ProcessClose("syncthing.exe")
        loop 30 {
            if !ProcessExist("syncthing.exe")
                break
            Sleep(100)
        }
    }
    try Run(SyncExe " --no-browser", A_ScriptDir, "Hide")
    UpdateTrayIcon()
    BuildMenu()
    TrayTip("Syncthing restarted", "SyncthingTray")
}

MenuExit(*) {
    if ProcessExist("syncthing.exe")
        ProcessClose("syncthing.exe")
    ExitApp()
}
