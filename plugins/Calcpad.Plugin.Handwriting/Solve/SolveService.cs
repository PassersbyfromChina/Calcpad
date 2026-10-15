using System.Text.Json.Nodes;
using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;

namespace Calcpad.Plugin.Handwriting.Solve;

/// <summary>What a probe of the math notes widgets on the canvas found.</summary>
public sealed record NotesProbe(
    bool WidgetExists,
    string? WidgetId,
    bool BoundToInk,
    bool SliderLive,
    int LineCount,
    string? MatchedLine,
    double LastUpdatedTicks);

/// <summary>Evaluation outcome of one expression.</summary>
public sealed record SolveEvaluation(bool Ok, string Expression, string? Value, string? Error);

/// <summary>
/// The iPadOS style solve flow, kept free of UI so it can be driven from the menu, the results panel
/// and the on-canvas solve bar alike.
/// <para>
/// Order of business: recognise (local first, cloud only with per upload consent) → apply the existing
/// confidence tiers (low confidence or a close runner up always asks the user) → hand the expression
/// to Math Notes through the frozen cross plugin commands, and if no notes block appears, render the
/// answer next to the ink itself. The ink is never modified, never covered and never deleted.
/// </para>
/// </summary>
public sealed class SolveService
{
    private readonly ISolveHost _host;
    private readonly IExpressionEngine _math;
    private readonly HandwritingSettings _settings;

    /// <summary>Creates the service.</summary>
    public SolveService(ISolveHost host, HandwritingSettings settings, IExpressionEngine? math = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? new HandwritingSettings();
        _math = math ?? new ExpressionEngine();
    }

    /// <summary>Evaluates an expression with the numerics engine.</summary>
    public SolveEvaluation Evaluate(string? text)
    {
        var expression = SolveText.NormalizeExpression(text);
        if (expression.Length == 0)
        {
            return new SolveEvaluation(false, expression, null, "没有可求解的算式 / nothing to solve");
        }

        try
        {
            var result = _math.Evaluate(expression);
            return result.Ok
                ? new SolveEvaluation(true, expression, result.Value ?? string.Empty, null)
                : new SolveEvaluation(false, expression, null, result.Error ?? "无法求值");
        }
        catch (Exception ex)
        {
            return new SolveEvaluation(false, expression, null, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// Recognises the ink of <paramref name="inkObjectId"/> and, when confidence allows, solves it.
    /// Ambiguity never guesses: the returned state carries the candidates and waits for a choice.
    /// </summary>
    public async ValueTask<SolveState> SolveInkAsync(string inkObjectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inkObjectId);
        var ink = await _host.Canvas.GetAsync(inkObjectId, ct).ConfigureAwait(false) as InkObject;
        if (ink is null)
        {
            return new SolveState
            {
                SourceObjectId = inkObjectId,
                Status = SolveStatus.Failed,
                Message = "该对象不是手写笔迹 / not an ink object"
            };
        }

        if (!ink.AllStrokes.Any())
        {
            return new SolveState
            {
                SourceObjectId = inkObjectId,
                Status = SolveStatus.Failed,
                Message = "笔迹是空的 / the ink object has no strokes"
            };
        }

        var request = _settings.ToRequest(RecognitionScope.Object, [inkObjectId], null, "math");
        var recognition = await _host.Ai.RecognizeAsync(request, ct).ConfigureAwait(false);
        var text = string.IsNullOrWhiteSpace(recognition.CorrectedText) ? recognition.Text : recognition.CorrectedText!;
        var candidates = recognition.Candidates
            .Select(c => new SolveCandidate(c.Text, c.Confidence))
            .Take(5)
            .ToList();

        if (recognition.NeedsUserChoice)
        {
            // Never guess. The bar (or the results panel) shows these and waits.
            return new SolveState
            {
                SourceObjectId = inkObjectId,
                RecognizedText = recognition.Text,
                Expression = text,
                Status = SolveStatus.NeedsChoice,
                Candidates = candidates,
                Numbers = SolveText.ExtractNumbers(text),
                Message = $"置信度 {recognition.Confidence:P0}，请选择正确的算式后再求解"
            };
        }

        var note = recognition.Tier == RecognitionConfidenceTier.Medium
            ? $"置信度中等（{recognition.Confidence:P0}），已采用并标注"
            : $"置信度 {recognition.Confidence:P0}";

        var state = await SolveTextAsync(inkObjectId, text, candidates, ct).ConfigureAwait(false);
        if (state.Message.Length == 0)
        {
            state = state with { Message = note };
        }

        if (recognition.Corrections.Count > 0)
        {
            state = state with { Message = $"{state.Message}；已纠错 {recognition.Corrections.Count} 处" };
        }

        return state;
    }

    /// <summary>Solves an explicitly chosen expression (candidate tap, edit, or a typed line).</summary>
    public async ValueTask<SolveState> SolveTextAsync(
        string inkObjectId,
        string text,
        IReadOnlyList<SolveCandidate>? candidates = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inkObjectId);
        var evaluation = Evaluate(text);
        var numbers = SolveText.ExtractNumbers(evaluation.Expression);

        if (!evaluation.Ok)
        {
            return new SolveState
            {
                SourceObjectId = inkObjectId,
                RecognizedText = text,
                Expression = evaluation.Expression,
                Status = SolveStatus.Failed,
                Candidates = candidates ?? [],
                Numbers = numbers,
                Message = $"无法求解：{evaluation.Error}"
            };
        }

        if (SolveText.IsBareNumber(evaluation.Expression))
        {
            return new SolveState
            {
                SourceObjectId = inkObjectId,
                RecognizedText = text,
                Expression = evaluation.Expression,
                Result = evaluation.Value ?? string.Empty,
                Status = SolveStatus.Failed,
                Numbers = numbers,
                Message = "只识别到一个数字，没有可求解的算式"
            };
        }

        var ink = await _host.Canvas.GetAsync(inkObjectId, ct).ConfigureAwait(false) as InkObject;
        var before = await ProbeAsync(inkObjectId, ct).ConfigureAwait(false);

        var notes = await TryNotesAsync(evaluation, inkObjectId, before, ct).ConfigureAwait(false);
        if (notes is not null)
        {
            var probe = await ProbeAsync(inkObjectId, ct).ConfigureAwait(false);
            var bound = probe.BoundToInk;
            return new SolveState
            {
                SourceObjectId = inkObjectId,
                RecognizedText = text,
                Expression = evaluation.Expression,
                Result = evaluation.Value ?? string.Empty,
                Status = bound ? SolveStatus.SolvedInNotes : SolveStatus.SolvedInNotesUnbound,
                Candidates = candidates ?? [],
                Numbers = numbers,
                NotesWidgetId = probe.WidgetId ?? notes,
                BoundToInk = bound,
                SliderAvailable = probe.SliderLive,
                InkPreserved = true,
                Engine = bound ? "mathnotes-solve" : "mathnotes-append",
                Message = bound
                    ? $"已作为数学笔记行求解，结果行内显示并绑定到这笔迹（{evaluation.Expression} = {evaluation.Value}）"
                    : $"已插入数学笔记并显示结果（{evaluation.Expression} = {evaluation.Value}）；笔迹绑定要等 Math Notes 的 solve 命令就绪"
            };
        }

        // No Math Notes reachable: render the answer ourselves, right next to the ink.
        var local = await CreateLocalResultAsync(ink, evaluation, ct).ConfigureAwait(false);
        return new SolveState
        {
            SourceObjectId = inkObjectId,
            RecognizedText = text,
            Expression = evaluation.Expression,
            Result = evaluation.Value ?? string.Empty,
            Status = SolveStatus.SolvedLocally,
            Candidates = candidates ?? [],
            Numbers = numbers,
            LocalResultObjectId = local,
            BoundToInk = false,
            InkPreserved = true,
            Engine = "local-fallback",
            Message = $"Math Notes 不可用，已在笔迹旁直接显示结果（{evaluation.Expression} = {evaluation.Value}），原始笔迹未改动"
        };
    }

    /// <summary>Deletes what the plugin rendered. The ink object is never touched.</summary>
    public async ValueTask<(bool Deleted, string Message)> DeleteResultAsync(SolveState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LocalResultObjectId is { Length: > 0 } localId)
        {
            var removed = await _host.Canvas.RemoveAsync(localId, ct).ConfigureAwait(false);
            return (removed, removed ? "已删除结果，笔迹保持不变" : "结果对象已不存在");
        }

        if (state.NotesWidgetId is { Length: > 0 } notesId)
        {
            var probe = await ProbeAsync(state.SourceObjectId, ct).ConfigureAwait(false);
            if (probe.LineCount <= 1)
            {
                var confirmed = await _host.Host
                    .ConfirmAsync("删除求解结果", "这会移除刚才创建的整块数学笔记（只包含这一行）。", ct)
                    .ConfigureAwait(false);
                if (!confirmed)
                {
                    return (false, "已取消");
                }

                var removed = await _host.Canvas.RemoveAsync(notesId, ct).ConfigureAwait(false);
                return (removed, removed ? "已删除数学笔记块，笔迹保持不变" : "笔记块已不存在");
            }

            return (false, "结果在包含多行的数学笔记块里，请在 Math Notes 中删除该行（本插件不会替你猜）");
        }

        return (false, "没有可删除的结果");
    }

    /// <summary>
    /// "Tap a number to adjust it": hands the number to the Math Notes slider; when the slider is not
    /// available it edits the number itself and says so.
    /// </summary>
    public async ValueTask<(AdjustOutcome Outcome, SolveState State, string Message)> AdjustNumberAsync(
        SolveState state,
        int index,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var numbers = state.Numbers.Count > 0 ? state.Numbers : SolveText.ExtractNumbers(state.Expression);
        if (numbers.Count == 0)
        {
            return (AdjustOutcome.NothingToAdjust, state, "这行里没有可调的数字");
        }

        var numberIndex = Math.Clamp(index, 0, numbers.Count - 1);
        var current = numbers[numberIndex];

        // 1) Preferred path: the math notes slider (the M1 interaction), exactly as iPad does it.
        //    Probing first keeps the degradation honest instead of pretending a slider opened.
        if (state.Expression.Length > 0 &&
            await _host.Host.HasCommandAsync(MathNotesCommands.Slider, ct).ConfigureAwait(false))
        {
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = (numberIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["text"] = state.Expression
            };
            if (state.SourceObjectId.Length > 0)
            {
                arguments["sourceObjectId"] = state.SourceObjectId;
            }

            if (state.NotesWidgetId is { Length: > 0 } target)
            {
                arguments["targetId"] = target;
            }

            var invoked = await _host.Host
                .ExecuteCommandAsync(MathNotesCommands.Slider, arguments, ct)
                .ConfigureAwait(false);
            var probe = await ProbeAsync(state.SourceObjectId, ct).ConfigureAwait(false);
            if (invoked && probe.SliderLive)
            {
                return (
                    AdjustOutcome.SliderOpened,
                    state with { SliderAvailable = true, Message = $"滑杆已打开：拖动第 {numberIndex + 1} 个数字（当前 {current}）即可实时重算" },
                    $"滑杆已打开，拖动调整「{current}」");
            }
        }

        // 2) Honest degradation: edit the literal here and say the slider is unavailable.
        var typed = await _host.Host
            .PromptAsync("调整数字", $"滑杆尚不可用，直接输入新的数字（当前 {current}）：", current, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(typed))
        {
            return (AdjustOutcome.Cancelled, state, "已取消调整");
        }

        var replacement = typed.Trim();
        if (!double.TryParse(replacement, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return (AdjustOutcome.Cancelled, state, $"「{replacement}」不是数字，未做修改");
        }

        var expression = SolveText.ReplaceNumber(state.Expression, numberIndex, replacement);
        var resolved = await SolveTextAsync(state.SourceObjectId, expression, state.Candidates, ct).ConfigureAwait(false);
        var message = $"滑杆不可用，已改用编辑模式：{current} → {replacement}（{resolved.Expression} = {resolved.Result}）";
        return (AdjustOutcome.EditFallback, resolved with { Message = message }, message);
    }

    /// <summary>Reads the math notes widgets to see whether a line is bound to this ink and whether a slider is live.</summary>
    public async ValueTask<NotesProbe> ProbeAsync(string inkObjectId, CancellationToken ct = default)
    {
        var widgets = await _host.Canvas
            .QueryAsync(new CanvasQuery { Kinds = [ObjectKinds.Widget] }, ct)
            .ConfigureAwait(false);

        var best = new NotesProbe(false, null, false, false, 0, null, 0);
        foreach (var widget in widgets.OfType<WidgetObject>())
        {
            if (!string.Equals(widget.WidgetType, MathNotesCommands.WidgetType, StringComparison.Ordinal))
            {
                continue;
            }

            var state = widget.State;
            var lines = state?["lines"] as JsonArray;
            var lineCount = lines?.Count ?? 0;
            var bound = false;
            string? matched = null;
            if (lines is not null)
            {
                foreach (var line in lines)
                {
                    var source = line?["sourceObjectId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(source) && string.Equals(source, inkObjectId, StringComparison.Ordinal))
                    {
                        bound = true;
                        matched = line?["text"]?.GetValue<string>();
                        break;
                    }
                }
            }

            var slider = state?["slider"] as JsonObject;
            var sliderLive = slider is not null && slider["line"] is not null;
            var updated = state?["updatedAt"]?.GetValue<string>();
            var ticks = DateTimeOffset.TryParse(updated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed.UtcTicks
                : 0;

            var candidate = new NotesProbe(true, widget.Id, bound, sliderLive, lineCount, matched, ticks);
            if (bound || best.WidgetId is null || candidate.LastUpdatedTicks > best.LastUpdatedTicks)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Places the answer next to the ink, styled so it reads as written rather than typed.</summary>
    private async ValueTask<string?> CreateLocalResultAsync(InkObject? ink, SolveEvaluation evaluation, CancellationToken ct)
    {
        if (ink is null)
        {
            return null;
        }

        var (fontFamily, italic, color, fontSize, _) = HandwritingStyle.Describe(ink);
        var text = SolveText.InlineResult(evaluation.Value);
        var width = Math.Max(48, text.Length * fontSize * 0.62);
        var height = Math.Max(fontSize * 1.5, 24);
        var placement = SolveGeometry.PlaceResult(ink.Bounds, new CSize(width, height));

        var result = new TextObject
        {
            Text = text,
            FontFamily = fontFamily,
            Italic = italic,
            Color = color,
            FontSize = fontSize,
            Bounds = placement,
            OwnerPluginId = "calcpad.handwriting"
        };
        result.Tags["handwriting.solve"] = "result";
        result.Tags["handwriting.solve.sourceInk"] = ink.Id;
        result.Tags["handwriting.solve.expression"] = evaluation.Expression;
        result.Tags["handwriting.solve.value"] = evaluation.Value ?? string.Empty;
        result.RecomputeBounds();
        result.Bounds = new CRect(placement.X, placement.Y, Math.Max(placement.Width, result.Bounds.Width), Math.Max(placement.Height, result.Bounds.Height));

        using var edit = await _host.Canvas.BeginEditAsync("手写求解", ct).ConfigureAwait(false);
        var id = await _host.Canvas.AddAsync(result, ct).ConfigureAwait(false);
        await edit.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Optimistic cross plugin call, guarded by an honest capability probe: <c>HasCommandAsync</c>
    /// decides whether Math Notes is even there, then the effect on the canvas decides whether the
    /// call landed. <c>solve</c> is preferred (it binds the line to the ink), <c>append</c> is the
    /// fallback, and when neither lands the caller renders the answer itself.
    /// </summary>
    private async ValueTask<string?> TryNotesAsync(
        SolveEvaluation evaluation,
        string inkObjectId,
        NotesProbe before,
        CancellationToken ct)
    {
        var hasSolve = await _host.Host.HasCommandAsync(MathNotesCommands.Solve, ct).ConfigureAwait(false);
        if (hasSolve)
        {
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = evaluation.Expression,
                ["sourceObjectId"] = inkObjectId,
                ["display"] = "inline"
            };
            if (before.WidgetId is { Length: > 0 } target)
            {
                arguments["targetId"] = target;
            }

            try
            {
                await _host.Host.ExecuteCommandAsync(MathNotesCommands.Solve, arguments, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _host.Log.Warn("calcpad.math.notes.solve failed", ex);
            }

            var afterSolve = await ProbeAsync(inkObjectId, ct).ConfigureAwait(false);
            if (afterSolve.WidgetExists &&
                (afterSolve.BoundToInk || afterSolve.WidgetId != before.WidgetId || afterSolve.LastUpdatedTicks > before.LastUpdatedTicks))
            {
                return afterSolve.WidgetId;
            }
        }

        if (!await _host.Host.HasCommandAsync(MathNotesCommands.Append, ct).ConfigureAwait(false))
        {
            return null;
        }

        // The solve command is missing or did not land: append still yields a normal notes line.
        var appendArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = evaluation.Expression
        };
        try
        {
            await _host.Host.ExecuteCommandAsync(MathNotesCommands.Append, appendArguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Log.Warn("calcpad.math.notes.append failed", ex);
            return null;
        }

        var afterAppend = await ProbeAsync(inkObjectId, ct).ConfigureAwait(false);
        var landed = afterAppend.WidgetExists &&
                     (afterAppend.WidgetId != before.WidgetId ||
                      afterAppend.LineCount > before.LineCount ||
                      afterAppend.LastUpdatedTicks > before.LastUpdatedTicks);
        return landed ? afterAppend.WidgetId : null;
    }
}
