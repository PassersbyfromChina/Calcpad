namespace Calcpad.Core.Numerics;

/// <summary>
/// Raised for any expected, user visible evaluation problem (bad syntax, unknown name, overflow,
/// incompatible units, ...). The engine converts it into an <see cref="EvaluationResult"/> failure;
/// it never escapes to the caller.
/// </summary>
public sealed class CalcException : Exception
{
    /// <summary>Creates the exception with a bilingual, human readable message.</summary>
    public CalcException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception and wraps an inner implementation error.</summary>
    public CalcException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Every user facing message the math engine can produce. Messages are bilingual so the same string
/// can be shown on a Chinese or an English surface without a separate resource pass.
/// </summary>
public static class Errors
{
    /// <summary>Empty or whitespace-only input.</summary>
    public const string Empty = "表达式为空 / expression is empty";

    /// <summary>Input longer than the engine accepts.</summary>
    public const string TooLong = "表达式过长 / expression is too long";

    /// <summary>Too many tokens to analyse safely.</summary>
    public const string TooManyTokens = "表达式过于复杂 / expression is too complex";

    /// <summary>Nesting deeper than the engine accepts.</summary>
    public const string TooDeep = "括号嵌套过深 / expression is nested too deeply";

    /// <summary>Division by zero.</summary>
    public const string DivideByZero = "除以零 / division by zero";

    /// <summary>Parentheses do not balance.</summary>
    public const string UnbalancedParen = "括号不匹配 / unbalanced parentheses";

    /// <summary>Unexpected character.</summary>
    public static string UnexpectedCharacter(char c) => $"无法识别的字符 “{c}” / unexpected character '{c}'";

    /// <summary>Unexpected token.</summary>
    public static string UnexpectedToken(string token) => $"意外的符号 “{token}” / unexpected token '{token}'";

    /// <summary>Expression ends while a value or operator was expected.</summary>
    public static string UnexpectedEnd => "表达式不完整 / expression ends unexpectedly";

    /// <summary>Unknown function name.</summary>
    public static string UnknownFunction(string name) => $"未知函数 “{name}” / unknown function '{name}'";

    /// <summary>Unknown variable or unit name.</summary>
    public static string UnknownIdentifier(string name) =>
        $"未知变量或单位 “{name}” / unknown variable or unit '{name}'";

    /// <summary>Function called with the wrong number of arguments.</summary>
    public static string BadArity(string name, int expected, int actual) =>
        $"函数 “{name}” 需要 {expected} 个参数，实际 {actual} 个 / '{name}' expects {expected} argument(s), got {actual}";

    /// <summary>Function called with the wrong number of arguments, variadic form.</summary>
    public static string BadArityAtLeast(string name, int minimum, int actual) =>
        $"函数 “{name}” 至少需要 {minimum} 个参数，实际 {actual} 个 / '{name}' expects at least {minimum} argument(s), got {actual}";

    /// <summary>Factorial of a negative or non-integer value.</summary>
    public const string FactorialNotInteger =
        "阶乘只支持非负整数 / factorial requires a non-negative integer";

    /// <summary>Factorial above the double precision limit.</summary>
    public const string FactorialTooLarge =
        "阶乘过大（n > 170 结果溢出）/ factorial too large (n > 170 overflows)";

    /// <summary>Result is not a finite number.</summary>
    public static string Overflow(string what) => $"{what}结果超出范围 / {what}result is out of range";

    /// <summary>Mathematically undefined result, e.g. sqrt(-1).</summary>
    public static string Domain(string what) => $"{what}定义域错误 / {what}is outside the domain";

    /// <summary>Units with different dimensions were combined.</summary>
    public static string IncompatibleUnits(string left, string right) =>
        $"单位不兼容：{left} 与 {right} / incompatible units: {left} and {right}";

    /// <summary>Affine units (temperatures) may only be converted, not multiplied.</summary>
    public const string AffineOperation =
        "温度等偏移单位只支持换算 / affine units (temperatures) only support conversion";

    /// <summary>A unit was raised to a power that leaves a fractional exponent.</summary>
    public static string FractionalUnitPower(string symbol) =>
        $"单位 {symbol} 的幂次无效 / invalid power for unit {symbol}";

    /// <summary>A value with a unit was used where a plain number is required.</summary>
    public static string ExpectedNumber(string symbol) =>
        $"此处需要无单位数值，实际为 {symbol} / a plain number is required here, got {symbol}";

    /// <summary>The target of a conversion is not a unit.</summary>
    public const string InvalidTargetUnit = "换算目标不是有效单位 / conversion target is not a valid unit";

    /// <summary>Assignment target is not a plain name.</summary>
    public const string InvalidAssignment = "赋值左侧必须是变量名 / the left side of '=' must be a name";

    /// <summary>Percent or other postfix operator applied to something that is not a number.</summary>
    public static string BadOperand(string op) => $"“{op}” 的操作数无效 / invalid operand for '{op}'";

    /// <summary>Unexpected implementation failure, kept human readable.</summary>
    public static string Unexpected(string detail) => $"无法计算该表达式 / unable to evaluate: {detail}";

    /// <summary>Number of significant digits requested is out of range.</summary>
    public const string BadPrecision = "精度必须在 1 到 17 之间 / precision must be between 1 and 17";

    /// <summary>Value is not a finite number when one is required.</summary>
    public const string NotFinite = "数值无效（NaN 或无穷大）/ value is not a finite number";
}

/// <summary>
/// Numeric guards shared by the evaluator and the unit algebra. They keep the engine free of
/// silent <c>NaN</c>/<c>Infinity</c> results, which is what makes the widget error states reliable.
/// </summary>
public static class Guards
{
    /// <summary>Largest factorial operand; 171! overflows a double.</summary>
    public const int MaxFactorial = 170;

    /// <summary>Largest magnitude accepted for a computed exponent.</summary>
    public const double MaxExponentMagnitude = 1e6;

    /// <summary>Rejects NaN and infinities with a readable message.</summary>
    public static double Finite(double value, string what)
    {
        if (double.IsNaN(value))
        {
            throw new CalcException(Errors.Domain(what));
        }

        if (double.IsInfinity(value))
        {
            throw new CalcException(Errors.Overflow(what));
        }

        return value == 0 ? 0 : value;
    }

    /// <summary>Requires a finite, non-negative integer within the factorial limit.</summary>
    public static int FactorialOperand(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            throw new CalcException(Errors.FactorialNotInteger);
        }

        var n = (int)Math.Round(value);
        if (n > MaxFactorial)
        {
            throw new CalcException(Errors.FactorialTooLarge);
        }

        return n;
    }

    /// <summary>Requires a whole number inside the exactly representable integer range.</summary>
    public static long WholeNumber(double value, string what)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 9.007199254740992e15)
        {
            throw new CalcException(Errors.Overflow(what));
        }

        if (Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            throw new CalcException(Errors.Domain(what));
        }

        return (long)Math.Round(value);
    }

    /// <summary>Clamps the requested significant digits into the supported range.</summary>
    public static int Precision(int value) => Math.Clamp(value, 1, 17);
}
