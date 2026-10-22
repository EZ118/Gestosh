using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static class GestureStateTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    const string TouchpadKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    const string MarkerKey = @"Software\Gestosh";
    static int _fail;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
        if (!ok) _fail++;
    }

    static int ReadValue()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(TouchpadKey))
        {
            if (key == null) return -1;
            object v = key.GetValue("ThreeFingerSlideEnabled");
            return v == null ? -1 : Convert.ToInt32(v);
        }
    }

    static bool MarkerManaged()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(MarkerKey))
        {
            if (key == null) return false;
            return Convert.ToInt32(key.GetValue("Managed", 0)) == 1;
        }
    }

    static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    static void SendExit()
    {
        IntPtr hwnd = FindWindow(null, "Gestosh.Hidden.Window.v1");
        if (hwnd == IntPtr.Zero) return;
        uint msg = RegisterWindowMessage("Gestosh.Command.v1");
        PostMessage(hwnd, msg, new IntPtr(4), IntPtr.Zero);
    }

    static int Main()
    {
        string root = @"E:\Gestosh";
        string exe = Path.Combine(root, @"dist\Gestosh.Debug.exe");
        string config = Path.Combine(root, @"tests\gesture-test.ini");
        string log = Path.Combine(root, @"tests\gesture-app.log");
        Process app = null;
        int before = ReadValue();
        try
        {
            if (FindWindow(null, "Gestosh.Hidden.Window.v1") != IntPtr.Zero)
                throw new Exception("Gestosh already running; aborting.");
            if (File.Exists(log)) File.Delete(log);
            Console.WriteLine("before value=" + before + " marker=" + MarkerManaged());

            var info = new ProcessStartInfo(exe, "--config \"" + config + "\" --log \"" + log + "\" --verbose");
            info.UseShellExecute = false;
            app = Process.Start(info);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !ReadShared(log).Contains("Gestosh initialized")) Thread.Sleep(100);
            Thread.Sleep(300);

            int during = ReadValue();
            bool marker = MarkerManaged();
            string text = ReadShared(log);
            Check("app is running", app != null && !app.HasExited, "");
            Check("three-finger slide disabled while running", during == 0, "value=" + during);
            Check("takeover marker written", marker, "managed=" + marker);
            Check("hotkeys registered", text.Contains("Hotkeys registered"), "");
            Check("touchpad detected", text.Contains("Touchpad found=True"), "");

            SendExit();
            bool exited = false;
            for (int i = 0; i < 40; i++)
            {
                if (app.HasExited) { exited = true; break; }
                Thread.Sleep(100);
            }
            int after = ReadValue();
            bool markerAfter = MarkerManaged();
            Check("app exits on command", exited, "hasExited=" + (app == null ? "null" : app.HasExited.ToString()));
            Check("three-finger slide restored", after == before, "before=" + before + " after=" + after);
            Check("takeover marker cleared", !markerAfter, "managed=" + markerAfter);
            Check("exit was logged", ReadShared(log).Contains("Stopped."), "");
            Console.WriteLine("FAILURES=" + _fail);
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 2;
        }
        finally
        {
            try
            {
                if (app != null && !app.HasExited)
                {
                    SendExit();
                    Thread.Sleep(700);
                    if (!app.HasExited) app.Kill();
                }
            }
            catch { }
            // Always leave the user's setting in the state we found.
            try
            {
                if (before == -1)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(TouchpadKey, true))
                        if (key != null) key.DeleteValue("ThreeFingerSlideEnabled", false);
                }
                else
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(TouchpadKey, true))
                        if (key != null) key.SetValue("ThreeFingerSlideEnabled", before, RegistryValueKind.DWord);
                }
                using (var marker = Registry.CurrentUser.OpenSubKey(MarkerKey, true))
                {
                    if (marker != null)
                    {
                        marker.DeleteValue("Managed", false);
                        marker.DeleteValue("OriginalExists", false);
                        marker.DeleteValue("OriginalValue", false);
                    }
                }
            }
            catch { }
        }
    }
}
