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
    public void A_picture_on_the_pasteboard_is_read_as_it_is_but_a_copied_file_is_no_picture()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS AppKit.");
            return;
        }
        var folder = Directory.CreateTempSubdirectory("easy-clip").FullName;
        var board = "EasyTest-" + Guid.NewGuid().ToString("N")[..8]; // the user's clipboard is never touched
        try
        {
            var png = Path.Combine(folder, "shot.png");
            Run("/usr/bin/sips", "-s", "format", "png", "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/GenericDocumentIcon.icns", "--out", png);
            Fill(board, $"pb.setDataForType($.NSData.dataWithContentsOfFile('{png}'), 'public.png');");
            var picture = NativeMethods.ClipboardPicture(board);
            Assert.NotNull(picture);
            Assert.Equal("png", picture.Value.Extension);
            Assert.Equal(File.ReadAllBytes(png), picture.Value.Data);

            // A file copied in Finder: a file URL and its icon as TIFF. Not a picture to save.
            Fill(board, $"pb.setStringForType('file://{png}', 'public.file-url'); pb.setDataForType($.NSData.dataWithContentsOfFile('{png}'), 'public.tiff');");
            Assert.Null(NativeMethods.ClipboardPicture(board));

            Fill(board, "pb.setStringForType('just words', 'public.utf8-plain-text');");
            Assert.Null(NativeMethods.ClipboardPicture(board));
        }
        finally
        {
            Fill(board, "pb.releaseGlobally;");
            Directory.Delete(folder, true);
        }
    }

    /// <summary>Clears the named pasteboard, then runs <paramref name="script"/> on it (as <c>pb</c>) in JXA.</summary>
    private static void Fill(string board, string script) =>
        Run("/usr/bin/osascript", "-l", "JavaScript", "-e", $"ObjC.import('AppKit'); var pb = $.NSPasteboard.pasteboardWithName('{board}'); pb.clearContents; {script}");

    private static void Run(string program, params string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
