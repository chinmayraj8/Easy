// Easy: PdfLayoutDocx rebuilds paragraphs, tables and headers from x2t's line boxes (PDF → DOCX). The documents here are
// shaped like x2t's line mode: per page, positioned paragraphs and one empty paragraph anchoring a text box per line.

using System.Xml.Linq;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public class PdfLayoutDocxTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const double Left = 124.7, Right = 470.6; // the text column, points

    /// <summary>A line box at (x, y) points, w × h points; '|' separates runs, "_x" makes run x a subscript.</summary>
    private static string Box(double x, double y, double w, string text, double h = 11.5) => $"""
        <w:r><w:drawing><wp:anchor><wp:positionH relativeFrom="page"><wp:posOffset>{(long)(x * 12700)}</wp:posOffset></wp:positionH>
        <wp:positionV relativeFrom="page"><wp:posOffset>{(long)(y * 12700)}</wp:posOffset></wp:positionV><wp:extent cx="{(long)(w * 12700)}" cy="{(long)(h * 12700)}"/>
        <wp:docPr id="1" name="Text Box"/><a:graphic><a:graphicData><wps:wsp><wps:txbx><w:txbxContent>
        <w:p><w:pPr><w:spacing w:before="0" w:line="229" w:lineRule="exact"/><w:ind/></w:pPr>{Runs(text)}</w:p>
        </w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>
        """;

    private static string Runs(string text) => string.Concat(text.Split('|').Select(part => part.StartsWith('_')
        ? $"<w:r><w:rPr><w:vertAlign w:val=\"subscript\"/></w:rPr><w:t>{part[1..]}</w:t></w:r>"
        : $"<w:r><w:t xml:space=\"preserve\">{part}</w:t></w:r>"));

    /// <summary>A horizontal rule shape (a table border) at y.</summary>
    private static string Rule(double x, double y, double w) => $"""
        <w:r><w:drawing><wp:anchor><wp:positionH relativeFrom="page"><wp:posOffset>{(long)(x * 12700)}</wp:posOffset></wp:positionH>
        <wp:positionV relativeFrom="page"><wp:posOffset>{(long)(y * 12700)}</wp:posOffset></wp:positionV><wp:extent cx="{(long)(w * 12700)}" cy="0"/>
        <wp:docPr id="2" name="Line"/><a:graphic><a:graphicData><wps:wsp><wps:spPr><a:ln w="9525"/></wps:spPr></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>
        """;

    /// <summary>A positioned paragraph (x2t's top-of-page lines): indents from the page edges, in twips.</summary>
    private static string Positioned(string text, int before, int left, int right) =>
        $"<w:p><w:pPr><w:spacing w:before=\"{before}\" w:line=\"321\" w:lineRule=\"exact\"/><w:ind w:left=\"{left}\" w:right=\"{right}\"/></w:pPr>{Runs(text)}</w:p>";

    /// <summary>Pages of positioned paragraphs and boxes; each page ends with a section break, like x2t's.</summary>
    private static XDocument Document(params (string Positioned, string[] Boxes)[] pages) => XDocument.Parse($"""
        <w:document xmlns:w="{W}" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
          xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
          xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body>
        {string.Concat(pages.Select(p => $"{p.Positioned}<w:p><w:pPr><w:spacing w:line=\"1\" w:lineRule=\"exact\"/></w:pPr>{string.Concat(p.Boxes)}</w:p>" +
            "<w:p><w:pPr><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"0\" w:right=\"0\" w:bottom=\"0\" w:left=\"0\"/></w:sectPr></w:pPr></w:p>"))}
        </w:body></w:document>
        """);

    private static List<PdfLayoutDocx.HeaderPart> Rebuild(XDocument document) => PdfLayoutDocx.Rebuild(document, (Left, Right));

    private static string Text(XElement element) => string.Concat(element.Descendants().Select(e =>
        e.Name == W + "t" ? e.Value : e.Name == W + "tab" && e.Parent?.Name == W + "r" ? "\t" : e.Name == W + "br" ? "\n" : e.Name == W + "softHyphen" ? "­" : ""));

    private static List<string> Paragraphs(XDocument document) =>
        document.Root!.Element(W + "body")!.Elements(W + "p").Select(Text).Where(t => t.Length > 0).ToList();

    [Fact]
    public void Lines_become_one_paragraph_and_line_end_hyphens_become_optional_hyphens()
    {
        var document = Document(("", [
            Box(Left, 200, 357.4, "Predictive autoscalers act on a forecast of load rather than on load al-"),
            Box(Left, 211.5, 357.4, "ready observed, and LSTM networks are a popular choice of fore-"),
            Box(Left, 223, 120, "caster. We test it."),
        ]));

        Rebuild(document);

        var paragraph = Assert.Single(Paragraphs(document));
        Assert.Equal("Predictive autoscalers act on a forecast of load rather than on load al­ready observed, and LSTM networks are a popular choice of fore­caster. We test it.", paragraph);
        Assert.DoesNotContain("al- ready", paragraph.Replace("­", "", StringComparison.Ordinal));
    }

    [Fact]
    public void A_compound_written_with_a_hyphen_elsewhere_keeps_it()
    {
        var document = Document(("", [
            Box(Left, 200, 357.4, "The re-implementation is ours. We wrote a re-"),
            Box(Left, 211.5, 200, "implementation of the formula."),
        ]));

        Rebuild(document);

        Assert.Contains("a re-implementation of the formula.", Assert.Single(Paragraphs(document)));
    }

    [Fact]
    public void A_name_broken_at_its_hyphen_keeps_it()
    {
        var document = Document(("", [
            Box(Left, 200, 357.4, "A bidirectional LSTM is the forecaster in the Kubernetes autoscaler of Dang-"),
            Box(Left, 211.5, 150, "Quang and Yoo [6]."),
        ]));

        Rebuild(document);

        Assert.Contains("of Dang-Quang and Yoo [6].", Assert.Single(Paragraphs(document)));
    }

    [Fact]
    public void A_short_last_line_or_an_indent_starts_a_new_paragraph()
    {
        var document = Document(("", [
            Box(Left, 200, 357.4, "The first paragraph runs over the whole width of the column and then"),
            Box(Left, 211.5, 100, "ends here."),
            Box(Left + 14, 223, 343, "The second one starts indented and is long enough to fill its line."),
            Box(Left, 234.5, 357.4, "It continues at the margin on its second line, which is full width too,"),
            Box(Left, 246, 80, "and ends."),
        ]));

        Rebuild(document);

        Assert.Equal(2, Paragraphs(document).Count);
        var second = document.Root!.Element(W + "body")!.Elements(W + "p").Where(p => Text(p).Length > 0).Last();
        Assert.Equal("280", second.Element(W + "pPr")!.Element(W + "ind")!.Attribute(W + "firstLine")!.Value); // 14 pt
    }

    /// <summary>Table 2's shape: a header over two lines, rules, values one box each (W given once per group).</summary>
    [Fact]
    public void Value_rows_become_a_table_with_one_value_per_cell()
    {
        var document = Document(("", [
            Box(Left, 150, 357.4, "Table 2. Four policies, each tuned and evaluated with the same weight."),
            Rule(132, 172, 330),
            Box(290, 172.6, 62, "SLA violation", 10.3), Box(136, 177.7, 15, "W", 10.3), Box(161, 177.7, 30, "Policy", 10.3), Box(251, 177.7, 26, "Cost", 10.3),
            Box(309, 182.9, 24, "(%)", 10.3),
            Rule(132, 193.5, 330),
            Box(136, 194, 11, "5", 10.3), Box(161, 194, 39, "Reactive", 10.3), Box(250, 194, 30, "0.207", 10.3), Box(308, 194, 26, "3.44", 10.3),
            Box(161, 204.9, 41, "HPA rule", 10.3), Box(250, 204.9, 30, "0.216", 10.3), Box(308, 204.9, 26, "2.36", 10.3),
            Box(136, 215.8, 15, "10", 10.3), Box(161, 215.8, 39, "Reactive", 10.3), Box(250, 215.8, 30, "0.377", 10.3), Box(308, 215.8, 26, "1.89", 10.3),
            Rule(132, 227, 330),
            Box(Left, 240, 357.4, "The results follow."),
        ]));

        Rebuild(document);

        var table = Assert.Single(document.Descendants(W + "tbl"));
        var rows = table.Elements(W + "tr").Select(r => r.Elements(W + "tc").Select(c => Text(c).Trim()).ToList()).ToList();
        Assert.Equal(["W", "Policy", "Cost", "SLA violation (%)"], rows[0]);
        Assert.Equal(["5", "Reactive", "0.207", "3.44"], rows[1]);
        Assert.Equal(["", "HPA rule", "0.216", "2.36"], rows[2]);
        Assert.Equal(["10", "Reactive", "0.377", "1.89"], rows[3]);
        Assert.Empty(table.Descendants(W + "vMerge")); // one value per cell, no merged columns
        Assert.NotNull(table.Element(W + "tblPr")!.Element(W + "tblBorders")!.Elements(W + "top").Single(b => b.Attribute(W + "val")!.Value == "single"));
        Assert.NotNull(table.Element(W + "tblPr")!.Element(W + "tblBorders")!.Elements(W + "bottom").Single(b => b.Attribute(W + "val")!.Value == "single"));
        Assert.Contains(table.Elements(W + "tr").First().Descendants(W + "tcBorders"), b => b.Element(W + "bottom") is not null); // under the header
        Assert.Equal(["Table 2. Four policies, each tuned and evaluated with the same weight.", "The results follow."], Paragraphs(document));
    }

    [Fact]
    public void A_heading_number_and_its_title_stay_one_paragraph()
    {
        var document = Document(("", [
            Box(Left, 200, 357.4, "A paragraph before the heading, as wide as the column, ending here."),
            Box(153.2, 222.9, 108, "Forecast Accuracy", 13.8), Box(Left, 224.8, 18, "5.1", 11.5),
            Box(Left, 248.7, 357.4, "Table 1 lists the forecast errors of each model on the test set."),
        ]));

        Rebuild(document);

        Assert.Contains("5.1\tForecast Accuracy", Paragraphs(document));
        Assert.Empty(document.Descendants(W + "tbl"));
    }

    /// <summary>A line split into boxes around subscripts, overlapping a little: one line, in order, with spaces.</summary>
    [Fact]
    public void Fragments_of_a_line_stay_in_order_even_when_they_overlap()
    {
        var document = Document(("", [
            Box(Left, 492.9, 194.7, "ble the demand. Given N slots, of which N|_u", 12.2),
            Box(315.8, 492.9, 98.7, "are violations and N|_o", 12.2),
            Box(408.6, 492.9, 73.7, "are over-provi-"),
            Box(Left, 504.9, 120, "sioned, we define cost."),
        ]));

        Rebuild(document);

        var paragraph = Assert.Single(Paragraphs(document));
        Assert.Equal("ble the demand. Given N slots, of which Nu are violations and No are over-provi­sioned, we define cost.", paragraph);
        Assert.Empty(document.Descendants(W + "tbl"));
        Assert.Equal(2, document.Descendants(W + "vertAlign").Count()); // the subscripts kept
    }

    [Fact]
    public void Page_numbers_become_a_page_field_in_alternating_headers()
    {
        string[] Body(double y) => [Box(Left, y, 357.4, "Body text that fills the line of the page from margin to margin, then"), Box(Left, y + 11.5, 100, "ends.")];
        var document = Document(
            ("", Body(160)),
            ("", [Box(Left, 120, 6, "2", 10.3), .. Body(160)]),
            ("", [Box(Right - 6, 120, 6, "3", 10.3), .. Body(160)]));

        var parts = Rebuild(document);

        Assert.DoesNotContain(Paragraphs(document), p => p is "2" or "3");
        Assert.Contains(parts, p => p.Type == "default" && p.Content.Descendants(W + "instrText").Any(i => i.Value.Contains("PAGE", StringComparison.Ordinal))
            && p.Content.Descendants(W + "jc").Single().Attribute(W + "val")!.Value == "right");
        Assert.Contains(parts, p => p.Type == "even" && p.Content.Descendants(W + "jc").Single().Attribute(W + "val")!.Value == "left");
        Assert.Contains(parts, p => p.Type == "first"); // page 1 has none
        Assert.NotNull(document.Descendants(W + "titlePg").SingleOrDefault());
    }

    [Fact]
    public void Centred_positioned_lines_keep_their_line_breaks()
    {
        var document = Document((
            Positioned("Cost-Aware Predictive Autoscaling with LSTM and", 2900, 2861, 2862) +
            Positioned("ARIMA Forecasts: A Fair Comparison with Reactive and", 38, 2504, 2504) +
            Positioned("HPA-Style Scaling", 38, 4844, 4844),
            [Box(Left, 260, 357.4, "Abstract. A paragraph of the body starts here and fills its line.")]));

        Rebuild(document);

        Assert.Equal("Cost-Aware Predictive Autoscaling with LSTM and\nARIMA Forecasts: A Fair Comparison with Reactive and\nHPA-Style Scaling", Paragraphs(document)[0]);
        var jc = document.Root!.Element(W + "body")!.Elements(W + "p").First().Element(W + "pPr")!.Element(W + "jc")!;
        Assert.Equal("center", jc.Attribute(W + "val")!.Value);
    }

    [Fact]
    public void Numbered_headings_at_the_top_of_each_page_stay_headings()
    {
        // Easy: "Section 1 Results", "Section 2 Results" (18 pt) at the top of every page recurred with their digits
        // masked and became one header reading "Section <page> Results"; a 9 pt running line with the same words stays
        // a header.
        string[] Page(int n) =>
        [
            Box(Left, 40, 80, "Quarterly Review", 10.3),
            Box(Left, 120, 200, $"Section {n} Results", 20.7),
            Box(Left, 160, 357.4, "Body text that fills the line of the page from margin to margin, then"),
            Box(Left, 171.5, 100, "ends."),
        ];
        var document = Document(("", Page(1)), ("", Page(2)), ("", Page(3)));

        var parts = Rebuild(document);

        var body = Paragraphs(document);
        Assert.Equal(["Section 1 Results", "Section 2 Results", "Section 3 Results"], body.Where(p => p.StartsWith("Section", StringComparison.Ordinal)));
        Assert.DoesNotContain(body, p => p.Contains("Quarterly", StringComparison.Ordinal));
        Assert.Contains(parts, p => Text(p.Content).Contains("Quarterly Review", StringComparison.Ordinal));
        Assert.DoesNotContain(parts, p => Text(p.Content).Contains("Section", StringComparison.Ordinal));
    }
}
