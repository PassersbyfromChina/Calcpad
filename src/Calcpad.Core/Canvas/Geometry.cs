namespace Calcpad.Core.Canvas;

public readonly record struct CPoint(double X, double Y)
{
    public static CPoint Origin => new(0, 0);

    public CPoint Offset(double dx, double dy) => new(X + dx, Y + dy);

    public double DistanceTo(CPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

public readonly record struct CSize(double Width, double Height)
{
    public static CSize Empty => new(0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct CRect(double X, double Y, double Width, double Height)
{
    public static CRect Empty => new(0, 0, 0, 0);

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public CPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public CPoint TopLeft => new(X, Y);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(CPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool Contains(CRect other) =>
        other.Left >= Left && other.Right <= Right && other.Top >= Top && other.Bottom <= Bottom;

    public bool IntersectsWith(CRect other) =>
        other.Left <= Right && other.Right >= Left && other.Top <= Bottom && other.Bottom >= Top;

    public CRect Inflate(double amount) =>
        new(X - amount, Y - amount, Width + (amount * 2), Height + (amount * 2));

    public CRect Union(CRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        var left = Math.Min(Left, other.Left);
        var top = Math.Min(Top, other.Top);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new CRect(left, top, right - left, bottom - top);
    }

    public CRect Translate(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public static CRect FromPoints(IEnumerable<CPoint> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var any = false;
        foreach (var p in points)
        {
            any = true;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return any ? new CRect(minX, minY, maxX - minX, maxY - minY) : Empty;
    }

    public static CRect Bounding(IEnumerable<CRect> rects)
    {
        var result = Empty;
        foreach (var r in rects)
        {
            result = result.Union(r);
        }

        return result;
    }

    public override string ToString() => $"[{X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}]";
}

/// <summary>
/// Maps an unbounded world coordinate system to the viewport.
/// screen = (world - offset) * zoom.
/// </summary>
public sealed class Camera
{
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Zoom { get; set; } = 1.0;

    public double MinZoom { get; set; } = 0.05;

    public double MaxZoom { get; set; } = 12.0;

    public CPoint WorldToScreen(CPoint world) => new((world.X - OffsetX) * Zoom, (world.Y - OffsetY) * Zoom);

    public CPoint ScreenToWorld(CPoint screen) => new((screen.X / Zoom) + OffsetX, (screen.Y / Zoom) + OffsetY);

    public CRect WorldToScreen(CRect world) =>
        new((world.X - OffsetX) * Zoom, (world.Y - OffsetY) * Zoom, world.Width * Zoom, world.Height * Zoom);

    public CRect VisibleWorld(CSize viewport) =>
        new(OffsetX, OffsetY, viewport.Width / Zoom, viewport.Height / Zoom);

    public void PanByScreen(double screenDx, double screenDy)
    {
        OffsetX -= screenDx / Zoom;
        OffsetY -= screenDy / Zoom;
    }

    public void PanByWorld(double worldDx, double worldDy)
    {
        OffsetX += worldDx;
        OffsetY += worldDy;
    }

    /// <summary>Zooms while keeping the world point under <paramref name="screenAnchor"/> fixed.</summary>
    public void ZoomAt(CPoint screenAnchor, double factor)
    {
        var before = ScreenToWorld(screenAnchor);
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var after = ScreenToWorld(screenAnchor);
        OffsetX += before.X - after.X;
        OffsetY += before.Y - after.Y;
    }

    public void CenterOn(CPoint world, CSize viewport)
    {
        OffsetX = world.X - (viewport.Width / Zoom / 2);
        OffsetY = world.Y - (viewport.Height / Zoom / 2);
    }

    public void FitTo(CRect world, CSize viewport, double paddingPixels = 48)
    {
        if (world.IsEmpty || viewport.IsEmpty)
        {
            return;
        }

        var usableWidth = Math.Max(1, viewport.Width - (paddingPixels * 2));
        var usableHeight = Math.Max(1, viewport.Height - (paddingPixels * 2));
        var zoom = Math.Min(usableWidth / world.Width, usableHeight / world.Height);
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        CenterOn(world.Center, viewport);
    }

    public Camera Clone() => new()
    {
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        Zoom = Zoom,
        MinZoom = MinZoom,
        MaxZoom = MaxZoom
    };
}
