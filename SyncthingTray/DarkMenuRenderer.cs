namespace SyncthingTray;

/// <summary>
/// Custom ToolStripRenderer that applies the dark theme (matching Settings/Help forms)
/// to the tray context menu.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color MenuBg = Color.FromArgb(0x1E, 0x1E, 0x2E);
    private static readonly Color MenuFg = Color.FromArgb(0xCD, 0xD6, 0xF3);
    private static readonly Color HighlightBg = Color.FromArgb(0x35, 0x35, 0x50);
    private static readonly Color SeparatorColor = Color.FromArgb(0x40, 0x40, 0x50);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        var color = e.Item.Selected && e.Item.Enabled ? HighlightBg : MenuBg;
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? MenuFg : Color.FromArgb(0x60, 0x60, 0x70);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        int y = bounds.Height / 2;
        using var pen = new Pen(SeparatorColor);
        e.Graphics.DrawLine(pen, bounds.Left + 4, y, bounds.Right - 4, y);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(MenuBg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(SeparatorColor);
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, rect);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Suppress default image margin rendering (white strip on left)
        using var brush = new SolidBrush(MenuBg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => SeparatorColor;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => HighlightBg;
        public override Color MenuStripGradientBegin => MenuBg;
        public override Color MenuStripGradientEnd => MenuBg;
        public override Color MenuItemSelectedGradientBegin => HighlightBg;
        public override Color MenuItemSelectedGradientEnd => HighlightBg;
        public override Color MenuItemPressedGradientBegin => HighlightBg;
        public override Color MenuItemPressedGradientEnd => HighlightBg;
        public override Color ImageMarginGradientBegin => MenuBg;
        public override Color ImageMarginGradientMiddle => MenuBg;
        public override Color ImageMarginGradientEnd => MenuBg;
        public override Color ToolStripDropDownBackground => MenuBg;
        public override Color SeparatorDark => SeparatorColor;
        public override Color SeparatorLight => SeparatorColor;
    }
}
