namespace Calcpad.Core.Numerics;

/// <summary>
/// Unit conversion backing the iPad style converter and available to every plugin through
/// <see cref="IUnitConverter"/>. The catalogue is shared with the evaluator, so
/// <c>3 m + 20 cm</c> and the converter always agree.
/// </summary>
public sealed class UnitConverter : IUnitConverter
{
    /// <summary>Significant digits used when a conversion is reported as text.</summary>
    public const int DefaultPrecision = 12;

    /// <inheritdoc />
    public IReadOnlyList<UnitCategory> Categories => UnitCatalog.Categories;

    /// <inheritdoc />
    public UnitCategory? FindCategory(string categoryId) => UnitCatalog.FindCategory(categoryId);

    /// <inheritdoc />
    public UnitConversionResult Convert(double value, string fromUnit, string toUnit)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return UnitConversionResult.Failure(Errors.NotFinite);
        }

        var from = UnitCatalog.FindUnitWithCategory(fromUnit);
        if (from is null)
        {
            return UnitConversionResult.Failure(UnknownUnit(fromUnit));
        }

        var to = UnitCatalog.FindUnitWithCategory(toUnit);
        if (to is null)
        {
            return UnitConversionResult.Failure(UnknownUnit(toUnit));
        }

        if (!string.Equals(from.Value.Category.Id, to.Value.Category.Id, StringComparison.OrdinalIgnoreCase))
        {
            return UnitConversionResult.Failure(Errors.IncompatibleUnits(from.Value.Unit.Symbol, to.Value.Unit.Symbol));
        }

        try
        {
            var baseValue = (value * from.Value.Unit.Factor) + from.Value.Unit.Offset;
            var converted = (baseValue - to.Value.Unit.Offset) / to.Value.Unit.Factor;
            if (double.IsNaN(converted) || double.IsInfinity(converted))
            {
                return UnitConversionResult.Failure(Errors.Overflow(string.Empty));
            }

            return new UnitConversionResult(
                true,
                NumberFormatter.Format(converted, DefaultPrecision),
                null,
                from.Value.Unit.Symbol,
                to.Value.Unit.Symbol);
        }
        catch (Exception ex)
        {
            return UnitConversionResult.Failure(Errors.Unexpected(ex.Message));
        }
    }

    /// <inheritdoc />
    public bool TryParseQuantity(string input, out double value, out string unit)
    {
        value = 0;
        unit = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            if (!NumberFormatter.TrySplitQuantity(input.Trim(), out value, out var rawUnit) ||
                string.IsNullOrWhiteSpace(rawUnit))
            {
                return false;
            }

            var info = UnitCatalog.FindUnit(rawUnit);
            if (info is null)
            {
                return false;
            }

            unit = info.Symbol;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public UnitConversionResult ConvertExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return UnitConversionResult.Failure(Errors.Empty);
        }

        if (expression.Length > ExpressionEngine.MaxExpressionLength)
        {
            return UnitConversionResult.Failure(Errors.TooLong);
        }

        try
        {
            var tokens = Lexer.Tokenize(expression);
            var ast = Parser.Parse(tokens);
            var scope = new EvalScope(new EvaluationContext { UnitAware = true }, allowIndirection: true);

            if (ast is not ConvertNode convert)
            {
                var plain = Evaluator.Run(ast, scope);
                return new UnitConversionResult(
                    true,
                    NumberFormatter.Format(plain.Value, DefaultPrecision),
                    null,
                    plain.HasUnit ? plain.Unit.Symbol : null,
                    plain.HasUnit ? plain.Unit.Symbol : null);
            }

            var source = Evaluator.Run(convert.Value, scope);
            var target = Evaluator.Run(convert.Target, scope);
            if (!target.HasUnit)
            {
                return UnitConversionResult.Failure(Errors.InvalidTargetUnit);
            }

            if (!source.HasUnit)
            {
                return UnitConversionResult.Failure(Errors.IncompatibleUnits("1", target.Unit.Symbol));
            }

            var converted = source.ConvertTo(target.Unit);
            return new UnitConversionResult(
                true,
                NumberFormatter.Format(converted.Value, DefaultPrecision),
                null,
                source.Unit.Symbol,
                target.Unit.Symbol);
        }
        catch (CalcException ex)
        {
            return UnitConversionResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            return UnitConversionResult.Failure(Errors.Unexpected(ex.Message));
        }
    }

    /// <summary>All categories as a lookup by id, handy for panels that list them.</summary>
    public IReadOnlyDictionary<string, UnitCategory> CategoriesById =>
        UnitCatalog.Categories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a unit anywhere in the catalogue.</summary>
    public UnitInfo? FindUnit(string unit) => UnitCatalog.FindUnit(unit);

    /// <summary>Converts a value together with its unit in one call.</summary>
    public UnitConversionResult ConvertExpression(double value, string fromUnit, string toUnit) =>
        Convert(value, fromUnit, toUnit);

    private static string UnknownUnit(string? unit) =>
        $"未知单位 “{unit}” / unknown unit '{unit}'";
}

/// <summary>Factory helpers so plugins can obtain engine instances without new dependencies.</summary>
public static class MathServices
{
    /// <summary>Creates the default expression engine.</summary>
    public static IExpressionEngine CreateExpressionEngine() => new ExpressionEngine();

    /// <summary>Creates the default unit converter.</summary>
    public static IUnitConverter CreateUnitConverter() => new UnitConverter();
}
