using Avalonia.Controls;
using Avalonia.Layout;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.UnitConverter;

/// <summary>
/// The built in unit converter. It contributes the iPad style converter card as a canvas widget,
/// with a menu entry under <c>Insert/Calculator</c>, a panel and the <c>Ctrl+3</c> shortcut.
/// </summary>
public sealed class UnitConverterPlugin : IPlugin
{
    /// <summary>Command that places a converter widget on the canvas.</summary>
    public const string InsertCommandId = "calcpad.unit.converter.insert";

    /// <summary>Command that opens the converter panel.</summary>
    public const string PanelCommandId = "calcpad.unit.converter.panel";

    /// <summary>Panel id contributed by this plugin.</summary>
    public const string PanelId = "calcpad.unit.converter.panel";

    /// <summary>Widget type contributed by this plugin.</summary>
    public const string WidgetTypeId = "calcpad.unit.converter";

    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.unit.converter",
        Name = "Unit Converter",
        Version = "0.0.0",
        Description = "Two column unit converter with live conversion and Chinese unit names. 单位换算：长度/质量/温度/面积/体积/速度/时间/能量/功率/压强/数据/角度等。",
        Icon = "swap",
        IsCore = true,
        EnabledByDefault = true,
        Keywords = ["unit", "converter", "convert", "单位", "换算", "temperature", "温度"],
        Permissions = [PermissionKind.CanvasRead, PermissionKind.CanvasWrite, PermissionKind.ShellIntegration],
        Menus =
        [
            new MenuContribution("Insert/Calculator", "Unit Converter", InsertCommandId, "Ctrl+3", "swap", 120, false, true, "单位换算"),
            new MenuContribution("View/Panels", "Unit Converter", PanelCommandId, null, "swap", 120, false, false, "单位换算面板")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+3", InsertCommandId, "Insert unit converter", null, "插入单位换算"),
            new ShortcutContribution("Ctrl+Shift+3", PanelCommandId, "Show unit converter", null, "显示单位换算面板")
        ],
        Panels =
        [
            new PanelContribution(PanelId, "Unit Converter", PanelCommandId, PanelPlacement.Right, 120, 360, "单位换算")
        ],
        Widgets =
        [
            new WidgetContribution(WidgetTypeId, "Unit Converter", InsertCommandId, 400, 560, "单位换算")
        ]
    };

    /// <summary>The renderer this plugin registers; exposed so headless hosts can drive it.</summary>
    public UnitConverterWidget Renderer { get; } = new();

    /// <summary>Convenience access to the converter used by the widget.</summary>
    public IUnitConverter Converter => Renderer.Converter;

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context;
        if (context is IPluginUiContext ui)
        {
            ui.Ui.RegisterWidget(Renderer);
            ui.Ui.RegisterPanel(PanelId, () => BuildPanel(context));
        }

        context.Log.Info($"UnitConverter ready with {Renderer.Converter.Categories.Count} categories");
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
            Title = "Unit Converter / 单位换算",
            OwnerPluginId = Manifest.Id,
            Bounds = new CRect((camera?.OffsetX ?? 0) + 140, (camera?.OffsetY ?? 0) + 140, 400, 560)
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
            plugin.Log.Info($"Unit converter inserted ({command.Trigger})");
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not insert the unit converter", ex);
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
            plugin.Log.Warn("Could not open the unit converter panel", ex);
        }
    }

    private Control BuildPanel(IPluginContext plugin)
    {
        var category = new ComboBox
        {
            ItemsSource = Renderer.Converter.Categories.Select(c => $"{c.NameZh} · {c.Name}").ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(12, 6, 12, 6)
        };
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
                    Text = "单位换算 / Unit Converter",
                    Margin = new Avalonia.Thickness(12, 12, 12, 0),
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = $"{Renderer.Converter.Categories.Count} 个类别、每个至少 6 个单位，含中文单位名与仿射温度换算。\n" +
                           "Tap a card to switch the unit, type on either side for live conversion.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(12, 0, 12, 0),
                    Opacity = 0.7,
                    FontSize = 12
                },
                category,
                insert
            }
        };
    }
}
