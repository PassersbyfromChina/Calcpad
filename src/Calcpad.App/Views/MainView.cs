using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Calcpad.Core.Plugins;
using Calcpad.App.Services;

namespace Calcpad.App.Views;

/// <summary>
/// The shell. Everything below the title bar is produced from plugin contributions: the menu bar,
/// the tool strip, the side panels and the canvas widgets. Disabling a plugin removes its UI.
/// </summary>
public sealed partial class MainView : UserControl
{
    private readonly CalcpadRuntime _runtime;
    private readonly Menu _menuBar = new();
    private readonly StackPanel _toolStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ContentControl _canvasHost = new();
    private readonly Border _panelHost = new();
    private readonly TextBlock _statusLeft = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusRight = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToastHost _toasts = new();
    private readonly TextBlock _panelTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    private readonly ContentControl _panelBody = new();
    private ICanvasSurface? _surface;

    public MainView(CalcpadRuntime runtime)
    {
        _runtime = runtime;

        Background = new SolidColorBrush(Color.Parse("#FF0B0B0D"));
        Focusable = true;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var menuRow = BuildMenuRow();
        Grid.SetRow(menuRow, 0);
        root.Children.Add(menuRow);

        var toolRow = BuildToolRow();
        Grid.SetRow(toolRow, 1);
        root.Children.Add(toolRow);

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ClipToBounds = true };
        _canvasHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _canvasHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        content.Children.Add(_canvasHost);
        Grid.SetColumn(_panelHost, 1);
        content.Children.Add(BuildPanelHost());
        Grid.SetRow(content, 2);
        root.Children.Add(content);

        var statusRow = BuildStatusRow();
        Grid.SetRow(statusRow, 3);
        root.Children.Add(statusRow);

        var overlay = new Grid();
        overlay.Children.Add(root);
        overlay.Children.Add(_toasts);

        Content = overlay;

        if (_runtime.Host is AppHostApi hostApi)
        {
            hostApi.AttachToastHost(_toasts);
            hostApi.AttachPanelHost(OpenPanel, _ => ClosePanel());
        }

        _runtime.Plugins.PluginsChanged += (_, _) => RebuildContributions();
        RebuildContributions();

        AttachedToVisualTree += (_, _) => AttachCanvas();
    }

    private Control BuildMenuRow()
    {
        _menuBar.VerticalAlignment = VerticalAlignment.Center;
        _menuBar.Background = new SolidColorBrush(Color.Parse("#FF151517"));
        var host = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF151517")),
            Padding = new Thickness(6, 2),
            Child = _menuBar
        };
        Grid.SetRow(host, 0);
        return host;
    }

    private Control BuildToolRow()
    {
        var host = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF121214")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF232326")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = _toolStrip
            }
        };
        return host;
    }

    private Control BuildPanelHost()
    {
        _panelHost.Background = new SolidColorBrush(Color.Parse("#FF151517"));
        _panelHost.BorderBrush = new SolidColorBrush(Color.Parse("#FF232326"));
        _panelHost.BorderThickness = new Thickness(1, 0, 0, 0);
        _panelHost.Width = 0;
        _panelHost.IsVisible = false;

        var close = new Button { Content = "✕", Classes = { "tool" }, Width = 32 };
        close.Click += (_, _) => ClosePanel();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(_panelTitle);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(new Border { Padding = new Thickness(14, 10), Child = header });
        stack.Children.Add(new ScrollViewer { Content = _panelBody, Padding = new Thickness(14, 0, 14, 14) });
        _panelHost.Child = stack;
        return _panelHost;
    }

    private Control BuildStatusRow()
    {
        _statusLeft.Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"));
        _statusRight.Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"));
        _statusRight.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_statusLeft);
        Grid.SetColumn(_statusRight, 1);
        grid.Children.Add(_statusRight);

        var host = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1E1E22")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF2C2C31")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 5),
            MinHeight = 28,
            Child = grid
        };
        Grid.SetRow(host, 3);
        return host;
    }

    private void AttachCanvas()
    {
        if (_surface is not null)
        {
            return;
        }

        _surface = _runtime.CanvasSurfaces.Create(_runtime.Canvas, _runtime.Ui, _runtime.Host);
        _canvasHost.Content = _surface.View;
        _surface.SetGridVisible(_runtime.Settings.ShowGrid);
        _runtime.Tools.ToolChanged += (_, id) => _surface.SetTool(id);
        _runtime.Ui.Invalidated += (_, _) => _surface.Invalidate();
        UpdateStatus();
    }

    private void OpenPanel(string pluginId, string panelId)
    {
        var contribution = _runtime.Plugins.Panels.FirstOrDefault(p => p.Id == panelId);
        var body = _runtime.Ui.CreatePanel(panelId);        if (body is null && contribution is not null)
        {
            _ = _runtime.Plugins.ExecuteCommandAsync(contribution.CommandId, new CommandContext
            {
                CommandId = contribution.CommandId,
                Trigger = "panel"
            });
            return;
        }

        if (body is null)
        {
            return;
        }

        _panelTitle.Text = Localized(contribution?.Title, contribution?.LocalizedTitleZh) ?? panelId;
        _panelBody.Content = body;
        _panelHost.Width = contribution?.PreferredWidth ?? 360;
        _panelHost.IsVisible = true;
    }

    private void ClosePanel()
    {
        _panelHost.IsVisible = false;
        _panelBody.Content = null;
    }

    private string? Localized(string? title, string? zh) =>
        _runtime.Settings.Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(zh) ? zh : title;

    private void UpdateStatus()
    {
        var doc = _runtime.Canvas.Document;
        var plugins = _runtime.Plugins.Plugins;
        var enabled = plugins.Count(p => p.Enabled && p.IsLoaded);
        var cloud = _runtime.Egress.CloudUploadEnabled ? "云上传：已开启" : "云上传：已禁止";
        _statusLeft.Text = $"对象 {doc.Objects.Count}    插件 {enabled}/{plugins.Count}    {cloud}";
        _statusRight.Text = $"缩放 {(doc.Camera.Zoom * 100):0}%    Calcpad {_runtime.Host.Info.Version}";
    }
}

/// <summary>Implemented by the canvas surface so the shell can drive the active tool.</summary>
public interface ICanvasToolSink
{
    void SetTool(string? toolId);
}
