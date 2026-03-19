using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace SyncthingTray;

/// <summary>
/// Main application context — manages the system tray icon, context menu,
/// polling timer, and all syncthing lifecycle operations.
/// </summary>
internal sealed partial class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SyncthingApi _api;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _iconTimer;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly OsdToolTip _osd;
    private readonly Icon _syncIcon;
    private readonly Icon _pauseIcon;

    // Hidden form to receive WndProc messages (TaskbarCreated, middle-click)
    private readonly MessageWindow _messageWindow;

    // State
    private bool _intentionalStop;
    private string _syncStatus = "unknown";
    private string _syncDetail = string.Empty;
    private readonly Dictionary<string, bool> _knownDevices = new();
    private bool _devicesPollSeeded;
    private int _lastConflictCount;
    private bool _paused;
    private int _connectedCount;
    private int _totalDevices;
    private bool _autoPaused;
    private int _lastNetworkCategory = -1;
    private long _lastUpdateCheck;
    private string _updateAvailable = string.Empty;
    private string _updateRunning = string.Empty;
    private long _lastActionTick;

    // Cached state for change detection
    private bool _lastRunningState;
    private bool _lastPausedState;
    private bool _firstIconPoll = true;

    // Cached per-cycle process check (avoid repeated Process.GetProcessesByName allocations)
    private bool _cachedRunning;
    private long _cachedRunningTick;

    // Pre-computed constant strings (avoid repeated interpolation)
    private static readonly string TitleString = $"SyncthingTray v{AppConfig.Version}";

    // Cached tooltip state — only rebuild string when components change
    private string _lastTipStatus = string.Empty;
    private string _lastTipDetail = string.Empty;
    private int _lastTipConnected = -1;
    private int _lastTipTotal = -1;

    // Folder cache
    private FolderInfo[] _folders = [];

    private bool _disposed;

    // Cached sync detail to avoid string allocation when percentage unchanged
    private double _lastPct = -1;

    // Compiled regex for JSON parsing (zero-allocation on hot path — compiled once)
    private static readonly Regex CompletionRegex = CreateCompletionRegex();
    private static readonly Regex DeviceBlockRegex = CreateDeviceBlockRegex();
    private static readonly Regex ConnectedRegex = CreateConnectedRegex();
    private static readonly Regex PausedRegex = CreatePausedRegex();
    private static readonly Regex PullErrorsRegex = CreatePullErrorsRegex();
    private static readonly Regex NewerRegex = CreateNewerRegex();
    private static readonly Regex LatestRegex = CreateLatestRegex();
    private static readonly Regex RunningVersionRegex = CreateRunningVersionRegex();
    private static readonly Regex FolderIdRegex = CreateFolderIdRegex();
    private static readonly Regex FolderLabelRegex = CreateFolderLabelRegex();
    private static readonly Regex FolderPathRegex = CreateFolderPathRegex();

    [GeneratedRegex("\"completion\"\\s*:\\s*([\\d.]+)", RegexOptions.Compiled)]
    private static partial Regex CreateCompletionRegex();
    [GeneratedRegex("\"([A-Z0-9]{7}-[^\"]+)\"\\s*:\\s*\\{", RegexOptions.Compiled)]
    private static partial Regex CreateDeviceBlockRegex();
    [GeneratedRegex("\"connected\"\\s*:\\s*(true|false)", RegexOptions.Compiled)]
    private static partial Regex CreateConnectedRegex();
    [GeneratedRegex("\"paused\"\\s*:\\s*(true|false)", RegexOptions.Compiled)]
    private static partial Regex CreatePausedRegex();
    [GeneratedRegex("\"pullErrors\"\\s*:\\s*(\\d+)", RegexOptions.Compiled)]
    private static partial Regex CreatePullErrorsRegex();
    [GeneratedRegex("\"newer\"\\s*:\\s*(true|false)", RegexOptions.Compiled)]
    private static partial Regex CreateNewerRegex();
    [GeneratedRegex("\"latest\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex CreateLatestRegex();
    [GeneratedRegex("\"running\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex CreateRunningVersionRegex();
    [GeneratedRegex("\"id\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex CreateFolderIdRegex();
    [GeneratedRegex("\"label\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex CreateFolderLabelRegex();
    [GeneratedRegex("\"path\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled)]
    private static partial Regex CreateFolderPathRegex();

    public TrayApplicationContext()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath) ?? AppContext.BaseDirectory;
        _config = new AppConfig(appDir);
        _api = new SyncthingApi(_config);

        // Load icons from embedded resources
        _syncIcon = LoadEmbeddedIcon("sync.ico");
        _pauseIcon = LoadEmbeddedIcon("pause.ico");

        _osd = new OsdToolTip();

        _trayIcon = new NotifyIcon
        {
            Icon = _syncIcon,
            Text = TitleString,
            Visible = true,
        };
        _trayIcon.DoubleClick += OnTrayDoubleClick;
        _trayIcon.MouseUp += OnTrayMouseUp;

        _messageWindow = new MessageWindow(this);

        // Startup delay
        if (_config.StartupDelay > 0)
            Thread.Sleep(_config.StartupDelay * 1000);

        // Launch syncthing if not running
        if (!IsSyncthingRunning())
            LaunchSyncthing();

        // Build initial menu
        BuildMenu();

        // Timers
        _iconTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _iconTimer.Tick += (_, _) => UpdateTrayIcon();
        _iconTimer.Start();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 10000 };
        _pollTimer.Tick += (_, _) => PollSyncStatus();
        _pollTimer.Start();

        // Initial poll
        PollSyncStatus();

        // Load folders
        LoadFolders();

        // First-run: open settings
        if (_config.IsFirstRun)
        {
            var delayTimer = new System.Windows.Forms.Timer { Interval = 500 };
            delayTimer.Tick += (_, _) =>
            {
                delayTimer.Stop();
                delayTimer.Dispose();
                OpenSettings();
            };
            delayTimer.Start();
        }
    }

    // --- Tray Icon Events ---

    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        if (_config.DblClickOpen)
            OpenWebUI();
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle && _config.MiddleClickEnabled)
            TogglePause();
    }

    // --- Icon & Tooltip ---

    private void UpdateTrayIcon()
    {
        bool running = IsSyncthingRunning();

        if (_firstIconPoll || running != _lastRunningState || _paused != _lastPausedState)
        {
            // Unexpected stop detection
            if (!_firstIconPoll && !running && !_intentionalStop)
            {
                ShowOsd("Syncthing has stopped unexpectedly!", 5000);
                NativeMethods.Beep(300, 300);
            }

            _intentionalStop = false;
            _lastRunningState = running;
            _lastPausedState = _paused;
            _firstIconPoll = false;

            _trayIcon.Icon = (running && !_paused) ? _syncIcon : _pauseIcon;
            BuildMenu();
        }
    }

    private void UpdateTooltip()
    {
        // Early exit: skip all string work if none of the tooltip components changed
        if (_syncStatus == _lastTipStatus
            && _syncDetail == _lastTipDetail
            && _connectedCount == _lastTipConnected
            && _totalDevices == _lastTipTotal)
        {
            return;
        }

        _lastTipStatus = _syncStatus;
        _lastTipDetail = _syncDetail;
        _lastTipConnected = _connectedCount;
        _lastTipTotal = _totalDevices;

        var statusSuffix = _syncStatus switch
        {
            "paused" => " \u2014 Paused",
            "idle" => " \u2014 Idle",
            "syncing" => " \u2014 Syncing",
            "error" => " \u2014 Error",
            "stopped" => " \u2014 Stopped",
            _ => string.Empty,
        };

        var tip = _syncDetail.Length > 0
            ? (_totalDevices > 0
                ? string.Concat(TitleString, statusSuffix, " (", _syncDetail, ") | ", _connectedCount.ToString(), "/", _totalDevices.ToString(), " devices")
                : string.Concat(TitleString, statusSuffix, " (", _syncDetail, ")"))
            : (_totalDevices > 0
                ? string.Concat(TitleString, statusSuffix, " | ", _connectedCount.ToString(), "/", _totalDevices.ToString(), " devices")
                : string.Concat(TitleString, statusSuffix));

        // NotifyIcon.Text max is 127 chars
        _trayIcon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // --- Menu Building ---

    private void BuildMenu()
    {
        var oldMenu = _trayIcon.ContextMenuStrip;
        var menu = new ContextMenuStrip();
        bool running = IsSyncthingRunning();

        // Title
        var titleItem = menu.Items.Add(TitleString);
        titleItem.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());

        // WebUI link
        menu.Items.Add(_config.WebUI, null, (_, _) => OpenWebUI());

        // Synced Folders submenu
        if (_folders.Length > 0)
        {
            var folderItem = new ToolStripMenuItem("Synced Folders");
            foreach (var f in _folders)
            {
                var path = f.Path;
                folderItem.DropDownItems.Add(f.Label, null, (_, _) => OpenFolder(path));
            }
            folderItem.DropDownItems.Add(new ToolStripSeparator());
            folderItem.DropDownItems.Add("Refresh", null, (_, _) => LoadFolders());
            menu.Items.Add(folderItem);
        }
        menu.Items.Add(new ToolStripSeparator());

        // Settings
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());

        // Pause/Resume
        if (running)
        {
            if (_paused)
                menu.Items.Add("Resume Syncing", null, (_, _) => MenuResume());
            else
                menu.Items.Add("Pause Syncing", null, (_, _) => MenuPause());
            menu.Items.Add(new ToolStripSeparator());
        }

        // Process controls
        menu.Items.Add("Restart Syncthing", null, (_, _) => MenuRestart());
        if (running)
            menu.Items.Add("Stop Syncthing", null, (_, _) => MenuStop());
        else
            menu.Items.Add("Start Syncthing", null, (_, _) => MenuStart());

        // Update check
        if (running)
        {
            var label = _updateAvailable.Length > 0
                ? $"Update Available: {_updateAvailable}"
                : "Check for Updates";
            menu.Items.Add(label, null, (_, _) => MenuCheckUpdate());
        }
        menu.Items.Add(new ToolStripSeparator());

        // Exit
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;

        // Dispose old menu — Dispose() cascades to all owned items and submenus
        oldMenu?.Dispose();
    }

    // --- Polling ---

    private void PollSyncStatus()
    {
        bool running = IsSyncthingRunning();
        if (!running || string.IsNullOrEmpty(_config.ApiKey))
        {
            _syncStatus = running ? "unknown" : "stopped";
            _syncDetail = string.Empty;
            UpdateTooltip();
            return;
        }

        // 1. Sync completion
        try
        {
            var (status, body) = _api.Get("/rest/db/completion");
            if (status == 200)
            {
                var m = CompletionRegex.Match(body);
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pct))
                {
                    pct = Math.Round(pct, 1);
                    if (pct >= 100)
                    {
                        _syncStatus = _paused ? "paused" : "idle";
                        _syncDetail = _paused ? "Paused" : "Up to date";
                    }
                    else
                    {
                        _syncStatus = "syncing";
                        // Only allocate a new string when pct actually changed
                        if (pct != _lastPct)
                        {
                            _lastPct = pct;
                            _syncDetail = string.Concat(pct.ToString(System.Globalization.CultureInfo.InvariantCulture), "% complete");
                        }
                    }
                }
                else
                {
                    _syncStatus = _paused ? "paused" : "idle";
                    _syncDetail = _paused ? "Paused" : "Connected";
                }
            }
            else
            {
                _syncStatus = "error";
                _syncDetail = $"API HTTP {status}";
            }
        }
        catch
        {
            _syncStatus = "error";
            _syncDetail = "API unreachable";
        }

        // 2. Device tracking
        try
        {
            var (status2, body2) = _api.Get("/rest/system/connections");
            if (status2 == 200)
            {
                bool allPaused = true;
                int deviceCount = 0;
                int connCount = 0;

                var deviceMatches = DeviceBlockRegex.Matches(body2);
                foreach (Match dm in deviceMatches)
                {
                    string deviceId = dm.Groups[1].Value;
                    int blockStart = dm.Index + dm.Length;

                    bool connected = false;
                    bool paused = false;

                    var cm = ConnectedRegex.Match(body2, blockStart);
                    if (cm.Success)
                        connected = cm.Groups[1].Value == "true";

                    var pm = PausedRegex.Match(body2, blockStart);
                    if (pm.Success)
                        paused = pm.Groups[1].Value == "true";

                    if (!paused)
                        allPaused = false;

                    deviceCount++;
                    if (connected)
                        connCount++;

                    if (_devicesPollSeeded && _knownDevices.TryGetValue(deviceId, out bool wasConnected))
                    {
                        if (connected && !wasConnected)
                            ShowOsd("Device connected", 3000);
                        else if (!connected && wasConnected)
                            ShowOsd("Device disconnected", 3000);
                    }
                    _knownDevices[deviceId] = connected;
                }
                _devicesPollSeeded = true;
                _connectedCount = connCount;
                _totalDevices = deviceCount;

                if (deviceCount > 0)
                    _paused = allPaused;
            }
        }
        catch { /* best-effort */ }

        // 3. Conflict detection
        try
        {
            var (status3, body3) = _api.Get("/rest/db/status?folder=default");
            if (status3 == 200)
            {
                var em = PullErrorsRegex.Match(body3);
                if (em.Success && int.TryParse(em.Groups[1].Value, out int errCount))
                {
                    if (errCount > _lastConflictCount && _lastConflictCount >= 0)
                    {
                        int newErrs = errCount - _lastConflictCount;
                        ShowOsd($"{newErrs} file error(s) detected \u2014 check Web UI", 5000);
                    }
                    _lastConflictCount = errCount;
                }
            }
        }
        catch { /* best-effort */ }

        // 4. Network auto-pause
        if (_config.NetworkAutoPause)
        {
            try
            {
                int cat = GetNetworkCategory();
                if (cat != _lastNetworkCategory && _lastNetworkCategory != -1)
                {
                    if (cat == 0 && !_paused)
                    {
                        _api.Post("/rest/system/pause");
                        _paused = true;
                        _autoPaused = true;
                        UpdateTrayIcon();
                        BuildMenu();
                        ShowOsd("Auto-paused: public network detected", 3000);
                    }
                    else if (cat != 0 && _autoPaused)
                    {
                        _api.Post("/rest/system/resume");
                        _paused = false;
                        _autoPaused = false;
                        UpdateTrayIcon();
                        BuildMenu();
                        ShowOsd("Auto-resumed: private network detected", 3000);
                    }
                }
                _lastNetworkCategory = cat;
            }
            catch { /* best-effort */ }
        }

        // 5. Auto-update check (24h rate limit)
        if (_config.AutoCheckUpdates && !string.IsNullOrEmpty(_config.ApiKey) && running)
        {
            long elapsed = Environment.TickCount64 - _lastUpdateCheck;
            if (_lastUpdateCheck == 0 || elapsed > 86400000)
                CheckForUpdate();
        }

        UpdateTooltip();
    }

    // --- Update Check ---

    private void CheckForUpdate()
    {
        _lastUpdateCheck = Environment.TickCount64;
        try
        {
            var (status, body) = _api.Get("/rest/system/upgrade");
            if (status == 200)
            {
                bool newer = false;
                string latest = string.Empty;
                string running = string.Empty;

                var mn = NewerRegex.Match(body);
                if (mn.Success) newer = mn.Groups[1].Value == "true";
                var ml = LatestRegex.Match(body);
                if (ml.Success) latest = ml.Groups[1].Value;
                var mr = RunningVersionRegex.Match(body);
                if (mr.Success) running = mr.Groups[1].Value;

                _updateRunning = running;

                if (newer && latest.Length > 0)
                {
                    _updateAvailable = latest;
                    BuildMenu();
                    ShowOsd($"Syncthing update available: {latest} (current: {running})", 5000);
                }
                else
                {
                    _updateAvailable = string.Empty;
                    BuildMenu();
                }
            }
        }
        catch { /* best-effort */ }
    }

    private void MenuCheckUpdate()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required \u2014 set in Settings", 3000);
            return;
        }

        if (_updateAvailable.Length > 0)
        {
            DoUpdate();
            return;
        }

        ShowOsd("Checking for updates...", 2000);
        CheckForUpdate();
        if (_updateAvailable.Length == 0)
        {
            var msg = "Syncthing is up to date";
            if (_updateRunning.Length > 0)
                msg += $" ({_updateRunning})";
            ShowOsd(msg, 3000);
        }
    }

    private void DoUpdate()
    {
        if (_updateAvailable.Length == 0) return;
        try
        {
            var (status, _) = _api.Post("/rest/system/upgrade");
            if (status == 200)
            {
                ShowOsd($"Syncthing upgrading to {_updateAvailable}...", 5000);
                _updateAvailable = string.Empty;
                BuildMenu();
            }
            else
            {
                ShowOsd($"Upgrade failed (HTTP {status})", 5000);
            }
        }
        catch
        {
            ShowOsd("Upgrade request failed", 5000);
        }
    }

    // --- Folder Loading ---

    private void LoadFolders()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _folders = [];
            BuildMenu();
            return;
        }

        try
        {
            var (status, body) = _api.Get("/rest/config/folders");
            if (status == 200)
            {
                var list = new List<FolderInfo>();
                var idMatches = FolderIdRegex.Matches(body);
                foreach (Match mid in idMatches)
                {
                    string fId = mid.Groups[1].Value;
                    int searchFrom = mid.Index + mid.Length;

                    string fLabel = string.Empty;
                    string fPath = string.Empty;

                    var ml = FolderLabelRegex.Match(body, searchFrom);
                    if (ml.Success) fLabel = ml.Groups[1].Value;
                    var mp = FolderPathRegex.Match(body, searchFrom);
                    if (mp.Success) fPath = mp.Groups[1].Value;

                    string displayName = fLabel.Length > 0 ? fLabel : fId;
                    if (fPath.Length > 0)
                        list.Add(new FolderInfo(fId, displayName, fPath));
                }
                _folders = list.ToArray();
            }
        }
        catch { /* best-effort */ }

        BuildMenu();
    }

    // --- Menu Actions ---

    private void OpenWebUI()
    {
        using var p = Process.Start(new ProcessStartInfo(_config.WebUI) { UseShellExecute = true });
    }

    private void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            using var p = Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = false });
        }
        else
        {
            ShowOsd($"Folder not found: {path}", 3000);
        }
    }

    private void OpenSettings()
    {
        // Single instance of settings
        foreach (Form f in Application.OpenForms)
        {
            if (f is SettingsForm sf)
            {
                sf.Activate();
                return;
            }
        }

        using var form = new SettingsForm(_config, _api, () =>
        {
            LoadFolders();
            ShowOsd("Settings saved", 3000);
        });
        form.ShowDialog();
    }

    private bool IsOverclickGuarded(int cooldownMs = 1500)
    {
        long now = Environment.TickCount64;
        if (now - _lastActionTick < cooldownMs)
        {
            ShowOsd("Please wait...", 1500);
            return true;
        }
        _lastActionTick = now;
        return false;
    }

    private void TogglePause()
    {
        if (IsOverclickGuarded(800)) return;
        if (_paused)
            MenuResume();
        else
            MenuPause();
    }

    private void MenuPause()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required for pause \u2014 set in Settings", 3000);
            return;
        }
        try
        {
            _api.Post("/rest/system/pause");
            _paused = true;
            UpdateTrayIcon();
            BuildMenu();
            ShowOsd("Syncing paused", 3000);
        }
        catch
        {
            ShowOsd("Failed to pause syncing", 3000);
        }
    }

    private void MenuResume()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required for resume \u2014 set in Settings", 3000);
            return;
        }
        try
        {
            _api.Post("/rest/system/resume");
            _paused = false;
            UpdateTrayIcon();
            BuildMenu();
            ShowOsd("Syncing resumed", 3000);
        }
        catch
        {
            ShowOsd("Failed to resume syncing", 3000);
        }
    }

    private void MenuStart()
    {
        if (IsOverclickGuarded()) return;
        if (!LaunchSyncthing())
        {
            ShowOsd("Could not launch syncthing.exe \u2014 check path in Settings", 5000);
            return;
        }
        UpdateTrayIcon();
        BuildMenu();
        ShowOsd("Syncthing started", 3000);
    }

    private void MenuStop()
    {
        if (IsOverclickGuarded()) return;
        if (IsSyncthingRunning())
        {
            _intentionalStop = true;
            StopSyncthing();
            UpdateTrayIcon();
            BuildMenu();
            ShowOsd("Syncthing stopped", 3000);
        }
        else
        {
            ShowOsd("Syncthing is not running", 3000);
        }
    }

    private void MenuRestart()
    {
        if (IsOverclickGuarded()) return;
        if (IsSyncthingRunning())
        {
            _intentionalStop = true;
            StopSyncthing();
        }
        LaunchSyncthing();
        UpdateTrayIcon();
        BuildMenu();
        ShowOsd("Syncthing restarted", 3000);
    }

    // --- Syncthing Process Management ---

    private bool LaunchSyncthing()
    {
        try
        {
            var args = _config.StartBrowser ? string.Empty : "--no-browser";
            var dir = Path.GetDirectoryName(_config.SyncExe) ?? string.Empty;
            var psi = new ProcessStartInfo
            {
                FileName = _config.SyncExe,
                Arguments = args,
                WorkingDirectory = dir,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            InvalidateRunningCache();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void StopSyncthing()
    {
        // Graceful: REST API shutdown
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            try
            {
                _api.Post("/rest/system/shutdown");
                for (int i = 0; i < 50; i++)
                {
                    InvalidateRunningCache();
                    if (!IsSyncthingRunning()) return;
                    Thread.Sleep(100);
                }
            }
            catch { /* fall through to force kill */ }
        }

        // Force kill fallback
        foreach (var p in Process.GetProcessesByName("syncthing"))
        {
            using (p)
            {
                try { p.Kill(); } catch { /* already gone */ }
            }
        }

        for (int i = 0; i < 30; i++)
        {
            InvalidateRunningCache();
            if (!IsSyncthingRunning()) break;
            Thread.Sleep(100);
        }
        InvalidateRunningCache();
    }

    // --- Network Category Detection (WMI) ---

    private static int GetNetworkCategory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\StandardCimv2",
                "SELECT NetworkCategory FROM MSFT_NetConnectionProfile");
            using var results = searcher.Get();
            foreach (ManagementObject profile in results)
            {
                using (profile)
                {
                    var cat = profile["NetworkCategory"];
                    if (cat is not null)
                        return Convert.ToInt32(cat);
                }
            }
        }
        catch { /* WMI unavailable */ }
        return -1;
    }

    // --- Helpers ---

    /// <summary>
    /// Checks if syncthing.exe is running. Result is cached for 2 seconds to avoid
    /// repeated Process.GetProcessesByName allocations within the same poll cycle
    /// (UpdateTrayIcon, PollSyncStatus, and BuildMenu can all call this in sequence).
    /// </summary>
    private bool IsSyncthingRunning()
    {
        long now = Environment.TickCount64;
        if (now - _cachedRunningTick < 2000)
            return _cachedRunning;

        var procs = Process.GetProcessesByName("syncthing");
        bool running = false;
        foreach (var p in procs)
        {
            using (p)
            {
                running = true;
            }
        }
        _cachedRunning = running;
        _cachedRunningTick = now;
        return running;
    }

    /// <summary>
    /// Invalidates the cached process check so the next call does a fresh lookup.
    /// Call after starting or stopping syncthing so state is immediately correct.
    /// </summary>
    private void InvalidateRunningCache()
    {
        _cachedRunningTick = 0;
    }

    private void ShowOsd(string text, int durationMs)
    {
        if (_osd.InvokeRequired)
            _osd.Invoke(() => _osd.ShowMessage(text, durationMs));
        else
            _osd.ShowMessage(text, durationMs);
    }

    private static Icon LoadEmbeddedIcon(string name)
    {
        var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(name);
        if (stream is not null)
        {
            using (stream)
                return new Icon(stream);
        }
        // Clone the shared system icon so we can safely dispose it later
        return (Icon)SystemIcons.Application.Clone();
    }

    private void ExitApplication()
    {
        _intentionalStop = true;
        StopSyncthing();
        Dispose();
        Application.Exit();
    }

    // --- Cleanup ---

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _iconTimer.Stop();
                _iconTimer.Dispose();
                _pollTimer.Stop();
                _pollTimer.Dispose();

                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();

                _osd.Dispose();
                _messageWindow.Dispose();

                _syncIcon.Dispose();
                _pauseIcon.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    // --- MessageWindow: hidden form for WndProc (TaskbarCreated recovery) ---

    private sealed class MessageWindow : Form
    {
        private readonly TrayApplicationContext _owner;
        private readonly uint _wmTaskbarCreated;

        public MessageWindow(TrayApplicationContext owner)
        {
            _owner = owner;
            _wmTaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Size = Size.Empty;
            // Create the handle without showing
            _ = Handle;
        }

        protected override void WndProc(ref Message m)
        {
            if (_wmTaskbarCreated != 0 && (uint)m.Msg == _wmTaskbarCreated)
            {
                // Explorer restarted — re-show tray icon
                _owner._trayIcon.Visible = true;
                _owner.UpdateTrayIcon();
            }
            base.WndProc(ref m);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }

    // --- Data types ---

    private sealed record FolderInfo(string Id, string Label, string Path);
}
