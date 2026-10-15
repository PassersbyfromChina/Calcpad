namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// An open undo group. Disposing the scope commits it, which is what <c>using</c> blocks in tool
/// code rely on; <see cref="CommitAsync"/> and <see cref="RollbackAsync"/> cover the plugin API
/// where an edit may have to be abandoned.
/// </summary>
public sealed class CanvasEditScope : ICanvasEdit
{
    private readonly CanvasEngine _engine;
    private int _state;

    internal CanvasEditScope(CanvasEngine engine, string label, string? originPluginId, bool isRoot, int startIndex)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Label = label;
        OriginPluginId = originPluginId;
        IsRoot = isRoot;
        StartIndex = startIndex;
    }

    /// <inheritdoc />
    public string Label { get; }

    /// <summary>Plugin that owns this edit, or null for user edits.</summary>
    public string? OriginPluginId { get; }

    /// <summary>True when this scope created the outermost group.</summary>
    public bool IsRoot { get; }

    /// <summary>Number of commands the group held when this scope opened, used for rollback.</summary>
    public int StartIndex { get; }

    /// <summary>True once the scope has been committed or rolled back.</summary>
    public bool IsClosed => Volatile.Read(ref _state) != 0;

    /// <summary>Closes the scope and keeps the recorded changes.</summary>
    public void Commit()
    {
        if (Interlocked.Exchange(ref _state, 1) != 0)
        {
            return;
        }

        _engine.CompleteEdit(this);
    }

    /// <summary>Closes the scope and reverses everything it recorded.</summary>
    public void Rollback()
    {
        if (Interlocked.Exchange(ref _state, 2) != 0)
        {
            return;
        }

        _engine.RollbackEdit(this);
    }

    /// <inheritdoc />
    public ValueTask CommitAsync(CancellationToken ct = default)
    {
        Commit();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RollbackAsync(CancellationToken ct = default)
    {
        Rollback();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Commit();
}
