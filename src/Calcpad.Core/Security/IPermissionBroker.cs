namespace Calcpad.Core.Security;

/// <summary>
/// Central authorization authority. The host implements it; plugins only ever consume it.
/// Grants are persisted so the user is asked once per plugin and capability, and can be
/// revoked at any time from the plugin manager.
/// </summary>
public interface IPermissionBroker
{
    /// <summary>Current state without prompting.</summary>
    PermissionState GetState(string pluginId, PermissionKind kind);

    /// <summary>Raised whenever a grant is created, denied or revoked.</summary>
    event EventHandler<PermissionChangedEventArgs>? Changed;

    /// <summary>
    /// Asks the user. Returns the decision. Callers must not assume the request was granted.
    /// </summary>
    ValueTask<PermissionDecision> RequestAsync(PermissionRequest request, CancellationToken ct = default);

    /// <summary>Throws <see cref="PermissionDeniedException"/> unless the grant exists.</summary>
    void Demand(string pluginId, PermissionKind kind, string api);

    bool IsGranted(string pluginId, PermissionKind kind);

    void Revoke(string pluginId, PermissionKind kind);

    void RevokeAll(string pluginId);

    IReadOnlyList<PermissionKind> GrantedFor(string pluginId);
}

/// <summary>Result of the user reviewing one outbound payload.</summary>
public sealed record EgressDecision(bool Allowed, string? RedactedText, bool RememberForSession, bool TextOnly = false)
{
    public static EgressDecision Blocked { get; } = new(false, null, false);

    /// <summary>Approved as-is.</summary>
    public static EgressDecision Approve(bool remember = false) => new(true, null, remember);

    /// <summary>Approved but only the text parts may leave the device; images must be dropped.</summary>
    public static EgressDecision ApproveTextOnly(bool remember = false) => new(true, null, remember, true);
}

/// <summary>What exactly would leave the device, prepared for user review before sending.</summary>
public sealed record EgressPreview(
    string Intent,
    string Destination,
    string Model,
    IReadOnlyList<EgressPart> Parts,
    int TotalCharacters,
    int TotalBytes)
{
    public bool IsEmpty => Parts.Count == 0;
}

public enum EgressPartKind
{
    Text,
    Image,
    Stroke
}

public sealed record EgressPart(EgressPartKind Kind, string Label, string? Text, int ByteCount, string? PreviewPngPath = null);

/// <summary>
/// Hard gate in front of every cloud call. No plugin can bypass it: the cloud transports ask
/// the guard for a token, and the guard refuses when the user has not seen and approved the payload.
/// </summary>
public interface IEgressGuard
{
    /// <summary>Global kill switch. When false, no content may be uploaded at all.</summary>
    bool CloudUploadEnabled { get; set; }

    /// <summary>Per-request maximum payload size in bytes.</summary>
    int MaxPayloadBytes { get; set; }

    /// <summary>Shows the payload to the user and records the decision in the audit log.</summary>
    ValueTask<EgressDecision> ReviewAsync(string pluginId, EgressPreview preview, CancellationToken ct = default);

    /// <summary>Recent audit entries, newest first.</summary>
    ValueTask<IReadOnlyList<EgressAuditEntry>> GetAuditAsync(int limit = 100, CancellationToken ct = default);
}

public sealed record EgressAuditEntry(
    DateTimeOffset At,
    string PluginId,
    string Intent,
    string Destination,
    string Model,
    int Characters,
    int Bytes,
    bool Allowed);
