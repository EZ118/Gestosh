using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Gestosh;

/// <summary>
/// End-to-end check of dock/restore for a maximized window. The test starts Gestosh with
/// a configuration that only manages this test's own window, maximizes it, docks, then
/// restores while sampling the window rectangle: a restore that drops back to the old
/// restore rectangle before maximizing would show up as a narrow sample (that jump is the
/// flash users saw).
/// </summary>
internal static class DockRestoreTest
{
    const int SW_MAXIMIZE = 3;
    const int CommandDock = 1;
    const int CommandRestore = 2;
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

    static string Describe(Native.RECT r)
    {
        return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
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
            "onlyProcesses=DockRestoreTest\r\nexcludeProcesses=\r\n",
            new UTF8Encoding(false));
    }

    static int Main()
    {
        string root = @"E:\Gestosh";
        string exe = Path.Combine(root, @"dist\Gestosh.Debug.exe");
        string config = Path.Combine(root, @"tests\dock-restore.ini");
        string appLog = Path.Combine(root, @"tests\dock-restore-app.log");
        Process app = null;
        IntPtr hwnd = IntPtr.Zero;
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
            Check("Gestosh started", Native.FindWindowW(null, App.WindowTitle) != IntPtr.Zero, "pid=" + (app != null ? app.Id : 0));
            Pump(800);

            hwnd = Native.CreateWindowExW(0, "STATIC", "Gestosh dock restore test",
                (uint)(Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE),
                200, 150, 640, 480, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            Check("test window created", hwnd != IntPtr.Zero, "hwnd=" + hwnd);
            if (hwnd == IntPtr.Zero) return 2;
            Pump(300);

            Native.ShowWindow(hwnd, SW_MAXIMIZE);
            Pump(500);
            Check("test window maximized", Native.IsZoomed(hwnd), "zoomed=" + Native.IsZoomed(hwnd));
            Native.RECT maximized;
            Native.GetWindowRect(hwnd, out maximized);
            Native.RECT maximizedClient = DockManager.ClientScreenRect(hwnd);
            Log("  maximized window: " + Describe(maximized));
            Log("  maximized client: " + Describe(maximizedClient));

            SendCommand(CommandDock);
            Pump(1400);
            Native.RECT docked;
            Native.GetWindowRect(hwnd, out docked);
            int dockedWidth = docked.Right - docked.Left;
            Log("  docked window: " + Describe(docked));
            Check("window was docked", Math.Abs(docked.Left - maximized.Left) > 50 || Math.Abs(docked.Top - maximized.Top) > 50,
                Describe(docked));
            Check("docked window keeps its (maximized) width", Math.Abs(dockedWidth - (maximized.Right - maximized.Left)) <= 4,
                "dockedWidth=" + dockedWidth);
            Check("docked window is a normal window", !Native.IsZoomed(hwnd), "zoomed=" + Native.IsZoomed(hwnd));

            // Restore while sampling; the old code dropped the window back to the small
            // restore rectangle for a frame before maximizing again.
            SendCommand(CommandRestore);
            int minWidth = int.MaxValue;
            int maxWidth = 0;
            int smallSamples = 0;
            int end = Environment.TickCount + 1600;
            do
            {
                Pump(8);
                Native.RECT sample;
                if (!Native.GetWindowRect(hwnd, out sample)) continue;
                int width = sample.Right - sample.Left;
                if (width < minWidth) minWidth = width;
                if (width > maxWidth) maxWidth = width;
                if (width < (maximized.Right - maximized.Left) - 60) smallSamples++;
            }
            while (Environment.TickCount < end);

            Native.RECT restored;
            Native.GetWindowRect(hwnd, out restored);
            Native.RECT restoredClient = DockManager.ClientScreenRect(hwnd);
            Log("  restore samples: minWidth=" + minWidth + " maxWidth=" + maxWidth + " smallSamples=" + smallSamples);
            Log("  restored window: " + Describe(restored));
            Check("restored window is maximized again", Native.IsZoomed(hwnd), "zoomed=" + Native.IsZoomed(hwnd));
            Check("restore never dropped to the small restore rectangle", smallSamples == 0,
                "smallSamples=" + smallSamples + " minWidth=" + minWidth);
            Check("restored window matches the maximized frame",
                Math.Abs(restored.Left - maximized.Left) + Math.Abs(restored.Top - maximized.Top) +
                Math.Abs(restored.Right - maximized.Right) + Math.Abs(restored.Bottom - maximized.Bottom) <= 4,
                Describe(restored));
            Check("restored client matches the maximized client",
                Math.Abs(restoredClient.Left - maximizedClient.Left) + Math.Abs(restoredClient.Top - maximizedClient.Top) +
                Math.Abs(restoredClient.Right - maximizedClient.Right) + Math.Abs(restoredClient.Bottom - maximizedClient.Bottom) <= 4,
                Describe(restoredClient));
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            try { if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd); } catch { }
            try
            {
                SendCommand(CommandRestore);
                Thread.Sleep(500);
                SendCommand(CommandExit);
                Thread.Sleep(600);
            }
            catch { }
            if (app != null && !app.HasExited)
            {
                try { app.Kill(); } catch { }
            }
            try
            {
                File.WriteAllText(@"E:\Gestosh\tests\dock-restore-result.txt", _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
