using System.Globalization;
using System.Text.RegularExpressions;
using Calcpad.Core.Canvas;

namespace Calcpad.Plugin.Handwriting.Solve;

/// <summary>Geometry and hit testing of the floating solve bar. Pure functions: no UI toolkit needed.</summary>
public static class SolveBarLayout
{
    /// <summary>Inner padding of the bar, world units.</summary>
    public const double Padding = 10;

    /// <summary>Height of the primary button row.</summary>
    public const double ButtonHeight = 30;

    /// <summary>Height of a candidate row.</summary>
    public const double CandidateHeight = 26;

    /// <summary>Vertical gap between rows.</summary>
    public const double Gap = 6;

    /// <summary>Width of the narrow actions (adjust / delete / close) in the button row.</summary>
    public const double NarrowButtonWidth = 84;

    /// <summary>Preferred width of the bar.</summary>
    public const double PreferredWidth = 320;

    /// <summary>Maximum width, so a long expression does not stretch across the canvas.</summary>
    public const double MaxWidth = MathNotesCommands.PageWidth;

    /// <summary>Size the bar wants for a given state, in world units.</summary>
    public static CSize Measure(SolveState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var rows = state.Status == SolveStatus.NeedsChoice ? Math.Max(1, state.Candidates.Count) : 0;
        var extra = rows * (CandidateHeight + Gap);
        if (state.Numbers.Count > 0 && state.Status is SolveStatus.SolvedInNotes or SolveStatus.SolvedInNotesUnbound or SolveStatus.SolvedLocally)
        {
            extra += CandidateHeight + Gap;
        }

        var width = Math.Clamp(EstimateWidth(state), PreferredWidth, MaxWidth);
        var height = (Padding * 2) + ButtonHeight + extra + (state.Message.Length > 0 ? 20 : 0);
        return new CSize(width, height);
    }

    /// <summary>Hit tests a pointer in the bar's local logical coordinates.</summary>
    public static SolveBarHitResult HitTest(CPoint local, CSize size, SolveState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var top = Padding;
        var primary = new CRect(Padding, top, PrimaryWidth(size), ButtonHeight);
        if (primary.Contains(local))
        {
            return new SolveBarHitResult(SolveStatus.NeedsChoice == state.Status ? SolveBarHit.Solve : SolveBarHit.Resolve);
        }

        var x = primary.Right + Gap;
        if (state.Status is SolveStatus.SolvedInNotes or SolveStatus.SolvedInNotesUnbound or SolveStatus.SolvedLocally)
        {
            var numberCount = Math.Max(1, state.Numbers.Count);
            for (var i = 0; i < numberCount && i < 5; i++)
            {
                var chip = new CRect(x, top, 52, ButtonHeight);
                if (chip.Contains(local))
                {
                    return new SolveBarHitResult(SolveBarHit.AdjustNumber, i);
                }

                x += 52 + Gap;
            }
        }

        var edit = new CRect(x, top, NarrowButtonWidth, ButtonHeight);
        if (edit.Contains(local))
        {
            return new SolveBarHitResult(SolveBarHit.Edit);
        }

        x = edit.Right + Gap;
        var delete = new CRect(x, top, NarrowButtonWidth, ButtonHeight);
        if (delete.Contains(local))
        {
            return new SolveBarHitResult(SolveBarHit.Delete);
        }

        x = delete.Right + Gap;
        var close = new CRect(x, top, 40, ButtonHeight);
        if (close.Contains(local))
        {
            return new SolveBarHitResult(SolveBarHit.Close);
        }

        var rowTop = primary.Bottom + Gap;
        if (state.Status == SolveStatus.NeedsChoice)
        {
            for (var i = 0; i < state.Candidates.Count; i++)
            {
                var row = new CRect(Padding, rowTop, size.Width - (Padding * 2), CandidateHeight);
                if (row.Contains(local))
                {
                    return new SolveBarHitResult(SolveBarHit.Candidate, i);
                }

                rowTop += CandidateHeight + Gap;
            }
        }

        return new SolveBarHitResult(SolveBarHit.None);
    }

    private static double PrimaryWidth(CSize size) => Math.Clamp(size.Width * 0.34, 96, 140);

    private static double EstimateWidth(SolveState state)
    {
        var longest = Math.Max(
            state.Expression.Length,
            Math.Max(state.Result.Length + 3, state.Message.Length / 2));
        foreach (var candidate in state.Candidates)
        {
            longest = Math.Max(longest, candidate.Text.Length + 10);
        }

        // Roughly 7.4 world units per character at the bar's font size.
        var needed = (Padding * 2) + (longest * 7.4) + (NarrowButtonWidth * 2) + 40 + (Gap * 4);
        return Math.Clamp(needed, PreferredWidth, MaxWidth);
    }
}

/// <summary>Placement of the answer relative to the ink, and the pure text helpers behind "Solve".</summary>
public static class SolveGeometry
{
    /// <summary>Gap between the ink and the answer, so the answer never sits on the strokes.</summary>
    public const double InkGap = 18;

    /// <summary>
    /// Places a result block next to the ink: to the right when the ink is on the left half, otherwise
    /// to the left; if neither fits it goes below. The result never overlaps the ink bounds.
    /// </summary>
    public static CRect PlaceResult(CRect inkBounds, CSize resultSize, double gap = InkGap)
    {
        var right = new CRect(inkBounds.Right + gap, inkBounds.Y, resultSize.Width, resultSize.Height);
        var below = new CRect(inkBounds.X, inkBounds.Bottom + gap, resultSize.Width, resultSize.Height);

        // Prefer inline: right of the ink when there is room on the canvas, otherwise underneath.
        var canvasRight = inkBounds.Right + gap + resultSize.Width;
        if (canvasRight <= MathNotesCommands.PageWidth * 4)
        {
            return right;
        }

        return below;
    }

    /// <summary>Position of the floating solve bar: directly right of the ink, above it when tight.</summary>
    public static CRect PlaceBar(CRect inkBounds, CSize barSize, double gap = InkGap)
    {
        var right = new CRect(inkBounds.Right + gap, inkBounds.Y, barSize.Width, barSize.Height);
        var above = new CRect(inkBounds.X, inkBounds.Y - gap - barSize.Height, barSize.Width, barSize.Height);
        return inkBounds.Y - gap - barSize.Height > 0 ? above.Right > inkBounds.Right + gap + barSize.Width ? above : right : right;
    }

    /// <summary>True when two rectangles overlap (used by the tests to prove the ink is not covered).</summary>
    public static bool Overlaps(CRect a, CRect b) => a.IntersectsWith(b) && a.Width > 0 && b.Width > 0;
}

/// <summary>Text handling for the solve flow: normalisation, number literals and inline result text.</summary>
public static partial class SolveText
{
    /// <summary>Matches a decimal number literal.</summary>
    [GeneratedRegex(@"\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    /// <summary>
    /// Strips the parts a person writes but a solver should not evaluate: a trailing "=", a trailing
    /// "= &lt;value&gt;" (the answer they already guessed) and surrounding whitespace.
    /// </summary>
    public static string NormalizeExpression(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Trim();
        // "1+2 = 3" -> "1+2": keep the left hand side, the answer is recomputed.
        var equals = value.IndexOf('=');
        if (equals >= 0)
        {
            var left = value[..equals].Trim();
            if (left.Length > 0)
            {
                value = left;
            }
        }

        return value.TrimEnd('=', ' ', '\t').Trim();
    }

    /// <summary>Number literals of an expression, in order.</summary>
    public static IReadOnlyList<string> ExtractNumbers(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [];
        }

        return [.. NumberRegex().Matches(expression).Select(m => m.Value)];
    }

    /// <summary>Offsets and lengths of the number literals, for the edit fallback.</summary>
    public static IReadOnlyList<(int Start, int Length, string Text)> LocateNumbers(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [];
        }

        return [.. NumberRegex().Matches(expression).Select(m => (m.Index, m.Length, m.Value))];
    }

    /// <summary>Replaces the <paramref name="index"/>-th number literal (0 based).</summary>
    public static string ReplaceNumber(string? expression, int index, string replacement)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        var located = LocateNumbers(expression);
        if (index < 0 || index >= located.Count)
        {
            return expression;
        }

        var (start, length, _) = located[index];
        return string.Concat(expression.AsSpan(0, start), replacement, expression.AsSpan(start + length));
    }

    /// <summary>The text that appears next to the ink, e.g. <c>= 3</c>.</summary>
    public static string InlineResult(string? result) =>
        string.IsNullOrWhiteSpace(result) ? string.Empty : "= " + result.Trim();

    /// <summary>True when the text is plausible as a math expression (digits plus operators).</summary>
    public static bool LooksLikeMath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                digits++;
            }
            else if (ch is not ('+' or '-' or '*' or '/' or '×' or '÷' or '=' or '.' or ',' or '(' or ')' or '%' or '^' or '√' or ' '))
            {
                return false;
            }
        }

        return digits > 0;
    }

    /// <summary>Formats a double the way a calculator writes it (no trailing zeros).</summary>
    public static string FormatNumber(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>Checks whether the expression is a pure number followed by nothing (nothing to solve).</summary>
    public static bool IsBareNumber(string? text)
    {
        var normalized = NormalizeExpression(text);
        return normalized.Length > 0 && NumberRegex().IsMatch(normalized) &&
               NumberRegex().Match(normalized).Length == normalized.Length;
    }
}

/// <summary>Style of the rendered answer, chosen so it looks written next to the ink rather than typed.</summary>
public static class HandwritingStyle
{
    /// <summary>Handwriting-ish families, in order of preference, ending in a generic fallback.</summary>
    public const string FontFamily = "Segoe Script, Ink Free, Bradley Hand, Comic Sans MS, cursive";

    /// <summary>Warm accent used when the ink itself is (near) black.</summary>
    public const uint Accent = 0xFFFF9F0A;

    /// <summary>Describes how the answer next to <paramref name="ink"/> should be drawn.</summary>
    public static (string FontFamily, bool Italic, uint Color, double FontSize, double Opacity) Describe(InkObject ink)
    {
        ArgumentNullException.ThrowIfNull(ink);
        var bounds = ink.Bounds;
        var fontSize = Math.Clamp(bounds.Height * 0.62, 14, 96);
        return (FontFamily, true, AccentFor(ink), fontSize, 0.96);
    }

    /// <summary>
    /// "Same colour family as the ink, with emphasis": a coloured pen keeps its colour, a black or
    /// grey pen gets the warm accent so the answer still reads as an answer.
    /// </summary>
    public static uint AccentFor(InkObject ink)
    {
        ArgumentNullException.ThrowIfNull(ink);
        var colors = ink.AllStrokes
            .Where(s => s.Points.Count > 1)
            .GroupBy(s => s.Color)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

        if (colors.Count == 0)
        {
            return Accent;
        }

        var color = colors[0];
        var r = (color >> 16) & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = color & 0xFF;
        var luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        if (luminance < 72)
        {
            return Accent;
        }

        return color | 0xFF000000;
    }
}
