using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class DockTest
{
    const int SW_MAXIMIZE = 3;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

    const uint SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);

    static int _failures;
    static readonly StringBuilder Report = new StringBuilder();

    static void Log(string text)
    {
        Console.WriteLine(text);
        Report.AppendLine(text);
    }

    static void Check(string name, bool ok, string detail)
    {
        Log((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
        if (!ok) _failures++;
    }

    static string ReadShared(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    static RECT Rect(IntPtr hwnd)
    {
        RECT r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    static string R(RECT r)
    {
        return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
    }

    static void SendCommand(int command)
    {
        IntPtr hwnd = FindWindow(null, "Gestosh.Hidden.Window.v1");
        if (hwnd == IntPtr.Zero) throw new Exception("Gestosh hidden window not found");
        uint msg = RegisterWindowMessage("Gestosh.Command.v1");
        PostMessage(hwnd, msg, new IntPtr(command), IntPtr.Zero);
    }

    static IntPtr WaitForWindow(Process process, int timeoutMs)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            Thread.Sleep(40);
        }
        return IntPtr.Zero;
    }

    static int Main()
    {
        string root = @"E:\Gestosh";
        string exe = Path.Combine(root, @"dist\Gestosh.Debug.exe");
        string config = Path.Combine(root, @"tests\test.ini");
        string log = Path.Combine(root, @"tests\app.log");
        string shot = Path.Combine(root, @"tests\artifacts\dock-test.png");
        Process app = null;
        var notepads = new List<Process>();
        var topmost = new List<IntPtr>();

        try
        {
            if (FindWindow(null, "Gestosh.Hidden.Window.v1") != IntPtr.Zero)
                throw new Exception("A Gestosh instance is already running; close it before running this test.");
            foreach (Process p in Process.GetProcessesByName("Gestosh")) p.Kill();
            if (File.Exists(log)) File.Delete(log);

            var info = new ProcessStartInfo(exe, "--config \"" + config + "\" --log \"" + log + "\" --verbose");
            info.UseShellExecute = false;
            app = Process.Start(info);
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(log) && ReadShared(log).Contains("Gestosh initialized")) break;
                Thread.Sleep(100);
            }
            if (!File.Exists(log)) throw new Exception("Gestosh did not create its log");
            Log("App started, pid=" + app.Id);

            int[,] positions = { { 0, 200, 800, 600 }, { 1120, 200, 800, 600 }, { 300, 700, 600, 300 }, { 100, 100, 800, 600 } };
            for (int i = 0; i < 4; i++)
            {
                var p = Process.Start("notepad.exe");
                notepads.Add(p);
                IntPtr hwnd = WaitForWindow(p, 5000);
                if (hwnd == IntPtr.Zero) throw new Exception("Notepad " + i + " has no window");
                SetWindowPos(hwnd, IntPtr.Zero, positions[i, 0], positions[i, 1], positions[i, 2], positions[i, 3], SWP_NOZORDER | SWP_NOACTIVATE);
            }
            Thread.Sleep(400);
            IntPtr maxHwnd = notepads[3].MainWindowHandle;
            ShowWindow(maxHwnd, SW_MAXIMIZE);
            Thread.Sleep(450);

            var hwnds = new IntPtr[4];
            for (int i = 0; i < 4; i++) hwnds[i] = notepads[i].MainWindowHandle;
            for (int i = 0; i < 4; i++)
            {
                SetWindowPos(hwnds[i], new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                topmost.Add(hwnds[i]);
            }
            Thread.Sleep(250);
            var before = new RECT[4];
            for (int i = 0; i < 4; i++) before[i] = Rect(hwnds[i]);
            bool wasMax = IsZoomed(hwnds[3]);
            IntPtr foregroundBefore = GetForegroundWindow();

            Log("=== before ===");
            for (int i = 0; i < 4; i++) Log("w" + i + " " + R(before[i]) + (i == 3 ? "  maximized=" + wasMax : ""));

            SendCommand(1);
            var sample = new HashSet<int>();
            for (int i = 0; i < 45; i++)
            {
                Thread.Sleep(10);
                RECT r = Rect(hwnds[0]);
                sample.Add(r.Left);
            }
            Thread.Sleep(700);
            var docked = new RECT[4];
            for (int i = 0; i < 4; i++) docked[i] = Rect(hwnds[i]);
            IntPtr foregroundDocked = GetForegroundWindow();

            Log("=== docked ===");
            for (int i = 0; i < 4; i++) Log("w" + i + " " + R(docked[i]));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(shot));
                using (var bmp = new Bitmap(1920, 1080))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(1920, 1080));
                    bmp.Save(shot, ImageFormat.Png);
                }
                Log("screenshot=" + shot);
            }
            catch (Exception ex) { Log("screenshot failed: " + ex.Message); }

            SendCommand(2);
            Thread.Sleep(1200);
            var restored = new RECT[4];
            for (int i = 0; i < 4; i++) restored[i] = Rect(hwnds[i]);
            bool isMaxAfter = IsZoomed(hwnds[3]);
            IntPtr foregroundRestored = GetForegroundWindow();
            Log("foreground before=" + foregroundBefore + " docked=" + foregroundDocked + " restored=" + foregroundRestored);
            Check("foreground window unchanged", foregroundBefore == foregroundDocked && foregroundBefore == foregroundRestored,
                "before=" + foregroundBefore + " docked=" + foregroundDocked + " restored=" + foregroundRestored);
            Log("=== restored ===");
            for (int i = 0; i < 4; i++) Log("w" + i + " " + R(restored[i]));
            Log("maximized after restore=" + isMaxAfter);

            for (int i = 0; i < 4; i++)
            {
                int bw = before[i].Right - before[i].Left, bh = before[i].Bottom - before[i].Top;
                int dw = docked[i].Right - docked[i].Left, dh = docked[i].Bottom - docked[i].Top;
                Check("w" + i + " size unchanged", bw == dw && bh == dh, "before=" + bw + "x" + bh + " docked=" + dw + "x" + dh);
            }
            Check("w0 docked to left edge", docked[0].Left < 0 && docked[0].Right > 0 && docked[0].Right <= 80, R(docked[0]));
            Check("w1 docked to right edge", docked[1].Left >= 1840 && docked[1].Right > 1920, R(docked[1]));
            Check("w2 avoids the bottom taskbar", !(docked[2].Top >= 900 && docked[2].Bottom > 1040), R(docked[2]));
            Check("w2 docked to an available edge",
                docked[2].Left < 0 || docked[2].Right > 1920 || docked[2].Top < 0, R(docked[2]));
            Check("w3 (maximized) docked to top edge", docked[3].Top < 0 && docked[3].Bottom > 0 && docked[3].Bottom <= 90, R(docked[3]));
            Check("animation has intermediate positions", sample.Count >= 4, "distinct x=" + sample.Count);
            for (int i = 0; i < 4; i++)
            {
                int tolerance = i == 3 ? 12 : 2;
                Check("w" + i + " restored position", Math.Abs(restored[i].Left - before[i].Left) <= tolerance && Math.Abs(restored[i].Top - before[i].Top) <= tolerance,
                    "before=" + R(before[i]) + " after=" + R(restored[i]));
            }
            Check("maximized state restored", isMaxAfter, "wasMax=" + wasMax + " isMax=" + isMaxAfter);

            // Clicking the visible sliver of a docked window must restore everything.
            SendCommand(1);
            Thread.Sleep(1000);
            var reDocked = new RECT[4];
            for (int i = 0; i < 4; i++) reDocked[i] = Rect(hwnds[i]);
            Check("re-docked for click test", reDocked[0].Left < 0 && reDocked[0].Right > 0, R(reDocked[0]));

            POINT oldCursor;
            GetCursorPos(out oldCursor);
            SetCursorPos(10, reDocked[0].Top + 80);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(1200);
            SetCursorPos(oldCursor.X, oldCursor.Y);

            var clickRestored = new RECT[4];
            for (int i = 0; i < 4; i++) clickRestored[i] = Rect(hwnds[i]);
            for (int i = 0; i < 4; i++)
            {
                int tolerance = i == 3 ? 12 : 2;
                Check("click restore w" + i,
                    Math.Abs(clickRestored[i].Left - before[i].Left) <= tolerance &&
                    Math.Abs(clickRestored[i].Top - before[i].Top) <= tolerance,
                    "before=" + R(before[i]) + " after=" + R(clickRestored[i]));
            }
            Check("click restores maximized state", IsZoomed(hwnds[3]), "isMax=" + IsZoomed(hwnds[3]));

            // Click the visible close button of a left-docked window.  The click
            // must be swallowed, otherwise show-desktop would close the window.
            SendCommand(1);
            Thread.Sleep(1000);
            var reDocked2 = new RECT[4];
            for (int i = 0; i < 4; i++) reDocked2[i] = Rect(hwnds[i]);
            GetCursorPos(out oldCursor);
            SetCursorPos(25, reDocked2[0].Top + 18);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(1200);
            SetCursorPos(oldCursor.X, oldCursor.Y);
            var closeRestored = new RECT[4];
            for (int i = 0; i < 4; i++) closeRestored[i] = Rect(hwnds[i]);
            Check("click on close button did not close the window",
                closeRestored[0].Right > closeRestored[0].Left && closeRestored[0].Bottom > closeRestored[0].Top,
                R(closeRestored[0]));
            for (int i = 0; i < 4; i++)
            {
                int tolerance = i == 3 ? 12 : 2;
                Check("close-button click restore w" + i,
                    Math.Abs(closeRestored[i].Left - before[i].Left) <= tolerance &&
                    Math.Abs(closeRestored[i].Top - before[i].Top) <= tolerance,
                    "before=" + R(before[i]) + " after=" + R(closeRestored[i]));
            }

            Log("");
            Log("FAILURES=" + _failures);
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            Log("FAILURES=" + (++_failures));
            return 2;
        }
        finally
        {
            try { SendCommand(4); } catch { }
            Thread.Sleep(400);
            if (app != null && !app.HasExited)
            {
                try { app.Kill(); } catch { }
            }
            foreach (var p in notepads)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.CloseMainWindow();
                        Thread.Sleep(120);
                        if (!p.HasExited) p.Kill();
                    }
                }
                catch { }
            }
            foreach (var hwnd in topmost)
            {
                try { SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
            }
            try { File.WriteAllText(@"E:\Gestosh\tests\docktest-result.txt", Report.ToString(), new UTF8Encoding(false)); } catch { }
        }
    }
}
