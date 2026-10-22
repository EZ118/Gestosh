using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RECT = Gestosh.Native.RECT;
using POINT = Gestosh.Native.POINT;

namespace Gestosh
{
    /// <summary>
    /// Full-screen App Expose overlay.  Live DWM thumbnails are rendered into a
    /// single topmost window; the source windows are never resized or moved.
    /// The overlay starts as a screenshot of the desktop and cross-fades to the
    /// dimmed desktop wallpaper while the frontmost non-minimized window scales
    /// from its real screen rect into the center of the arrangement.
    /// </summary>
    internal sealed class ExposeOverlay : IDisposable
    {
        const string WindowTitle = "Gestosh.AppExpose.v1";

        const uint WM_PAINT = 0x000F;
        const uint WM_ERASEBKGND = 0x0014;
        const uint WM_ACTIVATE = 0x0006;
        const uint WM_MOUSEACTIVATE = 0x0021;
        const uint WM_NCHITTEST = 0x0084;
        const uint WM_SETCURSOR = 0x0020;
        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_CLOSE = 0x0010;
        const uint WM_TIMER = 0x0113;
        const uint WM_DESTROY = 0x0002;

        const int TimerSnap = 1;
        const int TimerProgress = 2;

        sealed class Item
        {
            public WindowInfo Window;
            public IntPtr Thumbnail;
            public RECT Start;
            public RECT Target;
            public RECT Dest;
            public byte Opacity;
            public int Rank;
            public double Scale;
            public bool HasThumbnail;
        }

        sealed class LayoutCandidate
        {
            public int Rows;
            public List<List<Item>> Lines;
            public RECT[] Rects;
            public double[] Scales;
            public double Score;
        }

        readonly App _app;
        readonly List<Item> _items = new List<Item>();
        readonly List<IntPtr> _hiddenInputPanels = new List<IntPtr>();

        static IntPtr _wallpaperImage;
        static uint _wallpaperWidth;
        static uint _wallpaperHeight;
        static bool _wallpaperTried;
        static IntPtr _gdiplusToken;
        static IntPtr _staticBackgroundDc;
        static IntPtr _staticBackgroundBitmap;
        static IntPtr _staticBackgroundOld;
        static int _staticBackgroundWidth;
        static int _staticBackgroundHeight;
        static int _lastLayoutRows = -1;

        public static void Prepare()
        {
            EnsureWallpaper();
            BuildStaticBackground();
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
            Log.Debug("App Expose wallpaper: " + file + " " + width + "x" + height);
        }
        Native.WndProc _wndProc;
        IntPtr _hwnd;
        IntPtr _oldProc;
        IntPtr _background;
        IntPtr _panel;
        IntPtr _border;
        IntPtr _accent;
        IntPtr _hover;
        IntPtr _font;
        IntPtr _cursorArrow;
        IntPtr _cursorHand;
        IntPtr _cursorMove;
        IntPtr _previousForeground;

        int _originX;
        int _originY;
        int _width;
        int _height;
        int _focus = -1;
        int _selected = -1;
        bool _keyboardNavigated;
        bool _shown;
        IntPtr _keyboardHook;
        Native.LowLevelKeyboardProc _keyboardHookProc;
        IntPtr _mouseHook;
        Native.LowLevelMouseProc _mouseHookProc;

        double _progress;
        bool _progressAnimating;
        double _progressFrom;
        double _progressTo;
        double _progressStart;
        int _progressDuration;
        bool _closeWhenZero;
        bool _restorePreviousOnClose;
        IntPtr _activateAfterClose;

        int _hot = -1;
        int _drag = -1;
        bool _dragCanMove;
        bool _dragging;
        POINT _dragStart;
        RECT _dragHome;

        int _snap = -1;
        RECT _snapFrom;
        RECT _snapTo;
        double _snapStart;

        bool _disposed;

        public ExposeOverlay(App app)
        {
            _app = app;
        }

        public bool IsOpen { get { return _hwnd != IntPtr.Zero; } }

        public bool Show(double initialProgress, bool autoOpen)
        {
            var windows = WindowScanner.ScanExpose(_app.Config, _app.VirtualDesktop, _app.Hwnd);
            if (windows.Count == 0)
            {
                Log.Info("App Expose: no eligible windows.");
                return false;
            }

            _originX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            _originY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            _width = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            _height = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            _previousForeground = Native.GetForegroundWindow();

            CreateResources();

            _hwnd = Native.CreateWindowExW(
                (uint)(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
                "STATIC", WindowTitle, (uint)(Native.WS_POPUP | Native.WS_VISIBLE),
                -30000, -30000, _width, _height,
                IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Warn("App Expose: cannot create the overlay window. Win32=" + Marshal.GetLastWin32Error());
                return false;
            }

            _wndProc = WindowProc;
            _oldProc = Native.SetWindowLongPtr(_hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
            if (_oldProc == IntPtr.Zero)
            {
                Log.Warn("App Expose: cannot subclass the overlay window.");
                Native.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return false;
            }

            BuildItems(windows);
            _selected = _focus >= 0 ? _focus : 0;
            _keyboardNavigated = false;

            // Keep the overlay off-screen while its background and thumbnails are
            // prepared. A visible-but-offscreen window receives WM_PAINT reliably;
            // moving it on-screen then shows an already-composed frame.
            Native.SetWindowPos(_hwnd, new IntPtr(-1), -30000, -30000, _width, _height,
                Native.SWP_NOACTIVATE);
            RegisterThumbnails();
            Native.DwmFlush();
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            Native.UpdateWindow(_hwnd);

            Native.SetWindowPos(_hwnd, new IntPtr(-1), _originX, _originY, _width, _height,
                Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW);
            _shown = true;
            InstallKeyboardHook();
            InstallMouseHook();
            HideInputPanels();
            SetProgress(initialProgress);
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            if (autoOpen) AnimateProgressTo(1.0, 650);
            Log.Info("App Expose opened with " + _items.Count + " window(s), focus=" + _focus + ".");
            return true;
        }

        void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero) return;
            _keyboardHookProc = KeyboardHook;
            _keyboardHook = Native.SetWindowsHookExKeyboard(Native.WH_KEYBOARD_LL, _keyboardHookProc,
                Native.GetModuleHandleW(null), 0);
            if (_keyboardHook == IntPtr.Zero)
                Log.Warn("App Expose: cannot install keyboard hook. Win32=" + Marshal.GetLastWin32Error());
        }

        void UninstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            _keyboardHookProc = null;
        }

        static bool IsKeyDown(int key)
        {
            return (Native.GetAsyncKeyState(key) & 0x8000) != 0;
        }

        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || _disposed || !_shown)
                return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);

            int message = wParam.ToInt32();
            if (message != Native.WM_KEYDOWN && message != Native.WM_SYSKEYDOWN)
                return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);

            // While the close animation runs, keep swallowing keys so a shortcut
            // such as Win+Tab or Alt+Tab cannot open another overlay on top.
            if (_closeWhenZero) return new IntPtr(1);

            var data = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            int key = (int)data.vkCode;
            bool shift = IsKeyDown(Native.VK_SHIFT);
            bool control = IsKeyDown(Native.VK_CONTROL);
            bool alt = IsKeyDown(Native.VK_MENU);

            // Keep modifier presses from reaching the window that still owns focus.
            if (key == Native.VK_SHIFT || key == Native.VK_CONTROL || key == Native.VK_MENU)
                return new IntPtr(1);

            if (key == Native.VK_LWIN || key == Native.VK_RWIN)
            {
                CloseByKeyboard();
                return new IntPtr(1);
            }

            if (key == Native.VK_TAB)
            {
                if (control || alt)
                {
                    CloseByKeyboard();
                    return new IntPtr(1);
                }
                MoveSelection(shift ? -1 : 1);
                return new IntPtr(1);
            }

            if (key == Native.VK_LEFT || key == Native.VK_UP)
            {
                MoveSelection(-1);
                return new IntPtr(1);
            }
            if (key == Native.VK_RIGHT || key == Native.VK_DOWN)
            {
                MoveSelection(1);
                return new IntPtr(1);
            }
            if (key == Native.VK_RETURN || key == Native.VK_SPACE)
            {
                ActivateSelected();
                return new IntPtr(1);
            }
            if (key == Native.VK_ESCAPE)
            {
                CloseByKeyboard();
                return new IntPtr(1);
            }

            CloseByKeyboard();
            return new IntPtr(1);
        }

        void CloseByKeyboard()
        {
            if (_disposed) return;
            BeginCloseAnimation(false);
        }

        void MoveSelection(int delta)
        {
            if (_items.Count == 0) return;
            if (_selected < 0) _selected = 0;
            else _selected = (_selected + delta + _items.Count) % _items.Count;
            _keyboardNavigated = true;
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }

        void ActivateSelected()
        {
            if (_selected < 0 || _selected >= _items.Count) return;
            Activate(_selected);
        }

        void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseHookProc = MouseHook;
            _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseHookProc,
                Native.GetModuleHandleW(null), 0);
            if (_mouseHook == IntPtr.Zero)
                Log.Warn("App Expose: cannot install mouse hook. Win32=" + Marshal.GetLastWin32Error());
        }

        void UninstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            _mouseHookProc = null;
        }

        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || _disposed || !_shown)
                return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
            int message = wParam.ToInt32();
            if (message == WM_MOUSEMOVE)
            {
                // Never swallow mouse-move messages: doing so makes the system
                // cursor appear frozen while App Expose is open.
                if (_drag >= 0)
                {
                    var move = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    int mx = move.pt.X - _originX;
                    int my = move.pt.Y - _originY;
                    OnMouseMove(new IntPtr((my << 16) | (mx & 0xFFFF)));
                }
                return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }
            if (message != WM_LBUTTONDOWN && message != WM_LBUTTONUP &&
                message != WM_RBUTTONDOWN)
                return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);

            var data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
            int x = data.pt.X - _originX;
            int y = data.pt.Y - _originY;
            IntPtr lp = new IntPtr((y << 16) | (x & 0xFFFF));
            if (_closeWhenZero) return new IntPtr(1);
            if (message == WM_LBUTTONDOWN) OnMouseDown(lp);
            else if (message == WM_LBUTTONUP) OnMouseUp(lp);
            else BeginCloseAnimation(false);
            return new IntPtr(1);
        }

        void ForceForeground()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            uint foregroundThread = 0;
            if (foreground != IntPtr.Zero)
            {
                uint pid;
                foregroundThread = Native.GetWindowThreadProcessId(foreground, out pid);
            }
            uint currentThread = Native.GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                Native.AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                Native.SetForegroundWindow(_hwnd);
                Native.SetFocus(_hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        void CreateResources()
        {
            _background = Native.CreateSolidBrush(Rgb(18, 18, 24));
            _panel = Native.CreateSolidBrush(Rgb(48, 48, 58));
            _border = Native.CreateSolidBrush(Rgb(86, 86, 100));
            _accent = Native.CreateSolidBrush(Rgb(72, 150, 255));
            _hover = Native.CreateSolidBrush(Rgb(255, 255, 255));
            _font = Native.CreateFontW(-15, 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _cursorArrow = Native.LoadCursorW(IntPtr.Zero, new IntPtr(Native.IDC_ARROW));
            _cursorHand = Native.LoadCursorW(IntPtr.Zero, new IntPtr(Native.IDC_HAND));
            _cursorMove = Native.LoadCursorW(IntPtr.Zero, new IntPtr(Native.IDC_SIZEALL));
        }

        static void BuildStaticBackground()
        {
            if (_staticBackgroundDc != IntPtr.Zero) return;
            EnsureWallpaper();
            if (_wallpaperImage == IntPtr.Zero) return;

            int width = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
            int height = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return;
            try
            {
                _staticBackgroundDc = Native.CreateCompatibleDC(screen);
                _staticBackgroundBitmap = Native.CreateCompatibleBitmap(screen, width, height);
                if (_staticBackgroundDc == IntPtr.Zero || _staticBackgroundBitmap == IntPtr.Zero) return;
                _staticBackgroundOld = Native.SelectObject(_staticBackgroundDc, _staticBackgroundBitmap);
                if (!DrawWallpaperTo(_staticBackgroundDc, width, height)) return;

                _staticBackgroundWidth = width;
                _staticBackgroundHeight = height;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        static bool DrawWallpaperTo(IntPtr hdc, int width, int height)
        {
            if (_wallpaperImage == IntPtr.Zero) return false;
            IntPtr graphics = IntPtr.Zero;
            try
            {
                if (Native.GdipCreateFromHDC(hdc, out graphics) != 0 || graphics == IntPtr.Zero)
                    return false;
                Native.GdipSetInterpolationMode(graphics, 7); // HighQualityBicubic
                double scale = Math.Max((double)width / _wallpaperWidth, (double)height / _wallpaperHeight);
                int drawWidth = Math.Max(1, (int)Math.Round(_wallpaperWidth * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(_wallpaperHeight * scale));
                int x = (width - drawWidth) / 2;
                int y = (height - drawHeight) / 2;
                return Native.GdipDrawImageRectI(graphics, _wallpaperImage, x, y, drawWidth, drawHeight) == 0;
            }
            finally
            {
                if (graphics != IntPtr.Zero) Native.GdipDeleteGraphics(graphics);
            }
        }

        void DeleteResources()
        {
            if (_background != IntPtr.Zero) Native.DeleteObject(_background);
            if (_panel != IntPtr.Zero) Native.DeleteObject(_panel);
            if (_border != IntPtr.Zero) Native.DeleteObject(_border);
            if (_accent != IntPtr.Zero) Native.DeleteObject(_accent);
            if (_hover != IntPtr.Zero) Native.DeleteObject(_hover);
            if (_font != IntPtr.Zero) Native.DeleteObject(_font);
            _background = _panel = _border = _accent = _hover = _font = IntPtr.Zero;
        }

        void BuildItems(List<WindowInfo> windows)
        {
            _items.Clear();
            int n = windows.Count;
            _focus = n == 0 ? -1 : FindFocus(windows);
            if (n == 0) { _selected = -1; return; }

            for (int i = 0; i < n; i++)
            {
                var info = windows[i];
                var item = new Item();
                item.Window = info;
                item.Rank = i == _focus ? -1 : i;
                item.Start = LocalRect(info.Rect);
                item.Dest = item.Start;
                _items.Add(item);
            }

            _selected = _focus >= 0 ? _focus : 0;
            _keyboardNavigated = false;

            RECT viewport = GetViewport();
            if (n == 1)
            {
                LayoutSingle(_items[0], viewport);
                _lastLayoutRows = 1;
                return;
            }

            double[] baseScale = new double[n];
            int referenceLong = Math.Min(430, Math.Max(220, (int)(Math.Min(viewport.Width, viewport.Height) * 0.32)));
            for (int i = 0; i < n; i++)
            {
                int longSide = Math.Max(120, Math.Max(_items[i].Window.Rect.Width, _items[i].Window.Rect.Height));
                baseScale[i] = Math.Max(0.15, Math.Min(0.6, (double)referenceLong / longSide));
            }

            LayoutCandidate best = null;
            for (int rows = 1; rows <= n; rows++)
            {
                if (n == 2 && rows != 1) break;
                var lines = BuildLines(rows);
                var candidate = ScoreLayout(lines, rows, baseScale, viewport);
                if (best == null || candidate.Score > best.Score) best = candidate;
            }

            if (best != null)
            {
                for (int i = 0; i < n; i++)
                {
                    _items[i].Target = best.Rects[i];
                    _items[i].Scale = best.Scales[i];
                }
                _lastLayoutRows = best.Rows;
            }
        }

        RECT GetViewport()
        {
            RECT work = new RECT();
            if (!Native.SystemParametersInfoRect(Native.SPI_GETWORKAREA, 0, ref work, 0))
            {
                work.Left = _originX;
                work.Top = _originY;
                work.Right = _originX + _width;
                work.Bottom = _originY + _height;
            }
            work.Left -= _originX;
            work.Top -= _originY;
            work.Right -= _originX;
            work.Bottom -= _originY;
            if (work.Left < 0) work.Left = 0;
            if (work.Top < 0) work.Top = 0;
            if (work.Right > _width) work.Right = _width;
            if (work.Bottom > _height) work.Bottom = _height;

            const int padding = 32;
            RECT viewport;
            viewport.Left = work.Left + padding;
            viewport.Top = work.Top + padding;
            viewport.Right = work.Right - padding;
            viewport.Bottom = work.Bottom - padding;
            if (viewport.Right - viewport.Left < 80) viewport.Left = work.Left;
            if (viewport.Bottom - viewport.Top < 80) viewport.Top = work.Top;
            return viewport;
        }

        void LayoutSingle(Item item, RECT viewport)
        {
            int longSide = Math.Max(120, Math.Max(item.Window.Rect.Width, item.Window.Rect.Height));
            int referenceLong = Math.Min(430, Math.Max(220, (int)(Math.Min(viewport.Width, viewport.Height) * 0.32)));
            double scale = Math.Max(0.15, Math.Min(0.6, (double)referenceLong / longSide));
            int w = Math.Max(80, (int)Math.Round(Math.Max(80, item.Window.Rect.Width) * scale));
            int h = Math.Max(60, (int)Math.Round(Math.Max(60, item.Window.Rect.Height) * scale));
            int left = viewport.Left + (viewport.Width - w) / 2;
            int top = viewport.Top + (viewport.Height - h) / 2;
            item.Target.Left = left;
            item.Target.Top = top;
            item.Target.Right = left + w;
            item.Target.Bottom = top + h;
            item.Scale = scale;
        }

        List<List<Item>> BuildLines(int rows)
        {
            var lines = new List<List<Item>>();
            for (int i = 0; i < rows; i++) lines.Add(new List<Item>());
            int soft = (_items.Count + rows - 1) / rows;
            int row = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (row < rows - 1 && lines[row].Count >= soft) row++;
                lines[row].Add(_items[i]);
            }
            return lines;
        }

        LayoutCandidate ScoreLayout(List<List<Item>> lines, int rows, double[] baseScale, RECT viewport)
        {
            const int gapX = 16;
            const int gapY = 16;
            int n = _items.Count;
            var candidate = new LayoutCandidate();
            candidate.Rows = rows;
            candidate.Lines = lines;
            candidate.Rects = new RECT[n];
            candidate.Scales = new double[n];

            double widthScale = 1000.0;
            double heightScale = 1000.0;
            double totalRowBaseHeight = 0;
            for (int r = 0; r < lines.Count; r++)
            {
                double sumWidth = 0;
                double maxHeight = 0;
                for (int k = 0; k < lines[r].Count; k++)
                {
                    int index = _items.IndexOf(lines[r][k]);
                    double w = Math.Max(80, lines[r][k].Window.Rect.Width) * baseScale[index];
                    double h = Math.Max(60, lines[r][k].Window.Rect.Height) * baseScale[index];
                    sumWidth += w;
                    if (h > maxHeight) maxHeight = h;
                }
                double rowWidth = sumWidth + gapX * Math.Max(0, lines[r].Count - 1);
                if (rowWidth > 1)
                    widthScale = Math.Min(widthScale, Math.Max(0.01, (viewport.Width - gapX * Math.Max(0, lines[r].Count - 1)) / sumWidth));
                totalRowBaseHeight += maxHeight;
            }
            if (totalRowBaseHeight > 0)
                heightScale = Math.Max(0.01, (viewport.Height - gapY * Math.Max(0, lines.Count - 1)) / totalRowBaseHeight);
            double layoutScale = Math.Min(widthScale, heightScale);
            layoutScale = Math.Max(0.01, layoutScale);

            for (int i = 0; i < n; i++)
                candidate.Scales[i] = Math.Min(0.6, baseScale[i] * layoutScale);

            double totalHeight = 0;
            var rowHeights = new double[lines.Count];
            for (int r = 0; r < lines.Count; r++)
            {
                double maxHeight = 0;
                for (int k = 0; k < lines[r].Count; k++)
                {
                    int index = _items.IndexOf(lines[r][k]);
                    double h = Math.Max(60, lines[r][k].Window.Rect.Height) * candidate.Scales[index];
                    if (h > maxHeight) maxHeight = h;
                }
                rowHeights[r] = maxHeight;
                totalHeight += maxHeight;
            }
            totalHeight += gapY * Math.Max(0, lines.Count - 1);
            double top = viewport.Top + (viewport.Height - totalHeight) / 2.0;

            for (int r = 0; r < lines.Count; r++)
            {
                double rowWidth = 0;
                for (int k = 0; k < lines[r].Count; k++)
                {
                    int index = _items.IndexOf(lines[r][k]);
                    rowWidth += Math.Max(80, lines[r][k].Window.Rect.Width) * candidate.Scales[index];
                }
                rowWidth += gapX * Math.Max(0, lines[r].Count - 1);
                double left = viewport.Left + (viewport.Width - rowWidth) / 2.0;
                for (int k = 0; k < lines[r].Count; k++)
                {
                    Item item = lines[r][k];
                    int index = _items.IndexOf(item);
                    double scale = candidate.Scales[index];
                    int w = Math.Max(40, (int)Math.Round(Math.Max(80, item.Window.Rect.Width) * scale));
                    int h = Math.Max(30, (int)Math.Round(Math.Max(60, item.Window.Rect.Height) * scale));
                    int x = (int)Math.Round(left);
                    int y = (int)Math.Round(top + (rowHeights[r] - h) / 2.0);
                    candidate.Rects[index].Left = x;
                    candidate.Rects[index].Top = y;
                    candidate.Rects[index].Right = x + w;
                    candidate.Rects[index].Bottom = y + h;
                    left += w + gapX;
                }
                top += rowHeights[r] + gapY;
            }

            double totalArea = 0;
            double minScale = 1;
            for (int i = 0; i < n; i++)
            {
                totalArea += (candidate.Rects[i].Right - candidate.Rects[i].Left) *
                    (candidate.Rects[i].Bottom - candidate.Rects[i].Top);
                if (candidate.Scales[i] < minScale) minScale = candidate.Scales[i];
            }
            double area = Math.Max(1, viewport.Width * viewport.Height);
            double fillRatio = Math.Min(1.0, totalArea / area);

            double mean = (double)n / lines.Count;
            double variance = 0;
            for (int r = 0; r < lines.Count; r++)
            {
                double d = lines[r].Count - mean;
                variance += d * d;
            }
            variance /= lines.Count;
            double sd = Math.Sqrt(variance);
            double uniformity = mean > 0.01 ? Math.Max(0, 1.0 - sd / mean) : 0;

            double spatial = SpatialFidelity(candidate.Rects);
            double stability = _lastLayoutRows < 0 ? 0.5 :
                Math.Max(0, 1.0 - Math.Abs(rows - _lastLayoutRows) / (double)n);
            double scalePenalty = minScale < 0.15 ? (0.15 - minScale) / 0.15 : 0;
            double orphan = 0;
            if (lines.Count > 1)
            {
                double last = lines[lines.Count - 1].Count;
                double threshold = mean * 0.5;
                if (last < threshold && threshold > 0) orphan = (threshold - last) / threshold;
            }

            candidate.Score = 1.0 * fillRatio + 0.8 * uniformity + 1.2 * spatial +
                0.3 * stability - 1.0 * scalePenalty - 0.5 * orphan;
            return candidate;
        }

        double SpatialFidelity(RECT[] rects)
        {
            int n = rects.Length;
            if (n < 2) return 1.0;
            double sum = 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                double ox = _items[i].Window.Rect.Left + Math.Max(80, _items[i].Window.Rect.Width) / 2.0;
                double oy = _items[i].Window.Rect.Top + Math.Max(60, _items[i].Window.Rect.Height) / 2.0;
                double tx = rects[i].Left + (rects[i].Right - rects[i].Left) / 2.0;
                double ty = rects[i].Top + (rects[i].Bottom - rects[i].Top) / 2.0;
                for (int j = i + 1; j < n; j++)
                {
                    double o2x = _items[j].Window.Rect.Left + Math.Max(80, _items[j].Window.Rect.Width) / 2.0;
                    double o2y = _items[j].Window.Rect.Top + Math.Max(60, _items[j].Window.Rect.Height) / 2.0;
                    double t2x = rects[j].Left + (rects[j].Right - rects[j].Left) / 2.0;
                    double t2y = rects[j].Top + (rects[j].Bottom - rects[j].Top) / 2.0;
                    double ovx = o2x - ox, ovy = o2y - oy;
                    double tvx = t2x - tx, tvy = t2y - ty;
                    double ol = Math.Sqrt(ovx * ovx + ovy * ovy);
                    double tl = Math.Sqrt(tvx * tvx + tvy * tvy);
                    if (ol < 1 || tl < 1) continue;
                    sum += (ovx * tvx + ovy * tvy) / (ol * tl);
                    count++;
                }
            }
            return count == 0 ? 1.0 : sum / count;
        }

        int FindFocus(List<WindowInfo> windows)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            IntPtr root = foreground == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(foreground, (uint)Native.GA_ROOT);
            if (root != IntPtr.Zero)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    if (windows[i].Hwnd == root && !Native.IsIconic(windows[i].Hwnd)) return i;
                }
            }
            for (int i = 0; i < windows.Count; i++)
            {
                if (!Native.IsIconic(windows[i].Hwnd)) return i;
            }
            return 0;
        }

        RECT LocalRect(RECT screen)
        {
            RECT rect;
            rect.Left = screen.Left - _originX;
            rect.Top = screen.Top - _originY;
            rect.Right = screen.Right - _originX;
            rect.Bottom = screen.Bottom - _originY;
            return rect;
        }

        void HideInputPanels()
        {
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                string className = Native.GetWindowClass(hwnd);
                string title = Native.GetWindowTitle(hwnd);
                string lower = title == null ? "" : title.Trim().ToLowerInvariant();
                bool panel = className == "IME" || className == "MSCTFIME UI" ||
                    className == "InputTip" || className == "CandidateWindow" ||
                    (className == "Windows.UI.Core.CoreWindow" &&
                     (lower == "microsoft text input application" ||
                      lower == "windows input experience" ||
                      lower == "inputtip"));
                if (panel)
                {
                    Native.ShowWindow(hwnd, Native.SW_HIDE);
                    _hiddenInputPanels.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);
        }

        void RestoreInputPanels()
        {
            foreach (var hwnd in _hiddenInputPanels)
            {
                if (Native.IsWindow(hwnd)) Native.ShowWindow(hwnd, Native.SW_SHOW);
            }
            _hiddenInputPanels.Clear();
        }

        void RegisterThumbnails()
        {
            // Register the background windows first and the focus window last,
            // so the front window sits above the others in the DWM thumbnail order.
            foreach (var item in _items)
            {
                if (item.Rank < 0) continue;
                RegisterThumbnail(item);
            }
            if (_focus >= 0 && _focus < _items.Count) RegisterThumbnail(_items[_focus]);
        }

        void RegisterThumbnail(Item item)
        {
            if (!Native.IsWindow(item.Window.Hwnd)) return;
            IntPtr thumbnail;
            if (Native.DwmRegisterThumbnail(_hwnd, item.Window.Hwnd, out thumbnail) == 0)
            {
                item.Thumbnail = thumbnail;
                item.HasThumbnail = true;
                UpdateThumbnail(item);
            }
        }

        void RaiseThumbnail(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var item = _items[index];
            if (!Native.IsWindow(item.Window.Hwnd)) return;
            if (item.Thumbnail != IntPtr.Zero)
            {
                Native.DwmUnregisterThumbnail(item.Thumbnail);
                item.Thumbnail = IntPtr.Zero;
                item.HasThumbnail = false;
            }
            RegisterThumbnail(item);
        }

        void UpdateThumbnail(Item item)
        {
            if (item.Thumbnail == IntPtr.Zero) return;
            var properties = new Native.DWM_THUMBNAIL_PROPERTIES();
            properties.dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE |
                Native.DWM_TNP_OPACITY;
            properties.rcDestination = item.Dest;
            properties.opacity = item.Opacity;
            properties.fVisible = item.Opacity > 0;
            properties.fSourceClientAreaOnly = false;
            Native.DwmUpdateThumbnailProperties(item.Thumbnail, ref properties);
        }

        void SetProgress(double progress)
        {
            _progress = Math.Max(0, Math.Min(1, progress));
            ApplyProgress();
        }

        public void SetGestureProgress(double progress)
        {
            if (_disposed) return;
            StopProgressTimer();
            SetProgress(progress);
        }

        public void EndGesture(bool commit)
        {
            if (_disposed) return;
            if (commit)
            {
                _closeWhenZero = false;
                _restorePreviousOnClose = false;
                _activateAfterClose = IntPtr.Zero;
                AnimateProgressTo(1.0, 600);
            }
            else
            {
                BeginCloseAnimation(false);
            }
        }

        void BeginCloseAnimation(bool restorePrevious)
        {
            BeginCloseAnimation(restorePrevious, IntPtr.Zero);
        }

        void BeginCloseAnimation(bool restorePrevious, IntPtr activateAfter)
        {
            if (_disposed) return;
            _restorePreviousOnClose = restorePrevious;
            _activateAfterClose = activateAfter;
            _closeWhenZero = true;
            if (_progress <= 0.001)
            {
                Close(restorePrevious);
                if (activateAfter != IntPtr.Zero) FocusSource(activateAfter);
            }
            else
            {
                AnimateProgressTo(0.0, 500);
            }
        }

        public void SetCloseGestureProgress(double closeProgress)
        {
            if (_disposed) return;
            StopProgressTimer();
            SetProgress(1.0 - Math.Max(0, Math.Min(1, closeProgress)));
        }

        public void EndCloseGesture(bool close)
        {
            if (_disposed) return;
            if (close)
            {
                BeginCloseAnimation(false);
            }
            else
            {
                _closeWhenZero = false;
                _restorePreviousOnClose = false;
                AnimateProgressTo(1.0, 500);
            }
        }

        void ApplyProgress()
        {
            double focusEase = EaseInOutCubic(_progress);
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (i == _focus)
                {
                    item.Dest = LerpRect(item.Start, item.Target, focusEase);
                    item.Opacity = 255;
                }
                else
                {
                    double delay = 0.10 + Math.Max(0, item.Rank) * 0.035;
                    double local = (_progress - delay) / Math.Max(0.01, 1.0 - delay);
                    local = Math.Max(0, Math.Min(1, local));
                    local = EaseInOutCubic(local);
                    item.Dest = LerpRect(item.Start, item.Target, local);
                    item.Opacity = 255;
                }
                UpdateThumbnail(item);
            }
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            Native.UpdateWindow(_hwnd);
        }

        void AnimateProgressTo(double target, int duration)
        {
            _progressFrom = _progress;
            _progressTo = target;
            _progressStart = Clock.NowMs;
            _progressDuration = Math.Max(1, duration);
            _progressAnimating = true;
            Native.SetTimer(_hwnd, new IntPtr(TimerProgress), 15, IntPtr.Zero);
        }

        void StopProgressTimer()
        {
            _progressAnimating = false;
            Native.KillTimer(_hwnd, new IntPtr(TimerProgress));
        }

        void TickProgress()
        {
            if (!_progressAnimating)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerProgress));
                return;
            }
            double t = Math.Min(1.0, (Clock.NowMs - _progressStart) / _progressDuration);
            double eased = EaseInOutCubic(t);
            SetProgress(_progressFrom + (_progressTo - _progressFrom) * eased);
            if (t >= 1.0)
            {
                _progressAnimating = false;
                Native.KillTimer(_hwnd, new IntPtr(TimerProgress));
                if (_progressTo <= 0.001 && _closeWhenZero)
                {
                    IntPtr activate = _activateAfterClose;
                    _activateAfterClose = IntPtr.Zero;
                    bool restore = _restorePreviousOnClose;
                    Close(restore);
                    if (activate != IntPtr.Zero) FocusSource(activate);
                }
            }
        }

        static double EaseInOutCubic(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            if (t < 0.5) return 4.0 * t * t * t;
            double f = -2.0 * t + 2.0;
            return 1.0 - f * f * f / 2.0;
        }

        static RECT LerpRect(RECT a, RECT b, double t)
        {
            RECT rect;
            rect.Left = (int)Math.Round(a.Left + (b.Left - a.Left) * t);
            rect.Top = (int)Math.Round(a.Top + (b.Top - a.Top) * t);
            rect.Right = (int)Math.Round(a.Right + (b.Right - a.Right) * t);
            rect.Bottom = (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t);
            return rect;
        }

        IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (message)
                {
                    case WM_ERASEBKGND:
                        // Paint() always covers the full client area. Filling a dark
                        // erase colour here makes layered-window frames flash black.
                        return new IntPtr(1);
                    case WM_NCHITTEST:
                        // A top-level STATIC window can report HTTRANSPARENT and let
                        // clicks fall through to the desktop. Force the whole overlay
                        // to be a normal hit-test target.
                        return new IntPtr(1); // HTCLIENT
                    case WM_MOUSEACTIVATE:
                        return new IntPtr(3); // MA_NOACTIVATE
                    case WM_PAINT:
                        Paint(hwnd);
                        return IntPtr.Zero;
                    case WM_MOUSEMOVE:
                        OnMouseMove(lParam);
                        return IntPtr.Zero;
                    case WM_LBUTTONDOWN:
                        OnMouseDown(lParam);
                        return IntPtr.Zero;
                    case WM_LBUTTONUP:
                        OnMouseUp(lParam);
                        return IntPtr.Zero;
                    case WM_RBUTTONDOWN:
                        BeginCloseAnimation(false);
                        return IntPtr.Zero;
                    case WM_KEYDOWN:
                        if (wParam.ToInt32() == Native.VK_ESCAPE)
                        {
                            BeginCloseAnimation(false);
                            return IntPtr.Zero;
                        }
                        break;
                    case WM_CLOSE:
                        BeginCloseAnimation(false);
                        return IntPtr.Zero;
                    case WM_TIMER:
                        if (wParam.ToInt32() == TimerSnap) TickSnap();
                        else if (wParam.ToInt32() == TimerProgress) TickProgress();
                        return IntPtr.Zero;
                    case WM_SETCURSOR:
                        {
                            IntPtr cursor = _cursorArrow;
                            if (_dragging) cursor = _cursorMove;
                            else if (_hot >= 0 && _progress > 0.5) cursor = _cursorHand;
                            Native.SetCursor(cursor);
                            return new IntPtr(1);
                        }
                    case WM_DESTROY:
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("App Expose window failed: " + ex.Message);
            }
            return Native.CallWindowProcW(_oldProc, hwnd, message, wParam, lParam);
        }

        void Paint(IntPtr hwnd)
        {
            Native.PAINTSTRUCT paint;
            IntPtr hdc = Native.BeginPaint(hwnd, out paint);
            try
            {
                RECT client;
                Native.GetClientRect(hwnd, out client);

                bool hasStatic = _staticBackgroundDc != IntPtr.Zero &&
                    _staticBackgroundWidth == _width && _staticBackgroundHeight == _height;
                if (hasStatic)
                {
                    Native.BitBlt(hdc, 0, 0, _width, _height,
                        _staticBackgroundDc, 0, 0, Native.SRCCOPY);
                }
                else
                {
                    Native.FillRect(hdc, ref client, _background);
                }

                for (int i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    if (!item.HasThumbnail && item.Opacity > 15 && _panel != IntPtr.Zero)
                        Native.FillRect(hdc, ref item.Dest, _panel);

                    if (_progress > 0.06)
                    {
                        if (i == _selected && _drag < 0 && _hover != IntPtr.Zero)
                        {
                            RECT r = item.Dest;
                            for (int k = 0; k < 3; k++)
                            {
                                Native.FrameRect(hdc, ref r, _hover);
                                r.Left++;
                                r.Top++;
                                r.Right--;
                                r.Bottom--;
                                if (r.Right <= r.Left || r.Bottom <= r.Top) break;
                            }
                        }
                        else if (i == _hot && _drag < 0 && _hover != IntPtr.Zero)
                        {
                            RECT outer = item.Dest;
                            Native.FrameRect(hdc, ref outer, _hover);
                            RECT inner;
                            inner.Left = outer.Left + 1;
                            inner.Top = outer.Top + 1;
                            inner.Right = outer.Right - 1;
                            inner.Bottom = outer.Bottom - 1;
                            if (inner.Right > inner.Left && inner.Bottom > inner.Top)
                                Native.FrameRect(hdc, ref inner, _hover);
                        }
                        else
                        {
                            IntPtr border = i == _drag ? _accent : _border;
                            if (border != IntPtr.Zero) Native.FrameRect(hdc, ref item.Dest, border);
                        }
                    }
                }

                if (_drag < 0 && _progress > 0.5)
                {
                    if (_hot >= 0 && _hot < _items.Count) DrawTooltip(hdc, _items[_hot]);
                    else if (_keyboardNavigated && _selected >= 0 && _selected < _items.Count)
                        DrawTooltip(hdc, _items[_selected]);
                }
            }
            finally
            {
                Native.EndPaint(hwnd, ref paint);
            }
        }

        void DrawTooltip(IntPtr hdc, Item item)
        {
            string title = item.Window.Title;
            if (string.IsNullOrEmpty(title) || _font == IntPtr.Zero) return;

            IntPtr oldFont = Native.SelectObject(hdc, _font);
            Native.SetBkMode(hdc, Native.TRANSPARENT);
            Native.SetTextColor(hdc, Rgb(255, 255, 255));
            RECT calc;
            calc.Left = 0;
            calc.Top = 0;
            calc.Right = 560;
            calc.Bottom = 0;
            Native.DrawTextW(hdc, title, -1, ref calc,
                Native.DT_CALCRECT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

            int width = Math.Min(560, Math.Max(80, calc.Right + 20));
            int height = Math.Max(28, calc.Bottom + 10);
            int center = item.Dest.Left + (item.Dest.Right - item.Dest.Left) / 2;
            int left = center - width / 2;
            if (left < 8) left = 8;
            if (left + width > _width - 8) left = _width - width - 8;
            int top = item.Dest.Bottom + 8;
            if (top + height > _height - 8) top = item.Dest.Top - height - 8;
            if (top < 8) top = 8;

            RECT box;
            box.Left = left;
            box.Top = top;
            box.Right = left + width;
            box.Bottom = top + height;
            Native.FillRect(hdc, ref box, _panel);
            Native.FrameRect(hdc, ref box, _hover);
            Native.DrawTextW(hdc, title, -1, ref box,
                Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE |
                Native.DT_END_ELLIPSIS | Native.DT_NOPREFIX);
            Native.SelectObject(hdc, oldFont);
        }

        static int Rgb(int r, int g, int b)
        {
            return r | (g << 8) | (b << 16);
        }

        static int X(IntPtr lParam)
        {
            return (short)(lParam.ToInt32() & 0xFFFF);
        }

        static int Y(IntPtr lParam)
        {
            return (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        }

        static bool Inside(POINT p, RECT r)
        {
            return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        }

        int HitTest(POINT point)
        {
            if (_focus >= 0 && _focus < _items.Count && Inside(point, _items[_focus].Dest))
                return _focus;
            for (int i = 0; i < _items.Count; i++)
            {
                if (i == _focus) continue;
                if (Inside(point, _items[i].Dest)) return i;
            }
            return -1;
        }

        void OnMouseDown(IntPtr lParam)
        {
            if (_progress < 0.5) return;
            Native.SetCapture(_hwnd);
            if (_snap >= 0)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerSnap));
                var previous = _items[_snap];
                previous.Dest = previous.Target;
                previous.Opacity = 255;
                UpdateThumbnail(previous);
                _snap = -1;
            }

            _dragStart.X = X(lParam);
            _dragStart.Y = Y(lParam);
            _drag = HitTest(_dragStart);
            if (_drag >= 0)
            {
                _selected = _drag;
                _keyboardNavigated = false;
            }
            _dragging = false;
            _dragCanMove = false;
            if (_drag >= 0)
            {
                _dragHome = _items[_drag].Target;
                _dragCanMove = Inside(_dragStart, _items[_drag].Dest);
                _hot = _drag;
            }
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }

        void OnMouseMove(IntPtr lParam)
        {
            if (_progress < 0.5) return;
            POINT point;
            point.X = X(lParam);
            point.Y = Y(lParam);

            if (_drag >= 0)
            {
                int dx = point.X - _dragStart.X;
                int dy = point.Y - _dragStart.Y;
                if (_dragCanMove && (Math.Abs(dx) + Math.Abs(dy) > 4)) _dragging = true;
                if (_dragging)
                {
                    var item = _items[_drag];
                    item.Dest.Left = _dragHome.Left + dx;
                    item.Dest.Top = _dragHome.Top + dy;
                    item.Dest.Right = _dragHome.Right + dx;
                    item.Dest.Bottom = _dragHome.Bottom + dy;
                    item.Opacity = 255;
                    UpdateThumbnail(item);
                    Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
                }
                Native.SetCursor(_dragging ? _cursorMove : _cursorHand);
                return;
            }

            int hot = HitTest(point);
            if (hot != _hot)
            {
                _hot = hot;
                Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            }
            Native.SetCursor(hot >= 0 ? _cursorHand : _cursorArrow);
        }

        void OnMouseUp(IntPtr lParam)
        {
            if (_progress < 0.5) return;
            Native.ReleaseCapture();
            int index = _drag;
            bool dragged = _dragging;
            _drag = -1;
            _dragging = false;
            _dragCanMove = false;

            if (index < 0)
            {
                BeginCloseAnimation(false);
                return;
            }
            if (dragged)
            {
                StartSnap(index);
                return;
            }
            Activate(index);
        }

        void StartSnap(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            _snap = index;
            _snapFrom = _items[index].Dest;
            _snapTo = _items[index].Target;
            _snapStart = Clock.NowMs;
            Native.SetTimer(_hwnd, new IntPtr(TimerSnap), 15, IntPtr.Zero);
            Native.SetCursor(_cursorHand);
        }

        void TickSnap()
        {
            if (_snap < 0 || _snap >= _items.Count)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerSnap));
                return;
            }
            double t = Math.Min(1.0, (Clock.NowMs - _snapStart) / 160.0);
            double eased = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);
            var item = _items[_snap];
            item.Dest = LerpRect(_snapFrom, _snapTo, eased);
            item.Opacity = 255;
            UpdateThumbnail(item);
            Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            if (t >= 1.0)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerSnap));
                _snap = -1;
            }
        }

        void Activate(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            IntPtr source = _items[index].Window.Hwnd;
            if (!Native.IsWindow(source)) return;
            Log.Info("App Expose selected window 0x" + source.ToInt64().ToString("x"));

            // Raise the selected thumbnail in the DWM thumbnail order before the
            // closing animation starts. Also raise the real window in the normal
            // z-order (without activating it or making it topmost) so it is the
            // topmost window when the overlay disappears.
            RaiseThumbnail(index);
            Native.SetWindowPos(source, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

            // The user explicitly chose a window, so it is safe to take foreground
            // ownership for this transition; blank clicks and opening do not.
            long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                new IntPtr(exStyle & ~(long)Native.WS_EX_NOACTIVATE));
            ForceForeground();
            BeginCloseAnimation(false, source);
        }

        void FocusSource(IntPtr source)
        {
            if (!Native.IsWindow(source)) return;
            if (Native.IsIconic(source)) Native.ShowWindow(source, Native.SW_RESTORE);

            uint pid;
            uint targetThread = Native.GetWindowThreadProcessId(source, out pid);
            uint currentThread = Native.GetCurrentThreadId();
            bool attached = targetThread != 0 && targetThread != currentThread &&
                Native.AttachThreadInput(currentThread, targetThread, true);
            try
            {
                // Alt-Tab-like activation: bring the window to the front of the
                // normal z-order and give it focus. Never make it topmost.
                Native.BringWindowToTop(source);
                Native.SetForegroundWindow(source);
                Native.SetActiveWindow(source);
                Native.SetFocus(source);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(currentThread, targetThread, false);
            }
        }

        public void Close(bool restorePrevious)
        {
            if (_disposed) return;
            _disposed = true;
            _shown = false;
            UninstallKeyboardHook();
            UninstallMouseHook();
            _closeWhenZero = false;
            _activateAfterClose = IntPtr.Zero;
            if (_hwnd != IntPtr.Zero)
            {
                Native.KillTimer(_hwnd, new IntPtr(TimerSnap));
                Native.KillTimer(_hwnd, new IntPtr(TimerProgress));
                Native.ReleaseCapture();
                foreach (var item in _items)
                {
                    if (item.Thumbnail != IntPtr.Zero)
                        Native.DwmUnregisterThumbnail(item.Thumbnail);
                }
                _items.Clear();

                IntPtr hwnd = _hwnd;
                _hwnd = IntPtr.Zero;
                if (_oldProc != IntPtr.Zero)
                    Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, _oldProc);
                Native.DestroyWindow(hwnd);
            }
            DeleteResources();
            RestoreInputPanels();

            if (restorePrevious && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
                Native.SetForegroundWindow(_previousForeground);

            _app.ExposeClosed(this);
        }

        public void Dispose()
        {
            Close(false);
        }
    }
}



