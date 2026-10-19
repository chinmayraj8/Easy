// Easy: PdfFidelity's visual verdicts on drawn pages: a sub-pixel offset is antialiasing, a moved line is a shift, a
// deleted line is missing content.

using System.Runtime.Versioning;
using SkiaSharp;

namespace Filee.Engines.Tests.Fidelity;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
public class PdfFidelityTests
{
    private static readonly string Folder = Directory.CreateTempSubdirectory("fidelity-").FullName;

    /// <summary>A white "page" with three lines of text, moved down by <paramref name="dy"/> pixels.</summary>
    private static SKBitmap Page(float dy, bool withoutMiddleLine = false)
    {
        var bitmap = new SKBitmap(600, 400);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        // Sub-pixel placement without baseline snapping, so 0.4 px really moves the antialiased edges.
        using var font = new SKFont(SKTypeface.Default, 18) { Subpixel = true, BaselineSnap = false, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        string[] lines = ["The first line of the page", "A middle line that may go missing", "The last line of the page"];
        for (var i = 0; i < lines.Length; i++)
            if (!(withoutMiddleLine && i == 1))
                canvas.DrawText(lines[i], 40, 100 + i * 60 + dy, font, paint);
        return bitmap;
    }

    [Fact]
    public void The_same_page_is_identical()
    {
        using var a = Page(0);
        using var b = Page(0);
        Assert.Equal(PdfFidelity.Identical, PdfFidelity.ComparePage(1, a, b, Folder).Verdict);
    }

    [Fact]
    public void A_sub_pixel_offset_is_antialiasing()
    {
        using var a = Page(0);
        using var b = Page(0.4f);
        Assert.False(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), "the pixels differ");
        Assert.Equal(PdfFidelity.Antialiasing, PdfFidelity.ComparePage(1, a, b, Folder).Verdict);
    }

    [Fact]
    public void Moved_text_is_a_shift_with_its_region_marked()
    {
        using var a = Page(0);
        using var b = Page(6);
        var page = PdfFidelity.ComparePage(1, a, b, Folder);

        Assert.Equal("shifted or changed content", page.Verdict);
        Assert.NotEmpty(page.Regions);
        Assert.All(page.Regions, r => Assert.InRange(r.Top, 50, 260)); // around the text, not the empty page
    }

    [Fact]
    public void A_deleted_line_is_missing_content()
    {
        using var a = Page(0);
        using var b = Page(0, withoutMiddleLine: true);
        var page = PdfFidelity.ComparePage(1, a, b, Folder);

        Assert.Equal("content missing in output", page.Verdict);
        Assert.Contains(page.Regions, r => r.Top < 160 && r.Bottom > 150); // the middle line at y ≈ 160
    }
}
