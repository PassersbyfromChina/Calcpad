using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Hosting;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App;

/// <summary>
/// Everything the shell needs, assembled once at start-up. The composition root lives in
/// <see cref="CalcpadComposition"/>; UI code only ever sees this container.
/// </summary>
public sealed class CalcpadRuntime
{
    public required AppPaths Paths { get; init; }

    public required ICanvasEngine Canvas { get; init; }

    public required IPluginHostRuntime Plugins { get; init; }

    public required IUiRegistry Ui { get; init; }

    public required IHostApi Host { get; init; }

    public required IPermissionBroker Permissions { get; init; }

    public required IEgressGuard Egress { get; init; }

    /// <summary>
    /// Shared AI core: recognisers, local model discovery, provider registry. Plugins reach the
    /// per-plugin, permission checked facade through their context instead of this.
    /// </summary>
    public required AiCore Ai { get; init; }

    public required WorkspaceService Workspace { get; init; }

    public required AppSettings Settings { get; init; }

    public required IPluginContextFactory ContextFactory { get; init; }

    /// <summary>
    /// Builds the widget surface that hosts the infinite canvas. Supplied by the composition root
    /// so the shell never hard-codes the canvas implementation.
    /// </summary>
    public required ICanvasSurfaceFactory CanvasSurfaces { get; init; }

    public required ToolService Tools { get; init; }

    /// <summary>Releases the canvas engine's rasterizer hook and shuts plugins down.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var plugin in Plugins.Plugins.Where(p => p.Enabled && p.IsLoaded))
            {
                await Plugins.SetEnabledAsync(plugin.Id, false).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Shutdown must never throw into the UI thread.
        }

        if (ContextFactory is IAsyncDisposable asyncFactory)
        {
            await asyncFactory.DisposeAsync().ConfigureAwait(false);
        }
    }
}
