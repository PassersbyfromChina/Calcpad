using Calcpad.Core.Data;

namespace Calcpad.Plugin.History;

/// <summary>
/// One recorded calculation. Kept to primitives so the host's database can serialise it as JSON.
/// </summary>
/// <param name="Expression">Expression exactly as it was evaluated.</param>
/// <param name="Result">Formatted result, or an empty string when the calculation failed.</param>
/// <param name="Error">Readable error message when the calculation failed.</param>
/// <param name="At">When the calculation happened.</param>
public sealed record HistoryEntry(string Expression, string Result, string? Error, DateTimeOffset At)
{
    /// <summary>True when this entry records a failure.</summary>
    public bool IsError => !string.IsNullOrEmpty(Error);
}

/// <summary>
/// Persistence for the calculation history. Everything is written through the plugin's own
/// <see cref="IDataStore"/> partition (permission gated by DatabaseRead/DatabaseWrite); when no store
/// is available the cache still works in memory so the widget keeps rendering in a headless host.
/// </summary>
public sealed class HistoryStore
{
    /// <summary>Collection name used inside the plugin partition.</summary>
    public const string Collection = "history";

    /// <summary>How many entries are kept.</summary>
    public const int KeepLast = 500;

    private readonly IDataStore? _data;
    private readonly List<HistoryEntry> _cache = [];
    private readonly Lock _gate = new();

    /// <summary>Creates a store over an optional plugin data partition.</summary>
    public HistoryStore(IDataStore? data) => _data = data;

    /// <summary>Raised after the cache changed, so open widgets can repaint.</summary>
    public event EventHandler? Changed;

    /// <summary>Entries currently known, newest first.</summary>
    public IReadOnlyList<HistoryEntry> Cached
    {
        get
        {
            lock (_gate)
            {
                return [.. _cache];
            }
        }
    }

    /// <summary>True when entries can survive a restart.</summary>
    public bool IsPersistent => _data is not null;

    /// <summary>Reloads the newest <paramref name="max"/> entries from the database.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> RefreshAsync(int max = 200, CancellationToken ct = default)
    {
        if (_data is null)
        {
            return Cached;
        }

        var records = await _data.TailAsync<HistoryEntry>(Collection, max, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _cache.Clear();
            _cache.AddRange(records.Reverse());
        }

        RaiseChanged();
        return Cached;
    }

    /// <summary>Appends an entry, keeping the newest <see cref="KeepLast"/> items.</summary>
    public async Task AppendAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _cache.Insert(0, entry);
            if (_cache.Count > KeepLast)
            {
                _cache.RemoveRange(KeepLast, _cache.Count - KeepLast);
            }
        }

        if (_data is not null)
        {
            await _data.AppendAsync(Collection, entry, KeepLast, ct).ConfigureAwait(false);
        }

        RaiseChanged();
    }

    /// <summary>Appends a calculation from its expression and result text.</summary>
    public Task AppendAsync(string expression, string result, string? error = null, CancellationToken ct = default) =>
        AppendAsync(new HistoryEntry(expression ?? string.Empty, result ?? string.Empty, error, DateTimeOffset.UtcNow), ct);

    /// <summary>Removes every entry, from the cache and from the database.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _cache.Clear();
        }

        if (_data is not null)
        {
            await _data.ClearAsync(Collection, ct).ConfigureAwait(false);
        }

        RaiseChanged();
    }

    /// <summary>Case insensitive search over expressions and results, newest first.</summary>
    public IReadOnlyList<HistoryEntry> Search(string? query, int max = 200)
    {
        var limit = Math.Clamp(max, 1, KeepLast);
        var text = (query ?? string.Empty).Trim();
        lock (_gate)
        {
            if (text.Length == 0)
            {
                return [.. _cache.Take(limit)];
            }

            return [.. _cache
                .Where(e => e.Expression.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || e.Result.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || (e.Error?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(limit)];
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
