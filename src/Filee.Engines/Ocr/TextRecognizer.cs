// Reads the text in a picture with Apple's Vision framework (VNRecognizeTextRequest, the engine behind Live Text): on
// the Mac, offline, nothing to install. Called through the Objective-C runtime like MacOSServicesProvider. "Copy text"
// takes the lines; "Searchable PDF" also where each one is, to lay it over the page.

using System.Runtime.InteropServices;

namespace Filee.Engines.Ocr;

/// <summary>
/// A line of text found in a picture, and its box as fractions of the picture (0-1), origin at the bottom left.
/// </summary>
public readonly record struct RecognizedLine(string Text, double X, double Y, double Width, double Height);

public static class TextRecognizer
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string Vision = "/System/Library/Frameworks/Vision.framework/Vision";
    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(Vision, out _));

    /// <summary>The lines of text in an image file (PNG, JPEG, HEIC, TIFF ...), top to bottom; "" when there are none.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS, or Vision couldn't be loaded.</exception>
    public static string Recognize(string imagePath) => string.Join('\n', RecognizeLines(imagePath).Select(l => l.Text));

    /// <summary>True on a Mac where Vision is there to read text.</summary>
    public static bool IsAvailable => OperatingSystem.IsMacOS() && Loaded.Value;

    /// <summary>The lines of text in an image file with where each one is, top to bottom.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS, or Vision couldn't be loaded.</exception>
    public static IReadOnlyList<RecognizedLine> RecognizeLines(string imagePath)
    {
        if (!OperatingSystem.IsMacOS() || !Loaded.Value)
            throw new PlatformNotSupportedException("Text recognition needs macOS.");

        var pool = objc_autoreleasePoolPush();
        try
        {
            var path = Send(objc_getClass("NSString"), Sel("stringWithUTF8String:"), Utf8(imagePath, out var buffer));
            Marshal.FreeCoTaskMem(buffer);
            var url = Send(objc_getClass("NSURL"), Sel("fileURLWithPath:"), path);
            var handler = Send(Send(objc_getClass("VNImageRequestHandler"), Sel("alloc")), Sel("initWithURL:options:"),
                url, Send(objc_getClass("NSDictionary"), Sel("dictionary")));
            var request = Send(Send(objc_getClass("VNRecognizeTextRequest"), Sel("alloc")), Sel("init"));
            try
            {
                Send(request, Sel("setRecognitionLevel:"), 0); // accurate
                Send(request, Sel("setUsesLanguageCorrection:"), 1);
                Send(request, Sel("setAutomaticallyDetectsLanguage:"), 1);
                // The default skips text under 1/32 of the picture's height: all body text of a scanned page.
                SendDouble(request, Sel("setMinimumTextHeight:"), 0.005);
                var requests = Send(objc_getClass("NSArray"), Sel("arrayWithObject:"), request);
                nint error = 0;
                if (!Perform(handler, Sel("performRequests:error:"), requests, ref error))
                    throw new InvalidOperationException(Utf8String(Send(error, Sel("localizedDescription"))) ?? "Vision couldn't read the picture.");

                var results = Send(request, Sel("results"));
                var lines = new List<RecognizedLine>();
                var count = results == 0 ? 0 : (long)Send(results, Sel("count"));
                for (var i = 0; i < count; i++)
                {
                    var observation = Send(results, Sel("objectAtIndex:"), (nint)i);
                    var candidate = Send(Send(observation, Sel("topCandidates:"), 1), Sel("firstObject"));
                    if (candidate == 0 || Utf8String(Send(candidate, Sel("string"))) is not { } line)
                        continue;
                    var box = BoundingBox(observation);
                    lines.Add(new RecognizedLine(line, box.X, box.Y, box.Width, box.Height));
                }
                return lines;
            }
            finally
            {
                Send(request, Sel("release"));
                Send(handler, Sel("release"));
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static nint Sel(string name) => sel_registerName(name);

    /// <summary>
    /// The observation's boundingBox, a CGRect: returned in registers on Apple silicon, through a pointer
    /// (objc_msgSend_stret) on Intel.
    /// </summary>
    private static CGRect BoundingBox(nint observation)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return SendRect(observation, Sel("boundingBox"));
        SendRectStret(out var rect, observation, Sel("boundingBox"));
        return rect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern void SendRectStret(out CGRect rect, nint receiver, nint selector);

    private static nint Utf8(string text, out nint buffer) => buffer = Marshal.StringToCoTaskMemUTF8(text);

    private static string? Utf8String(nint text) =>
        text == 0 ? null : Marshal.PtrToStringUTF8(Send(text, Sel("UTF8String")));

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint first, nint second);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(nint receiver, nint selector, double argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Perform(nint receiver, nint selector, nint requests, ref nint error);
}
