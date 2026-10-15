using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Data;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.Core.Hosting;

/// <summary>
/// Builds the mediated context for one plugin by combining the canvas facade, the plugin's own data
/// partition, the permission broker and the shell supplied AI and host services.
/// </summary>
/// <remarks>
/// The shell decorates this factory to attach an <c>IUiRegistry</c>, which is what turns the context
/// into a UI capable one. <see cref="Create"/> and <see cref="Release"/> are idempotent so a plugin
/// that is disabled and re-enabled gets a fresh context without leaking the previous one.
/// </remarks>
public sealed class PluginContextFactory : IPluginContextFactory, IAsyncDisposable
{
    private readonly ICanvasEngine _canvas;
    private readonly Func<string, IDataStore> _dataStoreFor;
    private readonly IPermissionBroker _permissions;
    private readonly Func<string, IAiApi> _aiFor;
    private readonly IHostApi _host;
    private readonly IPluginSettingsFactory _settings;
    private readonly Func<string, IPluginLogger> _loggers;

    private readonly Dictionary<string, PluginContext> _contexts = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Creates the factory.</summary>
    /// <param name="canvas">Canvas engine; the factory asks it for a per plugin facade.</param>
    /// <param name="dataStoreFor">Creates the partition scoped data store of a plugin id.</param>
    /// <param name="permissions">Authorization authority shared by every plugin.</param>
    /// <param name="aiFor">
    /// Creates the AI facade of a plugin id. The facade is per plugin because the AI layer checks
    /// permissions with the calling plugin's identity; a single shared instance would let one plugin
    /// use another plugin's grants.
    /// </param>
    /// <param name="host">UI-agnostic host services implemented by the shell.</param>
    /// <param name="settings">Factory for per plugin settings.</param>
    /// <param name="loggers">Returns the logger of a plugin id.</param>
    public PluginContextFactory(
        ICanvasEngine canvas,
        Func<string, IDataStore> dataStoreFor,
        IPermissionBroker permissions,
        Func<string, IAiApi> aiFor,
        IHostApi host,
        IPluginSettingsFactory settings,
        Func<string, IPluginLogger> loggers)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _dataStoreFor = dataStoreFor ?? throw new ArgumentNullException(nameof(dataStoreFor));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _aiFor = aiFor ?? throw new ArgumentNullException(nameof(aiFor));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
    }

    /// <summary>Ids of the plugins that currently hold a context.</summary>
    public IReadOnlyList<string> ActivePluginIds
    {
        get
        {
            lock (_gate)
            {
                return _contexts.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>
    /// Authorization authority shared by every plugin. Exposed so a host can report live grants
    /// without wiring an extra delegate.
    /// </summary>
    public IPermissionBroker Permissions => _permissions;

    /// <inheritdoc />
    public IPluginContext Create(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var pluginId = plugin.Manifest?.Id ?? throw new ArgumentException("A plugin must declare a manifest id.", nameof(plugin));
        return CreateFor(pluginId);
    }

    /// <summary>
    /// Creates the context for a plugin id. Exposed so the shell can build a decorated context from
    /// just the id, and reused by <see cref="Create"/>.
    /// </summary>
    /// <param name="pluginId">Plugin the context belongs to.</param>
    public IPluginContext CreateFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_contexts.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            var context = new PluginContext(
                pluginId,
                _canvas.ApiFor(pluginId),
                _dataStoreFor(pluginId),
                _permissions,
                _aiFor(pluginId),
                _host,
                _settings.Create(pluginId),
                _loggers(pluginId));

            _contexts[pluginId] = context;
            return context;
        }
    }

    /// <inheritdoc />
    public void Release(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        PluginContext? context;
        lock (_gate)
        {
            if (!_contexts.Remove(pluginId, out context))
            {
                return;
            }
        }

        context.Dispose();
    }

    /// <summary>Releases every context. Called when the application shuts down.</summary>
    public void ReleaseAll()
    {
        List<PluginContext> contexts;
        lock (_gate)
        {
            contexts = _contexts.Values.ToList();
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            context.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        ReleaseAll();
        return ValueTask.CompletedTask;
    }
}
