using Calcpad.Core.Canvas;

namespace Calcpad.Core.Ai;

/// <summary>A group of strokes the recogniser treats as one character.</summary>
public sealed class CharCluster
{
    /// <summary>Strokes that belong to this character.</summary>
    public required IReadOnlyList<InkStroke> Strokes { get; init; }

    /// <summary>Bounding box of the cluster in world coordinates.</summary>
    public required CRect Bounds { get; init; }
}

/// <summary>Tuning knobs for stroke grouping.</summary>
public sealed record GroupingOptions
{
    /// <summary>Two stroke groups merge when the horizontal gap is at most this fraction of the
    /// smaller group's size. Small glyphs such as <c>+</c> therefore keep their distance from the
    /// digits around them, while the arm of a <c>+</c> still joins its own stem (they touch).</summary>
    public double CharGapFactor { get; init; } = 0.18;

    /// <summary>Two stroke groups merge when their horizontal ranges overlap by more than this
    /// fraction of the narrower group (two strokes of the same character overlap almost always).</summary>
    public double OverlapMergeRatio { get; init; } = 0.22;

    /// <summary>Vertical overlap needed for two clusters to belong to the same text line.</summary>
    public double LineOverlapRatio { get; init; } = 0.4;

    /// <summary>Gap (as a fraction of the median character width) above which a space is inserted.</summary>
    public double WordGapFactor { get; init; } = 0.85;
}

/// <summary>
/// Turns a flat list of ink strokes into characters, words and lines. Recognition quality depends on
/// this step more than on the classifier: a character split in half can never be recognised.
/// </summary>
public static class InkGrouping
{
    /// <summary>Groups strokes into character clusters, left to right.</summary>
    public static IReadOnlyList<CharCluster> ClusterCharacters(IReadOnlyList<InkStroke> strokes, GroupingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        options ??= new GroupingOptions();
        var items = new List<(InkStroke Stroke, CRect Bounds)>();
        foreach (var stroke in strokes)
        {
            if (stroke.Points.Count == 0)
            {
                continue;
            }

            items.Add((stroke, stroke.Bounds));
        }

        if (items.Count == 0)
        {
            return [];
        }

        items.Sort((a, b) => a.Bounds.X.CompareTo(b.Bounds.X));
        var groups = new List<List<(InkStroke Stroke, CRect Bounds)>> { new() { items[0] } };

        for (var i = 1; i < items.Count; i++)
        {
            var current = items[i];
            var last = groups[^1];
            var lastBounds = CRect.Bounding(last.Select(x => x.Bounds));
            if (ShouldMerge(lastBounds, current.Bounds, options))
            {
                last.Add(current);
            }
            else
            {
                groups.Add([current]);
            }
        }

        var result = new List<CharCluster>(groups.Count);
        foreach (var group in groups)
        {
            result.Add(new CharCluster
            {
                Strokes = [.. group.Select(g => g.Stroke)],
                Bounds = CRect.Bounding(group.Select(g => g.Bounds))
            });
        }

        return result;
    }

    /// <summary>Splits character clusters into lines (top to bottom); each line keeps left to right order.</summary>
    public static IReadOnlyList<IReadOnlyList<CharCluster>> SplitLines(
        IReadOnlyList<CharCluster> clusters,
        GroupingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        options ??= new GroupingOptions();
        if (clusters.Count == 0)
        {
            return [];
        }

        var lines = new List<List<CharCluster>>();
        foreach (var cluster in clusters.OrderBy(c => c.Bounds.Center.Y))
        {
            var placed = false;
            foreach (var line in lines)
            {
                var lineBounds = CRect.Bounding(line.Select(c => c.Bounds));
                var overlap = VerticalOverlap(lineBounds, cluster.Bounds);
                var smaller = Math.Min(lineBounds.Height, cluster.Bounds.Height);
                var ratio = smaller <= 1e-9 ? (overlap > 0 ? 1 : 0) : overlap / smaller;
                if (ratio >= options.LineOverlapRatio)
                {
                    line.Add(cluster);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                lines.Add([cluster]);
            }
        }

        var ordered = lines
            .Select(l => (Line: l, Top: l.Min(c => c.Bounds.Top)))
            .OrderBy(x => x.Top)
            .Select(x => (IReadOnlyList<CharCluster>)[.. x.Line.OrderBy(c => c.Bounds.X)])
            .ToList();

        return ordered;
    }

    /// <summary>
    /// Joins recognised characters of one line, inserting a space where the ink gap is clearly wider
    /// than a character. CJK clusters never get a space in front of them.
    /// </summary>
    public static string JoinLine(IReadOnlyList<(string Text, CRect Bounds, bool IsCjk)> characters, GroupingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(characters);
        options ??= new GroupingOptions();
        if (characters.Count == 0)
        {
            return string.Empty;
        }

        var widths = characters.Where(c => c.Bounds.Width > 1e-9).Select(c => c.Bounds.Width).OrderBy(w => w).ToList();
        var medianWidth = widths.Count == 0 ? 1.0 : widths[widths.Count / 2];
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < characters.Count; i++)
        {
            if (i > 0)
            {
                var previous = characters[i - 1];
                var current = characters[i];
                var gap = current.Bounds.Left - previous.Bounds.Right;
                var wide = gap > options.WordGapFactor * Math.Max(medianWidth, current.Bounds.Height * 0.6);
                if (wide && !current.IsCjk && !previous.IsCjk)
                {
                    builder.Append(' ');
                }
            }

            builder.Append(characters[i].Text);
        }

        return builder.ToString();
    }

    private static bool ShouldMerge(CRect a, CRect b, GroupingOptions options)
    {
        var overlap = HorizontalOverlap(a, b);
        var narrower = Math.Min(a.Width, b.Width);
        if (narrower > 1e-9 && overlap / narrower >= options.OverlapMergeRatio)
        {
            return true;
        }

        var gap = b.Left - a.Right;
        if (gap <= 0)
        {
            return true;
        }

        // Size of a group is its larger dimension: a horizontal arm has a height of zero but is
        // still a wide stroke, and it must be able to join the stem it touches.
        var reference = Math.Min(Math.Max(a.Width, a.Height), Math.Max(b.Width, b.Height));
        return reference > 1e-9 && gap <= options.CharGapFactor * reference;
    }

    private static double HorizontalOverlap(CRect a, CRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));

    private static double VerticalOverlap(CRect a, CRect b) => Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
}
