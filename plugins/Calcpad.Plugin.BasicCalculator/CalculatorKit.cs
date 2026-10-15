using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;

namespace Calcpad.Plugins.CalculatorKit;

/// <summary>What a keypad key does when it is pressed.</summary>
public enum CalculatorKeyKind
{
    /// <summary>A digit 0-9.</summary>
    Digit,

    /// <summary>The decimal point.</summary>
    Dot,

    /// <summary>A binary operator.</summary>
    Operator,

    /// <summary>The equals key.</summary>
    Equals,

    /// <summary>Clear: clears the entry, or everything when the entry is empty.</summary>
    Clear,

    /// <summary>Backspace.</summary>
    Backspace,

    /// <summary>Sign change.</summary>
    Sign,

    /// <summary>Percent, with the semantics of the iPad calculator.</summary>
    Percent,

    /// <summary>A function applied to the value on screen.</summary>
    Function,

    /// <summary>A constant inserted at the cursor.</summary>
    Constant,

    /// <summary>The 2nd modifier.</summary>
    Second,

    /// <summary>Angle unit toggle (Deg / Rad / Grad).</summary>
    Angle,

    /// <summary>An opening or closing parenthesis.</summary>
    Paren,

    /// <summary>Random number generator.</summary>
    Random,

    /// <summary>Scientific exponent (EE).</summary>
    Exponent,

    /// <summary>A two argument operator typed as a prefix, for example nCr.</summary>
    Prefix,

    /// <summary>Page toggle of a paged (narrow) keypad. Handled by the surface, not by the state machine.</summary>
    Page
}

/// <summary>
/// One key of a keypad. <see cref="Row"/>/<see cref="Column"/> plus the spans define the grid cell;
/// the widget turns that into a rectangle, so no pixel geometry is stored in the manifest.
/// </summary>
public sealed record CalculatorKey(
    string Id,
    string Label,
    string? SecondLabel,
    CalculatorKeyKind Kind,
    int Row,
    int Column,
    int RowSpan = 1,
    int ColumnSpan = 1,
    string? Primary = null,
    string? Secondary = null);

/// <summary>The two keypads: the iPad portrait basic pad and the iPad landscape scientific pad.</summary>
public static class CalculatorKeypads
{
    /// <summary>Columns of the basic pad.</summary>
    public const int BasicColumns = 4;

    /// <summary>Rows of the basic pad.</summary>
    public const int BasicRows = 5;

    /// <summary>Columns of the scientific pad.</summary>
    public const int ScientificColumns = 10;

    /// <summary>Rows of the scientific pad.</summary>
    public const int ScientificRows = 5;

    /// <summary>iPad portrait keypad: AC ± % ÷ / 7 8 9 × / 4 5 6 − / 1 2 3 + / 0 . =</summary>
    public static IReadOnlyList<CalculatorKey> Basic { get; } =
    [
        new("ac", "AC", "AC", CalculatorKeyKind.Clear, 0, 0),
        new("sign", "±", "±", CalculatorKeyKind.Sign, 0, 1),
        new("percent", "%", "%", CalculatorKeyKind.Percent, 0, 2),
        new("div", "÷", "÷", CalculatorKeyKind.Operator, 0, 3, Primary: "/"),
        new("7", "7", "7", CalculatorKeyKind.Digit, 1, 0),
        new("8", "8", "8", CalculatorKeyKind.Digit, 1, 1),
        new("9", "9", "9", CalculatorKeyKind.Digit, 1, 2),
        new("mul", "×", "×", CalculatorKeyKind.Operator, 1, 3, Primary: "*"),
        new("4", "4", "4", CalculatorKeyKind.Digit, 2, 0),
        new("5", "5", "5", CalculatorKeyKind.Digit, 2, 1),
        new("6", "6", "6", CalculatorKeyKind.Digit, 2, 2),
        new("sub", "−", "−", CalculatorKeyKind.Operator, 2, 3, Primary: "-"),
        new("1", "1", "1", CalculatorKeyKind.Digit, 3, 0),
        new("2", "2", "2", CalculatorKeyKind.Digit, 3, 1),
        new("3", "3", "3", CalculatorKeyKind.Digit, 3, 2),
        new("add", "+", "+", CalculatorKeyKind.Operator, 3, 3, Primary: "+"),
        new("0", "0", "0", CalculatorKeyKind.Digit, 4, 0, ColumnSpan: 2),
        new("dot", ".", ".", CalculatorKeyKind.Dot, 4, 2),
        new("eq", "=", "=", CalculatorKeyKind.Equals, 4, 3)
    ];

    /// <summary>iPad landscape scientific keypad: functions on the left, digits on the right.</summary>
    public static IReadOnlyList<CalculatorKey> Scientific { get; } =
    [
        new("second", "2nd", "2nd", CalculatorKeyKind.Second, 0, 0),
        new("lparen", "(", "(", CalculatorKeyKind.Paren, 0, 1, Primary: "("),
        new("rparen", ")", ")", CalculatorKeyKind.Paren, 0, 2, Primary: ")"),
        new("back", "⌫", "⌫", CalculatorKeyKind.Backspace, 0, 3),
        new("ac", "AC", "AC", CalculatorKeyKind.Clear, 0, 4),
        new("sign", "±", "±", CalculatorKeyKind.Sign, 0, 5),
        new("percent", "%", "%", CalculatorKeyKind.Percent, 0, 6),
        new("ee", "EE", "EE", CalculatorKeyKind.Exponent, 0, 7),
        new("pi", "π", "e", CalculatorKeyKind.Constant, 0, 8, Primary: "π", Secondary: "e"),
        new("div", "÷", "÷", CalculatorKeyKind.Operator, 0, 9, Primary: "/"),

        new("sqr", "x²", "x³", CalculatorKeyKind.Function, 1, 0, Primary: "sqr", Secondary: "cube"),
        new("sqrt", "√x", "∛x", CalculatorKeyKind.Function, 1, 1, Primary: "sqrt", Secondary: "cbrt"),
        new("pow", "xʸ", "ʸ√x", CalculatorKeyKind.Function, 1, 2, Primary: "pow", Secondary: "root"),
        new("exp", "eˣ", "10ˣ", CalculatorKeyKind.Function, 1, 3, Primary: "exp", Secondary: "pow10"),
        new("ln", "ln", "log₂", CalculatorKeyKind.Function, 1, 4, Primary: "ln", Secondary: "log2"),
        new("7", "7", "7", CalculatorKeyKind.Digit, 1, 5),
        new("8", "8", "8", CalculatorKeyKind.Digit, 1, 6),
        new("9", "9", "9", CalculatorKeyKind.Digit, 1, 7),
        new("mul", "×", "×", CalculatorKeyKind.Operator, 1, 8, Primary: "*"),
        new("const-e", "e", "π", CalculatorKeyKind.Constant, 1, 9, Primary: "e", Secondary: "π"),

        new("sin", "sin", "sin⁻¹", CalculatorKeyKind.Function, 2, 0, Primary: "sin", Secondary: "asin"),
        new("cos", "cos", "cos⁻¹", CalculatorKeyKind.Function, 2, 1, Primary: "cos", Secondary: "acos"),
        new("tan", "tan", "tan⁻¹", CalculatorKeyKind.Function, 2, 2, Primary: "tan", Secondary: "atan"),
        new("log", "log", "logₙ", CalculatorKeyKind.Function, 2, 3, Primary: "log10", Secondary: "log"),
        new("recip", "1/x", "x!", CalculatorKeyKind.Function, 2, 4, Primary: "recip", Secondary: "fact"),
        new("4", "4", "4", CalculatorKeyKind.Digit, 2, 5),
        new("5", "5", "5", CalculatorKeyKind.Digit, 2, 6),
        new("6", "6", "6", CalculatorKeyKind.Digit, 2, 7),
        new("sub", "−", "−", CalculatorKeyKind.Operator, 2, 8, Primary: "-"),
        new("rand", "Rand", "mod", CalculatorKeyKind.Random, 2, 9, Primary: "rand", Secondary: "mod"),

        new("sinh", "sinh", "sinh⁻¹", CalculatorKeyKind.Function, 3, 0, Primary: "sinh", Secondary: "asinh"),
        new("cosh", "cosh", "cosh⁻¹", CalculatorKeyKind.Function, 3, 1, Primary: "cosh", Secondary: "acosh"),
        new("tanh", "tanh", "tanh⁻¹", CalculatorKeyKind.Function, 3, 2, Primary: "tanh", Secondary: "atanh"),
        new("abs", "|x|", "nCr", CalculatorKeyKind.Function, 3, 3, Primary: "abs", Secondary: "nCr"),
        new("npr", "nPr", "gcd", CalculatorKeyKind.Prefix, 3, 4, Primary: "nPr", Secondary: "gcd"),
        new("1", "1", "1", CalculatorKeyKind.Digit, 3, 5),
        new("2", "2", "2", CalculatorKeyKind.Digit, 3, 6),
        new("3", "3", "3", CalculatorKeyKind.Digit, 3, 7),
        new("add", "+", "+", CalculatorKeyKind.Operator, 3, 8, Primary: "+"),
        new("angle", "Deg", "Rad", CalculatorKeyKind.Angle, 3, 9),

        new("sum", "Σ", "avg", CalculatorKeyKind.Prefix, 4, 0, Primary: "sum", Secondary: "avg"),
        new("min", "min", "max", CalculatorKeyKind.Prefix, 4, 1, Primary: "min", Secondary: "max"),
        new("sqr2", "x²", "x³", CalculatorKeyKind.Function, 4, 2, Primary: "sqr", Secondary: "cube"),
        new("0", "0", "0", CalculatorKeyKind.Digit, 4, 3, ColumnSpan: 2),
        new("dot", ".", ".", CalculatorKeyKind.Dot, 4, 5),
        new("eq", "=", "=", CalculatorKeyKind.Equals, 4, 6, ColumnSpan: 4)
    ];
}

/// <summary>Grid maths for the keypads: cell to rectangle, and hit testing.</summary>
public static class KeypadGeometry
{
    /// <summary>Height of the display strip for the given widget size.</summary>
    public static double DisplayHeight(CSize size, bool scientific) =>
        Math.Clamp(size.Height * (scientific ? 0.24 : 0.28), 84, 200);

    /// <summary>Rectangle of one key inside <paramref name="size"/>.</summary>
    public static CRect RectFor(
        CSize size,
        CalculatorKey key,
        int rows,
        int columns,
        double gap,
        double padding,
        double displayHeight)
    {
        var availableWidth = Math.Max(1, size.Width - (padding * 2));
        var availableHeight = Math.Max(1, size.Height - displayHeight - (padding * 2));
        var cellWidth = Math.Max(1, (availableWidth - (gap * (columns - 1))) / columns);
        var cellHeight = Math.Max(1, (availableHeight - (gap * (rows - 1))) / rows);
        var x = padding + (key.Column * (cellWidth + gap));
        var y = displayHeight + padding + (key.Row * (cellHeight + gap));
        var width = (cellWidth * key.ColumnSpan) + (gap * (key.ColumnSpan - 1));
        var height = (cellHeight * key.RowSpan) + (gap * (key.RowSpan - 1));
        return new CRect(x, y, width, height);
    }

    /// <summary>True when the key is drawn as a circle rather than a rounded rectangle.</summary>
    public static bool IsRound(CalculatorKey key, CRect rect, bool roundKeypad) =>
        roundKeypad && key.RowSpan == 1 && key.ColumnSpan == 1 && rect.Width > 0;

    /// <summary>
    /// Rectangle of one key inside an explicit keypad area. Used by the calculator surface, whose
    /// display, memory row and convert rows sit above the keypad.
    /// </summary>
    public static CRect RectIn(CRect area, CalculatorKey key, int rows, int columns, double gap)
    {
        var columns1 = Math.Max(1, columns);
        var rows1 = Math.Max(1, rows);
        var cellWidth = Math.Max(1, (area.Width - (gap * (columns1 - 1))) / columns1);
        var cellHeight = Math.Max(1, (area.Height - (gap * (rows1 - 1))) / rows1);
        var x = area.X + (key.Column * (cellWidth + gap));
        var y = area.Y + (key.Row * (cellHeight + gap));
        var width = (cellWidth * key.ColumnSpan) + (gap * (key.ColumnSpan - 1));
        var height = (cellHeight * key.RowSpan) + (gap * (key.RowSpan - 1));
        return new CRect(x, y, width, height);
    }

    /// <summary>Finds the key under a point inside an explicit keypad area.</summary>
    public static CalculatorKey? HitTestIn(
        IReadOnlyList<CalculatorKey> keys,
        CRect area,
        int rows,
        int columns,
        CPoint point,
        double gap)
    {
        foreach (var key in keys)
        {
            if (RectIn(area, key, rows, columns, gap).Contains(point))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Finds the key under a point, using a circle test for round keys and a rectangle test otherwise.</summary>
    public static CalculatorKey? HitTest(
        IReadOnlyList<CalculatorKey> keys,
        CSize size,
        int rows,
        int columns,
        CPoint point,
        double gap,
        double padding,
        double displayHeight,
        bool roundKeypad)
    {
        foreach (var key in keys)
        {
            var rect = RectFor(size, key, rows, columns, gap, padding, displayHeight);
            if (IsRound(key, rect, roundKeypad))
            {
                var radius = Math.Min(rect.Width, rect.Height) / 2;
                var center = rect.Center;
                if (point.DistanceTo(center) <= radius)
                {
                    return key;
                }

                continue;
            }

            if (rect.Contains(point))
            {
                return key;
            }
        }

        return null;
    }
}

/// <summary>
/// The pure calculator state machine shared by the basic and the scientific keypad. It owns the
/// iPad behaviour: chained operations, repeated equals, percent semantics, sign toggle, backspace,
/// error state and the Deg/Rad/Grad indicator. It never touches Avalonia and keeps every mutable
/// field inside <see cref="WidgetObject.State"/> when <see cref="Save"/> is used.
/// </summary>
public sealed class CalculatorState
{
    private readonly IExpressionEngine _engine;

    /// <summary>Creates a state machine bound to an engine.</summary>
    public CalculatorState(IExpressionEngine engine) => _engine = engine;

    /// <summary>Committed part of the expression, for example <c>12 + </c>.</summary>
    public string Tape { get; set; } = string.Empty;

    /// <summary>Text the user is currently typing, which may be a whole sub-expression.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>Last expression that was evaluated with <c>=</c>.</summary>
    public string LastExpression { get; set; } = string.Empty;

    /// <summary>Last result, formatted.</summary>
    public string LastResult { get; set; } = string.Empty;

    /// <summary>Operator of the last evaluated binary operation, used by repeated equals.</summary>
    public string LastOperator { get; set; } = string.Empty;

    /// <summary>Right operand of the last evaluated binary operation.</summary>
    public string LastOperand { get; set; } = string.Empty;

    /// <summary>True while the display shows <c>Error</c>; only clear recovers from it.</summary>
    public bool IsError { get; set; }

    /// <summary>True right after <c>=</c>, so the next digit starts a new calculation.</summary>
    public bool JustEvaluated { get; set; }

    /// <summary>State of the 2nd modifier.</summary>
    public bool SecondActive { get; set; }

    /// <summary>Angle unit used for trigonometry.</summary>
    public AngleUnit AngleUnit { get; set; } = AngleUnit.Degrees;

    /// <summary>Significant digits of the formatted display.</summary>
    public int Precision { get; set; } = 12;

    /// <summary>Hint shown under the display, for example the history confirmation.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>
    /// Scientific memory register, shared by every mode of the calculator surface. It survives AC
    /// and only MC clears it, exactly like a desk calculator.
    /// </summary>
    public double Memory { get; set; }

    /// <summary>True when the memory register holds a value; MR is highlighted in that case.</summary>
    public bool HasMemory => double.IsFinite(Memory) && Math.Abs(Memory) > 1e-12;

    /// <summary>Formatted memory value, for the indicator next to the memory keys.</summary>
    public string MemoryText => HasMemory ? NumberFormatter.Format(Memory, Precision) : "0";

    /// <summary>Label of the clear key: <c>C</c> while there is something to clear, otherwise <c>AC</c>.</summary>
    public string ClearLabel => IsError || (Entry.Length == 0 && !JustEvaluated) ? "AC" : "C";

    /// <summary>Short label of the angle key.</summary>
    public string AngleLabel => AngleUnit switch
    {
        AngleUnit.Radians => "Rad",
        AngleUnit.Gradians => "Grad",
        _ => "Deg"
    };

    /// <summary>Expression that would be evaluated if <c>=</c> were pressed now.</summary>
    public string CurrentExpression => (Tape + Entry).Trim();

    /// <summary>Large display line.</summary>
    public string DisplayText
    {
        get
        {
            if (IsError)
            {
                return "Error";
            }

            if (Entry.Length > 0)
            {
                // A number being typed is shown exactly as typed; anything richer is previewed.
                if (IsPlainNumber(Entry))
                {
                    return Grouped(Entry);
                }

                return Preview(CurrentExpression) ?? Grouped(Entry);
            }

            if (LastResult.Length > 0)
            {
                return Grouped(LastResult);
            }

            return "0";
        }
    }

    /// <summary>Small expression line above the value.</summary>
    public string ExpressionText => IsError
        ? LastExpression
        : JustEvaluated && LastExpression.Length > 0
            ? $"{LastExpression} ="
            : CurrentExpression;

    /// <summary>Applies a key press.</summary>
    public void Press(CalculatorKey key)
    {
        switch (key.Kind)
        {
            case CalculatorKeyKind.Digit:
                AppendText(key.Label);
                break;
            case CalculatorKeyKind.Dot:
                AppendDot();
                break;
            case CalculatorKeyKind.Operator:
                ApplyOperator(key.Primary ?? key.Label);
                break;
            case CalculatorKeyKind.Equals:
                Evaluate();
                break;
            case CalculatorKeyKind.Clear:
                if (IsError || (Entry.Length == 0 && !JustEvaluated))
                {
                    ClearAll();
                }
                else
                {
                    ClearEntry();
                }

                break;
            case CalculatorKeyKind.Backspace:
                Backspace();
                break;
            case CalculatorKeyKind.Sign:
                ToggleSign();
                break;
            case CalculatorKeyKind.Percent:
                ApplyPercent();
                break;
            case CalculatorKeyKind.Function:
                ApplyFunction(SecondActive ? key.Secondary ?? key.Primary ?? key.Label : key.Primary ?? key.Label);
                break;
            case CalculatorKeyKind.Constant:
                AppendConstant(SecondActive ? key.Secondary ?? key.Primary ?? key.Label : key.Primary ?? key.Label);
                break;
            case CalculatorKeyKind.Second:
                SecondActive = !SecondActive;
                break;
            case CalculatorKeyKind.Angle:
                ToggleAngle();
                break;
            case CalculatorKeyKind.Paren:
                AppendText(key.Primary ?? key.Label);
                break;
            case CalculatorKeyKind.Random:
                ApplyRandom(SecondActive);
                break;
            case CalculatorKeyKind.Exponent:
                AppendExponent();
                break;
            case CalculatorKeyKind.Prefix:
                ApplyPrefix(SecondActive ? key.Secondary ?? key.Primary ?? key.Label : key.Primary ?? key.Label);
                break;
            case CalculatorKeyKind.Page:
                // Paging belongs to the surface widget, not to the expression state.
                break;
        }
    }

    /// <summary>Appends typed text (digits, letters, operators) to the entry.</summary>
    public void AppendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (IsError)
        {
            return;
        }

        if (JustEvaluated)
        {
            Entry = string.Empty;
            Tape = string.Empty;
            JustEvaluated = false;
        }

        Entry += text;
        Hint = string.Empty;
    }

    /// <summary>Clears the entry, or everything when the entry is already empty.</summary>
    public void ClearEntry()
    {
        if (Entry.Length > 0)
        {
            Entry = string.Empty;
            return;
        }

        ClearAll();
    }

    /// <summary>Resets the whole calculator, including the error state.</summary>
    public void ClearAll()
    {
        Tape = string.Empty;
        Entry = string.Empty;
        LastExpression = string.Empty;
        LastResult = string.Empty;
        LastOperator = string.Empty;
        LastOperand = string.Empty;
        IsError = false;
        JustEvaluated = false;
        SecondActive = false;
        Hint = string.Empty;
    }

    /// <summary>Removes the last character of the entry, then unwinds the pendulum operator.</summary>
    public void Backspace()
    {
        if (IsError)
        {
            ClearAll();
            return;
        }

        if (Entry.Length > 0)
        {
            Entry = Entry[..^1];
            return;
        }

        UnwindOperator();
    }

    private void UnwindOperator()
    {
        var trimmed = Tape.TrimEnd();
        if (trimmed.Length == 0)
        {
            return;
        }

        var lastOp = LastTopLevelOperatorIndex(trimmed);
        if (lastOp < 0)
        {
            Tape = string.Empty;
            Entry = trimmed;
            return;
        }

        var operand = trimmed[(lastOp + 1)..].Trim();
        var prefix = trimmed[..lastOp].TrimEnd();
        if (operand.Length == 0)
        {
            // "12 + " becomes "12" in the entry again, exactly like the iPad keypad.
            var previousOp = LastTopLevelOperatorIndex(prefix);
            operand = previousOp < 0 ? prefix : prefix[(previousOp + 1)..].Trim();
            prefix = previousOp < 0 ? string.Empty : prefix[..previousOp].TrimEnd();
        }

        Tape = prefix.Length == 0 ? string.Empty : prefix + " ";
        Entry = operand;
        JustEvaluated = false;
    }

    private static int LastTopLevelOperatorIndex(string text)
    {
        var depth = 0;
        for (var i = text.Length - 1; i > 0; i--)
        {
            var c = text[i];
            if (c == ')')
            {
                depth++;
                continue;
            }

            if (c == '(')
            {
                depth--;
                continue;
            }

            if (depth != 0 || c is not ('+' or '-' or '*' or '/' or '×' or '÷' or '−'))
            {
                continue;
            }

            // Skip a unary sign: it follows another operator or an opening parenthesis.
            var previous = text[i - 1];
            if (previous is '+' or '-' or '*' or '/' or '×' or '÷' or '−' or '(')
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Sign change. A plain number is wrapped as <c>(-9)</c> so the negation stays visible and can be
    /// edited character by character, exactly like the iPad keypad; a richer expression is wrapped
    /// whole as <c>-(…)</c>. Pressing it again unwraps.
    /// </summary>
    public void ToggleSign()
    {
        if (IsError)
        {
            return;
        }

        if (Entry.Length == 0)
        {
            if (JustEvaluated && LastResult.Length > 0)
            {
                Entry = ToggleWrapped(LastResult);
                JustEvaluated = false;
                return;
            }

            Entry = "(-";
            return;
        }

        Entry = ToggleWrapped(Entry);
    }

    /// <summary>Wraps a value in <c>(-…)</c>, or unwraps it again when it already is negated.</summary>
    public static string ToggleWrapped(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return "(-";
        }

        // While a group is still open there is nothing sensible to wrap, so the sign is toggled on
        // the text as typed and stays reversible.
        if (!IsBalanced(trimmed))
        {
            return trimmed.StartsWith('-') ? trimmed[1..] : "-" + trimmed;
        }

        if (trimmed.StartsWith("(-", StringComparison.Ordinal) && trimmed.EndsWith(')') && IsPlainNumber(trimmed[2..^1]))
        {
            return trimmed[2..^1];
        }

        if (trimmed.StartsWith("-(", StringComparison.Ordinal) && trimmed.EndsWith(')'))
        {
            return trimmed[1..];
        }

        if (IsPlainNumber(trimmed))
        {
            return trimmed.StartsWith('-') ? $"(-{trimmed[1..]})" : $"(-{trimmed})";
        }

        // A group or a whole expression: negate it as written, keeping the text readable.
        return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? "-" + trimmed : $"-({trimmed})";
    }

    /// <summary>True when every parenthesis in the text is closed.</summary>
    public static bool IsBalanced(string text)
    {
        var depth = 0;
        foreach (var c in text ?? string.Empty)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
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

    /// <summary>Value the memory keys operate on: whatever is on screen right now.</summary>
    public double CurrentValue()
    {
        if (Entry.Length > 0 && EvaluateNumber(Entry) is { } entryValue)
        {
            return entryValue;
        }

        if (LastResult.Length > 0 && EvaluateNumber(LastResult) is { } resultValue)
        {
            return resultValue;
        }

        return 0;
    }

    /// <summary>M+ : adds the value on screen to the memory register.</summary>
    public void MemoryAdd()
    {
        Memory = Finite(Memory + CurrentValue());
        Hint = $"M+ {MemoryText}";
    }

    /// <summary>M− : subtracts the value on screen from the memory register.</summary>
    public void MemorySubtract()
    {
        Memory = Finite(Memory - CurrentValue());
        Hint = $"M− {MemoryText}";
    }

    /// <summary>MC : clears the memory register. AC deliberately keeps the memory.</summary>
    public void MemoryClear()
    {
        Memory = 0;
        Hint = "MC";
    }

    /// <summary>MR : recalls the memory value into the expression being typed.</summary>
    public void MemoryRecall()
    {
        if (IsError)
        {
            ClearAll();
        }

        var text = Num(Memory);
        if (JustEvaluated)
        {
            Entry = text;
            Tape = string.Empty;
            JustEvaluated = false;
            Hint = $"MR {text}";
            return;
        }

        if (Entry.Length == 0)
        {
            Entry = text;
            Hint = $"MR {text}";
            return;
        }

        // Recalling acts as an operand: after an operator it appends, otherwise it replaces what is
        // being typed so two numbers are never glued together.
        var last = Entry[^1];
        Entry = last is '+' or '-' or '*' or '/' or '×' or '÷' or '−' or '(' ? Entry + text : text;
        Hint = $"MR {text}";
    }

    /// <summary>
    /// Pulls the last evaluated expression back into the entry so it can be edited again; this is
    /// what tapping the result line does on the iPad.
    /// </summary>
    public bool RecallLastForEdit()
    {
        if (IsError)
        {
            ClearAll();
        }

        if (LastExpression.Length == 0)
        {
            return false;
        }

        Tape = string.Empty;
        Entry = LastExpression;
        JustEvaluated = false;
        Hint = "继续编辑 / editing";
        return true;
    }

    /// <summary>
    /// Appends the closing parentheses the user left open, so <c>cos(</c> then <c>0.6</c> then <c>=</c>
    /// evaluates as <c>cos(0.6)</c> - the behaviour of the iPad keypad.
    /// </summary>
    public static string AutoClose(string expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return string.Empty;
        }

        var depth = 0;
        foreach (var c in expression)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
        }

        return depth == 0 ? expression : expression + new string(')', depth);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>Commits the entry and stores the pending operator.</summary>
    public void ApplyOperator(string op)
    {
        if (IsError || string.IsNullOrEmpty(op))
        {
            return;
        }

        // After EE the sign belongs to the exponent, exactly like the iPad keypad.
        if (Entry.Length > 0 && (Entry[^1] == 'e' || Entry[^1] == 'E') && op is "+" or "-")
        {
            Entry += op;
            return;
        }

        if (JustEvaluated)
        {
            Tape = Entry.Length > 0 ? Entry : LastResult;
            Tape = Tape.Length > 0 ? Tape + " " + op + " " : string.Empty;
            Entry = string.Empty;
            JustEvaluated = false;
            LastOperator = op;
            return;
        }

        if (Entry.Length == 0)
        {
            if (Tape.TrimEnd().Length == 0)
            {
                return;
            }

            // Replace the pending operator instead of stacking a second one.
            var trimmed = Tape.TrimEnd();
            var lastSpace = trimmed.LastIndexOf(' ');
            Tape = lastSpace > 0 ? trimmed[..lastSpace].TrimEnd() + " " + op + " " : trimmed + " " + op + " ";
            LastOperator = op;
            return;
        }

        Tape = Tape + Entry + " " + op + " ";
        Entry = string.Empty;
        LastOperator = op;
    }

    /// <summary>Evaluates the current expression, repeating the last operation when nothing is pending.</summary>
    public void Evaluate()
    {
        if (IsError)
        {
            return;
        }

        var expression = AutoClose(CurrentExpression);
        if (expression.Length == 0)
        {
            if (LastExpression.Length == 0 || LastOperator.Length == 0 || LastOperand.Length == 0)
            {
                return;
            }

            expression = $"{LastResult} {LastOperator} {LastOperand}";
        }

        var context = new EvaluationContext
        {
            AngleUnit = AngleUnit,
            Precision = Precision,
            UnitAware = true
        };

        var result = _engine.Evaluate(expression, context);
        if (!result.Ok || string.IsNullOrEmpty(result.Value))
        {
            IsError = true;
            LastExpression = expression;
            Tape = string.Empty;
            Entry = string.Empty;
            Hint = result.Error ?? "无法计算 / cannot evaluate";
            return;
        }

        LastExpression = expression;
        LastResult = result.Value;
        RememberRepeat(expression);
        Tape = string.Empty;
        Entry = string.Empty;
        IsError = false;
        JustEvaluated = true;
    }

    /// <summary>Percent with the semantics of the iPad calculator.</summary>
    public void ApplyPercent()
    {
        if (IsError)
        {
            return;
        }

        var value = EntryValue();
        var pending = PendingOperator();
        if (pending is "+" or "-")
        {
            var left = EvaluateNumber(Tape.TrimEnd().TrimEnd('+', '-', '*', '/').TrimEnd());
            if (left.HasValue)
            {
                Entry = Num(left.Value * value / 100);
                return;
            }
        }

        Entry = Num(value / 100);
        JustEvaluated = false;
    }

    /// <summary>
    /// Applies a function. With a value on screen it wraps it (<c>0.6</c> then <c>cos</c> gives
    /// <c>cos(0.6)</c>); with nothing on screen it opens the call (<c>cos(</c>) and waits for the
    /// argument. Both halves of the iPad behaviour therefore work.
    /// </summary>
    public void ApplyFunction(string name)
    {
        if (IsError || string.IsNullOrEmpty(name))
        {
            return;
        }

        var hasOperand = Entry.Length > 0 || (JustEvaluated && LastResult.Length > 0);
        if (!hasOperand)
        {
            Entry = PrefixForm(name) ?? "(";
            JustEvaluated = false;
            Hint = string.Empty;
            return;
        }

        var current = Entry.Length > 0 ? Entry : LastResult;
        Entry = name switch
        {
            "sqr" => $"{Wrap(current)}²",
            "cube" => $"{Wrap(current)}³",
            "recip" => $"1/({current})",
            "fact" => $"{Wrap(current)}!",
            "pow" => $"{current}^",
            "root" => $"{current}^(1/",
            "pow10" => $"10^({current})",
            "sqrt" => $"√({current})",
            "cbrt" => $"∛({current})",
            _ => $"{name}({current})"
        };

        JustEvaluated = false;
        Hint = string.Empty;
    }

    /// <summary>Prefix spelling of a function, or null when the key only works on an existing value.</summary>
    private static string? PrefixForm(string name) => name switch
    {
        "sqr" or "cube" or "fact" or "recip" or "pow" or "root" => null,
        "sqrt" => "√(",
        "cbrt" => "∛(",
        "pow10" => "10^(",
        _ => $"{name}("
    };

    /// <summary>Applies a two argument function typed as a prefix.</summary>
    public void ApplyPrefix(string name)
    {
        if (IsError || string.IsNullOrEmpty(name))
        {
            return;
        }

        Entry = Entry.Length == 0 ? $"{name}(" : $"{name}({Entry}, ";
        JustEvaluated = false;
    }

    /// <summary>Inserts a random number, either alone or as a factor of the current entry.</summary>
    public void ApplyRandom(bool useModulo)
    {
        if (IsError)
        {
            return;
        }

        if (useModulo)
        {
            ApplyPrefix("mod");
            return;
        }

        Entry = Entry.Length == 0 || JustEvaluated ? "rand()" : $"{Entry}*rand()";
        JustEvaluated = false;
    }

    /// <summary>Inserts a constant.</summary>
    public void AppendConstant(string name)
    {
        if (IsError || string.IsNullOrEmpty(name))
        {
            return;
        }

        if (JustEvaluated)
        {
            Entry = string.Empty;
            Tape = string.Empty;
            JustEvaluated = false;
        }

        Entry += name;
    }

    /// <summary>Cycles the angle unit: degrees, radians, gradians.</summary>
    public void ToggleAngle() => AngleUnit = AngleUnit switch
    {
        AngleUnit.Degrees => AngleUnit.Radians,
        AngleUnit.Radians => AngleUnit.Gradians,
        _ => AngleUnit.Degrees
    };

    /// <summary>Serialises the state into a widget state object.</summary>
    public void Save(JsonObject target)
    {
        target["tape"] = Tape;
        target["entry"] = Entry;
        target["lastExpression"] = LastExpression;
        target["lastResult"] = LastResult;
        target["lastOperator"] = LastOperator;
        target["lastOperand"] = LastOperand;
        target["error"] = IsError;
        target["justEvaluated"] = JustEvaluated;
        target["second"] = SecondActive;
        target["angle"] = AngleUnit.ToString();
        target["precision"] = Precision;
        target["hint"] = Hint;
        target["memory"] = Memory;
    }

    /// <summary>Reads the state back, tolerating missing or malformed fields.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        Tape = Text(source, "tape");
        Entry = Text(source, "entry");
        LastExpression = Text(source, "lastExpression");
        LastResult = Text(source, "lastResult");
        LastOperator = Text(source, "lastOperator");
        LastOperand = Text(source, "lastOperand");
        IsError = Flag(source, "error");
        JustEvaluated = Flag(source, "justEvaluated");
        SecondActive = Flag(source, "second");
        Hint = Text(source, "hint");
        Precision = Math.Clamp(Int(source, "precision", 12), 1, 17);
        Memory = double.IsFinite(Number(source, "memory", 0)) ? Number(source, "memory", 0) : 0;
        AngleUnit = Enum.TryParse<AngleUnit>(Text(source, "angle"), true, out var angle) ? angle : AngleUnit.Degrees;
    }

    /// <summary>Reads a state machine out of a widget object, creating defaults when needed.</summary>
    public static CalculatorState From(WidgetObject target, IExpressionEngine engine)
    {
        var state = new CalculatorState(engine);
        state.Load(target.State);
        return state;
    }

    private static string Text(JsonObject source, string key) =>
        source.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : string.Empty;

    private static bool Flag(JsonObject source, string key) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        bool.TryParse(node.ToString(), out var value) && value;

    private static int Int(JsonObject source, string key, int fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double Number(JsonObject source, string key, double fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private void AppendDot()
    {
        if (IsError)
        {
            return;
        }

        if (JustEvaluated)
        {
            Entry = "0.";
            Tape = string.Empty;
            JustEvaluated = false;
            return;
        }

        var current = CurrentNumberFragment();
        if (current.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        Entry += current.Length == 0 ? "0." : ".";
    }

    private void AppendExponent()
    {
        if (IsError)
        {
            return;
        }

        var current = CurrentNumberFragment();
        if (current.Length == 0 || current.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Entry += "e";
    }

    private string CurrentNumberFragment()
    {
        var index = Entry.Length;
        while (index > 0 && (char.IsDigit(Entry[index - 1]) || Entry[index - 1] == '.' || Entry[index - 1] == 'e'
            || Entry[index - 1] == 'E' || Entry[index - 1] == '-' || Entry[index - 1] == '+'))
        {
            index--;
        }

        return Entry[index..];
    }

    private string? Preview(string expression)
    {
        if (expression.Length == 0)
        {
            return null;
        }

        var context = new EvaluationContext { AngleUnit = AngleUnit, Precision = Precision, UnitAware = true };
        var result = _engine.Evaluate(AutoClose(expression), context);
        return result.Ok ? result.Value : null;
    }

    private double EntryValue()
    {
        var parsed = EvaluateNumber(Entry.Length > 0 ? Entry : LastResult);
        return parsed ?? 0;
    }

    private double? EvaluateNumber(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var context = new EvaluationContext { AngleUnit = AngleUnit, Precision = Precision, UnitAware = true };
        var result = _engine.Evaluate(expression, context);
        if (!result.Ok || result.Value is null)
        {
            return null;
        }

        return NumberFormatter.TrySplitQuantity(result.Value, out var value, out _) ? value : null;
    }

    private string PendingOperator()
    {
        var trimmed = Tape.TrimEnd();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        for (var i = trimmed.Length - 1; i >= 0; i--)
        {
            if (trimmed[i] is '+' or '-' or '*' or '/' or '×' or '÷' or '−')
            {
                return trimmed[i] switch
                {
                    '×' => "*",
                    '÷' => "/",
                    '−' => "-",
                    _ => trimmed[i].ToString()
                };
            }

            if (char.IsDigit(trimmed[i]) || trimmed[i] == ')')
            {
                break;
            }
        }

        return string.Empty;
    }

    private void RememberRepeat(string expression)
    {
        var opIndex = -1;
        var depth = 0;
        var op = string.Empty;
        for (var i = expression.Length - 1; i >= 0; i--)
        {
            var c = expression[i];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(')
            {
                depth--;
            }
            else if (depth == 0 && c is '+' or '-' or '*' or '/' or '×' or '÷' or '−')
            {
                opIndex = i;
                op = c switch
                {
                    '×' => "*",
                    '÷' => "/",
                    '−' => "-",
                    _ => c.ToString()
                };
                break;
            }
        }

        if (opIndex > 0)
        {
            var left = expression[..opIndex].Trim();
            var right = expression[(opIndex + 1)..].Trim();
            if (left.Length > 0 && right.Length > 0)
            {
                LastOperator = op;
                LastOperand = right;
            }
        }
    }

    private string Num(double value) => NumberFormatter.Format(value, Precision);

    private static string Negate(string text) => text.StartsWith('-') ? text[1..] : "-" + text;

    private static string Wrap(string text)
    {
        var trimmed = text.Trim();
        var needsParens = trimmed.Length > 1 && !trimmed.All(c => char.IsDigit(c) || c == '.');
        return needsParens ? $"({trimmed})" : trimmed;
    }

    private static string Grouped(string text) =>
        NumberFormatter.TrySplitQuantity(text, out var value, out var unit)
            ? string.IsNullOrEmpty(unit)
                ? NumberFormatter.FormatGrouped(value, 12)
                : $"{NumberFormatter.FormatGrouped(value, 12)} {unit}"
            : text;

    private static bool IsPlainNumber(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        var hasDigit = false;
        foreach (var c in text)
        {
            if (char.IsDigit(c))
            {
                hasDigit = true;
                continue;
            }

            if (c is not ('.' or '-' or '+' or 'e' or 'E'))
            {
                return false;
            }
        }

        return hasDigit && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }
}

/// <summary>
/// Maps the key names a shell reports onto the logical keys of the keypads, accepting both Avalonia
/// style names (<c>D7</c>, <c>Back</c>, <c>Return</c>) and literal characters.
/// </summary>
public static class CalculatorKeyboard
{
    /// <summary>Normalises a raw key name; returns an empty string when the key is not ours.</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var text = key.Trim();
        return text switch
        {
            "Back" or "BackSpace" or "⌫" => "Backspace",
            "Return" or "Enter" or "NumPadEnter" or "=" => "=",
            "Escape" or "Delete" or "Clear" => "Clear",
            "Add" => "+",
            "Subtract" or "OemMinus" => "-",
            "Multiply" => "*",
            "Divide" or "OemQuestion" => "/",
            "Decimal" or "OemPeriod" or "NumPadDecimal" => ".",
            "Percent" or "Oem5" => "%",
            "D0" or "NumPad0" => "0",
            "D1" or "NumPad1" => "1",
            "D2" or "NumPad2" => "2",
            "D3" or "NumPad3" => "3",
            "D4" or "NumPad4" => "4",
            "D5" or "NumPad5" => "5",
            "D6" or "NumPad6" => "6",
            "D7" or "NumPad7" => "7",
            "D8" or "NumPad8" => "8",
            "D9" or "NumPad9" => "9",
            _ => text
        };
    }
}
