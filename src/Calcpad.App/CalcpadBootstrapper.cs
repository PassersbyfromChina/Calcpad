using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Hosting;

namespace Calcpad.App;

/// <summary>
/// Start-up entry point shared by the desktop and Android heads. The heads own only the platform
/// lifetime; everything else is assembled here.
/// </summary>
public static class CalcpadBootstrapper
{
    /// <summary>Android cannot pass an <see cref="AppPaths"/> through a static method, so the head sets it first.</summary>
    public static AppPaths? PathsOverride { get; set; }

    private static CalcpadRuntime? _runtime;

    /// <summary>
    /// Base builder for every head. The head must add its own platform backend, e.g.
    /// <c>CalcpadBootstrapper.BuildAvaloniaApp().UsePlatformDetect()</c> on desktop, because
    /// <c>Avalonia.Desktop</c> must not be referenced by the shared app assembly.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Called from the Android activity before the lifetime starts.</summary>
    public static void ConfigureAndroid(AppBuilder builder)
    {
        Prepare([], PathsOverride);
    }

    /// <summary>Builds the whole runtime. The caller then starts the platform lifetime.</summary>
    public static CalcpadRuntime Prepare(string[] args, AppPaths? paths = null)
    {
        if (_runtime is not null)
        {
            return _runtime;
        }

        var resolved = paths ?? PathsOverride ?? DefaultPaths();
        resolved.EnsureCreated();

        var runtime = CalcpadComposition.BuildAsync(resolved, args).GetAwaiter().GetResult();
        App.Runtime = runtime;
        _runtime = runtime;
        return runtime;
    }

    /// <summary>Desktop fallback when the head does not supply paths.</summary>
    public static AppPaths DefaultPaths()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Calcpad");
        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Calcpad");
        return new AppPaths(
            data,
            documents,
            Path.Combine(data, "plugins"),
            Path.Combine(data, "models"),
            Path.Combine(data, "cache"),
            Path.Combine(data, "calcpad.db"));
    }

    public static CalcpadRuntime? Current => _runtime;
}
