using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Calcpad.Core.Security;

namespace Calcpad.App.Views.Dialogs;

/// <summary>
/// The authorization prompt. Every capability a plugin asks for is shown with its risk level and
/// a plain language explanation; the user may refuse, and may refuse permanently.
/// </summary>
public sealed class PermissionDialog : DialogBase
{
    public PermissionDialog(PermissionRequest request)
        : base("插件请求授权", 520)
    {
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(Heading($"「{request.PluginName}」请求以下权限"));
        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            stack.Children.Add(Body($"用途说明：{request.Reason}"));
        }

        foreach (var kind in request.Kinds.Distinct())
        {
            var descriptor = PermissionCatalog.Describe(kind);
            var riskColor = descriptor.Risk switch
            {
                PermissionRisk.High => "#FFFF453A",
                PermissionRisk.Medium => "#FFFF9F0A",
                _ => "#FF30D158"
            };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock
            {
                Text = $"{descriptor.Title} / {descriptor.LocalizedTitleZh}",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White
            });
            header.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse(riskColor)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = descriptor.Risk switch
                    {
                        PermissionRisk.High => "高风险",
                        PermissionRisk.Medium => "中风险",
                        _ => "低风险"
                    },
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#FF101012"))
                }
            });

            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(header);
            card.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#FFB9B9C0"))
            });

            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF1C1C1E")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Child = card
            });
        }

        var note = new TextBlock
        {
            Text = "你可以随时在「插件」管理器中撤销授权。未授权的调用会被直接拒绝。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
        };
        stack.Children.Add(note);

        var remember = new CheckBox { Content = "记住我的选择", IsChecked = request.Remember, FontSize = 12 };
        stack.Children.Add(remember);

        var allow = Primary("允许");
        var deny = Secondary("拒绝");
        allow.Click += (_, _) => Close(new PermissionDecision(true, remember.IsChecked == true, request.Kinds.Distinct().ToList()));
        deny.Click += (_, _) => Close(new PermissionDecision(false, remember.IsChecked == true, []));
        stack.Children.Add(Row(deny, allow));

        SetBody(stack);
    }
}
