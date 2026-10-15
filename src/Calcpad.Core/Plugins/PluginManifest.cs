using Calcpad.Core.Security;

namespace Calcpad.Core.Plugins;

/// <summary>
/// Declarative description of a plugin. Menus, shortcuts, panels and tools declared here are
/// what the shell renders; the plugin itself only implements behaviour behind command ids.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Reverse-DNS unique id, e.g. <c>calcpad.handwriting</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Version { get; init; } = "0.0.0";

    public string? Description { get; init; }

    public string? Author { get; init; } = "PassersbyfromChina";

    public string? Icon { get; init; }

    /// <summary>Capabilities the plugin may ask for. Everything else is refused at call time.</summary>
    public IReadOnlyList<PermissionKind> Permissions { get; init; } = [];

    public IReadOnlyList<MenuContribution> Menus { get; init; } = [];

    public IReadOnlyList<ShortcutContribution> Shortcuts { get; init; } = [];

    public IReadOnlyList<PanelContribution> Panels { get; init; } = [];

    public IReadOnlyList<ToolContribution> Tools { get; init; } = [];

    public IReadOnlyList<WidgetContribution> Widgets { get; init; } = [];

    /// <summary>Search keywords, also used by the command palette and by the store listing.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Core plugins cannot be uninstalled, only disabled.</summary>
    public bool IsCore { get; init; }
}

public sealed record CommandContext
{
    public required string CommandId { get; init; }

    /// <summary>Canvas object the command was invoked on, if any.</summary>
    public string? SourceObjectId { get; init; }

    /// <summary>How the command was triggered: menu, shortcut, tool, widget, palette, api.</summary>
    public string Trigger { get; init; } = "menu";

    public IReadOnlyDictionary<string, string> Arguments { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>Implemented by every plugin, built-in or external.</summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }

    /// <summary>
    /// Called once after permissions are resolved. The plugin must not touch host APIs before this.
    /// </summary>
    ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default);

    ValueTask ShutdownAsync(CancellationToken ct = default);

    /// <summary>Dispatches a command declared in the manifest. Unknown ids must be ignored.</summary>
    ValueTask ExecuteCommandAsync(CommandContext context, CancellationToken ct = default);
}

public interface IPluginLogger
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message, Exception? error = null);

    void Error(string message, Exception? error = null);
}

/// <summary>
/// Per plugin settings, stored in the plugin's own database partition so that removing a plugin
/// can remove its data in one step.
/// </summary>
public interface IPluginSettings
{
    T? Get<T>(string key, T? fallback = default);

    void Set<T>(string key, T? value);

    ValueTask SaveAsync(CancellationToken ct = default);

    IReadOnlyList<string> Keys { get; }
}
