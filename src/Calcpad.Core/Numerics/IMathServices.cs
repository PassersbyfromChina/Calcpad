namespace Calcpad.Core.Numerics;

public enum AngleUnit
{
    Radians,
    Degrees,
    Gradians
}

public sealed record EvaluationContext
{
    public static EvaluationContext Default { get; } = new();

    public Dictionary<string, string> Variables { get; init; } = [];

    /// <summary>Significant digits kept in the formatted result.</summary>
    public int Precision { get; init; } = 12;

    public AngleUnit AngleUnit { get; init; } = AngleUnit.Degrees;

    /// <summary>When true results carry units, e.g. <c>3 m + 20 cm</c>.</summary>
    public bool UnitAware { get; init; } = true;
}

public sealed record EvaluationResult(
    bool Ok,
    string? Value,
    string? Error,
    string? NormalizedExpression,
    IReadOnlyList<string> Steps)
{
    public static EvaluationResult Failure(string error, string? normalized = null) =>
        new(false, null, error, normalized, []);
}

/// <summary>
/// The math engine behind Math Notes, the scientific keypad and handwriting correction.
/// Plugins must treat it as pure: no canvas or database access.
/// </summary>
public interface IExpressionEngine
{
    EvaluationResult Evaluate(string expression, EvaluationContext? context = null);

    bool TryEvaluate(string expression, out EvaluationResult result, EvaluationContext? context = null);

    /// <summary>Completions for the editor, e.g. function and unit names.</summary>
    IReadOnlyList<string> SuggestCompletions(string prefix, int max = 12);

    /// <summary>
    /// 0..1 score for how plausible a string is as a math expression. Handwriting correction uses
    /// it to choose between visually similar candidates such as <c>1</c>, <c>l</c> and <c>|</c>.
    /// </summary>
    double SyntaxPlausibility(string expression);

    /// <summary>Variables assigned so far by a sequence of lines, used for live Math Notes blocks.</summary>
    IReadOnlyList<string> ExtractAssignments(string expression);

    IReadOnlyList<string> KnownFunctions { get; }

    IReadOnlyList<string> KnownUnits { get; }
}

/// <summary>One unit inside a category.</summary>
/// <param name="Factor">Multiplier to the category base unit.</param>
/// <param name="Offset">Additive offset applied after scaling, needed for temperatures.</param>
public sealed record UnitInfo(string Id, string Name, string NameZh, string Symbol, double Factor, double Offset, bool IsBase)
{
    public bool Affine => Offset != 0;
}

public sealed record UnitCategory(string Id, string Name, string NameZh, IReadOnlyList<UnitInfo> Units)
{
    public UnitInfo? Find(string unit) =>
        Units.FirstOrDefault(u =>
            string.Equals(u.Id, unit, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(u.Symbol, unit, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(u.Name, unit, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(u.NameZh, unit, StringComparison.OrdinalIgnoreCase));
}

public sealed record UnitConversionResult(bool Ok, string? Value, string? Error, string? FromUnit, string? ToUnit)
{
    public static UnitConversionResult Failure(string error) => new(false, null, error, null, null);
}

/// <summary>Unit conversion backing the iPad style converter, available to every plugin.</summary>
public interface IUnitConverter
{
    IReadOnlyList<UnitCategory> Categories { get; }

    UnitCategory? FindCategory(string categoryId);

    UnitConversionResult Convert(double value, string fromUnit, string toUnit);

    /// <summary>Parses a natural input such as <c>"3 m"</c>, <c>"3绫?</c> or <c>"20cm"</c>.</summary>
    bool TryParseQuantity(string input, out double value, out string unit);

    /// <summary>Converts a whole expression, e.g. <c>"3 m to cm"</c> or <c>"3绫宠浆鍘樼背"</c>.</summary>
    UnitConversionResult ConvertExpression(string expression);
}
