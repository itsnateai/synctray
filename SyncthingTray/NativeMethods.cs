using System.Runtime.InteropServices;

namespace SyncthingTray;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Beep(uint dwFreq, uint dwDuration);
}
