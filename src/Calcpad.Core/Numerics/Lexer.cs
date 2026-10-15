using System.Globalization;
using System.Text;

namespace Calcpad.Core.Numerics;

/// <summary>Token kinds produced by the hand written lexer.</summary>
internal enum TokenKind
{
    /// <summary>A numeric literal, including scientific notation and digit separators.</summary>
    Number,

    /// <summary>A name: function, unit, constant or variable.</summary>
    Identifier,

    /// <summary>A binary or postfix operator.</summary>
    Operator,

    /// <summary><c>(</c>.</summary>
    LeftParen,

    /// <summary><c>)</c>.</summary>
    RightParen,

    /// <summary><c>,</c> argument separator.</summary>
    Comma,

    /// <summary><c>;</c> statement separator.</summary>
    Semicolon,

    /// <summary><c>=</c> assignment.</summary>
    Assign,

    /// <summary><c>to</c>, <c>转</c>, <c>-&gt;</c> or <c>→</c> unit conversion.</summary>
    Convert,

    /// <summary>Superscript two.</summary>
    Square,

    /// <summary>Superscript three.</summary>
    Cube,

    /// <summary><c>√</c>.</summary>
    Sqrt,

    /// <summary><c>∛</c>.</summary>
    Cbrt,

    /// <summary>End of input.</summary>
    End
}

/// <summary>One lexical token with its source position.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, double Number, int Position, int Length = 0)
{
    /// <summary>True when the token is the operator <paramref name="text"/>.</summary>
    public bool IsOperator(string text) =>
        Kind == TokenKind.Operator && string.Equals(Text, text, StringComparison.Ordinal);
}

/// <summary>
/// Hand written lexer for the calculator language. It understands the ASCII keyboard, the symbols
/// an iPad calculator shows (<c>×</c>, <c>÷</c>, <c>−</c>, <c>√</c>, <c>π</c>, <c>²</c>), digit
/// separators (<c>1,000</c>, <c>1_000_000</c>), Chinese unit names and the <c>转</c> conversion
/// keyword.
/// </summary>
internal static class Lexer
{
    /// <summary>Splits <paramref name="expression"/> into tokens, never looping forever.</summary>
    public static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var index = 0;
        var parenDepth = 0;
        var length = expression.Length;

        while (index < length)
        {
            var c = expression[index];

            if (char.IsWhiteSpace(c) || c == '\uFEFF')
            {
                index++;
                continue;
            }

            if (tokens.Count > 4096)
            {
                throw new CalcException(Errors.TooManyTokens);
            }

            if (char.IsDigit(c) || (c == '.' && index + 1 < length && char.IsDigit(expression[index + 1])))
            {
                tokens.Add(ReadNumber(expression, ref index, parenDepth));
                continue;
            }

            switch (c)
            {
                case '(':
                    parenDepth++;
                    tokens.Add(new Token(TokenKind.LeftParen, "(", 0, index++));
                    continue;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    tokens.Add(new Token(TokenKind.RightParen, ")", 0, index++));
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", 0, index++));
                    continue;
                case ';':
                case '\n':
                    tokens.Add(new Token(TokenKind.Semicolon, ";", 0, index++));
                    continue;
                case '=':
                    tokens.Add(new Token(TokenKind.Assign, "=", 0, index++));
                    continue;
                case '²':
                    tokens.Add(new Token(TokenKind.Square, "²", 0, index++));
                    continue;
                case '³':
                    tokens.Add(new Token(TokenKind.Cube, "³", 0, index++));
                    continue;
                case '√':
                    tokens.Add(new Token(TokenKind.Sqrt, "√", 0, index++));
                    continue;
                case '∛':
                    tokens.Add(new Token(TokenKind.Cbrt, "∛", 0, index++));
                    continue;
                case '+':
                case '*':
                case '/':
                case '^':
                case '%':
                case '!':
                    {
                        var position = index;
                        tokens.Add(new Token(TokenKind.Operator, NormalizeOperator(expression, ref index), 0, position));
                        continue;
                    }

                case '-':
                    if (index + 1 < length && expression[index + 1] == '>')
                    {
                        tokens.Add(new Token(TokenKind.Convert, "to", 0, index));
                        index += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Operator, "-", 0, index++));
                    }

                    continue;
                case '×':
                case '·':
                case '∗':
                    tokens.Add(new Token(TokenKind.Operator, "*", 0, index++));
                    continue;
                case '÷':
                    tokens.Add(new Token(TokenKind.Operator, "/", 0, index++));
                    continue;
                case '−':
                case '–':
                case '—':
                    tokens.Add(new Token(TokenKind.Operator, "-", 0, index++));
                    continue;
                case '→':
                    tokens.Add(new Token(TokenKind.Convert, "to", 0, index++));
                    continue;
                case '转':
                    tokens.Add(new Token(TokenKind.Convert, "to", 0, index++));
                    continue;
                case 'π':
                    tokens.Add(new Token(TokenKind.Identifier, "π", 0, index++, 1));
                    continue;
                case '&':
                case '|':
                    tokens.Add(new Token(TokenKind.Operator, c.ToString(), 0, index++, 1));
                    continue;
                case '$':
                case '€':
                case '£':
                case '¥':
                case '₩':
                case '₹':
                    // Currency prefix symbols behave like a unit name written before the number.
                    tokens.Add(new Token(TokenKind.Identifier, c.ToString(), 0, index++, 1));
                    continue;
                case '\'':
                case '"':
                    // Straight quotes typed for arcminutes / arcseconds.
                    tokens.Add(new Token(TokenKind.Identifier, c == '\'' ? "′" : "″", 0, index++, 1));
                    continue;
                default:
                    if (IsIdentifierStart(c))
                    {
                        tokens.Add(ReadIdentifier(expression, ref index, tokens));
                        continue;
                    }

                    throw new CalcException(Errors.UnexpectedCharacter(c));
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, 0, length));
        return tokens;
    }

    /// <summary>True when <paramref name="c"/> may start a name.</summary>
    public static bool IsIdentifierStart(char c) =>
        char.IsLetter(c) || c == '_' || c == '°' || c == '′' || c == '″' || c == 'π' || c == 'µ';

    /// <summary>
    /// True when <paramref name="c"/> may continue a name. The Chinese conversion keyword <c>转</c>
    /// is deliberately excluded so that <c>3米转厘米</c> lexes as three tokens.
    /// </summary>
    public static bool IsIdentifierPart(char c) =>
        (char.IsLetter(c) && c != '转') || char.IsDigit(c) || c == '_' || c == '°' || c == '′'
        || c == '″' || c == 'π' || c == 'µ';

    private static string NormalizeOperator(string text, ref int index)
    {
        var c = text[index];
        if (c == '*' && index + 1 < text.Length && text[index + 1] == '*')
        {
            index += 2;
            return "^";
        }

        index++;
        return c.ToString();
    }

    private static Token ReadNumber(string text, ref int index, int parenDepth)
    {
        var start = index;
        var builder = new StringBuilder();

        while (index < text.Length && char.IsDigit(text[index]))
        {
            builder.Append(text[index++]);
        }

        // Thousands separators: '_' everywhere, ',' only outside parentheses because a comma inside
        // an argument list is always an argument separator.
        while (index < text.Length &&
               (text[index] == '_' || (text[index] == ',' && parenDepth == 0)) &&
               IsThreeDigitGroup(text, index + 1))
        {
            index++;
            for (var i = 0; i < 3; i++)
            {
                builder.Append(text[index++]);
            }
        }

        if (index < text.Length && text[index] == '.' &&
            index + 1 < text.Length && char.IsDigit(text[index + 1]))
        {
            builder.Append('.');
            index++;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                builder.Append(text[index++]);
            }
        }
        else if (builder.Length > 0 && index < text.Length && text[index] == '.')
        {
            // Trailing dot, as in "1." which the iPad keypad produces while typing.
            builder.Append('.');
            index++;
        }

        if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
        {
            var lookahead = index + 1;
            var hasSign = lookahead < text.Length && (text[lookahead] == '+' || text[lookahead] == '-');
            var digitAt = hasSign ? lookahead + 1 : lookahead;
            if (digitAt < text.Length && char.IsDigit(text[digitAt]))
            {
                builder.Append('e');
                index++;
                if (hasSign)
                {
                    builder.Append(text[index++]);
                }

                while (index < text.Length && char.IsDigit(text[index]))
                {
                    builder.Append(text[index++]);
                }
            }
        }

        var literal = builder.ToString();
        if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new CalcException(Errors.UnexpectedToken(text[start..Math.Min(text.Length, index)]));
        }

        if (double.IsInfinity(value))
        {
            throw new CalcException(Errors.Overflow(string.Empty));
        }

        return new Token(TokenKind.Number, literal, value, start, index - start);
    }

    private static bool IsThreeDigitGroup(string text, int index)
    {
        if (index + 2 >= text.Length)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!char.IsDigit(text[index + i]))
            {
                return false;
            }
        }

        var after = index + 3;
        return after >= text.Length || !char.IsDigit(text[after]);
    }

    private static Token ReadIdentifier(string text, ref int index, List<Token> tokens)
    {
        var start = index;
        var builder = new StringBuilder();
        while (index < text.Length && IsIdentifierPart(text[index]))
        {
            builder.Append(text[index++]);
        }

        // "m²" is a catalogue unit but "x²" is the variable x squared: only merge the superscript when
        // the merged name really is a unit.
        while (index < text.Length && (text[index] == '²' || text[index] == '³'))
        {
            var candidate = builder.ToString() + text[index];
            if (UnitCatalog.FindUnit(candidate) is null)
            {
                break;
            }

            builder.Append(text[index++]);
        }

        var name = builder.ToString();
        var length = index - start;
        if (string.Equals(name, "to", StringComparison.OrdinalIgnoreCase) || name == "转")
        {
            return new Token(TokenKind.Convert, "to", 0, start, length);
        }

        // "per" is the spoken form of division: "miles per gallon".
        if (string.Equals(name, "per", StringComparison.OrdinalIgnoreCase))
        {
            return new Token(TokenKind.Operator, "/", 0, start, length);
        }

        // "1 percent" is the spoken form of the percent postfix.
        if (string.Equals(name, "percent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "percentage", StringComparison.OrdinalIgnoreCase))
        {
            return new Token(TokenKind.Operator, "%", 0, start, length);
        }

        if (string.Equals(name, "in", StringComparison.OrdinalIgnoreCase) && FollowsUnit(tokens, text, index))
        {
            // "4h in seconds" converts; a bare "4 in" stays inches.
            return new Token(TokenKind.Convert, "to", 0, start, length);
        }

        return new Token(TokenKind.Identifier, name, 0, start, length);
    }

    /// <summary>
    /// True when the word <c>in</c> is a conversion keyword: it must follow a unit (or a currency
    /// amount such as <c>$100</c>) and be followed by another unit name.
    /// </summary>
    private static bool FollowsUnit(List<Token> tokens, string text, int index)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var previous = tokens[^1];
        var hasUnitContext = previous.Kind switch
        {
            TokenKind.Identifier => UnitCatalog.FindUnit(previous.Text) is not null,
            TokenKind.Number => tokens.Count >= 2
                && tokens[^2].Kind == TokenKind.Identifier
                && UnitCatalog.FindUnit(tokens[^2].Text) is not null,
            _ => false
        };

        if (!hasUnitContext)
        {
            return false;
        }

        var next = index;
        while (next < text.Length && char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        return next < text.Length && (IsIdentifierStart(text[next]) || text[next] is '$' or '€' or '£' or '¥');
    }
}
