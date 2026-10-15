using System.Globalization;
using System.Net.Http;
using System.Text;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.Core.Ai;

/// <summary>
/// Per plugin AI facade. It binds one plugin id to that plugin's permission checked
/// <see cref="ICanvasApi"/> and to the host's egress guard, and delegates the heavy lifting to the
/// shared <see cref="AiCore"/>. Every cloud request is reviewed by
/// <see cref="IEgressGuard.ReviewAsync"/> first, and only the content the user approved is sent.
/// </summary>
public sealed class AiService : IAiApi
{
    private readonly AiCore _core;
    private readonly string _pluginId;
    private readonly ICanvasApi _canvas;
    private readonly IEgressGuard _egress;
    private readonly IPluginLogger _logger;

    /// <summary>Creates the facade for one plugin. Called by the composition root, never by a plugin.</summary>
    public AiService(AiCore core, string pluginId, ICanvasApi canvas, IEgressGuard egress, IPluginLogger log)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _pluginId = string.IsNullOrWhiteSpace(pluginId) ? throw new ArgumentException("pluginId is required", nameof(pluginId)) : pluginId;
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _egress = egress ?? throw new ArgumentNullException(nameof(egress));
        _logger = log ?? new NullPluginLogger();
        _core.CapabilitiesChanged += OnCoreCapabilitiesChanged;
    }

    /// <inheritdoc />
    public AiCapabilities Capabilities
    {
        get
        {
            var baseCapabilities = _core.Capabilities;
            var uploadEnabled = _egress.CloudUploadEnabled;
            var warnings = baseCapabilities.Warnings
                .Where(w => !w.StartsWith("云上传已关闭", StringComparison.Ordinal))
                .ToList();
            if (!uploadEnabled)
            {
                warnings.Add("云上传已关闭（IEgressGuard.CloudUploadEnabled=false）");
            }

            return baseCapabilities with { UploadEnabled = uploadEnabled, Warnings = warnings };
        }
    }

    /// <inheritdoc />
    public event EventHandler<AiCapabilities>? CapabilitiesChanged;

    /// <inheritdoc />
    public IAiProviderRegistry Providers => _core.Providers;

    /// <inheritdoc />
    public IReadOnlyList<LocalModelInfo> DiscoverLocalModels() => _core.DiscoverLocalModels();

    /// <inheritdoc />
    public async ValueTask<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _core.InitializeAsync(ct).ConfigureAwait(false);

        var targets = await ResolveTargetsAsync(request, ct).ConfigureAwait(false);
        var objectIds = targets.Select(t => t.ObjectId).ToList();
        var strokes = targets.SelectMany(t => t.Strokes).ToList();

        var local = await _core.Pipeline
            .RunLocalAsync(strokes, request, objectIds, ct)
            .ConfigureAwait(false);

        var diagnostics = new List<string>();
        if (local.Diagnostics is { Length: > 0 } localDiagnostics)
        {
            diagnostics.Add(localDiagnostics);
        }

        var cloudEnabled = request.AllowCloud && _egress.CloudUploadEnabled && targets.Count > 0;
        if (!cloudEnabled)
        {
            if (request.AllowCloud && !_egress.CloudUploadEnabled)
            {
                diagnostics.Add("云上传已被全局关闭，仅使用本地识别");
            }
            else if (request.AllowCloud && targets.Count == 0)
            {
                diagnostics.Add("没有可处理的对象，仅使用本地识别");
            }

            return local with { Diagnostics = Join(diagnostics, local.Diagnostics) };
        }

        var cloudSettings = _core.Registry.FindUsable(allowLocal: false, allowCloud: true);
        if (cloudSettings is null)
        {
            diagnostics.Add("未配置可用的云供应商，仅使用本地识别");
            return local with { Diagnostics = Join(diagnostics, local.Diagnostics) };
        }

        // Whole canvas is never uploaded as one image: each object is cropped and approved separately.
        var crops = targets
            .Where(t => t.Strokes.Count > 0)
            .Take(Math.Max(1, _core.Options.MaxCloudObjectsPerRecognition))
            .ToList();

        if (request.Scope == RecognitionScope.WholeCanvas)
        {
            diagnostics.Add($"整画布模式：按对象裁剪为 {crops.Count} 个区域逐个请求，未上传整画布图片");
        }

        if (targets.Count > crops.Count)
        {
            diagnostics.Add($"云端每次最多处理 {_core.Options.MaxCloudObjectsPerRecognition} 个对象，其余保持本地结果");
        }

        var cloudText = await TryCloudRecognitionAsync(crops, request, cloudSettings, diagnostics, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cloudText))
        {
            return local with { Diagnostics = Join(diagnostics, local.Diagnostics) };
        }

        var candidate = new RecognitionCandidate(cloudText, 0.85, cloudSettings.ProviderId, "cloud:" + (cloudSettings.Model ?? "default"));
        var cloudResult = _core.Pipeline.Evaluate(
            [candidate],
            RecognitionEngineKind.CloudApi,
            cloudSettings.ProviderId,
            request,
            Join(diagnostics, null),
            usedCloud: true,
            objectIds);

        return cloudResult;
    }

    /// <inheritdoc />
    public async ValueTask<string?> ApplyRecognitionAsync(
        string objectId,
        RecognitionResult result,
        bool keepInk,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(result.CorrectedText) ? result.Text : result.CorrectedText!;
        using var edit = await _canvas.BeginEditAsync("手写转文字", ct).ConfigureAwait(false);

        if (keepInk)
        {
            // Permission problems must surface to the UI: the canvas facade throws
            // PermissionDeniedException and it is deliberately not swallowed here.
            var updated = await _canvas.UpdateAsync(objectId, obj =>
            {
                if (obj is InkObject ink)
                {
                    ink.RecognizedText = text;
                    ink.RecognitionConfidence = result.Confidence;
                    ink.RecognitionEngine = result.EngineName;
                    ink.Touch();
                }
                else
                {
                    obj.Tags["ai.recognizedText"] = text;
                    obj.Touch();
                }
            }, ct).ConfigureAwait(false);

            await edit.CommitAsync(ct).ConfigureAwait(false);
            return updated ? objectId : null;
        }

        var source = await _canvas.GetAsync(objectId, ct).ConfigureAwait(false);
        if (source is null)
        {
            await edit.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var isMath = string.Equals(PostCorrector.DetectSyntax(text, null), "math", StringComparison.Ordinal);
        CanvasObject created = isMath
            ? new MathObject
            {
                Expression = text,
                Bounds = new CRect(source.Bounds.X, source.Bounds.Y, Math.Max(140, source.Bounds.Width), 56),
                OwnerPluginId = _pluginId
            }
            : new TextObject
            {
                Text = text,
                FontSize = Math.Clamp(source.Bounds.Height * 0.7, 12, 96),
                Bounds = new CRect(source.Bounds.X, source.Bounds.Y, Math.Max(120, source.Bounds.Width), Math.Max(28, source.Bounds.Height)),
                OwnerPluginId = _pluginId
            };
        created.RecomputeBounds();

        var newId = await _canvas.AddAsync(created, ct).ConfigureAwait(false);
        var removed = await _canvas.RemoveAsync(objectId, ct).ConfigureAwait(false);
        await edit.CommitAsync(ct).ConfigureAwait(false);

        if (!removed)
        {
            _logger.Warn($"Recognition: the source ink object '{objectId}' could not be removed; the new object '{newId}' was still created.");
        }

        return newId;
    }

    /// <inheritdoc />
    public async ValueTask<AiAnswer> AskAsync(AskRequest request, IProgress<string>? streamed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _core.InitializeAsync(ct).ConfigureAwait(false);

        var question = request.Question ?? string.Empty;
        var contextText = request.ContextText;
        var contextPng = request.ContextPng;

        // 1) On-device first. Loopback endpoints never leave the machine, so no upload review is needed.
        if (request.PreferOnDevice)
        {
            var localSettings = _core.Registry.FindUsable(allowLocal: true, allowCloud: false);
            if (localSettings is not null)
            {
                var transport = _core.Registry.FindTransport(localSettings.ProviderId);
                if (transport is not null)
                {
                    var png = localSettings.SendImageAllowed && transport.Info.SupportsVision ? contextPng : null;
                    var messages = BuildMessages(question, contextText, png, request.SystemHint);
                    var local = await SendWithTimeoutAsync(transport, localSettings, messages, streamed, ct).ConfigureAwait(false);
                    if (local.Text.Length > 0)
                    {
                        return local with { UsedCloud = false };
                    }

                    // Local endpoint configured but unreachable: say so, then fall through to the cloud.
                    _logger.Warn($"Local AI endpoint failed: {local.Reason}");
                    if (!request.AllowCloud)
                    {
                        return local with { Reason = $"本地模型不可用：{local.Reason}" };
                    }
                }
            }
        }

        // 2) Cloud path, in this exact order: global switch, request switch, provider, size, consent.
        if (!request.AllowCloud)
        {
            return AiAnswer.Refusal("请求未允许使用云端 AI（AllowCloud=false），且没有可用的本地模型");
        }

        if (!_egress.CloudUploadEnabled)
        {
            return AiAnswer.Refusal("云上传已关闭（IEgressGuard.CloudUploadEnabled=false）");
        }

        var cloudSettings = _core.Registry.FindUsable(allowLocal: false, allowCloud: true);
        if (cloudSettings is null)
        {
            return AiAnswer.Refusal("没有已启用的云服务配置");
        }

        var cloudTransport = _core.Registry.FindTransport(cloudSettings.ProviderId);
        if (cloudTransport is null)
        {
            return AiAnswer.Refusal($"供应商 {cloudSettings.ProviderId} 没有可用的传输实现");
        }

        var includeImage = contextPng is { Length: > 0 } && cloudSettings.SendImageAllowed && cloudTransport.Info.SupportsVision;
        var textBytes = Encoding.UTF8.GetByteCount(question) + (contextText is null ? 0 : Encoding.UTF8.GetByteCount(contextText));
        var imageBytes = includeImage ? contextPng!.Length : 0;
        var totalBytes = textBytes + imageBytes;

        var maxBytes = _egress.MaxPayloadBytes > 0 ? _egress.MaxPayloadBytes : _core.Options.MaxUploadBytes;
        if (totalBytes > maxBytes)
        {
            return AiAnswer.Refusal(
                $"待上传内容 {totalBytes.ToString(CultureInfo.InvariantCulture)} 字节超过上限 {maxBytes.ToString(CultureInfo.InvariantCulture)} 字节，已拒绝上传");
        }

        var preview = BuildPreview(question, contextText, includeImage ? contextPng : null, cloudSettings, request.Region);
        var decision = await _egress.ReviewAsync(_pluginId, preview, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return AiAnswer.Refusal("用户拒绝上传");
        }

        var approvedText = decision.RedactedText ?? contextText;
        if (decision.TextOnly)
        {
            if (string.IsNullOrWhiteSpace(approvedText))
            {
                return AiAnswer.Refusal("用户选择仅发送文字，但没有文字内容可发送");
            }

            includeImage = false;
        }

        var approvedPng = includeImage ? contextPng : null;
        var messagesToSend = BuildMessages(question, approvedText, approvedPng, request.SystemHint);
        var answer = await SendWithTimeoutAsync(cloudTransport, cloudSettings, messagesToSend, streamed, ct).ConfigureAwait(false);
        var note = decision.TextOnly ? "已按你的选择仅发送文字" : null;
        return answer with
        {
            UsedCloud = true,
            Reason = answer.Reason is { Length: > 0 } existing
                ? note is null ? existing : note + "；" + existing
                : note
        };
    }

    private async ValueTask<AiAnswer> SendWithTimeoutAsync(
        IAiChatProvider transport,
        AiProviderSettings settings,
        IReadOnlyList<AiChatMessage> messages,
        IProgress<string>? streamed,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_core.Options.CloudTimeout);
        try
        {
            // Exactly one request. Failures are reported, never retried in the background.
            var answer = await transport.ChatAsync(settings, messages, streamed, timeout.Token).ConfigureAwait(false);
            if (answer.Text.Length == 0 && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return answer with { Reason = $"请求超时（{_core.Options.CloudTimeout.TotalSeconds:0.#} 秒）" };
            }

            return answer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AiAnswer(string.Empty, transport.Info.DisplayName, settings.Model, !transport.Info.IsLocal, false, "请求超时");
        }
        catch (OperationCanceledException)
        {
            return new AiAnswer(string.Empty, transport.Info.DisplayName, settings.Model, !transport.Info.IsLocal, false, "请求已取消");
        }
        catch (Exception ex)
        {
            _logger.Warn("AI request failed", ex);
            return new AiAnswer(string.Empty, transport.Info.DisplayName, settings.Model, !transport.Info.IsLocal, false, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    private static IReadOnlyList<AiChatMessage> BuildMessages(string question, string? context, byte[]? png, string? systemHint)
    {
        var messages = new List<AiChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemHint))
        {
            messages.Add(new AiChatMessage("system", systemHint!));
        }

        var content = string.IsNullOrWhiteSpace(context)
            ? question
            : $"{question}\n\n--- 画布内容 ---\n{context}";
        messages.Add(new AiChatMessage("user", content, png));
        return messages;
    }

    private EgressPreview BuildPreview(string question, string? context, byte[]? png, AiProviderSettings settings, CRect? region)
    {
        var parts = new List<EgressPart>();
        if (!string.IsNullOrWhiteSpace(question))
        {
            parts.Add(new EgressPart(EgressPartKind.Text, "问题", question, Encoding.UTF8.GetByteCount(question)));
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            parts.Add(new EgressPart(EgressPartKind.Text, "画布内容", context, Encoding.UTF8.GetByteCount(context!)));
        }

        if (png is { Length: > 0 })
        {
            parts.Add(new EgressPart(
                EgressPartKind.Image,
                region is { } r ? $"圈选图像 {r.Width:0}x{r.Height:0}" : "圈选图像",
                null,
                png.Length));
        }

        var characters = parts.Where(p => p.Text is not null).Sum(p => p.Text!.Length);
        return new EgressPreview(
            "ask-ai",
            settings.BaseUrl ?? string.Empty,
            settings.Model ?? "default",
            parts,
            characters,
            parts.Sum(p => p.ByteCount));
    }

    private async ValueTask<string?> TryCloudRecognitionAsync(
        IReadOnlyList<RecognitionTarget> crops,
        RecognitionRequest request,
        AiProviderSettings cloudSettings,
        List<string> diagnostics,
        CancellationToken ct)
    {
        var transport = _core.Registry.FindTransport(cloudSettings.ProviderId);
        if (transport is null)
        {
            diagnostics.Add($"供应商 {cloudSettings.ProviderId} 没有可用的传输实现");
            return null;
        }

        var maxBytes = _egress.MaxPayloadBytes > 0 ? _egress.MaxPayloadBytes : _core.Options.MaxUploadBytes;
        var texts = new List<string>();

        foreach (var crop in crops)
        {
            ct.ThrowIfCancellationRequested();
            CanvasRaster? raster;
            try
            {
                raster = await _canvas.RasterizeAsync(crop.Bounds, 2.0, ct).ConfigureAwait(false);
            }
            catch (PermissionDeniedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"区域栅格化失败：{ex.Message}");
                continue;
            }

            if (raster is null || raster.Png.Length == 0)
            {
                continue;
            }

            var hint = crop.LocalText;
            var prompt = "识别图片中的手写内容，只输出识别到的文字或数学表达式，不要解释。";
            var imageBytes = cloudSettings.SendImageAllowed ? raster.Png.Length : 0;
            var textBytes = Encoding.UTF8.GetByteCount(prompt) + (hint is null ? 0 : Encoding.UTF8.GetByteCount(hint));
            if (imageBytes + textBytes > maxBytes)
            {
                diagnostics.Add($"对象 {crop.ObjectId} 的裁剪 {imageBytes + textBytes} 字节超过上限 {maxBytes}，未上传");
                continue;
            }

            var parts = new List<EgressPart>
            {
                new(EgressPartKind.Text, "指令", prompt, Encoding.UTF8.GetByteCount(prompt))
            };
            if (!string.IsNullOrWhiteSpace(hint))
            {
                parts.Add(new EgressPart(EgressPartKind.Text, "本地区域识别结果", hint, Encoding.UTF8.GetByteCount(hint!)));
            }

            if (cloudSettings.SendImageAllowed)
            {
                parts.Add(new EgressPart(
                    EgressPartKind.Image,
                    $"手写裁剪 {raster.WorldRegion.Width:0}x{raster.WorldRegion.Height:0}",
                    null,
                    raster.Png.Length));
            }

            var preview = new EgressPreview(
                "handwriting-recognition",
                cloudSettings.BaseUrl ?? string.Empty,
                cloudSettings.Model ?? "default",
                parts,
                parts.Where(p => p.Text is not null).Sum(p => p.Text!.Length),
                parts.Sum(p => p.ByteCount));

            var decision = await _egress.ReviewAsync(_pluginId, preview, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                diagnostics.Add("用户拒绝上传，已保留本地识别结果");
                return texts.Count == 0 ? null : string.Join("\n", texts);
            }

            var useImage = cloudSettings.SendImageAllowed && !decision.TextOnly;
            if (decision.TextOnly && string.IsNullOrWhiteSpace(hint))
            {
                diagnostics.Add("用户选择仅发送文字，但该区域没有文字内容，已跳过");
                continue;
            }

            var message = new AiChatMessage(
                "user",
                string.IsNullOrWhiteSpace(hint) ? prompt : $"{prompt}\n\n本地初步识别：{hint}",
                useImage ? raster.Png : null);

            var answer = await SendWithTimeoutAsync(transport, cloudSettings, [message], null, ct).ConfigureAwait(false);
            if (answer.Text.Length == 0)
            {
                diagnostics.Add($"云端识别失败：{answer.Reason}");
                continue;
            }

            texts.Add(answer.Text.Trim());
        }

        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    /// <summary>Resolves which objects and strokes a request covers.</summary>
    private async ValueTask<IReadOnlyList<RecognitionTarget>> ResolveTargetsAsync(RecognitionRequest request, CancellationToken ct)
    {
        var results = new List<RecognitionTarget>();
        switch (request.Scope)
        {
            case RecognitionScope.Object:
            {
                var ids = request.ObjectIds.Count > 0 ? request.ObjectIds : _canvas.Selection;
                foreach (var id in ids)
                {
                    var obj = await _canvas.GetAsync(id, ct).ConfigureAwait(false);
                    if (obj is not null)
                    {
                        results.Add(ToTarget(obj));
                    }
                }

                break;
            }

            case RecognitionScope.Selection:
            {
                foreach (var id in _canvas.Selection)
                {
                    var obj = await _canvas.GetAsync(id, ct).ConfigureAwait(false);
                    if (obj is not null)
                    {
                        results.Add(ToTarget(obj));
                    }
                }

                break;
            }

            case RecognitionScope.Region:
            {
                var polygon = request.Region ?? [];
                if (polygon.Count < 3)
                {
                    break;
                }

                var bounds = CRect.FromPoints(polygon);
                var objects = await _canvas.QueryAsync(new CanvasQuery { Region = bounds }, ct).ConfigureAwait(false);
                foreach (var obj in objects)
                {
                    if (ContainsPoint(polygon, obj.Bounds.Center) || obj.Bounds.IntersectsWith(bounds))
                    {
                        results.Add(ToTarget(obj, bounds));
                    }
                }

                break;
            }

            case RecognitionScope.WholeCanvas:
            {
                var objects = await _canvas.QueryAsync(CanvasQuery.Everything, ct).ConfigureAwait(false);
                foreach (var obj in objects)
                {
                    results.Add(ToTarget(obj));
                }

                break;
            }
        }

        return results;
    }

    private static RecognitionTarget ToTarget(CanvasObject obj, CRect? regionOverride = null)
    {
        var strokes = obj is InkObject ink ? ink.AllStrokes.ToList() : [];
        var bounds = regionOverride ?? obj.Bounds;
        var text = obj switch
        {
            InkObject i => i.RecognizedText,
            TextObject t => t.Text,
            MathObject m => string.IsNullOrEmpty(m.Result) ? m.Expression : $"{m.Expression} = {m.Result}",
            _ => null
        };

        return new RecognitionTarget(obj.Id, strokes, bounds, obj.Kind, text);
    }

    private static bool ContainsPoint(IReadOnlyList<CPoint> polygon, CPoint point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > point.Y) != (pj.Y > point.Y) &&
                point.X < ((pj.X - pi.X) * (point.Y - pi.Y) / ((pj.Y - pi.Y) == 0 ? double.Epsilon : pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static string? Join(List<string> diagnostics, string? existing)
    {
        var all = new List<string>(diagnostics);
        if (existing is { Length: > 0 })
        {
            all.Add(existing);
        }

        all = [.. all.Where(d => d.Length > 0).Distinct()];
        return all.Count == 0 ? null : string.Join("；", all);
    }

    private void OnCoreCapabilitiesChanged(object? sender, AiCapabilities capabilities) =>
        CapabilitiesChanged?.Invoke(this, Capabilities);

    private sealed record RecognitionTarget(string ObjectId, IReadOnlyList<InkStroke> Strokes, CRect Bounds, string Kind, string? LocalText);
}

/// <summary>Creates the composition root: one <see cref="AiCore"/> plus one facade per plugin.</summary>
public sealed class AiServiceFactory
{
    private readonly AiCore _core;
    private readonly IEgressGuard _egress;
    private readonly IPluginLogger _logger;
    private readonly Dictionary<string, AiService> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates the factory.</summary>
    public AiServiceFactory(AiCore core, IEgressGuard egress, IPluginLogger log)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _egress = egress ?? throw new ArgumentNullException(nameof(egress));
        _logger = log ?? new NullPluginLogger();
    }

    /// <summary>
    /// Returns the facade for a plugin. The canvas facade passed in is the one the host created for
    /// that plugin id, so every read and write is checked against that plugin's grants.
    /// </summary>
    public IAiApi Create(string pluginId, ICanvasApi canvas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(canvas);
        lock (_gate)
        {
            if (_services.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            var service = new AiService(_core, pluginId, canvas, _egress, _logger);
            _services[pluginId] = service;
            return service;
        }
    }

    /// <summary>The shared core, exposed for diagnostics.</summary>
    public AiCore Core => _core;
}

/// <summary>Logger used when the host does not care about AI diagnostics.</summary>
public sealed class NullPluginLogger : IPluginLogger
{
    /// <inheritdoc />
    public void Debug(string message)
    {
    }

    /// <inheritdoc />
    public void Info(string message)
    {
    }

    /// <inheritdoc />
    public void Warn(string message, Exception? error = null)
    {
    }

    /// <inheritdoc />
    public void Error(string message, Exception? error = null)
    {
    }
}
