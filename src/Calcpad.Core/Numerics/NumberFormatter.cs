using System.Globalization;
using System.Text;

namespace Calcpad.Core.Numerics;

/// <summary>
/// Number to text conversion used by every result, so the keypad, Math Notes and the converter all
/// print the same value. Values are rounded to a requested number of significant digits and never
/// show a trailing <c>.0</c> or a negative zero.
/// </summary>
public static class NumberFormatter
{
    /// <summary>Formats <paramref name="value"/> with up to <paramref name="precision"/> significant digits.</summary>
    public static string Format(double value, int precision)
    {
        var digits = Guards.Precision(precision);
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "∞" : "-∞";
        }

        if (value == 0)
        {
            return "0";
        }

        var magnitude = Math.Abs(value);
        if (magnitude < 1e15 && Math.Abs(value - Math.Round(value)) < 1e-9)
        {
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        var text = value.ToString("G" + digits, CultureInfo.InvariantCulture);
        return NormalizeExponent(text);
    }

    /// <summary>Formats with thousands separators, as the calculator display shows while typing.</summary>
    public static string FormatGrouped(double value, int precision)
    {
        var text = Format(value, precision);
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        if (exponentIndex >= 0)
        {
            return text;
        }

        var negative = text.StartsWith('-');
        if (negative)
        {
            text = text[1..];
        }

        var dot = text.IndexOf('.');
        var integer = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? string.Empty : text[dot..];
        if (integer.Length > 3)
        {
            var builder = new StringBuilder(integer.Length + (integer.Length / 3));
            var lead = integer.Length % 3;
            if (lead > 0)
            {
                builder.Append(integer, 0, lead);
            }

            for (var i = lead; i < integer.Length; i += 3)
            {
                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(integer, i, 3);
            }

            integer = builder.ToString();
        }

        return (negative ? "-" : string.Empty) + integer + fraction;
    }

    /// <summary>
    /// Splits a formatted quantity such as <c>3.2 m</c> back into its numeric part and unit text.
    /// Used when a value stored in <see cref="EvaluationContext.Variables"/> is read again.
    /// </summary>
    public static bool TrySplitQuantity(string? text, out double value, out string unit)
    {
        value = 0;
        unit = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        var end = 0;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] is '+' or '-' or '.' or 'e' or 'E'
            || ((span[end] == '+' || span[end] == '-') && end > 0 && (span[end - 1] is 'e' or 'E'))))
        {
            end++;
        }

        if (end == 0 || !double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        unit = span[end..].Trim();
        return true;
    }

    private static string NormalizeExponent(string text)
    {
        var index = text.IndexOfAny(['e', 'E']);
        if (index < 0)
        {
            return text;
        }

        var mantissa = text[..index];
        var exponent = text[(index + 1)..];
        var sign = string.Empty;
        if (exponent.StartsWith('+'))
        {
            exponent = exponent[1..];
        }
        else if (exponent.StartsWith('-'))
        {
            sign = "-";
            exponent = exponent[1..];
        }

        exponent = exponent.TrimStart('0');
        if (exponent.Length == 0)
        {
            exponent = "0";
        }

        if (mantissa.EndsWith('.'))
        {
            mantissa = mantissa[..^1];
        }

        return $"{mantissa}e{sign}{exponent}";
    }
}
