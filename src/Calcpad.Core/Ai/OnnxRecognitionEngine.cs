using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;

namespace Calcpad.Core.Ai;

/// <summary>Describes an on-device model file the user installed.</summary>
public sealed record LocalModelFile(string Id, string Name, LocalModelKind Kind, string Path, long SizeBytes, string? LabelsPath);

/// <summary>Finds the model files a user dropped into the models directory.</summary>
public static class LocalModelDiscovery
{
    /// <summary>Scans <paramref name="modelsDirectory"/> (and one level of sub directories) for models.</summary>
    public static IReadOnlyList<LocalModelFile> Scan(string? modelsDirectory)
    {
        var found = new List<LocalModelFile>();
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return found;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(modelsDirectory, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                .Where(p => Depth(modelsDirectory, p) <= 2)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return found;
        }

        foreach (var path in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (Exception)
            {
                continue;
            }

            var isOcr = path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
            var labels = isOcr ? FindLabels(path) : null;
            found.Add(new LocalModelFile(
                Id: Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                Name: Path.GetFileName(path),
                Kind: isOcr ? LocalModelKind.Ocr : LocalModelKind.Llm,
                Path: path,
                SizeBytes: info.Exists ? info.Length : 0,
                LabelsPath: labels));
        }

        return found;
    }

    /// <summary>Maps the discovery result onto the frozen <see cref="LocalModelInfo"/> contract.</summary>
    public static IReadOnlyList<LocalModelInfo> Describe(string? modelsDirectory) =>
        [.. Scan(modelsDirectory).Select(f => new LocalModelInfo(
            f.Id,
            f.Name,
            f.Kind,
            f.Path,
            f.SizeBytes,
            File.Exists(f.Path) && f.SizeBytes > 0,
            f.LabelsPath))];

    private static int Depth(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Count(c => c is '\\' or '/');
    }

    private static string? FindLabels(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(directory, "labels.txt"),
            Path.ChangeExtension(modelPath, ".labels.txt"),
            Path.Combine(directory, Path.GetFileNameWithoutExtension(modelPath) + ".txt")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Optional on-device recogniser backed by a user installed ONNX model.
/// <para>
/// The model is never shipped with Calcpad: the engine scans <c>AppPaths.ModelsDirectory</c>, and when
/// nothing is found it reports why and lets <see cref="StrokeRecognizer"/> do the work. The ONNX
/// session is created on the first inference only and every native failure degrades gracefully, so a
/// platform without the runtime (Android builds that do not bundle it) keeps working.
/// </para>
/// </summary>
public sealed class OnnxRecognitionEngine : IDisposable
{
    private readonly IPluginLogger? _logger;
    private readonly string? _modelPath;
    private readonly string? _labelsPath;
    private readonly object _gate = new();
    private OnnxSessionHolder? _holder;
    private string[]? _labels;
    private bool _disposed;

    private OnnxRecognitionEngine(string? modelsDirectory, LocalModelFile? model, string? unavailableReason, IPluginLogger? logger)
    {
        Directory = modelsDirectory;
        _modelPath = model?.Path;
        _labelsPath = model?.LabelsPath;
        LastError = unavailableReason;
        UnavailableReason = unavailableReason;
        Name = model is null ? "builtin-stroke-v1" : $"onnx:{Path.GetFileNameWithoutExtension(model.Path)}";
        WasRequested = model is not null;
        _logger = logger;
    }

    /// <summary>Creates the engine, or one that reports why it cannot run.</summary>
    public static OnnxRecognitionEngine Create(string? modelsDirectory, IPluginLogger? logger = null)
    {
        try
        {
            var models = LocalModelDiscovery.Scan(modelsDirectory);
            var ocr = models.FirstOrDefault(m => m.Kind == LocalModelKind.Ocr && m.SizeBytes > 0);
            return ocr is null
                ? new OnnxRecognitionEngine(
                    modelsDirectory,
                    null,
                    string.IsNullOrWhiteSpace(modelsDirectory)
                        ? "未配置本地模型目录，已使用内置识别器"
                        : $"未找到本地模型（{modelsDirectory}），已使用内置识别器",
                    logger)
                : new OnnxRecognitionEngine(modelsDirectory, ocr, null, logger);
        }
        catch (Exception ex)
        {
            return new OnnxRecognitionEngine(modelsDirectory, null, $"扫描本地模型失败：{ex.Message}", logger);
        }
    }

    /// <summary>Engine name reported in <see cref="RecognitionResult.EngineName"/>.</summary>
    public string Name { get; }

    /// <summary>Directory that was scanned, for diagnostics.</summary>
    public string? Directory { get; }

    /// <summary>True when a model file was found and has not failed yet.</summary>
    public bool IsAvailable => WasRequested && !_disposed && LastError is null;

    /// <summary>True when a model file was found (even if loading it failed).</summary>
    public bool WasRequested { get; }

    /// <summary>Why the engine cannot be used, when that is the case.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Error of the last failed attempt, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Path of the model in use, if any.</summary>
    public string? ModelPath => _modelPath;

    /// <summary>Runs the model over an ink group. Returns null when the model cannot be used.</summary>
    public StrokeRecognitionResult? Recognize(IReadOnlyList<InkStroke> strokes, RecognitionHint hint)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (!IsAvailable || _modelPath is null || strokes.Count == 0)
        {
            return null;
        }

        try
        {
            if (!EnsureLoaded(out var loadError))
            {
                LastError = loadError;
                return null;
            }

            var clusters = InkGrouping.ClusterCharacters(strokes);
            var lines = InkGrouping.SplitLines(clusters);
            var results = new List<LineRecognition>();
            var allCharacters = new List<CharRecognition>();

            foreach (var line in lines)
            {
                var characters = new List<CharRecognition>();
                foreach (var cluster in line)
                {
                    var recognized = RecognizeCluster(cluster);
                    if (recognized is not null)
                    {
                        characters.Add(recognized);
                    }
                }

                if (characters.Count == 0)
                {
                    continue;
                }

                var text = InkGrouping.JoinLine(
                    characters.Select(c => (c.Text, c.Cluster.Bounds, c.Channel == GlyphChannel.Cjk)).ToList());
                var confidence = characters.Min(c => c.Confidence);
                var candidates = characters
                    .SelectMany(c => c.Candidates)
                    .GroupBy(c => c.Text, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(c => c.Confidence).First())
                    .OrderByDescending(c => c.Confidence)
                    .Take(5)
                    .ToList();
                results.Add(new LineRecognition(text, confidence, characters, candidates));
                allCharacters.AddRange(characters);
            }

            if (results.Count == 0)
            {
                return null;
            }

            var fullText = string.Join("\n", results.Select(r => r.Text));
            var firstLine = results[0];
            var lineCandidates = firstLine.Candidates.Count > 0
                ? firstLine.Candidates
                : [new RecognitionCandidate(firstLine.Text, firstLine.Confidence, Name)];
            return new StrokeRecognitionResult(
                fullText,
                results.Min(r => r.Confidence),
                results,
                allCharacters,
                lineCandidates,
                $"本地模型 {Path.GetFileName(_modelPath)}");
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            _logger?.Warn("ONNX recognition failed", ex);
            return null;
        }
    }

    private bool EnsureLoaded(out string? error)
    {
        error = null;
        if (_holder is { IsLoaded: true })
        {
            return true;
        }

        lock (_gate)
        {
            if (_holder is { IsLoaded: true })
            {
                return true;
            }

            // Session creation happens here, on the first real inference, never in a constructor or
            // static initialiser.
            _holder ??= new OnnxSessionHolder();
            if (!_holder.TryLoad(_modelPath!, out var loadError))
            {
                error = loadError ?? "模型加载失败";
                UnavailableReason = $"本地模型不可用（{error}），已使用内置识别器";
                return false;
            }

            _labels = LoadLabels(_labelsPath, out var labelNote);
            _logger?.Info($"ONNX handwriting model loaded: {_modelPath} ({labelNote})");
            LastError = null;
            return true;
        }
    }

    private CharRecognition? RecognizeCluster(CharCluster cluster)
    {
        var holder = _holder;
        if (holder is null)
        {
            return null;
        }

        var normalized = InkGeometry.NormalizeInk(cluster.Strokes);
        if (normalized.Count == 0)
        {
            return null;
        }

        var (height, width) = holder.InputSize;
        var size = Math.Max(height, width);
        var rendered = InkRaster.Render(normalized, size, Math.Max(1.1, size / 20.0));
        var glyph = Resize(rendered, size, size, height, width);
        var output = holder.Run(glyph, out var runError);
        if (output is null)
        {
            LastError = runError ?? "推理失败";
            return null;
        }

        var probabilities = Softmax(output);
        var labels = _labels ?? [];
        var ranked = probabilities
            .Select((p, i) => (Label: i < labels.Length ? labels[i] : $"class_{i}", Confidence: p))
            .OrderByDescending(x => x.Confidence)
            .Take(5)
            .Select(x => new RecognitionCandidate(x.Label, x.Confidence, Name))
            .ToList();

        if (ranked.Count == 0)
        {
            return null;
        }

        var top = ranked[0];
        return new CharRecognition(top.Text, top.Confidence, ClassifyLabel(top.Text), ranked, cluster, "onnx");
    }

    private static GlyphChannel ClassifyLabel(string label)
    {
        if (label.Length == 0)
        {
            return GlyphChannel.Digit;
        }

        var ch = label[0];
        if (char.IsDigit(ch))
        {
            return GlyphChannel.Digit;
        }

        if (ch is '+' or '-' or '*' or '/' or '=' or '×' or '÷' or '^' or '(' or ')' or '%' or '.' or ',')
        {
            return GlyphChannel.Operator;
        }

        if (ch is >= 'A' and <= 'Z')
        {
            return GlyphChannel.LatinUpper;
        }

        return ch >= 0x4E00 && ch <= 0x9FFF ? GlyphChannel.Cjk : GlyphChannel.LatinLower;
    }

    private static double[] Softmax(float[] logits)
    {
        if (logits.Length == 0)
        {
            return [];
        }

        double max = logits.Max();
        var sum = 0.0;
        var result = new double[logits.Length];
        for (var i = 0; i < logits.Length; i++)
        {
            result[i] = Math.Exp(logits[i] - max);
            sum += result[i];
        }

        if (sum <= 0)
        {
            return result;
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] /= sum;
        }

        return result;
    }

    private static double[] Resize(double[] source, int sourceWidth, int sourceHeight, int targetHeight, int targetWidth)
    {
        var result = new double[targetHeight * targetWidth];
        for (var y = 0; y < targetHeight; y++)
        {
            var sy = Math.Min(sourceHeight - 1, (int)((y + 0.5) * sourceHeight / targetHeight));
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = Math.Min(sourceWidth - 1, (int)((x + 0.5) * sourceWidth / targetWidth));
                result[(y * targetWidth) + x] = source[(sy * sourceWidth) + sx];
            }
        }

        return result;
    }

    private static string[] LoadLabels(string? labelsPath, out string note)
    {
        if (labelsPath is not null && File.Exists(labelsPath))
        {
            try
            {
                var lines = File.ReadAllLines(labelsPath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToArray();
                if (lines.Length > 0)
                {
                    note = $"{lines.Length} labels from {Path.GetFileName(labelsPath)}";
                    return lines;
                }
            }
            catch (Exception)
            {
                note = "labels.txt 读取失败，使用内置默认标签";
                return DefaultLabels();
            }
        }

        note = "未找到 labels.txt，使用内置默认标签（0-9 / A-Z / a-z）";
        return DefaultLabels();
    }

    private static string[] DefaultLabels()
    {
        var labels = new List<string>();
        for (var c = '0'; c <= '9'; c++)
        {
            labels.Add(c.ToString());
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            labels.Add(c.ToString());
        }

        for (var c = 'a'; c <= 'z'; c++)
        {
            labels.Add(c.ToString());
        }

        return [.. labels];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _holder?.Dispose();
            _holder = null;
        }
    }
}
