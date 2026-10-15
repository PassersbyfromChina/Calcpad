using System.Text.Json.Nodes;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Plugins.CalculatorKit;

namespace Calcpad.Plugin.BasicCalculator;

/// <summary>
/// The iPad basic calculator as a canvas widget: near black card, round keys, grey digits, orange
/// operators, an expression line above a large value and full pointer plus keyboard interaction.
/// </summary>
public sealed class BasicCalculatorWidget : IWidgetRenderer
{
    private readonly IExpressionEngine _engine;

    /// <summary>Creates the widget, optionally with a shared expression engine.</summary>
    public BasicCalculatorWidget(IExpressionEngine? engine = null) => _engine = engine ?? new ExpressionEngine();

    /// <inheritdoc />
    public string WidgetType => "calcpad.calculator.basic";

    /// <summary>The keypad this widget draws.</summary>
    public IReadOnlyList<CalculatorKey> Keys => CalculatorKeypads.Basic;

    /// <summary>True when keys are drawn as circles, as on the iPad in portrait.</summary>
    public bool RoundKeys => true;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) => new(340, 520);

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

        CalculatorPaint.DrawPanel(drawing, new CRect(0, 0, size.Width, size.Height), theme.SurfaceBrush, 24);

        var displayHeight = KeypadGeometry.DisplayHeight(size, false);
        var padding = Math.Max(6, theme.WidgetPadding);
        var expressionArea = new CRect(padding + 4, 10, Math.Max(10, size.Width - (padding * 2) - 8), displayHeight * 0.38);
        CalculatorPaint.DrawTextIn(
            drawing,
            state.ExpressionText,
            expressionArea,
            Math.Clamp(displayHeight * 0.15, 11, 18),
            theme.Brush(theme.TextSecondary),
            CalculatorPaint.TextAlign.Right);

        var valueArea = new CRect(padding + 4, expressionArea.Bottom, expressionArea.Width, displayHeight - expressionArea.Height - 8);
        var valueBrush = theme.Brush(state.IsError ? theme.Danger : theme.TextPrimary);
        CalculatorPaint.DrawTextIn(
            drawing,
            state.DisplayText,
            valueArea,
            Math.Clamp(displayHeight * 0.22, 16, 30),
            valueBrush,
            CalculatorPaint.TextAlign.Right,
            bold: true,
            maxSize: Math.Clamp(displayHeight * 0.46, 22, 56));

        var pressed = ReadPressed(context.Object);
        foreach (var key in Keys)
        {
            var rect = KeypadGeometry.RectFor(
                size,
                key,
                CalculatorKeypads.BasicRows,
                CalculatorKeypads.BasicColumns,
                theme.KeyGap,
                padding,
                displayHeight);
            var isPressed = string.Equals(pressed, key.Id, StringComparison.Ordinal);
            var (fill, text) = CalculatorColors.For(key, theme, isPressed);
            CalculatorPaint.DrawKey(drawing, rect, theme.Brush(fill), KeypadGeometry.IsRound(key, rect, RoundKeys));
            var label = key.Id == "ac" ? state.ClearLabel : key.Label;
            CalculatorPaint.DrawTextIn(
                drawing,
                label,
                rect,
                Math.Clamp(rect.Height * 0.36, 12, 30),
                theme.Brush(text),
                CalculatorPaint.TextAlign.Center,
                bold: true);
        }

        if (!string.IsNullOrEmpty(state.Hint))
        {
            var hintArea = new CRect(padding, size.Height - 18, size.Width - (padding * 2), 14);
            CalculatorPaint.DrawTextIn(
                drawing,
                state.Hint,
                hintArea,
                10,
                theme.Brush(state.IsError ? theme.Danger : theme.TextSecondary),
                CalculatorPaint.TextAlign.Right);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(null, new Pen(theme.Brush(theme.Selection), 2), new Avalonia.RoundedRect(
                CalculatorPaint.ToRect(new CRect(1, 1, size.Width - 2, size.Height - 2)), 24));
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        var state = CalculatorState.From(context.Object, _engine);
        var size = context.LogicalSize;
        var theme = context.Host.Theme;
        var padding = Math.Max(6, theme.WidgetPadding);
        var displayHeight = KeypadGeometry.DisplayHeight(size, false);

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
                CalculatorKeypads.BasicRows,
                CalculatorKeypads.BasicColumns,
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
            case "=":
                var opKey = Keys.FirstOrDefault(k => k.Kind == CalculatorKeyKind.Operator && k.Primary == normalized)
                    ?? Keys.FirstOrDefault(k => k.Kind == CalculatorKeyKind.Equals);
                if (opKey is not null)
                {
                    Press(context.Object, context.Host, opKey);
                    return true;
                }

                break;
            case "Backspace":
                Apply(context.Object, context.Host, s => s.Backspace(), historyOnEquals: false);
                return true;
            case "Clear":
                Apply(context.Object, context.Host, s => s.ClearAll(), historyOnEquals: false);
                return true;
            case "%":
                Apply(context.Object, context.Host, s => s.ApplyPercent(), historyOnEquals: false);
                return true;
            case "±":
                Apply(context.Object, context.Host, s => s.ToggleSign(), historyOnEquals: false);
                return true;
            case "(":
            case ")":
            case ".":
                Apply(context.Object, context.Host, s => s.AppendText(normalized), historyOnEquals: false);
                return true;
        }

        if (normalized.Length == 1 && char.IsDigit(normalized[0]))
        {
            Apply(context.Object, context.Host, s => s.AppendText(normalized), historyOnEquals: false);
            return true;
        }

        return false;
    }

    /// <summary>Applies a key press to the widget state and records equals in the history.</summary>
    private void Press(WidgetObject target, IWidgetHost host, CalculatorKey key)
    {
        var state = CalculatorState.From(target, _engine);
        var before = state.LastExpression;
        state.Press(key);
        Save(target, state);
        var equals = key.Kind == CalculatorKeyKind.Equals && !state.IsError
            && state.LastExpression.Length > 0 && !string.Equals(before, state.LastExpression, StringComparison.Ordinal);
        if (equals)
        {
            CalculatorHistory.TryAppend(host, state.LastExpression, state.LastResult);
        }

        host.Invalidate();
    }

    /// <summary>Applies a state mutation and repaints.</summary>
    private void Apply(WidgetObject target, IWidgetHost host, Action<CalculatorState> mutate, bool historyOnEquals)
    {
        var state = CalculatorState.From(target, _engine);
        var before = state.LastExpression;
        mutate(state);
        Save(target, state);
        if (historyOnEquals && !state.IsError && state.LastExpression.Length > 0
            && !string.Equals(before, state.LastExpression, StringComparison.Ordinal))
        {
            CalculatorHistory.TryAppend(host, state.LastExpression, state.LastResult);
        }

        host.Invalidate();
    }

    private static void Save(WidgetObject target, CalculatorState state)
    {
        target.State ??= new JsonObject();
        state.Save(target.State);
    }

    /// <summary>Reads the key id that is currently highlighted.</summary>
    private static string? ReadPressed(WidgetObject target) =>
        target.State is { } state && state.TryGetPropertyValue("pressed", out var node) && node is not null
            ? node.ToString()
            : null;

    /// <summary>Stores the highlighted key id.</summary>
    private static void SetPressed(WidgetObject target, string? keyId)
    {
        target.State ??= new JsonObject();
        target.State["pressed"] = keyId;
    }
}
