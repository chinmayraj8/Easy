// Easy: compares a converted PDF with a reference rendering of the same document, beyond "the text is similar".
//
// - Text: words in reading order, normalized for what differs between renderers without changing the content: line-end
//   hyphenation (hyphens inside words are dropped on both sides), ligatures and compatibility forms (NFKC), soft
//   hyphens, whitespace. Compared as a multiset (extraction order differs between producers) and, for information, as
//   a sequence similarity.
// - Numbers: every numeric token (table values, results), as a multiset.
// - Pagination: page count, page sizes, and the page each numbered heading ("3.4 Live Deployment") starts on.
// - Images: count and placement per page.
// - Visual: both rendered at the same DPI. Ink within one pixel of ink in the other rendering is antialiasing; the rest is
//   real: ink only in the reference is missing or moved content, ink only in the output is extra, moved or overlapping
//   content. Each page gets an overlay (grey: both, red: reference only, blue: output only) and a verdict.

using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Parser.Parts;
using UglyToad.PdfPig.Tokens;

namespace Filee.Engines.Tests.Fidelity;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static partial class PdfFidelity
{
    /// <summary>A text line of a page: its words left to right, its baseline (points from the top).</summary>
    internal sealed record Line(string Text, double Top);

    internal sealed record PageFacts(int Number, double Width, double Height, List<Line> Lines, List<PdfRectangleBox> Images);

    internal sealed record PdfRectangleBox(double Left, double Top, double Width, double Height);

    internal sealed record VisualPage(int Number, string Verdict, double MissingPercent, double ExtraPercent, List<SKRectI> Regions, string Overlay);

    internal sealed record Report(
        IReadOnlyList<PageFacts> Reference, IReadOnlyList<PageFacts> Output,
        IReadOnlyList<string> MissingWords, IReadOnlyList<string> ExtraWords, double SequenceSimilarity,
        IReadOnlyList<string> MissingNumbers, IReadOnlyList<string> ExtraNumbers,
        IReadOnlyList<(string Heading, int ReferencePage, int OutputPage)> MovedHeadings,
        IReadOnlyList<string> PageProblems, IReadOnlyList<VisualPage> Visual)
    {
        /// <summary>Content lost or added: words, numbers, pages, images, or ink beyond antialiasing.</summary>
        public bool ContentDiffers => MissingWords.Count > 0 || ExtraWords.Count > 0 || MissingNumbers.Count > 0 || ExtraNumbers.Count > 0 || PageProblems.Count > 0;

        public bool LayoutDiffers => MovedHeadings.Count > 0 || Visual.Any(v => v.Verdict != Identical && v.Verdict != Antialiasing);
    }

    public const string Identical = "identical";
    public const string Antialiasing = "antialiasing only";

    /// <summary>Text lines, page sizes and image boxes of every page.</summary>
    public static List<PageFacts> Facts(string pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(page =>
        {
            // Lines and words from the glyphs themselves: PdfPig's word splitter glued table cells ("Changes119.8").
            var lines = page.Letters
                .GroupBy(l => Math.Round((page.Height - l.StartBaseLine.Y) / 2.5)) // one baseline within 2.5 pt
                .OrderBy(g => g.Key)
                .Select(g => new Line(LineText(g.OrderBy(l => l.StartBaseLine.X).ToList()), page.Height - g.First().StartBaseLine.Y))
                .Where(l => l.Text.Length > 0)
                .ToList();
            var images = page.GetImages().Select(i => new PdfRectangleBox(
                    Math.Round(i.BoundingBox.Left, 1), Math.Round(page.Height - i.BoundingBox.Top, 1), Math.Round(i.BoundingBox.Width, 1), Math.Round(i.BoundingBox.Height, 1)))
                .Concat(PatternImages(document, page))
                .OrderBy(i => i.Top).ThenBy(i => i.Left).ToList();
            return new PageFacts(page.Number, Math.Round(page.Width, 1), Math.Round(page.Height, 1), lines, images);
        }).ToList();
    }

    /// <summary>
    /// Images painted as tiling-pattern fills, which PdfPig's GetImages doesn't see (ONLYOFFICE draws every picture this
    /// way): the pattern's matrix moves its content, whose "w 0 0 h x y cm /Name Do" draws an image XObject.
    /// </summary>
    private static IEnumerable<PdfRectangleBox> PatternImages(PdfDocument document, Page page)
    {
        var scanner = document.Structure.TokenScanner;
        if (!page.Dictionary.TryGet(NameToken.Resources, scanner, out DictionaryToken? resources)
            || !resources.TryGet(NameToken.Pattern, scanner, out DictionaryToken? patterns))
            yield break;
        foreach (var token in patterns.Data.Values)
        {
            if (DirectObjectFinder.Get<StreamToken>(token, scanner) is not { } pattern
                || !pattern.StreamDictionary.TryGet(NameToken.Resources, scanner, out DictionaryToken? inner)
                || !inner.TryGet(NameToken.Xobject, scanner, out DictionaryToken? xobjects)
                || !xobjects.Data.Values.Any(x => DirectObjectFinder.Get<StreamToken>(x, scanner)?.StreamDictionary.TryGet(NameToken.Subtype, scanner, out NameToken? subtype) == true
                    && subtype == NameToken.Image))
                continue;
            double[] matrix = pattern.StreamDictionary.TryGet(NameToken.Matrix, scanner, out ArrayToken? m)
                ? [.. m.Data.OfType<NumericToken>().Select(n => n.Double)] : [1, 0, 0, 1, 0, 0];
            var content = Encoding.Latin1.GetString(pattern.Decode(DefaultFilterProvider.Instance).ToArray());
            if (DrawImage().Match(content) is not { Success: true } draw || matrix.Length != 6)
                continue;
            var cm = draw.Groups.Cast<Group>().Skip(1).Take(6).Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
            var (width, height) = (cm[0] * matrix[0], cm[3] * matrix[3]);
            var (left, bottom) = (matrix[4] + cm[4] * matrix[0], matrix[5] + cm[5] * matrix[3]);
            yield return new PdfRectangleBox(Math.Round(left, 1), Math.Round(page.Height - bottom - height, 1), Math.Round(width, 1), Math.Round(height, 1));
        }
    }

    /// <summary>
    /// A line's glyphs left to right as text: a space where the PDF has one, or where the gap to the next glyph is wider
    /// than a quarter of the line's median glyph width (a space is about half of it; kerning almost nothing). Producers
    /// such as ONLYOFFICE place words by position without space characters, and table cells never have one.
    /// </summary>
    private static string LineText(List<Letter> letters)
    {
        var widths = letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l => l.EndBaseLine.X - l.StartBaseLine.X).Where(w => w > 0).Order().ToList();
        var gap = widths.Count == 0 ? 1 : 0.25 * widths[widths.Count / 2];
        var text = new StringBuilder();
        for (var i = 0; i < letters.Count; i++)
        {
            if (i > 0 && !string.IsNullOrWhiteSpace(letters[i].Value) && !string.IsNullOrWhiteSpace(letters[i - 1].Value)
                && letters[i].StartBaseLine.X - letters[i - 1].EndBaseLine.X > gap)
                text.Append(' ');
            text.Append(string.IsNullOrWhiteSpace(letters[i].Value) ? " " : letters[i].Value);
        }
        return MultipleSpaces().Replace(text.ToString(), " ").Trim();
    }

    /// <summary>The words of the pages, normalized (see the file comment), in reading order.</summary>
    public static List<string> Words(IEnumerable<PageFacts> pages)
    {
        var text = new StringBuilder();
        var lines = pages.SelectMany(p => p.Lines).Select(l => l.Text.Normalize(NormalizationForm.FormKC).Replace("\u00AD", "", StringComparison.Ordinal)).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            // A line ending in a hyphen after a letter, followed by a line starting in lower case, is one word broken
            // by hyphenation: join without the hyphen. ("persis-" above a table row "Model …" is not.)
            var next = i + 1 < lines.Count ? lines[i + 1] : "";
            if (LineEndHyphen().IsMatch(lines[i]) && next.Length > 0 && char.IsLower(next[0]))
                text.Append(lines[i][..^1]);
            else
                text.Append(lines[i]).Append(' ');
        }
        return text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("-", "", StringComparison.Ordinal).Trim('.', ',', ';', ':'))
            // Bullets aren't words: a Symbol bullet (U+F0B7) extracts as "·" once rendered with a real font.
            .Select(w => string.Concat(w.Where(c => c is not ('•' or '·' or '▪' or '◦') && c is < '\uE000' or > '\uF8FF')))
            .Where(w => w.Length > 0).ToList();
    }

    /// <summary>Numbered headings ("3.4 Live Deployment") and the page each starts on.</summary>
    public static Dictionary<string, int> Headings(IEnumerable<PageFacts> pages)
    {
        var headings = new Dictionary<string, int>();
        foreach (var page in pages)
            foreach (var line in page.Lines)
                if (Heading().Match(line.Text) is { Success: true } match)
                    headings.TryAdd(match.Value.Trim(), page.Number);
        return headings;
    }

    /// <summary>Compares <paramref name="output"/> with <paramref name="reference"/>; overlays go to <paramref name="folder"/>.</summary>
    public static Report Compare(string reference, string output, string folder, int dpi = 100)
    {
        Directory.CreateDirectory(folder);
        var (refFacts, outFacts) = (Facts(reference), Facts(output));
        var (refWords, outWords) = (Words(refFacts), Words(outFacts));
        var (missingWords, extraWords) = Difference(refWords, outWords);
        var (missingNumbers, extraNumbers) = Difference(refWords.Where(IsNumber).ToList(), outWords.Where(IsNumber).ToList());

        var (refHeadings, outHeadings) = (Headings(refFacts), Headings(outFacts));
        var moved = refHeadings.Where(h => outHeadings.TryGetValue(h.Key, out var page) && page != h.Value)
            .Select(h => (h.Key, h.Value, outHeadings[h.Key])).ToList();

        var problems = new List<string>();
        if (refFacts.Count != outFacts.Count)
            problems.Add($"page count {refFacts.Count} → {outFacts.Count}");
        foreach (var (r, o) in refFacts.Zip(outFacts))
        {
            if (Math.Abs(r.Width - o.Width) > 0.5 || Math.Abs(r.Height - o.Height) > 0.5)
                problems.Add($"page {r.Number} size {r.Width}×{r.Height} → {o.Width}×{o.Height} pt");
            if (r.Images.Count != o.Images.Count)
                problems.Add($"page {r.Number} images {r.Images.Count} → {o.Images.Count}");
            else
                foreach (var (ri, oi) in r.Images.Zip(o.Images))
                    if (Math.Abs(ri.Left - oi.Left) > 2 || Math.Abs(ri.Top - oi.Top) > 2 || Math.Abs(ri.Width - oi.Width) > 2 || Math.Abs(ri.Height - oi.Height) > 2)
                        problems.Add($"page {r.Number} image moved or resized: {ri} → {oi}");
        }
        foreach (var heading in refHeadings.Keys.Where(h => !outHeadings.ContainsKey(h)))
            problems.Add($"heading \"{heading}\" missing");

        return new Report(refFacts, outFacts, missingWords, extraWords, SequenceSimilarity(refWords, outWords),
            missingNumbers, extraNumbers, moved, problems, Visual(reference, output, folder, dpi));
    }

    /// <summary>Words in one multiset and not the other, both ways.</summary>
    private static (List<string> Missing, List<string> Extra) Difference(List<string> reference, List<string> output)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in reference)
            counts[word] = counts.GetValueOrDefault(word) + 1;
        foreach (var word in output)
            counts[word] = counts.GetValueOrDefault(word) - 1;
        return (counts.Where(c => c.Value > 0).SelectMany(c => Enumerable.Repeat(c.Key, c.Value)).Order(StringComparer.Ordinal).ToList(),
                counts.Where(c => c.Value < 0).SelectMany(c => Enumerable.Repeat(c.Key, -c.Value)).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>2 × longest common subsequence ÷ total length (1 = same words in the same order).</summary>
    private static double SequenceSimilarity(List<string> a, List<string> b)
    {
        if (a.Count + b.Count == 0)
            return 1;
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        return Math.Round(2.0 * previous[b.Count] / (a.Count + b.Count), 4);
    }

    private static bool IsNumber(string word) => Number().IsMatch(word);

    /// <summary>
    /// Renders both PDFs at <paramref name="dpi"/> and compares ink page by page (see the file comment). A page only in
    /// one PDF is all missing or all extra.
    /// </summary>
    private static List<VisualPage> Visual(string reference, string output, string folder, int dpi)
    {
        var options = new RenderOptions(Dpi: dpi, BackgroundColor: SKColors.White);
        var refPages = Conversion.ToImages(File.ReadAllBytes(reference), null, options).ToList();
        var outPages = Conversion.ToImages(File.ReadAllBytes(output), null, options).ToList();
        var pages = new List<VisualPage>();
        try
        {
            for (var i = 0; i < Math.Max(refPages.Count, outPages.Count); i++)
                pages.Add(ComparePage(i + 1, i < refPages.Count ? refPages[i] : null, i < outPages.Count ? outPages[i] : null, folder));
        }
        finally
        {
            foreach (var bitmap in refPages.Concat(outPages))
                bitmap.Dispose();
        }
        return pages;
    }

    /// <summary>Ink: darker than this (0–255 luminance).</summary>
    private const int InkThreshold = 160;

    /// <summary>Share of a page's pixels (in %) that may differ before it isn't antialiasing any more.</summary>
    private const double Tolerance = 0.005;

    internal static VisualPage ComparePage(int number, SKBitmap? reference, SKBitmap? output, string folder)
    {
        var width = Math.Max(reference?.Width ?? 0, output?.Width ?? 0);
        var height = Math.Max(reference?.Height ?? 0, output?.Height ?? 0);
        var refInk = Ink(reference, width, height);
        var outInk = Ink(output, width, height);
        var (refNear, outNear) = (Dilate(refInk, width, height), Dilate(outInk, width, height));

        using var overlay = new SKBitmap(width, height);
        var missing = new bool[width * height];
        var extra = new bool[width * height];
        int missingCount = 0, extraCount = 0;
        for (var i = 0; i < width * height; i++)
        {
            missing[i] = refInk[i] && !outNear[i];
            extra[i] = outInk[i] && !refNear[i];
            missingCount += missing[i] ? 1 : 0;
            extraCount += extra[i] ? 1 : 0;
            var color = missing[i] ? new SKColor(220, 30, 30) : extra[i] ? new SKColor(30, 80, 230)
                : refInk[i] || outInk[i] ? new SKColor(170, 170, 170) : SKColors.White;
            overlay.SetPixel(i % width, i / width, color);
        }

        var regions = Regions(missing, extra, width, height);
        var total = (double)width * height;
        var (missingPercent, extraPercent) = (Math.Round(100 * missingCount / total, 3), Math.Round(100 * extraCount / total, 3));
        // Identical means the same pixels; ink that only moved within a pixel is antialiasing.
        var samePixels = reference is not null && output is not null && reference.Width == output.Width && reference.Height == output.Height
            && reference.GetPixelSpan().SequenceEqual(output.GetPixelSpan());
        var verdict = samePixels ? Identical
            : missingPercent <= Tolerance && extraPercent <= Tolerance ? Antialiasing
            : missingCount > 0 && extraCount > 0 ? "shifted or changed content"
            : missingCount > 0 ? "content missing in output"
            : "extra or overlapping content in output";

        using (var canvas = new SKCanvas(overlay))
        using (var paint = new SKPaint { Color = new SKColor(255, 140, 0), Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            foreach (var region in regions)
                canvas.DrawRect(region, paint);
        var path = Path.Combine(folder, $"diff-page-{number:00}.png");
        using (var file = File.Create(path))
            overlay.Encode(file, SKEncodedImageFormat.Png, 100);
        return new VisualPage(number, verdict, missingPercent, extraPercent, regions, path);
    }

    private static bool[] Ink(SKBitmap? bitmap, int width, int height)
    {
        var ink = new bool[width * height];
        if (bitmap is null)
            return ink;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                ink[y * width + x] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue < InkThreshold;
            }
        return ink;
    }

    /// <summary>Ink grown by one pixel in every direction: a glyph edge rendered a pixel over still matches.</summary>
    private static bool[] Dilate(bool[] ink, int width, int height)
    {
        var grown = new bool[ink.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (ink[y * width + x])
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (nx, ny) = (x + dx, y + dy);
                            if (nx >= 0 && ny >= 0 && nx < width && ny < height)
                                grown[ny * width + nx] = true;
                        }
        return grown;
    }

    /// <summary>Boxes around clusters of differing pixels: 16 px cells with any difference, merged when they touch.</summary>
    private static List<SKRectI> Regions(bool[] missing, bool[] extra, int width, int height)
    {
        const int Cell = 16;
        var (cols, rows) = ((width + Cell - 1) / Cell, (height + Cell - 1) / Cell);
        var marked = new bool[cols * rows];
        for (var i = 0; i < missing.Length; i++)
            if (missing[i] || extra[i])
                marked[i / width / Cell * cols + i % width / Cell] = true;
        var seen = new bool[marked.Length];
        var regions = new List<SKRectI>();
        for (var start = 0; start < marked.Length; start++)
        {
            if (!marked[start] || seen[start])
                continue;
            var (minX, minY, maxX, maxY) = (int.MaxValue, int.MaxValue, 0, 0);
            var stack = new Stack<int>([start]);
            seen[start] = true;
            while (stack.TryPop(out var cell))
            {
                var (cx, cy) = (cell % cols, cell / cols);
                (minX, minY, maxX, maxY) = (Math.Min(minX, cx), Math.Min(minY, cy), Math.Max(maxX, cx), Math.Max(maxY, cy));
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (cx + dx, cy + dy);
                        if (nx < 0 || ny < 0 || nx >= cols || ny >= rows || seen[ny * cols + nx] || !marked[ny * cols + nx])
                            continue;
                        seen[ny * cols + nx] = true;
                        stack.Push(ny * cols + nx);
                    }
            }
            regions.Add(new SKRectI(minX * Cell, minY * Cell, Math.Min(width, (maxX + 1) * Cell), Math.Min(height, (maxY + 1) * Cell)));
        }
        return regions;
    }

    /// <summary>The report as Markdown, with the overlays linked.</summary>
    public static string Markdown(Report report)
    {
        var text = new StringBuilder();
        string List(IReadOnlyList<string> items) => items.Count == 0 ? "none" : string.Join(", ", items.Take(40)) + (items.Count > 40 ? $" … ({items.Count})" : "");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Pages: {report.Reference.Count} → {report.Output.Count}; sizes: {string.Join(", ", report.Output.Select(p => $"{p.Width}×{p.Height}").Distinct())} pt");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Words missing in output: {List(report.MissingWords)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Words extra in output: {List(report.ExtraWords)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Reading-order similarity: {report.SequenceSimilarity:0.0000}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Numbers missing / extra: {List(report.MissingNumbers)} / {List(report.ExtraNumbers)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Headings on another page: {(report.MovedHeadings.Count == 0 ? "none" : string.Join(", ", report.MovedHeadings.Select(h => $"\"{h.Heading}\" {h.ReferencePage} → {h.OutputPage}")))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Images (output): {string.Join("; ", report.Output.Where(p => p.Images.Count > 0).Select(p => $"page {p.Number}: " + string.Join(", ", p.Images.Select(i => $"{i.Width}×{i.Height} pt at ({i.Left}, {i.Top})"))))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Page, size and image problems: {List(report.PageProblems)}");
        text.AppendLine();
        text.AppendLine("| Page | Visual verdict | Reference-only ink % | Output-only ink % | Regions | Overlay |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var page in report.Visual)
            text.AppendLine(CultureInfo.InvariantCulture, $"| {page.Number} | {page.Verdict} | {page.MissingPercent} | {page.ExtraPercent} | {page.Regions.Count} | {Path.GetFileName(page.Overlay)} |");
        return text.ToString();
    }

    [GeneratedRegex(@"([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+cm\s*/\S+\s+Do")]
    private static partial Regex DrawImage();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex(@"\p{L}-$")]
    private static partial Regex LineEndHyphen();

    [GeneratedRegex(@"^\d+(\.\d+)*\s+\p{Lu}[\p{L} \-]{2,60}$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[−\-+]?\d+([.,]\d+)*%?$")]
    private static partial Regex Number();
}
