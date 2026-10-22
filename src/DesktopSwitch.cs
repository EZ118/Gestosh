using System;
using System.Threading;

namespace Gestosh
{
    /// <summary>
    /// Moves to the neighbouring virtual desktop with Win+Ctrl+Left / Win+Ctrl+Right.
    ///
    /// Windows has no documented API for switching desktops (only for querying them),
    /// and the desktop switch itself has no progressive animation that an application
    /// can drive.  The switch is therefore a normal shell shortcut, hidden behind
    /// <see cref="DesktopSwipeOverlay"/>, which supplies the finger-following slide.
    /// </summary>
    internal static class DesktopSwitch
    {
        /// <param name="direction">+1 for the next desktop, -1 for the previous one.</param>
        public static void Adjacent(int direction)
        {
            if (direction == 0) return;
            int arrow = direction > 0 ? Native.VK_RIGHT : Native.VK_LEFT;
            uint scan = 0;
            try { scan = Native.MapVirtualKeyW((uint)arrow, 0); }
            catch { scan = 0; }

            // Press the modifiers before the arrow and report the real scan codes so the
            // shell sees the same chord a keyboard would produce.
            Native.keybd_event((byte)Native.VK_CONTROL, 0, 0, UIntPtr.Zero);
            Native.keybd_event((byte)Native.VK_LWIN, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            Native.keybd_event((byte)arrow, (byte)scan, Native.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            Thread.Sleep(30);
            Native.keybd_event((byte)arrow, (byte)scan,
                Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(20);
            Native.keybd_event((byte)Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.keybd_event((byte)Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);

            Log.Info("Desktop switch: " + (direction > 0 ? "next" : "previous") + ". scan=" + scan);
        }
    }
}
