using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>One stroke after arc length resampling and glyph box normalisation (unit box, y down).</summary>
public sealed record GlyphStroke(IReadOnlyList<CPoint> Points);

/// <summary>
/// Resampling, glyph box normalisation and rasterisation shared by the built-in recogniser and by
/// every prototype in its template library. Prototypes and user ink go through exactly the same
/// transform, which is what keeps template matching honest.
/// <para>
/// Conventions: the normalised glyph box is the unit square with the origin at the top left and
/// <c>y</c> growing downwards (same handedness as the canvas). Aspect ratio is preserved and the
/// glyph is centred, so a tall thin <c>1</c> stays tall and thin.
/// </para>
/// </summary>
public static class InkGeometry
{
    /// <summary>Number of points each stroke is resampled to before matching.</summary>
    public const int DefaultSamplesPerStroke = 24;

    /// <summary>Design box used by the built-in prototype library (a <c>16 x 16</c> box).</summary>
    public const double DesignBox = 16.0;

    /// <summary>Arc length resampling of a raw ink stroke to exactly <paramref name="count"/> points.</summary>
    public static IReadOnlyList<CPoint> Resample(IReadOnlyList<InkPoint> points, int count)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (count < 1)
        {
            count = 1;
        }

        var source = new List<CPoint>(points.Count);
        foreach (var p in points)
        {
            source.Add(new CPoint(p.X, p.Y));
        }

        return ResamplePoints(source, count);
    }

    /// <summary>Arc length resampling of a polyline to exactly <paramref name="count"/> points.</summary>
    public static IReadOnlyList<CPoint> ResamplePoints(IReadOnlyList<CPoint> points, int count)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (count < 1)
        {
            count = 1;
        }

        if (points.Count == 0)
        {
            return [new CPoint(0, 0)];
        }

        if (points.Count == 1 || count == 1)
        {
            return [points[0]];
        }

        var cumulative = new double[points.Count];
        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            total += points[i - 1].DistanceTo(points[i]);
            cumulative[i] = total;
        }

        if (total <= 1e-12)
        {
            var same = new CPoint[count];
            Array.Fill(same, points[0]);
            return same;
        }

        var result = new CPoint[count];
        var step = total / (count - 1);
        var cursor = 1;
        for (var i = 0; i < count; i++)
        {
            var target = i * step;
            while (cursor < points.Count - 1 && cumulative[cursor] < target)
            {
                cursor++;
            }

            var previousLength = cumulative[cursor - 1];
            var segmentLength = cumulative[cursor] - previousLength;
            var t = segmentLength <= 1e-12 ? 0 : (target - previousLength) / segmentLength;
            t = Math.Clamp(t, 0, 1);
            var a = points[cursor - 1];
            var b = points[cursor];
            result[i] = new CPoint(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
        }

        return result;
    }

    /// <summary>Bounding box of a set of points.</summary>
    public static CRect BoundsOf(IEnumerable<CPoint> points)
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

        return any ? new CRect(minX, minY, maxX - minX, maxY - minY) : CRect.Empty;
    }

    /// <summary>
    /// Resamples every stroke and maps the whole group into the unit glyph box, preserving the aspect
    /// ratio and centring the ink (the "de-centroid" step of the feature pipeline).
    /// </summary>
    public static IReadOnlyList<GlyphStroke> Normalize(
        IReadOnlyList<IReadOnlyList<CPoint>> strokes,
        int samplesPerStroke = DefaultSamplesPerStroke)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var resampled = new List<IReadOnlyList<CPoint>>(strokes.Count);
        var all = new List<CPoint>();
        foreach (var stroke in strokes)
        {
            var r = ResamplePoints(stroke, samplesPerStroke);
            resampled.Add(r);
            all.AddRange(r);
        }

        if (all.Count == 0)
        {
            return [];
        }

        var bounds = BoundsOf(all);
        var maxExtent = Math.Max(bounds.Width, bounds.Height);
        var scale = maxExtent <= 1e-9 ? 1.0 : 1.0 / maxExtent;
        var width = bounds.Width * scale;
        var height = bounds.Height * scale;
        var offsetX = ((1 - width) / 2) - (bounds.X * scale);
        var offsetY = ((1 - height) / 2) - (bounds.Y * scale);

        var normalized = new List<GlyphStroke>(resampled.Count);
        foreach (var stroke in resampled)
        {
            var mapped = new CPoint[stroke.Count];
            for (var i = 0; i < stroke.Count; i++)
            {
                mapped[i] = new CPoint((stroke[i].X * scale) + offsetX, (stroke[i].Y * scale) + offsetY);
            }

            normalized.Add(new GlyphStroke(mapped));
        }

        return normalized;
    }

    /// <summary>Convenience overload: normalises raw ink strokes directly.</summary>
    public static IReadOnlyList<GlyphStroke> NormalizeInk(
        IReadOnlyList<InkStroke> strokes,
        int samplesPerStroke = DefaultSamplesPerStroke)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var polylines = new List<IReadOnlyList<CPoint>>(strokes.Count);
        foreach (var stroke in strokes)
        {
            var pts = new List<CPoint>(stroke.Points.Count);
            foreach (var p in stroke.Points)
            {
                pts.Add(new CPoint(p.X, p.Y));
            }

            polylines.Add(pts);
        }

        return Normalize(polylines, samplesPerStroke);
    }

    /// <summary>Total path length of a normalised stroke group.</summary>
    public static double TotalLength(IReadOnlyList<GlyphStroke> strokes)
    {
        var total = 0.0;
        foreach (var stroke in strokes)
        {
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                total += stroke.Points[i - 1].DistanceTo(stroke.Points[i]);
            }
        }

        return total;
    }
}

/// <summary>
/// Rasterises normalised strokes into a small square grid and offers tolerant (chamfer) comparison.
/// This is the image channel of the built-in recogniser: it survives differences in stroke order and
/// stroke direction that a pure direction sequence cannot, while still being cheap enough for a phone.
/// </summary>
public static class InkRaster
{
    /// <summary>Renders strokes into a <paramref name="size"/> x <paramref name="size"/> coverage grid.</summary>
    public static double[] Render(IReadOnlyList<GlyphStroke> strokes, int size, double thickness = 1.1)
    {
        var grid = new double[size * size];
        if (size <= 0)
        {
            return grid;
        }

        foreach (var stroke in strokes)
        {
            var pts = stroke.Points;
            for (var i = 1; i < pts.Count; i++)
            {
                DrawSegment(grid, size, pts[i - 1], pts[i], thickness);
            }

            if (pts.Count == 1)
            {
                Plot(grid, size, pts[0].X * size, pts[0].Y * size, thickness);
            }
        }

        return grid;
    }

    private static void DrawSegment(double[] grid, int size, CPoint a, CPoint b, double thickness)
    {
        var x0 = a.X * size;
        var y0 = a.Y * size;
        var x1 = b.X * size;
        var y1 = b.Y * size;
        var length = Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
        var steps = Math.Max(1, (int)Math.Ceiling(length * 2));
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            Plot(grid, size, x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t), thickness);
        }
    }

    private static void Plot(double[] grid, int size, double cx, double cy, double thickness)
    {
        var radius = Math.Max(0.5, thickness);
        var minX = Math.Max(0, (int)Math.Floor(cx - radius));
        var maxX = Math.Min(size - 1, (int)Math.Ceiling(cx + radius));
        var minY = Math.Max(0, (int)Math.Floor(cy - radius));
        var maxY = Math.Min(size - 1, (int)Math.Ceiling(cy + radius));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = (x + 0.5) - cx;
                var dy = (y + 0.5) - cy;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var coverage = Math.Clamp(radius + 0.5 - distance, 0, 1);
                var index = (y * size) + x;
                if (coverage > grid[index])
                {
                    grid[index] = coverage;
                }
            }
        }
    }

    /// <summary>Two pass chamfer distance transform: distance in cells from every cell to the nearest ink.</summary>
    public static float[] DistanceTransform(double[] grid, int size, double inkThreshold = 0.35, double cap = 12.0)
    {
        var distance = new float[size * size];
        for (var i = 0; i < distance.Length; i++)
        {
            distance[i] = grid[i] >= inkThreshold ? 0f : (float)cap;
        }

        const float Orthogonal = 1f;
        const float Diagonal = 1.4142f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size) + x;
                var best = distance[i];
                if (x > 0)
                {
                    best = Math.Min(best, distance[i - 1] + Orthogonal);
                }

                if (y > 0)
                {
                    best = Math.Min(best, distance[i - size] + Orthogonal);
                    if (x > 0)
                    {
                        best = Math.Min(best, distance[i - size - 1] + Diagonal);
                    }

                    if (x < size - 1)
                    {
                        best = Math.Min(best, distance[i - size + 1] + Diagonal);
                    }
                }

                distance[i] = best;
            }
        }

        for (var y = size - 1; y >= 0; y--)
        {
            for (var x = size - 1; x >= 0; x--)
            {
                var i = (y * size) + x;
                var best = distance[i];
                if (x < size - 1)
                {
                    best = Math.Min(best, distance[i + 1] + Orthogonal);
                }

                if (y < size - 1)
                {
                    best = Math.Min(best, distance[i + size] + Orthogonal);
                    if (x < size - 1)
                    {
                        best = Math.Min(best, distance[i + size + 1] + Diagonal);
                    }

                    if (x > 0)
                    {
                        best = Math.Min(best, distance[i + size - 1] + Diagonal);
                    }
                }

                distance[i] = best;
            }
        }

        return distance;
    }

    /// <summary>
    /// Row and column ink profiles of a rendered glyph, each normalised to sum to one. They capture
    /// where the ink sits (a <c>0</c> has ink on both sides, a <c>3</c> mostly on the right) and are
    /// far more discriminative between similar digits than the mean chamfer distance alone.
    /// </summary>
    public static (double[] Horizontal, double[] Vertical) Profiles(double[] grid, int size, int bins = 0)
    {
        if (bins <= 0)
        {
            // Coarse bins: a peak that shifts by one cell must not dominate the distance, or a wobbly
            // pen stroke would stop matching its own prototype.
            bins = Math.Max(4, size / 3);
        }

        var rawHorizontal = new double[size];
        var rawVertical = new double[size];
        var total = 0.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var value = grid[(y * size) + x];
                if (value <= 0)
                {
                    continue;
                }

                rawHorizontal[y] += value;
                rawVertical[x] += value;
                total += value;
            }
        }

        var horizontal = new double[bins];
        var vertical = new double[bins];
        if (total <= 1e-9)
        {
            return (horizontal, vertical);
        }

        for (var i = 0; i < size; i++)
        {
            var bin = Math.Min(bins - 1, i * bins / size);
            horizontal[bin] += rawHorizontal[i];
            vertical[bin] += rawVertical[i];
        }

        for (var i = 0; i < bins; i++)
        {
            horizontal[i] /= total;
            vertical[i] /= total;
        }

        return (horizontal, vertical);
    }

    /// <summary>L1 distance between two profile pairs, averaged; <c>0..2</c>.</summary>
    public static double ProfileDistance(
        (double[] Horizontal, double[] Vertical) a,
        (double[] Horizontal, double[] Vertical) b)
    {
        if (a.Horizontal.Length != b.Horizontal.Length || a.Vertical.Length != b.Vertical.Length)
        {
            return 1.0;
        }

        var horizontal = 0.0;
        var vertical = 0.0;
        for (var i = 0; i < a.Horizontal.Length; i++)
        {
            horizontal += Math.Abs(a.Horizontal[i] - b.Horizontal[i]);
            vertical += Math.Abs(a.Vertical[i] - b.Vertical[i]);
        }

        return (horizontal + vertical) / 2;
    }

    /// <summary>
    /// Symmetric chamfer distance between two rendered glyphs, normalised to roughly <c>0..0.5</c>
    /// (cells divided by the grid size). 0 means identical ink.
    /// </summary>
    public static double SymmetricDistance(double[] a, double[] b, int size, double inkThreshold = 0.35)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.5;
        }

        var distanceToB = DistanceTransform(b, size, inkThreshold);
        var distanceToA = DistanceTransform(a, size, inkThreshold);
        double sum = 0;
        var count = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] >= inkThreshold)
            {
                sum += distanceToB[i];
                count++;
            }

            if (b[i] >= inkThreshold)
            {
                sum += distanceToA[i];
                count++;
            }
        }

        if (count == 0)
        {
            return 0.5;
        }

        return sum / count / size;
    }
}
