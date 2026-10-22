using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Gestosh
{
    internal sealed class TrayIcon
    {
        public const uint CallbackMessage = Native.WM_APP + 1;
        const int IconId = 1;
        const int NIIF_INFO = 1;
        const int NIIF_WARNING = 2;
        const int NIIF_ERROR = 3;

        readonly App _app;
        Native.NOTIFYICONDATA _data;
        IntPtr _icon;
        bool _ownsIcon;
        bool _added;
        bool _warnedFailure;

        public TrayIcon(App app)
        {
            _app = app;
        }

        public bool Added { get { return _added; } }

        public bool Add()
        {
            if (_added) return true;
            if (_app.Hwnd == IntPtr.Zero) return false;

            if (_icon == IntPtr.Zero) LoadIcon();
            _data = new Native.NOTIFYICONDATA();
            _data.cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
            _data.hWnd = _app.Hwnd;
            _data.uID = IconId;
            _data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
            _data.uCallbackMessage = CallbackMessage;
            _data.hIcon = _icon;
            _data.szTip = "Gestosh - 三指拖拽 / 五指捏合";
            _data.szInfo = "";
            _data.szInfoTitle = "";
            _data.uVersion = 0;

            if (!Native.Shell_NotifyIconW(Native.NIM_ADD, ref _data))
            {
                if (!_warnedFailure)
                {
                    _warnedFailure = true;
                    Log.Warn("Shell_NotifyIcon(NIM_ADD) failed. Win32=" + Marshal.GetLastWin32Error());
                }
                return false;
            }
            _warnedFailure = false;
            _data.uVersion = Native.NOTIFYICON_VERSION_4;
            Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref _data);
            _added = true;
            Log.Info("Tray icon added.");
            return true;
        }

        public void Remove()
        {
            if (!_added) return;
            var data = _data;
            data.uFlags = 0;
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
            _added = false;
            if (_icon != IntPtr.Zero)
            {
                if (_ownsIcon) Native.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
                _ownsIcon = false;
            }
        }

        public void ReAdd()
        {
            _added = false;
            Add();
        }

        void LoadIcon()
        {
            IntPtr large = IntPtr.Zero;
            IntPtr small = IntPtr.Zero;
            try
            {
                uint count = Native.ExtractIconExW(_app.ExePath, 0, out large, out small, 1);
                if (count > 0 && small != IntPtr.Zero)
                {
                    _icon = small;
                    _ownsIcon = true;
                }
                if (large != IntPtr.Zero) Native.DestroyIcon(large);
            }
            catch { }
            if (_icon == IntPtr.Zero)
            {
                _icon = Native.LoadIconW(IntPtr.Zero, new IntPtr(Native.IDI_APPLICATION));
                _ownsIcon = false;
            }
        }

        public void ShowMenu()
        {
            if (_app.Hwnd == IntPtr.Zero) return;
            Native.POINT point;
            if (!Native.GetCursorPos(out point)) return;
            IntPtr menu = _app.BuildTrayMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                Native.SetForegroundWindow(_app.Hwnd);
                int command = Native.TrackPopupMenuEx(menu,
                    Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN,
                    point.X, point.Y, _app.Hwnd, IntPtr.Zero);
                if (command != 0) _app.HandleMenu(command);
                Native.PostMessageW(_app.Hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Native.DestroyMenu(menu);
            }
        }

        public void HandleCallback(IntPtr lParam)
        {
            int message = (int)(lParam.ToInt64() & 0xFFFF);
            if (message == Native.WM_CONTEXTMENU || message == Native.WM_RBUTTONUP ||
                message == Native.NIN_SELECT || message == Native.NIN_KEYSELECT ||
                message == Native.WM_LBUTTONUP)
            {
                ShowMenu();
            }
        }

        public void ShowBalloon(string title, string text, bool warning)
        {
            if (!_added) return;
            var data = _data;
            data.uFlags = Native.NIF_INFO;
            data.szInfo = Truncate(text, 255);
            data.szInfoTitle = Truncate(title, 63);
            data.uVersion = 6000;
            data.dwInfoFlags = (uint)(warning ? NIIF_WARNING : NIIF_INFO);
            Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
        }

        static string Truncate(string text, int max)
        {
            if (text == null) return "";
            return text.Length <= max ? text : text.Substring(0, max);
        }
    }
}
