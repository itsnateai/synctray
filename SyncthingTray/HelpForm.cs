using System.Diagnostics;

namespace SyncthingTray;

/// <summary>
/// Dark-themed Help window matching the AHK version.
/// </summary>
internal sealed class HelpForm : Form
{
    private bool _disposed;

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color DividerColor = Color.FromArgb(0x40, 0x40, 0x50);

    public HelpForm()
    {
        Text = "SyncthingTray Help";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(400, 380);
        BackColor = BgColor;
        ShowInTaskbar = false;

        var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        var bodyFont = new Font("Segoe UI", 9f);
        var btnFont = new Font("Segoe UI", 8f);

        var lblTitle = new Label
        {
            Text = $"SyncthingTray v{AppConfig.Version}",
            Font = titleFont,
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
            "  Double-click \u2014 Open Syncthing Web UI\r\n" +
            "  Middle-click \u2014 Toggle pause/resume (if enabled in Settings)\r\n" +
            "  Right-click \u2014 Open menu\r\n" +
            "\r\n" +
            "Settings:\r\n" +
            "  API Key \u2014 Required for pause/resume, status polling,\r\n" +
            "    and graceful shutdown. Find in Syncthing Web UI\r\n" +
            "    under Actions > Settings > API Key.\r\n" +
            "\r\n" +
            "Status Icons:\r\n" +
            "  Sync icon \u2014 Syncthing is running and syncing\r\n" +
            "  Pause icon \u2014 Syncthing is paused or stopped\r\n" +
            "\r\n" +
            "Tooltip shows: status, sync progress, and\r\n" +
            "connected device count (e.g. 2/3 devices).\r\n" +
            "\r\n" +
            "Syncthing docs: docs.syncthing.net";

        var lblHelp = new Label
        {
            Text = helpText,
            Font = bodyFont,
            ForeColor = FgColor,
            Location = new Point(16, 46),
            Size = new Size(360, 280),
        };
        Controls.Add(lblHelp);

        var btnDocs = new Button
        {
            Text = "Syncthing Docs",
            Font = btnFont,
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
            Font = btnFont,
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

        // Store fonts for disposal
        Tag = new Font[] { titleFont, bodyFont, btnFont };
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && Tag is Font[] fonts)
            {
                foreach (var f in fonts)
                    f.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
