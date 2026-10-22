using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RECT = Gestosh.Native.RECT;
using WINDOWPLACEMENT = Gestosh.Native.WINDOWPLACEMENT;

namespace Gestosh
{
    internal enum DockEdge { Left, Right, Top, Bottom, None }

    internal sealed class WindowInfo
    {
        public IntPtr Hwnd;
        public RECT Rect;
        public WINDOWPLACEMENT Placement;
        public bool HasPlacement;
        public bool Maximized;
        public int ZOrder;
        public RECT WorkArea;
        public int Dpi;
        public string ProcessName = "";
        public string ClassName = "";
        public string Title = "";
        public DockEdge TaskbarEdge = DockEdge.None;
    }

    internal sealed class DockEntry
    {
        public IntPtr Hwnd;
        public RECT OriginalRect;
        public WINDOWPLACEMENT OriginalPlacement;
        public bool HasPlacement;
        public bool WasMaximized;
        public RECT DockRect;
        public DockEdge Edge;
        public RECT WorkArea;
        public int Dpi;
        public int ZOrder;
        public DockEdge TaskbarEdge = DockEdge.None;

        public bool IsAlive { get { return Native.IsWindow(Hwnd); } }
    }

    internal sealed class DockSession
    {
        public List<DockEntry> Entries = new List<DockEntry>();
        public bool Docked;
    }

    internal sealed class DockManager
    {
        public const int WM_ANIM_DONE = Native.WM_APP + 10;
        public const int WM_DOCK_CLICK = Native.WM_APP + 11;

        readonly App _app;
        readonly Animator _animator;
        DockSession _session;
        bool _animating;
        bool _animationWasRestore;
        int _generation;
        Pending _pending;

        enum Pending { None, Dock, Restore }

        public DockManager(App app)
        {
            _app = app;
            _animator = new Animator(app);
        }

        public bool IsDocked { get { return _session != null && _session.Docked; } }
        public bool IsBusy { get { return _animating || _session != null; } }
        public DockSession Session { get { return _session; } }

        public event Action StateChanged;

        public bool ContainsWindow(IntPtr hwnd)
        {
            var session = _session;
            if (session == null) return false;
            foreach (var entry in session.Entries)
                if (entry.Hwnd == hwnd) return true;
            return false;
        }

        public void FinalizeForExternalChange()
        {
            if (_session == null) return;
            Log.Info("External show-desktop change; restoring the dock session instantly.");
            FinalizeSessionInstant();
        }

        void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null) handler();
        }

        public void RequestDock()
        {
            if (_animating) { _pending = Pending.Dock; return; }
            if (_session != null && _session.Docked)
            {
                if (SessionOnCurrentDesktop()) return;
                Log.Info("Dock session belongs to another virtual desktop; restoring it instantly.");
                FinalizeSessionInstant();
            }
            if (_session != null) FinalizeSessionInstant();

            var windows = WindowScanner.Scan(_app.Config, _app.VirtualDesktop, _app.Hwnd);
            if (windows.Count == 0)
            {
                Log.Info("Dock requested, but no eligible windows were found.");
                return;
            }

            var entries = new List<DockEntry>();
            foreach (var window in windows)
            {
                var entry = new DockEntry();
                entry.Hwnd = window.Hwnd;
                entry.OriginalRect = window.Rect;
                entry.OriginalPlacement = window.Placement;
                entry.HasPlacement = window.HasPlacement;
                entry.WasMaximized = window.Maximized;
                entry.WorkArea = window.WorkArea;
                entry.Dpi = window.Dpi;
                entry.ZOrder = window.ZOrder;
                entry.TaskbarEdge = window.TaskbarEdge;
                entries.Add(entry);
            }

            var prepared = new List<DockEntry>();
            foreach (var entry in entries)
            {
                if (entry.WasMaximized && !entry.HasPlacement)
                {
                    Log.Warn("Maximized window 0x" + entry.Hwnd.ToInt64().ToString("x") + " has no placement; skipping.");
                    continue;
                }
                if (entry.WasMaximized && !PrepareMaximized(entry))
                {
                    Log.Warn("Cannot move maximized window 0x" + entry.Hwnd.ToInt64().ToString("x") + "; skipping.");
                    continue;
                }
                prepared.Add(entry);
            }
            if (prepared.Count == 0) return;

            DockLayout.Assign(prepared, _app.Config);
            _session = new DockSession();
            _session.Entries = prepared;
            _session.Docked = false;
            _animating = true;
            _animationWasRestore = false;
            _pending = Pending.None;
            int generation = ++_generation;
            var moves = new List<Animator.Move>();
            foreach (var entry in prepared)
                moves.Add(new Animator.Move(entry.Hwnd, entry.DockRect, false));
            Log.Info("Docking " + prepared.Count + " window(s).");
            _animator.Start(moves, _app.Config.AnimationDurationMs, _app.Config.AnimationFps, generation);
        }

        public void RequestRestore()
        {
            if (_animating) { _pending = Pending.Restore; return; }
            if (_session == null) return;

            var moves = new List<Animator.Move>();
            foreach (var entry in _session.Entries)
            {
                if (!entry.IsAlive) continue;
                if (Native.IsIconic(entry.Hwnd)) continue;
                moves.Add(new Animator.Move(entry.Hwnd, entry.OriginalRect, NeedsResize(entry.Hwnd, entry.OriginalRect)));
            }
            if (moves.Count == 0)
            {
                ClearSession();
                return;
            }
            _animating = true;
            _animationWasRestore = true;
            _pending = Pending.None;
            _session.Docked = false;
            RaiseStateChanged();
            int generation = ++_generation;
            Log.Info("Restoring " + moves.Count + " window(s).");
            _animator.Start(moves, _app.Config.AnimationDurationMs, _app.Config.AnimationFps, generation);
        }

        public void Toggle()
        {
            if (_session != null) RequestRestore();
            else RequestDock();
        }

        public void OnAnimationComplete(int generation)
        {
            if (generation != _generation) return;
            _animating = false;
            if (_animationWasRestore)
            {
                var session = _session;
                _session = null;
                if (session != null) ApplyRestorePlacements(session);
                Log.Info("Restore complete.");
            }
            else
            {
                if (_session != null) _session.Docked = true;
                Log.Info("Dock complete.");
            }

            Pending pending = _pending;
            _pending = Pending.None;
            if (pending == Pending.Dock) RequestDock();
            else if (pending == Pending.Restore) RequestRestore();
            RaiseStateChanged();
        }

        static bool NeedsResize(IntPtr hwnd, RECT target)
        {
            RECT current;
            if (!Native.GetWindowRect(hwnd, out current)) return true;
            return current.Width != target.Width || current.Height != target.Height;
        }

        void ApplyRestorePlacements(DockSession session)
        {
            foreach (var entry in session.Entries)
            {
                if (!entry.IsAlive) continue;
                if (!entry.WasMaximized) continue;
                if (Native.IsIconic(entry.Hwnd)) continue;
                if (!entry.HasPlacement || entry.OriginalPlacement.showCmd == 0) continue;
                MaximizeWithRestorePlacement(entry);
            }
        }

        /// <summary>
        /// Maximizes the window first and only then writes the real restore rectangle.
        /// Calling SetWindowPlacement on a normal window makes Windows restore it to the
        /// old rectangle and maximize again, which is the visible flash; maximizing the
        /// window that already sits on the maximized frame is a no-op geometry-wise.
        /// </summary>
        void MaximizeWithRestorePlacement(DockEntry entry)
        {
            if (Native.IsIconic(entry.Hwnd)) return;
            if (!Native.IsZoomed(entry.Hwnd))
                Native.ShowWindow(entry.Hwnd, Native.SW_SHOWMAXIMIZED);
            var placement = entry.OriginalPlacement;
            placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            if (!Native.SetWindowPlacement(entry.Hwnd, ref placement))
                Log.Warn("Could not restore maximized state for 0x" + entry.Hwnd.ToInt64().ToString("x"));
        }

        /// <summary>Screen rectangle of a window's client area.</summary>
        internal static RECT ClientScreenRect(IntPtr hwnd)
        {
            var result = new RECT();
            RECT client;
            if (!Native.GetClientRect(hwnd, out client)) return result;
            var origin = new Native.POINT();
            origin.X = client.Left;
            origin.Y = client.Top;
            if (!Native.ClientToScreen(hwnd, ref origin)) return result;
            result.Left = origin.X;
            result.Top = origin.Y;
            result.Right = origin.X + client.Width;
            result.Bottom = origin.Y + client.Height;
            return result;
        }

        /// <summary>
        /// Window rectangle that keeps the current client area when the window is turned
        /// into a normal (restored) window. A maximized window has a thinner non-client
        /// area, so re-maximizing a same-frame-size normal window resized the client and
        /// made the window visibly flash; keeping the client identical removes that resize.
        /// </summary>
        internal static bool TryGetClientPreservingRect(IntPtr hwnd, int dpi, out RECT windowRect)
        {
            windowRect = new RECT();
            RECT client = ClientScreenRect(hwnd);
            if (client.Width <= 0 || client.Height <= 0) return false;

            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            uint plainStyle = (uint)style & ~(Native.WS_MAXIMIZE | Native.WS_MINIMIZE);
            bool hasMenu = Native.GetMenu(hwnd) != IntPtr.Zero;
            if (dpi <= 0) dpi = Native.GetDpi(hwnd);

            RECT rect = client;
            bool adjusted;
            try
            {
                adjusted = Native.AdjustWindowRectExForDpi(ref rect, plainStyle, hasMenu, (uint)exStyle, (uint)dpi);
            }
            catch (EntryPointNotFoundException)
            {
                adjusted = false;
            }
            catch (DllNotFoundException)
            {
                adjusted = false;
            }
            if (!adjusted)
                adjusted = Native.AdjustWindowRectEx(ref rect, plainStyle, hasMenu, (uint)exStyle);
            if (!adjusted) return false;
            windowRect = rect;
            return true;
        }

        bool PrepareMaximized(DockEntry entry)
        {
            if (!entry.HasPlacement || entry.OriginalPlacement.showCmd == 0) return false;
            var placement = entry.OriginalPlacement;
            placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            placement.showCmd = Native.SW_SHOWNORMAL;
            placement.flags = 0;

            RECT target;
            if (!TryGetClientPreservingRect(entry.Hwnd, entry.Dpi, out target))
                target = entry.OriginalRect;
            placement.rcNormalPosition = target;

            if (!Native.SetWindowPlacement(entry.Hwnd, ref placement)) return false;
            bool moved = Native.SetWindowPos(entry.Hwnd, IntPtr.Zero, target.Left, target.Top,
                target.Width, target.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            if (!moved)
            {
                // Put it back exactly as it was before giving up on this window.
                var original = entry.OriginalPlacement;
                original.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                Native.SetWindowPlacement(entry.Hwnd, ref original);
                return false;
            }
            entry.OriginalRect = target;
            Log.Debug("Converted maximized window 0x" + entry.Hwnd.ToInt64().ToString("x") +
                " to a normal window that keeps its client rect.");
            return true;
        }

        bool SessionOnCurrentDesktop()
        {
            if (_session == null) return false;
            bool available = _app.VirtualDesktop.Available;
            foreach (var entry in _session.Entries)
            {
                if (!entry.IsAlive) continue;
                if (!available) return true;
                if (_app.VirtualDesktop.IsOnCurrentDesktop(entry.Hwnd)) return true;
            }
            return false;
        }

        public void FinalizeSessionInstant()
        {
            var session = _session;
            _session = null;
            _pending = Pending.None;
            _animating = false;
            _generation++;
            _animator.Cancel();
            if (session == null)
            {
                RaiseStateChanged();
                return;
            }
            foreach (var entry in session.Entries)
            {
                if (!entry.IsAlive) continue;
                if (entry.WasMaximized && entry.HasPlacement && entry.OriginalPlacement.showCmd != 0)
                {
                    MaximizeWithRestorePlacement(entry);
                }
                else
                {
                    Native.SetWindowPos(entry.Hwnd, IntPtr.Zero, entry.OriginalRect.Left, entry.OriginalRect.Top,
                        entry.OriginalRect.Width, entry.OriginalRect.Height,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                }
            }
            RaiseStateChanged();
        }

        void ClearSession()
        {
            _session = null;
            _pending = Pending.None;
            _animating = false;
            RaiseStateChanged();
        }

        public void Shutdown()
        {
            _animator.Cancel();
            if (_session != null) FinalizeSessionInstant();
            _animator.Dispose();
        }
    }

    internal static class DockLayout
    {
        public static void Assign(List<DockEntry> entries, Config config)
        {
            var counts = new Dictionary<string, int[]>();
            foreach (var entry in entries)
            {
                string key = entry.WorkArea.Left + "," + entry.WorkArea.Top + "," + entry.WorkArea.Right + "," + entry.WorkArea.Bottom;
                int[] edgeCounts;
                if (!counts.TryGetValue(key, out edgeCounts))
                {
                    edgeCounts = new int[4];
                    counts[key] = edgeCounts;
                }

                int cx = entry.OriginalRect.Left + entry.OriginalRect.Width / 2;
                int cy = entry.OriginalRect.Top + entry.OriginalRect.Height / 2;
                int blocked = int.MaxValue / 4;
                int[] distances =
                {
                    entry.TaskbarEdge == DockEdge.Left ? blocked : cx - entry.WorkArea.Left,
                    entry.TaskbarEdge == DockEdge.Right ? blocked : entry.WorkArea.Right - cx,
                    entry.TaskbarEdge == DockEdge.Top ? blocked : cy - entry.WorkArea.Top,
                    entry.TaskbarEdge == DockEdge.Bottom ? blocked : entry.WorkArea.Bottom - cy
                };

                // Prefer the nearest available edge, but push additional windows
                // toward other edges so their exposed strips do not pile up.
                int penalty = Math.Max(160, Math.Max(entry.WorkArea.Width, entry.WorkArea.Height) / 8);
                int best = -1;
                int bestScore = int.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    if ((int)entry.TaskbarEdge == i) continue;
                    int score = distances[i] + edgeCounts[i] * penalty;
                    if (best < 0 || score < bestScore)
                    {
                        best = i;
                        bestScore = score;
                    }
                }
                if (best < 0) best = 0;
                entry.Edge = (DockEdge)best;
                edgeCounts[best]++;
            }

            var groups = new Dictionary<string, List<DockEntry>>();
            foreach (var entry in entries)
            {
                string key = entry.WorkArea.Left + "," + entry.WorkArea.Top + "," + entry.WorkArea.Right + "," + entry.WorkArea.Bottom + "," + (int)entry.Edge;
                List<DockEntry> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<DockEntry>();
                    groups[key] = list;
                }
                list.Add(entry);
            }

            foreach (var group in groups.Values)
            {
                group.Sort(delegate(DockEntry a, DockEntry b) { return a.ZOrder.CompareTo(b.ZOrder); });
                LayoutGroup(group, config);
            }
        }

        static void LayoutGroup(List<DockEntry> group, Config config)
        {
            int n = group.Count;
            DockEdge edge = group[0].Edge;
            bool vertical = edge == DockEdge.Left || edge == DockEdge.Right;
            double scale = group[0].Dpi / 96.0;
            int expose = Math.Max(4, (int)Math.Round(config.ExposePx * scale));
            int maxExpose = Math.Max(expose, (int)Math.Round(config.MaxExposePx * scale));
            if (expose > maxExpose) expose = maxExpose;
            int gap = Math.Max(0, (int)Math.Round(config.StackStepPx * scale));
            int minVisible = Math.Max(16, (int)Math.Round(48 * scale));
            RECT work = group[0].WorkArea;
            int start = vertical ? work.Top : work.Left;
            int end = vertical ? work.Bottom : work.Right;
            int available = Math.Max(1, end - start);

            int total = 0;
            foreach (var entry in group) total += Along(entry, vertical);

            if (n == 1)
            {
                int pos = vertical ? group[0].OriginalRect.Top : group[0].OriginalRect.Left;
                SetDockRect(group[0], expose, minVisible, pos);
                return;
            }

            if (total + gap * (n - 1) <= available)
            {
                int pos = start;
                foreach (var entry in group)
                {
                    SetDockRect(entry, expose, minVisible, pos);
                    pos += Along(entry, vertical) + gap;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    var entry = group[i];
                    int along = Along(entry, vertical);
                    int maxStart = end - along;
                    if (maxStart < start) maxStart = start;
                    int pos = start + (maxStart - start) * i / (n - 1);
                    SetDockRect(entry, expose, minVisible, pos);
                }
            }
        }

        static int Along(DockEntry entry, bool vertical)
        {
            return vertical ? entry.OriginalRect.Height : entry.OriginalRect.Width;
        }

        static void SetDockRect(DockEntry entry, int depth, int minVisible, int alongPosition)
        {
            entry.DockRect = ComputeRect(entry.OriginalRect, entry.WorkArea, entry.Edge, depth, minVisible, alongPosition);
        }

        public static RECT ComputeRect(RECT window, RECT work, DockEdge edge, int depth, int minVisible)
        {
            return ComputeRect(window, work, edge, depth, minVisible, int.MinValue);
        }

        public static RECT ComputeRect(RECT window, RECT work, DockEdge edge, int depth, int minVisible, int alongPosition)
        {
            if (edge == DockEdge.None) return window;
            int w = window.Width;
            int h = window.Height;
            int left = window.Left;
            int top = window.Top;
            int alongLimit = Math.Max(8, minVisible);
            if (w < alongLimit * 2) alongLimit = Math.Max(4, w / 2);
            if (h < alongLimit * 2) alongLimit = Math.Max(4, h / 2);

            if (edge == DockEdge.Left || edge == DockEdge.Right)
            {
                int maxDepth = Math.Max(4, w - 6);
                depth = Math.Min(depth, maxDepth);
                if (edge == DockEdge.Left) left = work.Left - w + depth;
                else left = work.Right - depth;
                if (alongPosition != int.MinValue) top = alongPosition;
                int minTop = work.Top - h + alongLimit;
                int maxTop = work.Bottom - alongLimit;
                if (minTop > maxTop) top = work.Top + (work.Height - h) / 2;
                else top = Native.Clamp(top, minTop, maxTop);
            }
            else
            {
                int maxDepth = Math.Max(4, h - 6);
                depth = Math.Min(depth, maxDepth);
                if (edge == DockEdge.Top) top = work.Top - h + depth;
                else top = work.Bottom - depth;
                if (alongPosition != int.MinValue) left = alongPosition;
                int minLeft = work.Left - w + alongLimit;
                int maxLeft = work.Right - alongLimit;
                if (minLeft > maxLeft) left = work.Left + (work.Width - w) / 2;
                else left = Native.Clamp(left, minLeft, maxLeft);
            }
            RECT result;
            result.Left = left;
            result.Top = top;
            result.Right = left + w;
            result.Bottom = top + h;
            return result;
        }
    }

    internal sealed class Animator : IDisposable
    {
        internal sealed class Move
        {
            public IntPtr Hwnd;
            public RECT To;
            public bool ResizeAtEnd;
            public Move(IntPtr hwnd, RECT to, bool resizeAtEnd)
            {
                Hwnd = hwnd;
                To = to;
                ResizeAtEnd = resizeAtEnd;
            }
        }

        sealed class Job
        {
            public List<Move> Moves;
            public int DurationMs;
            public int Fps;
            public int Generation;
        }

        readonly App _app;
        readonly object _gate = new object();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        Thread _thread;
        Job _job;
        volatile bool _stop;
        volatile bool _cancel;
        volatile bool _busy;

        public Animator(App app)
        {
            _app = app;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "GestoshAnimator";
            _thread.Start();
        }

        public void Start(List<Move> moves, int durationMs, int fps, int generation)
        {
            var job = new Job();
            job.Moves = moves;
            job.DurationMs = Math.Max(0, durationMs);
            job.Fps = Math.Max(20, Math.Min(144, fps));
            job.Generation = generation;
            lock (_gate)
            {
                _job = job;
                _cancel = false;
            }
            _wake.Set();
        }

        public void Cancel()
        {
            _cancel = true;
            lock (_gate) { _job = null; }
            _wake.Set();
            for (int i = 0; i < 100 && _busy; i++) Thread.Sleep(5);
        }

        void Run()
        {
            uint timer = Native.timeBeginPeriod(1);
            try
            {
                while (!_stop)
                {
                    _wake.WaitOne(200);
                    if (_stop) break;
                    Job job;
                    lock (_gate)
                    {
                        job = _job;
                        _job = null;
                    }
                    if (job == null) continue;
                    try { Animate(job); }
                    catch (Exception ex)
                    {
                        Log.Error("Animation failed.", ex);
                        PostDone(job);
                    }
                }
            }
            finally
            {
                Native.timeEndPeriod(timer);
            }
        }

        void Animate(Job job)
        {
            _busy = true;
            try
            {
            var moves = new List<Move>();
            var from = new List<RECT>();
            foreach (var move in job.Moves)
            {
                if (!Native.IsWindow(move.Hwnd)) continue;
                RECT rect;
                if (!Native.GetWindowRect(move.Hwnd, out rect)) continue;
                moves.Add(move);
                from.Add(rect);
            }
            if (moves.Count == 0)
            {
                PostDone(job);
                return;
            }

            var watch = Stopwatch.StartNew();
            double frameMs = 1000.0 / job.Fps;
            double nextFrame = 0;
            double duration = job.DurationMs;

            while (!_stop && !_cancel)
            {
                double elapsedMs = watch.Elapsed.TotalMilliseconds;
                double t = duration <= 0 ? 1.0 : Math.Min(1.0, elapsedMs / duration);
                double eased = EaseOutCubic(t);
                bool final = t >= 1.0;
                for (int i = 0; i < moves.Count; i++)
                {
                    var move = moves[i];
                    if (!Native.IsWindow(move.Hwnd)) continue;
                    var start = from[i];
                    var end = move.To;
                    int x = (int)Math.Round(start.Left + (end.Left - start.Left) * eased);
                    int y = (int)Math.Round(start.Top + (end.Top - start.Top) * eased);
                    uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
                    if (!final) flags |= Native.SWP_ASYNCWINDOWPOS;
                    if (final && move.ResizeAtEnd) Native.SetWindowPos(move.Hwnd, IntPtr.Zero, x, y, end.Width, end.Height, flags);
                    else Native.SetWindowPos(move.Hwnd, IntPtr.Zero, x, y, 0, 0, flags | Native.SWP_NOSIZE);
                }
                if (final) break;
                nextFrame += frameMs;
                double wait = nextFrame - watch.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
                else if (wait > 0) Thread.Sleep(1);
            }

            if (!_cancel && !_stop) PostDone(job);
            }
            finally { _busy = false; }
        }

        void PostDone(Job job)
        {
            if (_app.Hwnd == IntPtr.Zero) return;
            Native.PostMessageW(_app.Hwnd, DockManager.WM_ANIM_DONE, new IntPtr(job.Generation), IntPtr.Zero);
        }

        static double EaseOutCubic(double t)
        {
            double u = 1.0 - t;
            return 1.0 - u * u * u;
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            try { if (_thread != null && _thread.IsAlive) _thread.Join(500); } catch { }
        }
    }

    internal static class WindowScanner
    {
        static readonly HashSet<string> ShellClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame", "TaskListThumbnailWnd",
            "ForegroundStaging", "XamlExplorerHostIslandWindow", "ApplicationManager_DesktopShellWindow",
            "Windows.UI.Composition.DesktopWindowContentBridge", "EdgeUiInputTopWndClass"
        };

        static readonly HashSet<string> ExposeExcludedClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame", "TaskListThumbnailWnd",
            "ForegroundStaging", "XamlExplorerHostIslandWindow", "ApplicationManager_DesktopShellWindow",
            "Windows.UI.Composition.DesktopWindowContentBridge", "EdgeUiInputTopWndClass",
            "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "Shell_InputSwitchTopLevelWindow", "DV2ControlHost",
            "#32768", "#32769", "tooltips_class32", "SysShadow", "DropShadow", "ComboLBox",
            "CEF-OSC-WIDGET"
        };

        public static List<WindowInfo> ScanExpose(Config config, VirtualDesktop virtualDesktop, IntPtr ownWindow)
        {
            var result = new List<WindowInfo>();
            uint ownPid = (uint)Process.GetCurrentProcess().Id;
            bool vdm = virtualDesktop != null && virtualDesktop.Available;
            int z = 0;

            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (hwnd == ownWindow) return true;
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (Native.IsCloaked(hwnd)) return true;
                if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return true;

                string className = Native.GetWindowClass(hwnd);
                if (ExposeExcludedClasses.Contains(className)) return true;

                long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if ((exStyle & Native.WS_EX_TRANSPARENT) != 0) return true;

                string title = Native.GetWindowTitle(hwnd);
                if (string.IsNullOrEmpty(title) || title.Trim().Length == 0) return true;
                if (IsExposePanelTitle(title)) return true;

                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == ownPid) return true;

                RECT rect;
                if (!Native.GetWindowRect(hwnd, out rect)) return true;
                if (rect.Width < 40 || rect.Height < 30) return true;

                if (vdm && !virtualDesktop.IsOnCurrentDesktop(hwnd)) return true;

                var monitor = Native.GetMonitorWork(hwnd);
                var info = new WindowInfo();
                info.Hwnd = hwnd;
                info.Rect = rect;
                info.HasPlacement = false;
                info.Maximized = Native.IsZoomed(hwnd);
                info.ZOrder = z++;
                info.WorkArea = monitor.rcWork;
                info.Dpi = Native.GetDpi(hwnd);
                info.ClassName = className;
                info.Title = title;
                result.Add(info);
                return true;
            }, IntPtr.Zero);

            Log.Debug("Expose scan: " + result.Count + " window(s).");
            return result;
        }

        static bool IsExposePanelTitle(string title)
        {
            string t = title.Trim().ToLowerInvariant();
            return t == "program manager" ||
                t == "desktop" ||
                t == "taskbar" ||
                t == "widgets" ||
                t == "notification center" ||
                t == "action center" ||
                t == "windows input experience" ||
                t == "microsoft text input application" ||
                t == "nvidia geforce overlay" ||
                t == "nvidia overlay" ||
                t == "xbox game bar";
        }

        public static List<WindowInfo> Scan(Config config, VirtualDesktop virtualDesktop, IntPtr ownWindow)
        {
            var result = new List<WindowInfo>();
            var processNames = new Dictionary<uint, string>();
            uint ownPid = (uint)Process.GetCurrentProcess().Id;
            int z = 0;
            bool onlyFilter = config.OnlyProcesses.Count > 0;
            bool excludeFilter = config.ExcludeProcesses.Count > 0;
            bool vdm = virtualDesktop != null && virtualDesktop.Available;

            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (hwnd == ownWindow) return true;
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (Native.IsIconic(hwnd) && !config.IncludeMinimized) return true;
                if (Native.IsCloaked(hwnd)) return true;
                if (Native.GetAncestor(hwnd, Native.GA_ROOT) != hwnd) return true;

                string className = Native.GetWindowClass(hwnd);
                if (ShellClasses.Contains(className)) return true;

                long exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && !config.IncludeToolWindows) return true;

                string title = Native.GetWindowTitle(hwnd);
                if (string.IsNullOrEmpty(title) || title.Trim().Length == 0) return true;
                if (title == "Gestosh.Hidden.Window.v1") return true;

                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == ownPid) return true;

                string processName = "";
                if (onlyFilter || excludeFilter)
                {
                    if (!processNames.TryGetValue(pid, out processName))
                    {
                        try { processName = Process.GetProcessById((int)pid).ProcessName; }
                        catch { processName = ""; }
                        processNames[pid] = processName;
                    }
                }
                if (onlyFilter && !Matches(processName, config.OnlyProcesses)) return true;
                if (excludeFilter && Matches(processName, config.ExcludeProcesses)) return true;

                RECT rect;
                if (!Native.GetWindowRect(hwnd, out rect)) return true;
                if (rect.Width < 24 || rect.Height < 24) return true;

                long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
                var monitor = Native.GetMonitorWork(hwnd);
                bool fullscreen = rect.Left <= monitor.rcMonitor.Left && rect.Top <= monitor.rcMonitor.Top
                    && rect.Right >= monitor.rcMonitor.Right && rect.Bottom >= monitor.rcMonitor.Bottom
                    && (style & Native.WS_CAPTION) == 0 && (style & Native.WS_THICKFRAME) == 0;
                if (fullscreen) return true;

                if (vdm && !virtualDesktop.IsOnCurrentDesktop(hwnd)) return true;

                var placement = new WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                bool hasPlacement = Native.GetWindowPlacement(hwnd, ref placement);
                bool maximized = Native.IsZoomed(hwnd) ||
                    (hasPlacement && placement.showCmd == Native.SW_SHOWMAXIMIZED);

                var info = new WindowInfo();
                info.Hwnd = hwnd;
                info.Rect = rect;
                info.Placement = hasPlacement ? placement : new WINDOWPLACEMENT();
                info.HasPlacement = hasPlacement;
                info.Maximized = maximized;
                info.ZOrder = z++;
                info.WorkArea = monitor.rcWork;
                info.Dpi = Native.GetDpi(hwnd);
                info.TaskbarEdge = DetectTaskbarEdge(monitor);
                info.ProcessName = processName;
                info.ClassName = className;
                info.Title = title;
                result.Add(info);
                return true;
            }, IntPtr.Zero);
            Log.Debug("Window scan: " + result.Count + " eligible.");
            return result;
        }

        static bool Matches(string processName, List<string> names)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            foreach (string name in names)
                if (string.Equals(processName, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static DockEdge DetectTaskbarEdge(Native.MONITORINFO monitor)
        {
            int left = monitor.rcWork.Left - monitor.rcMonitor.Left;
            int top = monitor.rcWork.Top - monitor.rcMonitor.Top;
            int right = monitor.rcMonitor.Right - monitor.rcWork.Right;
            int bottom = monitor.rcMonitor.Bottom - monitor.rcWork.Bottom;
            int max = Math.Max(Math.Max(left, top), Math.Max(right, bottom));
            if (max > 0)
            {
                if (max == left) return DockEdge.Left;
                if (max == top) return DockEdge.Top;
                if (max == right) return DockEdge.Right;
                return DockEdge.Bottom;
            }

            // With auto-hide the work area often equals the monitor.  The
            // primary taskbar still reports its edge through the appbar API.
            if ((monitor.dwFlags & 1) != 0)
            {
                var data = new Native.APPBARDATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
                if (Native.SHAppBarMessage(5, ref data) != IntPtr.Zero)
                {
                    if (data.uEdge == 0) return DockEdge.Left;
                    if (data.uEdge == 1) return DockEdge.Top;
                    if (data.uEdge == 2) return DockEdge.Right;
                    if (data.uEdge == 3) return DockEdge.Bottom;
                }
            }
            return DockEdge.None;
        }
    }

    internal sealed class VirtualDesktop : IDisposable
    {
        [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopManager
        {
            [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
            [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
            [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }

        [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
        class VirtualDesktopManagerCoClass { }

        readonly IVirtualDesktopManager _manager;
        public bool Available { get; private set; }

        public VirtualDesktop()
        {
            try
            {
                _manager = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass();
                Available = _manager != null;
                Log.Info("VirtualDesktopManager available.");
            }
            catch (Exception ex)
            {
                Available = false;
                Log.Warn("VirtualDesktopManager unavailable: " + ex.Message);
            }
        }

        public bool TryIsOnCurrentDesktop(IntPtr hwnd, out bool onCurrentDesktop)
        {
            onCurrentDesktop = true;
            if (!Available) return false;
            try
            {
                int onCurrent;
                int hr = _manager.IsWindowOnCurrentVirtualDesktop(hwnd, out onCurrent);
                if (hr != 0) return false;
                onCurrentDesktop = onCurrent != 0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool IsOnCurrentDesktop(IntPtr hwnd)
        {
            bool onCurrent;
            if (!TryIsOnCurrentDesktop(hwnd, out onCurrent)) return true;
            return onCurrent;
        }

        public void Dispose()
        {
            if (_manager != null && Marshal.IsComObject(_manager))
            {
                try { Marshal.ReleaseComObject(_manager); } catch { }
            }
        }
    }
}
