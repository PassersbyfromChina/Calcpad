using System.Globalization;
using System.Text.Json;
using Calcpad.Core.Ai;
using Calcpad.Core.Security;
using Calcpad.Core.Security.Impl;
using Microsoft.Data.Sqlite;

namespace Calcpad.Core.Data;

/// <summary>One saved canvas document.</summary>
public sealed record DocumentRecord(
    string Id,
    string Title,
    string Kind,
    string Json,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A persisted permission decision for one plugin and capability.</summary>
public sealed record PluginGrantRecord(string PluginId, PermissionKind Kind, bool Granted, DateTimeOffset UpdatedAt);

/// <summary>A persisted plugin enable/disable flag.</summary>
public sealed record PluginStateRecord(string PluginId, bool Enabled, DateTimeOffset UpdatedAt);

/// <summary>One key/value setting owned by a plugin, value stored as JSON.</summary>
public sealed record PluginSettingRecord(string PluginId, string Key, string Json, DateTimeOffset UpdatedAt);

/// <summary>A host level key/value setting.</summary>
public sealed record AppSettingRecord(string Key, string Json, DateTimeOffset UpdatedAt);

/// <summary>
/// Cloud provider configuration read back from the database.
/// <paramref name="KeyProtected"/> is false when the stored key could not be protected or could not
/// be decrypted, in which case <see cref="AiProviderSettings.ApiKey"/> is null and
/// <paramref name="Warning"/> explains what to tell the user.
/// </summary>
public sealed record StoredAiSettings(AiProviderSettings Settings, bool KeyProtected, string ProtectionScheme, string? Warning);

/// <summary>Audit entry plus the extra detail the frozen contract record cannot carry.</summary>
public sealed record EgressAuditDetail(EgressAuditEntry Entry, bool TextOnly);

/// <summary>
/// Host owned tables: canvas documents, permission grants, plugin enable state, plugin settings,
/// egress audit trail, cloud provider configuration and miscellaneous app settings.
/// </summary>
/// <remarks>
/// This store is not reachable by plugins: the permission broker and the plugin settings factory
/// mediate every plugin access, and plugin payload rows live in their own partitioned tables.
/// </remarks>
public sealed class SqliteHostStore
{
    /// <summary>App setting key holding the egress kill switch.</summary>
    public const string CloudUploadEnabledKey = "ai.cloud.upload.enabled";

    /// <summary>App setting key holding the per-request upload size limit.</summary>
    public const string MaxPayloadBytesKey = "ai.cloud.max.payload.bytes";

    /// <summary>Default per-request upload limit in bytes.</summary>
    public const int DefaultMaxPayloadBytes = 1_500_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false
    };

    private readonly CalcpadDatabase _database;

    /// <summary>Creates the host store over <paramref name="db"/>.</summary>
    /// <param name="db">Initialised (or lazily initialised) database handle.</param>
    public SqliteHostStore(CalcpadDatabase db)
    {
        _database = db ?? throw new ArgumentNullException(nameof(db));

        // The device key lives next to the database so that a copied database alone stays useless.
        SecretProtector.Configure(db.DataDirectory);
    }

    /// <summary>Database handle this store writes through.</summary>
    public CalcpadDatabase Database => _database;

    // ---------------------------------------------------------------- documents

    /// <summary>Inserts or replaces a document.</summary>
    public void SaveDocument(DocumentRecord document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        var payload = document.Json ?? string.Empty;
        var size = payload.Length == 0 ? 0 : System.Text.Encoding.UTF8.GetByteCount(payload);

        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO documents (id, title, kind, content_json, size_bytes, created_at, updated_at)
                VALUES ($id, $title, $kind, $json, $size, $created, $updated)
                ON CONFLICT (id) DO UPDATE SET
                    title = excluded.title,
                    kind = excluded.kind,
                    content_json = excluded.content_json,
                    size_bytes = excluded.size_bytes,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$title", document.Title ?? string.Empty);
            command.Parameters.AddWithValue("$kind", string.IsNullOrWhiteSpace(document.Kind) ? "canvas" : document.Kind);
            command.Parameters.AddWithValue("$json", payload);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$created", Format(document.CreatedAt == default ? DateTimeOffset.UtcNow : document.CreatedAt));
            command.Parameters.AddWithValue("$updated", Format(document.UpdatedAt == default ? DateTimeOffset.UtcNow : document.UpdatedAt));
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Reads one document, or null when the id is unknown.</summary>
    public DocumentRecord? GetDocument(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, title, kind, content_json, size_bytes, created_at, updated_at
                FROM documents WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDocument(reader) : null;
        });
    }

    /// <summary>Lists documents, most recently updated first.</summary>
    public IReadOnlyList<DocumentRecord> ListDocuments(int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return _database.Read(connection =>
        {
            var result = new List<DocumentRecord>();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, title, kind, content_json, size_bytes, created_at, updated_at
                FROM documents ORDER BY updated_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadDocument(reader));
            }

            return (IReadOnlyList<DocumentRecord>)result;
        });
    }

    /// <summary>Deletes a document. Returns false when the id was unknown.</summary>
    public bool DeleteDocument(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM documents WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        });
    }

    private static DocumentRecord ReadDocument(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        Parse(reader.GetString(5)),
        Parse(reader.GetString(6)));

    // ---------------------------------------------------------------- grants

    /// <summary>Every recorded decision for one plugin.</summary>
    public IReadOnlyList<PluginGrantRecord> GetGrants(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _database.Read(connection =>
        {
            var result = new List<PluginGrantRecord>();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT permission, granted, updated_at FROM plugin_grants WHERE plugin_id = $p ORDER BY permission;";
            command.Parameters.AddWithValue("$p", pluginId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (TryParseKind(reader.GetString(0), out var kind))
                {
                    result.Add(new PluginGrantRecord(pluginId, kind, reader.GetInt64(1) != 0, Parse(reader.GetString(2))));
                }
            }

            return (IReadOnlyList<PluginGrantRecord>)result;
        });
    }

    /// <summary>Every recorded decision for every plugin.</summary>
    public IReadOnlyList<PluginGrantRecord> GetAllGrants()
    {
        return _database.Read(connection =>
        {
            var result = new List<PluginGrantRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT plugin_id, permission, granted, updated_at FROM plugin_grants ORDER BY plugin_id, permission;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (TryParseKind(reader.GetString(1), out var kind))
                {
                    result.Add(new PluginGrantRecord(reader.GetString(0), kind, reader.GetInt64(2) != 0, Parse(reader.GetString(3))));
                }
            }

            return (IReadOnlyList<PluginGrantRecord>)result;
        });
    }

    /// <summary>Reads one decision, or null when the user was never asked.</summary>
    public bool? TryGetGrant(string pluginId, PermissionKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT granted FROM plugin_grants WHERE plugin_id = $p AND permission = $k;";
            command.Parameters.AddWithValue("$p", pluginId);
            command.Parameters.AddWithValue("$k", kind.ToString());
            var raw = command.ExecuteScalar();
            return raw is null or DBNull ? (bool?)null : Convert.ToInt64(raw) != 0;
        });
    }

    /// <summary>Writes one decision.</summary>
    public void SetGrant(string pluginId, PermissionKind kind, bool granted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO plugin_grants (plugin_id, permission, granted, updated_at)
                VALUES ($p, $k, $g, $t)
                ON CONFLICT (plugin_id, permission) DO UPDATE SET
                    granted = excluded.granted, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$p", pluginId);
            command.Parameters.AddWithValue("$k", kind.ToString());
            command.Parameters.AddWithValue("$g", granted ? 1 : 0);
            command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Writes several decisions in one transaction.</summary>
    public void SetGrants(string pluginId, IEnumerable<KeyValuePair<PermissionKind, bool>> decisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(decisions);
        var batch = decisions.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        _database.Write((connection, transaction) =>
        {
            foreach (var (kind, granted) in batch)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO plugin_grants (plugin_id, permission, granted, updated_at)
                    VALUES ($p, $k, $g, $t)
                    ON CONFLICT (plugin_id, permission) DO UPDATE SET
                        granted = excluded.granted, updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$p", pluginId);
                command.Parameters.AddWithValue("$k", kind.ToString());
                command.Parameters.AddWithValue("$g", granted ? 1 : 0);
                command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
                command.ExecuteNonQuery();
            }

            return batch.Count;
        });
    }

    /// <summary>Forgets every decision for one plugin, so the user is asked again.</summary>
    public void DeleteGrants(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM plugin_grants WHERE plugin_id = $p;";
            command.Parameters.AddWithValue("$p", pluginId);
            return command.ExecuteNonQuery();
        });
    }

    // ---------------------------------------------------------------- plugin enable state

    /// <summary>Reads the persisted enable flag, or null when the user never changed it.</summary>
    public bool? TryGetPluginEnabled(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT enabled FROM plugin_state WHERE plugin_id = $p;";
            command.Parameters.AddWithValue("$p", pluginId);
            var raw = command.ExecuteScalar();
            return raw is null or DBNull ? (bool?)null : Convert.ToInt64(raw) != 0;
        });
    }

    /// <summary>Persists the enable flag.</summary>
    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO plugin_state (plugin_id, enabled, updated_at)
                VALUES ($p, $e, $t)
                ON CONFLICT (plugin_id) DO UPDATE SET
                    enabled = excluded.enabled, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$p", pluginId);
            command.Parameters.AddWithValue("$e", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>All plugins whose enable flag differs from the manifest default.</summary>
    public IReadOnlyList<PluginStateRecord> GetPluginStates()
    {
        return _database.Read(connection =>
        {
            var result = new List<PluginStateRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT plugin_id, enabled, updated_at FROM plugin_state ORDER BY plugin_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new PluginStateRecord(reader.GetString(0), reader.GetInt64(1) != 0, Parse(reader.GetString(2))));
            }

            return (IReadOnlyList<PluginStateRecord>)result;
        });
    }

    /// <summary>Drops the persisted enable flag, used when a plugin is uninstalled.</summary>
    public void DeletePluginState(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM plugin_state WHERE plugin_id = $p;";
            command.Parameters.AddWithValue("$p", pluginId);
            return command.ExecuteNonQuery();
        });
    }

    // ---------------------------------------------------------------- plugin settings

    /// <summary>All settings of one plugin.</summary>
    public IReadOnlyList<PluginSettingRecord> GetPluginSettings(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _database.Read(connection =>
        {
            var result = new List<PluginSettingRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value_json, updated_at FROM plugin_settings WHERE plugin_id = $p ORDER BY key;";
            command.Parameters.AddWithValue("$p", pluginId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new PluginSettingRecord(
                    pluginId,
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "null" : reader.GetString(1),
                    Parse(reader.GetString(2))));
            }

            return (IReadOnlyList<PluginSettingRecord>)result;
        });
    }

    /// <summary>Writes one plugin setting.</summary>
    public void SetPluginSetting(string pluginId, string key, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO plugin_settings (plugin_id, key, value_json, updated_at)
                VALUES ($p, $k, $v, $t)
                ON CONFLICT (plugin_id, key) DO UPDATE SET
                    value_json = excluded.value_json, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$p", pluginId);
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", json ?? "null");
            command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Writes several settings of one plugin in a single transaction.</summary>
    public void SetPluginSettings(string pluginId, IEnumerable<PluginSettingRecord> settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(settings);
        var batch = settings.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        _database.Write((connection, transaction) =>
        {
            foreach (var setting in batch)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO plugin_settings (plugin_id, key, value_json, updated_at)
                    VALUES ($p, $k, $v, $t)
                    ON CONFLICT (plugin_id, key) DO UPDATE SET
                        value_json = excluded.value_json, updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$p", pluginId);
                command.Parameters.AddWithValue("$k", setting.Key);
                command.Parameters.AddWithValue("$v", setting.Json ?? "null");
                command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
                command.ExecuteNonQuery();
            }

            return batch.Count;
        });
    }

    /// <summary>Removes one plugin setting.</summary>
    public bool DeletePluginSetting(string pluginId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM plugin_settings WHERE plugin_id = $p AND key = $k;";
            command.Parameters.AddWithValue("$p", pluginId);
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>Removes every setting of one plugin. Used when the plugin is uninstalled.</summary>
    public void ClearPluginSettings(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM plugin_settings WHERE plugin_id = $p;";
            command.Parameters.AddWithValue("$p", pluginId);
            return command.ExecuteNonQuery();
        });
    }

    // ---------------------------------------------------------------- egress audit

    /// <summary>Appends one audit entry.</summary>
    public void AppendEgressAudit(EgressAuditEntry entry, bool textOnly = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO egress_audit (at, plugin_id, intent, destination, model, characters, bytes, allowed, text_only)
                VALUES ($at, $p, $i, $d, $m, $c, $b, $a, $t);
                """;
            command.Parameters.AddWithValue("$at", Format(entry.At == default ? DateTimeOffset.UtcNow : entry.At));
            command.Parameters.AddWithValue("$p", entry.PluginId ?? string.Empty);
            command.Parameters.AddWithValue("$i", entry.Intent ?? string.Empty);
            command.Parameters.AddWithValue("$d", entry.Destination ?? string.Empty);
            command.Parameters.AddWithValue("$m", entry.Model ?? string.Empty);
            command.Parameters.AddWithValue("$c", entry.Characters);
            command.Parameters.AddWithValue("$b", entry.Bytes);
            command.Parameters.AddWithValue("$a", entry.Allowed ? 1 : 0);
            command.Parameters.AddWithValue("$t", textOnly ? 1 : 0);
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Recent audit entries, newest first.</summary>
    public IReadOnlyList<EgressAuditEntry> GetEgressAudit(int limit = 100)
    {
        var detailed = GetEgressAuditDetail(limit);
        return detailed.Select(d => d.Entry).ToList();
    }

    /// <summary>Recent audit entries with the extra text-only flag, newest first.</summary>
    public IReadOnlyList<EgressAuditDetail> GetEgressAuditDetail(int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return _database.Read(connection =>
        {
            var result = new List<EgressAuditDetail>();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT at, plugin_id, intent, destination, model, characters, bytes, allowed, text_only
                FROM egress_audit ORDER BY id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = new EgressAuditEntry(
                    Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt64(7) != 0);
                result.Add(new EgressAuditDetail(entry, reader.GetInt64(8) != 0));
            }

            return (IReadOnlyList<EgressAuditDetail>)result;
        });
    }

    /// <summary>Number of audit rows, used by the privacy screen.</summary>
    public long CountEgressAudit()
    {
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM egress_audit;";
            return Convert.ToInt64(command.ExecuteScalar());
        });
    }

    /// <summary>Deletes every audit row. Only the user can trigger this.</summary>
    public void ClearEgressAudit()
    {
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM egress_audit;";
            return command.ExecuteNonQuery();
        });
    }

    // ---------------------------------------------------------------- AI provider settings

    /// <summary>All configured cloud providers, without their secrets in the clear.</summary>
    public IReadOnlyList<StoredAiSettings> GetAiProviderSettings()
    {
        return _database.Read(connection =>
        {
            var result = new List<StoredAiSettings>();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT provider_id, base_url, api_key, api_key_scheme, api_key_protected, model,
                       enabled, max_upload_bytes, send_image_allowed
                FROM ai_provider_settings ORDER BY provider_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadAiSettings(reader));
            }

            return (IReadOnlyList<StoredAiSettings>)result;
        });
    }

    /// <summary>Reads one provider configuration, or null when it was never saved.</summary>
    public StoredAiSettings? GetAiProviderSettings(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT provider_id, base_url, api_key, api_key_scheme, api_key_protected, model,
                       enabled, max_upload_bytes, send_image_allowed
                FROM ai_provider_settings WHERE provider_id = $p;
                """;
            command.Parameters.AddWithValue("$p", providerId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAiSettings(reader) : null;
        });
    }

    /// <summary>
    /// Persists a provider configuration. The API key is encrypted before it reaches SQLite; if it
    /// cannot be protected it is <em>not</em> written at all and the returned record reports
    /// <see cref="StoredAiSettings.KeyProtected"/> as false with an explanatory warning.
    /// </summary>
    public StoredAiSettings SaveAiProviderSettings(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ProviderId);

        var protectedSecret = SecretProtector.Protect(settings.ApiKey);
        var hasKey = !string.IsNullOrEmpty(settings.ApiKey);
        var keyProtected = !hasKey || protectedSecret is not null;
        var warning = default(string);

        if (hasKey && protectedSecret is null)
        {
            warning = "The API key could not be encrypted on this device, so it was not saved. " +
                      "Enter it again after enabling platform secure storage.";
        }

        var scheme = protectedSecret?.Scheme ?? SecretProtector.NoScheme;
        var cipher = protectedSecret?.CipherText;

        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ai_provider_settings
                    (provider_id, base_url, api_key, api_key_scheme, api_key_protected, model,
                     enabled, max_upload_bytes, send_image_allowed, updated_at)
                VALUES ($p, $u, $k, $s, $kp, $m, $e, $mb, $si, $t)
                ON CONFLICT (provider_id) DO UPDATE SET
                    base_url = excluded.base_url,
                    api_key = excluded.api_key,
                    api_key_scheme = excluded.api_key_scheme,
                    api_key_protected = excluded.api_key_protected,
                    model = excluded.model,
                    enabled = excluded.enabled,
                    max_upload_bytes = excluded.max_upload_bytes,
                    send_image_allowed = excluded.send_image_allowed,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$p", settings.ProviderId);
            command.Parameters.AddWithValue("$u", (object?)settings.BaseUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$k", (object?)cipher ?? DBNull.Value);
            command.Parameters.AddWithValue("$s", scheme);
            command.Parameters.AddWithValue("$kp", keyProtected ? 1 : 0);
            command.Parameters.AddWithValue("$m", (object?)settings.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("$e", settings.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$mb", settings.MaxUploadBytes);
            command.Parameters.AddWithValue("$si", settings.SendImageAllowed ? 1 : 0);
            command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        });

        var stored = settings with { ApiKey = protectedSecret is null ? null : settings.ApiKey };
        return new StoredAiSettings(stored, keyProtected, scheme, warning);
    }

    /// <summary>Removes a provider configuration including its stored key.</summary>
    public bool DeleteAiProviderSettings(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM ai_provider_settings WHERE provider_id = $p;";
            command.Parameters.AddWithValue("$p", providerId);
            return command.ExecuteNonQuery() > 0;
        });
    }

    private static StoredAiSettings ReadAiSettings(SqliteDataReader reader)
    {
        var providerId = reader.GetString(0);
        var baseUrl = reader.IsDBNull(1) ? null : reader.GetString(1);
        var cipher = reader.IsDBNull(2) ? null : reader.GetString(2);
        var scheme = reader.IsDBNull(3) ? SecretProtector.NoScheme : reader.GetString(3);
        var flaggedProtected = reader.GetInt64(4) != 0;
        var model = reader.IsDBNull(5) ? null : reader.GetString(5);
        var enabled = reader.GetInt64(6) != 0;
        var maxBytes = reader.GetInt32(7);
        var sendImage = reader.GetInt64(8) != 0;

        string? apiKey = null;
        var keyProtected = true;
        string? warning = null;

        if (!string.IsNullOrEmpty(cipher))
        {
            if (SecretProtector.TryUnprotect(cipher, scheme, out var clear, out var error))
            {
                apiKey = clear;
            }
            else
            {
                keyProtected = false;
                warning = error ?? "The stored API key could not be decrypted on this device.";
            }
        }
        else if (!flaggedProtected)
        {
            keyProtected = false;
            warning = "No usable API key is stored for this provider.";
        }

        var settings = new AiProviderSettings(providerId, baseUrl, apiKey, model, enabled, maxBytes, sendImage);
        return new StoredAiSettings(settings, keyProtected, scheme, warning);
    }

    // ---------------------------------------------------------------- app settings

    /// <summary>Reads a raw JSON app setting, or null when it was never written.</summary>
    public string? GetAppSettingJson(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_json FROM app_settings WHERE key = $k;";
            command.Parameters.AddWithValue("$k", key);
            var raw = command.ExecuteScalar();
            return raw is null or DBNull ? null : (string)raw;
        });
    }

    /// <summary>Reads a typed app setting, falling back to <paramref name="fallback"/>.</summary>
    public T? GetAppSetting<T>(string key, T? fallback = default)
    {
        var json = GetAppSettingJson(key);
        if (string.IsNullOrEmpty(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Writes a typed app setting as JSON.</summary>
    public void SetAppSetting<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO app_settings (key, value_json, updated_at)
                VALUES ($k, $v, $t)
                ON CONFLICT (key) DO UPDATE SET
                    value_json = excluded.value_json, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", json);
            command.Parameters.AddWithValue("$t", Format(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        });
    }

    /// <summary>Every app setting.</summary>
    public IReadOnlyList<AppSettingRecord> GetAppSettings()
    {
        return _database.Read(connection =>
        {
            var result = new List<AppSettingRecord>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value_json, updated_at FROM app_settings ORDER BY key;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new AppSettingRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "null" : reader.GetString(1),
                    Parse(reader.GetString(2))));
            }

            return (IReadOnlyList<AppSettingRecord>)result;
        });
    }

    /// <summary>Removes an app setting.</summary>
    public bool DeleteAppSetting(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM app_settings WHERE key = $k;";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteNonQuery() > 0;
        });
    }

    // ---------------------------------------------------------------- convenience helpers

    /// <summary>Reads the persisted egress kill switch. Absent means disabled.</summary>
    public bool GetCloudUploadEnabled() => GetAppSetting(CloudUploadEnabledKey, false);

    /// <summary>Persists the egress kill switch.</summary>
    public void SetCloudUploadEnabled(bool enabled) => SetAppSetting(CloudUploadEnabledKey, enabled);

    /// <summary>Reads the persisted maximum upload size.</summary>
    public int GetMaxPayloadBytes()
    {
        var value = GetAppSetting(MaxPayloadBytesKey, DefaultMaxPayloadBytes);
        return value <= 0 ? DefaultMaxPayloadBytes : value;
    }

    /// <summary>Persists the maximum upload size.</summary>
    public void SetMaxPayloadBytes(int bytes) => SetAppSetting(MaxPayloadBytesKey, bytes <= 0 ? DefaultMaxPayloadBytes : bytes);

    // ---------------------------------------------------------------- async wrappers

    /// <summary>Async form of <see cref="SaveDocument"/>.</summary>
    public ValueTask SaveDocumentAsync(DocumentRecord document, CancellationToken ct = default) =>
        Run(() => SaveDocument(document), ct);

    /// <summary>Async form of <see cref="GetDocument"/>.</summary>
    public ValueTask<DocumentRecord?> GetDocumentAsync(string id, CancellationToken ct = default) =>
        RunResult(() => GetDocument(id), ct);

    /// <summary>Async form of <see cref="ListDocuments"/>.</summary>
    public ValueTask<IReadOnlyList<DocumentRecord>> ListDocumentsAsync(int limit = 200, CancellationToken ct = default) =>
        RunResult(() => ListDocuments(limit), ct);

    /// <summary>Async form of <see cref="DeleteDocument"/>.</summary>
    public ValueTask<bool> DeleteDocumentAsync(string id, CancellationToken ct = default) =>
        RunResult(() => DeleteDocument(id), ct);

    /// <summary>Async form of <see cref="GetGrants"/>.</summary>
    public ValueTask<IReadOnlyList<PluginGrantRecord>> GetGrantsAsync(string pluginId, CancellationToken ct = default) =>
        RunResult(() => GetGrants(pluginId), ct);

    /// <summary>Async form of <see cref="SetGrant"/>.</summary>
    public ValueTask SetGrantAsync(string pluginId, PermissionKind kind, bool granted, CancellationToken ct = default) =>
        Run(() => SetGrant(pluginId, kind, granted), ct);

    /// <summary>Async form of <see cref="DeleteGrants"/>.</summary>
    public ValueTask DeleteGrantsAsync(string pluginId, CancellationToken ct = default) =>
        Run(() => DeleteGrants(pluginId), ct);

    /// <summary>Async form of <see cref="TryGetPluginEnabled"/>.</summary>
    public ValueTask<bool?> GetPluginEnabledAsync(string pluginId, CancellationToken ct = default) =>
        RunResult(() => TryGetPluginEnabled(pluginId), ct);

    /// <summary>Async form of <see cref="SetPluginEnabled"/>.</summary>
    public ValueTask SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default) =>
        Run(() => SetPluginEnabled(pluginId, enabled), ct);

    /// <summary>Async form of <see cref="GetPluginStates"/>.</summary>
    public ValueTask<IReadOnlyList<PluginStateRecord>> GetPluginStatesAsync(CancellationToken ct = default) =>
        RunResult(GetPluginStates, ct);

    /// <summary>Async form of <see cref="GetPluginSettings"/>.</summary>
    public ValueTask<IReadOnlyList<PluginSettingRecord>> GetPluginSettingsAsync(string pluginId, CancellationToken ct = default) =>
        RunResult(() => GetPluginSettings(pluginId), ct);

    /// <summary>Async form of <see cref="SetPluginSetting"/>.</summary>
    public ValueTask SetPluginSettingAsync(string pluginId, string key, string json, CancellationToken ct = default) =>
        Run(() => SetPluginSetting(pluginId, key, json), ct);

    /// <summary>Async form of <see cref="SetPluginSettings"/>.</summary>
    public ValueTask SetPluginSettingsAsync(string pluginId, IEnumerable<PluginSettingRecord> settings, CancellationToken ct = default) =>
        Run(() => SetPluginSettings(pluginId, settings), ct);

    /// <summary>Async form of <see cref="DeletePluginSetting"/>.</summary>
    public ValueTask<bool> DeletePluginSettingAsync(string pluginId, string key, CancellationToken ct = default) =>
        RunResult(() => DeletePluginSetting(pluginId, key), ct);

    /// <summary>Async form of <see cref="ClearPluginSettings"/>.</summary>
    public ValueTask ClearPluginSettingsAsync(string pluginId, CancellationToken ct = default) =>
        Run(() => ClearPluginSettings(pluginId), ct);

    /// <summary>Async form of <see cref="AppendEgressAudit"/>.</summary>
    public ValueTask AppendEgressAuditAsync(EgressAuditEntry entry, bool textOnly = false, CancellationToken ct = default) =>
        Run(() => AppendEgressAudit(entry, textOnly), ct);

    /// <summary>Async form of <see cref="GetEgressAudit"/>.</summary>
    public ValueTask<IReadOnlyList<EgressAuditEntry>> GetEgressAuditAsync(int limit = 100, CancellationToken ct = default) =>
        RunResult(() => GetEgressAudit(limit), ct);

    /// <summary>Async form of <see cref="GetEgressAuditDetail"/>.</summary>
    public ValueTask<IReadOnlyList<EgressAuditDetail>> GetEgressAuditDetailAsync(int limit = 100, CancellationToken ct = default) =>
        RunResult(() => GetEgressAuditDetail(limit), ct);

    /// <summary>Async form of <see cref="CountEgressAudit"/>.</summary>
    public ValueTask<long> CountEgressAuditAsync(CancellationToken ct = default) =>
        RunResult(CountEgressAudit, ct);

    /// <summary>Async form of <see cref="GetAiProviderSettings()"/>.</summary>
    public ValueTask<IReadOnlyList<StoredAiSettings>> GetAiProviderSettingsAsync(CancellationToken ct = default) =>
        RunResult(GetAiProviderSettings, ct);

    /// <summary>Async form of <see cref="GetAiProviderSettings(string)"/>.</summary>
    public ValueTask<StoredAiSettings?> GetAiProviderSettingsAsync(string providerId, CancellationToken ct = default) =>
        RunResult(() => GetAiProviderSettings(providerId), ct);

    /// <summary>Async form of <see cref="SaveAiProviderSettings"/>.</summary>
    public ValueTask<StoredAiSettings> SaveAiProviderSettingsAsync(AiProviderSettings settings, CancellationToken ct = default) =>
        RunResult(() => SaveAiProviderSettings(settings), ct);

    /// <summary>Async form of <see cref="DeleteAiProviderSettings"/>.</summary>
    public ValueTask<bool> DeleteAiProviderSettingsAsync(string providerId, CancellationToken ct = default) =>
        RunResult(() => DeleteAiProviderSettings(providerId), ct);

    /// <summary>Async form of <see cref="GetAppSetting{T}"/>.</summary>
    public ValueTask<T?> GetAppSettingAsync<T>(string key, T? fallback = default, CancellationToken ct = default) =>
        RunResult(() => GetAppSetting(key, fallback), ct);

    /// <summary>Async form of <see cref="SetAppSetting{T}"/>.</summary>
    public ValueTask SetAppSettingAsync<T>(string key, T value, CancellationToken ct = default) =>
        Run(() => SetAppSetting(key, value), ct);

    /// <summary>Async form of <see cref="GetAppSettings"/>.</summary>
    public ValueTask<IReadOnlyList<AppSettingRecord>> GetAppSettingsAsync(CancellationToken ct = default) =>
        RunResult(GetAppSettings, ct);

    /// <summary>Async form of <see cref="DeleteAppSetting"/>.</summary>
    public ValueTask<bool> DeleteAppSettingAsync(string key, CancellationToken ct = default) =>
        RunResult(() => DeleteAppSetting(key), ct);

    private static ValueTask Run(Action work, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            work();
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }

    private static ValueTask<T> RunResult<T>(Func<T> work, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(work());
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<T>(ex);
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static bool TryParseKind(string value, out PermissionKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind) && Enum.IsDefined(kind);
}
