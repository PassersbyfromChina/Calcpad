using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Calcpad.App.Views.Dialogs;

/// <summary>Shared chrome for the small modal dialogs the shell shows on behalf of plugins.</summary>
public abstract class DialogBase : Window
{
    protected DialogBase(string title, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FF151517"));
        ShowInTaskbar = false;
    }

    protected static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap
    };

    protected static TextBlock Body(string text) => new()
    {
        Text = text,
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.Parse("#FFC9C9CE")),
        TextWrapping = TextWrapping.Wrap
    };

    protected static Button Primary(string text)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Color.Parse("#FFFF9F0A")),
            Foreground = new SolidColorBrush(Color.Parse("#FF101012")),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(9)
        };
        return button;
    }

    protected static Button Secondary(string text)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Color.Parse("#FF2C2C2E")),
            Foreground = Brushes.White,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(9)
        };
        return button;
    }

    protected static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    protected void SetBody(Control content, Control? buttons = null)
    {
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(20) };
        stack.Children.Add(content);
        if (buttons is not null)
        {
            stack.Children.Add(buttons);
        }

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }
}
