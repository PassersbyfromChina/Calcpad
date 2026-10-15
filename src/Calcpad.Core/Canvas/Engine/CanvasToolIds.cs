namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Well known canvas tool ids. The built-in CanvasTools plugin contributes these tools and the
/// canvas view implements their behaviour, so both sides share one vocabulary instead of
/// duplicating string literals.
/// </summary>
public static class CanvasToolIds
{
    /// <summary>Selection and move tool. This is the idle tool of the canvas.</summary>
    public const string Select = "canvas.tool.select";

    /// <summary>Freehand pen.</summary>
    public const string Pen = "canvas.tool.pen";

    /// <summary>Wide, translucent highlighter.</summary>
    public const string Highlighter = "canvas.tool.highlighter";

    /// <summary>Stroke eraser.</summary>
    public const string Eraser = "canvas.tool.eraser";

    /// <summary>Freeform lasso selection.</summary>
    public const string Lasso = "canvas.tool.lasso";

    /// <summary>Rectangular (marquee) selection.</summary>
    public const string RectangleSelect = "canvas.tool.select.rect";

    /// <summary>Text insertion and inline editing.</summary>
    public const string Text = "canvas.tool.text";

    /// <summary>Shape drawing; the concrete shape comes from the view's active shape kind.</summary>
    public const string Shape = "canvas.tool.shape";

    /// <summary>Explicit pan tool; panning is also available with space, middle button or two fingers.</summary>
    public const string Pan = "canvas.tool.pan";

    /// <summary>Every id declared by this class, in palette order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Select,
        Pen,
        Highlighter,
        Eraser,
        Lasso,
        RectangleSelect,
        Text,
        Shape,
        Pan
    ];

    /// <summary>True when <paramref name="toolId"/> is one of the built-in tools.</summary>
    public static bool IsBuiltIn(string? toolId) => toolId is not null && All.Contains(toolId, StringComparer.Ordinal);
}
