using System.Drawing;
using System.Text.Json;

namespace HWMonitor;

enum DisplayMode
{
    Both,
    CpuOnly,
    GpuOnly,
}

sealed class TraySettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HWMonitor", "settings.json");

    public DisplayMode Mode { get; set; } = DisplayMode.Both;

    public int CpuColorArgb { get; set; } = Color.White.ToArgb();

    public int GpuColorArgb { get; set; } = Color.White.ToArgb();

    public float? FontSizePx { get; set; } = null;

    public FontStyle Style { get; set; } = FontStyle.Bold;

    public float OffsetX { get; set; } = 0f;

    public float OffsetY { get; set; } = 0f;

    public int BoostMode { get; set; } = 1;

    public bool AutoUpdateCheck { get; set; } = true;

    public static TraySettings Load()
    {
        try
        {
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<TraySettings>(json) ?? new TraySettings();
        }
        catch
        {
            return new TraySettings();
        }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best-effort; a failed save just means the mode resets next launch.
        }
    }
}
