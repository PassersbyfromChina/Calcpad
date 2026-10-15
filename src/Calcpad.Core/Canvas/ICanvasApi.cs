namespace Calcpad.Core.Canvas;

public enum CanvasChangeKind
{
    Added,
    Updated,
    Removed,
    SelectionChanged,
    CameraChanged,
    Reloaded
}

public sealed class CanvasChangedEventArgs : EventArgs
{
    public CanvasChangedEventArgs(CanvasChangeKind kind, IReadOnlyList<string> objectIds, string? originPluginId)
    {
        Kind = kind;
        ObjectIds = objectIds;
        OriginPluginId = originPluginId;
    }

    public CanvasChangeKind Kind { get; }

    public IReadOnlyList<string> ObjectIds { get; }

    /// <summary>Plugin that caused the change, or null for user/host edits.</summary>
    public string? OriginPluginId { get; }
}

/// <summary>Read request. All filters are optional and combine with AND.</summary>
public sealed record CanvasQuery
{
    public static CanvasQuery Everything { get; } = new();

    /// <summary>Restrict to these object kinds, e.g. <c>ink</c>, <c>math</c>.</summary>
    public IReadOnlyList<string>? Kinds { get; init; }

    /// <summary>Restrict to objects intersecting this world rectangle.</summary>
    public CRect? Region { get; init; }

    /// <summary>Restrict to the current selection.</summary>
    public bool SelectionOnly { get; init; }

    /// <summary>Include ink stroke geometry. Turn off for a cheap text-only pass.</summary>
    public bool IncludeStrokes { get; init; } = true;

    public int? Limit { get; init; }
}

/// <summary>Detached copy of canvas state, safe to hand to background work.</summary>
public sealed record CanvasSnapshot(
    string DocumentId,
    string Title,
    CRect ContentBounds,
    Camera Camera,
    IReadOnlyList<CanvasObject> Objects,
    IReadOnlyList<string> Selection)
{
    public IEnumerable<CanvasObject> OfKind(string kind) => Objects.Where(o => o.Kind == kind);

    public string ToPlainText()
    {
        var parts = new List<string>();
        foreach (var obj in Objects)
        {
            var text = obj switch
            {
                TextObject t => t.Text,
                MathObject m => string.IsNullOrEmpty(m.Result) ? m.Expression : $"{m.Expression} = {m.Result}",
                InkObject i => i.RecognizedText ?? string.Empty,
                AiCardObject a => $"{a.Question}\n{a.Answer}",
                WidgetObject w => w.Title,
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        return string.Join("\n", parts);
    }
}

/// <summary>
/// The canvas API a plugin sees. Every member is checked against the plugin's grants:
/// reads need <c>CanvasRead</c>, mutations need <c>CanvasWrite</c>.
/// </summary>
public interface ICanvasApi
{
    event EventHandler<CanvasChangedEventArgs>? Changed;

    /// <summary>Live camera. Reading it needs CanvasRead; changing it needs CanvasWrite.</summary>
    Camera Camera { get; }

    IReadOnlyList<string> Selection { get; }

    ValueTask<IReadOnlyList<CanvasObject>> QueryAsync(CanvasQuery? query = null, CancellationToken ct = default);

    ValueTask<CanvasObject?> GetAsync(string objectId, CancellationToken ct = default);

    /// <summary>Detached snapshot; the caller owns the result and may mutate it freely.</summary>
    ValueTask<CanvasSnapshot> SnapshotAsync(CanvasQuery? query = null, CancellationToken ct = default);

    /// <summary>Adds an object and returns its id.</summary>
    ValueTask<string> AddAsync(CanvasObject obj, CancellationToken ct = default);

    /// <summary>Mutates an object in place and returns false when the id is unknown.</summary>
    ValueTask<bool> UpdateAsync(string objectId, Action<CanvasObject> mutate, CancellationToken ct = default);

    ValueTask<bool> RemoveAsync(string objectId, CancellationToken ct = default);

    ValueTask SetSelectionAsync(IReadOnlyList<string> objectIds, CancellationToken ct = default);

    /// <summary>
    /// Groups everything until the returned scope is disposed into one undo step.
    /// Nested calls join the outer scope.
    /// </summary>
    ValueTask<ICanvasEdit> BeginEditAsync(string label, CancellationToken ct = default);

    /// <summary>Moves the camera so the region is comfortably visible.</summary>
    ValueTask RevealAsync(CRect region, CancellationToken ct = default);

    /// <summary>Asks the shell to activate a tool contributed by any plugin.</summary>
    ValueTask<bool> ActivateToolAsync(string toolId, CancellationToken ct = default);

    /// <summary>
    /// Renders a world region to a PNG crop. Needs CanvasRead. Used by recognition and by the
    /// ask-AI plugin, and the crop is exactly what the egress guard shows the user before upload.
    /// </summary>
    ValueTask<CanvasRaster?> RasterizeAsync(CRect region, double scale = 2.0, CancellationToken ct = default);
}

/// <summary>An open undo group.</summary>
public interface ICanvasEdit : IDisposable
{
    string Label { get; }

    ValueTask CommitAsync(CancellationToken ct = default);

    ValueTask RollbackAsync(CancellationToken ct = default);
}

/// <summary>Serialises a document and its objects, including plugin defined kinds.</summary>
public interface ICanvasSerializer
{
    string Serialize(CanvasDocument document);

    CanvasDocument Deserialize(string json);

    byte[] SerializeBinary(CanvasDocument document);

    CanvasDocument DeserializeBinary(ReadOnlySpan<byte> data);
}
