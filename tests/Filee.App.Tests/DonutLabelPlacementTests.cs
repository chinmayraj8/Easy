// Pixel-level check that slice labels sit on their slice's centre line.
// Regression: labels were centred on the measured text width while being laid out centred in a wider box,
// so short labels drifted right (reported with a screenshot of the donut editor).

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class DonutLabelPlacementTests
{
    private const double OuterRadius = 150;
    private const double HoleRatio = 0.42;

    [AvaloniaFact]
    public void Labels_are_centred_on_their_slices()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu
        {
            OuterRadius = OuterRadius,
            HoleRatio = HoleRatio,
            Items =
            [
                new DonutItem { Id = "a", Label = "PNG" },
                new DonutItem { Id = "b", Label = "JPG" },
                new DonutItem { Id = "c", Label = "PDF" },
                new DonutItem { Id = "d", Label = "ICO" },
            ],
            SliceBrush = Brushes.White,
            CenterBrush = Brushes.White,
            Foreground = Brushes.Black,
        };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight, Background = Brushes.White };
        window.Show();
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        using var frame = window.CaptureRenderedFrame()!;
        var (pixels, width, height) = Read(frame);
        var origin = donut.TranslatePoint(new Point(0, 0), window)!.Value;
        var center = new Point(origin.X + donut.Bounds.Width / 2, origin.Y + donut.Bounds.Height / 2);
        var labelRadius = (OuterRadius + OuterRadius * HoleRatio) / 2;

        for (var i = 0; i < 4; i++)
        {
            var anchor = DonutGeometry.PointAt(center, labelRadius, DonutGeometry.MidAngle(i, 4));
            var (cx, cy) = DarkCentroid(pixels, width, height, anchor, 34);
            Assert.True(Math.Abs(cx - anchor.X) < 3, $"slice {i}: text centre x {cx:0.0} vs slice centre {anchor.X:0.0}");
            Assert.True(Math.Abs(cy - anchor.Y) < 5, $"slice {i}: text centre y {cy:0.0} vs slice centre {anchor.Y:0.0}");
        }
        window.Close();
    }

    /// <summary>Average position of dark (text) pixels in a square around <paramref name="at"/>.</summary>
    private static (double X, double Y) DarkCentroid(byte[] bgra, int width, int height, Point at, int half)
    {
        double sx = 0, sy = 0;
        var n = 0;
        for (var y = Math.Max(0, (int)at.Y - half); y < Math.Min(height, (int)at.Y + half); y++)
        {
            for (var x = Math.Max(0, (int)at.X - half); x < Math.Min(width, (int)at.X + half); x++)
            {
                var o = (y * width + x) * 4;
                var luminance = 0.0722 * bgra[o] + 0.7152 * bgra[o + 1] + 0.2126 * bgra[o + 2];
                if (luminance < 110)
                {
                    sx += x;
                    sy += y;
                    n++;
                }
            }
        }
        Assert.True(n > 20, $"no label found near {at}");
        return (sx / n, sy / n);
    }

    private static (byte[] Pixels, int Width, int Height) Read(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var width = buffer.Size.Width;
        var height = buffer.Size.Height;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * width * 4, width * 4);
        return (pixels, width, height);
    }

    [AvaloniaFact]
    public void A_word_wider_than_its_slice_gets_a_smaller_font_instead_of_breaking()
    {
        // Easy: "Password" and "Compress" were broken mid-word ("Passwo / rd") in a donut of twelve slices.
        TestServices.EnsureInitialized("en");
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        static double Width(string word, Typeface face, double size) =>
            new FormattedText(word, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, null).Width;

        var narrow = Width("Password", typeface, 13) * 0.8;
        var (size, width) = DonutMenu.LabelFit("Password", typeface, narrow);
        Assert.InRange(size, 9, 12.5);
        Assert.True(Width("Password", typeface, size) < width, $"{size} {width}");
        // Too narrow even for the smallest size: the box grows to the word rather than breaking it.
        (size, width) = DonutMenu.LabelFit("Compress", typeface, 20);
        Assert.Equal(9, size);
        Assert.True(Width("Compress", typeface, 9) < width, $"{width}");
        // Short words keep the normal size; a label of short words wraps between them.
        Assert.Equal(13, DonutMenu.LabelFit("PPTX (slides)", typeface, Width("(slides)", typeface, 13) + 4).Size);
    }
}
