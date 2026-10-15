using System.Text.Json;
using Calcpad.Core.Data;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.Core.Hosting;

/// <summary>Creates the per plugin settings bag handed to a plugin context.</summary>
public interface IPluginSettingsFactory
{
    /// <summary>Returns the settings of <paramref name="pluginId"/>, creating them on first use.</summary>
    IPluginSettings Create(string pluginId);
}

/// <summary>
/// Settings backed by the <c>plugin_settings</c> table, with values stored as JSON. Reading and
/// writing a plugin's own settings is itself a database capability: when the plugin was not granted
/// <see cref="PermissionKind.DatabaseRead"/> / <see cref="PermissionKind.DatabaseWrite"/> the bag
/// degrades to memory and says so through the <see cref="PluginSettings.MemoryOnlyMarker"/> key.
/// </summary>
public sealed class PluginSettings : IPluginSettings
{
    /// <summary>
    /// Pseudo key added to <see cref="Keys"/> when the plugin has no database permission and the
    /// settings therefore live in memory only. The shell can show this as a warning.
    /// </summary>
    public const string MemoryOnlyMarker = "__calcpad.memory-only";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _pluginId;
    private readonly SqliteHostStore _store;
    private readonly IPermissionBroker _broker;
    private readonly IPluginLogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _memoryOnly;
    private bool _degradationLogged;

    /// <summary>Creates a settings bag for one plugin.</summary>
    /// <param name="pluginId">Owner of the settings.</param>
    /// <param name="store">Host store that owns the <c>plugin_settings</c> table.</param>
    /// <param name="broker">Authorization authority consulted before every read and write.</param>
    /// <param name="log">Logger used to explain a degraded bag.</param>
    public PluginSettings(string pluginId, SqliteHostStore store, IPermissionBroker broker, IPluginLogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _pluginId = pluginId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Plugin these settings belong to.</summary>
    public string PluginId => _pluginId;

    /// <summary>False when the plugin lacks the database capability and values are not persisted.</summary>
    public bool IsPersistent
    {
        get
        {
            lock (_gate)
            {
                return !_memoryOnly;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                var keys = _values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (_memoryOnly)
                {
                    keys.Add(MemoryOnlyMarker);
                }

                return keys;
            }
        }
    }

    /// <inheritdoc />
    public T? Get<T>(string key, T? fallback = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!_values.TryGetValue(key, out var json))
            {
                return fallback;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                _log.Warn($"Setting '{key}' of plugin '{_pluginId}' is not valid JSON; using the fallback.", ex);
                return fallback;
            }
        }
    }

    /// <inheritdoc />
    public void Set<T>(string key, T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            EnsureLoadedLocked();
            _values[key] = JsonSerializer.Serialize(value, JsonOptions);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            List<PluginSettingRecord> snapshot;
            lock (_gate)
            {
                EnsureLoadedLocked();

                if (!_broker.IsGranted(_pluginId, PermissionKind.DatabaseWrite))
                {
                    MarkMemoryOnlyLocked("DatabaseWrite");
                    return ValueTask.CompletedTask;
                }

                _memoryOnly = false;
                snapshot = _values
                    .Select(pair => new PluginSettingRecord(_pluginId, pair.Key, pair.Value, DateTimeOffset.UtcNow))
                    .ToList();
            }

            _store.SetPluginSettings(_pluginId, snapshot);
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <summary>Deletes every setting of this plugin from the database.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _loaded = true;
        }

        _store.ClearPluginSettings(_pluginId);
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!_broker.IsGranted(_pluginId, PermissionKind.DatabaseRead))
        {
            MarkMemoryOnlyLocked("DatabaseRead");
            return;
        }

        try
        {
            foreach (var record in _store.GetPluginSettings(_pluginId))
            {
                _values[record.Key] = record.Json;
            }

            _memoryOnly = false;
        }
        catch (Exception ex)
        {
            _log.Error($"Could not read the stored settings of plugin '{_pluginId}'.", ex);
            MarkMemoryOnlyLocked("DatabaseRead");
        }
    }

    private void MarkMemoryOnlyLocked(string capability)
    {
        _memoryOnly = true;
        if (_degradationLogged)
        {
            return;
        }

        _degradationLogged = true;
        _log.Warn(
            $"Plugin '{_pluginId}' was not granted {capability}; its settings stay in memory for this " +
            $"session and are reported as '{MemoryOnlyMarker}'.");
    }
}

/// <summary>Creates <see cref="PluginSettings"/> bags over the host store, one per plugin.</summary>
public sealed class SqlitePluginSettingsFactory : IPluginSettingsFactory
{
    private readonly SqliteHostStore _store;
    private readonly IPermissionBroker _broker;
    private readonly IPluginLogger _log;
    private readonly Dictionary<string, PluginSettings> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates the factory.</summary>
    /// <param name="store">Host store that owns the <c>plugin_settings</c> table.</param>
    /// <param name="broker">Authorization authority consulted by every bag.</param>
    /// <param name="log">Logger for diagnostics.</param>
    public SqlitePluginSettingsFactory(SqliteHostStore store, IPermissionBroker broker, IPluginLogger log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public IPluginSettings Create(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_gate)
        {
            if (!_cache.TryGetValue(pluginId, out var settings))
            {
                settings = new PluginSettings(pluginId, _store, _broker, _log);
                _cache[pluginId] = settings;
            }

            return settings;
        }
    }

    /// <summary>Drops the cached bag of a plugin, e.g. when it is uninstalled.</summary>
    public void Release(string pluginId)
    {
        lock (_gate)
        {
            _cache.Remove(pluginId);
        }
    }
}
