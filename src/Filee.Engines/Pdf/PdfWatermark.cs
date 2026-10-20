// "Watermark": the same text across the middle of every page, corner to corner, in big see-through grey letters drawn on
// top of the page (a picture on it can't hide them). Helvetica Bold, one of the fonts every PDF reader has, so nothing is
// embedded; Latin alphabets (WinAnsi), other letters become "?". A page shown sideways (/Rotate) gets it across the page
// as it is shown.

using System.Globalization;
using Filee.Core.Conversion;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Filee.Engines.Pdf;

internal static class PdfWatermark
{
    public const string DefaultText = "CONFIDENTIAL";
    private const string FontKey = "/EasyWatermark";
    private const string StateKey = "/EasyWatermarkAlpha";
    private const double Opacity = 0.22;
    private const double Gray = 0.45;
    private const double CapHeight = 0.718; // Helvetica Bold's capitals, per unit of font size

    public static IReadOnlyList<string> Write(ConversionStep step, CancellationToken ct)
    {
        var text = step.Preset.Pdf.WatermarkText.Trim() is { Length: > 0 } given ? given : DefaultText;
        // The text as the font will show it ("?" for letters WinAnsi lacks), measured in Helvetica Bold: wide per point.
        var encoded = PdfPageText.WinAnsi.GetBytes(text);
        var widthPerPoint = WidthPerPoint(PdfPageText.WinAnsi.GetString(encoded));

        using var document = PdfSharpConverter.Open(step.InputPath, PdfDocumentOpenMode.Modify);
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica-Bold");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        document.Internals.AddObject(font);
        var state = new PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState");
        state.Elements.SetReal("/ca", Opacity);
        state.Elements.SetReal("/CA", Opacity);
        document.Internals.AddObject(state);

        foreach (var page in document.Pages)
        {
            ct.ThrowIfCancellationRequested();
            PdfPageText.AddResource(page, "/Font", FontKey, font);
            PdfPageText.AddResource(page, "/ExtGState", StateKey, state);
            var shown = new ShownPage(page);
            // Corner to corner, filling 70 % of the diagonal; a short word no taller than a fifth of the page.
            var angle = Math.Atan2(shown.Height, shown.Width);
            var diagonal = Math.Sqrt(shown.Width * shown.Width + shown.Height * shown.Height);
            var size = Math.Min(diagonal * 0.7 / widthPerPoint, Math.Min(shown.Width, shown.Height) * 0.2);
            var width = widthPerPoint * size;
            // From the middle, back half the text along it and down half its capitals across it.
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            var x = shown.Width / 2 - cos * width / 2 + sin * size * CapHeight / 2;
            var y = shown.Height / 2 - sin * width / 2 - cos * size * CapHeight / 2;
            PdfPageText.Append(page, string.Create(CultureInfo.InvariantCulture,
                $"q {StateKey} gs {Gray:0.##} g BT {FontKey} {size:0.##} Tf {shown.TextMatrix(x, y, angle)} ({PdfPageText.Escape(encoded)}) Tj ET Q\n"));
        }

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        document.Save(output);
        return [output];
    }

    /// <summary>How wide <paramref name="text"/> is in Helvetica Bold at a size of 1 point.</summary>
    private static double WidthPerPoint(string text)
    {
        using var typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var measure = new SKFont(typeface, 1000);
        return Math.Max(measure.MeasureText(text), 1) / 1000;
    }
}
