using Avalonia;

namespace Calcpad.Desktop;

internal static class Program
{
    /// <summary>
    /// Desktop entry point. Prepares the host runtime with the desktop paths, then runs the Avalonia
    /// classic desktop lifetime until the last window closes.
    /// </summary>
    /// <param name="args">Command line arguments; <c>--data-dir</c> is honoured.</param>
    /// <returns>Process exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = DesktopPaths.Create(args);
        paths.EnsureCreated();

        Calcpad.App.CalcpadBootstrapper.Prepare(args, paths);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Avalonia entry point used by the runtime and by the XAML previewer. The method name is part of
    /// the Avalonia tooling contract and must not be renamed.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        Calcpad.App.CalcpadBootstrapper.BuildAvaloniaApp().UsePlatformDetect();
}
