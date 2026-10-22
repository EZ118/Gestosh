using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RECT = Gestosh.Native.RECT;

namespace Gestosh
{
    internal static class SelfTest
    {
        sealed class TestContext
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Failed;

            public void Check(string name, bool condition, string detail)
            {
                if (condition)
                {
                    Passed++;
                    Lines.Add("[PASS] " + name);
                }
                else
                {
                    Failed++;
                    Lines.Add("[FAIL] " + name + (string.IsNullOrEmpty(detail) ? "" : " : " + detail));
                }
            }

            public void Info(string text)
            {
                Lines.Add("[INFO] " + text);
            }
        }

        sealed class FakeSink : IGestureSink
        {
            public int BeginCount;
            public int EndCount;
            public int DragCount;
            public double DragX;
            public double DragY;
            public int DockCount;
            public int RestoreCount;
            public int TaskViewCount;
            public int ActionCenterCount;
            public int ExposeCount;
            public int ExposeBeginCount;
            public int ExposeEndCount;
            public bool ExposeCommitted;
            public double ExposeLastProgress;
            public bool ExposeOpen;
            public int ExposeCloseEndCount;
            public bool ExposeClosed;
            public double ExposeCloseLastProgress;

            public void BeginDrag() { BeginCount++; }
            public void DragBy(double dx, double dy) { DragCount++; DragX += dx; DragY += dy; }
            public void EndDrag() { EndCount++; }
            public void ShowTaskView() { TaskViewCount++; }
            public void ShowActionCenter() { ActionCenterCount++; }
            public int DesktopSwipeBeginCount;
            public int DesktopSwipeDirection;
            public int DesktopSwipeUpdateCount;
            public double DesktopSwipeLastDx;
            public int DesktopSwipeEndCount;
            public double DesktopSwipeEndDx;
            public double DesktopSwipeEndElapsed;
            public bool DesktopSwipeAccept = true;
            public bool DesktopSwipeBegin(int direction)
            {
                DesktopSwipeBeginCount++;
                DesktopSwipeDirection = direction;
                return DesktopSwipeAccept;
            }
            public void DesktopSwipeUpdate(double dx) { DesktopSwipeUpdateCount++; DesktopSwipeLastDx = dx; }
            public void DesktopSwipeEnd(double dx, double elapsedMs)
            {
                DesktopSwipeEndCount++;
                DesktopSwipeEndDx = dx;
                DesktopSwipeEndElapsed = elapsedMs;
            }
            public void RequestDock() { DockCount++; }
            public void RequestRestore() { RestoreCount++; }
            public void ExposeBegin() { ExposeCount++; ExposeBeginCount++; }
            public void ExposeProgress(double progress) { ExposeLastProgress = progress; }
            public void ExposeEnd(bool commit) { ExposeEndCount++; ExposeCommitted = commit; }
            public bool IsExposeOpen() { return ExposeOpen; }
            public void ExposeCloseProgress(double progress) { ExposeCloseLastProgress = progress; }
            public void ExposeCloseEnd(bool close) { ExposeCloseEndCount++; ExposeClosed = close; }
        }

        public static int Run(string outputPath)
        {
            var test = new TestContext();
            try
            {
                TestDrag(test);
                TestExposeSwipe(test);
                TestDesktopSwipe(test);
                TestGestureActions(test);
                TestPinch(test);
                TestPinchNoise(test);
                TestLayout(test);
                TestTaskbarAvoidance(test);
                TestConfig(test);
                TestHidSynthetic(test);
                TestEnvironment(test);
            }
            catch (Exception ex)
            {
                test.Check("unexpected exception", false, ex.ToString());
            }

            test.Lines.Add("");
            test.Lines.Add("PASS=" + test.Passed + " FAIL=" + test.Failed);
            foreach (string line in test.Lines) ConsoleHost.WriteLine(line);
            if (!string.IsNullOrEmpty(outputPath))
            {
                try { File.WriteAllLines(outputPath, test.Lines.ToArray(), new UTF8Encoding(false)); }
                catch (Exception ex) { ConsoleHost.WriteLine("Cannot write " + outputPath + ": " + ex.Message); }
            }
            return test.Failed == 0 ? 0 : 1;
        }

        static void TestDrag(TestContext test)
        {
            var cfg = new Config();
            cfg.ThreeFingerDrag = true;
            cfg.DragStartThreshold = 0.012;
            var sink = new FakeSink();
            var engine = new GestureEngine(cfg, sink);

            engine.Feed(Ring(3, 0.50, 0.50, 0.03), 0);
            engine.Feed(Ring(3, 0.53, 0.50, 0.03), 120);
            engine.Feed(Ring(3, 0.56, 0.50, 0.03), 150);
            bool began = sink.BeginCount == 1;
            bool dragged = sink.DragCount >= 2 && Math.Abs(sink.DragX - 0.06) < 0.005;
            engine.Feed(new List<Contact>(), 200);
            test.Check("3-finger drag begins and moves", began && dragged,
                "begin=" + sink.BeginCount + " drag=" + sink.DragCount + " dx=" + sink.DragX.ToString("0.000"));
            test.Check("3-finger drag releases", sink.EndCount == 1, "end=" + sink.EndCount);
        }

        static void TestExposeSwipe(TestContext test)
        {
            var cfg = new Config();
            cfg.ExposeSwipeDistance = 0.12;
            cfg.ExposeSwipeVerticalRatio = 1.5;
            cfg.ExposeSwipeMaxMs = 900;
            cfg.GestureCooldownMs = 10;

            var dragSink = new FakeSink();
            var dragEngine = new GestureEngine(cfg, dragSink);
            dragEngine.Feed(Ring(3, 0.50, 0.30, 0.03), 0);
            dragEngine.Feed(Ring(3, 0.50, 0.50, 0.03), 100);
            test.Check("3-finger downward still starts a drag",
                dragSink.BeginCount == 1 && dragSink.ExposeCount == 0,
                "drag=" + dragSink.BeginCount + " expose=" + dragSink.ExposeCount);
            dragEngine.Feed(new List<Contact>(), 200);

            var sink = new FakeSink();
            var engine = new GestureEngine(cfg, sink);
            engine.Feed(Ring(4, 0.50, 0.30, 0.03), 0);
            engine.Feed(Ring(4, 0.50, 0.50, 0.03), 100);
            test.Check("4-finger swipe down opens App Expose",
                sink.ExposeCount == 1 && sink.ExposeLastProgress > 0.9,
                "expose=" + sink.ExposeCount + " progress=" + sink.ExposeLastProgress.ToString("0.00"));

            engine.Feed(new List<Contact>(), 200);
            test.Check("4-finger App Expose commits on release",
                sink.ExposeEndCount == 1 && sink.ExposeCommitted,
                "end=" + sink.ExposeEndCount + " commit=" + sink.ExposeCommitted);

            var cancel = new FakeSink();
            var cancelEngine = new GestureEngine(cfg, cancel);
            cancelEngine.Feed(Ring(4, 0.50, 0.30, 0.03), 0);
            cancelEngine.Feed(Ring(4, 0.50, 0.34, 0.03), 100);
            cancelEngine.Feed(new List<Contact>(), 150);
            test.Check("short 4-finger swipe cancels",
                cancel.ExposeEndCount == 1 && !cancel.ExposeCommitted,
                "end=" + cancel.ExposeEndCount + " commit=" + cancel.ExposeCommitted);

            var closeSink = new FakeSink();
            closeSink.ExposeOpen = true;
            var closeEngine = new GestureEngine(cfg, closeSink);
            closeEngine.Feed(Ring(4, 0.50, 0.50, 0.03), 0);
            closeEngine.Feed(Ring(4, 0.50, 0.40, 0.03), 100);
            bool closeProgressed = closeSink.ExposeCloseLastProgress > 0.5;
            closeEngine.Feed(new List<Contact>(), 200);
            test.Check("4-finger swipe up closes App Expose",
                closeProgressed && closeSink.ExposeCloseEndCount == 1 && closeSink.ExposeClosed,
                "progress=" + closeSink.ExposeCloseLastProgress.ToString("0.00") +
                " end=" + closeSink.ExposeCloseEndCount + " closed=" + closeSink.ExposeClosed);

            var taskSink = new FakeSink();
            var taskEngine = new GestureEngine(cfg, taskSink);
            taskEngine.Feed(Ring(4, 0.50, 0.60, 0.03), 0);
            taskEngine.Feed(Ring(4, 0.50, 0.46, 0.03), 100);
            test.Check("4-finger swipe up opens Task View when App Expose is closed",
                taskSink.TaskViewCount == 1 && taskSink.ExposeCount == 0,
                "task=" + taskSink.TaskViewCount + " expose=" + taskSink.ExposeCount);

            var edgeSink = new FakeSink();
            var edgeEngine = new GestureEngine(cfg, edgeSink);
            edgeEngine.Feed(Ring(2, 0.93, 0.50, 0.02), 0);
            edgeEngine.Feed(Ring(2, 0.82, 0.50, 0.02), 100);
            test.Check("2-finger right-edge left swipe opens Action Center",
                edgeSink.ActionCenterCount == 1,
                "actionCenter=" + edgeSink.ActionCenterCount);

            var centerSink = new FakeSink();
            var centerEngine = new GestureEngine(cfg, centerSink);
            centerEngine.Feed(Ring(2, 0.50, 0.50, 0.02), 0);
            centerEngine.Feed(Ring(2, 0.38, 0.50, 0.02), 100);
            test.Check("2-finger center swipe does not open Action Center",
                centerSink.ActionCenterCount == 0,
                "actionCenter=" + centerSink.ActionCenterCount);
        }

        static void TestDesktopSwipe(TestContext test)
        {
            var cfg = new Config();
            cfg.DesktopSwipe = true;
            cfg.DesktopSwipeStartDistance = 0.012;
            cfg.ExposeSwipeVerticalRatio = 1.5;

            // Fingers to the right bring the previous desktop in from the left.
            var rightSink = new FakeSink();
            var rightEngine = new GestureEngine(cfg, rightSink);
            rightEngine.Feed(Ring(4, 0.50, 0.50, 0.03), 0);
            rightEngine.Feed(Ring(4, 0.56, 0.505, 0.03), 60);
            rightEngine.Feed(Ring(4, 0.62, 0.51, 0.03), 120);
            test.Check("4-finger swipe right starts a desktop slide to the previous desktop",
                rightSink.DesktopSwipeBeginCount == 1 && rightSink.DesktopSwipeDirection == -1 &&
                rightSink.ExposeCount == 0,
                "begin=" + rightSink.DesktopSwipeBeginCount + " dir=" + rightSink.DesktopSwipeDirection +
                " expose=" + rightSink.ExposeCount);
            test.Check("desktop slide follows the fingers",
                rightSink.DesktopSwipeUpdateCount >= 2 && Math.Abs(rightSink.DesktopSwipeLastDx - 0.12) < 0.005,
                "updates=" + rightSink.DesktopSwipeUpdateCount + " dx=" + rightSink.DesktopSwipeLastDx.ToString("0.000"));
            rightEngine.Feed(new List<Contact>(), 300);
            test.Check("desktop slide ends with the total travel",
                rightSink.DesktopSwipeEndCount == 1 && Math.Abs(rightSink.DesktopSwipeEndDx - 0.12) < 0.005 &&
                rightSink.DesktopSwipeEndElapsed >= 200,
                "end=" + rightSink.DesktopSwipeEndCount + " dx=" + rightSink.DesktopSwipeEndDx.ToString("0.000") +
                " elapsed=" + rightSink.DesktopSwipeEndElapsed.ToString("0"));

            var leftSink = new FakeSink();
            var leftEngine = new GestureEngine(cfg, leftSink);
            leftEngine.Feed(Ring(4, 0.50, 0.50, 0.03), 0);
            leftEngine.Feed(Ring(4, 0.42, 0.50, 0.03), 60);
            test.Check("4-finger swipe left slides to the next desktop",
                leftSink.DesktopSwipeBeginCount == 1 && leftSink.DesktopSwipeDirection == 1 &&
                leftSink.ExposeCount == 0,
                "begin=" + leftSink.DesktopSwipeBeginCount + " dir=" + leftSink.DesktopSwipeDirection);

            var downSink = new FakeSink();
            var downEngine = new GestureEngine(cfg, downSink);
            downEngine.Feed(Ring(4, 0.50, 0.40, 0.03), 0);
            downEngine.Feed(Ring(4, 0.50, 0.52, 0.03), 100);
            test.Check("4-finger vertical swipe still opens App Expose",
                downSink.DesktopSwipeBeginCount == 0 && downSink.ExposeCount == 1,
                "slide=" + downSink.DesktopSwipeBeginCount + " expose=" + downSink.ExposeCount);

            var diagonalSink = new FakeSink();
            var diagonalEngine = new GestureEngine(cfg, diagonalSink);
            diagonalEngine.Feed(Ring(4, 0.50, 0.50, 0.03), 0);
            diagonalEngine.Feed(Ring(4, 0.53, 0.53, 0.03), 100);
            test.Check("4-finger diagonal swipe triggers neither gesture",
                diagonalSink.DesktopSwipeBeginCount == 0 && diagonalSink.ExposeCount == 0,
                "slide=" + diagonalSink.DesktopSwipeBeginCount + " expose=" + diagonalSink.ExposeCount);

            var rejectSink = new FakeSink();
            rejectSink.DesktopSwipeAccept = false;
            var rejectEngine = new GestureEngine(cfg, rejectSink);
            rejectEngine.Feed(Ring(4, 0.50, 0.50, 0.03), 0);
            rejectEngine.Feed(Ring(4, 0.44, 0.50, 0.03), 60);
            rejectEngine.Feed(new List<Contact>(), 200);
            test.Check("desktop slide is not retried when the overlay is unavailable",
                rejectSink.DesktopSwipeBeginCount == 1 && rejectSink.DesktopSwipeUpdateCount == 0 &&
                rejectSink.DesktopSwipeEndCount == 0,
                "begin=" + rejectSink.DesktopSwipeBeginCount + " update=" + rejectSink.DesktopSwipeUpdateCount +
                " end=" + rejectSink.DesktopSwipeEndCount);

            var openSink = new FakeSink();
            openSink.ExposeOpen = true;
            var openEngine = new GestureEngine(cfg, openSink);
            openEngine.Feed(Ring(4, 0.50, 0.50, 0.03), 0);
            openEngine.Feed(Ring(4, 0.42, 0.50, 0.03), 60);
            test.Check("horizontal swipe inside App Expose does not slide desktops",
                openSink.DesktopSwipeBeginCount == 0,
                "begin=" + openSink.DesktopSwipeBeginCount);
        }

        static void TestGestureActions(TestContext test)
        {
            string path = Path.Combine(Path.GetTempPath(), "gestosh-actions.ini");
            try
            {
                File.WriteAllText(path,
                    "[gestures]\r\nthreeFingerAction=none\r\nfourFingerDownAction=taskview\r\n" +
                    "fourFingerUpAction=none\r\nfourFingerHorizontalAction=none\r\nedgeSwipeAction=none\r\n",
                    new UTF8Encoding(false));
                var parsed = Config.Load(path);
                test.Check("config parses the gesture mappings",
                    parsed.ThreeFingerAction == "none" && parsed.FourFingerDownAction == "taskview" &&
                    parsed.FourFingerUpAction == "none" && parsed.FourFingerHorizontalAction == "none" &&
                    parsed.EdgeSwipeAction == "none" && !parsed.ThreeFingerDrag && !parsed.DesktopSwipe,
                    "three=" + parsed.ThreeFingerAction + " down=" + parsed.FourFingerDownAction +
                    " up=" + parsed.FourFingerUpAction + " horizontal=" + parsed.FourFingerHorizontalAction +
                    " edge=" + parsed.EdgeSwipeAction);

                File.WriteAllText(path,
                    "; keep me\r\n[general]\r\nenabled=true\r\n\r\n[gestures]\r\n; comment\r\n" +
                    "threeFingerDrag=true\r\nexposeSwipeDistance=0.2\r\n\r\n[dock]\r\nexposePx=42\r\n",
                    new UTF8Encoding(false));
                Config.SaveGestureActions(path, "drag", "expose", "taskview", "desktop", "actioncenter");
                string text = File.ReadAllText(path);
                var reloaded = Config.Load(path);
                test.Check("settings are written back without dropping other keys",
                    text.Contains("; keep me") && text.Contains("exposePx=42") && text.Contains("; comment") &&
                    reloaded.ThreeFingerAction == "drag" && reloaded.FourFingerDownAction == "expose" &&
                    reloaded.FourFingerUpAction == "taskview" && reloaded.FourFingerHorizontalAction == "desktop" &&
                    reloaded.EdgeSwipeAction == "actioncenter" && reloaded.ThreeFingerDrag && reloaded.DesktopSwipe,
                    "length=" + text.Length);

                var none = new Config();
                none.FourFingerDownAction = "none";
                none.ThreeFingerAction = "none";
                none.FourFingerHorizontalAction = "none";
                none.EdgeSwipeAction = "none";
                var noneSink = new FakeSink();
                var noneEngine = new GestureEngine(none, noneSink);
                noneEngine.Feed(Ring(4, 0.50, 0.40, 0.03), 0);
                noneEngine.Feed(Ring(4, 0.50, 0.55, 0.03), 100);
                noneEngine.Feed(new List<Contact>(), 200);
                noneEngine.Feed(Ring(3, 0.50, 0.50, 0.03), 300);
                noneEngine.Feed(Ring(3, 0.60, 0.50, 0.03), 380);
                test.Check("gestures mapped to none stay silent",
                    noneSink.ExposeCount == 0 && noneSink.TaskViewCount == 0 && noneSink.BeginCount == 0 &&
                    noneSink.DesktopSwipeBeginCount == 0 && noneSink.ActionCenterCount == 0,
                    "expose=" + noneSink.ExposeCount + " task=" + noneSink.TaskViewCount +
                    " drag=" + noneSink.BeginCount + " slide=" + noneSink.DesktopSwipeBeginCount);

                var taskConfig = new Config();
                taskConfig.FourFingerDownAction = "taskview";
                taskConfig.GestureCooldownMs = 10;
                var taskSink = new FakeSink();
                var taskEngine = new GestureEngine(taskConfig, taskSink);
                taskEngine.Feed(Ring(4, 0.50, 0.40, 0.03), 0);
                taskEngine.Feed(Ring(4, 0.50, 0.55, 0.03), 100);
                test.Check("4-finger down can open Task View",
                    taskSink.TaskViewCount == 1 && taskSink.ExposeCount == 0,
                    "task=" + taskSink.TaskViewCount + " expose=" + taskSink.ExposeCount);

                var upConfig = new Config();
                upConfig.FourFingerUpAction = "expose";
                upConfig.GestureCooldownMs = 10;
                var upSink = new FakeSink();
                var upEngine = new GestureEngine(upConfig, upSink);
                upEngine.Feed(Ring(4, 0.50, 0.60, 0.03), 0);
                upEngine.Feed(Ring(4, 0.50, 0.44, 0.03), 100);
                test.Check("4-finger up can open App Expose",
                    upSink.ExposeCount == 1 && upSink.ExposeLastProgress > 0.5,
                    "expose=" + upSink.ExposeCount + " progress=" + upSink.ExposeLastProgress.ToString("0.00"));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        static void TestPinch(TestContext test)
        {
            var cfg = new Config();
            cfg.PinchFingers = 5;
            cfg.PinchSettleMs = 10;
            cfg.GestureCooldownMs = 10;
            cfg.PinchOutRatio = 1.35;
            cfg.PinchInRatio = 0.74;
            cfg.PinchMinTravel = 0.045;
            var sink = new FakeSink();
            var engine = new GestureEngine(cfg, sink);

            engine.Feed(Ring(5, 0.50, 0.50, 0.05), 0);
            engine.Feed(Ring(5, 0.50, 0.50, 0.15), 100);
            bool docked = sink.DockCount == 1;
            engine.Feed(Ring(5, 0.50, 0.50, 0.16), 150);
            test.Check("5-finger pinch-out docks exactly once", docked && sink.DockCount == 1, "dock=" + sink.DockCount);

            engine.Feed(new List<Contact>(), 200);
            engine.Feed(Ring(5, 0.50, 0.50, 0.15), 300);
            engine.Feed(Ring(5, 0.50, 0.50, 0.05), 400);
            test.Check("5-finger pinch-in restores", sink.RestoreCount == 1, "restore=" + sink.RestoreCount);
        }

        static void TestPinchNoise(TestContext test)
        {
            var cfg = new Config();
            cfg.PinchSettleMs = 10;
            cfg.GestureCooldownMs = 0;
            var sink = new FakeSink();
            var engine = new GestureEngine(cfg, sink);
            for (int i = 0; i < 20; i++)
            {
                engine.Feed(Ring(5, 0.50, 0.50, 0.05 + (i % 2 == 0 ? 0.001 : -0.001)), i * 20);
            }
            test.Check("5-finger noise does not trigger", sink.DockCount == 0 && sink.RestoreCount == 0,
                "dock=" + sink.DockCount + " restore=" + sink.RestoreCount);
        }

        static void TestLayout(TestContext test)
        {
            RECT work;
            work.Left = 0;
            work.Top = 0;
            work.Right = 1920;
            work.Bottom = 1040;
            RECT window;
            window.Left = 100;
            window.Top = 100;
            window.Right = 900;
            window.Bottom = 700;
            int depth = 42;

            RECT left = DockLayout.ComputeRect(window, work, DockEdge.Left, depth, 48);
            test.Check("dock left keeps size", left.Width == window.Width && left.Height == window.Height, left.ToString());
            test.Check("dock left exposes 42px", left.Right == work.Left + depth, left.ToString());

            RECT right = DockLayout.ComputeRect(window, work, DockEdge.Right, depth, 48);
            test.Check("dock right keeps size", right.Width == window.Width && right.Height == window.Height, right.ToString());
            test.Check("dock right exposes 42px", right.Left == work.Right - depth, right.ToString());

            RECT top = DockLayout.ComputeRect(window, work, DockEdge.Top, depth, 48);
            test.Check("dock top keeps size", top.Width == window.Width && top.Height == window.Height, top.ToString());
            test.Check("dock top exposes 42px", top.Bottom == work.Top + depth, top.ToString());

            RECT bottom = DockLayout.ComputeRect(window, work, DockEdge.Bottom, depth, 48);
            test.Check("dock bottom keeps size", bottom.Width == window.Width && bottom.Height == window.Height, bottom.ToString());
            test.Check("dock bottom exposes 42px", bottom.Top == work.Bottom - depth, bottom.ToString());

            RECT tiny;
            tiny.Left = 0;
            tiny.Top = 0;
            tiny.Right = 20;
            tiny.Bottom = 20;
            RECT tinyLeft = DockLayout.ComputeRect(tiny, work, DockEdge.Left, depth, 48);
            test.Check("tiny window does not become fully off-screen", tinyLeft.Right > work.Left, tinyLeft.ToString());
        }

        static void TestTaskbarAvoidance(TestContext test)
        {
            RECT work;
            work.Left = 0;
            work.Top = 0;
            work.Right = 1920;
            work.Bottom = 1040;

            var entries = new List<DockEntry>();
            for (int i = 0; i < 4; i++)
            {
                var entry = new DockEntry();
                entry.Hwnd = new IntPtr(i + 1);
                entry.WorkArea = work;
                entry.Dpi = 96;
                entry.ZOrder = i;
                entry.TaskbarEdge = DockEdge.Bottom;
                entry.OriginalRect = new RECT();
                entry.OriginalRect.Left = 100 + i * 300;
                entry.OriginalRect.Top = 200 + i * 40;
                entry.OriginalRect.Right = entry.OriginalRect.Left + 500;
                entry.OriginalRect.Bottom = entry.OriginalRect.Top + 400;
                entries.Add(entry);
            }

            DockLayout.Assign(entries, new Config());
            bool bottom = false;
            var edges = new StringBuilder();
            foreach (var entry in entries)
            {
                if (entry.Edge == DockEdge.Bottom) bottom = true;
                if (edges.Length > 0) edges.Append(",");
                edges.Append(entry.Edge);
            }
            test.Check("layout never chooses the taskbar edge", !bottom, edges.ToString());
        }

        static void TestConfig(TestContext test)
        {
            string path = Path.Combine(Path.GetTempPath(), "gestosh-selftest-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                File.WriteAllText(path, "[gestures]\r\npinchFingers=4\r\ndragSensitivity=2.0\r\n[dock]\r\nexposePx=60\r\n", Encoding.UTF8);
                var cfg = Config.Load(path);
                test.Check("config parses ints", cfg.PinchFingers == 4, "pinchFingers=" + cfg.PinchFingers);
                test.Check("config parses doubles", Math.Abs(cfg.DragSensitivity - 2.0) < 0.0001, "sensitivity=" + cfg.DragSensitivity);
                test.Check("config parses dock values", cfg.ExposePx == 60, "exposePx=" + cfg.ExposePx);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        static void TestHidSynthetic(TestContext test)
        {
            var manager = new TouchpadManager();
            try
            {
                if (!manager.Discover() || manager.Devices.Count == 0)
                {
                    test.Info("HID parser test skipped: no Precision Touchpad on this machine.");
                    return;
                }
                var device = manager.Devices[0];
                test.Info("HID parser test device: " + device.Describe().Replace("\r\n", " | "));
                if (device.InputReportLength < 2)
                {
                    test.Info("HID parser test skipped: report length is too small.");
                    return;
                }

                byte[] full = new byte[device.InputReportLength];
                for (int i = 0; i < full.Length; i++) full[i] = 0xFF;
                full[0] = (byte)device.ReportId;
                device.IncludesReportId = null;
                var frame = device.Parse(full, 0, full.Length, 0);
                test.Check("HID parser reads a full 5-contact report", frame != null && frame.Active.Count == 5,
                    frame == null ? "null" : "active=" + frame.Active.Count + " all=" + frame.All.Count);
                test.Check("HID parser detects the report ID", device.IncludesReportId == true,
                    "includesReportId=" + device.IncludesReportId);

                byte[] noId = new byte[full.Length - 1];
                for (int i = 0; i < noId.Length; i++) noId[i] = 0xFF;
                device.IncludesReportId = null;
                var frame2 = device.Parse(noId, 0, noId.Length, 0);
                test.Check("HID parser can prepend a missing report ID", frame2 != null && frame2.Active.Count == 5 && frame2.AddedReportId,
                    frame2 == null ? "null" : "active=" + frame2.Active.Count + " addedId=" + frame2.AddedReportId);

                byte[] noIdTricky = new byte[full.Length - 1];
                for (int i = 0; i < noIdTricky.Length; i++) noIdTricky[i] = 0xFF;
                noIdTricky[0] = (byte)device.ReportId; // a data byte that happens to equal the report ID
                device.IncludesReportId = null;
                var frameTricky = device.Parse(noIdTricky, 0, noIdTricky.Length, 0);
                test.Check("HID parser uses the report length instead of a coincidental first byte",
                    frameTricky != null && frameTricky.AddedReportId && frameTricky.Active.Count > 0,
                    frameTricky == null ? "null" : "active=" + frameTricky.Active.Count + " addedId=" + frameTricky.AddedReportId);

                byte[] zero = new byte[device.InputReportLength];
                zero[0] = (byte)device.ReportId;
                device.IncludesReportId = null;
                var frame3 = device.Parse(zero, 0, zero.Length, 0);
                test.Check("HID parser treats an idle report as no active contacts", frame3 != null && frame3.Active.Count == 0,
                    frame3 == null ? "null" : "active=" + frame3.Active.Count + " all=" + frame3.All.Count);
            }
            catch (Exception ex)
            {
                test.Check("HID parser test", false, ex.ToString());
            }
            finally
            {
                manager.Dispose();
            }
        }

        static void TestEnvironment(TestContext test)
        {
            using (var desktop = new VirtualDesktop())
            {
                test.Info("VirtualDesktopManager available=" + desktop.Available);
                var appConfig = new Config();
                var windows = WindowScanner.Scan(appConfig, desktop, IntPtr.Zero);
                test.Check("window scanner is enumerable", windows != null, "null result");
                test.Info("eligible top-level windows on this desktop: " + (windows == null ? 0 : windows.Count));
            }
        }

        static List<Contact> Ring(int count, double cx, double cy, double spread)
        {
            var list = new List<Contact>();
            for (int i = 0; i < count; i++)
            {
                double angle = Math.PI * 2.0 * i / count;
                list.Add(new Contact
                {
                    Id = i,
                    X = cx + Math.Cos(angle) * spread,
                    Y = cy + Math.Sin(angle) * spread,
                    Tip = true,
                    Confidence = true
                });
            }
            return list;
        }
    }
}
