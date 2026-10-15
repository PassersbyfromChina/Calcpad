using Calcpad.Core.Security;

namespace Calcpad.Core.Plugins;

public sealed record PluginRuntimeInfo(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Author,
    string? Icon,
    bool Enabled,
    bool IsCore,
    bool IsLoaded,
    string? Error,
    IReadOnlyList<PermissionKind> RequestedPermissions,
    IReadOnlyList<PermissionKind> GrantedPermissions,
    IReadOnlyList<string> Keywords,
    string? SourcePath);

/// <summary>
/// Aggregates every loaded plugin and the contributions they declared. The shell builds the menu
/// bar, shortcut table, tool palette and widget catalog purely from this, which is what makes the
/// whole application pluggable: removing a plugin removes its UI.
/// </summary>
public interface IPluginHostRuntime
{
    IReadOnlyList<PluginRuntimeInfo> Plugins { get; }

    event EventHandler? PluginsChanged;

    IReadOnlyList<MenuContribution> Menus { get; }

    IReadOnlyList<ShortcutContribution> Shortcuts { get; }

    IReadOnlyList<ToolContribution> Tools { get; }

    IReadOnlyList<PanelContribution> Panels { get; }

    IReadOnlyList<WidgetContribution> Widgets { get; }

    PluginRuntimeInfo? Find(string pluginId);

    /// <summary>Plugin that declared a command id, or null.</summary>
    string? OwnerOfCommand(string commandId);

    ValueTask InitializeAsync(CancellationToken ct = default);

    ValueTask SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default);

    ValueTask<bool> ExecuteCommandAsync(string commandId, CommandContext context, CancellationToken ct = default);

    /// <summary>Human readable label for a command, taken from the first contribution that declares it.</summary>
    string DescribeCommand(string commandId);

    /// <summary>Raised when a plugin asks the shell to surface something.</summary>
    event EventHandler<PluginRequestEventArgs>? Requested;
}

public sealed record PluginRequestEventArgs(string PluginId, string RequestKind, string? Payload);

/// <summary>Loads plugin assemblies from disk. Android uses the bundled catalog only.</summary>
public interface IPluginLoader
{
    IReadOnlyList<IPlugin> LoadFromDirectory(string directory);

    IReadOnlyList<IPlugin> LoadFromAssemblies(IEnumerable<string> assemblyPaths);
}
