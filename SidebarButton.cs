namespace HWMonitor;

/// <summary>A flat, hoverable navigation item used in the main window's sidebar.</summary>
sealed class SidebarButton : Control
{
    private static readonly Color IdleColor = Color.Transparent;
    private static readonly Color HoverColor = Color.FromArgb(14, 255, 255, 255);
    private static readonly Color SelectedColor = Color.FromArgb(24, 255, 255, 255);

    private bool _hovering;
    private bool _selected;

    public string Glyph { get; set; } = "";
    public Color AccentColor { get; set; } = Color.FromArgb(41, 182, 246);

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public SidebarButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 42;
        ForeColor = Color.FromArgb(200, 200, 205);
        Font = new Font("Segoe UI", 9.75f);
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        Color overlay = _selected ? SelectedColor : (_hovering ? HoverColor : IdleColor);
        using (var bgBrush = new SolidBrush(overlay))
        {
            g.FillRectangle(bgBrush, ClientRectangle);
        }

        if (_selected)
        {
            using var accentBrush = new SolidBrush(AccentColor);
            g.FillRectangle(accentBrush, 0, 6, 3, Height - 12);
        }

        using var textBrush = new SolidBrush(_selected ? Color.White : ForeColor);
        var glyphFont = new Font("Segoe UI Emoji", 11f);
        float glyphWidth = 30;
        var glyphRect = new RectangleF(14, 0, glyphWidth, Height);
        var textRect = new RectangleF(14 + glyphWidth, 0, Width - glyphWidth - 20, Height);
        var format = new StringFormat { LineAlignment = StringAlignment.Center };

        g.DrawString(Glyph, glyphFont, textBrush, glyphRect, format);
        g.DrawString(Text, Font, textBrush, textRect, format);
        glyphFont.Dispose();
    }
}
