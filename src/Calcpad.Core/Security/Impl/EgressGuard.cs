using Calcpad.Core.Data;
using Calcpad.Core.Hosting;
using Calcpad.Core.Plugins;

namespace Calcpad.Core.Security.Impl;

/// <summary>
/// Hard gate in front of every cloud call. Content only leaves the device when the user has switched
/// uploads on globally <em>and</em> approved the exact payload for this request.
/// </summary>
/// <remarks>
/// Every decision, including the ones taken without asking the user, is written to the
/// <c>egress_audit</c> table. A failure to write the audit trail never lets a payload through.
/// </remarks>
public sealed class EgressGuard : IEgressGuard
{
    private readonly SqliteHostStore _store;
    private readonly IEgressPrompt _prompt;
    private readonly IPluginLogger _log;
    private readonly object _gate = new();

    /// <summary>Payloads the user approved for the rest of the session, keyed by plugin+destination+model.</summary>
    private readonly HashSet<string> _sessionApprovals = new(StringComparer.Ordinal);

    private bool _cloudUploadEnabled;
    private int _maxPayloadBytes;

    /// <summary>Creates the guard and loads the persisted switch and size limit.</summary>
    /// <param name="store">Host store that owns the audit table and the settings.</param>
    /// <param name="prompt">Shell supplied review dialog.</param>
    /// <param name="log">Logger for diagnostics.</param>
    public EgressGuard(SqliteHostStore store, IEgressPrompt prompt, IPluginLogger log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        try
        {
            _cloudUploadEnabled = store.GetCloudUploadEnabled();
            _maxPayloadBytes = store.GetMaxPayloadBytes();
        }
        catch (Exception ex)
        {
            // Fail closed: no stored setting means uploads stay off.
            _cloudUploadEnabled = false;
            _maxPayloadBytes = SqliteHostStore.DefaultMaxPayloadBytes;
            _log.Error("Could not read the cloud upload settings; uploads stay disabled.", ex);
        }
    }

    /// <inheritdoc />
    public bool CloudUploadEnabled
    {
        get
        {
            lock (_gate)
            {
                return _cloudUploadEnabled;
            }
        }

        set
        {
            lock (_gate)
            {
                _cloudUploadEnabled = value;
                if (!value)
                {
                    _sessionApprovals.Clear();
                }
            }

            try
            {
                _store.SetCloudUploadEnabled(value);
            }
            catch (Exception ex)
            {
                _log.Error("Could not persist the cloud upload switch.", ex);
            }

            _log.Info(value
                ? "Cloud uploads enabled by the user; every payload still needs per request approval."
                : "Cloud uploads disabled; no content may leave the device.");
        }
    }

    /// <inheritdoc />
    public int MaxPayloadBytes
    {
        get
        {
            lock (_gate)
            {
                return _maxPayloadBytes;
            }
        }

        set
        {
            var normalized = value <= 0 ? SqliteHostStore.DefaultMaxPayloadBytes : value;
            lock (_gate)
            {
                _maxPayloadBytes = normalized;
            }

            try
            {
                _store.SetMaxPayloadBytes(normalized);
            }
            catch (Exception ex)
            {
                _log.Error("Could not persist the maximum upload size.", ex);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<EgressDecision> ReviewAsync(string pluginId, EgressPreview preview, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(preview);

        // 1. Global kill switch. Nothing is uploaded, the user is not even asked.
        if (!CloudUploadEnabled)
        {
            return Blocked(pluginId, preview, "cloud uploads are switched off", textOnly: false);
        }

        // 2. Empty payloads are meaningless and are refused without a prompt.
        if (preview.IsEmpty || preview.TotalBytes <= 0)
        {
            return Blocked(pluginId, preview, "the payload is empty", textOnly: false);
        }

        // 3. Size limit is enforced before the user is bothered.
        var limit = MaxPayloadBytes;
        if (preview.TotalBytes > limit)
        {
            return Blocked(pluginId, preview, $"the payload is {preview.TotalBytes} bytes, above the {limit} byte limit", textOnly: false);
        }

        // 4. A payload the user already approved for this session is not shown again.
        var sessionKey = SessionKey(pluginId, preview);
        lock (_gate)
        {
            if (_sessionApprovals.Contains(sessionKey))
            {
                var remembered = new EgressDecision(true, null, true);
                Audit(pluginId, preview, true, remembered.TextOnly);
                _log.Info($"Egress for '{pluginId}' allowed from the session approval of this destination.");
                return remembered;
            }
        }

        // 5. Ask the user.
        EgressDecision decision;
        try
        {
            decision = await _prompt.ReviewAsync(preview, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Audit(pluginId, preview, false, false);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Egress review failed for '{pluginId}'; the payload is blocked.", ex);
            Audit(pluginId, preview, false, false);
            return EgressDecision.Blocked;
        }

        if (!decision.Allowed)
        {
            Audit(pluginId, preview, false, decision.TextOnly);
            _log.Warn($"Egress for '{pluginId}' was refused by the user ({preview.Intent} -> {preview.Destination}).");
            return EgressDecision.Blocked;
        }

        if (decision.RememberForSession)
        {
            lock (_gate)
            {
                _sessionApprovals.Add(sessionKey);
            }
        }

        Audit(pluginId, preview, true, decision.TextOnly);
        _log.Info($"Egress for '{pluginId}' approved ({preview.TotalBytes} bytes, textOnly={decision.TextOnly}).");
        return decision;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EgressAuditEntry>> GetAuditAsync(int limit = 100, CancellationToken ct = default)
    {
        try
        {
            return await _store.GetEgressAuditAsync(limit, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Could not read the egress audit trail.", ex);
            return [];
        }
    }

    /// <summary>Audit entries with the extra text-only flag, newest first.</summary>
    public async ValueTask<IReadOnlyList<EgressAuditDetail>> GetAuditDetailAsync(int limit = 100, CancellationToken ct = default)
    {
        try
        {
            return await _store.GetEgressAuditDetailAsync(limit, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Could not read the egress audit trail.", ex);
            return [];
        }
    }

    /// <summary>Forgets every session approval, e.g. when the window is locked.</summary>
    public void ClearSessionApprovals()
    {
        lock (_gate)
        {
            _sessionApprovals.Clear();
        }
    }

    private static string SessionKey(string pluginId, EgressPreview preview) =>
        $"{pluginId}\u001f{preview.Destination}\u001f{preview.Model}\u001f{preview.Intent}";

    private EgressDecision Blocked(string pluginId, EgressPreview preview, string reason, bool textOnly)
    {
        Audit(pluginId, preview, false, textOnly);
        _log.Warn($"Egress for '{pluginId}' blocked without prompting: {reason}.");
        return EgressDecision.Blocked;
    }

    private void Audit(string pluginId, EgressPreview preview, bool allowed, bool textOnly)
    {
        var entry = new EgressAuditEntry(
            DateTimeOffset.UtcNow,
            pluginId,
            preview.Intent ?? string.Empty,
            preview.Destination ?? string.Empty,
            preview.Model ?? string.Empty,
            preview.TotalCharacters,
            preview.TotalBytes,
            allowed);

        try
        {
            _store.AppendEgressAudit(entry, textOnly);
        }
        catch (Exception ex)
        {
            _log.Error("Could not append to the egress audit trail.", ex);
        }
    }
}
