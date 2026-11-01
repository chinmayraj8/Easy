// Easy: rebuilds an editable DOCX from ONLYOFFICE's line-by-line reading of a PDF.
//
// x2t's line mode (m_nTextAssociationType 2) keeps a PDF's fonts and styles but writes every line as a text box at its
// position on the page (and a few top-of-page lines as positioned paragraphs). That is faithful but not a document: no
// paragraphs, tables are loose values, page numbers are text, words stay split at the PDF's line ends. This turns the
// lines into structure, from geometry only (nothing about a particular document):
// - lines: boxes on one baseline; a smaller box raised or lowered against a line is its sub- or superscript
// - tables: three or more consecutive rows of narrow, separated items aligned in columns; one value per cell, header
//   lines that share a row joined, horizontal rule shapes become borders
// - paragraphs: lines joined until a gap, a short last line, an indent, a list marker or another text size; indents,
//   alignment, line pitch and spacing measured from the positions; a paragraph can continue on the next page
// - line-end hyphenation becomes an optional hyphen where the PDF broke the word (so lines can break as they did), unless
//   the hyphenated form appears unbroken in the document
// - page numbers at the top or bottom of pages become a PAGE field in a header or footer (even/odd, title page)
// - pictures become inline pictures; margins come from the text block

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Filee.Engines.Office;

/// <summary>Builds paragraphs, tables and headers from a line-mode DOCX (see the file comment).</summary>
internal static partial class PdfLayoutDocx
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>What an item is; a Shape is any other drawing (a filled area, a logo, an icon).</summary>
    private enum Kind { Text, Picture, Rule, Shape }

    /// <summary>A line box, a positioned paragraph, a picture or a rule, in points from the page's top left.</summary>
    /// <param name="Centered">A positioned paragraph with equal indents from both page edges (how x2t centres a line).</param>
    /// <param name="Baseline">The text's baseline; for an item snapped to the PDF's glyphs, theirs.</param>
    /// <param name="FirstWord">Width of the text's first word (what a line before it would have needed to hold it).</param>
    /// <param name="SecondWord">Where the second word starts (after a list marker: where the text hangs from).</param>
    private sealed record Item(Kind Kind, double Left, double Top, double Width, double Height, XElement? Paragraph, XElement? Anchor,
        bool Flowing = false, bool Centered = false, double Baseline = 0, double FirstWord = 0, double SecondWord = 0, string? Font = null)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public string Text => Paragraph is null ? "" : string.Concat(Paragraph.Descendants(W + "t").Select(t => t.Value));
    }

    /// <summary>Items on one baseline, left to right, with their sub- and superscripts.</summary>
    private sealed class Row
    {
        public List<Item> Items { get; } = [];
        public List<(Item Item, bool Super)> Scripts { get; } = [];
        public double Top => Items.Min(i => i.Top);
        public double Bottom => Items.Max(i => i.Bottom);
        public double Height => Items.Max(i => i.Height);
        public double Left => Items.Min(i => i.Left);
        public double Right => Items.Max(i => i.Right);
        public string Text => string.Join(" ", Items.Select(i => i.Text.Trim()));
        public bool Flowing => Items.Count == 1 && Items[0].Flowing;
        public Kind Kind => Items[0].Kind;
        public double Baseline => Items[0].Baseline;
        /// <summary>Width of the line's first word: measured on the glyphs, else estimated from its letters.</summary>
        public double FirstWord => Items[0].FirstWord > 0 ? Items[0].FirstWord
            : 0.5 * Height * (Text.TrimStart().Split(' ', 2)[0].Length);
    }

    /// <summary>A glyph of the PDF: its advance from left to right, its baseline (points from the page top) and size.</summary>
    internal sealed record Glyph(double Left, double Right, double Baseline, double Size, string Value, string Font = "")
    {
        public bool Blank => IsBlank(Value);
    }

    /// <summary>A built block of the body in reading order, with where it starts and ends on its page.</summary>
    /// <param name="Columns">For a section break: the number of text columns of the section it ends; -1 for a column break; 0 otherwise.</param>
    private sealed record Block(List<XElement> Elements, double Top, double Bottom, int Page, List<Row>? Lines = null, int Columns = 0, double Gutter = 0);

    /// <summary>
    /// Rewrites the DOCX at <paramref name="path"/> in place. <paramref name="textColumn"/> is the text block's left and
    /// right edge in points measured on the PDF's glyphs (<see cref="TextColumn"/>); x2t's line boxes run wider than
    /// their text, so without it the column is estimated from them.
    /// </summary>
    /// <returns>Notes for the log: fonts the DOCX names instead of the ones x2t chose, and why.</returns>
    public static IReadOnlyList<string> Rebuild(string path, (double Left, double Right)? textColumn = null, IReadOnlyList<List<Glyph>>? glyphs = null,
        ISet<string>? installedFonts = null)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var document = Load(zip, "word/document.xml") ?? throw new InvalidDataException("No word/document.xml.");
        var uses = new Dictionary<string, Dictionary<string, int>>();
        var headers = Rebuild(document, textColumn, out var hyphenated, glyphs, uses);
        Save(zip, "word/document.xml", document);
        var notes = new List<string>();
        if (uses.Count > 0 && Load(zip, "word/styles.xml") is { } styles)
        {
            notes.AddRange(RestoreFonts(styles, uses, installedFonts));
            Save(zip, "word/styles.xml", styles);
        }
        if (headers.Count > 0)
            AddHeaders(zip, document, headers);
        // A PDF set with automatic hyphenation (several words rejoined): so is the DOCX, or its lines wrap longer. The
        // optional hyphens mark where the PDF broke; renderers that only break at those under hyphenation need this too.
        if (hyphenated && Load(zip, "word/settings.xml") is { } settings)
        {
            AddSetting(settings, "autoHyphenation");
            Save(zip, "word/settings.xml", settings);
        }
        return notes;
    }

    /// <summary>
    /// Metric-compatible fonts (same advance widths, so lines break the same): a PDF's font that isn't installed is
    /// named as an installed equivalent. Keys are font names in lower case without spaces.
    /// </summary>
    private static readonly Dictionary<string, string[]> Equivalents = new()
    {
        ["liberationsans"] = ["Arial", "Arimo", "Helvetica"],
        ["arimo"] = ["Arial", "Liberation Sans", "Helvetica"],
        ["arial"] = ["Liberation Sans", "Arimo", "Helvetica"],
        ["helvetica"] = ["Arial", "Liberation Sans", "Arimo"],
        ["liberationserif"] = ["Times New Roman", "Tinos", "Times"],
        ["tinos"] = ["Times New Roman", "Liberation Serif", "Times"],
        ["timesnewroman"] = ["Liberation Serif", "Tinos", "Times"],
        ["times"] = ["Times New Roman", "Liberation Serif", "Tinos"],
        ["liberationmono"] = ["Courier New", "Cousine", "Courier"],
        ["cousine"] = ["Courier New", "Liberation Mono", "Courier"],
        ["couriernew"] = ["Liberation Mono", "Cousine", "Courier"],
        ["courier"] = ["Courier New", "Liberation Mono", "Cousine"],
        ["carlito"] = ["Calibri"],
        ["calibri"] = ["Carlito"],
        ["caladea"] = ["Cambria"],
        ["cambria"] = ["Caladea"],
    };

    /// <summary>A font name as a key: lower case, no spaces, no PostScript suffixes ("ArialMT", "TimesNewRomanPSMT").</summary>
    private static string FontKey(string name) =>
        PostScriptSuffix().Replace(string.Concat(name.Where(char.IsLetterOrDigit)), "").ToLowerInvariant();

    /// <summary>
    /// x2t names a font it has, not the PDF's ("Helvetica" for an embedded Liberation Sans), and other fonts have other
    /// widths, so the text reflows. Each run style gets the font its characters were drawn with in the PDF: installed,
    /// as it is; else a metric-compatible installed one; else the PDF's name (the renderer substitutes).
    /// </summary>
    private static IEnumerable<string> RestoreFonts(XDocument styles, Dictionary<string, Dictionary<string, int>> uses, ISet<string>? installed)
    {
        var byKey = (installed ?? new HashSet<string>()).GroupBy(FontKey).ToDictionary(g => g.Key, g => g.First());
        string Name(string pdfFamily)
        {
            var key = FontKey(pdfFamily);
            if (byKey.TryGetValue(key, out var exact))
                return exact;
            if (Equivalents.TryGetValue(key, out var others) && others.FirstOrDefault(o => byKey.ContainsKey(FontKey(o))) is { } equivalent)
                return byKey[FontKey(equivalent)];
            return SpacedName().Replace(pdfFamily, "$1 $2");
        }
        foreach (var (styleId, families) in uses)
        {
            var style = styles.Root?.Elements(W + "style").FirstOrDefault(s => s.Attribute(W + "styleId")?.Value == styleId);
            var fonts = style?.Element(W + "rPr")?.Element(W + "rFonts");
            var current = fonts?.Attribute(W + "ascii")?.Value;
            if (fonts is null || current is null)
                continue;
            // The face (after "|") is the runs' own (see Faces).
            var family = families.OrderByDescending(f => f.Value).First().Key.Split('|')[0];
            if (SymbolFont().IsMatch(current) && !SymbolFont().IsMatch(family))
                continue; // a symbol font draws bullets from its own code points: never a text font instead
            if (family.Length == 0 || FontKey(family) == FontKey(current))
                continue;
            var name = Name(family);
            if (FontKey(name) == FontKey(current))
                continue;
            foreach (var slot in new[] { "ascii", "hAnsi", "cs" })
                fonts.SetAttributeValue(W + slot, name);
            yield return FontKey(name) == FontKey(family)
                ? $"{current} → {name} (the PDF's font)"
                : $"{current} → {name} (metric-compatible with the PDF's {family}, which isn't installed)";
        }
    }

    /// <summary>Room (points) added below the lowest line for the page's bottom margin (see the sectPr).</summary>
    private const double BottomSlack = 4;

    /// <summary>The order of w:settings' children in the schema (CT_Settings): a setting goes before any later one.</summary>
    private static readonly string[] SettingsOrder =
    [
        "writeProtection", "view", "zoom", "removePersonalInformation", "removeDateAndTime", "doNotDisplayPageBoundaries",
        "displayBackgroundShape", "printPostScriptOverText", "printFractionalCharacterWidth", "printFormsData", "embedTrueTypeFonts",
        "embedSystemFonts", "saveSubsetFonts", "saveFormsData", "mirrorMargins", "alignBordersAndEdges", "bordersDoNotSurroundHeader",
        "bordersDoNotSurroundFooter", "gutterAtTop", "hideSpellingErrors", "hideGrammaticalErrors", "activeWritingStyle",
        "proofState", "formsDesign", "attachedTemplate", "linkStyles", "stylePaneFormatFilter", "stylePaneSortMethod",
        "documentType", "mailMerge", "revisionView", "trackRevisions", "doNotTrackMoves", "doNotTrackFormatting",
        "documentProtection", "autoFormatOverride", "styleLockTheme", "styleLockQFSet", "defaultTabStop", "autoHyphenation",
        "consecutiveHyphenLimit", "hyphenationZone", "doNotHyphenateCaps", "showEnvelope", "summaryLength", "clickAndTypeStyle",
        "defaultTableStyle", "evenAndOddHeaders", "bookFoldRevPrinting", "bookFoldPrinting", "bookFoldPrintingSheets",
        "drawingGridHorizontalSpacing", "drawingGridVerticalSpacing", "displayHorizontalDrawingGridEvery",
        "displayVerticalDrawingGridEvery", "doNotUseMarginsForDrawingGridOrigin", "drawingGridHorizontalOrigin",
        "drawingGridVerticalOrigin", "doNotShadeFormData", "noPunctuationKerning", "characterSpacingControl", "printTwoOnOne",
        "strictFirstAndLastChars", "noLineBreaksAfter", "noLineBreaksBefore", "savePreviewPicture", "doNotValidateAgainstSchema",
        "saveInvalidXml", "ignoreMixedContent", "alwaysShowPlaceholderText", "doNotDemarcateInvalidXml", "saveXmlDataOnly",
        "useXSLTWhenSaving", "saveThroughXslt", "showXMLTags", "alwaysMergeEmptyNamespace", "updateFields", "hdrShapeDefaults",
        "footnotePr", "endnotePr", "compat", "docVars", "rsids", "mathPr", "attachedSchema", "themeFontLang", "clrSchemeMapping",
        "doNotIncludeSubdocsInStats", "doNotAutoCompressPictures", "forceUpgrade", "captions", "readModeInkLockDown",
        "smartTagType", "schemaLibrary", "shapeDefaults", "doNotEmbedSmartTags", "decimalSymbol", "listSeparator",
    ];

    /// <summary>Adds an empty on/off setting (w:name) in its schema place, unless it is there already.</summary>
    private static void AddSetting(XDocument settings, string name)
    {
        if (settings.Root!.Element(W + name) is not null)
            return;
        var index = Array.IndexOf(SettingsOrder, name);
        var later = settings.Root.Elements().FirstOrDefault(e => Array.IndexOf(SettingsOrder, e.Name.LocalName) > index);
        if (later is null)
            settings.Root.Add(new XElement(W + name));
        else
            later.AddBeforeSelf(new XElement(W + name));
    }

    /// <summary>w:pPr's children in schema order (CT_PPr); Word rejects a document with them out of order.</summary>
    private static readonly string[] ParagraphOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers", "pBdr",
        "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN",
        "bidi", "adjustRightInd", "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc",
        "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr", "sectPr", "pPrChange",
    ];

    /// <summary>w:rPr's children in schema order (CT_RPr).</summary>
    private static readonly string[] RunOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow", "emboss",
        "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz", "szCs",
        "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout",
        "specVanish", "oMath",
    ];

    /// <summary>Puts <paramref name="properties"/>' children in <paramref name="order"/> (unknown ones last, kept).</summary>
    private static void Order(XElement properties, string[] order)
    {
        var children = properties.Elements().ToList();
        int Rank(XElement e) => Array.IndexOf(order, e.Name.LocalName) is var i and >= 0 ? i : order.Length;
        if (children.Zip(children.Skip(1)).All(p => Rank(p.First) <= Rank(p.Second)))
            return;
        foreach (var child in children)
            child.Remove();
        properties.Add(children.OrderBy(Rank));
    }

    /// <summary>Header and footer parts to add: part name → (reference type, header or footer, content).</summary>
    internal sealed record HeaderPart(string Name, string Type, bool Footer, XElement Content);

    /// <summary>Rebuilds the body; returns the header and footer parts its sectPr refers to (relationship ids rIdEasyH*).</summary>
    internal static List<HeaderPart> Rebuild(XDocument document, (double Left, double Right)? textColumn = null, IReadOnlyList<List<Glyph>>? glyphs = null) =>
        Rebuild(document, textColumn, out _, glyphs, null);

    /// <summary>
    /// As above; <paramref name="hyphenated"/> tells whether the PDF was set with hyphenation (words rejoined).
    /// <paramref name="glyphs"/> are the PDF's glyphs per page (<see cref="Glyphs"/>): x2t's boxes run wider and taller
    /// than their text, so every line is snapped to the glyphs it holds.
    /// </summary>
    /// <param name="fontUses">Filled with, per run style, the PDF fonts its characters were drawn with (and how many).</param>
    internal static List<HeaderPart> Rebuild(XDocument document, (double Left, double Right)? textColumn, out bool hyphenated,
        IReadOnlyList<List<Glyph>>? glyphs, Dictionary<string, Dictionary<string, int>>? fontUses)
    {
        var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("No w:body.");
        var size = body.Descendants(W + "pgSz").FirstOrDefault();
        var (pageWidth, pageHeight) = (Number(size, "w", 11906) / 20.0, Number(size, "h", 16838) / 20.0);

        var allItems = Pages(body).Select((p, i) => Items(p, pageWidth, glyphs is not null && i < glyphs.Count ? glyphs[i] : null)).ToList();
        // Shapes don't take part in the flow: they shade the paragraphs they lie behind, or float behind the text.
        var shapes = allItems.Select(p => p.Where(i => i.Kind == Kind.Shape).ToList()).ToList();
        var pages = allItems.Select(p => p.Where(i => i.Kind != Kind.Shape).ToList()).ToList();
        if (fontUses is not null)
        {
            // A run of Symbol-font code points (a bullet) was drawn with the PDF's symbol font; x2t names a stand-in.
            static bool Symbols(string text) => text.Length > 0 && text.All(c => c is >= '\uF000' and <= '\uF0FF');
            var symbolFamily = glyphs?.SelectMany(g => g).Where(g => Symbols(g.Value)).GroupBy(g => g.Font).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            foreach (var item in pages.SelectMany(p => p).Where(i => i.Font is not null && i.Paragraph is not null))
                foreach (var run in item.Paragraph!.Elements(W + "r"))
                {
                    var text = string.Concat(run.Elements(W + "t").Select(t => t.Value));
                    // Runs with text take the item's font (its letters'); a space beside them says nothing.
                    var family = text.Any(char.IsLetterOrDigit) ? item.Font : Symbols(text) ? symbolFamily : null;
                    if (family is not null && run.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val")?.Value is { } styleId)
                    {
                        var counts = fontUses.TryGetValue(styleId, out var c) ? c : fontUses[styleId] = [];
                        counts[family] = counts.GetValueOrDefault(family) + text.Length;
                    }
                }
        }
        var rows = pages.Select(p => Rows(p)).ToList();

        // Running headers and footers: the first or last line near the top or bottom edge of a page that is its page
        // number alone, or whose text (digits aside) recurs on other pages ("2  Using the … Template").
        bool NearEdge(Row row) => row.Top < pageHeight * 0.2 || row.Bottom > pageHeight * 0.8;
        var edgeRows = rows.Select(page => page.Where(r => r.Kind == Kind.Text).ToList())
            .Select(text => new[] { text.FirstOrDefault(), text.LastOrDefault() }.OfType<Row>().Where(NearEdge).Distinct().ToList()).ToList();
        var recurring = edgeRows.SelectMany(c => c.Select(Running).Distinct()).GroupBy(k => k)
            .Where(g => g.Key.Any(char.IsLetter) && g.Count() >= Math.Max(2, 0.25 * rows.Count)).Select(g => g.Key).ToHashSet();
        // Only numbers changing is a running line when it is no taller than the body text: "Section 1 Results",
        // "Section 2 Results" in a heading's size at the top of each page are headings. They left the body for one
        // header that showed "Section <page> Results" on every page. The same words on several pages are running
        // text at any size (a letterhead).
        var exactly = edgeRows.SelectMany(c => c.Select(r => r.Text.Trim()).Distinct()).GroupBy(k => k)
            .Where(g => g.Count() >= Math.Max(2, 0.25 * rows.Count)).Select(g => g.Key).ToHashSet();
        var bodyHeight = rows.SelectMany(r => r).Where(r => r.Kind == Kind.Text && !r.Flowing).Select(r => Math.Round(r.Height * 2) / 2)
            .GroupBy(h => h).OrderByDescending(g => g.Count()).Select(g => g.Key).DefaultIfEmpty(double.MaxValue).First();
        bool RunningLine(Row row) => recurring.Contains(Running(row)) && (row.Height <= bodyHeight * 1.15 || exactly.Contains(row.Text.Trim()));
        // A page may have both: a running header and a running footer.
        var heads = new Dictionary<int, (Row Row, bool Top)>();
        var foots = new Dictionary<int, (Row Row, bool Top)>();
        for (var p = 0; p < rows.Count; p++)
            foreach (var candidate in edgeRows[p])
                if (PageNumber().IsMatch(candidate.Text.Trim()) && int.Parse(candidate.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture) == p + 1
                    || RunningLine(candidate))
                {
                    var atTop = candidate.Top < pageHeight * 0.2;
                    if ((atTop ? heads : foots).ContainsKey(p))
                        continue;
                    (atTop ? heads : foots)[p] = (candidate, atTop);
                    rows[p].Remove(candidate);
                }

        // The text block: margins of the page.
        // The text block: the most common left and right edges of full-width lines. Extremes would follow a stray item
        // or a line box's trailing space; the mode follows the justified body text.
        var bodyRows = rows.SelectMany(r => r).Where(r => !r.Flowing && r.Kind == Kind.Text).ToList();
        var widest = bodyRows.Count > 0 ? bodyRows.Max(r => r.Right - r.Left) : 0;
        var full = bodyRows.Where(r => r.Right - r.Left > 0.75 * widest).ToList();
        double Mode(IEnumerable<double> values) =>
            values.GroupBy(v => Math.Round(v * 2) / 2).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var left = textColumn?.Left ?? (full.Count > 0 ? Mode(full.Select(r => r.Left)) : 72);
        var right = textColumn?.Right ?? (full.Count > 0 ? Mode(full.Select(r => r.Right)) : pageWidth - 72);
        var top = rows.Where(r => r.Count > 0).Select(r => r.Min(x => x.Top)).DefaultIfEmpty(72).Min();
        // The bottom margin from the lowest line of all pages; a document of one page ends wherever its text does (a
        // résumé, a letter), which says nothing about the margin: there it mirrors the top margin.
        var bottom = rows.Count(r => r.Count > 0) > 1
            ? rows.Where(r => r.Count > 0).Max(r => r.Max(x => x.Bottom))
            : Math.Max(rows.SelectMany(r => r).Select(x => x.Bottom).DefaultIfEmpty(0).Max(), pageHeight - top - BottomSlack);
        var column = (Left: left, Right: right);

        var words = new Vocabulary(rows.SelectMany(r => r).Select(r => r.Text));
        var blocks = new List<Block>();
        var currentColumns = 1;
        var currentGutter = 0.0;
        for (var p = 0; p < rows.Count; p++)
        {
            var pageBlocks = new List<Block>();
            if (Gutter(rows[p], column) is not { } gutter)
            {
                if (currentColumns != 1)
                    pageBlocks.Add(SectionBreak(currentColumns, 0, p));
                currentColumns = 1;
                pageBlocks.AddRange(Blocks(rows[p], p, column, words, pageWidth));
            }
            else
            {
                // Bands: rows across the gutter (a title, an introduction, a wide figure) are one column; the rows
                // between them are two, the left one first, then a column break, then the right one.
                foreach (var (split, band) in Bands(rows[p], gutter))
                {
                    var columns = split ? 2 : 1;
                    if (columns != currentColumns && blocks.Count + pageBlocks.Count > 0)
                        pageBlocks.Add(SectionBreak(currentColumns, currentColumns == 2 ? gutter.Right - gutter.Left : 0, p));
                    currentColumns = columns;
                    if (!split)
                    {
                        pageBlocks.AddRange(Blocks(band, p, column, words, pageWidth));
                        continue;
                    }
                    // Each item to the side its middle is on: a box may reach into the gutter a little.
                    var middle = (gutter.Left + gutter.Right) / 2;
                    var leftRows = band.Select(r => Part(r, i => (i.Left + i.Right) / 2 < middle)).OfType<Row>().ToList();
                    var rightRows = band.Select(r => Part(r, i => (i.Left + i.Right) / 2 >= middle)).OfType<Row>().ToList();
                    // Both columns start where the band does: below the last block before it, or at the top margin.
                    var bandStart = pageBlocks.LastOrDefault(b => b.Columns == 0)?.Bottom ?? top;
                    pageBlocks.AddRange(Blocks(leftRows, p, (column.Left, gutter.Left), words, pageWidth));
                    var rightBlocks = Blocks(rightRows, p, (gutter.Right, column.Right), words, pageWidth).ToList();
                    if (rightBlocks.Count > 0)
                    {
                        var breaker = new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "before", 0),
                            new XAttribute(W + "after", 0), new XAttribute(W + "line", 20), new XAttribute(W + "lineRule", "exact"))),
                            new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "column"))));
                        pageBlocks.Add(new Block([breaker], bandStart, bandStart, p, Columns: -1));
                        pageBlocks.AddRange(rightBlocks);
                    }
                }
                if (currentColumns == 2)
                    currentGutter = gutter.Right - gutter.Left;
            }
            Decorate(pageBlocks, shapes[p], rows[p]);
            blocks.AddRange(pageBlocks);
        }

        // Spacing: the gap from the previous block's end on the same page; a block that opens a page keeps its offset
        // from the top margin.
        var output = new List<XElement>();
        Block? previous = null;
        var breaks = new List<(XElement Paragraph, int Columns, double Gutter)>();
        foreach (var block in blocks)
        {
            if (block.Columns < 0)
            {
                // The column break: no spacing of its own; the right column's first block measures from the band start.
                output.AddRange(block.Elements);
                previous = block;
                continue;
            }
            if (block.Columns > 0)
            {
                // The section so far ends in the last paragraph before here (an empty one after a table).
                if (output.LastOrDefault() is not { } last || last.Name != W + "p")
                    output.Add(last = Spacer());
                breaks.Add((last, block.Columns, block.Gutter));
                continue;
            }
            // A paragraph whose last line is full, followed on the next page by an unindented line of the same text: one
            // paragraph running over the page break.
            if (previous?.Lines is { } before && block.Lines is { } after && previous.Page != block.Page && RunsOn(before, after))
            {
                var joined = Paragraph([.. before, .. after], previous.Page, column, words, pageWidth);
                var spacing = previous.Elements[0].Element(W + "pPr")?.Element(W + "spacing")?.Attribute(W + "before")?.Value;
                output[^1] = joined.Elements[0];
                if (spacing is not null)
                    joined.Elements[0].Element(W + "pPr")!.Element(W + "spacing")!.SetAttributeValue(W + "before", spacing);
                previous = joined with { Page = block.Page, Top = previous.Top, Bottom = block.Bottom };
                continue;
            }
            // The right column starts again at the band's top: its gap is from the band's start, not the left column's end.
            var gap = previous is null || previous.Page != block.Page ? block.Top - top : block.Top - previous.Bottom;
            if (block.Elements[0].Name == W + "p")
            {
                SetSpacing(block.Elements[0], before: Math.Max(0, gap));
                // A page that ended three lines or more above the bottom margin before a heading (one short line) kept it
                // with its text on the next page: here too.
                if (previous is not null && previous.Page != block.Page && previous.Bottom < bottom - 3 * (previous.Lines?[^1].Height ?? 12)
                    && block.Lines is [var headingLine] && headingLine.Right - headingLine.Left < 0.6 * (column.Right - column.Left))
                    block.Elements[0].Element(W + "pPr")!.AddFirst(new XElement(W + "pageBreakBefore"));
            }
            output.AddRange(block.Elements);
            previous = block;
        }

        // A first page without one of them (a title page) has its own, empty or not.
        var titlePage = rows.Count > 1 && (heads.Count >= 2 && !heads.ContainsKey(0) || foots.Count >= 2 && !foots.ContainsKey(0));
        var parts = Headers(heads, column, titlePage, rows.Count).Concat(Headers(foots, column, titlePage, rows.Count)).ToList();
        var section = new XElement(W + "sectPr",
            parts.Select(h => new XElement(W + (h.Footer ? "footerReference" : "headerReference"),
                new XAttribute(W + "type", h.Type), new XAttribute(R + "id", "rIdEasy" + Path.GetFileNameWithoutExtension(h.Name)))),
            new XElement(W + "pgSz", new XAttribute(W + "w", Twips(pageWidth)), new XAttribute(W + "h", Twips(pageHeight))),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", Twips(top)), new XAttribute(W + "right", Twips(Math.Max(0, pageWidth - right))),
                // A few points below the lowest line box: lines are laid out by their pitch, a little taller than their
                // boxes, and a last line that overruns the margin by a fraction of a point moves to the next page. Far
                // less than a line, so no extra line fits.
                new XAttribute(W + "bottom", Twips(Math.Max(0, pageHeight - bottom - BottomSlack))), new XAttribute(W + "left", Twips(left)),
                new XAttribute(W + "header", Twips(heads.Values.Select(n => n.Row.Top).DefaultIfEmpty(top / 2).Min())),
                new XAttribute(W + "footer", Twips(foots.Values.Select(n => pageHeight - n.Row.Bottom).DefaultIfEmpty(36).Min())),
                new XAttribute(W + "gutter", 0)),
            currentColumns == 2 ? new XElement(W + "cols", new XAttribute(W + "num", 2), new XAttribute(W + "space", Twips(currentGutter))) : null,
            titlePage && parts.Count > 0 ? new XElement(W + "titlePg") : null);
        // Every section break carries the page and its headers (a section without them has none), its own column count,
        // and starts the next section on the same page.
        foreach (var (paragraph, columns, gutterWidth) in breaks)
        {
            var sectPr = new XElement(section);
            sectPr.Elements(W + "cols").Remove();
            var anchor = sectPr.Element(W + "titlePg") ?? null;
            var cols = new XElement(W + "cols", new XAttribute(W + "num", columns), new XAttribute(W + "space", Twips(gutterWidth)));
            if (anchor is not null)
                anchor.AddBeforeSelf(cols);
            else
                sectPr.Add(cols);
            sectPr.Element(W + "pgSz")!.AddBeforeSelf(new XElement(W + "type", new XAttribute(W + "val", "continuous")));
            var pPr = paragraph.Element(W + "pPr") ?? new XElement(W + "pPr");
            if (paragraph.Element(W + "pPr") is null)
                paragraph.AddFirst(pPr);
            pPr.Add(sectPr);
        }
        // The section after the last break starts on the same page too.
        if (breaks.Count > 0)
            section.Element(W + "pgSz")!.AddBeforeSelf(new XElement(W + "type", new XAttribute(W + "val", "continuous")));
        body.RemoveNodes();
        body.Add(output, section);
        foreach (var properties in body.Descendants().Where(e => e.Name == W + "pPr" || e.Name == W + "rPr").ToList())
            Order(properties, properties.Name == W + "pPr" ? ParagraphOrder : RunOrder);
        hyphenated = words.Joined >= 3;
        return parts;
    }

    /// <summary>The body split at its section breaks: one list of elements per PDF page.</summary>
    private static List<List<XElement>> Pages(XElement body)
    {
        var pages = new List<List<XElement>> { new() };
        foreach (var child in body.Elements())
        {
            if (child.Name == W + "sectPr")
                continue;
            pages[^1].Add(child);
            if (child.Element(W + "pPr")?.Element(W + "sectPr") is not null)
                pages.Add([]);
        }
        return pages.Where(p => p.Count > 0).ToList();
    }

    /// <summary>The page's items: anchored lines, pictures and rules, and its positioned paragraphs (top estimated).</summary>
    private static List<Item> Items(List<XElement> page, double pageWidth, List<Glyph>? glyphs)
    {
        var items = new List<Item>();
        var lines = glyphs is null ? [] : GlyphLines(glyphs);
        var used = new HashSet<List<Glyph>>();
        var y = 0.0;
        foreach (var element in page.Where(e => e.Name == W + "p"))
        {
            foreach (var anchor in element.Descendants(Wp + "anchor"))
                items.AddRange(Anchored(anchor));
            if (element.Descendants(Wp + "anchor").Any() || element.Element(W + "pPr")?.Element(W + "sectPr") is not null)
                continue;
            // A positioned paragraph: one line at "space before" below the previous one, its height its exact line.
            var pPr = element.Element(W + "pPr");
            var spacing = pPr?.Element(W + "spacing");
            var line = Number(spacing, "line", 240) / 20.0;
            var lineTop = y + Number(spacing, "before", 0) / 20.0;
            y = lineTop + line;
            if (IsBlank(string.Concat(element.Descendants(W + "t").Select(t => t.Value))))
                continue;
            var ind = pPr?.Element(W + "ind");
            var indLeft = Number(ind, "left", 0) / 20.0;
            var indRight = Number(ind, "right", 0) / 20.0;
            var positioned = new Item(Kind.Text, indLeft, lineTop, Math.Max(10, pageWidth - indLeft - indRight), line, new XElement(element), null,
                Flowing: true, Centered: Math.Abs(indLeft - indRight) < 3, Baseline: lineTop + 0.8 * line);
            // Its place on the page was only estimated: the PDF line with the same text (the first not taken) says.
            var key = Key(positioned.Text);
            var match = key.Length == 0 ? null : lines.FirstOrDefault(l => !used.Contains(l)
                && Key(string.Concat(l.Select(g => g.Value))).StartsWith(key[..Math.Min(key.Length, 16)], StringComparison.Ordinal));
            if (match is not null)
                used.Add(match);
            items.Add(match is null ? positioned : Snap(positioned, match));
        }
        if (glyphs is not null)
            items = SnapBoxes(items, glyphs);
        return items.OrderBy(i => i.Top).ThenBy(i => i.Left).ToList();
    }

    /// <summary>
    /// Snaps the line boxes to the glyphs they hold. Boxes of one line overlap at word boundaries, so each glyph goes to
    /// one box only: the one it lies deepest inside (farthest from that box's nearer edge).
    /// </summary>
    private static List<Item> SnapBoxes(List<Item> items, List<Glyph> glyphs)
    {
        var boxes = items.Where(i => i.Kind == Kind.Text && !i.Flowing).ToList();
        var assigned = boxes.ToDictionary(b => b, _ => new List<Glyph>());
        foreach (var glyph in glyphs)
        {
            var center = (glyph.Left + glyph.Right) / 2;
            var best = boxes.Where(b => center > b.Left - 1 && center < b.Right + 1 && glyph.Baseline > b.Top - 1 && glyph.Baseline < b.Bottom + 0.35 * b.Height)
                .OrderByDescending(b => Math.Min(center - b.Left, b.Right - center)).FirstOrDefault();
            if (best is not null)
                assigned[best].Add(glyph);
        }
        return items.SelectMany(i => assigned.TryGetValue(i, out var held) ? SplitAtGaps(i, held) : [i]).ToList();
    }

    /// <summary>
    /// A box cut where its glyphs leave a gap wider than 2.5 text sizes: x2t boxes a line with a part pushed to the right
    /// ("Education … 2021 – 2023") as one, hiding the gap. Cut only when the glyphs spell the box's text.
    /// </summary>
    private static IEnumerable<Item> SplitAtGaps(Item item, List<Glyph> held)
    {
        var ink = held.Where(g => !g.Blank).OrderBy(g => g.Left).ToList();
        var parts = new List<List<Glyph>>();
        foreach (var glyph in ink)
        {
            if (parts.Count == 0 || glyph.Left - parts[^1][^1].Right > 2.5 * Math.Max(1, glyph.Size))
                parts.Add([]);
            parts[^1].Add(glyph);
        }
        var counts = parts.Select(part => Key(string.Concat(part.Select(g => g.Value))).Length).ToList();
        if (parts.Count < 2 || item.Paragraph is null || counts.Sum() != Key(item.Text).Length || counts.Any(c => c == 0))
            return [Snap(item, held)];
        var result = new List<Item>();
        var consumed = 0;
        foreach (var (part, count) in parts.Zip(counts))
        {
            var range = held.Where(g => g.Left >= part[0].Left - 0.01 && g.Right <= part[^1].Right + 0.01 || g.Blank && g.Left > part[0].Left && g.Left < part[^1].Right).ToList();
            result.Add(Snap(item with { Paragraph = Letters(item.Paragraph, consumed, consumed + count) }, range));
            consumed += count;
        }
        return result;
    }

    /// <summary>
    /// A copy of the paragraph with only the text from letter (or digit) <paramref name="from"/> to <paramref name="to"/>;
    /// what follows a part's last letter up to the next part stays with it.
    /// </summary>
    private static XElement Letters(XElement paragraph, int from, int to)
    {
        var copy = new XElement(paragraph);
        var seen = 0;
        foreach (var t in copy.Descendants(W + "t").ToList())
        {
            var kept = new System.Text.StringBuilder();
            foreach (var c in t.Value)
            {
                var letter = char.IsLetterOrDigit(c);
                // Keep from the first letter of the part to the next part's first letter.
                if (letter ? seen >= from && seen < to : seen > from && seen <= to || seen == 0 && from == 0)
                    kept.Append(c);
                if (letter)
                    seen++;
            }
            if (kept.Length == 0)
                t.Remove();
            else
                t.Value = kept.ToString();
        }
        return copy;
    }

    /// <summary>An anchored drawing as items: a text line, a picture, or the horizontal rules a shape draws.</summary>
    private static IEnumerable<Item> Anchored(XElement anchor)
    {
        if (!long.TryParse(anchor.Element(Wp + "positionH")?.Element(Wp + "posOffset")?.Value, out var x)
            || !long.TryParse(anchor.Element(Wp + "positionV")?.Element(Wp + "posOffset")?.Value, out var y))
            yield break;
        var extent = anchor.Element(Wp + "extent");
        var (w, h) = (Plain(extent, "cx") / 12700.0, Plain(extent, "cy") / 12700.0);
        var (left, top) = (x / 12700.0, y / 12700.0);
        if (anchor.Descendants(Wps + "txbx").FirstOrDefault()?.Element(W + "txbxContent")?.Element(W + "p") is { } paragraph)
        {
            if (!IsBlank(string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value))))
                yield return new Item(Kind.Text, left, top, w, h, new XElement(paragraph), anchor);
            yield break;
        }
        if (anchor.Descendants(Pic + "pic").Any())
        {
            yield return new Item(Kind.Picture, left, top, w, h, null, anchor);
            yield break;
        }
        // Lines drawn as thin filled rectangles (table borders): horizontal rules, and vertical ones (a rule with a height).
        if (Fill(anchor) is not null && ThinBoxes(anchor, left, top, w, h) is { Count: > 0 } thin)
        {
            foreach (var box in thin.Where(b => b.Width >= b.Height ? b.Width >= 20 : b.Height >= 3))
                yield return box.Width >= box.Height
                    ? new Item(Kind.Rule, box.Left, box.Top + box.Height / 2, box.Width, 0, null, anchor)
                    : new Item(Kind.Rule, box.Left + box.Width / 2, box.Top, 0, box.Height, null, anchor);
            yield break;
        }
        // A filled area (a band behind a line, a banner) is a shape, not the rules its edges would make; an outline or a
        // thin line gives its horizontal rules; anything else drawn is kept as a shape.
        var filled = Fill(anchor) is not null && h > 2;
        var rules = filled ? [] : Rules(anchor, left, top, w, h).ToList();
        foreach (var (ruleLeft, ruleTop, ruleWidth) in rules)
            yield return new Item(Kind.Rule, ruleLeft, ruleTop, ruleWidth, 0, null, anchor);
        if (rules.Count == 0 && (w > 1 || h > 1))
            yield return new Item(Kind.Shape, left, top, w, h, null, anchor);
    }

    /// <summary>
    /// The boxes of a shape's straight sub-paths, on the page, when every one is at most 1.5 pt thin (lines drawn as
    /// filled rectangles); null for anything else (a curve, a thicker area).
    /// </summary>
    private static List<(double Left, double Top, double Width, double Height)>? ThinBoxes(XElement anchor, double left, double top, double width, double height)
    {
        var boxes = new List<(double Left, double Top, double Width, double Height)>();
        foreach (var path in anchor.Descendants(A + "path").Where(p => p.Parent?.Name == A + "pathLst"))
        {
            var (pw, ph) = (Plain(path, "w"), Plain(path, "h"));
            var points = new List<(double X, double Y)>();
            void Close()
            {
                if (points.Count > 0)
                    boxes.Add((points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y)));
                points.Clear();
            }
            foreach (var step in path.Elements())
            {
                if (step.Name == A + "moveTo")
                    Close();
                else if (step.Name != A + "lnTo" && step.Name != A + "close")
                    return null; // a curve
                if (step.Element(A + "pt") is { } pt)
                    points.Add((left + (pw > 0 ? Plain(pt, "x") * width / pw : 0), top + (ph > 0 ? Plain(pt, "y") * height / ph : 0)));
            }
            Close();
        }
        return boxes.All(b => Math.Min(b.Width, b.Height) <= 1.5) ? boxes : null;
    }

    /// <summary>The solid fill colour of a shape (RRGGBB), or null.</summary>
    private static string? Fill(XElement anchor) =>
        anchor.Descendants(Wps + "spPr").Elements(A + "solidFill").Elements(A + "srgbClr").Attributes("val").FirstOrDefault()?.Value;

    /// <summary>
    /// The horizontal lines a shape draws (table borders, section rules), at least 20 pt long: the segments of its
    /// custom path (x2t draws several rules with one path), else the shape itself when it is a thin, wide line.
    /// </summary>
    private static IEnumerable<(double Left, double Top, double Width)> Rules(XElement anchor, double left, double top, double width, double height)
    {
        var found = new List<(double Left, double Top, double Width)>();
        foreach (var path in anchor.Descendants(A + "path").Where(p => p.Parent?.Name == A + "pathLst"))
        {
            var (pw, ph) = (Plain(path, "w"), Plain(path, "h"));
            double X(XElement pt) => left + (pw > 0 ? Plain(pt, "x") * width / pw : 0);
            double Y(XElement pt) => top + (ph > 0 ? Plain(pt, "y") * height / ph : 0);
            XElement? from = null;
            foreach (var step in path.Elements())
            {
                var point = step.Element(A + "pt");
                if (point is null)
                    continue;
                if (step.Name == A + "lnTo" && from is not null && Math.Abs(Y(from) - Y(point)) < 0.5 && Math.Abs(X(from) - X(point)) >= 20)
                    found.Add((Math.Min(X(from), X(point)), Y(point), Math.Abs(X(from) - X(point))));
                from = point;
            }
        }
        if (found.Count == 0 && height <= 2 && width >= 20)
            found.Add((left, top, width));
        // A line drawn twice (x2t repeats each segment) is one rule.
        return found.GroupBy(r => Math.Round(r.Top * 2)).Select(g => g.First()).OrderBy(r => r.Top);
    }

    /// <summary>
    /// The item moved onto the glyphs it holds: their left and right edges and baseline (its height stays, its top
    /// follows the baseline), the width of its first word and where its second starts.
    /// </summary>
    private static Item Snap(Item item, List<Glyph> glyphs)
    {
        var ink = glyphs.Where(g => !g.Blank).OrderBy(g => g.Left).ToList();
        if (ink.Count == 0)
            return item;
        var size = ink.Max(g => g.Size);
        var main = ink.Where(g => g.Size >= 0.8 * size).Select(g => g.Baseline).Order().ToList();
        var baseline = main[main.Count / 2];
        // Words: glyphs split at a space glyph or a gap wider than a fifth of the text size.
        var ordered = glyphs.OrderBy(g => g.Left).ToList();
        var starts = new List<double> { ink[0].Left };
        var firstEnd = ink[0].Right;
        var inFirst = true;
        for (var i = 1; i < ordered.Count; i++)
        {
            var (previous, glyph) = (ordered[i - 1], ordered[i]);
            if (glyph.Blank)
                continue;
            if (previous.Blank || glyph.Left - previous.Right > 0.2 * Math.Max(1, glyph.Size))
            {
                inFirst = false;
                starts.Add(glyph.Left);
            }
            else if (inFirst)
                firstEnd = glyph.Right;
        }
        if (item.Paragraph is { } paragraph)
        {
            Ligatures(paragraph, ink);
            Faces(paragraph, ink);
        }
        var (left, right) = (ink.Min(g => g.Left), ink.Max(g => g.Right));
        return item with
        {
            Left = left,
            Width = Math.Max(1, right - left),
            Top = baseline - 0.8 * item.Height,
            Baseline = baseline,
            FirstWord = firstEnd - left,
            SecondWord = starts.Count > 1 ? starts[1] : right,
            Font = ink.GroupBy(g => g.Font).OrderByDescending(g => g.Count()).First().Key,
        };
    }

    /// <summary>
    /// Ligatures as letters: "ﬁ" (U+FB01) as "fi", and the letters x2t drops from a ligature glyph ("tt" drawn as one
    /// glyph comes out as one "t": "commited") put back from the glyph.
    /// </summary>
    private static void Ligatures(XElement paragraph, List<Glyph> ink)
    {
        var next = 0;
        foreach (var t in paragraph.Descendants(W + "t"))
        {
            var text = new System.Text.StringBuilder();
            var value = string.Concat(t.Value.Select(c => c is >= '\uFB00' and <= '\uFB06' ? c.ToString().Normalize(System.Text.NormalizationForm.FormKC) : c.ToString()));
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                text.Append(c);
                if (char.IsWhiteSpace(c))
                    continue;
                for (var k = next; k < Math.Min(next + 3, ink.Count); k++)
                {
                    var glyph = ink[k].Value.Normalize(System.Text.NormalizationForm.FormKC);
                    if (!glyph.StartsWith(c))
                        continue;
                    next = k + 1;
                    // A glyph of several letters: the text has them all, or gets the missing ones.
                    var rest = glyph[1..];
                    if (rest.Length > 0 && rest.All(char.IsLetter))
                    {
                        if (string.CompareOrdinal(value, i + 1, rest, 0, rest.Length) == 0)
                        {
                            text.Append(rest);
                            i += rest.Length;
                        }
                        else
                            text.Append(rest);
                    }
                    break;
                }
            }
            t.Value = text.ToString();
        }
    }

    /// <summary>
    /// Bold and italic on the runs drawn with a bold or italic face: x2t names one style per family, regular and italic
    /// alike. A run's letters are matched to the glyphs in order; the face most of them have is the run's. Only added: a
    /// PDF can embolden a regular font, and x2t's bold stays.
    /// </summary>
    private static void Faces(XElement paragraph, List<Glyph> ink)
    {
        var next = 0;
        foreach (var run in paragraph.Elements(W + "r").ToList())
        {
            var faces = new List<string>();
            foreach (var c in string.Concat(run.Elements(W + "t").Select(t => t.Value)).Where(c => !char.IsWhiteSpace(c)))
                for (var k = next; k < Math.Min(next + 3, ink.Count); k++)
                    if (ink[k].Value.Contains(c, StringComparison.Ordinal))
                    {
                        faces.Add(ink[k].Font.Split('|') is [_, var face] ? face : "");
                        next = k + 1;
                        break;
                    }
            if (faces.Count == 0)
                continue;
            var most = faces.GroupBy(f => f).OrderByDescending(g => g.Count()).First().Key;
            if (most.Length == 0)
                continue;
            var rPr = run.Element(W + "rPr") ?? new XElement(W + "rPr");
            if (rPr.Parent is null)
                run.AddFirst(rPr);
            var anchor = rPr.Element(W + "rFonts") ?? rPr.Element(W + "rStyle");
            void Insert(XElement after, params XElement[] added)
            {
                if (after is null)
                    rPr.AddFirst(added);
                else
                    after.AddAfterSelf(added);
            }
            if (most.Contains('b') && rPr.Element(W + "b") is null)
                Insert(anchor!, new XElement(W + "b"), new XElement(W + "bCs"));
            if (most.Contains('i') && rPr.Element(W + "i") is null)
                Insert((rPr.Element(W + "bCs") ?? rPr.Element(W + "b") ?? anchor)!, new XElement(W + "i"), new XElement(W + "iCs"));
        }
    }

    /// <summary>The page's glyphs as lines: one baseline within a third of the text size, left to right.</summary>
    private static List<List<Glyph>> GlyphLines(List<Glyph> glyphs)
    {
        var lines = new List<List<Glyph>>();
        foreach (var glyph in glyphs.Where(g => !g.Blank).OrderBy(g => g.Baseline).ThenBy(g => g.Left))
        {
            if (lines.Count > 0 && Math.Abs(lines[^1][0].Baseline - glyph.Baseline) < 0.33 * Math.Max(1, glyph.Size))
                lines[^1].Add(glyph);
            else
                lines.Add([glyph]);
        }
        foreach (var line in lines)
            line.Sort((a, b) => a.Left.CompareTo(b.Left));
        return lines;
    }

    /// <summary>Only white space, counting U+F020: the space of Symbol and Wingdings, which they map into the Private Use Area.</summary>
    private static bool IsBlank(string text) => text.All(c => char.IsWhiteSpace(c) || c == '\uF020');

    /// <summary>A glyph's font as "family|face" (face "b", "i" or "bi"; no bar for a regular face).</summary>
    private static string Face(string fontName) =>
        WordFonts.Face(fontName) is { Length: > 0 } face ? $"{WordFonts.Family(fontName)}|{face}" : WordFonts.Family(fontName);

    /// <summary>Text for matching: letters and digits only.</summary>
    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit));

    /// <summary>The PDF's glyphs, page by page, in points from each page's top left.</summary>
    public static List<List<Glyph>> Glyphs(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return document.GetPages().Select(page => page.Letters.Select(l => new Glyph(
            l.StartBaseLine.X, l.EndBaseLine.X, page.Height - l.StartBaseLine.Y,
            l.PointSize > 1 ? l.PointSize : Math.Max(1, l.GlyphRectangle.Height * 1.4), l.Value, Face(l.FontName ?? ""))).ToList()).ToList();
    }

    /// <summary>
    /// Items into rows: an item joins a row when its middle lies within the row and it doesn't overlap the row's items
    /// (beyond the slight overlap of boxes of one line).
    /// Then a small item (sub- or superscript: under 80 % of the height of a line it overlaps) moves to the line it
    /// overlaps most; a script hanging below its line often reaches into the next one. Positioned paragraphs and other
    /// kinds are rows of their own.
    /// </summary>
    private static List<Row> Rows(List<Item> items)
    {
        var rows = new List<Row>();
        foreach (var item in items)
        {
            var middle = item.Top + item.Height / 2;
            // Boxes of one line may overlap a little (x2t's boxes run wider than their text); more than ~0.6 line
            // heights of overlap is another line drawn over this one.
            var slack = 0.6 * item.Height;
            // One line: the same baseline (within a third of the text height) when the glyphs are known, else the middle
            // of the box within the row.
            bool SameLine(Row r) => item.Baseline > 0 && r.Baseline > 0
                ? Math.Abs(item.Baseline - r.Baseline) < 0.3 * Math.Min(item.Height, r.Height)
                : middle > r.Top && middle < r.Bottom;
            var row = item.Kind == Kind.Text && !item.Flowing
                ? rows.LastOrDefault(r => r.Kind == Kind.Text && !r.Flowing && SameLine(r)
                    && !r.Items.Any(o => o.Left < item.Right - slack && item.Left < o.Right - slack))
                : null;
            if (row is null)
            {
                row = new Row();
                rows.Add(row);
            }
            row.Items.Add(item);
            row.Items.Sort((a, b) => a.Left.CompareTo(b.Left));
        }

        static double Overlap(Item item, Row row) => Math.Min(item.Bottom, row.Bottom) - Math.Max(item.Top, row.Top);
        // A script is shorter than the line it hangs on; a line beside a lone tall glyph (a bullet in a symbol font) is not
        // that glyph's script.
        static bool Scripts(Item item, Row row) => item.Height < 0.8 * row.Height
            && Key(item.Text).Length < Key(string.Concat(row.Items.Where(o => o != item).Select(o => o.Text))).Length
            // beside a taller item of the line, not across a column's gap
            && row.Items.Any(o => o != item && o.Height > item.Height && item.Left - o.Right < item.Height && o.Left - item.Right < item.Height
                && !Marker(o.Text) && (o.Baseline <= 0 || item.Baseline <= 0 || Math.Abs(item.Baseline - o.Baseline) < 0.6 * o.Height));
        foreach (var row in rows.ToList())
        {
            if (row.Items.Count == 0 || row.Kind != Kind.Text || row.Flowing)
                continue; // emptied: its items moved to a line they script
            foreach (var item in row.Items.ToList())
            {
                var host = rows.Where(r => r != row && r.Items.Count > 0 && r.Kind == Kind.Text && !r.Flowing && Scripts(item, r)
                        && Overlap(item, r) > 0 && item.Left >= r.Left - item.Height && item.Right <= r.Right + item.Height)
                    .OrderByDescending(r => Overlap(item, r)).FirstOrDefault();
                if (host is null && row.Items.Count > 1 && Scripts(item, row))
                    host = row; // a script among its own line's items
                if (host is null)
                    continue;
                row.Items.Remove(item);
                host.Scripts.Add((item, item.Top + item.Height / 2 < host.Top + host.Height / 2));
            }
        }
        rows.RemoveAll(r => r.Items.Count == 0);
        return rows;
    }

    /// <summary>
    /// Hyphenated and plain words of the whole document, for deciding line-end hyphens, and how many words broken by
    /// hyphenation were rejoined.
    /// </summary>
    private sealed class Vocabulary
    {
        public HashSet<string> Hyphenated { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Plain { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Joined { get; set; }

        public Vocabulary(IEnumerable<string> lines)
        {
            foreach (var word in lines.SelectMany(l => WordToken().Matches(l).Select(m => m.Value)))
                (word.Contains('-', StringComparison.Ordinal) && !word.EndsWith('-') ? Hyphenated : Plain).Add(word);
        }
    }

    /// <summary>A page's rows as blocks: tables, pictures and paragraphs in reading order.</summary>
    private static IEnumerable<Block> Blocks(List<Row> rows, int page, (double Left, double Right) column, Vocabulary words, double pageWidth)
    {
        var width = column.Right - column.Left;
        bool Cells(Row r) => r.Kind == Kind.Text && !r.Flowing && r.Items.Count >= 2 && r.Items.All(i => i.Width < 0.6 * width) && !Heading(r)
            && r.Items.Zip(r.Items.Skip(1)).All(p => p.Second.Left - p.First.Right > 0.8 * p.First.Height);
        // A numbered heading ("2.2", "How to …") is two parts on a line too, but never a table's row.
        bool Narrow(Row r) => r.Kind == Kind.Text && !r.Flowing && r.Items.All(i => i.Width < 0.6 * width) && !Heading(r);
        // A small picture among a table's rows: an icon in a cell.
        bool Icon(Row r) => r.Kind == Kind.Picture && r.Items[0].Width < 0.3 * width;
        static bool Vertical(Row r) => r.Kind == Kind.Rule && r.Items[0].Height > 0;

        // First the tables: three or more rows with cells among consecutive narrow rows, with the narrow header lines
        // right above them and the rules around them, forming at least two columns that three rows use.
        var tables = new Dictionary<int, int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!Cells(rows[i]))
                continue;
            var (start, end, cellRows) = (i, i, 0);
            for (var j = i; j < rows.Count && (Narrow(rows[j]) || rows[j].Kind == Kind.Rule || Icon(rows[j])); j++)
            {
                if (j > i && rows[j].Kind == Kind.Text && rows[j].Top - rows[j - 1].Bottom > 2.5 * rows[j].Height)
                    break;
                // A ruled grid ends where its vertical rules do: the next text (a heading, a paragraph) is outside it.
                var middle = rows[j].Top + rows[j].Height / 2;
                if (j > i && rows[j].Kind == Kind.Text && rows.Skip(i).Take(j - i).Any(Vertical)
                    && !rows.Skip(Math.Max(0, i - 8)).Take(j - Math.Max(0, i - 8)).Any(r => Vertical(r) && r.Top <= middle && r.Bottom >= middle))
                    break;
                cellRows += Cells(rows[j]) ? 1 : 0;
                if (!Icon(rows[j]))
                    end = j; // a picture belongs to the table only before its last text
            }
            // Measured against text rows: a rule is 0 pt high.
            var lineHeight = rows[i].Height;
            // A header line sits close above the table's first text (measured to that, not to a rule between them).
            while (start > 0 && (rows[start - 1].Kind == Kind.Rule
                || Narrow(rows[start - 1]) && rows.Skip(start).First(r => r.Kind == Kind.Text).Top - rows[start - 1].Bottom < 0.6 * lineHeight
                || Icon(rows[start - 1]) && rows[start - 1].Bottom > rows[start].Top))
                start--;
            var text = rows.Skip(start).Take(end - start + 1).Where(r => r.Kind == Kind.Text).ToList();
            var columns = Columns(text.SelectMany(r => r.Items));
            var spread = text.Count(r => r.Items.Select(item => columns.FindIndex(c => item.Left < c.Right && item.Right > c.Left)).Distinct().Count() >= 2);
            // Two columns where every row with cells right-aligns its second part ("School … 2021 – 2023"): lines with a
            // right tab, not a table.
            var seconds = text.Where(Cells).Select(r => r.Items[^1]).ToList();
            var tabbed = columns.Count == 2 && text.Where(Cells).All(r => r.Items.Count == 2)
                && seconds.Max(i => i.Right) - seconds.Min(i => i.Right) < 3 && seconds.Max(i => i.Left) - seconds.Min(i => i.Left) > 6;
            if (cellRows >= 3 && columns.Count >= 2 && spread >= 3 && !tabbed)
            {
                tables[start] = end;
                i = end;
            }
        }

        var paragraph = new List<Row>();
        var blocks = new List<Block>();
        var rules = new List<Item>();
        var background = new List<Item>();
        void Flush()
        {
            if (paragraph.Count > 0)
                blocks.Add(Paragraph(paragraph, page, column, words, pageWidth));
            paragraph = [];
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (tables.TryGetValue(i, out var end))
            {
                Flush();
                var range = rows.Skip(i).Take(end - i + 1).ToList();
                var tableRows = range.Where(r => r.Kind == Kind.Text).ToList();
                var tableRules = range.Where(r => r.Kind == Kind.Rule).Select(r => r.Items[0])
                    .Concat(rows.Skip(end + 1).TakeWhile(r => r.Kind == Kind.Rule && r.Top < tableRows.Max(t => t.Bottom) + 8).Select(r => r.Items[0])).ToList();
                var tableTop = Math.Min(tableRows.Min(r => r.Top), tableRules.Select(r => r.Top).DefaultIfEmpty(double.MaxValue).Min());
                var tableBottom = Math.Max(tableRows.Max(r => r.Bottom), tableRules.Select(r => r.Bottom).DefaultIfEmpty(0).Max());
                var icons = range.Where(r => r.Kind == Kind.Picture).Select(r => r.Items[0]).ToList();
                blocks.Add(new Block([Spacer(), Table(tableRows, tableRules, icons, column, words)], tableTop, tableBottom, page));
                i = end + rows.Skip(end + 1).TakeWhile(r => r.Kind == Kind.Rule && r.Top < tableBottom + 8).Count();
                continue;
            }
            if (row.Kind == Kind.Rule)
            {
                rules.Add(row.Items[0]); // a rule outside a table: a border of the paragraph next to it (below)
                continue;
            }
            if (row.Kind == Kind.Picture && rows.Any(r => r.Kind == Kind.Text && r.Items.Any(t =>
                    (t.Left + t.Right) / 2 > row.Items[0].Left && (t.Left + t.Right) / 2 < row.Items[0].Right
                    && t.Baseline > row.Items[0].Top && t.Baseline < row.Items[0].Bottom)))
            {
                background.Add(row.Items[0]); // text drawn over it: a background, behind the text
                continue;
            }
            if (row.Kind == Kind.Picture)
            {
                Flush();
                var picture = row.Items[0];
                var centered = Math.Abs((picture.Left + picture.Right) / 2 - (column.Left + column.Right) / 2) < 4;
                var pPr = new XElement(W + "pPr", centered ? new XElement(W + "jc", new XAttribute(W + "val", "center"))
                    : new XElement(W + "ind", new XAttribute(W + "left", Twips(picture.Left - column.Left))));
                blocks.Add(new Block([new XElement(W + "p", pPr, new XElement(W + "r", new XElement(W + "drawing", Inline(picture.Anchor!))))], picture.Top, picture.Bottom, page));
                continue;
            }
            if (paragraph.Count > 0 && Breaks(paragraph, row, column))
                Flush();
            paragraph.Add(row);
        }
        Flush();
        if (background.Count > 0 && blocks.SelectMany(b => b.Elements).FirstOrDefault(e => e.Name == W + "p") is { } first)
            first.Add(background.Select(picture => new XElement(W + "r", new XElement(W + "drawing", Behind(picture.Anchor!)))));

        // Rules under a heading or between sections: the bottom border of the paragraph right above, else the top border
        // of the one right below, if wide enough to be a rule of the text and close to it.
        foreach (var rule in rules.Where(r => r.Width > 0.5 * width))
        {
            var above = blocks.LastOrDefault(b => b.Bottom <= rule.Top + 3 && rule.Top - b.Bottom < 12 && b.Elements[^1].Name == W + "p");
            var below = blocks.FirstOrDefault(b => b.Top >= rule.Top - 3 && b.Top - rule.Top < 12 && b.Elements[0].Name == W + "p");
            var (target, side, distance) = above is not null ? (above.Elements[^1], "bottom", rule.Top - above.Bottom)
                : below is not null ? (below.Elements[0], "top", below.Top - rule.Top) : (null, "", 0.0);
            if (target is null)
                continue;
            var pPr = target.Element(W + "pPr") ?? new XElement(W + "pPr");
            if (target.Element(W + "pPr") is null)
                target.AddFirst(pPr);
            var borders = pPr.Element(W + "pBdr") ?? new XElement(W + "pBdr");
            if (pPr.Element(W + "pBdr") is null)
                pPr.Add(borders);
            borders.Element(W + side)?.Remove();
            borders.Add(new XElement(W + side, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", Math.Max(2, (int)Math.Round(RuleWidth(rule) * 8))),
                new XAttribute(W + "space", Math.Clamp((int)Math.Round(distance), 0, 31)), new XAttribute(W + "color", "000000")));
        }
        return blocks;
    }

    /// <summary>
    /// The page's gutter between two text columns: a vertical strip at least 8 pt wide in the middle of the text block
    /// (30–70 %) that no narrow line crosses, with running text on each side. Lines wider than 55 % of the
    /// block (a title, an introduction, a wide figure) may cross it: they are the one-column bands. Null for one column.
    /// </summary>
    private static (double Left, double Right)? Gutter(List<Row> rows, (double Left, double Right) column)
    {
        if (PageGutter(rows, column) is { } whole)
            return whole;
        // Columns under (or over) a part laid out otherwise (a title block, authors side by side): the largest run of
        // rows from the top or the bottom with a gutter. The rows crossing it become one-column bands.
        for (var skip = 1; skip < rows.Count - 8; skip++)
            if (PageGutter(rows.Skip(skip).ToList(), column) is { } below)
                return below;
        for (var take = rows.Count - 1; take > 8; take--)
            if (PageGutter(rows.Take(take).ToList(), column) is { } above)
                return above;
        return null;
    }

    private static (double Left, double Right)? PageGutter(List<Row> rows, (double Left, double Right) column)
    {
        var width = column.Right - column.Left;
        var narrow = rows.Where(r => r.Kind == Kind.Text).SelectMany(r => r.Items).Where(i => i.Width < 0.55 * width).ToList();
        if (narrow.Count < 6)
            return null;
        (double Left, double Right)? best = null;
        var start = (double?)null;
        for (var x = column.Left + 0.3 * width; x <= column.Left + 0.7 * width; x += 1)
        {
            var crossed = narrow.Any(i => i.Left < x && i.Right > x);
            if (!crossed)
                start ??= x;
            if ((crossed || x + 1 > column.Left + 0.7 * width) && start is { } from)
            {
                var to = crossed ? x : x + 1;
                if (to - from >= 8 && (best is null || to - from > best.Value.Right - best.Value.Left))
                    best = (from, to);
                start = null;
            }
        }
        if (best is not { } gutter)
            return null;
        // Widen to the edges of the text on both sides, and require text on both.
        var leftEdge = narrow.Where(i => i.Right <= gutter.Left + 1).Select(i => i.Right).DefaultIfEmpty(gutter.Left).Max();
        var rightEdge = narrow.Where(i => i.Left >= gutter.Right - 1).Select(i => i.Left).DefaultIfEmpty(gutter.Right).Min();
        // Columns of running text: on each side, four or more lines filling most of that column (table cells and labels
        // beside each other are short, so a table isn't taken for two columns).
        var (leftWidth, rightWidth) = (leftEdge - column.Left, column.Right - rightEdge);
        // Full lines of running text on both sides: left lines reaching the gutter, right lines filling their column to
        // the margin. Dates and places pushed right beside entries ("SJB Institute … September 2023 – June 2027") don't.
        var leftRows = rows.Count(r => r.Kind == Kind.Text && r.Items.Any(i => i.Right <= gutter.Left + 1
            && i.Width >= 0.6 * leftWidth && i.Right >= leftEdge - 0.1 * leftWidth));
        var rightRows = rows.Count(r => r.Kind == Kind.Text && r.Items.Any(i => i.Left >= gutter.Right - 1
            && i.Width >= 0.75 * rightWidth && i.Right >= column.Right - 0.05 * rightWidth));
        return leftRows >= 4 && rightRows >= 4 ? (leftEdge, rightEdge) : null;
    }

    /// <summary>The page's rows in bands, top to bottom: rows crossing the gutter (one column) or between (two).</summary>
    private static IEnumerable<(bool Split, List<Row> Rows)> Bands(List<Row> rows, (double Left, double Right) gutter)
    {
        // A row with something across the gutter is one column.
        var bands = new List<(bool Split, List<Row> Rows)>();
        foreach (var row in rows)
        {
            var split = !row.Items.Any(i => i.Left < gutter.Left - 1 && i.Right > gutter.Right + 1);
            if (bands.Count > 0 && bands[^1].Split == split)
                bands[^1].Rows.Add(row);
            else
                bands.Add((split, [row]));
        }
        // Two or fewer text lines beside each other (between parts of a title block) aren't columns.
        for (var i = 0; i < bands.Count; i++)
            if (bands[i].Split && bands[i].Rows.Count(r => r.Kind == Kind.Text) <= 2 && bands.Count > 1)
                bands[i] = (false, bands[i].Rows);
        for (var i = bands.Count - 1; i > 0; i--)
            if (bands[i].Split == bands[i - 1].Split)
            {
                bands[i - 1].Rows.AddRange(bands[i].Rows);
                bands.RemoveAt(i);
            }
        return bands;
    }

    /// <summary>The part of a row on one side of the gutter (items and scripts the test keeps), or null if none.</summary>
    private static Row? Part(Row row, Func<Item, bool> keep)
    {
        var part = new Row();
        part.Items.AddRange(row.Items.Where(keep));
        part.Scripts.AddRange(row.Scripts.Where(s => keep(s.Item)));
        return part.Items.Count > 0 ? part : null;
    }

    /// <summary>A marker: the section so far ends here, with this many columns (and gutter, points).</summary>
    private static Block SectionBreak(int columns, double gutter, int page) => new([], 0, 0, page, Columns: columns, Gutter: gutter);

    /// <summary>
    /// A page's shapes: a filled rectangle behind a paragraph (no taller than it plus a little) becomes its shading;
    /// every other shape (banner, logo, icon, and filled areas behind several things) floats behind the text at its place
    /// on the page, anchored in the page's first paragraph.
    /// </summary>
    private static void Decorate(List<Block> blocks, List<Item> shapes, List<Row> rows)
    {
        var floating = new List<Item>();
        foreach (var shape in shapes)
        {
            var fill = Fill(shape.Anchor!);
            // Inside a table: the fill of the cells whose middles it covers (a slice of a fill only, nothing).
            if (fill is not null && blocks.Select(b => b.Elements.FirstOrDefault(e => e.Name == W + "tbl")?.Annotation<TableGrid>() is { } g ? (b, g) : default)
                    .FirstOrDefault(t => t.g is { } g && shape.Top >= t.b.Top - 3 && shape.Bottom <= t.b.Bottom + 3
                        && shape.Left >= g.Bounds[0] - 3 && shape.Right <= g.Bounds[^1] + 3) is { g: { } grid } inTable)
            {
                var table = inTable.b.Elements.First(e => e.Name == W + "tbl");
                var trs = table.Elements(W + "tr").ToList();
                for (var r = 0; r < trs.Count; r++)
                {
                    var middle = (grid.Tops[r] + grid.Bottoms[r]) / 2;
                    if (middle < shape.Top || middle > shape.Bottom)
                        continue;
                    var tcs = trs[r].Elements(W + "tc").ToList();
                    for (var c = 0; c < tcs.Count && c + 1 < grid.Bounds.Count; c++)
                    {
                        var center = (grid.Bounds[c] + grid.Bounds[c + 1]) / 2;
                        if (center < shape.Left || center > shape.Right)
                            continue;
                        var tcPr = tcs[c].Element(W + "tcPr")!;
                        tcPr.Element(W + "shd")?.Remove();
                        var shd = new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", fill.ToUpperInvariant()));
                        if (tcPr.Element(W + "tcBorders") is { } cellBorders)
                            cellBorders.AddAfterSelf(shd);
                        else
                            tcPr.Add(shd);
                    }
                }
                continue;
            }
            var behind = fill is null || fill.Equals("FFFFFF", StringComparison.OrdinalIgnoreCase) ? null : blocks.FirstOrDefault(b =>
                b.Lines is { } lines && b.Elements[0].Name == W + "p"
                && shape.Left <= lines.Min(l => l.Left) + 3 && shape.Right >= lines.Max(l => l.Right) - 3
                && shape.Top <= b.Top + 3 && shape.Bottom >= b.Bottom - 3 && shape.Height <= b.Bottom - b.Top + 2 * lines[0].Height
                && !rows.Any(r => r.Kind == Kind.Text && !lines.Contains(r) && r.Top >= shape.Top - 1 && r.Bottom <= shape.Bottom + 1));
            if (behind is not null)
            {
                var pPr = behind.Elements[0].Element(W + "pPr")!;
                pPr.Element(W + "shd")?.Remove();
                pPr.Add(new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", fill!.ToUpperInvariant())));
                continue;
            }
            floating.Add(shape);
        }
        var host = blocks.Select(b => b.Elements.FirstOrDefault(e => e.Name == W + "p")).FirstOrDefault(p => p is not null);
        if (host is null || floating.Count == 0)
            return;
        // Behind the text, at the shape's own place on the page; x2t positions them from the page already.
        host.Add(floating.Select(shape => new XElement(W + "r", new XElement(W + "drawing", Behind(shape.Anchor!)))));
    }

    /// <summary>A copy of an anchored drawing set behind the text, overlapping freely.</summary>
    private static XElement Behind(XElement anchor)
    {
        var copy = new XElement(anchor);
        copy.SetAttributeValue("behindDoc", 1);
        copy.SetAttributeValue("allowOverlap", 1);
        copy.SetAttributeValue("layoutInCell", 1);
        return copy;
    }

    /// <summary>Columns of a table: the unions of items that overlap horizontally, left to right.</summary>
    private static List<(double Left, double Right)> Columns(IEnumerable<Item> items)
    {
        var columns = new List<(double Left, double Right)>();
        foreach (var item in items.OrderBy(i => i.Left))
        {
            var index = columns.FindIndex(c => item.Left < c.Right && item.Right > c.Left);
            if (index < 0)
                columns.Add((item.Left, item.Right));
            else
                columns[index] = (Math.Min(columns[index].Left, item.Left), Math.Max(columns[index].Right, item.Right));
        }
        return Merge(columns.OrderBy(c => c.Left).ToList());
    }

    /// <summary>Whether <paramref name="row"/> starts a new paragraph after <paramref name="paragraph"/>'s lines.</summary>
    private static bool Breaks(List<Row> paragraph, Row row, (double Left, double Right) column)
    {
        var last = paragraph[^1];
        if (row.Flowing != last.Flowing && !(Snapped(row) && Snapped(last)) || Math.Abs(row.Height - last.Height) > 0.15 * last.Height)
            return true; // another kind or size of text
        if (row.Top - last.Bottom > 0.45 * last.Height)
            return true; // paragraph spacing
        if (ListMarker().IsMatch(row.Text) || Heading(row) || Heading(last) || TwoSided(row, column) || TwoSided(last, column))
            return true; // a list item, a heading, a line with a right-aligned part ("Company … dates")
        if (Monospaced(row) && Monospaced(last))
            return true; // code keeps its lines
        if (row.Flowing && last.Flowing && row.Items[0].Centered && last.Items[0].Centered)
            return false; // centred positioned lines: one paragraph (a title over several lines)
        // Would the next line's first word have fitted at the end of the previous line? Then the author broke the line:
        // a short last line, in ragged or justified text alike.
        var edge = Math.Max(paragraph.Max(r => r.Right), row.Right);
        if (edge > column.Right - 0.1 * (column.Right - column.Left))
            edge = Math.Max(edge, column.Right);
        if (edge - last.Right > row.FirstWord + 0.3 * row.Height)
            return true;
        if (row.Left > last.Left + 3)
            return !(paragraph.Count == 1 && ListMarker().IsMatch(paragraph[0].Text)); // first-line indent, unless a hanging list item
        if (row.Left < last.Left - 3)
            return paragraph.Count > 1; // back from a hanging indent: a new paragraph; after an indented first line: the same
        return false;
    }

    /// <summary>A line set in a monospaced font (code, a listing).</summary>
    private static bool Monospaced(Row row) => row.Items.All(i => i.Font is { } font && MonoFont().IsMatch(font));

    /// <summary>A row whose items were placed on the PDF's glyphs (positioned or boxed, they are measured alike).</summary>
    private static bool Snapped(Row row) => row.Items[0].FirstWord > 0;

    /// <summary>A line with a part pushed to the right margin after a wide gap: "Company … 2023 – 2027".</summary>
    private static bool TwoSided(Row row, (double Left, double Right) column) =>
        row.Items.Count >= 2 && AtRight(row.Items[^1], column) && row.Items[^1].Left - row.Items[^2].Right > 1.5 * row.Height;

    /// <summary>Ending at the right margin, give or take 3 % of the column: a right-aligned part may stop short of justified lines.</summary>
    private static bool AtRight(Item item, (double Left, double Right) column) =>
        Math.Abs(item.Right - column.Right) < 0.03 * (column.Right - column.Left);

    /// <summary>A list marker on its own: "•", "–", "1.", "a)", "[3]".</summary>
    private static bool Marker(string text) => MarkerOnly().IsMatch(text.Trim());

    /// <summary>A numbered heading line: a number and a title as separate items ("4", "Evaluation Method").</summary>
    private static bool Heading(Row row) =>
        row.Items.Count == 2 && SectionNumber().IsMatch(row.Items[0].Text.Trim()) && row.Items[1].Left - row.Items[0].Right > 0.4 * row.Height;

    /// <summary>Lines as one paragraph: runs joined (hyphenation removed), indents, alignment and pitch measured.</summary>
    private static Block Paragraph(List<Row> lines, int page, (double Left, double Right) column, Vocabulary words, double pageWidth)
    {
        var first = lines[0];
        var source = first.Items[0].Paragraph!;
        var pPr = source.Element(W + "pPr") is { } original ? new XElement(original) : new XElement(W + "pPr");
        pPr.Elements().Where(e => e.Name == W + "ind" || e.Name == W + "spacing" || e.Name == W + "jc" || e.Name == W + "tabs" || e.Name == W + "sectPr").Remove();
        var paragraph = new XElement(W + "p", pPr);

        // Positioned lines (x2t's top-of-page lines) centred with equal indents from both page edges are a centred
        // paragraph; all other lines, positioned or boxed, are measured alike.
        var centeredFlow = first.Flowing && lines.All(l => l.Items[0].Centered) && lines.Any(l => l.Right < column.Right - 6);
        if (centeredFlow)
        {
            pPr.Add(new XElement(W + "ind", new XAttribute(W + "left", 0), new XAttribute(W + "right", 0)));
            pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", "center")));
        }
        else
        {
            var contLeft = lines.Count > 1 ? lines.Skip(1).Min(l => l.Left) : first.Left;
            // A list item hangs its text from where the text after the marker starts, also on a single line.
            if (first.Items.Count >= 2 && Marker(first.Items[0].Text))
                contLeft = lines.Count > 1 ? lines.Skip(1).Min(l => l.Left) : first.Items[1].Left;
            else if (ListMarker().IsMatch(first.Text) && first.Items[0].SecondWord > first.Left && lines.Count == 1)
                contLeft = first.Items[0].SecondWord;
            var maxRight = lines.Max(l => l.Right);
            var centerOffset = lines.Select(l => (l.Left + l.Right) / 2 - (column.Left + column.Right) / 2).ToList();
            // Justified: the lines before the last end at the margin, or (a narrower block) at one edge, three lines or more.
            var nonLast = lines.Take(lines.Count - 1).ToList();
            var justified = nonLast.Count > 0 && (nonLast.All(l => Math.Abs(l.Right - column.Right) < 1.5)
                || nonLast.Count >= 2 && nonLast.All(l => l.Right > maxRight - 1.5));
            var centered = !justified && centerOffset.All(c => Math.Abs(c) < 4) && lines.All(l => l.Right < column.Right - 6) && !ListMarker().IsMatch(first.Text);
            // Only a justified block narrower than the column has a right indent; ragged lines end where their words do.
            var rightIndent = justified && maxRight - contLeft > (column.Right - column.Left) / 2 ? Math.Max(0, column.Right - maxRight) : 0;
            var ind = new XElement(W + "ind",
                new XAttribute(W + "left", centered ? 0 : Twips(Math.Max(0, contLeft - column.Left))),
                new XAttribute(W + "right", rightIndent > 6 ? Twips(rightIndent) : 0));
            if (!centered && first.Left - contLeft > 1)
                ind.Add(new XAttribute(W + "firstLine", Twips(first.Left - contLeft)));
            else if (!centered && contLeft - first.Left > 1)
                ind.Add(new XAttribute(W + "hanging", Twips(contLeft - first.Left)));
            pPr.Add(ind);
            if (justified || centered)
                pPr.Add(new XElement(W + "jc", new XAttribute(W + "val", justified ? "both" : "center")));
        }

        // Line pitch: the median distance between lines, exactly, as the PDF set it: baseline to baseline when the glyphs
        // are known (a line's box may be taller, around a bullet), else top to top.
        var gaps = lines.Zip(lines.Skip(1), (a, b) => a.Baseline > 0 && b.Baseline > 0 ? b.Baseline - a.Baseline : b.Top - a.Top)
            .Where(d => d > 0).Order().ToList();
        var pitch = gaps.Count == 0 ? first.Height : lines.Count > 2 ? gaps[gaps.Count / 2] : gaps[0];
        // x2t's line boxes run taller than the text (and a bullet's box taller still): lines may be set closer than a
        // box, not closer than the glyphs.
        pitch = Math.Max(0.85 * lines.Min(l => l.Height), pitch);
        SetSpacing(paragraph, line: pitch);

        // Centred lines were broken by their author (a title, an address): keep the breaks.
        var keepBreaks = pPr.Element(W + "jc")?.Attribute(W + "val")?.Value == "center";
        var stops = new SortedDictionary<int, string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var runs = LineRuns(lines[i], column, stops);
            if (i > 0 && keepBreaks)
            {
                TrimEnd(paragraph, hyphen: false);
                paragraph.Add(new XElement(W + "r", new XElement(W + "br")));
                TrimStart(runs);
            }
            else if (i > 0)
            {
                var previousText = string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value)).TrimEnd();
                var nextText = string.Concat(runs.SelectMany(r => r.Descendants(W + "t")).Select(t => t.Value)).TrimStart();
                var hyphen = Hyphenated(previousText, nextText, words);
                if (hyphen == false)
                    TrimEnd(paragraph, hyphen: false); // a compound broken at its own hyphen: joined as it is, no space
                else if (hyphen == true)
                {
                    // The PDF broke the word here: an optional hyphen, invisible unless the word ends a line, lets the
                    // renderer break where the original did without splitting the word for good.
                    TrimEnd(paragraph, hyphen: true);
                    paragraph.Add(SoftHyphen(paragraph.Elements(W + "r").LastOrDefault()));
                }
                else
                {
                    TrimEnd(paragraph, hyphen: false);
                    paragraph.Add(Run(" ", runs.FirstOrDefault()));
                }
                TrimStart(runs);
            }
            paragraph.Add(runs);
        }
        // A list item hanging its text from the indent ("7." then the text at the hanging indent): a tab after the
        // marker, as in a real list, so the first line's text starts where the other lines do.
        if (pPr.Element(W + "ind")?.Attribute(W + "hanging") is not null && ListMarker().Match(first.Text) is { Success: true } marker)
            TabAfterMarker(paragraph, marker.Value.TrimEnd().Length);
        // A hanging list item tabs from its marker to the indent: a stop there too, as not every renderer stops at the
        // hanging indent by itself.
        if (pPr.Element(W + "ind") is { } hangingInd && hangingInd.Attribute(W + "hanging") is not null && paragraph.Descendants(W + "tab").Any())
            stops.TryAdd(Number(hangingInd, "left", 0), "left");
        // Tab stops where the line's parts were: left at a part's start, right at the margin for a right-aligned part.
        if (stops.Count > 0)
            pPr.Add(new XElement(W + "tabs", stops.Select(t => new XElement(W + "tab", new XAttribute(W + "val", t.Value), new XAttribute(W + "pos", t.Key)))));

        var last = lines[^1];
        return new Block([paragraph], first.Top, last.Top + Math.Max(last.Height, pitch), page, lines);
    }

    /// <summary>
    /// Whether a line ending in "word-" joined to the next line is one word broken by hyphenation: the next line starts
    /// with a lower-case letter, and the hyphenated form isn't a word of the document while the joined form isn't known
    /// either way or is a plain word.
    /// </summary>
    /// <summary>
    /// How a line ending in "word-" joins the next one: a word broken by hyphenation (the hyphen goes, the next line
    /// starts in lower case), or a compound broken at its own hyphen (kept, no space: written so elsewhere, or the next
    /// part capitalised). Null when the line doesn't end in a hyphenated word.
    /// </summary>
    private static bool? Hyphenated(string previous, string next, Vocabulary words)
    {
        if (LineEndHyphen().Match(previous) is not { Success: true } head)
            return null;
        // Hyphenation never continues a word with a capital: "Dang-" + "Quang" is a compound broken at its hyphen.
        if (next.Length > 0 && char.IsUpper(next[0]))
            return false;
        if (LeadingWord().Match(next) is not { Success: true } tail)
            return null;
        var (start, rest) = (head.Groups[1].Value, tail.Value);
        var joined = !words.Hyphenated.Contains($"{start}-{rest}") || words.Plain.Contains(start + rest);
        words.Joined += joined ? 1 : 0;
        return joined;
    }

    /// <summary>A row's runs: items left to right, a tab before a far item, a space before a near one; scripts placed by x.</summary>
    private static List<XElement> LineRuns(Row row, (double Left, double Right) column, SortedDictionary<int, string> stops)
    {
        var pieces = row.Items.Select(i => (i.Left, i.Right, Item: i, Script: (bool?)null))
            .Concat(row.Scripts.Select(s => (s.Item.Left, s.Item.Right, s.Item, Script: (bool?)s.Super)))
            .OrderBy(p => p.Left).ToList();
        var runs = new List<XElement>();
        double? previousRight = null;
        var height = row.Height;
        var heading = Heading(row);
        var marker = row.Items.Count >= 2 && Marker(row.Items[0].Text);
        var lastItem = row.Items[^1];
        foreach (var (pieceLeft, pieceRight, item, script) in pieces)
        {
            var itemRuns = item.Paragraph!.Elements(W + "r").Select(r => new XElement(r)).ToList();
            // x2t nudged letter spacing so each line box fits the PDF exactly; reflowed text must not keep it.
            foreach (var run in itemRuns)
                run.Element(W + "rPr")?.Element(W + "spacing")?.Remove();
            if (script is { } super)
                foreach (var run in itemRuns)
                {
                    var rPr = run.Element(W + "rPr") ?? new XElement(W + "rPr");
                    if (run.Element(W + "rPr") is null)
                        run.AddFirst(rPr);
                    rPr.Element(W + "vertAlign")?.Remove();
                    rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", super ? "superscript" : "subscript")));
                }
            if (previousRight is { } edge && script is null && itemRuns.Count > 0)
            {
                var gap = pieceLeft - edge;
                // x2t splits a line into boxes at word boundaries (around sub- and superscripts), and the boxes may
                // even overlap a little: always a space between them, a tab for a wide gap.
                // A tab only where the PDF set parts apart on purpose: a heading's number and title, a list marker and its
                // text, a part pushed to the right margin, or a very wide gap. A justified line x2t split in two keeps a space.
                var pushedRight = item == lastItem && AtRight(item, column) && gap > 1.5 * height;
                if (gap > 3 * height || pushedRight || heading || marker && item == row.Items[1])
                {
                    // A heading's number and title, a list marker and its text, the parts of a two-sided line.
                    runs.Add(new XElement(W + "r", new XElement(W + "tab")));
                    if (item == lastItem && AtRight(item, column) && !marker)
                        stops[Twips(column.Right - column.Left)] = "right";
                    else if (!(marker && item == row.Items[1]))
                        stops[Twips(item.Left - column.Left)] = "left";
                }
                else if (!EndsWithSpace(runs) && !itemRuns.SelectMany(r => r.Descendants(W + "t")).FirstOrDefault()?.Value.StartsWith(' ') == true
                    && itemRuns[0].Element(W + "rPr")?.Element(W + "vertAlign") is null)
                    runs.Add(Run(" ", itemRuns[0])); // not before a sub- or superscript: it belongs to the letter before
            }
            runs.AddRange(itemRuns);
            previousRight = Math.Max(previousRight ?? pieceRight, pieceRight);
        }
        return runs;
    }

    /// <summary>
    /// Table rows as a table with one value per cell. Columns are the unions of items overlapping horizontally; a row
    /// overlapping the previous one by a third (a two-line header) joins it; rules become the top, bottom or a row's bottom
    /// border; a column whose items share a right edge is right-aligned, a shared middle centred.
    /// </summary>
    private static XElement Table(List<Row> rows, List<Item> rules, List<Item> icons, (double Left, double Right) column, Vocabulary words)
    {
        var merged = new List<List<Item>>();
        // Each item's line (a script with the line it belongs to) and whether it is raised or lowered, for the cells.
        var lineTop = new Dictionary<Item, double>();
        var scripts = new Dictionary<Item, bool>();
        foreach (var row in rows)
        {
            foreach (var item in row.Items)
                lineTop[item] = row.Top;
            foreach (var (item, super) in row.Scripts)
                (lineTop[item], scripts[item]) = (row.Top, super);
        }
        var vertical = rules.Where(r => r.Height > 0).ToList();
        rules = rules.Where(r => r.Height == 0).ToList();
        // A ruled grid: the lines between two rules are one row, their cells several lines long (rules drawn per cell,
        // or twice, count once). One rule inside is a header's (booktabs); a grid rules off every row.
        // A row's rule spans the table (a frame around an icon in a cell doesn't).
        var span = rows.Max(r => r.Right) - rows.Min(r => r.Left);
        var levels = new List<List<Item>>();
        foreach (var rule in rules.Where(r => r.Top > rows[0].Top + 1 && r.Top < rows[^1].Bottom - 1).OrderBy(r => r.Top))
            if (levels.Count > 0 && rule.Top - levels[^1][0].Top <= 1.5)
                levels[^1].Add(rule);
            else
                levels.Add([rule]);
        var inner = levels.Where(l => l.Sum(r => r.Width) >= 0.5 * span).Select(l => l[0].Top).ToList();
        if (inner.Count < 2)
            inner.Clear();
        if (inner.Count > 0)
        {
            int Band(double middle) => inner.Count(y => y < middle);
            var items = rows.SelectMany(row => row.Items.Concat(row.Scripts.Select(s => s.Item))).Concat(icons);
            merged.AddRange(items.GroupBy(i => Band(i.Top + i.Height / 2)).OrderBy(g => g.Key).Select(g => g.ToList()));
            icons = [];
        }
        else
        {
            var previousRow = (Top: 0.0, Bottom: 0.0);
            foreach (var row in rows)
            {
                var overlap = Math.Min(previousRow.Bottom, row.Bottom) - Math.Max(previousRow.Top, row.Top);
                if (merged.Count > 0 && overlap > row.Height / 3)
                    merged[^1].AddRange(row.Items.Concat(row.Scripts.Select(s => s.Item)));
                else
                    merged.Add([.. row.Items, .. row.Scripts.Select(s => s.Item)]);
                previousRow = (row.Top, row.Bottom);
            }
        }
        // Icons into the row they stand in (between the same rules, else the last row starting above their middle).
        foreach (var icon in icons)
        {
            var middle = icon.Top + icon.Height / 2;
            var index = Math.Max(0, merged.FindLastIndex(r => r.Min(i => i.Top) <= middle));
            merged[index].Add(icon);
        }

        var columns = Columns(merged.SelectMany(r => r));
        int ColumnOf(Item item) => columns.FindIndex(c => item.Left < c.Right && item.Right > c.Left) is var i and >= 0 ? i : 0;

        var tableLeft = Math.Min(columns[0].Left, rules.Select(r => r.Left).DefaultIfEmpty(columns[0].Left).Min());
        var tableRight = Math.Max(columns[^1].Right, rules.Select(r => r.Right).DefaultIfEmpty(columns[^1].Right).Max());
        var bounds = columns.Select((c, i) => i == 0 ? tableLeft : (columns[i - 1].Right + c.Left) / 2).Append(tableRight).ToList();
        // Vertical rules of the grid stand between columns (or at its sides); others frame something in a cell. Where
        // they stand, the columns end, the text clear of them by what the PDF left.
        var near = 0.4 * columns.Min(c => c.Right - c.Left);
        vertical = vertical.Where(v => bounds.Any(b => Math.Abs(v.Left - b) < near)).ToList();
        if (vertical.Count > 0)
            bounds = bounds.Select(b => vertical.Select(v => v.Left).OrderBy(x => Math.Abs(x - b)).First() is var x && Math.Abs(x - b) < near ? x : b).ToList();
        var padding = vertical.Count > 0 ? Math.Clamp(columns.Select((c, i) => c.Left - bounds[i]).Min(), 0, 10) : 0;
        var widths = bounds.Zip(bounds.Skip(1), (a, b) => Math.Max(1, Twips(b - a))).ToList();

        string Alignment(int index)
        {
            var items = merged.SelectMany(r => r).Where(i => ColumnOf(i) == index).ToList();
            if (items.Count < 2)
                return "left";
            double Spread(Func<Item, double> edge) => items.Max(edge) - items.Min(edge);
            var (l, r, m) = (Spread(i => i.Left), Spread(i => i.Right), Spread(i => (i.Left + i.Right) / 2));
            return l <= 1.5 ? "left" : r <= 1.5 ? "right" : m <= 1.5 ? "center" : "left";
        }
        var alignments = Enumerable.Range(0, columns.Count).Select(Alignment).ToList();

        XElement Border(string side, Item rule) => new(W + side, new XAttribute(W + "val", "single"),
            new XAttribute(W + "sz", Math.Max(2, (int)Math.Round(RuleWidth(rule) * 8))), new XAttribute(W + "space", 0), new XAttribute(W + "color", "000000"));
        var tops = merged.Select(r => r.Min(i => i.Top)).ToList();
        var bottoms = merged.Select(r => r.Max(i => i.Bottom)).ToList();
        var topRule = rules.FirstOrDefault(r => r.Top <= tops[0] + 1);
        var bottomRule = rules.LastOrDefault(r => r.Top >= bottoms[^1] - 1 && r != topRule);
        var rowRules = rules.Where(r => r != topRule && r != bottomRule)
            .GroupBy(r => Enumerable.Range(0, merged.Count).LastOrDefault(i => tops[i] < r.Top))
            .ToDictionary(g => g.Key, g => g.First());

        var table = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblW", new XAttribute(W + "w", widths.Sum()), new XAttribute(W + "type", "dxa")),
                new XElement(W + "tblInd", new XAttribute(W + "w", Twips(tableLeft - column.Left)), new XAttribute(W + "type", "dxa")),
                new XElement(W + "tblBorders",
                    topRule is not null ? Border("top", topRule) : new XElement(W + "top", new XAttribute(W + "val", "nil")),
                    vertical.Count > 0 ? Border("left", vertical[0]) : new XElement(W + "left", new XAttribute(W + "val", "nil")),
                    bottomRule is not null ? Border("bottom", bottomRule) : new XElement(W + "bottom", new XAttribute(W + "val", "nil")),
                    vertical.Count > 0 ? Border("right", vertical[0]) : new XElement(W + "right", new XAttribute(W + "val", "nil")),
                    new XElement(W + "insideH", new XAttribute(W + "val", "nil")),
                    // Vertical rules drawn: a grid, ruled between all columns.
                    vertical.Count > 0 ? Border("insideV", vertical[0]) : new XElement(W + "insideV", new XAttribute(W + "val", "nil"))),
                new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed")),
                // Text clear of a grid's vertical rules.
                new XElement(W + "tblCellMar", new[] { "top", "left", "bottom", "right" }.Select(side =>
                    new XElement(W + side, new XAttribute(W + "w", side is "left" or "right" ? Twips(padding) : 0), new XAttribute(W + "type", "dxa"))))),
            new XElement(W + "tblGrid", widths.Select(w => new XElement(W + "gridCol", new XAttribute(W + "w", w)))));

        for (var r = 0; r < merged.Count; r++)
        {
            var height = r + 1 < merged.Count ? tops[r + 1] - tops[r] : bottoms[r] - tops[r];
            var tr = new XElement(W + "tr", new XElement(W + "trPr", new XElement(W + "trHeight",
                new XAttribute(W + "val", Twips(Math.Max(1, height))), new XAttribute(W + "hRule", "atLeast"))));
            for (var c = 0; c < columns.Count; c++)
            {
                var items = merged[r].Where(i => ColumnOf(i) == c).OrderBy(i => lineTop.GetValueOrDefault(i, i.Top)).ThenBy(i => i.Left).ToList();
                // A cell breaks words only where the PDF did (soft hyphens), not by hyphenating on its own.
                XElement NewParagraph() => new(W + "p", new XElement(W + "pPr", new XElement(W + "suppressAutoHyphens"),
                    new XElement(W + "spacing", new XAttribute(W + "before", 0), new XAttribute(W + "after", 0)),
                    new XElement(W + "jc", new XAttribute(W + "val", alignments[c]))));
                var cell = new List<XElement> { NewParagraph() };
                var paragraph = cell[0];
                Item? marker = null; // where the paragraph's list marker stood
                // A list item hangs its text from where the PDF's next word (or line) starts, a tab after its marker.
                void Finish()
                {
                    TrimEnd(paragraph, hyphen: false);
                    if (marker is null)
                        return;
                    var hang = (Marker(marker.Text)
                        ? items.FirstOrDefault(o => o != marker && Math.Abs(o.Top - marker.Top) < 2 && o.Left > marker.Left)?.Left ?? marker.Right
                        : marker.SecondWord) - marker.Left;
                    var text = string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value));
                    if (hang > 1 && ListMarker().Match(text) is { Success: true } found)
                    {
                        var left = Twips(Math.Max(0, marker.Left - bounds[c] - padding) + hang);
                        var pPr = paragraph.Element(W + "pPr")!;
                        pPr.AddFirst(new XElement(W + "tabs", new XElement(W + "tab", new XAttribute(W + "val", "left"), new XAttribute(W + "pos", left))));
                        pPr.Element(W + "jc")!.AddBeforeSelf(new XElement(W + "ind", new XAttribute(W + "left", left), new XAttribute(W + "hanging", Twips(hang))));
                        TabAfterMarker(paragraph, found.Value.TrimEnd().Length);
                    }
                    marker = null;
                }
                foreach (var item in items)
                {
                    if (item.Kind == Kind.Picture)
                    {
                        if (paragraph.Elements(W + "r").Any())
                        {
                            Finish();
                            cell.Add(paragraph = NewParagraph());
                        }
                        paragraph.Add(new XElement(W + "r", new XElement(W + "drawing", Inline(item.Anchor!))));
                        cell.Add(paragraph = NewParagraph());
                        continue;
                    }
                    var runs = item.Paragraph!.Elements(W + "r").Select(run => new XElement(run)).ToList();
                    if (scripts.TryGetValue(item, out var raised))
                    {
                        foreach (var run in runs)
                        {
                            var rPr = run.Element(W + "rPr") ?? new XElement(W + "rPr");
                            if (rPr.Parent is null)
                                run.AddFirst(rPr);
                            rPr.Element(W + "vertAlign")?.Remove();
                            rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", raised ? "superscript" : "subscript")));
                        }
                        TrimEnd(paragraph, hyphen: false); // it belongs to the letter before
                        paragraph.Add(runs);
                        continue;
                    }
                    // A list item in a cell starts its own paragraph.
                    var isMarker = ListMarker().IsMatch(item.Text) || Marker(item.Text);
                    if (paragraph.Elements(W + "r").Any() && isMarker)
                    {
                        Finish();
                        cell.Add(paragraph = NewParagraph());
                    }
                    if (isMarker && !paragraph.Elements(W + "r").Any())
                        marker = item;
                    if (paragraph.Elements(W + "r").Any())
                    {
                        // A cell's text over two lines ("persis-", "tence") is joined like a paragraph's.
                        var before = string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value)).TrimEnd();
                        var hyphen = Hyphenated(before, item.Text.TrimStart(), words);
                        if (hyphen == false)
                            TrimEnd(paragraph, hyphen: false);
                        else if (hyphen == true)
                        {
                            TrimEnd(paragraph, hyphen: true);
                            paragraph.Add(SoftHyphen(paragraph.Elements(W + "r").LastOrDefault()));
                        }
                        else
                        {
                            TrimEnd(paragraph, hyphen: false);
                            paragraph.Add(Run(" ", item.Paragraph!.Element(W + "r")));
                        }
                        TrimStart(runs);
                    }
                    paragraph.Add(runs);
                }
                Finish();
                if (cell.Count > 1 && !paragraph.Elements(W + "r").Any())
                    cell.Remove(paragraph);
                tr.Add(new XElement(W + "tc", new XElement(W + "tcPr",
                    new XElement(W + "tcW", new XAttribute(W + "w", widths[c]), new XAttribute(W + "type", "dxa")),
                    rowRules.TryGetValue(r, out var rule) ? new XElement(W + "tcBorders", Border("bottom", rule)) : null), cell));
            }
            table.Add(tr);
        }
        table.AddAnnotation(new TableGrid(tops, Enumerable.Range(0, merged.Count).Select(r => r + 1 < merged.Count ? tops[r + 1] : bottoms[r]).ToList(), bounds));
        return table;
    }

    /// <summary>Where a table's rows and columns lie on the page (points), for shading its cells.</summary>
    private sealed record TableGrid(List<double> Tops, List<double> Bottoms, List<double> Bounds);

    /// <summary>Columns that still overlap after growing are one column.</summary>
    private static List<(double Left, double Right)> Merge(List<(double Left, double Right)> columns)
    {
        var merged = new List<(double Left, double Right)>();
        foreach (var c in columns)
            if (merged.Count > 0 && c.Left < merged[^1].Right)
                merged[^1] = (merged[^1].Left, Math.Max(merged[^1].Right, c.Right));
            else
                merged.Add(c);
        return merged;
    }

    /// <summary>A rule's line width in points (its a:ln w, EMU), 0.5 pt when not given.</summary>
    private static double RuleWidth(Item rule) =>
        rule.Anchor?.Descendants(A + "ln").Select(l => Plain(l, "w")).FirstOrDefault(w => w > 0) is { } w and > 0 ? w / 12700.0 : 0.5;

    /// <summary>
    /// Page numbers as PAGE fields: one header (or footer) for every page, or odd and even ones when the number sits on
    /// the left on even pages and the right on odd ones; the first page has none when the PDF's has none.
    /// </summary>
    private static List<HeaderPart> Headers(Dictionary<int, (Row Row, bool Top)> numbers, (double Left, double Right) column, bool titlePage, int pageCount)
    {
        if (numbers.Count < 2)
            return [];
        string Align(Row row) =>
            Math.Abs(row.Left - column.Left) < 6 ? "left" : Math.Abs(row.Right - column.Right) < 6 ? "right" : "center";
        var footer = numbers.Values.Count(n => !n.Top) > numbers.Count / 2;
        var odd = numbers.Where(n => (n.Key + 1) % 2 == 1).Select(n => Align(n.Value.Row)).GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var even = numbers.Where(n => (n.Key + 1) % 2 == 0).Select(n => Align(n.Value.Row)).GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var runStyle = numbers.Values.First().Row.Items[0].Paragraph!.Element(W + "r")?.Element(W + "rPr");
        IEnumerable<XElement> PageField(XElement? style, string instruction = " PAGE ")
        {
            XElement Field(string type, string? instruction = null) => new(W + "r", style is null ? null : new XElement(style),
                instruction is null ? new XElement(W + "fldChar", new XAttribute(W + "fldCharType", type)) : new XElement(W + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), instruction));
            return [Field("begin"), Field("", instruction), Field("separate"), new XElement(W + "r", style is null ? null : new XElement(style), new XElement(W + "t", "1")), Field("end")];
        }
        XElement Part(string align) => new(W + (footer ? "ftr" : "hdr"), new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
            new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "jc", new XAttribute(W + "val", align))), PageField(runStyle)));
        // A running line with text: its runs as laid out (tabs between its parts), its page number a PAGE field.
        XElement Line(int page, Row row)
        {
            var stops = new SortedDictionary<int, string>();
            var runs = LineRuns(row, column, stops).Where(r => r.Element(W + "drawing") is null).ToList();
            var paragraph = new XElement(W + "p", new XElement(W + "pPr",
                stops.Count > 0 ? new XElement(W + "tabs", stops.Select(t => new XElement(W + "tab", new XAttribute(W + "val", t.Value), new XAttribute(W + "pos", t.Key)))) : null,
                new XElement(W + "ind", new XAttribute(W + "left", Twips(Math.Max(0, row.Left - column.Left))))));
            // The page's number a PAGE field, the page count after it ("Page 2 of 3") a NUMPAGES field.
            var replaced = false;
            foreach (var run in runs)
            {
                if (run.Element(W + "t") is not { } t)
                {
                    paragraph.Add(run);
                    continue;
                }
                var style = run.Element(W + "rPr");
                var at = 0;
                foreach (var m in Digits().Matches(t.Value).Cast<Match>())
                {
                    var value = int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
                    var field = !replaced && value == page + 1 ? " PAGE " : replaced && value == pageCount ? " NUMPAGES " : null;
                    if (field is null)
                        continue;
                    if (m.Index > at)
                        paragraph.Add(Run(t.Value[at..m.Index], run));
                    paragraph.Add(PageField(style, field));
                    replaced = true;
                    at = m.Index + m.Length;
                }
                if (at == 0)
                    paragraph.Add(run);
                else if (at < t.Value.Length)
                    paragraph.Add(Run(t.Value[at..], run));
            }
            return new XElement(W + (footer ? "ftr" : "hdr"), new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName), paragraph);
        }
        bool Bare(Row row) => PageNumber().IsMatch(row.Text.Trim());
        var kind = footer ? "footer" : "header";
        // Odd pages (page index even) and even pages each take their running line from a later page of theirs.
        var textLines = numbers.Where(n => !Bare(n.Value.Row)).OrderByDescending(n => n.Key > 0).ThenBy(n => n.Key).ToList();
        var oddLine = textLines.Where(n => n.Key % 2 == 0).Select(n => (KeyValuePair<int, (Row Row, bool Top)>?)n).FirstOrDefault();
        var evenLine = textLines.Where(n => n.Key % 2 == 1).Select(n => (KeyValuePair<int, (Row Row, bool Top)>?)n).FirstOrDefault();
        var main = oddLine ?? evenLine;
        var parts = new List<HeaderPart> { new($"{kind}Easy1.xml", "default", footer, main is { } m ? Line(m.Key, m.Value.Row) : Part(odd ?? even ?? "center")) };
        if (oddLine is { } o && evenLine is { } e ? Running(o.Value.Row) != Running(e.Value.Row) : main is null && even is not null && odd is not null && even != odd)
            parts.Add(new($"{kind}Easy2.xml", "even", footer, evenLine is { } ev ? Line(ev.Key, ev.Value.Row) : Part(even!)));
        if (titlePage)
            parts.Add(new($"{kind}Easy3.xml", "first", footer, numbers.TryGetValue(0, out var first)
                ? Bare(first.Row) ? Part(Align(first.Row)) : Line(0, first.Row)
                : new XElement(W + (footer ? "ftr" : "hdr"), new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName), new XElement(W + "p"))));
        return parts;
    }

    /// <summary>Adds the header and footer parts with their relationships and content types (and even/odd headers).</summary>
    private static void AddHeaders(ZipArchive zip, XDocument document, List<HeaderPart> parts)
    {
        foreach (var part in parts)
            Save(zip, "word/" + part.Name, new XDocument(part.Content));
        var rels = Load(zip, "word/_rels/document.xml.rels") ?? new XDocument(new XElement(Rel + "Relationships"));
        foreach (var part in parts)
            rels.Root!.Add(new XElement(Rel + "Relationship", new XAttribute("Id", "rIdEasy" + Path.GetFileNameWithoutExtension(part.Name)),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + (part.Footer ? "footer" : "header")),
                new XAttribute("Target", part.Name)));
        Save(zip, "word/_rels/document.xml.rels", rels);
        var types = Load(zip, "[Content_Types].xml")!;
        foreach (var part in parts)
            types.Root!.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/word/" + part.Name),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml." + (part.Footer ? "footer" : "header") + "+xml")));
        Save(zip, "[Content_Types].xml", types);
        if (parts.Any(p => p.Type == "even") && Load(zip, "word/settings.xml") is { } settings)
        {
            AddSetting(settings, "evenAndOddHeaders");
            Save(zip, "word/settings.xml", settings);
        }
    }

    /// <summary>
    /// The PDF's text block, from its glyphs: the leftmost common left edge and the most common right edge of its
    /// full-width lines (points).
    /// Null when the PDF has no text.
    /// </summary>
    public static (double Left, double Right)? TextColumn(string pdf)
    {
        var edges = new List<(double Left, double Right)>();
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        // Running headers and footers sit in the outer bands of the page and may run wider than the text block.
        foreach (var page in document.GetPages())
            edges.AddRange(page.Letters.Where(l => !IsBlank(l.Value) && l.StartBaseLine.Y > 0.06 * page.Height && l.StartBaseLine.Y < 0.94 * page.Height)
                .GroupBy(l => Math.Round(l.StartBaseLine.Y / 2))
                .Select(g => (g.Min(l => l.StartBaseLine.X), g.Max(l => l.EndBaseLine.X))));
        if (edges.Count == 0)
            return null;
        var widest = edges.Max(e => e.Right - e.Left);
        var full = edges.Where(e => e.Right - e.Left > 0.75 * widest).ToList();
        // Edges to 0.5 pt, as the exact mean of the lines at them: a fraction of a point decides where lines wrap and
        // hyphenate. The left edge is the leftmost one enough lines share: indented lists may outnumber the lines at the margin, but
        // nothing in the body starts left of it.
        var support = Math.Max(3, 0.1 * full.Count);
        var left = full.Select(e => e.Left).GroupBy(v => Math.Round(v * 2) / 2).Where(g => g.Count() >= support)
            .OrderBy(g => g.Key).FirstOrDefault()?.Average() ?? full.Min(e => e.Left);
        // The right edge likewise: the rightmost one enough lines share (justified text); ragged text shares none, and
        // its block holds its longest line.
        var right = full.Select(e => e.Right).GroupBy(v => Math.Round(v * 2) / 2).Where(g => g.Count() >= support)
            .OrderByDescending(g => g.Key).FirstOrDefault()?.Average() ?? full.Max(e => e.Right);
        return (left, right);
    }

    /// <summary>A line's text with white space collapsed and numbers as "#": the same on every page for a running line.</summary>
    private static string Running(Row row) => Digits().Replace(string.Join(' ', row.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), "#");

    /// <summary>Whether the first lines of a page continue the previous page's last paragraph.</summary>
    private static bool RunsOn(List<Row> before, List<Row> after)
    {
        var (last, next) = (before[^1], after[0]);
        var left = before.Count > 1 ? before.Skip(1).Min(l => l.Left) : before[0].Left;
        return !last.Flowing && !next.Flowing && Math.Abs(last.Height - next.Height) < 0.15 * last.Height
            && last.Right >= Math.Max(before.Max(l => l.Right), next.Right) - 15
            && Math.Abs(next.Left - left) < 3 && !ListMarker().IsMatch(next.Text) && !Heading(next) && !Heading(last);
    }

    private static XElement Spacer() => new(W + "p", new XElement(W + "pPr",
        new XElement(W + "spacing", new XAttribute(W + "before", 0), new XAttribute(W + "after", 0), new XAttribute(W + "line", 20), new XAttribute(W + "lineRule", "exact"))));

    /// <summary>Sets space before (points) and the exact line pitch (points) of a paragraph; after is always 0.</summary>
    private static void SetSpacing(XElement paragraph, double? before = null, double? line = null)
    {
        var pPr = paragraph.Element(W + "pPr") ?? new XElement(W + "pPr");
        if (paragraph.Element(W + "pPr") is null)
            paragraph.AddFirst(pPr);
        var spacing = pPr.Element(W + "spacing");
        if (spacing is null)
        {
            spacing = new XElement(W + "spacing");
            pPr.AddFirst(spacing);
        }
        spacing.SetAttributeValue(W + "after", 0);
        if (before is { } b)
            spacing.SetAttributeValue(W + "before", Twips(b));
        if (line is { } l)
        {
            spacing.SetAttributeValue(W + "line", Twips(l));
            spacing.SetAttributeValue(W + "lineRule", "exact");
        }
    }

    private static XElement SoftHyphen(XElement? like) => new(W + "r",
        like?.Element(W + "rPr") is { } rPr ? new XElement(rPr) : null, new XElement(W + "softHyphen"));

    private static XElement Run(string text, XElement? like) => new(W + "r",
        like?.Element(W + "rPr") is { } rPr ? new XElement(rPr) : null,
        new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

    private static bool EndsWithSpace(List<XElement> runs) =>
        runs.SelectMany(r => r.Descendants(W + "t")).LastOrDefault()?.Value.EndsWith(' ') ?? true;

    /// <summary>Replaces the spaces after the first <paramref name="markerLength"/> characters (a list marker) with a tab.</summary>
    private static void TabAfterMarker(XElement paragraph, int markerLength)
    {
        // A tab after the marker already (the marker was a part of its own): nothing to do.
        var content = paragraph.Elements(W + "r").SelectMany(r => r.Elements()).ToList(); // not the tab stops in pPr
        var beforeTab = string.Concat(content.TakeWhile(e => e.Name != W + "tab").Where(e => e.Name == W + "t").Select(t => t.Value));
        if (content.Any(e => e.Name == W + "tab") && IsBlank(beforeTab[Math.Min(markerLength, beforeTab.Length)..]))
            return;
        var seen = 0;
        foreach (var t in paragraph.Descendants(W + "t").ToList())
        {
            if (seen + t.Value.Length <= markerLength)
            {
                seen += t.Value.Length;
                continue;
            }
            var cut = markerLength - seen;
            var (head, tail) = (t.Value[..cut], t.Value[cut..].TrimStart());
            var run = t.Parent!;
            if (tail.Length == t.Value.Length - cut)
                return; // no space after the marker: leave it
            var tabRun = new XElement(W + "r", run.Element(W + "rPr") is { } rPr ? new XElement(rPr) : null, new XElement(W + "tab"));
            t.Value = head;
            var rest = new XElement(run);
            rest.Element(W + "t")!.Value = tail;
            run.AddAfterSelf(tabRun, rest);
            if (head.Length == 0)
                run.Remove();
            return;
        }
    }

    /// <summary>Removes trailing spaces (and a trailing hyphen) from a paragraph's last text.</summary>
    private static void TrimEnd(XElement paragraph, bool hyphen)
    {
        foreach (var t in paragraph.Descendants(W + "t").Reverse())
        {
            var value = t.Value.TrimEnd();
            if (hyphen && value.EndsWith('-'))
            {
                t.Value = value[..^1];
                return;
            }
            t.Value = value;
            if (value.Length > 0)
                return;
        }
    }

    private static void TrimStart(List<XElement> runs)
    {
        foreach (var t in runs.SelectMany(r => r.Descendants(W + "t")))
        {
            t.Value = t.Value.TrimStart();
            if (t.Value.Length > 0)
                return;
        }
    }

    /// <summary>The anchored picture as an inline one: same size, description and graphic, no position.</summary>
    private static XElement Inline(XElement anchor) => new(Wp + "inline",
        new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
        new XElement(anchor.Element(Wp + "extent")!),
        anchor.Element(Wp + "effectExtent") is { } effect ? new XElement(effect) : null,
        new XElement(anchor.Element(Wp + "docPr")!),
        anchor.Element(Wp + "cNvGraphicFramePr") is { } frame ? new XElement(frame) : null,
        new XElement(anchor.Elements().First(e => e.Name.LocalName == "graphic")));

    private static int Twips(double points) => (int)Math.Round(points * 20);

    /// <summary>A w: attribute as a number.</summary>
    private static int Number(XElement? element, string attribute, int fallback) =>
        int.TryParse(element?.Attribute(W + attribute)?.Value, out var value) ? value : fallback;

    /// <summary>An attribute without namespace (DrawingML: cx, cy, w) as a number.</summary>
    private static long Plain(XElement? element, string attribute) =>
        long.TryParse(element?.Attribute(attribute)?.Value, out var value) ? value : 0;

    private static XDocument? Load(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void Save(ZipArchive zip, string name, XDocument document)
    {
        zip.GetEntry(name)?.Delete();
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    [GeneratedRegex(@"^\d{1,4}$")]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"mono|courier|consol|menlo|monaco|code|typewriter|fixed", RegexOptions.IgnoreCase)]
    private static partial Regex MonoFont();

    [GeneratedRegex(@"symbol|wingding|webding|dingbat", RegexOptions.IgnoreCase)]
    private static partial Regex SymbolFont();

    [GeneratedRegex(@"(PSMT|MT|PS)$", RegexOptions.IgnoreCase)]
    private static partial Regex PostScriptSuffix();

    [GeneratedRegex(@"(\p{Ll})(\p{Lu})")]
    private static partial Regex SpacedName();

    [GeneratedRegex(@"^\d+(\.\d+)*\.?$")]
    private static partial Regex SectionNumber();

    // Bullets include the Private Use Area: Symbol and Wingdings bullets come through as U+F0B7, U+F0A7 and the like.
    [GeneratedRegex(@"^(\[\d+\]|\d+[.)]\s|[–·\-\*]\s|[•▪◦●■□○◆◇►▸➢➤✓✔❖-])")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^(\[\d+\]|\d+[.)]|[a-z][.)]|[•▪◦–·\-\*●■□○◆◇►▸➢➤✓✔❖-])$")]
    private static partial Regex MarkerOnly();

    [GeneratedRegex(@"[\p{L}][\p{L}\-]*")]
    private static partial Regex WordToken();

    [GeneratedRegex(@"([\p{L}]+)-$")]
    private static partial Regex LineEndHyphen();

    [GeneratedRegex(@"^\p{Ll}[\p{L}]*")]
    private static partial Regex LeadingWord();
}
