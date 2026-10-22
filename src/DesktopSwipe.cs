using System;
using System.Runtime.InteropServices;

namespace Gestosh
{
    /// <summary>
    /// One finger-following virtual-desktop slide.
    ///
    /// Windows neither exposes the desktop-switch animation nor lets an application
    /// render another desktop's live content, and a window that belongs to the old
    /// desktop is cloaked the moment the desktop changes.  The switch therefore has
    /// to happen after the animation, so the overlay composites two layers itself:
    ///
    ///   * the real desktop, captured before the gesture starts, moving with the fingers;
    ///   * the incoming side, built from the shared wallpaper and the taskbar, which
    ///     is what a neighbouring desktop actually looks like once its windows are
    ///     not drawn.
    ///
    /// A cancelled swipe slides back and leaves the real desktop completely untouched;
    /// a committed swipe finishes the slide and only then switches the desktop, so the
    /// switch itself is never visible.
    /// </summary>
    internal sealed class DesktopSwipeOverlay : IDisposable
    {
        public const string WindowTitle = "Gestosh.DesktopSwipe.v1";

        const uint WM_PAINT = 0x000F;
        const uint WM_ERASEBKGND = 0x0014;
        const uint WM_MOUSEACTIVATE = 0x0021;
        const uint WM_NCHITTEST = 0x0084;
        const uint WM_TIMER = 0x0113;

        const int TimerSettle = 1;
        const int SettleIntervalMs = 15;

        // The wallpaper is loaded once and shared by every slide.
        static IntPtr _wallpaperImage;
        static uint _wallpaperWidth;
        static uint _wallpaperHeight;
        static bool _wallpaperTried;
        static IntPtr _gdiplusToken;

        readonly Config _config;
        readonly int _direction;
        readonly Action<int> _switchDesktop;
        readonly Action<DesktopSwipeOverlay> _closed;

        Native.WndProc _wndProc;
        IntPtr _hwnd;
        IntPtr _oldProc;
        IntPtr _desktopDc;
        IntPtr _desktopBitmap;
        IntPtr _desktopOld;
        IntPtr _incomingDc;
        IntPtr _incomingBitmap;
        IntPtr _incomingOld;
        // Every frame is composed here and presented with a single BitBlt; painting the
        // wallpaper and then the desktop straight onto the window made the whole screen
        // strobe between the two images.
        IntPtr _frameDc;
        IntPtr _frameBitmap;
        IntPtr _frameOld;
        IntPtr _brush;

        int _originX;
        int _originY;
        int _width;
        int _height;
        int _offset;

        // The taskbar is shared by every virtual desktop, so it must not slide with
        // the captured desktop; it is redrawn at its real position on each frame.
        int _taskbarX;
        int _taskbarY;
        int _taskbarWidth;
        int _taskbarHeight;

        int _settleFrom;
        int _settleTo;
        double _settleStart;
        int _settleDuration;
        bool _commit;
        bool _settling;
        bool _disposed;

        public DesktopSwipeOverlay(Config config, int direction, Action<int> switchDesktop,
            Action<DesktopSwipeOverlay> closed)
        {
            _config = config;
            _direction = direction >= 0 ? 1 : -1;
            _switchDesktop = switchDesktop;
            _closed = closed;
        }

        public IntPtr Hwnd { get { return _hwnd; } }

        public int Offset { get { return _offset; } }

        public int Width { get { return _width; } }

        public bool IsOpen { get { return _hwnd != IntPtr.Zero; } }

        /// <summary>Captures the desktop and shows the overlay. The desktop is not switched yet.</summary>
        public bool Show()
        {
            _originX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            _originY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            _width = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            _height = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

            if (!CaptureDesktop()) return false;
            if (!BuildIncomingLayer()) return false;
            if (!CreateFrameBuffer()) return false;

            // Create the window off-screen, paint the first frame there and only then
            // move it on screen: a visible-but-unpainted window flashes for one frame.
            _hwnd = Native.CreateWindowExW(
                (uint)(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
                "STATIC", WindowTitle, (uint)(Native.WS_POPUP | Native.WS_VISIBLE),
                -30000, -30000, _width, _height,
                IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Warn("Desktop slide: cannot create the overlay window. Win32=" + Marshal.GetLastWin32Error());
                return false;
            }

            _wndProc = WindowProc;
            _oldProc = Native.SetWindowLongPtr(_hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
            if (_oldProc == IntPtr.Zero)
            {
                Log.Warn("Desktop slide: cannot subclass the overlay window.");
                Close();
                return false;
            }

            Native.SetWindowPos(_hwnd, new IntPtr(-1), -30000, -30000, _width, _height,
                Native.SWP_NOACTIVATE);
            RenderFrame();
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            Native.UpdateWindow(_hwnd);
            Native.SetWindowPos(_hwnd, new IntPtr(-1), _originX, _originY, _width, _height,
                Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW);
            Native.DwmFlush();
            Log.Info("Desktop slide opened: direction=" + (_direction > 0 ? "next" : "previous") +
                " size=" + _width + "x" + _height + ".");
            return true;
        }

        /// <summary>Moves the captured desktop with the fingers.</summary>
        public void Follow(double dxNormalized)
        {
            if (_disposed || _settling) return;
            SetOffset(OffsetFor(dxNormalized));
        }

        /// <summary>
        /// Ends the gesture: finish the slide (and switch) or slide back. Commit needs
        /// either enough travel or a short, fast flick.
        /// </summary>
        public void Settle(double dxNormalized, double elapsedMs)
        {
            if (_disposed || _settling) return;

            double travel = Math.Abs(dxNormalized) * _config.DesktopSwipeGain;
            bool commit = travel >= _config.DesktopSwipeCommitRatio ||
                (travel >= _config.DesktopSwipeCommitRatio * 0.6 && elapsedMs <= 220);
            _commit = commit;
            _settleFrom = _offset;
            _settleTo = commit ? (_direction > 0 ? -_width : _width) : 0;
            if (_settleFrom == _settleTo)
            {
                Finish();
                return;
            }

            _settling = true;
            _settleStart = Clock.NowMs;
            _settleDuration = Math.Max(60, _config.DesktopSwipeSettleMs);
            Native.SetTimer(_hwnd, new IntPtr(TimerSettle), SettleIntervalMs, IntPtr.Zero);
            Log.Debug("Desktop slide settling: offset=" + _offset + " target=" + _settleTo +
                " commit=" + commit + " travel=" + travel.ToString("0.000"));
        }

        int OffsetFor(double dxNormalized)
        {
            double pixels = dxNormalized * _config.DesktopSwipeGain * _width;
            int value = (int)Math.Round(pixels);
            if (_direction > 0) return Math.Max(-_width, Math.Min(0, value));
            return Math.Max(0, Math.Min(_width, value));
        }

        void SetOffset(int value)
        {
            if (value == _offset) return;
            _offset = value;
            if (_hwnd == IntPtr.Zero) return;
            // Compose off-screen and present once; the window never shows a half-built frame.
            RenderFrame();
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            Native.UpdateWindow(_hwnd);
        }

        void RenderFrame()
        {
            if (_frameDc == IntPtr.Zero) return;
            if (_incomingDc != IntPtr.Zero)
                Native.BitBlt(_frameDc, 0, 0, _width, _height, _incomingDc, 0, 0, Native.SRCCOPY);
            if (_desktopDc != IntPtr.Zero)
                Native.BitBlt(_frameDc, _offset, 0, _width, _height, _desktopDc, 0, 0, Native.SRCCOPY);
            // Keep the taskbar in place while the desktop slides underneath it.
            if (_incomingDc != IntPtr.Zero && _taskbarWidth > 0)
                Native.BitBlt(_frameDc, _taskbarX, _taskbarY, _taskbarWidth, _taskbarHeight,
                    _incomingDc, _taskbarX, _taskbarY, Native.SRCCOPY);
        }

        void TickSettle()
        {
            if (_disposed) return;
            double t = _settleDuration <= 0 ? 1.0 : Math.Min(1.0, (Clock.NowMs - _settleStart) / _settleDuration);
            double eased = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);
            SetOffset((int)Math.Round(_settleFrom + (_settleTo - _settleFrom) * eased));
            if (t >= 1.0)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerSettle));
                _settling = false;
                Finish();
            }
        }

        void Finish()
        {
            if (_disposed) return;
            bool commit = _commit;
            // Switch while the overlay still covers the screen, then drop it; the new
            // desktop is already in place when the window disappears.
            if (commit && _switchDesktop != null)
            {
                try { _switchDesktop(_direction); }
                catch (Exception ex) { Log.Warn("Desktop switch failed: " + ex.Message); }
            }
            Close();
        }

        bool CreateFrameBuffer()
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                _frameDc = Native.CreateCompatibleDC(screen);
                _frameBitmap = Native.CreateCompatibleBitmap(screen, _width, _height);
                if (_frameDc == IntPtr.Zero || _frameBitmap == IntPtr.Zero) return false;
                _frameOld = Native.SelectObject(_frameDc, _frameBitmap);
                return true;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        bool CaptureDesktop()
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                Log.Warn("Desktop slide: cannot get the screen DC.");
                return false;
            }
            try
            {
                _desktopDc = Native.CreateCompatibleDC(screen);
                _desktopBitmap = Native.CreateCompatibleBitmap(screen, _width, _height);
                if (_desktopDc == IntPtr.Zero || _desktopBitmap == IntPtr.Zero) return false;
                _desktopOld = Native.SelectObject(_desktopDc, _desktopBitmap);
                return Native.BitBlt(_desktopDc, 0, 0, _width, _height, screen, _originX, _originY, Native.SRCCOPY);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>
        /// Builds the incoming desktop: the shared wallpaper plus the taskbar, which is
        /// the same on every virtual desktop. Windows of the neighbour desktop cannot be
        /// rendered before the switch, so they appear when the slide finishes.
        /// </summary>
        bool BuildIncomingLayer()
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                _incomingDc = Native.CreateCompatibleDC(screen);
                _incomingBitmap = Native.CreateCompatibleBitmap(screen, _width, _height);
                if (_incomingDc == IntPtr.Zero || _incomingBitmap == IntPtr.Zero) return false;
                _incomingOld = Native.SelectObject(_incomingDc, _incomingBitmap);

                bool painted = DrawWallpaper(_incomingDc);
                if (!painted)
                {
                    if (_brush == IntPtr.Zero) _brush = Native.CreateSolidBrush(Rgb(24, 24, 28));
                    var full = new Native.RECT();
                    full.Right = _width;
                    full.Bottom = _height;
                    Native.FillRect(_incomingDc, ref full, _brush);
                }
                CopyTaskbar();
                return true;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        bool DrawWallpaper(IntPtr hdc)
        {
            // PaintDesktop renders the wallpaper exactly as the shell does (including
            // slideshow wallpapers); fall back to the file from SPI_GETDESKWALLPAPER.
            if (Native.PaintDesktop(hdc)) return true;

            EnsureWallpaper();
            if (_wallpaperImage == IntPtr.Zero) return false;
            IntPtr graphics = IntPtr.Zero;
            try
            {
                if (Native.GdipCreateFromHDC(hdc, out graphics) != 0 || graphics == IntPtr.Zero)
                    return false;
                Native.GdipSetInterpolationMode(graphics, 7); // HighQualityBicubic
                double scale = Math.Max((double)_width / _wallpaperWidth, (double)_height / _wallpaperHeight);
                int drawWidth = Math.Max(1, (int)Math.Round(_wallpaperWidth * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(_wallpaperHeight * scale));
                int x = (_width - drawWidth) / 2;
                int y = (_height - drawHeight) / 2;
                return Native.GdipDrawImageRectI(graphics, _wallpaperImage, x, y, drawWidth, drawHeight) == 0;
            }
            finally
            {
                if (graphics != IntPtr.Zero) Native.GdipDeleteGraphics(graphics);
            }
        }

        void CopyTaskbar()
        {
            IntPtr taskbar = Native.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar)) return;
            Native.RECT rect;
            if (!Native.GetWindowRect(taskbar, out rect)) return;
            int x = rect.Left - _originX;
            int y = rect.Top - _originY;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;
            Native.BitBlt(_incomingDc, x, y, width, height, _desktopDc, x, y, Native.SRCCOPY);
            _taskbarX = x;
            _taskbarY = y;
            _taskbarWidth = width;
            _taskbarHeight = height;
        }

        static void EnsureWallpaper()
        {
            if (_wallpaperTried) return;
            _wallpaperTried = true;

            var path = new System.Text.StringBuilder(1024);
            if (!Native.SystemParametersInfoW(Native.SPI_GETDESKWALLPAPER, (uint)path.Capacity, path, 0))
                return;
            string file = path.ToString();
            if (string.IsNullOrEmpty(file)) return;

            var input = new Native.GdiplusStartupInput();
            input.GdiplusVersion = 1;
            IntPtr token;
            if (Native.GdiplusStartup(out token, ref input, IntPtr.Zero) != 0) return;
            IntPtr image = IntPtr.Zero;
            uint width;
            uint height;
            if (Native.GdipLoadImageFromFile(file, out image) != 0 || image == IntPtr.Zero ||
                Native.GdipGetImageWidth(image, out width) != 0 ||
                Native.GdipGetImageHeight(image, out height) != 0 ||
                width == 0 || height == 0)
            {
                if (image != IntPtr.Zero) Native.GdipDisposeImage(image);
                Native.GdiplusShutdown(token);
                return;
            }
            _wallpaperImage = image;
            _wallpaperWidth = width;
            _wallpaperHeight = height;
            _gdiplusToken = token;
        }

        IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (message)
                {
                    case WM_ERASEBKGND:
                        return new IntPtr(1);
                    case WM_MOUSEACTIVATE:
                        return new IntPtr(3); // MA_NOACTIVATE
                    case WM_NCHITTEST:
                        return new IntPtr(-1); // HTTRANSPARENT: clicks fall through
                    case WM_PAINT:
                        Paint(hwnd);
                        return IntPtr.Zero;
                    case WM_TIMER:
                        if (wParam.ToInt32() == TimerSettle) TickSettle();
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Desktop slide window failed: " + ex.Message);
            }
            return Native.CallWindowProcW(_oldProc, hwnd, message, wParam, lParam);
        }

        void Paint(IntPtr hwnd)
        {
            Native.PAINTSTRUCT paint;
            IntPtr hdc = Native.BeginPaint(hwnd, out paint);
            try
            {
                if (_frameDc != IntPtr.Zero)
                {
                    Native.BitBlt(hdc, 0, 0, _width, _height, _frameDc, 0, 0, Native.SRCCOPY);
                }
                else
                {
                    if (_brush == IntPtr.Zero) _brush = Native.CreateSolidBrush(Rgb(24, 24, 28));
                    var client = new Native.RECT();
                    Native.GetClientRect(hwnd, out client);
                    Native.FillRect(hdc, ref client, _brush);
                }
            }
            finally
            {
                Native.EndPaint(hwnd, ref paint);
            }
        }

        public void Close()
        {
            if (_disposed) return;
            _disposed = true;
            _settling = false;

            if (_hwnd != IntPtr.Zero)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerSettle));
                IntPtr hwnd = _hwnd;
                _hwnd = IntPtr.Zero;
                if (_oldProc != IntPtr.Zero)
                    Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, _oldProc);
                Native.DestroyWindow(hwnd);
            }

            ReleaseGdi();
            if (_closed != null) _closed(this);
        }

        void ReleaseGdi()
        {
            if (_desktopDc != IntPtr.Zero)
            {
                if (_desktopOld != IntPtr.Zero) Native.SelectObject(_desktopDc, _desktopOld);
                Native.DeleteDC(_desktopDc);
                _desktopDc = IntPtr.Zero;
            }
            if (_desktopBitmap != IntPtr.Zero)
            {
                Native.DeleteObject(_desktopBitmap);
                _desktopBitmap = IntPtr.Zero;
            }
            if (_incomingDc != IntPtr.Zero)
            {
                if (_incomingOld != IntPtr.Zero) Native.SelectObject(_incomingDc, _incomingOld);
                Native.DeleteDC(_incomingDc);
                _incomingDc = IntPtr.Zero;
            }
            if (_incomingBitmap != IntPtr.Zero)
            {
                Native.DeleteObject(_incomingBitmap);
                _incomingBitmap = IntPtr.Zero;
            }
            if (_frameDc != IntPtr.Zero)
            {
                if (_frameOld != IntPtr.Zero) Native.SelectObject(_frameDc, _frameOld);
                Native.DeleteDC(_frameDc);
                _frameDc = IntPtr.Zero;
            }
            if (_frameBitmap != IntPtr.Zero)
            {
                Native.DeleteObject(_frameBitmap);
                _frameBitmap = IntPtr.Zero;
            }
            if (_brush != IntPtr.Zero)
            {
                Native.DeleteObject(_brush);
                _brush = IntPtr.Zero;
            }
        }

        static int Rgb(int r, int g, int b)
        {
            return r | (g << 8) | (b << 16);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
