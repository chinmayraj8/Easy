// "Searchable PDF": a scanned page is only a picture, so nothing in it can be searched or selected. Each such page is
// drawn at 200 dpi and read with macOS Vision (TextRecognizer); every line it finds is written over the page as
// invisible text (text render mode 3) where the line is, the way scanners' OCR does it. The page itself is not touched:
// it looks exactly as before. Pages that already have text are left alone.
//
// The text uses Helvetica, one of the fonts every PDF reader has, so nothing is embedded; it covers the Latin
// alphabets (WinAnsi), other letters become "?". Its widths are declared all alike and each line is stretched (Tz) to
// the width of its box, so a selection covers the line as printed.

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Ocr;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;

namespace Filee.Engines.Pdf;

internal static class SearchablePdf
{
    private const int Dpi = 200;
    private const string FontKey = "/EasyOcrText";
    private const int GlyphWidth = 500; // per mille of the font size, for every character

    /// <summary>A page with at least this many letters already has text: it is not read again.</summary>
    private const int TextLetters = 20;

    public static IReadOnlyList<string> Write(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        if (!TextRecognizer.IsAvailable)
            throw new ConversionRefusedException("error.needs_macos");

        var bytes = File.ReadAllBytes(step.InputPath);
        using var document = PdfSharpConverter.Open(step.InputPath, PdfDocumentOpenMode.Modify);
        var scanned = ScannedPages(bytes, document.PageCount);
        if (scanned.Count == 0)
            step.Notice?.Invoke("notice.already_searchable");

        var scratch = Path.Combine(step.WorkDirectory, "ocr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        PdfDictionary? font = null;
        var found = 0;
        var options = new RenderOptions(Dpi: Dpi, WithAnnotations: false, WithFormFill: false, BackgroundColor: SKColors.White);
        for (var i = 0; i < scanned.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var index = scanned[i];
            var picture = Path.Combine(scratch, $"page-{index + 1}.png");
#pragma warning disable CA1416 // TextRecognizer.IsAvailable: this is macOS, where PDFium ships
            using (var bitmap = Conversion.ToImage(bytes, page: index, password: null, options))
#pragma warning restore CA1416
            using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                File.WriteAllBytes(picture, data.ToArray());

            var lines = TextRecognizer.RecognizeLines(picture).Where(l => l.Text.Trim().Length > 0).ToList();
            File.Delete(picture);
            if (lines.Count > 0)
            {
                font ??= AddFont(document);
                AddText(document.Pages[index], font, lines);
                found += lines.Count;
            }
            progress?.Report((i + 1.0) / scanned.Count);
        }
        if (scanned.Count > 0 && found == 0)
            step.Notice?.Invoke("notice.no_text_found");

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        document.Save(output);
        return [output];
    }

    /// <summary>The pages (zero-based) without text of their own: pictures of pages.</summary>
    private static List<int> ScannedPages(byte[] bytes, int pageCount)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(bytes);
        return Enumerable.Range(0, pageCount)
            .Where(i => pig.GetPage(i + 1).Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) < TextLetters)
            .ToList();
    }

    /// <summary>Helvetica in WinAnsi with every character as wide (see <see cref="GlyphWidth"/>), shared by every page.</summary>
    private static PdfDictionary AddFont(PdfDocument document)
    {
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        font.Elements.SetInteger("/FirstChar", 32);
        font.Elements.SetInteger("/LastChar", 255);
        var widths = new PdfArray(document);
        for (var c = 32; c <= 255; c++)
            widths.Elements.Add(new PdfInteger(GlyphWidth));
        font.Elements["/Widths"] = widths;
        document.Internals.AddObject(font);
        return font;
    }

    /// <summary>
    /// Writes the lines over the page, invisible. Vision's boxes are fractions of the page as shown (turned by
    /// /Rotate, cut to the crop box); they are mapped back into the page's own coordinates.
    /// </summary>
    private static void AddText(PdfPage page, PdfDictionary font, IReadOnlyList<RecognizedLine> lines)
    {
        PdfPageText.AddResource(page, "/Font", FontKey, font);
        var shown = new ShownPage(page);
        var text = new StringBuilder("BT 3 Tr\n");
        foreach (var line in lines)
        {
            var size = line.Height * shown.Height * 0.8; // the box holds ascenders and descenders
            if (size <= 0.5)
                continue;
            var encoded = PdfPageText.WinAnsi.GetBytes(line.Text.Trim());
            var stretch = line.Width * shown.Width / (encoded.Length * GlyphWidth / 1000.0 * size) * 100;
            // Baseline a fifth of the box above its bottom.
            var matrix = shown.TextMatrix(line.X * shown.Width, (line.Y + line.Height * 0.2) * shown.Height);
            text.Append(CultureInfo.InvariantCulture, $"{FontKey} {size:0.###} Tf {stretch:0.###} Tz {matrix} (")
                .Append(PdfPageText.Escape(encoded)).Append(") Tj\n");
        }
        PdfPageText.Append(page, text.Append("ET\n").ToString());
    }
}
