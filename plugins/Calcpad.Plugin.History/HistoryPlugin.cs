using Avalonia.Controls;
using Avalonia.Layout;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.History;

/// <summary>
/// The built in calculation history. It persists entries in its own database partition, lists the
/// most recent ones on a canvas widget, supports search, clear and click-to-reinsert, and exposes
/// <see cref="AppendCommandId"/> so the calculator plugins can record what they computed.
/// </summary>
public sealed class HistoryPlugin : IPlugin
{
    /// <summary>Command that places a history widget on the canvas.</summary>
    public const string InsertCommandId = "calcpad.history.insert";

    /// <summary>Command that opens the history panel.</summary>
    public const string PanelCommandId = "calcpad.history.focus";

    /// <summary>Command that clears every recorded entry.</summary>
    public const string ClearCommandId = "calcpad.history.clear";

    /// <summary>Cross plugin command: appends an entry from another plugin's keypad.</summary>
    public const string AppendCommandId = "calcpad.history.append";

    /// <summary>Panel id contributed by this plugin.</summary>
    public const string PanelId = "calcpad.history.panel";

    /// <summary>Widget type contributed by this plugin.</summary>
    public const string WidgetTypeId = "calcpad.history";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.history",
        Name = "History",
        Version = "0.0.0",
        Description = "Persisted calculation history with search and click-to-reinsert. 计算历史：持久化、搜索、点击回插画布。",
        Icon = "history",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["history", "recent", "log", "历史", "记录", "最近"],
        Permissions =
        [
            PermissionKind.CanvasRead,
            PermissionKind.CanvasWrite,
            PermissionKind.DatabaseRead,
            PermissionKind.DatabaseWrite,
            PermissionKind.ShellIntegration
        ],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "History", InsertCommandId, "Ctrl+Shift+5", "history", 140, false, true, "历史记录"),
            new MenuContribution("View/Panels", "History", PanelCommandId, "Ctrl+5", "history", 140, false, false, "历史记录面板"),
            new MenuContribution("Edit", "Clear History", ClearCommandId, null, "trash", 800, false, false, "清空历史")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+5", PanelCommandId, "Show history", null, "显示历史记录"),
            new ShortcutContribution("Ctrl+Shift+5", InsertCommandId, "Insert history list", null, "插入历史记录")
        ],
        Panels =
        [
            new PanelContribution(PanelId, "History", PanelCommandId, PanelPlacement.Right, 140, 340, "历史记录")
        ],
        Widgets =
        [
            new WidgetContribution(WidgetTypeId, "History", InsertCommandId, 360, 520, "历史记录")
        ]
    };

    /// <summary>Store shared by every history widget of this plugin instance.</summary>
    public HistoryStore Store { get; private set; } = new(null);

    /// <summary>The renderer this plugin registers; exposed so headless hosts can drive it.</summary>
    public HistoryWidget Renderer => _renderer ??= new HistoryWidget(Store);

    private HistoryWidget? _renderer;

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        Store = new HistoryStore(context.Data);
        _renderer = new HistoryWidget(Store);
        if (context is IPluginUiContext ui)
        {
            ui.Ui.RegisterWidget(_renderer);
            ui.Ui.RegisterPanel(PanelId, () => BuildPanel(context));
        }

        context.Log.Info("History ready (历史记录已就绪)");
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
            PanelCommandId => OpenPanelAsync(ct),
            ClearCommandId => ClearAsync(ct),
            AppendCommandId => AppendAsync(context, ct),
            _ => ValueTask.CompletedTask
        };

    /// <summary>Creates and attaches a widget object, ready to be added to the canvas.</summary>
    public WidgetObject CreateWidget()
    {
        var camera = _context?.Canvas.Camera;
        var widget = new WidgetObject
        {
            WidgetType = WidgetTypeId,
            Title = "History / 历史记录",
            OwnerPluginId = Manifest.Id,
            Bounds = new CRect((camera?.OffsetX ?? 0) + 260, (camera?.OffsetY ?? 0) + 260, 360, 520)
        };
        Renderer.InitializeState(widget);
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
            await Store.RefreshAsync(200, ct).ConfigureAwait(false);
            var widget = CreateWidget();
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(widget.Bounds, ct).ConfigureAwait(false);
            await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
            plugin.Log.Info($"History inserted ({command.Trigger})");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not insert the history widget", ex);
        }
    }

    private async ValueTask AppendAsync(CommandContext command, CancellationToken ct)
    {
        try
        {
            command.Arguments.TryGetValue("expression", out var expression);
            command.Arguments.TryGetValue("result", out var result);
            command.Arguments.TryGetValue("error", out var error);
            if (string.IsNullOrWhiteSpace(expression))
            {
                return;
            }

            await Store.AppendAsync(expression, result ?? string.Empty, error, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context?.Log.Warn("Could not record a history entry", ex);
        }
    }

    private async ValueTask ClearAsync(CancellationToken ct)
    {
        try
        {
            await Store.ClearAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context?.Log.Warn("Could not clear the history", ex);
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
            plugin.Log.Warn("Could not open the history panel", ex);
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
        var clear = new Button
        {
            Content = "清空历史 / Clear history",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(12, 0, 12, 6)
        };
        insert.Click += async (_, _) =>
        {
            try
            {
                await Store.RefreshAsync(200).ConfigureAwait(false);
                var widget = CreateWidget();
                await plugin.Canvas.AddAsync(widget).ConfigureAwait(false);
                await plugin.Canvas.RevealAsync(widget.Bounds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                plugin.Log.Warn("Panel insert failed", ex);
            }
        };
        clear.Click += async (_, _) =>
        {
            try
            {
                await Store.ClearAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                plugin.Log.Warn("Panel clear failed", ex);
            }
        };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "历史记录 / History",
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = $"{Store.Cached.Count} 条已缓存（最多 {HistoryStore.KeepLast} 条，按插件分区持久化）。\n" +
                           "The basic and scientific calculators record every '=' through the append command.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 0, 12, 0),
                    Opacity = 0.7,
                    FontSize = 12
                },
                insert,
                clear
            }
        };
    }
}
