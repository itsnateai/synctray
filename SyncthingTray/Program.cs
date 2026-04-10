using System.Diagnostics;

namespace SyncthingTray;

internal static class Program
{
    internal static bool KilledPreviousInstance { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "SyncthingTray_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
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
        UpdateDialog.CleanupUpdateArtifacts();

        ApplicationConfiguration.Initialize();

        if (isAfterUpdate)
            UpdateDialog.ShowUpdateToast();

        Application.Run(new TrayApplicationContext());
    }
}
