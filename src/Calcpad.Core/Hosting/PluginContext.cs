using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Data;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.Core.Hosting;

/// <summary>
/// The mediated context handed to one plugin instance. Every member is a permission checked facade
/// owned by the host, and the context carries the cancellation token that is signalled when the
/// plugin is disabled or the application shuts down.
/// </summary>
public sealed class PluginContext : IPluginContext, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private bool _disposed;

    /// <summary>Creates a context for one plugin.</summary>
    /// <param name="pluginId">Plugin this context belongs to.</param>
    /// <param name="canvas">Permission checked canvas facade.</param>
    /// <param name="data">Partition scoped data store.</param>
    /// <param name="permissions">Authorization authority, also exposed to the plugin.</param>
    /// <param name="ai">Permission checked AI facade.</param>
    /// <param name="host">UI-agnostic host services implemented by the shell.</param>
    /// <param name="settings">Per plugin settings.</param>
    /// <param name="log">Per plugin logger.</param>
    public PluginContext(
        string pluginId,
        ICanvasApi canvas,
        IDataStore data,
        IPermissionBroker permissions,
        IAiApi ai,
        IHostApi host,
        IPluginSettings settings,
        IPluginLogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        PluginId = pluginId;
        _shutdownToken = _shutdown.Token;
        Canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        Ai = ai ?? throw new ArgumentNullException(nameof(ai));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public string PluginId { get; }

    /// <inheritdoc />
    public ICanvasApi Canvas { get; }

    /// <inheritdoc />
    public IDataStore Data { get; }

    /// <inheritdoc />
    public IPermissionBroker Permissions { get; }

    /// <inheritdoc />
    public IAiApi Ai { get; }

    /// <inheritdoc />
    public IHostApi Host { get; }

    /// <inheritdoc />
    public IPluginSettings Settings { get; }

    /// <inheritdoc />
    public IPluginLogger Log { get; }

    /// <inheritdoc />
    public CancellationToken Shutdown => _shutdownToken;

    /// <summary>True once the context was released by the host.</summary>
    public bool IsShutdown => _shutdownToken.IsCancellationRequested;

    /// <summary>
    /// Signals <see cref="Shutdown"/> so the plugin can stop background work. Safe to call twice and
    /// safe to call from any thread.
    /// </summary>
    public void CancelShutdown()
    {
        if (_shutdownToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already released.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The source is intentionally not disposed: plugins may still hold the token and register
        // callbacks while they unwind after cancellation.
        CancelShutdown();
    }
}
