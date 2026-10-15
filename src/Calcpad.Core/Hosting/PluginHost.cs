using Calcpad.Core.Plugins;

namespace Calcpad.Core.Hosting;

/// <summary>
/// Aggregates every plugin Calcpad knows about and the contributions they declare. The shell builds
/// the menu bar, shortcut table, tool palette and widget catalog purely from this type, so disabling
/// a plugin removes its UI immediately.
/// </summary>
/// <remarks>
/// Initialization is fault isolated: a plugin that throws is marked as not loaded with an error
/// message while every other plugin keeps working. All state changes are serialised through one
/// gate; readers get immutable snapshots.
/// </remarks>
public sealed class PluginHost : IPluginHostRuntime
{
    private readonly IReadOnlyList<IPlugin> _builtIn;
    private readonly IPluginContextFactory _contexts;
    private readonly IPluginHostOptions _options;
    private readonly IPluginLogger _log;
    private readonly IPluginStateStore _stateStore;

    private readonly object _gate = new();
    private readonly List<Entry> _order = [];
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _commandOwners = new(StringComparer.Ordinal);

    private IReadOnlyList<PluginRuntimeInfo> _plugins = [];
    private IReadOnlyList<MenuContribution> _menus = [];
    private IReadOnlyList<ShortcutContribution> _shortcuts = [];
    private IReadOnlyList<ToolContribution> _tools = [];
    private IReadOnlyList<PanelContribution> _panels = [];
    private IReadOnlyList<WidgetContribution> _widgets = [];
    private bool _initialized;

    /// <summary>Creates the host from the frozen <see cref="PluginHostOptions"/> contract record.</summary>
    /// <param name="builtIn">Plugins compiled into the application, in manifest order.</param>
    /// <param name="contexts">Factory for the mediated per plugin context.</param>
    /// <param name="options">Start-up options supplied by the platform head.</param>
    /// <param name="log">Host logger.</param>
    /// <param name="stateStore">Persistent enable/disable state.</param>
    public PluginHost(
        IReadOnlyList<IPlugin> builtIn,
        IPluginContextFactory contexts,
        PluginHostOptions options,
        IPluginLogger log,
        IPluginStateStore stateStore)
        : this(builtIn, contexts, new PluginHostSettings(options ?? throw new ArgumentNullException(nameof(options))), log, stateStore)
    {
    }

    /// <summary>Creates the host from any <see cref="IPluginHostOptions"/> implementation.</summary>
    /// <param name="builtIn">Plugins compiled into the application, in manifest order.</param>
    /// <param name="contexts">Factory for the mediated per plugin context.</param>
    /// <param name="options">Start-up options supplied by the platform head.</param>
    /// <param name="log">Host logger.</param>
    /// <param name="stateStore">Persistent enable/disable state.</param>
    public PluginHost(
        IReadOnlyList<IPlugin> builtIn,
        IPluginContextFactory contexts,
        IPluginHostOptions options,
        IPluginLogger log,
        IPluginStateStore stateStore)
    {
        _builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        Loader = new FolderPluginLoader(log);
    }

    /// <inheritdoc />
    public event EventHandler? PluginsChanged;

    /// <inheritdoc />
    public event EventHandler<PluginRequestEventArgs>? Requested;

    /// <summary>
    /// Loader used for <see cref="IPluginHostOptions.PluginsDirectory"/>. Replaceable so tests and
    /// the verification suite can inject a fake without touching the disk.
    /// </summary>
    public IPluginLoader? Loader { get; set; }

    /// <summary>
    /// Supplies the live granted capabilities of a plugin for <see cref="PluginRuntimeInfo"/>.
    /// The composition root points this at the permission broker; when unset the host asks the
    /// context factory for its broker, and only if that fails are the grants reported as empty.
    /// </summary>
    public Func<string, IReadOnlyList<Security.PermissionKind>>? GrantedPermissionsProvider { get; set; }

    private Func<string, IReadOnlyList<Security.PermissionKind>>? ResolveGrantedProvider()
    {
        var provider = GrantedPermissionsProvider;
        if (provider is not null)
        {
            return provider;
        }

        return _contexts is PluginContextFactory concreteFactory
            ? concreteFactory.Permissions.GrantedFor
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginRuntimeInfo> Plugins
    {
        get
        {
            lock (_gate)
            {
                return _plugins;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MenuContribution> Menus
    {
        get
        {
            lock (_gate)
            {
                return _menus;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ShortcutContribution> Shortcuts
    {
        get
        {
            lock (_gate)
            {
                return _shortcuts;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolContribution> Tools
    {
        get
        {
            lock (_gate)
            {
                return _tools;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PanelContribution> Panels
    {
        get
        {
            lock (_gate)
            {
                return _panels;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WidgetContribution> Widgets
    {
        get
        {
            lock (_gate)
            {
                return _widgets;
            }
        }
    }

    /// <inheritdoc />
    public PluginRuntimeInfo? Find(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(pluginId, out var entry)
                ? entry.ToInfo(ResolveGrantedProvider())
                : null;
        }
    }

    /// <inheritdoc />
    public string? OwnerOfCommand(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        lock (_gate)
        {
            return _commandOwners.TryGetValue(commandId, out var entry) ? entry.Id : null;
        }
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        List<Entry> candidates;
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            candidates = BuildCandidatesLocked();

            _order.Clear();
            _entries.Clear();
            foreach (var entry in candidates)
            {
                _order.Add(entry);
                _entries[entry.Id] = entry;
            }
        }

        foreach (var entry in candidates)
        {
            if (ct.IsCancellationRequested)
            {
                _log.Warn("Plugin initialization was cancelled; the remaining plugins stay unloaded.");
                break;
            }

            var enabled = await ResolveStartupStateAsync(entry, ct).ConfigureAwait(false);
            entry.Enabled = enabled;
            if (!enabled)
            {
                _log.Info($"Plugin '{entry.Id}' is disabled; skipping initialization.");
                continue;
            }

            await LoadEntryAsync(entry, ct).ConfigureAwait(false);
        }

        Refresh();
    }

    /// <inheritdoc />
    public async ValueTask SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(pluginId, out entry);
        }

        if (entry is null)
        {
            _log.Warn($"Cannot change the state of unknown plugin '{pluginId}'.");
            return;
        }

        await PersistStateAsync(pluginId, enabled, ct).ConfigureAwait(false);

        if (enabled)
        {
            entry.Enabled = true;
            if (!entry.IsLoaded)
            {
                await LoadEntryAsync(entry, ct).ConfigureAwait(false);
            }
        }
        else
        {
            entry.Enabled = false;
            if (entry.IsLoaded)
            {
                await UnloadEntryAsync(entry).ConfigureAwait(false);
            }
        }

        Refresh();
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteCommandAsync(string commandId, CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(context);

        Entry? entry;
        lock (_gate)
        {
            _commandOwners.TryGetValue(commandId, out entry);
        }

        if (entry is null)
        {
            _log.Warn($"No plugin owns command '{commandId}'.");
            return false;
        }

        if (!entry.Enabled || !entry.IsLoaded)
        {
            _log.Warn($"Command '{commandId}' belongs to plugin '{entry.Id}', which is not loaded.");
            return false;
        }

        try
        {
            await entry.Plugin.ExecuteCommandAsync(context, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            _log.Error($"Plugin '{entry.Id}' threw while executing command '{commandId}'.", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public string DescribeCommand(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return string.Empty;
        }

        lock (_gate)
        {
            if (!_commandOwners.TryGetValue(commandId, out var entry))
            {
                return commandId;
            }

            foreach (var menu in entry.Plugin.Manifest.Menus)
            {
                if (string.Equals(menu.CommandId, commandId, StringComparison.Ordinal))
                {
                    return FirstNonEmpty(menu.LocalizedTitleZh, menu.Title, commandId);
                }
            }

            foreach (var shortcut in entry.Plugin.Manifest.Shortcuts)
            {
                if (string.Equals(shortcut.CommandId, commandId, StringComparison.Ordinal))
                {
                    return FirstNonEmpty(shortcut.LocalizedTitleZh, shortcut.Title, commandId);
                }
            }

            foreach (var tool in entry.Plugin.Manifest.Tools)
            {
                if (string.Equals(tool.CommandId, commandId, StringComparison.Ordinal))
                {
                    return FirstNonEmpty(tool.LocalizedTitleZh, tool.Title, commandId);
                }
            }

            foreach (var panel in entry.Plugin.Manifest.Panels)
            {
                if (string.Equals(panel.CommandId, commandId, StringComparison.Ordinal))
                {
                    return FirstNonEmpty(panel.LocalizedTitleZh, panel.Title, commandId);
                }
            }

            foreach (var widget in entry.Plugin.Manifest.Widgets)
            {
                if (string.Equals(widget.CommandId, commandId, StringComparison.Ordinal))
                {
                    return FirstNonEmpty(widget.LocalizedTitleZh, widget.Title, commandId);
                }
            }

            return commandId;
        }
    }

    /// <summary>
    /// Raises <see cref="Requested"/>. The shell calls this when a plugin asks it to surface
    /// something, for example to open a panel or highlight an object.
    /// </summary>
    /// <param name="pluginId">Plugin that made the request.</param>
    /// <param name="requestKind">Kind of request, e.g. <c>panel</c> or <c>toast</c>.</param>
    /// <param name="payload">Optional payload for the shell.</param>
    public void NotifyRequested(string pluginId, string requestKind, string? payload = null)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(requestKind))
        {
            return;
        }

        try
        {
            Requested?.Invoke(this, new PluginRequestEventArgs(pluginId, requestKind, payload));
        }
        catch (Exception ex)
        {
            _log.Error($"A plugin request handler threw for '{pluginId}'.", ex);
        }
    }

    private async ValueTask<bool> ResolveStartupStateAsync(Entry entry, CancellationToken ct)
    {
        if (_options.StartDisabled)
        {
            return false;
        }

        try
        {
            var stored = await _stateStore.GetEnabledAsync(entry.Id, ct).ConfigureAwait(false);
            return stored ?? entry.Plugin.Manifest.EnabledByDefault;
        }
        catch (Exception ex)
        {
            _log.Error($"Could not read the enabled state of '{entry.Id}'; using the manifest default.", ex);
            return entry.Plugin.Manifest.EnabledByDefault;
        }
    }

    private async ValueTask PersistStateAsync(string pluginId, bool enabled, CancellationToken ct)
    {
        try
        {
            await _stateStore.SetEnabledAsync(pluginId, enabled, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not persist the enabled state of '{pluginId}'.", ex);
        }
    }

    private async ValueTask LoadEntryAsync(Entry entry, CancellationToken ct)
    {
        try
        {
            var context = _contexts.Create(entry.Plugin);
            await entry.Plugin.InitializeAsync(context, ct).ConfigureAwait(false);
            entry.IsLoaded = true;
            entry.Error = null;
            _log.Info($"Plugin '{entry.Id}' initialized.");
        }
        catch (Exception ex)
        {
            entry.IsLoaded = false;
            entry.Error = ex.Message;
            _log.Error($"Plugin '{entry.Id}' failed to initialize; it stays unloaded.", ex);

            try
            {
                await entry.Plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception shutdownError)
            {
                _log.Warn($"Plugin '{entry.Id}' also threw while shutting down after a failed start.", shutdownError);
            }

            try
            {
                _contexts.Release(entry.Id);
            }
            catch (Exception releaseError)
            {
                _log.Warn($"Could not release the context of plugin '{entry.Id}'.", releaseError);
            }
        }
    }

    private async ValueTask UnloadEntryAsync(Entry entry)
    {
        try
        {
            await entry.Plugin.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"Plugin '{entry.Id}' threw while shutting down.", ex);
        }

        try
        {
            _contexts.Release(entry.Id);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not release the context of plugin '{entry.Id}'.", ex);
        }

        entry.IsLoaded = false;
        entry.Error = null;
        _log.Info($"Plugin '{entry.Id}' disabled and unloaded.");
    }

    private List<Entry> BuildCandidatesLocked()
    {
        var result = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plugin in _builtIn)
        {
            var entry = TryCreateEntry(plugin, sourcePath: null);
            if (entry is not null && seen.Add(entry.Id))
            {
                result.Add(entry);
            }
        }

        if (!_options.AllowExternalPlugins)
        {
            _log.Info("External plugin loading is disabled; only bundled plugins are used.");
            return result;
        }

        var directory = _options.PluginsDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _log.Info($"Plugin directory '{directory}' does not exist; only bundled plugins are used.");
            return result;
        }

        var loader = Loader;
        if (loader is null)
        {
            _log.Warn("No plugin loader is configured; external plugins are ignored.");
            return result;
        }

        IReadOnlyList<IPlugin> external;
        try
        {
            external = loader.LoadFromDirectory(directory);
        }
        catch (Exception ex)
        {
            _log.Error($"Loading plugins from '{directory}' failed; continuing with bundled plugins.", ex);
            return result;
        }

        foreach (var plugin in external)
        {
            var entry = TryCreateEntry(plugin, ResolveSourcePath(plugin));
            if (entry is null)
            {
                continue;
            }

            if (!seen.Add(entry.Id))
            {
                _log.Warn($"Ignoring external plugin '{entry.Id}' because a plugin with that id is already loaded.");
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Best effort source path of a plugin assembly for the plugin manager. Plugins loaded from a
    /// directory keep a real location even in a single-file host; assemblies bundled into the
    /// executable report an empty location and are reported as null.
    /// </summary>
    private static string? ResolveSourcePath(IPlugin plugin)
    {
#pragma warning disable IL3000 // Assembly.Location is empty only for assemblies bundled into a single file.
        var location = plugin.GetType().Assembly.Location;
#pragma warning restore IL3000
        return string.IsNullOrEmpty(location) ? null : location;
    }

    private Entry? TryCreateEntry(IPlugin plugin, string? sourcePath)
    {
        if (plugin is null)
        {
            return null;
        }

        try
        {
            var manifest = plugin.Manifest;
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                _log.Warn($"A plugin of type '{plugin.GetType().FullName}' has no manifest id and was ignored.");
                return null;
            }

            return new Entry(plugin, sourcePath);
        }
        catch (Exception ex)
        {
            _log.Error($"Reading the manifest of '{plugin.GetType().FullName}' failed; the plugin was ignored.", ex);
            return null;
        }
    }

    private void Refresh()
    {
        List<MenuContribution> menus;
        List<ShortcutContribution> shortcuts;
        List<ToolContribution> tools;
        List<PanelContribution> panels;
        List<WidgetContribution> widgets;

        lock (_gate)
        {
            var active = _order.Where(e => e.Enabled && e.IsLoaded).ToList();

            // Fall back to the context factory's broker so PluginRuntimeInfo.GrantedPermissions is
            // populated even when the composition root did not wire the delegate itself.
            var grantedProvider = ResolveGrantedProvider();

            _plugins = _order.Select(e => e.ToInfo(grantedProvider)).ToList();

            menus = active.SelectMany(e => e.Plugin.Manifest.Menus)
                .OrderBy(m => m.Order)
                .ThenBy(m => m.MenuPath, StringComparer.Ordinal)
                .ThenBy(m => m.Title, StringComparer.Ordinal)
                .ToList();

            shortcuts = active.SelectMany(e => e.Plugin.Manifest.Shortcuts).ToList();

            tools = active.SelectMany(e => e.Plugin.Manifest.Tools)
                .OrderBy(t => t.Order)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList();

            panels = active.SelectMany(e => e.Plugin.Manifest.Panels)
                .OrderBy(p => p.Order)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToList();

            widgets = active.SelectMany(e => e.Plugin.Manifest.Widgets)
                .OrderBy(w => w.WidgetType, StringComparer.Ordinal)
                .ToList();

            _menus = menus;
            _shortcuts = shortcuts;
            _tools = tools;
            _panels = panels;
            _widgets = widgets;

            _commandOwners.Clear();
            foreach (var entry in active)
            {
                foreach (var commandId in EnumerateCommandIds(entry.Plugin.Manifest))
                {
                    _commandOwners.TryAdd(commandId, entry);
                }
            }
        }

        _log.Debug(
            $"Plugin host refreshed: {_plugins.Count(p => p is { Enabled: true, IsLoaded: true })} active plugin(s), " +
            $"{menus.Count} menu item(s), {shortcuts.Count} shortcut(s), {tools.Count} tool(s), " +
            $"{panels.Count} panel(s), {widgets.Count} widget(s).");

        try
        {
            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("A PluginsChanged handler threw.", ex);
        }
    }

    private static IEnumerable<string> EnumerateCommandIds(PluginManifest manifest)
    {
        foreach (var menu in manifest.Menus)
        {
            yield return menu.CommandId;
        }

        foreach (var shortcut in manifest.Shortcuts)
        {
            yield return shortcut.CommandId;
        }

        foreach (var tool in manifest.Tools)
        {
            yield return tool.CommandId;
        }

        foreach (var panel in manifest.Panels)
        {
            yield return panel.CommandId;
        }

        foreach (var widget in manifest.Widgets)
        {
            yield return widget.CommandId;
        }
    }

    private static string FirstNonEmpty(string? preferred, string? fallback, string last) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred!
        : !string.IsNullOrWhiteSpace(fallback) ? fallback!
        : last;

    private sealed class Entry
    {
        public Entry(IPlugin plugin, string? sourcePath)
        {
            Plugin = plugin;
            SourcePath = sourcePath;
            Id = plugin.Manifest.Id;
        }

        public IPlugin Plugin { get; }

        public string Id { get; }

        public string? SourcePath { get; }

        public bool Enabled { get; set; }

        public bool IsLoaded { get; set; }

        public string? Error { get; set; }

        public PluginRuntimeInfo ToInfo(Func<string, IReadOnlyList<Security.PermissionKind>>? grantedProvider) => new(
            Id,
            Plugin.Manifest.Name,
            Plugin.Manifest.Version,
            Plugin.Manifest.Description,
            Plugin.Manifest.Author,
            Plugin.Manifest.Icon,
            Enabled,
            Plugin.Manifest.IsCore,
            IsLoaded,
            Error,
            Plugin.Manifest.Permissions,
            grantedProvider?.Invoke(Id) ?? [],
            Plugin.Manifest.Keywords,
            SourcePath);
    }
}
