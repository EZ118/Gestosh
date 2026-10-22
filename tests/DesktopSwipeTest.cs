using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using Gestosh;

/// <summary>
/// Drives DesktopSwipeOverlay directly (no desktop switch is performed: the test
/// passes its own switcher) and saves frames so the composition can be reviewed.
/// </summary>
internal static class DesktopSwipeTest
{
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

    static void Capture(string path, int width, int height)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));
                bitmap.Save(path, ImageFormat.Png);
            }
            Log("  screenshot: " + path);
        }
        catch (Exception ex)
        {
            Log("  screenshot failed: " + ex.Message);
        }
    }

    static string Sample(string path, int x, int y)
    {
        try
        {
            using (var bitmap = new Bitmap(path))
            {
                Color color = bitmap.GetPixel(Math.Min(x, bitmap.Width - 1), Math.Min(y, bitmap.Height - 1));
                return string.Format("#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
            }
        }
        catch (Exception ex) { return "sample failed: " + ex.Message; }
    }

    static int Main()
    {
        var config = new Config();
        config.DesktopSwipeGain = 1.0;
        config.DesktopSwipeSettleMs = 180;

        int width = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
        int height = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        int originX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int originY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        Log("virtual screen " + width + "x" + height + " at " + originX + "," + originY);

        string artifacts = @"E:\Gestosh\tests\artifacts";
        int switches = 0;
        int switchDirection = 0;

        DesktopSwipeOverlay overlay = null;
        try
        {
            overlay = new DesktopSwipeOverlay(config, 1,
                direction => { switches++; switchDirection = direction; },
                closed => { });
            bool shown = overlay.Show();
            Check("desktop slide overlay opens", shown && overlay.Hwnd != IntPtr.Zero, "hwnd=" + overlay.Hwnd);
            if (!shown) return 2;

            Pump(220);
            Check("overlay window is visible", Native.IsWindowVisible(overlay.Hwnd), "hwnd=" + overlay.Hwnd);

            Native.RECT rect;
            Native.GetWindowRect(overlay.Hwnd, out rect);
            Check("overlay covers the virtual screen",
                rect.Left == originX && rect.Top == originY &&
                rect.Right - rect.Left == width && rect.Bottom - rect.Top == height,
                rect.Left + "," + rect.Top + " " + (rect.Right - rect.Left) + "x" + (rect.Bottom - rect.Top));

            Capture(Path.Combine(artifacts, "desktop-slide-start.png"), width, height);

            overlay.Follow(-0.30);
            Pump(120);
            int expected = -(int)Math.Round(0.30 * width);
            Check("fingers move the captured desktop", Math.Abs(overlay.Offset - expected) <= 2,
                "offset=" + overlay.Offset + " expected=" + expected);
            Capture(Path.Combine(artifacts, "desktop-slide-mid.png"), width, height);

            overlay.Follow(-0.62);
            Pump(120);
            Check("slide clamps at one screen width", overlay.Offset >= -width,
                "offset=" + overlay.Offset + " width=" + width);
            string deepShot = Path.Combine(artifacts, "desktop-slide-deep.png");
            Capture(deepShot, width, height);
            Log("  incoming layer sample near the right edge: " + Sample(deepShot, width - 8, height / 2));

            IntPtr hwnd = overlay.Hwnd;
            overlay.Settle(-0.62, 300);
            Pump(500);
            Check("committed slide switches one desktop", switches == 1 && switchDirection == 1,
                "switches=" + switches + " direction=" + switchDirection);
            Check("overlay disappears after the switch", !Native.IsWindow(hwnd), "hwnd=" + hwnd);
            overlay = null;
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            if (overlay != null) overlay.Close();
        }

        int cancelSwitches = 0;
        DesktopSwipeOverlay cancel = null;
        try
        {
            cancel = new DesktopSwipeOverlay(config, -1,
                direction => { cancelSwitches++; },
                closed => { });
            bool shown = cancel.Show();
            Check("second overlay opens for the cancel case", shown, "hwnd=" + cancel.Hwnd);
            if (shown)
            {
                IntPtr hwnd = cancel.Hwnd;
                Pump(150);
                cancel.Follow(0.10);
                Pump(80);
                Check("cancel slide moves to the right", cancel.Offset > 0,
                    "offset=" + cancel.Offset);
                cancel.Settle(0.10, 400);
                Pump(500);
                Check("short slide cancels without switching", cancelSwitches == 0,
                    "switches=" + cancelSwitches);
                Check("cancelled overlay disappears", !Native.IsWindow(hwnd), "hwnd=" + hwnd);
            }
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            if (cancel != null) cancel.Close();
            try
            {
                File.WriteAllText(@"E:\Gestosh\tests\desktop-slide-result.txt", _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
