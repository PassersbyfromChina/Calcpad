using Calcpad.Core.Plugins;

namespace Calcpad.Core.Ai;

/// <summary>
/// Provider catalog plus settings persistence. Anything the host stores on disk goes through
/// <see cref="IAiSettingsStore"/> (which owns encryption of the API key); the registry itself only
/// keeps the settings in memory for the lifetime of the process.
/// </summary>
public sealed class AiProviderRegistry : IAiProviderRegistry
{
    private static readonly (string Id, string Name, string? BaseUrl, string? Model, bool Vision, bool Local)[] Catalog =
    [
        ("local", "本地模型（Ollama / LM Studio / llama.cpp）", "http://127.0.0.1:11434/v1", null, false, true),
        ("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", true, false),
        ("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", false, false),
        ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", null, true, false),
        ("custom", "自定义 OpenAI 兼容服务", null, null, true, false)
    ];

    private readonly IAiSettingsStore _store;
    private readonly IPluginLogger? _logger;
    private readonly bool _preferLocal;
    private readonly HttpClient? _http;
    private readonly Dictionary<string, IAiChatProvider> _transports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AiProviderSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    /// <summary>Creates the registry. A missing store means "settings live in memory only".</summary>
    public AiProviderRegistry(
        IAiSettingsStore? store = null,
        IEnumerable<IAiChatProvider>? transports = null,
        IPluginLogger? logger = null,
        bool preferLocal = true,
        HttpClient? http = null)
    {
        _store = store ?? new InMemoryAiSettingsStore();
        _logger = logger;
        _preferLocal = preferLocal;
        _http = http;
        foreach (var transport in transports ?? DefaultTransports())
        {
            _transports[transport.Id] = transport;
        }
    }

    /// <summary>Raised when settings or the active provider change.</summary>
    public event EventHandler? Changed;

    /// <summary>The transports the registry can dispatch to.</summary>
    public IReadOnlyList<IAiChatProvider> Transports => [.. _transports.Values];

    /// <inheritdoc />
    public IReadOnlyList<AiProviderInfo> Providers =>
        [.. Catalog.Select(c => ToInfo(c.Id, c.Name, c.Vision, c.Local))];

    /// <inheritdoc />
    public AiProviderInfo? Active
    {
        get
        {
            var usable = UsableSettings();
            if (usable.Count == 0)
            {
                return null;
            }

            var pick = usable[0];
            var entry = Catalog.FirstOrDefault(c => string.Equals(c.Id, pick.ProviderId, StringComparison.OrdinalIgnoreCase));
            return ToInfo(pick.ProviderId, entry.Name ?? pick.ProviderId, GuessVision(pick, entry.Local), entry.Local);
        }
    }

    /// <summary>Loads persisted settings once. Safe to call from every entry point.</summary>
    public async ValueTask EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var entry in Catalog)
            {
                try
                {
                    var stored = await _store.LoadAsync(entry.Id, ct).ConfigureAwait(false);
                    _settings[entry.Id] = stored ?? DefaultFor(entry.Id, entry.BaseUrl, entry.Model);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Failed to load AI provider settings for '{entry.Id}'", ex);
                    _settings[entry.Id] = DefaultFor(entry.Id, entry.BaseUrl, entry.Model);
                }
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public AiProviderSettings? GetSettings(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        if (_settings.TryGetValue(providerId, out var settings))
        {
            return settings;
        }

        var entry = Catalog.FirstOrDefault(c => string.Equals(c.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (entry.Id is null)
        {
            return null;
        }

        var created = DefaultFor(entry.Id, entry.BaseUrl, entry.Model);
        _settings[entry.Id] = created;
        return created;
    }

    /// <inheritdoc />
    public async ValueTask SaveSettingsAsync(AiProviderSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings[settings.ProviderId] = settings;
        try
        {
            // Persistence and API key protection belong to the store implementation.
            await _store.SaveAsync(settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to persist AI provider settings for '{settings.ProviderId}'", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask<(bool Ok, string Message)> TestAsync(string providerId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var settings = GetSettings(providerId);
        if (settings is null)
        {
            return (false, $"未知的供应商：{providerId}");
        }

        var transport = FindTransport(providerId);
        if (transport is null)
        {
            return (false, $"供应商 {providerId} 没有可用的传输实现");
        }

        return await transport.TestAsync(settings, ct).ConfigureAwait(false);
    }

    /// <summary>Finds the transport behind a provider id.</summary>
    public IAiChatProvider? FindTransport(string providerId) =>
        _transports.TryGetValue(providerId, out var transport) ? transport : null;

    /// <summary>
    /// Picks the provider to use: a local endpoint first when <paramref name="preferLocal"/> is set,
    /// then a cloud provider. Returns null when nothing usable is configured.
    /// </summary>
    public AiProviderSettings? FindUsable(bool allowLocal, bool allowCloud, bool? preferLocal = null)
    {
        ensureDefaults();
        var preferLocalFirst = preferLocal ?? _preferLocal;
        var usable = UsableSettings()
            .Where(s => IsLocal(s) ? allowLocal : allowCloud)
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        if (!preferLocalFirst)
        {
            return usable[0];
        }

        return usable.FirstOrDefault(IsLocal) ?? usable[0];

        void ensureDefaults()
        {
            foreach (var entry in Catalog)
            {
                _settings.TryAdd(entry.Id, DefaultFor(entry.Id, entry.BaseUrl, entry.Model));
            }
        }
    }

    /// <summary>True when the provider id is the on-device endpoint.</summary>
    public static bool IsLocal(AiProviderSettings settings) =>
        string.Equals(settings.ProviderId, "local", StringComparison.OrdinalIgnoreCase) ||
        IsLoopback(settings.BaseUrl);

    /// <summary>True when a base URL points at this machine: such traffic never leaves the device.</summary>
    public static bool IsLoopback(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               uri.Host is "0.0.0.0" or "::1";
    }

    private IEnumerable<IAiChatProvider> DefaultTransports()
    {
        foreach (var entry in Catalog)
        {
            // The shared HttpClient is injected so that every request goes through one pipeline
            // (proxy settings, tests) instead of a per provider instance nobody can intercept.
            yield return new OpenAiCompatibleProvider(entry.Id, entry.Name, entry.Vision, entry.Local, _http);
        }
    }

    private List<AiProviderSettings> UsableSettings() =>
        [.. _settings.Values.Where(s => s.IsUsable).OrderByDescending(IsLocal)];

    private AiProviderInfo ToInfo(string id, string name, bool vision, bool local)
    {
        var settings = GetSettings(id);
        return new AiProviderInfo(id, name, GuessVision(settings, vision), local, settings?.Model);
    }

    private static bool GuessVision(AiProviderSettings? settings, bool declared)
    {
        if (settings is null)
        {
            return declared;
        }

        var model = settings.Model ?? string.Empty;
        if (model.Length == 0)
        {
            return declared;
        }

        var visionish = model.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("-vl", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                        model.Contains("gemini", StringComparison.OrdinalIgnoreCase);
        return declared || visionish;
    }

    private static AiProviderSettings DefaultFor(string id, string? baseUrl, string? model) =>
        new(id, baseUrl, null, model, Enabled: false);
}
