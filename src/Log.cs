using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Gestosh
{
    internal static class Log
    {
        static readonly object Gate = new object();
        static StreamWriter _writer;
        static string _path = "";
        static bool _console;
        static bool _verbose;
        static long _written;

        public static string Path { get { return _path; } }
        public static bool Verbose { get { return _verbose; } }

        public static void Init(string path, bool console, bool verbose)
        {
            lock (Gate)
            {
                _path = path;
                _console = console;
                _verbose = verbose;
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        if (info.Length > 2 * 1024 * 1024)
                        {
                            string old = path + ".1";
                            try { if (File.Exists(old)) File.Delete(old); File.Move(path, old); }
                            catch { }
                        }
                    }
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(true));
                    _writer.AutoFlush = true;
                    _written = stream.Length;
                }
                catch
                {
                    _writer = null;
                }
            }
        }

        public static void Info(string message) { Write("I", message); }
        public static void Warn(string message) { Write("W", message); }
        public static void Error(string message) { Write("E", message); }
        public static void Debug(string message) { if (_verbose) Write("D", message); }

        public static void Error(string message, Exception ex)
        {
            Write("E", message + " " + (ex == null ? "" : ex.ToString()));
        }

        static void Write(string level, string message)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}", DateTime.Now, level, message);
            lock (Gate)
            {
                if (_writer != null)
                {
                    try
                    {
                        _writer.WriteLine(line);
                        _written += line.Length + 2;
                        if (_written > 2 * 1024 * 1024)
                        {
                            // Rotate lazily. The new file is opened on the next write.
                            try
                            {
                                _writer.Dispose();
                                _writer = null;
                                string old = _path + ".1";
                                if (File.Exists(old)) File.Delete(old);
                                if (File.Exists(_path)) File.Move(_path, old);
                                var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                                _writer = new StreamWriter(stream, new UTF8Encoding(true));
                                _writer.AutoFlush = true;
                                _written = 0;
                            }
                            catch { _writer = null; }
                        }
                    }
                    catch { }
                }
                if (_console)
                {
                    try { Console.WriteLine(line); } catch { }
                }
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    try { _writer.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }
    }
}
