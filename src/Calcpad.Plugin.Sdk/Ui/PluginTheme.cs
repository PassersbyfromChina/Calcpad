using Avalonia.Media;

namespace Calcpad.Plugin.Sdk.Ui;

/// <summary>
/// The palette every built-in widget draws with. Mirrors the look of the iPad calculator:
/// near black surfaces, soft grey keys, a single warm accent for operators.
/// </summary>
public sealed class PluginTheme
{
    public bool IsDark { get; init; } = true;

    public Color CanvasBackground { get; init; } = Color.Parse("#FF0B0B0D");

    public Color Surface { get; init; } = Color.Parse("#FF1C1C1E");

    public Color SurfaceAlt { get; init; } = Color.Parse("#FF2C2C2E");

    public Color SurfaceRaised { get; init; } = Color.Parse("#FF3A3A3C");

    public Color TextPrimary { get; init; } = Color.Parse("#FFFFFFFF");

    public Color TextSecondary { get; init; } = Color.Parse("#FF9A9AA0");

    public Color Accent { get; init; } = Color.Parse("#FFFF9F0A");

    public Color AccentText { get; init; } = Color.Parse("#FFFFFFFF");

    public Color Function { get; init; } = Color.Parse("#FFA5A5A5");

    public Color Danger { get; init; } = Color.Parse("#FFFF453A");

    public Color Success { get; init; } = Color.Parse("#FF30D158");

    public Color GridLine { get; init; } = Color.Parse("#14FFFFFF");

    public Color Selection { get; init; } = Color.Parse("#FF0A84FF");

    public double CornerRadius { get; init; } = 14;

    public double KeyGap { get; init; } = 8;

    /// <summary>Gap the shell keeps between a widget's bounds and its drawn shadow.</summary>
    public double WidgetPadding { get; init; } = 10;

    public static PluginTheme IpadDark { get; } = new();

    public static PluginTheme IpadLight { get; } = new()
    {
        IsDark = false,
        CanvasBackground = Color.Parse("#FFF2F2F7"),
        Surface = Color.Parse("#FFFFFFFF"),
        SurfaceAlt = Color.Parse("#FFE5E5EA"),
        SurfaceRaised = Color.Parse("#FFD1D1D6"),
        TextPrimary = Color.Parse("#FF1C1C1E"),
        TextSecondary = Color.Parse("#FF6C6C70"),
        Accent = Color.Parse("#FFFF9500"),
        Function = Color.Parse("#FF8E8E93"),
        GridLine = Color.Parse("#14000000")
    };

    public IBrush Brush(Color color) => new SolidColorBrush(color);

    public IBrush BackgroundBrush => Brush(CanvasBackground);

    public IBrush SurfaceBrush => Brush(Surface);

    public IBrush SurfaceAltBrush => Brush(SurfaceAlt);

    public IBrush TextBrush => Brush(TextPrimary);
}
