using System;

namespace Gestosh
{
    internal static class WindowsTaskView
    {
        public static void Toggle()
        {
            Native.keybd_event((byte)Native.VK_LWIN, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            Native.keybd_event((byte)Native.VK_TAB, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            Native.keybd_event((byte)Native.VK_TAB, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(10);
            Native.keybd_event((byte)Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
