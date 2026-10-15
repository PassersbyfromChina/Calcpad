using System.Text.Json.Nodes;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Plugins.CalculatorKit;

namespace Calcpad.Plugin.ScientificCalculator;

/// <summary>
/// The iPad landscape scientific calculator as a canvas widget: 2nd toggle, trigonometry with
/// inverse and hyperbolic variants, logarithms, powers and roots, factorial, constants, random,
/// EE, parentheses and the Deg/Rad/Grad indicator. Every key feeds <see cref="IExpressionEngine"/>.
/// </summary>
public sealed class ScientificCalculatorWidget : IWidgetRenderer
{
    private readonly IExpressionEngine _engine;

    /// <summary>Creates the widget, optionally with a shared expression engine.</summary>
    public ScientificCalculatorWidget(IExpressionEngine? engine = null) => _engine = engine ?? new ExpressionEngine();

    /// <inheritdoc />
    public string WidgetType => "calcpad.calculator.scientific";

    /// <summary>The keypad this widget draws.</summary>
    public IReadOnlyList<CalculatorKey> Keys => CalculatorKeypads.Scientific;

    /// <summary>Scientific keys are rounded rectangles, not circles.</summary>
    public bool RoundKeys => false;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) => new(760, 420);

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host) => InitializeState(target);

    /// <summary>Initialises <see cref="WidgetObject.State"/> without needing a live host.</summary>
    public void InitializeState(WidgetObject target)
    {
        target.State ??= new JsonObject();
        var state = CalculatorState.From(target, _engine);
        state.Save(target.State);
        target.State["pressed"] = null;
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Theme;
        var drawing = context.Drawing;
        var state = CalculatorState.From(context.Object, _engine);

        CalculatorPaint.DrawPanel(drawing, new CRect(0, 0, size.Width, size.Height), theme.SurfaceBrush, 22);

        var displayHeight = KeypadGeometry.DisplayHeight(size, true);
        var padding = Math.Max(6, theme.WidgetPadding);

        var badgeArea = new CRect(padding + 6, 12, Math.Min(120, size.Width * 0.16), displayHeight * 0.34);
        CalculatorPaint.DrawTextIn(
            drawing,
            state.AngleLabel,
            badgeArea,
            Math.Clamp(displayHeight * 0.14, 10, 16),
            theme.Brush(theme.Accent),
            CalculatorPaint.TextAlign.Left,
            bold: true);

        if (state.SecondActive)
        {
            var secondBadge = new CRect(badgeArea.X, badgeArea.Bottom + 2, badgeArea.Width, badgeArea.Height);
            CalculatorPaint.DrawTextIn(
                drawing,
                "2nd",
                secondBadge,
                Math.Clamp(displayHeight * 0.14, 10, 16),
                theme.Brush(theme.Accent),
                CalculatorPaint.TextAlign.Left,
                bold: true);
        }

        var textArea = new CRect(badgeArea.Right + 8, 10, Math.Max(10, size.Width - badgeArea.Right - padding - 14), displayHeight - 16);
        var expressionArea = new CRect(textArea.X, textArea.Y, textArea.Width, textArea.Height * 0.38);
        CalculatorPaint.DrawTextIn(
            drawing,
            state.ExpressionText,
            expressionArea,
            Math.Clamp(displayHeight * 0.15, 11, 18),
            theme.Brush(theme.TextSecondary),
            CalculatorPaint.TextAlign.Right);

        var valueArea = new CRect(textArea.X, expressionArea.Bottom, textArea.Width, textArea.Bottom - expressionArea.Bottom);
        CalculatorPaint.DrawTextIn(
            drawing,
            state.DisplayText,
            valueArea,
            Math.Clamp(displayHeight * 0.2, 14, 26),
            theme.Brush(state.IsError ? theme.Danger : theme.TextPrimary),
            CalculatorPaint.TextAlign.Right,
            bold: true,
            maxSize: Math.Clamp(displayHeight * 0.44, 20, 48));

        var pressed = ReadPressed(context.Object);
        foreach (var key in Keys)
        {
            var rect = KeypadGeometry.RectFor(
                size,
                key,
                CalculatorKeypads.ScientificRows,
                CalculatorKeypads.ScientificColumns,
                theme.KeyGap,
                padding,
                displayHeight);
            var isPressed = string.Equals(pressed, key.Id, StringComparison.Ordinal);
            var active = (key.Kind == CalculatorKeyKind.Second && state.SecondActive)
                || (key.Kind == CalculatorKeyKind.Angle && state.AngleUnit != AngleUnit.Degrees);
            var (fill, text) = CalculatorColors.For(key, theme, isPressed, active);
            CalculatorPaint.DrawKey(drawing, rect, theme.Brush(fill), false);
            CalculatorPaint.DrawTextIn(
                drawing,
                LabelFor(key, state),
                rect,
                Math.Clamp(rect.Height * 0.4, 9, 22),
                theme.Brush(text),
                CalculatorPaint.TextAlign.Center,
                bold: key.Kind is CalculatorKeyKind.Equals or CalculatorKeyKind.Operator);
        }

        if (!string.IsNullOrEmpty(state.Hint) && state.IsError)
        {
            var hintArea = new CRect(padding, size.Height - 16, size.Width - (padding * 2), 13);
            CalculatorPaint.DrawTextIn(
                drawing,
                state.Hint,
                hintArea,
                9.5,
                theme.Brush(theme.Danger),
                CalculatorPaint.TextAlign.Right,
                maxSize: 9.5);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Selection), 2),
                new Avalonia.RoundedRect(CalculatorPaint.ToRect(new CRect(1, 1, size.Width - 2, size.Height - 2)), 22));
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Host.Theme;
        var padding = Math.Max(6, theme.WidgetPadding);
        var displayHeight = KeypadGeometry.DisplayHeight(size, true);

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
                {
                    var key = Hit(context.Position, size, padding, displayHeight, theme.KeyGap);
                    if (key is null)
                    {
                        return false;
                    }

                    SetPressed(context.Object, key.Id);
                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Up:
                {
                    var pressedId = ReadPressed(context.Object);
                    if (pressedId is null)
                    {
                        return false;
                    }

                    SetPressed(context.Object, null);
                    var key = Hit(context.Position, size, padding, displayHeight, theme.KeyGap);
                    if (key is not null && string.Equals(key.Id, pressedId, StringComparison.Ordinal))
                    {
                        Press(context.Object, context.Host, key);
                    }

                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Cancel:
                if (ReadPressed(context.Object) is null)
                {
                    return false;
                }

                SetPressed(context.Object, null);
                context.Host.Invalidate();
                return true;

            default:
                return false;
        }

        CalculatorKey? Hit(CPoint point, CSize logical, double pad, double display, double gap) =>
            KeypadGeometry.HitTest(
                Keys,
                logical,
                CalculatorKeypads.ScientificRows,
                CalculatorKeypads.ScientificColumns,
                point,
                gap,
                pad,
                display,
                RoundKeys);
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        var normalized = CalculatorKeyboard.Normalize(context.Key);
        if (normalized.Length == 0)
        {
            return false;
        }

        var key = Keys.FirstOrDefault(k => string.Equals(k.Id, normalized, StringComparison.Ordinal));
        if (key is not null)
        {
            Press(context.Object, context.Host, key);
            return true;
        }

        switch (normalized)
        {
            case "+":
            case "-":
            case "*":
            case "/":
                var opKey = Keys.FirstOrDefault(k => k.Kind == CalculatorKeyKind.Operator && k.Primary == normalized);
                if (opKey is not null)
                {
                    Press(context.Object, context.Host, opKey);
                    return true;
                }

                break;
            case "=":
                var equals = Keys.FirstOrDefault(k => k.Kind == CalculatorKeyKind.Equals);
                if (equals is not null)
                {
                    Press(context.Object, context.Host, equals);
                    return true;
                }

                break;
            case "Backspace":
                Mutate(context, s => s.Backspace());
                return true;
            case "Clear":
                Mutate(context, s => s.ClearAll());
                return true;
            case "%":
                Mutate(context, s => s.ApplyPercent());
                return true;
            case "±":
                Mutate(context, s => s.ToggleSign());
                return true;
            case "^":
            case "(":
            case ")":
            case ".":
            case "!":
            case "π":
            case "e":
                Mutate(context, s => s.AppendText(normalized));
                return true;
        }

        if (normalized.Length == 1 && (char.IsDigit(normalized[0]) || char.IsLetter(normalized[0])))
        {
            Mutate(context, s => s.AppendText(normalized));
            return true;
        }

        return false;
    }

    /// <summary>Applies a key press, wiring equals into the history plugin.</summary>
    private void Press(WidgetObject target, IWidgetHost host, CalculatorKey key)
    {
        var state = CalculatorState.From(target, _engine);
        var before = state.LastExpression;
        state.Press(key);
        Save(target, state);
        if (key.Kind == CalculatorKeyKind.Equals && !state.IsError && state.LastExpression.Length > 0
            && !string.Equals(before, state.LastExpression, StringComparison.Ordinal))
        {
            CalculatorHistory.TryAppend(host, state.LastExpression, state.LastResult);
        }

        host.Invalidate();
    }

    private void Mutate(WidgetKeyContext context, Action<CalculatorState> mutate)
    {
        var state = CalculatorState.From(context.Object, _engine);
        mutate(state);
        Save(context.Object, state);
        context.Host.Invalidate();
    }

    /// <summary>Label drawn on a key, honouring 2nd and the current angle unit.</summary>
    private static string LabelFor(CalculatorKey key, CalculatorState state) => key.Kind switch
    {
        CalculatorKeyKind.Second => "2nd",
        CalculatorKeyKind.Angle => state.AngleLabel,
        CalculatorKeyKind.Clear => state.ClearLabel,
        _ => state.SecondActive ? key.SecondLabel ?? key.Label : key.Label
    };

    private static void Save(WidgetObject target, CalculatorState state)
    {
        target.State ??= new JsonObject();
        state.Save(target.State);
    }

    private static string? ReadPressed(WidgetObject target) =>
        target.State is { } state && state.TryGetPropertyValue("pressed", out var node) && node is not null
            ? node.ToString()
            : null;

    private static void SetPressed(WidgetObject target, string? keyId)
    {
        target.State ??= new JsonObject();
        target.State["pressed"] = keyId;
    }
}
