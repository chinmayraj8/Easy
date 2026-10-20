// Whether ⌥ Option is held right now (CoreGraphics' combined keyboard state; no permission needed): the tray menu shows
// its private items only then, the way macOS menus keep extra items out of sight.

using System.Runtime.InteropServices;

namespace Filee.Platform.MacOS;

public static class OptionKey
{
    private const ulong AlternateMask = 0x80000; // kCGEventFlagMaskAlternate

    public static bool IsDown => OperatingSystem.IsMacOS() && (CGEventSourceFlagsState(0) & AlternateMask) != 0; // kCGEventSourceStateCombinedSessionState

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern ulong CGEventSourceFlagsState(int stateId);
}
