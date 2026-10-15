using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App.Views.Canvas;

/// <summary>
/// Per plugin host handed to the widgets that live on the canvas. It exposes the permission checked
/// canvas API of the owning plugin, the shell services and the plugin's settings partition.
/// </summary>
internal sealed class CanvasWidgetHost : IWidgetHost
{
    private readonly InfiniteCanvasView _view;
    private readonly IHostApi _host;

    internal CanvasWidgetHost(InfiniteCanvasView view, ICanvasEngine engine, IHostApi host, string pluginId, IPluginSettings settings)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        PluginId = pluginId;
        Canvas = engine.ApiFor(pluginId);
        Settings = settings ?? new InMemoryPluginSettings();
    }

    /// <inheritdoc />
    public ICanvasApi Canvas { get; }

    /// <inheritdoc />
    public IHostApi Host => _host;

    /// <inheritdoc />
    public IPluginSettings Settings { get; }

    /// <inheritdoc />
    public string PluginId { get; }

    /// <inheritdoc />
    public PluginTheme Theme => _view.WidgetTheme;

    /// <inheritdoc />
    public string? FocusedKey { get; private set; }

    /// <inheritdoc />
    public async ValueTask InvokeCommandAsync(
        string commandId,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return;
        }

        if (_host is not IPluginHostRuntime runtime)
        {
            return;
        }

        try
        {
            var context = new CommandContext
            {
                CommandId = commandId,
                Trigger = "widget",
                Arguments = arguments ?? new Dictionary<string, string>()
            };
            await runtime.ExecuteCommandAsync(commandId, context, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The shell cancelled the command; nothing to do.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Widget command '{commandId}' failed: {ex}");
        }
    }

    /// <inheritdoc />
    public void Invalidate() => _view.Invalidate();

    /// <inheritdoc />
    public void RequestFocus(string? focusKey)
    {
        FocusedKey = focusKey;
        _view.SetWidgetFocus(this, focusKey);
    }
}

/// <summary>
/// In-memory settings used for widgets whose owning plugin is not the active plugin of the shell.
/// Values live for the lifetime of the canvas; the plugin's own partition is owned by the host.
/// </summary>
internal sealed class InMemoryPluginSettings : IPluginSettings
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<string> Keys => _values.Keys.ToArray();

    /// <inheritdoc />
    public T? Get<T>(string key, T? fallback = default)
    {
        if (string.IsNullOrEmpty(key) || !_values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value is T typed ? typed : fallback;
    }

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

/// <summary>Focus bookkeeping shared with <see cref="CanvasWidgetHost"/>.</summary>
public sealed partial class InfiniteCanvasView
{
    /// <summary>Theme widgets must draw with.</summary>
    internal PluginTheme WidgetTheme => _renderer.Theme;

    /// <summary>Marks which widget currently owns the keyboard.</summary>
    internal void SetWidgetFocus(CanvasWidgetHost host, string? focusKey)
    {
        _focusedPluginId = host.PluginId;
        _focusedKey = focusKey ?? string.Empty;
        Invalidate();
    }
}
