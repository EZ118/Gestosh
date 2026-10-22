using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RECT = Gestosh.Native.RECT;

namespace Gestosh
{
    internal static class Diagnostics
    {
        static TouchpadManager _touchpads;
        static GestureEngine _engine;
        static double _lastPrintTime;
        static int _lastContactCount = -1;
        static int _framesPrinted;
        static bool _verbose;
        static uint _windowMessage = 0;

        public static int Run(CommandLine command, string configPath, string exePath)
        {
            _verbose = command.Verbose;
            ConsoleHost.Ensure();
            ConsoleHost.WriteLine("Gestosh diagnostics");
            ConsoleHost.WriteLine("========================================");
            ConsoleHost.WriteLine("OS: " + Environment.OSVersion.VersionString + "  64-bit process=" + Environment.Is64BitProcess);
            ConsoleHost.WriteLine("Exe: " + exePath);
            ConsoleHost.WriteLine("Config: " + configPath);
            ConsoleHost.WriteLine("Log: " + Log.Path);
            ConsoleHost.WriteLine();

            var config = Config.Load(configPath);
            if (!File.Exists(configPath))
            {
                try { Config.WriteTemplate(configPath); } catch { }
            }

            using (var desktop = new VirtualDesktop())
            {
                ConsoleHost.WriteLine("Virtual desktop manager: " + (desktop.Available ? "available" : "unavailable (all windows will be considered current)"));
            }

            _touchpads = new TouchpadManager();
            bool found = _touchpads.Discover();
            ConsoleHost.WriteLine();
            if (!found)
            {
                ConsoleHost.WriteLine("No Windows Precision Touchpad (Usage Page 0x0D, Usage 0x05) was found.");
                ConsoleHost.WriteLine("Hints:");
                ConsoleHost.WriteLine("  - This tool needs a Windows Precision Touchpad.");
                ConsoleHost.WriteLine("  - Check that the touchpad is enabled in Windows Settings.");
                ConsoleHost.WriteLine("  - Run this from a laptop with a Precision Touchpad.");
                ConsoleHost.WriteLine("  - A 5-contact touchpad is required for the pinch gesture.");
                _touchpads.Dispose();
                return 2;
            }

            foreach (var device in _touchpads.Devices)
            {
                ConsoleHost.WriteLine(device.Describe());
            }
            ConsoleHost.WriteLine();

            IntPtr instance = Native.GetModuleHandleW(null);
            IntPtr hwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, "STATIC", "Gestosh.Diagnostic.Window",
                Native.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                ConsoleHost.WriteLine("Cannot create diagnostic window. Win32=" + Marshal.GetLastWin32Error());
                _touchpads.Dispose();
                return 3;
            }
            var proc = new Native.WndProc(DiagWindowProc);
            IntPtr oldProc = Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));
            _windowMessage = Native.RegisterWindowMessageW("Gestosh.Diagnostic.Input");

            _engine = new GestureEngine(config, new DiagnosticSink());
            _touchpads.Frame += OnFrame;
            if (!_touchpads.Register(hwnd))
            {
                ConsoleHost.WriteLine("Raw input registration failed. Win32=" + Marshal.GetLastWin32Error());
            }
            ConsoleHost.WriteLine("Listening for " + command.DiagnoseSeconds + " seconds. Touch the touchpad now.");
            ConsoleHost.WriteLine("Gesture thresholds: pinchFingers=" + config.PinchFingers + " outRatio=" + config.PinchOutRatio
                + " inRatio=" + config.PinchInRatio + " minTravel=" + config.PinchMinTravel + " settleMs=" + config.PinchSettleMs);
            ConsoleHost.WriteLine();

            DateTime end = DateTime.UtcNow.AddSeconds(Math.Max(1, command.DiagnoseSeconds));
            while (DateTime.UtcNow < end)
            {
                Native.MSG message;
                while (Native.PeekMessageW(out message, IntPtr.Zero, 0, 0, 1))
                {
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessageW(ref message);
                }
                Thread.Sleep(5);
            }

            ConsoleHost.WriteLine();
            ConsoleHost.WriteLine("Reports: " + _touchpads.Reports + "  rawReports=" + _touchpads.RawReports + "  frames printed=" + _framesPrinted);
            _touchpads.Frame -= OnFrame;
            _touchpads.Dispose();
            Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, oldProc);
            Native.DestroyWindow(hwnd);
            return 0;
        }

        static IntPtr DiagWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == Native.WM_INPUT)
            {
                if (_touchpads != null) _touchpads.HandleRawInput(lParam);
                return IntPtr.Zero;
            }
            if (message == Native.WM_DESTROY)
            {
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        static void OnFrame(TouchpadFrame frame)
        {
            double now = frame.TimeMs;
            bool countChanged = frame.ContactCount != _lastContactCount;
            if (!countChanged && frame.Active.Count == 0) return;
            if (!countChanged && now - _lastPrintTime < 60) return;
            _lastPrintTime = now;
            _lastContactCount = frame.ContactCount;
            _framesPrinted++;

            var sb = new StringBuilder();
            sb.AppendFormat("rid={0,-3} cc={1,-2} active={2,-2}", frame.ReportId, frame.ContactCount, frame.Active.Count);
            if (frame.AddedReportId) sb.Append(" [id prepended]");
            sb.Append("  ");
            foreach (var contact in frame.All)
            {
                if (!contact.Tip) continue;
                sb.AppendFormat("id={0} c={1} ({2:0.000},{3:0.000})  ", contact.Id, contact.Confidence ? 1 : 0, contact.X, contact.Y);
            }
            if (_verbose && _framesPrinted <= 20) sb.Append("raw=" + frame.RawHex);
            ConsoleHost.WriteLine(sb.ToString());
            Console.Out.Flush();

            if (_engine != null) _engine.Feed(frame.Active, frame.TimeMs);
        }

        sealed class DiagnosticSink : IGestureSink
        {
            bool _dragging;
            double _dragTotal;

            public void BeginDrag()
            {
                _dragging = true;
                _dragTotal = 0;
                ConsoleHost.WriteLine("  >>> GESTURE: 3-finger drag begin");
            }

            public void DragBy(double dxNormalized, double dyNormalized)
            {
                if (!_dragging) return;
                _dragTotal += Math.Abs(dxNormalized) + Math.Abs(dyNormalized);
                if (_dragTotal > 0.03)
                {
                    _dragTotal = 0;
                    ConsoleHost.WriteLine(string.Format("  >>> GESTURE: drag move dx={0:0.000} dy={1:0.000}", dxNormalized, dyNormalized));
                }
            }

            public void EndDrag()
            {
                if (!_dragging) return;
                _dragging = false;
                ConsoleHost.WriteLine("  >>> GESTURE: drag end");
            }

            public void RequestDock()
            {
                ConsoleHost.WriteLine("  >>> GESTURE: DOCK (pinch out)");
            }

            public void ShowTaskView()
            {
                ConsoleHost.WriteLine("  >>> GESTURE: Windows Task View (4-finger swipe up)");
            }

            public void ShowActionCenter()
            {
                ConsoleHost.WriteLine("  >>> GESTURE: Windows Action Center (2-finger right-edge swipe)");
            }

            public void RequestRestore()
            {
                ConsoleHost.WriteLine("  >>> GESTURE: RESTORE (pinch in)");
            }

            public void ExposeBegin()
            {
                ConsoleHost.WriteLine("  >>> GESTURE: APP EXPOSE begin");
            }

            public void ExposeProgress(double progress)
            {
                ConsoleHost.WriteLine(string.Format("  >>> GESTURE: APP EXPOSE progress {0:0.00}", progress));
            }

            public void ExposeEnd(bool commit)
            {
                ConsoleHost.WriteLine("  >>> GESTURE: APP EXPOSE end commit=" + commit);
            }

            public bool IsExposeOpen()
            {
                return false;
            }

            public void ExposeCloseProgress(double progress)
            {
                ConsoleHost.WriteLine(string.Format("  >>> GESTURE: APP EXPOSE close {0:0.00}", progress));
            }

            public void ExposeCloseEnd(bool close)
            {
                ConsoleHost.WriteLine("  >>> GESTURE: APP EXPOSE close end=" + close);
            }

            public bool DesktopSwipeBegin(int direction)
            {
                ConsoleHost.WriteLine("  >>> GESTURE: DESKTOP slide -> " + (direction > 0 ? "next" : "previous"));
                return false;
            }

            public void DesktopSwipeUpdate(double dxNormalized)
            {
                ConsoleHost.WriteLine(string.Format("  >>> GESTURE: DESKTOP slide dx={0:0.000}", dxNormalized));
            }

            public void DesktopSwipeEnd(double dxNormalized, double elapsedMs)
            {
                ConsoleHost.WriteLine(string.Format("  >>> GESTURE: DESKTOP slide end dx={0:0.000} elapsed={1:0}ms",
                    dxNormalized, elapsedMs));
            }
        }
    }
}
