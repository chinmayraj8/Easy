// Reads the text in a picture with Apple's Vision framework (VNRecognizeTextRequest, the engine behind Live Text): on
// the Mac, offline, nothing to install. Called through the Objective-C runtime like MacOSServicesProvider.

using System.Runtime.InteropServices;

namespace Filee.Platform.MacOS;

public static class TextRecognizer
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string Vision = "/System/Library/Frameworks/Vision.framework/Vision";
    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(Vision, out _));

    /// <summary>The lines of text in an image file (PNG, JPEG, HEIC, TIFF ...), top to bottom; "" when there are none.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS, or Vision couldn't be loaded.</exception>
    public static string Recognize(string imagePath)
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
                var lines = new List<string>();
                var count = results == 0 ? 0 : (long)Send(results, Sel("count"));
                for (var i = 0; i < count; i++)
                {
                    var candidate = Send(Send(Send(results, Sel("objectAtIndex:"), (nint)i), Sel("topCandidates:"), 1), Sel("firstObject"));
                    if (candidate != 0 && Utf8String(Send(candidate, Sel("string"))) is { } line)
                        lines.Add(line);
                }
                return string.Join('\n', lines);
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
