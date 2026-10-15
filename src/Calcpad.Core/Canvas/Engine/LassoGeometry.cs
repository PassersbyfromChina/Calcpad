namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Polygon helpers behind lasso selection. Everything is allocation free and tolerant of
/// degenerate input (a single point, repeated points, non finite coordinates) so a stray pointer
/// sample can never crash a selection.
/// </summary>
public static class LassoGeometry
{
    /// <summary>Tolerance used when comparing coordinates.</summary>
    public const double Epsilon = 1e-9;

    /// <summary>
    /// Copies a pointer trail into a clean polygon: non finite points are dropped, consecutive
    /// duplicates are collapsed and a closing point identical to the first is removed.
    /// </summary>
    /// <returns>The number of points written to <paramref name="output"/>.</returns>
    public static int Normalize(IReadOnlyList<CPoint>? source, List<CPoint> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        if (source is null)
        {
            return 0;
        }

        foreach (var point in source)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                continue;
            }

            if (output.Count > 0 && Same(output[^1], point))
            {
                continue;
            }

            output.Add(point);
        }

        if (output.Count > 2 && Same(output[0], output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        return output.Count;
    }

    /// <summary>Even-odd point in polygon test.</summary>
    public static bool PointInPolygon(CPoint point, IReadOnlyList<CPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var count = polygon.Count;
        if (count == 0)
        {
            return false;
        }

        if (count == 1)
        {
            return Same(point, polygon[0]);
        }

        if (count == 2)
        {
            return DistanceToSegment(point, polygon[0], polygon[1]) <= Epsilon;
        }

        var inside = false;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            var current = polygon[i];
            var previous = polygon[j];
            if ((current.Y > point.Y) != (previous.Y > point.Y))
            {
                var crossX = (((previous.X - current.X) * (point.Y - current.Y)) / (previous.Y - current.Y)) + current.X;
                if (point.X < crossX)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>True when the rectangle touches the polygon: overlap, containment or crossing.</summary>
    public static bool RectIntersectsPolygon(CRect rect, IReadOnlyList<CPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var count = polygon.Count;
        if (count == 0 || rect.IsEmpty)
        {
            return false;
        }

        if (count == 1)
        {
            return rect.Contains(polygon[0]);
        }

        if (count == 2)
        {
            return SegmentIntersectsRect(polygon[0], polygon[1], rect);
        }

        foreach (var vertex in polygon)
        {
            if (rect.Contains(vertex))
            {
                return true;
            }
        }

        if (PointInPolygon(rect.TopLeft, polygon) ||
            PointInPolygon(new CPoint(rect.Right, rect.Top), polygon) ||
            PointInPolygon(new CPoint(rect.Right, rect.Bottom), polygon) ||
            PointInPolygon(new CPoint(rect.Left, rect.Bottom), polygon))
        {
            return true;
        }

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            if (SegmentIntersectsRect(polygon[j], polygon[i], rect))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the rectangle lies completely inside the polygon. A rectangle that merely touches
    /// the outline is not considered enclosed.
    /// </summary>
    public static bool RectInsidePolygon(CRect rect, IReadOnlyList<CPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var count = polygon.Count;
        if (count < 3)
        {
            return false;
        }

        var topLeft = rect.TopLeft;
        var topRight = new CPoint(rect.Right, rect.Top);
        var bottomRight = new CPoint(rect.Right, rect.Bottom);
        var bottomLeft = new CPoint(rect.Left, rect.Bottom);
        if (!PointInPolygon(topLeft, polygon) ||
            !PointInPolygon(topRight, polygon) ||
            !PointInPolygon(bottomRight, polygon) ||
            !PointInPolygon(bottomLeft, polygon))
        {
            return false;
        }

        // A concave polygon can contain all four corners while an edge still cuts through the
        // rectangle, so the outline must be checked as well.
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            if (SegmentIntersectsRect(polygon[j], polygon[i], rect))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the segment touches or crosses the rectangle.</summary>
    public static bool SegmentIntersectsRect(CPoint a, CPoint b, CRect rect)
    {
        if (rect.Contains(a) || rect.Contains(b))
        {
            return true;
        }

        var topLeft = new CPoint(rect.Left, rect.Top);
        var topRight = new CPoint(rect.Right, rect.Top);
        var bottomRight = new CPoint(rect.Right, rect.Bottom);
        var bottomLeft = new CPoint(rect.Left, rect.Bottom);
        return SegmentsIntersect(a, b, topLeft, topRight) ||
               SegmentsIntersect(a, b, topRight, bottomRight) ||
               SegmentsIntersect(a, b, bottomRight, bottomLeft) ||
               SegmentsIntersect(a, b, bottomLeft, topLeft);
    }

    /// <summary>True when the two closed segments share at least one point.</summary>
    public static bool SegmentsIntersect(CPoint p1, CPoint p2, CPoint p3, CPoint p4)
    {
        var d1 = Cross(p3, p4, p1);
        var d2 = Cross(p3, p4, p2);
        var d3 = Cross(p1, p2, p3);
        var d4 = Cross(p1, p2, p4);

        if (Opposite(d1, d2) && Opposite(d3, d4))
        {
            return true;
        }

        if (IsZero(d1) && OnSegment(p3, p4, p1))
        {
            return true;
        }

        if (IsZero(d2) && OnSegment(p3, p4, p2))
        {
            return true;
        }

        if (IsZero(d3) && OnSegment(p1, p2, p3))
        {
            return true;
        }

        return IsZero(d4) && OnSegment(p1, p2, p4);
    }

    /// <summary>Shortest distance from a point to a segment.</summary>
    public static double DistanceToSegment(CPoint point, CPoint a, CPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-18)
        {
            return point.DistanceTo(a);
        }

        var t = Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return point.DistanceTo(new CPoint(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>Shortest distance from a point to a polyline.</summary>
    public static double DistanceToPolyline(CPoint point, IReadOnlyList<CPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (points.Count == 1)
        {
            return point.DistanceTo(points[0]);
        }

        var best = double.PositiveInfinity;
        for (var i = 1; i < points.Count; i++)
        {
            var distance = DistanceToSegment(point, points[i - 1], points[i]);
            if (distance < best)
            {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>True when the two points are within <see cref="Epsilon"/>.</summary>
    public static bool Same(CPoint a, CPoint b) =>
        Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Y - b.Y) <= Epsilon;

    private static double Cross(CPoint a, CPoint b, CPoint c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static bool Opposite(double a, double b) => (a > Epsilon && b < -Epsilon) || (a < -Epsilon && b > Epsilon);

    private static bool IsZero(double value) => Math.Abs(value) <= Epsilon;

    private static bool OnSegment(CPoint a, CPoint b, CPoint p) =>
        p.X >= Math.Min(a.X, b.X) - Epsilon &&
        p.X <= Math.Max(a.X, b.X) + Epsilon &&
        p.Y >= Math.Min(a.Y, b.Y) - Epsilon &&
        p.Y <= Math.Max(a.Y, b.Y) + Epsilon;
}
