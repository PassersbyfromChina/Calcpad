using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Rendering;

namespace Calcpad.Plugin.CanvasTools;

/// <summary>
/// Widget host provider used while rasterising. Widgets still get their real canvas API (so they can
/// draw from their own state), but shell services are inert: a crop must never show a dialog, move
/// the camera or throw.
/// </summary>
internal sealed class RasterizerWidgetHostProvider : IWidgetHostProvider
{
    private readonly ICanvasEngine _engine;
    private readonly IUiRegistry _ui;
    private readonly Dictionary<string, RasterizerWidgetHost> _hosts = new(StringComparer.Ordinal);

    internal RasterizerWidgetHostProvider(ICanvasEngine engine, IUiRegistry ui)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <inheritdoc />
    public IWidgetHost? GetHost(WidgetObject widget)
    {
        if (widget is null)
        {
            return null;
        }

        var pluginId = string.IsNullOrWhiteSpace(widget.OwnerPluginId) ? "calcpad.core" : widget.OwnerPluginId;
        if (!_hosts.TryGetValue(pluginId, out var host))
        {
            host = new RasterizerWidgetHost(_engine, _ui, pluginId);
            _hosts[pluginId] = host;
        }

        return host;
    }

    /// <inheritdoc />
    public bool IsWidgetFocused(WidgetObject widget) => false;
}

/// <summary>Inert widget host handed to widgets during offscreen rendering.</summary>
internal sealed class RasterizerWidgetHost : IWidgetHost
{
    internal RasterizerWidgetHost(ICanvasEngine engine, IUiRegistry ui, string pluginId)
    {
        PluginId = pluginId;
        Canvas = engine.ApiFor(pluginId);
        Settings = new MemoryPluginSettings();
        try
        {
            Theme = ui.Theme ?? PluginTheme.IpadDark;
        }
        catch
        {
            Theme = PluginTheme.IpadDark;
        }
    }

    /// <inheritdoc />
    public ICanvasApi Canvas { get; }

    /// <inheritdoc />
    public IHostApi Host { get; } = new InertHostApi();

    /// <inheritdoc />
    public IPluginSettings Settings { get; }

    /// <inheritdoc />
    public string PluginId { get; }

    /// <inheritdoc />
    public PluginTheme Theme { get; }

    /// <inheritdoc />
    public string? FocusedKey => null;

    /// <inheritdoc />
    public ValueTask InvokeCommandAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Invalidate()
    {
    }

    /// <inheritdoc />
    public void RequestFocus(string? focusKey)
    {
    }
}

/// <summary>Shell services that do nothing. Used only for offscreen rendering.</summary>
internal sealed class InertHostApi : IHostApi
{
    /// <inheritdoc />
    public AppInfo Info { get; } = new(
        "Calcpad",
        "0.0.0",
        "offscreen",
        Environment.Version.ToString(),
        false,
        string.Empty);

    /// <inheritdoc />
    public ValueTask ShowToastAsync(string message, ToastLevel level = ToastLevel.Info, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask<string?> PromptAsync(string title, string message, string? initial = null, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask OpenPanelAsync(string pluginId, string panelId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask ClosePanelAsync(string pluginId, string panelId, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask RevealRegionAsync(CRect region, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DispatchAsync(Action action, CancellationToken ct = default)
    {
        action();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<T> DispatchAsync<T>(Func<T> action, CancellationToken ct = default) =>
        ValueTask.FromResult(action());

    /// <inheritdoc />
    public ValueTask RunWithProgressAsync(
        string title,
        Func<IProgressReporter, CancellationToken, ValueTask> work,
        CancellationToken ct = default) => work(InertProgress.Instance, ct);
}

/// <summary>Progress reporter that discards updates.</summary>
internal sealed class InertProgress : IProgressReporter
{
    internal static InertProgress Instance { get; } = new();

    private InertProgress()
    {
    }

    /// <inheritdoc />
    public void Report(double fraction, string? message = null)
    {
    }
}

/// <summary>Settings store that keeps values in memory for the lifetime of the host.</summary>
internal sealed class MemoryPluginSettings : IPluginSettings
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<string> Keys => _values.Keys.ToArray();

    /// <inheritdoc />
    public T? Get<T>(string key, T? fallback = default) =>
        !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out var value) && value is T typed ? typed : fallback;

    /// <inheritdoc />
    public void Set<T>(string key, T? value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (value is null)
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value;
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
}
