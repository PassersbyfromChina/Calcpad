using Calcpad.Core.Numerics;

namespace Calcpad.Core.Ai;

/// <summary>Knobs of the post corrector.</summary>
public sealed record PostCorrectionOptions
{
    /// <summary>Beam width of the confusion pair search. Four keeps a phone responsive.</summary>
    public int BeamWidth { get; init; } = 4;

    /// <summary>Minimum plausibility gain required before a correction is applied.</summary>
    public double MinimumGain { get; init; } = 0.02;

    /// <summary>Maximum number of substitution sites the search explores.</summary>
    public int MaxSites { get; init; } = 8;

    /// <summary>Weight of the injected math engine when it is available.</summary>
    public double EngineWeight { get; init; } = 0.6;
}

/// <summary>Result of the post correction stage.</summary>
public sealed record PostCorrectionResult(
    string Text,
    double Plausibility,
    string Syntax,
    IReadOnlyList<CorrectionNote> Notes);

/// <summary>
/// Grammar and dictionary based correction. Two paths: <c>math</c> (confusion pairs plus an
/// expression plausibility score, using <see cref="IExpressionEngine.SyntaxPlausibility"/> when the
/// host injected an engine) and <c>text</c> (dictionary plus the multi character confusions
/// <c>rn/m</c>, <c>vv/w</c>, <c>cl/d</c>). Every accepted change produces a
/// <see cref="CorrectionNote"/> so the UI can show what was changed and why.
/// </summary>
public sealed class PostCorrector
{
    private static readonly Dictionary<char, char> SingleConfusions = new()
    {
        ['O'] = '0',
        ['o'] = '0',
        ['l'] = '1',
        ['I'] = '1',
        ['|'] = '1',
        ['S'] = '5',
        ['s'] = '5',
        ['Z'] = '2',
        ['z'] = '2',
        ['B'] = '8',
        ['g'] = '9',
        ['q'] = '9',
        ['b'] = '6'
    };

    private static readonly Dictionary<char, string> MathReasons = new()
    {
        ['0'] = "视觉混淆：字母被识别成 0 的位置应为数字（O/o/D → 0）",
        ['1'] = "视觉混淆：l/I/| 与 1 同形，数学上下文中按数字 1 处理",
        ['5'] = "视觉混淆：S/s 与 5 同形，按数字 5 处理",
        ['2'] = "视觉混淆：Z/z 与 2 同形，按数字 2 处理",
        ['8'] = "视觉混淆：B 与 8 同形，按数字 8 处理",
        ['9'] = "视觉混淆：g/q 与 9 同形，按数字 9 处理",
        ['6'] = "视觉混淆：b 与 6 同形，按数字 6 处理",
        ['7'] = "视觉混淆：T 与 7 同形，按数字 7 处理"
    };

    private static readonly (string From, string To, string Reason)[] MultiConfusions =
    [
        ("rn", "m", "连笔：rn 与 m 同形"),
        ("vv", "w", "连笔：vv 与 w 同形"),
        ("cl", "d", "连笔：cl 与 d 同形")
    ];

    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "again", "all", "also", "an", "and", "answer", "any", "are", "area", "as", "ask", "at",
        "back", "be", "because", "been", "before", "below", "between", "both", "but", "by", "calculate", "calculator",
        "can", "case", "change", "circle", "come", "compute", "convert", "cos", "could", "cube", "curve", "data",
        "day", "decimal", "degree", "do", "does", "done", "down", "each", "end", "equation", "error", "even", "every",
        "example", "expression", "factor", "false", "figure", "find", "first", "five", "for", "force", "form", "four",
        "from", "function", "get", "give", "go", "good", "graph", "half", "has", "have", "he", "help", "her", "here",
        "high", "his", "how", "if", "in", "input", "into", "is", "it", "its", "just", "keep", "know", "large", "last",
        "length", "less", "let", "like", "line", "list", "little", "log", "long", "look", "make", "many", "mass",
        "math", "matrix", "may", "mean", "meter", "might", "minus", "model", "more", "most", "much", "must", "my", "need",
        "new", "next", "no", "not", "note", "now", "number", "of", "off", "on", "one", "only", "open", "or", "other",
        "our", "out", "over", "own", "page", "part", "percent", "please", "plus", "point", "power", "press", "put",
        "question", "rate", "ratio", "read", "result", "right", "root", "round", "row", "run", "same", "save", "say",
        "scale", "see", "set", "seven", "shape", "she", "should", "show", "side", "simple", "sin", "since", "six",
        "size", "so", "solar", "some", "speed", "square", "step", "still", "such", "sum", "table", "take", "tan",
        "tell", "ten", "than", "that", "the", "their", "them", "then", "there", "these", "they", "thing", "think",
        "this", "three", "through", "time", "to", "together", "too", "total", "true", "try", "two", "under", "unit",
        "up", "use", "value", "variable", "very", "want", "was", "wave", "way", "we", "well", "were", "what", "when",
        "where", "which", "while", "who", "why", "will", "with", "word", "work", "would", "write", "year", "yes",
        "you", "your", "zero"
    };

    private static readonly HashSet<string> ChineseWords = new(StringComparer.Ordinal)
    {
        "一", "二", "三", "四", "五", "六", "七", "八", "九", "十", "百", "千", "万", "米", "厘米", "毫米", "秒",
        "分钟", "小时", "分", "时", "克", "千克", "斤", "元", "角", "加", "减", "乘", "除", "等于", "小于", "大于",
        "多少", "计算", "结果", "面积", "体积", "长度", "重量", "速度", "时间", "温度", "小数", "小数点", "分数",
        "平方", "立方", "开方", "周长", "直径", "半径", "圆周率", "函数", "方程", "变量", "单位", "换算", "数字"
    };

    private readonly PostCorrectionOptions _options;
    private readonly IExpressionEngine? _engine;

    /// <summary>Creates a corrector. <paramref name="engine"/> may be null: the internal validator is used then.</summary>
    public PostCorrector(PostCorrectionOptions? options = null, IExpressionEngine? engine = null)
    {
        _options = options ?? new PostCorrectionOptions();
        _engine = engine;
    }

    /// <summary>True when a host supplied math engine is used for plausibility scoring.</summary>
    public bool HasMathEngine => _engine is not null;

    /// <summary>Normalises look-alike code points (full width, U+2212 minus) before anything else runs.</summary>
    public static string NormalizeSymbols(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\u2212' => '-',
                '\u2013' or '\u2014' or '\uFF0D' => '-',
                '\uFF0B' => '+',
                '\u00D7' or '\u2715' or '\u2716' => '×',
                '\u00F7' => '÷',
                '\uFF1D' => '=',
                '\uFF0C' => ',',
                '\u3002' or '\uFF0E' => '.',
                '\uFF08' => '(',
                '\uFF09' => ')',
                '\uFF1A' => ':',
                '\uFF05' => '%',
                '\uFF0F' => '/',
                '\u3000' => ' ',
                _ => ch
            });
        }

        return builder.ToString();
    }

    /// <summary>Runs the appropriate correction path.</summary>
    public PostCorrectionResult Correct(string text, string? expectedSyntax, IReadOnlyList<string>? languages = null)
    {
        var normalized = NormalizeSymbols(text);
        var notes = new List<CorrectionNote>();
        if (!string.Equals(normalized, text, StringComparison.Ordinal))
        {
            notes.Add(new CorrectionNote(text, normalized, "规范化全角/特殊符号"));
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new PostCorrectionResult(normalized, 0, DetectSyntax(normalized, expectedSyntax), notes);
        }

        var syntax = DetectSyntax(normalized, expectedSyntax);
        var (corrected, extraNotes) = syntax == "math"
            ? CorrectMath(normalized)
            : CorrectText(normalized, languages);
        notes.AddRange(extraNotes);

        var plausibility = syntax == "math" ? MathPlausibility(corrected) : TextPlausibility(corrected);
        return new PostCorrectionResult(corrected, plausibility, syntax, notes);
    }

    /// <summary>Plausibility of an expression as math, combining the injected engine with the internal validator.</summary>
    public double MathPlausibility(string text)
    {
        var internalScore = InternalMathPlausibility(text);
        if (_engine is null)
        {
            return internalScore;
        }

        double engineScore;
        try
        {
            engineScore = Math.Clamp(_engine.SyntaxPlausibility(text), 0, 1);
        }
        catch (Exception)
        {
            return internalScore;
        }

        var weight = Math.Clamp(_options.EngineWeight, 0, 1);
        return (weight * engineScore) + ((1 - weight) * internalScore);
    }

    /// <summary>Score of a piece of natural language: dictionary hits and character class consistency.</summary>
    public static double TextPlausibility(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var stripped = text.Trim();
        if (ChineseWords.Contains(stripped))
        {
            return 1.0;
        }

        var words = SplitWords(stripped).ToList();
        if (words.Count == 0)
        {
            return 0.4;
        }

        var hits = words.Count(w => EnglishWords.Contains(w) || ChineseWords.Contains(w) || w.All(char.IsDigit));
        var score = 0.35 + (0.5 * hits / words.Count);

        // Mixed letter/digit tokens are a strong sign of a letter/digit confusion.
        if (words.Any(IsMixedToken))
        {
            score -= 0.25;
        }

        return Math.Clamp(score, 0, 1);
    }

    /// <summary>Detects whether the text should be corrected as math or as natural language.</summary>
    public static string DetectSyntax(string text, string? expectedSyntax)
    {
        if (string.Equals(expectedSyntax, "math", StringComparison.OrdinalIgnoreCase))
        {
            return "math";
        }

        if (string.Equals(expectedSyntax, "text", StringComparison.OrdinalIgnoreCase))
        {
            return "text";
        }

        var mathSymbols = 0;
        var letters = 0;
        var digits = 0;
        var cjk = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch) || ch is '.' or ',' or '+' or '-' or '*' or '/' or '=' or '^' or '(' or ')' or '%' or '!' or '√' or '×' or '÷')
            {
                if (char.IsDigit(ch))
                {
                    digits++;
                }
                else
                {
                    mathSymbols++;
                }
            }
            else if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                cjk++;
            }
            else if (char.IsLetter(ch))
            {
                letters++;
            }
        }

        if (mathSymbols > 0 && letters <= 1)
        {
            return "math";
        }

        if (cjk > 0)
        {
            return "text";
        }

        if (digits > 0 && letters == 0)
        {
            return "math";
        }

        return letters > digits ? "text" : "math";
    }

    private (string Text, List<CorrectionNote> Notes) CorrectMath(string text)
    {
        var sites = new List<(int Index, int Length, string Replacement, string Reason)>();

        void AddSite(int index, int length, string replacement, string reason)
        {
            if (sites.Count < _options.MaxSites)
            {
                sites.Add((index, length, replacement, reason));
            }
        }

        var hasDigit = text.Any(char.IsDigit);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is 'x' or 'X' && i > 0 && i < text.Length - 1 && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
            {
                AddSite(i, 1, "×", "视觉混淆：x 位于两个数字之间，按乘号处理");
                continue;
            }

            if (!SingleConfusions.TryGetValue(ch, out var replacement))
            {
                continue;
            }

            // Never rewrite a letter that is part of a longer word: "Sin(30)" stays "Sin(30)".
            var partOfIdentifier = (i > 0 && char.IsLetter(text[i - 1])) ||
                                   (i + 1 < text.Length && char.IsLetter(text[i + 1]));
            if (partOfIdentifier)
            {
                continue;
            }

            // Letters that sit in a numeric neighbourhood are misread digits. A stray capital in an
            // otherwise numeric expression (the "S" of a hand written "5+2") is treated the same way,
            // and the change is always reported as a CorrectionNote.
            var numericNeighbourhood = IsNumericNeighbourhood(text, i) || IsLastTokenNumeric(text, i);
            var strayCapital = hasDigit && char.IsUpper(ch);
            if (!numericNeighbourhood && !strayCapital)
            {
                continue;
            }

            AddSite(i, 1, replacement.ToString(), MathReasons.TryGetValue(replacement, out var reason)
                ? reason
                : $"视觉混淆：{ch} → {replacement}");
        }

        // A leading decimal point: ".5" reads better as "0.5" and only changes the value by nothing.
        if (text.StartsWith('.') && text.Length > 1 && char.IsDigit(text[1]))
        {
            AddSite(0, 0, "0", "补全小数点前的 0");
        }
        else if (text.StartsWith(",", StringComparison.Ordinal) && text.Length > 1 && char.IsDigit(text[1]))
        {
            AddSite(0, 1, "0.", "补全小数点前的 0（, 视为小数点）");
        }

        for (var i = 0; i < text.Length - 1; i++)
        {
            foreach (var (from, to, reason) in MultiConfusions)
            {
                if (string.CompareOrdinal(text, i, from, 0, from.Length) == 0)
                {
                    AddSite(i, from.Length, to, reason);
                }
            }
        }

        if (sites.Count == 0)
        {
            return (text, []);
        }

        var baseScore = MathPlausibility(text);
        var beam = new List<Candidate> { new(text, baseScore, []) };
        foreach (var site in sites.OrderByDescending(s => s.Index))
        {
            var next = new List<Candidate>();
            foreach (var candidate in beam)
            {
                next.Add(candidate);
                if (site.Index + site.Length > candidate.Text.Length)
                {
                    continue;
                }

                var replaced = string.Concat(
                    candidate.Text.AsSpan(0, site.Index),
                    site.Replacement,
                    candidate.Text.AsSpan(site.Index + site.Length));
                var notes = new List<CorrectionNote>(candidate.Notes)
                {
                    new(candidate.Text.Substring(site.Index, site.Length), site.Replacement, site.Reason)
                };
                next.Add(new Candidate(replaced, MathPlausibility(replaced), notes));
            }

            beam = next
                .OrderByDescending(c => c.Score)
                .Take(Math.Max(1, _options.BeamWidth))
                .ToList();
        }

        var winner = beam.OrderByDescending(c => c.Score).First();
        if (winner.Score <= baseScore + _options.MinimumGain ||
            string.Equals(winner.Text, text, StringComparison.Ordinal))
        {
            return (text, []);
        }

        return (winner.Text, BuildNotes(text, winner.Text, winner.Notes));
    }

    private (string Text, List<CorrectionNote> Notes) CorrectText(string text, IReadOnlyList<string>? languages)
    {
        var baseScore = TextPlausibility(text);
        var beam = new List<Candidate> { new(text, baseScore, []) };
        var sites = new List<(int Index, int Length, string Replacement, string Reason)>();

        for (var i = 0; i < text.Length - 1; i++)
        {
            foreach (var (from, to, reason) in MultiConfusions)
            {
                if (string.CompareOrdinal(text, i, from, 0, from.Length) == 0)
                {
                    sites.Add((i, from.Length, to, reason));
                }
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i]))
            {
                continue;
            }

            var insideWord = (i > 0 && char.IsLetter(text[i - 1])) || (i + 1 < text.Length && char.IsLetter(text[i + 1]));
            if (!insideWord)
            {
                continue;
            }

            var replacement = text[i] switch
            {
                '0' => "o",
                '1' => "l",
                '5' => "s",
                '2' => "z",
                '8' => "b",
                '9' => "g",
                '6' => "b",
                _ => null
            };
            if (replacement is not null)
            {
                sites.Add((i, 1, replacement, $"词典纠正：单词中的数字 {text[i]} 按字母 {replacement} 处理"));
            }
        }

        if (languages is { Count: > 0 } && languages.Any(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
        {
            // CJK text: a digit squeezed between two ideographs is almost always a misread character.
            for (var i = 1; i < text.Length - 1; i++)
            {
                if (char.IsDigit(text[i]) && IsCjk(text[i - 1]) && IsCjk(text[i + 1]))
                {
                    sites.Add((i, 1, "十", "中文上下文：数字位于汉字之间，按「十」处理"));
                }
            }
        }

        if (sites.Count == 0)
        {
            return (text, []);
        }

        foreach (var site in sites.OrderByDescending(s => s.Index).Take(_options.MaxSites))
        {
            var next = new List<Candidate>();
            foreach (var candidate in beam)
            {
                next.Add(candidate);
                if (site.Index + site.Length > candidate.Text.Length)
                {
                    continue;
                }

                var replaced = string.Concat(
                    candidate.Text.AsSpan(0, site.Index),
                    site.Replacement,
                    candidate.Text.AsSpan(site.Index + site.Length));
                var notes = new List<CorrectionNote>(candidate.Notes)
                {
                    new(candidate.Text.Substring(site.Index, site.Length), site.Replacement, site.Reason)
                };
                next.Add(new Candidate(replaced, TextPlausibility(replaced), notes));
            }

            beam = next.OrderByDescending(c => c.Score).Take(Math.Max(1, _options.BeamWidth)).ToList();
        }

        var winner = beam.OrderByDescending(c => c.Score).First();
        if (winner.Score <= baseScore + _options.MinimumGain || string.Equals(winner.Text, text, StringComparison.Ordinal))
        {
            return (text, []);
        }

        return (winner.Text, BuildNotes(text, winner.Text, winner.Notes));
    }

    private static bool IsCjk(char ch) => ch >= 0x4E00 && ch <= 0x9FFF;

    private static bool IsMixedToken(string word) =>
        word.Length > 1 && word.Any(char.IsLetter) && word.Any(char.IsDigit);

    private static IEnumerable<string> SplitWords(string text)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>
    /// Character level diff of the accepted changes. Regions that were rewritten are reported as a
    /// single note so the user sees "O.5 → 0.5" instead of two isolated edits.
    /// </summary>
    private static List<CorrectionNote> BuildNotes(string original, string corrected, List<CorrectionNote> candidateNotes)
    {
        if (string.Equals(original, corrected, StringComparison.Ordinal))
        {
            return [];
        }

        var prefix = 0;
        while (prefix < original.Length && prefix < corrected.Length && original[prefix] == corrected[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < original.Length - prefix && suffix < corrected.Length - prefix &&
               original[original.Length - 1 - suffix] == corrected[corrected.Length - 1 - suffix])
        {
            suffix++;
        }

        var originalSpan = original.Substring(prefix, original.Length - prefix - suffix);
        var correctedSpan = corrected.Substring(prefix, corrected.Length - prefix - suffix);
        var reason = candidateNotes.Count > 0
            ? string.Join("；", candidateNotes.Select(n => n.Reason).Distinct())
            : "语法/词典纠正";
        return [new CorrectionNote(originalSpan, correctedSpan, reason)];
    }

    private static bool IsNumericNeighbourhood(string text, int index)
    {
        var leftDigit = index > 0 && char.IsDigit(text[index - 1]);
        if (!leftDigit && index >= 2 && text[index - 1] == '.' && char.IsDigit(text[index - 2]))
        {
            leftDigit = true;
        }

        var rightDigit = index + 1 < text.Length && char.IsDigit(text[index + 1]);
        if (!rightDigit && index + 2 < text.Length && text[index + 1] == '.' && char.IsDigit(text[index + 2]))
        {
            // "O.5" reads as a number: the dot is part of the token that follows.
            rightDigit = true;
        }

        return leftDigit || rightDigit;
    }

    private static bool IsLastTokenNumeric(string text, int index)
    {
        if (index != text.Length - 1)
        {
            return false;
        }

        // A trailing single letter in an otherwise numeric expression, e.g. "2+3O" or "1l".
        for (var i = index - 1; i >= 0; i--)
        {
            if (char.IsDigit(text[i]))
            {
                return true;
            }

            if (!char.IsLetter(text[i]))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Internal structural validator, used when no math engine was injected. Rewards balanced
    /// parentheses, alternation between operands and operators, and proper number tokens.
    /// </summary>
    public static double InternalMathPlausibility(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var trimmed = text.Trim();
        var textHasDigit = trimmed.Any(char.IsDigit);
        var score = 1.0;
        var depth = 0;
        var previous = TokenKind.None;
        var i = 0;
        var sawDigit = false;
        var sawOperator = false;

        while (i < trimmed.Length)
        {
            var ch = trimmed[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (char.IsDigit(ch) || (ch == '.' && i + 1 < trimmed.Length && char.IsDigit(trimmed[i + 1])))
            {
                var isLeadingDot = ch == '.';
                var start = i;
                var dots = 0;
                while (i < trimmed.Length && (char.IsDigit(trimmed[i]) || trimmed[i] == '.'))
                {
                    if (trimmed[i] == '.')
                    {
                        dots++;
                    }

                    i++;
                }

                sawDigit = true;
                if (dots > 1)
                {
                    score -= 0.35;
                }

                if (isLeadingDot && start == 0)
                {
                    score -= 0.15; // legal, but "0.5" is what the user meant
                }

                if (trimmed[i - 1] == '.')
                {
                    score -= 0.2;
                }

                if (previous is TokenKind.Number or TokenKind.Identifier or TokenKind.Close)
                {
                    score -= 0.35;
                }

                previous = TokenKind.Number;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                var hasDigit = false;
                while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_'))
                {
                    hasDigit |= char.IsDigit(trimmed[i]);
                    i++;
                }

                var length = i - start;
                if (hasDigit && length > 1)
                {
                    score -= 0.4; // "l23", "O5"
                }

                // A lone letter that looks like a digit, inside an otherwise numeric expression, is
                // probably a misread digit: "S+2" is less plausible than "5+2". This is what makes the
                // correction measurable instead of a coin flip.
                if (length == 1 && textHasDigit && SingleConfusions.ContainsKey(trimmed[start]))
                {
                    score -= 0.22;
                }

                if (previous is TokenKind.Number or TokenKind.Identifier or TokenKind.Close)
                {
                    score -= 0.35;
                }

                previous = TokenKind.Identifier;
                continue;
            }

            switch (ch)
            {
                case '(':
                case '[':
                    depth++;
                    previous = TokenKind.Open;
                    i++;
                    continue;
                case ')':
                case ']':
                    depth--;
                    if (depth < 0)
                    {
                        score -= 0.4;
                        depth = 0;
                    }

                    previous = TokenKind.Close;
                    i++;
                    continue;
                case '+':
                case '-':
                case '*':
                case '/':
                case '^':
                case '×':
                case '÷':
                case '%':
                case '=':
                case '<':
                case '>':
                case ',':
                case '!':
                    sawOperator = true;
                    if (previous is TokenKind.Operator)
                    {
                        // Unary minus right after another operator or an opening bracket is fine.
                        score -= ch == '-' ? 0.05 : 0.2;
                    }

                    if (previous is TokenKind.None)
                    {
                        score -= ch == '-' || ch == '+' ? 0.02 : 0.25;
                    }

                    previous = TokenKind.Operator;
                    i++;
                    continue;
                case '√':
                case '|':
                    previous = TokenKind.Operator;
                    i++;
                    continue;
                default:
                    score -= 0.1;
                    previous = TokenKind.Other;
                    i++;
                    continue;
            }
        }

        if (depth != 0)
        {
            score -= 0.3 * Math.Abs(depth);
        }

        if (previous is TokenKind.Operator || previous is TokenKind.Open)
        {
            score -= 0.3;
        }

        if (sawDigit)
        {
            score += 0.1;
        }

        if (sawOperator)
        {
            score += 0.05;
        }

        return Math.Clamp(score, 0, 1);
    }

    private enum TokenKind
    {
        None,
        Number,
        Identifier,
        Operator,
        Open,
        Close,
        Other
    }

    private sealed record Candidate(string Text, double Score, List<CorrectionNote> Notes);
}
