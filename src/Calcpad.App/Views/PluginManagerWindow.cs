using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.App.Views;

/// <summary>
/// The authorization centre. This is where the user sees what every plugin asked for, what it
/// currently holds, and can take it away. Nothing in Calcpad can grant itself a capability.
/// </summary>
public sealed class PluginManagerWindow : Window
{
    private readonly CalcpadRuntime _runtime;
    private readonly StackPanel _list = new() { Spacing = 12 };

    public PluginManagerWindow(CalcpadRuntime runtime)
    {
        _runtime = runtime;
        Title = "插件与授权";
        Width = 760;
        Height = 640;
        Background = new SolidColorBrush(Color.Parse("#FF101012"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = "插件与授权",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        header.Children.Add(new TextBlock
        {
            Text = "插件只能使用这里授予的能力。撤销后，相关调用会立刻被拒绝。云上传默认禁止，且每次都会单独确认。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
        });

        var global = BuildGlobalControls();

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(20) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(global, Dock.Top);
        root.Children.Add(global);
        root.Children.Add(new ScrollViewer { Content = _list });

        Content = root;
        Refresh();
    }

    private Control BuildGlobalControls()
    {
        var upload = new CheckBox
        {
            Content = "允许向云端上传内容（默认关闭）",
            IsChecked = _runtime.Egress.CloudUploadEnabled,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 12, 0, 0)
        };
        upload.IsCheckedChanged += (_, _) =>
        {
            _runtime.Egress.CloudUploadEnabled = upload.IsChecked == true;
        };

        var folder = new Button
        {
            Content = "打开插件目录",
            Classes = { "tool" },
            Margin = new Thickness(0, 8, 0, 8)
        };
        folder.Click += async (_, _) =>
        {
            await _runtime.Host.ShowToastAsync($"插件目录：{_runtime.Paths.PluginsDirectory}");
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(upload);
        stack.Children.Add(new TextBlock
        {
            Text = "关闭后，任何插件都无法把画布内容送出这台设备。",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
        });
        stack.Children.Add(folder);
        return stack;
    }

    private void Refresh()
    {
        _list.Children.Clear();
        foreach (var plugin in _runtime.Plugins.Plugins.OrderByDescending(p => p.IsCore).ThenBy(p => p.Name))
        {
            _list.Children.Add(BuildPluginCard(plugin));
        }

        if (_runtime.Plugins.Plugins.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "没有加载任何插件。",
                Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
            });
        }
    }

    private Control BuildPluginCard(PluginRuntimeInfo plugin)
    {
        var card = new StackPanel { Spacing = 8 };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        title.Children.Add(new TextBlock
        {
            Text = $"v{plugin.Version}",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#FF9A9AA0"))
        });
        if (plugin.IsCore)
        {
            title.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF2C2C2E")),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1),
                Child = new TextBlock { Text = "内置", FontSize = 11, Foreground = Brushes.White }
            });
        }

        if (!plugin.IsLoaded && !string.IsNullOrEmpty(plugin.Error))
        {
            title.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF7A1F1A")),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1),
                Child = new TextBlock { Text = "加载失败", FontSize = 11, Foreground = Brushes.White }
            });
        }

        card.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(plugin.Description))
        {
            card.Children.Add(new TextBlock
            {
                Text = plugin.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#FFB9B9C0"))
            });
        }

        if (!string.IsNullOrWhiteSpace(plugin.Error))
        {
            card.Children.Add(new TextBlock
            {
                Text = plugin.Error,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#FFFF453A"))
            });
        }

        var enable = new CheckBox
        {
            Content = "启用",
            IsChecked = plugin.Enabled,
            Foreground = Brushes.White,
            IsEnabled = !plugin.IsCore || true
        };
        enable.IsCheckedChanged += async (_, _) =>
        {
            try
            {
                await _runtime.Plugins.SetEnabledAsync(plugin.Id, enable.IsChecked == true);
                if (_runtime.Ui is Services.UiRegistry registry)
                {
                    registry.ForgetPlugin(plugin.Id);
                }
            }
            catch (Exception ex)
            {
                await _runtime.Host.ShowToastAsync($"切换插件失败：{ex.Message}", ToastLevel.Error);
            }

            Refresh();
        };
        card.Children.Add(enable);

        var perms = new WrapPanel { Orientation = Orientation.Horizontal };
        var declared = plugin.RequestedPermissions.Count == 0
            ? [PermissionKind.ShellIntegration]
            : plugin.RequestedPermissions;

        foreach (var kind in declared.Distinct())
        {
            var descriptor = PermissionCatalog.Describe(kind);
            var granted = plugin.GrantedPermissions.Contains(kind);
            var chip = new Button
            {
                Content = $"{(granted ? "✓ " : "✕ ")}{descriptor.LocalizedTitleZh}",
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(999),
                FontSize = 12,
                Background = new SolidColorBrush(Color.Parse(granted ? "#FF1F4D2B" : "#FF2A2A2E")),
                Foreground = Brushes.White,
                IsEnabled = plugin.Enabled
            };
            var captured = kind;
            chip.Click += (_, _) =>
            {
                if (_runtime.Permissions.IsGranted(plugin.Id, captured))
                {
                    _runtime.Permissions.Revoke(plugin.Id, captured);
                }
                else
                {
                    _ = RequestAsync(plugin, captured);
                }

                Refresh();
            };
            perms.Children.Add(chip);
        }

        card.Children.Add(perms);

        var cardBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1A1D")),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = card
        };
        return cardBorder;
    }

    private async Task RequestAsync(PluginRuntimeInfo plugin, PermissionKind kind)
    {
        var decision = await _runtime.Permissions.RequestAsync(new PermissionRequest(
            plugin.Id,
            plugin.Name,
            [kind],
            "用户在插件管理器中手动授予。",
            Remember: true));

        if (decision.Granted)
        {
            await _runtime.Host.ShowToastAsync($"已授权 {plugin.Name}", ToastLevel.Success);
        }
    }
}
