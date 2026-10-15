using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;

namespace Calcpad.Plugin.Handwriting;

/// <summary>Per object text the user can override before the result is written back.</summary>
public sealed class ObjectWriteback
{
    /// <summary>Object the text belongs to.</summary>
    public required string ObjectId { get; init; }

    /// <summary>Kind of the object, e.g. <c>ink</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Text that will be written into this object.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>True when this object is part of the write back (per item opt out).</summary>
    public bool Selected { get; set; } = true;
}

/// <summary>
/// Holds one recognition pass: the result, the per object write back plan and a real undo snapshot.
/// All canvas access goes through the plugin's own permission checked <see cref="ICanvasApi"/>.
/// </summary>
public sealed class RecognitionSession
{
    private readonly IPluginContext _context;
    private readonly List<CanvasObject> _undoSnapshot = [];
    private readonly Dictionary<string, string?> _previousRecognizedText = [];

    /// <summary>Creates a session; use <see cref="RunAsync"/> to fill it.</summary>
    public RecognitionSession(IPluginContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>The raw result of the last pass, if any.</summary>
    public RecognitionResult? Result { get; private set; }

    /// <summary>Scope that was recognised.</summary>
    public RecognitionScope Scope { get; private set; } = RecognitionScope.Object;

    /// <summary>Objects the result belongs to.</summary>
    public IReadOnlyList<string> TargetObjectIds { get; private set; } = [];

    /// <summary>Per object plan the UI edits and the write back consumes.</summary>
    public List<ObjectWriteback> Writebacks { get; } = [];

    /// <summary>Human readable description of what was processed, shown before the user confirms.</summary>
    public string ScopeSummary { get; private set; } = string.Empty;

    /// <summary>True once a write back happened and can still be undone.</summary>
    public bool CanUndo => _undoSnapshot.Count > 0 || _previousRecognizedText.Count > 0;

    /// <summary>Objects created by the last write back.</summary>
    public IReadOnlyList<string> CreatedObjectIds { get; private set; } = [];

    /// <summary>Runs recognition for a scope and prepares the write back plan.</summary>
    public async ValueTask<RecognitionResult> RunAsync(
        RecognitionScope scope,
        HandwritingSettings settings,
        IReadOnlyList<string>? objectIds = null,
        IReadOnlyList<CPoint>? region = null,
        string? syntax = null,
        CancellationToken ct = default)
    {
        Scope = scope;
        var request = settings.ToRequest(scope, objectIds, region, syntax);
        var result = await _context.Ai.RecognizeAsync(request, ct).ConfigureAwait(false);
        Result = result;
        TargetObjectIds = result.SourceObjectIds.Count > 0
            ? result.SourceObjectIds
            : (scope == RecognitionScope.Selection ? _context.Canvas.Selection.ToList() : objectIds?.ToList() ?? []);

        var text = string.IsNullOrWhiteSpace(result.CorrectedText) ? result.Text : result.CorrectedText!;
        Writebacks.Clear();
        foreach (var id in TargetObjectIds)
        {
            var obj = await _context.Canvas.GetAsync(id, ct).ConfigureAwait(false);
            Writebacks.Add(new ObjectWriteback
            {
                ObjectId = id,
                Kind = obj?.Kind ?? ObjectKinds.Ink,
                Text = text
            });
        }

        ScopeSummary = DescribeScope(scope, TargetObjectIds.Count, region);
        return result;
    }

    /// <summary>Text of the top candidate (after correction), used as the default for every object.</summary>
    public string TopText => Result is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(Result.CorrectedText) ? Result.Text : Result.CorrectedText!;

    /// <summary>Applies the plan to the canvas. Returns the ids of the objects that were written.</summary>
    public async ValueTask<IReadOnlyList<string>> AcceptAsync(HandwritingSettings settings, CancellationToken ct = default)
    {
        var written = new List<string>();
        CreatedObjectIds = [];
        _undoSnapshot.Clear();
        _previousRecognizedText.Clear();

        using var edit = await _context.Canvas.BeginEditAsync("手写转文字", ct).ConfigureAwait(false);

        foreach (var writeback in Writebacks.Where(w => w.Selected))
        {
            ct.ThrowIfCancellationRequested();
            var before = await _context.Canvas.GetAsync(writeback.ObjectId, ct).ConfigureAwait(false);
            if (before is null)
            {
                continue;
            }

            // Undo snapshot: the untouched object, and for ink its previous recognised text.
            _undoSnapshot.Add(before.Clone());
            if (before is InkObject ink)
            {
                _previousRecognizedText[writeback.ObjectId] = ink.RecognizedText;
            }

            var text = string.IsNullOrWhiteSpace(writeback.Text) ? TopText : writeback.Text;
            var result = (Result ?? RecognitionResult.Empty) with { Text = text, CorrectedText = null };
            var newId = await _context.Ai
                .ApplyRecognitionAsync(writeback.ObjectId, result, settings.KeepInkAfterRecognition, ct)
                .ConfigureAwait(false);

            if (newId is { Length: > 0 } && newId != writeback.ObjectId)
            {
                CreatedObjectIds = [.. CreatedObjectIds, newId];
            }
        }

        await edit.CommitAsync(ct).ConfigureAwait(false);
        written.AddRange(_undoSnapshot.Select(o => o.Id));
        return written;
    }

    /// <summary>
    /// Reverts the last write back: created objects are removed and the original objects restored from
    /// the snapshot, so "undo" in the panel behaves like the shell's own undo.
    /// </summary>
    public async ValueTask<bool> UndoAsync(CancellationToken ct = default)
    {
        if (!CanUndo)
        {
            return false;
        }

        using var edit = await _context.Canvas.BeginEditAsync("撤销手写转文字", ct).ConfigureAwait(false);

        foreach (var created in CreatedObjectIds)
        {
            await _context.Canvas.RemoveAsync(created, ct).ConfigureAwait(false);
        }

        foreach (var snapshot in _undoSnapshot)
        {
            var existing = await _context.Canvas.GetAsync(snapshot.Id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                await _context.Canvas.AddAsync(snapshot.Clone(), ct).ConfigureAwait(false);
            }
            else
            {
                var text = _previousRecognizedText.TryGetValue(snapshot.Id, out var previous) ? previous : null;
                await _context.Canvas.UpdateAsync(snapshot.Id, obj =>
                {
                    if (obj is InkObject ink)
                    {
                        ink.RecognizedText = text;
                        ink.RecognitionConfidence = null;
                        ink.Touch();
                    }
                }, ct).ConfigureAwait(false);
            }
        }

        await edit.CommitAsync(ct).ConfigureAwait(false);
        _undoSnapshot.Clear();
        _previousRecognizedText.Clear();
        CreatedObjectIds = [];
        return true;
    }

    private static string DescribeScope(RecognitionScope scope, int objectCount, IReadOnlyList<CPoint>? region)
    {
        var area = region is { Count: > 0 } ? CRect.FromPoints(region) : CRect.Empty;
        return scope switch
        {
            RecognitionScope.WholeCanvas => $"全画布：{objectCount} 个对象（云端只按对象逐个裁剪，绝不整画布上传）",
            RecognitionScope.Region => $"圈选区域：{objectCount} 个对象，" +
                                       (area.IsEmpty ? "区域未知" : $"{area.Width:0}×{area.Height:0} 世界单位"),
            RecognitionScope.Selection => $"当前选中：{objectCount} 个对象",
            _ => $"当前对象：{objectCount} 个"
        };
    }
}
