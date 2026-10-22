using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class ExposeTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint WM_KEYDOWN = 0x0100;
    const int VK_ESCAPE = 0x1B;

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

    static void Capture(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var bmp = new Bitmap(1920, 1080))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(1920, 1080));
                bmp.Save(path, ImageFormat.Png);
            }
        }
        catch (Exception ex) { Log("screenshot failed: " + ex.Message); }
    }

    static int Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : @"E:\Gestosh\dist\Gestosh.Debug.exe";
        string config = args.Length > 1 ? args[1] : @"E:\Gestosh\tests\expose-test.ini";
        string log = args.Length > 2 ? args[2] : @"E:\Gestosh\tests\expose-app.log";
        string shot = @"E:\Gestosh\tests\artifacts\expose-test.png";
        Process app = null;
        try
        {
            if (FindWindow(null, "Gestosh.Hidden.Window.v1") != IntPtr.Zero)
                throw new Exception("Gestosh is already running");

            var psi = new ProcessStartInfo(exe,
                "--config \"" + config + "\" --log \"" + log + "\" --verbose");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            app = Process.Start(psi);
            Log("app pid=" + app.Id);

            IntPtr hidden = IntPtr.Zero;
            for (int i = 0; i < 40 && hidden == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                hidden = FindWindow(null, "Gestosh.Hidden.Window.v1");
            }
            Check("hidden window exists", hidden != IntPtr.Zero, hidden.ToString());
            if (hidden == IntPtr.Zero) return 2;
            Thread.Sleep(1200); // let startup wallpaper pre-rendering finish

            uint command = RegisterWindowMessage("Gestosh.Command.v1");
            Check("command message registered", command != 0, command.ToString());
            PostMessage(hidden, command, new IntPtr(6), IntPtr.Zero);
            Thread.Sleep(120);
            IntPtr midOverlay = FindWindow(null, "Gestosh.AppExpose.v1");
            if (midOverlay != IntPtr.Zero)
            {
                string midShot = @"E:\Gestosh\tests\artifacts\expose-mid.png";
                Capture(midShot);
                Log("mid-transition screenshot=" + midShot);
            }
            Thread.Sleep(1350);

            IntPtr overlay = FindWindow(null, "Gestosh.AppExpose.v1");            Check("App Expose overlay opened", overlay != IntPtr.Zero && IsWindowVisible(overlay),
                "hwnd=" + overlay + " visible=" + (overlay != IntPtr.Zero && IsWindowVisible(overlay)));
            if (overlay != IntPtr.Zero)
            {
                RECT r;
                GetWindowRect(overlay, out r);
                Log("overlay rect=" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
                Capture(shot);
                Log("screenshot=" + shot);

                PostMessage(overlay, WM_KEYDOWN, new IntPtr(VK_ESCAPE), IntPtr.Zero);
                Thread.Sleep(700);
                Check("Escape closes App Expose", !IsWindow(overlay), "hwnd=" + overlay);
            }

            // If Escape was not handled, toggle the overlay closed before exit.
            PostMessage(hidden, command, new IntPtr(6), IntPtr.Zero);
            Thread.Sleep(500);
            PostMessage(hidden, command, new IntPtr(4), IntPtr.Zero);
            Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            if (app != null && !app.HasExited)
            {
                try { app.Kill(); } catch { }
            }
            try { File.WriteAllText(@"E:\Gestosh\tests\expose-test-result.txt", _log.ToString(), new UTF8Encoding(false)); }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
