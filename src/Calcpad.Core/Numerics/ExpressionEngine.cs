using System.Globalization;
using System.Text;

namespace Calcpad.Core.Numerics;

/// <summary>
/// The hand written expression engine behind Math Notes, the scientific keypad and handwriting
/// correction. It is pure: no canvas, database or UI dependency, and every failure is reported as a
/// readable bilingual message instead of an exception.
/// </summary>
public sealed class ExpressionEngine : IExpressionEngine
{
    /// <summary>Longest expression the engine accepts, to bound lexing and parsing work.</summary>
    public const int MaxExpressionLength = 2000;

    private static readonly string[] FunctionNames = [.. Functions.Names];

    /// <inheritdoc />
    public IReadOnlyList<string> KnownFunctions => FunctionNames;

    /// <inheritdoc />
    public IReadOnlyList<string> KnownUnits { get; } = UnitCatalog.AllNames();

    /// <summary>Names of the mathematical constants the engine understands.</summary>
    public IReadOnlyList<string> KnownConstants => Evaluator.Constants.Names;

    /// <inheritdoc />
    public EvaluationResult Evaluate(string expression, EvaluationContext? context = null)
    {
        if (expression is null || string.IsNullOrWhiteSpace(expression))
        {
            return EvaluationResult.Failure(Errors.Empty);
        }

        if (expression.Length > MaxExpressionLength)
        {
            return EvaluationResult.Failure(Errors.TooLong);
        }

        var actual = context ?? EvaluationContext.Default;
        string? normalized = null;
        try
        {
            var tokens = Lexer.Tokenize(expression);
            var ast = Parser.Parse(tokens);
            normalized = ast.Render();
            var scope = new EvalScope(actual, allowIndirection: true);
            var value = Evaluator.Run(ast, scope);
            var formatted = FormatResult(value, actual);
            return new EvaluationResult(true, formatted, null, normalized, BuildSteps(normalized, scope, formatted));
        }
        catch (CalcException ex)
        {
            return EvaluationResult.Failure(ex.Message, normalized);
        }
        catch (Exception ex)
        {
            // The engine never lets an implementation surprise escape into the UI.
            return EvaluationResult.Failure(Errors.Unexpected(ex.Message), normalized);
        }
    }

    /// <inheritdoc />
    public bool TryEvaluate(string expression, out EvaluationResult result, EvaluationContext? context = null)
    {
        result = Evaluate(expression, context);
        return result.Ok;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SuggestCompletions(string prefix, int max = 12)
    {
        var limit = Math.Clamp(max, 0, 200);
        if (limit == 0)
        {
            return [];
        }

        var text = (prefix ?? string.Empty).Trim();
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddMatches(startsWith: true);
        if (results.Count < limit)
        {
            AddMatches(startsWith: false);
        }

        return results.Count > limit ? [.. results.Take(limit)] : results;

        void AddMatches(bool startsWith)
        {
            foreach (var candidate in Candidates())
            {
                if (results.Count >= limit)
                {
                    return;
                }

                if (candidate.Length == 0 || !seen.Add(candidate))
                {
                    continue;
                }

                var matches = text.Length == 0
                    || (startsWith
                        ? candidate.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                        : candidate.Contains(text, StringComparison.OrdinalIgnoreCase));
                if (!matches)
                {
                    seen.Remove(candidate);
                    continue;
                }

                results.Add(candidate);
            }
        }

        IEnumerable<string> Candidates()
        {
            foreach (var name in KnownFunctions)
            {
                yield return name;
            }

            foreach (var name in KnownConstants)
            {
                yield return name;
            }

            foreach (var name in KnownUnits)
            {
                yield return name;
            }
        }
    }

    /// <inheritdoc />
    public double SyntaxPlausibility(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0;
        }

        // Bound the work: handwriting candidates are short, anything longer is scored on its head.
        var text = expression.Length > 400 ? expression[..400] : expression;
        List<Token> tokens;
        try
        {
            tokens = Lexer.Tokenize(text);
        }
        catch (CalcException)
        {
            var position = Math.Max(0, FirstRejectedPosition(text));
            var ratio = (double)position / Math.Max(1, text.Length);
            return Math.Clamp(0.5 * ratio * ratio, 0, 0.5);
        }

        var recognized = RecognizedRatio(tokens);
        try
        {
            Parser.Parse(tokens);
            return Math.Clamp(0.75 + (0.25 * recognized), 0, 1);
        }
        catch (CalcException)
        {
            // A partially valid expression: keep it in the middle band so the picker can still offer it.
            var score = 0.6 * recognized;
            if (IsBalanced(tokens))
            {
                score += 0.1;
            }
            else
            {
                score -= 0.05;
            }

            if (EndsWithDanglingOperator(tokens))
            {
                score -= 0.15;
            }

            if (LongestParsablePrefixRatio(tokens) >= 0.5)
            {
                score += 0.05;
            }

            return Math.Clamp(score, 0, 0.7);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractAssignments(string expression)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
        {
            return names;
        }

        try
        {
            var tokens = Lexer.Tokenize(expression);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Kind == TokenKind.Identifier && tokens[i + 1].Kind == TokenKind.Assign)
                {
                    if (seen.Add(tokens[i].Text))
                    {
                        names.Add(tokens[i].Text);
                    }

                    // Skip the assigned expression so that "x = y = 3"-like text is not double counted.
                    while (i < tokens.Count && tokens[i].Kind != TokenKind.Semicolon)
                    {
                        i++;
                    }
                }
            }
        }
        catch (CalcException)
        {
            // Unparseable text simply has no assignments.
        }

        return names;
    }

    private static string FormatResult(Quantity value, EvaluationContext context)
    {
        var precision = Guards.Precision(context.Precision);
        return context.UnitAware ? value.Format(precision) : NumberFormatter.Format(value.Value, precision);
    }

    private static IReadOnlyList<string> BuildSteps(string normalized, EvalScope scope, string formatted)
    {
        var steps = new List<string> { $"标准化 / normalize：{normalized}" };
        if (scope.Trace.Count == 0)
        {
            steps.Add($"取数 / literal：{normalized}");
        }

        foreach (var step in scope.Trace)
        {
            steps.Add($"{Label(step.Kind)}：{step.Before} = {step.After}");
        }

        steps.Add($"格式化 / format：{formatted}");
        steps.Add($"结果 / result：{formatted}");
        return steps;

        static string Label(string kind) => kind switch
        {
            "assign" => "赋值 / assign",
            "convert" => "单位换算 / convert",
            _ => "括号与优先级 / precedence"
        };
    }

    private static double RecognizedRatio(List<Token> tokens)
    {
        var total = 0;
        var recognized = 0d;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.End:
                case TokenKind.Semicolon:
                    continue;
                case TokenKind.Identifier:
                    total++;
                    recognized += IsKnownName(token.Text) ? 1 : 0.6;
                    continue;
                default:
                    total++;
                    recognized += 1;
                    continue;
            }
        }

        return total == 0 ? 0 : Math.Clamp(recognized / total, 0, 1);
    }

    private static bool IsKnownName(string name) =>
        Functions.TryGet(name, out _)
        || Evaluator.Constants.TryGet(name, out _)
        || UnitCatalog.FindUnit(name) is not null;

    private static bool IsBalanced(List<Token> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.LeftParen)
            {
                depth++;
            }
            else if (token.Kind == TokenKind.RightParen)
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static bool EndsWithDanglingOperator(List<Token> tokens)
    {
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == TokenKind.End || tokens[i].Kind == TokenKind.Semicolon)
            {
                continue;
            }

            return tokens[i].Kind is TokenKind.Operator or TokenKind.LeftParen or TokenKind.Comma
                or TokenKind.Convert or TokenKind.Assign;
        }

        return false;
    }

    private static double LongestParsablePrefixRatio(List<Token> tokens)
    {
        var significant = tokens.Count - 1; // exclude End
        if (significant <= 1)
        {
            return 0;
        }

        for (var take = significant - 1; take >= 1; take--)
        {
            var slice = new List<Token>(tokens.GetRange(0, take))
            {
                new(TokenKind.End, string.Empty, 0, 0)
            };
            try
            {
                Parser.Parse(slice);
                return (double)take / significant;
            }
            catch (CalcException)
            {
                // keep shrinking
            }
        }

        return 0;
    }

    private static int FirstRejectedPosition(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || char.IsDigit(c) || c == '.' || c == ',' || c == '_'
                || c == '+' || c == '-' || c == '*' || c == '/' || c == '^' || c == '%' || c == '!'
                || c == '(' || c == ')' || c == ';' || c == '=' || c == '×' || c == '÷' || c == '−'
                || c == '·' || c == '√' || c == '∛' || c == '²' || c == '³' || c == '→' || c == '转'
                || Lexer.IsIdentifierStart(c))
            {
                continue;
            }

            return i;
        }

        return text.Length;
    }

    /// <summary>Formats a value the way the engine does, for keypads and panels.</summary>
    public string FormatValue(double value, int precision) => NumberFormatter.Format(value, precision);

    /// <summary>Formats a value with thousands separators, as the calculator display shows.</summary>
    public string FormatValueGrouped(double value, int precision) =>
        NumberFormatter.FormatGrouped(value, precision);

    /// <summary>Converts a stored variable text such as <c>3.2 m</c> back into a value.</summary>
    public bool TryParseValue(string? text, out double value, out string unit) =>
        NumberFormatter.TrySplitQuantity(text, out value, out unit);

    /// <summary>Culture used for every number the engine prints.</summary>
    public static CultureInfo NumberCulture => CultureInfo.InvariantCulture;

    /// <summary>Renders a human readable list of the supported operators, for help surfaces.</summary>
    public static string OperatorHelp => new StringBuilder()
        .Append("+ - × ÷ ^ % ! ( ) , ; = to")
        .ToString();
}
