using Avalonia.Controls;

namespace Calcpad.App.Views;

/// <summary>
/// Adapts the concrete canvas control to <see cref="ICanvasSurface"/>. Kept free of any reference
/// to the canvas view type so the shell can be compiled and tested before the view lands.
/// </summary>
public sealed class CanvasSurface : ICanvasSurface
{
    private readonly Action<string?> _setTool;
    private readonly Func<double, ValueTask> _zoomBy;
    private readonly Func<ValueTask> _zoomToFit;
    private readonly Action<bool> _setGrid;
    private readonly Action _invalidate;

    public CanvasSurface(
        Control view,
        Action<string?> setTool,
        Func<double, ValueTask> zoomBy,
        Func<ValueTask> zoomToFit,
        Action<bool> setGrid,
        Action invalidate)
    {
        View = view;
        _setTool = setTool;
        _zoomBy = zoomBy;
        _zoomToFit = zoomToFit;
        _setGrid = setGrid;
        _invalidate = invalidate;
    }

    public Control View { get; }

    public void SetTool(string? toolId) => _setTool(toolId);

    public void Invalidate() => _invalidate();

    public ValueTask ZoomByAsync(double factor) => _zoomBy(factor);

    public ValueTask ZoomToFitAsync() => _zoomToFit();

    public void SetGridVisible(bool visible) => _setGrid(visible);

    public void FocusCanvas() => View.Focus();
}
