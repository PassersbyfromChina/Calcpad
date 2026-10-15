using Microsoft.Data.Sqlite;

namespace Calcpad.Core.Data;

/// <summary>
/// Owns the single SQLite database file Calcpad uses on every platform. Connections are pooled
/// and handed out per operation, writes are serialised through one gate, and the schema is
/// created and migrated exactly once per process.
/// </summary>
/// <remarks>
/// The database runs in WAL mode so that a long read (loading a document) never blocks a write
/// (autosaving a plugin record) and vice versa. All access goes through parameterised statements.
/// </remarks>
public sealed class CalcpadDatabase : IDisposable, IAsyncDisposable
{
    /// <summary>Schema revision this build knows how to create.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly object ProviderGate = new();
    private static bool _providerReady;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _initializationGate = new();
    private Task? _initialization;
    private bool _disposed;

    /// <summary>Creates a database handle for <paramref name="databaseFile"/>.</summary>
    /// <param name="databaseFile">Absolute path of the SQLite file. The directory is created if needed.</param>
    public CalcpadDatabase(string databaseFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);

        DatabaseFile = Path.GetFullPath(databaseFile);
        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DataDirectory = directory ?? Directory.GetCurrentDirectory();
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            ForeignKeys = true
        }.ToString();

        EnsureNativeProviderReady();
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DatabaseFile { get; }

    /// <summary>Directory that holds the database file.</summary>
    public string DataDirectory { get; }

    /// <summary>Connection string used for every connection handed out by this instance.</summary>
    public string ConnectionString { get; }

    /// <summary>True once <see cref="InitializeAsync"/> finished successfully.</summary>
    public bool IsInitialized => _initialization is { IsCompletedSuccessfully: true };

    /// <summary>
    /// Makes sure the bundled SQLite native library is registered. Microsoft.Data.Sqlite ships the
    /// <c>e_sqlite3</c> bundle, which contains Android, Windows and Unix binaries.
    /// </summary>
    private static void EnsureNativeProviderReady()
    {
        if (_providerReady)
        {
            return;
        }

        lock (ProviderGate)
        {
            if (_providerReady)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _providerReady = true;
        }
    }

    /// <summary>Creates the schema and applies pending migrations. Safe to call concurrently.</summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_initializationGate)
        {
            return _initialization ??= Task.Run(() => Migrate(ct), CancellationToken.None);
        }
    }

    /// <summary>Opens a ready to use connection with the per-connection pragmas applied.</summary>
    public SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Runs a read-only unit of work on its own connection.</summary>
    public T Read<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        InitializeAsync(ct).GetAwaiter().GetResult();
        ct.ThrowIfCancellationRequested();

        using var connection = OpenConnection();
        return work(connection);
    }

    /// <summary>Runs a read-only unit of work on its own connection.</summary>
    public async ValueTask<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            return Read(work, ct);
        }
        catch (Exception ex)
        {
            return await ValueTask.FromException<T>(ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a mutating unit of work inside one serialised transaction. Concurrent writers queue up
    /// instead of failing with "database is locked".
    /// </summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        InitializeAsync(ct).GetAwaiter().GetResult();
        _writeGate.Wait(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var result = work(connection, transaction);
            transaction.Commit();
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Runs a mutating unit of work inside one serialised transaction.</summary>
    public async ValueTask<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(ct).ConfigureAwait(false);
            return Write(work, ct);
        }
        catch (Exception ex)
        {
            return await ValueTask.FromException<T>(ex).ConfigureAwait(false);
        }
    }

    /// <summary>Forces the write-ahead log back into the main database file.</summary>
    public void Checkpoint()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                return command.ExecuteNonQuery();
            });
        }
        catch (SqliteException)
        {
            // A busy checkpoint is not an error: the WAL is replayed on the next open.
        }
    }

    /// <summary>Reads the recorded schema revision, or 0 when the database is brand new.</summary>
    public int GetSchemaVersion()
    {
        return Read(connection =>
        {
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
            if (Convert.ToInt64(exists.ExecuteScalar()) == 0)
            {
                return 0;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }

    private void Migrate(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _writeGate.Wait(ct);
        try
        {
            using var connection = OpenConnection();

            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                wal.ExecuteScalar();
            }

            using (var create = connection.CreateCommand())
            {
                create.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS schema_version (
                        version     INTEGER NOT NULL PRIMARY KEY,
                        description TEXT    NOT NULL,
                        applied_at  TEXT    NOT NULL
                    );
                    """;
                create.ExecuteNonQuery();
            }

            var applied = new HashSet<int>();
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT version FROM schema_version;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    applied.Add(reader.GetInt32(0));
                }
            }

            foreach (var migration in Migrations)
            {
                ct.ThrowIfCancellationRequested();
                if (applied.Contains(migration.Version))
                {
                    continue;
                }

                using var transaction = connection.BeginTransaction();
                foreach (var statement in migration.Statements)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                using (var record = connection.CreateCommand())
                {
                    record.Transaction = transaction;
                    record.CommandText =
                        "INSERT INTO schema_version (version, description, applied_at) VALUES ($v, $d, $t);";
                    record.Parameters.AddWithValue("$v", migration.Version);
                    record.Parameters.AddWithValue("$d", migration.Description);
                    record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                    record.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static readonly SchemaMigration[] Migrations =
    [
        new(1, "initial schema", [
            // Host owned canvas documents.
            """
            CREATE TABLE IF NOT EXISTS documents (
                id           TEXT    NOT NULL PRIMARY KEY,
                title        TEXT    NOT NULL,
                kind         TEXT    NOT NULL DEFAULT 'canvas',
                content_json TEXT    NOT NULL,
                size_bytes   INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_documents_updated ON documents (updated_at DESC);",

            // One row per plugin and capability the user decided about.
            """
            CREATE TABLE IF NOT EXISTS plugin_grants (
                plugin_id  TEXT    NOT NULL,
                permission TEXT    NOT NULL,
                granted    INTEGER NOT NULL,
                updated_at TEXT    NOT NULL,
                PRIMARY KEY (plugin_id, permission)
            );
            """,

            // Per plugin key/value settings, values stored as JSON.
            """
            CREATE TABLE IF NOT EXISTS plugin_settings (
                plugin_id  TEXT NOT NULL,
                key        TEXT NOT NULL,
                value_json TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (plugin_id, key)
            );
            """,

            // Persistent enable/disable flag per plugin.
            """
            CREATE TABLE IF NOT EXISTS plugin_state (
                plugin_id  TEXT    NOT NULL PRIMARY KEY,
                enabled    INTEGER NOT NULL,
                updated_at TEXT    NOT NULL
            );
            """,

            // Audit trail of every outbound AI payload, allowed or blocked.
            """
            CREATE TABLE IF NOT EXISTS egress_audit (
                id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                at          TEXT    NOT NULL,
                plugin_id   TEXT    NOT NULL,
                intent      TEXT    NOT NULL,
                destination TEXT    NOT NULL,
                model       TEXT    NOT NULL,
                characters  INTEGER NOT NULL,
                bytes       INTEGER NOT NULL,
                allowed     INTEGER NOT NULL,
                text_only   INTEGER NOT NULL DEFAULT 0
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_egress_audit_at ON egress_audit (at DESC);",

            // Cloud provider configuration. api_key is always stored encrypted.
            """
            CREATE TABLE IF NOT EXISTS ai_provider_settings (
                provider_id        TEXT    NOT NULL PRIMARY KEY,
                base_url           TEXT,
                api_key            TEXT,
                api_key_scheme     TEXT    NOT NULL DEFAULT 'none',
                api_key_protected  INTEGER NOT NULL DEFAULT 0,
                model              TEXT,
                enabled            INTEGER NOT NULL DEFAULT 0,
                max_upload_bytes   INTEGER NOT NULL DEFAULT 1500000,
                send_image_allowed INTEGER NOT NULL DEFAULT 0,
                updated_at         TEXT    NOT NULL
            );
            """,

            // Miscellaneous host settings.
            """
            CREATE TABLE IF NOT EXISTS app_settings (
                key        TEXT NOT NULL PRIMARY KEY,
                value_json TEXT,
                updated_at TEXT NOT NULL
            );
            """,

            // Plugin private key/value records, partitioned by plugin_id.
            """
            CREATE TABLE IF NOT EXISTS plugin_data (
                plugin_id  TEXT NOT NULL,
                collection TEXT NOT NULL,
                key        TEXT NOT NULL,
                value_json TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (plugin_id, collection, key)
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_plugin_data_collection ON plugin_data (plugin_id, collection, updated_at DESC);",

            // Plugin private append-only logs.
            """
            CREATE TABLE IF NOT EXISTS plugin_log (
                id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                plugin_id  TEXT    NOT NULL,
                collection TEXT    NOT NULL,
                value_json TEXT,
                created_at TEXT    NOT NULL
            );
            """,
            "CREATE INDEX IF NOT EXISTS ix_plugin_log_collection ON plugin_log (plugin_id, collection, id DESC);"
        ])
    ];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
        }
        catch (Exception)
        {
            // Nothing to release when the pool was never created.
        }

        _writeGate.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (IsInitialized)
        {
            try
            {
                await _initialization!.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The failure was already surfaced to the caller that triggered initialization.
            }
        }

        Dispose();
    }

    private sealed record SchemaMigration(int Version, string Description, string[] Statements);
}
