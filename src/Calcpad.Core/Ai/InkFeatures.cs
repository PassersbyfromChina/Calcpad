using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>
/// One sample of the sequence a prototype and an input glyph are compared on: the position inside the
/// glyph box (so a line at the top never matches a line at the bottom), the tangent direction, the
/// curvature and whether the pen was up.
/// </summary>
public readonly record struct DirectionSample(double X, double Y, double Dx, double Dy, double Curvature, bool PenUp);

/// <summary>
/// Handwriting features extracted from a normalised glyph: direction sequence, curvature, start/end
/// tangents, closure, aspect ratio, free ends and crossing count, plus length weighted direction and
/// curvature histograms used by the feature vector channel.
/// </summary>
public sealed class GlyphFeatures
{
    /// <summary>Normalised strokes the features were computed from.</summary>
    public required IReadOnlyList<GlyphStroke> Strokes { get; init; }

    /// <summary>Concatenated direction sequence, with one pen-up marker between strokes.</summary>
    public required IReadOnlyList<DirectionSample> Directions { get; init; }

    /// <summary>Twelve bin, length weighted histogram of tangent angles (0 = right, growing clockwise).</summary>
    public required double[] DirectionHistogram { get; init; }

    /// <summary>Eight bin histogram of absolute turning angle between consecutive samples.</summary>
    public required double[] CurvatureHistogram { get; init; }

    /// <summary>Width divided by height of the ink bounding box (1 when the ink is degenerate).</summary>
    public double AspectRatio { get; init; } = 1.0;

    /// <summary>Smallest <c>end-to-start distance / path length</c> over the strokes; 0 = perfectly closed.</summary>
    public double Closure { get; init; } = 1.0;

    /// <summary>Tangent angle at the first point of the first stroke, in radians.</summary>
    public double StartAngle { get; init; }

    /// <summary>Tangent angle at the last point of the last stroke, in radians.</summary>
    public double EndAngle { get; init; }

    /// <summary>Number of free stroke ends (two per stroke, minus the ones that land on other ink).</summary>
    public int EndpointCount { get; init; }

    /// <summary>Number of crossings inside the glyph (self intersections and intersections between strokes).</summary>
    public int IntersectionCount { get; init; }

    /// <summary>Number of separate pen strokes.</summary>
    public int StrokeCount { get; init; }

    /// <summary>Ink length divided by the bounding box diagonal: a cheap "how busy is this glyph" measure.</summary>
    public double InkLengthRatio { get; init; }

    /// <summary>Fraction of the <c>6 x 6</c> coarse cells that contain ink.</summary>
    public double FillRatio { get; init; }

    /// <summary>Fixed length feature vector (histograms plus scalar shape descriptors), all values in <c>0..1</c>.</summary>
    public required double[] Vector { get; init; }
}

/// <summary>Feature extraction used by every recognition channel.</summary>
public static class GlyphFeaturesExtractor
{
    private const int DirectionBins = 12;
    private const int CurvatureBins = 8;

    /// <summary>Extracts the feature set of a normalised glyph.</summary>
    public static GlyphFeatures Extract(IReadOnlyList<GlyphStroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var directions = new List<DirectionSample>();
        var directionHistogram = new double[DirectionBins];
        var curvatureHistogram = new double[CurvatureBins];
        var all = new List<CPoint>();

        foreach (var stroke in strokes)
        {
            all.AddRange(stroke.Points);
            var previousAngle = double.NaN;
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                var a = stroke.Points[i - 1];
                var b = stroke.Points[i];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length <= 1e-9)
                {
                    continue;
                }

                dx /= length;
                dy /= length;
                var angle = Math.Atan2(dy, dx);
                var curvature = double.IsNaN(previousAngle) ? 0 : NormalizeAngle(angle - previousAngle) / Math.PI;
                previousAngle = angle;
                directions.Add(new DirectionSample((a.X + b.X) / 2, (a.Y + b.Y) / 2, dx, dy, curvature, false));

                var bin = (int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * DirectionBins) % DirectionBins;
                if (bin < 0)
                {
                    bin += DirectionBins;
                }

                directionHistogram[bin] += length;
                var curvatureBin = Math.Min(CurvatureBins - 1, (int)(Math.Abs(curvature) * CurvatureBins));
                curvatureHistogram[curvatureBin] += length;
            }

            if (stroke.Points.Count > 1)
            {
                directions.Add(new DirectionSample(0.5, 0.5, 0, 0, 0, true));
            }
        }

        Normalize(directionHistogram);
        Normalize(curvatureHistogram);

        var bounds = InkGeometry.BoundsOf(all);
        var diagonal = Math.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height));
        var totalLength = 0.0;
        var closure = 1.0;
        foreach (var stroke in strokes)
        {
            if (stroke.Points.Count < 2)
            {
                closure = Math.Min(closure, 1.0);
                continue;
            }

            var length = 0.0;
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                length += stroke.Points[i - 1].DistanceTo(stroke.Points[i]);
            }

            totalLength += length;
            if (length > 1e-9)
            {
                closure = Math.Min(closure, stroke.Points[0].DistanceTo(stroke.Points[^1]) / length);
            }
        }

        var aspect = bounds.Height <= 1e-9 ? 1.0 : bounds.Width / bounds.Height;
        var startAngle = directions.Count > 0 ? Math.Atan2(directions[0].Dy, directions[0].Dx) : 0;
        var endAngle = 0.0;
        for (var i = directions.Count - 1; i >= 0; i--)
        {
            if (!directions[i].PenUp)
            {
                endAngle = Math.Atan2(directions[i].Dy, directions[i].Dx);
                break;
            }
        }

        var vector = new double[DirectionBins + CurvatureBins + 7];
        Array.Copy(directionHistogram, 0, vector, 0, DirectionBins);
        Array.Copy(curvatureHistogram, 0, vector, DirectionBins, CurvatureBins);
        var offset = DirectionBins + CurvatureBins;
        vector[offset + 0] = Math.Clamp(aspect / 2.0, 0, 1);
        vector[offset + 1] = Math.Clamp(1 - closure, 0, 1);
        vector[offset + 2] = Math.Clamp((Math.Cos(startAngle) + 1) / 2, 0, 1);
        vector[offset + 3] = Math.Clamp((Math.Sin(startAngle) + 1) / 2, 0, 1);
        vector[offset + 4] = Math.Clamp((Math.Cos(endAngle) + 1) / 2, 0, 1);
        vector[offset + 5] = Math.Clamp((Math.Sin(endAngle) + 1) / 2, 0, 1);
        vector[offset + 6] = Math.Clamp(strokes.Count / 8.0, 0, 1);

        return new GlyphFeatures
        {
            Strokes = strokes,
            Directions = directions,
            DirectionHistogram = directionHistogram,
            CurvatureHistogram = curvatureHistogram,
            AspectRatio = aspect,
            Closure = closure,
            StartAngle = startAngle,
            EndAngle = endAngle,
            EndpointCount = strokes.Count * 2,
            IntersectionCount = CountIntersections(strokes),
            StrokeCount = strokes.Count,
            InkLengthRatio = diagonal <= 1e-9 ? 0 : Math.Clamp(totalLength / diagonal / 4.0, 0, 1),
            FillRatio = ComputeFillRatio(strokes),
            Vector = vector
        };
    }

    /// <summary>Euclidean distance between two feature vectors.</summary>
    public static double VectorDistance(double[] a, double[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        var sum = 0.0;
        for (var i = 0; i < length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        return Math.Sqrt(sum);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI)
        {
            angle -= 2 * Math.PI;
        }

        while (angle < -Math.PI)
        {
            angle += 2 * Math.PI;
        }

        return angle;
    }

    private static void Normalize(double[] values)
    {
        var sum = 0.0;
        foreach (var v in values)
        {
            sum += v;
        }

        if (sum <= 1e-12)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= sum;
        }
    }

    private static int CountIntersections(IReadOnlyList<GlyphStroke> strokes)
    {
        var count = 0;
        for (var s = 0; s < strokes.Count; s++)
        {
            var a = strokes[s].Points;
            for (var i = 1; i < a.Count; i++)
            {
                for (var t = s; t < strokes.Count; t++)
                {
                    var b = strokes[t].Points;
                    var start = t == s ? i + 2 : 1;
                    for (var j = start; j < b.Count; j++)
                    {
                        if (SegmentsIntersect(a[i - 1], a[i], b[j - 1], b[j]))
                        {
                            count++;
                        }
                    }
                }
            }
        }

        return count;
    }

    private static bool SegmentsIntersect(CPoint p1, CPoint p2, CPoint p3, CPoint p4)
    {
        var d1 = Cross(p3, p4, p1);
        var d2 = Cross(p3, p4, p2);
        var d3 = Cross(p1, p2, p3);
        var d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(CPoint a, CPoint b, CPoint c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static double ComputeFillRatio(IReadOnlyList<GlyphStroke> strokes)
    {
        const int Cells = 6;
        var occupied = new bool[Cells * Cells];
        var hits = 0;
        foreach (var stroke in strokes)
        {
            foreach (var p in stroke.Points)
            {
                var x = Math.Clamp((int)(p.X * Cells), 0, Cells - 1);
                var y = Math.Clamp((int)(p.Y * Cells), 0, Cells - 1);
                var index = (y * Cells) + x;
                if (!occupied[index])
                {
                    occupied[index] = true;
                    hits++;
                }
            }
        }

        return (double)hits / (Cells * Cells);
    }
}

/// <summary>
/// Dynamic time warping over position + direction sequences. This is the primary channel of the
/// built-in recogniser: it is robust to speed, stroke length and sampling rate, and because both
/// inputs are normalised first it is also robust to scale and translation, while the position term
/// still keeps a top bar from matching a bottom bar.
/// </summary>
public static class DirectionDtw
{
    private const double PenUpMismatchCost = 0.55;
    private const double DirectionWeight = 0.34;
    private const double PositionWeight = 0.51;
    private const double CurvatureWeight = 0.15;

    /// <summary>
    /// Mean per-step cost of the optimal alignment, roughly <c>0..1.1</c>; 0 for identical sequences.
    /// A Sakoe-Chiba band keeps the cost linear for the sequence lengths used here.
    /// </summary>
    public static double MeanCost(IReadOnlyList<DirectionSample> a, IReadOnlyList<DirectionSample> b, double bandFraction = 0.35)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
        {
            return 1.0;
        }

        var n = a.Count;
        var m = b.Count;

        // The band must always be wide enough to reach the far corner: sequences with very different
        // stroke counts (one stroke vs two) would otherwise be unreachable and report the worst
        // possible score, which would break the ranking between a dash and an equals sign.
        var band = Math.Max(
            Math.Abs(n - m) + 2,
            Math.Max(4, (int)(Math.Max(n, m) * bandFraction)));
        var previous = new double[m + 1];
        var current = new double[m + 1];
        Array.Fill(previous, double.PositiveInfinity);
        previous[0] = 0;

        for (var i = 1; i <= n; i++)
        {
            Array.Fill(current, double.PositiveInfinity);
            var jStart = Math.Max(1, i - band);
            var jEnd = Math.Min(m, i + band);
            for (var j = jStart; j <= jEnd; j++)
            {
                var cost = Cost(a[i - 1], b[j - 1]);
                var best = Math.Min(Math.Min(previous[j], current[j - 1]), previous[j - 1]);
                current[j] = best + cost;
            }

            (previous, current) = (current, previous);
        }

        var total = previous[m];
        if (double.IsPositiveInfinity(total))
        {
            return 1.0;
        }

        return total / (n + m);
    }

    private static double Cost(in DirectionSample a, in DirectionSample b)
    {
        if (a.PenUp || b.PenUp)
        {
            return a.PenUp == b.PenUp ? 0 : PenUpMismatchCost;
        }

        var dot = (a.Dx * b.Dx) + (a.Dy * b.Dy);
        var direction = 1 - Math.Clamp(dot, -1, 1);
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var position = Math.Sqrt((dx * dx) + (dy * dy));
        var curvature = Math.Min(1.0, Math.Abs(a.Curvature - b.Curvature));
        return (DirectionWeight * direction) + (PositionWeight * position) + (CurvatureWeight * curvature);
    }
}
