using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace HWMonitor;

sealed class TrayApplicationContext : ApplicationContext
{
    private const string StartupTaskName = "HWMonitor";

    private readonly HardwareMonitor _monitor = new();
    private readonly TraySettings _settings = TraySettings.Load();

    private readonly NotifyIcon _cpuIcon;
    private readonly NotifyIcon _gpuIcon;
    private readonly ToolStripMenuItem _cpuMenuItem;
    private readonly ToolStripMenuItem _gpuMenuItem;
    private readonly ToolStripMenuItem _modeBothItem;
    private readonly ToolStripMenuItem _modeCpuItem;
    private readonly ToolStripMenuItem _modeGpuItem;
    private readonly ToolStripMenuItem _styleRegularItem;
    private readonly ToolStripMenuItem _styleBoldItem;
    private readonly ToolStripMenuItem _styleItalicItem;
    private readonly ToolStripMenuItem _styleBoldItalicItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _boostDisabledItem;
    private readonly ToolStripMenuItem _boostEnabledItem;
    private readonly ToolStripMenuItem _boostAggressiveItem;
    private readonly System.Windows.Forms.Timer _timer;
    private MainForm? _mainForm;

    public TrayApplicationContext()
    {
        _cpuMenuItem = new ToolStripMenuItem("CPU: --") { Enabled = false };
        _gpuMenuItem = new ToolStripMenuItem("GPU: --") { Enabled = false };

        _modeBothItem = new ToolStripMenuItem("Show CPU + GPU", null, (_, _) => SetMode(DisplayMode.Both));
        _modeCpuItem = new ToolStripMenuItem("Show CPU only", null, (_, _) => SetMode(DisplayMode.CpuOnly));
        _modeGpuItem = new ToolStripMenuItem("Show GPU only", null, (_, _) => SetMode(DisplayMode.GpuOnly));

        _styleRegularItem = new ToolStripMenuItem("Regular", null, (_, _) => SetStyle(FontStyle.Regular));
        _styleBoldItem = new ToolStripMenuItem("Bold", null, (_, _) => SetStyle(FontStyle.Bold));
        _styleItalicItem = new ToolStripMenuItem("Italic", null, (_, _) => SetStyle(FontStyle.Italic));
        _styleBoldItalicItem = new ToolStripMenuItem("Bold Italic", null, (_, _) => SetStyle(FontStyle.Bold | FontStyle.Italic));
        var styleMenu = new ToolStripMenuItem("Font Style");
        styleMenu.DropDownItems.Add(_styleRegularItem);
        styleMenu.DropDownItems.Add(_styleBoldItem);
        styleMenu.DropDownItems.Add(_styleItalicItem);
        styleMenu.DropDownItems.Add(_styleBoldItalicItem);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_cpuMenuItem);
        menu.Items.Add(_gpuMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_modeBothItem);
        menu.Items.Add(_modeCpuItem);
        menu.Items.Add(_modeGpuItem);
        menu.Items.Add(new ToolStripSeparator());
        var colorMenu = new ToolStripMenuItem("Choose Color");
        colorMenu.DropDownItems.Add("CPU Color...", null, (_, _) => ChooseColor(isCpu: true));
        colorMenu.DropDownItems.Add("GPU Color...", null, (_, _) => ChooseColor(isCpu: false));
        menu.Items.Add(colorMenu);
        menu.Items.Add(styleMenu);
        menu.Items.Add("Set Font Size (px)...", null, (_, _) => ShowFontSizeDialog());
        menu.Items.Add("Auto Font Size", null, (_, _) => SetFontSizePx(null));
        menu.Items.Add("Set Position Offset...", null, (_, _) => ShowOffsetDialog());
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        _boostDisabledItem = new ToolStripMenuItem("Disabled", null, (_, _) => SetBoost(0));
        _boostEnabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => SetBoost(1));
        _boostAggressiveItem = new ToolStripMenuItem("Aggressive", null, (_, _) => SetBoost(2));
        var boostMenu = new ToolStripMenuItem("CPU Boost Mode");
        boostMenu.DropDownItems.Add(_boostDisabledItem);
        boostMenu.DropDownItems.Add(_boostEnabledItem);
        boostMenu.DropDownItems.Add(_boostAggressiveItem);
        menu.Items.Add(boostMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Diagnostics...", null, (_, _) => ShowDiagnostics());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _cpuIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "HWMonitor CPU",
        };
        _gpuIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = "HWMonitor GPU",
        };
        _cpuIcon.MouseClick += OnTrayIconMouseClick;
        _gpuIcon.MouseClick += OnTrayIconMouseClick;

        UpdateModeChecks();
        UpdateStyleChecks();
        ApplyDisplayMode();
        UpdateBoostChecks();
        ApplyBoostMode(_settings.BoostMode);
        _startupItem.Checked = IsStartupTaskRegistered();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm();
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }
        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private void ExitApplication()
    {
        _mainForm?.Close();
        ExitThread();
    }

    private void SetMode(DisplayMode mode)
    {
        _settings.Mode = mode;
        _settings.Save();
        UpdateModeChecks();
        ApplyDisplayMode();
        Refresh();
    }

    private void ChooseColor(bool isCpu)
    {
        using var dialog = new ColorDialog
        {
            Color = Color.FromArgb(isCpu ? _settings.CpuColorArgb : _settings.GpuColorArgb),
            FullOpen = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        if (isCpu)
        {
            _settings.CpuColorArgb = dialog.Color.ToArgb();
        }
        else
        {
            _settings.GpuColorArgb = dialog.Color.ToArgb();
        }
        _settings.Save();
        Refresh();
    }

    private void UpdateModeChecks()
    {
        _modeBothItem.Checked = _settings.Mode == DisplayMode.Both;
        _modeCpuItem.Checked = _settings.Mode == DisplayMode.CpuOnly;
        _modeGpuItem.Checked = _settings.Mode == DisplayMode.GpuOnly;
    }

    private void SetStyle(FontStyle style)
    {
        _settings.Style = style;
        _settings.Save();
        UpdateStyleChecks();
        Refresh();
    }

    private void UpdateStyleChecks()
    {
        _styleRegularItem.Checked = _settings.Style == FontStyle.Regular;
        _styleBoldItem.Checked = _settings.Style == FontStyle.Bold;
        _styleItalicItem.Checked = _settings.Style == FontStyle.Italic;
        _styleBoldItalicItem.Checked = _settings.Style == (FontStyle.Bold | FontStyle.Italic);
    }

    private void SetFontSizePx(float? px)
    {
        _settings.FontSizePx = px is null ? null : Math.Clamp(px.Value, 4f, 64f);
        _settings.Save();
        Refresh();
    }

    private float GetAutoFitSizePx()
    {
        Size iconSize = SystemInformation.SmallIconSize;
        int width = Math.Max(iconSize.Width, 16);
        int height = Math.Max(iconSize.Height, 16);
        using var bitmap = new Bitmap(width, height);
        using Graphics g = Graphics.FromImage(bitmap);
        return ComputeAutoFitSize(g, "88", _settings.Style, width, height);
    }

    private void ShowFontSizeDialog()
    {
        float current = _settings.FontSizePx ?? GetAutoFitSizePx();

        using var form = new Form
        {
            Text = "Set Font Size",
            Width = 240,
            Height = 150,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var label = new Label { Text = "Font size (px):", Dock = DockStyle.Top, AutoSize = false, Height = 24 };
        var upDown = new NumericUpDown
        {
            Minimum = 4,
            Maximum = 64,
            DecimalPlaces = 1,
            Increment = 0.5m,
            Value = (decimal)Math.Clamp(current, 4f, 64f),
            Dock = DockStyle.Top,
        };
        upDown.ValueChanged += (_, _) => SetFontSizePx((float)upDown.Value);

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var autoButton = new Button { Text = "Auto" };
        autoButton.Click += (_, _) =>
        {
            SetFontSizePx(null);
            upDown.Value = (decimal)Math.Clamp(GetAutoFitSizePx(), 4f, 64f);
        };
        buttonPanel.Controls.Add(okButton);
        buttonPanel.Controls.Add(autoButton);

        form.Controls.Add(upDown);
        form.Controls.Add(label);
        form.Controls.Add(buttonPanel);
        form.AcceptButton = okButton;

        form.ShowDialog();
    }

    private void ShowOffsetDialog()
    {
        using var form = new Form
        {
            Text = "Set Position Offset",
            Width = 240,
            Height = 190,
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var xLabel = new Label { Text = "X offset (px):", Dock = DockStyle.Top, Height = 24 };
        var xUpDown = new NumericUpDown
        {
            Minimum = -16,
            Maximum = 16,
            DecimalPlaces = 1,
            Increment = 0.5m,
            Value = (decimal)Math.Clamp(_settings.OffsetX, -16f, 16f),
            Dock = DockStyle.Top,
        };
        var yLabel = new Label { Text = "Y offset (px):", Dock = DockStyle.Top, Height = 24 };
        var yUpDown = new NumericUpDown
        {
            Minimum = -16,
            Maximum = 16,
            DecimalPlaces = 1,
            Increment = 0.5m,
            Value = (decimal)Math.Clamp(_settings.OffsetY, -16f, 16f),
            Dock = DockStyle.Top,
        };

        xUpDown.ValueChanged += (_, _) =>
        {
            _settings.OffsetX = (float)xUpDown.Value;
            _settings.Save();
            Refresh();
        };
        yUpDown.ValueChanged += (_, _) =>
        {
            _settings.OffsetY = (float)yUpDown.Value;
            _settings.Save();
            Refresh();
        };

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var resetButton = new Button { Text = "Reset" };
        resetButton.Click += (_, _) =>
        {
            xUpDown.Value = 0m;
            yUpDown.Value = 0m;
        };
        buttonPanel.Controls.Add(okButton);
        buttonPanel.Controls.Add(resetButton);

        form.Controls.Add(yUpDown);
        form.Controls.Add(yLabel);
        form.Controls.Add(xUpDown);
        form.Controls.Add(xLabel);
        form.Controls.Add(buttonPanel);
        form.AcceptButton = okButton;

        form.ShowDialog();
    }

    private void SetBoost(int mode)
    {
        _settings.BoostMode = mode;
        _settings.Save();
        UpdateBoostChecks();
        ApplyBoostMode(mode);
    }

    private void UpdateBoostChecks()
    {
        _boostDisabledItem.Checked = _settings.BoostMode == 0;
        _boostEnabledItem.Checked = _settings.BoostMode == 1;
        _boostAggressiveItem.Checked = _settings.BoostMode == 2;
    }

    private static void ApplyBoostMode(int mode)
    {
        try
        {
            RunPowercfg($"/setacvalueindex scheme_current sub_processor PERFBOOSTMODE {mode}");
            RunPowercfg($"/setdcvalueindex scheme_current sub_processor PERFBOOSTMODE {mode}");
            RunPowercfg("/setactive scheme_current");
        }
        catch
        {
            // Best-effort; the checkmark still reflects the user's chosen setting.
        }
    }

    private static void RunPowercfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process? proc = Process.Start(psi);
        proc?.WaitForExit();
    }

    private void ToggleStartup()
    {
        bool enable = !_startupItem.Checked;
        SetStartupTaskRegistered(enable);
        _startupItem.Checked = IsStartupTaskRegistered();
    }

    private static bool IsStartupTaskRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{StartupTaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartupTaskRegistered(bool enable)
    {
        try
        {
            string args = enable
                ? $"/Create /TN \"{StartupTaskName}\" /TR \"\\\"{Application.ExecutablePath}\\\"\" /SC ONLOGON /RL HIGHEST /F"
                : $"/Delete /TN \"{StartupTaskName}\" /F";

            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch
        {
            // Best-effort; the checkbox will just reflect whatever schtasks actually did.
        }
    }

    private void ApplyDisplayMode()
    {
        _cpuIcon.Visible = _settings.Mode is DisplayMode.Both or DisplayMode.CpuOnly;
        _gpuIcon.Visible = _settings.Mode is DisplayMode.Both or DisplayMode.GpuOnly;
    }

    private void Refresh()
    {
        Reading reading;
        try
        {
            reading = _monitor.Read();
        }
        catch (Exception ex)
        {
            _cpuMenuItem.Text = "CPU: error";
            _gpuMenuItem.Text = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
            _cpuIcon.Text = "HWMonitor: sensor error";
            _gpuIcon.Text = "HWMonitor: sensor error";
            return;
        }

        string cpuText = Format(reading.CpuTempC);
        string gpuText = Format(reading.GpuTempC);
        string cpuFanText = FormatFan(reading.CpuFanRpm);
        string gpuFanText = FormatFan(reading.GpuFanRpm);

        _cpuMenuItem.Text = $"CPU: {cpuText}";
        _gpuMenuItem.Text = $"GPU: {gpuText}";

        _cpuIcon.Text = $"CPU: {cpuText}\nFan: {cpuFanText}";
        _gpuIcon.Text = $"GPU: {gpuText}\nFan: {gpuFanText}";

        Color cpuColor = Color.FromArgb(_settings.CpuColorArgb);
        Color gpuColor = Color.FromArgb(_settings.GpuColorArgb);
        UpdateIcon(_cpuIcon, reading.CpuTempC, cpuColor, _settings.Style, _settings.FontSizePx, _settings.OffsetX, _settings.OffsetY);
        UpdateIcon(_gpuIcon, reading.GpuTempC, gpuColor, _settings.Style, _settings.FontSizePx, _settings.OffsetX, _settings.OffsetY);
    }

    private static void UpdateIcon(NotifyIcon notifyIcon, float? tempC, Color color, FontStyle style, float? fontSizePx, float offsetX, float offsetY)
    {
        Icon? oldIcon = notifyIcon.Icon;
        notifyIcon.Icon = BuildIcon(tempC, color, style, fontSizePx, offsetX, offsetY);
        if (!ReferenceEquals(oldIcon, SystemIcons.Application))
        {
            oldIcon?.Dispose();
        }
    }

    private static string Format(float? tempC) => tempC is { } value ? $"{value:0}°C" : "n/a";

    private static string FormatFan(float? rpm) => rpm is { } value ? $"{value:0} RPM" : "n/a";

    private void ShowDiagnostics()
    {
        string dump;
        try
        {
            dump = _monitor.DumpSensors();
        }
        catch (Exception ex)
        {
            dump = $"Failed to read sensors:\n{ex}";
        }

        using var form = new Form
        {
            Text = "HWMonitor Diagnostics",
            Width = 700,
            Height = 500,
            StartPosition = FormStartPosition.CenterScreen,
        };
        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = dump.Replace("\n", "\r\n"),
        };
        form.Controls.Add(textBox);
        form.ShowDialog();
    }

    private static float ComputeAutoFitSize(Graphics g, string label, FontStyle style, int width, int height)
    {
        float autoFitSize = height * 1.4f;
        while (true)
        {
            using var probe = new Font("Segoe UI", autoFitSize, style, GraphicsUnit.Pixel);
            SizeF measured = g.MeasureString(label, probe);
            if ((measured.Width <= width && measured.Height <= height) || autoFitSize <= 5f)
            {
                break;
            }
            autoFitSize -= 0.5f;
        }
        return autoFitSize;
    }

    private static Icon BuildIcon(float? tempC, Color color, FontStyle style, float? fontSizePx, float offsetX, float offsetY)
    {
        Size iconSize = SystemInformation.SmallIconSize;
        int width = Math.Max(iconSize.Width, 16);
        int height = Math.Max(iconSize.Height, 16);

        string label = tempC is { } value ? $"{value:0}" : "?";
        var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        float baseFontSize;
        using (var measureBitmap = new Bitmap(width, height))
        using (Graphics mg = Graphics.FromImage(measureBitmap))
        {
            baseFontSize = Math.Max(fontSizePx ?? ComputeAutoFitSize(mg, label, style, width, height), 4f);
        }

        // Render at a higher resolution and downscale; GDI+ anti-aliasing on a true
        // 16px canvas leaves gaps in thin strokes, this smooths them out instead.
        const int supersample = 4;
        int bigWidth = width * supersample;
        int bigHeight = height * supersample;

        using var bigBitmap = new Bitmap(bigWidth, bigHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bigBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            using var font = new Font("Segoe UI", baseFontSize * supersample, style, GraphicsUnit.Pixel);
            g.DrawString(label, font, brush, new RectangleF(offsetX * supersample, offsetY * supersample, bigWidth, bigHeight), format);
        }

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g2 = Graphics.FromImage(bitmap))
        {
            g2.CompositingMode = CompositingMode.SourceCopy;
            g2.CompositingQuality = CompositingQuality.HighQuality;
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.SmoothingMode = SmoothingMode.HighQuality;
            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g2.DrawImage(bigBitmap, new Rectangle(0, 0, width, height));
        }

        nint hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _cpuIcon.Visible = false;
            _cpuIcon.Dispose();
            _gpuIcon.Visible = false;
            _gpuIcon.Dispose();
            _monitor.Dispose();
            _mainForm?.Dispose();
        }
        base.Dispose(disposing);
    }
}

static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool DestroyIcon(nint hIcon);
}
