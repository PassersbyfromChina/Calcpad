using Avalonia.Controls;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App.Services;

/// <summary>
/// Collects everything plugins register at run time. The menu bar, tool palette, side panels and
/// canvas widgets are all produced from here, so a disabled plugin disappears from the product
/// without a single change in the shell.
/// </summary>
public sealed class UiRegistry : IUiRegistry
{
    private readonly ICanvasEngine _engine;
    private readonly IHostApi _host;
    private readonly Func<string, IPluginSettings> _settingsFor;
    private readonly Dictionary<string, IWidgetRenderer> _widgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Control>> _panels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<Control>> _overlays = new(StringComparer.Ordinal);

    public UiRegistry(ICanvasEngine engine, IHostApi host, Func<string, IPluginSettings> settingsFor)
    {
        _engine = engine;
        _host = host;
        _settingsFor = settingsFor;
    }

    /// <summary>
    /// Bound after the plugin host exists, which is necessarily later than the registry itself
    /// because the host's contributions are built from what plugins register here.
    /// </summary>
    public IPluginHostRuntime? Plugins { get; set; }

    public PluginTheme Theme { get; set; } = PluginTheme.IpadDark;

    public event EventHandler? Invalidated;

    public IReadOnlyCollection<IWidgetRenderer> Widgets => _widgets.Values;

    public void RegisterWidget(IWidgetRenderer renderer) => _widgets[renderer.WidgetType] = renderer;

    public IWidgetRenderer? FindWidget(string widgetType) =>
        _widgets.TryGetValue(widgetType, out var renderer) ? renderer : null;

    public void RegisterPanel(string panelId, Func<Control> factory) => _panels[panelId] = factory;

    public Control? CreatePanel(string panelId) =>
        _panels.TryGetValue(panelId, out var factory) ? factory() : null;

    public bool HasPanel(string panelId) => _panels.ContainsKey(panelId);

    public void RegisterOverlay(string overlayId, Func<Control> factory) => _overlays[overlayId] = factory;

    public Control? CreateOverlay(string overlayId) =>
        _overlays.TryGetValue(overlayId, out var factory) ? factory() : null;

    public void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>Drops every contribution of a plugin that was just disabled.</summary>
    public void ForgetPlugin(string pluginId)
    {
        foreach (var key in _widgets.Where(kv => kv.Value is IPluginOwned owned && owned.PluginId == pluginId).Select(kv => kv.Key).ToList())
        {
            _widgets.Remove(key);
        }

        Invalidate();
    }

    public IWidgetHost CreateWidgetHost(string pluginId) => new WidgetHost(this, pluginId);

    private sealed class WidgetHost : IWidgetHost
    {
        private readonly UiRegistry _owner;

        public WidgetHost(UiRegistry owner, string pluginId)
        {
            _owner = owner;
            PluginId = pluginId;
            Canvas = owner._engine.ApiFor(pluginId);
            Settings = owner._settingsFor(pluginId);
        }

        public string PluginId { get; }

        public ICanvasApi Canvas { get; }

        public IHostApi Host => _owner._host;

        public IPluginSettings Settings { get; }

        public PluginTheme Theme => _owner.Theme;

        public string? FocusedKey { get; private set; }

        public async ValueTask InvokeCommandAsync(string commandId, IReadOnlyDictionary<string, string>? arguments = null, CancellationToken ct = default)
        {
            var host = _owner.Plugins;
            if (host is null)
            {
                return;
            }

            var context = new CommandContext
            {
                CommandId = commandId,
                Trigger = "widget",
                Arguments = arguments ?? new Dictionary<string, string>()
            };
            await host.ExecuteCommandAsync(commandId, context, ct).ConfigureAwait(false);
        }

        public void Invalidate() => _owner.Invalidate();

        public void RequestFocus(string? focusKey) => FocusedKey = focusKey;
    }
}

/// <summary>Implemented by plugin supplied UI pieces so the shell can clean up after a disable.</summary>
public interface IPluginOwned
{
    string PluginId { get; }
}
