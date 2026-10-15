using Calcpad.Core.Ai;

namespace Calcpad.Core.Data;

/// <summary>
/// Database backed <see cref="IAiSettingsStore"/>. Cloud provider configuration, including the API
/// key, is written through <see cref="SqliteHostStore"/>, which encrypts the key before it reaches
/// SQLite (Windows DPAPI, AES-256-GCM elsewhere).
/// </summary>
public sealed class AiSettingsStore : IAiSettingsStore
{
    private readonly SqliteHostStore _store;

    /// <summary>Creates the store over the host tables.</summary>
    /// <param name="store">Host store that owns the <c>ai_provider_settings</c> table.</param>
    public AiSettingsStore(SqliteHostStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>True when the API key of the last loaded provider could not be protected.</summary>
    public bool LastLoadWasDegraded { get; private set; }

    /// <summary>Warning produced by the last load, or null when the key was read normally.</summary>
    public string? LastLoadWarning { get; private set; }

    /// <inheritdoc />
    public ValueTask<AiProviderSettings?> LoadAsync(string id, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var stored = _store.GetAiProviderSettings(id);
            LastLoadWasDegraded = stored is { KeyProtected: false };
            LastLoadWarning = stored?.Warning;
            return ValueTask.FromResult(stored?.Settings);
        }
        catch (Exception ex)
        {
            LastLoadWasDegraded = true;
            LastLoadWarning = ex.Message;
            return ValueTask.FromException<AiProviderSettings?>(ex);
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(AiProviderSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            ct.ThrowIfCancellationRequested();
            var stored = _store.SaveAiProviderSettings(settings);
            LastLoadWasDegraded = !stored.KeyProtected;
            LastLoadWarning = stored.Warning;
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
    }
}
