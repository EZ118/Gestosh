using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Gestosh
{
    internal static class ConsoleHost
    {
        static bool _initialized;

        public static void Ensure()
        {
            if (_initialized) return;
            _initialized = true;
#if !CONSOLE_BUILD
            try
            {
                bool attached = Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
                if (!attached) attached = Native.AllocConsole();
                if (attached)
                {
                    OpenStreams();
                    try { Native.SetConsoleTitleW("Gestosh diagnostics"); } catch { }
                }
            }
            catch { }
#else
            try { Native.SetConsoleTitleW("Gestosh diagnostics"); } catch { }
#endif
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        }

        static void OpenStreams()
        {
            IntPtr output = Native.CreateFileW("CONOUT$",
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (output != new IntPtr(-1))
            {
                var stream = new FileStream(new SafeFileHandle(output, true), FileAccess.Write, 4096, false);
                var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.AutoFlush = true;
                Console.SetOut(writer);
                Console.SetError(writer);
            }
            IntPtr input = Native.CreateFileW("CONIN$",
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (input != new IntPtr(-1))
            {
                var stream = new FileStream(new SafeFileHandle(input, true), FileAccess.Read, 4096, false);
                Console.SetIn(new StreamReader(stream, Encoding.UTF8));
            }
        }

        public static void WriteLine(string text)
        {
            try { Console.WriteLine(text); } catch { }
        }

        public static void WriteLine()
        {
            try { Console.WriteLine(); } catch { }
        }

        public static void ShowHelpFallback()
        {
            try
            {
                Native.MessageBoxW(IntPtr.Zero, CommandLine.HelpText, "Gestosh", Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_TOPMOST);
            }
            catch { }
        }
    }
}
