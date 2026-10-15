using Calcpad.Core.Data;
using Calcpad.Core.Hosting;
using Calcpad.Core.Plugins;

namespace Calcpad.Core.Security.Impl;

/// <summary>
/// Default authorization authority. Decisions are persisted in the <c>plugin_grants</c> table so the
/// user is asked once per plugin and capability, while "just this once" answers stay in memory for
/// the lifetime of the process.
/// </summary>
/// <remarks>
/// All state transitions are serialised through one gate and every read path is lock protected,
/// because plugins routinely call host APIs from their own background threads.
/// </remarks>
public sealed class PermissionBroker : IPermissionBroker
{
    private readonly SqliteHostStore _store;
    private readonly IPermissionPrompt _prompt;
    private readonly IPluginLogger _log;

    private readonly object _gate = new();

    /// <summary>Persisted decisions, loaded from the database on first use per plugin.</summary>
    private readonly Dictionary<string, Dictionary<PermissionKind, bool>> _persisted = new(StringComparer.Ordinal);

    /// <summary>Plugins whose persisted decisions have already been loaded.</summary>
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    /// <summary>Grants and denials that apply to this session only.</summary>
    private readonly Dictionary<string, Dictionary<PermissionKind, bool>> _session = new(StringComparer.Ordinal);

    /// <summary>Capabilities the user explicitly revoked, reported as <see cref="PermissionState.Revoked"/>.</summary>
    private readonly Dictionary<string, HashSet<PermissionKind>> _revoked = new(StringComparer.Ordinal);

    /// <summary>Creates the broker.</summary>
    /// <param name="store">Host store that owns the grant table.</param>
    /// <param name="prompt">Shell supplied dialog used to ask the user.</param>
    /// <param name="log">Logger for diagnostics; never used for secrets.</param>
    public PermissionBroker(SqliteHostStore store, IPermissionPrompt prompt, IPluginLogger log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public event EventHandler<PermissionChangedEventArgs>? Changed;

    /// <inheritdoc />
    public PermissionState GetState(string pluginId, PermissionKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_gate)
        {
            if (_revoked.TryGetValue(pluginId, out var revokedKinds) && revokedKinds.Contains(kind))
            {
                return PermissionState.Revoked;
            }

            var granted = LookupLocked(pluginId, kind);
            if (granted is { } value)
            {
                return value ? PermissionState.Granted : PermissionState.Denied;
            }

            return PermissionState.Unknown;
        }
    }

    /// <inheritdoc />
    public bool IsGranted(string pluginId, PermissionKind kind) => GetState(pluginId, kind) == PermissionState.Granted;

    /// <inheritdoc />
    public async ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PluginId);

        var kinds = request.Kinds?.Distinct().ToArray() ?? [];
        if (kinds.Length == 0)
        {
            return PermissionDecision.Denied;
        }

        // Report the current state to the shell so the dialog can show "already granted" rows.
        var current = new Dictionary<PermissionKind, PermissionState>(kinds.Length);
        foreach (var kind in kinds)
        {
            current[kind] = GetState(request.PluginId, kind);
        }

        PermissionDecision decision;
        try
        {
            decision = await _prompt.AskAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Permission prompt failed for '{request.PluginId}'; treating the request as denied.", ex);
            decision = PermissionDecision.Denied;
        }

        var grantedKinds = ResolveGrantedKinds(request, decision, kinds);
        var persist = request.Remember && decision.Remembered;

        List<PermissionChangedEventArgs> events;
        lock (_gate)
        {
            EnsureLoadedLocked(request.PluginId);

            var session = GetOrAdd(_session, request.PluginId);
            var persisted = GetOrAdd(_persisted, request.PluginId);
            _revoked.Remove(request.PluginId);
            events = new List<PermissionChangedEventArgs>(kinds.Length);

            var toPersist = new List<KeyValuePair<PermissionKind, bool>>();
            foreach (var kind in kinds)
            {
                var granted = grantedKinds.Contains(kind);
                if (persist)
                {
                    // A persisted answer always wins over an earlier "this session only" answer.
                    session.Remove(kind);
                    persisted[kind] = granted;
                    toPersist.Add(new KeyValuePair<PermissionKind, bool>(kind, granted));
                }
                else
                {
                    session[kind] = granted;
                }

                events.Add(new PermissionChangedEventArgs(
                    request.PluginId,
                    kind,
                    granted ? PermissionState.Granted : PermissionState.Denied));
            }

            if (toPersist.Count > 0)
            {
                try
                {
                    _store.SetGrants(request.PluginId, toPersist);
                }
                catch (Exception ex)
                {
                    _log.Error($"Could not persist permission decision for '{request.PluginId}'.", ex);
                }
            }
        }

        foreach (var args in events)
        {
            Raise(args);
        }

        var result = grantedKinds.Count == 0
            ? PermissionDecision.Denied
            : new PermissionDecision(true, persist, grantedKinds);

        _log.Info(
            $"Permission request for '{request.PluginId}' ({string.Join(", ", kinds)}): " +
            $"granted [{string.Join(", ", grantedKinds)}], persisted={persist}");

        return result;
    }

    /// <inheritdoc />
    public void Demand(string pluginId, PermissionKind kind, string api)
    {
        if (!IsGranted(pluginId, kind))
        {
            throw new PermissionDeniedException(pluginId, kind, api);
        }
    }

    /// <inheritdoc />
    public void Revoke(string pluginId, PermissionKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_gate)
        {
            EnsureLoadedLocked(pluginId);
            GetOrAdd(_session, pluginId).Remove(kind);
            GetOrAdd(_persisted, pluginId)[kind] = false;
            GetOrAdd(_revoked, pluginId).Add(kind);

            try
            {
                _store.SetGrant(pluginId, kind, false);
            }
            catch (Exception ex)
            {
                _log.Error($"Could not persist revocation of {kind} for '{pluginId}'.", ex);
            }
        }

        Raise(new PermissionChangedEventArgs(pluginId, kind, PermissionState.Revoked));
        _log.Info($"Revoked {kind} for plugin '{pluginId}'.");
    }

    /// <inheritdoc />
    public void RevokeAll(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        List<PermissionKind> affected;
        lock (_gate)
        {
            EnsureLoadedLocked(pluginId);

            var persisted = GetOrAdd(_persisted, pluginId);
            affected = persisted.Where(p => p.Value).Select(p => p.Key)
                .Concat(_session.TryGetValue(pluginId, out var session)
                    ? session.Where(p => p.Value).Select(p => p.Key)
                    : Enumerable.Empty<PermissionKind>())
                .Distinct()
                .ToList();

            _session.Remove(pluginId);
            var revoked = GetOrAdd(_revoked, pluginId);
            foreach (var kind in affected)
            {
                persisted[kind] = false;
                revoked.Add(kind);
            }

            if (affected.Count > 0)
            {
                try
                {
                    _store.SetGrants(pluginId, affected.Select(k => new KeyValuePair<PermissionKind, bool>(k, false)));
                }
                catch (Exception ex)
                {
                    _log.Error($"Could not persist revocation for '{pluginId}'.", ex);
                }
            }
        }

        foreach (var kind in affected)
        {
            Raise(new PermissionChangedEventArgs(pluginId, kind, PermissionState.Revoked));
        }

        _log.Info($"Revoked {affected.Count} permission(s) for plugin '{pluginId}'.");
    }

    /// <inheritdoc />
    public IReadOnlyList<PermissionKind> GrantedFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_gate)
        {
            EnsureLoadedLocked(pluginId);
            var result = new HashSet<PermissionKind>();

            if (_persisted.TryGetValue(pluginId, out var persisted))
            {
                foreach (var (kind, granted) in persisted.Where(p => p.Value))
                {
                    result.Add(kind);
                }
            }

            if (_session.TryGetValue(pluginId, out var session))
            {
                foreach (var (kind, granted) in session)
                {
                    if (granted)
                    {
                        result.Add(kind);
                    }
                    else
                    {
                        result.Remove(kind);
                    }
                }
            }

            if (_revoked.TryGetValue(pluginId, out var revoked))
            {
                foreach (var kind in revoked)
                {
                    result.Remove(kind);
                }
            }

            return result.ToList();
        }
    }

    /// <summary>Forgets every decision for a plugin, so the user is asked again from scratch.</summary>
    public void Reset(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_gate)
        {
            _session.Remove(pluginId);
            _revoked.Remove(pluginId);
            _persisted.Remove(pluginId);
            _loaded.Remove(pluginId);
            try
            {
                _store.DeleteGrants(pluginId);
            }
            catch (Exception ex)
            {
                _log.Error($"Could not clear stored grants for '{pluginId}'.", ex);
            }
        }
    }

    /// <summary>Drops in-memory state so the next read comes from the database.</summary>
    public void InvalidateCache(string? pluginId = null)
    {
        lock (_gate)
        {
            if (pluginId is null)
            {
                _persisted.Clear();
                _loaded.Clear();
                return;
            }

            _persisted.Remove(pluginId);
            _loaded.Remove(pluginId);
        }
    }

    private static List<PermissionKind> ResolveGrantedKinds(
        PermissionRequest request,
        PermissionDecision decision,
        IReadOnlyList<PermissionKind> kinds)
    {
        if (!decision.Granted && decision.GrantedKinds.Count == 0)
        {
            return [];
        }

        if (decision.Granted && decision.GrantedKinds.Count == 0)
        {
            return kinds.ToList();
        }

        var allowed = decision.GrantedKinds.ToHashSet();
        return kinds.Where(allowed.Contains).ToList();
    }

    private bool? LookupLocked(string pluginId, PermissionKind kind)
    {
        if (_session.TryGetValue(pluginId, out var session) && session.TryGetValue(kind, out var sessionValue))
        {
            return sessionValue;
        }

        EnsureLoadedLocked(pluginId);
        if (_persisted.TryGetValue(pluginId, out var persisted) && persisted.TryGetValue(kind, out var value))
        {
            return _revoked.TryGetValue(pluginId, out var revoked) && revoked.Contains(kind) ? false : value;
        }

        return null;
    }

    private void EnsureLoadedLocked(string pluginId)
    {
        if (!_loaded.Add(pluginId))
        {
            return;
        }

        var dict = GetOrAdd(_persisted, pluginId);
        try
        {
            foreach (var grant in _store.GetGrants(pluginId))
            {
                dict[grant.Kind] = grant.Granted;
            }
        }
        catch (Exception ex)
        {
            _loaded.Remove(pluginId);
            _log.Error($"Could not read stored grants for '{pluginId}'; treating every capability as not granted.", ex);
        }
    }

    private void Raise(PermissionChangedEventArgs args)
    {
        try
        {
            Changed?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _log.Error($"A permission change handler threw for '{args.PluginId}' / {args.Kind}.", ex);
        }
    }

    private static Dictionary<PermissionKind, bool> GetOrAdd(
        Dictionary<string, Dictionary<PermissionKind, bool>> map,
        string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = new Dictionary<PermissionKind, bool>();
            map[key] = value;
        }

        return value;
    }

    private static HashSet<PermissionKind> GetOrAdd(
        Dictionary<string, HashSet<PermissionKind>> map,
        string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = new HashSet<PermissionKind>();
            map[key] = value;
        }

        return value;
    }
}
