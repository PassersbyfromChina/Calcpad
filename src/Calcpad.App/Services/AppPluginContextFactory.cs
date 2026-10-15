using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Data;
using Calcpad.Core.Hosting;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App.Services;

/// <summary>
/// Adds the UI members to the core plugin context. Core stays UI free, so the shell decorates the
/// context instead of the core factory knowing about Avalonia.
/// </summary>
public sealed class AppPluginContext : IPluginUiContext
{
    private readonly IPluginContext _inner;

    public AppPluginContext(IPluginContext inner, IUiRegistry ui, IClipboardApi clipboard)
    {
        _inner = inner;
        Ui = ui;
        Clipboard = clipboard;
    }

    public IUiRegistry Ui { get; }

    public IClipboardApi Clipboard { get; }

    public string PluginId => _inner.PluginId;

    public ICanvasApi Canvas => _inner.Canvas;

    public IDataStore Data => _inner.Data;

    public IPermissionBroker Permissions => _inner.Permissions;

    public IAiApi Ai => _inner.Ai;

    public IHostApi Host => _inner.Host;

    public IPluginSettings Settings => _inner.Settings;

    public IPluginLogger Log => _inner.Log;

    public CancellationToken Shutdown => _inner.Shutdown;
}

/// <summary>Decorates the core context factory so plugins can reach the UI registry.</summary>
public sealed class AppPluginContextFactory : IPluginContextFactory
{
    private readonly IPluginContextFactory _inner;
    private readonly IUiRegistry _ui;
    private readonly IShellClipboard _clipboard;
    private readonly IPermissionBroker _permissions;
    private readonly Dictionary<string, IPluginUiContext> _contexts = new(StringComparer.Ordinal);

    public AppPluginContextFactory(IPluginContextFactory inner, IUiRegistry ui, IShellClipboard clipboard, IPermissionBroker permissions)
    {
        _inner = inner;
        _ui = ui;
        _clipboard = clipboard;
        _permissions = permissions;
    }

    public IReadOnlyDictionary<string, IPluginUiContext> Contexts => _contexts;

    public IPluginContext Create(IPlugin plugin)
    {
        var pluginId = plugin.Manifest.Id;
        var context = new AppPluginContext(
            _inner.Create(plugin),
            _ui,
            new GatedClipboard(_permissions, pluginId, _clipboard));
        _contexts[pluginId] = context;
        return context;
    }

    public void Release(string pluginId)
    {
        _contexts.Remove(pluginId);
        _inner.Release(pluginId);
    }
}
