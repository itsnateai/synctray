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

    /// <summary>
    /// Enable or disable the Startup-folder shortcut. Returns false on any failure
    /// (COM unavailable, file locked, group policy block) so callers can tell the
    /// user instead of silently accepting a "Run on startup" setting that never fires.
    /// </summary>
    public static bool Apply(bool enable, string iconPath)
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
                if (shellType is null)
                {
                    TrayLog.Warn("StartupShortcut: WScript.Shell ProgID not registered.");
                    return false;
                }
                shell = Activator.CreateInstance(shellType);
                if (shell is null)
                {
                    TrayLog.Warn("StartupShortcut: Activator returned null for WScript.Shell.");
                    return false;
                }

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, [lnk]);

                if (shortcut is null)
                {
                    TrayLog.Warn("StartupShortcut: CreateShortcut returned null.");
                    return false;
                }
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
                return true;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("StartupShortcut.Apply(enable) threw: " + ex.Message);
                return false;
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
                return true;
            }
            catch (Exception ex)
            {
                TrayLog.Warn("StartupShortcut delete failed: " + ex.Message);
                return false;
            }
        }
    }
}
