namespace Calcpad.Core.Numerics;

/// <summary>
/// One numeric literal inside a notes line, with the exact source span so a slider can write the new
/// value back into the text, plus a suggested range and step.
/// </summary>
/// <param name="Text">Literal exactly as written, for example <c>1,000</c> or <c>1.5e-3</c>.</param>
/// <param name="Start">UTF-16 start offset inside the line.</param>
/// <param name="Length">UTF-16 length inside the line.</param>
/// <param name="Value">Numeric value.</param>
/// <param name="Min">Suggested slider minimum.</param>
/// <param name="Max">Suggested slider maximum.</param>
/// <param name="Step">Suggested slider step, and the increment of the +/- buttons.</param>
/// <param name="IsInteger">True when the literal was written without a fractional part.</param>
public sealed record NumericLiteral(
    string Text,
    int Start,
    int Length,
    double Value,
    double Min,
    double Max,
    double Step,
    bool IsInteger)
{
    /// <summary>True when <paramref name="offset"/> falls inside this literal.</summary>
    public bool Contains(int offset) => offset >= Start && offset < Start + Length;
}

/// <summary>Result of one statement inside a line, so a multi statement line shows every value.</summary>
/// <param name="Text">Statement source text.</param>
/// <param name="Value">Formatted value, or null when the statement failed.</param>
/// <param name="Error">Readable error, or null when the statement succeeded.</param>
public sealed record StatementEvaluation(string Text, string? Value, string? Error);

/// <summary>
/// Everything a notes line needs: statement by statement values, the natural unit note, the
/// normalised expression, the trace and the numeric literals a slider can grab.
/// </summary>
/// <param name="Ok">True when the last statement succeeded.</param>
/// <param name="Value">Value of the last statement, formatted with its unit.</param>
/// <param name="Error">Readable error of the last failing statement.</param>
/// <param name="NormalizedExpression">Canonical text of the last statement.</param>
/// <param name="Steps">Displayable reduction steps of the last statement.</param>
/// <param name="Natural">Natural unit note such as <c>= 10.16 cm</c>, or null.</param>
/// <param name="Statements">Every statement on the line, in order.</param>
/// <param name="Literals">Numeric literals found on the line, in source order.</param>
/// <param name="Assignments">Names assigned by the line, in order.</param>
public sealed record LineEvaluation(
    bool Ok,
    string? Value,
    string? Error,
    string? NormalizedExpression,
    IReadOnlyList<string> Steps,
    string? Natural,
    IReadOnlyList<StatementEvaluation> Statements,
    IReadOnlyList<NumericLiteral> Literals,
    IReadOnlyList<string> Assignments);

/// <summary>
/// Line level helpers for Math Notes: splitting a line into statements the way the iPad app does
/// (spaces or semicolons), and locating the numeric literals a slider can edit.
/// </summary>
public static class LineAnalysis
{
    /// <summary>
    /// Splits a notes line into statements. Semicolons and newlines always separate; a
    /// <c>name =</c> that follows whitespace outside parentheses starts a new statement, which is how
    /// <c>a = 1 a = a + 1</c> and <c>width = 5 area = width * height =</c> work on one line.
    /// </summary>
    public static IReadOnlyList<string> SplitStatements(string? lineText)
    {
        var statements = new List<string>();
        if (string.IsNullOrWhiteSpace(lineText))
        {
            return statements;
        }

        var depth = 0;
        var start = 0;
        for (var i = 0; i < lineText.Length; i++)
        {
            var c = lineText[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 && (c == ';' || c == '\n'))
            {
                SplitPiece(lineText[start..i], statements);
                start = i + 1;
            }
        }

        SplitPiece(lineText[start..], statements);
        return statements;
    }

    /// <summary>Finds every numeric literal on a line, in source order.</summary>
    public static IReadOnlyList<NumericLiteral> FindNumericLiterals(string? lineText)
    {
        var literals = new List<NumericLiteral>();
        if (string.IsNullOrWhiteSpace(lineText))
        {
            return literals;
        }

        List<Token> tokens;
        try
        {
            tokens = Lexer.Tokenize(lineText);
        }
        catch (CalcException)
        {
            return literals;
        }

        foreach (var token in tokens)
        {
            if (token.Kind != TokenKind.Number || token.Length <= 0)
            {
                continue;
            }

            literals.Add(Describe(token));
        }

        return literals;
    }

    /// <summary>Builds the literal description including the suggested slider range.</summary>
    private static NumericLiteral Describe(Token token)
    {
        var value = token.Number;
        var isInteger = !token.Text.Contains('.', StringComparison.Ordinal)
            && !token.Text.Contains('e', StringComparison.OrdinalIgnoreCase);
        var magnitude = Math.Abs(value);
        var step = magnitude > 0 ? Math.Pow(10, Math.Floor(Math.Log10(magnitude)) - 1) : 1;
        if (isInteger && step < 1)
        {
            step = 1;
        }

        if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step))
        {
            step = 1;
        }

        var min = value >= 0 ? 0 : -10 * magnitude;
        var max = magnitude > 0 ? 10 * magnitude : 10;
        if (isInteger)
        {
            min = Math.Floor(min);
            max = Math.Ceiling(max);
            step = Math.Max(1, Math.Round(step));
        }

        if (max <= min)
        {
            max = min + (step * 10);
        }

        return new NumericLiteral(token.Text, token.Position, token.Length, value, min, max, step, isInteger);
    }

    private static void SplitPiece(string piece, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(piece))
        {
            return;
        }

        var depth = 0;
        var start = 0;
        for (var i = 0; i < piece.Length; i++)
        {
            var c = piece[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0 || i == start || !char.IsWhiteSpace(piece[i - 1]) || !Lexer.IsIdentifierStart(c))
            {
                continue;
            }

            var end = i;
            while (end < piece.Length && Lexer.IsIdentifierPart(piece[end]))
            {
                end++;
            }

            var after = end;
            while (after < piece.Length && char.IsWhiteSpace(piece[after]))
            {
                after++;
            }

            if (after >= piece.Length || piece[after] != '='
                || (after + 1 < piece.Length && piece[after + 1] == '='))
            {
                continue;
            }

            // A trailing '=' is the Math Notes "show the value" marker, not a new statement.
            var trailing = after + 1;
            while (trailing < piece.Length && char.IsWhiteSpace(piece[trailing]))
            {
                trailing++;
            }

            if (trailing >= piece.Length)
            {
                continue;
            }

            Add(target, piece[start..i]);
            start = i;
        }

        Add(target, piece[start..]);
    }

    private static void Add(List<string> target, string statement)
    {
        var trimmed = statement.Trim();
        if (trimmed.Length > 0)
        {
            target.Add(trimmed);
        }
    }
}
