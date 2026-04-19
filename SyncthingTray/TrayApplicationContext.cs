using System.Collections.Concurrent;
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
    // Accessed from both the UI thread (LoadFolders via Settings Save) and the poll
    // thread (PollSyncStatusCore). Concurrent dict removes the enumerate-during-remove
    // race that would throw InvalidOperationException on a plain Dictionary.
    private readonly ConcurrentDictionary<string, int> _folderPullErrors = new();
    // Serializes LoadFolders so a poll-tick lazy reload can't race with a Settings-Save
    // reload. Both threads would otherwise fire duplicate HTTP GETs and compete on
    // the _folders assignment + _folderPullErrors prune.
    private readonly System.Threading.SemaphoreSlim _loadFoldersGate = new(1, 1);
    // volatile: publication ordering with `_folders` — readers that see the latch
    // set must also see the latest `_folders` assignment done immediately before it.
    private volatile bool _foldersLoadedSuccessfully;
    // volatile: read by the poll-tick on a thread-pool thread to decide whether
    // to fire auto-pause / auto-resume HTTP, written by the UI thread from
    // MenuPause / MenuResume / ClearPauseState. The race guard on the UI-marshal
    // paths relies on the read seeing the UI thread's latest write so that a
    // user-initiated MenuPause landing mid-auto-pause doesn't get silently
    // converted into an _autoPaused state by the pool-thread marshal.
    private volatile bool _paused;

    // Absolute UTC time when a timed pause should auto-resume. null = no timed pause
    // active (either not paused at all, or an "Until resumed" pause with no deadline).
    // Using absolute wall-clock time (rather than trusting WinForms Timer.Interval)
    // makes the pause survive sleep/hibernate correctly — MWB pattern.
    private DateTime? _pauseResumeAtUtc;

    // Original duration of the active pause: 0 = untimed, 5 or 30 = minutes.
    // Only meaningful when _paused is true. Used for pause.dat round-trip, OSD
    // text on pause/resume events, and the "Resuming in <N min>" tooltip during
    // a timed pause. (An earlier revision of the pause submenu used this to set
    // .Checked marks; the current menu rebuilds from scratch on every state
    // change so no checkmarks are wired today.)
    private int _activePauseMinutes;

    // Set during RestorePauseStateOnStartup when we inherit an active pause from
    // a prior session. Syncthing's /rest/system/pause is runtime-only state, so
    // after its process restart the daemon has forgotten it was paused. The next
    // poll-tick re-POSTs pause to reconcile Syncthing with our sidecar. The flag
    // is NOT cleared on the POST's 200 response — we wait for a subsequent poll
    // that actually observes `allPaused == true` in /rest/system/connections
    // before dropping the flag. Otherwise a stale pre-reapply snapshot could
    // trip the external-resume branch and silently drop the inherited pause.
    //
    // Volatile: written from both UI thread (MenuPause/ClearPauseState/Restore)
    // and the poll thread (ReapplyInheritedPause). `_foldersLoadedSuccessfully`
    // already uses this same pattern for publication ordering across threads.
    private volatile bool _pauseNeedsReapply;

    // One-shot latch for the "Start browser when Syncthing launches" setting.
    // Set in StartAfterDelay if the checkbox is on; cleared as soon as the first
    // poll confirms Syncthing is reachable and OpenWebUI is fired. Lives outside
    // LaunchSyncthing because the browser should also pop when the tray restarts
    // against an already-running Syncthing (previously the setting silently
    // no-op'd that case).
    private bool _pendingOpenWebUI;

    private readonly System.Windows.Forms.Timer _pauseTimer;
    // Tracks one-shot Timers (startup, first-run, stability) so Dispose can stop +
    // release them if the tray exits before their Tick fires — otherwise fast-exit
    // (<30 s) leaves the WinForms timer queue holding native handles that don't
    // drop until the finalizer runs.
    private readonly List<System.Windows.Forms.Timer> _oneShotTimers = new();
    private string _pauseStatePath = string.Empty;
    private int _connectedCount;
    private int _totalDevices;
    // volatile: same concurrency contract as `_paused` — UI-thread write, pool-thread
    // read in the auto-resume branch. A user-initiated ClearPauseState flipping this
    // to false mid-auto-resume must win the race.
    private volatile bool _autoPaused;
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

    // Maps Syncthing deviceID -> human-readable name. Populated from /rest/config/devices
    // on each LoadFolders call. Cached across calls so a transient roster-fetch failure
    // doesn't suddenly flip device headers back to shortened IDs; only a later successful
    // fetch replaces the cache.
    private Dictionary<string, string> _deviceRoster = new(StringComparer.Ordinal);

    // Syncthing's own device ID for this machine. Used to exclude "self" from the
    // folder.devices lists when grouping, so folders don't all appear under a
    // local-device header. Fetched once from /rest/system/status — immutable for the
    // process lifetime of a given Syncthing instance.
    private string _myDeviceId = string.Empty;

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

        // Pause auto-resume timer. Interval is re-armed each tick against the
        // absolute UTC deadline so wake-from-sleep doesn't drift the resume time.
        _pauseTimer = new System.Windows.Forms.Timer();
        _pauseTimer.Tick += OnPauseTimerTick;
        _pauseStatePath = Path.Combine(appDir, "pause.dat");
        RestorePauseStateOnStartup();

        // Startup delay — use a timer so the message pump stays alive
        if (_config.StartupDelay > 0)
        {
            var startupTimer = new System.Windows.Forms.Timer { Interval = _config.StartupDelay * 1000 };
            startupTimer.Tick += (_, _) =>
            {
                startupTimer.Stop();
                _oneShotTimers.Remove(startupTimer);
                startupTimer.Dispose();
                StartAfterDelay();
            };
            _oneShotTimers.Add(startupTimer);
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
                _oneShotTimers.Remove(firstRunTimer);
                firstRunTimer.Dispose();
                if (_disposed) return;
                OpenSettings();
            };
            _oneShotTimers.Add(firstRunTimer);
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
                TrayLog.Warn($"AppConfig.Load: file locked or unreadable: {Path.GetFileName(_config.SettingsFilePath)}");
                break;
            case AppConfigLoadResult.Corrupt:
                ShowOsd("SyncthingTray.ini appears corrupt — defaults loaded; original will be backed up on Save", 8000);
                TrayLog.Warn($"AppConfig.Load: corrupt file detected: {Path.GetFileName(_config.SettingsFilePath)}");
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
        // AND proactively clean up stale .old/.new update artifacts. The artifact
        // cleanup is deliberately deferred — doing it in Program.Main would defeat
        // the sentinel feature on post-update crashes (we'd delete the .old backup
        // before we knew whether the new version was stable).
        var stabilityTimer = new System.Windows.Forms.Timer { Interval = 30000 };
        stabilityTimer.Tick += (_, _) =>
        {
            stabilityTimer.Stop();
            _oneShotTimers.Remove(stabilityTimer);
            stabilityTimer.Dispose();
            if (_disposed) return;
            UpdateDialog.TryDeleteCrashSentinel();
            UpdateDialog.CleanupStaleUpdateArtifacts();
            TrayLog.Info("30s stability reached; crash sentinel + .old/.new cleaned.");
        };
        _oneShotTimers.Add(stabilityTimer);
        stabilityTimer.Start();

        // Sleep/wake: on Resume, the Syncthing process state and the network
        // category (for auto-pause) may both be stale. Drop caches and force an
        // immediate poll so the tray reflects reality before the next 10s tick.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        TrayLog.Info("PowerMode: Resume — invalidating caches + polling.");
        InvalidateRunningCache();

        // SystemEvents.PowerModeChanged fires on a CLR-pooled background thread.
        // Marshal everything to UI — the pause-state reads below MUST NOT race
        // with UI-thread MenuResume/ClearPauseState mutations.
        RunOnUi(() =>
        {
            if (_disposed) return;

            // A 30-min pause through a 2-hr sleep must resume at wake, not 30 min
            // after wake. The poll-tick picks up Syncthing's state eventually, but
            // firing an explicit deadline check here avoids a 10s-window stale menu.
            if (_paused && _pauseResumeAtUtc is DateTime due)
            {
                if (DateTime.UtcNow >= due)
                    MenuResume();
                else
                    StartOrRearmPauseTimer();
            }

            _pollTimer.Stop();
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { PollSyncStatusCore(); }
                catch (Exception ex) { TrayLog.Warn("PowerMode Resume poll faulted: " + ex.Message); }
                finally { if (!_disposed) RunOnUi(() => { if (!_disposed) _pollTimer.Start(); }); }
            });
        });
    }

    private void StartAfterDelay()
    {
        if (_disposed) return;

        if (!IsSyncthingRunning())
            LaunchSyncthing();

        // Latch a browser-open on first reachable poll, regardless of whether we
        // cold-launched Syncthing or it was already up. Cleared in the poll-tick.
        if (_config.StartBrowser)
            _pendingOpenWebUI = true;

        _iconTimer.Start();
        _pollTimer.Start();

        // Both calls below do synchronous HTTP (IsReachable probe + /rest/system/status
        // + /rest/config/folders + /rest/config/devices). Running them on the UI thread
        // meant the tray icon appeared but the menu was unresponsive for up to ~1.8 s
        // while Syncthing cold-started. Both functions are already written to be
        // pool-safe (BuildMenu/UpdateTooltip/UpdateTrayIcon all self-marshal via
        // RunOnUi), matching the poll-tick and PowerMode-resume call sites.
        _ = Task.Run(() =>
        {
            try { PollSyncStatusCore(); }
            catch (Exception ex) { TrayLog.Warn("StartAfterDelay PollSyncStatus faulted: " + ex.Message); }
            try { LoadFolders(); }
            catch (Exception ex) { TrayLog.Warn("StartAfterDelay LoadFolders faulted: " + ex.Message); }
        });
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

        // Synced Folders submenu — grouped by device prefix. Any label prefix (first
        // word before a space or underscore) shared by 2+ folders becomes a disabled
        // header with stripped child labels; singletons drop into an unnamed bucket
        // at the bottom. Heuristic matches how users name device-scoped folders
        // (s20_Dcim, s24_Pictures, tablet_Downloads) — no device-roster API call needed.
        if (_folders.Length > 0)
        {
            var folderItem = new ToolStripMenuItem("Synced Folders");
            BuildSyncedFoldersMenu(folderItem);
            menu.Items.Add(folderItem);
        }

        // Rescan Now
        if (running && !string.IsNullOrEmpty(_config.ApiKey))
            menu.Items.Add("Force Rescan Now", null, (_, _) => MenuRescanAll());

        menu.Items.Add(new ToolStripSeparator());

        // Settings
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());

        // Pause/Resume. When paused, the direct Resume item stays top-level for a
        // single-click resume; when not paused, the Pause submenu offers 5 min /
        // 30 min / Until resumed. MWB pattern, adapted so that the common case
        // (click to resume) remains one click rather than a submenu dive.
        if (running)
        {
            if (_paused)
            {
                string resumeLabel = "Resume Syncing";
                if (_pauseResumeAtUtc is DateTime due)
                {
                    var remaining = due - DateTime.UtcNow;
                    if (remaining.TotalSeconds > 0)
                        resumeLabel = $"Resume Syncing (auto in {FormatPauseRemaining(remaining)})";
                }
                // Overclick guard is on the click-path only; direct MenuResume callers
                // (OnPauseTimerTick auto-resume, OnPowerModeChanged, and TogglePause's
                // own guarded path) must not consume the 800 ms budget or the user's
                // very next click after an automatic resume would get "Please wait…".
                menu.Items.Add(resumeLabel, null, (_, _) =>
                {
                    if (IsOverclickGuarded(800)) return;
                    MenuResume();
                });
            }
            else
            {
                // "Until resumed" sits at the top as the primary/default action;
                // timed options live below a separator as secondary choices.
                var pauseItem = new ToolStripMenuItem("Pause Syncing");
                pauseItem.DropDownItems.Add("Until resumed", null, (_, _) => MenuPause(0));
                pauseItem.DropDownItems.Add(new ToolStripSeparator());
                pauseItem.DropDownItems.Add("5 minutes", null, (_, _) => MenuPause(5));
                pauseItem.DropDownItems.Add("30 minutes", null, (_, _) => MenuPause(30));
                menu.Items.Add(pauseItem);
            }
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

    // Short, human-readable handle for a device ID when no friendly name is known.
    // Syncthing device IDs are base32 with hyphen groups — the first group reads
    // cleanly as a stable handle (e.g. "ABCDEFG" for ID "ABCDEFG-HIJKLMN-...").
    private static string ShortenDeviceId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "unknown";
        int dash = id.IndexOf('-');
        if (dash > 0) return id[..dash];
        return id.Length > 7 ? id[..7] : id;
    }

    /// <summary>
    /// Pulls the device roster from /rest/config/devices and this Syncthing instance's
    /// own myID from /rest/system/status. Both are used by BuildSyncedFoldersMenu to
    /// group folders under human-readable device headers. Failures leave the prior
    /// cached values in place — better to show stale names than to drop back to
    /// shortened IDs on a transient blip.
    /// </summary>
    private void LoadDeviceRosterAndMyId()
    {
        // myID only needs fetching once per process — Syncthing's device identity
        // is immutable for a given install (tied to a self-signed cert).
        if (string.IsNullOrEmpty(_myDeviceId))
        {
            try
            {
                var (s, body) = _api.Get("/rest/system/status", timeoutMs: 1500);
                if (s == 200)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("myID", out var idEl))
                        _myDeviceId = idEl.GetString() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                TrayLog.Warn("LoadDeviceRosterAndMyId(myID): " + ex.Message);
            }
        }

        try
        {
            var (s, body) = _api.Get("/rest/config/devices", timeoutMs: 1500);
            if (s != 200) return;
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                string id = d.TryGetProperty("deviceID", out var i) ? i.GetString() ?? string.Empty : string.Empty;
                string name = d.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0) continue;
                if (name.Length == 0) name = ShortenDeviceId(id);
                next[id] = name;
            }
            _deviceRoster = next;
        }
        catch (Exception ex)
        {
            TrayLog.Warn("LoadDeviceRosterAndMyId(devices): " + ex.Message);
        }
    }

    /// <summary>
    /// Builds the Synced Folders submenu grouped by remote device, using structured
    /// data from Syncthing's config (folder.devices + device roster) rather than
    /// parsing folder labels. A folder shared with N remote devices appears under N
    /// device headers — this is by design so every header's contents are complete.
    /// Folders with no remote devices fall into a "Local only" bucket at the bottom.
    /// </summary>
    private void BuildSyncedFoldersMenu(ToolStripMenuItem folderItem)
    {
        // deviceName -> folders shared with that device (dups across headers are
        // expected for multi-device folders).
        var byDevice = new Dictionary<string, List<FolderInfo>>(StringComparer.Ordinal);
        var localOnly = new List<FolderInfo>();

        foreach (var f in _folders)
        {
            // Filter self out. If _myDeviceId isn't known yet (first load failed the
            // system/status fetch), the local device leaks in as its own group — ugly
            // but not broken; resolves itself on the next successful LoadFolders.
            var remotes = f.DeviceIds.Where(id => id.Length > 0 && id != _myDeviceId).ToArray();
            if (remotes.Length == 0)
            {
                localOnly.Add(f);
                continue;
            }
            foreach (var id in remotes)
            {
                var name = _deviceRoster.TryGetValue(id, out var n) && n.Length > 0
                    ? n
                    : ShortenDeviceId(id);
                if (!byDevice.TryGetValue(name, out var list))
                    byDevice[name] = list = new List<FolderInfo>();
                list.Add(f);
            }
        }

        var orderedDeviceNames = byDevice.Keys
            .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        void EmitFolder(FolderInfo f)
        {
            var path = f.Path;
            var folderId = f.Id;
            var subItem = new ToolStripMenuItem(MenuTextSanitizer.Sanitize(f.Label));
            subItem.DropDownItems.Add("Open Folder", null, (_, _) => OpenFolder(path));
            subItem.DropDownItems.Add("Rescan", null, (_, _) => MenuRescanFolder(folderId));
            folderItem.DropDownItems.Add(subItem);
        }

        bool firstGroup = true;
        foreach (var deviceName in orderedDeviceNames)
        {
            if (!firstGroup) folderItem.DropDownItems.Add(new ToolStripSeparator());
            firstGroup = false;
            folderItem.DropDownItems.Add(new ToolStripMenuItem(MenuTextSanitizer.Sanitize(deviceName)) { Enabled = false });
            foreach (var f in byDevice[deviceName].OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase))
                EmitFolder(f);
        }

        if (localOnly.Count > 0)
        {
            if (!firstGroup) folderItem.DropDownItems.Add(new ToolStripSeparator());
            firstGroup = false;
            folderItem.DropDownItems.Add(new ToolStripMenuItem("Local only") { Enabled = false });
            foreach (var f in localOnly.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase))
                EmitFolder(f);
        }

        folderItem.DropDownItems.Add(new ToolStripSeparator());
        folderItem.DropDownItems.Add("Refresh List", null, (_, _) =>
        {
            // Pool-thread hop — LoadFolders does synchronous HTTP (up to ~1.8 s on a
            // slow Syncthing). Running on the UI thread froze the menu between click
            // and dismiss. BuildMenu inside LoadFolders self-marshals back to the UI.
            _ = Task.Run(() =>
            {
                try { LoadFolders(); }
                catch (Exception ex) { TrayLog.Warn("Refresh List LoadFolders faulted: " + ex.Message); }
            });
        });
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

        // One-shot: once Syncthing is running + reachable, honor the "Start browser
        // when Syncthing launches" setting regardless of whether we cold-launched
        // Syncthing or joined an existing process. Fires at most once per tray session.
        // Re-check _config.StartBrowser at fire time so a user who toggled the setting
        // off in Settings during the cold-start window (before Syncthing became
        // reachable) doesn't get a browser popped at them anyway.
        if (_pendingOpenWebUI && _api.IsReachable())
        {
            _pendingOpenWebUI = false;
            if (_config.StartBrowser)
                RunOnUi(OpenWebUI);
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

        // Lazy folder reload — when the tray starts while Syncthing is off, LoadFolders
        // bails on the probe and leaves _folders empty. Once Syncthing comes up and a
        // poll succeeds, refetch so the "Synced Folders" submenu appears without the
        // user having to open+save Settings or hit Refresh List.
        // Using a success latch (not `_folders.Length == 0`) so legit zero-folder
        // users don't re-fetch every 10s forever.
        if (!_foldersLoadedSuccessfully)
            LoadFolders();

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
                    {
                        // First poll after inheriting pause from pause.dat: re-apply
                        // to Syncthing before reconciling local state. Confirm-handshake:
                        // stay in this branch until Syncthing actually reports
                        // `allPaused == true`. The prior shape of this code cleared
                        // the reapply flag on the POST's 200 response, but the next
                        // poll could still see `allPaused == false` (stale snapshot
                        // that was fetched before the POST took effect, admin-resumed
                        // concurrently, or silent rejection) — hitting the else branch
                        // and silently dropping the inherited pause.
                        if (_pauseNeedsReapply)
                        {
                            if (allPaused)
                            {
                                // Syncthing has confirmed the reapply; safe to drop the flag.
                                _pauseNeedsReapply = false;
                                TrayLog.Info("Inherited pause confirmed by Syncthing; reapply flag cleared.");
                            }
                            else
                            {
                                // Re-POST and stay in this branch. ReapplyInheritedPause is
                                // idempotent server-side; fire again on every unconfirmed tick.
                                ReapplyInheritedPause();
                            }
                            // Either way, skip the external-resume branch this tick.
                        }
                        else
                        {
                            // _paused read-modify-write + ClearPauseState (which touches
                            // _pauseTimer, a WinForms UI-affine timer) must happen on
                            // the UI thread — this branch runs from PollSyncStatusCore
                            // via `await Task.Run(...)` which drops the sync context.
                            bool pausedNow = allPaused;
                            RunOnUi(() =>
                            {
                                if (_disposed) return;
                                bool wasPaused = _paused;
                                _paused = pausedNow;
                                // External resume (user hit Resume in Web UI, or our deadline
                                // already fired and Syncthing reflects it): drop our timer +
                                // sidecar so a stale deadline doesn't double-fire MenuResume().
                                if (wasPaused && !_paused)
                                    ClearPauseState();
                            });
                        }
                    }

                    // Prune stale device entries — if a device was removed from
                    // Syncthing config, it won't reappear in this response; drop it
                    // from _knownDevices to prevent unbounded growth.
                    var seenDevices = new HashSet<string>(connections.EnumerateObject().Select(p => p.Name));
                    foreach (var id in _knownDevices.Keys.Where(k => !seenDevices.Contains(k)).ToList())
                        _knownDevices.Remove(id);
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
                    // Auto-pause / auto-resume: HTTP on this (pool) thread, then
                    // marshal the UI-affine state mutations (_pauseTimer.Stop,
                    // UpdateTrayIcon, BuildMenu) onto the UI thread as a single atom.
                    // WinForms Timer.Stop() off-UI is undefined behavior — historically
                    // this block's 99/100 silent success is exactly the symptom.
                    if (cat == 0 && !_paused)
                    {
                        var (status, _) = _api.Post("/rest/system/pause");
                        if (status == 200)
                        {
                            RunOnUi(() =>
                            {
                                if (_disposed) return;
                                // CAS guard: if the user flipped _paused true
                                // (MenuPause) between the pool-thread read above
                                // and this marshal, their action wins. Stamping
                                // _autoPaused on their manual pause would cause
                                // the next network transition to auto-resume over
                                // their intent.
                                if (_paused) return;
                                _paused = true;
                                _autoPaused = true;
                                // Auto-pause has no deadline — it resumes on the next network
                                // category transition, not on a clock. Unify the state so the
                                // menu renders "Resume Syncing" (no countdown).
                                _activePauseMinutes = 0;
                                _pauseResumeAtUtc = null;
                                _pauseTimer.Stop();
                                PersistPauseState();
                                UpdateTrayIcon();
                                BuildMenu();
                                ShowOsd("Auto-paused: public network detected", 3000);
                            });
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
                            RunOnUi(() =>
                            {
                                if (_disposed) return;
                                // CAS guard: if the user flipped the state (either
                                // ClearPauseState dropped _autoPaused or MenuResume
                                // cleared _paused) between the pool-thread read and
                                // this marshal, there's nothing left to auto-resume.
                                if (!_autoPaused || !_paused) return;
                                ClearPauseState();
                                UpdateTrayIcon();
                                BuildMenu();
                                ShowOsd("Auto-resumed: private network detected", 3000);
                            });
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

    /// <summary>
    /// Called from both the poll-tick (pool thread) and MenuCheckUpdate (now also
    /// pool thread via Task.Run). HTTP runs here; _updateAvailable/_updateRunning
    /// writes + the BuildMenu marshal back to UI.
    /// </summary>
    private void CheckForUpdate()
    {
        _lastUpdateCheck = Environment.TickCount64;
        try
        {
            var (status, body) = _api.Get("/rest/system/upgrade");
            if (status != 200) return;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool newer = root.TryGetProperty("newer", out var nEl) && nEl.GetBoolean();
            string latest = root.TryGetProperty("latest", out var lEl) ? lEl.GetString() ?? string.Empty : string.Empty;
            string running = root.TryGetProperty("running", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;

            RunOnUi(() =>
            {
                if (_disposed) return;
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
            });
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
        // CheckForUpdate fires its own HTTP; hop to pool so the click doesn't
        // freeze the menu. Follow-up OSD reads _updateAvailable on UI thread.
        _ = Task.Run(() =>
        {
            CheckForUpdate();
            RunOnUi(() =>
            {
                if (_disposed) return;
                if (_updateAvailable.Length == 0)
                {
                    var msg = "Syncthing is up to date";
                    if (_updateRunning.Length > 0)
                        msg += $" ({_updateRunning})";
                    ShowOsd(msg, 3000);
                }
            });
        });
    }

    private void DoUpdate()
    {
        if (_updateAvailable.Length == 0) return;
        _ = Task.Run(() =>
        {
            int status;
            try
            {
                status = _api.Post("/rest/system/upgrade").StatusCode;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("DoUpdate POST threw: " + ex.Message);
                ShowOsd("Upgrade request failed", 5000);
                return;
            }
            if (status == 200)
            {
                RunOnUi(() =>
                {
                    if (_disposed) return;
                    ShowOsd($"Syncthing upgrading to {_updateAvailable}...", 5000);
                    _updateAvailable = string.Empty;
                    BuildMenu();
                });
            }
            else
            {
                ShowOsd($"Upgrade failed (HTTP {status})", 5000);
            }
        });
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

        // Non-blocking gate — if another thread is already inside LoadFolders (UI
        // Settings-Save vs poll-tick lazy reload), skip. The in-flight call will
        // publish results via `_folders = ...`. No need to duplicate the HTTP GET.
        if (!_loadFoldersGate.Wait(0))
        {
            TrayLog.Info("LoadFolders: skipped — concurrent call in flight.");
            return;
        }
        try
        {
            // Fast probe — skip the 5s HttpClient timeout on the UI thread when Syncthing
            // isn't listening. Called from the Save-settings callback, so a 5s freeze here
            // showed up to the user as "Save button lags then closes".
            if (!_api.IsReachable())
            {
                // Drop the stale list — menu shouldn't show ghost folders from before
                // Syncthing died. The poll-tick lazy-reload will refill it when
                // Syncthing comes back.
                _folders = [];
                _foldersLoadedSuccessfully = false;
                BuildMenu();
                return;
            }

            try
            {
                var (status, body) = _api.Get("/rest/config/folders", timeoutMs: 1500);
                if (status == 200)
                {
                    using var doc = JsonDocument.Parse(body);
                    var list = new List<FolderInfo>();
                    foreach (var folder in doc.RootElement.EnumerateArray())
                    {
                        string fId = folder.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                        string fLabel = folder.TryGetProperty("label", out var lblEl) ? lblEl.GetString() ?? string.Empty : string.Empty;
                        string fPath = folder.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

                        // Parse the devices[] array — each entry is {deviceID, introducedBy, ...}.
                        // We keep only the IDs; names resolve via the device roster fetched below.
                        var deviceIds = new List<string>();
                        if (folder.TryGetProperty("devices", out var devsEl) && devsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var dev in devsEl.EnumerateArray())
                            {
                                if (dev.TryGetProperty("deviceID", out var didEl))
                                {
                                    var did = didEl.GetString();
                                    if (!string.IsNullOrEmpty(did)) deviceIds.Add(did);
                                }
                            }
                        }

                        string displayName = fLabel.Length > 0 ? fLabel : fId;
                        if (fPath.Length > 0)
                            list.Add(new FolderInfo(fId, displayName, fPath, deviceIds.ToArray()));
                    }
                    _folders = list.ToArray();

                    // Refresh the device roster + myID alongside the folder list so the
                    // menu can group by human-readable device name. Failures don't abort
                    // the folder load — degraded mode is grouping by shortened device ID.
                    LoadDeviceRosterAndMyId();

                    // Success latch — even an empty folder list counts. Prevents the poll
                    // tick from re-firing LoadFolders every 10s for zero-folder users.
                    _foldersLoadedSuccessfully = true;
                    TrayLog.Info($"LoadFolders: HTTP 200, {_folders.Length} folder(s) loaded, {_deviceRoster.Count} device(s) in roster.");

                    // Prune per-folder error tracking for folders no longer configured —
                    // otherwise _folderPullErrors grows unbounded over months of churn.
                    var live = new HashSet<string>(_folders.Select(f => f.Id));
                    foreach (var id in _folderPullErrors.Keys.Where(k => !live.Contains(k)).ToList())
                        _folderPullErrors.TryRemove(id, out _);
                }
                else
                {
                    // Non-200 (401 after token rotate, 500 during Syncthing restart).
                    // Reset latch so the next poll tick retries — otherwise a single
                    // successful load + a later auth change strands us with ghost folders
                    // forever.
                    _foldersLoadedSuccessfully = false;
                    TrayLog.Warn($"LoadFolders: HTTP {status}; will retry on next poll.");
                }
            }
            catch (Exception ex)
            {
                _foldersLoadedSuccessfully = false;
                TrayLog.Warn("LoadFolders threw: " + ex.Message);
            }

            BuildMenu();
        }
        finally
        {
            // Swallow ObjectDisposedException on tray-exit race: if Dispose fires
            // while a pool thread is still inside LoadFolders, _loadFoldersGate can
            // be disposed before we reach here. The exception would be caught by
            // the outer Task.Run catch but emit a misleading "LoadFolders faulted"
            // log during normal clean exit.
            try { _loadFoldersGate.Release(); }
            catch (ObjectDisposedException) { }
        }
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

    // Windows path APIs treat '\' and '/' as interchangeable separators, so
    // `//srv/s`, `\/srv\s`, `/\srv\s` all normalise to UNC alongside `\\srv\s`.
    // Prefix-string matching (StartsWith(@"\\")) only catches 1 of the 4 forms;
    // predicate on character-class membership at positions [0] and [1] instead.
    internal static bool IsUncPath(string path)
    {
        return path.Length >= 2
            && (path[0] == '\\' || path[0] == '/')
            && (path[1] == '\\' || path[1] == '/');
    }

    private void OpenFolder(string path)
    {
        // Reject UNC, URI schemes (ms-settings:, shell:appsfolder\..., etc.),
        // and non-fully-qualified paths before handing anything to the shell.
        // Syncthing's /rest/config/folders is the source of truth for `path`,
        // but defense-in-depth: a hostile peer-shared folder definition or a
        // corrupted config could smuggle a protocol handler path through.
        // UNC is refused (not just skipped) because Directory.Exists is bypassed
        // on UNC for timeout reasons — we don't want Process.Start racing the
        // shell against an unreachable share either.
        int colonIdx = path.IndexOf(':');
        bool isUnc = IsUncPath(path);
        bool hasUriColon = colonIdx >= 0 && colonIdx != 1;
        if (isUnc || hasUriColon || !Path.IsPathFullyQualified(path))
        {
            string safe = path.Length > 80 ? path.Substring(0, 80) + "..." : path;
            ShowOsd($"Folder path rejected: {safe}", 3500);
            TrayLog.Warn($"OpenFolder rejected suspicious path: {path}");
            return;
        }
        if (!Directory.Exists(path))
        {
            ShowOsd($"Folder not found: {path}", 3000);
            return;
        }

        // Fire on a pool thread so the context menu dismisses immediately.
        // UseShellExecute=true lets the Windows shell reuse any already-running
        // Explorer process — meaningfully faster than spawning explorer.exe via
        // a fresh Process.Start(fileName: "explorer.exe") which always forks a
        // new process (100-500 ms on a cold cache).
        _ = Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                RunOnUi(() => ShowOsd($"Failed to open folder: {ex.Message}", 4000));
                TrayLog.Warn($"OpenFolder({path}) threw: {ex.Message}");
            }
        });
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

        using var form = new SettingsForm(
            _config, _api, _osd,
            // Apply: lightweight — rebuild the menu so local-settings labels (WebUI
            // URL etc.) reflect what the user just saved. No HTTP refetch.
            onApplied: BuildMenu,
            // Save: full path — refetch folder list + OSD confirmation.
            // Hop to a pool thread so Save-click doesn't block the dialog close
            // on LoadFolders's HTTP round-trip (up to 1.5 s + IsReachable probe
            // ~300 ms). Matches the threading contract at the other LoadFolders
            // call sites (poll-tick Task.Run(PollSyncStatusCore)).
            onSaved: () =>
            {
                _ = Task.Run(() =>
                {
                    LoadFolders();
                    RunOnUi(() => ShowOsd("Settings saved", 3000));
                });
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

    // Click-path (double-click / middle-click) default is untimed pause — preserves
    // the muscle memory from pre-timed-pause versions where a single click toggled
    // indefinite pause/resume.
    private void TogglePause()
    {
        if (IsOverclickGuarded(800)) return;
        if (_paused)
            MenuResume();
        else
            MenuPause(0);
    }

    /// <summary>
    /// Enter (or re-arm) a pause. minutes = 0 means "Until resumed" (no deadline).
    /// Click-handler entry point — runs HTTP on a pool thread so a 5-second HttpClient
    /// timeout on a dead Syncthing can't freeze the tray menu. State mutations + UI
    /// updates marshal back via RunOnUi.
    /// </summary>
    private void MenuPause(int minutes)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required for pause \u2014 set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            int status;
            try
            {
                status = _api.Post("/rest/system/pause").StatusCode;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("MenuPause POST threw: " + ex.Message);
                ShowOsd("Failed to pause syncing", 3000);
                return;
            }
            if (status != 200)
            {
                ShowOsd($"Pause failed (HTTP {status})", 3000);
                return;
            }
            RunOnUi(() =>
            {
                if (_disposed) return;
                _paused = true;
                _activePauseMinutes = minutes;
                _pauseTimer.Stop();
                if (minutes > 0)
                {
                    _pauseResumeAtUtc = DateTime.UtcNow.AddMinutes(minutes);
                    StartOrRearmPauseTimer();
                }
                else
                {
                    _pauseResumeAtUtc = null;
                }
                PersistPauseState();
                UpdateTrayIcon();
                BuildMenu();
                ShowOsd($"Syncing paused ({FormatPauseDuration(minutes)})", 3000);
            });
        });
    }

    /// <summary>
    /// Resume syncing. Click-handler entry point — HTTP on pool thread, state + UI
    /// on UI thread. Also called from OnPauseTimerTick and OnPowerModeChanged (both
    /// UI-thread callers — the Task.Run hop there is redundant but safe).
    /// </summary>
    private void MenuResume()
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required for resume \u2014 set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            int status;
            try
            {
                status = _api.Post("/rest/system/resume").StatusCode;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("MenuResume POST threw: " + ex.Message);
                ShowOsd("Failed to resume syncing", 3000);
                return;
            }
            if (status != 200)
            {
                ShowOsd($"Resume failed (HTTP {status})", 3000);
                return;
            }
            RunOnUi(() =>
            {
                if (_disposed) return;
                ClearPauseState();
                UpdateTrayIcon();
                BuildMenu();
                ShowOsd("Syncing resumed", 3000);
            });
        });
    }

    /// <summary>
    /// Reset all pause-related state. Called after a successful resume (manual
    /// or auto) and when the poll-tick detects Syncthing was resumed externally.
    /// </summary>
    private void ClearPauseState()
    {
        _paused = false;
        _autoPaused = false;
        _activePauseMinutes = 0;
        _pauseResumeAtUtc = null;
        _pauseNeedsReapply = false;
        _pauseTimer.Stop();
        DeletePauseStateFile();
    }

    private void StartOrRearmPauseTimer()
    {
        if (_pauseResumeAtUtc is not DateTime due) return;
        var msUntil = (long)(due - DateTime.UtcNow).TotalMilliseconds;
        // WinForms Timer.Interval must be ≥ 1. Clamp short deadlines to 1s so the
        // tick handler runs soon and fires the auto-resume path.
        _pauseTimer.Interval = (int)Math.Max(1000, Math.Min(int.MaxValue, msUntil));
        _pauseTimer.Start();
    }

    private void OnPauseTimerTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _pauseTimer.Stop();
        if (_pauseResumeAtUtc is not DateTime due)
            return;

        if (DateTime.UtcNow >= due)
        {
            TrayLog.Info("Pause timer elapsed — auto-resuming.");
            MenuResume();
        }
        else
        {
            // Early tick (shouldn't happen, but guard — a Timer with extreme Interval
            // values can fire prematurely). Re-arm for the remaining slice.
            StartOrRearmPauseTimer();
        }
    }

    private static string FormatPauseDuration(int minutes) => minutes switch
    {
        0 => "until resumed",
        1 => "1 minute",
        _ => $"{minutes} minutes",
    };

    private static string FormatPauseRemaining(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 2)
            return $"{(int)Math.Ceiling(remaining.TotalMinutes)} min";
        if (remaining.TotalSeconds >= 60)
            return "1 min";
        var secs = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{secs} sec";
    }

    private void PersistPauseState()
    {
        try
        {
            if (!_paused)
            {
                DeletePauseStateFile();
                return;
            }
            // Line 1: minutes (0 = untimed). Line 2: resume-at ticks (empty for untimed).
            // Line 3: auto-pause flag (1 if this pause was triggered by network auto-pause,
            // 0 if user-initiated). The auto flag lets RestorePauseStateOnStartup drop an
            // inherited auto-pause when the network is no longer public — otherwise a
            // reboot mid-auto-pause would leave sync stuck paused even after returning
            // to a private network.
            var ticksLine = _pauseResumeAtUtc is DateTime due ? due.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            var autoLine = _autoPaused ? "1" : "0";
            var body = $"{_activePauseMinutes}\n{ticksLine}\n{autoLine}";
            File.WriteAllText(_pauseStatePath, body);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"PersistPauseState: {ex.Message}");
        }
    }

    private void DeletePauseStateFile()
    {
        try
        {
            if (File.Exists(_pauseStatePath)) File.Delete(_pauseStatePath);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"DeletePauseStateFile: {ex.Message}");
        }
    }

    /// <summary>
    /// Called once from the ctor. If pause.dat exists, re-attach the timer and
    /// mark that Syncthing needs to be re-paused on the first successful poll
    /// (Syncthing loses its runtime pause state across its own restart / a
    /// reboot). Expired timed pauses are dropped so we don't re-pause a deadline
    /// that's already in the past.
    /// </summary>
    // pause.dat has a fixed 3-line schema (minutes + ticks + auto flag). The real
    // file is ~20 bytes. A tampered / runaway writer could balloon it to GB — we
    // cap the read at 256 KB so a boot-time OOM can't brick the tray.
    private const long PauseStateMaxBytes = 256 * 1024;

    private void RestorePauseStateOnStartup()
    {
        try
        {
            if (!File.Exists(_pauseStatePath)) return;
            var info = new FileInfo(_pauseStatePath);
            if (info.Length > PauseStateMaxBytes)
            {
                TrayLog.Warn($"RestorePauseStateOnStartup: pause.dat is {info.Length} bytes (cap {PauseStateMaxBytes}) — deleting.");
                DeletePauseStateFile();
                return;
            }
            var lines = File.ReadAllLines(_pauseStatePath);
            if (lines.Length < 1 || !int.TryParse(lines[0], out int minutes))
            {
                TrayLog.Warn("RestorePauseStateOnStartup: malformed pause.dat — deleting.");
                DeletePauseStateFile();
                return;
            }

            // Parse the auto-paused flag (line 3). Missing = legacy 2-line format from
            // earlier v2.2.12 builds — default to false (treat as manual pause).
            bool wasAutoPaused = lines.Length >= 3 && lines[2].Trim() == "1";

            // Network-adaptive restore: if this pause was triggered by auto-pause on
            // public wifi but we're now on a private/domain network, the pause reason
            // is already gone. Drop the inherited pause so sync resumes automatically
            // on boot rather than leaving the user manually clicking Resume. WMI
            // unavailable (cat == -1) means we can't confirm network state — conservative
            // default is to keep the pause and let the user decide.
            if (wasAutoPaused && _config.NetworkAutoPause)
            {
                int cat = GetNetworkCategory();
                // NetworkCategory: 0 = Public, 1 = Private, 2 = DomainAuthenticated.
                if (cat > 0)
                {
                    TrayLog.Info($"RestorePauseStateOnStartup: inherited auto-pause but network is now private (cat={cat}) — dropping stale pause.");
                    DeletePauseStateFile();
                    return;
                }
                // cat == 0 (still public) or cat == -1 (WMI down): fall through, restore pause.
            }

            if (minutes > 0)
            {
                if (lines.Length < 2 || !long.TryParse(lines[1], out long ticks))
                {
                    TrayLog.Warn("RestorePauseStateOnStartup: timed pause missing ticks — deleting.");
                    DeletePauseStateFile();
                    return;
                }
                // Bounds-check: `new DateTime(ticks)` throws on negative or
                // > DateTime.MaxValue.Ticks. Untrusted input (hand-edit, tampered
                // pause.dat) must not OOM or crash the tray at boot.
                if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
                {
                    TrayLog.Warn($"RestorePauseStateOnStartup: ticks out of range ({ticks}) — deleting.");
                    DeletePauseStateFile();
                    return;
                }
                var due = new DateTime(ticks, DateTimeKind.Utc);
                if (due - DateTime.UtcNow <= TimeSpan.FromSeconds(5))
                {
                    TrayLog.Info("RestorePauseStateOnStartup: deadline already passed — dropping stale pause.");
                    DeletePauseStateFile();
                    return;
                }
                _activePauseMinutes = minutes;
                _pauseResumeAtUtc = due;
                StartOrRearmPauseTimer();
            }
            else
            {
                _activePauseMinutes = 0;
                _pauseResumeAtUtc = null;
            }

            _paused = true;
            _autoPaused = wasAutoPaused;
            _pauseNeedsReapply = true;
            TrayLog.Info($"RestorePauseStateOnStartup: inherited pause ({FormatPauseDuration(minutes)}, auto={wasAutoPaused}); Syncthing will be re-paused on first successful poll.");
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"RestorePauseStateOnStartup: {ex.Message}");
            DeletePauseStateFile();
        }
    }

    /// <summary>
    /// Called from the poll-tick once Syncthing is confirmed reachable. Re-POSTs
    /// /rest/system/pause to reconcile Syncthing's runtime state with our sidecar.
    /// The flag is NOT cleared here on HTTP 200 — the caller does that when the
    /// NEXT poll confirms `allPaused == true`. Firing multiple times while waiting
    /// for confirmation is safe (pause is idempotent server-side).
    /// </summary>
    private void ReapplyInheritedPause()
    {
        if (!_pauseNeedsReapply) return;
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            // Can't act without an API key. Leave the flag set so a later key
            // entry + poll cycle can still reconcile.
            return;
        }
        try
        {
            var (status, _) = _api.Post("/rest/system/pause");
            if (status == 200)
                TrayLog.Info("ReapplyInheritedPause: POST 200 — awaiting next-poll confirmation.");
            else
                TrayLog.Warn($"ReapplyInheritedPause: HTTP {status} — will retry next poll.");
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"ReapplyInheritedPause: {ex.Message}");
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
        // HTTP on pool thread — a dead Syncthing would otherwise freeze the tray
        // menu for HttpClient.Timeout (5 s). OSD self-marshals to UI.
        _ = Task.Run(() =>
        {
            try
            {
                var (status, _) = _api.Post("/rest/db/scan");
                ShowOsd(status == 200 ? "Rescanning all folders..." : $"Rescan failed (HTTP {status})", 3000);
            }
            catch (Exception ex)
            {
                TrayLog.Warn("MenuRescanAll POST threw: " + ex.Message);
                ShowOsd("Rescan request failed", 3000);
            }
        });
    }

    private void MenuRescanFolder(string folderId)
    {
        if (IsOverclickGuarded(800)) return;
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            ShowOsd("API Key required — set in Settings", 3000);
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var (status, _) = _api.Post($"/rest/db/scan?folder={Uri.EscapeDataString(folderId)}");
                ShowOsd(status == 200 ? $"Rescanning {folderId}..." : $"Rescan failed (HTTP {status})", 3000);
            }
            catch (Exception ex)
            {
                TrayLog.Warn("MenuRescanFolder POST threw: " + ex.Message);
                ShowOsd("Rescan request failed", 3000);
            }
        });
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
            // Tray owns browser-opening (see _pendingOpenWebUI). Always pass
            // --no-browser so Syncthing doesn't race with us, and so restart-the-
            // tray-while-Syncthing-is-running still honors the setting.
            const string args = "--no-browser";
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
            // `using` so a throw on .Id (process exited between Start and the Id
            // read — rare, but observed on slow-disk + corrupt-exe combinations)
            // still releases the Process handle; otherwise it leaked until the
            // finalizer ran. Process.Id is only valid until Dispose, so capture
            // the int inside the using scope before releasing.
            using var p = Process.Start(psi);
            if (p is null)
            {
                ShowOsd("Failed to start syncthing process", 5000);
                return false;
            }
            _launchedPid = p.Id;
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
        // Graceful: REST API shutdown. Syncthing acks the shutdown POST as soon as
        // it queues the shutdown internally — it doesn't wait for full process
        // teardown before responding — so a 2s timeout is plenty. The 5s default
        // is what used to make the tray close feel frozen when a poll-tick held
        // the keep-alive connection.
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            try
            {
                _api.Post("/rest/system/shutdown", timeoutMs: 2000);
                // Short wait for the process to actually exit. 20×100ms = 2s cap;
                // if Syncthing's still around after that we fall through to kill.
                for (int i = 0; i < 20; i++)
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
        if (_disposed) return;
        if (_osd.InvokeRequired)
        {
            // BeginInvoke — fire-and-forget by design. A synchronous Invoke from a
            // pool thread can race with the UI thread's Dispose path during shutdown.
            try { _osd.BeginInvoke(() => { if (!_disposed) _osd.ShowMessage(text, durationMs); }); }
            catch (ObjectDisposedException) { /* tray is exiting */ }
            catch (InvalidOperationException) { /* handle not yet created */ }
        }
        else
        {
            _osd.ShowMessage(text, durationMs);
        }
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
        // Intentional shutdown is by definition stable — clear the sentinel so the
        // next launch doesn't falsely warn about a crash that never happened.
        UpdateDialog.TryDeleteCrashSentinel();

        // Stop timers BEFORE the shutdown call. Otherwise a poll-tick that fires
        // mid-shutdown can queue a GET on the HttpClient's single keep-alive
        // connection to localhost:8384, and the /rest/system/shutdown POST waits
        // behind it (HTTP/1.1 serializes per-connection). That's what turns a
        // normally-snappy close into a several-second freeze.
        try { _pollTimer.Stop(); } catch { /* already disposed */ }
        try { _iconTimer.Stop(); } catch { /* already disposed */ }
        try { _pauseTimer.Stop(); } catch { /* already disposed */ }

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
                // Unsubscribe first — SystemEvents holds a strong reference that
                // otherwise keeps this context alive past tray exit and fires
                // PowerModeChanged on a disposed state.
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;

                _iconTimer.Stop();
                _iconTimer.Dispose();
                _pollTimer.Stop();
                _pollTimer.Dispose();
                _pauseTimer.Stop();
                _pauseTimer.Dispose();

                // One-shot timers (startup/first-run/stability) that haven't
                // ticked yet — stop + dispose so their native timer handles drop
                // now rather than on the finalizer thread. The Tick handlers
                // themselves Remove from this list on fire, so by exit this is
                // only non-empty when we exited before the timer elapsed.
                foreach (var t in _oneShotTimers)
                {
                    try { t.Stop(); t.Dispose(); } catch { /* already disposed */ }
                }
                _oneShotTimers.Clear();

                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();

                _osd.Dispose();
                _messageWindow.Dispose();

                _api.Dispose();
                _syncIcon.Dispose();
                _pauseIcon.Dispose();
                _loadFoldersGate.Dispose();
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

    private sealed record FolderInfo(string Id, string Label, string Path, string[] DeviceIds);
}
