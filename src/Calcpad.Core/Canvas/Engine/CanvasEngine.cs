using Calcpad.Core.Security;

namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// The infinite canvas engine: ownership of the document, the camera, the spatial index, undo /
/// history and the permission checked plugin facade. Everything the shell and the plugin host need
/// goes through <see cref="ICanvasEngine"/>; plugins only ever receive
/// <see cref="ApiFor"/> results, where every call re-checks the plugin's grants.
/// </summary>
/// <remarks>
/// All mutations are serialised on an internal gate so a plugin running on a background thread can
/// safely add or edit objects while the UI thread renders. Events are raised on the calling thread:
/// the view marshals to the UI thread when needed.
/// </remarks>
public sealed class CanvasEngine : ICanvasEngineHost
{
    private readonly object _gate = new();
    private readonly CanvasHistory _history = new();
    private readonly CanvasGridIndex _index;
    private readonly CanvasObjectRegistry _registry;
    private readonly CanvasSerializer _serializer;
    private readonly Dictionary<string, PluginCanvasApi> _apis = new(StringComparer.Ordinal);
    private ICanvasEngineLog _log = NullCanvasEngineLog.Instance;
    private ICanvasRasterizer? _rasterizer;
    private bool _applying;

    /// <summary>Creates an engine with an empty document.</summary>
    /// <param name="permissions">Authorization authority consulted by every plugin API call.</param>
    /// <param name="registry">Kind to type map; a private registry is created when omitted.</param>
    public CanvasEngine(IPermissionBroker permissions, CanvasObjectRegistry? registry = null)
    {
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _registry = registry ?? new CanvasObjectRegistry();
        _serializer = new CanvasSerializer(_registry);
        _index = new CanvasGridIndex();
        Document = new CanvasDocument();
    }

    /// <summary>Authorization authority used by <see cref="ApiFor"/>.</summary>
    public IPermissionBroker Permissions { get; }

    /// <summary>Log sink for engine and plugin failures; never null.</summary>
    public ICanvasEngineLog Log
    {
        get => _log;
        set => _log = value ?? NullCanvasEngineLog.Instance;
    }

    /// <summary>Viewport size in device independent pixels, kept up to date by the view.</summary>
    public CSize ViewportSize { get; set; } = new(1280, 800);

    /// <summary>
    /// Callback the shell installs so plugins can ask for a tool through
    /// <see cref="ICanvasApi.ActivateToolAsync"/>. Null means "no tool host installed".
    /// </summary>
    public Func<string, CancellationToken, ValueTask<bool>>? ToolActivator { get; set; }

    /// <summary>Undo / redo state. Exposed so the shell can render undo labels.</summary>
    public CanvasHistory History => _history;

    /// <summary>Spatial index statistics of the most recent region query.</summary>
    public CanvasGridIndex Index => _index;

    /// <inheritdoc />
    public CanvasDocument Document { get; private set; }

    /// <inheritdoc />
    public Camera Camera => Document.Camera;

    /// <inheritdoc />
    public CanvasObjectRegistry Registry => _registry;

    /// <inheritdoc />
    public ICanvasSerializer Serializer => _serializer;

    /// <inheritdoc />
    public ICanvasRasterizer? Rasterizer
    {
        get => _rasterizer;
        set => _rasterizer = value;
    }

    /// <inheritdoc />
    public bool CanUndo => _history.CanUndo;

    /// <inheritdoc />
    public bool CanRedo => _history.CanRedo;

    /// <inheritdoc />
    public event EventHandler<CanvasChangedEventArgs>? Changed;

    /// <inheritdoc />
    public event EventHandler? HistoryChanged;

    /// <inheritdoc />
    public ICanvasApi ApiFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
        {
            if (!_apis.TryGetValue(pluginId, out var api))
            {
                api = new PluginCanvasApi(this, pluginId);
                _apis[pluginId] = api;
            }

            return api;
        }
    }

    /// <summary>Drops the cached facade of a plugin, e.g. when it is unloaded.</summary>
    public void ReleaseApi(string pluginId)
    {
        lock (_gate)
        {
            if (_apis.Remove(pluginId, out var api))
            {
                api.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CanvasObject> Query(CanvasQuery? query = null)
    {
        lock (_gate)
        {
            var effective = query ?? CanvasQuery.Everything;
            var result = Select(effective);
            if (effective.IncludeStrokes)
            {
                return result;
            }

            for (var i = 0; i < result.Count; i++)
            {
                if (result[i] is InkObject ink)
                {
                    result[i] = StripStrokes(ink);
                }
            }

            return result;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CanvasObject> Export(CanvasQuery? query = null)
    {
        lock (_gate)
        {
            var effective = query ?? CanvasQuery.Everything;
            var source = Select(effective);
            var result = new List<CanvasObject>(source.Count);
            foreach (var obj in source)
            {
                if (!effective.IncludeStrokes && obj is InkObject ink)
                {
                    result.Add(StripStrokes(ink));
                    continue;
                }

                result.Add(obj.Clone());
            }

            return result;
        }
    }

    /// <summary>Live object lookup by id, or null.</summary>
    public CanvasObject? Get(string? objectId)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return null;
        }

        lock (_gate)
        {
            return Document.Find(objectId);
        }
    }

    /// <summary>Detached copy of one object, or null.</summary>
    public CanvasObject? GetDetached(string? objectId)
    {
        lock (_gate)
        {
            return Document.Find(objectId)?.Clone();
        }
    }

    /// <summary>Thread safe copy of the current selection ids.</summary>
    public IReadOnlyList<string> SelectionSnapshot()
    {
        lock (_gate)
        {
            return Document.Selection.ToArray();
        }
    }

    /// <summary>Builds a fully detached snapshot of the document, camera and selection.</summary>
    public CanvasSnapshot CreateSnapshot(CanvasQuery? query = null)
    {
        lock (_gate)
        {
            var effective = query ?? CanvasQuery.Everything;
            var source = Select(effective);
            var objects = new List<CanvasObject>(source.Count);
            foreach (var obj in source)
            {
                if (!effective.IncludeStrokes && obj is InkObject ink)
                {
                    objects.Add(StripStrokes(ink));
                    continue;
                }

                objects.Add(obj.Clone());
            }

            return new CanvasSnapshot(
                Document.Id,
                Document.Title,
                Document.ContentBounds,
                Camera.Clone(),
                objects,
                Document.Selection.ToArray());
        }
    }

    /// <inheritdoc />
    public CanvasObject? HitTest(CPoint world, double tolerance = 0)
    {
        if (!IsFinite(world))
        {
            return null;
        }

        lock (_gate)
        {
            var slack = double.IsFinite(tolerance) && tolerance > 0 ? tolerance : 0;
            var probe = new CRect(world.X - slack, world.Y - slack, slack * 2, slack * 2);
            var ids = new List<string>();
            _index.Query(probe, ids);

            CanvasObject? best = null;
            foreach (var id in ids)
            {
                var obj = Document.Find(id);
                if (obj is null || !obj.Visible || obj.Locked)
                {
                    continue;
                }

                if (best is not null && !IsAbove(obj, best))
                {
                    continue;
                }

                if (CanvasHitTester.Hits(obj, world, slack))
                {
                    best = obj;
                }
            }

            return best;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CanvasObject> LassoSelect(IReadOnlyList<CPoint> polygon, bool fullyEnclosed = false)
    {
        lock (_gate)
        {
            var points = new List<CPoint>();
            if (LassoGeometry.Normalize(polygon, points) == 0)
            {
                SetSelection([], null);
                return [];
            }

            var ids = new List<string>();
            _index.Query(CRect.FromPoints(points).Inflate(1), ids);

            var hits = new List<CanvasObject>();
            foreach (var id in ids)
            {
                var obj = Document.Find(id);
                if (obj is null || !obj.Visible || obj.Locked)
                {
                    continue;
                }

                var selected = fullyEnclosed
                    ? LassoGeometry.RectInsidePolygon(obj.Bounds, points)
                    : LassoGeometry.RectIntersectsPolygon(obj.Bounds, points);
                if (selected)
                {
                    hits.Add(obj);
                }
            }

            hits.Sort(CompareByDepth);
            SetSelection(hits.Select(o => o.Id).ToArray(), null);
            return hits;
        }
    }

    /// <inheritdoc />
    public IDisposable BeginEdit(string label, string? originPluginId = null)
    {
        lock (_gate)
        {
            var isRoot = _history.GroupDepth == 0;
            var startIndex = isRoot ? 0 : _history.GroupCount;
            _history.BeginGroup(label, originPluginId);
            return new CanvasEditScope(this, string.IsNullOrWhiteSpace(label) ? "Edit" : label, originPluginId, isRoot, startIndex);
        }
    }

    /// <summary>Closes an edit scope and keeps its changes as one undo step.</summary>
    public void CompleteEdit(CanvasEditScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            _history.EndGroup(rollback: false);
            RaiseHistoryChanged();
        }
    }

    /// <summary>Closes an edit scope and reverses everything it recorded.</summary>
    public void RollbackEdit(CanvasEditScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            var commands = _history.TruncateGroup(scope.StartIndex);
            if (commands.Count > 0)
            {
                _applying = true;
                try
                {
                    foreach (var command in commands)
                    {
                        try
                        {
                            command.Undo(this);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Rolling back '{command.Label}' failed.", ex);
                        }
                    }
                }
                finally
                {
                    _applying = false;
                }
            }

            _history.EndGroup(rollback: true);
            RaiseHistoryChanged();
        }
    }

    /// <inheritdoc />
    public string Add(CanvasObject obj, string? originPluginId = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        lock (_gate)
        {
            EnsureUniqueId(obj);
            if (originPluginId is not null && string.IsNullOrEmpty(obj.OwnerPluginId))
            {
                obj.OwnerPluginId = originPluginId;
            }

            if (obj.Bounds.IsEmpty)
            {
                obj.RecomputeBounds();
            }

            if (obj.ZOrder == 0 && Document.Objects.Count > 0)
            {
                obj.ZOrder = Document.NextZOrder();
            }

            obj.Touch();
            AttachObject(obj, originPluginId);
            PushHistory(new AddObjectCommand(obj, originPluginId));
            return obj.Id;
        }
    }

    /// <inheritdoc />
    public bool Update(string objectId, Action<CanvasObject> mutate, string? originPluginId = null)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var target = Document.Find(objectId);
            if (target is null)
            {
                return false;
            }

            var before = target.Clone();
            try
            {
                mutate(target);
            }
            catch
            {
                ObjectStateCopier.Copy(before.Clone(), target);
                throw;
            }

            target.Id = objectId;
            target.RecomputeBounds();
            target.Touch();
            var after = target.Clone();
            _index.Update(objectId, target.Bounds);
            RefreshGroupBounds();
            Document.ModifiedAt = DateTimeOffset.UtcNow;
            PushHistory(new UpdateObjectCommand(objectId, before, after, originPluginId));
            RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.Updated, [objectId], originPluginId));
            return true;
        }
    }

    /// <summary>
    /// Applies a mutation to a detached copy and then writes the result back. Used by the plugin
    /// facade so plugin code never receives a reference to a live document object.
    /// </summary>
    public bool UpdateDetached(string objectId, Action<CanvasObject> mutate, string? originPluginId = null)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var target = Document.Find(objectId);
            if (target is null)
            {
                return false;
            }

            var before = target.Clone();
            var work = target.Clone();
            mutate(work);
            work.Id = objectId;
            work.RecomputeBounds();
            work.Touch();
            var after = work.Clone();
            if (target.GetType() != work.GetType())
            {
                throw new InvalidOperationException("A plugin may not change the kind of an existing canvas object.");
            }

            ObjectStateCopier.Copy(work, target);
            _index.Update(objectId, target.Bounds);
            RefreshGroupBounds();
            Document.ModifiedAt = DateTimeOffset.UtcNow;
            PushHistory(new UpdateObjectCommand(objectId, before, after, originPluginId));
            RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.Updated, [objectId], originPluginId));
            return true;
        }
    }

    /// <inheritdoc />
    public bool Remove(string objectId, string? originPluginId = null)
    {
        if (string.IsNullOrEmpty(objectId))
        {
            return false;
        }

        lock (_gate)
        {
            var target = Document.Find(objectId);
            if (target is null)
            {
                return false;
            }

            DetachObject(objectId, originPluginId);
            PushHistory(new RemoveObjectCommand(target, originPluginId));
            return true;
        }
    }

    /// <inheritdoc />
    public void SetSelection(IEnumerable<string> objectIds) => SetSelection(objectIds, null);

    /// <summary>Replaces the selection, recording which plugin caused the change.</summary>
    public void SetSelection(IEnumerable<string> objectIds, string? originPluginId)
    {
        ArgumentNullException.ThrowIfNull(objectIds);
        lock (_gate)
        {
            var before = Document.Selection.ToArray();
            var next = NormalizeSelection(objectIds);
            if (SameIds(before, next))
            {
                return;
            }

            PushHistory(new SelectionCommand(before, next, originPluginId));
            ApplySelection(next, originPluginId);
        }
    }

    /// <summary>Moves every listed object by a world space delta.</summary>
    /// <returns>The number of objects that were moved.</returns>
    public int Translate(IReadOnlyList<string> objectIds, double worldDx, double worldDy, string? originPluginId = null)
    {
        if (objectIds is null || objectIds.Count == 0)
        {
            return 0;
        }

        if (!double.IsFinite(worldDx) || !double.IsFinite(worldDy) || (worldDx == 0 && worldDy == 0))
        {
            return 0;
        }

        lock (_gate)
        {
            var moved = 0;
            foreach (var id in objectIds.Distinct(StringComparer.Ordinal))
            {
                var target = Document.Find(id);
                if (target is null || target.Locked)
                {
                    continue;
                }

                if (Update(id, obj => TranslateObject(obj, worldDx, worldDy), originPluginId))
                {
                    moved++;
                }
            }

            return moved;
        }
    }

    /// <summary>
    /// Erases whole ink strokes whose geometry passes within <paramref name="worldRadius"/> of
    /// <paramref name="world"/>. Objects left without strokes are deleted.
    /// </summary>
    /// <returns>The number of strokes that were erased.</returns>
    public int EraseInk(CPoint world, double worldRadius, string? originPluginId = null)
    {
        if (!IsFinite(world) || !double.IsFinite(worldRadius))
        {
            return 0;
        }

        var radius = worldRadius > 0 ? worldRadius : 1;
        lock (_gate)
        {
            var probe = new CRect(world.X - radius, world.Y - radius, radius * 2, radius * 2);
            var ids = new List<string>();
            _index.Query(probe, ids);

            var erased = 0;
            foreach (var id in ids)
            {
                if (Document.Find(id) is not InkObject ink)
                {
                    continue;
                }

                var victims = new List<InkStroke>();
                foreach (var stroke in ink.AllStrokes)
                {
                    if (CanvasHitTester.HitsStroke(stroke, world, radius))
                    {
                        victims.Add(stroke);
                    }
                }

                if (victims.Count == 0)
                {
                    continue;
                }

                erased += victims.Count;
                var total = ink.Groups.Sum(g => g.Strokes.Count);
                if (victims.Count >= total)
                {
                    Remove(id, originPluginId);
                    continue;
                }

                Update(id, target =>
                {
                    var typed = (InkObject)target;
                    foreach (var group in typed.Groups)
                    {
                        group.Strokes.RemoveAll(victims.Contains);
                    }
                }, originPluginId);
            }

            return erased;
        }
    }

    /// <inheritdoc />
    public void Undo()
    {
        lock (_gate)
        {
            if (_applying || _history.IsGrouping)
            {
                return;
            }

            var command = _history.TakeUndo();
            if (command is null)
            {
                return;
            }

            _applying = true;
            try
            {
                command.Undo(this);
            }
            catch (Exception ex)
            {
                Log.Error($"Undo of '{command.Label}' failed.", ex);
            }
            finally
            {
                _applying = false;
            }

            _history.PushRedo(command);
            RaiseHistoryChanged();
        }
    }

    /// <inheritdoc />
    public void Redo()
    {
        lock (_gate)
        {
            if (_applying || _history.IsGrouping)
            {
                return;
            }

            var command = _history.TakeRedo();
            if (command is null)
            {
                return;
            }

            _applying = true;
            try
            {
                command.Redo(this);
            }
            catch (Exception ex)
            {
                Log.Error($"Redo of '{command.Label}' failed.", ex);
            }
            finally
            {
                _applying = false;
            }

            _history.PushUndo(command);
            RaiseHistoryChanged();
        }
    }

    /// <inheritdoc />
    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            RaiseHistoryChanged();
        }
    }

    /// <inheritdoc />
    public void Load(CanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            Document = document;
            _index.Rebuild(Document.Objects);
            _history.Clear();
            var ids = Document.Objects.Select(o => o.Id).ToArray();
            RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.Reloaded, ids, null));
            RaiseHistoryChanged();
        }
    }

    /// <summary>Moves the camera so a world region is comfortably visible.</summary>
    public void Reveal(CRect region, string? originPluginId = null)
    {
        if (!IsUsableRegion(region) || region.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            var padded = new CRect(
                region.X,
                region.Y,
                Math.Max(region.Width, 1),
                Math.Max(region.Height, 1));
            Camera.FitTo(padded, ViewportSize, 64);
            RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.CameraChanged, [], originPluginId));
        }
    }

    /// <summary>Raises <see cref="Changed"/> with <see cref="CanvasChangeKind.CameraChanged"/>.</summary>
    public void NotifyCameraChanged(string? originPluginId = null)
    {
        lock (_gate)
        {
            RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.CameraChanged, [], originPluginId));
        }
    }

    /// <summary>Asks the shell to activate a tool, e.g. after a plugin command.</summary>
    public async ValueTask<bool> ActivateToolAsync(string toolId, string? originPluginId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return false;
        }

        var activator = ToolActivator;
        if (activator is null)
        {
            Log.Warn($"No tool activator is installed; tool '{toolId}' was not activated.");
            return false;
        }

        try
        {
            return await activator(toolId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Activating tool '{toolId}' failed.", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<CanvasRaster?> RasterizeAsync(CRect region, double scale = 2.0, CancellationToken ct = default)
    {
        var rasterizer = _rasterizer;
        if (rasterizer is null || !IsUsableRegion(region) || region.IsEmpty)
        {
            return null;
        }

        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        scale = Math.Clamp(scale, 0.05, 16);
        try
        {
            return await rasterizer.RasterizeAsync(region, scale, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Rasterizing the canvas failed.", ex);
            return null;
        }
    }

    // ----- operations invoked by undo commands -------------------------------------------------

    /// <summary>Inserts an object without recording history. Used by add and by undo of delete.</summary>
    internal void AttachObject(CanvasObject obj, string? originPluginId)
    {
        if (obj is null || Document.Find(obj.Id) is not null)
        {
            return;
        }

        if (obj.Bounds.IsEmpty)
        {
            obj.RecomputeBounds();
        }

        Document.Objects.Add(obj);
        _index.Add(obj.Id, obj.Bounds);
        RefreshGroupBounds();
        Document.ModifiedAt = DateTimeOffset.UtcNow;
        RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.Added, [obj.Id], originPluginId));
    }

    /// <summary>Removes an object without recording history. Used by delete and by undo of add.</summary>
    internal void DetachObject(string objectId, string? originPluginId)
    {
        var target = Document.Find(objectId);
        if (target is null)
        {
            return;
        }

        Document.Objects.Remove(target);
        _index.Remove(objectId);
        Document.Selection.Remove(objectId);
        RefreshGroupBounds();
        Document.ModifiedAt = DateTimeOffset.UtcNow;
        RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.Removed, [objectId], originPluginId));
    }

    /// <summary>Restores a captured object state onto the live instance.</summary>
    internal void ApplyObjectState(string objectId, CanvasObject snapshot, string? originPluginId)
    {
        if (snapshot is null)
        {
            return;
        }

        var target = Document.Find(objectId);
        if (target is null || target.GetType() != snapshot.GetType())
        {
            return;
        }

        ObjectStateCopier.Copy(snapshot.Clone(), target);
        target.Id = objectId;
        _index.Update(objectId, target.Bounds);
        RefreshGroupBounds();
        Document.ModifiedAt = DateTimeOffset.UtcNow;
        RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.Updated, [objectId], originPluginId));
    }

    /// <summary>Replaces the selection, raising a change event when it actually differs.</summary>
    internal void ApplySelection(IEnumerable<string> objectIds, string? originPluginId)
    {
        var next = NormalizeSelection(objectIds);
        if (SameIds(Document.Selection, next))
        {
            return;
        }

        Document.Selection.Clear();
        Document.Selection.AddRange(next);
        RaiseChanged(new CanvasChangedEventArgs(CanvasChangeKind.SelectionChanged, next, originPluginId));
    }

    // ----- helpers -----------------------------------------------------------------------------

    private void PushHistory(CanvasCommand command)
    {
        if (_applying)
        {
            return;
        }

        _history.Push(command);
        RaiseHistoryChanged();
    }

    private List<CanvasObject> Select(CanvasQuery query)
    {
        List<CanvasObject> result;
        if (query.Region is { } region && IsUsableRegion(region))
        {
            var ids = new List<string>();
            _index.Query(region, ids);
            result = new List<CanvasObject>(ids.Count);
            foreach (var id in ids)
            {
                var obj = Document.Find(id);
                if (obj is not null && obj.Bounds.IntersectsWith(region))
                {
                    result.Add(obj);
                }
            }
        }
        else
        {
            result = [.. Document.Objects];
        }

        if (query.SelectionOnly)
        {
            var selection = new HashSet<string>(Document.Selection, StringComparer.Ordinal);
            result.RemoveAll(o => !selection.Contains(o.Id));
        }

        if (query.Kinds is { Count: > 0 } kinds)
        {
            var wanted = new HashSet<string>(kinds, StringComparer.Ordinal);
            result.RemoveAll(o => !wanted.Contains(o.Kind));
        }

        result.Sort(CompareByDepth);
        if (query.Limit is { } limit && limit >= 0 && result.Count > limit)
        {
            result.RemoveRange(limit, result.Count - limit);
        }

        return result;
    }

    private static InkObject StripStrokes(InkObject ink)
    {
        var copy = (InkObject)ink.Clone();
        copy.Groups.Clear();
        return copy;
    }

    private static int CompareByDepth(CanvasObject a, CanvasObject b)
    {
        var byZ = a.ZOrder.CompareTo(b.ZOrder);
        return byZ != 0 ? byZ : a.CreatedAt.CompareTo(b.CreatedAt);
    }

    private static bool IsAbove(CanvasObject candidate, CanvasObject current)
    {
        if (candidate.ZOrder != current.ZOrder)
        {
            return candidate.ZOrder > current.ZOrder;
        }

        return candidate.CreatedAt >= current.CreatedAt;
    }

    private void EnsureUniqueId(CanvasObject obj)
    {
        if (string.IsNullOrEmpty(obj.Id) || Document.Find(obj.Id) is not null)
        {
            obj.Id = Guid.NewGuid().ToString("n");
        }
    }

    private void RefreshGroupBounds()
    {
        List<GroupObject>? groups = null;
        foreach (var obj in Document.Objects)
        {
            if (obj is GroupObject group)
            {
                groups ??= [];
                groups.Add(group);
            }
        }

        if (groups is null)
        {
            return;
        }

        foreach (var group in groups)
        {
            var bounds = CRect.Empty;
            foreach (var childId in group.Children)
            {
                var child = Document.Find(childId);
                if (child is not null)
                {
                    bounds = bounds.Union(child.Bounds);
                }
            }

            if (bounds.IsEmpty || SameRect(bounds, group.Bounds))
            {
                continue;
            }

            group.Bounds = bounds;
            _index.Update(group.Id, bounds);
        }
    }

    private static void TranslateObject(CanvasObject obj, double dx, double dy)
    {
        switch (obj)
        {
            case InkObject ink:
                foreach (var group in ink.Groups)
                {
                    foreach (var stroke in group.Strokes)
                    {
                        for (var i = 0; i < stroke.Points.Count; i++)
                        {
                            var point = stroke.Points[i];
                            if (double.IsFinite(point.X) && double.IsFinite(point.Y))
                            {
                                stroke.Points[i] = new InkPoint(point.X + dx, point.Y + dy, point.Pressure, point.TimeMs);
                            }
                        }
                    }
                }

                break;

            case ShapeObject shape:
                for (var i = 0; i < shape.Points.Count; i++)
                {
                    var point = shape.Points[i];
                    if (double.IsFinite(point.X) && double.IsFinite(point.Y))
                    {
                        shape.Points[i] = new CPoint(point.X + dx, point.Y + dy);
                    }
                }

                break;
        }

        obj.Bounds = obj.Bounds.Translate(dx, dy);
        obj.RecomputeBounds();
    }

    private List<string> NormalizeSelection(IEnumerable<string> objectIds)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in objectIds)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id))
            {
                continue;
            }

            if (Document.Find(id) is not null)
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static bool SameIds(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRect(CRect a, CRect b) =>
        Math.Abs(a.X - b.X) < 1e-9 &&
        Math.Abs(a.Y - b.Y) < 1e-9 &&
        Math.Abs(a.Width - b.Width) < 1e-9 &&
        Math.Abs(a.Height - b.Height) < 1e-9;

    private static bool IsFinite(CPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsUsableRegion(CRect region) =>
        double.IsFinite(region.X) &&
        double.IsFinite(region.Y) &&
        double.IsFinite(region.Width) &&
        double.IsFinite(region.Height) &&
        region.Width >= 0 &&
        region.Height >= 0;

    private void RaiseChanged(CanvasChangedEventArgs args)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<CanvasChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                Log.Warn("A canvas Changed handler threw an exception.", ex);
            }
        }
    }

    private void RaiseHistoryChanged()
    {
        var handlers = HistoryChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn("A canvas HistoryChanged handler threw an exception.", ex);
            }
        }
    }
}
