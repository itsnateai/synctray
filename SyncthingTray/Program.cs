using System.Diagnostics;

namespace SyncthingTray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Single-instance enforcement: kill previous instances
        string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "SyncthingTray");
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                if (p.Id != Environment.ProcessId)
                {
                    try { p.Kill(); } catch { /* already exiting */ }
                }
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
