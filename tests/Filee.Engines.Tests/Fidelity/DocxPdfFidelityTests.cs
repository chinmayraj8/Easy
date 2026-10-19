// Easy: DOCX → PDF fidelity with ONLYOFFICE, and the checks of PdfFidelity itself. Skips where ONLYOFFICE isn't installed.
//
// To check a document against a reference PDF (e.g. ONLYOFFICE's own export, or Word's), run:
//   FIDELITY_DOCX=paper.docx FIDELITY_REFERENCE=reference.pdf FIDELITY_OUT=report-folder \
//     dotnet test tests/Filee.Engines.Tests --filter FullyQualifiedName~Supplied_document
// The folder gets output.pdf, report.md (renderer, version, settings, fonts, differences) and one overlay per page.

using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;

namespace Filee.Engines.Tests.Fidelity;

[SupportedOSPlatform("macos")]
public partial class DocxPdfFidelityTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly OnlyOfficeConverter Engine = new();

    private static string AllFonts => Path.Combine(OnlyOfficeConverter.FontFolder, "AllFonts.js");

    private static void SkipUnlessInstalled() =>
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Engine.GetStatus().IsAvailable, "ONLYOFFICE not installed (macOS only)");

    /// <summary>Distinct, searchable words: paragraph <c>n</c> holds "alpha{n}" … so a lost paragraph is identifiable.</summary>
    private static string Sentence(int n) =>
        $"Paragraph alpha{n} describes measurement{n} with enough ordinary words to wrap over two lines on the page, " +
        $"including figures such as {n}.25 and {n * 3}% so that numbers are compared as well.";

    /// <summary>A Times New Roman document of <paramref name="paragraphs"/>, a small table, one font it may not have.</summary>
    private string MakeDocx(IEnumerable<int> paragraphs, string? missingFont = null, string page = DocxBuilder.DefaultPage)
    {
        var docx = new DocxBuilder { SectionProperties = page };
        docx.Styles = docx.Styles.Replace("w:ascii=\"맑은 고딕\" w:eastAsia=\"맑은 고딕\" w:hAnsi=\"맑은 고딕\"", "w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\"", StringComparison.Ordinal);
        docx.Paragraph("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>1 Introduction</w:t></w:r></w:p>");
        foreach (var n in paragraphs)
            docx.Paragraph($"<w:p><w:r><w:t>{Sentence(n)}</w:t></w:r></w:p>");
        // Cell widths as Word writes them: without w:tcW, ONLYOFFICE shrinks the columns to their text (the grid is ignored).
        docx.Paragraph("""
            <w:tbl><w:tblPr><w:tblStyle w:val="TableGrid"/></w:tblPr><w:tblGrid><w:gridCol w:w="3000"/><w:gridCol w:w="3000"/></w:tblGrid>
            <w:tr><w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>Cost</w:t></w:r></w:p></w:tc><w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>0.207</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>Changes</w:t></w:r></w:p></w:tc><w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>119.8</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """);
        // A 3 × 1.5 inch picture (216 × 108 pt).
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.SteelBlue, 240, 120))
            docx.Paragraph($"<w:p>{DocxBuilder.InlinePicture(docx.Image(image.ToByteArray(ImageMagick.MagickFormat.Png)), 2743200, 1371600)}</w:p>");
        docx.Paragraph("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>2 Conclusion</w:t></w:r></w:p>");
        if (missingFont is not null)
            docx.Paragraph($"<w:p><w:r><w:rPr><w:rFonts w:ascii=\"{missingFont}\" w:hAnsi=\"{missingFont}\"/></w:rPr><w:t>Set in a font this Mac lacks.</w:t></w:r></w:p>");
        return docx.Save(Path.Combine(fx.NewFolder(), "doc.docx"));
    }

    private async Task<string> ToPdfAsync(string docx)
    {
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "out.pdf");
        await Engine.ConvertAsync(new ConversionStep(docx, "docx", "pdf", new Preset { TargetFormat = "pdf" }, new FixedOutput(output), folder),
            null, TestContext.Current.CancellationToken);
        return output;
    }

    [Fact]
    public async Task Two_renderings_of_one_document_are_identical()
    {
        SkipUnlessInstalled();
        var docx = MakeDocx(Enumerable.Range(1, 30));

        var report = PdfFidelity.Compare(await ToPdfAsync(docx), await ToPdfAsync(docx), fx.NewFolder());

        Assert.False(report.ContentDiffers, PdfFidelity.Markdown(report));
        Assert.False(report.LayoutDiffers, PdfFidelity.Markdown(report));
        Assert.Equal(1, report.SequenceSimilarity);
        Assert.True(report.Reference.Count >= 2, "the document spans pages");
    }

    [Fact]
    public async Task A_removed_paragraph_is_missing_and_shifts_what_follows()
    {
        SkipUnlessInstalled();
        var reference = await ToPdfAsync(MakeDocx(Enumerable.Range(1, 30)));
        var output = await ToPdfAsync(MakeDocx(Enumerable.Range(1, 30).Where(n => n != 5)));

        var report = PdfFidelity.Compare(reference, output, fx.NewFolder());

        Assert.True(report.ContentDiffers);
        Assert.Contains("alpha5", report.MissingWords);
        Assert.Contains("measurement5", report.MissingWords);
        Assert.Contains("5.25", report.MissingNumbers);
        Assert.Empty(report.ExtraWords);
        Assert.Equal("shifted or changed content", report.Visual[0].Verdict); // later lines moved up
        Assert.True(File.Exists(report.Visual[0].Overlay));
    }

    [Fact]
    public async Task Output_keeps_page_size_picture_size_fonts_and_text()
    {
        SkipUnlessInstalled();
        const string Letter = "<w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>";
        var paragraphs = Enumerable.Range(1, 12).ToList();
        var pdf = await ToPdfAsync(MakeDocx(paragraphs, page: Letter));

        var facts = PdfFidelity.Facts(pdf);
        Assert.All(facts, page => Assert.Equal((612, 792), (page.Width, page.Height))); // 12240 × 15840 twips
        var bytes = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken));
        // Every font has its program embedded: one FontFile per font descriptor (a Type0 font names its font twice).
        Assert.True(FontDescriptor().Matches(bytes).Count > 0);
        Assert.Equal(FontDescriptor().Matches(bytes).Count, FontFile().Matches(bytes).Count);

        var picture = Assert.Single(facts.SelectMany(f => f.Images)); // found even when painted as a pattern
        Assert.Equal((216, 108), (picture.Width, picture.Height));

        var expected = PdfFidelity.Words([new PdfFidelity.PageFacts(1, 0, 0,
            [.. paragraphs.Select(n => new PdfFidelity.Line(Sentence(n), 0)), new PdfFidelity.Line("Cost 0.207 Changes 119.8", 0)], [])]);
        var actual = PdfFidelity.Words(facts);
        var lost = expected.Except(actual).ToList();
        Assert.True(lost.Count == 0, $"lost {string.Join(" ", lost)}; last lines: {string.Join(" | ", facts.SelectMany(f => f.Lines).TakeLast(6).Select(l => l.Text))}");
    }

    [Fact]
    public async Task Missing_fonts_are_reported_with_the_text_they_affect()
    {
        SkipUnlessInstalled();
        var docx = MakeDocx([1, 2], missingFont: "Easy Missing Font");
        var pdf = await ToPdfAsync(docx);

        var check = OnlyOfficeConverter.CheckFonts(docx, pdf, AllFonts);

        Assert.Contains(check.Requested, r => r.Font == "Times New Roman" && r.Characters > 100);
        var missing = Assert.Single(check.Missing);
        Assert.Equal(("Easy Missing Font", "Set in a font this Mac lacks.".Length), missing);
        // Whatever stood in for it was drawn with a font the document never asked for, and is named.
        Assert.NotEmpty(check.Fallbacks);
        Assert.DoesNotContain("Times New Roman", check.Fallbacks.Keys);
    }

    [Fact]
    public async Task A_conversion_records_its_renderer_settings_and_fonts()
    {
        SkipUnlessInstalled();
        var log = new RecordingLogger();
        var notices = new List<string>();
        var docx = MakeDocx([1], missingFont: "Easy Missing Font");
        var folder = fx.NewFolder();
        await new OnlyOfficeConverter(log).ConvertAsync(new ConversionStep(docx, "docx", "pdf", new Preset { TargetFormat = "pdf" },
            new FixedOutput(Path.Combine(folder, "out.pdf")), folder, Notice: notices.Add), null, TestContext.Current.CancellationToken);

        var record = Assert.Single(log.Messages);
        Assert.Matches(@"DOCX → PDF with ONLYOFFICE/\d+\.\d+", record); // the PDF's producer: renderer and exact version
        Assert.Contains("format 0x0201", record);
        Assert.Contains("Times New Roman (", record);
        Assert.Contains("Missing (substituted): Easy Missing Font (29 chars)", record);
        Assert.Equal(["notice.fonts_substituted"], notices);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<OnlyOfficeConverter>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    /// <summary>Opt-in: a real document against a reference PDF (see the file comment). Fails when content differs.</summary>
    [Fact]
    public async Task Supplied_document_against_its_reference()
    {
        SkipUnlessInstalled();
        var (docx, reference) = (Environment.GetEnvironmentVariable("FIDELITY_DOCX"), Environment.GetEnvironmentVariable("FIDELITY_REFERENCE"));
        Assert.SkipWhen(docx is null || reference is null, "set FIDELITY_DOCX and FIDELITY_REFERENCE");
        var folder = Environment.GetEnvironmentVariable("FIDELITY_OUT") ?? fx.NewFolder();
        Directory.CreateDirectory(folder);

        // Work on copies, so the originals are only ever read: cp, because a document open in ONLYOFFICE is locked
        // against .NET's reads (see OnlyOfficeConverter).
        var source = Path.Combine(folder, "source.docx");
        using (var cp = System.Diagnostics.Process.Start("/bin/cp", [docx!, source])!)
            await cp.WaitForExitAsync(TestContext.Current.CancellationToken);
        docx = source;
        var output = Path.Combine(folder, "output.pdf");
        File.Copy(await ToPdfAsync(docx), output, overwrite: true);
        var report = PdfFidelity.Compare(reference!, output, folder);
        var fonts = OnlyOfficeConverter.CheckFonts(docx!, output, AllFonts);
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(output))
        using (var refPdf = UglyToad.PdfPig.PdfDocument.Open(reference!))
            await File.WriteAllTextAsync(Path.Combine(folder, "report.md"), $"""
                # DOCX → PDF fidelity

                - Document: {docx}
                - Reference: {reference} (producer: {refPdf.Information.Producer})
                - Output: {output} (producer: {pdf.Information.Producer}; converter {OnlyOfficeConverter.Locate()?.Converter}, bundle {Engine.GetStatus().Version})
                - Export settings: x2t m_nFormatTo 0x0201 (PDF), PDF/A off, fonts embedded as subsets; font list {AllFonts}
                - Rendered for comparison with PDFium at 100 dpi
                - Fonts requested by the text: {string.Join(", ", fonts.Requested.Select(r => $"{r.Font} ({r.Characters} chars)"))}
                - Fonts not installed (substituted): {(fonts.Missing.Count == 0 ? "none" : string.Join(", ", fonts.Missing.Select(m => $"{m.Font} ({m.Characters} chars)")))}
                - Glyph fallbacks: {(fonts.Fallbacks.Count == 0 ? "none" : string.Join(", ", fonts.Fallbacks.Select(f => $"{f.Key} for \"{f.Value}\"")))}

                {PdfFidelity.Markdown(report)}
                """, TestContext.Current.CancellationToken);

        Assert.False(report.ContentDiffers, PdfFidelity.Markdown(report));
    }

    /// <summary>
    /// Opt-in: PDF → DOCX judged by rendering the DOCX back to PDF and comparing it with the original
    /// (FIDELITY_PDF, FIDELITY_OUT). The folder gets rebuilt.docx, roundtrip.pdf, report.md and overlays.
    /// </summary>
    [Fact]
    public async Task Supplied_pdf_round_trip()
    {
        SkipUnlessInstalled();
        var original = Environment.GetEnvironmentVariable("FIDELITY_PDF");
        Assert.SkipWhen(original is null, "set FIDELITY_PDF");
        var folder = Environment.GetEnvironmentVariable("FIDELITY_OUT") ?? fx.NewFolder();
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.pdf");
        using (var cp = System.Diagnostics.Process.Start("/bin/cp", [original!, source])!)
            await cp.WaitForExitAsync(TestContext.Current.CancellationToken);

        var docx = Path.Combine(folder, "rebuilt.docx");
        await Engine.ConvertAsync(new ConversionStep(source, "pdf", "docx", new Preset { TargetFormat = "docx" }, new FixedOutput(docx), fx.NewFolder()),
            null, TestContext.Current.CancellationToken);
        var roundTrip = Path.Combine(folder, "roundtrip.pdf");
        File.Copy(await ToPdfAsync(docx), roundTrip, overwrite: true);

        var report = PdfFidelity.Compare(source, roundTrip, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "report.md"),
            $"# PDF → DOCX round trip\n\n- Original: {original}\n- Rebuilt DOCX rendered back with ONLYOFFICE ({Engine.GetStatus().Version})\n\n{PdfFidelity.Markdown(report)}",
            TestContext.Current.CancellationToken);
        // A rebuilt document reflows: pages may differ by a line or two (hyphenation settings aren't in a PDF), and
        // sub- and superscripts extract differently once they are real runs. Text order, numbers and headings must hold.
        var details = PdfFidelity.Markdown(report);
        Assert.InRange(report.Output.Count, report.Reference.Count - 1, report.Reference.Count + 1);
        Assert.True(report.SequenceSimilarity >= 0.97, details);
        Assert.True(report.MissingNumbers.Count <= 2, details);
        Assert.True(report.MovedHeadings.Count == 0, details);
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }

    [GeneratedRegex(@"/Type\s*/FontDescriptor")]
    private static partial Regex FontDescriptor();

    [GeneratedRegex(@"/FontFile[23]?\s")]
    private static partial Regex FontFile();
}
