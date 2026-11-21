using System;
using System.IO;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>Where the program lives and where it may write.</summary>
    public static class Paths
    {
        public static readonly string ExePath = Application.ExecutablePath;
        public static readonly string ExeDir = Path.GetDirectoryName(ExePath);

        static string _dataDir;
        /// <summary>The EXE's folder if it's writable, otherwise %LOCALAPPDATA%\WinWipeHelper.</summary>
        public static string DataDir
        {
            get
            {
                if (_dataDir != null) return _dataDir;
                _dataDir = IsWritable(ExeDir) ? ExeDir : LocalAppDataDir;
                Directory.CreateDirectory(_dataDir);
                return _dataDir;
            }
        }

        public static string LocalAppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWipeHelper");

        /// <summary>logs folder next to the EXE, or %LOCALAPPDATA%\WinWipeHelper\logs if the EXE's folder is read-only.</summary>
        public static string LogDir
        {
            get
            {
                var next = Path.Combine(ExeDir, "logs");
                if (IsWritable(ExeDir)) { try { Directory.CreateDirectory(next); if (IsWritable(next)) return next; } catch { } }
                var fallback = Path.Combine(LocalAppDataDir, "logs");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        /// <summary>Scratch folder for this run (job files, downloads that aren't kept).</summary>
        public static string TempDir
        {
            get
            {
                var d = Path.Combine(Path.GetTempPath(), "WinWipeHelper");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static bool IsWritable(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                var probe = Path.Combine(dir, ".wwh-write-test-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        public static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

        public static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        /// <summary>Normalizes a folder path for comparisons: full path, no trailing slash.</summary>
        public static string NormDir(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            try
            {
                p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
                if (p.Length < 2 || p[1] != ':' && !p.StartsWith("\\\\")) return null;
                p = Path.GetFullPath(p);
            }
            catch { return null; }
            return p.TrimEnd('\\');
        }

        public static bool IsUnder(string path, string dir)
        {
            if (path == null || dir == null) return false;
            return path.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
