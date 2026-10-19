// Easy: which fonts a DOCX's text asks for, which of them the renderer has, and which fonts a PDF actually drew with.
//
// A missing font is replaced silently by every renderer, and is the most common reason a PDF doesn't look like the
// document. The font a run of text uses is resolved the way Word does for Latin text (the ascii slot): the run's own
// w:rFonts, its character style, its paragraph style and the styles those are based on, then the document defaults;
// theme references (majorAscii, minorHAnsi, …) become the theme's major or minor Latin typeface. East Asian and complex
// script slots are left out: they only matter for those scripts.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Filee.Engines.Office;

/// <summary>Fonts a DOCX requests and fonts a PDF used (see the file comment).</summary>
internal static partial class WordFonts
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Characters of text per font family the document's text is set in, largest first.</summary>
    public static IReadOnlyList<(string Font, int Characters)> Requested(string docx)
    {
        using var zip = ZipFile.OpenRead(docx);
        XDocument? Part(string name) => zip.GetEntry(name) is { } entry ? XDocument.Load(entry.Open()) : null;

        var theme = Part("word/theme/theme1.xml");
        string? Theme(string major) => theme?.Descendants(A + (major == "major" ? "majorFont" : "minorFont"))
            .Elements(A + "latin").Attributes("typeface").Select(a => a.Value).FirstOrDefault(v => v.Length > 0);
        string? FontOf(XElement? rPr)
        {
            var fonts = rPr?.Element(W + "rFonts");
            if (fonts?.Attribute(W + "ascii")?.Value is { Length: > 0 } name)
                return name;
            return fonts?.Attribute(W + "asciiTheme")?.Value is { } reference
                ? Theme(reference.StartsWith("major", StringComparison.Ordinal) ? "major" : "minor")
                : null;
        }

        var styles = Part("word/styles.xml");
        var byId = styles?.Root?.Elements(W + "style")
            .Where(s => s.Attribute(W + "styleId") is not null)
            .GroupBy(s => s.Attribute(W + "styleId")!.Value).ToDictionary(g => g.Key, g => g.First()) ?? [];
        string? DefaultStyle(string type) => byId.Values.FirstOrDefault(s =>
            s.Attribute(W + "type")?.Value == type && s.Attribute(W + "default")?.Value is "1" or "true")?.Attribute(W + "styleId")?.Value;
        string? StyleFont(string? id)
        {
            for (var depth = 0; id is not null && depth < 20 && byId.TryGetValue(id, out var style); depth++)
            {
                if (FontOf(style.Element(W + "rPr")) is { } font)
                    return font;
                id = style.Element(W + "basedOn")?.Attribute(W + "val")?.Value;
            }
            return null;
        }
        var defaults = FontOf(styles?.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr"));
        var defaultParagraph = DefaultStyle("paragraph");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parts = zip.Entries.Select(e => e.FullName)
            .Where(n => n == "word/document.xml" || TextPart().IsMatch(n)).ToList();
        foreach (var part in parts)
        {
            foreach (var paragraph in Part(part)!.Descendants(W + "p"))
            {
                var pPr = paragraph.Element(W + "pPr");
                var paragraphStyle = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? defaultParagraph;
                foreach (var run in paragraph.Descendants(W + "r").Where(r => r.Ancestors(W + "p").First() == paragraph))
                {
                    var characters = run.Elements(W + "t").Sum(t => t.Value.Length);
                    if (characters == 0)
                        continue;
                    var rPr = run.Element(W + "rPr");
                    var font = FontOf(rPr) ?? StyleFont(rPr?.Element(W + "rStyle")?.Attribute(W + "val")?.Value)
                        ?? StyleFont(paragraphStyle) ?? defaults ?? "Times New Roman"; // Word's own fallback
                    counts[font] = counts.GetValueOrDefault(font) + characters;
                }
            }
        }
        return counts.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).ToList();
    }

    /// <summary>Font families listed in ONLYOFFICE's AllFonts.js (the fonts it found on the computer).</summary>
    public static HashSet<string> Installed(string allFontsJs) =>
        FontListEntry().Matches(File.ReadAllText(allFontsJs)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The characters a PDF drew with each font family (subset prefix and style suffix removed).</summary>
    public static IReadOnlyDictionary<string, string> Drawn(string pdf)
    {
        var drawn = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
            foreach (var letter in page.Letters)
            {
                var family = Family(letter.FontName ?? "");
                (drawn.TryGetValue(family, out var set) ? set : drawn[family] = new(StringComparer.Ordinal)).Add(letter.Value);
            }
        return drawn.ToDictionary(d => d.Key, d => string.Concat(d.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"BAAAAA+Times New Roman-Bold" → "Times New Roman".</summary>
    internal static string Family(string fontName)
    {
        var name = fontName.Replace("#20", " ", StringComparison.Ordinal);
        if (name.Length > 7 && name[6] == '+')
            name = name[7..];
        return StyleSuffix().Replace(name, "");
    }

    /// <summary>The face a font name's style suffix gives: "b", "i", "bi" or "" ("Times-BoldItalic" → "bi").</summary>
    internal static string Face(string fontName)
    {
        var suffix = StyleSuffix().Match(fontName.Replace("#20", " ", StringComparison.Ordinal)).Value;
        var bold = suffix.Contains("Bold", StringComparison.OrdinalIgnoreCase) || suffix.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || suffix.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || suffix.Contains("Semibold", StringComparison.OrdinalIgnoreCase);
        var italic = suffix.Contains("Italic", StringComparison.OrdinalIgnoreCase) || suffix.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        return (bold ? "b" : "") + (italic ? "i" : "");
    }

    [GeneratedRegex(@"^word/(header|footer)\d*\.xml$|^word/(footnotes|endnotes)\.xml$")]
    private static partial Regex TextPart();

    [GeneratedRegex(@"\[""([^""]+)"",\s*\d")]
    private static partial Regex FontListEntry();

    [GeneratedRegex(@"[-,](Bold|Italic|BoldItalic|Oblique|BoldOblique|Regular|Roman|Semibold|SemiboldItalic|Medium|MediumItalic|Light|LightItalic|Black|Heavy)(MT)?$", RegexOptions.IgnoreCase)]
    private static partial Regex StyleSuffix();
}
