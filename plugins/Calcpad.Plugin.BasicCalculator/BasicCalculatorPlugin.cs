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

    /// <summary>
    /// Toolbar entry for the calculator. The shell turns every <c>ToolContribution</c> into a tool
    /// strip button that runs its command, so this is the button the user taps first.
    /// </summary>
    public const string ToolId = "calcpad.tool.calculator";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.basic.calculator",
        Name = "Calculator",
        Version = "0.0.0",
        Description = "The iPadOS 18 style calculator surface on the infinite canvas: Basic, Scientific, Convert and Math Notes share one widget and one expression. 画布上的 iPad 计算器：基础/科学/换算/数学笔记四模式共用一个 widget 与同一份表达式。",
        Icon = "calculator",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["calculator", "keypad", "basic", "scientific", "convert", "math notes", "计算器", "基础", "科学", "换算", "数学笔记", "ipad"],
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration, PermissionKind.Network],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "Calculator", InsertCommandId, "Ctrl+1", "calculator", 100, false, true, "计算器"),
            new MenuContribution("View/Panels", "Calculator", PanelCommandId, null, "calculator", 100, false, false, "计算器面板")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+1", InsertCommandId, "Insert calculator", null, "插入计算器")
        ],
        Tools =
        [
            // The tool strip button: clicking it runs the insert command, which places the surface.
            new ToolContribution(ToolId, "Calculator", InsertCommandId, "Ctrl+1", "calculator", 10, "计算器")
        ],
        Commands =
        [
            // API only commands: not surfaced as a menu entry, but the host must still be able to
            // route them (the history list calls refill, other plugins may call insert).
            CalculatorCommands.InsertCommandId,
            CalculatorCommands.RefillCommandId
        ],
        Panels =
        [
            new PanelContribution(PanelId, "Calculator", PanelCommandId, PanelPlacement.Right, 100, 320, "计算器")
        ],
        Widgets =
        [
            new WidgetContribution(
                CalculatorCommands.SurfaceWidgetType,
                "Calculator (Basic / Scientific / Convert / Math Notes)",
                InsertCommandId,
                380,
                560,
                "计算器（基础/科学/换算/数学笔记）")
        ]
    };

    /// <summary>The renderer this plugin registers for the legacy per-mode widget type.</summary>
    public BasicCalculatorWidget Renderer { get; } = new();

    /// <summary>
    /// The one calculator surface (Basic / Scientific / Convert / Math Notes). Every calculator
    /// command in the product inserts this widget; the per-mode renderers stay registered so
    /// documents written before the surface existed keep rendering.
    /// </summary>
    public CalculatorSurfaceWidget Surface { get; } = new();

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        Surface.RatesRefresher = RefreshRatesAsync;
        if (context is IPluginUiContext ui)
        {
            Surface.Ui = ui.Ui;
            ui.Ui.RegisterWidget(Renderer);
            ui.Ui.RegisterWidget(Surface);
            ui.Ui.RegisterPanel(PanelId, () => BuildPanel(context));
        }

        context.Log.Info("Calculator surface ready: Basic / Scientific / Convert / Math Notes (计算器四模式已就绪)");
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
            CalculatorCommands.InsertCommandId => InsertAsync(context, ct),
            CalculatorCommands.RefillCommandId => RefillAsync(context, ct),
            PanelCommandId => OpenPanelAsync(ct),
            _ => ValueTask.CompletedTask
        };

    /// <summary>Creates the calculator surface, ready to be added to the canvas.</summary>
    public WidgetObject CreateWidget(CalculatorMode mode = CalculatorMode.Basic)
    {
        var camera = _context?.Canvas.Camera;
        var size = CalculatorSurfaceFactory.DefaultSize(mode);
        var bounds = new CRect((camera?.OffsetX ?? 0) + 120, (camera?.OffsetY ?? 0) + 120, size.Width, size.Height);
        return CalculatorSurfaceFactory.CreateWidget(mode, ownerPluginId: Manifest.Id, bounds: bounds);
    }

    /// <summary>Creates the legacy basic-only widget; kept for documents written before the surface.</summary>
    public WidgetObject CreateLegacyWidget()
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
            var mode = CalculatorMode.Basic;
            if (command.Arguments.TryGetValue(CalculatorCommands.ModeArgument, out var requested))
            {
                CalculatorModes.TryParse(requested, out mode);
            }

            var widget = CreateWidget(mode);
            var id = await plugin.Canvas.AddAsync(widget, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(widget.Bounds, ct).ConfigureAwait(false);
            await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
            plugin.Log.Info($"Calculator inserted in {CalculatorModes.Label(mode)} mode ({command.Trigger})");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not insert the calculator", ex);
        }
    }

    /// <summary>
    /// Refills a calculator surface from the history list: the newest surface is used, and when the
    /// canvas has none a fresh one is inserted so the tap always lands somewhere useful.
    /// </summary>
    private async ValueTask RefillAsync(CommandContext command, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            command.Arguments.TryGetValue(CalculatorCommands.ExpressionArgument, out var expression);
            command.Arguments.TryGetValue(CalculatorCommands.ResultArgument, out var result);
            if (string.IsNullOrWhiteSpace(expression))
            {
                return;
            }

            var widgets = await plugin.Canvas
                .QueryAsync(new CanvasQuery { Kinds = [ObjectKinds.Widget], IncludeStrokes = false }, ct)
                .ConfigureAwait(false);

            WidgetObject? target = null;
            foreach (var candidate in widgets)
            {
                if (candidate is WidgetObject widget
                    && string.Equals(widget.WidgetType, CalculatorCommands.SurfaceWidgetType, StringComparison.Ordinal)
                    && (target is null || widget.ModifiedAt > target.ModifiedAt))
                {
                    target = widget;
                }
            }

            if (target is null)
            {
                var created = CreateWidget(CalculatorMode.Basic);
                Surface.Refill(created, expression, result);
                var id = await plugin.Canvas.AddAsync(created, ct).ConfigureAwait(false);
                await plugin.Canvas.RevealAsync(created.Bounds, ct).ConfigureAwait(false);
                await plugin.Canvas.SetSelectionAsync([id], ct).ConfigureAwait(false);
                plugin.Log.Info("Calculator inserted to receive a history entry");
                return;
            }

            var targetId = target.Id;
            await plugin.Canvas.UpdateAsync(targetId, obj =>
            {
                if (obj is WidgetObject widget)
                {
                    Surface.Refill(widget, expression, result);
                }
            }, ct).ConfigureAwait(false);
            await plugin.Canvas.RevealAsync(target.Bounds, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not refill the calculator from the history", ex);
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

    /// <summary>
    /// Fetches daily exchange rates for the converter. Runs only inside the plugin, because the
    /// permission broker lives here: the user is asked once, and a refusal leaves the converter on
    /// the manual table labelled as such instead of showing invented numbers.
    /// </summary>
    private async ValueTask<bool> RefreshRatesAsync(CurrencyRateTable table, CancellationToken ct)
    {
        var context = _context;
        if (context is null)
        {
            return false;
        }

        try
        {
            if (!context.Permissions.IsGranted(Manifest.Id, PermissionKind.Network))
            {
                var decision = await context.Permissions
                    .RequestAsync(
                        new PermissionRequest(
                            Manifest.Id,
                            Manifest.Name,
                            [PermissionKind.Network],
                            "获取每日汇率 / fetch daily exchange rates"),
                        ct)
                    .ConfigureAwait(false);
                if (!decision.Granted)
                {
                    context.Log.Warn("Daily rates refused by the user; staying on the manual table");
                    return false;
                }
            }

            context.Permissions.Demand(Manifest.Id, PermissionKind.Network, "refresh daily exchange rates");
            using var provider = new HttpCurrencyRateProvider();
            var ok = await table.RefreshAsync(provider, ct).ConfigureAwait(false);
            context.Log.Info(ok
                ? $"Daily rates loaded ({table.SourceLabel})"
                : $"Daily rates unavailable ({table.Error})");
            return ok;
        }
        catch (Exception ex)
        {
            context.Log.Warn("Could not fetch daily exchange rates", ex);
            return false;
        }
    }

    private Control BuildPanel(IPluginContext plugin)
    {
        var insert = new Button
        {
            Content = "在画布上插入计算器 / Insert calculator",
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

        var sourceLabel = new TextBlock
        {
            Text = Surface.Rates.SourceLabel,
            Margin = new Avalonia.Thickness(12, 0, 12, 0),
            Opacity = 0.75,
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var useDaily = new Button
        {
            Content = "使用每日汇率 / Use daily rates",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(12, 4, 12, 0)
        };
        useDaily.Click += async (_, _) =>
        {
            Surface.Rates.UseDailyRates();
            var ok = await RefreshRatesAsync(Surface.Rates, CancellationToken.None).ConfigureAwait(true);
            sourceLabel.Text = Surface.Rates.SourceLabel;
            useDaily.Content = ok
                ? "已启用每日汇率 / daily rates on"
                : "汇率不可用，未使用任何猜测值 / unavailable";
        };

        var useManual = new Button
        {
            Content = "回到手动汇率 / Use manual rates",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(12, 4, 12, 0)
        };
        useManual.Click += async (_, _) =>
        {
            Surface.Rates.UseManualRates();
            Surface.Rates.SaveToSettings(plugin.Settings);
            await plugin.Settings.SaveAsync().ConfigureAwait(true);
            sourceLabel.Text = Surface.Rates.SourceLabel;
        };

        var rateRows = new StackPanel { Spacing = 3, Margin = new Avalonia.Thickness(12, 4, 12, 4) };
        foreach (var currency in Surface.Rates.Currencies)
        {
            var box = new TextBox
            {
                Text = Surface.Rates.GetManualRate(currency.Id)?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                Width = 90,
                FontSize = 12
            };
            var code = currency.Id;
            async void Commit()
            {
                if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)
                    && Surface.Rates.SetManualRate(code, rate))
                {
                    Surface.Rates.SaveToSettings(plugin.Settings);
                    await plugin.Settings.SaveAsync().ConfigureAwait(true);
                    sourceLabel.Text = Surface.Rates.SourceLabel;
                }
                else
                {
                    box.Text = Surface.Rates.GetManualRate(code)?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    Commit();
                }
            };

            rateRows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{currency.Id} · 1 USD",
                        Width = 110,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    box
                }
            });
        }

        var reset = new Button
        {
            Content = "恢复出厂汇率 / Reset to shipped rates",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(12, 0, 12, 12)
        };
        reset.Click += async (_, _) =>
        {
            Surface.Rates.ResetManualRates();
            Surface.Rates.SaveToSettings(plugin.Settings);
            await plugin.Settings.SaveAsync().ConfigureAwait(true);
            sourceLabel.Text = Surface.Rates.SourceLabel;
        };

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "计算器 / Calculator",
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "基础 / 科学 / 换算 / 数学笔记 共用一个 widget 和同一份表达式；切换模式不会清空正在输入的内容。\n" +
                           "One widget, four modes, one shared expression.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 0, 12, 0),
                    Opacity = 0.7,
                    FontSize = 12
                },
                insert,
                new TextBlock
                {
                    Text = "汇率 / Exchange rates",
                    Margin = new Avalonia.Thickness(12, 8, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "默认离线手动汇率，可逐条编辑；只有显式启用且授予网络权限后才联网取每日汇率，失败时显示“汇率不可用”，绝不会用猜测值冒充实时汇率。",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 0, 12, 0),
                    Opacity = 0.7,
                    FontSize = 11
                },
                sourceLabel,
                useDaily,
                useManual,
                new ScrollViewer
                {
                    MaxHeight = 220,
                    Content = rateRows,
                    Margin = new Avalonia.Thickness(0, 4, 0, 4)
                },
                reset
            }
        };
    }
}
