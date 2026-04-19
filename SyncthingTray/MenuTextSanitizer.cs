namespace SyncthingTray;

/// <summary>
/// Sanitises untrusted strings (folder labels, device names) before they reach
/// a <see cref="ToolStripMenuItem.Text"/> property or a log line.
///
/// Three concerns:
///   1. WinForms treats <c>&amp;</c> as a keyboard-mnemonic prefix — a hostile
///      peer label like <c>"&amp;Dropbox"</c> would underline the D and steal
///      that accelerator on a menu open. Double it to render literally.
///   2. Control characters (CR/LF/NUL, other non-printables) corrupt the
///      rendered menu and, more importantly, inject into <c>TrayLog</c> when
///      the same string is interpolated into a diagnostic line. Strip them.
///   3. A runaway label (10 KB of Unicode) crashes menu layout. Cap length.
/// </summary>
internal static class MenuTextSanitizer
{
    public const int MaxLength = 120;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var chars = new char[Math.Min(value.Length, MaxLength) * 2];
        int w = 0;
        int emitted = 0;
        foreach (var ch in value)
        {
            if (emitted >= MaxLength) break;
            // Strip C0/C1 control characters (CR, LF, TAB, NUL, DEL, etc).
            // Tabs render but throw off menu alignment; CR/LF split log lines.
            if (ch < 0x20 || ch == 0x7F) continue;
            if (ch == '&')
            {
                chars[w++] = '&';
                chars[w++] = '&';
            }
            else
            {
                chars[w++] = ch;
            }
            emitted++;
        }
        if (w == 0) return string.Empty;
        var result = new string(chars, 0, w);
        // If we stopped mid-way through the source, hint truncation so the
        // user can tell a long label wasn't just coincidentally cut off by
        // WinForms. Only appended when we actually truncated.
        if (emitted == MaxLength && value.Length > MaxLength)
            result += "…";
        return result;
    }
}
