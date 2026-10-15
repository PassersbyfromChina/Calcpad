using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Calcpad.Core.Canvas;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Rendering;

/// <summary>
/// Supplies the widget host used while a <see cref="WidgetObject"/> is painted. The live canvas
/// view returns its real per plugin host; the offscreen rasteriser returns a minimal, side effect
/// free host so a PNG crop can never pop a dialog or throw.
/// </summary>
public interface IWidgetHostProvider
{
    /// <summary>Host for one widget, or null to paint the fallback placeholder.</summary>
    IWidgetHost? GetHost(WidgetObject widget);

    /// <summary>True when the widget currently owns the keyboard.</summary>
    bool IsWidgetFocused(WidgetObject widget) => false;
}

/// <summary>
/// Draws a <see cref="CanvasDocument"/> into any <see cref="DrawingContext"/>. This is the single
/// rendering implementation used by the live infinite canvas, by the PNG rasteriser that feeds
/// handwriting recognition and by the ask-AI plugin, so a crop always looks like what the user sees.
/// </summary>
/// <remarks>
/// Depends only on Calcpad.Core, Calcpad.Plugin.Sdk and Avalonia: the CanvasTools plugin links this
/// source file and must be able to compile it without referencing the application assembly.
/// </remarks>
public sealed class CanvasDocumentRenderer
{
    private const double MinGridPitchPixels = 26;
    private const double BaseGridStep = 32;
    private const int MaxImageCacheEntries = 64;

    private readonly IUiRegistry _ui;
    private readonly Dictionary<string, Typeface> _typefaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Length, Bitmap Bitmap)> _imageCache = new(StringComparer.Ordinal);
    private readonly PluginTheme _theme;

    /// <summary>Creates a renderer that resolves widget types through a UI registry.</summary>
    public CanvasDocumentRenderer(IUiRegistry ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _theme = SafeTheme(ui);
    }

    /// <summary>Widget host lookup, installed by the canvas view and by the rasteriser.</summary>
    public IWidgetHostProvider? Hosts { get; set; }

    /// <summary>
    /// Optional spatial index hook. When set, it returns the candidate objects for a world region;
    /// otherwise every document object is scanned (fine for small documents).
    /// </summary>
    public Func<CanvasDocument, CRect, IReadOnlyList<CanvasObject>>? VisibleObjects { get; set; }

    /// <summary>Reports a non fatal rendering failure so the shell can log or surface it.</summary>
    public Action<string, Exception?>? OnError { get; set; }

    /// <summary>The palette this renderer draws with.</summary>
    public PluginTheme Theme => _theme;

    /// <summary>
    /// Renders the document. <paramref name="viewport"/> is the target size in device independent
    /// pixels, so the rasteriser simply builds a camera whose zoom equals the requested pixel scale.
    /// </summary>
    /// <param name="ctx">Target drawing context.</param>
    /// <param name="doc">Document to draw.</param>
    /// <param name="camera">World to screen mapping.</param>
    /// <param name="viewport">Target size in device independent pixels.</param>
    /// <param name="drawGrid">Draw the adaptive background grid and axes.</param>
    /// <param name="drawSelection">Draw selection outlines and handles.</param>
    /// <param name="selected">Ids that count as selected; may be null.</param>
    public void Render(
        DrawingContext ctx,
        CanvasDocument doc,
        Camera camera,
        CSize viewport,
        bool drawGrid,
        bool drawSelection,
        IReadOnlySet<string>? selected)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(camera);
        if (!IsUsable(viewport) || !double.IsFinite(camera.Zoom) || camera.Zoom <= 0)
        {
            return;
        }

        var visible = camera.VisibleWorld(viewport);
        if (drawGrid)
        {
            DrawGrid(ctx, camera, viewport);
        }

        foreach (var obj in EnumerateVisible(doc, visible, camera))
        {
            try
            {
                DrawObject(ctx, obj, camera);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Drawing canvas object '{obj.Id}' ({obj.Kind}) failed.", ex);
            }
        }

        if (drawSelection && selected is { Count: > 0 })
        {
            DrawSelection(ctx, doc, camera, selected);
        }
    }

    /// <summary>Draws a single object. Public so tools can preview an object before it is committed.</summary>
    public void DrawObject(DrawingContext ctx, CanvasObject obj, Camera camera)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(camera);
        if (!obj.Visible)
        {
            return;
        }

        var opacity = double.IsFinite(obj.Opacity) ? Math.Clamp(obj.Opacity, 0, 1) : 1;
        if (opacity <= 0.01)
        {
            return;
        }

        using var state = opacity < 0.999 ? ctx.PushOpacity(opacity) : default;
        switch (obj)
        {
            case InkObject ink:
                DrawInk(ctx, ink, camera);
                break;

            case TextObject text:
                DrawText(ctx, text, camera);
                break;

            case MathObject math:
                DrawMath(ctx, math, camera);
                break;

            case AiCardObject card:
                DrawAiCard(ctx, card, camera);
                break;

            case ImageObject image:
                DrawImage(ctx, image, camera);
                break;

            case ShapeObject shape:
                DrawShape(ctx, shape, camera);
                break;

            case WidgetObject widget:
                DrawWidget(ctx, widget, camera);
                break;

            case GroupObject:
                break;

            default:
                DrawFallback(ctx, obj, camera);
                break;
        }
    }

    /// <summary>Drops cached decoded images; call when the document is replaced.</summary>
    public void ResetCaches()
    {
        foreach (var entry in _imageCache.Values)
        {
            entry.Bitmap.Dispose();
        }

        _imageCache.Clear();
        _typefaces.Clear();
    }

    private IEnumerable<CanvasObject> EnumerateVisible(CanvasDocument doc, CRect visible, Camera camera)
    {
        IReadOnlyList<CanvasObject> candidates;
        if (VisibleObjects is not null)
        {
            try
            {
                candidates = VisibleObjects(doc, visible);
            }
            catch (Exception ex)
            {
                OnError?.Invoke("The canvas spatial index query failed; falling back to a full scan.", ex);
                candidates = doc.Objects;
            }
        }
        else
        {
            candidates = doc.Objects;
        }

        var result = new List<CanvasObject>(candidates.Count);
        foreach (var obj in candidates)
        {
            if (!obj.Visible)
            {
                continue;
            }

            var bounds = obj.Bounds;
            if (bounds.IsEmpty && obj is GroupObject)
            {
                continue;
            }

            if (bounds.IntersectsWith(visible) || bounds.Inflate(32 / camera.Zoom).IntersectsWith(visible))
            {
                result.Add(obj);
            }
        }

        result.Sort(static (a, b) =>
        {
            var byZ = a.ZOrder.CompareTo(b.ZOrder);
            return byZ != 0 ? byZ : a.CreatedAt.CompareTo(b.CreatedAt);
        });
        return result;
    }

    // ----- background ---------------------------------------------------------------------------

    private void DrawGrid(DrawingContext ctx, Camera camera, CSize viewport)
    {
        var step = ChooseGridStep(camera.Zoom);
        var screenStep = step * camera.Zoom;
        if (!double.IsFinite(screenStep) || screenStep < 2)
        {
            return;
        }

        var origin = camera.WorldToScreen(CPoint.Origin);
        var minorBrush = new SolidColorBrush(_theme.GridLine);
        var majorBrush = new SolidColorBrush(WithAlpha(_theme.GridLine, 0x40));
        var minorPen = new Pen(minorBrush, 1);
        var majorPen = new Pen(majorBrush, 1);

        var firstX = origin.X - (Math.Ceiling(origin.X / screenStep) * screenStep);
        for (var x = firstX; x <= viewport.Width; x += screenStep)
        {
            var worldIndex = (int)Math.Round((x - origin.X) / screenStep);
            var pen = worldIndex % 5 == 0 ? majorPen : minorPen;
            var px = Math.Round(x) + 0.5;
            ctx.DrawLine(pen, new Point(px, 0), new Point(px, viewport.Height));
        }

        var firstY = origin.Y - (Math.Ceiling(origin.Y / screenStep) * screenStep);
        for (var y = firstY; y <= viewport.Height; y += screenStep)
        {
            var worldIndex = (int)Math.Round((y - origin.Y) / screenStep);
            var pen = worldIndex % 5 == 0 ? majorPen : minorPen;
            var py = Math.Round(y) + 0.5;
            ctx.DrawLine(pen, new Point(0, py), new Point(viewport.Width, py));
        }

        var axisBrush = new SolidColorBrush(WithAlpha(_theme.Selection, 0x59));
        var axisPen = new Pen(axisBrush, 1.4);
        if (origin.X >= 0 && origin.X <= viewport.Width)
        {
            var px = Math.Round(origin.X) + 0.5;
            ctx.DrawLine(axisPen, new Point(px, 0), new Point(px, viewport.Height));
        }

        if (origin.Y >= 0 && origin.Y <= viewport.Height)
        {
            var py = Math.Round(origin.Y) + 0.5;
            ctx.DrawLine(axisPen, new Point(0, py), new Point(viewport.Width, py));
        }
    }

    private static double ChooseGridStep(double zoom)
    {
        var step = BaseGridStep;
        var guard = 0;
        while (step * zoom < MinGridPitchPixels && guard++ < 64)
        {
            step *= step < 64 ? 2 : 1.25;
            step = Math.Round(step * 100) / 100;
        }

        return step;
    }

    // ----- objects ------------------------------------------------------------------------------

    private void DrawInk(DrawingContext ctx, InkObject ink, Camera camera)
    {
        foreach (var group in ink.Groups)
        {
            foreach (var stroke in group.Strokes)
            {
                DrawInkStroke(ctx, stroke, camera);
            }
        }
    }

    /// <summary>
    /// Draws one stroke. Public so a tool can preview the stroke it is currently capturing without
    /// waiting for the object to be committed.
    /// </summary>
    public void DrawInkStroke(DrawingContext ctx, InkStroke stroke, Camera camera)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(camera);
        if (stroke.Points.Count == 0 || !double.IsFinite(camera.Zoom) || camera.Zoom <= 0)
        {
            return;
        }

        var color = Color.FromUInt32(stroke.Color);
        var opacity = double.IsFinite(stroke.Opacity) ? Math.Clamp(stroke.Opacity, 0.02, 1) : 1;
        var brush = new SolidColorBrush(color, opacity);
        var width = Math.Clamp(stroke.Width * camera.Zoom, 0.4, 4096);
        if (stroke.Points.Count == 1)
        {
            var dot = camera.WorldToScreen(stroke.Points[0].ToPoint());
            var radius = Math.Max(0.3, width / 2);
            ctx.DrawEllipse(brush, null, new Point(dot.X, dot.Y), radius, radius);
            return;
        }

        var pen = new Pen(brush, width)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var geometry = BuildStrokeGeometry(stroke, camera);
        ctx.DrawGeometry(null, pen, geometry);
    }

    private static StreamGeometry BuildStrokeGeometry(InkStroke stroke, Camera camera)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        var first = camera.WorldToScreen(stroke.Points[0].ToPoint());
        path.BeginFigure(new Point(first.X, first.Y), false);
        if (stroke.Points.Count == 2)
        {
            var end = camera.WorldToScreen(stroke.Points[1].ToPoint());
            path.LineTo(new Point(end.X, end.Y));
        }
        else
        {
            // Midpoint quadratic smoothing: cheap, stable and visually identical to a spline fit.
            for (var i = 1; i < stroke.Points.Count - 1; i++)
            {
                var control = camera.WorldToScreen(stroke.Points[i].ToPoint());
                var next = camera.WorldToScreen(stroke.Points[i + 1].ToPoint());
                var mid = new Point((control.X + next.X) / 2, (control.Y + next.Y) / 2);
                path.QuadraticBezierTo(new Point(control.X, control.Y), mid);
            }

            var last = camera.WorldToScreen(stroke.Points[^1].ToPoint());
            path.LineTo(new Point(last.X, last.Y));
        }

        path.EndFigure(false);
        return geometry;
    }

    private void DrawText(DrawingContext ctx, TextObject text, Camera camera)
    {
        var rect = ToRect(camera.WorldToScreen(text.Bounds));
        var size = Math.Clamp(text.FontSize * camera.Zoom, 0.5, 2048);
        var brush = new SolidColorBrush(Color.FromUInt32(text.Color));
        var typeface = ResolveTypeface(text.FontFamily, text.Bold, text.Italic);
        var formatted = CreateText(text.Text, typeface, size, brush);
        formatted.MaxTextWidth = Math.Max(8, rect.Width);
        using (ctx.PushClip(rect.Inflate(2)))
        {
            ctx.DrawText(formatted, rect.TopLeft);
        }
    }

    private void DrawMath(DrawingContext ctx, MathObject math, Camera camera)
    {
        var rect = ToRect(camera.WorldToScreen(math.Bounds));
        var radius = Math.Clamp(12 * camera.Zoom, 0, Math.Min(rect.Width, rect.Height) / 2);
        var accent = Color.FromUInt32(math.AccentColor);
        var surface = new SolidColorBrush(_theme.Surface);
        var border = new Pen(new SolidColorBrush(_theme.SurfaceRaised), 1);
        ctx.DrawRectangle(surface, border, new RoundedRect(rect, radius));

        var barWidth = Math.Clamp(4 * camera.Zoom, 1.5, 8);
        var bar = new Rect(rect.X, rect.Y + (rect.Height * 0.16), barWidth, rect.Height * 0.68);
        ctx.DrawRectangle(new SolidColorBrush(accent), null, new RoundedRect(bar, barWidth / 2));

        var padding = Math.Clamp(10 * camera.Zoom, 2, 18);
        var textRect = new Rect(rect.X + padding + barWidth, rect.Y + padding, Math.Max(4, rect.Width - (padding * 2) - barWidth), Math.Max(4, rect.Height - (padding * 2)));
        using (ctx.PushClip(textRect))
        {
            var expressionSize = Math.Clamp(16 * camera.Zoom, 1, 512);
            var resultSize = Math.Clamp(15 * camera.Zoom, 1, 512);
            var expression = CreateText(math.Expression, ResolveTypeface(null, false, false), expressionSize, new SolidColorBrush(_theme.TextPrimary));
            var resultText = math.IsError
                ? (string.IsNullOrEmpty(math.ErrorMessage) ? "error" : math.ErrorMessage!)
                : math.Result ?? string.Empty;
            var resultBrush = math.IsError ? new SolidColorBrush(_theme.Danger) : new SolidColorBrush(accent);

            // Two lines when the card is tall enough, otherwise one compact line that never clips.
            if (resultText.Length > 0 && textRect.Height >= (expressionSize * 1.35) + (resultSize * 1.45))
            {
                expression.MaxTextWidth = textRect.Width;
                expression.Trimming = TextTrimming.CharacterEllipsis;
                ctx.DrawText(expression, textRect.TopLeft);

                var result = CreateText(resultText, ResolveTypeface(null, true, false), resultSize, resultBrush);
                result.MaxTextWidth = textRect.Width;
                result.MaxTextHeight = Math.Max(4, textRect.Bottom - (textRect.Y + (expressionSize * 1.3)));
                result.Trimming = TextTrimming.CharacterEllipsis;
                ctx.DrawText(result, new Point(textRect.X, textRect.Y + (expressionSize * 1.3)));
                return;
            }

            var line = resultText.Length == 0 ? math.Expression : $"{math.Expression} = {resultText}";
            var combined = CreateText(line, ResolveTypeface(null, false, false), expressionSize, new SolidColorBrush(_theme.TextPrimary));
            combined.MaxTextWidth = textRect.Width;
            combined.MaxTextHeight = textRect.Height;
            combined.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(combined, textRect.TopLeft);
        }
    }

    private void DrawAiCard(DrawingContext ctx, AiCardObject card, Camera camera)
    {
        var rect = ToRect(camera.WorldToScreen(card.Bounds));
        var radius = Math.Clamp(14 * camera.Zoom, 0, Math.Min(rect.Width, rect.Height) / 2);
        var surface = new SolidColorBrush(_theme.Surface);
        var border = new Pen(new SolidColorBrush(card.WasUploaded ? _theme.Accent : _theme.SurfaceRaised), 1);
        ctx.DrawRectangle(surface, border, new RoundedRect(rect, radius));

        var padding = Math.Clamp(12 * camera.Zoom, 2, 22);
        var innerWidth = Math.Max(4, rect.Width - (padding * 2));
        var y = rect.Y + padding;

        var headerSize = Math.Clamp(12 * camera.Zoom, 1, 256);
        var header = CreateText(
            card.IsStreaming ? $"{card.Provider} · {card.Model ?? "local"} · thinking…" : $"{card.Provider} · {card.Model ?? "local"}",
            ResolveTypeface(null, true, false),
            headerSize,
            new SolidColorBrush(_theme.TextSecondary));
        header.MaxTextWidth = innerWidth;
        header.Trimming = TextTrimming.CharacterEllipsis;
        ctx.DrawText(header, new Point(rect.X + padding, y));
        y += header.Height + (padding * 0.4);

        var questionSize = Math.Clamp(14 * camera.Zoom, 1, 320);
        var question = CreateText(card.Question, ResolveTypeface(null, false, false), questionSize, new SolidColorBrush(_theme.TextSecondary));
        question.MaxTextWidth = innerWidth;
        question.MaxTextHeight = Math.Max(4, rect.Height * 0.35);
        question.Trimming = TextTrimming.CharacterEllipsis;
        ctx.DrawText(question, new Point(rect.X + padding, y));
        y += question.Height + (padding * 0.5);

        var answerSize = Math.Clamp(15 * camera.Zoom, 1, 320);
        var answer = CreateText(card.Answer, ResolveTypeface(null, false, false), answerSize, new SolidColorBrush(_theme.TextPrimary));
        answer.MaxTextWidth = innerWidth;
        answer.MaxTextHeight = Math.Max(4, rect.Bottom - padding - y);
        answer.Trimming = TextTrimming.CharacterEllipsis;
        using (ctx.PushClip(new Rect(rect.X + padding, y, innerWidth, Math.Max(4, rect.Bottom - padding - y))))
        {
            ctx.DrawText(answer, new Point(rect.X + padding, y));
        }
    }

    private void DrawImage(DrawingContext ctx, ImageObject image, Camera camera)
    {
        var rect = ToRect(camera.WorldToScreen(image.Bounds));
        var bitmap = GetImage(image);
        if (bitmap is not null)
        {
            ctx.DrawImage(bitmap, rect);
        }
        else
        {
            var surface = new SolidColorBrush(_theme.SurfaceAlt);
            ctx.DrawRectangle(surface, new Pen(new SolidColorBrush(_theme.SurfaceRaised), 1), new RoundedRect(rect, 6));
            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                var size = Math.Clamp(12 * camera.Zoom, 1, 256);
                var caption = CreateText(image.Caption, ResolveTypeface(null, false, false), size, new SolidColorBrush(_theme.TextSecondary));
                caption.MaxTextWidth = Math.Max(4, rect.Width - 8);
                caption.Trimming = TextTrimming.CharacterEllipsis;
                ctx.DrawText(caption, new Point(rect.X + 4, rect.Y + 4));
            }
        }

        if (!string.IsNullOrWhiteSpace(image.Caption) && bitmap is not null)
        {
            var size = Math.Clamp(12 * camera.Zoom, 1, 256);
            var caption = CreateText(image.Caption, ResolveTypeface(null, false, false), size, new SolidColorBrush(_theme.TextSecondary));
            caption.MaxTextWidth = Math.Max(4, rect.Width);
            caption.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(caption, new Point(rect.X, rect.Bottom + 2));
        }
    }

    private void DrawShape(DrawingContext ctx, ShapeObject shape, Camera camera)
    {
        var rect = ToRect(camera.WorldToScreen(shape.Bounds));
        var stroke = new SolidColorBrush(Color.FromUInt32(shape.StrokeColor));
        var thickness = Math.Clamp(shape.StrokeWidth * camera.Zoom, 0.4, 512);
        IBrush? fill = shape.FillColor.HasValue ? new SolidColorBrush(Color.FromUInt32(shape.FillColor.Value)) : null;
        var pen = new Pen(stroke, thickness);
        if (shape.Dashed)
        {
            pen.DashStyle = new DashStyle([4, 3], 0);
        }

        switch (shape.Shape)
        {
            case ShapeKind.Rectangle:
                ctx.DrawRectangle(fill, pen, rect);
                break;

            case ShapeKind.Ellipse:
                ctx.DrawEllipse(fill, pen, rect);
                break;

            case ShapeKind.Arrow:
            case ShapeKind.Line:
            case ShapeKind.Freeform:
            default:
                DrawPolyline(ctx, shape, camera, pen, fill);
                break;
        }
    }

    private static void DrawPolyline(DrawingContext ctx, ShapeObject shape, Camera camera, Pen pen, IBrush? fill)
    {
        if (shape.Points.Count == 0)
        {
            return;
        }

        if (shape.Points.Count == 1)
        {
            var only = camera.WorldToScreen(shape.Points[0]);
            ctx.DrawEllipse(pen.Brush, null, new Point(only.X, only.Y), Math.Max(0.5, pen.Thickness / 2), Math.Max(0.5, pen.Thickness / 2));
            return;
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var first = camera.WorldToScreen(shape.Points[0]);
            path.BeginFigure(new Point(first.X, first.Y), fill is not null);
            for (var i = 1; i < shape.Points.Count; i++)
            {
                var point = camera.WorldToScreen(shape.Points[i]);
                path.LineTo(new Point(point.X, point.Y));
            }

            path.EndFigure(shape.Shape == ShapeKind.Freeform);
        }

        ctx.DrawGeometry(fill, pen, geometry);

        if (shape.Shape == ShapeKind.Arrow)
        {
            DrawArrowHead(ctx, shape, camera, pen);
        }
    }

    private static void DrawArrowHead(DrawingContext ctx, ShapeObject shape, Camera camera, Pen pen)
    {
        var tip = camera.WorldToScreen(shape.Points[^1]);
        var previous = camera.WorldToScreen(shape.Points[^2]);
        var dx = tip.X - previous.X;
        var dy = tip.Y - previous.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-6)
        {
            return;
        }

        var size = Math.Clamp(pen.Thickness * 4, 6, 64);
        var ux = dx / length;
        var uy = dy / length;
        var baseX = tip.X - (ux * size);
        var baseY = tip.Y - (uy * size);
        var half = size * 0.42;
        var left = new Point(baseX - (uy * half), baseY + (ux * half));
        var right = new Point(baseX + (uy * half), baseY - (ux * half));
        var head = new StreamGeometry();
        using (var path = head.Open())
        {
            path.BeginFigure(new Point(tip.X, tip.Y), true);
            path.LineTo(left);
            path.LineTo(right);
            path.EndFigure(true);
        }

        ctx.DrawGeometry(pen.Brush, null, head);
    }

    private void DrawWidget(DrawingContext ctx, WidgetObject widget, Camera camera)
    {
        if (string.IsNullOrWhiteSpace(widget.WidgetType))
        {
            DrawWidgetPlaceholder(ctx, widget, camera, "widget type missing");
            return;
        }

        var renderer = _ui.FindWidget(widget.WidgetType);
        if (renderer is null)
        {
            DrawWidgetPlaceholder(ctx, widget, camera, $"widget '{widget.WidgetType}' unavailable");
            return;
        }

        var host = Hosts?.GetHost(widget);
        if (host is null)
        {
            DrawWidgetPlaceholder(ctx, widget, camera, $"widget '{widget.WidgetType}' has no host");
            return;
        }

        var bounds = camera.WorldToScreen(widget.Bounds);
        var context = new WidgetRenderContext
        {
            Drawing = ctx,
            Bounds = new CRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            Object = widget,
            Host = host,
            Theme = _theme,
            Zoom = camera.Zoom,
            IsFocused = Hosts?.IsWidgetFocused(widget) ?? false,
            IsSelected = false
        };

        try
        {
            renderer.Render(context);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Widget '{widget.WidgetType}' failed to render.", ex);
            DrawWidgetPlaceholder(ctx, widget, camera, $"widget '{widget.WidgetType}' failed");
        }
    }

    private void DrawWidgetPlaceholder(DrawingContext ctx, WidgetObject widget, Camera camera, string message)
    {
        var rect = ToRect(camera.WorldToScreen(widget.Bounds));
        ctx.DrawRectangle(
            new SolidColorBrush(_theme.SurfaceAlt),
            new Pen(new SolidColorBrush(_theme.Danger), 1),
            new RoundedRect(rect, 8));
        var size = Math.Clamp(12 * camera.Zoom, 1, 256);
        var title = string.IsNullOrWhiteSpace(widget.Title) ? widget.WidgetType : widget.Title;
        var text = CreateText($"{title}\n{message}", ResolveTypeface(null, false, false), size, new SolidColorBrush(_theme.TextSecondary));
        text.MaxTextWidth = Math.Max(4, rect.Width - 12);
        text.MaxTextHeight = Math.Max(4, rect.Height - 12);
        text.Trimming = TextTrimming.CharacterEllipsis;
        ctx.DrawText(text, new Point(rect.X + 6, rect.Y + 6));
    }

    private void DrawFallback(DrawingContext ctx, CanvasObject obj, Camera camera)
    {
        var rect = ToRect(camera.WorldToScreen(obj.Bounds));
        ctx.DrawRectangle(
            new SolidColorBrush(_theme.SurfaceAlt),
            new Pen(new SolidColorBrush(_theme.SurfaceRaised), 1),
            new RoundedRect(rect, 6));
        var size = Math.Clamp(12 * camera.Zoom, 1, 256);
        var text = CreateText(obj.Kind, ResolveTypeface(null, false, false), size, new SolidColorBrush(_theme.TextSecondary));
        text.MaxTextWidth = Math.Max(4, rect.Width - 8);
        text.Trimming = TextTrimming.CharacterEllipsis;
        ctx.DrawText(text, new Point(rect.X + 4, rect.Y + 4));
    }

    private void DrawSelection(DrawingContext ctx, CanvasDocument doc, Camera camera, IReadOnlySet<string> selected)
    {
        var pen = new Pen(new SolidColorBrush(_theme.Selection), 1.5);
        var handleBrush = new SolidColorBrush(_theme.Selection);
        foreach (var obj in doc.Objects)
        {
            if (!obj.Visible || !selected.Contains(obj.Id))
            {
                continue;
            }

            var rect = ToRect(camera.WorldToScreen(obj.Bounds)).Inflate(3);
            ctx.DrawRectangle(null, pen, new RoundedRect(rect, 4));
            var handle = Math.Clamp(6 / Math.Max(camera.Zoom, 0.001), 2, 12);
            foreach (var corner in new[]
                     {
                         rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight
                     })
            {
                ctx.DrawRectangle(handleBrush, null, new Rect(corner.X - (handle / 2), corner.Y - (handle / 2), handle, handle));
            }
        }
    }

    // ----- helpers ------------------------------------------------------------------------------

    private Bitmap? GetImage(ImageObject image)
    {
        if (image.Png.Length == 0 || image.Png.Length > 32 * 1024 * 1024)
        {
            return null;
        }

        if (_imageCache.TryGetValue(image.Id, out var cached) && cached.Length == image.Png.Length)
        {
            return cached.Bitmap;
        }

        try
        {
            using var stream = new MemoryStream(image.Png, writable: false);
            var bitmap = new Bitmap(stream);
            if (_imageCache.Remove(image.Id, out var previous))
            {
                previous.Bitmap.Dispose();
            }

            if (_imageCache.Count >= MaxImageCacheEntries)
            {
                foreach (var entry in _imageCache.Values)
                {
                    entry.Bitmap.Dispose();
                }

                _imageCache.Clear();
            }

            _imageCache[image.Id] = (image.Png.Length, bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Image object '{image.Id}' could not be decoded.", ex);
            return null;
        }
    }

    private Typeface ResolveTypeface(string? family, bool bold, bool italic)
    {
        var key = $"{family}|{bold}|{italic}";
        if (_typefaces.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var style = italic ? FontStyle.Italic : FontStyle.Normal;
        var weight = bold ? FontWeight.SemiBold : FontWeight.Normal;
        Typeface typeface;
        try
        {
            var fontFamily = string.IsNullOrWhiteSpace(family)
                ? FontFamily.Default
                : FontFamily.Parse(family);
            typeface = new Typeface(fontFamily, style, weight, FontStretch.Normal);
        }
        catch
        {
            typeface = new Typeface(FontFamily.Default, style, weight, FontStretch.Normal);
        }

        _typefaces[key] = typeface;
        return typeface;
    }

    private static FormattedText CreateText(string? text, Typeface typeface, double size, IBrush brush) =>
        new(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(0.5, size),
            brush);

    private static PluginTheme SafeTheme(IUiRegistry ui)
    {
        try
        {
            return ui.Theme ?? PluginTheme.IpadDark;
        }
        catch
        {
            return PluginTheme.IpadDark;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static bool IsUsable(CSize size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;

    private static Rect ToRect(CRect rect) =>
        new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
}
