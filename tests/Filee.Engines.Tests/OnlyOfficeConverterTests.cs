// Easy: Word, PowerPoint and Excel → PDF through an installed ONLYOFFICE (macOS). Skips where it isn't installed.
// Word-family inputs come from macOS's textutil, so a Times and Courier document is a real DOCX/DOC/RTF/ODT.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public partial class OnlyOfficeConverterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly OnlyOfficeConverter Engine = new();

    private static void SkipUnlessInstalled() =>
        Assert.SkipUnless(Engine.GetStatus().IsAvailable, "ONLYOFFICE not installed (macOS only)");

    /// <summary>A two-font RTF (Times New Roman heading, Courier New line) turned into <paramref name="format"/> by textutil.</summary>
    private string MakeDocument(string format)
    {
        var folder = fx.NewFolder();
        var rtf = Path.Combine(folder, "paper.rtf");
        File.WriteAllText(rtf,
            @"{\rtf1\ansi{\fonttbl{\f0\froman Times New Roman;}{\f1\fmodern Courier New;}}" +
            @"\f0\fs32\b Cost-Aware Predictive Autoscaling\b0\par\f1\fs20 someone@example.com\par}");
        if (format == "rtf")
            return rtf;
        var output = Path.Combine(folder, "paper." + format);
        using var textutil = Process.Start("/usr/bin/textutil", ["-convert", format, rtf, "-output", output])!;
        textutil.WaitForExit();
        Assert.Equal(0, textutil.ExitCode);
        return output;
    }

    private async Task<string> ConvertAsync(string input, string from)
    {
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "out.pdf");
        var before = await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken);
        var written = await Engine.ConvertAsync(
            new ConversionStep(input, from, "pdf", new Preset { TargetFormat = "pdf" }, new FixedOutput(output), folder),
            null, TestContext.Current.CancellationToken);

        Assert.Equal([output], written);
        Assert.StartsWith("%PDF-", File.ReadAllText(output)[..8]);
        Assert.Equal(before, await File.ReadAllBytesAsync(input, TestContext.Current.CancellationToken)); // input untouched
        return output;
    }

    private static IReadOnlyList<string> Fonts(string pdf) =>
        BaseFont().Matches(File.ReadAllText(pdf)).Select(m => m.Groups[1].Value).Distinct().ToList();

    [Theory]
    [InlineData("docx")]
    [InlineData("doc")]
    [InlineData("rtf")]
    [InlineData("odt")]
    public async Task Word_documents_keep_their_fonts(string format)
    {
        SkipUnlessInstalled();
        var pdf = await ConvertAsync(MakeDocument(format), format);

        var fonts = Fonts(pdf);
        Assert.Contains(fonts, f => f.Contains("Times", StringComparison.Ordinal));
        Assert.Contains(fonts, f => f.Contains("Courier", StringComparison.Ordinal)); // not swapped for a sans font
    }

    /// <summary>ONLYOFFICE locks the documents it has open; converting one of those failed before.</summary>
    [Fact]
    public async Task A_document_open_and_locked_in_another_app_converts()
    {
        SkipUnlessInstalled();
        var docx = MakeDocument("docx");
        await using var held = new FileStream(docx, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // exclusive lock

        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "out.pdf");
        var written = await Engine.ConvertAsync(
            new ConversionStep(docx, "docx", "pdf", new Preset { TargetFormat = "pdf" }, new FixedOutput(output), folder),
            null, TestContext.Current.CancellationToken);

        Assert.Equal([output], written);
    }

    [Fact]
    public async Task Presentations_get_one_page_per_slide()
    {
        SkipUnlessInstalled();
        var pptx = new PptxBuilder();
        pptx.Slide(pptx.Title("First"));
        pptx.Slide(pptx.Title("Second"));
        var pdf = await ConvertAsync(pptx.Save(Path.Combine(fx.NewFolder(), "deck.pptx")), "pptx");

        if (OperatingSystem.IsMacOS())
            Assert.Equal(2, PDFtoImage.Conversion.GetPageCount(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Spreadsheets_convert()
    {
        SkipUnlessInstalled();
        var xlsx = new XlsxBuilder();
        xlsx.Sheet("Sales", XlsxBuilder.Row(1, xlsx.Text("A1", "Region"), XlsxBuilder.Number("B1", 42)));
        await ConvertAsync(xlsx.Save(Path.Combine(fx.NewFolder(), "sales.xlsx")), "xlsx");
    }

    [Fact]
    public void Word_to_pdf_takes_the_direct_route_when_installed()
    {
        SkipUnlessInstalled();
        var route = new RoutePlanner(fx.Converters.Append(Engine).Where(c => c.GetStatus().IsAvailable)).Plan("docx", "pdf");

        Assert.NotNull(route);
        Assert.Equal("onlyoffice", Assert.Single(route.Steps).Converter.Id); // not DOCX → HWPX → PDF
    }

    [Fact]
    public void Settings_show_the_installed_version()
    {
        SkipUnlessInstalled();
        Assert.Matches(@"^\d+\.\d+", Engine.GetStatus().Version);
    }

    [Fact]
    public void The_task_file_escapes_paths()
    {
        var task = OnlyOfficeConverter.TaskFile("/tmp/a & b/in.docx", "/tmp/<out>.pdf", "pdf", "/themes").ToString();

        Assert.Contains("<m_sFileFrom>/tmp/a &amp; b/in.docx</m_sFileFrom>", task);
        Assert.Contains("<m_sFileTo>/tmp/&lt;out&gt;.pdf</m_sFileTo>", task);
        Assert.Contains("<m_nFormatTo>513</m_nFormatTo>", task);
        Assert.DoesNotContain("m_oTextParams", task); // only PDF sources are regrouped
    }

    [Fact]
    public void A_pdf_source_asks_for_its_lines_with_positions()
    {
        var task = OnlyOfficeConverter.TaskFile("/tmp/in.pdf", "/tmp/out.docx", "docx", "/themes").ToString();

        Assert.Contains("<m_nFormatTo>65</m_nFormatTo>", task);
        Assert.Contains("<m_nTextAssociationType>2</m_nTextAssociationType>", task);
    }

    [Fact]
    public async Task Pdf_to_docx_keeps_the_text_and_fonts()
    {
        SkipUnlessInstalled();
        var pdf = await ConvertAsync(MakeDocument("docx"), "docx");
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "out.docx");

        var written = await Engine.ConvertAsync(
            new ConversionStep(pdf, "pdf", "docx", new Preset { TargetFormat = "docx" }, new FixedOutput(output), folder),
            null, TestContext.Current.CancellationToken);

        Assert.Equal([output], written);
        using var zip = System.IO.Compression.ZipFile.OpenRead(output);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var text = Regex.Replace(xml, "<[^>]+>", "");
        Assert.Contains("Cost-Aware Predictive Autoscaling", text);
        Assert.Contains("someone@example.com", text);
        Assert.DoesNotContain("txbxContent", xml); // flowing paragraphs, no text boxes
        Assert.DoesNotContain("wp:anchor", xml);
        // ONLYOFFICE names fonts in character styles; the font table lists those in use: the typewriter line kept its font.
        using var fonts = new StreamReader(zip.GetEntry("word/fontTable.xml")!.Open());
        var table = await fonts.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("w:name=\"Courier New\"", table);
        Assert.Contains("w:name=\"Times New Roman\"", table);
    }

    [Fact]
    public void Pdf_to_docx_prefers_onlyoffice_until_the_docx_writer_is_moved_up()
    {
        SkipUnlessInstalled();
        var converters = fx.Converters.Append(Engine).Where(c => c.GetStatus().IsAvailable).ToList();
        RoutePlanner Planner(List<string> priority) =>
            new(converters, c => Math.Min(priority.IndexOf(c.Id) is >= 0 and var i ? i : priority.Count, 9));

        var defaults = new Filee.Core.Settings.AppSettings().EnginePriority;
        Assert.Equal("onlyoffice", Assert.Single(Planner(defaults).Plan("pdf", "docx")!.Steps).Converter.Id);

        List<string> writerFirst = ["docx-writer", .. defaults.Where(id => id != "docx-writer")];
        Assert.Equal("docx-writer", Assert.Single(Planner(writerFirst).Plan("pdf", "docx")!.Steps).Converter.Id);
    }

    private sealed class FixedOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }

    [GeneratedRegex(@"/BaseFont\s*/(?:[A-Z]{6}\+)?([A-Za-z-]+)")]
    private static partial Regex BaseFont();
}
