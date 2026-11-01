// Page borders, shaded boxes and rules beside paragraphs for PdfDocumentReader (PDF → DOCX).
//
// Lines and fills outside ruled tables were dropped. Three common ones are kept:
// - lines near all four paper edges, each along most of the page (a frame) → a page border;
// - a filled rectangle around whole paragraphs (a quote or prompt box) → their shading, and a line along one of its
//   edges (a bar at its left side) → that side's border;
// - a long line just under a paragraph (the rule under a heading) → its bottom border; just over one → its top border.
// Other drawings stay out, as before.

using Filee.Engines.Hwp.Hwpx;
using UglyToad.PdfPig.Core;

namespace Filee.Engines.Pdf;

internal sealed partial class PdfDocumentReader
{
    /// <summary>Farthest a page frame lies from the paper edges (points).</summary>
    private const double FrameReach = 72;

    /// <summary>The page frame, taken out of <paramref name="rules"/>; null when the page has none.</summary>
    private static HPageBorder? PageFrame(List<Rule> rules, double width, double height)
    {
        Rule? Find(bool horizontal, Func<Rule, bool> near) => rules
            .Where(r => r.Horizontal == horizontal && near(r) && r.Length >= (horizontal ? width : height) * 0.8)
            .OrderByDescending(r => r.Length).FirstOrDefault();
        if (Find(true, r => height - r.Position <= FrameReach) is not { } top
            || Find(true, r => r.Position <= FrameReach) is not { } bottom
            || Find(false, r => r.Position <= FrameReach) is not { } left
            || Find(false, r => width - r.Position <= FrameReach) is not { } right)
            return null;
        foreach (var rule in new[] { top, bottom, left, right })
            rules.Remove(rule);
        return new HPageBorder(Line(top), height - top.Position, width - right.Position, bottom.Position, left.Position);
    }

    private static HBorder Line(Rule rule) =>
        new(rule.Dashed ? HBorderStyle.Dash : HBorderStyle.Solid, rule.Thickness * 25.4 / 72, rule.Color);

    private static PdfRectangle Bounds(Rule r) => r.Horizontal
        ? new PdfRectangle(r.Start, r.Position - r.Thickness / 2, r.End, r.Position + r.Thickness / 2)
        : new PdfRectangle(r.Position - r.Thickness / 2, r.Start, r.Position + r.Thickness / 2, r.End);

    /// <summary>
    /// Frames around pictures as their outlines; framed or shaded boxes and rules beside paragraphs, outside tables, as
    /// the paragraphs' <see cref="Item.Decoration"/>.
    /// </summary>
    private static void Decorate(PageContent page, List<RuledTable> tables, List<Rule> rules, List<(PdfRectangle Bounds, string Color)> shades,
        List<(PdfRectangle Bounds, Rule Edge)> frames)
    {
        bool InTable(PdfRectangle r) => tables.Any(t => Overlaps(t.Bounds, r));
        // A table's own rules, its outline included (a hairline never overlaps the table by EdgeTolerance).
        bool OfTable(Rule rule) => Bounds(rule) is var b && tables.Any(t => b.Left >= t.Bounds.Left - EdgeTolerance && b.Right <= t.Bounds.Right + EdgeTolerance
                                                                          && b.Bottom >= t.Bounds.Bottom - EdgeTolerance && b.Top <= t.Bounds.Top + EdgeTolerance);
        var free = rules.Where(r => !OfTable(r)).ToList();
        var text = page.Items.Where(i => !i.IsPicture && !i.IsTable && i.Lines.Count > 0).ToList();
        static bool Near(PdfRectangle a, PdfRectangle b) =>
            Math.Abs(a.Left - b.Left) <= EdgeTolerance && Math.Abs(a.Right - b.Right) <= EdgeTolerance
            && Math.Abs(a.Top - b.Top) <= EdgeTolerance && Math.Abs(a.Bottom - b.Bottom) <= EdgeTolerance;
        List<Item> Inside(PdfRectangle box) =>
            text.Where(i => i.Left >= box.Left - 1 && i.Right <= box.Right + 1 && i.Top <= box.Top + 1 && i.Bottom >= box.Bottom - 1).ToList();
        // The frame's own edges aren't rules beside paragraphs.
        void TakeEdges(PdfRectangle box) => free.RemoveAll(r =>
            (r.Horizontal ? Math.Min(Math.Abs(r.Position - box.Top), Math.Abs(r.Position - box.Bottom)) : Math.Min(Math.Abs(r.Position - box.Left), Math.Abs(r.Position - box.Right)))
                <= r.Thickness / 2 + SameLine
            && r.Start >= (r.Horizontal ? box.Left : box.Bottom) - RuleGap && r.End <= (r.Horizontal ? box.Right : box.Top) + RuleGap);

        var framedShades = new List<PdfRectangle>();
        foreach (var (box, edge) in frames)
        {
            // Not a table's outline, not the page frame (a page border already; a tall picture's frame can be as large).
            if (InTable(box) || (box.Left <= FrameReach && box.Bottom <= FrameReach && page.Width - box.Right <= FrameReach && page.Height - box.Top <= FrameReach))
                continue;
            // A frame's edges are never rules beside paragraphs, whatever it frames.
            TakeEdges(box);
            if (page.Items.FirstOrDefault(i => i.IsPicture && i.PictureBounds is { } p && Near(p, box)) is { } picture)
            {
                picture.Outline = Line(edge);
                continue;
            }
            var inside = Inside(box);
            if (inside.Count == 0)
                continue;
            var fill = shades.Where(s => Near(s.Bounds, box)).Select(s => s.Color).FirstOrDefault();
            framedShades.AddRange(shades.Where(s => Near(s.Bounds, box)).Select(s => s.Bounds));
            var line = Line(edge);
            var decoration = new HParaBox(fill, new HBorders(line, line, line, line), inside.Min(i => i.Left) - box.Left);
            foreach (var item in inside)
                item.Decoration = decoration;
        }

        foreach (var (box, fill) in shades)
        {
            // Not cell shading, not a page background, not a framed box's fill (done above).
            if (InTable(box) || box.Width * box.Height > page.Width * page.Height * 0.5 || framedShades.Contains(box))
                continue;
            var inside = Inside(box);
            if (inside.Count == 0)
                continue;
            HBorder Edge(bool horizontal, double position, double start, double end)
            {
                var rule = free.FirstOrDefault(r => r.Horizontal == horizontal && Math.Abs(r.Position - position) <= r.Thickness / 2 + SameLine
                                                    && r.Start <= start + RuleGap && r.End >= end - RuleGap);
                if (rule is null)
                    return HBorder.None;
                free.Remove(rule);
                return Line(rule);
            }
            var borders = new HBorders(
                Left: Edge(false, box.Left, box.Bottom, box.Top),
                Right: Edge(false, box.Right, box.Bottom, box.Top),
                Top: Edge(true, box.Top, box.Left, box.Right),
                Bottom: Edge(true, box.Bottom, box.Left, box.Right));
            var decoration = new HParaBox(fill, borders, inside.Min(i => i.Left) - box.Left);
            foreach (var item in inside)
                item.Decoration = decoration;
        }

        // A rule across much of the page, within a line of a paragraph's bottom (or top), starting at or before its text.
        foreach (var rule in free.Where(r => r.Horizontal && r.Length >= page.Width * 0.4))
        {
            bool Along(Item i) => rule.Start <= i.Left + RuleGap && rule.End >= i.Right - RuleGap;
            double Reach(Item i) => Math.Max(6, i.Size * 1.2);
            if (text.Where(i => Along(i) && i.Bottom >= rule.Position && i.Bottom - rule.Position <= Reach(i))
                    .OrderBy(i => i.Bottom - rule.Position).FirstOrDefault() is { } above)
            {
                var box = above.Decoration ?? new HParaBox(null, HBorders.Empty, 0);
                above.Decoration = box with { Borders = box.Borders with { Bottom = Line(rule) }, Space = Math.Max(box.Space, above.Bottom - rule.Position) };
            }
            else if (text.Where(i => Along(i) && rule.Position >= i.Top && rule.Position - i.Top <= Reach(i))
                         .OrderBy(i => rule.Position - i.Top).FirstOrDefault() is { } below)
            {
                var box = below.Decoration ?? new HParaBox(null, HBorders.Empty, 0);
                below.Decoration = box with { Borders = box.Borders with { Top = Line(rule) }, Space = Math.Max(box.Space, rule.Position - below.Top) };
            }
        }
    }
}
