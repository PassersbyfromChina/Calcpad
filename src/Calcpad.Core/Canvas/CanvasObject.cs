using System.Text.Json.Nodes;

namespace Calcpad.Core.Canvas;

/// <summary>Well known object kinds. Plugins may add their own kinds; the registry handles them.</summary>
public static class ObjectKinds
{
    public const string Ink = "ink";
    public const string Text = "text";
    public const string Math = "math";
    public const string Widget = "widget";
    public const string AiCard = "ai.card";
    public const string Image = "image";
    public const string Shape = "shape";
    public const string Group = "group";
}

/// <summary>
/// Base type for everything that lives on the infinite canvas. Bounds are in world coordinates
/// and are the only thing the hit tester and the renderer cull with, so derived types must keep
/// them up to date.
/// </summary>
public abstract class CanvasObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public abstract string Kind { get; }

    public CRect Bounds { get; set; }

    public int ZOrder { get; set; }

    public bool Locked { get; set; }

    public bool Visible { get; set; } = true;

    public double Opacity { get; set; } = 1.0;

    /// <summary>Plugin that created the object. Used for provenance and for bulk cleanup on uninstall.</summary>
    public string? OwnerPluginId { get; set; }

    public string? GroupId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Tags { get; set; } = [];

    public abstract CanvasObject Clone();

    /// <summary>Recomputes <see cref="Bounds"/> from the payload.</summary>
    public abstract void RecomputeBounds();

    public void Touch() => ModifiedAt = DateTimeOffset.UtcNow;

    protected void CopyBaseTo(CanvasObject target)
    {
        target.Id = Id;
        target.Bounds = Bounds;
        target.ZOrder = ZOrder;
        target.Locked = Locked;
        target.Visible = Visible;
        target.Opacity = Opacity;
        target.OwnerPluginId = OwnerPluginId;
        target.GroupId = GroupId;
        target.CreatedAt = CreatedAt;
        target.ModifiedAt = ModifiedAt;
        target.Tags = new Dictionary<string, string>(Tags);
    }
}

public enum InkTool
{
    Pen,
    Highlighter,
    Eraser
}

/// <summary>Handwriting / ink drawn on the canvas, optionally carrying its recognised text.</summary>
public sealed class InkObject : CanvasObject
{
    public override string Kind => ObjectKinds.Ink;

    public List<InkGroup> Groups { get; set; } = [];

    public InkTool Tool { get; set; } = InkTool.Pen;

    /// <summary>Result of the last recognition pass, if any.</summary>
    public string? RecognizedText { get; set; }

    public double? RecognitionConfidence { get; set; }

    public string? RecognitionEngine { get; set; }

    /// <summary>Language tags used for the last recognition pass, e.g. <c>zh-Hans</c>, <c>en</c>.</summary>
    public List<string> RecognitionLanguages { get; set; } = [];

    public IEnumerable<InkStroke> AllStrokes => Groups.SelectMany(g => g.Strokes);

    public override void RecomputeBounds()
    {
        var bounds = CRect.Bounding(Groups.Select(g => g.Bounds));
        if (bounds.IsEmpty && Groups.Count > 0)
        {
            bounds = CRect.Bounding(Groups.SelectMany(g => g.Strokes).Select(s => s.Bounds));
        }

        Bounds = bounds;
    }

    public override CanvasObject Clone()
    {
        var clone = new InkObject
        {
            Groups = [.. Groups.Select(g => g.Clone())],
            Tool = Tool,
            RecognizedText = RecognizedText,
            RecognitionConfidence = RecognitionConfidence,
            RecognitionEngine = RecognitionEngine,
            RecognitionLanguages = [.. RecognitionLanguages]
        };
        CopyBaseTo(clone);
        return clone;
    }
}

public sealed class TextObject : CanvasObject
{
    public override string Kind => ObjectKinds.Text;

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 18;

    public uint Color { get; set; } = 0xFF1B1B1F;

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public string FontFamily { get; set; } = "Inter, Segoe UI, Noto Sans CJK SC, sans-serif";

    public override void RecomputeBounds()
    {
        // Estimate: the renderer refines this once it has real text metrics.
        var lines = Text.Split('\n');
        var widest = lines.Length == 0 ? 0 : lines.Max(l => l.Length);
        var width = Math.Max(FontSize, widest * FontSize * 0.58);
        var height = Math.Max(FontSize * 1.4, lines.Length * FontSize * 1.35);
        Bounds = new CRect(Bounds.X, Bounds.Y, width, height);
    }

    public override CanvasObject Clone()
    {
        var clone = new TextObject
        {
            Text = Text,
            FontSize = FontSize,
            Color = Color,
            Bold = Bold,
            Italic = Italic,
            FontFamily = FontFamily
        };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>A live math block: what the user typed or recognised, plus its evaluated result.</summary>
public sealed class MathObject : CanvasObject
{
    public override string Kind => ObjectKinds.Math;

    public string Expression { get; set; } = string.Empty;

    public string? Result { get; set; }

    public bool IsError { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Variables captured at evaluation time, so the block stays reproducible.</summary>
    public Dictionary<string, string> Variables { get; set; } = [];

    public int Precision { get; set; } = 12;

    public bool ShowSteps { get; set; }

    public uint AccentColor { get; set; } = 0xFFFF9F0A;

    public override void RecomputeBounds()
    {
        var text = string.IsNullOrEmpty(Result) ? Expression : $"{Expression} = {Result}";
        var width = Math.Max(140, (text.Length * 10.5) + 32);
        var height = 56;
        Bounds = new CRect(Bounds.X, Bounds.Y, width, height);
    }

    public override CanvasObject Clone()
    {
        var clone = new MathObject
        {
            Expression = Expression,
            Result = Result,
            IsError = IsError,
            ErrorMessage = ErrorMessage,
            Variables = new Dictionary<string, string>(Variables),
            Precision = Precision,
            ShowSteps = ShowSteps,
            AccentColor = AccentColor
        };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>
/// A plugin supplied interactive widget placed on the canvas (calculator keypad, unit converter,
/// graphing pane, ...). The payload is opaque JSON owned by the contributing plugin.
/// </summary>
public sealed class WidgetObject : CanvasObject
{
    public override string Kind => ObjectKinds.Widget;

    public string WidgetType { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public JsonObject? State { get; set; }

    public override void RecomputeBounds() => Bounds = new CRect(Bounds.X, Bounds.Y, Math.Max(80, Bounds.Width), Math.Max(60, Bounds.Height));

    public override CanvasObject Clone()
    {
        var clone = new WidgetObject
        {
            WidgetType = WidgetType,
            Title = Title,
            State = State?.DeepClone() as JsonObject
        };
        CopyBaseTo(clone);
        return clone;
    }
}

/// <summary>An answer produced by the ask-AI plugin, kept on the canvas as a first class object.</summary>
public sealed class AiCardObject : CanvasObject
{
    public override string Kind => ObjectKinds.AiCard;

    public string Question { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    /// <summary>Where the answer came from, e.g. <c>on-device</c> or a provider display name.</summary>
    public string Provider { get; set; } = "on-device";

    public string? Model { get; set; }

    /// <summary>Plain text that was actually uploaded, kept for the user's own audit.</summary>
    public string? UploadedExcerpt { get; set; }

    public bool WasUploaded { get; set; }

    public bool IsStreaming { get; set; }

    public override void RecomputeBounds() =>
        Bounds = new CRect(Bounds.X, Bounds.Y, Math.Max(240, Bounds.Width), Math.Max(120, Bounds.Height));

    public override CanvasObject Clone()
    {
        var clone = new AiCardObject
        {
            Question = Question,
            Answer = Answer,
            Provider = Provider,
            Model = Model,
            UploadedExcerpt = UploadedExcerpt,
            WasUploaded = WasUploaded,
            IsStreaming = IsStreaming
        };
        CopyBaseTo(clone);
        return clone;
    }
}

public sealed class ImageObject : CanvasObject
{
    public override string Kind => ObjectKinds.Image;

    /// <summary>PNG bytes. Small crops only; large scans are referenced by path instead.</summary>
    public byte[] Png { get; set; } = [];

    public string? Caption { get; set; }

    public string? FilePath { get; set; }

    public override void RecomputeBounds() =>
        Bounds = new CRect(Bounds.X, Bounds.Y, Math.Max(40, Bounds.Width), Math.Max(40, Bounds.Height));

    public override CanvasObject Clone()
    {
        var clone = new ImageObject
        {
            Png = [.. Png],
            Caption = Caption,
            FilePath = FilePath
        };
        CopyBaseTo(clone);
        return clone;
    }
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Freeform
}

/// <summary>A lasso / selection outline, or a user drawn shape.</summary>
public sealed class ShapeObject : CanvasObject
{
    public override string Kind => ObjectKinds.Shape;

    public ShapeKind Shape { get; set; } = ShapeKind.Rectangle;

    public List<CPoint> Points { get; set; } = [];

    public uint StrokeColor { get; set; } = 0xFF0A84FF;

    public uint? FillColor { get; set; }

    public double StrokeWidth { get; set; } = 1.5;

    public bool Dashed { get; set; }

    /// <summary>Objects enclosed by this lasso, resolved when the selection was made.</summary>
    public List<string> CapturedObjectIds { get; set; } = [];

    public override void RecomputeBounds()
    {
        Bounds = Points.Count > 0
            ? CRect.FromPoints(Points)
            : new CRect(Bounds.X, Bounds.Y, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));
    }

    public override CanvasObject Clone()
    {
        var clone = new ShapeObject
        {
            Shape = Shape,
            Points = [.. Points],
            StrokeColor = StrokeColor,
            FillColor = FillColor,
            StrokeWidth = StrokeWidth,
            Dashed = Dashed,
            CapturedObjectIds = [.. CapturedObjectIds]
        };
        CopyBaseTo(clone);
        return clone;
    }
}

public sealed class GroupObject : CanvasObject
{
    public override string Kind => ObjectKinds.Group;

    public List<string> Children { get; set; } = [];

    public override void RecomputeBounds()
    {
        // Bounds are kept in sync by the engine, which can see the children.
    }

    public override CanvasObject Clone()
    {
        var clone = new GroupObject { Children = [.. Children] };
        CopyBaseTo(clone);
        return clone;
    }
}
