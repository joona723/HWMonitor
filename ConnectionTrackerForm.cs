namespace HWMonitor;

/// <summary>Live bandwidth graphs for a set of connections chosen via "Track Bandwidth" on the Connections tab.</summary>
sealed class ConnectionTrackerForm : Form
{
    private const int GraphHeight = 150;
    private const int GraphSpacing = 14;

    private static readonly Color WindowBg = Color.FromArgb(18, 19, 21);
    private static readonly Color TextPrimary = Color.FromArgb(230, 230, 232);

    private static readonly Color[] Palette =
    [
        Color.FromArgb(41, 182, 246),
        Color.FromArgb(102, 187, 106),
        Color.FromArgb(255, 167, 38),
        Color.FromArgb(179, 136, 255),
        Color.FromArgb(255, 112, 112),
        Color.FromArgb(255, 213, 79),
    ];

    private readonly NetworkEtwMonitor _etwMonitor;
    private readonly List<(ConnectionInfo Connection, PerformanceGraph Graph)> _tracked = [];
    private readonly Panel _graphsPanel;
    private readonly System.Windows.Forms.Timer _timer;

    public ConnectionTrackerForm(NetworkEtwMonitor etwMonitor, IReadOnlyList<ConnectionInfo> connections, Func<int, string> processNameFor)
    {
        _etwMonitor = etwMonitor;

        Text = connections.Count == 1 ? "Bandwidth Tracker" : $"Bandwidth Tracker ({connections.Count} connections)";
        Size = new Size(620, 720);
        MinimumSize = new Size(360, 260);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = WindowBg;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9f);
        Icon = SystemIcons.Application;

        if (!etwMonitor.IsRunning)
        {
            Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 40,
                Padding = new Padding(16, 12, 16, 0),
                ForeColor = Color.FromArgb(255, 138, 128),
                Text = "Per-connection network tracking is unavailable (the ETW session isn't running). Numbers below will stay at zero.",
            });
        }

        // Positioned and sized by hand (instead of a FlowLayoutPanel) so each graph can be
        // stretched to the panel's full width whenever the window is resized - see LayoutGraphs.
        _graphsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16),
            BackColor = WindowBg,
        };
        _graphsPanel.Resize += (_, _) => LayoutGraphs();

        int colorIndex = 0;
        foreach (ConnectionInfo c in connections)
        {
            string remote = c.RemotePort == 0 ? "*" : $"{c.RemoteAddress}:{c.RemotePort}";
            var graph = new PerformanceGraph(180)
            {
                Title = processNameFor(c.Pid),
                SubText = $"PID {c.Pid}  •  {c.Protocol}  •  {c.LocalAddress}:{c.LocalPort} → {remote}",
                LineColor = Palette[colorIndex++ % Palette.Length],
                AutoScale = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _graphsPanel.Controls.Add(graph);
            _tracked.Add((c, graph));
        }

        Controls.Add(_graphsPanel);
        LayoutGraphs();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshGraphs();
        _timer.Start();
        RefreshGraphs();
    }

    private void LayoutGraphs()
    {
        int width = Math.Max(_graphsPanel.ClientSize.Width - _graphsPanel.Padding.Horizontal, 200);
        int y = _graphsPanel.Padding.Top;
        foreach ((_, PerformanceGraph graph) in _tracked)
        {
            graph.SetBounds(_graphsPanel.Padding.Left, y, width, GraphHeight);
            y += GraphHeight + GraphSpacing;
        }
    }

    private void RefreshGraphs()
    {
        foreach ((ConnectionInfo connection, PerformanceGraph graph) in _tracked)
        {
            (long rx, long tx) = _etwMonitor.GetRatesForConnection(connection);
            graph.ValueText = $"{NetRateGlyphs.Download} {MainForm.FormatBytesPerSec(rx)}   {NetRateGlyphs.Upload} {MainForm.FormatBytesPerSec(tx)}";
            graph.AddSample(rx + tx);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
