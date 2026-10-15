using Calcpad.Core.Data;

namespace Calcpad.Core.Hosting;

/// <summary>
/// Remembers whether a plugin is enabled. Kept deliberately small so the host can be driven from
/// SQLite in production and from memory in tests.
/// </summary>
public interface IPluginStateStore
{
    /// <summary>Reads the persisted flag, or null when the user never changed it.</summary>
    ValueTask<bool?> GetEnabledAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Persists the flag.</summary>
    ValueTask SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default);

    /// <summary>Plugins the user switched off, used by the plugin manager screen.</summary>
    ValueTask<IReadOnlyList<string>> ListDisabledAsync(CancellationToken ct = default);

    /// <summary>Forgets the flag, e.g. when a plugin is uninstalled.</summary>
    ValueTask ForgetAsync(string pluginId, CancellationToken ct = default);
}

/// <summary>SQLite backed plugin enable state.</summary>
public sealed class SqlitePluginStateStore : IPluginStateStore
{
    private readonly SqliteHostStore _store;

    /// <summary>Creates the store over the host tables.</summary>
    /// <param name="store">Host store that owns the <c>plugin_state</c> table.</param>
    public SqlitePluginStateStore(SqliteHostStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public ValueTask<bool?> GetEnabledAsync(string pluginId, CancellationToken ct = default) =>
        _store.GetPluginEnabledAsync(pluginId, ct);

    /// <inheritdoc />
    public ValueTask SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default) =>
        _store.SetPluginEnabledAsync(pluginId, enabled, ct);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListDisabledAsync(CancellationToken ct = default)
    {
        var states = await _store.GetPluginStatesAsync(ct).ConfigureAwait(false);
        return states.Where(s => !s.Enabled).Select(s => s.PluginId).ToList();
    }

    /// <inheritdoc />
    public ValueTask ForgetAsync(string pluginId, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            _store.DeletePluginState(pluginId);
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }
}

/// <summary>In-memory plugin enable state, used by headless hosts and by the verification suite.</summary>
public sealed class InMemoryPluginStateStore : IPluginStateStore
{
    private readonly Dictionary<string, bool> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates an empty store.</summary>
    public InMemoryPluginStateStore()
    {
    }

    /// <summary>Creates a store pre-populated with explicit flags.</summary>
    /// <param name="seed">Plugin id to enabled flag pairs.</param>
    public InMemoryPluginStateStore(IEnumerable<KeyValuePair<string, bool>> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        foreach (var (id, enabled) in seed)
        {
            _states[id] = enabled;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool?> GetEnabledAsync(string pluginId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_states.TryGetValue(pluginId, out var enabled) ? enabled : (bool?)null);
        }
    }

    /// <inheritdoc />
    public ValueTask SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _states[pluginId] = enabled;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListDisabledAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<string> disabled = _states.Where(s => !s.Value).Select(s => s.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
            return ValueTask.FromResult(disabled);
        }
    }

    /// <inheritdoc />
    public ValueTask ForgetAsync(string pluginId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _states.Remove(pluginId);
        }

        return ValueTask.CompletedTask;
    }
}
