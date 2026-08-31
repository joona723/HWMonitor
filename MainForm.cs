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
    private readonly System.Windows.Forms.Timer _timer;

    private readonly PerformanceGraph _cpuGraph;
    private readonly PerformanceGraph _memGraph;
    private readonly PerformanceGraph _netGraph;
    private readonly PerformanceGraph _gpuGraph;

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

    private List<ProcessSample> _lastProcessSamples = new();
    private List<ConnectionInfo> _lastConnections = new();

    public MainForm()
    {
        Text = "HWMonitor - System Monitor";
        Size = new Size(1080, 700);
        MinimumSize = new Size(820, 520);
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

        _performancePage = new Panel { Dock = DockStyle.Fill, BackColor = WindowBg };
        _performancePage.Controls.Add(perfLayout);

        // --- Processes page -----------------------------------------------------
        (_processSearchBox, _processGrid) = BuildSearchableGrid(
            placeholder: "Filter by name or PID...",
            columns: [
                ("Name", "Name", 26),
                ("PID", "PID", 8),
                ("CPU", "CPU %", 10),
                ("Memory", "Memory", 14),
                ("Network", "Network", 16),
                ("Connections", "Connections", 12),
            ]);
        _processGrid.ColumnHeaderMouseClick += (_, e) => OnSortColumnClicked(_processGrid, e.ColumnIndex, ref _processSortColumn, ref _processSortDescending, RenderProcessRows);
        _processSearchBox.TextChanged += (_, _) => RenderProcessRows();
        AttachEndTaskMenu();
        _processesPage = BuildPage(_processSearchBox, _processGrid);

        // --- Connections page -----------------------------------------------------
        (_connectionSearchBox, _connectionGrid) = BuildSearchableGrid(
            placeholder: "Filter by process, IP, or port...",
            columns: [
                ("Protocol", "Proto", 8),
                ("PID", "PID", 8),
                ("Process", "Process", 18),
                ("Local", "Local Address", 20),
                ("Remote", "Remote Address", 20),
                ("State", "State", 12),
                ("Usage", "Usage", 12),
                ("Risk", "Risk", 22),
            ]);
        _connectionGrid.ColumnHeaderMouseClick += (_, e) => OnSortColumnClicked(_connectionGrid, e.ColumnIndex, ref _connectionSortColumn, ref _connectionSortDescending, RenderConnectionRows);
        _connectionSearchBox.TextChanged += (_, _) => RenderConnectionRows();
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
        _statusLabel = new ToolStripStatusLabel { ForeColor = TextSecondary, Text = "Starting..." };
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(bodyPanel);
        Controls.Add(headerPanel);
        Controls.Add(statusStrip);

        SelectPage(0);

        bool etwStarted = _etwMonitor.Start();
        _statusLabel.Text = etwStarted
            ? "Per-app network speed: live (ETW)"
            : $"Per-app network speed unavailable ({_etwMonitor.StartError}); showing connection counts only";

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();

        RefreshAll();
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
            sortDescending = clicked is not ("Name" or "Process" or "Protocol" or "Local" or "Remote" or "State");
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
        _netGraph.ValueText = $"↓{FormatBytesPerSec((long)stats.NetworkRxBytesPerSec)}  ↑{FormatBytesPerSec((long)stats.NetworkTxBytesPerSec)}";
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

        List<ProcessSample> ordered = _processSortColumn switch
        {
            "PID" => Order(filtered, p => p.Pid, _processSortDescending),
            "CPU" => Order(filtered, p => p.CpuPercent, _processSortDescending),
            "Memory" => Order(filtered, p => p.WorkingSetBytes, _processSortDescending),
            "Network" => Order(filtered, p => p.NetRxBytesPerSec + p.NetTxBytesPerSec, _processSortDescending),
            "Connections" => Order(filtered, p => p.ConnectionCount, _processSortDescending),
            _ => Order(filtered, p => p.Name, _processSortDescending, StringComparer.OrdinalIgnoreCase),
        };

        object? selectedPid = _processGrid.SelectedRows.Count > 0 ? _processGrid.SelectedRows[0].Tag : null;

        _processGrid.SuspendLayout();
        _processGrid.Rows.Clear();
        foreach (ProcessSample sample in ordered)
        {
            int rowIndex = _processGrid.Rows.Add(
                sample.Name,
                sample.Pid,
                $"{sample.CpuPercent:0.0}%",
                FormatBytes(sample.WorkingSetBytes),
                FormatBytesPerSec(sample.NetRxBytesPerSec + sample.NetTxBytesPerSec),
                sample.ConnectionCount);
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
                (pidNames.TryGetValue(c.Pid, out string? n) && n.Contains(filter, StringComparison.OrdinalIgnoreCase)));
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
            string usage = _etwMonitor.IsRunning ? $"↓{FormatBytesPerSec(rx)} ↑{FormatBytesPerSec(tx)}" : "n/a";
            int rowIndex = _connectionGrid.Rows.Add(c.Protocol, c.Pid, processName, local, remote, c.State, usage, reason ?? "");
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

    private static void UpdateSortGlyphs(DataGridView grid, string sortColumn, bool descending)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = column.Name == sortColumn
                ? (descending ? SortOrder.Descending : SortOrder.Ascending)
                : SortOrder.None;
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

    private static string FormatBytesPerSec(long bytesPerSec) => $"{FormatBytes(bytesPerSec)}/s";

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
