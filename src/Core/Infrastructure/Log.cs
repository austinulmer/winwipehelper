using System;
using System.IO;
using System.Text;

namespace WinWipeHelper
{
    /// <summary>One log file per action, e.g. logs\Install-2026-11-21_031500.log.</summary>
    public sealed class Log : IDisposable
    {
        readonly object _lock = new object();
        StreamWriter _w;
        public string FilePath { get; }

        /// <summary>Raised for every line (on the writing thread). UIs marshal it themselves.</summary>
        public event Action<string> Line;

        Log(string path)
        {
            FilePath = path;
            try
            {
                _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { _w = null; }
        }

        public static Log Start(string action)
        {
            string path;
            try { path = Path.Combine(Paths.LogDir, $"{Paths.SafeFileName(action)}-{Paths.Timestamp()}.log"); }
            catch { path = Path.Combine(Path.GetTempPath(), $"WinWipeHelper-{Paths.SafeFileName(action)}-{Paths.Timestamp()}.log"); }
            var log = new Log(path);
            log.Write($"{Edition.ProductName} {typeof(Log).Assembly.GetName().Version} - {action}");
            log.Write($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}, elevated: {Elevation.IsElevated}");
            return log;
        }

        public void Write(string text)
        {
            if (text == null) return;
            lock (_lock)
            {
                foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    try { _w?.WriteLine($"{DateTime.Now:HH:mm:ss}  {raw}"); } catch { }
                }
            }
            try { Line?.Invoke(text); } catch { }
        }

        public void Error(string text) => Write("ERROR: " + text);

        public void Dispose()
        {
            lock (_lock) { try { _w?.Dispose(); } catch { } _w = null; }
        }
    }
}
