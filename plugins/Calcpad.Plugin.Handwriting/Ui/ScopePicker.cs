using Avalonia.Controls;
using Calcpad.Core.Ai;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.Handwriting.Ui;

/// <summary>
/// The "what should I recognise?" overlay. It makes the scope explicit before anything is processed
/// and states up front how many objects and how large an area will be handled, plus whether anything
/// can be uploaded.
/// </summary>
public sealed class ScopePicker
{
    private readonly HandwritingPlugin _plugin;
    private readonly PluginTheme _theme;
    private readonly StackPanel _root = UiKit.VStack(10);
    private readonly TextBlock _summary;

    /// <summary>Creates the overlay.</summary>
    public ScopePicker(HandwritingPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _theme = plugin.Theme;
        _summary = UiKit.Body("正在统计…", _theme, secondary: true);
    }

    /// <summary>Builds the overlay control.</summary>
    public Control Build()
    {
        Render();
        _ = RefreshSummaryAsync();
        return new Border
        {
            Background = _theme.Brush(_theme.Surface),
            BorderBrush = _theme.Brush(_theme.GridLine),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(_theme.CornerRadius),
            Padding = new Avalonia.Thickness(14),
            MinWidth = 320,
            Child = _root
        };
    }

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(UiKit.Title("手写转文字：选择范围", _theme));
        _root.Children.Add(UiKit.Primary("整画布转文字", _theme, () => Run(RecognitionScope.WholeCanvas)));
        _root.Children.Add(UiKit.Secondary("圈选部分转文字（套索）", _theme, () => Run(RecognitionScope.Region)));
        _root.Children.Add(UiKit.Secondary("只识别当前选中对象", _theme, () => Run(RecognitionScope.Selection)));
        _root.Children.Add(UiKit.Secondary("取消", _theme, Close));
        _root.Children.Add(_summary);
    }

    private async Task RefreshSummaryAsync()
    {
        try
        {
            var whole = await _plugin.DescribeScopeAsync(RecognitionScope.WholeCanvas).ConfigureAwait(true);
            var selection = await _plugin.DescribeScopeAsync(RecognitionScope.Selection).ConfigureAwait(true);
            _summary.Text = $"整画布：{whole}\n当前选中：{selection}";
        }
        catch (Exception ex)
        {
            _summary.Text = $"无法统计范围：{ex.Message}";
        }
    }

    private void Run(RecognitionScope scope)
    {
        Close();
        switch (scope)
        {
            case RecognitionScope.WholeCanvas:
                _ = _plugin.RecognizeWholeCanvasAsync();
                break;
            case RecognitionScope.Region:
                _ = _plugin.RecognizeRegionAsync();
                break;
            default:
                _ = _plugin.RecognizeSelectionAsync();
                break;
        }
    }

    private void Close()
    {
        var context = _plugin.Context;
        if (context is not null)
        {
            _ = context.Host.ClosePanelAsync(context.PluginId, HandwritingCommands.ScopeOverlayId);
        }
    }
}
