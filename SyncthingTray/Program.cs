using System.Diagnostics;

namespace SyncthingTray;

internal static class Program
{
    internal static bool KilledPreviousInstance { get; private set; }

    /// <summary>
    /// True when the last boot crashed before clearing its crash sentinel AND a
    /// .old backup exists — the previous update likely broke something and the
    /// user should be told. Surfaced via OSD from TrayApplicationContext.
    /// </summary>
    internal static bool CrashSentinelPersisted { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "SyncthingTray_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "SyncthingTray");
            int mySession = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;

                    // Only replace same-session processes; on a multi-user machine we
                    // have no authority to kill another user's tray instance (Kill would
                    // throw AccessDenied) and even if we did it would surprise that user.
                    int theirSession;
                    try { theirSession = p.SessionId; }
                    catch { theirSession = -1; }
                    if (theirSession != mySession) continue;

                    try { p.Kill(); KilledPreviousInstance = true; } catch { /* already exiting */ }
                }
            }

            try
            {
                if (!mutex.WaitOne(3000))
                    return;
            }
            catch (AbandonedMutexException)
            {
                // Old process was killed before releasing the mutex — we now own it
            }
        }

        bool isAfterUpdate = args.Contains("--after-update");

        // Crash-sentinel check: if a sentinel persists and this is NOT the immediate
        // post-update boot (where the sentinel is expected to still be there), the
        // previous run crashed before proving stability. Record for TrayContext to surface.
        if (!isAfterUpdate && File.Exists(UpdateDialog.CrashSentinelPath))
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            if (File.Exists(exePath + ".old"))
            {
                CrashSentinelPersisted = true;
            }
            // Clear regardless so a one-time crash doesn't shout forever.
            UpdateDialog.TryDeleteCrashSentinel();
        }

        // Torn-state recovery (exe missing, .old present) must run before we launch
        // the tray. The proactive .old cleanup is DEFERRED to the stability timer —
        // deleting .old here on an --after-update boot would defeat the entire crash
        // sentinel feature, because a new-version crash within 30s would leave us
        // with no backup to point the user at.
        UpdateDialog.RecoverFromTornUpdate();

        ApplicationConfiguration.Initialize();

        if (isAfterUpdate)
            UpdateDialog.ShowUpdateToast();

        Application.Run(new TrayApplicationContext());
    }
}
