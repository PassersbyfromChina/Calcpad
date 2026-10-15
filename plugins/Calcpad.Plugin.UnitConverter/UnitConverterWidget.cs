using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.UnitConverter;

/// <summary>
/// The iPad style unit converter as a canvas widget: two value cards with unit pickers, live two way
/// conversion, category chips, unit names in English and Chinese, a button that pulls a quantity
/// from the canvas and a button that pushes the conversion back onto the canvas.
/// </summary>
public sealed class UnitConverterWidget : IWidgetRenderer
{
    private static readonly Typeface Face =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"));

    private static readonly Typeface BoldFace =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly IUnitConverter _converter;
    private readonly Plugins.CalculatorKit.CurrencyRateTable _rates;

    /// <summary>Creates the widget around a converter implementation.</summary>
    public UnitConverterWidget(
        IUnitConverter? converter = null,
        Plugins.CalculatorKit.CurrencyRateTable? rates = null)
    {
        _converter = converter ?? new Calcpad.Core.Numerics.UnitConverter();
        _rates = rates ?? new Plugins.CalculatorKit.CurrencyRateTable();
    }

    /// <inheritdoc />
    public string WidgetType => "calcpad.unit.converter";

    /// <summary>The converter backing this widget.</summary>
    public IUnitConverter Converter => _converter;

    /// <summary>Currency rate table currencies convert through.</summary>
    public Plugins.CalculatorKit.CurrencyRateTable Rates => _rates;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) => new(400, 560);

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host) => InitializeState(target);

    /// <summary>Initialises <see cref="WidgetObject.State"/> without needing a live host.</summary>
    public void InitializeState(WidgetObject target)
    {
        target.State ??= new JsonObject();
        var model = UnitConverterModel.From(target, _converter, _rates);
        model.Reset();
        model.Save(target.State);
        target.State["pressed"] = null;
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var drawing = context.Drawing;
        var size = context.LogicalSize;
        var model = UnitConverterModel.From(context.Object, _converter, _rates);
        var picker = model.Picker.Length == 0 ? null : model.Picker;
        var layout = UnitConverterLayout.Compute(size, model.Categories, model.CategoryId, picker);
        var pressed = ReadPressed(context.Object);

        CalculatorPanel(drawing, theme, new CRect(0, 0, size.Width, size.Height));
        DrawText(drawing, "单位换算 / Unit Converter", layout.Title, 17, theme.Brush(theme.TextPrimary), TextAlign.Left, true);
        DrawText(
            drawing,
            $"{model.CurrentCategory.NameZh} · {model.CurrentCategory.Name}",
            layout.Title,
            12,
            theme.Brush(theme.TextSecondary),
            TextAlign.Right);

        foreach (var (category, rect) in layout.Chips)
        {
            var selected = string.Equals(category.Id, model.CategoryId, StringComparison.OrdinalIgnoreCase);
            var isPressed = string.Equals(pressed, $"chip:{category.Id}", StringComparison.Ordinal);
            var fill = selected ? theme.Accent : theme.SurfaceRaised;
            if (isPressed)
            {
                fill = Lighten(fill, 0.3);
            }

            Panel(drawing, rect, theme.Brush(fill), 12);
            DrawText(
                drawing,
                category.NameZh,
                rect,
                12,
                theme.Brush(selected ? theme.AccentText : theme.TextPrimary),
                TextAlign.Center,
                selected);
        }

        DrawCard(drawing, theme, layout.FromCard, layout.FromUnitButton, model, "from", pressed, "从 / From");
        DrawCard(drawing, theme, layout.ToCard, layout.ToUnitButton, model, "to", pressed, "到 / To");

        var swapFill = string.Equals(pressed, "swap", StringComparison.Ordinal)
            ? Lighten(theme.Accent, 0.3)
            : theme.Accent;
        drawing.DrawEllipse(
            theme.Brush(swapFill),
            null,
            new Point(layout.SwapButton.Center.X, layout.SwapButton.Center.Y),
            layout.SwapButton.Width / 2,
            layout.SwapButton.Height / 2);
        DrawText(drawing, "⇄", layout.SwapButton, 16, theme.Brush(theme.AccentText), TextAlign.Center, true);

        if (picker is not null)
        {
            Panel(drawing, layout.PickerArea, theme.Brush(theme.Surface), 14);
            foreach (var (unit, rect) in layout.PickerItems)
            {
                var side = picker;
                var current = string.Equals(side, "from", StringComparison.Ordinal) ? model.FromUnit : model.ToUnit;
                var selected = string.Equals(unit.Symbol, current, StringComparison.OrdinalIgnoreCase);
                var isPressed = string.Equals(pressed, $"unit:{side}:{unit.Symbol}", StringComparison.Ordinal);
                var fill = selected ? theme.Accent : theme.SurfaceRaised;
                if (isPressed)
                {
                    fill = Lighten(fill, 0.3);
                }

                Panel(drawing, rect, theme.Brush(fill), 10);
                DrawText(
                    drawing,
                    $"{unit.NameZh} {unit.Symbol}",
                    rect,
                    11.5,
                    theme.Brush(selected ? theme.AccentText : theme.TextPrimary),
                    TextAlign.Center);
            }
        }

        DrawButton(drawing, theme, layout.FromCanvasButton, "取画布 / From canvas", theme.Function, OnLightFace(theme), IsPressed(pressed, "btn:canvas"));
        DrawButton(drawing, theme, layout.InsertButton, "插入 / Insert", theme.Accent, theme.AccentText, IsPressed(pressed, "btn:insert"));
        DrawButton(drawing, theme, layout.ClearButton, "清空 / Clear", theme.SurfaceRaised, theme.TextPrimary, IsPressed(pressed, "btn:clear"));

        if (model.Hint.Length > 0)
        {
            var hintRect = new CRect(layout.Title.X, layout.FromCanvasButton.Bottom + 2, layout.Title.Width, 14);
            DrawText(drawing, model.Hint, hintRect, 10.5, theme.Brush(theme.Danger), TextAlign.Left);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Selection), 2),
                new RoundedRect(new Rect(1, 1, Math.Max(1, size.Width - 2), Math.Max(1, size.Height - 2)), 18));
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        var theme = context.Host.Theme;
        var size = context.LogicalSize;
        var model = UnitConverterModel.From(context.Object, _converter, _rates);
        var picker = model.Picker.Length == 0 ? null : model.Picker;
        var layout = UnitConverterLayout.Compute(size, model.Categories, model.CategoryId, picker);

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
                {
                    var action = HitAction(layout, picker, context.Position);
                    if (action is null)
                    {
                        return false;
                    }

                    SetPressed(context.Object, action);
                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Up:
                {
                    var action = ReadPressed(context.Object);
                    if (action is null)
                    {
                        return false;
                    }

                    SetPressed(context.Object, null);
                    if (string.Equals(HitAction(layout, picker, context.Position), action, StringComparison.Ordinal))
                    {
                        Perform(context, model, action);
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
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        var model = UnitConverterModel.From(context.Object, _converter, _rates);
        var key = context.Key ?? string.Empty;
        switch (key)
        {
            case "Back" or "BackSpace":
                model.Backspace();
                break;
            case "Escape":
                model.Picker = string.Empty;
                model.Hint = string.Empty;
                break;
            case "Return" or "Enter":
                model.Active = string.Equals(model.Active, "from", StringComparison.Ordinal) ? "to" : "from";
                model.Picker = string.Equals(model.Picker, "from", StringComparison.Ordinal) ? "to"
                    : string.Equals(model.Picker, "to", StringComparison.Ordinal) ? string.Empty : "from";
                break;
            case "Delete":
                model.Clear();
                break;
            case "Tab":
                model.Active = string.Equals(model.Active, "from", StringComparison.Ordinal) ? "to" : "from";
                break;
            default:
                if (key.Length == 1 && (char.IsDigit(key[0]) || key[0] is '.' or '-' or '+' or 'e' or 'E'))
                {
                    model.AppendText(key);
                }
                else if (key.Length == 1 && char.IsLetter(key[0]))
                {
                    model.AppendText(key);
                }
                else
                {
                    return false;
                }

                break;
        }

        Save(context.Object, model);
        context.Host.Invalidate();
        return true;
    }

    /// <summary>Performs a layout action, saving the state afterwards.</summary>
    private void Perform(WidgetPointerContext context, UnitConverterModel model, string action)
    {
        if (action.StartsWith("chip:", StringComparison.Ordinal))
        {
            model.SelectCategory(action[5..]);
        }
        else if (action.StartsWith("unit:", StringComparison.Ordinal))
        {
            var parts = action.Split(':', 3);
            if (parts.Length == 3)
            {
                model.SelectUnit(parts[1], parts[2]);
            }
        }
        else
        {
            switch (action)
            {
                case "card:from":
                    model.Active = "from";
                    model.Picker = string.Equals(model.Picker, "from", StringComparison.Ordinal) ? string.Empty : "from";
                    break;
                case "card:to":
                    model.Active = "to";
                    model.Picker = string.Equals(model.Picker, "to", StringComparison.Ordinal) ? string.Empty : "to";
                    break;
                case "swap":
                    model.Swap();
                    break;
                case "btn:clear":
                    model.Clear();
                    break;
                case "btn:canvas":
                    _ = PullFromCanvasAsync(context, model);
                    break;
                case "btn:insert":
                    _ = PushToCanvasAsync(context, model);
                    break;
            }
        }

        Save(context.Object, model);
        context.Host.Invalidate();
    }

    private static string? HitAction(UnitConverterLayout layout, string? pickerSide, CPoint point)
    {
        foreach (var (category, rect) in layout.Chips)
        {
            if (rect.Contains(point))
            {
                return $"chip:{category.Id}";
            }
        }

        if (layout.FromUnitButton.Contains(point))
        {
            return "card:from";
        }

        if (layout.ToUnitButton.Contains(point))
        {
            return "card:to";
        }

        if (layout.FromCard.Contains(point))
        {
            return "card:from";
        }

        if (layout.ToCard.Contains(point))
        {
            return "card:to";
        }

        if (CircleContains(layout.SwapButton, point))
        {
            return "swap";
        }

        if (pickerSide is { Length: > 0 })
        {
            foreach (var (unit, rect) in layout.PickerItems)
            {
                if (rect.Contains(point))
                {
                    return $"unit:{pickerSide}:{unit.Symbol}";
                }
            }
        }

        if (layout.FromCanvasButton.Contains(point))
        {
            return "btn:canvas";
        }

        if (layout.InsertButton.Contains(point))
        {
            return "btn:insert";
        }

        return layout.ClearButton.Contains(point) ? "btn:clear" : null;
    }

    private async Task PullFromCanvasAsync(WidgetPointerContext context, UnitConverterModel model)
    {
        try
        {
            var objects = await context.Host.Canvas.QueryAsync(
                new CanvasQuery { Kinds = [ObjectKinds.Math, ObjectKinds.Text], IncludeStrokes = false }).ConfigureAwait(true);
            var text = objects
                .Select(o => o switch
                {
                    MathObject m => m.Result ?? m.Expression,
                    TextObject t => t.Text,
                    _ => null
                })
                .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (text is null)
            {
                model.Hint = "画布上没有可用的数值 / nothing numeric on the canvas";
                return;
            }

            if (_converter.TryParseQuantity(text, out var value, out var unit))
            {
                model.ApplyQuantity(value, unit);
                model.Hint = string.Empty;
                return;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            {
                model.ApplyQuantity(plain, model.FromUnit);
                model.Hint = string.Empty;
                return;
            }

            model.Hint = "无法识别画布上的数值 / could not read a quantity";
        }
        catch (Exception ex)
        {
            model.Hint = "读取画布失败 / canvas read failed";
            _ = ex;
        }
    }

    private async Task PushToCanvasAsync(WidgetPointerContext context, UnitConverterModel model)
    {
        try
        {
            var expression = $"{model.FromText} {model.FromUnit} to {model.ToUnit}";
            var result = $"{model.ToText} {model.ToUnit}";
            var target = context.Object;
            var item = new MathObject
            {
                Expression = expression,
                Result = result,
                OwnerPluginId = "calcpad.unit.converter",
                Bounds = new CRect(target.Bounds.Right + 24, target.Bounds.Y, 240, 56)
            };
            await context.Host.Canvas.AddAsync(item).ConfigureAwait(true);
            await context.Host.Canvas.RevealAsync(item.Bounds).ConfigureAwait(true);
            model.Hint = "已插入画布 / inserted";
        }
        catch (Exception)
        {
            model.Hint = "插入失败 / insert failed";
        }
    }

    private static bool CircleContains(CRect rect, CPoint point)
    {
        var radius = rect.Width / 2;
        return point.DistanceTo(rect.Center) <= radius;
    }

    private static bool IsPressed(string? pressed, string action) => string.Equals(pressed, action, StringComparison.Ordinal);

    private void DrawCard(
        DrawingContext drawing,
        PluginTheme theme,
        CRect card,
        CRect unitButton,
        UnitConverterModel model,
        string side,
        string? pressed,
        string caption)
    {
        var isActive = string.Equals(model.Active, side, StringComparison.Ordinal);
        Panel(drawing, card, theme.Brush(theme.SurfaceAlt), 16);
        if (isActive)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Accent), 1.6),
                new RoundedRect(new Rect(card.X, card.Y, card.Width, card.Height), 16));
        }

        var captionRect = new CRect(card.X + 10, card.Y + 6, card.Width - 20, 16);
        DrawText(drawing, caption, captionRect, 11, theme.Brush(theme.TextSecondary), TextAlign.Left);

        var valueText = string.Equals(side, "from", StringComparison.Ordinal) ? model.FromText : model.ToText;
        var valueRect = new CRect(card.X + 10, captionRect.Bottom, card.Width - 20, unitButton.Y - captionRect.Bottom - 4);
        DrawFittedText(
            drawing,
            valueText.Length == 0 ? "0" : valueText,
            valueRect,
            30,
            14,
            theme.Brush(theme.TextPrimary),
            TextAlign.Right,
            true);

        var unit = string.Equals(side, "from", StringComparison.Ordinal) ? model.FromUnit : model.ToUnit;
        var info = model.CurrentCategory.Find(unit);
        var label = info is null ? unit : $"{info.NameZh} {info.Symbol}";
        var isPressed = string.Equals(pressed, $"card:{side}", StringComparison.Ordinal);
        var fill = isPressed ? Lighten(theme.SurfaceRaised, 0.3) : theme.SurfaceRaised;
        Panel(drawing, unitButton, theme.Brush(fill), 10);
        DrawText(drawing, label, unitButton, 11.5, theme.Brush(theme.TextPrimary), TextAlign.Center);
    }

    private static void CalculatorPanel(DrawingContext drawing, PluginTheme theme, CRect rect) =>
        drawing.DrawRectangle(theme.SurfaceBrush, null, new RoundedRect(new Rect(rect.X, rect.Y, rect.Width, rect.Height), 20));

    private static void Panel(DrawingContext drawing, CRect rect, IBrush fill, double radius) =>
        drawing.DrawRectangle(fill, null, new RoundedRect(new Rect(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height)), radius));

    private static void DrawButton(
        DrawingContext drawing,
        PluginTheme theme,
        CRect rect,
        string label,
        Color fill,
        Color text,
        bool pressed)
    {
        Panel(drawing, rect, theme.Brush(pressed ? Lighten(fill, 0.3) : fill), 12);
        DrawText(drawing, label, rect, 12, theme.Brush(text), TextAlign.Center, true);
    }

    private static void DrawText(
        DrawingContext drawing,
        string text,
        CRect area,
        double size,
        IBrush brush,
        TextAlign align,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height).Inflate(1));
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            bold ? BoldFace : Face,
            size,
            brush);
        var x = align switch
        {
            TextAlign.Left => area.X + 6,
            TextAlign.Right => area.Right - formatted.Width - 6,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        var y = area.Y + ((area.Height - formatted.Height) / 2);
        drawing.DrawText(formatted, new Point(x, y));
    }

    private static void DrawFittedText(
        DrawingContext drawing,
        string text,
        CRect area,
        double maxSize,
        double minSize,
        IBrush brush,
        TextAlign align,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        var size = Math.Max(minSize, maxSize);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        while (formatted.Width > area.Width - 8 && size > minSize)
        {
            size -= 1;
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        }

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height).Inflate(1));
        var x = align switch
        {
            TextAlign.Left => area.X + 4,
            TextAlign.Right => area.Right - formatted.Width - 4,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        drawing.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
        (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
        (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));

    private static Color OnLightFace(PluginTheme theme) =>
        theme.IsDark ? Color.Parse("#FF1C1C1E") : Color.Parse("#FFFFFFFF");

    private static void Save(WidgetObject target, UnitConverterModel model)
    {
        target.State ??= new JsonObject();
        model.Save(target.State);
    }

    private static string? ReadPressed(WidgetObject target) =>
        target.State is { } state && state.TryGetPropertyValue("pressed", out var node) && node is not null
            ? node.ToString()
            : null;

    private static void SetPressed(WidgetObject target, string? action)
    {
        target.State ??= new JsonObject();
        target.State["pressed"] = action;
    }

    private enum TextAlign
    {
        Left,
        Center,
        Right
    }
}
