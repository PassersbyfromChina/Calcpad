namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Uniform grid spatial index over object bounds. A region query only visits the grid cells the
/// region overlaps, so panning and hit testing stay cheap with thousands of objects on the
/// infinite canvas. Objects that are enormous or carry non finite coordinates are kept in a small
/// overflow set instead of being written into thousands of cells.
/// </summary>
public sealed class CanvasGridIndex
{
    private const int MaxCellsPerObject = 64;
    private const long MaxCellVisits = 100_000;
    private const int CoordinateLimit = 1_000_000;

    private readonly Dictionary<long, List<string>> _cells = [];
    private readonly Dictionary<string, CRect> _bounds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _spanning = new(StringComparer.Ordinal);

    /// <summary>Creates an index. Cell size is the grid pitch in world units.</summary>
    public CanvasGridIndex(double cellSize = 512)
    {
        CellSize = double.IsFinite(cellSize) && cellSize >= 1 ? cellSize : 512;
    }

    /// <summary>Grid pitch in world units.</summary>
    public double CellSize { get; }

    /// <summary>Number of indexed objects.</summary>
    public int Count => _bounds.Count;

    /// <summary>Number of non empty grid cells.</summary>
    public int CellCount => _cells.Count;

    /// <summary>Number of objects held in the overflow set.</summary>
    public int SpanningCount => _spanning.Count;

    /// <summary>Objects the most recent query had to consider. Used by diagnostics and tests.</summary>
    public int LastCandidateCount { get; private set; }

    /// <summary>Grid cells the most recent query touched.</summary>
    public int LastCellVisitCount { get; private set; }

    /// <summary>True when the most recent query had to fall back to a full scan.</summary>
    public bool LastScanWasFull { get; private set; }

    /// <summary>Every indexed object id. Not ordered.</summary>
    public IEnumerable<string> AllIds => _bounds.Keys;

    /// <summary>Adds or replaces one object.</summary>
    public void Add(string id, CRect bounds)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        if (_bounds.ContainsKey(id))
        {
            Remove(id);
        }

        _bounds[id] = bounds;
        if (!TryRange(bounds, out var minX, out var minY, out var maxX, out var maxY))
        {
            _spanning.Add(id);
            return;
        }

        var cells = ((long)maxX - minX + 1) * ((long)maxY - minY + 1);
        if (cells > MaxCellsPerObject)
        {
            _spanning.Add(id);
            return;
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var key = Key(x, y);
                if (!_cells.TryGetValue(key, out var list))
                {
                    list = [];
                    _cells[key] = list;
                }

                list.Add(id);
            }
        }
    }

    /// <summary>Removes one object. Returns false when it was not indexed.</summary>
    public bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id) || !_bounds.Remove(id, out var bounds))
        {
            return false;
        }

        if (_spanning.Remove(id))
        {
            return true;
        }

        if (!TryRange(bounds, out var minX, out var minY, out var maxX, out var maxY))
        {
            return true;
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var key = Key(x, y);
                if (!_cells.TryGetValue(key, out var list))
                {
                    continue;
                }

                list.Remove(id);
                if (list.Count == 0)
                {
                    _cells.Remove(key);
                }
            }
        }

        return true;
    }

    /// <summary>Moves an object to new bounds.</summary>
    public void Update(string id, CRect bounds) => Add(id, bounds);

    /// <summary>Drops every entry.</summary>
    public void Clear()
    {
        _cells.Clear();
        _bounds.Clear();
        _spanning.Clear();
        LastCandidateCount = 0;
        LastCellVisitCount = 0;
        LastScanWasFull = false;
    }

    /// <summary>Rebuilds the whole index from a document.</summary>
    public void Rebuild(IEnumerable<CanvasObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        Clear();
        foreach (var obj in objects)
        {
            Add(obj.Id, obj.Bounds);
        }
    }

    /// <summary>Collects the ids whose bounds intersect <paramref name="region"/>.</summary>
    public IReadOnlyList<string> Query(CRect region)
    {
        var results = new List<string>();
        Query(region, results);
        return results;
    }

    /// <summary>
    /// Collects the ids whose bounds intersect <paramref name="region"/> into
    /// <paramref name="results"/>, which is cleared first.
    /// </summary>
    public void Query(CRect region, List<string> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        LastCandidateCount = 0;
        LastCellVisitCount = 0;
        LastScanWasFull = false;

        if (_bounds.Count == 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!TryRange(region, out var minX, out var minY, out var maxX, out var maxY))
        {
            ScanAll(seen, results, region);
            return;
        }

        var cells = ((long)maxX - minX + 1) * ((long)maxY - minY + 1);
        if (cells > MaxCellVisits || cells > ((long)_cells.Count * 8) + 64)
        {
            ScanAll(seen, results, region);
            return;
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                LastCellVisitCount++;
                if (!_cells.TryGetValue(Key(x, y), out var list))
                {
                    continue;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    var id = list[i];
                    if (seen.Add(id) && Intersects(id, region))
                    {
                        results.Add(id);
                    }
                }
            }
        }

        foreach (var id in _spanning)
        {
            if (seen.Add(id) && Intersects(id, region))
            {
                results.Add(id);
            }
        }

        LastCandidateCount = seen.Count;
    }

    private void ScanAll(HashSet<string> seen, List<string> results, CRect region)
    {
        LastScanWasFull = true;
        foreach (var pair in _bounds)
        {
            seen.Add(pair.Key);
            if (Intersects(pair.Value, region))
            {
                results.Add(pair.Key);
            }
        }

        LastCandidateCount = seen.Count;
    }

    private bool Intersects(string id, CRect region) =>
        _bounds.TryGetValue(id, out var bounds) && bounds.IntersectsWith(region);

    private static bool Intersects(CRect bounds, CRect region) => bounds.IntersectsWith(region);

    private bool TryRange(CRect bounds, out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = minY = maxX = maxY = 0;
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
        {
            return false;
        }

        var left = Math.Floor(bounds.Left / CellSize);
        var top = Math.Floor(bounds.Top / CellSize);
        var right = Math.Floor(bounds.Right / CellSize);
        var bottom = Math.Floor(bounds.Bottom / CellSize);
        if (left < -CoordinateLimit || top < -CoordinateLimit || right > CoordinateLimit || bottom > CoordinateLimit)
        {
            return false;
        }

        minX = (int)left;
        minY = (int)top;
        maxX = (int)right;
        maxY = (int)bottom;
        return true;
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
