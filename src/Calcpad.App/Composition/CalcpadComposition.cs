using Avalonia.Controls;
using Calcpad.App.Services;
using Calcpad.App.Views;
using Calcpad.App.Views.Canvas;
using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Canvas.Engine;
using Calcpad.Core.Data;
using Calcpad.Core.Hosting;
using Calcpad.Core.Numerics;
using Calcpad.Core.Security.Impl;
using Calcpad.Plugin.CanvasTools;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App;

/// <summary>
/// Composition root. The only place in Calcpad where concrete implementations are constructed;
/// every other file depends on the frozen contracts only.
/// </summary>
public static class CalcpadComposition
{
    public static async Task<CalcpadRuntime> BuildAsync(AppPaths paths, string[] args)
    {
        // ---- shell level services --------------------------------------------------------
        var settings = AppSettings.Load(paths);
        var database = new CalcpadDatabase(paths.DatabaseFile);
        var hostStore = new SqliteHostStore(database);
        var loggers = new PluginLoggerFactory(paths.DataDirectory);
        var shellLog = loggers.Create("calcpad.shell");

        // Dialogs need an owner window, which only exists once the lifetime has started.
        Window? OwnerProvider() =>
            Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

        var permissions = new PermissionBroker(hostStore, new AppPermissionPrompt(OwnerProvider), shellLog);
        var egress = new EgressGuard(hostStore, new AppEgressPrompt(OwnerProvider), shellLog);

        // ---- canvas ----------------------------------------------------------------------
        var registry = new CanvasObjectRegistry();
        var canvasEngine = new CanvasEngine(permissions, registry);

        // ---- math ------------------------------------------------------------------------
        IExpressionEngine math = new ExpressionEngine();
        IUnitConverter units = new UnitConverter();

        // ---- AI --------------------------------------------------------------------------
        var aiSettings = new AiSettingsStore(hostStore);
        var aiCore = new AiCore(
            new AiCoreOptions
            {
                ModelsDirectory = paths.ModelsDirectory,
                PreferOnDevice = true
            },
            aiSettings,
            http: null,
            math: math,
            log: shellLog);
        var aiServices = new AiServiceFactory(aiCore, egress, shellLog);

        // ---- host api --------------------------------------------------------------------
        var hostApi = new AppHostApi(paths, canvasEngine, OwnerProvider);

        // ---- plugin host -----------------------------------------------------------------
        var settingsFactory = new SqlitePluginSettingsFactory(hostStore, permissions, shellLog);
        var coreContexts = new PluginContextFactory(
            canvasEngine,
            pluginId => new SqliteDataStore(database, pluginId, permissions),
            permissions,
            pluginId => aiServices.Create(pluginId, canvasEngine.ApiFor(pluginId)),
            hostApi,
            settingsFactory,
            pluginId => loggers.Create(pluginId));

        var ui = new UiRegistry(canvasEngine, hostApi, pluginId => settingsFactory.Create(pluginId));
        var shellClipboard = new AvaloniaShellClipboard(AvaloniaShellClipboard.ResolveTopLevel);
        var contexts = new AppPluginContextFactory(coreContexts, ui, shellClipboard, permissions);

        var pluginHost = new PluginHost(
            BuiltInPlugins.Create(),
            contexts,
            new PluginHostOptions(paths.PluginsDirectory, AllowExternalPlugins: !OperatingSystem.IsAndroid()),
            shellLog,
            new SqlitePluginStateStore(hostStore));

        ui.Plugins = pluginHost;
        hostApi.Commands = pluginHost;

        // The context factory is decorated by the shell, so the host cannot discover the broker on
        // its own; without this the plugin manager would show every plugin as holding nothing.
        pluginHost.GrantedPermissionsProvider = permissions.GrantedFor;

        await pluginHost.InitializeAsync().ConfigureAwait(false);

        // ---- tools and rasteriser --------------------------------------------------------
        var tools = new ToolService();
        canvasEngine.ToolActivator = (toolId, _) =>
        {
            tools.SetTool(toolId);
            return ValueTask.FromResult(true);
        };

        canvasEngine.Rasterizer = new AvaloniaCanvasRasterizer(canvasEngine, ui);

        // ---- workspace -------------------------------------------------------------------
        var workspace = new WorkspaceService(canvasEngine, paths);
        await RestoreAsync(workspace, settings).ConfigureAwait(false);

        var surfaces = new CanvasSurfaceFactory
        {
            SettingsProvider = pluginId => settingsFactory.Create(pluginId)
        };

        return new CalcpadRuntime
        {
            Paths = paths,
            Canvas = canvasEngine,
            Plugins = pluginHost,
            Ui = ui,
            Host = hostApi,
            Permissions = permissions,
            Egress = egress,
            Ai = aiCore,
            Workspace = workspace,
            Settings = settings,
            ContextFactory = contexts,
            CanvasSurfaces = surfaces,
            Tools = tools
        };
    }

    private static async Task RestoreAsync(WorkspaceService workspace, AppSettings settings)
    {
        var last = settings.LastDocumentPath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last))
        {
            try
            {
                if (await workspace.LoadAsync(last).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // A corrupt or moved document must never stop start-up.
            }
        }

        workspace.NewDocument();
    }

    /// <summary>Builds the infinite canvas control and adapts it to the shell's surface contract.</summary>
    private sealed class CanvasSurfaceFactory : ICanvasSurfaceFactory
    {
        public ICanvasSurface Create(ICanvasEngine engine, IUiRegistry ui, Calcpad.Core.Plugins.IHostApi host)
        {
            var view = new InfiniteCanvasView(engine, ui, host)
            {
                // Lets canvas widgets persist their state through the same settings store plugins use.
                SettingsProvider = SettingsProvider
            };
            return new CanvasSurface(
                view,
                toolId => view.SetTool(toolId ?? string.Empty),
                factor => view.ZoomByAsync(factor),
                () => view.ZoomToFitAsync(),
                visible => view.SetGridVisible(visible),
                view.Invalidate);
        }

        /// <summary>Bound by the composition root once the plugin settings factory exists.</summary>
        public Func<string, Calcpad.Core.Plugins.IPluginSettings?>? SettingsProvider { get; set; }
    }
}
