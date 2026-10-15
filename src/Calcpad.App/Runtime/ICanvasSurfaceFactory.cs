using Avalonia.Controls;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.App;

/// <summary>Shell facing view of the canvas surface.</summary>
public interface ICanvasSurface
{
    Control View { get; }

    void SetTool(string? toolId);

    void Invalidate();

    ValueTask ZoomByAsync(double factor);

    ValueTask ZoomToFitAsync();

    void SetGridVisible(bool visible);

    void FocusCanvas();
}

/// <summary>
/// Creates the control that hosts the infinite canvas. Implemented by the composition root so the
/// shell does not depend on a concrete canvas implementation.
/// </summary>
public interface ICanvasSurfaceFactory
{
    ICanvasSurface Create(ICanvasEngine engine, IUiRegistry ui, IHostApi host);
}
