using System;
using System.IO;

namespace Gestosh
{
    internal sealed class CommandLine
    {
        public string ConfigPath = "";
        public string LogPath = "";
        public string ActionName = "";
        public int Action = 0;
        public bool Diagnose;
        public int DiagnoseSeconds = 30;
        public bool SelfTest;
        public string SelfTestPath = "";
        public bool NoTray;
        public bool Verbose;
        public bool Help;
        public string Error = "";

        public static CommandLine Parse(string[] args)
        {
            var command = new CommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string lower = arg.ToLowerInvariant();
                if (lower == "--config" || lower == "-config" || lower == "-c")
                {
                    if (++i >= args.Length) { command.Error = "--config requires a path"; break; }
                    command.ConfigPath = args[i];
                }
                else if (lower.StartsWith("--config=")) command.ConfigPath = arg.Substring(9);
                else if (lower == "--log" || lower == "-log")
                {
                    if (++i >= args.Length) { command.Error = "--log requires a path"; break; }
                    command.LogPath = args[i];
                }
                else if (lower.StartsWith("--log=")) command.LogPath = arg.Substring(6);
                else if (lower == "--dock") { command.Action = 1; command.ActionName = "dock"; }
                else if (lower == "--restore") { command.Action = 2; command.ActionName = "restore"; }
                else if (lower == "--toggle") { command.Action = 3; command.ActionName = "toggle"; }
                else if (lower == "--exit" || lower == "--quit") { command.Action = 4; command.ActionName = "exit"; }
                else if (lower == "--reload") { command.Action = 5; command.ActionName = "reload"; }
                else if (lower == "--expose") { command.Action = 6; command.ActionName = "expose"; }
                else if (lower == "--diagnose" || lower == "-diagnose")
                {
                    command.Diagnose = true;
                    if (i + 1 < args.Length && IsNumber(args[i + 1])) { command.DiagnoseSeconds = int.Parse(args[++i]); }
                }
                else if (lower.StartsWith("--diagnose=")) { command.Diagnose = true; int.TryParse(arg.Substring(11), out command.DiagnoseSeconds); }
                else if (lower == "--selftest")
                {
                    command.SelfTest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) command.SelfTestPath = args[++i];
                }
                else if (lower.StartsWith("--selftest=")) { command.SelfTest = true; command.SelfTestPath = arg.Substring(11); }
                else if (lower == "--no-tray") command.NoTray = true;
                else if (lower == "--verbose" || lower == "-v") command.Verbose = true;
                else if (lower == "--help" || lower == "-h" || lower == "/?") command.Help = true;
                else if (lower == "--version") { command.Help = true; }
                else
                {
                    command.Error = "Unknown argument: " + arg;
                    break;
                }
            }
            return command;
        }

        static bool IsNumber(string text)
        {
            int value;
            return int.TryParse(text, out value);
        }

        public static string FindConfigPath(string exePath)
        {
            string local = Path.Combine(Path.GetDirectoryName(exePath), "Gestosh.ini");
            if (File.Exists(local)) return local;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gestosh", "Gestosh.ini");
        }

        public static string DefaultLogPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gestosh", "gestosh.log");
        }

        public static string HelpText
        {
            get
            {
                return "Gestosh - Windows 精密触摸板手势扩展\r\n"
                    + "\r\n"
                    + "用法:\r\n"
                    + "  Gestosh.exe                       启动托盘程序\r\n"
                    + "  Gestosh.exe --dock                让当前桌面的窗口贴边（向运行中的实例发送命令）\r\n"
                    + "  Gestosh.exe --restore             恢复窗口\r\n"
                    + "  Gestosh.exe --toggle              切换贴边/恢复\r\n"
                    + "  Gestosh.exe --exit                退出运行中的实例\r\n"
                    + "  Gestosh.exe --reload              重新加载配置\r\n"
                    + "  Gestosh.exe --expose              打开/关闭 App Expose 窗口预览\r\n"
                    + "  Gestosh.exe --diagnose [seconds]  控制台诊断：设备、报告、手势识别\r\n"
                    + "  Gestosh.exe --selftest [file]     运行内部测试\r\n"
                    + "\r\n"
                    + "选项:\r\n"
                    + "  --config <file>   指定配置文件（默认 %APPDATA%\\Gestosh\\Gestosh.ini）\r\n"
                    + "  --log <file>      指定日志文件\r\n"
                    + "  --no-tray         不创建托盘图标\r\n"
                    + "  --verbose         输出更详细的日志\r\n"
                    + "\r\n"
                    + "手势:\r\n"
                    + "  三指移动           鼠标左键拖拽（任意方向）\r\n"
                    + "  四指向下滑动       打开 App Expose 窗口预览\r\n"
                    + "  四指向上滑动       关闭 App Expose；未打开时打开 Windows Task View\r\n"
                    + "  四指向左/右滑动    渐进切换到上一个/下一个虚拟桌面\r\n"
                    + "  双指右边缘向左滑   打开 Windows 操作中心\r\n"
                    + "  五指由收缩至张开   当前桌面窗口贴边显示桌面\r\n"
                    + "  五指由张开至收缩   恢复窗口原来的位置与大小\r\n"
                    + "\r\n"
                    + "内置快捷键: Ctrl+Alt+Shift+D / R / T（贴边 / 恢复 / 切换）\r\n";
            }
        }
    }
}
