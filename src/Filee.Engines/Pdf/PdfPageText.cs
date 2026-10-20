// What Searchable PDF and Watermark share to write text onto an existing page: the page as it is shown (its crop box,
// turned by /Rotate) mapped back into the page's own coordinates, WinAnsi strings for the fonts every PDF reader has, the
// page's resources, and drawing on top of the page's own content.

using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Filee.Engines.Pdf;

internal static class PdfPageText
{
    /// <summary>The standard fonts' encoding (Windows-1252): Latin alphabets; other letters become "?".</summary>
    public static readonly Encoding WinAnsi;

    static PdfPageText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WinAnsi = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    /// <summary>A PDF literal string's inside: \, ( and ) escaped, bytes outside printable ASCII as octal.</summary>
    public static string Escape(byte[] encoded)
    {
        var escaped = new StringBuilder(encoded.Length);
        foreach (var b in encoded)
        {
            if (b is (byte)'\\' or (byte)'(' or (byte)')')
                escaped.Append('\\').Append((char)b);
            else if (b is < 32 or > 126)
                escaped.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else
                escaped.Append((char)b);
        }
        return escaped.ToString();
    }

    /// <summary>Puts <paramref name="value"/> in the page's resources: under <paramref name="category"/> (/Font, /ExtGState) as <paramref name="key"/>.</summary>
    public static void AddResource(PdfPage page, string category, string key, PdfDictionary value)
    {
        var resources = page.Resources;
        if (resources.Elements.GetDictionary(category) is not { } entries)
        {
            entries = new PdfDictionary(page.Owner);
            resources.Elements[category] = entries;
        }
        entries.Elements[key] = value.Reference;
    }

    /// <summary>
    /// Draws <paramref name="content"/> (PDF operators) over the page. The page's own drawing may leave the coordinate
    /// system changed: it is wrapped in q/Q so this starts from the page's coordinates.
    /// </summary>
    public static void Append(PdfPage page, string content)
    {
        page.Contents.PrependContent().CreateStream("q\n"u8.ToArray());
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("Q\n" + content));
    }
}

/// <summary>
/// A page as it is shown: its crop box (the media box when it has none), turned by /Rotate. Points and directions on it,
/// origin at the bottom left as shown, become points and directions in the page's own coordinates.
/// </summary>
internal readonly struct ShownPage
{
    private readonly double _left, _bottom, _width, _height;
    private readonly int _rotation;

    public ShownPage(PdfPage page)
    {
        var box = page.CropBoxReadOnly.IsEmpty ? page.MediaBoxReadOnly : page.CropBoxReadOnly;
        (_left, _bottom, _width, _height) = (box.X1, box.Y1, box.Width, box.Height);
        _rotation = ((page.Rotate % 360) + 360) % 360;
    }

    /// <summary>Width as shown: the height when the page is turned a quarter.</summary>
    public double Width => _rotation is 90 or 270 ? _height : _width;

    public double Height => _rotation is 90 or 270 ? _width : _height;

    /// <summary>The point (x, y) of the page as shown, in the page's coordinates.</summary>
    public (double X, double Y) Point(double x, double y)
    {
        var (px, py) = _rotation switch
        {
            90 => (_width - y, x),
            180 => (_width - x, _height - y),
            270 => (y, _height - x),
            _ => (x, y),
        };
        return (_left + px, _bottom + py);
    }

    /// <summary>A direction on the page as shown, in the page's coordinates.</summary>
    public (double X, double Y) Direction(double x, double y) => _rotation switch
    {
        90 => (-y, x),
        180 => (-x, -y),
        270 => (y, -x),
        _ => (x, y),
    };

    /// <summary>
    /// The text matrix ("a b c d e f Tm") for text starting at (x, y) on the page as shown and running at
    /// <paramref name="angle"/> radians from its horizontal (0: along the line, as read).
    /// </summary>
    public string TextMatrix(double x, double y, double angle = 0)
    {
        var run = Direction(Math.Cos(angle), Math.Sin(angle));
        var up = Direction(-Math.Sin(angle), Math.Cos(angle));
        var origin = Point(x, y);
        return string.Create(CultureInfo.InvariantCulture,
            $"{run.X:0.####} {run.Y:0.####} {up.X:0.####} {up.Y:0.####} {origin.X:0.###} {origin.Y:0.###} Tm");
    }
}
