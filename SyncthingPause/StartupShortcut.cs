using System.Runtime.InteropServices;

namespace SyncthingPause;

/// <summary>
/// Manages the startup shortcut (.lnk) in the user's Startup folder.
/// Uses WScript.Shell COM for .lnk creation.
/// </summary>
internal static class StartupShortcut
{
    private static string LnkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "SyncthingPause.lnk");

    /// <summary>
    /// v3.0.0 — path to the rename predecessor's startup shortcut. Used by
    /// <see cref="CleanupRenamePredecessorLnk"/> on launch so an old
    /// SyncthingTray.lnk can't auto-launch a now-replaced exe alongside us.
    /// </summary>
    private static string LegacyLnkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "SyncthingTray.lnk");

    /// <summary>
    /// v3.0.0 — does the rename predecessor's startup .lnk exist right now?
    /// Caller uses this to decide whether to re-create the new-name .lnk
    /// after <see cref="CleanupRenamePredecessorLnk"/> deletes the old one
    /// (preserves the user's RunOnStartup intent across the rename).
    /// </summary>
    public static bool LegacyRenamePredecessorLnkExists() => File.Exists(LegacyLnkPath);

    /// <summary>
    /// v3.0.0 — delete the rename predecessor's startup shortcut so a parallel
    /// auto-launch of SyncthingTray.exe doesn't fight us over syncthing.exe.
    /// Best-effort: any failure is logged at warn level. Safe to remove in v4.
    /// </summary>
    public static void CleanupRenamePredecessorLnk()
    {
        var oldLnk = LegacyLnkPath;
        if (!File.Exists(oldLnk)) return;
        try
        {
            File.Delete(oldLnk);
            TrayLog.Info("Deleted rename-predecessor SyncthingTray.lnk from Startup folder.");
        }
        catch (Exception ex)
        {
            TrayLog.Warn("Could not delete rename-predecessor SyncthingTray.lnk: " + ex.Message);
        }
    }

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
            var target = Environment.ProcessPath ?? "SyncthingPause.exe";
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
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, ["SyncthingPause"]);
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
