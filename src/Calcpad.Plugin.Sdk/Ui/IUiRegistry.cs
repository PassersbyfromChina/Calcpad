using Avalonia.Controls;
using Calcpad.Core.Plugins;

namespace Calcpad.Plugin.Sdk.Ui;

/// <summary>
/// What a plugin registers during <c>InitializeAsync</c>. The shell owns the implementations and
/// keeps plugin contributions isolated, so a plugin can only add UI, never restyle the shell.
/// </summary>
public interface IUiRegistry
{
    void RegisterWidget(IWidgetRenderer renderer);

    IWidgetRenderer? FindWidget(string widgetType);

    /// <summary>Registers a side panel body. Panels are hosted by the shell in the placement
    /// declared by the plugin manifest.</summary>
    void RegisterPanel(string panelId, Func<Control> factory);

    /// <summary>Instantiates a previously registered panel, or null when the id is unknown.</summary>
    Control? CreatePanel(string panelId);

    bool HasPanel(string panelId);

    /// <summary>Registers a floating overlay, e.g. the disambiguation picker.</summary>
    void RegisterOverlay(string overlayId, Func<Control> factory);

    /// <summary>Instantiates a previously registered overlay, or null when the id is unknown.</summary>
    Control? CreateOverlay(string overlayId);

    /// <summary>Called by widgets that change their own rendering and want a repaint.</summary>
    void Invalidate();

    PluginTheme Theme { get; }

    event EventHandler? Invalidated;
}

/// <summary>
/// Plugin facing context. The base members come from the UI free core contract; the UI members
/// are added here so headless hosts (tests, CLI) can keep using <see cref="IPluginContext"/>.
/// </summary>
public interface IPluginUiContext : IPluginContext
{
    IUiRegistry Ui { get; }
}
