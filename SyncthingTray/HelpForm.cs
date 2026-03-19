using System.Diagnostics;

namespace SyncthingTray;

/// <summary>
/// Dark-themed Help window matching the AHK version.
/// </summary>
internal sealed class HelpForm : Form
{
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _btnFont;
    private bool _disposed;

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DividerColor = Color.FromArgb(0x40, 0x40, 0x50);

    public HelpForm()
    {
        _titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        _bodyFont = new Font("Segoe UI", 9f);
        _btnFont = new Font("Segoe UI", 8f);

        Text = "SyncthingTray Help";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(400, 380);
        BackColor = BgColor;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;

        var lblTitle = new Label
        {
            Text = $"SyncthingTray v{AppConfig.Version}",
            Font = _titleFont,
            ForeColor = FgColor,
            Location = new Point(16, 14),
            AutoSize = true,
        };
        Controls.Add(lblTitle);

        var divider = new Label
        {
            Location = new Point(0, 36),
            Size = new Size(400, 1),
            BackColor = DividerColor,
        };
        Controls.Add(divider);

        const string helpText =
            "Tray Icon Actions:\r\n" +
            "  Double-click & Middle-click \u2014 configurable in Settings\r\n" +
            "    (Open Web UI, Force Rescan, Pause/Resume, or None)\r\n" +
            "  Right-click \u2014 Open context menu\r\n" +
            "\r\n" +
            "Context Menu:\r\n" +
            "  Synced Folders \u2014 browse synced folder list\r\n" +
            "  Force Rescan Now \u2014 trigger rescan on all folders\r\n" +
            "  Pause/Resume \u2014 toggle syncing on/off\r\n" +
            "  Start/Stop/Restart \u2014 control Syncthing process\r\n" +
            "\r\n" +
            "Settings:\r\n" +
            "  API Key \u2014 required for status, pause/resume, and\r\n" +
            "    discovery settings. Find in Syncthing Web UI\r\n" +
            "    under Actions > Settings > API Key.\r\n" +
            "  Check Config \u2014 validate settings against Syncthing\r\n" +
            "\r\n" +
            "Status:\r\n" +
            "  Tray tooltip shows sync state, progress, and\r\n" +
            "  connected device count (e.g. 1/2 devices).\r\n" +
            "\r\n" +
            "Syncthing docs: docs.syncthing.net";

        var lblHelp = new Label
        {
            Text = helpText,
            Font = _bodyFont,
            ForeColor = FgColor,
            Location = new Point(16, 46),
            Size = new Size(360, 280),
        };
        Controls.Add(lblHelp);

        var btnDocs = new Button
        {
            Text = "Syncthing Docs",
            Font = _btnFont,
            Location = new Point(16, 336),
            Size = new Size(120, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
        };
        btnDocs.Click += (_, _) =>
        {
            using var p = Process.Start(new ProcessStartInfo("https://docs.syncthing.net") { UseShellExecute = true });
        };
        Controls.Add(btnDocs);

        var btnClose = new Button
        {
            Text = "Close",
            Font = _btnFont,
            Location = new Point(260, 336),
            Size = new Size(120, 26),
            FlatStyle = FlatStyle.Flat,
            ForeColor = FgColor,
            BackColor = BgColor,
            DialogResult = DialogResult.Cancel,
        };
        btnClose.Click += (_, _) => Close();
        Controls.Add(btnClose);

        CancelButton = btnClose;
        AcceptButton = btnClose;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _titleFont.Dispose();
                _bodyFont.Dispose();
                _btnFont.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
