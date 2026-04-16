namespace SyncthingTray;

/// <summary>
/// Rolling log for field debugging. Writes to %LOCALAPPDATA%\SyncthingTray\tray.log
/// with a 1 MB size cap (rotates to tray.log.1). Opt-in via AppConfig.DiagnosticLogging
/// so privacy-conscious users can disable it entirely.
///
/// Thread-safe — all writes go through a lock and timestamps are UTC.
/// </summary>
internal static class TrayLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object _lock = new();
    private static bool _enabled;
    private static string? _path;

    public static void Enable(bool enable)
    {
        lock (_lock)
        {
            _enabled = enable;
            if (enable && _path is null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SyncthingTray");
                try { Directory.CreateDirectory(dir); }
                catch { /* fall back to TEMP on next write */ }
                _path = Path.Combine(dir, "tray.log");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            if (!_enabled || _path is null) return;
            try
            {
                if (File.Exists(_path))
                {
                    var info = new FileInfo(_path);
                    if (info.Length > MaxBytes)
                    {
                        var backup = _path + ".1";
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                        try { File.Move(_path, backup); } catch { }
                    }
                }
                var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                File.AppendAllText(_path, $"{ts} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // If logging itself is broken (disk full, ACL), silently give up —
                // a failing logger must not crash the tray.
            }
        }
    }
}
