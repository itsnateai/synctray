using System.Text;

namespace SyncthingTray;

/// <summary>
/// Manages INI-based settings for SyncthingTray.
/// </summary>
internal sealed class AppConfig
{
    public static readonly string Version = typeof(AppConfig).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Click action values: "webui", "rescan", "pause", "none"
    public static readonly string[] ClickActions = ["Open Web UI", "Rescan Now", "Pause/Resume", "Do Nothing"];
    public static readonly string[] ClickActionValues = ["webui", "rescan", "pause", "none"];

    // Settings
    public string DblClickAction { get; set; } = "webui";
    public string MiddleClickAction { get; set; } = "pause";
    public bool RunOnStartup { get; set; }
    public bool StartBrowser { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string SyncExe { get; set; } = string.Empty;
    public string WebUI { get; set; } = "http://localhost:8384";
    public int StartupDelay { get; set; } = 20;
    public bool NetworkAutoPause { get; set; }
    public bool AutoCheckUpdates { get; set; }
    public bool SoundNotifications { get; set; }
    public bool StopOnExit { get; set; }

    public string SettingsFilePath { get; }
    public bool IsPortable { get; }
    public bool IsFirstRun { get; }

    /// <summary>
    /// Tracks which keys were explicitly present in the INI file.
    /// Only these keys are written back on Save(), so new defaults
    /// in future versions aren't overridden by stale saved values.
    /// </summary>
    private readonly HashSet<string> _configuredKeys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public AppConfig(string appDirectory)
    {
        SettingsFilePath = Path.Combine(appDirectory, "SyncthingTray.ini");
        SyncExe = Path.Combine(appDirectory, "syncthing.exe");

        // Portable mode detection
        try
        {
            var root = Path.GetPathRoot(appDirectory);
            if (root is not null)
            {
                var driveInfo = new DriveInfo(root);
                IsPortable = driveInfo.DriveType == DriveType.Removable;
            }
        }
        catch { /* ignore drive detection failures */ }

        IsFirstRun = !File.Exists(SettingsFilePath);
        if (!IsFirstRun)
            Load();
    }

    public void Load()
    {
        try
        {
            var lines = File.ReadAllLines(SettingsFilePath, Utf8NoBom);
            var settings = ParseIni(lines);

            _configuredKeys.Clear();
            foreach (var key in settings.Keys)
                _configuredKeys.Add(key);

            // New action-based settings (v2.1+)
            DblClickAction = GetString(settings, "DblClickAction", string.Empty);
            MiddleClickAction = GetString(settings, "MiddleClickAction", string.Empty);

            // Backward compat: migrate old boolean settings if new keys absent
            if (string.IsNullOrEmpty(DblClickAction))
                DblClickAction = GetBool(settings, "DblClickOpen", true) ? "webui" : "none";
            if (string.IsNullOrEmpty(MiddleClickAction))
                MiddleClickAction = GetBool(settings, "MiddleClickEnabled", true) ? "pause" : "none";

            RunOnStartup = GetBool(settings, "RunOnStartup", false);
            StartBrowser = GetBool(settings, "StartBrowser", false);
            ApiKey = GetString(settings, "ApiKey", string.Empty);
            NetworkAutoPause = GetBool(settings, "NetworkAutoPause", false);
            AutoCheckUpdates = GetBool(settings, "AutoCheckUpdates", false);
            SoundNotifications = GetBool(settings, "SoundNotifications", false);
            StopOnExit = GetBool(settings, "StopOnExit", false);

            var exe = GetString(settings, "SyncExe", string.Empty);
            if (!string.IsNullOrEmpty(exe))
                SyncExe = exe;

            var webUi = GetString(settings, "WebUI", string.Empty);
            if (!string.IsNullOrEmpty(webUi))
                WebUI = webUi;

            if (int.TryParse(GetString(settings, "StartupDelay", "0"), out int delay))
                StartupDelay = delay;
        }
        catch { /* settings file locked or corrupt — use defaults */ }
    }

    /// <summary>
    /// Mark a key as explicitly configured (called from UI save).
    /// </summary>
    public void MarkConfigured(string key) => _configuredKeys.Add(key);

    /// <summary>
    /// Mark all current settings as configured (user saved via Settings dialog).
    /// </summary>
    public void MarkAllConfigured()
    {
        string[] allKeys = ["DblClickAction", "MiddleClickAction", "RunOnStartup", "StartBrowser", "ApiKey",
            "SyncExe", "WebUI", "StartupDelay", "NetworkAutoPause", "AutoCheckUpdates", "SoundNotifications", "StopOnExit"];
        foreach (var key in allKeys)
            _configuredKeys.Add(key);
    }

    public bool Save()
    {
        // When saving from the UI, all keys are explicitly configured
        MarkAllConfigured();

        var sb = new StringBuilder();
        sb.AppendLine("[Settings]");
        sb.AppendLine($"DblClickAction={DblClickAction}");
        sb.AppendLine($"MiddleClickAction={MiddleClickAction}");
        sb.AppendLine($"RunOnStartup={BoolToStr(RunOnStartup)}");
        sb.AppendLine($"StartBrowser={BoolToStr(StartBrowser)}");
        sb.AppendLine($"ApiKey={ApiKey}");
        sb.AppendLine($"SyncExe={SyncExe}");
        sb.AppendLine($"WebUI={WebUI}");
        sb.AppendLine($"StartupDelay={StartupDelay}");
        sb.AppendLine($"NetworkAutoPause={BoolToStr(NetworkAutoPause)}");
        sb.AppendLine($"AutoCheckUpdates={BoolToStr(AutoCheckUpdates)}");
        sb.AppendLine($"SoundNotifications={BoolToStr(SoundNotifications)}");
        sb.AppendLine($"StopOnExit={BoolToStr(StopOnExit)}");

        try
        {
            var tmpPath = SettingsFilePath + ".tmp";
            File.WriteAllText(tmpPath, sb.ToString(), Utf8NoBom);
            File.Move(tmpPath, SettingsFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseIni(string[] lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '[')
                continue;
            int eq = trimmed.IndexOf('=');
            if (eq > 0)
                dict[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
        }
        return dict;
    }

    private static bool GetBool(Dictionary<string, string> d, string key, bool def)
        => d.TryGetValue(key, out var v) ? v == "1" : def;

    private static string GetString(Dictionary<string, string> d, string key, string def)
        => d.TryGetValue(key, out var v) ? v : def;

    private static string BoolToStr(bool b) => b ? "1" : "0";

    public static int ActionValueToIndex(string value)
    {
        int idx = Array.IndexOf(ClickActionValues, value);
        return idx >= 0 ? idx : 0;
    }

    public static string ActionIndexToValue(int index)
        => index >= 0 && index < ClickActionValues.Length ? ClickActionValues[index] : "none";
}
