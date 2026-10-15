namespace Calcpad.Core.Canvas.Engine;

/// <summary>One reversible canvas mutation.</summary>
internal abstract class CanvasCommand
{
    protected CanvasCommand(string label, string? originPluginId)
    {
        Label = label;
        OriginPluginId = originPluginId;
    }

    /// <summary>Human readable label shown in undo menus.</summary>
    public string Label { get; }

    /// <summary>Plugin that caused the change, or null for user/host edits.</summary>
    public string? OriginPluginId { get; }

    /// <summary>Re-applies the change.</summary>
    public abstract void Redo(CanvasEngine engine);

    /// <summary>Reverses the change.</summary>
    public abstract void Undo(CanvasEngine engine);
}

/// <summary>Adds an object; undo detaches it again. The instance is reused so identity survives.</summary>
internal sealed class AddObjectCommand : CanvasCommand
{
    private readonly CanvasObject _target;

    public AddObjectCommand(CanvasObject target, string? originPluginId)
        : base("Add", originPluginId)
    {
        _target = target;
    }

    public override void Redo(CanvasEngine engine) => engine.AttachObject(_target, OriginPluginId);

    public override void Undo(CanvasEngine engine) => engine.DetachObject(_target.Id, OriginPluginId);
}

/// <summary>Removes an object; undo puts the very same instance back.</summary>
internal sealed class RemoveObjectCommand : CanvasCommand
{
    private readonly CanvasObject _target;

    public RemoveObjectCommand(CanvasObject target, string? originPluginId)
        : base("Delete", originPluginId)
    {
        _target = target;
    }

    public override void Redo(CanvasEngine engine) => engine.DetachObject(_target.Id, OriginPluginId);

    public override void Undo(CanvasEngine engine) => engine.AttachObject(_target, OriginPluginId);
}

/// <summary>Applies a captured object state, forward or backward.</summary>
internal sealed class UpdateObjectCommand : CanvasCommand
{
    private readonly string _objectId;
    private readonly CanvasObject _before;
    private readonly CanvasObject _after;

    public UpdateObjectCommand(string objectId, CanvasObject before, CanvasObject after, string? originPluginId)
        : base("Edit", originPluginId)
    {
        _objectId = objectId;
        _before = before;
        _after = after;
    }

    public override void Redo(CanvasEngine engine) => engine.ApplyObjectState(_objectId, _after, OriginPluginId);

    public override void Undo(CanvasEngine engine) => engine.ApplyObjectState(_objectId, _before, OriginPluginId);
}

/// <summary>Restores the selection that was current before a change.</summary>
internal sealed class SelectionCommand : CanvasCommand
{
    private readonly string[] _before;
    private readonly string[] _after;

    public SelectionCommand(IReadOnlyList<string> before, IReadOnlyList<string> after, string? originPluginId)
        : base("Selection", originPluginId)
    {
        _before = [.. before];
        _after = [.. after];
    }

    public override void Redo(CanvasEngine engine) => engine.ApplySelection(_after, OriginPluginId);

    public override void Undo(CanvasEngine engine) => engine.ApplySelection(_before, OriginPluginId);
}

/// <summary>Groups several commands into the single undo step produced by <c>BeginEdit</c>.</summary>
internal sealed class CompositeCommand : CanvasCommand
{
    private readonly List<CanvasCommand> _children = [];

    public CompositeCommand(string label, string? originPluginId)
        : base(label, originPluginId)
    {
    }

    public int Count => _children.Count;

    public void Add(CanvasCommand child) => _children.Add(child);

    /// <summary>Removes and returns the most recent child, used when an edit scope rolls back.</summary>
    public CanvasCommand? TakeLast()
    {
        if (_children.Count == 0)
        {
            return null;
        }

        var last = _children[^1];
        _children.RemoveAt(_children.Count - 1);
        return last;
    }

    public override void Redo(CanvasEngine engine)
    {
        foreach (var child in _children)
        {
            child.Redo(engine);
        }
    }

    public override void Undo(CanvasEngine engine)
    {
        for (var i = _children.Count - 1; i >= 0; i--)
        {
            _children[i].Undo(engine);
        }
    }
}
