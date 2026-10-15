namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Precise hit testing per object kind. Bounds only give a coarse answer, so ink and shapes are
/// tested against their real geometry; everything else uses bounds with a small touch slop so thin
/// objects stay easy to grab with a finger.
/// </summary>
internal static class CanvasHitTester
{
    /// <summary>Touch slop in world units added on top of the caller supplied tolerance.</summary>
    internal const double Slop = 2.0;

    /// <summary>True when <paramref name="world"/> is inside or close enough to <paramref name="obj"/>.</summary>
    internal static bool Hits(CanvasObject obj, CPoint world, double tolerance)
    {
        var slack = double.IsFinite(tolerance) && tolerance > 0 ? tolerance : 0;
        return obj switch
        {
            InkObject ink => HitsInk(ink, world, slack),
            ShapeObject shape => HitsShape(shape, world, slack),
            _ => obj.Bounds.Inflate(slack + Slop).Contains(world)
        };
    }

    /// <summary>True when one stroke passes within <paramref name="threshold"/> of the point.</summary>
    internal static bool HitsStroke(InkStroke stroke, CPoint world, double threshold)
    {
        if (stroke.Points.Count == 0)
        {
            return false;
        }

        var width = double.IsFinite(stroke.Width) && stroke.Width > 0 ? stroke.Width / 2 : 0;
        var reach = threshold + width;
        if (!stroke.Bounds.Inflate(reach).Contains(world))
        {
            return false;
        }

        if (stroke.Points.Count == 1)
        {
            return world.DistanceTo(stroke.Points[0].ToPoint()) <= reach;
        }

        var points = new List<CPoint>(stroke.Points.Count);
        foreach (var point in stroke.Points)
        {
            points.Add(point.ToPoint());
        }

        return LassoGeometry.DistanceToPolyline(world, points) <= reach;
    }

    private static bool HitsInk(InkObject ink, CPoint world, double slack)
    {
        foreach (var stroke in ink.AllStrokes)
        {
            if (HitsStroke(stroke, world, slack))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HitsShape(ShapeObject shape, CPoint world, double slack)
    {
        var tolerance = slack + Slop;
        var bounds = shape.Bounds;
        if (bounds.IsEmpty || !bounds.Inflate(tolerance).Contains(world))
        {
            return false;
        }

        var filled = shape.FillColor.HasValue;
        switch (shape.Shape)
        {
            case ShapeKind.Rectangle:
                return filled ? bounds.Contains(world) : DistanceToRectBorder(world, bounds) <= tolerance;

            case ShapeKind.Ellipse:
            {
                var radiusX = bounds.Width / 2;
                var radiusY = bounds.Height / 2;
                if (radiusX <= 0 || radiusY <= 0)
                {
                    return bounds.Contains(world);
                }

                var center = bounds.Center;
                var nx = (world.X - center.X) / radiusX;
                var ny = (world.Y - center.Y) / radiusY;
                var distance = Math.Sqrt((nx * nx) + (ny * ny));
                if (filled)
                {
                    return distance <= 1 + (tolerance / Math.Min(radiusX, radiusY));
                }

                return Math.Abs(distance - 1) * Math.Min(radiusX, radiusY) <= tolerance;
            }

            default:
                if (shape.Points.Count == 0)
                {
                    return filled ? bounds.Contains(world) : DistanceToRectBorder(world, bounds) <= tolerance;
                }

                if (filled && LassoGeometry.PointInPolygon(world, shape.Points))
                {
                    return true;
                }

                return LassoGeometry.DistanceToPolyline(world, shape.Points) <= tolerance;
        }
    }

    /// <summary>Distance from a point to the outline of a rectangle.</summary>
    internal static double DistanceToRectBorder(CPoint point, CRect rect)
    {
        var dx = Math.Max(Math.Max(rect.Left - point.X, 0), point.X - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - point.Y, 0), point.Y - rect.Bottom);
        if (dx > 0 || dy > 0)
        {
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        return Math.Min(
            Math.Min(point.X - rect.Left, rect.Right - point.X),
            Math.Min(point.Y - rect.Top, rect.Bottom - point.Y));
    }
}
