using System.Runtime.InteropServices;

namespace SyncthingTray;

/// <summary>
/// Manages the startup shortcut (.lnk) in the user's Startup folder.
/// Uses WScript.Shell COM for .lnk creation.
/// </summary>
internal static class StartupShortcut
{
    private static string LnkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "SyncthingTray.lnk");

    public static void Apply(bool enable, string iconPath)
    {
        var lnk = LnkPath;
        if (enable)
        {
            var target = Environment.ProcessPath ?? "SyncthingTray.exe";
            var workDir = Path.GetDirectoryName(target) ?? string.Empty;
            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return;
                shell = Activator.CreateInstance(shellType);
                if (shell is null) return;

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, [lnk]);

                if (shortcut is null) return;
                var scType = shortcut.GetType();
                scType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, [target]);
                scType.InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, [workDir]);
                scType.InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, ["SyncthingTray"]);
                if (File.Exists(iconPath))
                {
                    scType.InvokeMember("IconLocation",
                        System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{iconPath},0"]);
                }
                scType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut is not null)
                    Marshal.ReleaseComObject(shortcut);
                if (shell is not null)
                    Marshal.ReleaseComObject(shell);
            }
        }
        else
        {
            try
            {
                if (File.Exists(lnk))
                    File.Delete(lnk);
            }
            catch { /* shortcut locked or already gone */ }
        }
    }
}
