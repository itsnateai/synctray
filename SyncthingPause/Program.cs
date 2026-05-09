using System.Diagnostics;

namespace SyncthingPause;

internal static class Program
{
    internal static bool KilledPreviousInstance { get; private set; }
    internal static bool KilledRenamePredecessor { get; private set; }

    /// <summary>
    /// True when the last boot crashed before clearing its crash sentinel AND a
    /// .old backup exists — the previous update likely broke something and the
    /// user should be told. Surfaced via OSD from TrayApplicationContext.
    /// </summary>
    internal static bool CrashSentinelPersisted { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // v3.0.0 rename bridge: SyncthingTray.exe (rename predecessor) uses a
        // different single-instance mutex name, so the same-name kill loop
        // below won't find it. Kill any SyncthingTray instance in our session
        // FIRST so it releases its pause.dat lock before TryMigratePauseDat
        // tries to copy it, and so we don't both fight over syncthing.exe
        // lifecycle. One-time bridge — safe to remove in v4 once the rename
        // window has closed and no installs are still running v2.x.
        KillRenamePredecessor();

        using var mutex = new Mutex(true, "SyncthingPause_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "SyncthingPause");
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

    /// <summary>
    /// v3.0.0: kill any SyncthingTray.exe (rename predecessor) processes in our
    /// session so the rename install can safely take over syncthing.exe
    /// lifecycle and migrate pause.dat. Same-session-only so we don't reach
    /// across user accounts on a multi-user box.
    ///
    /// Verifier round (2026-05-08): added <c>WaitForExit</c> after <c>Kill</c>.
    /// On Windows, <c>Process.Kill</c> sends <c>TerminateProcess</c> which is
    /// asynchronous w.r.t. handle release — without the wait, <c>TryMigratePauseDat</c>
    /// can fire <c>File.Copy</c> on a still-locked <c>pause.dat</c>. The retry
    /// backoff in <c>TryMigratePauseDat</c> is the safety net, but the wait
    /// here makes the happy path actually happy.
    /// </summary>
    private static void KillRenamePredecessor()
    {
        int currentSession;
        try { currentSession = Process.GetCurrentProcess().SessionId; }
        catch { return; /* if we can't read our own session, abort the bridge */ }

        Process[] predecessors;
        try { predecessors = Process.GetProcessesByName("SyncthingTray"); }
        catch { return; /* enumeration ACL failure — defer to user */ }

        foreach (var p in predecessors)
        {
            using (p)
            {
                int theirSession;
                try { theirSession = p.SessionId; }
                catch { continue; /* zombie / vanished — skip */ }
                if (theirSession != currentSession) continue;
                try
                {
                    p.Kill();
                    // Wait up to 2 s for the kernel to release file handles
                    // (mainly pause.dat). Don't propagate WaitForExit failure —
                    // a non-zero exit is fine; what matters is "process gone".
                    try { p.WaitForExit(2000); } catch { /* timeout / already gone */ }
                    KilledRenamePredecessor = true;
                }
                catch { /* already exiting / Defender hold / elevation mismatch — best effort */ }
            }
        }
    }
}
