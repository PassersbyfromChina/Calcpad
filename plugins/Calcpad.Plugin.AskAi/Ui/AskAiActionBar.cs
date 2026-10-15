using Avalonia.Controls;
using Avalonia.Media;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.AskAi.Ui;

/// <summary>
/// The floating action bar that appears next to a lasso. It states what was captured and offers the
/// two entry points: open the full preview, or send the text only.
/// </summary>
public sealed class AskAiActionBar
{
    private readonly AskAiPlugin _plugin;
    private readonly PluginTheme _theme;
    private readonly StackPanel _root = AskAiUi.VStack(8);

    /// <summary>Creates the bar.</summary>
    public AskAiActionBar(AskAiPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _theme = plugin.Theme;
        _plugin.StateChanged += (_, _) => Render();
    }

    /// <summary>Builds the overlay control.</summary>
    public Control Build()
    {
        var border = new Border
        {
            Background = _theme.Brush(_theme.SurfaceRaised),
            BorderBrush = _theme.Brush(_theme.GridLine),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(_theme.CornerRadius),
            Padding = new Avalonia.Thickness(12),
            MinWidth = 260,
            Child = _root
        };
        Render();
        return border;
    }

    private void Render()
    {
        _root.Children.Clear();
        var draft = _plugin.Draft;
        _root.Children.Add(AskAiUi.Title("问 AI", _theme));
        if (draft is null)
        {
            _root.Children.Add(AskAiUi.Body("没有待处理的内容。", _theme, secondary: true));
            return;
        }

        var local = _plugin.IsLocalOnly;
        _root.Children.Add(AskAiUi.Badge(
            local ? "本地处理，未联网" : "会使用云端（发送前确认）",
            local ? _theme.Success : _theme.Accent,
            _theme));
        _root.Children.Add(AskAiUi.Body(draft.Describe(), _theme, secondary: true));

        var row = AskAiUi.HStack(6);
        row.Children.Add(AskAiUi.Primary("问 AI…", _theme, () => _ = _plugin.OpenPreviewAsync()));
        row.Children.Add(AskAiUi.Secondary("只看文字", _theme, () =>
        {
            if (_plugin.Draft is { } current)
            {
                current.IncludeImage = false;
                _plugin.SetStatus("已选择只发送文字，图片不会离开设备。");
            }

            _ = _plugin.OpenPreviewAsync();
        }));
        row.Children.Add(AskAiUi.Secondary("取消", _theme, Close));
        _root.Children.Add(row);
    }

    private void Close()
    {
        var context = _plugin.Context;
        if (context is not null)
        {
            _ = context.Host.ClosePanelAsync(context.PluginId, AskAiCommands.ActionBarId);
        }
    }
}
