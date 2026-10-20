// Whether the Mac's screen is locked right now (⌃⌘Q, the screen saver or sleep asking for the password): the session's
// "CGSSessionScreenIsLocked" flag. No permission needed.

using System.Runtime.InteropServices;

namespace Filee.Platform.MacOS;

public static class ScreenLock
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public static bool IsLocked
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
                return false;
            var session = CGSessionCopyCurrentDictionary();
            if (session == 0)
                return false; // no window server session (an SSH login): nothing on screen to lock
            var key = CFStringCreateWithCString(0, "CGSSessionScreenIsLocked", 0x08000100);
            try
            {
                var value = CFDictionaryGetValue(session, key);
                return value != 0 && CFBooleanGetValue(value);
            }
            finally
            {
                CFRelease(key);
                CFRelease(session);
            }
        }
    }

    [DllImport(CoreGraphics)]
    private static extern nint CGSessionCopyCurrentDictionary();

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetValue(nint dictionary, nint key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(nint boolean);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}
