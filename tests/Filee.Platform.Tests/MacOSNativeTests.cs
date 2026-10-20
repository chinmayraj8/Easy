using Filee.Platform.MacOS;

namespace Filee.Platform.Tests;

public sealed class MacOSNativeTests
{
    [Fact]
    public void NativeFrameworksAndPermissionProbeLoadOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS frameworks.");
            return;
        }

        var platform = new MacOSPlatformServices();
        _ = platform.HasAccessibilityPermission;
        _ = platform.PrefersReducedMotion;
        _ = platform.ProcessNameAt(0, 0);
        Assert.Equal(4, platform.SystemDragThreshold);
    }

    [Fact]
    public void TextRecognizerReadsThePrintedLines()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS Vision.");
            return;
        }

        // A printed page as a picture: text → PDF (cupsfilter) → JPEG (sips), both part of macOS.
        var folder = Directory.CreateTempSubdirectory("easy-ocr").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "t.txt"), "Hello Easy\nInvoice number 4821\n");
            Run("/bin/sh", "-c", $"cd '{folder}' && /usr/sbin/cupsfilter -o cpi=4 -o lpi=2 t.txt > t.pdf 2>/dev/null && /usr/bin/sips -s format jpeg t.pdf --out t.jpg");

            var text = TextRecognizer.Recognize(Path.Combine(folder, "t.jpg"));

            Assert.Contains("Hello Easy", text);
            Assert.Contains("4821", text);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void Run(string program, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(program, arguments);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
