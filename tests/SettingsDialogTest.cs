using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using Gestosh;

/// <summary>
/// Opens the settings dialog, saves a screenshot and drives the OK button so the
/// callback (and therefore the config round-trip) is exercised without a human.
/// </summary>
internal static class SettingsDialogTest
{
    const uint WM_COMMAND = 0x0111;
    const uint CB_SETCURSEL = 0x014E;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetDlgItem(IntPtr dialog, int id);

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

    static void Capture(string path)
    {
        try
        {
            int width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));
                bitmap.Save(path, ImageFormat.Png);
            }
            Log("  screenshot: " + path);
        }
        catch (Exception ex) { Log("  screenshot failed: " + ex.Message); }
    }

    static int Main()
    {
        string savedThree = null;
        string savedDown = null;
        string savedUp = null;
        string savedHorizontal = null;
        string savedEdge = null;
        int saves = 0;

        var config = new Config();
        config.ThreeFingerAction = "drag";
        config.FourFingerDownAction = "expose";
        config.FourFingerUpAction = "taskview";
        config.FourFingerHorizontalAction = "desktop";
        config.EdgeSwipeAction = "actioncenter";

        SettingsDialog dialog = null;
        try
        {
            dialog = new SettingsDialog(config,
                (three, down, up, horizontal, edge) =>
                {
                    saves++;
                    savedThree = three; savedDown = down; savedUp = up;
                    savedHorizontal = horizontal; savedEdge = edge;
                },
                closed => { });

            bool shown = dialog.Show(IntPtr.Zero);
            Check("settings dialog opens", shown && dialog.Hwnd != IntPtr.Zero, "hwnd=" + dialog.Hwnd);
            if (!shown) return 2;
            Pump(600);

            Native.RECT rect;
            Native.GetWindowRect(dialog.Hwnd, out rect);
            Log("  dialog rect=" + rect.Left + "," + rect.Top + " " +
                (rect.Right - rect.Left) + "x" + (rect.Bottom - rect.Top));
            Check("dialog has a sensible size",
                rect.Right - rect.Left > 300 && rect.Bottom - rect.Top > 200,
                (rect.Right - rect.Left) + "x" + (rect.Bottom - rect.Top));

            Capture(@"E:\Gestosh\tests\artifacts\settings-dialog.png");

            // OK with the current values.
            Native.PostMessage(dialog.Hwnd, WM_COMMAND, new IntPtr(1), IntPtr.Zero);
            Pump(300);
            Check("OK saves the current mappings",
                saves == 1 && savedThree == "drag" && savedDown == "expose" && savedUp == "taskview" &&
                savedHorizontal == "desktop" && savedEdge == "actioncenter",
                "saves=" + saves + " three=" + savedThree + " down=" + savedDown + " up=" + savedUp +
                " horizontal=" + savedHorizontal + " edge=" + savedEdge);
            Check("dialog closes after saving", dialog.Hwnd == IntPtr.Zero, "hwnd=" + dialog.Hwnd);

            // Second pass: change two mappings and confirm the new values are returned.
            var second = new SettingsDialog(config,
                (three, down, up, horizontal, edge) =>
                {
                    saves++;
                    savedThree = three; savedDown = down; savedUp = up;
                    savedHorizontal = horizontal; savedEdge = edge;
                },
                closed => { });
            if (second.Show(IntPtr.Zero))
            {
                Pump(300);
                IntPtr comboThree = GetDlgItem(second.Hwnd, 100);
                IntPtr comboDown = GetDlgItem(second.Hwnd, 101);
                Check("dialog exposes its combo boxes", comboThree != IntPtr.Zero && comboDown != IntPtr.Zero,
                    "three=" + comboThree + " down=" + comboDown);
                Native.SendMessageW(comboThree, CB_SETCURSEL, new IntPtr(0), IntPtr.Zero); // 无
                Native.SendMessageW(comboDown, CB_SETCURSEL, new IntPtr(2), IntPtr.Zero);  // 任务视图
                Native.PostMessage(second.Hwnd, WM_COMMAND, new IntPtr(1), IntPtr.Zero);
                Pump(300);
                Check("changed mappings are returned",
                    saves == 2 && savedThree == "none" && savedDown == "taskview",
                    "saves=" + saves + " three=" + savedThree + " down=" + savedDown);
            }
            else
            {
                Check("second settings dialog opens", false, "hwnd=0");
            }

            // Cancel must not save.
            var third = new SettingsDialog(config,
                (three, down, up, horizontal, edge) => { saves++; },
                closed => { });
            if (third.Show(IntPtr.Zero))
            {
                Pump(200);
                Native.PostMessage(third.Hwnd, WM_COMMAND, new IntPtr(2), IntPtr.Zero);
                Pump(300);
                Check("cancel leaves the mapping untouched", saves == 2, "saves=" + saves);
            }
            else
            {
                Check("third settings dialog opens", false, "hwnd=0");
            }
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            _failures++;
        }
        finally
        {
            if (dialog != null) dialog.Close();
            try
            {
                File.WriteAllText(@"E:\Gestosh\tests\settings-dialog-result.txt", _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Log("FAILURES=" + _failures);
        }
        return _failures == 0 ? 0 : 1;
    }
}
