using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>What the user asked to convert.</summary>
public enum RecognitionScope
{
    /// <summary>Only the ink strokes of one object.</summary>
    Object,

    /// <summary>Everything inside a lasso region.</summary>
    Region,

    /// <summary>The whole visible canvas.</summary>
    WholeCanvas,

    /// <summary>Only the current selection.</summary>
    Selection
}

public enum RecognitionEngineKind
{
    None,
    /// <summary>Strokebased recogniser compiled into Calcpad. Always available, never uploads.</summary>
    BuiltIn,

    /// <summary>A model the user installed on this device.</summary>
    OnDeviceModel,

    /// <summary>A remote model, only ever reached through the egress guard.</summary>
    CloudApi
}

public enum RecognitionConfidenceTier
{
    None,
    Low,
    Medium,
    High
}

public sealed record RecognitionRequest
{
    public required RecognitionScope Scope { get; init; }

    /// <summary>Explicit objects to convert.</summary>
    public IReadOnlyList<string> ObjectIds { get; init; } = [];

    /// <summary>Lasso polygon in world coordinates when <see cref="Scope"/> is Region.</summary>
    public IReadOnlyList<CPoint>? Region { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = ["zh-Hans", "en"];

    /// <summary>Hint for the post corrector: <c>math</c>, <c>text</c> or null to auto detect.</summary>
    public string? ExpectedSyntax { get; init; }

    /// <summary>When false the cloud is never contacted, even if it is configured.</summary>
    public bool AllowCloud { get; init; }

    /// <summary>Ask the user before every upload (default). Never silently flipped to false.</summary>
    public bool RequireUploadConsent { get; init; } = true;
}

public sealed record RecognitionCandidate(string Text, double Confidence, string Engine, string? Note = null);

public sealed record CorrectionNote(string Original, string Corrected, string Reason);

public sealed record RecognitionResult
{
    public required string Text { get; init; }

    public double Confidence { get; init; }

    public RecognitionConfidenceTier Tier { get; init; }

    /// <summary>Ranked alternatives. Populated when the top candidates are too close to call.</summary>
    public IReadOnlyList<RecognitionCandidate> Candidates { get; init; } = [];

    /// <summary>True when the user must pick: the engine refuses to guess.</summary>
    public bool NeedsUserChoice { get; init; }

    public RecognitionEngineKind Engine { get; init; }

    public string? EngineName { get; init; }

    /// <summary>Text after grammar / dictionary correction.</summary>
    public string? CorrectedText { get; init; }

    public IReadOnlyList<CorrectionNote> Corrections { get; init; } = [];

    public bool UsedCloud { get; init; }

    public string? Diagnostics { get; init; }

    /// <summary>Objects this result should be written back to.</summary>
    public IReadOnlyList<string> SourceObjectIds { get; init; } = [];

    public static RecognitionResult Empty { get; } = new()
    {
        Text = string.Empty,
        Tier = RecognitionConfidenceTier.None,
        Engine = RecognitionEngineKind.None
    };
}

public sealed record AskRequest
{
    public required string Question { get; init; }

    /// <summary>Text the user chose to include as context.</summary>
    public string? ContextText { get; init; }

    /// <summary>Optional crop the user chose to include as an image.</summary>
    public byte[]? ContextPng { get; init; }

    /// <summary>World region the crop came from, recorded for the audit trail.</summary>
    public CRect? Region { get; init; }

    public bool AllowCloud { get; init; }

    public bool PreferOnDevice { get; init; } = true;

    public string? SystemHint { get; init; }
}

public sealed record AiAnswer(string Text, string Provider, string? Model, bool UsedCloud, bool Refused = false, string? Reason = null)
{
    public static AiAnswer Refusal(string reason) => new(string.Empty, "none", null, false, true, reason);
}

public sealed record AiCapabilities(
    bool OnDeviceRecognition,
    bool OnDeviceChat,
    bool CloudConfigured,
    string? CloudProvider,
    string? CloudModel,
    bool UploadEnabled,
    IReadOnlyList<string> Warnings);

public enum LocalModelKind
{
    /// <summary>Handwriting / text OCR model in ONNX format.</summary>
    Ocr,

    /// <summary>Small language model served locally (e.g. via a local OpenAI compatible endpoint).</summary>
    Llm
}

public sealed record LocalModelInfo(string Id, string Name, LocalModelKind Kind, string Path, long SizeBytes, bool Available, string? LabelsPath = null);

public sealed record AiProviderInfo(string Id, string DisplayName, bool SupportsVision, bool IsLocal, string? Model);

public sealed record AiProviderSettings(
    string ProviderId,
    string? BaseUrl,
    string? ApiKey,
    string? Model,
    bool Enabled,
    int MaxUploadBytes = 1_500_000,
    bool SendImageAllowed = false)
{
    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>
/// Everything AI related that a plugin may use. On-device paths run first; the cloud is only
/// reachable through <see cref="Calcpad.Core.Security.IEgressGuard"/> consent.
/// </summary>
public interface IAiApi
{
    AiCapabilities Capabilities { get; }

    event EventHandler<AiCapabilities>? CapabilitiesChanged;

    ValueTask<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct = default);

    /// <summary>Writes the recognition result into a new or existing object; needs CanvasWrite.</summary>
    ValueTask<string?> ApplyRecognitionAsync(string objectId, RecognitionResult result, bool keepInk, CancellationToken ct = default);

    ValueTask<AiAnswer> AskAsync(AskRequest request, IProgress<string>? streamed = null, CancellationToken ct = default);

    IReadOnlyList<LocalModelInfo> DiscoverLocalModels();

    IAiProviderRegistry Providers { get; }
}

public interface IAiProviderRegistry
{
    IReadOnlyList<AiProviderInfo> Providers { get; }

    AiProviderInfo? Active { get; }

    AiProviderSettings? GetSettings(string providerId);

    ValueTask SaveSettingsAsync(AiProviderSettings settings, CancellationToken ct = default);

    /// <summary>Sends a tiny probe so the user can verify a key without uploading canvas content.</summary>
    ValueTask<(bool Ok, string Message)> TestAsync(string providerId, CancellationToken ct = default);
}
