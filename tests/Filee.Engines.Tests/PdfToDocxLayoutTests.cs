// Easy: regression tests for PDF → DOCX defects found by the conversion audit (tests/conversion-audit):
//
// - Numbered headings at the top of every page ("Section 1", "Section 2") were taken for a running head and dropped.
// - Running heads, footers and page numbers were dropped instead of becoming the document's header and footer.
// - Lines the PDF set apart (a column of short lines) were joined into one paragraph that wrapped differently, because
//   the page was written wider than the PDF's text.
// - Margins measured from the glyphs pushed every page down by the line spacing: the last lines spilled onto an extra
//   page.
// - Two-column pages came out as one column.
// - The OCR text layer of a scanned page was dropped (the page became only a picture), and turned text was dropped.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using static Filee.Engines.Tests.DocxAssert;

namespace Filee.Engines.Tests;

public class PdfToDocxLayoutTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private async Task<string> ToDocxAsync(string pdf)
    {
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "docx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var docx = job.Outputs.Single();
        ValidPackage(docx);
        return docx;
    }

    private static List<string> BodyTexts(HDocument model) =>
        [.. Paragraphs(model.Sections.SelectMany(s => s.Blocks)).Select(p => TextOf(p).Trim()).Where(t => t.Length > 0)];

    /// <summary>Three pages: a 9 pt running head, an 18 pt numbered heading, body lines and "Page n of 3" at the foot.</summary>
    private static string Report(string folder)
    {
        var pdf = new PdfBuilder();
        for (var page = 1; page <= 3; page++)
        {
            pdf.NewPage();
            var regular = pdf.Font();
            pdf.Line("Quarterly Review", 9, regular, 57, 30);
            pdf.Line($"Section {page} Results", 18, pdf.Font(bold: true), 57, 75);
            for (var line = 0; line < 5; line++)
                pdf.Line($"Body line {line} of page {page} with some words.", 11, regular, 57, 110 + line * 16);
            pdf.Line($"Page {page} of 3", 9, regular, 270, 815);
        }
        return pdf.Save(Path.Combine(folder, "report.pdf"));
    }

    [Fact]
    public async Task Numbered_headings_at_the_top_of_each_page_stay_in_the_text()
    {
        var model = ReadBack(await ToDocxAsync(Report(fx.NewFolder())));
        var texts = BodyTexts(model);

        Assert.Equal(["Section 1 Results", "Section 2 Results", "Section 3 Results"], texts.Where(t => t.StartsWith("Section", StringComparison.Ordinal)));
        Assert.All(Paragraphs(model.Sections.SelectMany(s => s.Blocks)).Where(p => TextOf(p).StartsWith("Section", StringComparison.Ordinal)),
            p => Assert.Equal(1, p.HeadingLevel));
    }

    [Fact]
    public async Task Running_head_and_page_numbers_become_the_header_and_footer()
    {
        var docx = await ToDocxAsync(Report(fx.NewFolder()));
        var model = ReadBack(docx);

        // Not in the body, where they would repeat in the middle of the text ...
        Assert.DoesNotContain(BodyTexts(model), t => t.Contains("Quarterly", StringComparison.Ordinal) || t.StartsWith("Page ", StringComparison.Ordinal));
        // ... but the header and footer of every page, with the page number and count as fields.
        var section = Assert.Single(model.Sections);
        Assert.Equal("Quarterly Review", TextOf(Assert.Single(Paragraphs(Assert.Single(section.Headers).Blocks))).Trim());
        var footer = Assert.Single(Paragraphs(Assert.Single(section.Footers).Blocks));
        Assert.Equal([HFieldKind.PageNumber, HFieldKind.TotalPages], footer.Inlines.OfType<HField>().Select(f => f.Kind));
        Assert.Equal("Page  of ", TextOf(footer));
        Assert.Equal(HAlign.Center, footer.Format.Align);
        // The header sits above the body: its distance from the paper edge is less than the body's margin.
        Assert.True(section.Page!.Header > 0 && section.Page.Footer > 0, $"{section.Page}");

        using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
        var footerXml = string.Concat(zip.Entries.Where(e => e.FullName.StartsWith("word/footer", StringComparison.Ordinal))
            .Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.Contains("PAGE", footerXml);
        Assert.Contains("NUMPAGES", footerXml);
        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(3, pages);
    }

    [Fact]
    public async Task A_heading_repeated_word_for_word_at_the_top_is_still_a_running_head()
    {
        // A 9 pt title with no number in it on every page: the header, as before.
        var pdf = new PdfBuilder();
        for (var page = 1; page <= 3; page++)
        {
            pdf.NewPage();
            pdf.Line("Annual Plan", 9, pdf.Font(), 57, 30);
            pdf.Line($"Content of page {page} goes here.", 11, pdf.Font(), 57, 110);
        }
        var model = ReadBack(await ToDocxAsync(pdf.Save(Path.Combine(fx.NewFolder(), "plan.pdf"))));

        Assert.DoesNotContain("Annual Plan", BodyTexts(model));
        Assert.Equal("Annual Plan", TextOf(Paragraphs(model.Sections[0].Headers.Single().Blocks).Single()).Trim());
    }

    [Fact]
    public async Task A_chapter_running_head_that_changes_is_left_out_not_repeated_on_every_page()
    {
        // "Chapter One" on pages 1-2, "Chapter Two" on 3-4: no single header can show both.
        var pdf = new PdfBuilder();
        for (var page = 1; page <= 4; page++)
        {
            pdf.NewPage();
            pdf.Line(page <= 2 ? "Chapter One: Start" : "Chapter Two: End", 9, pdf.Font(), 57, 30);
            pdf.Line($"Text of page {page}.", 11, pdf.Font(), 57, 110);
        }
        var model = ReadBack(await ToDocxAsync(pdf.Save(Path.Combine(fx.NewFolder(), "chapters.pdf"))));

        Assert.DoesNotContain(BodyTexts(model), t => t.StartsWith("Chapter", StringComparison.Ordinal));
        Assert.All(model.Sections, s => Assert.Empty(s.Headers));
    }

    [Fact]
    public async Task Short_lines_the_pdf_set_apart_keep_their_line_breaks()
    {
        // Forty lines that end near the middle of the page: written on a wider page they must not run together.
        var pdf = new PdfBuilder().NewPage();
        for (var i = 0; i < 40; i++)
            pdf.Line($"Entry {i:00} large document line", 10, pdf.Font(), 57, 60 + i * 14.2);
        var docx = await ToDocxAsync(pdf.Save(Path.Combine(fx.NewFolder(), "lines.pdf")));
        var model = ReadBack(docx);

        var paragraph = Paragraphs(model.Sections.SelectMany(s => s.Blocks)).Single(p => TextOf(p).Length > 0);
        Assert.Equal(39, paragraph.Inlines.OfType<HLineBreak>().Count());
        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(1, pages);
    }

    /// <summary>
    /// Easy: a page frame, a grey prompt box with a bar at its left, the rule under a heading and the frames around
    /// screenshots were all dropped (only ruled tables kept their lines).
    /// </summary>
    [Fact]
    public async Task Page_border_shaded_box_heading_rule_and_picture_frame_are_kept()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        var font = pdf.Font();
        pdf.Frame(20, 20, 555, 802, 1);
        pdf.Line("Part A: Prompt design", 14, pdf.Font(bold: true), 60, 80);
        pdf.Rule(60, 86, 536, 86, 0.75);
        pdf.Line("Body text before the box.", 11, font, 60, 120);
        pdf.Bar(60, 150, 476, 40, (243, 243, 243));
        pdf.Bar(60, 150, 3, 40, (102, 102, 102));
        pdf.Line("Explain how retrieval works in simple terms.", 10, font, 68, 165);
        pdf.Line("Compare it with fine-tuning.", 10, font, 68, 180);
        pdf.Line("Body text after the box.", 11, font, 60, 220);
        pdf.Picture(PdfBuilder.Photo(folder, "shot.jpg", ImageMagick.MagickFormat.Jpeg), 60, 260, 300, 180);
        pdf.Frame(60, 260, 300, 180);
        pdf.Line("Figure 1: a screenshot.", 9, font, 150, 455);
        var docx = await ToDocxAsync(pdf.Save(Path.Combine(folder, "decorated.pdf")));

        using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        string Paragraph(string text) => xml.Split("</w:p>").Single(p => p.Contains(text, StringComparison.Ordinal));

        Assert.Contains("<w:pgBorders", xml);
        Assert.Contains("<w:bottom w:val=\"single\"", Paragraph("Part A"));
        Assert.Contains("w:fill=\"F3F3F3\"", Paragraph("Explain how retrieval"));
        Assert.Contains("<w:left w:val=\"single\"", Paragraph("Explain how retrieval"));
        Assert.Contains("w:fill=\"F3F3F3\"", Paragraph("Compare it with")); // one box over both paragraphs
        Assert.DoesNotContain("<w:pBdr>", Paragraph("Body text before"));
        Assert.DoesNotContain("<w:pBdr>", Paragraph("Body text after"));
        Assert.DoesNotContain("<w:pBdr>", Paragraph("Figure 1")); // the frame's edge is the picture's, not the caption's
        Assert.Matches("<pic:spPr>.*<a:ln w=", xml);
    }

    [Fact]
    public async Task A_page_full_of_lines_still_fits_on_one_page()
    {
        // The first baseline sits a line height below the margin in Word ("at least" spacing puts the room above the
        // text): margins measured from the glyphs moved the page down and its last lines onto a second page.
        var pdf = new PdfBuilder().NewPage();
        for (var i = 0; i < 48; i++)
            pdf.Line($"Line {i:00} of a page that is full from the top margin down to the bottom one.", 11, pdf.Font(), 57, 72 + i * 14.5);
        var docx = await ToDocxAsync(pdf.Save(Path.Combine(fx.NewFolder(), "full.pdf")));

        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(1, pages);
        else
            Assert.Skip("LibreOffice is not installed: the page count can't be checked");
    }

    [Fact]
    public async Task Two_columns_become_two_columns_with_a_column_break()
    {
        var pdf = new PdfBuilder().NewPage();
        for (var column = 0; column < 2; column++)
        {
            for (var i = 0; i < 30; i++)
                pdf.Line($"Col{column}Line{i:00} lorem ipsum", 10, pdf.Font(), column == 0 ? 57 : 312, 60 + i * 17);
        }
        var docx = await ToDocxAsync(pdf.Save(Path.Combine(fx.NewFolder(), "columns.pdf")));

        var body = Body(docx);
        // Columns start with the first paragraph (the section break that ends them carries w:cols num="2").
        var cols = body.Descendants(W + "cols").Select(c => (string?)c.Attribute(W + "num")).ToList();
        Assert.Contains("2", cols);
        var paragraphs = body.Descendants(W + "p").ToList();
        var secondColumn = paragraphs.First(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)).StartsWith("Col1Line00", StringComparison.Ordinal));
        Assert.Contains(secondColumn.Descendants(W + "br"), br => (string?)br.Attribute(W + "type") == "column");
        // Reading order: the whole first column, then the second.
        var text = string.Concat(body.Descendants(W + "t").Select(t => t.Value));
        Assert.True(text.IndexOf("Col0Line29", StringComparison.Ordinal) < text.IndexOf("Col1Line00", StringComparison.Ordinal));
        if (await ConvertibleByLibreOfficeAsync(fx, docx) is { } pages)
            Assert.Equal(1, pages);
    }

    [Fact]
    public async Task A_scan_with_an_ocr_layer_gives_its_text()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        pdf.Picture(PdfBuilder.Photo(folder, "scan.png", ImageMagick.MagickFormat.Png, 595, 842), 0, 0, 595, 842);
        for (var i = 0; i < 10; i++)
            pdf.InvisibleLine($"ScannedLine{i:00} printed on paper", 14, pdf.Font(), 60, 80 + i * 30);
        var docx = await ToDocxAsync(pdf.Save(Path.Combine(folder, "scan.pdf")));

        var text = string.Join(" ", BodyTexts(ReadBack(docx)));
        for (var i = 0; i < 10; i++)
            Assert.Contains($"ScannedLine{i:00} printed on paper", text);
        // The page-sized scan is not also put behind the text (it would show every line twice).
        Assert.DoesNotContain(Body(docx).Descendants(Wp + "anchor"), a => (string?)a.Attribute("behindDoc") == "1");
    }

    [Fact]
    public async Task A_scan_without_text_is_still_the_page_picture()
    {
        var folder = fx.NewFolder();
        var pdf = new PdfBuilder().NewPage();
        pdf.Picture(PdfBuilder.Photo(folder, "scan.png", ImageMagick.MagickFormat.Png, 595, 842), 0, 0, 595, 842);
        var docx = await ToDocxAsync(pdf.Save(Path.Combine(folder, "scan.pdf")));

        Assert.Single(Body(docx).Descendants(Wp + "anchor"), a => (string?)a.Attribute("behindDoc") == "1");
    }

    [Fact]
    public async Task Turned_text_is_kept()
    {
        var pdf = PdfBuilder.WithTurnedText(Path.Combine(fx.NewFolder(), "turned.pdf"),
            ("Upright body text of the page.", 57, 100, false),
            ("Upright second line of the page.", 57, 120, false),
            ("Turned label", 540, 400, true));
        var text = string.Join(" ", BodyTexts(ReadBack(await ToDocxAsync(pdf))));

        Assert.Contains("Upright body text of the page.", text);
        Assert.Matches(new Regex("Turned\\s+label"), text);
    }

    [Fact]
    public async Task Text_export_still_reads_on_without_the_running_head()
    {
        var job = await fx.ConvertAsync([Report(fx.NewFolder())], new Preset { TargetFormat = "txt" });
        var text = await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Quarterly", text);
        Assert.DoesNotContain("of 3", text);
        Assert.Contains("Section 2 Results", text);
    }
}
