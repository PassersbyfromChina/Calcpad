using Avalonia.Controls;
using Avalonia.Layout;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Plugins.CalculatorKit;

namespace Calcpad.Plugin.ScientificCalculator;

/// <summary>
/// The scientific keypad. Since iPadOS 18 the scientific pad is a mode of the one calculator, so this
/// plugin's insert command places the shared calculator surface and switches it to Scientific; the
/// legacy scientific-only widget type stays registered so older documents keep rendering.
/// </summary>
public sealed class ScientificCalculatorPlugin : IPlugin
{
    /// <summary>Command that places the calculator surface in scientific mode.</summary>
    public const string InsertCommandId = "calcpad.scientific.calculator.insert";

    /// <summary>Command that opens the scientific calculator panel.</summary>
    public const string PanelCommandId = "calcpad.scientific.calculator.panel";

    /// <summary>Panel id contributed by this plugin.</summary>
    public const string PanelId = "calcpad.scientific.calculator.panel";

    /// <summary>Legacy scientific-only widget type; the surface is the current path.</summary>
    public const string WidgetTypeId = "calcpad.calculator.scientific";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.scientific.calculator",
        Name = "Scientific Calculator",
        Version = "0.0.0",
        Description = "Scientific mode of the calculator surface: 2nd, trigonometry, logs, roots, constants and the MC/M+/M−/MR memory keys. 计算器的科学模式。",
        Icon = "calculator-scientific",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["calculator", "scientific", "trigonometry", "memory", "科学计算器", "三角函数", "2nd", "内存"],
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "Scientific (calculator mode)", InsertCommandId, "Ctrl+2", "calculator-scientific", 110, false, true, "科学模式"),
            new MenuContribution("View/Panels", "Scientific Calculator", PanelCommandId, null, "calculator-scientific", 110, false, false, "科学计算器面板")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+2", InsertCommandId, "Insert calculator in scientific mode", null, "插入科学模式计算器")
        ],
        Panels =
        [
            new PanelContribution(PanelId, "Scientific Calculator", PanelCommandId, PanelPlacement.Right, 110, 340, "科学计算器")
        ]
    };

    /// <summary>The renderer this plugin registers; exposed so headless hosts can drive it.</summary>
    public ScientificCalculatorWidget Renderer { get; } = new();

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        if (context is IPluginUiContext ui)
        {
            ui.Ui.RegisterWidget(Renderer);
            ui.Ui.RegisterPanel(PanelId, () => BuildPanel(context));
        }

        context.Log.Info("ScientificCalculator ready (科学计算器已就绪)");
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

    /// <summary>Creates the shared calculator surface switched to scientific mode.</summary>
    public WidgetObject CreateWidget()
    {
        var camera = _context?.Canvas.Camera;
        var size = CalculatorSurfaceFactory.DefaultSize(CalculatorMode.Scientific);
        var bounds = new CRect((camera?.OffsetX ?? 0) + 160, (camera?.OffsetY ?? 0) + 160, size.Width, size.Height);
        return CalculatorSurfaceFactory.CreateWidget(CalculatorMode.Scientific, ownerPluginId: Manifest.Id, bounds: bounds);
    }

    /// <summary>Creates the legacy scientific-only widget; kept for documents written before the surface.</summary>
    public WidgetObject CreateLegacyWidget()
    {
        var camera = _context?.Canvas.Camera;
        var widget = new WidgetObject
        {
            WidgetType = WidgetTypeId,
            Title = "Scientific Calculator / 科学计算器",
            OwnerPluginId = Manifest.Id,
            Bounds = new CRect((camera?.OffsetX ?? 0) + 160, (camera?.OffsetY ?? 0) + 160, 760, 420)
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
            plugin.Log.Info($"Calculator inserted in Scientific mode ({command.Trigger})");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not insert the scientific calculator", ex);
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
            plugin.Log.Warn("Could not open the scientific calculator panel", ex);
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
                    Text = "科学计算器 / Scientific Calculator",
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "2nd 切换反三角与双曲，Deg/Rad/Grad 指示与切换，π e Rand EE，xʸ √ ∛ x!。\n" +
                           "All evaluation goes through the shared expression engine, so units work too.",
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
