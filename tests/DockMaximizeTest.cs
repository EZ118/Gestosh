using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Gestosh;

/// <summary>
/// Reproduces the geometry used when a maximized window is restored from the dock:
/// the window is a normal window sitting on the maximized frame, so maximizing it must
/// not first drop back to the old restore rect (that jump was the visible flash).
/// </summary>
internal static class DockMaximizeTest
{
    static int _failures;
    static readonly StringBuilder _log = new StringBuilder();

    const short SW_MAXIMIZE = 3;
    const short SW_SHOWNORMAL = 1;
    const short SW_SHOWMAXIMIZED = 3;

    static void Log(string text)
    {
        Console.WriteLine(text);
        _log.AppendLine(text);
    }

    static void Check(string name, bool ok, string detail)
    {
        Log((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : " : " + detail));
        if (!ok) _failures++;
    }

    static void Pump(int milliseconds)
    {
        int end = Environment.TickCount + milliseconds;
        do
        {
            Native.MSG message;
            while (Native.PeekMessageW(out message, IntPtr.Zero, 0, 0, 1))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
            }
            Thread.Sleep(5);
        }
        while (Environment.TickCount < end);
    }

    static string Describe(Native.RECT r)
    {
        return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
    }

    static int Delta(Native.RECT a, Native.RECT b)
    {
        return Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top) +
            Math.Abs(a.Right - b.Right) + Math.Abs(a.Bottom - b.Bottom);
    }

    static Native.WINDOWPLACEMENT Placement(IntPtr hwnd)
    {
        var placement = new Native.WINDOWPLACEMENT();
        placement.length = Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
        Native.GetWindowPlacement(hwnd, ref placement);
        return placement;
    }

    static int Main()
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = Native.CreateWindowExW(0, "STATIC", "Gestosh dock geometry test",
                (uint)(Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE),
                200, 150, 640, 480, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            Check("test window created", hwnd != IntPtr.Zero, "hwnd=" + hwnd);
            if (hwnd == IntPtr.Zero) return 2;
            Pump(300);

            var original = Placement(hwnd);
            original.showCmd = SW_SHOWNORMAL;
            Log("  original restore rect: " + Describe(original.rcNormalPosition));

            Native.ShowWindow(hwnd, SW_MAXIMIZE);
            Pump(400);
            Check("test window is maximized", Native.IsZoomed(hwnd), "zoomed=" + Native.IsZoomed(hwnd));
            Native.RECT maximized;
            Native.GetWindowRect(hwnd, out maximized);
            Native.RECT maximizedClient = DockManager.ClientScreenRect(hwnd);
            Log("  maximized window: " + Describe(maximized));
            Log("  maximized client: " + Describe(maximizedClient));

            // Step 1: the dock turn a maximized window into a same-client normal window.
            Native.RECT preserving;
            bool computed = DockManager.TryGetClientPreservingRect(hwnd, Native.GetDpi(hwnd), out preserving);
            Check("computed a client-preserving window rect", computed, Describe(preserving));
            if (!computed) return 3;

            var normal = original;
            normal.showCmd = SW_SHOWNORMAL;
            normal.flags = 0;
            normal.rcNormalPosition = preserving;
            Native.SetWindowPlacement(hwnd, ref normal);
            Native.SetWindowPos(hwnd, IntPtr.Zero, preserving.Left, preserving.Top,
                preserving.Width, preserving.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            Pump(300);
            Check("window is normal while docked", !Native.IsZoomed(hwnd), "zoomed=" + Native.IsZoomed(hwnd));

            // Step 2 (new restore path): maximize directly, then write the real restore rect.
            Native.ShowWindow(hwnd, SW_MAXIMIZE);
            Pump(400);
            Check("direct maximize works", Native.IsZoomed(hwnd), "zoomed=" + Native.IsZoomed(hwnd));
            Native.RECT afterMaximize;
            Native.GetWindowRect(hwnd, out afterMaximize);
            Check("direct maximize does not move the window", Delta(afterMaximize, maximized) <= 2,
                Describe(afterMaximize) + " vs " + Describe(maximized));

            var restore = original;
            restore.showCmd = SW_SHOWMAXIMIZED;
            restore.flags = 0;
            Native.SetWindowPlacement(hwnd, ref restore);
            Pump(400);

            Native.RECT afterPlacement;
            Native.GetWindowRect(hwnd, out afterPlacement);
            Native.RECT afterClient = DockManager.ClientScreenRect(hwnd);
            Check("window stays maximized after the placement update", Native.IsZoomed(hwnd),
                "zoomed=" + Native.IsZoomed(hwnd));
            Check("placement update does not move the maximized window", Delta(afterPlacement, maximized) <= 2,
                Describe(afterPlacement));
            Check("client stays identical", Delta(afterClient, maximizedClient) <= 2, Describe(afterClient));

            var stored = Placement(hwnd);
            int restoreDelta = Delta(stored.rcNormalPosition, original.rcNormalPosition);
            Log("  stored restore rect: " + Describe(stored.rcNormalPosition));
            Check("restore rect is remembered for the next unmaximize",
                stored.showCmd == SW_SHOWMAXIMIZED && restoreDelta <= 2,
                "showCmd=" + stored.showCmd + " delta=" + restoreDelta);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
            try
            {
                System.IO.File.WriteAllText(@"E:\Gestosh\tests\dock-maximize-result.txt", _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
