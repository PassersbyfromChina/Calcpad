using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;

namespace Calcpad.Core.Ai;

/// <summary>Confidence thresholds that drive the tiering rules.</summary>
public sealed record RecognitionThresholds
{
    /// <summary>At or above this confidence the result is used as is.</summary>
    public double High { get; init; } = 0.85;

    /// <summary>At or above this confidence the result is used but marked.</summary>
    public double Medium { get; init; } = 0.60;

    /// <summary>When the best two candidates are this close the user must choose.</summary>
    public double AmbiguityDelta { get; init; } = 0.12;

    /// <summary>Maps a confidence onto a tier.</summary>
    public RecognitionConfidenceTier TierFor(double confidence) => confidence >= High
        ? RecognitionConfidenceTier.High
        : confidence >= Medium
            ? RecognitionConfidenceTier.Medium
            : RecognitionConfidenceTier.Low;
}

/// <summary>
/// Turns raw engine output into a <see cref="RecognitionResult"/>: tiering, the "never guess"
/// ambiguity rule and post correction. Kept separate from the transports so it can be exercised
/// directly with synthetic confidences.
/// </summary>
public sealed class RecognitionPipeline
{
    private readonly IPluginLogger? _logger;

    /// <summary>Creates a pipeline. Every dependency is optional; the built-in recogniser is used then.</summary>
    public RecognitionPipeline(
        RecognitionThresholds? thresholds = null,
        PostCorrector? corrector = null,
        StrokeRecognizer? recognizer = null,
        OnnxRecognitionEngine? onnx = null,
        IPluginLogger? logger = null)
    {
        Thresholds = thresholds ?? new RecognitionThresholds();
        Recognizer = recognizer ?? new StrokeRecognizer();
        Corrector = corrector ?? new PostCorrector();
        Onnx = onnx;
        _logger = logger;
    }

    /// <summary>Tiering thresholds in force.</summary>
    public RecognitionThresholds Thresholds { get; }

    /// <summary>The built-in, always available recogniser.</summary>
    public StrokeRecognizer Recognizer { get; }

    /// <summary>The post corrector (grammar, dictionary, confusion pairs).</summary>
    public PostCorrector Corrector { get; }

    /// <summary>Optional on-device ONNX model, when the user installed one.</summary>
    public OnnxRecognitionEngine? Onnx { get; }

    /// <summary>
    /// Runs on-device recognition only: the ONNX model when one is installed, the built-in
    /// recogniser otherwise (and as a fallback when the model fails). Never touches the network.
    /// </summary>
    public ValueTask<RecognitionResult> RunLocalAsync(
        IReadOnlyList<InkStroke> strokes,
        RecognitionRequest request,
        IReadOnlyList<string>? sourceObjectIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var hint = RecognitionHint.Create(request.ExpectedSyntax, request.Languages);
        string? diagnostics = null;

        if (Onnx is { IsAvailable: true } onnx)
        {
            try
            {
                var onnxResult = onnx.Recognize(strokes, hint);
                if (onnxResult is { Candidates.Count: > 0 } && !string.IsNullOrEmpty(onnxResult.Text))
                {
                    return ValueTask.FromResult(Evaluate(
                        onnxResult.Candidates,
                        RecognitionEngineKind.OnDeviceModel,
                        onnx.Name,
                        request,
                        onnxResult.Diagnostics,
                        usedCloud: false,
                        sourceObjectIds));
                }

                diagnostics = onnx.LastError is { Length: > 0 } error
                    ? $"本地模型未能给出结果（{error}），已使用内置识别器"
                    : "本地模型未能给出结果，已使用内置识别器";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warn("ONNX recognition failed, falling back to the built-in recogniser", ex);
                diagnostics = $"本地模型推理失败（{ex.GetType().Name}: {ex.Message}），已降级为内置识别器";
            }
        }
        else if (Onnx is { IsAvailable: false } unavailable && Onnx.WasRequested)
        {
            diagnostics = unavailable.UnavailableReason ?? "未找到本地模型，已使用内置识别器";
        }
        else if (!string.IsNullOrEmpty(Onnx?.Directory))
        {
            diagnostics = "未找到本地模型，已使用内置识别器";
        }

        var builtIn = Recognizer.Recognize(strokes, hint, ct);
        var candidates = BuildLineCandidates(builtIn);
        var text = builtIn.Text;
        return ValueTask.FromResult(Evaluate(
            candidates,
            RecognitionEngineKind.BuiltIn,
            "builtin-stroke-v1",
            request,
            diagnostics ?? builtIn.Diagnostics,
            usedCloud: false,
            sourceObjectIds,
            rawText: text));
    }

    /// <summary>
    /// Applies the tiering, ambiguity and correction rules to a ranked candidate list. Also used for
    /// cloud results, which is why <paramref name="engine"/> is a parameter.
    /// </summary>
    public RecognitionResult Evaluate(
        IReadOnlyList<RecognitionCandidate> candidates,
        RecognitionEngineKind engine,
        string engineName,
        RecognitionRequest request,
        string? diagnostics = null,
        bool usedCloud = false,
        IReadOnlyList<string>? sourceObjectIds = null,
        string? rawText = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        candidates ??= [];

        if (candidates.Count == 0)
        {
            return new RecognitionResult
            {
                Text = rawText ?? string.Empty,
                Confidence = 0,
                Tier = RecognitionConfidenceTier.None,
                Candidates = [],
                NeedsUserChoice = false,
                Engine = engine,
                EngineName = engineName,
                UsedCloud = usedCloud,
                Diagnostics = diagnostics ?? "没有可用的识别结果",
                SourceObjectIds = sourceObjectIds ?? []
            };
        }

        var ordered = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .OrderByDescending(c => c.Confidence)
            .ToList();

        if (ordered.Count == 0)
        {
            return RecognitionResult.Empty with { Engine = engine, EngineName = engineName, Diagnostics = diagnostics };
        }

        var best = ordered[0];
        var tier = Thresholds.TierFor(best.Confidence);
        var closeCall = ordered.Count > 1 && best.Confidence - ordered[1].Confidence <= Thresholds.AmbiguityDelta;
        var needsChoice = tier == RecognitionConfidenceTier.Low || closeCall;

        var syntax = string.Equals(request.ExpectedSyntax, "math", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(request.ExpectedSyntax, "text", StringComparison.OrdinalIgnoreCase)
            ? request.ExpectedSyntax!.ToLowerInvariant()
            : PostCorrector.DetectSyntax(best.Text, request.ExpectedSyntax);

        var correction = Corrector.Correct(best.Text, syntax, request.Languages);

        var notes = new List<string>();
        if (diagnostics is { Length: > 0 })
        {
            notes.Add(diagnostics);
        }

        if (needsChoice)
        {
            notes.Add(closeCall && tier != RecognitionConfidenceTier.Low
                ? "前两名候选过于接近，已停下来等待用户选择"
                : "置信度过低，已停下来等待用户选择");
        }
        else if (tier == RecognitionConfidenceTier.Medium)
        {
            notes.Add("置信度中等，建议用户复核");
        }

        if (correction.Notes.Count > 0)
        {
            notes.Add($"已纠错 {correction.Notes.Count} 处");
        }

        return new RecognitionResult
        {
            Text = rawText ?? best.Text,
            Confidence = best.Confidence,
            Tier = tier,
            Candidates = ordered,
            NeedsUserChoice = needsChoice,
            Engine = engine,
            EngineName = engineName,
            CorrectedText = string.Equals(correction.Text, best.Text, StringComparison.Ordinal) ? null : correction.Text,
            Corrections = correction.Notes,
            UsedCloud = usedCloud,
            Diagnostics = notes.Count == 0 ? null : string.Join("；", notes),
            SourceObjectIds = sourceObjectIds ?? []
        };
    }

    /// <summary>Builds full text candidates from a per line recognition (multi line ink included).</summary>
    private IReadOnlyList<RecognitionCandidate> BuildLineCandidates(StrokeRecognitionResult result)
    {
        if (result.Lines.Count == 0)
        {
            return result.Candidates;
        }

        if (result.Lines.Count == 1)
        {
            var line = result.Lines[0];
            return line.Candidates.Count > 0
                ? line.Candidates
                : [new RecognitionCandidate(line.Text, line.Confidence, "builtin")];
        }

        var hypotheses = new List<(string Text, double Score)>();
        var indices = new int[result.Lines.Count];
        var limit = 6;
        while (limit-- > 0)
        {
            var parts = new List<string>(result.Lines.Count);
            var score = 1.0;
            var changed = false;
            for (var i = 0; i < result.Lines.Count; i++)
            {
                var lineCandidates = result.Lines[i].Candidates;
                var candidate = lineCandidates.Count > 0
                    ? lineCandidates[Math.Min(indices[i], lineCandidates.Count - 1)]
                    : new RecognitionCandidate(result.Lines[i].Text, result.Lines[i].Confidence, "builtin");
                parts.Add(candidate.Text);
                score *= Math.Max(0.001, candidate.Confidence);
                changed |= indices[i] != 0;
            }

            if (!changed && hypotheses.Count > 0)
            {
                // advance
            }

            hypotheses.Add((string.Join("\n", parts), score));

            var bump = indices.Length - 1;
            while (bump >= 0 && indices[bump] + 1 >= Math.Min(2, Math.Max(1, result.Lines[bump].Candidates.Count)))
            {
                indices[bump] = 0;
                bump--;
            }

            if (bump < 0)
            {
                break;
            }

            indices[bump]++;
        }

        return hypotheses
            .GroupBy(h => h.Text, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Select(h => new RecognitionCandidate(h.Text, h.Score, "builtin", "multi-line"))
            .ToList();
    }
}
