using System.Diagnostics;

namespace SyncthingTray;

internal static class Program
{
    internal static bool KilledPreviousInstance { get; private set; }

    [STAThread]
    static void Main()
    {
        // Single-instance enforcement via named Mutex
        using var mutex = new Mutex(true, "SyncthingTray_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running — kill it and take over
            string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "SyncthingTray");
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    if (p.Id != Environment.ProcessId)
                    {
                        try { p.Kill(); KilledPreviousInstance = true; } catch { /* already exiting */ }
                    }
                }
            }

            // Wait briefly for the old instance to release the mutex
            if (!mutex.WaitOne(3000))
                return; // Could not acquire — bail out
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
