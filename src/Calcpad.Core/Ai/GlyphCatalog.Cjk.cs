namespace Calcpad.Core.Ai;

/// <summary>
/// CJK half of the built-in prototype library: CJK numerals, the unit and arithmetic characters a
/// calculator sees most often, and a set of common components. Strokes are authored in writing order
/// in the <c>16 x 16</c> design box because the DTW channel compares direction sequences.
/// </summary>
public static partial class GlyphCatalog
{
    private static partial IReadOnlyList<GlyphPrototype> BuildCjk()
    {
        const GlyphChannel C = GlyphChannel.Cjk;
        var list = new List<GlyphPrototype>();

        // CJK numerals and quantity characters.
        list.Add(new GlyphPrototype("一", C, new GlyphBuilder().Line(2.5, 8.0, 13.5, 8.0).Strokes));
        list.Add(new GlyphPrototype("二", C, new GlyphBuilder().Line(3.0, 5.5, 13.0, 5.5).Line(2.0, 12.0, 14.0, 12.0).Strokes));
        list.Add(new GlyphPrototype("二", C, new GlyphBuilder().Line(2.0, 5.5, 14.0, 5.5).Line(2.0, 12.0, 14.0, 12.0).Strokes, "even"));
        list.Add(new GlyphPrototype("三", C, new GlyphBuilder().Line(3.0, 3.5, 13.0, 3.5).Line(4.5, 8.0, 11.5, 8.0).Line(2.5, 13.0, 13.5, 13.0).Strokes));
        list.Add(new GlyphPrototype("四", C, new GlyphBuilder().Poly(3.5, 3.0, 3.5, 13.0).Poly(3.5, 3.0, 13.0, 3.0, 13.0, 13.0).Poly(7.5, 6.5, 5.5, 11.5).Poly(9.5, 6.5, 9.5, 11.0, 11.5, 11.5).Line(3.5, 13.0, 13.0, 13.0).Strokes));
        list.Add(new GlyphPrototype("五", C, new GlyphBuilder().Line(3.0, 3.5, 13.0, 3.5).Poly(8.5, 3.5, 7.0, 8.5).Line(2.5, 8.5, 13.5, 8.5).Poly(3.5, 8.5, 3.5, 13.0, 13.0, 13.0).Strokes));
        list.Add(new GlyphPrototype("六", C, new GlyphBuilder().Dot(8, 3.5, 1.3).Line(2.5, 7.0, 13.5, 7.0).Poly(8.0, 7.0, 5.5, 11.5, 4.0, 13.5).Poly(8.0, 7.0, 10.5, 11.5, 12.0, 13.5).Strokes));
        list.Add(new GlyphPrototype("七", C, new GlyphBuilder().Line(2.5, 6.0, 13.5, 6.0).Poly(9.0, 3.0, 8.0, 10.5, 9.5, 13.0, 12.5, 12.5).Strokes));
        list.Add(new GlyphPrototype("八", C, new GlyphBuilder().Poly(8.0, 3.0, 5.0, 11.5, 3.5, 13.5).Poly(8.0, 3.0, 10.5, 11.5, 12.5, 13.5).Strokes));
        list.Add(new GlyphPrototype("九", C, new GlyphBuilder().Poly(6.0, 2.5, 4.5, 7.5, 3.0, 9.5).Poly(11.5, 2.5, 11.0, 9.0, 9.5, 12.0, 7.0, 13.5, 5.0, 12.5).Strokes));
        list.Add(new GlyphPrototype("十", C, new GlyphBuilder().Line(2.0, 8.0, 14.0, 8.0).Line(8.0, 2.0, 8.0, 14.0).Strokes));
        list.Add(new GlyphPrototype("十", C, new GlyphBuilder().Line(8.0, 2.0, 8.0, 14.0).Line(2.0, 8.0, 14.0, 8.0).Strokes, "alt-order"));
        list.Add(new GlyphPrototype("百", C, new GlyphBuilder().Line(2.5, 3.0, 13.5, 3.0).Poly(8.0, 3.0, 6.5, 6.5).Poly(5.0, 6.5, 5.0, 13.5).Poly(5.0, 6.5, 11.5, 6.5, 11.5, 13.5).Line(5.0, 10.0, 11.5, 10.0).Line(5.0, 13.5, 11.5, 13.5).Strokes));
        list.Add(new GlyphPrototype("千", C, new GlyphBuilder().Poly(8.5, 2.5, 5.0, 6.5).Line(2.0, 8.0, 14.0, 8.0).Line(8.5, 2.5, 8.5, 14.0).Strokes));
        list.Add(new GlyphPrototype("万", C, new GlyphBuilder().Line(2.5, 3.0, 13.5, 3.0).Poly(7.0, 3.0, 5.0, 8.5).Poly(4.0, 8.5, 4.0, 12.5, 12.5, 12.5, 10.5, 11.5).Strokes));

        // Unit characters.
        list.Add(new GlyphPrototype("米", C, new GlyphBuilder().Dot(8, 3.0, 1.3).Poly(8.0, 4.5, 4.0, 7.5).Poly(8.0, 4.5, 12.0, 7.5).Line(2.0, 8.5, 14.0, 8.5).Line(8.0, 4.5, 8.0, 14.0).Poly(8.0, 9.5, 5.0, 13.0).Poly(8.0, 9.5, 11.0, 13.0).Strokes));
        list.Add(new GlyphPrototype("厘", C, new GlyphBuilder().Line(2.5, 3.0, 13.0, 3.0).Poly(2.5, 3.0, 2.5, 14.0).Line(4.5, 6.5, 12.0, 6.5).Poly(5.5, 6.5, 5.5, 10.0, 11.5, 10.0, 11.5, 6.5).Line(5.5, 10.0, 11.5, 10.0).Line(5.5, 13.0, 11.5, 13.0).Line(8.0, 10.0, 8.0, 13.0).Strokes));
        list.Add(new GlyphPrototype("秒", C, new GlyphBuilder().Poly(6.0, 2.5, 4.0, 5.0).Line(2.5, 5.0, 10.0, 5.0).Line(6.0, 2.5, 6.0, 9.5).Poly(6.0, 5.5, 3.0, 8.0).Poly(6.0, 5.5, 9.0, 8.0).Line(11.0, 6.0, 11.0, 13.5).Poly(8.5, 8.5, 13.0, 8.5).Poly(11.0, 13.5, 8.5, 14.5).Strokes));
        list.Add(new GlyphPrototype("分", C, new GlyphBuilder().Poly(8.0, 2.5, 5.5, 6.0).Poly(8.0, 2.5, 10.5, 6.0).Poly(11.5, 6.0, 4.0, 13.5).Poly(4.0, 6.5, 4.0, 10.0, 12.0, 10.0, 12.0, 6.5).Strokes));
        list.Add(new GlyphPrototype("时", C, new GlyphBuilder().Poly(4.0, 3.5, 4.0, 12.0).Poly(4.0, 3.5, 9.0, 3.5, 9.0, 12.0, 4.0, 12.0).Line(4.0, 7.5, 9.0, 7.5).Line(11.0, 5.0, 11.0, 13.5).Line(9.5, 8.5, 13.5, 8.5).Dot(12.5, 11.5, 1.1).Strokes));
        list.Add(new GlyphPrototype("克", C, new GlyphBuilder().Line(2.5, 4.5, 13.5, 4.5).Line(8.0, 1.5, 8.0, 7.5).Poly(5.5, 7.5, 5.5, 11.5, 10.5, 11.5, 10.5, 7.5).Poly(7.0, 11.5, 5.5, 13.8).Poly(9.0, 11.5, 10.5, 13.8).Strokes));
        list.Add(new GlyphPrototype("斤", C, new GlyphBuilder().Poly(11.0, 2.5, 4.0, 5.5).Poly(4.0, 5.5, 5.0, 9.0).Line(4.0, 8.0, 13.0, 8.0).Line(7.5, 8.0, 7.5, 14.0).Strokes));
        list.Add(new GlyphPrototype("元", C, new GlyphBuilder().Line(2.5, 4.0, 13.5, 4.0).Line(3.5, 7.5, 12.5, 7.5).Poly(6.0, 7.5, 4.5, 12.5, 3.5, 13.8).Poly(10.0, 7.5, 11.5, 12.5, 12.5, 13.8).Strokes));
        list.Add(new GlyphPrototype("小", C, new GlyphBuilder().Line(8.0, 3.0, 8.0, 13.5).Poly(8.0, 7.0, 5.0, 11.5, 4.5, 13.0).Poly(8.0, 7.0, 11.0, 11.5, 11.5, 13.0).Strokes));
        list.Add(new GlyphPrototype("于", C, new GlyphBuilder().Line(3.0, 4.5, 13.0, 4.5).Line(2.5, 8.0, 13.5, 8.0).Poly(8.0, 4.5, 8.0, 12.0, 6.5, 13.8).Strokes));

        // Arithmetic and punctuation characters.
        list.Add(new GlyphPrototype("加", C, new GlyphBuilder().Poly(3.0, 3.0, 8.5, 3.0, 8.5, 10.5, 7.0, 12.5).Poly(6.5, 4.0, 4.5, 9.0, 3.0, 11.0).Poly(10.5, 7.5, 10.5, 13.5, 14.5, 13.5, 14.5, 7.5, 10.5, 7.5).Strokes));
        list.Add(new GlyphPrototype("加", C, new GlyphBuilder().Poly(3.0, 3.0, 8.5, 3.0, 8.5, 10.5, 7.0, 12.5).Poly(6.5, 4.0, 4.5, 9.0, 3.0, 11.0).Poly(11.0, 8.0, 11.0, 13.0, 14.0, 13.0, 14.0, 8.0, 11.0, 8.0).Strokes, "small-mouth"));
        list.Add(new GlyphPrototype("减", C, new GlyphBuilder().Dot(3.0, 5.0, 1.1).Poly(3.5, 7.0, 2.5, 9.0).Line(6.0, 3.5, 13.0, 3.5).Poly(9.5, 3.5, 8.0, 6.5).Line(5.5, 6.5, 13.5, 6.5).Poly(7.5, 8.5, 7.5, 11.5, 11.5, 11.5, 11.5, 8.5).Line(9.5, 11.5, 9.5, 8.5).Poly(12.0, 7.0, 11.0, 13.5).Strokes));
        list.Add(new GlyphPrototype("等", C, new GlyphBuilder().Poly(5.0, 2.5, 3.5, 5.0).Poly(11.0, 2.5, 12.5, 5.0).Line(4.0, 6.5, 12.0, 6.5).Line(8.0, 6.5, 8.0, 9.0).Line(3.5, 9.0, 12.5, 9.0).Line(10.5, 9.0, 10.5, 13.5).Dot(12.5, 12.0, 1.0).Strokes));
        list.Add(new GlyphPrototype("点", C, new GlyphBuilder().Line(6.0, 2.5, 6.0, 6.5).Line(3.5, 4.5, 9.5, 4.5).Poly(4.0, 7.5, 4.0, 12.0, 11.5, 12.0, 11.5, 7.5).Dot(3.5, 13.8, 1.1).Dot(7.0, 14.2, 1.1).Dot(10.5, 14.2, 1.1).Dot(13.5, 13.6, 1.1).Strokes));
        list.Add(new GlyphPrototype("数", C, new GlyphBuilder().Dot(3.0, 3.0, 1.2).Poly(3.0, 4.0, 1.5, 6.5).Line(1.5, 6.5, 6.0, 6.5).Line(3.5, 4.5, 3.5, 10.0).Poly(3.5, 6.0, 1.5, 9.0).Poly(3.5, 6.0, 5.5, 9.0).Poly(9.0, 3.0, 12.0, 5.5).Poly(12.0, 3.0, 9.5, 6.5).Line(10.5, 6.0, 10.5, 10.5).Poly(8.0, 7.5, 13.0, 7.5).Poly(10.5, 10.5, 9.0, 13.5).Strokes));

        // Common components / radicals.
        list.Add(new GlyphPrototype("亻", C, new GlyphBuilder().Poly(8.5, 2.5, 6.5, 6.0).Line(6.5, 6.0, 7.0, 14.0).Strokes));
        list.Add(new GlyphPrototype("氵", C, new GlyphBuilder().Dot(7.0, 4.5, 1.1).Dot(6.0, 8.5, 1.1).Poly(6.0, 11.0, 6.5, 13.0, 4.5, 14.0).Strokes));
        list.Add(new GlyphPrototype("扌", C, new GlyphBuilder().Line(6.5, 3.0, 6.5, 14.0).Line(2.5, 6.5, 11.0, 6.5).Poly(6.5, 8.5, 3.5, 11.5).Strokes));
        list.Add(new GlyphPrototype("讠", C, new GlyphBuilder().Dot(7.5, 3.5, 1.1).Poly(6.5, 6.5, 8.0, 8.0, 6.5, 13.0).Strokes));
        list.Add(new GlyphPrototype("忄", C, new GlyphBuilder().Dot(4.0, 6.0, 1.1).Dot(11.0, 6.0, 1.1).Line(7.5, 3.0, 7.5, 14.0).Strokes));
        list.Add(new GlyphPrototype("口", C, new GlyphBuilder().Poly(3.5, 3.5, 3.5, 12.0, 12.5, 12.0, 12.5, 3.5, 3.5, 3.5).Strokes));
        list.Add(new GlyphPrototype("口", C, new GlyphBuilder().Poly(3.5, 3.5, 12.5, 3.5, 12.5, 12.0).Line(12.5, 12.0, 3.5, 12.0).Line(3.5, 12.0, 3.5, 3.5).Strokes, "three-strokes"));
        list.Add(new GlyphPrototype("土", C, new GlyphBuilder().Line(3.0, 6.0, 13.0, 6.0).Line(8.0, 3.0, 8.0, 13.0).Line(2.0, 13.0, 14.0, 13.0).Strokes));
        list.Add(new GlyphPrototype("大", C, new GlyphBuilder().Line(2.0, 5.5, 14.0, 5.5).Poly(8.0, 2.5, 4.5, 13.5).Poly(8.0, 2.5, 11.5, 13.5).Strokes));
        list.Add(new GlyphPrototype("女", C, new GlyphBuilder().Poly(9.5, 3.0, 3.0, 12.0).Poly(6.0, 4.5, 12.5, 9.0).Poly(7.5, 6.5, 3.0, 13.5).Line(2.0, 9.5, 14.0, 9.5).Strokes));
        list.Add(new GlyphPrototype("子", C, new GlyphBuilder().Poly(4.0, 3.5, 11.0, 3.5, 7.0, 7.5).Line(7.0, 4.0, 7.0, 13.5).Poly(7.0, 13.5, 5.0, 14.5).Strokes));
        list.Add(new GlyphPrototype("木", C, new GlyphBuilder().Line(2.0, 6.0, 14.0, 6.0).Line(8.0, 2.0, 8.0, 14.0).Poly(8.0, 7.0, 4.5, 12.5).Poly(8.0, 7.0, 11.5, 12.5).Strokes));
        list.Add(new GlyphPrototype("日", C, new GlyphBuilder().Poly(4.5, 3.0, 4.5, 13.0).Poly(4.5, 3.0, 11.5, 3.0, 11.5, 13.0, 4.5, 13.0).Line(4.5, 8.0, 11.5, 8.0).Strokes));
        list.Add(new GlyphPrototype("月", C, new GlyphBuilder().Poly(5.0, 3.0, 4.0, 6.0, 4.0, 14.0).Poly(5.0, 3.0, 11.5, 3.0, 12.5, 6.0, 12.5, 14.0).Line(4.0, 8.0, 12.5, 8.0).Line(4.0, 11.5, 12.5, 11.5).Strokes));
        list.Add(new GlyphPrototype("目", C, new GlyphBuilder().Poly(4.5, 2.5, 4.5, 13.5).Poly(4.5, 2.5, 11.5, 2.5, 11.5, 13.5, 4.5, 13.5).Line(4.5, 6.0, 11.5, 6.0).Line(4.5, 9.5, 11.5, 9.5).Strokes));
        list.Add(new GlyphPrototype("力", C, new GlyphBuilder().Poly(3.0, 3.0, 8.5, 3.0, 8.5, 10.5, 7.0, 12.5).Poly(6.5, 4.0, 4.5, 9.0, 3.0, 11.0).Strokes));
        list.Add(new GlyphPrototype("又", C, new GlyphBuilder().Poly(3.0, 4.0, 12.5, 8.5).Poly(11.0, 4.5, 7.0, 8.5, 5.0, 12.0).Strokes));
        list.Add(new GlyphPrototype("心", C, new GlyphBuilder().Dot(3.5, 7.0, 1.2).Poly(5.0, 9.0, 8.0, 8.0, 11.0, 9.5, 13.0, 12.5).Dot(7.0, 12.5, 1.2).Dot(11.5, 12.8, 1.2).Strokes));

        return list;
    }
}
