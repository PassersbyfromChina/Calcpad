using Calcpad.Core.Ai;
using Calcpad.Core.Plugins;

namespace Calcpad.Plugin.Handwriting;

/// <summary>How the plugin chooses the recognition engine.</summary>
public enum HandwritingEnginePreference
{
    /// <summary>Built-in stroke recogniser only. Never touches the network.</summary>
    BuiltInOnly,

    /// <summary>Prefer a model the user installed, fall back to the built-in recogniser.</summary>
    PreferLocalModel,

    /// <summary>Use the cloud when a provider is configured and the user approves each upload.</summary>
    AllowCloud
}

/// <summary>User visible settings of the handwriting plugin, stored in the plugin's own partition.</summary>
public sealed record HandwritingSettings
{
    /// <summary>Languages offered to the recogniser.</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["zh-Hans", "en"];

    /// <summary>Which engine the user wants.</summary>
    public HandwritingEnginePreference Engine { get; init; } = HandwritingEnginePreference.PreferLocalModel;

    /// <summary>Confidence at or above which a result is accepted silently.</summary>
    public double HighConfidenceThreshold { get; init; } = 0.85;

    /// <summary>Confidence at or above which a result is accepted but marked for review.</summary>
    public double MediumConfidenceThreshold { get; init; } = 0.60;

    /// <summary>Top two candidates closer than this always require a decision.</summary>
    public double AmbiguityDelta { get; init; } = 0.12;

    /// <summary>Ask before every upload (the default; turning it off still leaves the egress guard in place).</summary>
    public bool ConfirmEveryUpload { get; init; } = true;

    /// <summary>Default action when a result is accepted.</summary>
    public bool KeepInkAfterRecognition { get; init; } = true;

    /// <summary>Write recognised math straight into a MathObject instead of a TextObject.</summary>
    public bool PreferMathObject { get; init; } = true;

    /// <summary>Recognise the visible region only when the whole canvas is requested.</summary>
    public bool VisibleRegionOnly { get; init; } = true;

    /// <summary>Loads the settings from the plugin partition.</summary>
    public static HandwritingSettings Load(IPluginSettings store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var settings = new HandwritingSettings
        {
            Languages = store.Get<string[]>("languages") ?? ["zh-Hans", "en"],
            Engine = store.Get<string>("engine") is { Length: > 0 } engine && Enum.TryParse<HandwritingEnginePreference>(engine, out var parsed)
                ? parsed
                : HandwritingEnginePreference.PreferLocalModel,
            HighConfidenceThreshold = store.Get<double?>("thresholdHigh") ?? 0.85,
            MediumConfidenceThreshold = store.Get<double?>("thresholdMedium") ?? 0.60,
            AmbiguityDelta = store.Get<double?>("ambiguityDelta") ?? 0.12,
            ConfirmEveryUpload = store.Get<bool?>("confirmEveryUpload") ?? true,
            KeepInkAfterRecognition = store.Get<bool?>("keepInk") ?? true,
            PreferMathObject = store.Get<bool?>("preferMathObject") ?? true,
            VisibleRegionOnly = store.Get<bool?>("visibleRegionOnly") ?? true
        };

        return settings with
        {
            HighConfidenceThreshold = Math.Clamp(settings.HighConfidenceThreshold, 0.5, 0.99),
            MediumConfidenceThreshold = Math.Clamp(settings.MediumConfidenceThreshold, 0.2, 0.95),
            AmbiguityDelta = Math.Clamp(settings.AmbiguityDelta, 0.01, 0.5)
        };
    }

    /// <summary>Persists the settings into the plugin partition.</summary>
    public async ValueTask SaveAsync(IPluginSettings store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Set("languages", Languages.ToArray());
        store.Set("engine", Engine.ToString());
        store.Set("thresholdHigh", HighConfidenceThreshold);
        store.Set("thresholdMedium", MediumConfidenceThreshold);
        store.Set("ambiguityDelta", AmbiguityDelta);
        store.Set("confirmEveryUpload", ConfirmEveryUpload);
        store.Set("keepInk", KeepInkAfterRecognition);
        store.Set("preferMathObject", PreferMathObject);
        store.Set("visibleRegionOnly", VisibleRegionOnly);
        await store.SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Builds the recognition request for a scope, honouring the engine preference.</summary>
    public RecognitionRequest ToRequest(
        RecognitionScope scope,
        IReadOnlyList<string>? objectIds = null,
        IReadOnlyList<Calcpad.Core.Canvas.CPoint>? region = null,
        string? syntax = null) =>
        new()
        {
            Scope = scope,
            ObjectIds = objectIds ?? [],
            Region = region,
            Languages = Languages,
            ExpectedSyntax = syntax,
            AllowCloud = Engine == HandwritingEnginePreference.AllowCloud,
            RequireUploadConsent = ConfirmEveryUpload
        };
}
