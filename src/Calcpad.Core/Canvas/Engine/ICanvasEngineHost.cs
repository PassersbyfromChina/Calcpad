using Calcpad.Core.Security;

namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Host only view of the canvas engine: everything the shell and the canvas view need on top of the
/// frozen <see cref="ICanvasEngine"/> contract (viewport bookkeeping, detached lookups, bulk ink and
/// move operations, camera notifications). Plugins never see this interface - they only get the
/// permission checked <see cref="ICanvasApi"/> from <see cref="ICanvasEngine.ApiFor"/>.
/// </summary>
public interface ICanvasEngineHost : ICanvasEngine
{
    /// <summary>Authorization authority consulted by the plugin facades.</summary>
    IPermissionBroker Permissions { get; }

    /// <summary>Log sink for engine and plugin failures; never null.</summary>
    ICanvasEngineLog Log { get; set; }

    /// <summary>Viewport size in device independent pixels, kept up to date by the view.</summary>
    CSize ViewportSize { get; set; }

    /// <summary>Callback the shell installs so plugins can activate a tool.</summary>
    Func<string, CancellationToken, ValueTask<bool>>? ToolActivator { get; set; }

    /// <summary>Undo / redo state.</summary>
    CanvasHistory History { get; }

    /// <summary>Spatial index statistics of the most recent region query.</summary>
    CanvasGridIndex Index { get; }

    /// <summary>Live object lookup by id, or null.</summary>
    CanvasObject? Get(string? objectId);

    /// <summary>Detached copy of one object, or null.</summary>
    CanvasObject? GetDetached(string? objectId);

    /// <summary>Thread safe copy of the current selection ids.</summary>
    IReadOnlyList<string> SelectionSnapshot();

    /// <summary>Fully detached snapshot of the document, camera and selection.</summary>
    CanvasSnapshot CreateSnapshot(CanvasQuery? query = null);

    /// <summary>Moves every listed object by a world space delta.</summary>
    int Translate(IReadOnlyList<string> objectIds, double worldDx, double worldDy, string? originPluginId = null);

    /// <summary>Erases ink strokes within a world radius and returns how many strokes went away.</summary>
    int EraseInk(CPoint world, double worldRadius, string? originPluginId = null);

    /// <summary>Replaces the selection, recording which plugin caused the change.</summary>
    void SetSelection(IEnumerable<string> objectIds, string? originPluginId);

    /// <summary>Moves the camera so a world region is comfortably visible.</summary>
    void Reveal(CRect region, string? originPluginId = null);

    /// <summary>Raises <see cref="ICanvasEngine.Changed"/> with <see cref="CanvasChangeKind.CameraChanged"/>.</summary>
    void NotifyCameraChanged(string? originPluginId = null);

    /// <summary>Asks the shell to activate a tool.</summary>
    ValueTask<bool> ActivateToolAsync(string toolId, string? originPluginId = null, CancellationToken ct = default);

    /// <summary>Drops the cached facade of a plugin, e.g. when it is unloaded.</summary>
    void ReleaseApi(string pluginId);
}
