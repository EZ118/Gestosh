using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Gestosh;

/// <summary>
/// Checks that choosing a window in App Expose takes the foreground *while the overlay is
/// still covering the screen*. Activating only after the close animation made the window
/// appear and then jump to the foreground, which looked like a flash.
/// </summary>
internal static class ExposeFocusTest
{
    const int CommandExpose = 6;
    const int CommandExit = 4;

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
            "onlyProcesses=ExposeFocusTest\r\nexcludeProcesses=\r\n",
            new UTF8Encoding(false));
    }

    static IntPtr CreateTestWindow(string title, int x, int y)
    {
        return Native.CreateWindowExW(0, "STATIC", title,
            (uint)(Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE),
            x, y, 560, 400, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
    }

    static int Main()
    {
        string root = @"E:\Gestosh";
        string exe = Path.Combine(root, @"dist\Gestosh.Debug.exe");
        string config = Path.Combine(root, @"tests\expose-focus.ini");
        string appLog = Path.Combine(root, @"tests\expose-focus-app.log");
        Process app = null;
        var windows = new List<IntPtr>();
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

            IntPtr a = CreateTestWindow("Gestosh focus A", 90, 120);
            IntPtr b = CreateTestWindow("Gestosh focus B", 760, 180);
            if (a != IntPtr.Zero) windows.Add(a);
            if (b != IntPtr.Zero) windows.Add(b);
            Check("two test windows created", a != IntPtr.Zero && b != IntPtr.Zero, "a=" + a + " b=" + b);
            if (a == IntPtr.Zero || b == IntPtr.Zero) return 2;
            Pump(400);
            Native.SetForegroundWindow(a);
            Pump(200);

            SendCommand(CommandExpose);
            IntPtr overlay = IntPtr.Zero;
            deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && overlay == IntPtr.Zero)
            {
                Pump(50);
                overlay = Native.FindWindowW(null, "Gestosh.AppExpose.v1");
            }
            Check("App Expose opened", overlay != IntPtr.Zero && Native.IsWindowVisible(overlay), "hwnd=" + overlay);
            if (overlay == IntPtr.Zero) return 3;
            Pump(1000);

            Key((byte)Native.VK_TAB);
            Pump(150);

            IntPtr before = Native.GetForegroundWindow();
            Key((byte)Native.VK_RETURN);

            // Right after Enter the overlay must still exist (it is closing), yet the
            // chosen window must already own the foreground.
            bool activatedWhileCovered = false;
            IntPtr target = IntPtr.Zero;
            for (int i = 0; i < 40; i++)
            {
                IntPtr foreground = Native.GetForegroundWindow();
                bool overlayAlive = Native.IsWindow(overlay);
                if (foreground == a || foreground == b)
                {
                    target = foreground;
                    if (overlayAlive) activatedWhileCovered = true;
                    break;
                }
                Thread.Sleep(15);
            }
            Check("a test window became foreground", target != IntPtr.Zero, "target=" + target + " before=" + before);
            Check("foreground switch happened while the overlay was still visible",
                activatedWhileCovered, "overlayAlive=" + (target != IntPtr.Zero && Native.IsWindow(overlay)));

            Pump(900);
            Check("overlay is gone", !Native.IsWindow(overlay), "hwnd=" + overlay);
            IntPtr after = Native.GetForegroundWindow();
            Check("chosen window stays in the foreground", after == a || after == b, "after=" + after + " target=" + target);
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
                File.WriteAllText(@"E:\Gestosh\tests\expose-focus-result.txt", _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
