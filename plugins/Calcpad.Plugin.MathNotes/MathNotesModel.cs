using System.Globalization;
using System.Text.Json.Nodes;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;

namespace Calcpad.Plugin.MathNotes;

/// <summary>
/// One curve drawn inside a line's inline graph. The curve keeps a reference to the line it came
/// from, so editing that line updates the graph live instead of freezing a copy of the equation.
/// </summary>
/// <param name="SourceLine">Index of the line holding the equation, or -1 when it is detached.</param>
/// <param name="ColorIndex">Palette index (0 warm orange, 1 blue, 2 green, 3 pink, 4 purple, 5 teal).</param>
/// <param name="Expression">Last known right hand side, used when the source line disappears.</param>
public sealed record GraphCurve(int SourceLine, int ColorIndex, string Expression);

/// <summary>Inline graph attached to one notes line.</summary>
public sealed class LineGraph
{
    /// <summary>Height of the plot area in logical units, changed by the resize handle.</summary>
    public double Height { get; set; } = 170;

    /// <summary>Width of the plot area; clamped to the page content width when drawn.</summary>
    public double Width { get; set; } = 460;

    /// <summary>The visible window of the plot.</summary>
    public GraphView View { get; } = new();

    /// <summary>Curves overlaid on this graph, each with its own colour.</summary>
    public List<GraphCurve> Curves { get; } = [];

    /// <summary>Height the graph occupies inside the page, including its header.</summary>
    public double TotalHeight => Height + 30;

    /// <summary>Serialises the graph into JSON primitives.</summary>
    public void Save(JsonObject target)
    {
        var curves = new JsonArray();
        foreach (var curve in Curves)
        {
            curves.Add(new JsonObject
            {
                ["sourceLine"] = curve.SourceLine,
                ["color"] = curve.ColorIndex,
                ["expression"] = curve.Expression
            });
        }

        target["curves"] = curves;
        target["width"] = Width;
        target["height"] = Height;
        var view = new JsonObject();
        View.Save(view);
        target["view"] = view;
    }

    /// <summary>Reads a graph back, tolerating missing or malformed fields.</summary>
    public static LineGraph Load(JsonObject? source)
    {
        var graph = new LineGraph();
        if (source is null)
        {
            return graph;
        }

        graph.Width = Read(source, "width", graph.Width);
        graph.Height = Math.Clamp(Read(source, "height", graph.Height), 90, 520);
        if (source.TryGetPropertyValue("view", out var viewNode) && viewNode is JsonObject viewObject)
        {
            graph.View.Load(viewObject);
        }

        if (source.TryGetPropertyValue("curves", out var curvesNode) && curvesNode is JsonArray curves)
        {
            foreach (var item in curves)
            {
                if (item is not JsonObject curve)
                {
                    continue;
                }

                var sourceLine = (int)Read(curve, "sourceLine", -1);
                var color = (int)Read(curve, "color", graph.Curves.Count % 6);
                var expression = curve.TryGetPropertyValue("expression", out var expressionNode) && expressionNode is not null
                    ? expressionNode.ToString()
                    : string.Empty;
                graph.Curves.Add(new GraphCurve(sourceLine, color, expression));
            }
        }

        return graph;
    }

    private static double Read(JsonObject source, string key, double fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>One line of a math notes page: what the user wrote plus its live result.</summary>
public sealed class MathNoteLine
{
    /// <summary>Expression text as written.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Formatted result, empty when the line is empty or failed.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Readable error message, empty when the line evaluated fine.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Natural unit note such as <c>= 10.16 cm</c>, empty when there is none.</summary>
    public string Natural { get; set; } = string.Empty;

    /// <summary>Ink object this line was recognised from, when it came from handwriting.</summary>
    public string? SourceObjectId { get; set; }

    /// <summary>Inline graph attached to this line, or null.</summary>
    public LineGraph? Graph { get; set; }

    /// <summary>True when the last evaluation failed.</summary>
    public bool IsError => Error.Length > 0;

    /// <summary>True when a result is available.</summary>
    public bool HasValue => Result.Length > 0;

    /// <summary>
    /// The iPad shows a result as soon as an equals sign is on the line; a bare quantity such as
    /// <c>4 in</c> also shows its value.
    /// </summary>
    public bool ShowsResult => Text.Contains('=', StringComparison.Ordinal) || IsBareQuantity;

    /// <summary>True when the line is just a number with a unit, for example <c>18 m</c>.</summary>
    public bool IsBareQuantity
    {
        get
        {
            var text = Text.Trim();
            if (text.Length == 0 || text.Contains('=', StringComparison.Ordinal))
            {
                return false;
            }

            var digits = 0;
            foreach (var c in text)
            {
                if (char.IsDigit(c))
                {
                    digits++;
                    continue;
                }

                if (c is '.' or ',' or '_' or '+' or '-' or ' ' or 'e' or 'E')
                {
                    continue;
                }

                // The first non numeric character ends the number; the rest must look like a unit.
                return digits > 0 && char.IsLetter(c);
            }

            return false;
        }
    }

    /// <summary>Right hand side of an assignment, or the whole text.</summary>
    public string RightHandSide
    {
        get
        {
            var index = Text.IndexOf('=', StringComparison.Ordinal);
            return index < 0 ? Text : Text[(index + 1)..].Trim();
        }
    }
}

/// <summary>
/// The numeric slider that appears when a number inside a line is tapped: it carries the exact span
/// of the literal so dragging rewrites that number in place, and everything below recomputes.
/// </summary>
public sealed class SliderState
{
    /// <summary>Index of the line being edited.</summary>
    public int Line { get; set; }

    /// <summary>UTF-16 start of the literal inside the line text.</summary>
    public int Start { get; set; }

    /// <summary>UTF-16 length of the literal.</summary>
    public int Length { get; set; }

    /// <summary>Current value shown by the slider.</summary>
    public double Value { get; set; }

    /// <summary>Lower bound of the slider.</summary>
    public double Min { get; set; }

    /// <summary>Upper bound of the slider.</summary>
    public double Max { get; set; }

    /// <summary>Step used by the slider and by the increment buttons.</summary>
    public double Step { get; set; } = 1;

    /// <summary>Ink object the line came from, when the slider was opened from handwriting.</summary>
    public string? SourceObjectId { get; set; }

    /// <summary>Fraction of the way between <see cref="Min"/> and <see cref="Max"/>.</summary>
    public double Fraction => Max > Min ? Math.Clamp((Value - Min) / (Max - Min), 0, 1) : 0;

    /// <summary>Serialises the slider into JSON primitives.</summary>
    public void Save(JsonObject target)
    {
        target["line"] = Line;
        target["start"] = Start;
        target["length"] = Length;
        target["value"] = Value;
        target["min"] = Min;
        target["max"] = Max;
        target["step"] = Step;
        target["sourceObjectId"] = SourceObjectId;
    }

    /// <summary>Reads a slider back, returning null when the state has none.</summary>
    public static SliderState? Load(JsonObject? source)
    {
        if (source is null || !source.TryGetPropertyValue("line", out var lineNode) || lineNode is null)
        {
            return null;
        }

        return new SliderState
        {
            Line = (int)Read(source, "line", 0),
            Start = (int)Read(source, "start", 0),
            Length = (int)Read(source, "length", 1),
            Value = Read(source, "value", 0),
            Min = Read(source, "min", 0),
            Max = Read(source, "max", 10),
            Step = Math.Max(1e-9, Read(source, "step", 1)),
            SourceObjectId = source.TryGetPropertyValue("sourceObjectId", out var ink) && ink is not null
                ? ink.ToString()
                : null
        };
    }

    private static double Read(JsonObject source, string key, double fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>
/// The mathematical view of a plot area: a world window plus the mapping to widget local pixels.
/// Kept free of Avalonia so zoom and pan behaviour can be tested directly.
/// </summary>
public sealed class GraphView
{
    /// <summary>Left edge of the visible world window.</summary>
    public double XMin { get; set; } = -10;

    /// <summary>Right edge of the visible world window.</summary>
    public double XMax { get; set; } = 10;

    /// <summary>Bottom edge of the visible world window.</summary>
    public double YMin { get; set; } = -10;

    /// <summary>Top edge of the visible world window.</summary>
    public double YMax { get; set; } = 10;

    /// <summary>Screen rectangle the window is drawn into.</summary>
    public CRect Area { get; set; } = new(0, 0, 100, 100);

    /// <summary>Smallest allowed span of either axis.</summary>
    public const double MinSpan = 1e-6;

    /// <summary>Largest allowed span of either axis.</summary>
    public const double MaxSpan = 1e9;

    /// <summary>Maps a world point to widget local pixels.</summary>
    public CPoint WorldToScreen(double x, double y)
    {
        var width = Math.Max(1e-9, XMax - XMin);
        var height = Math.Max(1e-9, YMax - YMin);
        return new CPoint(
            Area.X + ((x - XMin) / width * Area.Width),
            Area.Bottom - ((y - YMin) / height * Area.Height));
    }

    /// <summary>Maps widget local pixels back to world coordinates.</summary>
    public (double X, double Y) ScreenToWorld(CPoint point)
    {
        var width = Math.Max(1e-9, Area.Width);
        var height = Math.Max(1e-9, Area.Height);
        return (
            XMin + ((point.X - Area.X) / width * (XMax - XMin)),
            YMin + ((Area.Bottom - point.Y) / height * (YMax - YMin)));
    }

    /// <summary>Zooms while keeping the world point under <paramref name="anchor"/> fixed.</summary>
    public void ZoomAt(CPoint anchor, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor))
        {
            return;
        }

        var (worldX, worldY) = ScreenToWorld(anchor);
        var xSpan = Math.Clamp((XMax - XMin) * factor, MinSpan, MaxSpan);
        var ySpan = Math.Clamp((YMax - YMin) * factor, MinSpan, MaxSpan);
        var fx = Math.Clamp((anchor.X - Area.X) / Math.Max(1e-9, Area.Width), 0, 1);
        var fy = Math.Clamp((anchor.Y - Area.Top) / Math.Max(1e-9, Area.Height), 0, 1);

        XMin = worldX - (fx * xSpan);
        XMax = XMin + xSpan;
        YMax = worldY + (fy * ySpan);
        YMin = YMax - ySpan;
    }

    /// <summary>Pans by a screen space delta.</summary>
    public void PanByScreen(double screenDx, double screenDy)
    {
        var width = Math.Max(1e-9, Area.Width);
        var height = Math.Max(1e-9, Area.Height);
        var dx = screenDx / width * (XMax - XMin);
        var dy = screenDy / height * (YMax - YMin);
        XMin -= dx;
        XMax -= dx;
        YMin += dy;
        YMax += dy;
    }

    /// <summary>Moves the window so that it contains the given world rectangle.</summary>
    public void Fit(double xMin, double xMax, double yMin, double yMax)
    {
        if (xMax <= xMin || yMax <= yMin)
        {
            return;
        }

        XMin = xMin;
        XMax = xMax;
        YMin = yMin;
        YMax = yMax;
    }

    /// <summary>Restores the default window.</summary>
    public void Reset()
    {
        XMin = -10;
        XMax = 10;
        YMin = -10;
        YMax = 10;
    }

    /// <summary>Writes the window into a widget state object.</summary>
    public void Save(JsonObject target)
    {
        target["xmin"] = XMin;
        target["xmax"] = XMax;
        target["ymin"] = YMin;
        target["ymax"] = YMax;
    }

    /// <summary>Reads the window back, tolerating garbage.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        XMin = Read(source, "xmin", XMin);
        XMax = Read(source, "xmax", XMax);
        YMin = Read(source, "ymin", YMin);
        YMax = Read(source, "ymax", YMax);
        if (!(XMax > XMin) || !(YMax > YMin))
        {
            Reset();
        }
    }

    private static double Read(JsonObject source, string key, double fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>
/// One page of Math Notes: several lines, live evaluation the moment an equals sign appears,
/// variables shared down the page, numeric sliders that rewrite a literal in place and inline
/// graphs attached to the lines that define <c>y = f(x)</c>. Every page keeps its own state, so a
/// canvas can hold as many pages as the user likes.
/// </summary>
public sealed class MathNotesModel
{
    /// <summary>Largest number of lines a page accepts.</summary>
    public const int MaxLines = 64;

    /// <summary>Width of the paper content column in world units.</summary>
    public const double DefaultPageWidth = 520;

    /// <summary>Number of curve colours in the palette.</summary>
    public const int CurvePaletteSize = 6;

    private readonly ExpressionEngine _engine;

    /// <summary>Creates the model around an expression engine.</summary>
    public MathNotesModel(ExpressionEngine engine)
    {
        _engine = engine;
        Lines.Add(new MathNoteLine());
        Recompute();
    }

    /// <summary>The engine this page evaluates with.</summary>
    public ExpressionEngine Engine => _engine;

    /// <summary>The lines of the page.</summary>
    public List<MathNoteLine> Lines { get; } = [];

    /// <summary>Variables shared by the whole page, in evaluation order.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Index of the line being edited.</summary>
    public int Selected { get; set; }

    /// <summary>Significant digits used for results.</summary>
    public int Precision { get; set; } = 12;

    /// <summary>Angle unit used by trigonometric functions.</summary>
    public AngleUnit AngleUnit { get; set; } = AngleUnit.Degrees;

    /// <summary>Fixed content width of the sheet, independent of the canvas zoom.</summary>
    public double PageWidth { get; set; } = DefaultPageWidth;

    /// <summary>True when the in-page calculator keypad is shown.</summary>
    public bool ShowKeypad { get; set; }

    /// <summary>Vertical scroll offset of the page, in rows.</summary>
    public int Scroll { get; set; }

    /// <summary>The open numeric slider, or null.</summary>
    public SliderState? Slider { get; private set; }

    /// <summary>Coordinate readout shown under the plot that has focus.</summary>
    public string Readout { get; set; } = string.Empty;

    /// <summary>Transient hint shown at the bottom of the page.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>The line being edited, or null when the page is empty.</summary>
    public MathNoteLine? SelectedLine => Selected >= 0 && Selected < Lines.Count ? Lines[Selected] : null;

    /// <summary>Graph currently focused: the selected line's graph, else the first one on the page.</summary>
    public LineGraph? FocusGraph =>
        SelectedLine?.Graph ?? Lines.FirstOrDefault(l => l.Graph is not null)?.Graph;

    /// <summary>Compatibility shim: true when the selected line has a graph.</summary>
    public bool ShowGraph
    {
        get => SelectedLine?.Graph is not null;
        set
        {
            if (value)
            {
                InsertGraph(Selected);
            }
            else
            {
                RemoveGraph(Selected);
            }
        }
    }

    /// <summary>Compatibility shim exposing the focused graph's window.</summary>
    public GraphView Graph => FocusGraph?.View ?? new GraphView();

    /// <summary>True when the selected line can be plotted.</summary>
    public bool CanPlot => _engine.TryDescribeGraph(SelectedLine?.Text ?? string.Empty, out _, out _);

    /// <summary>True when the selected line already has a graph.</summary>
    public bool HasGraph => SelectedLine?.Graph is not null;

    /// <summary>Re-evaluates every line, threading variables from top to bottom.</summary>
    public void Recompute()
    {
        Variables.Clear();
        var context = new EvaluationContext
        {
            Variables = Variables,
            Precision = Precision,
            AngleUnit = AngleUnit,
            UnitAware = true
        };

        foreach (var line in Lines)
        {
            Evaluate(line, context);
        }

        if (Slider is not null && (Slider.Line < 0 || Slider.Line >= Lines.Count))
        {
            Slider = null;
        }
    }

    /// <summary>Appends typed text to the selected line and re-evaluates immediately.</summary>
    public void AppendText(string text)
    {
        var line = EnsureSelectedLine();
        if (line.Text.Length + text.Length > 400)
        {
            Hint = "这一行太长了 / line is too long";
            return;
        }

        line.Text += text;
        Hint = string.Empty;
        Recompute();
    }

    /// <summary>Removes the last character of the selected line.</summary>
    public void Backspace()
    {
        var line = SelectedLine;
        if (line is null)
        {
            return;
        }

        if (line.Text.Length == 0)
        {
            if (Lines.Count > 1)
            {
                Lines.RemoveAt(Selected);
                Selected = Math.Clamp(Selected, 0, Lines.Count - 1);
            }
        }
        else
        {
            line.Text = line.Text[..^1];
        }

        Recompute();
    }

    /// <summary>Inserts a new line below the selected one.</summary>
    public void NewLine()
    {
        if (Lines.Count >= MaxLines)
        {
            Hint = "行数已达上限 / too many lines";
            return;
        }

        Lines.Insert(Selected + 1, new MathNoteLine());
        Selected++;
        Recompute();
    }

    /// <summary>Moves the selection by <paramref name="delta"/> lines.</summary>
    public void MoveSelection(int delta) =>
        Selected = Math.Clamp(Selected + delta, 0, Math.Max(0, Lines.Count - 1));

    /// <summary>Selects a line by index.</summary>
    public void SelectLine(int index) => Selected = Math.Clamp(index, 0, Math.Max(0, Lines.Count - 1));

    /// <summary>Clears the selected line.</summary>
    public void ClearLine()
    {
        var line = SelectedLine;
        if (line is null)
        {
            return;
        }

        line.Text = string.Empty;
        Recompute();
    }

    /// <summary>Copies a result into the line below, creating it when needed.</summary>
    public bool InsertResultBelow(int index)
    {
        if (index < 0 || index >= Lines.Count)
        {
            return false;
        }

        var source = Lines[index];
        var text = source.IsError ? source.Error : source.Result;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (index + 1 >= Lines.Count)
        {
            if (Lines.Count >= MaxLines)
            {
                return false;
            }

            Lines.Add(new MathNoteLine());
        }

        var target = Lines[index + 1];
        target.Text = string.IsNullOrEmpty(target.Text) ? text : $"{target.Text}+{text}";
        Selected = index + 1;
        Hint = "已回填结果 / result filled in";
        Recompute();
        return true;
    }

    /// <summary>
    /// Appends a line from outside the page (handwriting recognition, an imported expression) and
    /// optionally binds it to the ink object it came from.
    /// </summary>
    public int AppendLine(string text, string? sourceObjectId = null)
    {
        if (Lines.Count >= MaxLines)
        {
            Hint = "行数已达上限 / too many lines";
            return -1;
        }

        var line = new MathNoteLine { Text = text ?? string.Empty, SourceObjectId = sourceObjectId };
        if (Lines.Count == 1 && Lines[0].Text.Length == 0 && Lines[0].Graph is null)
        {
            Lines[0] = line;
            Selected = 0;
        }
        else
        {
            Lines.Add(line);
            Selected = Lines.Count - 1;
        }

        Hint = sourceObjectId is null ? string.Empty : "已从手写插入 / inserted from handwriting";
        Recompute();
        return Selected;
    }

    /// <summary>Opens the numeric slider on one literal of a line.</summary>
    public bool OpenSlider(int lineIndex, int number = 1, string? sourceObjectId = null)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count)
        {
            return false;
        }

        var literals = _engine.FindNumericLiterals(Lines[lineIndex].Text);
        if (literals.Count == 0)
        {
            Hint = "这一行没有可调的数字 / no number to adjust on this line";
            return false;
        }

        var literal = literals[Math.Clamp(number - 1, 0, literals.Count - 1)];
        Selected = lineIndex;
        Slider = new SliderState
        {
            Line = lineIndex,
            Start = literal.Start,
            Length = literal.Length,
            Value = literal.Value,
            Min = literal.Min,
            Max = literal.Max,
            Step = literal.Step,
            SourceObjectId = sourceObjectId ?? Lines[lineIndex].SourceObjectId
        };
        Hint = string.Empty;
        return true;
    }

    /// <summary>Opens the slider on the line belonging to an ink object, if there is one.</summary>
    public bool OpenSliderForInk(string sourceObjectId, int number = 1)
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            if (string.Equals(Lines[i].SourceObjectId, sourceObjectId, StringComparison.Ordinal))
            {
                return OpenSlider(i, number, sourceObjectId);
            }
        }

        return false;
    }

    /// <summary>Closes the slider.</summary>
    public void CloseSlider() => Slider = null;

    /// <summary>Moves the slider by whole steps, clamped to its range.</summary>
    public bool NudgeSlider(int steps) =>
        Slider is not null && SetSliderValue(Slider.Value + (Slider.Step * steps));

    /// <summary>Sets the slider value, rewrites the literal in the line and recomputes the page.</summary>
    public bool SetSliderValue(double value)
    {
        var slider = Slider;
        if (slider is null || slider.Line < 0 || slider.Line >= Lines.Count)
        {
            return false;
        }

        var clamped = Math.Clamp(value, slider.Min, slider.Max);
        var line = Lines[slider.Line];
        var start = Math.Clamp(slider.Start, 0, line.Text.Length);
        var length = Math.Clamp(slider.Length, 0, line.Text.Length - start);
        var text = FormatSliderValue(clamped, slider.Step);
        line.Text = line.Text[..start] + text + line.Text[(start + length)..];

        // Re-locate the literal after the edit so dragging keeps working when the text got longer.
        var literals = _engine.FindNumericLiterals(line.Text);
        var best = literals
            .Where(l => l.Start <= start)
            .OrderByDescending(l => l.Start)
            .FirstOrDefault()
            ?? literals.FirstOrDefault(l => l.Start >= start)
            ?? (literals.Count > 0 ? literals[0] : null);
        if (best is not null)
        {
            slider.Start = best.Start;
            slider.Length = best.Length;
        }

        slider.Value = clamped;
        Recompute();
        return true;
    }

    /// <summary>Inserts an inline graph on a graphable line.</summary>
    public bool InsertGraph(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count)
        {
            return false;
        }

        var line = Lines[lineIndex];
        if (line.Graph is not null)
        {
            Selected = lineIndex;
            return true;
        }

        if (!_engine.TryDescribeGraph(line.Text, out var expression, out _))
        {
            Hint = "这一行不是 x 的函数，无法绘图 / this line is not a function of x";
            return false;
        }

        var graph = new LineGraph { Width = Math.Max(200, PageWidth - 24) };
        graph.Curves.Add(new GraphCurve(lineIndex, 0, expression));
        line.Graph = graph;
        Selected = lineIndex;
        Hint = "已插入图形 / graph inserted";
        return true;
    }

    /// <summary>Removes the inline graph from a line.</summary>
    public bool RemoveGraph(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is null)
        {
            return false;
        }

        Lines[lineIndex].Graph = null;
        Hint = "已移除图形 / graph removed";
        return true;
    }

    /// <summary>Toggles the inline graph of a line.</summary>
    public bool ToggleGraph(int lineIndex) =>
        lineIndex >= 0 && lineIndex < Lines.Count && Lines[lineIndex].Graph is not null
            ? RemoveGraph(lineIndex)
            : InsertGraph(lineIndex);

    /// <summary>
    /// Adds another line's equation to an existing graph as an extra coloured curve, which is how the
    /// iPad app overlays several equations on one graph.
    /// </summary>
    public bool AddCurve(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph)
        {
            return false;
        }

        for (var offset = 1; offset < Lines.Count; offset++)
        {
            foreach (var candidate in new[] { lineIndex + offset, lineIndex - offset })
            {
                if (candidate < 0 || candidate >= Lines.Count || candidate == lineIndex)
                {
                    continue;
                }

                if (!_engine.TryDescribeGraph(Lines[candidate].Text, out var expression, out _))
                {
                    continue;
                }

                if (graph.Curves.Any(c => string.Equals(Resolve(c, graph), expression, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                graph.Curves.Add(new GraphCurve(candidate, graph.Curves.Count % CurvePaletteSize, expression));
                Hint = "已叠加曲线 / curve added";
                return true;
            }
        }

        Hint = "没有其他 y = 行可以叠加 / no other y = line to overlay";
        return false;
    }

    /// <summary>Removes one curve from a graph.</summary>
    public bool RemoveCurve(int lineIndex, int curveIndex)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph
            || curveIndex < 0 || curveIndex >= graph.Curves.Count)
        {
            return false;
        }

        graph.Curves.RemoveAt(curveIndex);
        if (graph.Curves.Count == 0)
        {
            Lines[lineIndex].Graph = null;
        }

        Hint = "已移除曲线 / curve removed";
        return true;
    }

    /// <summary>Cycles the colour of a curve through the palette.</summary>
    public bool CycleCurveColor(int lineIndex, int curveIndex)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph
            || curveIndex < 0 || curveIndex >= graph.Curves.Count)
        {
            return false;
        }

        var curve = graph.Curves[curveIndex];
        graph.Curves[curveIndex] = curve with { ColorIndex = (curve.ColorIndex + 1) % CurvePaletteSize };
        return true;
    }

    /// <summary>Moves a graph onto another graphable line.</summary>
    public bool MoveGraph(int fromLine, int toLine)
    {
        if (fromLine == toLine || fromLine < 0 || fromLine >= Lines.Count || toLine < 0 || toLine >= Lines.Count)
        {
            return false;
        }

        var graph = Lines[fromLine].Graph;
        if (graph is null)
        {
            return false;
        }

        if (Lines[toLine].Graph is not null || !_engine.TryDescribeGraph(Lines[toLine].Text, out var expression, out _))
        {
            return false;
        }

        Lines[fromLine].Graph = null;
        // The new host line draws itself in slot 0; curves of other lines follow the graph.
        graph.Curves.RemoveAll(c => c.SourceLine == fromLine);
        graph.Curves.Insert(0, new GraphCurve(toLine, 0, expression));
        for (var i = 0; i < graph.Curves.Count; i++)
        {
            graph.Curves[i] = graph.Curves[i] with { ColorIndex = i % CurvePaletteSize };
        }

        Lines[toLine].Graph = graph;
        Selected = toLine;
        Hint = "图形已移动 / graph moved";
        return true;
    }

    /// <summary>Resizes the graph attached to a line.</summary>
    public void ResizeGraph(int lineIndex, double width, double height)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph)
        {
            return;
        }

        graph.Width = Math.Clamp(width, 200, Math.Max(220, PageWidth));
        graph.Height = Math.Clamp(height, 90, 520);
    }

    /// <summary>The expression a curve currently draws, following its source line live.</summary>
    public string Resolve(GraphCurve curve, LineGraph? graph = null)
    {
        _ = graph;
        if (curve.SourceLine >= 0 && curve.SourceLine < Lines.Count
            && _engine.TryDescribeGraph(Lines[curve.SourceLine].Text, out var expression, out _))
        {
            return expression;
        }

        return curve.Expression;
    }

    /// <summary>Samples one curve into a polyline over the graph's visible window.</summary>
    public (double[] Xs, double?[] Ys) Sample(int lineIndex, int curveIndex, int count)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph
            || curveIndex < 0 || curveIndex >= graph.Curves.Count)
        {
            return ([], []);
        }

        return SampleExpression(Resolve(graph.Curves[curveIndex], graph), graph.View, count);
    }

    /// <summary>Compatibility shim: samples the selected line's first curve.</summary>
    public (double[] Xs, double?[] Ys) Sample(int count)
    {
        var line = SelectedLine;
        if (line?.Graph is { Curves.Count: > 0 })
        {
            return Sample(Selected, 0, count);
        }

        return line is not null && _engine.TryDescribeGraph(line.Text, out var expression, out _)
            ? SampleExpression(expression, Graph, count)
            : ([], []);
    }

    /// <summary>Evaluates one curve at a given x.</summary>
    public double? ValueAt(int lineIndex, int curveIndex, double x)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph
            || curveIndex < 0 || curveIndex >= graph.Curves.Count)
        {
            return null;
        }

        return EvaluateAt(Resolve(graph.Curves[curveIndex], graph), x);
    }

    /// <summary>Compatibility shim: evaluates the selected line's function at <paramref name="x"/>.</summary>
    public double? ValueAt(double x)
    {
        var line = SelectedLine;
        if (line?.Graph is { Curves.Count: > 0 })
        {
            return ValueAt(Selected, 0, x);
        }

        return line is not null && _engine.TryDescribeGraph(line.Text, out var expression, out _)
            ? EvaluateAt(expression, x)
            : null;
    }

    /// <summary>Updates the coordinate readout for a point inside a graph.</summary>
    public void UpdateReadout(int lineIndex, CPoint point)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count || Lines[lineIndex].Graph is not { } graph)
        {
            return;
        }

        var (x, _) = graph.View.ScreenToWorld(point);
        var y = ValueAt(lineIndex, 0, x);
        Readout = y is null
            ? $"x = {NumberFormatter.Format(x, 6)}"
            : $"x = {NumberFormatter.Format(x, 6)},  y = {NumberFormatter.Format(y.Value, 6)}";
    }

    /// <summary>Compatibility shim: updates the readout using the focused graph.</summary>
    public void UpdateReadout(CPoint point)
    {
        var index = SelectedLine?.Graph is not null ? Selected : Lines.FindIndex(l => l.Graph is not null);
        if (index >= 0)
        {
            UpdateReadout(index, point);
        }
    }

    /// <summary>Writes the page into a widget state object using JSON primitives only.</summary>
    public void Save(JsonObject target)
    {
        var array = new JsonArray();
        foreach (var line in Lines)
        {
            var item = new JsonObject
            {
                ["text"] = line.Text,
                ["result"] = line.Result,
                ["error"] = line.Error,
                ["natural"] = line.Natural,
                ["sourceObjectId"] = line.SourceObjectId
            };
            if (line.Graph is { } graph)
            {
                var graphObject = new JsonObject();
                graph.Save(graphObject);
                item["graph"] = graphObject;
            }
            else
            {
                item["graph"] = null;
            }

            array.Add(item);
        }

        target["lines"] = array;
        target["selected"] = Selected;
        target["precision"] = Precision;
        target["angle"] = AngleUnit.ToString();
        target["readout"] = Readout;
        target["hint"] = Hint;
        target["scroll"] = Scroll;
        target["page"] = new JsonObject { ["width"] = PageWidth, ["keypad"] = ShowKeypad };
        if (Slider is { } slider)
        {
            var sliderObject = new JsonObject();
            slider.Save(sliderObject);
            target["slider"] = sliderObject;
        }
        else
        {
            target["slider"] = null;
        }

        target["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the page back and re-evaluates it, tolerating missing or malformed state.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        if (source.TryGetPropertyValue("lines", out var node) && node is JsonArray array)
        {
            Lines.Clear();
            foreach (var item in array)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                var line = new MathNoteLine
                {
                    Text = Text(obj, "text"),
                    SourceObjectId = obj.TryGetPropertyValue("sourceObjectId", out var ink) && ink is not null
                        ? ink.ToString()
                        : null
                };
                if (obj.TryGetPropertyValue("graph", out var graphNode) && graphNode is JsonObject graphObject)
                {
                    line.Graph = LineGraph.Load(graphObject);
                }

                Lines.Add(line);
                if (Lines.Count >= MaxLines)
                {
                    break;
                }
            }
        }

        if (Lines.Count == 0)
        {
            Lines.Add(new MathNoteLine());
        }

        Selected = Math.Clamp(ReadInt(source, "selected", 0), 0, Lines.Count - 1);
        Precision = Math.Clamp(ReadInt(source, "precision", 12), 1, 17);
        AngleUnit = Enum.TryParse<AngleUnit>(Text(source, "angle"), true, out var angle)
            ? angle
            : AngleUnit.Degrees;
        Readout = Text(source, "readout");
        Hint = Text(source, "hint");
        Scroll = Math.Max(0, ReadInt(source, "scroll", 0));
        if (source.TryGetPropertyValue("page", out var pageNode) && pageNode is JsonObject page)
        {
            PageWidth = Math.Clamp(ReadDouble(page, "width", DefaultPageWidth), 260, 900);
            ShowKeypad = page.TryGetPropertyValue("keypad", out var keypad) && keypad is not null
                && bool.TryParse(keypad.ToString(), out var visible) && visible;
        }

        Slider = source.TryGetPropertyValue("slider", out var sliderNode) && sliderNode is JsonObject sliderObject
            ? SliderState.Load(sliderObject)
            : null;

        Recompute();
    }

    /// <summary>Reads a page out of a widget object.</summary>
    public static MathNotesModel From(WidgetObject target, ExpressionEngine engine)
    {
        var model = new MathNotesModel(engine);
        model.Load(target.State);
        return model;
    }

    private void Evaluate(MathNoteLine line, EvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(line.Text))
        {
            line.Result = string.Empty;
            line.Error = string.Empty;
            line.Natural = string.Empty;
            return;
        }

        var evaluation = _engine.EvaluateLine(line.Text, context);
        line.Result = evaluation.Value ?? string.Empty;
        line.Error = evaluation.Ok ? string.Empty : evaluation.Error ?? "无法计算 / cannot evaluate";
        line.Natural = evaluation.Natural ?? string.Empty;
    }

    private (double[] Xs, double?[] Ys) SampleExpression(string expression, GraphView view, int count)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return ([], []);
        }

        var samples = Math.Clamp(count, 16, 512);
        var xs = new double[samples];
        var ys = new double?[samples];
        for (var i = 0; i < samples; i++)
        {
            var x = view.XMin + ((view.XMax - view.XMin) * i / (samples - 1));
            xs[i] = x;
            ys[i] = EvaluateAt(expression, x);
        }

        return (xs, ys);
    }

    private double? EvaluateAt(string expression, double x)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        var buffer = new Dictionary<string, string>(Variables, StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = NumberFormatter.Format(x, 15)
        };
        var context = new EvaluationContext
        {
            Variables = buffer,
            Precision = Precision,
            AngleUnit = AngleUnit,
            UnitAware = true
        };
        var result = _engine.Evaluate(expression, context);
        if (!result.Ok || result.Value is null)
        {
            return null;
        }

        return NumberFormatter.TrySplitQuantity(result.Value, out var value, out _) ? value : null;
    }

    private static string FormatSliderValue(double value, double step)
    {
        var decimals = step >= 1 ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(step)), 0, 6);
        return NumberFormatter.Format(Math.Round(value, decimals), 15);
    }

    private MathNoteLine EnsureSelectedLine()
    {
        if (Lines.Count == 0)
        {
            Lines.Add(new MathNoteLine());
            Selected = 0;
        }

        Selected = Math.Clamp(Selected, 0, Lines.Count - 1);
        return Lines[Selected];
    }

    private static string Text(JsonObject source, string key) =>
        source.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : string.Empty;

    private static int ReadInt(JsonObject source, string key, int fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ReadDouble(JsonObject source, string key, double fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
