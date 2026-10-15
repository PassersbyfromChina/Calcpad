namespace Calcpad.Core.Ai;

/// <summary>
/// Minimal persistence port for <see cref="AiProviderSettings"/>.
/// <para>
/// Contract split (agreed with the host team): <see cref="Calcpad.Core.Ai"/> only ever calls this
/// interface. Persisting the record, protecting <see cref="AiProviderSettings.ApiKey"/> at rest
/// (encryption, DPAPI/Keystore, database transaction, migrations) is entirely the responsibility of
/// the implementation supplied by the host. The AI layer keeps the key in memory for the lifetime of
/// a request and never writes it anywhere itself.
/// </para>
/// </summary>
public interface IAiSettingsStore
{
    /// <summary>
    /// Loads the settings previously saved for <paramref name="id"/>, or <see langword="null"/> when the
    /// provider was never configured on this device. Implementations must not throw for a missing row.
    /// </summary>
    ValueTask<AiProviderSettings?> LoadAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Saves (upserts) the settings for <see cref="AiProviderSettings.ProviderId"/>. The implementation
    /// owns encryption of the API key and must be safe to call repeatedly.
    /// </summary>
    ValueTask SaveAsync(AiProviderSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Headless / test implementation of <see cref="IAiSettingsStore"/> that keeps everything in memory.
/// The host replaces it with its database backed store; the AI layer works with either.
/// </summary>
public sealed class InMemoryAiSettingsStore : IAiSettingsStore
{
    private readonly Dictionary<string, AiProviderSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Creates an empty store.</summary>
    public InMemoryAiSettingsStore()
    {
    }

    /// <summary>Creates a store pre-populated with <paramref name="seed"/>.</summary>
    public InMemoryAiSettingsStore(IEnumerable<AiProviderSettings> seed)
    {
        foreach (var s in seed)
        {
            _settings[s.ProviderId] = s;
        }
    }

    /// <inheritdoc />
    public ValueTask<AiProviderSettings?> LoadAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_settings.TryGetValue(id, out var found) ? found : null);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(AiProviderSettings settings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _settings[settings.ProviderId] = settings;
        }

        return ValueTask.CompletedTask;
    }
}
