using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Calcpad.Core.Canvas;
using Calcpad.Core.Hosting;
using Calcpad.Core.Plugins;
using Calcpad.App.Views.Dialogs;

namespace Calcpad.App.Services;

/// <summary>
/// The <see cref="IHostApi"/> the shell hands to plugins: toasts, confirmations, prompts, panels
/// and progress. Everything marshals onto the UI thread.
/// </summary>
public sealed class AppHostApi : IHostApi
{
    private readonly AppPaths _paths;
    private readonly ICanvasEngine _engine;
    private Func<Window?> _ownerProvider;
    private ToastHost? _toastHost;
    private Action<string, string>? _panelOpener;
    private Action<string>? _panelCloser;

    public AppHostApi(AppPaths paths, ICanvasEngine engine, Func<Window?> ownerProvider)
    {
        _paths = paths;
        _engine = engine;
        _ownerProvider = ownerProvider;
        Info = BuildInfo(paths);
    }

    public void AttachToastHost(ToastHost host) => _toastHost = host;

    public void AttachPanelHost(Action<string, string> opener, Action<string> closer)
    {
        _panelOpener = opener;
        _panelCloser = closer;
    }

    public AppInfo Info { get; }

    public async ValueTask ShowToastAsync(string message, ToastLevel level = ToastLevel.Info, CancellationToken ct = default)
    {
        var host = _toastHost;
        if (host is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => host.Show(message, level));
        }
    }

    public async ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = _ownerProvider();
            var dialog = new ConfirmDialog(title, message);
            return owner is null ? await dialog.ShowDialog<bool>(new Window()) : await dialog.ShowDialog<bool>(owner);
        });
    }

    public async ValueTask<string?> PromptAsync(string title, string message, string? initial = null, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = _ownerProvider();
            var dialog = new PromptDialog(title, message, initial);
            return owner is null ? await dialog.ShowDialog<string?>(new Window()) : await dialog.ShowDialog<string?>(owner);
        });
    }

    public ValueTask OpenPanelAsync(string pluginId, string panelId, CancellationToken ct = default)
    {
        _panelOpener?.Invoke(pluginId, panelId);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClosePanelAsync(string pluginId, string panelId, CancellationToken ct = default)
    {
        _panelCloser?.Invoke(panelId);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RevealRegionAsync(CRect region, CancellationToken ct = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var camera = _engine.Camera;
            var viewport = new CSize(1200, 800);
            camera.FitTo(region, viewport, 80);
        });
    }

    public ValueTask DispatchAsync(Action action, CancellationToken ct = default) =>
        new(Dispatcher.UIThread.InvokeAsync(action).GetTask());

    public ValueTask<T> DispatchAsync<T>(Func<T> action, CancellationToken ct = default) =>
        new(Dispatcher.UIThread.InvokeAsync(action).GetTask());

    public async ValueTask RunWithProgressAsync(string title, Func<IProgressReporter, CancellationToken, ValueTask> work, CancellationToken ct = default)
    {
        var reporter = new SimpleProgress();
        await work(reporter, ct);
    }

    private sealed class SimpleProgress : IProgressReporter
    {
        public double Fraction { get; private set; }

        public string? Message { get; private set; }

        public void Report(double fraction, string? message = null)
        {
            Fraction = fraction;
            Message = message;
        }
    }

    /// <summary>Builds the info record once the real paths are known.</summary>
    public static AppInfo BuildInfo(AppPaths paths)
    {
        var platform = OperatingSystem.IsAndroid() ? "Android" : OperatingSystem.IsWindows() ? "Windows" : "Desktop";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        return new AppInfo(
            "Calcpad",
            typeof(AppHostApi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            $"{platform} {arch}",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            SupportsOnDeviceAi: true,
            paths.DataDirectory);
    }
}

/// <summary>Implemented by the shell's overlay layer so toasts can be shown from anywhere.</summary>
public interface IToastSink
{
    void Show(string message, ToastLevel level);
}

/// <summary>Small stacked toast presenter used by the status area.</summary>
public sealed class ToastHost : StackPanel
{
    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Avalonia.Thickness(0, 0, 20, 20);
        IsHitTestVisible = false;
    }

    public void Show(string message, ToastLevel level)
    {
        var border = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(level switch
            {
                ToastLevel.Error => Avalonia.Media.Color.Parse("#FF7A1F1A"),
                ToastLevel.Warning => Avalonia.Media.Color.Parse("#FF6B4A12"),
                ToastLevel.Success => Avalonia.Media.Color.Parse("#FF14401F"),
                _ => Avalonia.Media.Color.Parse("#FF23232A")
            }),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(14, 9),
            Child = new TextBlock { Text = message, Foreground = Avalonia.Media.Brushes.White, FontSize = 13 }
        };

        Children.Add(border);
        DispatcherTimer.RunOnce(() =>
        {
            if (Children.Contains(border))
            {
                Children.Remove(border);
            }
        }, TimeSpan.FromSeconds(level == ToastLevel.Error ? 9 : 4));
    }
}
