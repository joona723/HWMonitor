namespace HWMonitor;

/// <summary>
/// Single source for the download/upload glyphs shown next to network rates, so every place that
/// displays a rate (connections grid, dashboard graph, bandwidth tracker) stays visually consistent.
/// Solid triangles instead of thin arrow glyphs (↓/↑) - bolder and easier to tell apart at small sizes.
/// </summary>
static class NetRateGlyphs
{
    public const string Download = "▼";
    public const string Upload = "▲";
}
