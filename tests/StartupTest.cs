using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class StartupTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    static int Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : @"E:\Gestosh\dist\Gestosh.exe";
        string config = args.Length > 1 ? args[1] : @"E:\Gestosh\tests\startup-test.ini";
        string log = args.Length > 2 ? args[2] : @"E:\Gestosh\tests\startup.log";
        if (FindWindow(null, "Gestosh.Hidden.Window.v1") != IntPtr.Zero)
        {
            Console.WriteLine("FAIL: Gestosh already running");
            return 2;
        }
        if (File.Exists(log)) File.Delete(log);
        Process app = null;
        int failures = 0;
        try
        {
            var info = new ProcessStartInfo(exe, "--config \"" + config + "\" --log \"" + log + "\" --verbose");
            info.UseShellExecute = false;
            app = Process.Start(info);
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline && !ReadShared(log).Contains("Gestosh initialized"))
                Thread.Sleep(100);
            Thread.Sleep(400);
            string text = ReadShared(log);
            Console.WriteLine(text);
            if (!text.Contains("Gestosh initialized")) { Console.WriteLine("FAIL: not initialized"); failures++; }
            if (!text.Contains("Touchpad found=True")) { Console.WriteLine("FAIL: touchpad not found"); failures++; }
            if (!text.Contains("Raw input registered")) { Console.WriteLine("FAIL: raw input not registered"); failures++; }
            if (!text.Contains("Tray icon added")) { Console.WriteLine("FAIL: tray icon not added"); failures++; }
            if (!text.Contains("Hotkeys registered")) { Console.WriteLine("FAIL: hotkeys not registered"); failures++; }
            if (app.HasExited) { Console.WriteLine("FAIL: app exited early"); failures++; }

            IntPtr hwnd = FindWindow(null, "Gestosh.Hidden.Window.v1");
            if (hwnd == IntPtr.Zero) { Console.WriteLine("FAIL: hidden window not found"); failures++; }
            else
            {
                uint msg = RegisterWindowMessage("Gestosh.Command.v1");
                PostMessage(hwnd, msg, new IntPtr(4), IntPtr.Zero);
            }
            for (int i = 0; i < 50 && !app.HasExited; i++) Thread.Sleep(100);
            if (!app.HasExited) { Console.WriteLine("FAIL: app did not exit"); failures++; }
            string final = ReadShared(log);
            if (!final.Contains("Stopped.")) { Console.WriteLine("FAIL: stop not logged"); failures++; }
            Console.WriteLine("FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                if (app != null && !app.HasExited)
                {
                    IntPtr hwnd = FindWindow(null, "Gestosh.Hidden.Window.v1");
                    if (hwnd != IntPtr.Zero)
                        PostMessage(hwnd, RegisterWindowMessage("Gestosh.Command.v1"), new IntPtr(4), IntPtr.Zero);
                    Thread.Sleep(700);
                    if (!app.HasExited) app.Kill();
                }
            }
            catch { }
        }
    }
}
