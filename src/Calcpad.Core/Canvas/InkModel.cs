namespace Calcpad.Core.Canvas;

/// <summary>One sampled point of a pen stroke, in world coordinates.</summary>
public readonly record struct InkPoint(double X, double Y, double Pressure, double TimeMs)
{
    public CPoint ToPoint() => new(X, Y);
}

public sealed class InkStroke
{
    public List<InkPoint> Points { get; set; } = [];

    /// <summary>Stroke width in world units.</summary>
    public double Width { get; set; } = 2.5;

    /// <summary>ARGB colour.</summary>
    public uint Color { get; set; } = 0xFF1B1B1F;

    public double Opacity { get; set; } = 1.0;

    public CRect Bounds => CRect.FromPoints(Points.Select(p => p.ToPoint()));

    public double Length
    {
        get
        {
            var total = 0.0;
            for (var i = 1; i < Points.Count; i++)
            {
                total += new CPoint(Points[i - 1].X, Points[i - 1].Y).DistanceTo(new CPoint(Points[i].X, Points[i].Y));
            }

            return total;
        }
    }

    public InkStroke Clone() => new()
    {
        Points = [.. Points],
        Width = Width,
        Color = Color,
        Opacity = Opacity
    };
}

/// <summary>
/// A single handwriting group. Kept separate from the visible object so recognition can
/// regroup strokes (per line, per word) without touching what the user drew.
/// </summary>
public sealed class InkGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public List<InkStroke> Strokes { get; set; } = [];

    public CRect Bounds => CRect.Bounding(Strokes.Select(s => s.Bounds));

    public InkGroup Clone() => new()
    {
        Id = Id,
        Strokes = [.. Strokes.Select(s => s.Clone())]
    };
}
