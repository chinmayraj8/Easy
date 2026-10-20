// "Copy text": reads the text in pictures and PDFs (scanned or not) with macOS Vision and puts it on the clipboard.
// PDF pages are rendered at 200 dpi first; pictures are flattened onto white (Vision reads black text on a transparent
// background as black on black).

using System.Diagnostics;
using Filee.Core.Formats;
using Filee.Platform.MacOS;
using ImageMagick;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SkiaSharp;

namespace Filee.App.Services;

public sealed class TextCopyService(ILogger<TextCopyService> log)
{
    /// <summary>Copies the text of <paramref name="files"/> to the clipboard; returns its length (0: none found).</summary>
    public async Task<int> CopyAsync(IReadOnlyList<string> files)
    {
        var text = await Task.Run(() => string.Join("\n\n", files.Select(Read).Where(t => t.Length > 0)));
        if (text.Length == 0)
            return 0;
        using var pbcopy = Process.Start(new ProcessStartInfo("/usr/bin/pbcopy") { RedirectStandardInput = true, UseShellExecute = false })!;
        await pbcopy.StandardInput.WriteAsync(text);
        pbcopy.StandardInput.Close();
        await pbcopy.WaitForExitAsync();
        return text.Length;
    }

    private string Read(string file)
    {
        if (!OperatingSystem.IsMacOS())
            return "";
        var folder = Directory.CreateTempSubdirectory("easy-text").FullName;
        try
        {
            var pictures = FormatRegistry.Detect(file)?.Id == "pdf" ? RenderPages(file, folder) : [Flatten(file, folder)];
            return string.Join("\n\n", pictures.Select(TextRecognizer.Recognize).Where(t => t.Length > 0));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't read the text of {File}", file);
            return "";
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static List<string> RenderPages(string pdf, string folder)
    {
        var pages = new List<string>();
        var options = new RenderOptions(Dpi: 200, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
#pragma warning disable CA1416 // PDFium ships for macOS; Read only runs there
        foreach (var bitmap in Conversion.ToImages(File.ReadAllBytes(pdf), password: null, options))
#pragma warning restore CA1416
        {
            using (bitmap)
            using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            {
                var path = Path.Combine(folder, $"page-{pages.Count + 1}.png");
                File.WriteAllBytes(path, data.ToArray());
                pages.Add(path);
            }
        }
        return pages;
    }

    private static string Flatten(string picture, string folder)
    {
        using var image = new MagickImage(picture);
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
        var path = Path.Combine(folder, "picture.png");
        image.Write(path, MagickFormat.Png);
        return path;
    }
}
