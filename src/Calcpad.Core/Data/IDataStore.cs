namespace Calcpad.Core.Data;

public sealed record DataRecord<T>(string Collection, string Key, T? Value, DateTimeOffset UpdatedAt);

/// <summary>
/// Plugin private storage. Every plugin gets its own partition inside the local SQLite database,
/// so uninstalling a plugin can drop exactly its own rows. Backed by the host, permission gated
/// by <c>DatabaseRead</c> / <c>DatabaseWrite</c>.
/// </summary>
public interface IDataStore
{
    ValueTask<T?> GetAsync<T>(string collection, string key, CancellationToken ct = default);

    ValueTask SetAsync<T>(string collection, string key, T value, CancellationToken ct = default);

    ValueTask<bool> DeleteAsync(string collection, string key, CancellationToken ct = default);

    ValueTask<IReadOnlyList<DataRecord<T>>> ListAsync<T>(string collection, int limit = 500, CancellationToken ct = default);

    ValueTask<long> CountAsync(string collection, CancellationToken ct = default);

    ValueTask ClearAsync(string collection, CancellationToken ct = default);

    ValueTask<T> GetOrCreateAsync<T>(string collection, string key, Func<T> factory, CancellationToken ct = default);

    /// <summary>Collections this plugin has actually written to.</summary>
    ValueTask<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default);

    /// <summary>Appends to a capped, append-only log collection (recognition history, chat history).</summary>
    ValueTask AppendAsync<T>(string collection, T value, int keepLast = 500, CancellationToken ct = default);

    ValueTask<IReadOnlyList<T>> TailAsync<T>(string collection, int count = 50, CancellationToken ct = default);
}
