using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Gestosh
{
    internal static class Program
    {
        internal static uint CommandMessage;

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {
                try { Native.SetProcessDPIAware(); } catch { }
            }

            CommandLine command = CommandLine.Parse(args);
            string exePath = "";
            try { exePath = Process.GetCurrentProcess().MainModule.FileName; }
            catch { exePath = System.Reflection.Assembly.GetExecutingAssembly().Location; }

            string configPath = string.IsNullOrEmpty(command.ConfigPath)
                ? CommandLine.FindConfigPath(exePath) : Path.GetFullPath(command.ConfigPath);
            string logPath = string.IsNullOrEmpty(command.LogPath) ? CommandLine.DefaultLogPath() : Path.GetFullPath(command.LogPath);

            if (!string.IsNullOrEmpty(command.Error))
            {
                ConsoleHost.Ensure();
                ConsoleHost.WriteLine(command.Error);
                ConsoleHost.WriteLine("");
                ConsoleHost.WriteLine(CommandLine.HelpText);
                return 2;
            }
            if (command.Help)
            {
                ConsoleHost.Ensure();
                ConsoleHost.WriteLine(CommandLine.HelpText);
                return 0;
            }

            if (command.SelfTest)
            {
                ConsoleHost.Ensure();
                Log.Init(logPath, false, true);
                return SelfTest.Run(command.SelfTestPath);
            }

            if (command.Diagnose)
            {
                ConsoleHost.Ensure();
                Log.Init(logPath, false, true);
                return Diagnostics.Run(command, configPath, exePath);
            }

            // A normal desktop launch is medium integrity.  A launch from a chat
            // file card or a sandboxed Explorer window is low integrity; the shell
            // rejects the tray icon, HKCU writes and often raw HID input.  Warn
            // instead of leaving an invisible, useless background process.
            if (Native.IsLowIntegrity() && string.IsNullOrEmpty(command.LogPath))
            {
                string dir = "";
                try { dir = Path.GetDirectoryName(exePath); } catch { }
                string text =
                    "Gestosh 正在以受限（低完整性）权限运行，无法使用托盘、触摸板原始输入和系统手势设置。\r\n\r\n" +
                    "这通常是因为从聊天窗口的文件卡片、沙箱资源管理器或受限终端启动。\r\n" +
                    "请关闭这个后台进程，按 Win+E 打开正常的 Windows 文件资源管理器，进入：\r\n" +
                    dir + "\r\n" +
                    "然后双击 Gestosh.exe。\r\n\r\n" +
                    "如果仍出现此提示，请在普通 PowerShell 中运行：\r\n" +
                    "Gestosh.Debug.exe --diagnose 30";
                Native.MessageBoxW(IntPtr.Zero, text, "Gestosh", Native.MB_OK | Native.MB_ICONWARNING | Native.MB_TOPMOST);
                return 3;
            }

            Log.Init(logPath, false, command.Verbose);
            Program.CommandMessage = Native.RegisterWindowMessageW("Gestosh.Command.v1");
            Log.Info("Gestosh starting. PID=" + Process.GetCurrentProcess().Id + " exe=" + exePath);
            Log.Info("Config: " + configPath);
            Log.Info("Log: " + logPath);

            IntPtr existing = Native.FindWindowW(null, App.WindowTitle);
            if (existing != IntPtr.Zero)
            {
                if (command.Action != 0)
                {
                    if (Program.CommandMessage != 0)
                    {
                        Native.PostMessageW(existing, Program.CommandMessage, new IntPtr(command.Action), IntPtr.Zero);
                        Log.Info("Sent command " + command.ActionName + " to the running instance.");
                        return 0;
                    }
                }
                Log.Info("Gestosh is already running.");
                return 0;
            }

            if (command.Action != 0)
            {
                ConsoleHost.Ensure();
                ConsoleHost.WriteLine("Gestosh is not running; start Gestosh.exe first.");
                Log.Warn("Command '" + command.ActionName + "' ignored: no running instance.");
                return 2;
            }

            bool created;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(true, "Local\\Gestosh.SingleInstance.v1", out created);
            }
            catch (Exception ex)
            {
                Log.Error("Cannot create the single-instance mutex.", ex);
                return 1;
            }
            if (!created)
            {
                Log.Warn("Another Gestosh instance already owns the mutex.");
                return 2;
            }

            App app = new App(command, configPath, logPath, exePath);
            try
            {
                if (!app.Initialize())
                {
                    app.Exit();
                    return 1;
                }
                app.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Fatal error in the main loop.", ex);
                try { app.Exit(); } catch { }
                return 1;
            }
            finally
            {
                if (created)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex.Dispose();
                Log.Shutdown();
            }
        }
    }

    internal sealed class App
    {
        public const string WindowTitle = "Gestosh.Hidden.Window.v1";
        public const int MenuDock = 101;
        public const int MenuRestore = 102;
        public const int MenuToggle = 103;
        public const int MenuEnabled = 104;
        public const int MenuReload = 105;
        public const int MenuConfig = 106;
        public const int MenuLog = 107;
        public const int MenuRestoreGesture = 108;
        public const int MenuStatus = 109;
        public const int MenuExpose = 110;
        public const int MenuExit = 111;
        public const int MenuSettings = 112;

        public readonly Config Config;
        public readonly string ConfigPath;
        public readonly string LogPath;
        public readonly string ExePath;
        public IntPtr Hwnd;
        public bool Enabled;
        public bool Exiting;

        public TrayIcon Tray;
        public TouchpadManager Touchpads;
        public GestureEngine Gestures;
        public MouseDragger Dragger;
        public DockManager Dock;
        public ExposeOverlay Expose;
        public DesktopSwipeOverlay DesktopSwipe;
        public SettingsDialog Settings;
        IntPtr _desktopSwipeForeground;
        bool _gestureExpose;
        public VirtualDesktop VirtualDesktop;
        public SystemGestures SystemGestures;

        readonly CommandLine _command;
        Native.WndProc _wndProc;
        IntPtr _oldWndProc;
        uint _taskbarCreatedMessage;
        bool _hotkeysRegistered;
        bool _loggedFirstFrame;
        bool _dockClickWatch;
        IntPtr _mouseHook;
        Native.LowLevelMouseProc _mouseHookProc;
        IntPtr _keyboardHook;
        Native.LowLevelKeyboardProc _keyboardHookProc;
        bool _winDown;

        const int TimerMaintenance = 1;

        public App(CommandLine command, string configPath, string logPath, string exePath)
        {
            _command = command;
            ConfigPath = configPath;
            LogPath = logPath;
            ExePath = exePath;
            try { Config = Config.Load(configPath); }
            catch (Exception ex)
            {
                Log.Warn("Cannot load configuration; using defaults: " + ex.Message);
                Config = new Config();
            }
        }

        public bool Initialize()
        {
            if (!File.Exists(ConfigPath))
            {
                try
                {
                    Config.WriteTemplate(ConfigPath);
                    Config.CopyFrom(Config.Load(ConfigPath));
                }
                catch (Exception ex)
                {
                    Log.Warn("Cannot write default configuration: " + ex.Message);
                }
            }
            Enabled = Config.Enabled;

            IntPtr instance = Native.GetModuleHandleW(null);
            Hwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, "STATIC", WindowTitle, Native.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero)
            {
                Log.Error("Could not create the hidden message window. Win32=" + Marshal.GetLastWin32Error());
                return false;
            }

            _wndProc = new Native.WndProc(WindowProcedure);
            _oldWndProc = Native.SetWindowLongPtr(Hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
            if (_oldWndProc == IntPtr.Zero)
                Log.Warn("SetWindowLongPtr(GWLP_WNDPROC) returned NULL; messages may not be handled.");
            _taskbarCreatedMessage = Native.RegisterWindowMessageW("TaskbarCreated");

            VirtualDesktop = new VirtualDesktop();
            Touchpads = new TouchpadManager();
            bool found = Touchpads.Discover();
            Touchpads.Frame += OnTouchpadFrame;
            bool registered = Touchpads.Register(Hwnd);
            if (!registered)
                Log.Warn("Raw input registration failed; gestures will not work.");

            Dragger = new MouseDragger(Config);
            Gestures = new GestureEngine(Config, new AppGestureSink(this));
            Dock = new DockManager(this);
            Dock.StateChanged += UpdateDockClickWatch;
            SystemGestures = new SystemGestures(Config);
            SystemGestures.TakeOver();
            ExposeOverlay.Prepare();

            Tray = new TrayIcon(this);
            if (Config.ShowTrayIcon && !_command.NoTray) Tray.Add();
            RegisterHotkeys();
            Native.SetTimer(Hwnd, new IntPtr(TimerMaintenance), 5000, IntPtr.Zero);

            string build = "";
            try { build = " (" + File.GetLastWriteTime(ExePath).ToString("MM-dd HH:mm") + ")"; }
            catch { }
            Log.Info("Gestosh " + typeof(App).Assembly.GetName().Version + build +
                " initialized. Touchpad found=" + found +
                ", tray=" + Tray.Added + ", desktopSwipe=" + (Config.DesktopSwipe ? "on" : "off"));
            if (!found)
            {
                if (Tray.Added)
                    Tray.ShowBalloon("Gestosh", "未检测到 Windows Precision Touchpad。请运行 Gestosh.Debug.exe --diagnose 查看设备。", true);
            }
            return true;
        }

        public void Run()
        {
            Native.MSG message;
            while (Native.GetMessageW(out message, IntPtr.Zero, 0, 0) > 0)
            {
                if (Settings != null && Settings.IsDialogMessage(ref message)) continue;
                Native.TranslateMessage(ref message);
                Native.DispatchMessageW(ref message);
            }
            if (!Exiting) Exit();
        }

        IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (message == Native.WM_INPUT)
                {
                    if (!Exiting && Touchpads != null) Touchpads.HandleRawInput(lParam);
                    return IntPtr.Zero;
                }
                if (message == Native.WM_HOTKEY)
                {
                    HandleHotkey(wParam.ToInt32());
                    return IntPtr.Zero;
                }
                if (message == TrayIcon.CallbackMessage)
                {
                    if (Tray != null) Tray.HandleCallback(lParam);
                    return IntPtr.Zero;
                }
                if (message == (uint)DockManager.WM_ANIM_DONE)
                {
                    if (Dock != null) Dock.OnAnimationComplete(wParam.ToInt32());
                    return IntPtr.Zero;
                }
                if (message == DockManager.WM_DOCK_CLICK)
                {
                    StopDockClickWatch();
                    if (Dock != null)
                    {
                        if (wParam != IntPtr.Zero) Native.SetForegroundWindow(wParam);
                        Dock.RequestRestore();
                    }
                    return IntPtr.Zero;
                }
                if (message == Native.WM_TIMER)
                {
                    Maintenance();
                    return IntPtr.Zero;
                }
                if (message == Native.WM_ENDSESSION)
                {
                    if (wParam != IntPtr.Zero) Exit();
                    return IntPtr.Zero;
                }
                if (message == Native.WM_QUERYENDSESSION) return new IntPtr(1);
                if (message == Native.WM_CLOSE)
                {
                    Exit();
                    return IntPtr.Zero;
                }
                if (message == Native.WM_DESTROY)
                {
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
                }
                if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
                {
                    if (Config.ShowTrayIcon && Tray != null) Tray.ReAdd();
                    return IntPtr.Zero;
                }
                if (Program.CommandMessage != 0 && message == Program.CommandMessage)
                {
                    HandleCommand(wParam.ToInt32());
                    return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Window procedure error.", ex);
            }
            if (_oldWndProc != IntPtr.Zero)
                return Native.CallWindowProcW(_oldWndProc, hwnd, message, wParam, lParam);
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        void HandleCommand(int command)
        {
            switch (command)
            {
                case 1: if (Dock != null) Dock.RequestDock(); break;
                case 2: if (Dock != null) Dock.RequestRestore(); break;
                case 3: if (Dock != null) Dock.Toggle(); break;
                case 4: Exit(); break;
                case 5: ReloadConfiguration(); break;
                case 6: ShowExpose(); break;
            }
        }

        void HandleHotkey(int id)
        {
            switch (id)
            {
                case 1: Dock.RequestDock(); break;
                case 2: Dock.RequestRestore(); break;
                case 3: Dock.Toggle(); break;
            }
        }

        void OnTouchpadFrame(TouchpadFrame frame)
        {
            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                Log.Info("First touchpad frame: reportLength=" + (frame.Raw == null ? 0 : frame.Raw.Length) +
                    " contactCount=" + frame.ContactCount + " active=" + frame.Active.Count + " raw=" + frame.RawHex);
            }
            if (Exiting || !Enabled || Gestures == null) return;
            Gestures.Feed(frame.Active, frame.TimeMs);
        }

        void UpdateDockClickWatch()
        {
            if (Dock == null || !Dock.IsDocked || Hwnd == IntPtr.Zero)
            {
                StopDockClickWatch();
                return;
            }
            StartDockClickWatch();
        }

        void StartDockClickWatch()
        {
            if (_dockClickWatch || Hwnd == IntPtr.Zero) return;
            _dockClickWatch = true;
            _winDown = false;

            _mouseHookProc = MouseHook;
            _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseHookProc,
                Native.GetModuleHandleW(null), 0);
            if (_mouseHook != IntPtr.Zero) Log.Info("Dock click hook installed.");
            else Log.Warn("Could not install the dock click hook; click-to-restore is unavailable.");

            _keyboardHookProc = KeyboardHook;
            _keyboardHook = Native.SetWindowsHookExKeyboard(Native.WH_KEYBOARD_LL, _keyboardHookProc,
                Native.GetModuleHandleW(null), 0);
            if (_keyboardHook != IntPtr.Zero) Log.Info("Dock Win+D hook installed.");
            else Log.Warn("Could not install the dock Win+D hook.");
        }

        void StopDockClickWatch()
        {
            _dockClickWatch = false;
            if (_mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            _winDown = false;
        }

        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0)
                {
                    var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    int message = wParam.ToInt32();
                    bool down = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
                    bool up = message == Native.WM_KEYUP || message == Native.WM_SYSKEYUP;
                    if (info.vkCode == Native.VK_LWIN || info.vkCode == Native.VK_RWIN)
                    {
                        if (down) _winDown = true;
                        else if (up) _winDown = false;
                    }
                    else if (down && info.vkCode == Native.VK_D &&
                        (_winDown ||
                         (Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 ||
                         (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0) &&
                        Dock != null && Dock.IsDocked)
                    {
                        // Let Win+D continue to Explorer, but first put our docked
                        // windows back at their real positions.  Otherwise Explorer
                        // minimizes them while they are off-screen and their restored
                        // positions become the docked positions forever.
                        Dock.FinalizeForExternalChange();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Dock Win+D hook failed: " + ex.Message);
            }
            return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && wParam.ToInt32() == Native.WM_LBUTTONDOWN &&
                    _mouseHook != IntPtr.Zero && Dock != null && Dock.IsDocked)
                {
                    var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    IntPtr hit = Native.WindowFromPoint(info.pt);
                    IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, (uint)Native.GA_ROOT);
                    if (root == IntPtr.Zero) root = hit;
                    if (root != IntPtr.Zero && Dock.ContainsWindow(root))
                    {
                        Native.PostMessageW(Hwnd, DockManager.WM_DOCK_CLICK, root, IntPtr.Zero);
                        return new IntPtr(1);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Dock click hook failed: " + ex.Message);
            }
            return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        void Maintenance()
        {
            if (Exiting || Touchpads == null) return;
            if (Touchpads.Devices.Count == 0)
            {
                if (Touchpads.Discover())
                {
                    Touchpads.Register(Hwnd);
                    Log.Info("Precision touchpad appeared.");
                    if (Tray != null && Tray.Added)
                        Tray.ShowBalloon("Gestosh", "已检测到精密触摸板。", false);
                }
            }
            else if (!Touchpads.Registered)
            {
                Touchpads.Register(Hwnd);
            }
            if (Config.ShowTrayIcon && !_command.NoTray && Tray != null && !Tray.Added)
                Tray.Add();
        }

        public void ToggleEnabled()
        {
            Enabled = !Enabled;
            Config.Enabled = Enabled;
            if (!Enabled)
            {
                if (Dragger != null) Dragger.End();
                if (Gestures != null) Gestures.Reset();
            }
            Log.Info("Gestures " + (Enabled ? "enabled." : "paused."));
        }

        public void ReloadConfiguration()
        {
            ReloadConfiguration(false);
        }

        public void ReloadConfiguration(bool quiet)
        {
            try
            {
                if (!File.Exists(ConfigPath)) Config.WriteTemplate(ConfigPath);
                var loaded = Config.Load(ConfigPath);
                Config.CopyFrom(loaded);
                Enabled = Config.Enabled;
                if (Gestures != null) Gestures.Reset();
                if (Dragger != null) Dragger.End();
                RegisterHotkeys();
                if (SystemGestures != null)
                {
                    if (Config.ManageThreeFingerGesture) SystemGestures.TakeOver();
                    else SystemGestures.Restore();
                }
                if (Config.ShowTrayIcon && !_command.NoTray)
                {
                    if (Tray != null) Tray.Add();
                }
                else if (Tray != null) Tray.Remove();
                Log.Info("Configuration reloaded.");
                if (!quiet && Tray != null && Tray.Added) Tray.ShowBalloon("Gestosh", "配置已重新加载。", false);
            }
            catch (Exception ex)
            {
                Log.Error("Reload configuration failed.", ex);
                if (Tray != null && Tray.Added) Tray.ShowBalloon("Gestosh", "配置加载失败，详见日志。", true);
            }
        }

        public IntPtr BuildTrayMenu()
        {
            IntPtr menu = Native.CreatePopupMenu();
            if (menu == IntPtr.Zero) return IntPtr.Zero;
            bool docked = Dock != null && Dock.IsDocked;
            bool busy = Dock != null && Dock.IsBusy;
            Append(menu, MenuDock, "贴边显示桌面  (Ctrl+Alt+Shift+D)", docked ? Native.MF_GRAYED : 0);
            Append(menu, MenuRestore, "恢复窗口  (Ctrl+Alt+Shift+R)", busy || !docked ? Native.MF_GRAYED : 0);
            Append(menu, MenuToggle, "切换贴边/恢复  (Ctrl+Alt+Shift+T)", 0);
            Append(menu, MenuExpose, "显示所有窗口 (App Expose)", 0);
            Append(menu, MenuSettings, "手势设置...", 0);
            Append(menu, 0, null, 0);
            Append(menu, MenuEnabled, "启用手势", Enabled ? Native.MF_CHECKED : 0);
            Append(menu, MenuReload, "重新加载配置", 0);
            Append(menu, MenuConfig, "打开配置文件", 0);
            Append(menu, MenuLog, "打开日志文件", 0);
            Append(menu, 0, null, 0);
            Append(menu, MenuRestoreGesture, "恢复 Windows 触摸板手势", 0);
            Append(menu, MenuStatus, "诊断信息...", 0);
            Append(menu, 0, null, 0);
            Append(menu, MenuExit, "退出", 0);
            return menu;
        }

        static void Append(IntPtr menu, int id, string text, uint extraFlags)
        {
            if (id == 0) Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
            else Native.AppendMenuW(menu, Native.MF_STRING | extraFlags, new IntPtr(id), text);
        }

        public void HandleMenu(int id)
        {
            switch (id)
            {
                case MenuDock: Dock.RequestDock(); break;
                case MenuRestore: Dock.RequestRestore(); break;
                case MenuToggle: Dock.Toggle(); break;
                case MenuExpose: ShowExpose(); break;
                case MenuSettings: OpenSettings(); break;
                case MenuEnabled: ToggleEnabled(); break;
                case MenuReload: ReloadConfiguration(); break;
                case MenuConfig: OpenPath(ConfigPath, true); break;
                case MenuLog: OpenPath(LogPath, false); break;
                case MenuRestoreGesture: RestoreWindowsGesture(); break;
                case MenuStatus: ShowStatus(); break;
                case MenuExit: Exit(); break;
            }
        }

        void RestoreWindowsGesture()
        {
            if (SystemGestures == null) return;
            SystemGestures.RestoreNow();
            if (Tray != null && Tray.Added)
                Tray.ShowBalloon("Gestosh", "已恢复 Windows 触摸板手势。三/四指手势可能与系统冲突，重启 Gestosh 可再次接管。", false);
        }

        void OpenPath(string path, bool isConfig)
        {
            try
            {
                if (isConfig && !File.Exists(path)) Config.WriteTemplate(path);
                if (!File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, "");
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Cannot open " + path + ": " + ex.Message);
            }
        }

        public void ShowExpose()
        {
            if (Exiting) return;
            if (Expose != null)
            {
                CloseExpose();
                return;
            }
            if (Dock != null) Dock.FinalizeForExternalChange();
            var overlay = new ExposeOverlay(this);
            try
            {
                if (!overlay.Show(0, true))
                {
                    overlay.Dispose();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Cannot open App Expose: " + ex.Message);
                overlay.Dispose();
                return;
            }
            Expose = overlay;
            _gestureExpose = false;
        }

        public void BeginExposeGesture()
        {
            if (Exiting || Expose != null) return;
            if (Dock != null) Dock.FinalizeForExternalChange();
            var overlay = new ExposeOverlay(this);
            try
            {
                if (!overlay.Show(0, false))
                {
                    overlay.Dispose();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Cannot open App Expose: " + ex.Message);
                overlay.Dispose();
                return;
            }
            Expose = overlay;
            _gestureExpose = true;
        }

        public void UpdateExposeGesture(double progress)
        {
            if (_gestureExpose && Expose != null) Expose.SetGestureProgress(progress);
        }

        public void EndExposeGesture(bool commit)
        {
            if (!_gestureExpose) return;
            _gestureExpose = false;
            if (Expose != null) Expose.EndGesture(commit);
        }

        public void CloseExpose()
        {
            _gestureExpose = false;
            if (Expose != null) Expose.Close(false);
            Expose = null;
        }

        internal void ExposeClosed(ExposeOverlay overlay)
        {
            if (ReferenceEquals(Expose, overlay))
            {
                Expose = null;
                _gestureExpose = false;
            }
        }

        /// <summary>
        /// Starts the finger-following desktop slide. The overlay captures the current
        /// desktop; the desktop itself changes only if the gesture commits.
        /// </summary>
        public bool BeginDesktopSwipe(int direction)
        {
            if (Exiting || !Enabled)
            {
                Log.Debug("Desktop slide skipped: gestures disabled or exiting.");
                return false;
            }
            if (Expose != null)
            {
                Log.Debug("Desktop slide skipped: App Expose is open.");
                return false;
            }
            if (DesktopSwipe != null)
            {
                Log.Debug("Desktop slide skipped: another slide is still finishing.");
                return false;
            }
            if (Dock != null && Dock.IsDocked)
            {
                Log.Debug("Desktop slide skipped: windows are docked.");
                return false;
            }

            _desktopSwipeForeground = Native.GetForegroundWindow();
            var overlay = new DesktopSwipeOverlay(Config, direction, DesktopSwipeSwitch, DesktopSwipeClosed);
            try
            {
                if (!overlay.Show())
                {
                    overlay.Dispose();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Cannot start the desktop slide: " + ex.Message);
                overlay.Dispose();
                return false;
            }
            DesktopSwipe = overlay;
            return true;
        }

        public void UpdateDesktopSwipe(double dxNormalized)
        {
            if (DesktopSwipe != null) DesktopSwipe.Follow(dxNormalized);
        }

        public void EndDesktopSwipe(double dxNormalized, double elapsedMs)
        {
            if (DesktopSwipe != null) DesktopSwipe.Settle(dxNormalized, elapsedMs);
        }

        public void CloseDesktopSwipe()
        {
            if (DesktopSwipe != null) DesktopSwipe.Close();
        }

        public void OpenSettings()
        {
            if (Exiting) return;
            if (Settings != null && Settings.IsOpen)
            {
                Native.SetForegroundWindow(Settings.Hwnd);
                return;
            }
            var dialog = new SettingsDialog(Config, SaveGestureSettings, SettingsClosed);
            try
            {
                if (!dialog.Show(Hwnd))
                {
                    dialog.Dispose();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Cannot open the settings dialog: " + ex.Message);
                dialog.Dispose();
                return;
            }
            Settings = dialog;
        }

        void SettingsClosed(SettingsDialog dialog)
        {
            if (ReferenceEquals(Settings, dialog)) Settings = null;
        }

        void SaveGestureSettings(string three, string down, string up, string horizontal, string edge)
        {
            try
            {
                Config.SaveGestureActions(ConfigPath, three, down, up, horizontal, edge);
                ReloadConfiguration(true);
                if (Tray != null && Tray.Added)
                    Tray.ShowBalloon("Gestosh", "手势设置已保存并生效。", false);
            }
            catch (Exception ex)
            {
                Log.Warn("Cannot save the gesture settings: " + ex.Message);
                if (Tray != null && Tray.Added)
                    Tray.ShowBalloon("Gestosh", "保存手势设置失败，详见日志。", true);
            }
        }

        /// <summary>
        /// Switches the desktop and checks that Windows actually moved. A single
        /// desktop, an elevated foreground window or a blocked key injection leaves
        /// the session in place, and that is worth a log line instead of a silent
        /// no-op after the slide.
        /// </summary>
        void DesktopSwipeSwitch(int direction)
        {
            IntPtr hwnd = DesktopSwipe != null ? DesktopSwipe.Hwnd : IntPtr.Zero;
            Log.Debug(string.Format("Desktop switch: foreground=0x{0:x} overlay=0x{1:x} anchor=0x{2:x}",
                Native.GetForegroundWindow().ToInt64(), hwnd.ToInt64(), _desktopSwipeForeground.ToInt64()));

            DesktopSwitch.Adjacent(direction);
            if (VirtualDesktop == null || !VirtualDesktop.Available) return;
            if (hwnd == IntPtr.Zero) return;

            // A window on the old desktop is a reliable probe only if the virtual
            // desktop manager actually tracks it. Use both the overlay and the window
            // that had focus before the slide, and treat COM errors as "unknown"
            // instead of deciding that the switch failed.
            IntPtr anchor = _desktopSwipeForeground;
            for (int i = 0; i < 24; i++)
            {
                System.Threading.Thread.Sleep(15);
                bool onCurrent;
                if (VirtualDesktop.TryIsOnCurrentDesktop(hwnd, out onCurrent) && !onCurrent)
                {
                    Log.Info("Desktop switched.");
                    return;
                }
                if (anchor != IntPtr.Zero && Native.IsWindow(anchor) &&
                    VirtualDesktop.TryIsOnCurrentDesktop(anchor, out onCurrent) && !onCurrent)
                {
                    Log.Info("Desktop switched.");
                    return;
                }
            }
            Log.Warn("Desktop switch did not take effect (blocked input, or the shell ignored the shortcut).");
        }

        internal void DesktopSwipeClosed(DesktopSwipeOverlay overlay)
        {
            if (ReferenceEquals(DesktopSwipe, overlay)) DesktopSwipe = null;
        }

        void ShowStatus()
        {
            var text = new StringBuilder();
            text.AppendLine("Gestosh 状态");
            text.AppendLine("----------------------------------------");
            text.AppendLine("版本: " + typeof(App).Assembly.GetName().Version);
            text.AppendLine("配置文件: " + ConfigPath);
            text.AppendLine("日志文件: " + LogPath);
            text.AppendLine("手势: " + (Enabled ? "启用" : "暂停"));
            text.AppendLine("贴边状态: " + (Dock != null && Dock.IsDocked ? "已贴边" : "未贴边"));
            text.AppendLine("托盘: " + (Tray != null && Tray.Added ? "已创建" : "不可用（资源管理器未运行？）"));
            text.AppendLine("Windows 触摸板手势接管: " + (SystemGestures != null && SystemGestures.Managed ? "是（退出时恢复）" : "否"));
            text.AppendLine("原始输入: " + (Touchpads != null && Touchpads.Registered ? "已注册" : "未注册"));
            text.AppendLine("报告数: " + (Touchpads != null ? Touchpads.Reports.ToString() : "0"));
            text.AppendLine();
            if (Touchpads != null && Touchpads.Devices.Count > 0)
            {
                foreach (var device in Touchpads.Devices)
                    text.AppendLine(device.Describe());
            }
            else
            {
                text.AppendLine("未检测到 Windows Precision Touchpad（Usage Page 0x0D / Usage 0x05）。");
                text.AppendLine("请确认：");
                text.AppendLine("  1. 笔记本触控板是 Windows 精密触控板；");
                text.AppendLine("  2. 触控板在 Windows 设置中未被禁用；");
                text.AppendLine("  3. 运行 Gestosh.Debug.exe --diagnose 查看 HID 设备列表。");
            }
            Native.MessageBoxW(Hwnd, text.ToString(), "Gestosh", Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_TOPMOST);
        }

        void RegisterHotkeys()
        {
            if (Hwnd == IntPtr.Zero) return;
            UnregisterHotkeys();
            if (!Config.Hotkeys) return;
            bool all = true;
            all &= Native.RegisterHotKey(Hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, 0x44);
            all &= Native.RegisterHotKey(Hwnd, 2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, 0x52);
            all &= Native.RegisterHotKey(Hwnd, 3, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, 0x54);
            _hotkeysRegistered = all;
            if (all) Log.Info("Hotkeys registered: Ctrl+Alt+Shift+D/R/T.");
            else Log.Warn("One or more hotkeys could not be registered (already in use?).");
        }

        void UnregisterHotkeys()
        {
            if (Hwnd == IntPtr.Zero) return;
            Native.UnregisterHotKey(Hwnd, 1);
            Native.UnregisterHotKey(Hwnd, 2);
            Native.UnregisterHotKey(Hwnd, 3);
            _hotkeysRegistered = false;
        }

        public void Exit()
        {
            if (Exiting) return;
            Exiting = true;
            Log.Info("Shutting down.");
            try
            {
                if (Gestures != null) Gestures.Reset();
                if (Dragger != null) Dragger.End();
                if (Settings != null) Settings.Close();
                CloseDesktopSwipe();
                CloseExpose();
                if (Dock != null) Dock.Shutdown();
                if (SystemGestures != null) SystemGestures.Restore();
                if (Tray != null) Tray.Remove();
                UnregisterHotkeys();
                if (Hwnd != IntPtr.Zero)
                {
                    Native.KillTimer(Hwnd, new IntPtr(TimerMaintenance));
                    StopDockClickWatch();
                    if (Touchpads != null) Touchpads.Dispose();
                    if (VirtualDesktop != null) VirtualDesktop.Dispose();
                    Native.DestroyWindow(Hwnd);
                    Hwnd = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error during shutdown.", ex);
            }
            Native.PostQuitMessage(0);
            Log.Info("Stopped.");
            Log.Shutdown();
        }
    }

    internal sealed class AppGestureSink : IGestureSink
    {
        readonly App _app;

        public AppGestureSink(App app)
        {
            _app = app;
        }

        public void BeginDrag()
        {
            if (_app.Enabled && _app.Dragger != null) _app.Dragger.Begin();
        }

        public void DragBy(double dxNormalized, double dyNormalized)
        {
            if (_app.Enabled && _app.Dragger != null) _app.Dragger.Move(dxNormalized, dyNormalized);
        }

        public void EndDrag()
        {
            if (_app.Dragger != null) _app.Dragger.End();
        }

        public void ShowTaskView()
        {
            if (!_app.Enabled || _app.Expose != null) return;
            if (_app.Dock != null && _app.Dock.IsDocked) return;
            _app.CloseDesktopSwipe();
            Log.Info("4-finger swipe up: opening Windows Task View.");
            WindowsTaskView.Toggle();
        }

        public void ShowActionCenter()
        {
            if (!_app.Enabled) return;
            if (_app.Expose != null) _app.CloseExpose();
            _app.CloseDesktopSwipe();
            Log.Info("2-finger right-edge swipe: opening Windows Action Center.");
            Native.keybd_event((byte)Native.VK_LWIN, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            Native.keybd_event(0x41, 0, 0, UIntPtr.Zero); // A
            System.Threading.Thread.Sleep(20);
            Native.keybd_event(0x41, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            System.Threading.Thread.Sleep(10);
            Native.keybd_event((byte)Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public void RequestDock()
        {
            if (_app.Expose != null) _app.CloseExpose();
            _app.CloseDesktopSwipe();
            if (_app.Enabled && _app.Dock != null) _app.Dock.RequestDock();
        }

        public void RequestRestore()
        {
            if (_app.Expose != null) _app.CloseExpose();
            _app.CloseDesktopSwipe();
            if (_app.Enabled && _app.Dock != null) _app.Dock.RequestRestore();
        }

        public bool DesktopSwipeBegin(int direction)
        {
            if (!_app.Enabled) return false;
            return _app.BeginDesktopSwipe(direction);
        }

        public void DesktopSwipeUpdate(double dxNormalized)
        {
            if (_app.Enabled) _app.UpdateDesktopSwipe(dxNormalized);
        }

        public void DesktopSwipeEnd(double dxNormalized, double elapsedMs)
        {
            if (_app.Enabled) _app.EndDesktopSwipe(dxNormalized, elapsedMs);
        }

        public void ExposeBegin()
        {
            if (_app.Enabled) _app.BeginExposeGesture();
        }

        public void ExposeProgress(double progress)
        {
            if (_app.Enabled) _app.UpdateExposeGesture(progress);
        }

        public void ExposeEnd(bool commit)
        {
            if (_app.Enabled) _app.EndExposeGesture(commit);
        }

        public bool IsExposeOpen()
        {
            return _app.Expose != null;
        }

        public void ExposeCloseProgress(double progress)
        {
            if (_app.Expose != null) _app.Expose.SetCloseGestureProgress(progress);
        }

        public void ExposeCloseEnd(bool close)
        {
            if (_app.Expose != null) _app.Expose.EndCloseGesture(close);
        }
    }
}
