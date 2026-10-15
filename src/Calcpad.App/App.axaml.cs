using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Calcpad.App.Views;

namespace Calcpad.App;

public partial class App : Application
{
    /// <summary>Set by <see cref="CalcpadBootstrapper"/> before the lifetime starts.</summary>
    public static CalcpadRuntime? Runtime { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var runtime = Runtime;
        if (runtime is not null)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow(runtime);
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            {
                // Android and any other single view host.
                single.MainView = new MainView(runtime);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
