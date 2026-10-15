using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.MathNotes;

/// <summary>Geometry of one editable row.</summary>
public sealed record MathNoteRowLayout(int Index, CRect Row, CRect TextArea, CRect ResultArea);

/// <summary>Pure geometry of the math notes block, recomputed from the logical size.</summary>
public sealed class MathNotesLayout
{
    /// <summary>Title strip.</summary>
    public CRect Title { get; init; }

    /// <summary>Plot toggle button.</summary>
    public CRect PlotButton { get; init; }

    /// <summary>Rows currently visible; the list scrolls so the selected row is always shown.</summary>
    public IReadOnlyList<MathNoteRowLayout> Rows { get; init; } = [];

    /// <summary>Plot area; empty while the graph is hidden.</summary>
    public CRect GraphArea { get; init; }

    /// <summary>Coordinate readout strip.</summary>
    public CRect Readout { get; init; }

    /// <summary>First line index that is visible.</summary>
    public int FirstVisible { get; init; }

    /// <summary>Computes the layout for a widget of the given logical size.</summary>
    public static MathNotesLayout Compute(
        CSize size,
        int lineCount,
        bool showGraph,
        int selected,
        bool canPlot)
    {
        const double padding = 12;
        var width = Math.Max(220, size.Width);
        var height = Math.Max(160, size.Height);
        var title = new CRect(padding, 8, width - (padding * 2), 26);
        var plotButtonWidth = canPlot || showGraph ? 92d : 0d;
        var plotButton = new CRect(title.Right - plotButtonWidth, title.Y + 2, plotButtonWidth, 22);

        var footer = height - padding;
        var graphArea = CRect.Empty;
        var readout = CRect.Empty;
        if (showGraph)
        {
            var graphHeight = Math.Max(140, (height - title.Bottom) * 0.5);
            readout = new CRect(padding, footer - 16, width - (padding * 2), 16);
            graphArea = new CRect(padding, Math.Max(title.Bottom + 4, readout.Y - graphHeight - 4), width - (padding * 2), graphHeight);
        }

        var rowsBottom = showGraph ? graphArea.Y - 6 : footer;
        var rowHeight = 34d;
        var available = Math.Max(rowHeight, rowsBottom - title.Bottom - 2);
        var visible = Math.Max(1, (int)(available / rowHeight));
        var first = Math.Clamp(selected - visible + 1, 0, Math.Max(0, lineCount - visible));
        var rows = new List<MathNoteRowLayout>();
        for (var i = first; i < Math.Min(lineCount, first + visible); i++)
        {
            var row = new CRect(title.X, title.Bottom + 2 + ((i - first) * rowHeight), title.Width, rowHeight - 4);
            var resultWidth = Math.Min(row.Width * 0.45, 190);
            rows.Add(new MathNoteRowLayout(
                i,
                row,
                new CRect(row.X + 8, row.Y, row.Width - resultWidth - 16, row.Height),
                new CRect(row.Right - resultWidth - 6, row.Y, resultWidth, row.Height)));
        }

        return new MathNotesLayout
        {
            Title = title,
            PlotButton = plotButton,
            Rows = rows,
            GraphArea = graphArea,
            Readout = readout,
            FirstVisible = first
        };
    }
}

/// <summary>
/// A math notes block on the canvas: several lines, live evaluation, variables shared inside the
/// block and clickable results. A line that defines <c>y = f(x)</c> can be plotted with a hand drawn
/// coordinate system that supports zoom, pan and a coordinate readout.
/// </summary>
public sealed class MathNotesWidget : IWidgetRenderer
{
    private static readonly Typeface Face =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"));

    private static readonly Typeface BoldFace =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly IExpressionEngine _engine;

    /// <summary>Creates the widget, optionally with a shared expression engine.</summary>
    public MathNotesWidget(IExpressionEngine? engine = null) => _engine = engine ?? new ExpressionEngine();

    /// <inheritdoc />
    public string WidgetType => "calcpad.math.notes";

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) => new(480, 440);

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host) => InitializeState(target);

    /// <summary>Initialises <see cref="WidgetObject.State"/> without needing a live host.</summary>
    public void InitializeState(WidgetObject target)
    {
        target.State ??= new JsonObject();
        var model = MathNotesModel.From(target, _engine);
        model.Save(target.State);
        target.State["pressed"] = null;
        target.State["dragging"] = false;
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Theme;
        var drawing = context.Drawing;
        var model = MathNotesModel.From(context.Object, _engine);
        var layout = MathNotesLayout.Compute(size, model.Lines.Count, model.ShowGraph, model.Selected, model.CanPlot);
        var pressed = ReadPressed(context.Object);

        drawing.DrawRectangle(
            theme.SurfaceBrush,
            null,
            new RoundedRect(new Rect(0, 0, size.Width, size.Height), 20));

        DrawText(drawing, "数学笔记 / Math Notes", layout.Title, 16, theme.Brush(theme.TextPrimary), Align.Left, true);
        var status = model.CanPlot ? "y = f(x)" : $"{model.Lines.Count} 行 / lines";
        DrawText(drawing, status, new CRect(layout.Title.X, layout.Title.Y, Math.Max(10, layout.PlotButton.X - layout.Title.X - 8), layout.Title.Height), 11, theme.Brush(theme.TextSecondary), Align.Right);

        if (layout.PlotButton.Width > 1)
        {
            var active = model.ShowGraph;
            var fill = active ? theme.Accent : theme.SurfaceRaised;
            if (string.Equals(pressed, "plot", StringComparison.Ordinal))
            {
                fill = Lighten(fill, 0.3);
            }

            Panel(drawing, layout.PlotButton, theme.Brush(fill), 10);
            DrawText(
                drawing,
                active ? "隐藏图像 / Hide" : "绘图 / Plot",
                layout.PlotButton,
                10.5,
                theme.Brush(active ? theme.AccentText : theme.TextPrimary),
                Align.Center,
                true);
        }

        foreach (var row in layout.Rows)
        {
            var line = model.Lines[row.Index];
            var selected = row.Index == model.Selected;
            Panel(drawing, row.Row, theme.Brush(selected ? theme.SurfaceRaised : theme.SurfaceAlt), 10);
            DrawText(
                drawing,
                line.Text.Length == 0 ? "输入表达式 / type an expression" : line.Text,
                row.TextArea,
                selected ? 13.5 : 12.5,
                theme.Brush(line.Text.Length == 0 ? theme.TextSecondary : theme.TextPrimary),
                Align.Left,
                selected);

            var resultText = line.IsError ? "⚠ " + line.Error : line.Result;
            if (resultText.Length > 0)
            {
                var resultFill = selected ? theme.Surface : theme.SurfaceAlt;
                Panel(drawing, row.ResultArea, theme.Brush(resultFill), 8);
                DrawFitted(
                    drawing,
                    line.IsError ? "Error" : "= " + line.Result,
                    row.ResultArea,
                    12.5,
                    8,
                    theme.Brush(line.IsError ? theme.Danger : theme.Accent),
                    Align.Right,
                    true);
            }

            if (line.IsError && selected)
            {
                DrawText(
                    drawing,
                    line.Error,
                    new CRect(row.Row.X + 8, row.Row.Bottom - 12, row.Row.Width - 16, 12),
                    9,
                    theme.Brush(theme.Danger),
                    Align.Left);
            }
        }

        if (model.ShowGraph && layout.GraphArea.Height > 10)
        {
            DrawGraph(drawing, theme, layout.GraphArea, model);
            if (model.Readout.Length > 0)
            {
                DrawText(drawing, model.Readout, layout.Readout, 10.5, theme.Brush(theme.TextSecondary), Align.Left);
            }
        }

        if (model.Hint.Length > 0)
        {
            var hintArea = new CRect(layout.Title.X, size.Height - 16, layout.Title.Width, 14);
            DrawText(drawing, model.Hint, hintArea, 10, theme.Brush(theme.TextSecondary), Align.Right);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Selection), 2),
                new RoundedRect(new Rect(1, 1, Math.Max(1, size.Width - 2), Math.Max(1, size.Height - 2)), 20));
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Host.Theme;
        var model = MathNotesModel.From(context.Object, _engine);
        var layout = MathNotesLayout.Compute(size, model.Lines.Count, model.ShowGraph, model.Selected, model.CanPlot);
        if (model.ShowGraph && layout.GraphArea.Height > 1)
        {
            model.Graph.Area = layout.GraphArea;
        }

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
                {
                    if (layout.PlotButton.Width > 1 && layout.PlotButton.Contains(context.Position))
                    {
                        SetPressed(context.Object, "plot");
                        context.Host.Invalidate();
                        return true;
                    }

                    foreach (var row in layout.Rows)
                    {
                        if (row.ResultArea.Contains(context.Position) && model.Lines[row.Index].Result.Length > 0)
                        {
                            SetPressed(context.Object, $"result:{row.Index}");
                            context.Host.Invalidate();
                            return true;
                        }

                        if (row.Row.Contains(context.Position))
                        {
                            SetPressed(context.Object, $"row:{row.Index}");
                            context.Host.Invalidate();
                            return true;
                        }
                    }

                    if (model.ShowGraph && layout.GraphArea.Contains(context.Position))
                    {
                        SetPressed(context.Object, "graph");
                        SetDragging(context.Object, false);
                        SetLastPoint(context.Object, context.Position);
                        model.UpdateReadout(context.Position);
                        Save(context.Object, model);
                        context.Host.Invalidate();
                        return true;
                    }

                    return false;
                }

            case WidgetPointerPhase.Move:
                {
                    var pressed = ReadPressed(context.Object);
                    if (!string.Equals(pressed, "graph", StringComparison.Ordinal) || !context.IsPrimary)
                    {
                        return false;
                    }

                    var last = ReadLastPoint(context.Object) ?? context.Position;
                    model.Graph.PanByScreen(context.Position.X - last.X, context.Position.Y - last.Y);
                    SetLastPoint(context.Object, context.Position);
                    SetDragging(context.Object, true);
                    model.UpdateReadout(context.Position);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Up:
                {
                    var pressed = ReadPressed(context.Object);
                    SetPressed(context.Object, null);
                    if (pressed is null)
                    {
                        return false;
                    }

                    if (string.Equals(pressed, "plot", StringComparison.Ordinal))
                    {
                        model.ShowGraph = !model.ShowGraph;
                    }
                    else if (pressed.StartsWith("result:", StringComparison.Ordinal) &&
                             int.TryParse(pressed[7..], out var resultIndex))
                    {
                        model.InsertResultBelow(resultIndex);
                    }
                    else if (pressed.StartsWith("row:", StringComparison.Ordinal) &&
                             int.TryParse(pressed[4..], out var rowIndex))
                    {
                        model.SelectLine(rowIndex);
                    }
                    else if (string.Equals(pressed, "graph", StringComparison.Ordinal))
                    {
                        model.UpdateReadout(context.Position);
                    }

                    SetDragging(context.Object, false);
                    Save(context.Object, model);
                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Cancel:
                SetPressed(context.Object, null);
                SetDragging(context.Object, false);
                context.Host.Invalidate();
                return true;

            case WidgetPointerPhase.Wheel:
                if (!model.ShowGraph || !layout.GraphArea.Contains(context.Position))
                {
                    return false;
                }

                model.Graph.Area = layout.GraphArea;
                model.Graph.ZoomAt(context.Position, context.WheelDelta > 0 ? 0.88 : 1.14);
                model.UpdateReadout(context.Position);
                Save(context.Object, model);
                context.Host.Invalidate();
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        var model = MathNotesModel.From(context.Object, _engine);
        var key = context.Key ?? string.Empty;
        switch (key)
        {
            case "Return" or "Enter":
                model.NewLine();
                break;
            case "Back" or "BackSpace" or "Backspace":
                model.Backspace();
                break;
            case "Up":
                model.MoveSelection(-1);
                break;
            case "Down":
                model.MoveSelection(1);
                break;
            case "Escape" or "Delete" or "Clear":
                model.ClearLine();
                break;
            case "Tab":
                model.MoveSelection(1);
                break;
            default:
                if (key.Length == 1)
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

    private static void DrawGraph(DrawingContext drawing, PluginTheme theme, CRect area, MathNotesModel model)
    {
        drawing.DrawRectangle(
            theme.Brush(theme.IsDark ? Color.Parse("#FF151517") : Color.Parse("#FFF7F7FA")),
            null,
            new RoundedRect(new Rect(area.X, area.Y, area.Width, area.Height), 12));

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height));
        var graph = model.Graph;
        graph.Area = area;

        var xSpan = graph.XMax - graph.XMin;
        var ySpan = graph.YMax - graph.YMin;
        var xStep = NiceStep(xSpan / 6);
        var yStep = NiceStep(ySpan / 4);
        var gridPen = new Pen(theme.Brush(theme.GridLine), 1);
        var axisPen = new Pen(theme.Brush(theme.TextSecondary), 1.2);
        var labelBrush = theme.Brush(theme.TextSecondary);

        for (var x = Math.Ceiling(graph.XMin / xStep) * xStep; x <= graph.XMax && xStep > 0; x += xStep)
        {
            var p1 = graph.WorldToScreen(x, graph.YMin);
            var p2 = graph.WorldToScreen(x, graph.YMax);
            drawing.DrawLine(gridPen, new Point(p1.X, p1.Y), new Point(p2.X, p2.Y));
            var label = NumberFormatter.Format(x, 6);
            DrawText(drawing, label, new CRect(p1.X - 24, area.Bottom - 14, 48, 12), 9, labelBrush, Align.Center);
        }

        for (var y = Math.Ceiling(graph.YMin / yStep) * yStep; y <= graph.YMax && yStep > 0; y += yStep)
        {
            var p1 = graph.WorldToScreen(graph.XMin, y);
            var p2 = graph.WorldToScreen(graph.XMax, y);
            drawing.DrawLine(gridPen, new Point(p1.X, p1.Y), new Point(p2.X, p2.Y));
            DrawText(drawing, NumberFormatter.Format(y, 6), new CRect(area.X + 2, p1.Y - 6, 44, 12), 9, labelBrush, Align.Left);
        }

        if (graph.XMin <= 0 && graph.XMax >= 0)
        {
            var top = graph.WorldToScreen(0, graph.YMax);
            var bottom = graph.WorldToScreen(0, graph.YMin);
            drawing.DrawLine(axisPen, new Point(top.X, top.Y), new Point(bottom.X, bottom.Y));
        }

        if (graph.YMin <= 0 && graph.YMax >= 0)
        {
            var left = graph.WorldToScreen(graph.XMin, 0);
            var right = graph.WorldToScreen(graph.XMax, 0);
            drawing.DrawLine(axisPen, new Point(left.X, left.Y), new Point(right.X, right.Y));
        }

        var samples = (int)Math.Clamp(area.Width, 64, 420);
        var (xs, ys) = model.Sample(samples);
        if (xs.Length > 1)
        {
            var pen = new Pen(theme.Brush(theme.Accent), 2);
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

                    // A jump larger than the visible height means an asymptote: start a new figure.
                    if (open && Math.Abs(y - previousY) > ySpan * 1.5)
                    {
                        ctx.EndFigure(false);
                        open = false;
                    }

                    var point = graph.WorldToScreen(xs[i], y);
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

    private static void Panel(DrawingContext drawing, CRect rect, IBrush fill, double radius) =>
        drawing.DrawRectangle(fill, null, new RoundedRect(new Rect(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height)), radius));

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
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        var x = align switch
        {
            Align.Left => area.X + 4,
            Align.Right => area.Right - formatted.Width - 4,
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

        var size = Math.Max(minSize, maxSize);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        while (formatted.Width > area.Width - 6 && size > minSize)
        {
            size -= 0.5;
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        }

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height).Inflate(1));
        var x = align switch
        {
            Align.Left => area.X + 3,
            Align.Right => area.Right - formatted.Width - 3,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        drawing.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
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

    private static string? ReadPressed(WidgetObject target) =>
        target.State is { } state && state.TryGetPropertyValue("pressed", out var node) && node is not null
            ? node.ToString()
            : null;

    private static void SetPressed(WidgetObject target, string? action)
    {
        target.State ??= new JsonObject();
        target.State["pressed"] = action;
    }

    private static void SetDragging(WidgetObject target, bool dragging)
    {
        target.State ??= new JsonObject();
        target.State["dragging"] = dragging;
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
