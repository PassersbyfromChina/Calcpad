namespace Calcpad.Core.Numerics;

/// <summary>One callable function: name, accepted argument count and implementation.</summary>
internal sealed record FunctionSpec(
    string Name,
    int MinArgs,
    int MaxArgs,
    Func<Quantity[], AngleUnit, Quantity> Invoke);

/// <summary>
/// The built in function library. Trigonometry honours <see cref="EvaluationContext.AngleUnit"/>
/// unless the argument already carries an angle unit such as <c>°</c>; roots and powers keep units
/// consistent; every entry is domain and overflow guarded.
/// </summary>
internal static class Functions
{
    private const int MaxCombinatoricLoop = 5000;

    private static readonly Lock RandomLock = new();
    private static readonly Random RandomSource = new();

    private static readonly Dictionary<string, FunctionSpec> Table = BuildTable();

    /// <summary>Every function name, in catalogue order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Table.Keys];

    /// <summary>Looks a function up by name, case insensitively.</summary>
    public static bool TryGet(string name, out FunctionSpec spec) => Table.TryGetValue(name, out spec!);

    private static Dictionary<string, FunctionSpec> BuildTable()
    {
        var table = new Dictionary<string, FunctionSpec>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, int min, int max, Func<Quantity[], AngleUnit, Quantity> invoke) =>
            table[name] = new FunctionSpec(name, min, max, invoke);

        void Unary(string name, Func<double, double> f) => Add(name, 1, 1,
            (a, _) => Number(f(Dimensionless(a[0], name))));

        // Trigonometry: the argument may be a plain number in the current angle unit, or a value that
        // already carries an angle unit such as 90°.
        Add("sin", 1, 1, (a, u) => Number(Guarded(Math.Sin(ToRadians(a[0], u)), "sin")));
        Add("cos", 1, 1, (a, u) => Number(Guarded(Math.Cos(ToRadians(a[0], u)), "cos")));
        Add("tan", 1, 1, (a, u) => Number(Guarded(Math.Tan(ToRadians(a[0], u)), "tan")));
        Add("cot", 1, 1, (a, u) => Number(Guarded(1 / Math.Tan(ToRadians(a[0], u)), "cot")));
        Add("sec", 1, 1, (a, u) => Number(Guarded(1 / Math.Cos(ToRadians(a[0], u)), "sec")));
        Add("csc", 1, 1, (a, u) => Number(Guarded(1 / Math.Sin(ToRadians(a[0], u)), "csc")));
        Add("asin", 1, 1, (a, u) => Number(FromRadians(Guarded(Math.Asin(Dimensionless(a[0], "asin")), "asin"), u)));
        Add("acos", 1, 1, (a, u) => Number(FromRadians(Guarded(Math.Acos(Dimensionless(a[0], "acos")), "acos"), u)));
        Add("atan", 1, 1, (a, u) => Number(FromRadians(Math.Atan(Dimensionless(a[0], "atan")), u)));
        Add("acot", 1, 1, (a, u) => Number(FromRadians(Math.Atan(1 / Dimensionless(a[0], "acot")), u)));
        Add("atan2", 2, 2, (a, u) => Number(FromRadians(
            Math.Atan2(Dimensionless(a[0], "atan2"), Dimensionless(a[1], "atan2")), u)));

        // Hyperbolic functions ignore the angle unit.
        Unary("sinh", Math.Sinh);
        Unary("cosh", Math.Cosh);
        Unary("tanh", Math.Tanh);
        Unary("coth", x => 1 / Math.Tanh(x));
        Unary("asinh", Math.Asinh);
        Unary("acosh", Math.Acosh);
        Unary("atanh", Math.Atanh);

        Unary("ln", Math.Log);
        Unary("log2", Math.Log2);
        Unary("log10", Math.Log10);
        Add("log", 1, 2, (a, _) => a.Length == 1
            ? Number(Guarded(Math.Log10(Dimensionless(a[0], "log")), "log"))
            : Number(Guarded(
                Math.Log(Dimensionless(a[0], "log")) / Math.Log(Dimensionless(a[1], "log")), "log")));
        Unary("exp", Math.Exp);
        Add("sqrt", 1, 1, (a, _) => Root(a[0], 2, "sqrt"));
        Add("cbrt", 1, 1, (a, _) => Root(a[0], 3, "cbrt"));
        Add("root", 2, 2, (a, _) => Root(a[1], Dimensionless(a[0], "root"), "root"));
        Add("nthroot", 2, 2, (a, _) => Root(a[1], Dimensionless(a[0], "nthroot"), "root"));

        Add("abs", 1, 1, (a, _) => new Quantity(Math.Abs(a[0].Value), a[0].Unit));
        Add("sign", 1, 1, (a, _) => Number(Math.Sign(a[0].Value)));
        Add("floor", 1, 1, (a, _) => new Quantity(Math.Floor(a[0].Value), a[0].Unit));
        Add("ceil", 1, 1, (a, _) => new Quantity(Math.Ceiling(a[0].Value), a[0].Unit));
        Add("round", 1, 2, (a, _) =>
        {
            var digits = a.Length == 2 ? (int)Guards.WholeNumber(Dimensionless(a[1], "round"), "round") : 0;
            digits = Math.Clamp(digits, 0, 15);
            return new Quantity(Math.Round(a[0].Value, digits, MidpointRounding.AwayFromZero), a[0].Unit);
        });
        Add("trunc", 1, 1, (a, _) => new Quantity(Math.Truncate(a[0].Value), a[0].Unit));
        Add("pow", 2, 2, (a, _) => a[0].Pow(Dimensionless(a[1], "pow")));
        Add("mod", 2, 2, (a, _) => a[0].Subtract(a[1].Multiply(
            Number(Math.Truncate(a[0].Divide(a[1]).RequireNumber())))));
        Add("fact", 1, 1, (a, _) => Number(Factorial(Dimensionless(a[0], "fact"))));

        Add("min", 1, int.MaxValue, (a, _) => MinMax(a, false));
        Add("max", 1, int.MaxValue, (a, _) => MinMax(a, true));
        Add("sum", 1, int.MaxValue, (a, _) => Fold(a, static (x, y) => x.Add(y)));
        Add("avg", 1, int.MaxValue, (a, _) => Fold(a, static (x, y) => x.Add(y)).Divide(Number(a.Length)));
        Add("mean", 1, int.MaxValue, (a, _) => Fold(a, static (x, y) => x.Add(y)).Divide(Number(a.Length)));
        Add("gcd", 2, int.MaxValue, (a, _) => Number(FoldIntegers(a, true)));
        Add("lcm", 2, int.MaxValue, (a, _) => Number(FoldIntegers(a, false)));
        Add("nCr", 2, 2, (a, _) => Number(Combinations(a[0], a[1], false)));
        Add("nPr", 2, 2, (a, _) => Number(Combinations(a[0], a[1], true)));
        Add("hypot", 2, int.MaxValue, (a, _) =>
        {
            var squares = 0d;
            foreach (var q in a)
            {
                var v = Dimensionless(q, "hypot");
                squares += v * v;
            }

            return Number(Guarded(Math.Sqrt(squares), "hypot"));
        });
        Add("rand", 0, 2, (a, _) => Number(RandomValue(a)));

        return table;
    }

    private static Quantity Number(double value) => Quantity.Number(Guarded(value, string.Empty));

    private static double Guarded(double value, string what) => Guards.Finite(value, what.Length == 0 ? string.Empty : what + " ");

    private static double Dimensionless(Quantity quantity, string name) =>
        quantity.IsDimensionless ? quantity.Value : throw new CalcException(Errors.ExpectedNumber(quantity.Unit.Symbol));

    private static double ToRadians(Quantity quantity, AngleUnit unit)
    {
        if (quantity.HasUnit)
        {
            if (quantity.Unit.Dimension == Dimension.OfAngle)
            {
                return quantity.Value * quantity.Unit.Factor;
            }

            throw new CalcException(Errors.ExpectedNumber(quantity.Unit.Symbol));
        }

        return unit switch
        {
            AngleUnit.Radians => quantity.Value,
            AngleUnit.Gradians => quantity.Value * Math.PI / 200,
            _ => quantity.Value * Math.PI / 180
        };
    }

    private static double FromRadians(double radians, AngleUnit unit) => unit switch
    {
        AngleUnit.Radians => radians,
        AngleUnit.Gradians => radians * 200 / Math.PI,
        _ => radians * 180 / Math.PI
    };

    private static Quantity Root(Quantity operand, double degree, string name)
    {
        if (Math.Abs(degree) < 1e-12)
        {
            throw new CalcException(Errors.Domain(name + " "));
        }

        var unit = operand.IsDimensionless ? UnitExpr.None : UnitExpr.Pow(operand.Unit, 1 / degree);
        if (operand.Value < 0)
        {
            if (Math.Abs(degree - Math.Round(degree)) > 1e-9 || Math.Abs(degree % 2) < 1e-9)
            {
                throw new CalcException(Errors.Domain(name + " "));
            }

            return new Quantity(Guarded(-Math.Pow(-operand.Value, 1 / degree), name), unit);
        }

        return new Quantity(Guarded(Math.Pow(operand.Value, 1 / degree), name), unit);
    }

    private static double Factorial(double value)
    {
        var n = Guards.FactorialOperand(value);
        var result = 1d;
        for (var i = 2; i <= n; i++)
        {
            result *= i;
        }

        return result;
    }

    private static Quantity MinMax(Quantity[] args, bool wantMaximum)
    {
        var best = args[0];
        for (var i = 1; i < args.Length; i++)
        {
            var candidate = args[i];
            EnsureCompatible(best, candidate, wantMaximum ? "max" : "min");
            var converted = candidate.IsDimensionless ? candidate.Value : candidate.Unit.ConvertTo(candidate.Value, best.Unit);
            var bestValue = best.Value;
            if (wantMaximum ? converted > bestValue : converted < bestValue)
            {
                best = new Quantity(converted, best.Unit);
            }
        }

        return best;
    }

    private static Quantity Fold(Quantity[] args, Func<Quantity, Quantity, Quantity> operation)
    {
        var accumulator = args[0];
        for (var i = 1; i < args.Length; i++)
        {
            EnsureCompatible(accumulator, args[i], "sum");
            accumulator = operation(accumulator, args[i]);
        }

        return accumulator;
    }

    private static void EnsureCompatible(Quantity left, Quantity right, string name)
    {
        if (left.IsDimensionless != right.IsDimensionless)
        {
            throw new CalcException(Errors.IncompatibleUnits(
                left.IsDimensionless ? "1" : left.Unit.Symbol,
                right.IsDimensionless ? "1" : right.Unit.Symbol));
        }

        if (left.HasUnit && !left.Unit.IsCompatibleWith(right.Unit))
        {
            throw new CalcException(Errors.IncompatibleUnits(left.Unit.Symbol, right.Unit.Symbol));
        }

        _ = name;
    }

    private static double FoldIntegers(Quantity[] args, bool greatestCommonDivisor)
    {
        var result = (double)Math.Abs(Guards.WholeNumber(Dimensionless(args[0], "gcd"), "gcd"));
        for (var i = 1; i < args.Length; i++)
        {
            var next = (double)Math.Abs(Guards.WholeNumber(Dimensionless(args[i], "gcd"), "gcd"));
            result = greatestCommonDivisor ? Gcd(result, next) : Lcm(result, next);
            if (result > 9.007199254740992e15)
            {
                throw new CalcException(Errors.Overflow(string.Empty));
            }
        }

        return result;
    }

    private static double Gcd(double a, double b)
    {
        while (b > 0.5)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static double Lcm(double a, double b) => a < 0.5 || b < 0.5 ? 0 : a / Gcd(a, b) * b;

    private static double Combinations(Quantity nQuantity, Quantity rQuantity, bool permutations)
    {
        var n = Guards.WholeNumber(Dimensionless(nQuantity, permutations ? "nPr" : "nCr"), "n");
        var r = Guards.WholeNumber(Dimensionless(rQuantity, permutations ? "nPr" : "nCr"), "r");
        if (n < 0 || r < 0 || r > n)
        {
            throw new CalcException(Errors.Domain(string.Empty));
        }

        if (r > MaxCombinatoricLoop)
        {
            throw new CalcException(Errors.Overflow(string.Empty));
        }

        var result = 1d;
        for (var i = 0; i < r; i++)
        {
            result *= n - i;
            if (!permutations)
            {
                result /= i + 1;
            }

            if (double.IsInfinity(result))
            {
                throw new CalcException(Errors.Overflow(string.Empty));
            }
        }

        return result;
    }

    private static double RandomValue(Quantity[] args)
    {
        double sample;
        lock (RandomLock)
        {
            sample = RandomSource.NextDouble();
        }

        return args.Length switch
        {
            0 => sample,
            1 => sample * Dimensionless(args[0], "rand"),
            _ => Dimensionless(args[0], "rand") + (sample * (Dimensionless(args[1], "rand") - Dimensionless(args[0], "rand")))
        };
    }
}
