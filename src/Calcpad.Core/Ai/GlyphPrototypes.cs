using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>Which recognition channel a prototype belongs to. Channels are ranked separately so a
/// digit is never silently turned into a letter with the same picture.</summary>
public enum GlyphChannel
{
    /// <summary>Digits 0-9.</summary>
    Digit,

    /// <summary>Operators and punctuation of a math expression.</summary>
    Operator,

    /// <summary>Latin capitals.</summary>
    LatinUpper,

    /// <summary>Latin small letters.</summary>
    LatinLower,

    /// <summary>CJK ideographs, radicals and CJK punctuation.</summary>
    Cjk
}

/// <summary>
/// One hand built prototype: a small set of strokes authored in a <c>16 x 16</c> design box with
/// <c>y</c> pointing down. Prototypes are normalised with exactly the same code path as user ink.
/// </summary>
public sealed record GlyphPrototype(
    string Label,
    GlyphChannel Channel,
    IReadOnlyList<IReadOnlyList<CPoint>> Strokes,
    string Variant = "default");

/// <summary>Fluent authoring helper for prototype strokes in the <c>16 x 16</c> design box.</summary>
public sealed class GlyphBuilder
{
    private readonly List<List<CPoint>> _strokes = [];
    private List<CPoint>? _current;

    /// <summary>Starts a new stroke at the given design space point.</summary>
    public GlyphBuilder Move(double x, double y)
    {
        _current = [new CPoint(x, y)];
        _strokes.Add(_current);
        return this;
    }

    /// <summary>Appends a point to the current stroke (starting one when needed).</summary>
    public GlyphBuilder To(double x, double y)
    {
        if (_current is null)
        {
            return Move(x, y);
        }

        _current.Add(new CPoint(x, y));
        return this;
    }

    /// <summary>Adds a straight stroke.</summary>
    public GlyphBuilder Line(double x1, double y1, double x2, double y2) => Move(x1, y1).To(x2, y2);

    /// <summary>Adds a polyline stroke from flat <c>x, y, x, y, ...</c> coordinates.</summary>
    public GlyphBuilder Poly(params double[] xy)
    {
        if (xy.Length < 2)
        {
            return this;
        }

        Move(xy[0], xy[1]);
        for (var i = 2; i + 1 < xy.Length; i += 2)
        {
            To(xy[i], xy[i + 1]);
        }

        return this;
    }

    /// <summary>Adds an elliptical arc as a new stroke. Angles are in degrees, <c>90</c> points down.</summary>
    public GlyphBuilder Arc(double cx, double cy, double rx, double ry, double startDeg, double sweepDeg, int steps = 18)
    {
        var points = SampleArc(cx, cy, rx, ry, startDeg, sweepDeg, steps);
        _current = [.. points];
        _strokes.Add(_current);
        return this;
    }

    /// <summary>Continues the current stroke with an elliptical arc.</summary>
    public GlyphBuilder ExtendArc(double cx, double cy, double rx, double ry, double startDeg, double sweepDeg, int steps = 18)
    {
        var points = SampleArc(cx, cy, rx, ry, startDeg, sweepDeg, steps);
        if (_current is null)
        {
            _current = [];
            _strokes.Add(_current);
        }

        foreach (var p in points)
        {
            _current.Add(p);
        }

        return this;
    }

    /// <summary>Adds a small dot as a very short stroke (used by <c>i</c>, <c>:</c>, <c>!</c>, ...).</summary>
    public GlyphBuilder Dot(double x, double y, double radius = 0.9)
    {
        var half = radius / 2;
        return Move(x - half, y - half).To(x + half, y + half);
    }

    /// <summary>The authored strokes.</summary>
    public IReadOnlyList<IReadOnlyList<CPoint>> Strokes => _strokes;

    private static List<CPoint> SampleArc(double cx, double cy, double rx, double ry, double startDeg, double sweepDeg, int steps)
    {
        steps = Math.Max(2, steps);
        var points = new List<CPoint>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var angle = (startDeg + (sweepDeg * i / steps)) * Math.PI / 180.0;
            points.Add(new CPoint(cx + (rx * Math.Cos(angle)), cy + (ry * Math.Sin(angle))));
        }

        return points;
    }
}

/// <summary>A prototype with its normalised features, direction sequence and raster precomputed.</summary>
public sealed class CompiledGlyphPrototype
{
    /// <summary>Label the recogniser reports, e.g. <c>7</c> or <c>+</c>.</summary>
    public required string Label { get; init; }

    /// <summary>Channel this prototype competes in.</summary>
    public required GlyphChannel Channel { get; init; }

    /// <summary>Variant name, e.g. <c>reversed</c>.</summary>
    public required string Variant { get; init; }

    /// <summary>Handwriting features of the normalised prototype.</summary>
    public required GlyphFeatures Features { get; init; }

    /// <summary>Rendered prototype used by the image channel.</summary>
    public required double[] Raster { get; init; }

    /// <summary>Chamfer distance transform of <see cref="Raster"/>, cached for matching.</summary>
    public required float[] DistanceTransform { get; init; }

    /// <summary>Row and column ink profiles of <see cref="Raster"/>.</summary>
    public required (double[] Horizontal, double[] Vertical) Profiles { get; init; }

    /// <summary>Unique key, e.g. <c>Digit:7:reversed</c>.</summary>
    public string Key => $"{Channel}:{Label}:{Variant}";

    /// <summary>Ranking label that also carries the channel, so <c>o</c> and <c>0</c> stay distinct.</summary>
    public string ChannelLabel => $"{Channel}|{Label}";
}

/// <summary>
/// Compiles the hand built prototype library into matchable form. Every prototype is expanded into
/// direction variants (strokes reversed, stroke order reversed, both) because people draw the same
/// character in different stroke orders and directions; without this the DTW channel would be
/// needlessly brittle.
/// </summary>
public static class GlyphPrototypeCompiler
{
    /// <summary>Expands and compiles prototypes.</summary>
    public static IReadOnlyList<CompiledGlyphPrototype> Compile(
        IEnumerable<GlyphPrototype> prototypes,
        int samplesPerStroke = InkGeometry.DefaultSamplesPerStroke,
        int rasterSize = 24)
    {
        ArgumentNullException.ThrowIfNull(prototypes);
        var compiled = new List<CompiledGlyphPrototype>();
        foreach (var prototype in prototypes)
        {
            foreach (var variant in Expand(prototype))
            {
                var normalized = InkGeometry.Normalize(variant.Strokes, samplesPerStroke);
                if (normalized.Count == 0)
                {
                    continue;
                }

                var features = GlyphFeaturesExtractor.Extract(normalized);
                var raster = InkRaster.Render(normalized, rasterSize);
                compiled.Add(new CompiledGlyphPrototype
                {
                    Label = prototype.Label,
                    Channel = prototype.Channel,
                    Variant = variant.Variant,
                    Features = features,
                    Raster = raster,
                    DistanceTransform = InkRaster.DistanceTransform(raster, rasterSize),
                    Profiles = InkRaster.Profiles(raster, rasterSize)
                });
            }
        }

        return compiled;
    }

    private static IEnumerable<GlyphPrototype> Expand(GlyphPrototype prototype)
    {
        yield return prototype;

        var reversed = prototype.Strokes.Select(s => (IReadOnlyList<CPoint>)[.. s.Reverse()]).ToList();
        if (!SameShape(prototype.Strokes, reversed))
        {
            yield return prototype with { Strokes = reversed, Variant = "reversed" };
        }

        var orderReversed = prototype.Strokes.Reverse().ToList();
        if (orderReversed.Count > 1)
        {
            yield return prototype with { Strokes = orderReversed, Variant = "order-reversed" };
            yield return prototype with { Strokes = [.. orderReversed.Select(s => (IReadOnlyList<CPoint>)[.. s.Reverse()])], Variant = "order-and-direction-reversed" };
        }
    }

    private static bool SameShape(IReadOnlyList<IReadOnlyList<CPoint>> a, IReadOnlyList<IReadOnlyList<CPoint>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Count != b[i].Count)
            {
                return false;
            }

            for (var j = 0; j < a[i].Count; j++)
            {
                if (a[i][j] != b[i][j])
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>
/// The built-in prototype library. It is deliberately small, hand authored and versioned with the
/// source: digits, the operators a calculator needs, Latin upper and lower case, common CJK radicals
/// and a set of high frequency Chinese characters used with units and arithmetic.
/// </summary>
public static partial class GlyphCatalog
{
    private static readonly Lazy<IReadOnlyList<CompiledGlyphPrototype>> Compiled = new(() => GlyphPrototypeCompiler.Compile(BuildAll()));

    /// <summary>All compiled prototypes, built once on first use.</summary>
    public static IReadOnlyList<CompiledGlyphPrototype> Prototypes => Compiled.Value;

    /// <summary>Raw (uncompiled) prototypes; exposed for diagnostics and for the verification harness.</summary>
    public static IReadOnlyList<GlyphPrototype> Raw => BuildAll();

    /// <summary>CJK prototypes: radicals, CJK numerals, unit and arithmetic characters. Implemented in
    /// <c>GlyphCatalog.Cjk.cs</c>.</summary>
    private static partial IReadOnlyList<GlyphPrototype> BuildCjk();

    private static IReadOnlyList<GlyphPrototype> BuildAll()
    {
        var all = new List<GlyphPrototype>();
        all.AddRange(BuildDigits());
        all.AddRange(BuildOperators());
        all.AddRange(BuildLatinUpper());
        all.AddRange(BuildLatinLower());
        all.AddRange(BuildCjk());
        return all;
    }

    private static IEnumerable<GlyphPrototype> BuildDigits()
    {
        const GlyphChannel C = GlyphChannel.Digit;
        yield return new GlyphPrototype("0", C, new GlyphBuilder().Arc(8, 8.5, 4.6, 6.4, -90, 360).Strokes);
        yield return new GlyphPrototype("0", C, new GlyphBuilder().Arc(8, 8.5, 3.4, 6.0, -90, 360).Strokes, "narrow");
        yield return new GlyphPrototype("1", C, new GlyphBuilder().Poly(4.6, 4.8, 7.6, 2.6, 7.6, 14.8).Strokes);
        yield return new GlyphPrototype("1", C, new GlyphBuilder().Line(8, 2.6, 8, 14.8).Strokes, "plain");
        yield return new GlyphPrototype("1", C, new GlyphBuilder().Line(8, 2.6, 8, 14.8).Line(4.2, 14.8, 11.8, 14.8).Strokes, "based");
        yield return new GlyphPrototype("2", C, new GlyphBuilder().Poly(2.8, 4.6, 4.4, 2.6, 9.4, 2.2, 11.8, 3.6, 12.2, 5.6, 10.6, 8.2, 4.2, 13, 3.0, 14.6, 13.0, 14.6).Strokes);
        yield return new GlyphPrototype("2", C, new GlyphBuilder().Poly(3.2, 5.0, 6.0, 2.4, 10.0, 2.4, 12.0, 4.4, 12.0, 6.0, 4.0, 13.6, 3.4, 14.4, 12.8, 14.4).Strokes, "angular");
        yield return new GlyphPrototype("3", C, new GlyphBuilder().Poly(3.0, 3.2, 5.4, 2.2, 9.8, 2.2, 11.8, 3.8, 11.6, 5.6, 9.6, 7.4, 6.6, 7.8, 9.8, 7.6, 12.0, 9.4, 12.2, 11.6, 10.4, 13.8, 6.2, 14.6, 3.2, 13.4).Strokes);
        yield return new GlyphPrototype("3", C, new GlyphBuilder().Arc(8, 5.0, 4.4, 3.2, 180, 180).ExtendArc(8, 10.5, 4.6, 4.0, 0, 180).Strokes, "round");
        yield return new GlyphPrototype("3", C, new GlyphBuilder().Poly(2.8, 2.8, 12.6, 2.8, 12.6, 7.8, 5.6, 7.8).Poly(12.6, 6.8, 12.6, 13.4, 2.8, 13.4).Strokes, "flat-top");
        yield return new GlyphPrototype("4", C, new GlyphBuilder().Poly(10.4, 2.4, 3.0, 10.6, 13.0, 10.6).Line(10.4, 2.4, 10.4, 14.8).Strokes);
        yield return new GlyphPrototype("4", C, new GlyphBuilder().Poly(10.4, 2.4, 3.0, 10.6).Line(3.0, 10.6, 13.0, 10.6).Line(10.4, 2.4, 10.4, 14.8).Strokes, "open");
        yield return new GlyphPrototype("5", C, new GlyphBuilder().Poly(12.2, 2.4, 4.0, 2.4, 3.4, 7.2, 8.0, 6.6, 11.4, 7.8, 12.6, 10.4, 11.6, 13.0, 8.0, 14.6, 3.6, 14.0).Strokes);
        yield return new GlyphPrototype("5", C, new GlyphBuilder().Line(12.0, 2.6, 4.2, 2.6).Poly(4.2, 2.6, 3.6, 7.4, 8.2, 6.8, 11.6, 8.0, 12.6, 10.6, 11.4, 13.2, 7.8, 14.6, 3.8, 14.0).Strokes, "two-stroke");
        yield return new GlyphPrototype("6", C, new GlyphBuilder().Poly(11.6, 3.0, 8.8, 2.2, 6.4, 3.2, 4.8, 5.6, 4.2, 8.6, 4.4, 10.6, 5.6, 13.0, 8.4, 14.4, 10.8, 13.4, 12.2, 11.2, 12.0, 9.0, 10.4, 7.6, 8.0, 7.4, 6.0, 8.2, 4.8, 9.6).Strokes);
        yield return new GlyphPrototype("6", C, new GlyphBuilder().Poly(11.6, 3.0, 8.8, 2.2, 6.4, 3.2, 4.8, 5.6, 4.2, 8.6).Arc(8.2, 11.0, 4.0, 3.4, 180, 360).Strokes, "two-stroke");
        yield return new GlyphPrototype("7", C, new GlyphBuilder().Poly(3.0, 2.6, 13.0, 2.6, 6.6, 14.8).Strokes);
        yield return new GlyphPrototype("7", C, new GlyphBuilder().Poly(3.0, 2.6, 13.0, 2.6, 6.6, 14.8).Line(5.6, 9.0, 10.6, 9.0).Strokes, "crossed");
        yield return new GlyphPrototype("7", C, new GlyphBuilder().Line(3.2, 2.8, 12.8, 2.8).Line(12.8, 2.8, 7.0, 14.6).Strokes, "two-stroke");
        yield return new GlyphPrototype("8", C, new GlyphBuilder().Arc(8, 5.0, 3.6, 2.9, 180, 360).ExtendArc(8, 11.2, 4.3, 3.6, 180, 360).Strokes);
        yield return new GlyphPrototype("8", C, new GlyphBuilder().Arc(8, 5.0, 3.6, 2.9, -90, 360).Arc(8, 11.2, 4.3, 3.6, -90, 360).Strokes, "two-loops");
        yield return new GlyphPrototype("9", C, new GlyphBuilder().Arc(8, 5.6, 3.8, 3.4, -90, 360).Poly(11.8, 3.0, 11.8, 11.0, 10.4, 13.6, 7.6, 14.8, 5.2, 14.2).Strokes);
        yield return new GlyphPrototype("9", C, new GlyphBuilder().Arc(8, 5.6, 3.8, 3.4, -90, 360).Line(11.8, 3.2, 11.8, 14.6).Strokes, "straight-tail");
    }

    private static IEnumerable<GlyphPrototype> BuildOperators()
    {
        const GlyphChannel C = GlyphChannel.Operator;
        yield return new GlyphPrototype("+", C, new GlyphBuilder().Line(8, 3.4, 8, 12.6).Line(3.4, 8, 12.6, 8).Strokes);
        yield return new GlyphPrototype("+", C, new GlyphBuilder().Line(8, 2.6, 8, 13.4).Line(2.6, 8, 13.4, 8).Strokes, "large");
        yield return new GlyphPrototype("-", C, new GlyphBuilder().Line(2.5, 8.0, 13.5, 8.0).Strokes);
        yield return new GlyphPrototype("-", C, new GlyphBuilder().Line(3.6, 8.0, 12.4, 8.0).Strokes, "short");
        yield return new GlyphPrototype("×", C, new GlyphBuilder().Line(3.6, 3.6, 12.4, 12.4).Line(12.4, 3.6, 3.6, 12.4).Strokes);
        yield return new GlyphPrototype("÷", C, new GlyphBuilder().Line(3.0, 8.0, 13.0, 8.0).Dot(8, 4.4).Dot(8, 11.6).Strokes);
        yield return new GlyphPrototype("=", C, new GlyphBuilder().Line(2.8, 6.2, 13.2, 6.2).Line(2.8, 9.8, 13.2, 9.8).Strokes);
        yield return new GlyphPrototype("=", C, new GlyphBuilder().Line(3.6, 6.0, 12.4, 6.0).Line(3.6, 10.0, 12.4, 10.0).Strokes, "short");
        yield return new GlyphPrototype(".", C, new GlyphBuilder().Dot(8, 13.2, 1.1).Strokes);
        yield return new GlyphPrototype(",", C, new GlyphBuilder().Poly(8.6, 11.6, 8.6, 13.8, 6.8, 15.8).Strokes);
        yield return new GlyphPrototype("(", C, new GlyphBuilder().Arc(14, 8, 10, 8, 210, -60).Strokes);
        yield return new GlyphPrototype(")", C, new GlyphBuilder().Arc(-6, 8, 10, 8, -30, 60).Strokes);
        yield return new GlyphPrototype("^", C, new GlyphBuilder().Poly(3.0, 13.0, 8.0, 3.6, 13.0, 13.0).Strokes);
        yield return new GlyphPrototype("√", C, new GlyphBuilder().Poly(1.6, 9.4, 4.0, 8.4, 6.6, 15.0, 13.0, 2.2).Line(9.6, 2.2, 14.6, 2.2).Strokes);
        yield return new GlyphPrototype("%", C, new GlyphBuilder().Arc(5.2, 5.0, 2.6, 2.6, -90, 360).Line(11.6, 3.0, 4.4, 13.0).Arc(11.0, 11.4, 2.6, 2.6, -90, 360).Strokes);
        yield return new GlyphPrototype("!", C, new GlyphBuilder().Line(8, 2.4, 8, 10.6).Dot(8, 13.6, 1.1).Strokes);
        yield return new GlyphPrototype("/", C, new GlyphBuilder().Line(3.6, 14.0, 12.4, 2.0).Strokes);
        yield return new GlyphPrototype("*", C, new GlyphBuilder().Line(8, 2.6, 8, 13.4).Line(3.6, 5.2, 12.4, 10.8).Line(12.4, 5.2, 3.6, 10.8).Strokes);
        yield return new GlyphPrototype(":", C, new GlyphBuilder().Dot(8, 5.2).Dot(8, 11.2).Strokes);
        yield return new GlyphPrototype("[", C, new GlyphBuilder().Poly(11.4, 2.0, 5.0, 2.0, 5.0, 14.0, 11.4, 14.0).Strokes);
        yield return new GlyphPrototype("]", C, new GlyphBuilder().Poly(4.6, 2.0, 11.0, 2.0, 11.0, 14.0, 4.6, 14.0).Strokes);
        yield return new GlyphPrototype("|", C, new GlyphBuilder().Line(8, 1.8, 8, 14.2).Strokes);
        yield return new GlyphPrototype("<", C, new GlyphBuilder().Poly(11.5, 3.0, 4.5, 8.0, 11.5, 13.0).Strokes);
        yield return new GlyphPrototype(">", C, new GlyphBuilder().Poly(4.5, 3.0, 11.5, 8.0, 4.5, 13.0).Strokes);
    }

    private static IEnumerable<GlyphPrototype> BuildLatinUpper()
    {
        const GlyphChannel C = GlyphChannel.LatinUpper;
        yield return new GlyphPrototype("A", C, new GlyphBuilder().Poly(2.2, 14.4, 8.0, 2.2, 13.8, 14.4).Line(4.8, 9.2, 11.2, 9.2).Strokes);
        yield return new GlyphPrototype("B", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Poly(3.2, 2.2, 9.6, 2.2, 11.8, 4.0, 11.8, 6.2, 9.6, 8.2, 3.2, 8.2).Poly(3.2, 8.2, 10.6, 8.2, 13.0, 10.2, 13.0, 12.4, 10.6, 14.4, 3.2, 14.4).Strokes);
        yield return new GlyphPrototype("C", C, new GlyphBuilder().Arc(8, 8.2, 5.6, 6.2, -50, -260).Strokes);
        yield return new GlyphPrototype("D", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Poly(3.2, 2.2, 8.8, 2.2, 12.8, 6.0, 12.8, 10.6, 8.8, 14.4, 3.2, 14.4).Strokes);
        yield return new GlyphPrototype("E", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Line(3.2, 2.2, 13.0, 2.2).Line(3.2, 8.2, 11.0, 8.2).Line(3.2, 14.4, 13.0, 14.4).Strokes);
        yield return new GlyphPrototype("F", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Line(3.2, 2.2, 13.0, 2.2).Line(3.2, 8.2, 11.0, 8.2).Strokes);
        yield return new GlyphPrototype("G", C, new GlyphBuilder().Arc(8, 8.2, 5.6, 6.2, -50, -250).Poly(13.6, 8.6, 13.6, 13.2, 9.0, 13.2).Strokes);
        yield return new GlyphPrototype("H", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Line(12.8, 2.2, 12.8, 14.4).Line(3.2, 8.2, 12.8, 8.2).Strokes);
        yield return new GlyphPrototype("I", C, new GlyphBuilder().Line(8, 2.2, 8, 14.4).Strokes);
        yield return new GlyphPrototype("I", C, new GlyphBuilder().Line(4.6, 2.2, 11.4, 2.2).Line(8, 2.2, 8, 14.4).Line(4.6, 14.4, 11.4, 14.4).Strokes, "serif");
        yield return new GlyphPrototype("J", C, new GlyphBuilder().Poly(10.2, 2.2, 10.2, 11.0, 9.0, 13.2, 6.2, 13.6, 4.2, 11.6, 4.2, 10.0).Strokes);
        yield return new GlyphPrototype("K", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Poly(13.0, 2.2, 3.6, 8.6).Poly(6.4, 6.4, 13.0, 14.4).Strokes);
        yield return new GlyphPrototype("L", C, new GlyphBuilder().Line(3.4, 2.2, 3.4, 14.4).Line(3.4, 14.4, 12.8, 14.4).Strokes);
        yield return new GlyphPrototype("M", C, new GlyphBuilder().Poly(2.2, 14.4, 2.2, 2.2, 8.0, 9.2, 13.8, 2.2, 13.8, 14.4).Strokes);
        yield return new GlyphPrototype("N", C, new GlyphBuilder().Poly(3.0, 14.4, 3.0, 2.2, 13.0, 14.4, 13.0, 2.2).Strokes);
        yield return new GlyphPrototype("O", C, new GlyphBuilder().Arc(8, 8.2, 5.6, 6.2, -90, 360).Strokes);
        yield return new GlyphPrototype("O", C, new GlyphBuilder().Arc(8, 8.2, 4.4, 6.0, -90, 360).Strokes, "narrow");
        yield return new GlyphPrototype("P", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Poly(3.2, 2.2, 9.8, 2.2, 12.6, 4.6, 12.6, 6.6, 9.8, 9.0, 3.2, 9.0).Strokes);
        yield return new GlyphPrototype("Q", C, new GlyphBuilder().Arc(8, 8.2, 5.6, 6.2, -90, 360).Line(9.8, 10.8, 13.8, 15.2).Strokes);
        yield return new GlyphPrototype("R", C, new GlyphBuilder().Line(3.2, 2.2, 3.2, 14.4).Poly(3.2, 2.2, 9.8, 2.2, 12.6, 4.6, 12.6, 6.6, 9.8, 9.0, 3.2, 9.0).Poly(8.0, 9.0, 13.0, 14.4).Strokes);
        yield return new GlyphPrototype("S", C, new GlyphBuilder().Poly(13.0, 4.0, 11.0, 2.2, 5.0, 2.2, 3.2, 4.0, 3.2, 6.0, 5.0, 8.0, 11.0, 8.2, 13.0, 10.2, 13.0, 12.4, 11.0, 14.4, 5.0, 14.4, 3.2, 12.4).Strokes);
        yield return new GlyphPrototype("T", C, new GlyphBuilder().Line(2.2, 2.2, 13.8, 2.2).Line(8.0, 2.2, 8.0, 14.4).Strokes);
        yield return new GlyphPrototype("U", C, new GlyphBuilder().Poly(3.0, 2.2, 3.0, 11.0, 5.0, 13.6, 11.0, 13.6, 13.0, 11.0, 13.0, 2.2).Strokes);
        yield return new GlyphPrototype("V", C, new GlyphBuilder().Poly(2.4, 2.2, 8.0, 14.4, 13.6, 2.2).Strokes);
        yield return new GlyphPrototype("W", C, new GlyphBuilder().Poly(1.8, 2.2, 4.4, 14.4, 8.0, 6.0, 11.6, 14.4, 14.2, 2.2).Strokes);
        yield return new GlyphPrototype("X", C, new GlyphBuilder().Line(3.0, 2.2, 13.0, 14.4).Line(13.0, 2.2, 3.0, 14.4).Strokes);
        yield return new GlyphPrototype("Y", C, new GlyphBuilder().Poly(3.0, 2.2, 8.0, 8.4).Poly(13.0, 2.2, 8.0, 8.4).Line(8.0, 8.4, 8.0, 14.4).Strokes);
        yield return new GlyphPrototype("Z", C, new GlyphBuilder().Line(3.0, 2.2, 13.0, 2.2).Line(13.0, 2.2, 3.0, 14.4).Line(3.0, 14.4, 13.0, 14.4).Strokes);
    }

    private static IEnumerable<GlyphPrototype> BuildLatinLower()
    {
        const GlyphChannel C = GlyphChannel.LatinLower;
        yield return new GlyphPrototype("a", C, new GlyphBuilder().Arc(8, 11.0, 4.4, 3.4, -90, 360).Line(12.4, 7.8, 12.4, 14.4).Strokes);
        yield return new GlyphPrototype("a", C, new GlyphBuilder().Poly(12.4, 7.8, 12.4, 14.4, 12.4, 7.8).Arc(8, 11.0, 4.4, 3.4, -90, 360).Strokes, "two-stroke");
        yield return new GlyphPrototype("b", C, new GlyphBuilder().Line(3.6, 2.2, 3.6, 14.4).Arc(8, 11.0, 4.4, 3.4, -90, 360).Strokes);
        yield return new GlyphPrototype("c", C, new GlyphBuilder().Arc(8, 11.0, 4.4, 3.4, -40, -280).Strokes);
        yield return new GlyphPrototype("d", C, new GlyphBuilder().Line(12.4, 2.2, 12.4, 14.4).Arc(8, 11.0, 4.4, 3.4, -90, 360).Strokes);
        yield return new GlyphPrototype("e", C, new GlyphBuilder().Arc(8, 11.0, 4.4, 3.4, 0, -320).Line(3.6, 11.0, 12.4, 11.0).Strokes);
        yield return new GlyphPrototype("f", C, new GlyphBuilder().Poly(11.0, 3.2, 9.0, 2.2, 7.4, 3.2, 7.4, 14.4).Line(4.2, 8.0, 11.0, 8.0).Strokes);
        yield return new GlyphPrototype("g", C, new GlyphBuilder().Arc(8, 11.0, 4.4, 3.4, -90, 360).Poly(12.4, 7.8, 12.4, 14.6, 11.0, 16.2, 6.4, 16.2, 4.6, 15.0).Strokes);
        yield return new GlyphPrototype("h", C, new GlyphBuilder().Line(3.6, 2.2, 3.6, 14.4).Poly(3.6, 9.4, 6.0, 7.8, 10.0, 7.8, 12.4, 10.0, 12.4, 14.4).Strokes);
        yield return new GlyphPrototype("i", C, new GlyphBuilder().Line(8, 7.8, 8, 14.4).Dot(8, 4.4).Strokes);
        yield return new GlyphPrototype("j", C, new GlyphBuilder().Poly(9.0, 7.8, 9.0, 15.0, 8.0, 16.6, 5.0, 16.6, 4.0, 15.0).Dot(9, 4.4).Strokes);
        yield return new GlyphPrototype("k", C, new GlyphBuilder().Line(3.6, 2.2, 3.6, 14.4).Poly(11.0, 7.8, 4.0, 12.0).Poly(6.0, 10.6, 11.6, 14.4).Strokes);
        yield return new GlyphPrototype("l", C, new GlyphBuilder().Line(8, 2.2, 8, 14.4).Strokes);
        yield return new GlyphPrototype("m", C, new GlyphBuilder().Line(2.4, 7.8, 2.4, 14.4).Poly(2.4, 10.0, 4.0, 8.4, 6.0, 8.4, 7.4, 10.0, 7.4, 14.4).Poly(7.4, 10.0, 9.0, 8.4, 11.0, 8.4, 12.6, 10.0, 12.6, 14.4).Strokes);
        yield return new GlyphPrototype("n", C, new GlyphBuilder().Line(3.6, 7.8, 3.6, 14.4).Poly(3.6, 10.0, 5.6, 8.4, 9.6, 8.4, 12.4, 10.4, 12.4, 14.4).Strokes);
        yield return new GlyphPrototype("o", C, new GlyphBuilder().Arc(8, 11.0, 4.4, 3.4, -90, 360).Strokes);
        yield return new GlyphPrototype("p", C, new GlyphBuilder().Line(3.6, 7.8, 3.6, 16.4).Arc(8, 11.0, 4.4, 3.4, -90, 360).Strokes);
        yield return new GlyphPrototype("q", C, new GlyphBuilder().Line(12.4, 7.8, 12.4, 16.4).Arc(8, 11.0, 4.4, 3.4, -90, 360).Strokes);
        yield return new GlyphPrototype("r", C, new GlyphBuilder().Line(4.0, 7.8, 4.0, 14.4).Poly(4.0, 10.6, 6.0, 8.6, 9.6, 8.6, 12.0, 10.0).Strokes);
        yield return new GlyphPrototype("s", C, new GlyphBuilder().Poly(12.0, 9.4, 10.2, 8.4, 6.0, 8.4, 4.6, 9.4, 4.6, 10.6, 6.0, 11.4, 10.4, 11.6, 12.4, 12.4, 12.4, 13.4, 10.6, 14.4, 6.0, 14.4, 4.2, 13.2).Strokes);
        yield return new GlyphPrototype("t", C, new GlyphBuilder().Poly(7.0, 3.6, 7.0, 12.4, 8.2, 14.2, 10.4, 14.4).Line(4.0, 7.8, 10.6, 7.8).Strokes);
        yield return new GlyphPrototype("u", C, new GlyphBuilder().Poly(3.6, 7.8, 3.6, 12.4, 5.2, 14.4, 9.6, 14.4, 12.4, 12.0).Line(12.4, 7.8, 12.4, 14.4).Strokes);
        yield return new GlyphPrototype("v", C, new GlyphBuilder().Poly(3.4, 7.8, 8.0, 14.4, 12.6, 7.8).Strokes);
        yield return new GlyphPrototype("w", C, new GlyphBuilder().Poly(2.4, 7.8, 5.0, 14.4, 8.0, 10.0, 11.0, 14.4, 13.6, 7.8).Strokes);
        yield return new GlyphPrototype("x", C, new GlyphBuilder().Line(3.4, 7.8, 12.6, 14.4).Line(12.6, 7.8, 3.4, 14.4).Strokes);
        yield return new GlyphPrototype("y", C, new GlyphBuilder().Poly(3.4, 7.8, 8.0, 14.4).Poly(12.6, 7.8, 8.0, 14.4, 6.0, 17.4, 3.0, 17.4).Strokes);
        yield return new GlyphPrototype("z", C, new GlyphBuilder().Line(3.4, 7.8, 12.6, 7.8).Line(12.6, 7.8, 3.4, 14.4).Line(3.4, 14.4, 12.6, 14.4).Strokes);
    }
}
