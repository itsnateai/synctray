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
    }

    public void ShowMessage(string text, int durationMs = 3000)
    {
        if (_disposed) return;

        _hideTimer.Stop();

        _label.Text = text;

        // Size to content
        var textSize = TextRenderer.MeasureText(text, _label.Font, new Size(400, 0), TextFormatFlags.WordBreak);
        Size = new Size(textSize.Width + 20, textSize.Height + 16);

        // Position near cursor, offset so it doesn't steal focus
        var pos = Cursor.Position;
        pos.Offset(16, 16);

        // Keep on screen
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (pos.X + Width > screen.Right)
            pos.X = screen.Right - Width;
        if (pos.Y + Height > screen.Bottom)
            pos.Y = screen.Bottom - Height;

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
