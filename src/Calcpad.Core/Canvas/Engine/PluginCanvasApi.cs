using Calcpad.Core.Security;

namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// The permission checked canvas facade handed to one plugin. Every single member asks
/// <see cref="IPermissionBroker.Demand"/> first: reads require <see cref="PermissionKind.CanvasRead"/>,
/// mutations require <see cref="PermissionKind.CanvasWrite"/>. Results are always detached copies,
/// so a plugin can never reach into a live document object and bypass the checks.
/// </summary>
internal sealed class PluginCanvasApi : ICanvasApi, IDisposable
{
    private readonly CanvasEngine _engine;
    private readonly string _pluginId;
    private EventHandler<CanvasChangedEventArgs>? _changed;
    private bool _disposed;

    internal PluginCanvasApi(CanvasEngine engine, string pluginId)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? throw new ArgumentException("Plugin id is required.", nameof(pluginId)) : pluginId;
        _engine.Changed += OnEngineChanged;
    }

    /// <inheritdoc />
    public event EventHandler<CanvasChangedEventArgs>? Changed
    {
        add
        {
            Demand(PermissionKind.CanvasRead, "ICanvasApi.Changed");
            _changed += value;
        }

        remove => _changed -= value;
    }

    /// <inheritdoc />
    public Camera Camera
    {
        get
        {
            Demand(PermissionKind.CanvasRead, "ICanvasApi.Camera");
            return _engine.Camera;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Selection
    {
        get
        {
            Demand(PermissionKind.CanvasRead, "ICanvasApi.Selection");
            return _engine.SelectionSnapshot();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CanvasObject>> QueryAsync(CanvasQuery? query = null, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasRead, "ICanvasApi.QueryAsync");
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Guard(() => _engine.Export(query), "QueryAsync"));
    }

    /// <inheritdoc />
    public ValueTask<CanvasObject?> GetAsync(string objectId, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasRead, "ICanvasApi.GetAsync");
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Guard(() => _engine.GetDetached(objectId), "GetAsync"));
    }

    /// <inheritdoc />
    public ValueTask<CanvasSnapshot> SnapshotAsync(CanvasQuery? query = null, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasRead, "ICanvasApi.SnapshotAsync");
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Guard(() => _engine.CreateSnapshot(query), "SnapshotAsync"));
    }

    /// <inheritdoc />
    public ValueTask<string> AddAsync(CanvasObject obj, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.AddAsync");
        ArgumentNullException.ThrowIfNull(obj);
        ct.ThrowIfCancellationRequested();
        var clone = obj.Clone();
        if (string.IsNullOrEmpty(clone.OwnerPluginId))
        {
            clone.OwnerPluginId = _pluginId;
        }

        return ValueTask.FromResult(Guard(() => _engine.Add(clone, _pluginId), "AddAsync"));
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateAsync(string objectId, Action<CanvasObject> mutate, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.UpdateAsync");
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Guard(() => _engine.UpdateDetached(objectId, mutate, _pluginId), "UpdateAsync"));
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string objectId, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.RemoveAsync");
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Guard(() => _engine.Remove(objectId, _pluginId), "RemoveAsync"));
    }

    /// <inheritdoc />
    public ValueTask SetSelectionAsync(IReadOnlyList<string> objectIds, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.SetSelectionAsync");
        ArgumentNullException.ThrowIfNull(objectIds);
        ct.ThrowIfCancellationRequested();
        Guard(() =>
        {
            _engine.SetSelection(objectIds, _pluginId);
            return true;
        }, "SetSelectionAsync");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ICanvasEdit> BeginEditAsync(string label, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.BeginEditAsync");
        ct.ThrowIfCancellationRequested();
        var scope = (ICanvasEdit)_engine.BeginEdit(label, _pluginId);
        return ValueTask.FromResult(scope);
    }

    /// <inheritdoc />
    public ValueTask RevealAsync(CRect region, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.RevealAsync");
        ct.ThrowIfCancellationRequested();
        Guard(() =>
        {
            _engine.Reveal(region, _pluginId);
            return true;
        }, "RevealAsync");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> ActivateToolAsync(string toolId, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasWrite, "ICanvasApi.ActivateToolAsync");
        ct.ThrowIfCancellationRequested();
        return _engine.ActivateToolAsync(toolId, _pluginId, ct);
    }

    /// <inheritdoc />
    public ValueTask<CanvasRaster?> RasterizeAsync(CRect region, double scale = 2.0, CancellationToken ct = default)
    {
        Demand(PermissionKind.CanvasRead, "ICanvasApi.RasterizeAsync");
        ct.ThrowIfCancellationRequested();
        return _engine.RasterizeAsync(region, scale, ct);
    }

    /// <summary>Detaches the facade from the engine.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.Changed -= OnEngineChanged;
        _changed = null;
    }

    private void Demand(PermissionKind kind, string api)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine.Permissions.Demand(_pluginId, kind, api);
    }

    private T Guard<T>(Func<T> work, string api)
    {
        try
        {
            return work();
        }
        catch (PermissionDeniedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _engine.Log.Error($"Plugin '{_pluginId}' failed in ICanvasApi.{api}.", ex);
            throw;
        }
    }

    private void OnEngineChanged(object? sender, CanvasChangedEventArgs args)
    {
        var handlers = _changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<CanvasChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _engine.Log.Warn($"Plugin '{_pluginId}' threw inside its ICanvasApi.Changed handler.", ex);
            }
        }
    }
}
