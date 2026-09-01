using System.Drawing.Drawing2D;
using System.Linq;

namespace HWMonitor;

/// <summary>A row of small per-core bars (temperature or clock speed), drawn as a rounded card.</summary>
sealed class CoreMetricStrip : Control
{
    private IReadOnlyList<CoreReading> _values = [];

    public string Title { get; set; } = "";
    public string ValueFormat { get; set; } = "0";
    public double MaxValue { get; set; } = 100;
    public Func<float, Color>? ColorForValue { get; set; }

    private static readonly Color CardColor = Color.FromArgb(32, 33, 37);
    private static readonly Color BorderColor = Color.FromArgb(50, 52, 58);
    private static readonly Color TitleColor = Color.FromArgb(150, 152, 158);
    private static readonly Color BarTrackColor = Color.FromArgb(44, 45, 50);
    private static readonly Color EmptyColor = Color.FromArgb(90, 92, 98);

    public CoreMetricStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Color.Gainsboro;
        MinimumSize = new Size(160, 90);
    }

    public IReadOnlyList<CoreReading> Values
    {
        get => _values;
        set { _values = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.Black);

        var cardRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath cardPath = RoundedRect(cardRect, 10))
        {
            using var cardBrush = new SolidBrush(CardColor);
            g.FillPath(cardBrush, cardPath);
            using var borderPen = new Pen(BorderColor, 1f);
            g.DrawPath(borderPen, cardPath);
        }

        const int padX = 16;
        const int headerTop = 12;

        using (var titleBrush = new SolidBrush(TitleColor))
        using (var titleFont = new Font("Segoe UI Semibold", 9f))
        {
            g.DrawString(Title.ToUpperInvariant(), titleFont, titleBrush, padX, headerTop);
        }

        var chartRect = new Rectangle(padX, 36, Width - padX * 2, Height - 36 - 22);
        if (chartRect.Width <= 4 || chartRect.Height <= 4)
        {
            return;
        }

        if (_values.Count == 0)
        {
            using var naBrush = new SolidBrush(TitleColor);
            using var naFont = new Font("Segoe UI", 9f);
            g.DrawString("n/a", naFont, naBrush, padX, chartRect.Top + chartRect.Height / 2f - 8);
            return;
        }

        double max = Math.Max(MaxValue, _values.Max(v => v.Value));
        int count = _values.Count;
        float gap = 4f;
        float barWidth = Math.Max((chartRect.Width - gap * (count - 1)) / count, 2f);

        using var valueFont = new Font("Segoe UI", 7f);
        using var labelFont = new Font("Segoe UI", 7f);
        using var labelBrush = new SolidBrush(TitleColor);

        for (int i = 0; i < count; i++)
        {
            float x = chartRect.Left + i * (barWidth + gap);
            float normalized = (float)Math.Clamp(_values[i].Value / max, 0.02, 1);
            float barHeight = chartRect.Height * normalized;
            var trackRect = new RectangleF(x, chartRect.Top, barWidth, chartRect.Height);
            var barRect = new RectangleF(x, chartRect.Bottom - barHeight, barWidth, barHeight);

            using (var trackBrush = new SolidBrush(BarTrackColor))
            {
                g.FillRectangle(trackBrush, trackRect);
            }

            Color barColor = ColorForValue?.Invoke(_values[i].Value) ?? EmptyColor;
            using (var barBrush = new SolidBrush(barColor))
            {
                g.FillRectangle(barBrush, barRect);
            }

            string valueText = _values[i].Value.ToString(ValueFormat);
            SizeF measured = g.MeasureString(valueText, valueFont);
            if (measured.Width <= barWidth + 6)
            {
                using var valueBrush = new SolidBrush(Color.White);
                g.DrawString(valueText, valueFont, valueBrush, x + barWidth / 2f - measured.Width / 2f, barRect.Top - 12);
            }

            string label = (i + 1).ToString();
            SizeF labelSize = g.MeasureString(label, labelFont);
            g.DrawString(label, labelFont, labelBrush, x + barWidth / 2f - labelSize.Width / 2f, chartRect.Bottom + 4);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
