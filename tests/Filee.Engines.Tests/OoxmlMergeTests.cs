// "Merge DOCX" / "Merge PPTX": the merged package passes the Open XML SDK validator and holds every file's content,
// with lists, footnotes and slide masters carried over.

using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class OoxmlMergeTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace W = DocxAssert.W;

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    private static Preset Merge(string format) => BuiltInData.OfficeMergePresets().Single(p => p.TargetFormat == format);

    private async Task<string> DocxAsync(string folder, string name, string markdown)
    {
        var md = Path.Combine(folder, name + ".md");
        await File.WriteAllTextAsync(md, markdown, TestContext.Current.CancellationToken);
        var job = await fx.ConvertAsync([md], new Preset { TargetFormat = "docx" });
        AssertDone(job);
        return job.Outputs.Single();
    }

    [Fact]
    public async Task Docx_files_merge_into_one_valid_document_with_sections_lists_and_footnotes()
    {
        var folder = fx.NewFolder();
        var first = await DocxAsync(folder, "first", "# One\n\n1. alpha\n2. beta\n\nNoted.[^a]\n\n[^a]: First note.\n");
        var second = await DocxAsync(folder, "second", "# Two\n\n1. gamma\n2. delta\n\nAlso noted.[^b]\n\n[^b]: Second note.\n");

        var job = await fx.ConvertAsync([first, second], Merge("docx"));

        AssertDone(job);
        var merged = job.Outputs.Single();
        Assert.Equal("first_merged.docx", Path.GetFileName(merged));
        DocxAssert.ValidPackage(merged);
        var body = DocxAssert.Body(merged);
        var text = string.Concat(body.Descendants(W + "t").Select(t => t.Value));
        Assert.Contains("alpha", text);
        Assert.Contains("gamma", text);
        Assert.True(text.IndexOf("beta", StringComparison.Ordinal) < text.IndexOf("gamma", StringComparison.Ordinal));
        // Two sections: the first file's ends in a paragraph, the second's is the body's own.
        Assert.Single(body.Descendants(W + "pPr").Elements(W + "sectPr"));
        // The second list doesn't continue the first: its paragraphs use another list id.
        var listIds = body.Descendants(W + "numId").Select(n => (string)n.Attribute(W + "val")!).Distinct().ToList();
        Assert.True(listIds.Count >= 2, string.Join(",", listIds));
        using var document = WordprocessingDocument.Open(merged, false);
        var notes = document.MainDocumentPart!.FootnotesPart!.Footnotes!.InnerText;
        Assert.Contains("First note.", notes);
        Assert.Contains("Second note.", notes);
    }

    [Fact]
    public async Task Pptx_files_merge_into_one_valid_presentation_with_every_slide()
    {
        async Task<string> Deck(int pages)
        {
            var pdf = PdfSlidesTests.MakePdf(fx.NewFolder(), Enumerable.Repeat((960.0, 540.0), pages).ToArray());
            var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "pptx" });
            AssertDone(job);
            return job.Outputs.Single();
        }
        var first = await Deck(2);
        var second = await Deck(3);

        var job = await fx.ConvertAsync([first, second], Merge("pptx"));

        AssertDone(job);
        var merged = job.Outputs.Single();
        PdfSlidesTests.ValidPackage(merged);
        using var presentation = PresentationDocument.Open(merged, false);
        var part = presentation.PresentationPart!;
        Assert.Equal(5, part.Presentation!.SlideIdList!.Count());
        Assert.Equal(2, part.Presentation.SlideMasterIdList!.Count());
        var ids = part.Presentation.SlideMasterIdList.Elements<DocumentFormat.OpenXml.Presentation.SlideMasterId>().Select(m => m.Id!.Value)
            .Concat(part.SlideMasterParts.SelectMany(m => m.SlideMaster!.SlideLayoutIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideLayoutId>()).Select(l => l.Id!.Value))
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
