using Calcpad.Core.Canvas;
using Calcpad.Core.Canvas.Engine;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.Plugin.CanvasTools;

/// <summary>
/// Built-in plugin that owns the canvas tools: pen, highlighter, eraser, lasso, rectangular marquee,
/// text and shapes. Each tool is a <see cref="ToolContribution"/> plus a menu entry and a portable
/// shortcut, and the plugin only ever talks to the canvas through the permission checked
/// <see cref="ICanvasApi"/>, so it has no special access compared with a third party plugin.
/// </summary>
public sealed class CanvasToolsPlugin : IPlugin
{
    /// <summary>Reverse-DNS plugin id.</summary>
    public const string PluginId = "calcpad.canvastools";

    private static readonly ToolSpec[] ToolSpecs =
    [
        new(CanvasToolIds.Select, "Select", "选择", CanvasToolsCommands.ToolSelect, "Alt+V", "\u2316", 10),
        new(CanvasToolIds.Pen, "Pen", "钢笔", CanvasToolsCommands.ToolPen, "Alt+P", "\u270E", 20),
        new(CanvasToolIds.Highlighter, "Highlighter", "荧光笔", CanvasToolsCommands.ToolHighlighter, "Alt+H", "\u25AC", 30),
        new(CanvasToolIds.Eraser, "Eraser", "橡皮", CanvasToolsCommands.ToolEraser, "Alt+E", "\u232B", 40),
        new(CanvasToolIds.Lasso, "Lasso select", "套索选择", CanvasToolsCommands.ToolLasso, "Alt+L", "\u25CC", 50),
        new(CanvasToolIds.RectangleSelect, "Rectangle select", "矩形选择", CanvasToolsCommands.ToolRectangleSelect, "Alt+R", "\u25AD", 60),
        new(CanvasToolIds.Text, "Text", "文本", CanvasToolsCommands.ToolText, "Alt+T", "T", 70),
        new(CanvasToolIds.Shape, "Shape", "形状", CanvasToolsCommands.ToolShape, "Alt+U", "\u25C7", 80)
    ];

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = CreateManifest();

    /// <summary>Ids of the tools this plugin contributes, in palette order.</summary>
    public static IReadOnlyList<string> ToolIds { get; } = [.. ToolSpecs.Select(t => t.Id)];

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Log(ToastLevel.Info, $"{PluginId} initialized with {ToolSpecs.Length} canvas tools.");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ShutdownAsync(CancellationToken ct = default)
    {
        _context = null;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteCommandAsync(CommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var canvas = _context?.Canvas;
        if (canvas is null)
        {
            return;
        }

        try
        {
            var toolId = CanvasToolsCommands.ToolForCommand(context.CommandId);
            if (toolId is not null)
            {
                var activated = await canvas.ActivateToolAsync(toolId, ct).ConfigureAwait(false);
                if (!activated)
                {
                    Log(ToastLevel.Warning, $"The canvas did not accept tool '{toolId}'.");
                }

                return;
            }

            switch (context.CommandId)
            {
                case CanvasToolsCommands.ViewZoomFit:
                {
                    var snapshot = await canvas.SnapshotAsync(new CanvasQuery { IncludeStrokes = false }, ct).ConfigureAwait(false);
                    if (!snapshot.ContentBounds.IsEmpty)
                    {
                        await canvas.RevealAsync(snapshot.ContentBounds, ct).ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        catch (PermissionDeniedException ex)
        {
            _context?.Log.Warn($"Canvas tools command '{context.CommandId}' was refused: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // The shell cancelled the command.
        }
        catch (Exception ex)
        {
            _context?.Log.Error($"Canvas tools command '{context.CommandId}' failed.", ex);
        }
    }

    private static PluginManifest CreateManifest()
    {
        var menus = new List<MenuContribution>();
        var shortcuts = new List<ShortcutContribution>();
        var tools = new List<ToolContribution>();

        foreach (var spec in ToolSpecs)
        {
            tools.Add(new ToolContribution(
                spec.Id,
                spec.Title,
                spec.CommandId,
                spec.Shortcut,
                spec.Icon,
                spec.Order,
                spec.TitleZh));

            shortcuts.Add(new ShortcutContribution(
                spec.Shortcut,
                spec.CommandId,
                spec.Title,
                "canvas",
                spec.TitleZh));

            menus.Add(new MenuContribution(
                $"视图/工具/{spec.Title}",
                spec.Title,
                spec.CommandId,
                spec.Shortcut,
                spec.Icon,
                spec.Order,
                RequiresCanvasWrite: true,
                LocalizedTitleZh: spec.TitleZh));
        }

        // Insertion style entries, so the tools that create content are also reachable from 插入.
        menus.Add(new MenuContribution(
            "插入/文本",
            "Text",
            CanvasToolsCommands.ToolText,
            "Alt+T",
            "T",
            10,
            RequiresCanvasWrite: true,
            LocalizedTitleZh: "文本"));
        menus.Add(new MenuContribution(
            "插入/形状",
            "Shape",
            CanvasToolsCommands.ToolShape,
            "Alt+U",
            "\u25C7",
            20,
            RequiresCanvasWrite: true,
            LocalizedTitleZh: "形状"));
        menus.Add(new MenuContribution(
            "视图/适应画布内容",
            "Fit canvas content",
            CanvasToolsCommands.ViewZoomFit,
            null,
            null,
            60,
            LocalizedTitleZh: "适应画布内容"));

        return new PluginManifest
        {
            Id = PluginId,
            Name = "Canvas Tools",
            Version = "1.0.0",
            Description = "Pen, highlighter, eraser, lasso, marquee, text and shape tools for the infinite canvas, plus the PNG rasteriser used by handwriting recognition and ask-AI.",
            Author = "PassersbyfromChina",
            Icon = "\u270E",
            IsCore = true,
            EnabledByDefault = true,
            Permissions =
            [
                PermissionKind.CanvasRead,
                PermissionKind.CanvasWrite,
                PermissionKind.ShellIntegration
            ],
            Keywords = ["canvas", "pen", "ink", "highlighter", "eraser", "lasso", "select", "text", "shape", "raster", "screenshot"],
            Tools = tools,
            Shortcuts = shortcuts,
            Menus = menus
        };
    }

    private void Log(ToastLevel level, string message)
    {
        switch (level)
        {
            case ToastLevel.Warning:
                _context?.Log.Warn(message);
                break;

            case ToastLevel.Error:
                _context?.Log.Error(message);
                break;

            default:
                _context?.Log.Info(message);
                break;
        }
    }

    private sealed record ToolSpec(
        string Id,
        string Title,
        string TitleZh,
        string CommandId,
        string Shortcut,
        string Icon,
        int Order);
}
