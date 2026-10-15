using Calcpad.Core.Canvas.Engine;

namespace Calcpad.Plugin.CanvasTools;

/// <summary>
/// Command ids contributed by the canvas tools plugin. The shell dispatches them; every command
/// reaches the canvas through the permission checked <c>ICanvasApi</c> only.
/// </summary>
public static class CanvasToolsCommands
{
    /// <summary>Prefix shared by every command of this plugin.</summary>
    public const string Prefix = "calcpad.canvastools.";

    /// <summary>Activates the selection / move tool.</summary>
    public const string ToolSelect = Prefix + "tool.select";

    /// <summary>Activates the freehand pen.</summary>
    public const string ToolPen = Prefix + "tool.pen";

    /// <summary>Activates the highlighter.</summary>
    public const string ToolHighlighter = Prefix + "tool.highlighter";

    /// <summary>Activates the stroke eraser.</summary>
    public const string ToolEraser = Prefix + "tool.eraser";

    /// <summary>Activates the freeform lasso.</summary>
    public const string ToolLasso = Prefix + "tool.lasso";

    /// <summary>Activates the rectangular marquee.</summary>
    public const string ToolRectangleSelect = Prefix + "tool.rect";

    /// <summary>Activates the text tool.</summary>
    public const string ToolText = Prefix + "tool.text";

    /// <summary>Activates the shape tool.</summary>
    public const string ToolShape = Prefix + "tool.shape";

    /// <summary>Frames everything on the canvas.</summary>
    public const string ViewZoomFit = Prefix + "view.zoomFit";

    /// <summary>Maps a tool id to the command that activates it.</summary>
    public static string? CommandForTool(string toolId) => toolId switch
    {
        CanvasToolIds.Select => ToolSelect,
        CanvasToolIds.Pen => ToolPen,
        CanvasToolIds.Highlighter => ToolHighlighter,
        CanvasToolIds.Eraser => ToolEraser,
        CanvasToolIds.Lasso => ToolLasso,
        CanvasToolIds.RectangleSelect => ToolRectangleSelect,
        CanvasToolIds.Text => ToolText,
        CanvasToolIds.Shape => ToolShape,
        _ => null
    };

    /// <summary>Maps an activation command back to the tool it selects, or null.</summary>
    public static string? ToolForCommand(string commandId) => commandId switch
    {
        ToolSelect => CanvasToolIds.Select,
        ToolPen => CanvasToolIds.Pen,
        ToolHighlighter => CanvasToolIds.Highlighter,
        ToolEraser => CanvasToolIds.Eraser,
        ToolLasso => CanvasToolIds.Lasso,
        ToolRectangleSelect => CanvasToolIds.RectangleSelect,
        ToolText => CanvasToolIds.Text,
        ToolShape => CanvasToolIds.Shape,
        _ => null
    };

    /// <summary>Every command id this plugin answers to.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ToolSelect,
        ToolPen,
        ToolHighlighter,
        ToolEraser,
        ToolLasso,
        ToolRectangleSelect,
        ToolText,
        ToolShape,
        ViewZoomFit
    ];
}
