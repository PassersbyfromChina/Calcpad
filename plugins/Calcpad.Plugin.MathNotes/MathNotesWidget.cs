using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.MathNotes;

/// <summary>Geometry of one editable row, plus the graph and slider that hang off it.</summary>
public sealed record MathNoteRowLayout(
    int Index,
    CRect Row,
    CRect TextArea,
    CRect ResultArea,
    CRect GraphButton,
    CRect GraphHeader,
    CRect GraphArea,
    CRect GraphResize,
    CRect CurveAdd,
    IReadOnlyList<CRect> CurveChips);

/// <summary>Geometry of the numeric slider shown under its line.</summary>
public sealed class SliderLayout
{
    /// <summary>Line the slider edits.</summary>
    public int Line { get; init; }

    /// <summary>Panel background.</summary>
    public CRect Panel { get; init; }

    /// <summary>Slider track.</summary>
    public CRect Track { get; init; }

    /// <summary>Draggable knob.</summary>
    public CRect Knob { get; init; }

    /// <summary>Decrement button.</summary>
    public CRect Minus { get; init; }

    /// <summary>Increment button.</summary>
    public CRect Plus { get; init; }

    /// <summary>Close button.</summary>
    public CRect Close { get; init; }

    /// <summary>Current value readout.</summary>
    public CRect Value { get; init; }

    /// <summary>Lower bound label.</summary>
    public CRect MinLabel { get; init; }

    /// <summary>Upper bound label.</summary>
    public CRect MaxLabel { get; init; }
}

/// <summary>
/// Paper-like geometry of a Math Notes page: fixed content width, steady line rhythm, row sized
/// graphs and the slider that opens under the line it edits.
/// </summary>
public sealed class MathNotesLayout
{
    private const double RowHeight = 34;
    private const double SliderHeight = 52;
    private const double GraphHeaderHeight = 30;
    private const double PageMargin = 12;
    private const double PagePadding = 14;
    private const double GraphButtonWidth = 52;

    /// <summary>The sheet of paper.</summary>
    public CRect Page { get; init; }

    /// <summary>Title row.</summary>
    public CRect Title { get; init; }

    /// <summary>Button that creates another page.</summary>
    public CRect NewNoteButton { get; init; }

    /// <summary>Button that shows or hides the in-page keypad.</summary>
    public CRect KeypadToggle { get; init; }

    /// <summary>Graph button of the selected row; kept for compatibility with older callers.</summary>
    public CRect PlotButton { get; init; }

    /// <summary>Visible rows.</summary>
    public IReadOnlyList<MathNoteRowLayout> Rows { get; init; } = [];

    /// <summary>Slider under the row it edits, or null.</summary>
    public SliderLayout? Slider { get; init; }

    /// <summary>Plot area of the selected row; kept for compatibility with older callers.</summary>
    public CRect GraphArea { get; init; }

    /// <summary>Readout strip.</summary>
    public CRect Readout { get; init; }

    /// <summary>In-page keypad area; empty while hidden.</summary>
    public CRect Keypad { get; init; }

    /// <summary>Keypad keys.</summary>
    public IReadOnlyList<(string Action, CRect Rect)> Keys { get; init; } = [];

    /// <summary>First line index that is visible.</summary>
    public int FirstVisible { get; init; }

    /// <summary>Computes the layout of the page for a widget of the given logical size.</summary>
    public static MathNotesLayout Compute(
        CSize size,
        IReadOnlyList<MathNoteLine> lines,
        int selected,
        double pageWidth,
        SliderState? slider,
        bool keypad,
        int scroll = 0)
    {
        var width = Math.Max(180, size.Width);
        var height = Math.Max(120, size.Height);
        var content = Math.Min(Math.Max(240, pageWidth), Math.Max(200, width - (PageMargin * 2)));
        var page = new CRect((width - content) / 2, PageMargin / 2, content, Math.Max(80, height - PageMargin));
        var title = new CRect(page.X + PagePadding, page.Y + 10, Math.Max(40, page.Width - (PagePadding * 2)), 26);

        var newNote = new CRect(title.Right - 58, title.Y + 2, 58, 22);
        var keypadToggle = new CRect(newNote.X - 56, title.Y + 2, 50, 22);

        var footer = page.Bottom - 8;
        var readout = new CRect(title.X, footer - 15, title.Width, 15);
        var keypadRect = CRect.Empty;
        var keys = new List<(string, CRect)>();
        if (keypad)
        {
            const double keyHeight = 30;
            const double keyGap = 5;
            var keypadHeight = (KeypadRows.Length * keyHeight) + ((KeypadRows.Length - 1) * keyGap);
            keypadRect = new CRect(title.X, footer - 16 - keypadHeight, title.Width, keypadHeight);
            for (var row = 0; row < KeypadRows.Length; row++)
            {
                var columns = KeypadRows[row].Length;
                var cellWidth = (keypadRect.Width - (keyGap * (columns - 1))) / columns;
                for (var column = 0; column < columns; column++)
                {
                    keys.Add((KeypadRows[row][column], new CRect(
                        keypadRect.X + (column * (cellWidth + keyGap)),
                        keypadRect.Y + (row * (keyHeight + keyGap)),
                        cellWidth,
                        keyHeight)));
                }
            }
        }

        var rowsBottom = (keypad ? keypadRect.Y : footer) - 18;
        var rows = new List<MathNoteRowLayout>();
        var y = title.Bottom + 6;
        var first = Math.Max(0, Math.Clamp(scroll, 0, Math.Max(0, lines.Count - 1)));
        var selectedGraph = CRect.Empty;
        var selectedButton = CRect.Empty;

        for (var i = first; i < lines.Count; i++)
        {
            var line = lines[i];
            var graphHeight = line.Graph is null ? 0 : GraphHeaderHeight + line.Graph.Height;
            var sliderHeight = slider is not null && slider.Line == i ? SliderHeight : 0;
            var total = RowHeight + graphHeight + sliderHeight;
            if (y + total > rowsBottom + 0.5 && rows.Count > 0)
            {
                break;
            }

            var row = new CRect(title.X, y, title.Width, RowHeight);
            var resultWidth = Math.Min(row.Width * 0.42, 200);
            var graphButton = new CRect(row.Right - resultWidth - GraphButtonWidth - 4, row.Y + 6, GraphButtonWidth, 22);
            var resultArea = new CRect(row.Right - resultWidth, row.Y + 4, resultWidth, row.Height - 8);

            var graphHeader = CRect.Empty;
            var graphArea = CRect.Empty;
            var resize = CRect.Empty;
            var curveAdd = CRect.Empty;
            var chips = new List<CRect>();
            if (line.Graph is { } graph)
            {
                var plotWidth = Math.Min(graph.Width, title.Width);
                graphHeader = new CRect(row.X, row.Bottom, plotWidth, GraphHeaderHeight - 6);
                resize = new CRect(row.X + plotWidth - 18, graphHeader.Y, 18, GraphHeaderHeight - 6);
                curveAdd = new CRect(graphHeader.Right - 58, graphHeader.Y + 2, 56, 20);
                var chipX = graphHeader.X + 22;
                foreach (var _ in graph.Curves)
                {
                    var chip = new CRect(chipX, graphHeader.Y + 2, 68, 20);
                    if (chip.Right > curveAdd.X - 4)
                    {
                        break;
                    }

                    chips.Add(chip);
                    chipX += 72;
                }

                graphArea = new CRect(row.X, graphHeader.Bottom, plotWidth, graph.Height);
            }

            rows.Add(new MathNoteRowLayout(
                i,
                row,
                new CRect(row.X + 8, row.Y, Math.Max(20, row.Width - resultWidth - GraphButtonWidth - 20), row.Height),
                resultArea,
                graphButton,
                graphHeader,
                graphArea,
                resize,
                curveAdd,
                chips));

            if (i == selected)
            {
                selectedGraph = graphArea;
                selectedButton = graphButton;
            }

            y += total;
        }

        SliderLayout? sliderLayout = null;
        if (slider is not null)
        {
            var row = rows.FirstOrDefault(r => r.Index == slider.Line);
            if (row is not null)
            {
                var panelTop = row.GraphArea.IsEmpty
                    ? row.Row.Bottom
                    : row.GraphArea.Bottom;
                var panel = new CRect(row.Row.X, panelTop, row.Row.Width, SliderHeight);
                var track = new CRect(panel.X + 36, panel.Y + 30, Math.Max(40, panel.Width - 72), 8);
                const double knobSize = 16;
                sliderLayout = new SliderLayout
                {
                    Line = slider.Line,
                    Panel = panel,
                    Track = track,
                    Knob = new CRect(
                        track.X + (track.Width * slider.Fraction) - (knobSize / 2),
                        track.Center.Y - (knobSize / 2),
                        knobSize,
                        knobSize),
                    Minus = new CRect(panel.X + 6, panel.Y + 24, 26, 20),
                    Plus = new CRect(panel.Right - 32, panel.Y + 24, 26, 20),
                    Close = new CRect(panel.Right - 24, panel.Y + 4, 20, 20),
                    Value = new CRect(panel.X + 8, panel.Y + 4, Math.Max(60, panel.Width - 120), 18),
                    MinLabel = new CRect(track.X, track.Bottom + 2, 60, 12),
                    MaxLabel = new CRect(track.Right - 60, track.Bottom + 2, 60, 12)
                };
            }
        }

        return new MathNotesLayout
        {
            Page = page,
            Title = title,
            NewNoteButton = newNote,
            KeypadToggle = keypadToggle,
            PlotButton = selectedButton,
            Rows = rows,
            Slider = sliderLayout,
            GraphArea = selectedGraph,
            Readout = readout,
            Keypad = keypadRect,
            Keys = keys,
            FirstVisible = first
        };
    }

    /// <summary>Compatibility overload used by earlier callers: a graph on the selected row.</summary>
    public static MathNotesLayout Compute(CSize size, int lineCount, bool showGraph, int selected, bool canPlot)
    {
        var lines = new List<MathNoteLine>();
        for (var i = 0; i < Math.Max(1, lineCount); i++)
        {
            var line = new MathNoteLine { Text = i == selected ? "y = x^2" : "1+1" };
            if (i == selected && showGraph)
            {
                var graph = new LineGraph();
                graph.Curves.Add(new GraphCurve(i, 0, "x^2"));
                line.Graph = graph;
            }

            lines.Add(line);
        }

        _ = canPlot;
        return Compute(size, lines, selected, MathNotesModel.DefaultPageWidth, null, false);
    }

    private static readonly string[][] KeypadRows =
    [
        ["7", "8", "9", "/", "x", "back"],
        ["4", "5", "6", "*", "y", "enter"],
        ["1", "2", "3", "-", "(", ")"],
        ["0", ".", "=", "+", "^", "π"]
    ];
}

/// <summary>
/// A Math Notes page on the infinite canvas: paper-like sheet with a fixed content width, lines that
/// evaluate the moment an equals sign appears, numeric sliders that rewrite a literal in place and
/// inline graphs attached to the lines that define <c>y = f(x)</c>.
/// </summary>
public sealed class MathNotesWidget : IWidgetRenderer
{
    private static readonly Typeface Face =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"));

    private static readonly Typeface BoldFace =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);

    private static readonly Color[] CurveColors =
    [
        Color.Parse("#FFFF9F0A"),
        Color.Parse("#FF0A84FF"),
        Color.Parse("#FF30D158"),
        Color.Parse("#FFFF375F"),
        Color.Parse("#FFBF5AF2"),
        Color.Parse("#FF40C8E0")
    ];

    private readonly ExpressionEngine _engine;

    /// <summary>Creates the widget, optionally with a shared expression engine.</summary>
    public MathNotesWidget(ExpressionEngine? engine = null) => _engine = engine ?? new ExpressionEngine();

    /// <inheritdoc />
    public string WidgetType => "calcpad.math.notes";

    /// <summary>The expression engine backing this widget.</summary>
    public ExpressionEngine Engine => _engine;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) => new(560, 460);

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host) => InitializeState(target);

    /// <summary>Initialises <see cref="WidgetObject.State"/> without needing a live host.</summary>
    public void InitializeState(WidgetObject target)
    {
        target.State ??= new JsonObject();
        var model = MathNotesModel.From(target, _engine);
        model.Save(target.State);
        target.State["pressed"] = null;
        target.State["dragging"] = null;
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        try
        {
            RenderPage(context);
        }
        catch (Exception ex)
        {
            // A widget must never take the canvas down with it.
            try
            {
                var theme = context.Theme;
                var bounds = new CRect(0, 0, context.LogicalSize.Width, context.LogicalSize.Height);
                Panel(context.Drawing, bounds, theme.Brush(theme.Surface), 12);
                DrawText(context.Drawing, "数学笔记渲染失败 / render failed: " + ex.Message,
                    bounds.Inflate(-10), 11, theme.Brush(theme.Danger), Align.Left);
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }

    private void RenderPage(WidgetRenderContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Theme;
        var zoom = context.Zoom <= 0 ? 1 : context.Zoom;
        var drawing = context.Drawing;
        var model = MathNotesModel.From(context.Object, _engine);

        // The shell hands us screen space bounds and no transform. Clip in screen space first, then
        // map local logical coordinates (0..LogicalSize) onto those bounds.
        using var clip = drawing.PushClip(new Rect(
            context.Bounds.X,
            context.Bounds.Y,
            Math.Max(1, context.Bounds.Width),
            Math.Max(1, context.Bounds.Height)));
        using var transform = drawing.PushTransform(
            Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(context.Bounds.X, context.Bounds.Y));

        var layout = MathNotesLayout.Compute(size, model.Lines, model.Selected, model.PageWidth, model.Slider, model.ShowKeypad, model.Scroll);
        var pressed = ReadString(context.Object, "pressed");

        DrawSheet(drawing, theme, layout);

        DrawText(drawing, "数学笔记 / Math Notes", layout.Title, 15, theme.Brush(theme.TextPrimary), Align.Left, true);
        DrawButton(drawing, theme, layout.NewNoteButton, "新建", string.Equals(pressed, "newNote", StringComparison.Ordinal));
        DrawButton(
            drawing,
            theme,
            layout.KeypadToggle,
            model.ShowKeypad ? "收起" : "键盘",
            string.Equals(pressed, "keypad", StringComparison.Ordinal),
            active: model.ShowKeypad);

        DrawRows(drawing, theme, layout, model, pressed);
        DrawSlider(drawing, theme, layout, model, pressed);
        DrawKeypad(drawing, theme, layout, pressed);

        if (model.Readout.Length > 0)
        {
            DrawText(drawing, model.Readout, layout.Readout, 10.5, theme.Brush(theme.TextSecondary), Align.Left);
        }

        if (model.Hint.Length > 0)
        {
            DrawText(drawing, model.Hint, layout.Readout, 10.5, theme.Brush(theme.TextSecondary), Align.Right);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Selection), 2),
                new RoundedRect(new Rect(layout.Page.X, layout.Page.Y, layout.Page.Width, layout.Page.Height), 12));
        }
    }

    private static void DrawSheet(DrawingContext drawing, PluginTheme theme, MathNotesLayout layout)
    {
        var paper = theme.IsDark ? Color.Parse("#FF161618") : Color.Parse("#FFFFFFFF");
        drawing.DrawRectangle(theme.Brush(paper), null, new RoundedRect(
            new Rect(layout.Page.X, layout.Page.Y, layout.Page.Width, layout.Page.Height), 12));
        drawing.DrawRectangle(
            null,
            new Pen(theme.Brush(theme.GridLine), 1),
            new RoundedRect(new Rect(layout.Page.X + 0.5, layout.Page.Y + 0.5, layout.Page.Width - 1, layout.Page.Height - 1), 12));

        // A hairline under the title keeps the sheet reading like a ruled page.
        drawing.DrawLine(
            new Pen(theme.Brush(theme.GridLine), 1),
            new Point(layout.Title.X, layout.Title.Bottom + 2),
            new Point(layout.Title.Right, layout.Title.Bottom + 2));
    }

    private void DrawRows(DrawingContext drawing, PluginTheme theme, MathNotesLayout layout, MathNotesModel model, string? pressed)
    {
        foreach (var row in layout.Rows)
        {
            var line = model.Lines[row.Index];
            var selected = row.Index == model.Selected;

            if (selected)
            {
                Panel(drawing, row.Row, theme.Brush(theme.SurfaceAlt), 6);
            }

            drawing.DrawLine(
                new Pen(theme.Brush(theme.GridLine), 1),
                new Point(row.Row.X, row.Row.Bottom),
                new Point(row.Row.Right, row.Row.Bottom));

            var text = line.Text.Length == 0 ? "输入算式 / type an expression" : line.Text;
            var brush = theme.Brush(line.Text.Length == 0 ? theme.TextSecondary : theme.TextPrimary);
            var fontSize = FitFontSize(text, row.TextArea.Width, 14.5, 9.5);
            var formatted = Text(text, fontSize, brush, selected);
            drawing.DrawText(formatted, new Point(row.TextArea.X + 2, row.Row.Y + ((row.Row.Height - formatted.Height) / 2)));

            // Handwriting provenance marker: this line is bound to an ink object.
            if (line.SourceObjectId is { Length: > 0 })
            {
                DrawText(drawing, "✎", new CRect(row.Row.X - 7, row.Row.Y + 2, 12, 14), 10, theme.Brush(theme.Accent), Align.Left);
            }

            if (line.ShowsResult && line.HasValue && !line.IsError)
            {
                var resultBrush = theme.Brush(theme.Accent);
                if (line.Text.TrimEnd().EndsWith("=", StringComparison.Ordinal))
                {
                    // Inline, immediately after the equation, exactly like the iPad page.
                    var offset = row.TextArea.X + 2 + formatted.Width + 8;
                    var room = row.ResultArea.Right - offset;
                    if (room > 24)
                    {
                        var fitted = FittedText(line.Result, room, 14.5, 9.5, resultBrush, true);
                        drawing.DrawText(fitted, new Point(offset, row.Row.Y + ((row.Row.Height - fitted.Height) / 2)));
                    }
                }
                else
                {
                    DrawText(drawing, "= " + line.Result, row.ResultArea, 13, resultBrush, Align.Right, true);
                }

                if (line.Natural.Length > 0)
                {
                    DrawText(
                        drawing,
                        line.Natural,
                        new CRect(row.Row.X + 8, row.Row.Bottom - 11, row.Row.Width - 16, 11),
                        9,
                        theme.Brush(theme.TextSecondary),
                        Align.Left);
                }
            }
            else if (line.IsError)
            {
                DrawText(drawing, "⚠", row.ResultArea, 13, theme.Brush(theme.Danger), Align.Right, true);
                if (selected)
                {
                    DrawText(
                        drawing,
                        line.Error,
                        new CRect(row.Row.X + 8, row.Row.Bottom - 11, row.Row.Width - 16, 11),
                        9,
                        theme.Brush(theme.Danger),
                        Align.Left);
                }
            }

            if (row.GraphButton.Width > 0)
            {
                var active = line.Graph is not null;
                DrawButton(
                    drawing,
                    theme,
                    row.GraphButton,
                    active ? "收起" : "绘图",
                    string.Equals(pressed, $"row:graph:{row.Index}", StringComparison.Ordinal),
                    active);
            }

            if (line.Graph is { } graph)
            {
                DrawGraph(drawing, theme, model, row, graph, pressed);
            }
        }
    }

    private void DrawGraph(
        DrawingContext drawing,
        PluginTheme theme,
        MathNotesModel model,
        MathNoteRowLayout row,
        LineGraph graph,
        string? pressed)
    {
        Panel(drawing, row.GraphHeader, theme.Brush(theme.SurfaceRaised), 6);
        DrawText(drawing, "⠿", new CRect(row.GraphHeader.X + 2, row.GraphHeader.Y, 16, row.GraphHeader.Height), 12,
            theme.Brush(theme.TextSecondary), Align.Center);

        for (var i = 0; i < row.CurveChips.Count && i < graph.Curves.Count; i++)
        {
            var curve = graph.Curves[i];
            var chip = row.CurveChips[i];
            var color = CurveColors[Math.Abs(curve.ColorIndex) % CurveColors.Length];
            Panel(drawing, chip, theme.Brush(color), 5);
            DrawFitted(drawing, model.Resolve(curve, graph), chip.Inflate(-6), 10, 7, theme.Brush(Color.Parse("#FF101012")), Align.Center, true);
        }

        DrawButton(drawing, theme, row.CurveAdd, "+ 曲线", string.Equals(pressed, $"graph:add:{row.Index}", StringComparison.Ordinal));
        DrawText(drawing, "◢", row.GraphResize, 11, theme.Brush(theme.TextSecondary), Align.Center);

        graph.View.Area = row.GraphArea;
        var plot = row.GraphArea;
        drawing.DrawRectangle(
            theme.Brush(theme.IsDark ? Color.Parse("#FF101012") : Color.Parse("#FFF8F8FB")),
            null,
            new RoundedRect(new Rect(plot.X, plot.Y, plot.Width, plot.Height), 8));

        using var clip = drawing.PushClip(new Rect(plot.X, plot.Y, plot.Width, plot.Height));
        var xSpan = graph.View.XMax - graph.View.XMin;
        var ySpan = graph.View.YMax - graph.View.YMin;
        var xStep = NiceStep(xSpan / 6);
        var yStep = NiceStep(ySpan / 4);
        var gridPen = new Pen(theme.Brush(theme.GridLine), 1);
        var axisPen = new Pen(theme.Brush(theme.TextSecondary), 1.1);
        var labelBrush = theme.Brush(theme.TextSecondary);

        for (var x = Math.Ceiling(graph.View.XMin / xStep) * xStep; x <= graph.View.XMax && xStep > 0; x += xStep)
        {
            var top = graph.View.WorldToScreen(x, graph.View.YMax);
            var bottom = graph.View.WorldToScreen(x, graph.View.YMin);
            drawing.DrawLine(gridPen, new Point(top.X, top.Y), new Point(bottom.X, bottom.Y));
            DrawText(drawing, NumberFormatter.Format(x, 6), new CRect(top.X - 24, plot.Bottom - 13, 48, 11), 8.5, labelBrush, Align.Center);
        }

        for (var y = Math.Ceiling(graph.View.YMin / yStep) * yStep; y <= graph.View.YMax && yStep > 0; y += yStep)
        {
            var left = graph.View.WorldToScreen(graph.View.XMin, y);
            var right = graph.View.WorldToScreen(graph.View.XMax, y);
            drawing.DrawLine(gridPen, new Point(left.X, left.Y), new Point(right.X, right.Y));
            DrawText(drawing, NumberFormatter.Format(y, 6), new CRect(plot.X + 2, left.Y - 6, 44, 11), 8.5, labelBrush, Align.Left);
        }

        if (graph.View.XMin <= 0 && graph.View.XMax >= 0)
        {
            var top = graph.View.WorldToScreen(0, graph.View.YMax);
            var bottom = graph.View.WorldToScreen(0, graph.View.YMin);
            drawing.DrawLine(axisPen, new Point(top.X, top.Y), new Point(bottom.X, bottom.Y));
        }

        if (graph.View.YMin <= 0 && graph.View.YMax >= 0)
        {
            var left = graph.View.WorldToScreen(graph.View.XMin, 0);
            var right = graph.View.WorldToScreen(graph.View.XMax, 0);
            drawing.DrawLine(axisPen, new Point(left.X, left.Y), new Point(right.X, right.Y));
        }

        var samples = (int)Math.Clamp(plot.Width, 64, 420);
        for (var c = 0; c < graph.Curves.Count; c++)
        {
            var (xs, ys) = model.Sample(row.Index, c, samples);
            if (xs.Length < 2)
            {
                continue;
            }

            var color = CurveColors[Math.Abs(graph.Curves[c].ColorIndex) % CurveColors.Length];
            var pen = new Pen(theme.Brush(color), 2);
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var open = false;
                double previousY = double.NaN;
                for (var i = 0; i < xs.Length; i++)
                {
                    if (ys[i] is not { } y || double.IsNaN(y) || double.IsInfinity(y))
                    {
                        if (open)
                        {
                            ctx.EndFigure(false);
                            open = false;
                        }

                        continue;
                    }

                    if (open && Math.Abs(y - previousY) > ySpan * 1.5)
                    {
                        ctx.EndFigure(false);
                        open = false;
                    }

                    var point = graph.View.WorldToScreen(xs[i], y);
                    if (!open)
                    {
                        ctx.BeginFigure(new Point(point.X, point.Y), false);
                        open = true;
                    }
                    else
                    {
                        ctx.LineTo(new Point(point.X, point.Y));
                    }

                    previousY = y;
                }

                if (open)
                {
                    ctx.EndFigure(false);
                }
            }

            drawing.DrawGeometry(null, pen, geometry);
        }
    }

    private static void DrawSlider(DrawingContext drawing, PluginTheme theme, MathNotesLayout layout, MathNotesModel model, string? pressed)
    {
        if (layout.Slider is not { } slider || model.Slider is not { } state)
        {
            return;
        }

        Panel(drawing, slider.Panel, theme.Brush(theme.SurfaceRaised), 8);
        DrawText(
            drawing,
            $"数字滑杆 · {NumberFormatter.Format(state.Value, 12)}",
            slider.Value,
            11.5,
            theme.Brush(theme.TextPrimary),
            Align.Left,
            true);

        var trackColor = theme.IsDark ? Color.Parse("#FF3A3A3C") : Color.Parse("#FFD1D1D6");
        Panel(drawing, slider.Track, theme.Brush(trackColor), slider.Track.Height / 2);
        var filled = new CRect(slider.Track.X, slider.Track.Y, Math.Max(2, slider.Track.Width * state.Fraction), slider.Track.Height);
        Panel(drawing, filled, theme.Brush(theme.Accent), slider.Track.Height / 2);
        drawing.DrawEllipse(
            theme.Brush(theme.AccentText),
            new Pen(theme.Brush(theme.Accent), 2),
            new Point(slider.Knob.Center.X, slider.Knob.Center.Y),
            slider.Knob.Width / 2,
            slider.Knob.Height / 2);

        DrawButton(drawing, theme, slider.Minus, "−", string.Equals(pressed, "slider:minus", StringComparison.Ordinal));
        DrawButton(drawing, theme, slider.Plus, "+", string.Equals(pressed, "slider:plus", StringComparison.Ordinal));
        DrawButton(drawing, theme, slider.Close, "×", string.Equals(pressed, "slider:close", StringComparison.Ordinal));
        DrawText(drawing, NumberFormatter.Format(state.Min, 6), slider.MinLabel, 9, theme.Brush(theme.TextSecondary), Align.Left);
        DrawText(drawing, NumberFormatter.Format(state.Max, 6), slider.MaxLabel, 9, theme.Brush(theme.TextSecondary), Align.Right);
        DrawText(
            drawing,
            "←/→ 步进 · PgUp/PgDn ×10 · Esc 关闭",
            new CRect(Math.Max(slider.Panel.X + 120, slider.Panel.Right - 250), slider.Panel.Y + 4, 240, 14),
            8.5,
            theme.Brush(theme.TextSecondary),
            Align.Right);
    }

    private static void DrawKeypad(DrawingContext drawing, PluginTheme theme, MathNotesLayout layout, string? pressed)
    {
        if (layout.Keys.Count == 0)
        {
            return;
        }

        Panel(drawing, layout.Keypad, theme.Brush(theme.SurfaceAlt), 8);
        foreach (var (action, rect) in layout.Keys)
        {
            var isPressed = string.Equals(pressed, $"key:{action}", StringComparison.Ordinal);
            var fill = isPressed ? theme.Accent : theme.SurfaceRaised;
            Panel(drawing, rect, theme.Brush(fill), 6);
            DrawText(drawing, KeyLabel(action), rect, 12, theme.Brush(isPressed ? theme.AccentText : theme.TextPrimary), Align.Center, true);
        }
    }

    private static string KeyLabel(string action) => action switch
    {
        "back" => "⌫",
        "enter" => "⏎",
        _ => action
    };

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        var size = context.LogicalSize;
        var model = MathNotesModel.From(context.Object, _engine);
        var layout = MathNotesLayout.Compute(size, model.Lines, model.Selected, model.PageWidth, model.Slider, model.ShowKeypad, model.Scroll);

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
                return OnDown(context, model, layout);

            case WidgetPointerPhase.Move:
                return OnMove(context, model, layout);

            case WidgetPointerPhase.Up:
                return OnUp(context, model, layout);

            case WidgetPointerPhase.Cancel:
                SetString(context.Object, "pressed", null);
                SetString(context.Object, "dragging", null);
                context.Host.Invalidate();
                return true;

            case WidgetPointerPhase.Wheel:
                return OnWheel(context, model, layout);

            default:
                return false;
        }
    }

    private bool OnWheel(WidgetPointerContext context, MathNotesModel model, MathNotesLayout layout)
    {
        foreach (var row in layout.Rows)
        {
            if (model.Lines[row.Index].Graph is { } graph && row.GraphArea.Contains(context.Position))
            {
                graph.View.Area = row.GraphArea;
                graph.View.ZoomAt(context.Position, context.WheelDelta > 0 ? 0.88 : 1.14);
                model.UpdateReadout(row.Index, context.Position);
                Save(context.Object, model);
                context.Host.Invalidate();
                return true;
            }
        }

        if (layout.Page.Contains(context.Position))
        {
            model.Scroll = Math.Max(0, model.Scroll + (context.WheelDelta > 0 ? -1 : 1));
            Save(context.Object, model);
            context.Host.Invalidate();
            return true;
        }

        return false;
    }

    private bool OnDown(WidgetPointerContext context, MathNotesModel model, MathNotesLayout layout)
    {
        var point = context.Position;

        foreach (var (action, rect) in layout.Keys)
        {
            if (rect.Contains(point))
            {
                SetString(context.Object, "pressed", $"key:{action}");
                context.Host.Invalidate();
                return true;
            }
        }

        if (layout.NewNoteButton.Contains(point))
        {
            SetString(context.Object, "pressed", "newNote");
            context.Host.Invalidate();
            return true;
        }

        if (layout.KeypadToggle.Contains(point))
        {
            SetString(context.Object, "pressed", "keypad");
            context.Host.Invalidate();
            return true;
        }

        if (layout.Slider is { } slider && model.Slider is not null)
        {
            if (slider.Close.Contains(point))
            {
                SetString(context.Object, "pressed", "slider:close");
                context.Host.Invalidate();
                return true;
            }

            if (slider.Minus.Contains(point))
            {
                SetString(context.Object, "pressed", "slider:minus");
                context.Host.Invalidate();
                return true;
            }

            if (slider.Plus.Contains(point))
            {
                SetString(context.Object, "pressed", "slider:plus");
                context.Host.Invalidate();
                return true;
            }

            if (slider.Track.Inflate(12).Contains(point))
            {
                SetString(context.Object, "pressed", "slider:knob");
                SetString(context.Object, "dragging", "slider");
                ApplySliderFromPoint(model, slider, point);
                Save(context.Object, model);
                context.Host.Invalidate();
                return true;
            }
        }

        foreach (var row in layout.Rows)
        {
            var line = model.Lines[row.Index];
            if (line.Graph is not null)
            {
                if (row.GraphResize.Contains(point))
                {
                    SetString(context.Object, "pressed", $"graph:resize:{row.Index}");
                    SetString(context.Object, "dragging", $"resize:{row.Index}");
                    SetLastPoint(context.Object, point);
                    return true;
                }

                if (row.CurveAdd.Contains(point))
                {
                    SetString(context.Object, "pressed", $"graph:add:{row.Index}");
                    context.Host.Invalidate();
                    return true;
                }

                for (var i = 0; i < row.CurveChips.Count; i++)
                {
                    if (!row.CurveChips[i].Contains(point))
                    {
                        continue;
                    }

                    var chip = row.CurveChips[i];
                    var action = point.X > chip.Right - 14
                        ? $"graph:remove:{row.Index}:{i}"
                        : $"graph:color:{row.Index}:{i}";
                    SetString(context.Object, "pressed", action);
                    context.Host.Invalidate();
                    return true;
                }

                if (row.GraphHeader.Contains(point))
                {
                    SetString(context.Object, "pressed", $"graph:move:{row.Index}");
                    SetString(context.Object, "dragging", $"move:{row.Index}");
                    SetLastPoint(context.Object, point);
                    context.Host.Invalidate();
                    return true;
                }

                if (row.GraphArea.Contains(point))
                {
                    SetString(context.Object, "pressed", $"graph:pan:{row.Index}");
                    SetString(context.Object, "dragging", $"pan:{row.Index}");
                    SetLastPoint(context.Object, point);
                    line.Graph.View.Area = row.GraphArea;
                    model.UpdateReadout(row.Index, point);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                }
            }

            if (row.GraphButton.Width > 0 && row.GraphButton.Contains(point))
            {
                SetString(context.Object, "pressed", $"row:graph:{row.Index}");
                context.Host.Invalidate();
                return true;
            }

            if (row.ResultArea.Contains(point) && line.Result.Length > 0 && !line.IsError)
            {
                SetString(context.Object, "pressed", $"result:{row.Index}");
                context.Host.Invalidate();
                return true;
            }

            if (row.Row.Contains(point))
            {
                SetString(context.Object, "pressed", $"row:{row.Index}");
                SetLastPoint(context.Object, point);
                context.Host.Invalidate();
                return true;
            }
        }

        return false;
    }

    private bool OnMove(WidgetPointerContext context, MathNotesModel model, MathNotesLayout layout)
    {
        var dragging = ReadString(context.Object, "dragging");
        if (dragging is null || !context.IsPrimary)
        {
            return false;
        }

        var last = ReadLastPoint(context.Object) ?? context.Position;
        var dx = context.Position.X - last.X;
        var dy = context.Position.Y - last.Y;

        if (string.Equals(dragging, "slider", StringComparison.Ordinal) && layout.Slider is { } slider)
        {
            ApplySliderFromPoint(model, slider, context.Position);
        }
        else if (dragging.StartsWith("pan:", StringComparison.Ordinal) && int.TryParse(dragging[4..], out var panLine))
        {
            if (model.Lines[panLine].Graph is { } graph)
            {
                graph.View.Area = layout.Rows.FirstOrDefault(r => r.Index == panLine)?.GraphArea ?? graph.View.Area;
                graph.View.PanByScreen(dx, dy);
                model.UpdateReadout(panLine, context.Position);
            }
        }
        else if (dragging.StartsWith("resize:", StringComparison.Ordinal) && int.TryParse(dragging[7..], out var resizeLine))
        {
            if (model.Lines[resizeLine].Graph is { } graph)
            {
                model.ResizeGraph(resizeLine, graph.Width + dx, graph.Height + dy);
            }
        }
        else if (dragging.StartsWith("move:", StringComparison.Ordinal))
        {
            // The drop target is resolved on Up.
        }
        else
        {
            return false;
        }

        SetLastPoint(context.Object, context.Position);
        Save(context.Object, model);
        context.Host.Invalidate();
        return true;
    }

    private bool OnUp(WidgetPointerContext context, MathNotesModel model, MathNotesLayout layout)
    {
        var pressed = ReadString(context.Object, "pressed");
        var dragging = ReadString(context.Object, "dragging");
        SetString(context.Object, "pressed", null);
        SetString(context.Object, "dragging", null);
        if (pressed is null)
        {
            return false;
        }

        if (pressed.StartsWith("key:", StringComparison.Ordinal))
        {
            ApplyKey(model, pressed[4..]);
        }
        else if (string.Equals(pressed, "newNote", StringComparison.Ordinal))
        {
            model.Hint = "用「新建笔记」命令再放一张纸 / use the New note command for another page";
        }
        else if (string.Equals(pressed, "keypad", StringComparison.Ordinal))
        {
            model.ShowKeypad = !model.ShowKeypad;
        }
        else if (string.Equals(pressed, "slider:close", StringComparison.Ordinal))
        {
            model.CloseSlider();
        }
        else if (string.Equals(pressed, "slider:minus", StringComparison.Ordinal))
        {
            model.NudgeSlider(-1);
        }
        else if (string.Equals(pressed, "slider:plus", StringComparison.Ordinal))
        {
            model.NudgeSlider(1);
        }
        else if (string.Equals(pressed, "slider:knob", StringComparison.Ordinal) && layout.Slider is { } slider)
        {
            ApplySliderFromPoint(model, slider, context.Position);
        }
        else if (pressed.StartsWith("row:graph:", StringComparison.Ordinal)
                 && int.TryParse(pressed[10..], out var graphRow))
        {
            model.ToggleGraph(graphRow);
        }
        else if (pressed.StartsWith("graph:add:", StringComparison.Ordinal)
                 && int.TryParse(pressed[10..], out var addRow))
        {
            model.AddCurve(addRow);
        }
        else if (pressed.StartsWith("graph:color:", StringComparison.Ordinal))
        {
            var parts = pressed.Split(':');
            if (parts.Length == 4 && int.TryParse(parts[2], out var colorLine) && int.TryParse(parts[3], out var colorCurve))
            {
                model.CycleCurveColor(colorLine, colorCurve);
            }
        }
        else if (pressed.StartsWith("graph:remove:", StringComparison.Ordinal))
        {
            var parts = pressed.Split(':');
            if (parts.Length == 4 && int.TryParse(parts[2], out var removeLine) && int.TryParse(parts[3], out var removeCurve))
            {
                model.RemoveCurve(removeLine, removeCurve);
            }
        }
        else if (pressed.StartsWith("graph:pan:", StringComparison.Ordinal)
                 && int.TryParse(pressed[10..], out var panRow))
        {
            model.UpdateReadout(panRow, context.Position);
        }
        else if (pressed.StartsWith("graph:move:", StringComparison.Ordinal)
                 && int.TryParse(pressed[11..], out var moveRow))
        {
            var target = layout.Rows.FirstOrDefault(r => r.Row.Contains(context.Position));
            if (dragging is not null && target is not null && target.Index != moveRow)
            {
                model.MoveGraph(moveRow, target.Index);
            }
        }
        else if (pressed.StartsWith("result:", StringComparison.Ordinal)
                 && int.TryParse(pressed[7..], out var resultRow))
        {
            model.InsertResultBelow(resultRow);
        }
        else if (pressed.StartsWith("row:", StringComparison.Ordinal)
                 && int.TryParse(pressed[4..], out var rowIndex))
        {
            model.SelectLine(rowIndex);
            var row = layout.Rows.FirstOrDefault(r => r.Index == rowIndex);
            if (row is not null)
            {
                // Tapping a number is the signature gesture: it opens the slider on that literal.
                var literal = LiteralAt(model, row, context.Position);
                if (literal >= 0)
                {
                    model.OpenSlider(rowIndex, literal + 1);
                }
            }
        }

        Save(context.Object, model);
        context.Host.Invalidate();
        return true;
    }

    /// <summary>Maps a tap inside a row to the numeric literal it hit, or -1.</summary>
    private int LiteralAt(MathNotesModel model, MathNoteRowLayout row, CPoint point)
    {
        var line = model.Lines[row.Index];
        if (line.Text.Length == 0)
        {
            return -1;
        }

        var literals = _engine.FindNumericLiterals(line.Text);
        if (literals.Count == 0)
        {
            return -1;
        }

        var fontSize = FitFontSize(line.Text, row.TextArea.Width, 14.5, 9.5);
        var width = WidthOf(line.Text, fontSize);
        var ratio = Math.Clamp((point.X - row.TextArea.X - 2) / Math.Max(1, width), 0, 1);
        var index = (int)Math.Round(ratio * line.Text.Length);

        for (var i = 0; i < literals.Count; i++)
        {
            if (literals[i].Contains(index))
            {
                return i;
            }
        }

        // Tolerate a couple of characters of slop so a fat finger still grabs the number.
        for (var i = 0; i < literals.Count; i++)
        {
            var literal = literals[i];
            if (index >= literal.Start - 2 && index <= literal.Start + literal.Length + 2)
            {
                return i;
            }
        }

        return -1;
    }

    private static void ApplySliderFromPoint(MathNotesModel model, SliderLayout slider, CPoint point)
    {
        if (model.Slider is not { } state)
        {
            return;
        }

        var fraction = Math.Clamp((point.X - slider.Track.X) / Math.Max(1, slider.Track.Width), 0, 1);
        model.SetSliderValue(state.Min + (fraction * (state.Max - state.Min)));
    }

    private static void ApplyKey(MathNotesModel model, string action)
    {
        switch (action)
        {
            case "back":
                model.Backspace();
                break;
            case "enter":
                model.NewLine();
                break;
            default:
                model.AppendText(action);
                break;
        }
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        var model = MathNotesModel.From(context.Object, _engine);
        var key = NormalizeKey(context.Key);

        // The slider owns the arrow keys while it is open.
        if (model.Slider is not null)
        {
            switch (key)
            {
                case "Left":
                case "Down":
                    model.NudgeSlider(-1);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "Right":
                case "Up":
                    model.NudgeSlider(1);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "PageUp":
                    model.NudgeSlider(10);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "PageDown":
                    model.NudgeSlider(-10);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "Home":
                    model.SetSliderValue(model.Slider.Min);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "End":
                    model.SetSliderValue(model.Slider.Max);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "Escape":
                    model.CloseSlider();
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                case "Tab":
                    {
                        var line = model.Slider.Line;
                        var count = model.Engine.FindNumericLiterals(model.Lines[line].Text).Count;
                        var next = (IndexOfLiteral(model, line) + 2) > count ? 1 : IndexOfLiteral(model, line) + 2;
                        model.OpenSlider(line, next);
                        Save(context.Object, model);
                        context.Host.Invalidate();
                        return true;
                    }
            }
        }

        switch (key)
        {
            case "Return":
            case "Enter":
                model.NewLine();
                break;
            case "Back":
                model.Backspace();
                break;
            case "Up":
                model.MoveSelection(-1);
                break;
            case "Down":
                model.MoveSelection(1);
                break;
            case "Escape":
            case "Delete":
                model.ClearLine();
                break;
            case "Tab":
                model.MoveSelection(1);
                break;
            case "G":
                model.ToggleGraph(model.Selected);
                break;
            default:
                if (key.Length == 1 && !char.IsControl(key[0]))
                {
                    model.AppendText(key);
                }
                else
                {
                    return false;
                }

                break;
        }

        Save(context.Object, model);
        context.Host.Invalidate();
        return true;
    }

    private static int IndexOfLiteral(MathNotesModel model, int lineIndex)
    {
        if (model.Slider is not { } slider)
        {
            return 0;
        }

        var literals = model.Engine.FindNumericLiterals(model.Lines[lineIndex].Text);
        for (var i = 0; i < literals.Count; i++)
        {
            if (literals[i].Start == slider.Start)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Normalises <c>Key.ToString()</c> names and literal characters.</summary>
    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        // A space arrives as a literal character: trimming it away would swallow the key.
        if (char.IsWhiteSpace(key[0]))
        {
            return " ";
        }

        var text = key.Trim();
        return text switch
        {
            "Back" or "BackSpace" or "Backspace" => "Back",
            "Return" or "Enter" or "NumPadEnter" => "Enter",
            "Escape" or "Clear" => "Escape",
            "Space" => " ",
            "OemPeriod" or "Decimal" or "NumPadDecimal" => ".",
            "Add" or "OemPlus" => "+",
            "Subtract" or "OemMinus" => "-",
            "Multiply" => "*",
            "Divide" or "OemQuestion" => "/",
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

    private static double FitFontSize(string text, double maxWidth, double maxSize, double minSize)
    {
        var size = Math.Max(minSize, maxSize);
        while (size > minSize && WidthOf(text, size) > maxWidth - 6)
        {
            size -= 0.5;
        }

        return size;
    }

    /// <summary>
    /// Measures a text run. Headless hosts have no font manager, so a deterministic approximation is
    /// used instead of throwing; the interactive hit tests then still work without a display.
    /// </summary>
    private static double WidthOf(string text, double size)
    {
        try
        {
            return Text(text, size, Brushes.White).Width;
        }
        catch (Exception)
        {
            return text.Length * size * 0.55;
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);

    private static FormattedText FittedText(string text, double maxWidth, double maxSize, double minSize, IBrush brush, bool bold = false)
    {
        var size = Math.Max(minSize, maxSize);
        var formatted = Text(text, size, brush, bold);
        while (formatted.Width > maxWidth && size > minSize)
        {
            size -= 0.5;
            formatted = Text(text, size, brush, bold);
        }

        return formatted;
    }

    private static void Panel(DrawingContext drawing, CRect rect, IBrush fill, double radius) =>
        drawing.DrawRectangle(fill, null, new RoundedRect(
            new Rect(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height)),
            Math.Min(radius, Math.Max(1, Math.Min(rect.Width, rect.Height) / 2))));

    private static void DrawButton(DrawingContext drawing, PluginTheme theme, CRect rect, string label, bool pressed, bool active = false)
    {
        if (rect.Width <= 1 || rect.Height <= 1)
        {
            return;
        }

        var fill = active ? theme.Accent : theme.SurfaceRaised;
        if (pressed)
        {
            fill = Lighten(fill, 0.3);
        }

        Panel(drawing, rect, theme.Brush(fill), 8);
        DrawText(
            drawing,
            label,
            rect,
            Math.Clamp(rect.Height * 0.5, 8, 12),
            theme.Brush(active ? theme.AccentText : theme.TextPrimary),
            Align.Center,
            true);
    }

    private static void DrawText(
        DrawingContext drawing,
        string text,
        CRect area,
        double size,
        IBrush brush,
        Align align,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height).Inflate(1));
        var formatted = Text(text, size, brush, bold);
        var x = align switch
        {
            Align.Left => area.X + 3,
            Align.Right => area.Right - formatted.Width - 3,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        drawing.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
    }

    private static void DrawFitted(
        DrawingContext drawing,
        string text,
        CRect area,
        double maxSize,
        double minSize,
        IBrush brush,
        Align align,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        var formatted = FittedText(text, area.Width, maxSize, minSize, brush, bold);
        var x = align switch
        {
            Align.Left => area.X + 2,
            Align.Right => area.Right - formatted.Width - 2,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        drawing.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw))
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(raw));
        var fraction = raw / Math.Pow(10, exponent);
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * Math.Pow(10, exponent);
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
        (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
        (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));

    private static void Save(WidgetObject target, MathNotesModel model)
    {
        target.State ??= new JsonObject();
        model.Save(target.State);
    }

    private static string? ReadString(WidgetObject target, string key) =>
        target.State is { } state && state.TryGetPropertyValue(key, out var node) && node is not null
            ? node.ToString()
            : null;

    private static void SetString(WidgetObject target, string key, string? value)
    {
        target.State ??= new JsonObject();
        target.State[key] = value;
    }

    private static void SetLastPoint(WidgetObject target, CPoint point)
    {
        target.State ??= new JsonObject();
        target.State["lastX"] = point.X;
        target.State["lastY"] = point.Y;
    }

    private static CPoint? ReadLastPoint(WidgetObject target)
    {
        if (target.State is not { } state ||
            !state.TryGetPropertyValue("lastX", out var xNode) || xNode is null ||
            !state.TryGetPropertyValue("lastY", out var yNode) || yNode is null)
        {
            return null;
        }

        return double.TryParse(xNode.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
               double.TryParse(yNode.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? new CPoint(x, y)
            : null;
    }

    private enum Align
    {
        Left,
        Center,
        Right
    }
}
