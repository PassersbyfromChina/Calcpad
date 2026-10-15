namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Undo / redo stack for the canvas. Every user gesture and every plugin mutation is recorded as a
/// reversible command; <see cref="CanvasEngine.BeginEdit"/> merges the commands of one gesture into
/// a single composite step, and nested scopes join the outer step instead of creating their own.
/// </summary>
public sealed class CanvasHistory
{
    private readonly List<CanvasCommand> _undo = [];
    private readonly List<CanvasCommand> _redo = [];
    private CompositeCommand? _group;
    private int _depth;

    /// <summary>Maximum number of retained undo steps; the oldest step is dropped first.</summary>
    public int Capacity { get; set; } = 500;

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when a previous undo can be replayed.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Number of retained undo steps.</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>Number of retained redo steps.</summary>
    public int RedoDepth => _redo.Count;

    /// <summary>True while an edit scope is open.</summary>
    public bool IsGrouping => _depth > 0;

    /// <summary>Label of the step that <see cref="CanvasEngine.Undo"/> would revert.</summary>
    public string? NextUndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>Label of the step that <see cref="CanvasEngine.Redo"/> would replay.</summary>
    public string? NextRedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Undo labels, newest first.</summary>
    public IReadOnlyList<string> UndoLabels
    {
        get
        {
            var labels = new string[_undo.Count];
            for (var i = 0; i < _undo.Count; i++)
            {
                labels[i] = _undo[_undo.Count - 1 - i].Label;
            }

            return labels;
        }
    }

    /// <summary>Redo labels, next step first.</summary>
    public IReadOnlyList<string> RedoLabels
    {
        get
        {
            var labels = new string[_redo.Count];
            for (var i = 0; i < _redo.Count; i++)
            {
                labels[i] = _redo[_redo.Count - 1 - i].Label;
            }

            return labels;
        }
    }

    /// <summary>Drops every recorded step and closes any open group.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _group = null;
        _depth = 0;
    }

    internal int GroupDepth => _depth;

    internal int GroupCount => _group?.Count ?? 0;

    internal void BeginGroup(string label, string? originPluginId)
    {
        if (_depth == 0)
        {
            _group = new CompositeCommand(string.IsNullOrWhiteSpace(label) ? "Edit" : label, originPluginId);
        }

        _depth++;
    }

    /// <summary>
    /// Closes the innermost scope. Committing the root scope pushes its composite when it has
    /// children; rolling back drops it, because the engine has already reversed its commands.
    /// </summary>
    internal void EndGroup(bool rollback)
    {
        if (_depth > 0)
        {
            _depth--;
        }

        if (_depth > 0)
        {
            return;
        }

        var group = _group;
        _group = null;
        if (rollback || group is not { Count: > 0 })
        {
            return;
        }

        PushCore(group);
    }

    /// <summary>Removes children beyond <paramref name="keepCount"/> and returns them newest first.</summary>
    internal IReadOnlyList<CanvasCommand> TruncateGroup(int keepCount)
    {
        var removed = new List<CanvasCommand>();
        while (_group is not null && _group.Count > keepCount)
        {
            var command = _group.TakeLast();
            if (command is null)
            {
                break;
            }

            removed.Add(command);
        }

        return removed;
    }

    internal void Push(CanvasCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_depth > 0 && _group is not null)
        {
            _group.Add(command);
            return;
        }

        PushCore(command);
    }

    internal CanvasCommand? TakeUndo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return command;
    }

    internal CanvasCommand? TakeRedo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return command;
    }

    internal void PushRedo(CanvasCommand command)
    {
        _redo.Add(command);
        Trim();
    }

    internal void PushUndo(CanvasCommand command)
    {
        _undo.Add(command);
        Trim();
    }

    private void PushCore(CanvasCommand command)
    {
        _undo.Add(command);
        _redo.Clear();
        Trim();
    }

    private void Trim()
    {
        var capacity = Capacity < 1 ? 1 : Capacity;
        while (_undo.Count > capacity)
        {
            _undo.RemoveAt(0);
        }

        while (_redo.Count > capacity)
        {
            _redo.RemoveAt(0);
        }
    }
}
