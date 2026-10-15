using System.Text.Json;
using Calcpad.Core.Security;
using Microsoft.Data.Sqlite;

namespace Calcpad.Core.Data;

/// <summary>
/// SQLite backed <see cref="IDataStore"/>. Every row carries the owning <c>plugin_id</c> and every
/// statement filters on it, so one plugin can never observe another plugin's records even when both
/// use the same collection and key.
/// </summary>
/// <remarks>
/// Every public member demands the matching capability from <see cref="IPermissionBroker"/> before
/// touching the database and throws <see cref="PermissionDeniedException"/> when it is missing.
/// </remarks>
public sealed class SqliteDataStore : IDataStore
{
    /// <summary>Characters that would make a collection or key ambiguous. Rejected up front.</summary>
    private static readonly char[] ReservedCharacters = ['\0'];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false
    };

    private readonly CalcpadDatabase _database;
    private readonly string _pluginId;
    private readonly IPermissionBroker _broker;

    /// <summary>Creates the partition scoped store for one plugin.</summary>
    /// <param name="db">Database handle shared by the whole process.</param>
    /// <param name="pluginId">Owner of every row written through this instance.</param>
    /// <param name="broker">Authorization authority re-checked on every single call.</param>
    public SqliteDataStore(CalcpadDatabase db, string pluginId, IPermissionBroker broker)
    {
        _database = db ?? throw new ArgumentNullException(nameof(db));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _pluginId = pluginId;
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    /// <summary>Plugin that owns this partition.</summary>
    public string PluginId => _pluginId;

    /// <inheritdoc />
    public ValueTask<T?> GetAsync<T>(string collection, string key, CancellationToken ct = default)
    {
        Deny(collection, nameof(GetAsync));
        Demand(PermissionKind.DatabaseRead, nameof(GetAsync));

        try
        {
            return ValueTask.FromResult(_database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT value_json FROM plugin_data WHERE plugin_id = $p AND collection = $c AND key = $k;";
                command.Parameters.AddWithValue("$p", _pluginId);
                command.Parameters.AddWithValue("$c", collection);
                command.Parameters.AddWithValue("$k", key);
                var raw = command.ExecuteScalar();
                return raw is null or DBNull ? default : Deserialize<T>((string)raw);
            }, ct));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<T?>(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync<T>(string collection, string key, T value, CancellationToken ct = default)
    {
        Deny(collection, nameof(SetAsync));
        Demand(PermissionKind.DatabaseWrite, nameof(SetAsync));

        try
        {
            _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO plugin_data (plugin_id, collection, key, value_json, updated_at)
                    VALUES ($p, $c, $k, $v, $t)
                    ON CONFLICT (plugin_id, collection, key)
                    DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$p", _pluginId);
                command.Parameters.AddWithValue("$c", collection);
                command.Parameters.AddWithValue("$k", key);
                command.Parameters.AddWithValue("$v", Serialize(value));
                command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                return command.ExecuteNonQuery();
            }, ct);

            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string collection, string key, CancellationToken ct = default)
    {
        Deny(collection, nameof(DeleteAsync));
        Demand(PermissionKind.DatabaseWrite, nameof(DeleteAsync));

        try
        {
            var affected = _database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM plugin_data WHERE plugin_id = $p AND collection = $c AND key = $k;";
                command.Parameters.AddWithValue("$p", _pluginId);
                command.Parameters.AddWithValue("$c", collection);
                command.Parameters.AddWithValue("$k", key);
                return command.ExecuteNonQuery();
            }, ct);

            return ValueTask.FromResult(affected > 0);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<bool>(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DataRecord<T>>> ListAsync<T>(string collection, int limit = 500, CancellationToken ct = default)
    {
        Deny(collection, nameof(ListAsync));
        Demand(PermissionKind.DatabaseRead, nameof(ListAsync));
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        try
        {
            var rows = _database.Read(connection =>
            {
                var result = new List<DataRecord<T>>();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT key, value_json, updated_at FROM plugin_data
                    WHERE plugin_id = $p AND collection = $c
                    ORDER BY updated_at DESC, key ASC
                    LIMIT $l;
                    """;
                command.Parameters.AddWithValue("$p", _pluginId);
                command.Parameters.AddWithValue("$c", collection);
                command.Parameters.AddWithValue("$l", limit);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new DataRecord<T>(
                        collection,
                        reader.GetString(0),
                        Deserialize<T>(reader.IsDBNull(1) ? null : reader.GetString(1)),
                        ParseTimestamp(reader.GetString(2))));
                }

                return (IReadOnlyList<DataRecord<T>>)result;
            }, ct);

            return ValueTask.FromResult(rows);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<IReadOnlyList<DataRecord<T>>>(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask<long> CountAsync(string collection, CancellationToken ct = default)
    {
        Deny(collection, nameof(CountAsync));
        Demand(PermissionKind.DatabaseRead, nameof(CountAsync));

        try
        {
            var count = _database.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM plugin_data WHERE plugin_id = $p AND collection = $c;";
                command.Parameters.AddWithValue("$p", _pluginId);
                command.Parameters.AddWithValue("$c", collection);
                return Convert.ToInt64(command.ExecuteScalar());
            }, ct);

            return ValueTask.FromResult(count);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<long>(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(string collection, CancellationToken ct = default)
    {
        Deny(collection, nameof(ClearAsync));
        Demand(PermissionKind.DatabaseWrite, nameof(ClearAsync));

        try
        {
            _database.Write((connection, transaction) =>
            {
                using var values = connection.CreateCommand();
                values.Transaction = transaction;
                values.CommandText = "DELETE FROM plugin_data WHERE plugin_id = $p AND collection = $c;";
                values.Parameters.AddWithValue("$p", _pluginId);
                values.Parameters.AddWithValue("$c", collection);
                values.ExecuteNonQuery();

                using var log = connection.CreateCommand();
                log.Transaction = transaction;
                log.CommandText = "DELETE FROM plugin_log WHERE plugin_id = $p AND collection = $c;";
                log.Parameters.AddWithValue("$p", _pluginId);
                log.Parameters.AddWithValue("$c", collection);
                return log.ExecuteNonQuery();
            }, ct);

            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<T> GetOrCreateAsync<T>(string collection, string key, Func<T> factory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var existing = await GetAsync<T>(collection, key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var created = factory();
        await SetAsync(collection, key, created, ct).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default)
    {
        Demand(PermissionKind.DatabaseRead, nameof(ListCollectionsAsync));

        try
        {
            var collections = _database.Read(connection =>
            {
                var result = new List<string>();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT collection FROM plugin_data WHERE plugin_id = $p
                    UNION
                    SELECT collection FROM plugin_log WHERE plugin_id = $p
                    ORDER BY collection ASC;
                    """;
                command.Parameters.AddWithValue("$p", _pluginId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0));
                }

                return (IReadOnlyList<string>)result;
            }, ct);

            return ValueTask.FromResult(collections);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<IReadOnlyList<string>>(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync<T>(string collection, T value, int keepLast = 500, CancellationToken ct = default)
    {
        Deny(collection, nameof(AppendAsync));
        Demand(PermissionKind.DatabaseWrite, nameof(AppendAsync));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keepLast);

        try
        {
            _database.Write((connection, transaction) =>
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO plugin_log (plugin_id, collection, value_json, created_at)
                        VALUES ($p, $c, $v, $t);
                        """;
                    insert.Parameters.AddWithValue("$p", _pluginId);
                    insert.Parameters.AddWithValue("$c", collection);
                    insert.Parameters.AddWithValue("$v", Serialize(value));
                    insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                    insert.ExecuteNonQuery();
                }

                using var trim = connection.CreateCommand();
                trim.Transaction = transaction;
                trim.CommandText =
                    """
                    DELETE FROM plugin_log
                    WHERE plugin_id = $p AND collection = $c AND id NOT IN (
                        SELECT id FROM plugin_log
                        WHERE plugin_id = $p AND collection = $c
                        ORDER BY id DESC
                        LIMIT $keep
                    );
                    """;
                trim.Parameters.AddWithValue("$p", _pluginId);
                trim.Parameters.AddWithValue("$c", collection);
                trim.Parameters.AddWithValue("$keep", keepLast);
                return trim.ExecuteNonQuery();
            }, ct);

            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<T>> TailAsync<T>(string collection, int count = 50, CancellationToken ct = default)
    {
        Deny(collection, nameof(TailAsync));
        Demand(PermissionKind.DatabaseRead, nameof(TailAsync));
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        try
        {
            var tail = _database.Read(connection =>
            {
                var newestFirst = new List<T>();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT value_json FROM plugin_log
                    WHERE plugin_id = $p AND collection = $c
                    ORDER BY id DESC
                    LIMIT $l;
                    """;
                command.Parameters.AddWithValue("$p", _pluginId);
                command.Parameters.AddWithValue("$c", collection);
                command.Parameters.AddWithValue("$l", count);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    newestFirst.Add(Deserialize<T>(reader.IsDBNull(0) ? null : reader.GetString(0))!);
                }

                // Oldest first inside the returned window, which is what history views expect.
                newestFirst.Reverse();
                return (IReadOnlyList<T>)newestFirst;
            }, ct);

            return ValueTask.FromResult(tail);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<IReadOnlyList<T>>(ex);
        }
    }

    /// <summary>Removes every row this plugin owns. Used when the plugin is uninstalled.</summary>
    public ValueTask PurgeAsync(CancellationToken ct = default)
    {
        Demand(PermissionKind.DatabaseWrite, nameof(PurgeAsync));

        try
        {
            _database.Write((connection, transaction) =>
            {
                using var values = connection.CreateCommand();
                values.Transaction = transaction;
                values.CommandText = "DELETE FROM plugin_data WHERE plugin_id = $p;";
                values.Parameters.AddWithValue("$p", _pluginId);
                values.ExecuteNonQuery();

                using var log = connection.CreateCommand();
                log.Transaction = transaction;
                log.CommandText = "DELETE FROM plugin_log WHERE plugin_id = $p;";
                log.Parameters.AddWithValue("$p", _pluginId);
                return log.ExecuteNonQuery();
            }, ct);

            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    private void Demand(PermissionKind kind, string api)
    {
        _broker.Demand(_pluginId, kind, api);
    }

    private static void Deny(string collection, string api)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection, api);
        if (collection.IndexOfAny(ReservedCharacters) >= 0)
        {
            throw new ArgumentException("Collection names must not contain control characters.", api);
        }
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
