using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>Which channels may compete for a given request, derived from the languages and the syntax hint.</summary>
public sealed record RecognitionHint
{
    /// <summary>Digits may be recognised.</summary>
    public bool AllowDigits { get; init; } = true;

    /// <summary>Operators and math punctuation may be recognised.</summary>
    public bool AllowOperators { get; init; } = true;

    /// <summary>Latin capitals may be recognised.</summary>
    public bool AllowLatinUpper { get; init; } = true;

    /// <summary>Latin small letters may be recognised.</summary>
    public bool AllowLatinLower { get; init; } = true;

    /// <summary>CJK characters may be recognised.</summary>
    public bool AllowCjk { get; init; } = true;

    /// <summary><c>math</c>, <c>text</c> or <c>null</c> when the request does not know yet.</summary>
    public string? ExpectedSyntax { get; init; }

    /// <summary>Score multiplier applied to Latin channels in math mode (letters stay possible but lose).</summary>
    public double MathModeLatinPenalty { get; init; } = 0.9;

    /// <summary>Score multiplier applied to the operator channel in text mode.</summary>
    public double TextModeOperatorPenalty { get; init; } = 0.82;

    /// <summary>Builds a hint from the request language tags (BCP-47 prefixes are enough).</summary>
    public static RecognitionHint Create(string? expectedSyntax, IReadOnlyList<string>? languages)
    {
        var allowCjk = true;
        var allowLatin = true;
        if (languages is { Count: > 0 })
        {
            allowCjk = languages.Any(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                                          l.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
            allowLatin = languages.Any(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
                                             l.StartsWith("de", StringComparison.OrdinalIgnoreCase) ||
                                             l.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ||
                                             l.StartsWith("es", StringComparison.OrdinalIgnoreCase));
            if (!allowCjk && !allowLatin)
            {
                allowLatin = true;
            }
        }

        var math = string.Equals(expectedSyntax, "math", StringComparison.OrdinalIgnoreCase);
        var text = string.Equals(expectedSyntax, "text", StringComparison.OrdinalIgnoreCase);
        return new RecognitionHint
        {
            AllowDigits = true,
            AllowOperators = !text,
            AllowLatinUpper = allowLatin,
            AllowLatinLower = allowLatin,
            // A math expression never contains ideographs: keeping the CJK channel out of the way is
            // what stops a busy Chinese template from stealing a digit.
            AllowCjk = !math && allowCjk,
            ExpectedSyntax = expectedSyntax
        };
    }
}

/// <summary>Tuning knobs of the built-in recogniser.</summary>
public sealed record StrokeRecognizerOptions
{
    /// <summary>Points every stroke is resampled to.</summary>
    public int SamplesPerStroke { get; init; } = InkGeometry.DefaultSamplesPerStroke;

    /// <summary>Raster size of the image channel.</summary>
    public int RasterSize { get; init; } = 24;

    /// <summary>Softmax temperature used to turn match scores into confidences.</summary>
    public double SoftmaxTemperature { get; init; } = 0.055;

    /// <summary>Weight of the direction sequence (DTW) channel.</summary>
    public double DtwWeight { get; init; } = 0.40;

    /// <summary>Weight of the rasterised image (chamfer) channel.</summary>
    public double BitmapWeight { get; init; } = 0.30;

    /// <summary>Weight of the ink profile channel, which separates <c>0</c> from <c>3</c> and <c>O</c>.</summary>
    public double ProfileWeight { get; init; } = 0.22;

    /// <summary>Weight of the aggregate feature vector channel.</summary>
    public double FeatureWeight { get; init; } = 0.08;

    /// <summary>Decay of the DTW channel score.</summary>
    public double DtwDecay { get; init; } = 3.2;

    /// <summary>Decay of the image channel score.</summary>
    public double BitmapDecay { get; init; } = 11.0;

    /// <summary>Decay of the profile channel score.</summary>
    public double ProfileDecay { get; init; } = 2.6;

    /// <summary>Decay of the feature vector channel score.</summary>
    public double FeatureDecay { get; init; } = 4.0;

    /// <summary>How many candidates a character and a line report.</summary>
    public int MaxCandidates { get; init; } = 5;

    /// <summary>How many labels survive the cheap feature vector prefilter.</summary>
    public int PrefilterLabels { get; init; } = 44;

    /// <summary>How many labels per channel always survive the prefilter.</summary>
    public int PrefilterPerChannel { get; init; } = 12;

    /// <summary>Labels whose prefllter distance is within this margin of the best also survive.</summary>
    public double PrefilterMargin { get; init; } = 0.25;

    /// <summary>Grouping options used when the recogniser has to find characters itself.</summary>
    public GroupingOptions Grouping { get; init; } = new();
}

/// <summary>One recognised character.</summary>
public sealed record CharRecognition(
    string Text,
    double Confidence,
    GlyphChannel Channel,
    IReadOnlyList<RecognitionCandidate> Candidates,
    CharCluster Cluster,
    string? Note = null);

/// <summary>One recognised text line.</summary>
public sealed record LineRecognition(
    string Text,
    double Confidence,
    IReadOnlyList<CharRecognition> Characters,
    IReadOnlyList<RecognitionCandidate> Candidates);

/// <summary>Everything the built-in recogniser produced for one ink group.</summary>
public sealed record StrokeRecognitionResult(
    string Text,
    double Confidence,
    IReadOnlyList<LineRecognition> Lines,
    IReadOnlyList<CharRecognition> Characters,
    IReadOnlyList<RecognitionCandidate> Candidates,
    string? Diagnostics = null);

/// <summary>
/// The built-in, offline handwriting recogniser. It never touches the network and never returns an
/// empty answer for ink it was given: the worst case is a low confidence candidate list, which the
/// pipeline turns into a "please pick" prompt instead of a guess.
/// <para>
/// Channels: a dynamic time warping match over direction sequences, a tolerant raster
/// (chamfer) match and an aggregate feature vector match. Scores are fused per label and turned into
/// confidences with a softmax, so visually close labels (<c>0</c>/<c>O</c>, <c>1</c>/<c>l</c>) surface
/// as competing candidates rather than being silently collapsed.
/// </para>
/// </summary>
public sealed class StrokeRecognizer
{
    private static readonly string[] ConfusionGroups =
    [
        "0Oo", "1lI|7", "5Ss", "2Zz", "8B", "6b", "9gq", "Cc(", "UuVvWw", "Xx×", "Kk", "Yy",
        "+t", "T7", "Mm", "Nn", "Aa", "Ee", "Rr", "Dd", "Qq", "Gg", "Hh", "Jj", "Ff", "Ll", "Pp"
    ];

    private readonly StrokeRecognizerOptions _options;
    private readonly IReadOnlyList<CompiledGlyphPrototype> _prototypes;
    private readonly Dictionary<string, List<CompiledGlyphPrototype>> _byLabel = new(StringComparer.Ordinal);

    /// <summary>Creates a recogniser over the built-in prototype library.</summary>
    public StrokeRecognizer(StrokeRecognizerOptions? options = null, IReadOnlyList<CompiledGlyphPrototype>? prototypes = null)
    {
        _options = options ?? new StrokeRecognizerOptions();
        _prototypes = prototypes ?? GlyphCatalog.Prototypes;
        foreach (var prototype in _prototypes)
        {
            if (!_byLabel.TryGetValue(prototype.ChannelLabel, out var list))
            {
                list = [];
                _byLabel[prototype.ChannelLabel] = list;
            }

            list.Add(prototype);
        }
    }

    /// <summary>Options this recogniser was built with.</summary>
    public StrokeRecognizerOptions Options => _options;

    /// <summary>Number of labels the library can produce.</summary>
    public int LabelCount => _byLabel.Count;

    /// <summary>Recognises a whole ink group: characters, lines and ranked full text candidates.</summary>
    public StrokeRecognitionResult Recognize(
        IReadOnlyList<InkStroke> strokes,
        RecognitionHint? hint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        hint ??= new RecognitionHint();
        if (strokes.Count == 0)
        {
            return new StrokeRecognitionResult(string.Empty, 0, [], [], [], "没有笔迹");
        }

        var clusters = InkGrouping.ClusterCharacters(strokes, _options.Grouping);
        var lines = InkGrouping.SplitLines(clusters, _options.Grouping);
        var lineResults = new List<LineRecognition>();
        var allCharacters = new List<CharRecognition>();
        var lineCandidates = new List<RecognitionCandidate>();

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            var recognised = new List<CharRecognition>(line.Count);
            foreach (var cluster in line)
            {
                recognised.Add(RecognizeCharacter(cluster, hint));
            }

            var lineResult = BuildLine(recognised);
            lineResults.Add(lineResult);
            allCharacters.AddRange(recognised);
            if (lineCandidates.Count == 0)
            {
                lineCandidates.AddRange(lineResult.Candidates);
            }
        }

        var text = string.Join("\n", lineResults.Select(l => l.Text));
        var confidence = lineResults.Count == 0 ? 0 : lineResults.Min(l => l.Confidence);
        return new StrokeRecognitionResult(text, confidence, lineResults, allCharacters, lineCandidates, null);
    }

    /// <summary>Recognises a single character cluster and returns its ranked candidates.</summary>
    public CharRecognition RecognizeCharacter(CharCluster cluster, RecognitionHint? hint = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        hint ??= new RecognitionHint();
        var ranked = Rank(cluster, hint);
        if (ranked.Count == 0)
        {
            return new CharRecognition("?", 0, GlyphChannel.Digit, [], cluster, "无法匹配任何模板");
        }

        var top = ranked[0];
        var candidates = ranked
            .Take(_options.MaxCandidates)
            .Select(r => new RecognitionCandidate(r.Label, r.Confidence, "builtin", DescribeChannel(r.Channel)))
            .ToList();

        var ambiguous = ranked.Count > 1 && ranked[0].Confidence - ranked[1].Confidence <= 0.12;
        return new CharRecognition(
            top.Label,
            top.Confidence,
            top.Channel,
            candidates,
            cluster,
            ambiguous ? "ambiguous" : DescribeChannel(top.Channel));
    }

    /// <summary>Recognises one already normalised glyph; used for diagnostics and verification.</summary>
    public IReadOnlyList<RecognitionCandidate> RecognizeGlyph(IReadOnlyList<InkStroke> strokes, RecognitionHint? hint = null)
    {
        var clusters = InkGrouping.ClusterCharacters(strokes, _options.Grouping);
        if (clusters.Count == 0)
        {
            return [];
        }

        return Rank(clusters[0], hint ?? new RecognitionHint())
            .Take(_options.MaxCandidates)
            .Select(r => new RecognitionCandidate(r.Label, r.Confidence, "builtin", DescribeChannel(r.Channel)))
            .ToList();
    }

    private static string DescribeChannel(GlyphChannel channel) => channel switch
    {
        GlyphChannel.Digit => "channel:digit",
        GlyphChannel.Operator => "channel:operator",
        GlyphChannel.LatinUpper => "channel:latin-upper",
        GlyphChannel.LatinLower => "channel:latin-lower",
        _ => "channel:cjk"
    };

    private LineRecognition BuildLine(IReadOnlyList<CharRecognition> characters)
    {
        if (characters.Count == 0)
        {
            return new LineRecognition(string.Empty, 0, [], []);
        }

        var baseText = JoinCharacters(characters);
        var baseConfidence = characters.Min(c => c.Confidence);
        var hypotheses = new List<(string Text, double Score, string Note)> { (baseText, baseConfidence, "top-1") };

        // Alternative line readings: the cartesian product of the two best candidates per character,
        // capped so a long line cannot explode combinatorially.
        var alternatives = new List<(string Text, double Score, string Note)>();
        var limit = 12;
        var indices = new int[characters.Count];
        while (limit-- > 0)
        {
            var bump = indices.Length - 1;
            while (bump >= 0 && indices[bump] + 1 >= Math.Min(2, characters[bump].Candidates.Count))
            {
                indices[bump] = 0;
                bump--;
            }

            if (bump < 0)
            {
                break;
            }

            indices[bump]++;
            var text = string.Empty;
            var score = 1.0;
            var changed = false;
            for (var i = 0; i < characters.Count; i++)
            {
                var candidate = characters[i].Candidates[Math.Min(indices[i], characters[i].Candidates.Count - 1)];
                score *= Math.Max(0.001, candidate.Confidence);
                if (indices[i] != 0)
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            text = JoinWithIndices(characters, indices);
            alternatives.Add((text, score, "alternative"));
        }

        hypotheses.AddRange(alternatives
            .Where(a => !string.Equals(a.Text, baseText, StringComparison.Ordinal))
            .OrderByDescending(a => a.Score)
            .Take(_options.MaxCandidates - 1));

        // Explicitly surface the multi character confusions the spec calls out (rn/m, vv/w, cl/d):
        // they are single glyphs to the eye but two glyphs to a template matcher.
        foreach (var hypothesis in hypotheses.ToList())
        {
            foreach (var (from, to) in new[] { ("rn", "m"), ("vv", "w"), ("cl", "d") })
            {
                if (hypothesis.Text.Contains(from, StringComparison.Ordinal))
                {
                    var merged = hypothesis.Text.Replace(from, to, StringComparison.Ordinal);
                    if (!hypotheses.Any(h => string.Equals(h.Text, merged, StringComparison.Ordinal)))
                    {
                        hypotheses.Add((merged, hypothesis.Score * 0.98, "merged-strokes"));
                    }
                }
            }
        }

        var orderedHypotheses = hypotheses.OrderByDescending(h => h.Score).ToList();
        var candidates = orderedHypotheses
            .Take(_options.MaxCandidates)
            .Select(h => new RecognitionCandidate(h.Text, h.Score, "builtin", h.Note))
            .ToList();

        var topText = orderedHypotheses.Count > 0 ? orderedHypotheses[0].Text : baseText;
        var topScore = orderedHypotheses.Count > 0 ? orderedHypotheses[0].Score : baseConfidence;
        return new LineRecognition(topText, topScore, characters, candidates);
    }

    private string JoinCharacters(IReadOnlyList<CharRecognition> characters)
    {
        // A pure math line is written without spaces: "1+2=3" and not "1 + 2 = 3".
        var mathOnly = characters.All(c => c.Channel is GlyphChannel.Digit or GlyphChannel.Operator);
        if (mathOnly)
        {
            return string.Concat(characters.Select(c => c.Text));
        }

        var parts = characters
            .Select(c => (c.Text, c.Cluster.Bounds, c.Channel == GlyphChannel.Cjk))
            .ToList();
        return InkGrouping.JoinLine(parts, _options.Grouping);
    }

    /// <summary>Joins one candidate per character, honouring the no-space rule for math lines.</summary>
    private string JoinWithIndices(IReadOnlyList<CharRecognition> characters, IReadOnlyList<int> indices)
    {
        var texts = new List<string>(characters.Count);
        for (var i = 0; i < characters.Count; i++)
        {
            var candidates = characters[i].Candidates;
            var index = Math.Min(indices[i], Math.Max(0, candidates.Count - 1));
            texts.Add(candidates.Count > 0 ? candidates[index].Text : characters[i].Text);
        }

        var mathOnly = characters.All(c => c.Channel is GlyphChannel.Digit or GlyphChannel.Operator);
        if (mathOnly)
        {
            return string.Concat(texts);
        }

        var parts = new List<(string Text, CRect Bounds, bool IsCjk)>(characters.Count);
        for (var i = 0; i < characters.Count; i++)
        {
            parts.Add((texts[i], characters[i].Cluster.Bounds, characters[i].Channel == GlyphChannel.Cjk));
        }

        return InkGrouping.JoinLine(parts, _options.Grouping);
    }

    private List<(string Label, GlyphChannel Channel, double Score, double Confidence)> Rank(CharCluster cluster, RecognitionHint hint)
    {
        var normalized = InkGeometry.NormalizeInk(cluster.Strokes, _options.SamplesPerStroke);
        if (normalized.Count == 0)
        {
            return [];
        }

        var features = GlyphFeaturesExtractor.Extract(normalized);
        var raster = InkRaster.Render(normalized, _options.RasterSize);
        var profiles = InkRaster.Profiles(raster, _options.RasterSize);

        // Stage 1: cheap prefilter. The aggregate feature vector cannot separate "1" from "|" or "0"
        // from "O", and it ranks a three stroke "+" poorly, so the prefilter uses the ink profile
        // distance instead: it costs the same as one channel of the full match and is far more
        // discriminative. Every channel keeps its own shortlist so a correct label is never dropped.
        var labelDistances = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var prototype in _prototypes)
        {
            if (!IsAllowed(prototype.Channel, hint))
            {
                continue;
            }

            var distance = InkRaster.ProfileDistance(profiles, prototype.Profiles);
            if (!labelDistances.TryGetValue(prototype.ChannelLabel, out var best) || distance < best)
            {
                labelDistances[prototype.ChannelLabel] = distance;
            }
        }

        var orderedLabels = labelDistances.OrderBy(kv => kv.Value).ToList();
        var bestDistance = orderedLabels.Count > 0 ? orderedLabels[0].Value : 0;
        var selected = new List<string>();
        var perChannel = new Dictionary<GlyphChannel, int>();
        foreach (var (key, distance) in orderedLabels)
        {
            if (selected.Count >= _options.PrefilterLabels)
            {
                break;
            }

            if (!_byLabel.TryGetValue(key, out var prototypes) || prototypes.Count == 0)
            {
                continue;
            }

            var channel = prototypes[0].Channel;
            perChannel.TryGetValue(channel, out var seen);
            if (seen < _options.PrefilterPerChannel || distance <= bestDistance + _options.PrefilterMargin)
            {
                selected.Add(key);
                perChannel[channel] = seen + 1;
            }
        }

        // Stage 2: full match (DTW + raster + features) on the surviving labels.
        var scored = new List<(string Label, GlyphChannel Channel, double Score)>();
        foreach (var key in selected)
        {
            if (!_byLabel.TryGetValue(key, out var prototypes))
            {
                continue;
            }

            var label = prototypes[0].Label;
            var channel = prototypes[0].Channel;
            if (!IsAllowed(channel, hint))
            {
                continue;
            }

            var best = 0.0;
            foreach (var prototype in prototypes)
            {
                var dtw = Math.Exp(-_options.DtwDecay * DirectionDtw.MeanCost(features.Directions, prototype.Features.Directions));
                var bitmap = Math.Exp(-_options.BitmapDecay * InkRaster.SymmetricDistance(raster, prototype.Raster, _options.RasterSize));
                var profile = Math.Exp(-_options.ProfileDecay * InkRaster.ProfileDistance(profiles, prototype.Profiles));
                var feature = Math.Exp(-_options.FeatureDecay * GlyphFeaturesExtractor.VectorDistance(features.Vector, prototype.Features.Vector));
                var total = _options.DtwWeight + _options.BitmapWeight + _options.ProfileWeight + _options.FeatureWeight;
                var fused = ((_options.DtwWeight * dtw) +
                             (_options.BitmapWeight * bitmap) +
                             (_options.ProfileWeight * profile) +
                             (_options.FeatureWeight * feature)) / total;
                if (fused > best)
                {
                    best = fused;
                }
            }

            var penalised = best * Penalty(channel, hint);
            scored.Add((label, channel, penalised));
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var ordered = scored
            .GroupBy(s => $"{s.Channel}|{s.Label}", StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(s => s.Score).First())
            .OrderByDescending(s => s.Score)
            .ToList();

        var pool = ordered.Take(16).ToList();
        var exponentials = pool.Select(s => Math.Exp(s.Score / _options.SoftmaxTemperature)).ToList();
        var sum = exponentials.Sum();
        var result = new List<(string Label, GlyphChannel Channel, double Score, double Confidence)>(pool.Count);
        for (var i = 0; i < pool.Count; i++)
        {
            result.Add((pool[i].Label, pool[i].Channel, pool[i].Score, sum <= 0 ? 0 : exponentials[i] / sum));
        }

        EnsureConfusionPeers(result, ordered);
        return result;
    }

    private static void EnsureConfusionPeers(
        List<(string Label, GlyphChannel Channel, double Score, double Confidence)> result,
        IReadOnlyList<(string Label, GlyphChannel Channel, double Score)> all)
    {
        if (result.Count == 0)
        {
            return;
        }

        var top = result[0].Label;
        var group = ConfusionGroups.FirstOrDefault(g => g.Contains(top, StringComparison.Ordinal));
        if (group is null)
        {
            return;
        }

        foreach (var peer in all)
        {
            if (peer.Label.Length != 1 || !group.Contains(peer.Label, StringComparison.Ordinal) ||
                string.Equals(peer.Label, top, StringComparison.Ordinal))
            {
                continue;
            }

            if (result.Any(r => string.Equals(r.Label, peer.Label, StringComparison.Ordinal)))
            {
                continue;
            }

            // Keep the peer visible even when it came from another channel: that is exactly the
            // "0 or O?" decision the user must make.
            result.Add((peer.Label, peer.Channel, peer.Score, Math.Max(0.01, peer.Score * 0.9)));
        }

        result.Sort((a, b) => b.Score.CompareTo(a.Score));
    }

    private static bool IsAllowed(GlyphChannel channel, RecognitionHint hint) => channel switch
    {
        GlyphChannel.Digit => hint.AllowDigits || hint.AllowOperators,
        GlyphChannel.Operator => hint.AllowOperators,
        GlyphChannel.LatinUpper => hint.AllowLatinUpper,
        GlyphChannel.LatinLower => hint.AllowLatinLower,
        _ => hint.AllowCjk
    };

    private static double Penalty(GlyphChannel channel, RecognitionHint hint)
    {
        var math = string.Equals(hint.ExpectedSyntax, "math", StringComparison.OrdinalIgnoreCase);
        var text = string.Equals(hint.ExpectedSyntax, "text", StringComparison.OrdinalIgnoreCase);
        if (math && (channel is GlyphChannel.LatinUpper or GlyphChannel.LatinLower or GlyphChannel.Cjk))
        {
            return hint.MathModeLatinPenalty;
        }

        return text && channel == GlyphChannel.Operator ? hint.TextModeOperatorPenalty : 1.0;
    }
}
