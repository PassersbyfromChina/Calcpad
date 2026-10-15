namespace Calcpad.Core.Numerics;

/// <summary>
/// The "natural unit" note Math Notes shows next to a bare quantity: <c>4 in</c> reads as
/// <c>= 10.16 cm</c>, <c>18 m</c> reads as <c>= 59 ft 0.66 in</c> and <c>15 miles</c> as
/// <c>= 24.14 km</c>. Metric switches to imperial and back, durations are decomposed, currencies are
/// reported in US dollars.
/// </summary>
public static class NaturalUnits
{
    private static readonly string[] ImperialLengths = ["in", "ft", "yd", "mi", "nmi", "′", "″", "尺", "寸", "里"];

    private static readonly string[] BinaryData = ["KiB", "MiB", "GiB", "TiB"];

    /// <summary>
    /// Describes the quantity in its natural counterpart unit, or null when there is no useful note.
    /// The returned text already carries the leading <c>= </c> so a surface can print it verbatim.
    /// </summary>
    public static string? Describe(Quantity quantity, int precision = 6)
    {
        if (!quantity.HasUnit)
        {
            return null;
        }

        var text = Core(quantity, precision);
        return string.IsNullOrEmpty(text) ? null : $"= {text}";
    }

    private static string? Core(Quantity quantity, int precision)
    {
        var dimension = quantity.Unit.Dimension;
        var symbol = quantity.Unit.Symbol;
        var digits = Guards.Precision(precision);

        if (dimension == Dimension.OfLength)
        {
            var meters = quantity.Value * quantity.Unit.Factor;
            if (ImperialLengths.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            {
                return meters switch
                {
                    var m when Math.Abs(m) < 1 => $"{NumberFormatter.Format(m * 100, digits)} cm",
                    var m when Math.Abs(m) >= 1000 => $"{NumberFormatter.Format(m / 1000, digits)} km",
                    var m => $"{NumberFormatter.Format(m, digits)} m"
                };
            }

            return FeetAndInches(meters, digits);
        }

        if (dimension == Dimension.OfMass)
        {
            var kilograms = quantity.Value * quantity.Unit.Factor;
            return string.Equals(symbol, "kg", StringComparison.OrdinalIgnoreCase)
                ? $"{NumberFormatter.Format(kilograms / 0.45359237, digits)} lb"
                : string.Equals(symbol, "g", StringComparison.OrdinalIgnoreCase)
                    ? $"{NumberFormatter.Format(kilograms * 1000 / 0.45359237, digits)} lb"
                    : $"{NumberFormatter.Format(kilograms, digits)} kg";
        }

        if (dimension == Dimension.OfTime)
        {
            return Duration(quantity.Value * quantity.Unit.Factor, digits);
        }

        if (dimension == Dimension.OfTemperature)
        {
            var kelvin = (quantity.Value * quantity.Unit.Factor) + quantity.Unit.Offset;
            return string.Equals(symbol, "°F", StringComparison.OrdinalIgnoreCase)
                ? $"{NumberFormatter.Format(kelvin - 273.15, digits)} °C"
                : $"{NumberFormatter.Format(((kelvin - 273.15) * 9 / 5) + 32, digits)} °F";
        }

        if (dimension == Dimension.OfLength.Subtract(Dimension.OfTime))
        {
            var metersPerSecond = quantity.Value * quantity.Unit.Factor;
            return string.Equals(symbol, "km/h", StringComparison.OrdinalIgnoreCase)
                ? $"{NumberFormatter.Format(metersPerSecond / 0.44704, digits)} mph"
                : $"{NumberFormatter.Format(metersPerSecond * 3.6, digits)} km/h";
        }

        if (dimension == Dimension.OfLength.Scale(2))
        {
            var squareMeters = quantity.Value * quantity.Unit.Factor;
            return $"{NumberFormatter.Format(squareMeters * 10.763910416709722, digits)} ft²";
        }

        if (dimension == Dimension.OfLength.Scale(3))
        {
            var liters = quantity.Value * quantity.Unit.Factor;
            return $"{NumberFormatter.Format(liters / 3.785411784, digits)} gal";
        }

        if (dimension == Dimension.OfCurrency)
        {
            var dollars = quantity.Value * quantity.Unit.Factor;
            if (string.Equals(symbol, "$", StringComparison.Ordinal))
            {
                return null;
            }

            return $"${NumberFormatter.Format(dollars, digits)}";
        }

        if (dimension == Dimension.OfData)
        {
            var bytes = quantity.Value * quantity.Unit.Factor;
            if (BinaryData.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            {
                return $"{NumberFormatter.Format(bytes / 1e6, digits)} MB";
            }

            return Math.Abs(bytes) >= 1048576
                ? $"{NumberFormatter.Format(bytes / 1048576, digits)} MiB"
                : $"{NumberFormatter.Format(bytes / 1024, digits)} KiB";
        }

        return null;
    }

    private static string FeetAndInches(double meters, int digits)
    {
        var totalInches = meters / 0.0254;
        if (Math.Abs(totalInches) < 12)
        {
            return $"{NumberFormatter.Format(totalInches, digits)} in";
        }

        var feet = Math.Truncate(totalInches / 12);
        var inches = totalInches - (feet * 12);
        if (Math.Abs(inches) < 0.005)
        {
            return $"{NumberFormatter.Format(feet, digits)} ft";
        }

        return $"{NumberFormatter.Format(feet, digits)} ft {NumberFormatter.Format(inches, 2)} in";
    }

    private static string Duration(double seconds, int digits)
    {
        var negative = seconds < 0;
        var total = Math.Abs(seconds);
        var hours = Math.Truncate(total / 3600);
        var minutes = Math.Truncate((total - (hours * 3600)) / 60);
        var rest = total - (hours * 3600) - (minutes * 60);
        var parts = new List<string>();
        if (hours > 0)
        {
            parts.Add($"{NumberFormatter.Format(hours, digits)} h");
        }

        if (minutes > 0)
        {
            parts.Add($"{NumberFormatter.Format(minutes, digits)} min");
        }

        if (rest > 0.0005 || parts.Count == 0)
        {
            parts.Add($"{NumberFormatter.Format(rest, digits)} s");
        }

        return (negative ? "-" : string.Empty) + string.Join(' ', parts);
    }
}
