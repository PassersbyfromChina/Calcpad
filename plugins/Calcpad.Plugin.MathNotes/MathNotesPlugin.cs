using System.Text.Json.Nodes;
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

    /// <summary>
    /// Cross plugin command that solves recognised text as a normal notes line and shows the result
    /// inline. Arguments: <c>text</c> (required), <c>sourceObjectId</c> (optional ink object),
    /// <c>targetId</c> (optional existing page) and <c>display</c> (defaults to <c>inline</c>).
    /// </summary>
    public const string SolveCommandId = "calcpad.math.notes.solve";

    /// <summary>
    /// Cross plugin command that opens the numeric slider on a line. Arguments: <c>sourceObjectId</c>,
    /// <c>text</c>, <c>number</c> (1 based) and <c>targetId</c>.
    /// </summary>
    public const string SliderCommandId = "calcpad.math.notes.slider";

    /// <summary>Command that adds another notes page to the canvas.</summary>
    public const string NewNoteCommandId = "calcpad.math.notes.new";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.math.notes",
        Name = "Math Notes",
        Version = "0.0.0",
        Description = "Paper style Math Notes pages: live evaluation, sliders on numbers, inline graphs. 数学笔记：纸张式笔记页、数字滑杆、行内图形。",
        Icon = "notes",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["math", "notes", "graph", "plot", "slider", "笔记", "绘图", "滑杆", "函数"],
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "Math Notes", InsertCommandId, "Ctrl+4", "notes", 130, false, true, "数学笔记"),
            new MenuContribution("Insert/Calculator", "New Math Notes Page", NewNoteCommandId, "Ctrl+Alt+4", "notes", 132, false, true, "新建笔记页"),
            new MenuContribution("Insert/Calculator", "Math Notes from selection", AppendCommandId, null, "notes", 131, false, true, "从选区生成数学笔记"),
            new MenuContribution("View/Panels", "Math Notes", PanelCommandId, null, "notes", 130, false, false, "数学笔记面板")
        ],
        // Pure API commands: handwriting recognition and the AI card call these, so they are declared
        // here instead of inventing menu entries the user would never click.
        Commands = [AppendCommandId, SolveCommandId, SliderCommandId],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+4", InsertCommandId, "Insert math notes", null, "插入数学笔记"),
            new ShortcutContribution("Ctrl+Alt+4", NewNoteCommandId, "New math notes page", null, "新建数学笔记页"),
            new ShortcutContribution("Ctrl+Shift+4", PanelCommandId, "Show math notes", null, "显示数学笔记面板")
        ],
        Panels =
        [
            new PanelContribution(PanelId, "Math Notes", PanelCommandId, PanelPlacement.Right, 130, 380, "数学笔记")
        ],
        Widgets =
        [
            new WidgetContribution(WidgetTypeId, "Math Notes", InsertCommandId, 560, 460, "数学笔记")
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
            NewNoteCommandId => InsertAsync(context, ct),
            AppendCommandId => AppendAsync(context, ct),
            SolveCommandId => SolveAsync(context, ct),
            SliderCommandId => SliderAsync(context, ct),
            PanelCommandId => OpenPanelAsync(ct),
            _ => ValueTask.CompletedTask
        };

    /// <summary>Creates and attaches a widget object, ready to be added to the canvas.</summary>
    public WidgetObject CreateWidget(string? initialText = null, string? sourceObjectId = null)
    {
        var camera = _context?.Canvas.Camera;
        var widget = new WidgetObject
        {
            WidgetType = WidgetTypeId,
            Title = "Math Notes / 数学笔记",
            OwnerPluginId = Manifest.Id,
            Bounds = new CRect((camera?.OffsetX ?? 0) + 200, (camera?.OffsetY ?? 0) + 200, 560, 460)
        };
        Renderer.InitializeState(widget);
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            var model = MathNotesModel.From(widget, Renderer.Engine);
            foreach (var line in initialText.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                model.SelectLine(model.Lines.Count - 1);
                model.AppendLine(line.Trim(), sourceObjectId);
            }

            model.Save(widget.State!);
        }

        return widget;
    }

    /// <summary>
    /// Solves recognised text on its own line, inline, optionally binding the line to the ink object
    /// it came from (<c>calcpad.math.notes.solve</c>).
    /// </summary>
    private async ValueTask SolveAsync(CommandContext command, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            command.Arguments.TryGetValue("text", out var text);
            command.Arguments.TryGetValue("sourceObjectId", out var sourceObjectId);
            command.Arguments.TryGetValue("targetId", out var targetId);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = plugin.Canvas.Selection.Count > 0
                    ? await ReadSelectedTextAsync(plugin, plugin.Canvas.Selection[^1], ct).ConfigureAwait(false)
                    : null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await plugin.Host.ShowToastAsync("没有可求解的内容 / nothing to solve", ToastLevel.Warning, ct).ConfigureAwait(false);
                return;
            }

            // An existing page: append the line and store the ink binding.
            if (!string.IsNullOrWhiteSpace(targetId) &&
                await plugin.Canvas.GetAsync(targetId, ct).ConfigureAwait(false) is WidgetObject existing &&
                string.Equals(existing.WidgetType, WidgetTypeId, StringComparison.Ordinal))
            {
                var model = MathNotesModel.From(existing, Renderer.Engine);
                foreach (var line in text.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries))
                {
                    model.AppendLine(line.Trim(), sourceObjectId);
                }

                model.Save(existing.State ??= new JsonObject());
                await plugin.Canvas.RevealAsync(existing.Bounds, ct).ConfigureAwait(false);
                plugin.Log.Info("Solved handwriting text into an existing notes page");
                return;
            }

            var widget = CreateWidget(text, sourceObjectId);
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(widget.Bounds, ct).ConfigureAwait(false);
            await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
            plugin.Log.Info("Solved handwriting text into a new notes page");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not solve the recognised text", ex);
        }
    }

    /// <summary>
    /// Opens the numeric slider for a line, which is how handwriting hands a number over to the
    /// slider (<c>calcpad.math.notes.slider</c>).
    /// </summary>
    private async ValueTask SliderAsync(CommandContext command, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            command.Arguments.TryGetValue("sourceObjectId", out var sourceObjectId);
            command.Arguments.TryGetValue("text", out var text);
            command.Arguments.TryGetValue("targetId", out var targetId);

            // Invoked from the menu without arguments: use whatever the user has selected.
            if (string.IsNullOrWhiteSpace(sourceObjectId) && string.IsNullOrWhiteSpace(text) &&
                plugin.Canvas.Selection.Count > 0)
            {
                var selectedId = plugin.Canvas.Selection[^1];
                var selected = await plugin.Canvas.GetAsync(selectedId, ct).ConfigureAwait(false);
                if (selected is InkObject)
                {
                    sourceObjectId = selectedId;
                }

                var selectedText = await ReadSelectedTextAsync(plugin, selectedId, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    text = selectedText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                }
            }

            var number = command.Arguments.TryGetValue("number", out var numberText)
                && int.TryParse(numberText, out var parsed) ? Math.Max(1, parsed) : 1;

            var target = await FindPageAsync(plugin, targetId, sourceObjectId, text, ct).ConfigureAwait(false);
            if (target is null)
            {
                plugin.Log.Warn("No math notes page to host the slider");
                return;
            }

            var model = MathNotesModel.From(target, Renderer.Engine);
            var opened = false;
            if (!string.IsNullOrWhiteSpace(sourceObjectId))
            {
                opened = model.OpenSliderForInk(sourceObjectId, number);
            }

            if (!opened && !string.IsNullOrWhiteSpace(text))
            {
                var index = model.Lines.FindIndex(l => string.Equals(l.Text.Trim(), text.Trim(), StringComparison.Ordinal));
                if (index < 0)
                {
                    index = model.AppendLine(text);
                }

                opened = index >= 0 && model.OpenSlider(index, number, sourceObjectId);
            }

            if (!opened)
            {
                opened = model.OpenSlider(model.Selected, number, sourceObjectId);
            }

            model.Save(target.State ??= new JsonObject());
            await plugin.Canvas.RevealAsync(target.Bounds, ct).ConfigureAwait(false);
            plugin.Log.Info(opened ? "Slider opened" : "No number to slide on that line");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not open the numeric slider", ex);
        }
    }

    private async ValueTask<WidgetObject?> FindPageAsync(
        IPluginContext plugin,
        string? targetId,
        string? sourceObjectId,
        string? text,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(targetId) &&
            await plugin.Canvas.GetAsync(targetId, ct).ConfigureAwait(false) is WidgetObject explicitTarget &&
            string.Equals(explicitTarget.WidgetType, WidgetTypeId, StringComparison.Ordinal))
        {
            return explicitTarget;
        }

        var pages = await plugin.Canvas.QueryAsync(
            new CanvasQuery { Kinds = [ObjectKinds.Widget], IncludeStrokes = false },
            ct).ConfigureAwait(false);

        var candidates = pages.OfType<WidgetObject>()
            .Where(w => string.Equals(w.WidgetType, WidgetTypeId, StringComparison.Ordinal))
            .ToList();

        if (!string.IsNullOrWhiteSpace(sourceObjectId))
        {
            var match = candidates.FirstOrDefault(w => MathNotesModel.From(w, Renderer.Engine).Lines
                .Any(l => string.Equals(l.SourceObjectId, sourceObjectId, StringComparison.Ordinal)));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var match = candidates.FirstOrDefault(w => MathNotesModel.From(w, Renderer.Engine).Lines
                .Any(l => string.Equals(l.Text.Trim(), text.Trim(), StringComparison.Ordinal)));
            if (match is not null)
            {
                return match;
            }
        }

        if (candidates.Count > 0)
        {
            return candidates[^1];
        }

        // Nothing on the canvas yet: create a page so the slider has somewhere to live.
        if (!string.IsNullOrWhiteSpace(text))
        {
            var widget = CreateWidget(text, sourceObjectId);
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            _ = id;
            return widget;
        }

        return null;
    }

    private static async ValueTask<string?> ReadSelectedTextAsync(IPluginContext plugin, string objectId, CancellationToken ct)
    {
        var selected = await plugin.Canvas.GetAsync(objectId, ct).ConfigureAwait(false);
        return selected switch
        {
            MathObject math => math.Result is { Length: > 0 } result ? $"{math.Expression}\n{result}" : math.Expression,
            TextObject plain => plain.Text,
            InkObject ink => ink.RecognizedText,
            _ => null
        };
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
