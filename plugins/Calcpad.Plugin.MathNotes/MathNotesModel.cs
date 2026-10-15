using System.Globalization;
using System.Text.Json.Nodes;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;

namespace Calcpad.Plugin.MathNotes;

/// <summary>One line of a math notes block: what the user wrote plus its live result.</summary>
public sealed class MathNoteLine
{
    /// <summary>Expression text as written.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Formatted result, empty when the line is empty or failed.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Readable error message, empty when the line evaluated fine.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>True when the last evaluation failed.</summary>
    public bool IsError => Error.Length > 0;

    /// <summary>True when the line assigns a function of <c>x</c> and can be plotted.</summary>
    public bool IsPlottable =>
        Text.Contains('=', StringComparison.Ordinal)
        && Text[(Text.IndexOf('=', StringComparison.Ordinal) + 1)..].Contains('x', StringComparison.OrdinalIgnoreCase);

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
/// The multi line math block: every line is evaluated live, variables assigned on one line are
/// visible on the following lines, and a line that defines <c>y = f(x)</c> can be plotted.
/// </summary>
public sealed class MathNotesModel
{
    /// <summary>Largest number of lines a block accepts.</summary>
    public const int MaxLines = 64;

    private readonly IExpressionEngine _engine;

    /// <summary>Creates the model around an expression engine.</summary>
    public MathNotesModel(IExpressionEngine engine)
    {
        _engine = engine;
        Lines.Add(new MathNoteLine());
        Recompute();
    }

    /// <summary>The lines of the block.</summary>
    public List<MathNoteLine> Lines { get; } = [];

    /// <summary>Variables shared by the whole block, in evaluation order.</summary>
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Index of the line being edited.</summary>
    public int Selected { get; set; }

    /// <summary>Significant digits used for results.</summary>
    public int Precision { get; set; } = 12;

    /// <summary>Angle unit used by trigonometric functions.</summary>
    public AngleUnit AngleUnit { get; set; } = AngleUnit.Degrees;

    /// <summary>True when the plotting pane is open.</summary>
    public bool ShowGraph { get; set; }

    /// <summary>The plot window.</summary>
    public GraphView Graph { get; } = new();

    /// <summary>Coordinate readout shown under the plot.</summary>
    public string Readout { get; set; } = string.Empty;

    /// <summary>Transient hint shown at the bottom of the widget.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>The line being edited, or null when the block is empty.</summary>
    public MathNoteLine? SelectedLine => Selected >= 0 && Selected < Lines.Count ? Lines[Selected] : null;

    /// <summary>True when the selected line can be plotted.</summary>
    public bool CanPlot => SelectedLine?.IsPlottable == true;

    /// <summary>Re-evaluates every line, threading variables from top to bottom.</summary>
    public void Recompute()
    {
        Variables.Clear();
        foreach (var line in Lines)
        {
            Evaluate(line);
        }
    }

    /// <summary>Appends typed text to the selected line.</summary>
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
    public void MoveSelection(int delta)
    {
        Selected = Math.Clamp(Selected + delta, 0, Math.Max(0, Lines.Count - 1));
    }

    /// <summary>Selects a line by index.</summary>
    public void SelectLine(int index)
    {
        Selected = Math.Clamp(index, 0, Math.Max(0, Lines.Count - 1));
    }

    /// <summary>Clears the selected line, or removes it when it is already empty.</summary>
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

    /// <summary>Samples the selected line's function into a polyline.</summary>
    public (double[] Xs, double?[] Ys) Sample(int count)
    {
        var line = SelectedLine;
        if (line is null || !line.IsPlottable)
        {
            return ([], []);
        }

        var samples = Math.Clamp(count, 16, 512);
        var xs = new double[samples];
        var ys = new double?[samples];
        var source = line.RightHandSide;

        var hadX = Variables.TryGetValue("x", out var previousX);
        var buffer = new Dictionary<string, string>(Variables, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < samples; i++)
        {
            var x = Graph.XMin + ((Graph.XMax - Graph.XMin) * i / (samples - 1));
            xs[i] = x;
            buffer["x"] = NumberFormatter.Format(x, 15);
            var context = new EvaluationContext
            {
                Variables = buffer,
                Precision = Precision,
                AngleUnit = AngleUnit,
                UnitAware = true
            };
            var result = _engine.Evaluate(source, context);
            if (!result.Ok || result.Value is null)
            {
                ys[i] = null;
                continue;
            }

            ys[i] = NumberFormatter.TrySplitQuantity(result.Value, out var value, out _) ? value : null;
        }

        if (hadX)
        {
            Variables["x"] = previousX!;
        }
        else
        {
            Variables.Remove("x");
        }

        return (xs, ys);
    }

    /// <summary>Evaluates the selected function at one x, for the coordinate readout.</summary>
    public double? ValueAt(double x)
    {
        var line = SelectedLine;
        if (line is null || !line.IsPlottable)
        {
            return null;
        }

        var hadX = Variables.TryGetValue("x", out var previousX);
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
        var result = _engine.Evaluate(line.RightHandSide, context);
        if (hadX)
        {
            Variables["x"] = previousX!;
        }
        else
        {
            Variables.Remove("x");
        }

        if (!result.Ok || result.Value is null)
        {
            return null;
        }

        return NumberFormatter.TrySplitQuantity(result.Value, out var value, out _) ? value : null;
    }

    /// <summary>Formats the readout for a point in the plot.</summary>
    public void UpdateReadout(CPoint point)
    {
        var (x, y) = Graph.ScreenToWorld(point);
        var sampled = ValueAt(x);
        Readout = sampled is null
            ? $"x = {NumberFormatter.Format(x, 6)}"
            : $"x = {NumberFormatter.Format(x, 6)},  y = {NumberFormatter.Format(sampled.Value, 6)}";
    }

    /// <summary>Writes the model into a widget state object using JSON primitives only.</summary>
    public void Save(JsonObject target)
    {
        var array = new JsonArray();
        foreach (var line in Lines)
        {
            array.Add(new JsonObject { ["text"] = line.Text });
        }

        target["lines"] = array;
        target["selected"] = Selected;
        target["precision"] = Precision;
        target["angle"] = AngleUnit.ToString();
        target["showGraph"] = ShowGraph;
        target["readout"] = Readout;
        target["hint"] = Hint;
        var graph = new JsonObject();
        Graph.Save(graph);
        target["graph"] = graph;
        target["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the model back and re-evaluates it, tolerating missing or malformed state.</summary>
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
                var text = item is JsonObject obj && obj.TryGetPropertyValue("text", out var value) && value is not null
                    ? value.ToString()
                    : string.Empty;
                Lines.Add(new MathNoteLine { Text = text ?? string.Empty });
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
        AngleUnit = Enum.TryParse<AngleUnit>(ReadText(source, "angle", "Degrees"), true, out var angle)
            ? angle
            : AngleUnit.Degrees;
        ShowGraph = ReadBool(source, "showGraph");
        Readout = ReadText(source, "readout", string.Empty);
        Hint = ReadText(source, "hint", string.Empty);
        if (source.TryGetPropertyValue("graph", out var graphNode) && graphNode is JsonObject graphObject)
        {
            Graph.Load(graphObject);
        }

        Recompute();
    }

    /// <summary>Reads a model out of a widget object.</summary>
    public static MathNotesModel From(WidgetObject target, IExpressionEngine engine)
    {
        var model = new MathNotesModel(engine);
        model.Load(target.State);
        return model;
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

    private void Evaluate(MathNoteLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text))
        {
            line.Result = string.Empty;
            line.Error = string.Empty;
            return;
        }

        var context = new EvaluationContext
        {
            Variables = Variables,
            Precision = Precision,
            AngleUnit = AngleUnit,
            UnitAware = true
        };
        var result = _engine.Evaluate(line.Text, context);
        if (result.Ok && result.Value is not null)
        {
            line.Result = result.Value;
            line.Error = string.Empty;
            return;
        }

        line.Result = string.Empty;
        line.Error = result.Error ?? "无法计算 / cannot evaluate";
    }

    private static string ReadText(JsonObject source, string key, string fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : fallback;

    private static int ReadInt(JsonObject source, string key, int fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(JsonObject source, string key) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        bool.TryParse(node.ToString(), out var value) && value;
}
