using Calcpad.Core.Plugins;

namespace Calcpad.Plugin.AskAi;

/// <summary>User visible settings of the ask-AI plugin.</summary>
public sealed record AskAiSettings
{
    /// <summary>Use an on-device endpoint whenever one is configured (no upload review needed).</summary>
    public bool PreferOnDevice { get; init; } = true;

    /// <summary>Include the lasso crop as an image by default (the user can always untick it).</summary>
    public bool IncludeImageByDefault { get; init; }

    /// <summary>Always show the payload preview, even for local models.</summary>
    public bool AlwaysPreview { get; init; } = true;

    /// <summary>Place an answer card next to the question instead of at the canvas centre.</summary>
    public bool PlaceCardNearSource { get; init; } = true;

    /// <summary>Loads the settings from the plugin partition.</summary>
    public static AskAiSettings Load(IPluginSettings store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new AskAiSettings
        {
            PreferOnDevice = store.Get<bool?>("preferOnDevice") ?? true,
            IncludeImageByDefault = store.Get<bool?>("includeImage") ?? false,
            AlwaysPreview = store.Get<bool?>("alwaysPreview") ?? true,
            PlaceCardNearSource = store.Get<bool?>("placeCardNearSource") ?? true
        };
    }

    /// <summary>Persists the settings.</summary>
    public async ValueTask SaveAsync(IPluginSettings store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Set("preferOnDevice", PreferOnDevice);
        store.Set("includeImage", IncludeImageByDefault);
        store.Set("alwaysPreview", AlwaysPreview);
        store.Set("placeCardNearSource", PlaceCardNearSource);
        await store.SaveAsync(ct).ConfigureAwait(false);
    }
}
