using System.Globalization;

namespace Calcpad.Core.Numerics;

/// <summary>One reduction step of the evaluation trace, shown by Math Notes.</summary>
/// <param name="Kind">Step family: <c>precedence</c>, <c>assign</c> or <c>convert</c>.</param>
/// <param name="Before">Expression text before the reduction.</param>
/// <param name="After">Value text after the reduction.</param>
internal sealed record TraceStep(string Kind, string Before, string After);

/// <summary>
/// Evaluation state for one expression: variables, angle unit, precision and the collected trace.
/// Variables are kept in a private copy and written back to the caller's dictionary when the caller
/// owns a mutable context, which is how Math Notes threads <c>x = 3</c> into the next line.
/// </summary>
internal sealed class EvalScope
{
    private readonly Dictionary<string, string> _variables;
    private readonly Dictionary<string, UnitExpr> _unitVariables = new(StringComparer.OrdinalIgnoreCase);

    public EvalScope(EvaluationContext context, bool allowIndirection)
    {
        Context = context;
        AllowIndirection = allowIndirection;
        _variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in context.Variables)
        {
            _variables[pair.Key] = pair.Value;
        }
    }

    /// <summary>The context the caller supplied.</summary>
    public EvaluationContext Context { get; }

    /// <summary>Controls whether a stored variable may be re-parsed as a quantity.</summary>
    public bool AllowIndirection { get; }

    /// <summary>Significant digits for formatted values.</summary>
    public int Precision => Guards.Precision(Context.Precision);

    /// <summary>Angle unit used by trigonometry.</summary>
    public AngleUnit AngleUnit => Context.AngleUnit;

    /// <summary>False when the caller wants plain numbers only.</summary>
    public bool UnitAware => Context.UnitAware;

    /// <summary>Trace collected so far.</summary>
    public List<TraceStep> Trace { get; } = [];

    /// <summary>Nodes already reduced to text, used to print partially evaluated steps.</summary>
    public Dictionary<Node, string> Substitutions { get; } = [];

    /// <summary>Names assigned by the expression, in order.</summary>
    public List<string> AssignedNames { get; } = [];

    /// <summary>True when a variable name is known in this scope.</summary>
    public bool TryGetVariable(string name, out string value) => _variables.TryGetValue(name, out value!);

    /// <summary>True when a variable carries a unit recorded earlier in this scope.</summary>
    public bool TryGetVariableUnit(string name, out UnitExpr unit) => _unitVariables.TryGetValue(name, out unit!);

    /// <summary>Records an assignment and mirrors it into the caller's dictionary when possible.</summary>
    public void Assign(string name, Quantity value)
    {
        var text = value.Format(Precision);
        _variables[name] = text;
        if (value.HasUnit)
        {
            _unitVariables[name] = value.Unit;
        }
        else
        {
            _unitVariables.Remove(name);
        }

        AssignedNames.Add(name);

        if (!ReferenceEquals(Context, EvaluationContext.Default))
        {
            try
            {
                Context.Variables[name] = text;
            }
            catch (NotSupportedException)
            {
                // The caller handed us a read only dictionary; the local copy still works.
            }
        }
    }

    /// <summary>Adds a trace step while keeping the trace bounded.</summary>
    public void Record(string kind, Node node, string after)
    {
        if (Trace.Count >= 5)
        {
            return;
        }

        var before = node.Render(Substitutions);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        Trace.Add(new TraceStep(kind, before, after));
    }
}

/// <summary>
/// Walks the AST and produces a <see cref="Quantity"/> plus, optionally, a readable reduction trace.
/// All arithmetic goes through <see cref="Quantity"/>, so unit checking, division by zero, overflow
/// and factorial limits are enforced in exactly one place.
/// </summary>
internal static class Evaluator
{
    /// <summary>Evaluates a parsed expression, recording its reduction steps.</summary>
    public static Quantity Run(Node root, EvalScope scope) => root switch
    {
        SequenceNode sequence => RunSequence(sequence, scope),
        _ => Evaluate(root, scope, null)
    };

    private static Quantity RunSequence(SequenceNode sequence, EvalScope scope)
    {
        var result = Quantity.Number(0);
        foreach (var statement in sequence.Statements)
        {
            result = Evaluate(statement, scope, null);
        }

        return result;
    }

    private static Quantity Evaluate(Node node, EvalScope scope, string? stepKind)
    {
        switch (node)
        {
            case NumberNode number:
                return Quantity.Number(number.Value);

            case ConstantNode constant:
                return Quantity.Number(constant.Value);

            case IdentifierNode identifier:
                return EvaluateIdentifier(identifier, scope);

            case UnaryNode unary:
                {
                    var operand = Evaluate(unary.Operand, scope, null);
                    var value = unary.Op switch
                    {
                        "-" => operand.Negate(),
                        "√" => FunctionsRoot(operand, 2, "√"),
                        "∛" => FunctionsRoot(operand, 3, "∛"),
                        _ => operand
                    };
                    return Reduce(scope, node, value, stepKind);
                }

            case PostfixNode postfix:
                {
                    var operand = Evaluate(postfix.Operand, scope, null);
                    var value = postfix.Op switch
                    {
                        "!" => Quantity.Number(Factorial(operand)),
                        "%" => new Quantity(operand.Value / 100, operand.Unit),
                        "²" => operand.Pow(2),
                        "³" => operand.Pow(3),
                        _ => operand
                    };
                    return Reduce(scope, node, value, stepKind);
                }

            case BinaryNode binary:
                return EvaluateBinary(binary, scope);

            case CallNode call:
                {
                    var arguments = new Quantity[call.Arguments.Count];
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        arguments[i] = Evaluate(call.Arguments[i], scope, null);
                    }

                    return Reduce(scope, node, Invoke(call, arguments, scope), stepKind);
                }

            case AssignNode assign:
                {
                    var value = Evaluate(assign.Value, scope, null);
                    scope.Assign(assign.Name, value);
                    return Reduce(scope, node, value, "assign");
                }

            case ConvertNode convert:
                {
                    var value = Evaluate(convert.Value, scope, null);
                    var target = Evaluate(convert.Target, scope, null);
                    if (!target.HasUnit)
                    {
                        throw new CalcException(Errors.InvalidTargetUnit);
                    }

                    if (!value.HasUnit)
                    {
                        throw new CalcException(Errors.IncompatibleUnits("1", target.Unit.Symbol));
                    }

                    return Reduce(scope, node, value.ConvertTo(target.Unit), "convert");
                }

            case SequenceNode sequence:
                return RunSequence(sequence, scope);

            default:
                throw new CalcException(Errors.Unexpected(node.GetType().Name));
        }
    }

    private static Quantity EvaluateBinary(BinaryNode binary, EvalScope scope)
    {
        var left = Evaluate(binary.Left, scope, null);
        var right = Evaluate(binary.Right, scope, null);
        var value = binary.Op switch
        {
            "+" => left.Add(right),
            "-" => left.Subtract(right),
            "*" => left.Multiply(right),
            "/" => left.Divide(right),
            "%" => Modulo(left, right),
            "^" => left.Pow(right.RequireNumber()),
            "&" => Bitwise(left, right, and: true),
            "|" => Bitwise(left, right, and: false),
            _ => throw new CalcException(Errors.UnexpectedToken(binary.Op))
        };

        return Reduce(scope, binary, value, "precedence");
    }

    /// <summary>Bitwise and / or on whole numbers, as Math Notes accepts with <c>&amp;</c> and <c>|</c>.</summary>
    private static Quantity Bitwise(Quantity left, Quantity right, bool and)
    {
        var a = Guards.WholeNumber(left.RequireNumber(), "bitwise ");
        var b = Guards.WholeNumber(right.RequireNumber(), "bitwise ");
        return Quantity.Number(and ? a & b : a | b);
    }

    private static Quantity Modulo(Quantity left, Quantity right)
    {
        if (!left.IsDimensionless || !right.IsDimensionless)
        {
            throw new CalcException(Errors.ExpectedNumber(
                left.HasUnit ? left.Unit.Symbol : right.Unit.Symbol));
        }

        if (right.Value == 0)
        {
            throw new CalcException(Errors.DivideByZero);
        }

        return Quantity.Number(left.Value % right.Value);
    }

    private static Quantity EvaluateIdentifier(IdentifierNode identifier, EvalScope scope)
    {
        var name = identifier.Name;

        if (scope.TryGetVariable(name, out var stored))
        {
            var quantity = ReadStoredValue(stored, scope, name);
            scope.Substitutions[identifier] = quantity.Format(scope.Precision);
            return quantity;
        }

        if (Constants.TryGet(name, out var constant))
        {
            return Quantity.Number(constant);
        }

        if (!scope.UnitAware)
        {
            return UnitCatalog.FindUnit(name) is not null ? Quantity.Number(1) : throw Unknown(name);
        }

        if (UnitCatalog.FindUnitWithCategory(name) is { } found)
        {
            return Quantity.One(UnitCatalog.ToExpression(found.Unit));
        }

        throw Unknown(name);

        static CalcException Unknown(string value) => new(Errors.UnknownIdentifier(value));
    }

    private static Quantity Invoke(CallNode call, Quantity[] arguments, EvalScope scope)
    {
        if (!Functions.TryGet(call.Name, out var spec))
        {
            throw new CalcException(Errors.UnknownFunction(call.Name));
        }

        if (arguments.Length < spec.MinArgs || arguments.Length > spec.MaxArgs)
        {
            throw new CalcException(spec.MaxArgs == int.MaxValue
                ? Errors.BadArityAtLeast(spec.Name, spec.MinArgs, arguments.Length)
                : Errors.BadArity(spec.Name, spec.MinArgs, arguments.Length));
        }

        return spec.Invoke(arguments, scope.AngleUnit);
    }

    private static Quantity FunctionsRoot(Quantity operand, int degree, string name)
    {
        if (Math.Abs(degree) < 1e-12)
        {
            throw new CalcException(Errors.Domain(name));
        }

        var unit = operand.IsDimensionless ? UnitExpr.None : UnitExpr.Pow(operand.Unit, 1d / degree);
        if (operand.Value < 0 && degree % 2 == 0)
        {
            throw new CalcException(Errors.Domain(name));
        }

        var magnitude = Math.Pow(Math.Abs(operand.Value), 1d / degree);
        var value = operand.Value < 0 ? -magnitude : magnitude;
        return new Quantity(Guards.Finite(value, name + " "), unit);
    }

    private static double Factorial(Quantity operand)
    {
        var n = Guards.FactorialOperand(operand.RequireNumber());
        var result = 1d;
        for (var i = 2; i <= n; i++)
        {
            result *= i;
        }

        return result;
    }

    private static Quantity Reduce(EvalScope scope, Node node, Quantity value, string? kind)
    {
        var text = value.Format(scope.Precision);
        scope.Record(kind ?? "precedence", node, text);
        scope.Substitutions[node] = text;
        return value;
    }

    private static Quantity ReadStoredValue(string stored, EvalScope scope, string name)
    {
        if (double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            if (scope.TryGetVariableUnit(name, out var knownUnit))
            {
                return new Quantity(plain, knownUnit);
            }

            return Quantity.Number(plain);
        }

        if (!scope.AllowIndirection || !TryParseStoredQuantity(stored, out var quantity))
        {
            throw new CalcException(Errors.UnknownIdentifier(name));
        }

        return quantity;
    }

    /// <summary>Reads a stored variable text such as <c>3.2 m</c> back into a quantity with its unit.</summary>
    public static bool TryParseStoredQuantity(string? text, out Quantity quantity)
    {
        quantity = Quantity.Number(0);
        if (!NumberFormatter.TrySplitQuantity(text, out var value, out var unitText))
        {
            return false;
        }

        if (string.IsNullOrEmpty(unitText))
        {
            quantity = Quantity.Number(value);
            return true;
        }

        if (!TryComposeUnit(unitText, out var unit))
        {
            quantity = Quantity.Number(value);
            return true;
        }

        quantity = new Quantity(value, unit);
        return true;
    }

    /// <summary>Rebuilds a compound unit symbol such as <c>m/s</c> or <c>kg·m^2/s^2</c>.</summary>
    private static bool TryComposeUnit(string text, out UnitExpr unit)
    {
        unit = UnitExpr.None;
        var segments = text.Split('/', StringSplitOptions.TrimEntries);
        if (segments.Length > 2)
        {
            return false;
        }

        var numerator = ParseFactors(segments[0]);
        if (numerator is null)
        {
            return false;
        }

        var denominator = segments.Length == 2 ? ParseFactors(segments[1].Trim('(', ')')) : UnitExpr.None;
        if (denominator is null)
        {
            return false;
        }

        unit = denominator.IsNone ? numerator : UnitExpr.Divide(numerator, denominator);
        return true;
    }

    private static UnitExpr? ParseFactors(string text)
    {
        var result = UnitExpr.None;
        foreach (var raw in text.Split(['·', '*'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exponent = 1d;
            var symbol = raw;
            var caret = raw.IndexOf('^');
            if (caret >= 0)
            {
                symbol = raw[..caret];
                if (!double.TryParse(raw[(caret + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out exponent))
                {
                    return null;
                }
            }

            var info = UnitCatalog.FindUnit(symbol);
            if (info is null)
            {
                return null;
            }

            var expression = UnitCatalog.ToExpression(info);
            if (Math.Abs(exponent - 1) > 1e-12)
            {
                expression = UnitExpr.Pow(expression, exponent);
            }

            result = result.IsNone ? expression : UnitExpr.Multiply(result, expression);
        }

        return result;
    }

    /// <summary>Well known mathematical constants.</summary>
    public static class Constants
    {
        private static readonly Dictionary<string, double> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["π"] = Math.PI,
            ["pi"] = Math.PI,
            ["e"] = Math.E,
            ["tau"] = Math.Tau,
            ["φ"] = 1.6180339887498948482,
            ["phi"] = 1.6180339887498948482
        };

        /// <summary>Looks a constant up by name.</summary>
        public static bool TryGet(string name, out double value) => Map.TryGetValue(name, out value);

        /// <summary>Every constant name.</summary>
        public static IReadOnlyList<string> Names { get; } = [.. Map.Keys];
    }
}
