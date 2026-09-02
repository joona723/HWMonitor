using System.Drawing.Drawing2D;
using System.Linq;

namespace HWMonitor;

/// <summary>A small scrolling line/area chart, drawn as a rounded card in the style of a modern monitoring dashboard.</summary>
sealed class PerformanceGraph : Control
{
    private readonly Queue<double> _samples = new();
    private readonly int _capacity;

    public string Title { get; set; } = "";
    public string ValueText { get; set; } = "";
    /// <summary>When set, drawn instead of <see cref="ValueText"/> as separately-colored runs (e.g. a green download rate next to an orange upload rate).</summary>
    public (string Text, Color Color)[]? ValueTextParts { get; set; }
    public string SubText { get; set; } = "";
    public Color LineColor { get; set; } = Color.FromArgb(0, 174, 219);
    public double MaxValue { get; set; } = 100;
    public bool AutoScale { get; set; }

    private static readonly Color CardColor = Color.FromArgb(32, 33, 37);
    private static readonly Color BorderColor = Color.FromArgb(50, 52, 58);
    private static readonly Color GridColor = Color.FromArgb(44, 45, 50);
    private static readonly Color TitleColor = Color.FromArgb(150, 152, 158);
    private static readonly Color SubTextColor = Color.FromArgb(130, 132, 138);

    public PerformanceGraph(int historyCapacity = 90)
    {
        _capacity = historyCapacity;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Color.Gainsboro;
        MinimumSize = new Size(160, 100);
    }

    public void AddSample(double value)
    {
        _samples.Enqueue(value);
        while (_samples.Count > _capacity)
        {
            _samples.Dequeue();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.Black);

        var cardRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath cardPath = RoundedRect(cardRect, 10))
        {
            using (var cardBrush = new SolidBrush(CardColor))
            {
                g.FillPath(cardBrush, cardPath);
            }
            using var borderPen = new Pen(BorderColor, 1f);
            g.DrawPath(borderPen, cardPath);
        }

        const int padX = 16;
        const int headerTop = 12;

        using (var dotBrush = new SolidBrush(LineColor))
        {
            g.FillEllipse(dotBrush, padX, headerTop + 4, 8, 8);
        }
        using (var titleBrush = new SolidBrush(TitleColor))
        using (var titleFont = new Font("Segoe UI Semibold", 9f))
        {
            g.DrawString(Title.ToUpperInvariant(), titleFont, titleBrush, padX + 14, headerTop);
        }

        using (var valueFont = new Font("Segoe UI", 15f, FontStyle.Bold))
        {
            if (ValueTextParts is { Length: > 0 } parts)
            {
                float totalWidth = parts.Sum(p => g.MeasureString(p.Text, valueFont).Width);
                float x = Width - totalWidth - padX;
                foreach ((string text, Color color) in parts)
                {
                    using var partBrush = new SolidBrush(color);
                    g.DrawString(text, valueFont, partBrush, x, headerTop - 4);
                    x += g.MeasureString(text, valueFont).Width;
                }
            }
            else
            {
                using var valueBrush = new SolidBrush(Color.White);
                SizeF measured = g.MeasureString(ValueText, valueFont);
                g.DrawString(ValueText, valueFont, valueBrush, Width - measured.Width - padX, headerTop - 4);
            }
        }

        if (!string.IsNullOrEmpty(SubText))
        {
            using var subBrush = new SolidBrush(SubTextColor);
            using var subFont = new Font("Segoe UI", 8.25f);
            SizeF measured = g.MeasureString(SubText, subFont);
            g.DrawString(SubText, subFont, subBrush, Width - measured.Width - padX, headerTop + 20);
        }

        var chartRect = new Rectangle(padX, 46, Width - padX * 2, Height - 46 - 12);
        if (chartRect.Width <= 4 || chartRect.Height <= 4)
        {
            return;
        }

        using (var gridPen = new Pen(GridColor))
        {
            for (int i = 0; i <= 3; i++)
            {
                int y = chartRect.Top + chartRect.Height * i / 3;
                g.DrawLine(gridPen, chartRect.Left, y, chartRect.Right, y);
            }
        }

        if (_samples.Count < 2)
        {
            return;
        }

        double[] values = _samples.ToArray();
        double max = AutoScale ? Math.Max(values.Max() * 1.15, 1) : MaxValue;

        var points = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float x = chartRect.Left + (float)i / (_capacity - 1) * chartRect.Width;
            float normalized = (float)Math.Clamp(values[i] / max, 0, 1);
            float y = chartRect.Bottom - normalized * chartRect.Height;
            points[i] = new PointF(x, y);
        }

        var fillPoints = new PointF[points.Length + 2];
        Array.Copy(points, fillPoints, points.Length);
        fillPoints[^2] = new PointF(points[^1].X, chartRect.Bottom);
        fillPoints[^1] = new PointF(points[0].X, chartRect.Bottom);

        using (var fillBrush = new LinearGradientBrush(
            chartRect with { Height = chartRect.Height + 1 },
            Color.FromArgb(90, LineColor.R, LineColor.G, LineColor.B),
            Color.FromArgb(4, LineColor.R, LineColor.G, LineColor.B),
            LinearGradientMode.Vertical))
        {
            g.FillPolygon(fillBrush, fillPoints);
        }

        using var linePen = new Pen(LineColor, 1.8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(linePen, points);

        using var dotOuter = new SolidBrush(Color.FromArgb(60, LineColor.R, LineColor.G, LineColor.B));
        using var dotInner = new SolidBrush(LineColor);
        PointF last = points[^1];
        g.FillEllipse(dotOuter, last.X - 5, last.Y - 5, 10, 10);
        g.FillEllipse(dotInner, last.X - 2.5f, last.Y - 2.5f, 5, 5);
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
