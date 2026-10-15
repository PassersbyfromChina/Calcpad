using Avalonia.Controls;
using Avalonia.Media;

namespace Calcpad.App.Views;

/// <summary>Desktop window. All of its content is the shared <see cref="MainView"/>.</summary>
public sealed class MainWindow : Window
{
    public MainWindow(CalcpadRuntime runtime)
    {
        Title = $"Calcpad {runtime.Host.Info.Version}";
        Width = 1100;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#FF0B0B0D"));
        Content = new MainView(runtime);
    }
}
