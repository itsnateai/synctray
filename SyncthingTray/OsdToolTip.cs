namespace SyncthingTray;

/// <summary>
/// Borderless, topmost form that acts as a transient tooltip near the cursor.
/// Disposes its auto-hide timer properly.
/// </summary>
internal sealed class OsdToolTip : Form
{
    private readonly Label _label;
    private readonly Font _labelFont;
    private readonly System.Windows.Forms.Timer _hideTimer;
    private bool _disposed;

    private static readonly Color BgColor = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FgColor = Color.FromArgb(0xCD, 0xD6, 0xF3);

    public OsdToolTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BgColor;
        AutoSize = false;
        Opacity = 0.95;
        Padding = new Padding(1);

        _labelFont = new Font("Segoe UI", 9f);
        _label = new Label
        {
            AutoSize = true,
            ForeColor = FgColor,
            BackColor = BgColor,
            Font = _labelFont,
            Location = new Point(8, 6),
            MaximumSize = new Size(400, 0),
        };
        Controls.Add(_label);

        _hideTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _hideTimer.Tick += (_, _) => HideOsd();

        // Pre-warm the window handle off-screen so first ShowMessage doesn't flicker
        Location = new Point(-9999, -9999);
        _label.Text = " ";
        Size = new Size(1, 1);
        Show();
        Hide();
    }

    public void ShowMessage(string text, int durationMs = 3000)
    {
        if (_disposed) return;

        _hideTimer.Stop();

        _label.Text = text;

        // Size to content
        var textSize = TextRenderer.MeasureText(text, _label.Font, new Size(400, 0), TextFormatFlags.WordBreak);
        Size = new Size(textSize.Width + 20, textSize.Height + 16);

        // Position just above the system tray (bottom-right of working area)
        var screen = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        var pos = new Point(
            screen.Right - Width - 8,
            screen.Bottom - Height - 8
        );

        Location = pos;

        if (Visible)
        {
            Invalidate();
            Update();
        }
        else
        {
            Show();
        }

        _hideTimer.Interval = durationMs;
        _hideTimer.Start();
    }

    private void HideOsd()
    {
        _hideTimer.Stop();
        if (!_disposed)
            Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _hideTimer.Stop();
                _hideTimer.Dispose();
                _label.Dispose();
                _labelFont.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(0x44, 0x44, 0x5A), 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x00000008 | 0x08000000;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;
}
