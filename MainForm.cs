using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace HWMonitor;

/// <summary>Task-Manager-style monitor window: live Performance graphs, a Processes list, and a Network Connections list.</summary>
sealed class MainForm : Form
{
    private static readonly Color WindowBg = Color.FromArgb(18, 19, 21);
    private static readonly Color SidebarBg = Color.FromArgb(14, 15, 17);
    private static readonly Color CardBg = Color.FromArgb(26, 27, 30);
    private static readonly Color BorderColor = Color.FromArgb(42, 43, 48);
    private static readonly Color HeaderBg = Color.FromArgb(14, 15, 17);
    private static readonly Color TextPrimary = Color.FromArgb(230, 230, 232);
    private static readonly Color TextSecondary = Color.FromArgb(140, 142, 148);

    private static readonly Color AccentCpu = Color.FromArgb(41, 182, 246);
    private static readonly Color AccentMem = Color.FromArgb(179, 136, 255);
    private static readonly Color AccentNet = Color.FromArgb(102, 187, 106);
    private static readonly Color AccentGpu = Color.FromArgb(255, 167, 38);

    private readonly SystemStatsService _systemStats = new();
    private readonly ProcessMonitorService _processMonitor = new();
    private readonly NetworkConnectionsService _connectionsService = new();
    private readonly NetworkEtwMonitor _etwMonitor = new();
    private readonly HardwareMonitor _hardwareMonitor = new();
    private readonly ProcessPriorityStore _priorityStore = ProcessPriorityStore.Load();
    private readonly TraySettings _settings = TraySettings.Load();
    private readonly System.Windows.Forms.Timer _timer;

    private static readonly (string Label, int Ms)[] RefreshIntervalChoices =
    [
        ("0.25s", 250),
        ("0.5s", 500),
        ("1s", 1000),
        ("2s", 2000),
        ("5s", 5000),
        ("10s", 10000),
    ];

    private readonly PerformanceGraph _cpuGraph;
    private readonly PerformanceGraph _memGraph;
    private readonly PerformanceGraph _netGraph;
    private readonly PerformanceGraph _gpuGraph;
    private readonly CoreMetricStrip _coreTempStrip;
    private readonly CoreMetricStrip _coreClockStrip;

    private readonly Label _powerCpuValue;
    private readonly Label _powerGpuValue;
    private readonly Label _powerTotalValue;
    private readonly Label _fanCpuValue;
    private readonly Label _fanGpuValue;
    private readonly Label _fanPumpValue;
    private readonly Label _clockValue;
    private readonly Label _gpuHotSpotValue;
    private readonly Label _gpuMemJunctionValue;

    private readonly DataGridView _processGrid;
    private readonly DataGridView _connectionGrid;
    private readonly TextBox _processSearchBox;
    private readonly TextBox _connectionSearchBox;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly Label _cpuChipValue;
    private readonly Label _memChipValue;
    private readonly Label _gpuChipValue;
    private readonly Label _netChipValue;

    private readonly Panel _performancePage;
    private readonly Panel _processesPage;
    private readonly Panel _connectionsPage;
    private readonly SidebarButton _navPerformance;
    private readonly SidebarButton _navProcesses;
    private readonly SidebarButton _navConnections;

    private string _processSortColumn = "CPU";
    private bool _processSortDescending = true;
    private string _connectionSortColumn = "PID";
    private bool _connectionSortDescending = false;
    private bool _connectionsShowSuspiciousOnly;
    private Label _connectionsAllTab = null!;
    private Label _connectionsSuspiciousTab = null!;
    private bool _processTreeView;
    private Label _processFlatTab = null!;
    private Label _processTreeTab = null!;

    private List<ProcessSample> _lastProcessSamples = new();
    private List<ConnectionInfo> _lastConnections = new();

    public MainForm()
    {
        Text = "HWMonitor - System Monitor";
        Size = new Size(1080, 920);
        MinimumSize = new Size(820, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = WindowBg;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9f);
        Icon = SystemIcons.Application;

        // --- Performance page -------------------------------------------------
        _cpuGraph = new PerformanceGraph { Title = "CPU", LineColor = AccentCpu, MaxValue = 100, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 7, 7) };
        _gpuGraph = new PerformanceGraph { Title = "GPU", LineColor = AccentGpu, MaxValue = 100, Dock = DockStyle.Fill, Margin = new Padding(7, 0, 0, 7) };
        _memGraph = new PerformanceGraph { Title = "Memory", LineColor = AccentMem, MaxValue = 100, Dock = DockStyle.Fill, Margin = new Padding(0, 7, 7, 0) };
        _netGraph = new PerformanceGraph { Title = "Network", LineColor = AccentNet, AutoScale = true, Dock = DockStyle.Fill, Margin = new Padding(7, 7, 0, 0) };

        var perfLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, BackColor = WindowBg };
        perfLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        perfLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        perfLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        perfLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        perfLayout.Controls.Add(_cpuGraph, 0, 0);
        perfLayout.Controls.Add(_gpuGraph, 1, 0);
        perfLayout.Controls.Add(_memGraph, 0, 1);
        perfLayout.Controls.Add(_netGraph, 1, 1);

        _coreTempStrip = new CoreMetricStrip
        {
            Title = "Per-Core Temp",
            ValueFormat = "0",
            MaxValue = 90,
            ColorForValue = TempBarColor,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 7, 0),
        };
        _coreClockStrip = new CoreMetricStrip
        {
            Title = "Per-Core Clock (MHz)",
            ValueFormat = "0",
            MaxValue = 5000,
            ColorForValue = _ => AccentCpu,
            Dock = DockStyle.Fill,
            Margin = new Padding(7, 0, 0, 0),
        };
        var coreStripsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, BackColor = WindowBg, Height = 130 };
        coreStripsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        coreStripsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        coreStripsLayout.Controls.Add(_coreTempStrip, 0, 0);
        coreStripsLayout.Controls.Add(_coreClockStrip, 1, 0);

        (Control powerCpuTile, _powerCpuValue) = BuildDetailTile("CPU POWER", AccentCpu);
        (Control powerGpuTile, _powerGpuValue) = BuildDetailTile("GPU POWER", AccentGpu);
        (Control powerTotalTile, _powerTotalValue) = BuildDetailTile("EST. TOTAL POWER", TextPrimary);
        (Control fanCpuTile, _fanCpuValue) = BuildDetailTile("CPU FAN", AccentCpu);
        (Control fanGpuTile, _fanGpuValue) = BuildDetailTile("GPU FAN", AccentGpu);
        (Control fanPumpTile, _fanPumpValue) = BuildDetailTile("PUMP", AccentNet);
        (Control clockTile, _clockValue) = BuildDetailTile("CPU CLOCK (BASE/NOW/BOOST)", AccentCpu, width: 190);
        (Control hotSpotTile, _gpuHotSpotValue) = BuildDetailTile("GPU HOT SPOT", AccentGpu);
        (Control memJunctionTile, _gpuMemJunctionValue) = BuildDetailTile("GPU MEM JUNCTION", AccentGpu);

        var tilesFlow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 132, BackColor = WindowBg, WrapContents = true, AutoScroll = true };
        tilesFlow.Controls.Add(powerCpuTile);
        tilesFlow.Controls.Add(powerGpuTile);
        tilesFlow.Controls.Add(powerTotalTile);
        tilesFlow.Controls.Add(fanCpuTile);
        tilesFlow.Controls.Add(fanGpuTile);
        tilesFlow.Controls.Add(fanPumpTile);
        tilesFlow.Controls.Add(clockTile);
        tilesFlow.Controls.Add(hotSpotTile);
        tilesFlow.Controls.Add(memJunctionTile);

        var detailsPanel = new Panel { Dock = DockStyle.Bottom, Height = 280, BackColor = WindowBg, Padding = new Padding(0, 14, 0, 0) };
        detailsPanel.Controls.Add(coreStripsLayout);
        detailsPanel.Controls.Add(tilesFlow);

        _performancePage = new Panel { Dock = DockStyle.Fill, BackColor = WindowBg };
        _performancePage.Controls.Add(perfLayout);
        _performancePage.Controls.Add(detailsPanel);

        // --- Processes page -----------------------------------------------------
        (_processSearchBox, _processGrid) = BuildSearchableGrid(
            placeholder: "Filter by name or PID...",
            columns: [
                ("Name", "Name", 22),
                ("PID", "PID", 7),
                ("CPU", "CPU %", 8),
                ("Memory", "Memory (WS / Priv)", 17),
                ("Disk", "Disk I/O", 12),
                ("GPU", "GPU %", 7),
                ("Network", "Network", 12),
                ("Connections", "Conn", 6),
                ("Threads", "Threads", 7),
                ("Handles", "Handles", 8),
            ]);
        _processGrid.ColumnHeaderMouseClick += (_, e) => OnSortColumnClicked(_processGrid, e.ColumnIndex, ref _processSortColumn, ref _processSortDescending, RenderProcessRows);
        _processGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowSelectedProcessProperties(); };
        _processSearchBox.TextChanged += (_, _) => RenderProcessRows();
        AttachEndTaskMenu();
        _processesPage = BuildPage(_processSearchBox, _processGrid, BuildProcessToolbar());

        // --- Connections page -----------------------------------------------------
        (_connectionSearchBox, _connectionGrid) = BuildSearchableGrid(
            placeholder: "Filter by process, path, IP, or port...",
            columns: [
                ("Protocol", "Proto", 7),
                ("PID", "PID", 7),
                ("Process", "Process", 15),
                ("Path", "Path", 20),
                ("Local", "Local Address", 16),
                ("Remote", "Remote Address", 16),
                ("State", "State", 10),
                ("Usage", $"{NetRateGlyphs.Download} In / {NetRateGlyphs.Upload} Out", 13),
                ("Risk", "Risk", 16),
            ]);
        _connectionGrid.MultiSelect = true;
        _connectionGrid.Columns["Usage"]!.ToolTipText =
            $"Current network speed for this connection's process:\n{NetRateGlyphs.Download} In = incoming/download rate\n{NetRateGlyphs.Upload} Out = outgoing/upload rate";
        _connectionGrid.ColumnHeaderMouseClick += (_, e) => OnSortColumnClicked(_connectionGrid, e.ColumnIndex, ref _connectionSortColumn, ref _connectionSortDescending, RenderConnectionRows);
        _connectionGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowProcessPropertiesForPid((int)_connectionGrid.Rows[e.RowIndex].Cells["PID"].Value); };
        _connectionSearchBox.TextChanged += (_, _) => RenderConnectionRows();
        AttachConnectionContextMenu();
        _connectionsPage = BuildPage(_connectionSearchBox, _connectionGrid, BuildConnectionFilterBar());

        // --- Content area (holds the three pages, one visible at a time) --------
        var contentPanel = new Panel { Dock = DockStyle.Fill, BackColor = WindowBg, Padding = new Padding(24, 20, 24, 20) };
        contentPanel.Controls.Add(_connectionsPage);
        contentPanel.Controls.Add(_processesPage);
        contentPanel.Controls.Add(_performancePage);

        // --- Sidebar --------------------------------------------------------------
        _navPerformance = new SidebarButton { Text = "Performance", Glyph = "📊", AccentColor = AccentCpu, Dock = DockStyle.Fill };
        _navProcesses = new SidebarButton { Text = "Processes", Glyph = "🧩", AccentColor = AccentCpu, Dock = DockStyle.Fill };
        _navConnections = new SidebarButton { Text = "Connections", Glyph = "🌐", AccentColor = AccentCpu, Dock = DockStyle.Fill };
        _navPerformance.BackColor = SidebarBg;
        _navProcesses.BackColor = SidebarBg;
        _navConnections.BackColor = SidebarBg;
        _navPerformance.Click += (_, _) => SelectPage(0);
        _navProcesses.Click += (_, _) => SelectPage(1);
        _navConnections.Click += (_, _) => SelectPage(2);

        var navLayout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4, Height = 42 * 3, BackColor = SidebarBg };
        navLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        navLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        navLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        navLayout.Controls.Add(_navPerformance, 0, 0);
        navLayout.Controls.Add(_navProcesses, 0, 1);
        navLayout.Controls.Add(_navConnections, 0, 2);

        var sidebarPanel = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = SidebarBg, Padding = new Padding(0, 16, 0, 0) };
        sidebarPanel.Controls.Add(navLayout);

        var bodyPanel = new Panel { Dock = DockStyle.Fill, BackColor = WindowBg };
        bodyPanel.Controls.Add(contentPanel);
        bodyPanel.Controls.Add(sidebarPanel);

        // --- Header -----------------------------------------------------------
        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = HeaderBg };
        var titleLabel = new Label
        {
            Text = "HWMonitor",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(24, 10),
        };
        var subtitleLabel = new Label
        {
            Text = "Server Monitor",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = TextSecondary,
            AutoSize = true,
            Location = new Point(24, 34),
        };
        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(subtitleLabel);

        (Control cpuChip, _cpuChipValue) = BuildHeaderChip("CPU", AccentCpu);
        (Control memChip, _memChipValue) = BuildHeaderChip("MEMORY", AccentMem);
        (Control gpuChip, _gpuChipValue) = BuildHeaderChip("GPU", AccentGpu);
        (Control netChip, _netChipValue) = BuildHeaderChip("NETWORK", AccentNet, width: 118, valueFontSize: 10.5f);
        var chipFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            BackColor = HeaderBg,
            Padding = new Padding(0, 9, 24, 0),
        };
        chipFlow.Controls.Add(netChip);
        chipFlow.Controls.Add(gpuChip);
        chipFlow.Controls.Add(memChip);
        chipFlow.Controls.Add(cpuChip);
        headerPanel.Controls.Add(chipFlow);

        var headerDivider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = BorderColor };
        headerPanel.Controls.Add(headerDivider);

        // --- Status bar --------------------------------------------------------
        var statusStrip = new StatusStrip { BackColor = HeaderBg, SizingGrip = false };
        statusStrip.Renderer = new ToolStripProfessionalRenderer(new DarkStatusStripColors());
        _statusLabel = new ToolStripStatusLabel { ForeColor = TextSecondary, Text = "Starting...", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(new ToolStripStatusLabel { ForeColor = TextSecondary, Text = "Refresh every:" });
        var refreshCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 64 };
        foreach ((string label, int _) in RefreshIntervalChoices)
        {
            refreshCombo.Items.Add(label);
        }
        int currentChoiceIndex = Array.FindIndex(RefreshIntervalChoices, c => c.Ms == _settings.RefreshIntervalMs);
        refreshCombo.SelectedIndex = currentChoiceIndex >= 0 ? currentChoiceIndex : 2;
        refreshCombo.SelectedIndexChanged += (_, _) => SetRefreshInterval(RefreshIntervalChoices[refreshCombo.SelectedIndex].Ms);
        statusStrip.Items.Add(refreshCombo);

        Controls.Add(bodyPanel);
        Controls.Add(headerPanel);
        Controls.Add(statusStrip);

        SelectPage(0);

        bool etwStarted = _etwMonitor.Start();
        _statusLabel.Text = etwStarted
            ? "Per-app network speed: live (ETW)"
            : $"Per-app network speed unavailable ({_etwMonitor.StartError}); showing connection counts only";

        _timer = new System.Windows.Forms.Timer { Interval = RefreshIntervalChoices[refreshCombo.SelectedIndex].Ms };
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();

        RefreshAll();
    }

    private void SetRefreshInterval(int ms)
    {
        _timer.Interval = ms;
        _settings.RefreshIntervalMs = ms;
        _settings.Save();
    }

    private void SelectPage(int index)
    {
        _performancePage.Visible = index == 0;
        _processesPage.Visible = index == 1;
        _connectionsPage.Visible = index == 2;
        _navPerformance.Selected = index == 0;
        _navProcesses.Selected = index == 1;
        _navConnections.Selected = index == 2;
    }

    private static (Control chip, Label value) BuildHeaderChip(string caption, Color accent, int width = 96, float valueFontSize = 12f)
    {
        var chip = new Panel { Width = width, Height = 42, BackColor = CardBg, Margin = new Padding(6, 0, 0, 0) };
        var captionLabel = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = TextSecondary,
            AutoSize = true,
            Location = new Point(12, 6),
        };
        var valueLabel = new Label
        {
            Text = "--",
            Font = new Font("Segoe UI", valueFontSize, FontStyle.Bold),
            ForeColor = accent,
            AutoSize = true,
            Location = new Point(12, 18),
        };
        chip.Controls.Add(captionLabel);
        chip.Controls.Add(valueLabel);
        return (chip, valueLabel);
    }

    private static (Control tile, Label value) BuildDetailTile(string caption, Color accent, int width = 130)
    {
        var tile = new Panel { Width = width, Height = 54, BackColor = CardBg, Margin = new Padding(0, 0, 8, 0) };
        var captionLabel = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7f),
            ForeColor = TextSecondary,
            AutoSize = false,
            Size = new Size(width - 16, 16),
            Location = new Point(10, 6),
        };
        var valueLabel = new Label
        {
            Text = "--",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = accent,
            AutoSize = false,
            Size = new Size(width - 16, 22),
            Location = new Point(10, 24),
        };
        tile.Controls.Add(captionLabel);
        tile.Controls.Add(valueLabel);
        return (tile, valueLabel);
    }

    private static Color TempBarColor(float tempC) => tempC switch
    {
        < 60f => Color.FromArgb(102, 187, 106),
        < 80f => Color.FromArgb(255, 202, 40),
        _ => Color.FromArgb(239, 83, 80),
    };

    private static Panel BuildPage(TextBox searchBox, DataGridView grid, Control? toolbar = null)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = WindowBg };

        var searchWrapper = new Panel { BackColor = BorderColor, Dock = DockStyle.Top, Height = 37, Padding = new Padding(1) };
        var searchInner = new Panel { Dock = DockStyle.Fill, BackColor = CardBg, Padding = new Padding(12, 0, 12, 0) };
        searchBox.Dock = DockStyle.Fill;
        searchInner.Controls.Add(searchBox);
        searchWrapper.Controls.Add(searchInner);

        var gridWrapper = new Panel { BackColor = BorderColor, Dock = DockStyle.Fill, Padding = new Padding(1, 0, 1, 1) };
        gridWrapper.Controls.Add(grid);

        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = WindowBg, Padding = new Padding(0, 12, 0, 0) };
        gridHost.Controls.Add(gridWrapper);

        page.Controls.Add(gridHost);
        if (toolbar is not null)
        {
            page.Controls.Add(toolbar);
        }
        page.Controls.Add(searchWrapper);
        return page;
    }

    private Panel BuildConnectionFilterBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = WindowBg, Padding = new Padding(2, 10, 0, 0) };

        _connectionsAllTab = MakeFilterTab("All Connections");
        _connectionsSuspiciousTab = MakeFilterTab("⚠ Suspicious");

        _connectionsAllTab.Click += (_, _) => { _connectionsShowSuspiciousOnly = false; UpdateConnectionFilterTabStyles(); RenderConnectionRows(); };
        _connectionsSuspiciousTab.Click += (_, _) => { _connectionsShowSuspiciousOnly = true; UpdateConnectionFilterTabStyles(); RenderConnectionRows(); };

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = WindowBg };
        flow.Controls.Add(_connectionsAllTab);
        flow.Controls.Add(_connectionsSuspiciousTab);
        bar.Controls.Add(flow);

        UpdateConnectionFilterTabStyles();
        return bar;
    }

    private Panel BuildProcessToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = WindowBg, Padding = new Padding(2, 10, 0, 0) };

        _processFlatTab = MakeFilterTab("Flat");
        _processTreeTab = MakeFilterTab("Tree View");

        _processFlatTab.Click += (_, _) => { _processTreeView = false; UpdateProcessTabStyles(); RenderProcessRows(); };
        _processTreeTab.Click += (_, _) => { _processTreeView = true; UpdateProcessTabStyles(); RenderProcessRows(); };

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = WindowBg };
        flow.Controls.Add(_processFlatTab);
        flow.Controls.Add(_processTreeTab);
        bar.Controls.Add(flow);

        UpdateProcessTabStyles();
        return bar;
    }

    private void UpdateProcessTabStyles()
    {
        _processFlatTab.ForeColor = _processTreeView ? TextSecondary : TextPrimary;
        _processFlatTab.Font = new Font("Segoe UI", 9f, _processTreeView ? FontStyle.Regular : FontStyle.Bold);
        _processTreeTab.ForeColor = _processTreeView ? TextPrimary : TextSecondary;
        _processTreeTab.Font = new Font("Segoe UI", 9f, _processTreeView ? FontStyle.Bold : FontStyle.Regular);
    }

    private static Label MakeFilterTab(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Cursor = Cursors.Hand,
        Font = new Font("Segoe UI", 9f),
        ForeColor = TextSecondary,
        Margin = new Padding(0, 0, 20, 0),
    };

    private void UpdateConnectionFilterTabStyles()
    {
        _connectionsAllTab.ForeColor = _connectionsShowSuspiciousOnly ? TextSecondary : TextPrimary;
        _connectionsAllTab.Font = new Font("Segoe UI", 9f, _connectionsShowSuspiciousOnly ? FontStyle.Regular : FontStyle.Bold);
        _connectionsSuspiciousTab.ForeColor = _connectionsShowSuspiciousOnly ? Color.FromArgb(255, 138, 128) : TextSecondary;
        _connectionsSuspiciousTab.Font = new Font("Segoe UI", 9f, _connectionsShowSuspiciousOnly ? FontStyle.Bold : FontStyle.Regular);
    }

    private (TextBox, DataGridView) BuildSearchableGrid(string placeholder, (string Name, string Header, int Weight)[] columns)
    {
        var searchBox = new TextBox
        {
            BackColor = CardBg,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            PlaceholderText = placeholder,
            Font = new Font("Segoe UI", 9.5f),
        };

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = CardBg,
            ForeColor = TextPrimary,
            GridColor = BorderColor,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            Font = new Font("Segoe UI", 9.25f),
        };
        grid.ColumnHeadersHeight = 36;
        grid.DefaultCellStyle.BackColor = CardBg;
        grid.DefaultCellStyle.ForeColor = TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(45, AccentCpu.R, AccentCpu.G, AccentCpu.B);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.Padding = new Padding(10, 4, 10, 4);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(30, 31, 34);
        grid.ColumnHeadersDefaultCellStyle.BackColor = CardBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.RowTemplate.Height = 30;

        typeof(DataGridView)
            .GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(grid, true);

        foreach ((string name, string header, int weight) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.Programmatic,
                Tag = header,
            });
        }

        return (searchBox, grid);
    }

    private static void OnSortColumnClicked(DataGridView grid, int columnIndex, ref string sortColumn, ref bool sortDescending, Action render)
    {
        string clicked = grid.Columns[columnIndex].Name;
        if (clicked == sortColumn)
        {
            sortDescending = !sortDescending;
        }
        else
        {
            sortColumn = clicked;
            sortDescending = clicked is not ("Name" or "Process" or "Protocol" or "Local" or "Remote" or "State" or "Path");
        }
        render();
    }

    private static readonly (string Label, ProcessPriorityClass Priority)[] PriorityChoices =
    [
        ("Realtime", ProcessPriorityClass.RealTime),
        ("High", ProcessPriorityClass.High),
        ("Above Normal", ProcessPriorityClass.AboveNormal),
        ("Normal", ProcessPriorityClass.Normal),
        ("Below Normal", ProcessPriorityClass.BelowNormal),
        ("Idle", ProcessPriorityClass.Idle),
    ];

    private void AttachEndTaskMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("End Task", null, (_, _) => EndSelectedTask());
        menu.Items.Add("Restart", null, (_, _) => RestartSelectedProcess());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Suspend", null, (_, _) => SetSelectedProcessSuspended(true));
        menu.Items.Add("Resume", null, (_, _) => SetSelectedProcessSuspended(false));
        menu.Items.Add(new ToolStripSeparator());

        var priorityMenu = new ToolStripMenuItem("Priority");
        foreach ((string label, ProcessPriorityClass priority) in PriorityChoices)
        {
            var item = new ToolStripMenuItem(label) { Tag = priority };
            item.Click += (_, _) => SetSelectedProcessPriority(priority);
            priorityMenu.DropDownItems.Add(item);
        }
        priorityMenu.DropDownItems.Add(new ToolStripSeparator());
        var forgetItem = new ToolStripMenuItem("Forget Saved Priority");
        forgetItem.Click += (_, _) => ForgetSelectedProcessPriority();
        priorityMenu.DropDownItems.Add(forgetItem);
        priorityMenu.DropDownOpening += (_, _) => UpdatePriorityMenuChecks(priorityMenu);
        menu.Items.Add(priorityMenu);

        menu.Items.Add("Set Affinity...", null, (_, _) => ShowAffinityDialogForSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open File Location", null, (_, _) => OpenFileLocationForSelected());
        menu.Items.Add("Properties...", null, (_, _) => ShowSelectedProcessProperties());

        _processGrid.ContextMenuStrip = menu;
        _processGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _processGrid.ClearSelection();
                _processGrid.Rows[e.RowIndex].Selected = true;
            }
        };
    }

    private void UpdatePriorityMenuChecks(ToolStripMenuItem priorityMenu)
    {
        ProcessPriorityClass? current = null;
        if (_processGrid.SelectedRows.Count > 0 && _processGrid.SelectedRows[0].Tag is int pid)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                current = process.PriorityClass;
            }
            catch
            {
                // Process may be protected or have exited; leave unchecked.
            }
        }

        foreach (ToolStripItem item in priorityMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem && menuItem.Tag is ProcessPriorityClass p)
            {
                menuItem.Checked = current == p;
            }
        }
    }

    private void SetSelectedProcessPriority(ProcessPriorityClass priority)
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        string name = _processGrid.SelectedRows[0].Cells["Name"].Value?.ToString() ?? "";

        if (priority == ProcessPriorityClass.RealTime)
        {
            DialogResult confirm = MessageBox.Show(
                this,
                $"Realtime priority can make the system unresponsive if \"{name}\" misbehaves. Continue?",
                "Set Priority",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                return;
            }
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not set priority: {ex.Message}", "Set Priority", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (name.Length > 0)
        {
            _priorityStore.Set(name, priority);
        }
    }

    private void ForgetSelectedProcessPriority()
    {
        if (_processGrid.SelectedRows.Count == 0)
        {
            return;
        }

        string name = _processGrid.SelectedRows[0].Cells["Name"].Value?.ToString() ?? "";
        if (name.Length > 0)
        {
            _priorityStore.Clear(name);
        }
    }

    private void ApplyStoredPriorityRules()
    {
        if (_priorityStore.Rules.Count == 0)
        {
            return;
        }

        foreach (ProcessSample sample in _lastProcessSamples)
        {
            if (_priorityStore.Get(sample.Name) is not { } desired)
            {
                continue;
            }

            try
            {
                using Process process = Process.GetProcessById(sample.Pid);
                if (process.PriorityClass != desired)
                {
                    process.PriorityClass = desired;
                }
            }
            catch
            {
                // Protected process or it exited between sampling and here; skip silently.
            }
        }
    }

    private void EndSelectedTask()
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        string name = _processGrid.SelectedRows[0].Cells["Name"].Value?.ToString() ?? "process";
        DialogResult confirm = MessageBox.Show(
            this,
            $"End \"{name}\" (PID {pid})?",
            "End Task",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not end process: {ex.Message}", "End Task", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartSelectedProcess()
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        string name = _processGrid.SelectedRows[0].Cells["Name"].Value?.ToString() ?? "process";
        string? path;
        try
        {
            using Process process = Process.GetProcessById(pid);
            path = process.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read process path: {ex.Message}", "Restart", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (path is null)
        {
            MessageBox.Show(this, $"Could not determine the executable path for \"{name}\" (access denied or it's a protected process).", "Restart", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult confirm = MessageBox.Show(this, $"Restart \"{name}\" (PID {pid})?", "Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(5000);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not restart \"{name}\": {ex.Message}", "Restart", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetSelectedProcessSuspended(bool suspend)
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        string name = _processGrid.SelectedRows[0].Cells["Name"].Value?.ToString() ?? "process";

        try
        {
            using Process process = Process.GetProcessById(pid);
            int status = suspend ? NativeMethods.NtSuspendProcess(process.Handle) : NativeMethods.NtResumeProcess(process.Handle);
            if (status != 0)
            {
                MessageBox.Show(this, $"Could not {(suspend ? "suspend" : "resume")} \"{name}\" (NTSTATUS 0x{status:X8}).", suspend ? "Suspend" : "Resume", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not {(suspend ? "suspend" : "resume")} \"{name}\": {ex.Message}", suspend ? "Suspend" : "Resume", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowAffinityDialogForSelected()
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        string name = _processGrid.SelectedRows[0].Cells["Name"].Value?.ToString() ?? "process";

        Process process;
        long currentMask;
        try
        {
            process = Process.GetProcessById(pid);
            currentMask = process.ProcessorAffinity.ToInt64();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read affinity for \"{name}\": {ex.Message}", "Set Affinity", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var form = new Form
        {
            Text = $"Set Affinity - {name} (PID {pid})",
            Width = 280,
            Height = 380,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        int coreCount = Environment.ProcessorCount;
        for (int i = 0; i < coreCount; i++)
        {
            list.Items.Add($"CPU {i}", (currentMask & (1L << i)) != 0);
        }

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 8, 6) };
        var okButton = new Button { Text = "Apply", DialogResult = DialogResult.OK };
        var allButton = new Button { Text = "All" };
        allButton.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        buttonPanel.Controls.Add(okButton);
        buttonPanel.Controls.Add(allButton);

        form.Controls.Add(list);
        form.Controls.Add(buttonPanel);
        form.AcceptButton = okButton;

        if (form.ShowDialog(this) != DialogResult.OK)
        {
            using (process) { }
            return;
        }

        long newMask = 0;
        for (int i = 0; i < list.CheckedIndices.Count; i++)
        {
            newMask |= 1L << list.CheckedIndices[i];
        }

        if (newMask == 0)
        {
            MessageBox.Show(this, "At least one CPU must be selected.", "Set Affinity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            using (process) { }
            return;
        }

        try
        {
            process.ProcessorAffinity = (nint)newMask;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not set affinity for \"{name}\": {ex.Message}", "Set Affinity", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OpenFileLocationForSelected()
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        OpenFileLocationForPid(pid);
    }

    private void OpenFileLocationForPid(int pid)
    {
        string? path;
        try
        {
            using Process process = Process.GetProcessById(pid);
            path = process.MainModule?.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not locate the executable: {ex.Message}", "Open File Location", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (path is null)
        {
            MessageBox.Show(this, "Could not determine the executable path (access denied or protected process).", "Open File Location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open Explorer: {ex.Message}", "Open File Location", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSelectedProcessProperties()
    {
        if (_processGrid.SelectedRows.Count == 0 || _processGrid.SelectedRows[0].Tag is not int pid)
        {
            return;
        }

        ShowProcessPropertiesForPid(pid);
    }

    private void ShowProcessPropertiesForPid(int pid)
    {
        Cursor previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        ProcessDetails details;
        try
        {
            details = ProcessInspector.Inspect(pid);
        }
        finally
        {
            Cursor = previousCursor;
        }

        using var form = new ProcessDetailsForm(details);
        form.ShowDialog(this);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(nint processHandle);

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(nint processHandle);
    }

    private void RefreshAll()
    {
        SystemStats stats = _systemStats.Sample();
        _cpuGraph.ValueText = $"{stats.CpuPercent:0.0}%";
        _cpuGraph.AddSample(stats.CpuPercent);
        _cpuChipValue.Text = $"{stats.CpuPercent:0}%";

        double memPercent = stats.MemoryTotalBytes > 0 ? stats.MemoryUsedBytes / stats.MemoryTotalBytes * 100 : 0;
        _memGraph.ValueText = $"{memPercent:0.0}%";
        _memGraph.SubText = $"{FormatBytes((long)stats.MemoryUsedBytes)} / {FormatBytes((long)stats.MemoryTotalBytes)}";
        _memGraph.AddSample(memPercent);
        _memChipValue.Text = $"{memPercent:0}%";

        double netTotal = stats.NetworkRxBytesPerSec + stats.NetworkTxBytesPerSec;
        _netGraph.ValueText = $"{NetRateGlyphs.Download}{FormatBytesPerSec((long)stats.NetworkRxBytesPerSec)}  {NetRateGlyphs.Upload}{FormatBytesPerSec((long)stats.NetworkTxBytesPerSec)}";
        _netGraph.AddSample(netTotal);
        _netChipValue.Text = FormatBytesPerSec((long)netTotal);

        Reading reading;
        try
        {
            reading = _hardwareMonitor.Read();
        }
        catch
        {
            reading = default;
        }

        _cpuGraph.SubText = reading.CpuTempC is { } cpuTemp ? $"{cpuTemp:0}°C" : "";

        double gpuPercent = reading.GpuLoadPercent ?? 0;
        _gpuGraph.ValueText = reading.GpuLoadPercent is { } gpuLoad ? $"{gpuLoad:0.0}%" : "n/a";
        _gpuGraph.SubText = reading.GpuMemoryUsedMb is { } usedMb && reading.GpuMemoryTotalMb is { } totalMb
            ? $"{FormatBytes((long)usedMb * 1024 * 1024)} / {FormatBytes((long)totalMb * 1024 * 1024)}"
            : reading.GpuTempC is { } gpuTemp ? $"{gpuTemp:0}°C" : "";
        _gpuGraph.AddSample(gpuPercent);
        _gpuChipValue.Text = reading.GpuLoadPercent is { } gpuChipLoad ? $"{gpuChipLoad:0}%" : "n/a";

        _coreTempStrip.Values = reading.CpuCoreTemps ?? [];
        _coreClockStrip.Values = reading.CpuCoreClocksMhz ?? [];

        _powerCpuValue.Text = reading.CpuPackagePowerW is { } cpuW ? $"{cpuW:0.0} W" : "n/a";
        _powerGpuValue.Text = reading.GpuPowerW is { } gpuW ? $"{gpuW:0.0} W" : "n/a";
        _powerTotalValue.Text = reading.EstimatedTotalPowerW is { } totalW ? $"{totalW:0.0} W" : "n/a";
        _fanCpuValue.Text = reading.CpuFanRpm is { } cpuFan ? $"{cpuFan:0} RPM" : "n/a";
        _fanGpuValue.Text = reading.GpuFanRpm is { } gpuFan ? $"{gpuFan:0} RPM" : "n/a";
        _fanPumpValue.Text = reading.PumpRpm is { } pumpRpm ? $"{pumpRpm:0} RPM" : "n/a";
        _gpuHotSpotValue.Text = reading.GpuHotSpotC is { } hotSpot ? $"{hotSpot:0}°C" : "n/a";
        _gpuMemJunctionValue.Text = reading.GpuMemoryJunctionC is { } memJunction ? $"{memJunction:0}°C" : "n/a";

        string baseClockText = reading.CpuBaseClockMhz is { } baseClock ? $"{baseClock}" : "?";
        string nowClockText = reading.CpuAvgClockMhz is { } nowClock ? $"{nowClock:0}" : "?";
        string boostClockText = reading.CpuMaxClockMhzSeen is { } boostClock ? $"{boostClock:0}" : "?";
        _clockValue.Text = $"{baseClockText} / {nowClockText} / {boostClockText}";

        _etwMonitor.RecomputeRates();

        List<ConnectionInfo> connections;
        try
        {
            connections = _connectionsService.List();
        }
        catch
        {
            connections = new List<ConnectionInfo>();
        }
        Dictionary<int, int> connectionCounts = NetworkConnectionsService.CountsByPid(connections);

        _lastProcessSamples = _processMonitor.Sample(_etwMonitor, connectionCounts).ToList();
        _lastConnections = connections;

        ApplyStoredPriorityRules();

        RenderProcessRows();
        RenderConnectionRows();

        _statusLabel.Text = _etwMonitor.IsRunning
            ? $"Processes: {_lastProcessSamples.Count}   |   Connections: {_lastConnections.Count}   |   Per-app network: live (ETW)"
            : $"Processes: {_lastProcessSamples.Count}   |   Connections: {_lastConnections.Count}   |   Per-app network unavailable ({_etwMonitor.StartError})";
    }

    private List<ProcessSample> ApplyProcessSort(IEnumerable<ProcessSample> seq) => _processSortColumn switch
    {
        "PID" => Order(seq, p => p.Pid, _processSortDescending),
        "CPU" => Order(seq, p => p.CpuPercent, _processSortDescending),
        "Memory" => Order(seq, p => p.WorkingSetBytes, _processSortDescending),
        "Disk" => Order(seq, p => p.DiskReadBytesPerSec + p.DiskWriteBytesPerSec, _processSortDescending),
        "GPU" => Order(seq, p => p.GpuPercent ?? -1, _processSortDescending),
        "Network" => Order(seq, p => p.NetRxBytesPerSec + p.NetTxBytesPerSec, _processSortDescending),
        "Connections" => Order(seq, p => p.ConnectionCount, _processSortDescending),
        "Threads" => Order(seq, p => p.ThreadCount, _processSortDescending),
        "Handles" => Order(seq, p => p.HandleCount, _processSortDescending),
        _ => Order(seq, p => p.Name, _processSortDescending, StringComparer.OrdinalIgnoreCase),
    };

    private static List<(ProcessSample Sample, int Depth)> BuildProcessTree(List<ProcessSample> filtered, Func<IEnumerable<ProcessSample>, List<ProcessSample>> sortSiblings)
    {
        HashSet<int> pidsInSet = filtered.Select(p => p.Pid).ToHashSet();
        Dictionary<int, List<ProcessSample>> byParent = filtered
            .Where(p => p.ParentPid != p.Pid)
            .GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => sortSiblings(g));

        List<ProcessSample> roots = sortSiblings(filtered.Where(p => p.ParentPid == p.Pid || !pidsInSet.Contains(p.ParentPid)));

        var result = new List<(ProcessSample, int)>();
        var visited = new HashSet<int>();

        void Walk(ProcessSample sample, int depth)
        {
            if (!visited.Add(sample.Pid))
            {
                return;
            }

            result.Add((sample, depth));
            if (byParent.TryGetValue(sample.Pid, out List<ProcessSample>? children))
            {
                foreach (ProcessSample child in children)
                {
                    Walk(child, depth + 1);
                }
            }
        }

        foreach (ProcessSample root in roots)
        {
            Walk(root, 0);
        }

        foreach (ProcessSample leftover in filtered.Where(p => !visited.Contains(p.Pid)))
        {
            Walk(leftover, 0);
        }

        return result;
    }

    private void RenderProcessRows()
    {
        IEnumerable<ProcessSample> filtered = _lastProcessSamples;
        string filter = _processSearchBox.Text.Trim();
        if (filter.Length > 0)
        {
            filtered = filtered.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        List<(ProcessSample Sample, int Depth)> rows = _processTreeView
            ? BuildProcessTree(filtered.ToList(), ApplyProcessSort)
            : ApplyProcessSort(filtered).Select(s => (Sample: s, Depth: 0)).ToList();

        object? selectedPid = _processGrid.SelectedRows.Count > 0 ? _processGrid.SelectedRows[0].Tag : null;

        _processGrid.SuspendLayout();
        _processGrid.Rows.Clear();
        foreach ((ProcessSample sample, int depth) in rows)
        {
            string displayName = depth > 0 ? new string(' ', depth * 3) + "└ " + sample.Name : sample.Name;
            string memory = $"{FormatBytes(sample.WorkingSetBytes)} / {FormatBytes(sample.PrivateBytes)}";
            string disk = FormatBytesPerSec(sample.DiskReadBytesPerSec + sample.DiskWriteBytesPerSec);
            string gpu = sample.GpuPercent is { } gpuPercent ? $"{gpuPercent:0.0}%" : "n/a";

            int rowIndex = _processGrid.Rows.Add(
                displayName,
                sample.Pid,
                $"{sample.CpuPercent:0.0}%",
                memory,
                disk,
                gpu,
                FormatBytesPerSec(sample.NetRxBytesPerSec + sample.NetTxBytesPerSec),
                sample.ConnectionCount,
                sample.ThreadCount,
                sample.HandleCount);
            _processGrid.Rows[rowIndex].Tag = sample.Pid;
            if (selectedPid is int pid && pid == sample.Pid)
            {
                _processGrid.Rows[rowIndex].Selected = true;
            }
        }
        _processGrid.ResumeLayout();
        UpdateSortGlyphs(_processGrid, _processSortColumn, _processSortDescending);
    }

    private void RenderConnectionRows()
    {
        Dictionary<int, string> pidNames = _lastProcessSamples.ToDictionary(p => p.Pid, p => p.Name);
        var pidPaths = new Dictionary<int, string?>();

        string? GetProcessPath(int pid)
        {
            if (pidPaths.TryGetValue(pid, out string? cached))
            {
                return cached;
            }

            string? path = null;
            try
            {
                using Process proc = Process.GetProcessById(pid);
                path = proc.MainModule?.FileName;
            }
            catch
            {
                // Access denied, process exited, or bitness mismatch; leave unresolved.
            }

            pidPaths[pid] = path;
            return path;
        }

        IEnumerable<ConnectionInfo> filtered = _lastConnections;
        string filter = _connectionSearchBox.Text.Trim();
        if (filter.Length > 0)
        {
            filtered = filtered.Where(c =>
                c.LocalAddress.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.RemoteAddress.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (pidNames.TryGetValue(c.Pid, out string? n) && n.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (GetProcessPath(c.Pid) is { } p && p.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        List<(ConnectionInfo Connection, string? Reason)> flagged = filtered
            .Select(c => (Connection: c, Reason: SuspiciousConnectionAnalyzer.Classify(c, GetProcessPath(c.Pid))))
            .ToList();

        if (_connectionsShowSuspiciousOnly)
        {
            flagged = flagged.Where(x => x.Reason is not null).ToList();
        }

        List<(ConnectionInfo Connection, string? Reason)> ordered = _connectionSortColumn switch
        {
            "PID" => Order(flagged, x => x.Connection.Pid, _connectionSortDescending),
            "Path" => Order(flagged, x => GetProcessPath(x.Connection.Pid) ?? "", _connectionSortDescending, StringComparer.OrdinalIgnoreCase),
            "Protocol" => Order(flagged, x => x.Connection.Protocol, _connectionSortDescending, StringComparer.OrdinalIgnoreCase),
            "Local" => Order(flagged, x => x.Connection.LocalPort, _connectionSortDescending),
            "Remote" => Order(flagged, x => x.Connection.RemotePort, _connectionSortDescending),
            "State" => Order(flagged, x => x.Connection.State, _connectionSortDescending, StringComparer.OrdinalIgnoreCase),
            "Usage" => Order(flagged, x => Sum(_etwMonitor.GetRatesForProcess(x.Connection.Pid)), _connectionSortDescending),
            "Risk" => Order(flagged, x => x.Reason ?? "", _connectionSortDescending, StringComparer.OrdinalIgnoreCase),
            _ => Order(flagged, x => x.Connection.Pid, _connectionSortDescending),
        };

        _connectionGrid.SuspendLayout();
        _connectionGrid.Rows.Clear();
        foreach ((ConnectionInfo c, string? reason) in ordered)
        {
            string processName = pidNames.TryGetValue(c.Pid, out string? name) ? name : $"pid {c.Pid}";
            string local = $"{c.LocalAddress}:{c.LocalPort}";
            string remote = c.RemotePort == 0 ? "*" : $"{c.RemoteAddress}:{c.RemotePort}";
            (long rx, long tx) = _etwMonitor.GetRatesForProcess(c.Pid);
            string usage = _etwMonitor.IsRunning ? $"{NetRateGlyphs.Download}{FormatBytesPerSec(rx)} {NetRateGlyphs.Upload}{FormatBytesPerSec(tx)}" : "n/a";
            string path = GetProcessPath(c.Pid) ?? "";
            int rowIndex = _connectionGrid.Rows.Add(c.Protocol, c.Pid, processName, path, local, remote, c.State, usage, reason ?? "");
            _connectionGrid.Rows[rowIndex].Tag = c;
            if (reason is not null)
            {
                DataGridViewRow row = _connectionGrid.Rows[rowIndex];
                row.DefaultCellStyle.BackColor = Color.FromArgb(48, 30, 30);
                row.Cells["Risk"].Style.ForeColor = Color.FromArgb(255, 138, 128);
            }
        }
        _connectionGrid.ResumeLayout();
        UpdateSortGlyphs(_connectionGrid, _connectionSortColumn, _connectionSortDescending);
    }

    private void AttachConnectionContextMenu()
    {
        var menu = new ContextMenuStrip();
        var trackItem = new ToolStripMenuItem("Track Bandwidth...", null, (_, _) => TrackSelectedConnections());
        menu.Items.Add(trackItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy Local Address", null, (_, _) => CopySelectedConnectionAddress(local: true));
        menu.Items.Add("Copy Remote Address", null, (_, _) => CopySelectedConnectionAddress(local: false));
        menu.Items.Add(new ToolStripSeparator());
        var closeItem = new ToolStripMenuItem("Close Connection", null, (_, _) => CloseSelectedConnection());
        menu.Items.Add(closeItem);
        menu.Items.Add("End Owning Process", null, (_, _) => EndSelectedConnectionProcess());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Go to Process", null, (_, _) => GoToSelectedConnectionProcess());
        menu.Items.Add("Open File Location", null, (_, _) => OpenFileLocationForSelectedConnection());
        menu.Items.Add("Properties...", null, (_, _) => ShowSelectedConnectionProcessProperties());

        menu.Opening += (_, _) =>
        {
            trackItem.Enabled = _connectionGrid.SelectedRows.Count > 0;
            closeItem.Enabled = _connectionGrid.SelectedRows.Count > 0 && _connectionGrid.SelectedRows[0].Tag is ConnectionInfo { Protocol: "TCP" };
        };

        _connectionGrid.ContextMenuStrip = menu;
        _connectionGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !_connectionGrid.Rows[e.RowIndex].Selected)
            {
                _connectionGrid.ClearSelection();
                _connectionGrid.Rows[e.RowIndex].Selected = true;
            }
        };
    }

    private ConnectionInfo? SelectedConnection() =>
        _connectionGrid.SelectedRows.Count > 0 && _connectionGrid.SelectedRows[0].Tag is ConnectionInfo c ? c : null;

    private List<ConnectionInfo> SelectedConnections() =>
        _connectionGrid.Rows
            .Cast<DataGridViewRow>()
            .Where(r => r.Selected && r.Tag is ConnectionInfo)
            .Select(r => (ConnectionInfo)r.Tag!)
            .ToList();

    private void TrackSelectedConnections()
    {
        List<ConnectionInfo> connections = SelectedConnections();
        if (connections.Count == 0)
        {
            return;
        }

        var tracker = new ConnectionTrackerForm(
            _etwMonitor,
            connections,
            pid => _lastProcessSamples.FirstOrDefault(p => p.Pid == pid)?.Name ?? $"pid {pid}");
        tracker.Show(this);
    }

    private void CopySelectedConnectionAddress(bool local)
    {
        if (SelectedConnection() is not { } c)
        {
            return;
        }

        string text = local ? $"{c.LocalAddress}:{c.LocalPort}" : (c.RemotePort == 0 ? c.RemoteAddress : $"{c.RemoteAddress}:{c.RemotePort}");
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another app; not worth failing loudly over.
        }
    }

    private void CloseSelectedConnection()
    {
        if (SelectedConnection() is not { } c || c.Protocol != "TCP")
        {
            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            $"Force-close the TCP connection {c.LocalAddress}:{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort}?\n\nThe owning process keeps running; only this socket is torn down.",
            "Close Connection",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        if (!NetworkConnectionsService.CloseTcpConnection(c))
        {
            MessageBox.Show(this, "Could not close the connection. It may have already closed, or the OS refused the request.", "Close Connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EndSelectedConnectionProcess()
    {
        if (SelectedConnection() is not { } c)
        {
            return;
        }

        string name = _lastProcessSamples.FirstOrDefault(p => p.Pid == c.Pid)?.Name ?? $"pid {c.Pid}";
        DialogResult confirm = MessageBox.Show(this, $"End \"{name}\" (PID {c.Pid})?", "End Task", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(c.Pid);
            process.Kill();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not end process: {ex.Message}", "End Task", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void GoToSelectedConnectionProcess()
    {
        if (SelectedConnection() is not { } c)
        {
            return;
        }

        SelectPage(1);
        _processSearchBox.Text = c.Pid.ToString();
        for (int i = 0; i < _processGrid.Rows.Count; i++)
        {
            if (_processGrid.Rows[i].Tag is int pid && pid == c.Pid)
            {
                _processGrid.ClearSelection();
                _processGrid.Rows[i].Selected = true;
                _processGrid.FirstDisplayedScrollingRowIndex = i;
                break;
            }
        }
    }

    private void OpenFileLocationForSelectedConnection()
    {
        if (SelectedConnection() is { } c)
        {
            OpenFileLocationForPid(c.Pid);
        }
    }

    private void ShowSelectedConnectionProcessProperties()
    {
        if (SelectedConnection() is { } c)
        {
            ShowProcessPropertiesForPid(c.Pid);
        }
    }

    private static void UpdateSortGlyphs(DataGridView grid, string sortColumn, bool descending)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            string baseText = column.Tag as string ?? column.HeaderText;
            column.HeaderText = column.Name == sortColumn
                ? $"{baseText} {(descending ? "▼" : "▲")}"
                : baseText;
        }
    }

    private static List<T> Order<T, TKey>(IEnumerable<T> seq, Func<T, TKey> keySelector, bool descending, IComparer<TKey>? comparer = null)
    {
        IOrderedEnumerable<T> ordered = comparer is null
            ? (descending ? seq.OrderByDescending(keySelector) : seq.OrderBy(keySelector))
            : (descending ? seq.OrderByDescending(keySelector, comparer) : seq.OrderBy(keySelector, comparer));
        return ordered.ToList();
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    internal static string FormatBytesPerSec(long bytesPerSec) => $"{FormatBytes(bytesPerSec)}/s";

    private static long Sum((long Rx, long Tx) rates) => rates.Rx + rates.Tx;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _etwMonitor.Dispose();
        _hardwareMonitor.Dispose();
        base.OnFormClosed(e);
    }

    private sealed class DarkStatusStripColors : ProfessionalColorTable
    {
        public override Color StatusStripGradientBegin => HeaderBg;
        public override Color StatusStripGradientEnd => HeaderBg;
    }
}
