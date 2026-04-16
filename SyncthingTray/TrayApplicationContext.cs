using System.Diagnostics;
using System.Management;
using System.Text.Json;

namespace SyncthingTray;

/// <summary>
/// Main application context — manages the system tray icon, context menu,
/// polling timer, and all syncthing lifecycle operations.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
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
    private bool _deviceApiFailureNotified;
    private readonly Dictionary<string, int> _folderPullErrors = new();
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
    private long _lastUnexpectedStopAlertTick;

    // Menu rebuild tracking — skip if state unchanged
    private bool _lastMenuRunning;
    private bool _lastMenuPaused;
    private int _lastMenuFolderCount;
    private string _lastMenuUpdate = string.Empty;
    private string _lastMenuStatus = string.Empty;
    private string _lastMenuDetail = string.Empty;
    private int _lastMenuConnected = -1;
    private int _lastMenuTotal = -1;
    private bool _menuBuilt;

    // Cached per-cycle process check (avoid repeated Process.GetProcessesByName allocations)
    private bool _cachedRunning;
    private long _cachedRunningTick;

    // Cached WMI network category (60s TTL — WMI is slow, network category rarely changes)
    private int _cachedNetworkCategory = -1;
    private long _cachedNetworkCategoryTick;
    private bool _wmiFailureNotified;

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
    private bool _stopping;

    // Cached sync detail to avoid string allocation when percentage unchanged
    private double _lastPct = -1;

    // PID of the syncthing process we launched (for targeted kill)
    private int _launchedPid;

    public TrayApplicationContext()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath) ?? AppContext.BaseDirectory;
        _config = new AppConfig(appDir);
        TrayLog.Enable(_config.DiagnosticLogging);
        TrayLog.Info($"SyncthingTray v{AppConfig.Version} starting. Portable={_config.IsPortable}, FirstRun={_config.IsFirstRun}.");
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

        // Build initial menu (always, even during startup delay)
        BuildMenu();

        // Timers
        _iconTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _iconTimer.Tick += (_, _) => UpdateTrayIcon();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 10000 };
        _pollTimer.Tick += OnPollTick;

        // Startup delay — use a timer so the message pump stays alive
        if (_config.StartupDelay > 0)
        {
            var startupTimer = new System.Windows.Forms.Timer { Interval = _config.StartupDelay * 1000 };
            startupTimer.Tick += (_, _) =>
            {
                startupTimer.Stop();
                startupTimer.Dispose();
                StartAfterDelay();
            };
            startupTimer.Start();
        }
        else
        {
            StartAfterDelay();
        }

        // First-run: open settings. Also seed a stub INI so cancelling the dialog
        // doesn't re-trigger first-run on the next launch forever.
        if (_config.IsFirstRun)
        {
            _config.SeedFirstRunStub();
            var firstRunTimer = new System.Windows.Forms.Timer { Interval = 500 };
            firstRunTimer.Tick += (_, _) =>
            {
                firstRunTimer.Stop();
                firstRunTimer.Dispose();
                if (_disposed) return;
                OpenSettings();
            };
            firstRunTimer.Start();
        }

        // Notify if we replaced a previous instance
        if (Program.KilledPreviousInstance)
            ShowOsd("Replaced previous SyncthingTray instance", 3000);

        // Surface config-load failures that happened before the OSD existed.
        switch (_config.LoadResult)
        {
            case AppConfigLoadResult.Locked:
                ShowOsd("Settings file could not be read — using last session defaults", 6000);
                TrayLog.Warn($"AppConfig.Load: file locked or unreadable: {_config.SettingsFilePath}");
                break;
            case AppConfigLoadResult.Corrupt:
                ShowOsd("SyncthingTray.ini appears corrupt — defaults loaded; original will be backed up on Save", 8000);
                TrayLog.Warn($"AppConfig.Load: corrupt file detected: {_config.SettingsFilePath}");
                break;
        }

        // Crash sentinel persisted from the previous run — likely a bad update.
        if (Program.CrashSentinelPersisted)
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            ShowOsd(
                $"Previous version may have crashed. Backup available at {Path.GetFileName(exePath)}.old if you need to roll back.",
                12000);
            TrayLog.Warn("Crash sentinel persisted across launches — previous run did not reach stable uptime.");
        }

        // Stability proof: if we reach 30s without crashing, clear the crash sentinel
        // so the next launch doesn't spuriously warn the user.
        var stabilityTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        stabilityTimer.Tick += (_, _) =>
        {
            stabilityTimer.Stop();
            stabilityTimer.Dispose();
            UpdateDialog.TryDeleteCrashSentinel();
            TrayLog.Info("30s stability reached; crash sentinel cleared.");
        };
        stabilityTimer.Start();
    }

    private void StartAfterDelay()
    {
        if (_disposed) return;

        if (!IsSyncthingRunning())
            LaunchSyncthing();

        _iconTimer.Start();
        _pollTimer.Start();

        PollSyncStatus();
        LoadFolders();
    }

    // --- Tray Icon Events ---

    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        ExecuteClickAction(_config.DblClickAction);
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
            ExecuteClickAction(_config.MiddleClickAction);
    }

    private void ExecuteClickAction(string action)
    {
        switch (action)
        {
            case "webui":
                OpenWebUI();
                break;
            case "rescan":
                MenuRescanAll();
                break;
            case "pause":
                TogglePause();
                break;
            // "none" or unknown — do nothing
        }
    }

    // --- Icon & Tooltip ---

    private void UpdateTrayIcon()
    {
        if (_messageWindow.InvokeRequired)
        {
            RunOnUi(UpdateTrayIcon);
            return;
        }

        bool running = IsSyncthingRunning();

        if (_firstIconPoll || running != _lastRunningState || _paused != _lastPausedState)
        {
            // Unexpected stop detection. Rate-limit so a crash-restart flap
            // during a Syncthing upgrade or reboot doesn't spam OSDs+beeps.
            if (!_firstIconPoll && !running && !_intentionalStop)
            {
                long now = Environment.TickCount64;
                const long AlertCooldownMs = 5 * 60_000; // 5 min between alerts
                if (_lastUnexpectedStopAlertTick == 0 || now - _lastUnexpectedStopAlertTick > AlertCooldownMs)
                {
                    _lastUnexpectedStopAlertTick = now;
                    ShowOsd("Syncthing has stopped unexpectedly!", 5000);
                    if (_config.SoundNotifications)
                        PlaySound(System.Media.SystemSounds.Hand);
                    else
                    {
                        _ = Task.Run(() =>
                        {
                            try { NativeMethods.Beep(300, 300); } catch { /* no speaker */ }
                        });
                    }
                    TrayLog.Warn("Syncthing stopped unexpectedly.");
                }
            }
            else if (running && _lastUnexpectedStopAlertTick != 0)
            {
                _lastUnexpectedStopAlertTick = 0; // reset on recovery
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
        if (_messageWindow.InvokeRequired)
        {
            RunOnUi(UpdateTooltip);
            return;
        }

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
            "idle" => " \u2014 Online",
            "syncing" => " \u2014 Syncing",
            "error" => " \u2014 Error",
            "stopped" => " \u2014 Stopped",
            _ => string.Empty,
        };

        var tip = _syncDetail.Length > 0
            ? string.Concat(TitleString, statusSuffix, " (", _syncDetail, ")")
            : string.Concat(TitleString, statusSuffix);

        // NotifyIcon.Text max is 127 chars
        _trayIcon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    // --- Menu Building ---

    private static readonly DarkMenuRenderer DarkRenderer = new();

    private void BuildMenu()
    {
        if (_messageWindow.InvokeRequired)
        {
            RunOnUi(BuildMenu);
            return;
        }

        bool running = IsSyncthingRunning();

        // Skip rebuild if nothing that affects menu items has changed
        if (_menuBuilt
            && running == _lastMenuRunning
            && _paused == _lastMenuPaused
            && _folders.Length == _lastMenuFolderCount
            && _updateAvailable == _lastMenuUpdate
            && _syncStatus == _lastMenuStatus
            && _syncDetail == _lastMenuDetail
            && _connectedCount == _lastMenuConnected
            && _totalDevices == _lastMenuTotal)
        {
            return;
        }
        _menuBuilt = true;
        _lastMenuRunning = running;
        _lastMenuPaused = _paused;
        _lastMenuFolderCount = _folders.Length;
        _lastMenuUpdate = _updateAvailable;
        _lastMenuStatus = _syncStatus;
        _lastMenuDetail = _syncDetail;
        _lastMenuConnected = _connectedCount;
        _lastMenuTotal = _totalDevices;

        var oldMenu = _trayIcon.ContextMenuStrip;
        var menu = new ContextMenuStrip { Renderer = DarkRenderer };

        // Title
        var titleItem = menu.Items.Add(TitleString);
        titleItem.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());

        // Status + WebUI link
        var statusText = running
            ? _syncStatus switch
            {
                "paused" => "Paused",
                "idle" => _syncDetail.Length > 0 ? $"Online — {_syncDetail}" : "Online",
                "syncing" => _syncDetail.Length > 0 ? $"Syncing — {_syncDetail}" : "Syncing...",
                "error" => _syncDetail.Length > 0 ? $"Error — {_syncDetail}" : "Error",
                _ => "Running",
            }
            : "Stopped";
        var statusItem = menu.Items.Add($"Syncthing: {statusText}");
        statusItem.Enabled = false;
        menu.Items.Add(_config.WebUI, null, (_, _) => OpenWebUI());

        // Synced Folders submenu
        if (_folders.Length > 0)
        {
            var folderItem = new ToolStripMenuItem("Synced Folders");
            foreach (var f in _folders)
            {
                var path = f.Path;
                var folderId = f.Id;
                var subItem = new ToolStripMenuItem(f.Label);
                subItem.DropDownItems.Add("Open Folder", null, (_, _) => OpenFolder(path));
                subItem.DropDownItems.Add("Rescan", null, (_, _) => MenuRescanFolder(folderId));
                folderItem.DropDownItems.Add(subItem);
            }
            folderItem.DropDownItems.Add(new ToolStripSeparator());
            folderItem.DropDownItems.Add("Refresh List", null, (_, _) => LoadFolders());
            menu.Items.Add(folderItem);
        }

        // Rescan Now
        if (running && !string.IsNullOrEmpty(_config.ApiKey))
            menu.Items.Add("Force Rescan Now", null, (_, _) => MenuRescanAll());

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

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon.ContextMenuStrip = menu;

        // Dispose old menu — Dispose() cascades to all owned items and submenus
        oldMenu?.Dispose();
    }

    // --- Polling ---

    private async void OnPollTick(object? sender, EventArgs e)
    {
        // Timer tick fires on the UI thread. Stop it so long polls can't stack,
        // then run the actual HTTP work on the thread pool so a slow Syncthing
        // response (cascading 5s timeouts) can't freeze the tray menu/tooltip.
        _pollTimer.Stop();
        try
        {
            await Task.Run(PollSyncStatusCore);
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Poll task faulted: " + ex.Message);
        }
        finally
        {
            if (!_disposed)
                _pollTimer.Start();
        }
    }

    /// <summary>
    /// Initial synchronous poll called from StartAfterDelay — populates tooltip and
    /// menu state before the first timer tick fires.
    /// </summary>
    private void PollSyncStatus() => PollSyncStatusCore();

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread when called from a
    /// background thread. No-op on the UI thread.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_messageWindow.InvokeRequired)
        {
            try { _messageWindow.BeginInvoke(action); }
            catch (ObjectDisposedException) { /* handle already destroyed — tray exiting */ }
            catch (InvalidOperationException) { /* handle not yet created — same exit state */ }
        }
        else
        {
            action();
        }
    }

    private void PollSyncStatusCore()
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
        bool apiReachable = true;
        try
        {
            var (status, body) = _api.Get("/rest/db/completion");
            if (status < 0) { apiReachable = false; }
            if (status == 200)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("completion", out var compEl) && compEl.TryGetDouble(out double pct))
                {
                    pct = Math.Round(pct, 1);
                    if (pct >= 100)
                    {
                        _syncStatus = _paused ? "paused" : "idle";
                        _syncDetail = _paused ? "Paused" : "";
                    }
                    else
                    {
                        _syncStatus = "syncing";
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
            apiReachable = false;
        }

        // Fast-fail: skip remaining API calls if connection refused
        if (!apiReachable)
        {
            UpdateTooltip();
            return;
        }

        // 2. Device tracking
        try
        {
            var (status2, body2) = _api.Get("/rest/system/connections");
            if (status2 == 200)
            {
                if (_deviceApiFailureNotified)
                {
                    TrayLog.Info("Device connections endpoint recovered.");
                    _deviceApiFailureNotified = false;
                }

                using var doc2 = JsonDocument.Parse(body2);
                if (doc2.RootElement.TryGetProperty("connections", out var connections))
                {
                    bool allPaused = true;
                    int deviceCount = 0;
                    int connCount = 0;

                    foreach (var prop in connections.EnumerateObject())
                    {
                        string deviceId = prop.Name;
                        var dev = prop.Value;

                        bool connected = dev.TryGetProperty("connected", out var cEl) && cEl.GetBoolean();
                        bool paused = dev.TryGetProperty("paused", out var pEl) && pEl.GetBoolean();

                        if (!paused)
                            allPaused = false;

                        deviceCount++;
                        if (connected)
                            connCount++;

                        if (_devicesPollSeeded && _knownDevices.TryGetValue(deviceId, out bool wasConnected))
                        {
                            if (connected && !wasConnected)
                            {
                                ShowOsd("Device connected", 3000);
                                PlaySound(System.Media.SystemSounds.Asterisk);
                            }
                            else if (!connected && wasConnected)
                            {
                                ShowOsd("Device disconnected", 3000);
                                PlaySound(System.Media.SystemSounds.Exclamation);
                            }
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
            else
            {
                // HTTP non-200 — one-shot OSD so the user knows device events
                // (connect/disconnect notifications) have silently stopped.
                if (!_deviceApiFailureNotified)
                {
                    _deviceApiFailureNotified = true;
                    ShowOsd($"Device polling failed (HTTP {status2}) — check API key", 5000);
                    TrayLog.Warn($"Device connections endpoint returned HTTP {status2}.");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_deviceApiFailureNotified)
            {
                _deviceApiFailureNotified = true;
                ShowOsd("Device polling failed — check Syncthing status", 5000);
                TrayLog.Warn("Device connections threw: " + ex.Message);
            }
        }

        // 3. Conflict detection (check all known folders)
        // Track per-folder error counts so a transient failure on one folder
        // doesn't reset totalErrors and then spuriously re-fire the "N new errors"
        // OSD when that folder recovers.
        try
        {
            foreach (var folder in _folders)
            {
                try
                {
                    var (fs, fb) = _api.Get($"/rest/db/status?folder={Uri.EscapeDataString(folder.Id)}");
                    if (fs == 200)
                    {
                        using var fd = JsonDocument.Parse(fb);
                        if (fd.RootElement.TryGetProperty("pullErrors", out var peEl) && peEl.TryGetInt32(out int ec))
                            _folderPullErrors[folder.Id] = ec;
                    }
                    // Non-200 or missing pullErrors — keep the prior count rather
                    // than defaulting to 0 (which would fake a recovery).
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"Folder status read failed for {folder.Id}: {ex.Message}");
                }
            }

            int totalErrors = 0;
            foreach (var kv in _folderPullErrors) totalErrors += kv.Value;

            if (totalErrors > _lastConflictCount && _lastConflictCount >= 0)
            {
                int newErrs = totalErrors - _lastConflictCount;
                ShowOsd($"{newErrs} file error(s) detected \u2014 check Web UI", 5000);
                PlaySound(System.Media.SystemSounds.Hand);
            }
            _lastConflictCount = totalErrors;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Conflict detection outer catch: " + ex.Message);
        }

        // 4. Network auto-pause
        if (_config.NetworkAutoPause)
        {
            try
            {
                int cat = GetNetworkCategory();
                if (cat == -1)
                {
                    // WMI query failed — one-shot warn so user knows auto-pause is asleep
                    if (!_wmiFailureNotified)
                    {
                        _wmiFailureNotified = true;
                        ShowOsd("Network auto-pause: WMI unavailable — still syncing", 5000);
                        TrayLog.Warn("Network auto-pause disabled: WMI returned no NetworkCategory.");
                    }
                }
                else if (cat != _lastNetworkCategory && _lastNetworkCategory != -1)
                {
                    if (cat == 0 && !_paused)
                    {
                        var (status, _) = _api.Post("/rest/system/pause");
                        if (status == 200)
                        {
                            _paused = true;
                            _autoPaused = true;
                            UpdateTrayIcon();
                            BuildMenu();
                            ShowOsd("Auto-paused: public network detected", 3000);
                        }
                        else
                        {
                            ShowOsd($"Auto-pause FAILED (HTTP {status}) — still syncing on public network", 6000);
                            TrayLog.Warn($"Auto-pause request returned HTTP {status}; _paused not flipped.");
                        }
                    }
                    else if (cat != 0 && _autoPaused)
                    {
                        var (status, _) = _api.Post("/rest/system/resume");
                        if (status == 200)
                        {
                            _paused = false;
                            _autoPaused = false;
                            UpdateTrayIcon();
                            BuildMenu();
                            ShowOsd("Auto-resumed: private network detected", 3000);
                        }
                        else
                        {
                            ShowOsd($"Auto-resume FAILED (HTTP {status}) — still paused", 5000);
                            TrayLog.Warn($"Auto-resume request returned HTTP {status}.");
                        }
                    }
                }
                _lastNetworkCategory = cat;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("Auto-pause error: " + ex.Message);
            }
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
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                bool newer = root.TryGetProperty("newer", out var nEl) && nEl.GetBoolean();
                string latest = root.TryGetProperty("latest", out var lEl) ? lEl.GetString() ?? string.Empty : string.Empty;
                string running = root.TryGetProperty("running", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;

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
                using var doc = JsonDocument.Parse(body);
                var list = new List<FolderInfo>();
                foreach (var folder in doc.RootElement.EnumerateArray())
                {
                    string fId = folder.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    string fLabel = folder.TryGetProperty("label", out var lblEl) ? lblEl.GetString() ?? string.Empty : string.Empty;
                    string fPath = folder.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

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
        var url = _config.WebUI;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowOsd("Web UI URL is invalid — check Settings", 4000);
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowOsd("Could not open browser — check Windows default browser", 5000);
            TrayLog.Warn("OpenWebUI Process.Start failed: " + ex.Message);
        }
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

        using var form = new SettingsForm(_config, _api, _osd, () =>
        {
            LoadFolders();
            ShowOsd("Settings saved", 3000);
        });
        form.ShowDialog();
    }

    private bool IsOverclickGuarded(int cooldownMs = 1500)
    {
        if (_stopping) return true;
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
            var (status, _) = _api.Post("/rest/system/pause");
            if (status != 200)
            {
                ShowOsd($"Pause failed (HTTP {status})", 3000);
                return;
            }
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
            var (status, _) = _api.Post("/rest/system/resume");
            if (status != 200)
            {
                ShowOsd($"Resume failed (HTTP {status})", 3000);
                return;
            }
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

    private void MenuRescanAll()
    {
        if (IsOverclickGuarded(800)) return;
        if (!IsSyncthingRunning())
        {
            ShowOsd("Syncthing is not running", 3000);
            return;
        }
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        try
        {
            var (status, _) = _api.Post("/rest/db/scan");
            if (status == 200)
                ShowOsd("Rescanning all folders...", 3000);
            else
                ShowOsd($"Rescan failed (HTTP {status})", 3000);
        }
        catch
        {
            ShowOsd("Rescan request failed", 3000);
        }
    }

    private void MenuRescanFolder(string folderId)
    {
        if (IsOverclickGuarded(800)) return;
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        try
        {
            var (status, _) = _api.Post($"/rest/db/scan?folder={Uri.EscapeDataString(folderId)}");
            if (status == 200)
                ShowOsd($"Rescanning {folderId}...", 3000);
            else
                ShowOsd($"Rescan failed (HTTP {status})", 3000);
        }
        catch
        {
            ShowOsd("Rescan request failed", 3000);
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
        if (!File.Exists(_config.SyncExe))
        {
            ShowOsd($"syncthing.exe not found: {_config.SyncExe}", 5000);
            return false;
        }

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
            // Limit Syncthing's Go runtime to 2 OS threads — reduces memory/CPU
            // footprint on a machine already running Claude, Docker, Semgrep, etc.
            psi.Environment["GOMAXPROCS"] = "2";
            var p = Process.Start(psi);
            if (p is null)
            {
                ShowOsd("Failed to start syncthing process", 5000);
                return false;
            }
            _launchedPid = p.Id;
            p.Dispose();
            InvalidateRunningCache();
            return true;
        }
        catch (Exception ex)
        {
            ShowOsd($"Launch failed: {ex.Message}", 5000);
            return false;
        }
    }

    private void StopSyncthing()
    {
        _stopping = true;
        _iconTimer.Stop();
        try
        {
            StopSyncthingCore();
        }
        finally
        {
            _stopping = false;
            if (!_disposed)
                _iconTimer.Start();
        }
    }

    private void StopSyncthingCore()
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
                    Application.DoEvents();
                }
            }
            catch { /* fall through to force kill */ }
        }

        // Force kill fallback — target our launched PID first
        if (_launchedPid != 0)
        {
            try
            {
                using var p = Process.GetProcessById(_launchedPid);
                p.Kill();
            }
            catch { /* already gone or wrong PID */ }
            _launchedPid = 0;
        }
        else
        {
            // No tracked PID — fall back to killing by name
            foreach (var p in Process.GetProcessesByName("syncthing"))
            {
                using (p)
                {
                    try { p.Kill(); } catch { /* already gone */ }
                }
            }
        }

        for (int i = 0; i < 30; i++)
        {
            InvalidateRunningCache();
            if (!IsSyncthingRunning()) break;
            Thread.Sleep(100);
            Application.DoEvents();
        }
        InvalidateRunningCache();
    }

    // --- Network Category Detection (WMI, cached 60s) ---

    private int GetNetworkCategory()
    {
        long now = Environment.TickCount64;
        if (now - _cachedNetworkCategoryTick < 60000)
            return _cachedNetworkCategory;

        _cachedNetworkCategoryTick = now;
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
                    {
                        _cachedNetworkCategory = Convert.ToInt32(cat);
                        return _cachedNetworkCategory;
                    }
                }
            }
        }
        catch { /* WMI unavailable */ }
        _cachedNetworkCategory = -1;
        return -1;
    }

    // --- Helpers ---

    /// <summary>
    /// Checks if syncthing.exe is running. Result is cached for 2 seconds to avoid
    /// repeated lookups within the same poll cycle (UpdateTrayIcon, PollSyncStatus,
    /// and BuildMenu can all call this in sequence).
    /// When we launched syncthing ourselves (_launchedPid != 0), uses O(1)
    /// GetProcessById instead of enumerating the entire process table.
    /// </summary>
    private bool IsSyncthingRunning()
    {
        long now = Environment.TickCount64;
        if (now - _cachedRunningTick < 2000)
            return _cachedRunning;

        try
        {
            bool running;
            if (_launchedPid != 0)
            {
                // Fast path: O(1) kernel handle lookup by PID
                try
                {
                    using var p = Process.GetProcessById(_launchedPid);
                    running = !p.HasExited;
                }
                catch (ArgumentException)
                {
                    // PID no longer exists
                    running = false;
                    _launchedPid = 0;
                }
            }
            else
            {
                // Slow path: only when syncthing was already running before we started
                var procs = Process.GetProcessesByName("syncthing");
                running = false;
                foreach (var p in procs)
                {
                    using (p)
                    {
                        running = true;
                    }
                }
            }
            _cachedRunning = running;
            _cachedRunningTick = now;
        }
        catch (OutOfMemoryException)
        {
            // Under extreme memory pressure, process enumeration can fail.
            // Return last known state rather than crashing the tray app.
            _cachedRunningTick = now;
        }
        return _cachedRunning;
    }

    /// <summary>
    /// Invalidates the cached process check so the next call does a fresh lookup.
    /// Call after starting or stopping syncthing so state is immediately correct.
    /// </summary>
    private void InvalidateRunningCache()
    {
        _cachedRunningTick = 0;
    }

    private void PlaySound(System.Media.SystemSound sound)
    {
        if (_config.SoundNotifications)
            sound.Play();
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
        if (_config.StopOnExit)
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

                _api.Dispose();
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
                // Explorer restarted — re-show tray icon and force icon re-assignment
                _owner._trayIcon.Visible = true;
                _owner._firstIconPoll = true;
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
