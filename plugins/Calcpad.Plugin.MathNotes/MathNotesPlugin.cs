using Avalonia.Controls;
using Avalonia.Layout;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.MathNotes;

/// <summary>
/// The built in math notes plugin. It contributes the handwritten-and-typed math block as a canvas
/// widget, with live evaluation, shared variables, plotting and click-to-fill results.
/// </summary>
public sealed class MathNotesPlugin : IPlugin
{
    /// <summary>Command that places a math notes block on the canvas.</summary>
    public const string InsertCommandId = "calcpad.math.notes.insert";

    /// <summary>Command that opens the math notes panel.</summary>
    public const string PanelCommandId = "calcpad.math.notes.panel";

    /// <summary>
    /// Cross plugin command: turns text (for example handwriting recognition output) into a math
    /// notes block, or appends it to a block given by <c>widgetId</c>.
    /// </summary>
    public const string AppendCommandId = "calcpad.math.notes.append";

    /// <summary>Panel id contributed by this plugin.</summary>
    public const string PanelId = "calcpad.math.notes.panel";

    /// <summary>Widget type contributed by this plugin.</summary>
    public const string WidgetTypeId = "calcpad.math.notes";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.math.notes",
        Name = "Math Notes",
        Version = "0.0.0",
        Description = "Multi line math notes with live evaluation, shared variables and function plotting. 数学笔记：逐行实时求值、变量共享、函数绘图。",
        Icon = "notes",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["math", "notes", "graph", "plot", "笔记", "绘图", "函数"],
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "Math Notes", InsertCommandId, "Ctrl+4", "notes", 130, false, true, "数学笔记"),
            new MenuContribution("Insert/Calculator", "Math Notes from selection", AppendCommandId, null, "notes", 131, false, true, "从选区生成数学笔记"),
            new MenuContribution("View/Panels", "Math Notes", PanelCommandId, null, "notes", 130, false, false, "数学笔记面板")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+4", InsertCommandId, "Insert math notes", null, "插入数学笔记"),
            new ShortcutContribution("Ctrl+Shift+4", PanelCommandId, "Show math notes", null, "显示数学笔记面板")
        ],
        Panels =
        [
            new PanelContribution(PanelId, "Math Notes", PanelCommandId, PanelPlacement.Right, 130, 380, "数学笔记")
        ],
        Widgets =
        [
            new WidgetContribution(WidgetTypeId, "Math Notes", InsertCommandId, 480, 440, "数学笔记")
        ]
    };

    /// <summary>The renderer this plugin registers; exposed so headless hosts can drive it.</summary>
    public MathNotesWidget Renderer { get; } = new();

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        if (context is IPluginUiContext ui)
        {
            ui.Ui.RegisterWidget(Renderer);
            ui.Ui.RegisterPanel(PanelId, () => BuildPanel(context));
        }

        context.Log.Info("MathNotes ready (数学笔记已就绪)");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ShutdownAsync(CancellationToken ct = default)
    {
        _context = null;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ExecuteCommandAsync(CommandContext context, CancellationToken ct = default) =>
        context.CommandId switch
        {
            InsertCommandId => InsertAsync(context, ct),
            AppendCommandId => AppendAsync(context, ct),
            PanelCommandId => OpenPanelAsync(ct),
            _ => ValueTask.CompletedTask
        };

    /// <summary>Creates and attaches a widget object, ready to be added to the canvas.</summary>
    public WidgetObject CreateWidget(string? initialText = null)
    {
        var camera = _context?.Canvas.Camera;
        var widget = new WidgetObject
        {
            WidgetType = WidgetTypeId,
            Title = "Math Notes / 数学笔记",
            OwnerPluginId = Manifest.Id,
            Bounds = new CRect((camera?.OffsetX ?? 0) + 200, (camera?.OffsetY ?? 0) + 200, 480, 440)
        };
        Renderer.InitializeState(widget);
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            var model = Calcpad.Plugin.MathNotes.MathNotesModel.From(widget, new Calcpad.Core.Numerics.ExpressionEngine());
            foreach (var line in initialText.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                model.AppendText(line.Trim());
                model.NewLine();
            }

            model.Save(widget.State!);
        }

        return widget;
    }

    private async ValueTask InsertAsync(CommandContext command, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            var widget = CreateWidget();
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(widget.Bounds, ct).ConfigureAwait(false);
            await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
            plugin.Log.Info($"Math notes inserted ({command.Trigger})");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not insert the math notes block", ex);
        }
    }

    /// <summary>
    /// Turns recognized or typed text into a notes block. Other plugins (handwriting recognition,
    /// the AI card) call this through <c>calcpad.math.notes.append</c> with a <c>text</c> argument.
    /// </summary>
    private async ValueTask AppendAsync(CommandContext command, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            command.Arguments.TryGetValue("text", out var text);
            if (string.IsNullOrWhiteSpace(text))
            {
                var selection = plugin.Canvas.Selection;
                var selectedId = selection.Count > 0 ? selection[^1] : null;
                var selected = selectedId is null ? null : await plugin.Canvas.GetAsync(selectedId, ct).ConfigureAwait(false);
                text = selected switch
                {
                    MathObject math => math.Result is { Length: > 0 } result ? $"{math.Expression}\n{result}" : math.Expression,
                    TextObject plain => plain.Text,
                    InkObject ink => ink.RecognizedText,
                    _ => null
                };
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await plugin.Host.ShowToastAsync("没有可用的内容 / nothing to import", ToastLevel.Warning, ct).ConfigureAwait(false);
                return;
            }

            var widget = CreateWidget(text);
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(widget.Bounds, ct).ConfigureAwait(false);
            await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
            plugin.Log.Info("Math notes created from imported text");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not import text into math notes", ex);
        }
    }

    private async ValueTask OpenPanelAsync(CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            await plugin.Host.OpenPanelAsync(Manifest.Id, PanelId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not open the math notes panel", ex);
        }
    }

    private Control BuildPanel(IPluginContext plugin)
    {
        var insert = new Button
        {
            Content = "在画布上插入 / Insert on canvas",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(12, 6, 12, 6)
        };
        insert.Click += async (_, _) =>
        {
            try
            {
                var widget = CreateWidget();
                await plugin.Canvas.AddAsync(widget).ConfigureAwait(false);
                await plugin.Canvas.RevealAsync(widget.Bounds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                plugin.Log.Warn("Panel insert failed", ex);
            }
        };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "数学笔记 / Math Notes",
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "逐行实时求值，变量在块内共享；行首写 y = f(x) 后点「绘图」即可缩放平移查看图像。\n" +
                           "Live evaluation, shared variables, plot y = f(x) with zoom and pan.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 0, 12, 0),
                    Opacity = 0.7,
                    FontSize = 12
                },
                insert
            }
        };
    }
}
