using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Gestosh
{
    internal interface IGestureSink
    {
        void BeginDrag();
        void DragBy(double dxNormalized, double dyNormalized);
        void EndDrag();
        void ShowTaskView();
        void ShowActionCenter();
        bool DesktopSwipeBegin(int direction);
        void DesktopSwipeUpdate(double dxNormalized);
        void DesktopSwipeEnd(double dxNormalized, double elapsedMs);
        void RequestDock();
        void RequestRestore();
        void ExposeBegin();
        void ExposeProgress(double progress);
        void ExposeEnd(bool commit);
        bool IsExposeOpen();
        void ExposeCloseProgress(double progress);
        void ExposeCloseEnd(bool close);
    }

    /// <summary>
    /// Recognizes the two gestures Gestosh owns:
    ///   * exactly three contacts moving together  -> left-button mouse drag
    ///   * five contacts spreading / pinching       -> dock / undock
    /// Contacts are normalized to the touchpad surface (0..1 on each axis).
    /// </summary>
    internal sealed class GestureEngine
    {
        readonly Config _config;
        readonly IGestureSink _sink;
        public bool Enabled = true;

        bool _dragSession;
        bool _dragStarted;
        bool _dragCancelled;
        double _dragStartTime;
        double _dragStartX;
        double _dragStartY;
        double _dragLastX;
        double _dragLastY;
        double _dragStartSpread;

        bool _pinchSession;
        bool _pinchTriggered;
        double _pinchStartTime;
        double _pinchBaseSpread;
        double _pinchLastSpread;
        double _lastTriggerTime = -100000;

        bool _exposeSession;
        bool _exposeActive;
        bool _exposeOpenAtStart;
        bool _exposeClosing;
        int _exposeDirection = 1;
        bool _taskViewTriggered;
        double _lastTaskViewTime = -100000;
        double _lastExposeTime = -100000;
        double _exposeProgress;
        double _exposeCloseProgress;
        double _exposeStartTime;
        double _exposeStartX;
        double _exposeStartY;

        bool _edgeSwipeSession;
        bool _edgeSwipeEligible;
        bool _edgeSwipeTriggered;
        double _edgeSwipeStartTime;
        double _edgeSwipeStartX;
        double _edgeSwipeStartY;
        double _lastEdgeSwipeTime = -100000;

        bool _desktopSwipeSession;
        bool _desktopSwipeRejected;
        int _desktopSwipeDirection;
        double _desktopSwipeStartTime;
        double _desktopSwipeStartX;
        double _desktopSwipeLastDx;
        double _lastDesktopSwipeTime = -100000;
        double _fourFingerMaxDx;
        double _fourFingerMaxDy;

        public GestureEngine(Config config, IGestureSink sink)
        {
            _config = config;
            _sink = sink;
        }

        public void Reset()
        {
            EndDesktopSwipe(-100000);
            EndEdgeSwipe();
            EndExposeGesture(-100000, false);
            EndExpose();
            EndDrag();
            EndPinch();
        }

        public void Feed(IList<Contact> contacts, double nowMs)
        {
            if (!Enabled)
            {
                Reset();
                return;
            }

            int count = contacts == null ? 0 : contacts.Count;
            double cx, cy, spread;
            Compute(contacts, out cx, out cy, out spread);
            if (count != 2) EndEdgeSwipe();

            if (count == 3)
            {
                EndPinch();
                EndDesktopSwipe(nowMs);
                if (_exposeActive || _exposeClosing) EndExposeGesture(nowMs, true);
                EndExpose();
                if (_config.ThreeFingerAction == "drag")
                {
                    if (!_dragSession)
                    {
                        _dragSession = true;
                        _dragStarted = false;
                        _dragCancelled = false;
                        _dragStartTime = nowMs;
                        _dragStartX = cx;
                        _dragStartY = cy;
                        _dragLastX = cx;
                        _dragLastY = cy;
                        _dragStartSpread = spread;
                    }
                    else
                    {
                        UpdateDrag(cx, cy, spread, nowMs);
                    }
                }
            }
            else if (count == 4)
            {
                EndPinch();
                EndDrag();
                if (!_exposeSession)
                {
                    _exposeSession = true;
                    _exposeActive = false;
                    _exposeClosing = false;
                    _exposeOpenAtStart = _sink.IsExposeOpen();
                    _taskViewTriggered = false;
                    _desktopSwipeRejected = false;
                    _fourFingerMaxDx = 0;
                    _fourFingerMaxDy = 0;
                    _exposeStartTime = nowMs;
                    _exposeStartX = cx;
                    _exposeStartY = cy;
                    _exposeCloseProgress = 0;
                }

                if (_exposeOpenAtStart)
                {
                    UpdateExposeCloseGesture(cx, cy, nowMs);
                }
                else if (_desktopSwipeSession)
                {
                    _desktopSwipeLastDx = cx - _desktopSwipeStartX;
                    _sink.DesktopSwipeUpdate(_desktopSwipeLastDx);
                }
                else if (!TryStartDesktopSwipe(cx, cy, nowMs))
                {
                    _fourFingerMaxDx = Math.Max(_fourFingerMaxDx, Math.Abs(cx - _exposeStartX));
                    _fourFingerMaxDy = Math.Max(_fourFingerMaxDy, Math.Abs(cy - _exposeStartY));
                    UpdateExposeGesture(cx, cy, nowMs);
                }
            }
            else
            {
                EndDesktopSwipe(nowMs);
                EndExposeGesture(nowMs, true);
                EndExpose();
                EndDrag();
                if (count == 2)
                {
                    EndPinch();
                    UpdateEdgeSwipe(contacts, nowMs);
                }
                else if (count == _config.PinchFingers)
                {
                    if (!_pinchSession)
                    {
                        _pinchSession = true;
                        _pinchTriggered = false;
                        _pinchStartTime = nowMs;
                        _pinchBaseSpread = spread;
                        _pinchLastSpread = spread;
                    }
                    else
                    {
                        UpdatePinch(spread, nowMs);
                    }
                }
                else
                {
                    EndPinch();
                }
            }
        }

        void EndEdgeSwipe()
        {
            _edgeSwipeSession = false;
            _edgeSwipeEligible = false;
            _edgeSwipeTriggered = false;
        }

        /// <summary>
        /// Four fingers moving sideways slide the neighbouring virtual desktop in.
        /// The gesture only starts once the movement is clearly horizontal, so the
        /// vertical App Expose / Task View gestures keep working unchanged.
        /// </summary>
        bool TryStartDesktopSwipe(double cx, double cy, double nowMs)
        {
            if (!_config.DesktopSwipe || _config.FourFingerHorizontalAction != "desktop" || _desktopSwipeRejected) return false;
            if (_exposeActive || _exposeClosing) return false;

            double dx = cx - _exposeStartX;
            double dy = cy - _exposeStartY;
            if (Math.Abs(dx) < _config.DesktopSwipeStartDistance) return false;
            if (Math.Abs(dx) < Math.Abs(dy) * _config.ExposeSwipeVerticalRatio) return false;
            if (nowMs - _lastDesktopSwipeTime < _config.GestureCooldownMs) return false;

            // Fingers moving left bring the next desktop in from the right.
            int direction = dx < 0 ? 1 : -1;
            if (!_sink.DesktopSwipeBegin(direction))
            {
                _desktopSwipeRejected = true;
                Log.Debug("4-finger horizontal swipe ignored: desktop slide unavailable.");
                return false;
            }

            _desktopSwipeSession = true;
            _desktopSwipeDirection = direction;
            _desktopSwipeStartTime = nowMs;
            _desktopSwipeStartX = _exposeStartX;
            _desktopSwipeLastDx = dx;
            Log.Info(string.Format("4-finger swipe {0}: dx={1:0.000} dy={2:0.000}; sliding to the {3} desktop.",
                direction > 0 ? "left" : "right", dx, dy, direction > 0 ? "next" : "previous"));
            _sink.DesktopSwipeUpdate(dx);
            return true;
        }

        void EndDesktopSwipe(double nowMs)
        {
            if (!_desktopSwipeSession)
            {
                _desktopSwipeRejected = false;
                return;
            }
            _desktopSwipeSession = false;
            _desktopSwipeRejected = false;
            _lastDesktopSwipeTime = nowMs;
            _sink.DesktopSwipeEnd(_desktopSwipeLastDx, nowMs - _desktopSwipeStartTime);
            Log.Debug("Desktop slide ended at dx=" + _desktopSwipeLastDx.ToString("0.000"));
        }

        void UpdateEdgeSwipe(IList<Contact> contacts, double nowMs)
        {
            if (contacts == null || contacts.Count != 2) return;
            double x1 = contacts[0].X;
            double y1 = contacts[0].Y;
            double x2 = contacts[1].X;
            double y2 = contacts[1].Y;
            double cx = (x1 + x2) * 0.5;
            double cy = (y1 + y2) * 0.5;

            if (!_edgeSwipeSession)
            {
                _edgeSwipeSession = true;
                _edgeSwipeTriggered = false;
                _edgeSwipeEligible = Math.Min(x1, x2) >= 0.82;
                _edgeSwipeStartTime = nowMs;
                _edgeSwipeStartX = cx;
                _edgeSwipeStartY = cy;
                return;
            }
            if (!_edgeSwipeEligible || _edgeSwipeTriggered) return;
            if (nowMs - _edgeSwipeStartTime > 700) return;

            double dx = cx - _edgeSwipeStartX;
            double dy = cy - _edgeSwipeStartY;
            if (dx > -0.045) return;
            if (-dx < Math.Abs(dy) * 1.5) return;
            if (nowMs - _lastEdgeSwipeTime < _config.GestureCooldownMs) return;
            if (_config.EdgeSwipeAction != "actioncenter") return;

            _edgeSwipeTriggered = true;
            _lastEdgeSwipeTime = nowMs;
            Log.Info(string.Format("2-finger right-edge swipe: dx={0:0.000} dy={1:0.000}; opening Action Center.", dx, dy));
            _sink.ShowActionCenter();
        }

        void UpdateExposeGesture(double cx, double cy, double nowMs)
        {
            if (!_exposeSession) return;
            double dx = cx - _exposeStartX;
            double dy = cy - _exposeStartY;

            if (_exposeActive)
            {
                double travel = _exposeDirection > 0 ? dy : -dy;
                double progress = Math.Max(0, Math.Min(1, travel / _config.ExposeSwipeDistance));
                if (Math.Abs(progress - _exposeProgress) > 0.004)
                {
                    _exposeProgress = progress;
                    _sink.ExposeProgress(progress);
                }
                return;
            }

            if (_taskViewTriggered) return;
            if (nowMs - _exposeStartTime > _config.ExposeSwipeMaxMs) return;

            // Only a clearly vertical movement counts; horizontal swipes belong to the
            // desktop slide, which is evaluated before this method.
            if (Math.Abs(dy) < Math.Abs(dx) * _config.ExposeSwipeVerticalRatio) return;

            if (dy > 0)
            {
                string action = _config.FourFingerDownAction;
                if (action == "none") return;
                if (dy < 0.02) return;
                if (action == "taskview")
                {
                    TriggerTaskView(nowMs, dy, dx, "down");
                    return;
                }
                if (nowMs - _lastExposeTime < _config.GestureCooldownMs) return;
                BeginExposeGesture(1, dy, nowMs, dx, "down");
            }
            else if (dy < 0)
            {
                string action = _config.FourFingerUpAction;
                if (action == "none") return;
                double up = -dy;
                if (action == "taskview")
                {
                    double minUp = Math.Max(0.03, _config.ExposeSwipeDistance * 0.25);
                    if (up < minUp) return;
                    TriggerTaskView(nowMs, up, dx, "up");
                    return;
                }
                if (up < 0.02) return;
                if (nowMs - _lastExposeTime < _config.GestureCooldownMs) return;
                BeginExposeGesture(-1, up, nowMs, dx, "up");
            }
        }

        void TriggerTaskView(double nowMs, double travel, double dx, string direction)
        {
            if (nowMs - _lastTaskViewTime < _config.GestureCooldownMs) return;
            _taskViewTriggered = true;
            _lastTaskViewTime = nowMs;
            Log.Info(string.Format("4-finger swipe {0}: travel={1:0.000} dx={2:0.000}; opening Windows Task View.",
                direction, travel, dx));
            _sink.ShowTaskView();
        }

        void BeginExposeGesture(int direction, double travel, double nowMs, double dx, string label)
        {
            _exposeActive = true;
            _exposeDirection = direction;
            _exposeProgress = Math.Max(0, Math.Min(1, travel / _config.ExposeSwipeDistance));
            _lastExposeTime = nowMs;
            Log.Info(string.Format("4-finger swipe {0}: travel={1:0.000} dx={2:0.000}; opening App Expose.",
                label, travel, dx));
            _sink.ExposeBegin();
            _sink.ExposeProgress(_exposeProgress);
        }

        void UpdateExposeCloseGesture(double cx, double cy, double nowMs)
        {
            double dx = cx - _exposeStartX;
            double dy = cy - _exposeStartY;
            if (dy >= -0.02) return;
            if (-dy < Math.Abs(dx) * _config.ExposeSwipeVerticalRatio) return;

            _exposeClosing = true;
            double progress = Math.Max(0, Math.Min(1, -dy / _config.ExposeSwipeDistance));
            if (Math.Abs(progress - _exposeCloseProgress) > 0.004)
            {
                _exposeCloseProgress = progress;
                _sink.ExposeCloseProgress(progress);
            }
        }

        void EndExposeGesture(double nowMs, bool commitIfOpen)
        {
            if (_exposeClosing)
            {
                bool close = _exposeCloseProgress >= 0.5;
                _sink.ExposeCloseEnd(close);
                _exposeClosing = false;
                _exposeCloseProgress = 0;
                _lastExposeTime = nowMs;
                Log.Debug("App Expose close gesture ended, close=" + close);
                return;
            }
            if (!_exposeActive) return;
            bool commit = commitIfOpen && _exposeProgress >= 0.55;
            _sink.ExposeEnd(commit);
            _exposeActive = false;
            _lastExposeTime = nowMs;
            Log.Debug("App Expose open gesture ended at " + _exposeProgress.ToString("0.00") + ", commit=" + commit);
        }

        void UpdateDrag(double cx, double cy, double spread, double nowMs)
        {
            double dx = cx - _dragLastX;
            double dy = cy - _dragLastY;
            _dragLastX = cx;
            _dragLastY = cy;

            if (_dragStarted)
            {
                // A contact appearing/disappearing can move the centroid abruptly; ignore a huge jump.
                if (Math.Abs(dx) + Math.Abs(dy) < 0.25)
                    _sink.DragBy(dx, dy);
                return;
            }
            if (_dragCancelled) return;

            double moved = Math.Sqrt((cx - _dragStartX) * (cx - _dragStartX) + (cy - _dragStartY) * (cy - _dragStartY));
            double spreadChange = Math.Abs(spread - _dragStartSpread);
            double elapsed = nowMs - _dragStartTime;

            if (elapsed < 30 || moved < _config.DragStartThreshold) return;

            // If the three fingers are mostly spreading or pinching, this is not a pan.
            if (spreadChange > moved * 1.8 + 0.003)
            {
                _dragCancelled = true;
                Log.Debug("3-finger gesture cancelled: spread change " + spreadChange.ToString("0.0000")
                    + " vs movement " + moved.ToString("0.0000"));
                return;
            }

            _dragStarted = true;
            _sink.BeginDrag();
            _sink.DragBy(cx - _dragStartX, cy - _dragStartY);
            Log.Debug("3-finger drag started.");
        }

        void UpdatePinch(double spread, double nowMs)
        {
            _pinchLastSpread = spread;
            if (_pinchTriggered) return;
            if (nowMs - _pinchStartTime < _config.PinchSettleMs) return;
            if (nowMs - _lastTriggerTime < _config.GestureCooldownMs) return;
            if (_pinchBaseSpread <= 0.0001) return;

            double ratio = spread / _pinchBaseSpread;
            double travel = spread - _pinchBaseSpread;

            if (ratio >= _config.PinchOutRatio && travel >= _config.PinchMinTravel)
            {
                _pinchTriggered = true;
                _lastTriggerTime = nowMs;
                Log.Info(string.Format("5-finger pinch-out: ratio={0:0.00} travel={1:0.000}; docking.", ratio, travel));
                _sink.RequestDock();
            }
            else if (ratio <= _config.PinchInRatio && -travel >= _config.PinchMinTravel)
            {
                _pinchTriggered = true;
                _lastTriggerTime = nowMs;
                Log.Info(string.Format("5-finger pinch-in: ratio={0:0.00} travel={1:0.000}; restoring.", ratio, travel));
                _sink.RequestRestore();
            }
        }

        void EndDrag()
        {
            if (_dragStarted) _sink.EndDrag();
            _dragSession = false;
            _dragStarted = false;
            _dragCancelled = false;
        }

        void EndPinch()
        {
            _pinchSession = false;
            _pinchTriggered = false;
        }

        void EndExpose()
        {
            if (_exposeSession && _fourFingerMaxDx + _fourFingerMaxDy > 0.005)
            {
                Log.Debug(string.Format("4-finger gesture ended: max dx={0:0.000} dy={1:0.000}.",
                    _fourFingerMaxDx, _fourFingerMaxDy));
            }
            _fourFingerMaxDx = 0;
            _fourFingerMaxDy = 0;
            _exposeSession = false;
            _exposeActive = false;
            _exposeClosing = false;
            _exposeOpenAtStart = false;
            _exposeProgress = 0;
            _exposeCloseProgress = 0;        }

        static void Compute(IList<Contact> contacts, out double cx, out double cy, out double spread)
        {
            cx = 0; cy = 0; spread = 0;
            if (contacts == null || contacts.Count == 0) return;
            for (int i = 0; i < contacts.Count; i++)
            {
                cx += contacts[i].X;
                cy += contacts[i].Y;
            }
            cx /= contacts.Count;
            cy /= contacts.Count;
            double sum = 0;
            for (int i = 0; i < contacts.Count; i++)
            {
                double dx = contacts[i].X - cx;
                double dy = contacts[i].Y - cy;
                sum += dx * dx + dy * dy;
            }
            spread = Math.Sqrt(sum / contacts.Count);
        }
    }

    /// <summary>Translates normalized finger deltas into an absolute left-button drag.</summary>
    internal sealed class MouseDragger
    {
        readonly Config _config;
        bool _active;
        bool _sendFailed;
        int _targetX;
        int _targetY;
        double _pixelsPerUnit;
        int _virtualLeft, _virtualTop, _virtualWidth, _virtualHeight;

        public MouseDragger(Config config)
        {
            _config = config;
        }

        public bool Active { get { return _active; } }

        public void Begin()
        {
            if (_active) return;
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;

            _virtualLeft = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            _virtualTop = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            _virtualWidth = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            _virtualHeight = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

            _targetX = cursor.X;
            _targetY = cursor.Y;

            IntPtr monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            int width = _virtualWidth;
            if (monitor != IntPtr.Zero && Native.GetMonitorInfoW(monitor, ref info))
                width = Math.Max(320, info.rcWork.Width);
            _pixelsPerUnit = Math.Max(200.0, width * 0.9 * _config.DragSensitivity);

            // Establish the drag at the current cursor position before moving.
            SendAbsolute(_targetX, _targetY);
            if (!SendButton(true))
            {
                Log.Warn("Could not press the left mouse button for a 3-finger drag.");
                return;
            }
            _active = true;
            Log.Debug("Mouse drag begin at " + _targetX + "," + _targetY);
        }

        public void Move(double dxNormalized, double dyNormalized)
        {
            if (!_active) return;
            _targetX += (int)Math.Round(dxNormalized * _pixelsPerUnit);
            _targetY += (int)Math.Round(dyNormalized * _pixelsPerUnit);
            _targetX = Native.Clamp(_targetX, _virtualLeft, _virtualLeft + _virtualWidth - 1);
            _targetY = Native.Clamp(_targetY, _virtualTop, _virtualTop + _virtualHeight - 1);
            SendAbsolute(_targetX, _targetY);
        }

        public void End()
        {
            if (!_active) return;
            _active = false;
            SendButton(false);
            Log.Debug("Mouse drag end.");
        }

        bool SendButton(bool down)
        {
            var input = new Native.INPUT[1];
            input[0].type = 0;
            input[0].u.mi.dx = 0;
            input[0].u.mi.dy = 0;
            input[0].u.mi.mouseData = 0;
            input[0].u.mi.dwFlags = down ? 0x0002u : 0x0004u;
            input[0].u.mi.time = 0;
            input[0].u.mi.dwExtraInfo = IntPtr.Zero;
            uint sent = Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT)));
            if (sent == 0 && !_sendFailed)
            {
                _sendFailed = true;
                Log.Warn("SendInput (left button) failed. Win32=" + Marshal.GetLastWin32Error());
            }
            return sent != 0;
        }

        void SendAbsolute(int x, int y)
        {
            int vx = _virtualLeft;
            int vy = _virtualTop;
            int vw = Math.Max(1, _virtualWidth - 1);
            int vh = Math.Max(1, _virtualHeight - 1);
            long nx = ((long)(x - vx) * 65535) / vw;
            long ny = ((long)(y - vy) * 65535) / vh;
            var input = new Native.INPUT[1];
            input[0].type = 0;
            input[0].u.mi.dx = (int)Math.Max(0, Math.Min(65535, nx));
            input[0].u.mi.dy = (int)Math.Max(0, Math.Min(65535, ny));
            input[0].u.mi.mouseData = 0;
            input[0].u.mi.dwFlags = 0x0001u | 0x8000u | 0x4000u; // MOVE | ABSOLUTE | VIRTUALDESK
            input[0].u.mi.time = 0;
            input[0].u.mi.dwExtraInfo = IntPtr.Zero;
            uint sent = Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT)));
            if (sent == 0 && !_sendFailed)
            {
                _sendFailed = true;
                Log.Warn("SendInput (move) failed. Win32=" + Marshal.GetLastWin32Error());
            }
        }
    }
}
