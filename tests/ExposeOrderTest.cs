using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using Gestosh;

/// <summary>
/// Verifies that the App Expose arrangement does not depend on which window is in the
/// foreground / on top: opening it twice with a different foreground window must produce
/// the same layout (only the selection highlight may differ).
/// </summary>
internal static class ExposeOrderTest
{
    const int CommandExpose = 6;
    const int CommandExit = 4;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    static int _failures;
    static readonly StringBuilder _log = new StringBuilder();

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

    static void Key(byte vk)
    {
        Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(25);
        Native.keybd_event(vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static void SendCommand(int command)
    {
        IntPtr hidden = Native.FindWindowW(null, App.WindowTitle);
        if (hidden == IntPtr.Zero) throw new Exception("Gestosh hidden window not found");
        uint message = Native.RegisterWindowMessageW("Gestosh.Command.v1");
        Native.PostMessage(hidden, message, new IntPtr(command), IntPtr.Zero);
    }

    static void WriteConfig(string path)
    {
        File.WriteAllText(path,
            "[general]\r\nenabled=true\r\ntray=false\r\n" +
            "manageThreeFingerGesture=false\r\nmanageFourFingerGesture=false\r\nhotkeys=false\r\n\r\n" +
            "[animation]\r\ndurationMs=340\r\nfps=60\r\n\r\n" +
            "[dock]\r\nexposePx=42\r\nstackStepPx=10\r\nmaxExposePx=220\r\n" +
            "includeToolWindows=false\r\nincludeMinimized=false\r\n" +
            "onlyProcesses=ExposeOrderTest\r\nexcludeProcesses=\r\n",
            new UTF8Encoding(false));
    }

    static readonly IntPtr[] Brushes = new IntPtr[4];
    static Native.WndProc _paintProc;

    static IntPtr PaintProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x000F) // WM_PAINT
        {
            var paint = new Native.PAINTSTRUCT();
            IntPtr dc = Native.BeginPaint(hwnd, out paint);
            FillWindow(dc, hwnd);
            Native.EndPaint(hwnd, ref paint);
            return IntPtr.Zero;
        }
        if (message == 0x0014) // WM_ERASEBKGND
        {
            FillWindow(wParam, hwnd);
            return new IntPtr(1);
        }
        return Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    static void FillWindow(IntPtr dc, IntPtr hwnd)
    {
        string title = Native.GetWindowTitle(hwnd);
        int index = title.Length > 0 ? (title[title.Length - 1] - '1') : 0;
        if (index < 0 || index > 3) index = 0;
        Native.RECT client;
        if (!Native.GetClientRect(hwnd, out client)) return;
        Native.FillRect(dc, ref client, Brushes[index]);
    }

    static IntPtr CreateWindow(string title, int x, int y)
    {
        IntPtr hwnd = Native.CreateWindowExW(0, "STATIC", title,
            (uint)(Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE),
            x, y, 520, 360, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
        {
            // Paint each window in a distinct colour so a reordered layout is obvious in a
            // screenshot (identical white windows would hide the swap).
            Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC,
                System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_paintProc));
            // Keep the test windows above the console so the caption clicks always land.
            Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        return hwnd;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    static void Click(IntPtr window)
    {
        Native.RECT rect;
        if (!Native.GetWindowRect(window, out rect)) return;
        int x = (rect.Left + rect.Right) / 2;
        int y = rect.Top + 10;
        var point = new Native.POINT();
        point.X = x;
        point.Y = y;
        IntPtr under = Native.WindowFromPoint(point);
        Log("  click " + x + "," + y + " target=0x" + window.ToInt64().ToString("x") +
            " under=0x" + under.ToInt64().ToString("x"));
        // Click the caption: a client-area click on a plain STATIC window does not always
        // activate it, and the test needs the foreground window to really change.
        Native.SetCursorPos(x, y);
        Thread.Sleep(80);
        Native.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(250);
        Log("  foreground after click: 0x" + Native.GetForegroundWindow().ToInt64().ToString("x"));

        if (Native.GetForegroundWindow() == window) return;
        uint pid;
        uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        uint currentThread = Native.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
            Native.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            Native.SetForegroundWindow(window);
            Native.SetFocus(window);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(currentThread, foregroundThread, false);
        }
        Thread.Sleep(200);
        if (Native.GetForegroundWindow() != window)
        {
            SwitchToThisWindow(window, true);
            Thread.Sleep(250);
        }
    }

    static IntPtr OpenExpose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SendCommand(CommandExpose);
            int end = Environment.TickCount + 1800;
            do
            {
                Pump(40);
                IntPtr overlay = Native.FindWindowW(null, "Gestosh.AppExpose.v1");
                if (overlay != IntPtr.Zero && Native.IsWindowVisible(overlay)) return overlay;
            }
            while (Environment.TickCount < end);
        }
        return IntPtr.Zero;
    }

    static bool CloseExpose()
    {
        IntPtr overlay = Native.FindWindowW(null, "Gestosh.AppExpose.v1");
        if (overlay == IntPtr.Zero) return true;

        // The overlay handles WM_KEYDOWN(VK_ESCAPE) in its window procedure, which does not
        // depend on the keyboard hook or on which process owns the foreground.
        Native.PostMessage(overlay, 0x0100, new IntPtr(Native.VK_ESCAPE), IntPtr.Zero);
        int end = Environment.TickCount + 2500;
        do
        {
            Pump(40);
            if (Native.FindWindowW(null, "Gestosh.AppExpose.v1") == IntPtr.Zero) return true;
        }
        while (Environment.TickCount < end);
        return false;
    }

    static string RectText(IntPtr window)
    {
        Native.RECT rect;
        if (!Native.GetWindowRect(window, out rect)) return "?";
        return rect.Left + "," + rect.Top + " " + (rect.Right - rect.Left) + "x" + (rect.Bottom - rect.Top);
    }

    static Bitmap Capture(Native.RECT area)
    {
        var bitmap = new Bitmap(area.Right - area.Left, area.Bottom - area.Top, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, bitmap.Size);
        }
        return bitmap;
    }

    static byte[] Bytes(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] buffer = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    static byte[] CaptureBytes(Native.RECT area)
    {
        using (Bitmap bitmap = Capture(area)) return Bytes(bitmap);
    }

    static int ZIndex(IntPtr target)
    {
        int index = 0;
        int found = -1;
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
        {
            if (hwnd == target)
            {
                found = index;
                return false;
            }
            index++;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static byte[] WaitStable(Native.RECT full)
    {
        byte[] previous = null;
        int end = Environment.TickCount + 5000;
        do
        {
            Pump(90);
            byte[] current = CaptureBytes(full);
            if (previous != null && DiffRatio(previous, current) < 0.002) return current;
            previous = current;
        }
        while (Environment.TickCount < end);
        return previous;
    }

    static bool WaitPainted(Native.RECT full, byte[] reference)
    {
        int end = Environment.TickCount + 3000;
        do
        {
            Pump(60);
            using (Bitmap bitmap = Capture(full))
            {
                if (DiffRatio(reference, Bytes(bitmap)) > 0.03) return true;
            }
        }
        while (Environment.TickCount < end);
        return false;
    }

    static double DiffRatio(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 1.0;
        long changed = 0;
        for (int i = 0; i + 3 < a.Length; i += 4)
        {
            int d = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            if (d > 24) changed++;
        }
        return (double)changed / (a.Length / 4);
    }

    static int Main()
    {
        string root = @"E:\Gestosh";
        string exe = Path.Combine(root, @"dist\Gestosh.Debug.exe");
        string config = Path.Combine(root, @"tests\expose-order.ini");
        string appLog = Path.Combine(root, @"tests\expose-order-app.log");
        Process app = null;
        var windows = new System.Collections.Generic.List<IntPtr>();
        try
        {
            if (Native.FindWindowW(null, App.WindowTitle) != IntPtr.Zero)
                throw new Exception("A Gestosh instance is already running; close it before running this test.");
            WriteConfig(config);
            if (File.Exists(appLog)) File.Delete(appLog);

            var info = new ProcessStartInfo(exe, "--config \"" + config + "\" --log \"" + appLog + "\" --verbose");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            app = Process.Start(info);
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (Native.FindWindowW(null, App.WindowTitle) != IntPtr.Zero) break;
                Thread.Sleep(100);
            }
            Check("Gestosh started", Native.FindWindowW(null, App.WindowTitle) != IntPtr.Zero, "");
            Pump(700);

            _paintProc = PaintProc;
            Brushes[0] = Native.CreateSolidBrush(0x0000C8); // red
            Brushes[1] = Native.CreateSolidBrush(0x00C800); // green
            Brushes[2] = Native.CreateSolidBrush(0xC80000); // blue
            Brushes[3] = Native.CreateSolidBrush(0x00C8C8); // yellow

            IntPtr w0 = CreateWindow("Gestosh order 1", 60, 60);
            IntPtr w1 = CreateWindow("Gestosh order 2", 760, 60);
            IntPtr w2 = CreateWindow("Gestosh order 3", 60, 520);
            IntPtr w3 = CreateWindow("Gestosh order 4", 760, 520);
            windows.Add(w0); windows.Add(w1); windows.Add(w2); windows.Add(w3);
            Check("four test windows created",
                w0 != IntPtr.Zero && w1 != IntPtr.Zero && w2 != IntPtr.Zero && w3 != IntPtr.Zero, "");
            if (w0 == IntPtr.Zero || w1 == IntPtr.Zero || w2 == IntPtr.Zero || w3 == IntPtr.Zero) return 2;
            Pump(500);

            Native.RECT full;
            full.Left = 0;
            full.Top = 0;
            full.Right = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            full.Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN);

            // First look: window 1 in the foreground.
            Click(w0);
            IntPtr fg1 = Native.GetForegroundWindow();
            Log("  first foreground: 0x" + fg1.ToInt64().ToString("x"));
            IntPtr overlay = OpenExpose();
            Check("App Expose opened (first)", overlay != IntPtr.Zero, "hwnd=" + overlay);
            Native.SetCursorPos(4, 4);
            byte[] firstBytes = WaitStable(full);
            using (Bitmap first = Capture(full))
            {
                first.Save(Path.Combine(root, @"tests\artifacts\expose-order-1.png"), ImageFormat.Png);
                Check("App Expose closed (first)", CloseExpose(), "");

                // Second look: a different window in the foreground *and* on top of the
                // stacking order, which is what switching windows normally does.
                int zBefore = ZIndex(w2);
                Native.SetWindowPos(w2, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                int zAfter = ZIndex(w2);
                Click(w2);
                IntPtr fg2 = Native.GetForegroundWindow();
                Log("  second foreground: 0x" + fg2.ToInt64().ToString("x"));
                Check("stacking order changed between the two looks", zAfter != zBefore,
                    "zBefore=" + zBefore + " zAfter=" + zAfter + " fg1=" + fg1 + " fg2=" + fg2);

                IntPtr overlay2 = OpenExpose();
                Check("App Expose opened (second)", overlay2 != IntPtr.Zero, "hwnd=" + overlay2);
                Native.SetCursorPos(4, 4);
                byte[] secondBytes = WaitStable(full);
                using (Bitmap second = Capture(full))
                {
                    second.Save(Path.Combine(root, @"tests\artifacts\expose-order-2.png"), ImageFormat.Png);
                    double ratio = DiffRatio(firstBytes, secondBytes);
                    Log("  differing pixels: " + (ratio * 100.0).ToString("0.00") + "%");
                    Check("App Expose layout does not depend on the foreground window", ratio < 0.015,
                        (ratio * 100.0).ToString("0.00") + "%");
                }
            }
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            try
            {
                if (Native.FindWindowW(null, "Gestosh.AppExpose.v1") != IntPtr.Zero) Key((byte)Native.VK_ESCAPE);
            }
            catch { }
            foreach (IntPtr window in windows)
            {
                try { Native.DestroyWindow(window); } catch { }
            }
            try
            {
                SendCommand(CommandExit);
                Thread.Sleep(500);
            }
            catch { }
            if (app != null && !app.HasExited)
            {
                try { app.Kill(); } catch { }
            }
            try
            {
                File.WriteAllText(Path.Combine(@"E:\Gestosh\tests", "expose-order-result.txt"), _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
