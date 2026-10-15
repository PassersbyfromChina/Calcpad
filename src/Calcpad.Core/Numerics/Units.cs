namespace Calcpad.Core.Numerics;

/// <summary>
/// Exponent vector of a physical dimension. Length, mass, time, temperature, data and angle are
/// tracked independently; every other category (area, volume, speed, energy, ...) is derived from
/// them, so multiplying <c>1 m</c> by <c>1 m</c> yields <c>1 m^2</c> and <c>1 m / 1 s</c> yields
/// <c>1 m/s</c>.
/// </summary>
public readonly struct Dimension : IEquatable<Dimension>
{
    private const double Epsilon = 1e-9;

    /// <summary>Creates a dimension vector, normalising values that are numerically zero.</summary>
    public Dimension(
        double length,
        double mass,
        double time,
        double temperature,
        double data,
        double angle,
        double currency = 0)
    {
        Length = Snap(length);
        Mass = Snap(mass);
        Time = Snap(time);
        Temperature = Snap(temperature);
        Data = Snap(data);
        Angle = Snap(angle);
        Currency = Snap(currency);
    }

    /// <summary>Exponent of the length dimension.</summary>
    public double Length { get; }

    /// <summary>Exponent of the mass dimension.</summary>
    public double Mass { get; }

    /// <summary>Exponent of the time dimension.</summary>
    public double Time { get; }

    /// <summary>Exponent of the thermodynamic temperature dimension.</summary>
    public double Temperature { get; }

    /// <summary>Exponent of the digital information dimension.</summary>
    public double Data { get; }

    /// <summary>Exponent of the plane angle dimension.</summary>
    public double Angle { get; }

    /// <summary>Exponent of the currency dimension.</summary>
    public double Currency { get; }

    /// <summary>The dimensionless vector.</summary>
    public static Dimension None => default;

    /// <summary>Length.</summary>
    public static Dimension OfLength => new(1, 0, 0, 0, 0, 0);

    /// <summary>Mass.</summary>
    public static Dimension OfMass => new(0, 1, 0, 0, 0, 0);

    /// <summary>Time.</summary>
    public static Dimension OfTime => new(0, 0, 1, 0, 0, 0);

    /// <summary>Thermodynamic temperature.</summary>
    public static Dimension OfTemperature => new(0, 0, 0, 1, 0, 0);

    /// <summary>Digital information.</summary>
    public static Dimension OfData => new(0, 0, 0, 0, 1, 0);

    /// <summary>Plane angle.</summary>
    public static Dimension OfAngle => new(0, 0, 0, 0, 0, 1);

    /// <summary>Currency.</summary>
    public static Dimension OfCurrency => new(0, 0, 0, 0, 0, 0, 1);

    /// <summary>True when every exponent is zero, i.e. the value is a plain number.</summary>
    public bool IsZero =>
        Length == 0 && Mass == 0 && Time == 0 && Temperature == 0 && Data == 0 && Angle == 0 && Currency == 0;

    /// <summary>Adds two dimension vectors (multiplication of the quantities).</summary>
    public Dimension Add(Dimension other) => new(
        Length + other.Length,
        Mass + other.Mass,
        Time + other.Time,
        Temperature + other.Temperature,
        Data + other.Data,
        Angle + other.Angle,
        Currency + other.Currency);

    /// <summary>Subtracts two dimension vectors (division of the quantities).</summary>
    public Dimension Subtract(Dimension other) => new(
        Length - other.Length,
        Mass - other.Mass,
        Time - other.Time,
        Temperature - other.Temperature,
        Data - other.Data,
        Angle - other.Angle,
        Currency - other.Currency);

    /// <summary>Scales every exponent, used by powers and roots.</summary>
    public Dimension Scale(double factor) => new(
        Length * factor,
        Mass * factor,
        Time * factor,
        Temperature * factor,
        Data * factor,
        Angle * factor,
        Currency * factor);

    /// <summary>True when every exponent is a whole number, i.e. the dimension can be printed as a unit.</summary>
    public bool IsIntegral => IsWhole(Length) && IsWhole(Mass) && IsWhole(Time)
        && IsWhole(Temperature) && IsWhole(Data) && IsWhole(Angle) && IsWhole(Currency);

    /// <inheritdoc />
    public bool Equals(Dimension other) =>
        Math.Abs(Length - other.Length) < Epsilon &&
        Math.Abs(Mass - other.Mass) < Epsilon &&
        Math.Abs(Time - other.Time) < Epsilon &&
        Math.Abs(Temperature - other.Temperature) < Epsilon &&
        Math.Abs(Data - other.Data) < Epsilon &&
        Math.Abs(Angle - other.Angle) < Epsilon &&
        Math.Abs(Currency - other.Currency) < Epsilon;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Dimension other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Quantize(Length), Quantize(Mass), Quantize(Time), Quantize(Temperature), Quantize(Data),
        Quantize(Angle), Quantize(Currency));

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Dimension left, Dimension right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Dimension left, Dimension right) => !left.Equals(right);

    /// <summary>Human readable vector such as <c>L^1·T^-1</c>; empty for dimensionless values.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        Append(parts, "L", Length);
        Append(parts, "M", Mass);
        Append(parts, "T", Time);
        Append(parts, "Θ", Temperature);
        Append(parts, "D", Data);
        Append(parts, "A", Angle);
        Append(parts, "¤", Currency);
        return parts.Count == 0 ? "1" : string.Join("·", parts);

        static void Append(List<string> target, string symbol, double exponent)
        {
            if (exponent == 0)
            {
                return;
            }

            target.Add(Math.Abs(exponent - 1) < Epsilon ? symbol : $"{symbol}^{Format(exponent)}");
        }

        static string Format(double value) => Math.Abs(value - Math.Round(value)) < Epsilon
            ? ((int)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double Snap(double value) => Math.Abs(value) < 1e-12 ? 0 : value;

    private static bool IsWhole(double value) => Math.Abs(value - Math.Round(value)) < Epsilon;

    private static int Quantize(double value) => (int)Math.Round(Snap(value) * 1e6);
}

/// <summary>
/// The unit attached to a value: a scale <see cref="Factor"/> to the SI base of
/// <see cref="Dimension"/>, an optional additive <see cref="Offset"/> (temperatures) and the
/// printed <see cref="Symbol"/>.
/// </summary>
public sealed class UnitExpr
{
    private readonly (string Symbol, double Exponent)[] _terms;

    private UnitExpr(
        Dimension dimension,
        double factor,
        double offset,
        (string, double)[] terms,
        string symbol,
        bool prefixSymbol = false)
    {
        Dimension = dimension;
        Factor = factor;
        Offset = offset;
        _terms = terms;
        Symbol = symbol;
        PrefixSymbol = prefixSymbol;
    }

    /// <summary>Physical dimension of the value.</summary>
    public Dimension Dimension { get; }

    /// <summary>Multiplier that converts a value in this unit to the base unit of the dimension.</summary>
    public double Factor { get; }

    /// <summary>Additive offset applied after scaling; non-zero only for affine units such as °C.</summary>
    public double Offset { get; }

    /// <summary>True for affine units, which cannot take part in multiplication or powers.</summary>
    public bool IsAffine => Offset != 0;

    /// <summary>True when the value carries no unit at all.</summary>
    public bool IsNone => _terms.Length == 0;

    /// <summary>The unit terms that make up the printed symbol, in display order.</summary>
    public IReadOnlyList<(string Symbol, double Exponent)> Terms => _terms;

    /// <summary>Printed unit symbol, for example <c>m</c>, <c>cm</c>, <c>m/s</c> or <c>m^2</c>.</summary>
    public string Symbol { get; }

    /// <summary>
    /// True when the symbol is written before the number, as currencies are: <c>$27</c> rather than
    /// <c>27 $</c>.
    /// </summary>
    public bool PrefixSymbol { get; }

    /// <summary>The unit of a plain number.</summary>
    public static UnitExpr None { get; } = new(Dimension.None, 1, 0, [], string.Empty);

    /// <summary>Builds the unit expression of a single catalogue unit.</summary>
    public static UnitExpr FromUnit(UnitInfo unit, Dimension dimension, bool prefixSymbol = false) =>
        new(dimension, unit.Factor, unit.Offset, [(unit.Symbol, 1d)], unit.Symbol, prefixSymbol);

    /// <summary>Multiplies two units; affine units are rejected.</summary>
    public static UnitExpr Multiply(UnitExpr left, UnitExpr right)
    {
        RejectAffine(left, right);
        return Combine(left, right, 1);
    }

    /// <summary>Divides two units; affine units are rejected.</summary>
    public static UnitExpr Divide(UnitExpr left, UnitExpr right)
    {
        RejectAffine(left, right);
        return Combine(left, right, -1);
    }

    /// <summary>Raises a unit to a power; affine units and fractional powers of odd dimensions are rejected.</summary>
    public static UnitExpr Pow(UnitExpr unit, double exponent)
    {
        if (unit.IsNone)
        {
            return None;
        }

        if (unit.IsAffine)
        {
            throw new CalcException(Errors.AffineOperation);
        }

        var terms = new List<(string, double)>();
        foreach (var (symbol, power) in unit._terms)
        {
            var scaled = power * exponent;
            if (Math.Abs(scaled) > 1e-12)
            {
                terms.Add((symbol, scaled));
            }
        }

        var dimension = unit.Dimension.Scale(exponent);
        if (!dimension.IsIntegral)
        {
            throw new CalcException(Errors.FractionalUnitPower(unit.Symbol));
        }

        return new UnitExpr(dimension, Math.Pow(unit.Factor, exponent), 0, terms.ToArray(), Render(terms));
    }

    /// <summary>Square root of a unit, allowed only when every exponent stays whole.</summary>
    public static UnitExpr Sqrt(UnitExpr unit) => Pow(unit, 0.5);

    /// <summary>True when the two units describe the same physical dimension.</summary>
    public bool IsCompatibleWith(UnitExpr other) => Dimension == other.Dimension;

    /// <summary>Converts <paramref name="value"/> from this unit into <paramref name="target"/>.</summary>
    public double ConvertTo(double value, UnitExpr target)
    {
        if (!IsCompatibleWith(target))
        {
            throw new CalcException(Errors.IncompatibleUnits(Symbol, target.Symbol));
        }

        if (IsAffine || target.IsAffine)
        {
            var baseValue = (value * Factor) + Offset;
            return (baseValue - target.Offset) / target.Factor;
        }

        return value * Factor / target.Factor;
    }

    /// <inheritdoc />
    public override string ToString() => IsNone ? "1" : Symbol;

    private static UnitExpr Combine(UnitExpr left, UnitExpr right, double sign)
    {
        var terms = new List<(string Symbol, double Exponent)>(left._terms);
        foreach (var (symbol, exponent) in right._terms)
        {
            var index = terms.FindIndex(t => string.Equals(t.Symbol, symbol, StringComparison.Ordinal));
            if (index >= 0)
            {
                terms[index] = (symbol, terms[index].Exponent + (exponent * sign));
            }
            else
            {
                terms.Add((symbol, exponent * sign));
            }
        }

        terms.RemoveAll(t => Math.Abs(t.Exponent) < 1e-12);
        var dimension = sign > 0 ? left.Dimension.Add(right.Dimension) : left.Dimension.Subtract(right.Dimension);
        var factor = sign > 0 ? left.Factor * right.Factor : left.Factor / right.Factor;
        return new UnitExpr(dimension, factor, 0, terms.ToArray(), Render(terms));
    }

    private static void RejectAffine(UnitExpr left, UnitExpr right)
    {
        if (left.IsAffine || right.IsAffine)
        {
            throw new CalcException(Errors.AffineOperation);
        }
    }

    private static string Render(List<(string Symbol, double Exponent)> terms)
    {
        if (terms.Count == 0)
        {
            return string.Empty;
        }

        var positive = new List<string>();
        var negative = new List<string>();
        foreach (var (symbol, exponent) in terms)
        {
            if (exponent > 0)
            {
                positive.Add(RenderTerm(symbol, exponent));
            }
            else
            {
                negative.Add(RenderTerm(symbol, -exponent));
            }
        }

        var numerator = positive.Count == 0 ? "1" : string.Join("·", positive);
        if (negative.Count == 0)
        {
            return numerator;
        }

        var denominator = string.Join("·", negative);
        return negative.Count == 1 ? $"{numerator}/{denominator}" : $"{numerator}/({denominator})";
    }

    private static string RenderTerm(string symbol, double exponent) =>
        Math.Abs(exponent - 1) < 1e-12
            ? symbol
            : $"{symbol}^{exponent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
}
