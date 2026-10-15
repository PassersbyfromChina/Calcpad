namespace Calcpad.Core.Plugins;

/// <summary>
/// Menu entry contribution. <paramref name="MenuPath"/> uses a slash separated path such as
/// <c>Insert/Calculator</c>; the shell creates missing sub menus automatically.
/// </summary>
public sealed record MenuContribution(
    string MenuPath,
    string Title,
    string CommandId,
    string? Shortcut = null,
    string? Icon = null,
    int Order = 100,
    bool RequiresSelection = false,
    bool RequiresCanvasWrite = false,
    string? LocalizedTitleZh = null);

/// <summary>
/// Shortcut contribution. <paramref name="Gesture"/> is a portable gesture string such as
/// <c>Ctrl+Shift+H</c>, <c>Meta+K</c> or <c>Alt+L</c>; the shell maps Meta to Ctrl on
/// Windows and Android so the same plugin works on every platform.
/// </summary>
public sealed record ShortcutContribution(
    string Gesture,
    string CommandId,
    string Title,
    string? When = null,
    string? LocalizedTitleZh = null);

public enum PanelPlacement
{
    Left,
    Right,
    Bottom,
    Floating
}

public sealed record PanelContribution(
    string Id,
    string Title,
    string CommandId,
    PanelPlacement Placement = PanelPlacement.Right,
    int Order = 100,
    double PreferredWidth = 360,
    string? LocalizedTitleZh = null);

/// <summary>A canvas tool the plugin contributes (pen, lasso, text, ...).</summary>
public sealed record ToolContribution(
    string Id,
    string Title,
    string CommandId,
    string? Shortcut = null,
    string? Icon = null,
    int Order = 100,
    string? LocalizedTitleZh = null);

/// <summary>A widget type the plugin can place on the infinite canvas (keypad, converter, graph, ...).</summary>
public sealed record WidgetContribution(
    string WidgetType,
    string Title,
    string CommandId,
    double DefaultWidth = 320,
    double DefaultHeight = 480,
    string? LocalizedTitleZh = null);
