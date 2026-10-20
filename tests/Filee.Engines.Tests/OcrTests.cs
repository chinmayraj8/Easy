// Text recognition with macOS Vision: the lines of a printed page and where each one is on it, and "Searchable PDF",
// which lays them invisibly over a scanned page.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Ocr;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public sealed class OcrTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly Preset Searchable = new() { TargetFormat = "pdf", Pdf = { MakeSearchable = true } };

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    /// <summary>A printed page as a picture: text → PDF (cupsfilter) → JPEG (sips), both part of macOS.</summary>
    internal static string PrintedPage(string folder, string text, string format = "jpeg", string extension = "jpg")
    {
        File.WriteAllText(Path.Combine(folder, "t.txt"), text);
        Run("/bin/sh", "-c", $"cd '{folder}' && /usr/sbin/cupsfilter -o cpi=4 -o lpi=2 t.txt > t.pdf 2>/dev/null && /usr/bin/sips -s format {format} t.pdf --out t.{extension} >/dev/null");
        return Path.Combine(folder, $"t.{extension}");
    }

    [Fact]
    public void Printed_lines_are_read_top_to_bottom_with_their_place_on_the_page()
    {
        if (!TextRecognizer.IsAvailable)
        {
            Assert.Skip("Requires macOS Vision.");
            return;
        }
        var folder = Directory.CreateTempSubdirectory("easy-ocr").FullName;
        try
        {
            var page = PrintedPage(folder, "Hello Easy\nInvoice number 4821\n");

            var lines = TextRecognizer.RecognizeLines(page);

            Assert.Contains("Hello Easy", TextRecognizer.Recognize(page));
            var hello = lines.Single(l => l.Text.Contains("Hello Easy"));
            var invoice = lines.Single(l => l.Text.Contains("4821"));
            Assert.True(hello.Y > invoice.Y, "the first line is higher up (origin at the bottom)");
            Assert.All(lines, l => Assert.True(l.X is >= 0 and < 1 && l.Y is >= 0 and < 1 && l.Width > 0 && l.Height > 0 && l.X + l.Width <= 1.001));
            Assert.True(hello.Y > 0.5, "printed at the top of the page");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_scanned_page_becomes_searchable_and_still_looks_the_same()
    {
        if (!TextRecognizer.IsAvailable)
        {
            Assert.Skip("Requires macOS Vision.");
            return;
        }
        var folder = fx.NewFolder();
        // A scan: the page is only a picture, nothing to search.
        var scan = (await fx.ConvertAsync([PrintedPage(folder, "Hello Easy\nInvoice number 4821\n")], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using (var before = UglyToad.PdfPig.PdfDocument.Open(scan))
            Assert.Empty(before.GetPage(1).Letters);

        var job = await fx.ConvertAsync([scan], Searchable);

        AssertDone(job);
        using var after = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        Assert.Equal(1, after.NumberOfPages);
        var page = after.GetPage(1);
        Assert.Contains("Hello Easy", page.Text);
        Assert.Contains("4821", page.Text);
        Assert.Single(page.GetImages()); // the scan itself is still there, untouched
        var hello = page.Letters.First(l => l.Value == "H");
        Assert.True(hello.StartBaseLine.Y > page.Height / 2, "laid over the line at the top of the page");
        Assert.All(page.Letters, l => Assert.True(l.StartBaseLine.X >= 0 && l.StartBaseLine.X <= page.Width && l.StartBaseLine.Y >= 0 && l.StartBaseLine.Y <= page.Height));

        // The text is invisible: drawn, the page is the same, pixel for pixel.
#pragma warning disable CA1416 // PDFium ships for macOS, where this runs
        using var original = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(scan), page: 0);
        using var searchable = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(job.Outputs.Single()), page: 0);
#pragma warning restore CA1416
        Assert.Equal(original.Bytes, searchable.Bytes);
    }

    [Fact]
    public async Task A_page_scanned_sideways_and_turned_upright_gets_its_text_on_the_page()
    {
        if (!TextRecognizer.IsAvailable)
        {
            Assert.Skip("Requires macOS Vision.");
            return;
        }
        var folder = fx.NewFolder();
        var picture = PrintedPage(folder, "Sideways memo 7310\n");
        Run("/usr/bin/sips", "-r", "270", picture, "--out", picture); // the scanner saw it lying on its side
        var scan = (await fx.ConvertAsync([picture], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using (var document = PdfReader.Open(scan, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Rotate = 90; // shown upright
            document.Save(scan);
        }

        var job = await fx.ConvertAsync([scan], Searchable);

        AssertDone(job);
        using var after = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        var page = after.GetPage(1);
        Assert.Contains("7310", page.Text);
        // PdfPig places letters on the page as shown (upright): the line printed at the top is there, inside the page.
        Assert.All(page.Letters, l => Assert.True(l.StartBaseLine.X >= 0 && l.StartBaseLine.X <= page.Width
            && l.StartBaseLine.Y >= 0 && l.StartBaseLine.Y <= page.Height, $"{l.Value} at {l.StartBaseLine}"));
        Assert.True(page.Letters.First(l => l.Value == "7").StartBaseLine.Y > page.Height / 2, "at the top of the upright page");
    }

    [Fact]
    public async Task A_page_with_text_already_is_left_as_it_is()
    {
        if (!TextRecognizer.IsAvailable)
        {
            Assert.Skip("Requires macOS Vision.");
            return;
        }
        var folder = fx.NewFolder();
        PrintedPage(folder, "Already digital text here\n");
        var digital = Path.Combine(folder, "t.pdf"); // cupsfilter's PDF has real text

        var job = await fx.ConvertAsync([digital], Searchable);

        AssertDone(job);
        Assert.Equal("notice.already_searchable", job.Files.Single().NoticeKey);
        using var before = UglyToad.PdfPig.PdfDocument.Open(digital);
        using var after = UglyToad.PdfPig.PdfDocument.Open(job.Outputs.Single());
        Assert.Equal(before.GetPage(1).Letters.Count, after.GetPage(1).Letters.Count); // no second copy of the text
    }

    private static void Run(string program, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(program, arguments);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
