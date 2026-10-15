using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;

namespace Calcpad.Plugin.Sdk.Ui;

/// <summary>Services a widget may use while it is live on the canvas.</summary>
public interface IWidgetHost
{
    ICanvasApi Canvas { get; }

    IHostApi Host { get; }

    IPluginSettings Settings { get; }

    string PluginId { get; }

    PluginTheme Theme { get; }

    /// <summary>Dispatches a command declared by this plugin (or any other enabled plugin).</summary>
    ValueTask InvokeCommandAsync(string commandId, IReadOnlyDictionary<string, string>? arguments = null, CancellationToken ct = default);

    /// <summary>Asks the shell to repaint this widget.</summary>
    void Invalidate();

    /// <summary>Marks which widget currently owns the keyboard.</summary>
    void RequestFocus(string? focusKey);

    string? FocusedKey { get; }

    /// <summary>
    /// Host for another plugin, used when a widget embeds that plugin's surface. Crucially the
    /// returned host carries the <em>other</em> plugin's identity, so the embedded surface runs with
    /// its own grants instead of borrowing the embedder's permissions. Returns null when no such
    /// plugin is enabled; the default keeps every existing host implementation compiling.
    /// </summary>
    IWidgetHost? ForPlugin(string pluginId) => null;
}

public sealed class WidgetMeasureContext
{
    public required WidgetObject Object { get; init; }

    public required IWidgetHost Host { get; init; }

    /// <summary>Current canvas zoom, so widgets can keep a readable minimum size.</summary>
    public double Zoom { get; init; } = 1.0;
}

public sealed class WidgetRenderContext
{
    public required DrawingContext Drawing { get; init; }

    /// <summary>Screen space rectangle the widget must paint inside.</summary>
    public required CRect Bounds { get; init; }

    public required WidgetObject Object { get; init; }

    public required IWidgetHost Host { get; init; }

    public required PluginTheme Theme { get; init; }

    public double Zoom { get; init; } = 1.0;

    public bool IsSelected { get; init; }

    public bool IsFocused { get; init; }

    /// <summary>Local logical size: <see cref="Bounds"/> divided by <see cref="Zoom"/>.</summary>
    public CSize LogicalSize => new(
        Zoom <= 0 ? Bounds.Width : Bounds.Width / Zoom,
        Zoom <= 0 ? Bounds.Height : Bounds.Height / Zoom);
}

public enum WidgetPointerPhase
{
    Down,
    Move,
    Up,
    Cancel,
    Wheel
}

public sealed class WidgetPointerContext
{
    public required WidgetPointerPhase Phase { get; init; }

    /// <summary>Position in widget local logical coordinates, origin at the widget's top left.</summary>
    public required CPoint Position { get; init; }

    public required CSize LogicalSize { get; init; }

    public required WidgetObject Object { get; init; }

    public required IWidgetHost Host { get; init; }

    public bool IsPrimary { get; init; } = true;

    public int ClickCount { get; init; } = 1;

    public double WheelDelta { get; init; }

    public bool Handled { get; set; }
}

public sealed class WidgetKeyContext
{
    public required string Key { get; init; }

    public required WidgetObject Object { get; init; }

    public required IWidgetHost Host { get; init; }

    public bool Control { get; init; }

    public bool Shift { get; init; }

    public bool Alt { get; init; }

    public bool Handled { get; set; }
}

/// <summary>
/// A live, interactive surface placed on the infinite canvas. This is how plugins own the UI:
/// the calculator keypad, the unit converter and the graph pane are all widgets, not hard coded
/// screens, so disabling a plugin removes them from the product entirely.
/// </summary>
public interface IWidgetRenderer
{
    string WidgetType { get; }

    /// <summary>Preferred size in world units for a freshly inserted widget.</summary>
    CSize Measure(WidgetMeasureContext context);

    void Render(WidgetRenderContext context);

    /// <summary>Return true when the widget consumed the event.</summary>
    bool OnPointer(WidgetPointerContext context) => false;

    bool OnKey(WidgetKeyContext context) => false;

    /// <summary>Called when the widget is first placed, to initialise <see cref="WidgetObject.State"/>.</summary>
    void OnAttached(WidgetObject target, IWidgetHost host)
    {
    }

    void OnDetached(WidgetObject target, IWidgetHost host)
    {
    }
}
