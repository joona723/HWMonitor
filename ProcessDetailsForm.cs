namespace HWMonitor;

/// <summary>Read-only "Properties" dialog for a single process: identity, path, publisher, integrity, modules, windows.</summary>
sealed class ProcessDetailsForm : Form
{
    public ProcessDetailsForm(ProcessDetails details)
    {
        Text = $"{details.Name} (PID {details.Pid}) Properties";
        Width = 640;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(480, 400);

        var infoLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 6),
        };
        infoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        infoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(infoLayout, "Path", details.Path ?? "(unavailable)");
        AddRow(infoLayout, "Command line", details.CommandLine ?? "(unavailable)");
        AddRow(infoLayout, "Publisher", details.Publisher ?? "(unsigned or unknown)");
        AddRow(infoLayout, "Integrity level", details.IntegrityLevel ?? "(unavailable)");
        AddRow(infoLayout, "User", details.UserName ?? "(unavailable)");
        AddRow(infoLayout, "Parent PID", details.ParentPid > 0 ? details.ParentPid.ToString() : "(unknown / exited)");
        AddRow(infoLayout, "Started", details.StartTime is { } started ? started.ToString("g") : "(unavailable)");

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var modulesTab = new TabPage($"Modules ({details.Modules.Count})");
        modulesTab.Controls.Add(BuildListTextBox(details.Modules, "(no modules visible - process may be protected or 32/64-bit mismatched)"));
        tabs.TabPages.Add(modulesTab);

        var windowsTab = new TabPage($"Windows ({details.Windows.Count})");
        windowsTab.Controls.Add(BuildListTextBox(details.Windows, "(no visible top-level windows)"));
        tabs.TabPages.Add(windowsTab);

        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 12, 6) };
        buttonPanel.Controls.Add(closeButton);

        Controls.Add(tabs);
        Controls.Add(infoLayout);
        Controls.Add(buttonPanel);
        AcceptButton = closeButton;
    }

    private static void AddRow(TableLayoutPanel layout, string label, string value)
    {
        int row = layout.RowCount;
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = label, AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Margin = new Padding(0, 3, 6, 3) }, 0, row);
        layout.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 3, 0, 3) }, 1, row);
    }

    private static TextBox BuildListTextBox(IReadOnlyList<string> items, string emptyText)
    {
        return new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = items.Count > 0 ? string.Join("\r\n", items) : emptyText,
        };
    }
}
