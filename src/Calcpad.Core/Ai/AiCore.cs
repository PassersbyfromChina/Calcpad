using System.Net.Http;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;

namespace Calcpad.Core.Ai;

/// <summary>Options of the shared AI core. Supplied once by the composition root.</summary>
public sealed record AiCoreOptions
{
    /// <summary>Directory scanned for user installed ONNX models (<c>AppPaths.ModelsDirectory</c>).</summary>
    public string ModelsDirectory { get; init; } = string.Empty;

    /// <summary>Languages used when a request does not specify any.</summary>
    public IReadOnlyList<string> DefaultLanguages { get; init; } = ["zh-Hans", "en"];

    /// <summary>Confidence at or above which a result is used as is.</summary>
    public double HighConfidenceThreshold { get; init; } = 0.85;

    /// <summary>Confidence at or above which a result is used but marked for review.</summary>
    public double MediumConfidenceThreshold { get; init; } = 0.60;

    /// <summary>Top two candidates this close together always require a user decision.</summary>
    public double AmbiguityDelta { get; init; } = 0.12;

    /// <summary>Default per request upload cap. The egress guard's value wins when it is set.</summary>
    public int MaxUploadBytes { get; init; } = 1_500_000;

    /// <summary>Prefer an on-device endpoint over the cloud when both are configured.</summary>
    public bool PreferOnDevice { get; init; } = true;

    /// <summary>Timeout applied to a single cloud request. No request is ever retried in the background.</summary>
    public TimeSpan CloudTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum number of per object crops sent to the cloud for one recognition pass.</summary>
    public int MaxCloudObjectsPerRecognition { get; init; } = 8;

    /// <summary>Name reported for the built-in recogniser.</summary>
    public string BuiltInRecognizerName { get; init; } = "builtin-stroke-v1";
}

/// <summary>
/// Shared, plugin agnostic AI kernel: recogniser, optional ONNX session, provider registry and HTTP
/// client. It holds no canvas facade and no plugin identity, so it can live for the whole process;
/// per plugin access goes through <see cref="AiService"/>, which binds a plugin id to its own
/// permission checked <c>ICanvasApi</c>.
/// </summary>
public sealed class AiCore : IAsyncDisposable
{
    private readonly AiCoreOptions _options;
    private readonly IPluginLogger? _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly OnnxRecognitionEngine _onnx;
    private readonly AiProviderRegistry _registry;
    private bool _disposed;

    /// <summary>Creates the core. All optional dependencies have safe defaults.</summary>
    public AiCore(
        AiCoreOptions options,
        IAiSettingsStore settings,
        HttpClient? http = null,
        IExpressionEngine? math = null,
        IPluginLogger? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = log;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Math = math;

        _registry = new AiProviderRegistry(settings, transports: null, logger: log, preferLocal: options.PreferOnDevice, http: _http);
        _registry.Changed += (_, _) => CapabilitiesChanged?.Invoke(this, Capabilities);

        _onnx = OnnxRecognitionEngine.Create(options.ModelsDirectory, log);
        Pipeline = new RecognitionPipeline(
            new RecognitionThresholds
            {
                High = options.HighConfidenceThreshold,
                Medium = options.MediumConfidenceThreshold,
                AmbiguityDelta = options.AmbiguityDelta
            },
            corrector: new PostCorrector(engine: math),
            recognizer: new StrokeRecognizer(),
            onnx: _onnx,
            logger: log);
    }

    /// <summary>Options this core was created with.</summary>
    public AiCoreOptions Options => _options;

    /// <summary>The math engine used for correction, when the host injected one.</summary>
    public IExpressionEngine? Math { get; }

    /// <summary>Recognition pipeline (tiering, ambiguity rule, correction).</summary>
    public RecognitionPipeline Pipeline { get; }

    /// <summary>Provider catalog and settings.</summary>
    public IAiProviderRegistry Providers => _registry;

    /// <summary>Registry with its concrete API (transports, initialization).</summary>
    public AiProviderRegistry Registry => _registry;

    /// <summary>Logger handed to <see cref="AiService"/> instances.</summary>
    public IPluginLogger? Logger => _logger;

    /// <summary>HTTP client shared by every cloud transport.</summary>
    public HttpClient Http => _http;

    /// <summary>Raised when the capability set changes, e.g. after settings were saved.</summary>
    public event EventHandler<AiCapabilities>? CapabilitiesChanged;

    /// <summary>
    /// What is actually available right now. <see cref="AiCapabilities.UploadEnabled"/> is reported as
    /// false here because only a plugin facade sees the egress guard; <see cref="AiService.Capabilities"/>
    /// reports the real value.
    /// </summary>
    public AiCapabilities Capabilities
    {
        get
        {
            var providers = _registry.Providers;
            var usable = providers
                .Select(p => _registry.GetSettings(p.Id))
                .Where(s => s is { } setting && setting.IsUsable)
                .Select(s => s!)
                .ToList();
            var local = usable.FirstOrDefault(AiProviderRegistry.IsLocal);
            var cloud = usable.FirstOrDefault(s => !AiProviderRegistry.IsLocal(s));

            return new AiCapabilities(
                OnDeviceRecognition: true,
                OnDeviceChat: local is not null,
                CloudConfigured: cloud is not null,
                CloudProvider: cloud is null ? null : _registry.Providers.FirstOrDefault(p => p.Id == cloud.ProviderId)?.DisplayName,
                CloudModel: cloud?.Model,
                UploadEnabled: false,
                Warnings: BuildWarnings(uploadEnabled: false));
        }
    }

    /// <summary>Models the user installed, with availability and label file path.</summary>
    public IReadOnlyList<LocalModelInfo> DiscoverLocalModels() => LocalModelDiscovery.Describe(_options.ModelsDirectory);

    /// <summary>True when a usable ONNX model was found on this device.</summary>
    public bool HasLocalModel => _onnx.IsAvailable;

    /// <summary>Diagnostic line describing the local model situation.</summary>
    public string LocalModelDiagnostics => _onnx.IsAvailable
        ? $"已加载本地模型：{_onnx.ModelPath}"
        : _onnx.UnavailableReason ?? "未找到本地模型，已使用内置识别器";

    internal OnnxRecognitionEngine Onnx => _onnx;

    /// <summary>
    /// Loads persisted provider settings once. Safe to call from every entry point and from the
    /// composition root at startup, so <see cref="Capabilities"/> is accurate before the first request.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _registry.EnsureInitializedAsync(ct).ConfigureAwait(false);
    }

    internal IReadOnlyList<string> BuildWarnings(bool uploadEnabled)
    {
        var warnings = new List<string>();
        if (!_onnx.IsAvailable)
        {
            warnings.Add(_onnx.UnavailableReason ?? "未找到本地模型，已使用内置识别器");
        }

        if (!Pipeline.Corrector.HasMathEngine)
        {
            warnings.Add("宿主未注入 IExpressionEngine，纠错使用内置语法校验");
        }

        var providers = _registry.Providers
            .Select(p => _registry.GetSettings(p.Id))
            .Where(s => s is { } setting && setting.IsUsable)
            .Select(s => s!)
            .ToList();
        if (providers.Count == 0)
        {
            warnings.Add("未配置任何可用的 AI 供应商（本地或云），仅内置识别器可用");
        }

        var cloud = providers.FirstOrDefault(s => !AiProviderRegistry.IsLocal(s));
        if (cloud is not null && !cloud.SendImageAllowed)
        {
            warnings.Add("云供应商未开启图片上传，只能发送文字");
        }

        if (!uploadEnabled)
        {
            warnings.Add("云上传已关闭（IEgressGuard.CloudUploadEnabled=false）");
        }

        return warnings;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _onnx.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
