using System;
using System.Runtime.InteropServices;

namespace Gestosh
{
    /// <summary>
    /// Tray-menu settings window. It exposes one combo box per gesture so the user can
    /// map a gesture to an action without editing Gestosh.ini by hand. Built from plain
    /// Win32 controls (no WinForms/WPF) like the rest of the project.
    /// </summary>
    internal sealed class SettingsDialog : IDisposable
    {
        public const string WindowTitle = "Gestosh 手势设置";

        const uint WM_PAINT = 0x000F;
        const uint WM_ERASEBKGND = 0x0014;
        const uint WM_CTLCOLORSTATIC = 0x0138;
        const uint WM_COMMAND = 0x0111;
        const uint WM_CLOSE = 0x0010;
        const uint WM_DESTROY = 0x0002;
        const uint WM_SETFONT = 0x0030;

        const uint CB_ADDSTRING = 0x0143;
        const uint CB_GETCURSEL = 0x0147;
        const uint CB_SETCURSEL = 0x014E;

        const uint WS_CHILD = 0x40000000;
        const uint WS_VISIBLE = 0x10000000;
        const uint WS_TABSTOP = 0x00010000;
        const uint WS_VSCROLL = 0x00200000;
        const uint WS_CAPTION = 0x00C00000;
        const uint WS_SYSMENU = 0x00080000;
        const uint WS_GROUP = 0x00020000;
        const uint CBS_DROPDOWNLIST = 0x0003;
        const uint BS_DEFPUSHBUTTON = 0x00000001;
        const uint BS_PUSHBUTTON = 0x00000000;
        const int WS_EX_DLGMODALFRAME = 0x00000001;

        const int IdOk = 1;
        const int IdCancel = 2;

        // 96-DPI layout metrics.
        const int Margin = 20;
        const int RowHeight = 36;
        const int LabelWidth = 200;
        const int Gap = 12;
        const int ComboWidth = 220;
        const int ButtonWidth = 88;
        const int ButtonHeight = 30;
        const int NoteHeight = 20;

        static readonly string[] ThreeValues = { "none", "drag" };
        static readonly string[] ThreeLabels = { "无", "拖拽" };

        static readonly string[] FourValues = { "none", "expose", "taskview" };
        static readonly string[] FourLabels = { "无", "App Exposé", "Windows 任务视图" };

        static readonly string[] HorizontalValues = { "none", "desktop" };
        static readonly string[] HorizontalLabels = { "无", "切换桌面" };

        static readonly string[] EdgeValues = { "none", "actioncenter" };
        static readonly string[] EdgeLabels = { "无", "Windows 操作中心" };

        readonly Config _config;
        readonly Action<string, string, string, string, string> _save;
        readonly Action<SettingsDialog> _closed;

        Native.WndProc _wndProc;
        IntPtr _hwnd;
        IntPtr _oldProc;
        IntPtr _font;
        IntPtr _background;
        IntPtr _comboThree;
        IntPtr _comboDown;
        IntPtr _comboUp;
        IntPtr _comboHorizontal;
        IntPtr _comboEdge;
        int _dpi = 96;
        bool _disposed;

        public SettingsDialog(Config config, Action<string, string, string, string, string> save,
            Action<SettingsDialog> closed)
        {
            _config = config;
            _save = save;
            _closed = closed;
        }

        public IntPtr Hwnd { get { return _hwnd; } }

        public bool IsOpen { get { return _hwnd != IntPtr.Zero; } }

        public bool Show(IntPtr owner)
        {
            _hwnd = Native.CreateWindowExW(
                (uint)(Native.WS_EX_TOOLWINDOW | WS_EX_DLGMODALFRAME),
                "STATIC", WindowTitle, WS_CAPTION | WS_SYSMENU | Native.WS_POPUP,
                0, 0, 400, 300,
                owner, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Warn("Settings: cannot create the window. Win32=" + Marshal.GetLastWin32Error());
                return false;
            }

            _wndProc = WindowProc;
            _oldProc = Native.SetWindowLongPtr(_hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
            if (_oldProc == IntPtr.Zero)
            {
                Log.Warn("Settings: cannot subclass the window.");
                Close();
                return false;
            }

            _dpi = Native.GetDpi(_hwnd);
            if (_dpi <= 0) _dpi = 96;
            _font = Native.CreateFontW(-Scale(9), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _background = Native.CreateSolidBrush(Native.GetSysColor(15)); // COLOR_BTNFACE

            CreateControls();
            CenterWindow();
            Native.ShowWindow(_hwnd, 5); // SW_SHOW
            Native.SetForegroundWindow(_hwnd);
            Log.Info("Settings dialog opened.");
            return true;
        }

        int Scale(int value)
        {
            return (value * _dpi + 48) / 96;
        }

        void CreateControls()
        {
            int width = Scale(2 * Margin + LabelWidth + Gap + ComboWidth);
            int rowsTop = Scale(18);
            int labelX = Scale(Margin);
            int comboX = Scale(Margin + LabelWidth + Gap);
            int comboWidth = Scale(ComboWidth);
            int comboDrop = Scale(180);

            string[] labels = { "三指：", "四指向下滑动：", "四指向上滑动：", "四指左右滑动：", "双指由右侧向内滑动：" };
            IntPtr[] combos = new IntPtr[5];

            for (int i = 0; i < labels.Length; i++)
            {
                int y = rowsTop + Scale(RowHeight) * i;
                IntPtr label = Native.CreateWindowExW(0, "STATIC", labels[i],
                    WS_CHILD | WS_VISIBLE, labelX, y + Scale(6), Scale(LabelWidth), Scale(20),
                    _hwnd, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
                ApplyFont(label);

                IntPtr combo = Native.CreateWindowExW(0, "COMBOBOX", null,
                    WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWNLIST | WS_GROUP,
                    comboX, y + Scale(3), comboWidth, comboDrop,
                    _hwnd, new IntPtr(100 + i), Native.GetModuleHandleW(null), IntPtr.Zero);
                ApplyFont(combo);
                combos[i] = combo;
            }

            _comboThree = combos[0];
            _comboDown = combos[1];
            _comboUp = combos[2];
            _comboHorizontal = combos[3];
            _comboEdge = combos[4];

            Fill(_comboThree, ThreeLabels, ThreeValues, _config.ThreeFingerAction);
            Fill(_comboDown, FourLabels, FourValues, _config.FourFingerDownAction);
            Fill(_comboUp, FourLabels, FourValues, _config.FourFingerUpAction);
            Fill(_comboHorizontal, HorizontalLabels, HorizontalValues, _config.FourFingerHorizontalAction);
            Fill(_comboEdge, EdgeLabels, EdgeValues, _config.EdgeSwipeAction);

            int noteY = rowsTop + Scale(RowHeight) * labels.Length + Scale(2);
            IntPtr note = Native.CreateWindowExW(0, "STATIC", "保存到 Gestosh.ini，确定后立即生效。",
                WS_CHILD | WS_VISIBLE, labelX, noteY, width - Scale(2 * Margin), Scale(NoteHeight),
                _hwnd, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
            ApplyFont(note);

            int buttonY = noteY + Scale(NoteHeight) + Scale(12);
            int okX = width - Scale(Margin) - Scale(ButtonWidth) * 2 - Scale(8);
            int cancelX = width - Scale(Margin) - Scale(ButtonWidth);
            IntPtr ok = Native.CreateWindowExW(0, "BUTTON", "确定",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                okX, buttonY, Scale(ButtonWidth), Scale(ButtonHeight),
                _hwnd, new IntPtr(IdOk), Native.GetModuleHandleW(null), IntPtr.Zero);
            ApplyFont(ok);
            IntPtr cancel = Native.CreateWindowExW(0, "BUTTON", "取消",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                cancelX, buttonY, Scale(ButtonWidth), Scale(ButtonHeight),
                _hwnd, new IntPtr(IdCancel), Native.GetModuleHandleW(null), IntPtr.Zero);
            ApplyFont(cancel);

            int clientWidth = width;
            int clientHeight = buttonY + Scale(ButtonHeight) + Scale(Margin);

            // Turn the client size into a window size using the real (DPI-scaled) frame.
            Native.RECT windowRect;
            Native.RECT clientRect;
            int frameWidth = 0;
            int frameHeight = 0;
            if (Native.GetWindowRect(_hwnd, out windowRect) && Native.GetClientRect(_hwnd, out clientRect))
            {
                frameWidth = (windowRect.Right - windowRect.Left) - (clientRect.Right - clientRect.Left);
                frameHeight = (windowRect.Bottom - windowRect.Top) - (clientRect.Bottom - clientRect.Top);
            }
            Native.MoveWindow(_hwnd, 0, 0, clientWidth + frameWidth, clientHeight + frameHeight, false);

            if (_comboThree != IntPtr.Zero) Native.SetFocus(_comboThree);
        }

        void ApplyFont(IntPtr control)
        {
            if (control != IntPtr.Zero && _font != IntPtr.Zero)
                Native.SendMessageW(control, WM_SETFONT, _font, new IntPtr(1));
        }

        static void Fill(IntPtr combo, string[] labels, string[] values, string current)
        {
            if (combo == IntPtr.Zero) return;
            for (int i = 0; i < labels.Length; i++)
                Native.SendMessageText(combo, CB_ADDSTRING, IntPtr.Zero, labels[i]);

            int selected = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == current)
                {
                    selected = i;
                    break;
                }
            }
            Native.SendMessageW(combo, CB_SETCURSEL, new IntPtr(selected), IntPtr.Zero);
        }

        static string Selected(IntPtr combo, string[] values, string fallback)
        {
            if (combo == IntPtr.Zero) return fallback;
            int index = Native.SendMessageW(combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (index < 0 || index >= values.Length) return fallback;
            return values[index];
        }

        void CenterWindow()
        {
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;
            IntPtr monitor = Native.MonitorFromPoint(cursor, 2); // MONITOR_DEFAULTTONEAREST
            var info = new Native.MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.RECT work;
            if (monitor != IntPtr.Zero && Native.GetMonitorInfoW(monitor, ref info)) work = info.rcWork;
            else
            {
                work = new Native.RECT();
                work.Right = Native.GetSystemMetrics(Native.SM_CXSCREEN);
                work.Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            }

            Native.RECT current;
            if (!Native.GetWindowRect(_hwnd, out current)) return;
            int width = current.Right - current.Left;
            int height = current.Bottom - current.Top;
            int x = work.Left + (work.Right - work.Left - width) / 2;
            int y = work.Top + (work.Bottom - work.Top - height) / 2;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void Apply()
        {
            string three = Selected(_comboThree, ThreeValues, _config.ThreeFingerAction);
            string down = Selected(_comboDown, FourValues, _config.FourFingerDownAction);
            string up = Selected(_comboUp, FourValues, _config.FourFingerUpAction);
            string horizontal = Selected(_comboHorizontal, HorizontalValues, _config.FourFingerHorizontalAction);
            string edge = Selected(_comboEdge, EdgeValues, _config.EdgeSwipeAction);

            Log.Info(string.Format("Settings: three={0} down={1} up={2} horizontal={3} edge={4}",
                three, down, up, horizontal, edge));
            if (_save != null) _save(three, down, up, horizontal, edge);
            Close();
        }

        IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (message)
                {
                    case WM_ERASEBKGND:
                        if (_background != IntPtr.Zero)
                        {
                            Native.RECT client;
                            Native.GetClientRect(hwnd, out client);
                            Native.FillRect(wParam, ref client, _background);
                            return new IntPtr(1);
                        }
                        break;
                    case WM_CTLCOLORSTATIC:
                        if (_background != IntPtr.Zero)
                        {
                            Native.SetBkMode(wParam, 1); // TRANSPARENT
                            return _background;
                        }
                        break;
                    case WM_COMMAND:
                        {
                            int id = wParam.ToInt32() & 0xFFFF;
                            if (id == IdOk) { Apply(); return IntPtr.Zero; }
                            if (id == IdCancel) { Close(); return IntPtr.Zero; }
                        }
                        break;
                    case WM_CLOSE:
                        Close();
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Settings window failed: " + ex.Message);
            }
            return Native.CallWindowProcW(_oldProc, hwnd, message, wParam, lParam);
        }

        public bool IsDialogMessage(ref Native.MSG message)
        {
            if (_hwnd == IntPtr.Zero) return false;
            if (message.hwnd != _hwnd && !Native.IsChild(_hwnd, message.hwnd)) return false;
            return Native.IsDialogMessageW(_hwnd, ref message);
        }

        public void Close()
        {
            if (_disposed) return;
            _disposed = true;

            IntPtr hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                if (_oldProc != IntPtr.Zero) Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, _oldProc);
                Native.DestroyWindow(hwnd);
            }
            if (_font != IntPtr.Zero) Native.DeleteObject(_font);
            if (_background != IntPtr.Zero) Native.DeleteObject(_background);
            _font = IntPtr.Zero;
            _background = IntPtr.Zero;
            _comboThree = _comboDown = _comboUp = _comboHorizontal = _comboEdge = IntPtr.Zero;

            Log.Info("Settings dialog closed.");
            if (_closed != null) _closed(this);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
