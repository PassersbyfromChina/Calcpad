using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Plugins.CalculatorKit;

namespace Calcpad.Plugin.BasicCalculator;

/// <summary>
/// The built in basic calculator. It contributes the iPad style keypad as a canvas widget, a menu
/// entry under <c>Insert/Calculator</c>, a panel and the <c>Ctrl+1</c> shortcut.
/// </summary>
public sealed class BasicCalculatorPlugin : IPlugin
{
    /// <summary>Command that places a basic calculator widget on the canvas.</summary>
    public const string InsertCommandId = "calcpad.basic.calculator.insert";

    /// <summary>Command that opens the calculator panel.</summary>
    public const string PanelCommandId = "calcpad.basic.calculator.panel";

    /// <summary>Panel id contributed by this plugin.</summary>
    public const string PanelId = "calcpad.basic.calculator.panel";

    /// <summary>Widget type contributed by this plugin.</summary>
    public const string WidgetTypeId = "calcpad.calculator.basic";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.basic.calculator",
        Name = "Basic Calculator",
        Version = "0.0.0",
        Description = "iPad style basic calculator drawn on the infinite canvas. 画布上的 iPad 基础计算器。",
        Icon = "calculator",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["calculator", "keypad", "basic", "计算器", "基础", "ipad"],
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "Basic Calculator", InsertCommandId, "Ctrl+1", "calculator", 100, false, true, "基础计算器"),
            new MenuContribution("View/Panels", "Basic Calculator", PanelCommandId, null, "calculator", 100, false, false, "基础计算器面板")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+1", InsertCommandId, "Insert basic calculator", null, "插入基础计算器")
        ],
        Panels =
        [
            new PanelContribution(PanelId, "Basic Calculator", PanelCommandId, PanelPlacement.Right, 100, 320, "基础计算器")
        ],
        Widgets =
        [
            new WidgetContribution(WidgetTypeId, "Basic Calculator", InsertCommandId, 340, 520, "基础计算器")
        ]
    };

    /// <summary>The renderer this plugin registers; exposed so headless hosts can drive it.</summary>
    public BasicCalculatorWidget Renderer { get; } = new();

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        if (context is IPluginUiContext ui)
        {
            ui.Ui.RegisterWidget(Renderer);
            ui.Ui.RegisterPanel(PanelId, () => BuildPanel(context));
        }

        context.Log.Info("BasicCalculator ready (基础计算器已就绪)");
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
            _ => ValueTask.CompletedTask
        };

    /// <summary>Creates and attaches a widget object, ready to be added to the canvas.</summary>
    public WidgetObject CreateWidget()
    {
        var camera = _context?.Canvas.Camera;
        var widget = new WidgetObject
        {
            WidgetType = WidgetTypeId,
            Title = "Basic Calculator / 基础计算器",
            OwnerPluginId = Manifest.Id,
            Bounds = new CRect((camera?.OffsetX ?? 0) + 120, (camera?.OffsetY ?? 0) + 120, 340, 520)
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
            var widget = CreateWidget();
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(widget.Bounds, ct).ConfigureAwait(false);
            await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
            plugin.Log.Info($"Basic calculator inserted ({command.Trigger})");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not insert the basic calculator", ex);
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
            plugin.Log.Warn("Could not open the basic calculator panel", ex);
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
                    Text = "基础计算器 / Basic Calculator",
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "iPad 观感：圆形按键、橙色运算符、表达式与结果同屏。\n" +
                           "Chained operations, repeated equals, percent, sign and backspace.",
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
