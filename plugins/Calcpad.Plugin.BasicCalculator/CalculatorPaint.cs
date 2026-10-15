using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugins.CalculatorKit;

/// <summary>
/// Small drawing helpers shared by the basic and the scientific keypad. Everything is painted with
/// primitives the SDK exposes, so the widgets stay independent of the shell's control tree.
/// </summary>
public static class CalculatorPaint
{
    /// <summary>Typeface used by every keypad label.</summary>
    public static readonly Typeface Face =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"));

    /// <summary>Typeface used for operator glyphs, which look better slightly bolder.</summary>
    public static readonly Typeface BoldFace =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>Converts a canvas rectangle into an Avalonia rectangle.</summary>
    public static Rect ToRect(CRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Converts a canvas point into an Avalonia point.</summary>
    public static Point ToPoint(CPoint point) => new(point.X, point.Y);

    /// <summary>Creates a formatted text run.</summary>
    public static FormattedText Text(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);

    /// <summary>Picks the largest font size that still fits <paramref name="maxWidth"/>.</summary>
    public static FormattedText FittedText(
        string text,
        double maxWidth,
        double maxSize,
        double minSize,
        IBrush brush,
        bool bold = false)
    {
        var size = Math.Max(minSize, maxSize);
        var formatted = Text(text, size, brush, bold);
        while (formatted.Width > maxWidth && size > minSize)
        {
            size = Math.Max(minSize, size - 1);
            formatted = Text(text, size, brush, bold);
        }

        return formatted;
    }

    /// <summary>Draws text clipped to a rectangle, vertically centred.</summary>
    public static void DrawTextIn(
        DrawingContext drawing,
        string text,
        CRect area,
        double size,
        IBrush brush,
        TextAlign align = TextAlign.Center,
        bool bold = false,
        double maxSize = 0)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 1 || area.Height <= 1)
        {
            return;
        }

        using var clip = drawing.PushClip(ToRect(area).Inflate(1));
        var formatted = maxSize > size
            ? FittedText(text, area.Width - 6, maxSize, size, brush, bold)
            : FittedText(text, area.Width - 6, size, Math.Min(size, 8), brush, bold);
        var x = align switch
        {
            TextAlign.Left => area.X + 6,
            TextAlign.Right => area.Right - formatted.Width - 6,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        var y = area.Y + ((area.Height - formatted.Height) / 2);
        drawing.DrawText(formatted, new Point(x, y));
    }

    /// <summary>Fills a rounded key, using a circle when the rectangle is square.</summary>
    public static void DrawKey(DrawingContext drawing, CRect rect, IBrush fill, bool round)
    {
        if (round)
        {
            var circleRadius = Math.Min(rect.Width, rect.Height) / 2;
            drawing.DrawEllipse(fill, null, ToPoint(rect.Center), circleRadius, circleRadius);
            return;
        }

        var corner = Math.Min(Math.Min(rect.Width, rect.Height) / 2, 22);
        drawing.DrawRectangle(fill, null, new RoundedRect(ToRect(rect), corner));
    }

    /// <summary>Fills a rounded panel.</summary>
    public static void DrawPanel(DrawingContext drawing, CRect rect, IBrush fill, double radius = 18) =>
        drawing.DrawRectangle(fill, null, new RoundedRect(ToRect(rect), radius));

    /// <summary>Blends a colour towards white, used for the pressed key highlight.</summary>
    public static Color Lighten(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
            (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
            (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));
    }

    /// <summary>Colour used for text drawn on light key faces.</summary>
    public static Color OnLightFace(PluginTheme theme) => theme.IsDark ? Color.Parse("#FF1C1C1E") : Color.Parse("#FFFFFFFF");

    /// <summary>Where a text run is anchored inside its rectangle.</summary>
    public enum TextAlign
    {
        /// <summary>Left edge.</summary>
        Left,

        /// <summary>Horizontal centre.</summary>
        Center,

        /// <summary>Right edge.</summary>
        Right
    }
}

/// <summary>Key face colours, following the iPad palette exposed by <see cref="PluginTheme"/>.</summary>
public static class CalculatorColors
{
    /// <summary>Fill and text colour of a key, optionally lightened because it is pressed.</summary>
    public static (Color Fill, Color Text) For(CalculatorKey key, PluginTheme theme, bool pressed, bool active = false)
    {
        var (fill, text) = key.Kind switch
        {
            CalculatorKeyKind.Operator or CalculatorKeyKind.Equals => (theme.Accent, theme.AccentText),
            CalculatorKeyKind.Clear or CalculatorKeyKind.Sign or CalculatorKeyKind.Percent
                or CalculatorKeyKind.Backspace or CalculatorKeyKind.Second or CalculatorKeyKind.Angle =>
                (theme.Function, CalculatorPaint.OnLightFace(theme)),
            CalculatorKeyKind.Digit or CalculatorKeyKind.Dot => (theme.SurfaceRaised, theme.TextPrimary),
            _ => (theme.SurfaceAlt, theme.TextPrimary)
        };

        if (active)
        {
            fill = theme.Accent;
            text = theme.AccentText;
        }

        return (pressed ? CalculatorPaint.Lighten(fill, 0.3) : fill, text);
    }
}

/// <summary>
/// Bridges the calculators to the history plugin. The history plugin owns the storage; every other
/// plugin only sends a command, so the calculator family has no compile time dependency on it.
/// </summary>
public static class CalculatorHistory
{
    /// <summary>Command id declared by the history plugin for appending an entry.</summary>
    public const string AppendCommandId = "calcpad.history.append";

    /// <summary>Sends one calculation to the history plugin, ignoring hosts that do not provide it.</summary>
    public static void TryAppend(IWidgetHost host, string expression, string result, string? error = null)
    {
        if (host is null || string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["expression"] = expression,
            ["result"] = result ?? string.Empty
        };
        if (!string.IsNullOrEmpty(error))
        {
            arguments["error"] = error;
        }

        try
        {
            var pending = host.InvokeCommandAsync(AppendCommandId, arguments);
            if (!pending.IsCompletedSuccessfully)
            {
                Observe(pending);
            }
        }
        catch (Exception)
        {
            // Recording history is best effort: a missing history plugin or a denied permission
            // must never break the keypad.
        }
    }

    private static async void Observe(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignored on purpose
        }
    }
}
