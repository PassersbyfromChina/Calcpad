using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.Handwriting.Ui;

/// <summary>Small helpers so the plugin builds its panels in code without a XAML toolchain.</summary>
internal static class UiKit
{
    public static StackPanel VStack(double spacing = 8) =>
        new() { Orientation = Orientation.Vertical, Spacing = spacing };

    public static StackPanel HStack(double spacing = 8) =>
        new() { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Title(string text, PluginTheme theme) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = theme.Brush(theme.TextPrimary),
        TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock Body(string text, PluginTheme theme, bool secondary = false) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = theme.Brush(secondary ? theme.TextSecondary : theme.TextPrimary),
        TextWrapping = TextWrapping.Wrap
    };

    public static Button Primary(string text, PluginTheme theme, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Background = theme.Brush(theme.Accent),
            Foreground = theme.Brush(theme.AccentText),
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(10)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button Secondary(string text, PluginTheme theme, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Background = theme.Brush(theme.SurfaceAlt),
            Foreground = theme.Brush(theme.TextPrimary),
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(10)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Border Card(Control child, PluginTheme theme, bool raised = false) => new()
    {
        Child = child,
        Background = theme.Brush(raised ? theme.SurfaceRaised : theme.Surface),
        BorderBrush = theme.Brush(theme.GridLine),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(theme.CornerRadius),
        Padding = new Thickness(12)
    };

    public static Border Badge(string text, Color color, PluginTheme theme) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = theme.Brush(color)
        },
        BorderBrush = theme.Brush(color),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center
    };

    public static Border Banner(string text, Color color, PluginTheme theme) => new()
    {
        Child = Body(text, theme),
        Background = theme.Brush(color),
        Opacity = 0.92,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10)
    };

    public static CheckBox Toggle(string label, bool value, PluginTheme theme, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = theme.Brush(theme.TextPrimary),
            FontSize = 12.5
        };
        box.IsCheckedChanged += (_, _) => onChange(box.IsChecked == true);
        return box;
    }

    public static ComboBox Picker(IEnumerable<string> options, int selectedIndex, PluginTheme theme, Action<int> onChange)
    {
        var combo = new ComboBox
        {
            ItemsSource = options.ToList(),
            SelectedIndex = selectedIndex,
            MinWidth = 190,
            FontSize = 12.5
        };
        combo.SelectionChanged += (_, _) => onChange(combo.SelectedIndex);
        return combo;
    }

    public static string Percent(double value) => $"{value * 100:0}%";
}
