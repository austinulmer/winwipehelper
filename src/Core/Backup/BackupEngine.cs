using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinWipeHelper
{
    /// <summary>
    /// Files under your Desktop, Documents, Pictures, Videos, Music, Downloads or user folder are stored by
    /// location type (Documents/...), so they restore into the *current* user's folders even if the username
    /// or OneDrive redirection changed. Anything else is stored as _abs/&lt;drive&gt;/... and restores to the same path.
    /// </summary>
    public static class BackupEngine
    {
        public const string Prefix = "Files-Backup";
        public const string InfoEntry = "_backup-info.json";

        // ---- known locations -----------------------------------------------------------------
        public static List<KeyValuePair<string, string>> Roots()
        {
            string K(Guid g, Environment.SpecialFolder fallback) => Paths.NormDir(Shell.KnownFolder(g) ?? Environment.GetFolderPath(fallback));
            var downloads = Paths.NormDir(Shell.KnownFolder(Shell.FolderDownloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            var list = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Desktop", K(Shell.FolderDesktop, Environment.SpecialFolder.DesktopDirectory)),
                new KeyValuePair<string, string>("Documents", K(Shell.FolderDocuments, Environment.SpecialFolder.MyDocuments)),
                new KeyValuePair<string, string>("Pictures", K(Shell.FolderPictures, Environment.SpecialFolder.MyPictures)),
                new KeyValuePair<string, string>("Videos", K(Shell.FolderVideos, Environment.SpecialFolder.MyVideos)),
                new KeyValuePair<string, string>("Music", K(Shell.FolderMusic, Environment.SpecialFolder.MyMusic)),
                new KeyValuePair<string, string>("Downloads", downloads),
                new KeyValuePair<string, string>("Home", K(Shell.FolderProfile, Environment.SpecialFolder.UserProfile)),   // last, so the folders above win
            };
            return list.Where(kv => kv.Value != null).ToList();
        }

        public static string RootLabel(string key) => key == "Home" ? "Your user folder" : key;

        /// <summary>Path on disk -> name inside the zip.</summary>
        public static string ToEntry(string path, List<KeyValuePair<string, string>> roots)
        {
            foreach (var kv in roots)
            {
                var r = kv.Value.TrimEnd('\\');
                if (path.Equals(r, StringComparison.OrdinalIgnoreCase)) return kv.Key + "/";
                if (path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                    return kv.Key + "/" + path.Substring(r.Length + 1).Replace('\\', '/');
            }
            if (path.Length >= 3 && path[1] == ':')
                return "_abs/" + char.ToUpperInvariant(path[0]) + "/" + path.Substring(3).Replace('\\', '/');
            // UNC paths: _abs/UNC/server/share/...
            return "_abs/UNC/" + path.TrimStart('\\').Replace('\\', '/');
        }

        /// <summary>Name inside the zip -> path on this PC (for the current user).</summary>
        public static string FromEntry(string name, List<KeyValuePair<string, string>> roots)
        {
            // Refuse names that could climb out of their folder ("..", empty or rooted segments, drive letters).
            var segs = name.Split('/');
            for (int i = 0; i < segs.Length; i++)
            {
                var s = segs[i];
                bool lastAndEmpty = i == segs.Length - 1 && s.Length == 0;
                if (!lastAndEmpty && (s.Length == 0 || s == "." || s == ".." || s.IndexOf(':') >= 0 && !(i == 1 && segs[0] == "_abs") || s.IndexOf('\\') >= 0))
                    throw new InvalidDataException("Unsafe path in backup: " + name);
            }
            var parts = name.Split(new[] { '/' }, 2);
            var rest = parts.Length > 1 ? parts[1] : "";
            string result, under;
            if (parts[0] == "_abs")
            {
                var p = rest.Split(new[] { '/' }, 2);
                var tail = (p.Length > 1 ? p[1] : "").Replace('/', '\\');
                if (p[0] == "UNC") { under = "\\\\"; result = "\\\\" + tail; }
                else
                {
                    if (p[0].Length != 1 || !char.IsLetter(p[0][0])) throw new InvalidDataException("Unsafe drive in backup: " + name);
                    under = p[0] + ":\\"; result = under + tail;
                }
            }
            else
            {
                var root = roots.FirstOrDefault(kv => kv.Key.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).Value;
                if (root == null) throw new InvalidDataException("Unknown folder in backup: " + parts[0]);
                under = root;
                result = rest.Length == 0 ? root : Path.Combine(root, rest.Replace('/', '\\'));
            }
            var full = Path.GetFullPath(result);
            if (!Paths.IsUnder(full, under.TrimEnd('\\')) && !full.Equals(under, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe path in backup: " + name);
            return result;
        }

        // ---- what's already compressed ----------------------------------------------------------
        static readonly HashSet<string> NoCompress = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".3gp", ".mts", ".m2ts",
            ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma",
            ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".jar", ".apk", ".msi", ".msix", ".appx",
            ".pdf", ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".odt", ".ods", ".odp", ".epub",
        };
        public static CompressionLevel LevelFor(string path) => NoCompress.Contains(Path.GetExtension(path)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;

        // ---- reparse points -------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public FileAttributes dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileW(string name, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);

        static uint ReparseTag(string path)
        {
            var h = FindFirstFileW(path.Length >= 248 && !path.StartsWith(@"\\?\") ? @"\\?\" + path : path, out var d);
            if (h == new IntPtr(-1)) return 0;
            FindClose(h);
            return (d.dwFileAttributes & FileAttributes.ReparsePoint) != 0 ? d.dwReserved0 : 0;
        }

        const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

        /// <summary>
        /// Junctions, symlinks and mount points are skipped (avoids loops and duplicates). OneDrive folders are
        /// reparse points too, but not "name surrogates", so they're followed. Online-only cloud files are skipped
        /// (reading them would download them; they're in the cloud already).
        /// </summary>
        public static string SkipReason(string path, FileAttributes attr, bool isDir)
        {
            if ((attr & FileAttributes.ReparsePoint) == 0) return null;
            uint tag = ReparseTag(path);
            bool nameSurrogate = (tag & 0x20000000) != 0;          // symlink, junction, mount point
            if (nameSurrogate || tag == 0x8000001B /* app execution alias */) return isDir ? "junction or link (skipped)" : "link (skipped)";
            if (!isDir && (attr & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) return "online-only cloud file, not on this PC (skipped)";
            return null;
        }

        // ---- listing ---------------------------------------------------------------------------
        public static List<FileInfo> ListFiles(IEnumerable<string> selected, List<string> problems, CancellationToken cancel = default)
        {
            var list = new List<FileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in selected)
            {
                if (File.Exists(p))
                {
                    var fi = new FileInfo(p);
                    var why = SkipReason(fi.FullName, fi.Attributes, false);
                    if (why != null) { problems.Add($"{why}: {fi.FullName}"); continue; }
                    if (seen.Add(fi.FullName)) list.Add(fi);
                    continue;
                }
                if (!Directory.Exists(p)) { problems.Add("Not found: " + p); continue; }
                var stack = new Stack<string>();
                stack.Push(p);
                while (stack.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    var d = stack.Pop();
                    try
                    {
                        var di = new DirectoryInfo(d);
                        foreach (var sub in di.EnumerateDirectories())
                        {
                            var why = SkipReason(sub.FullName, sub.Attributes, true);
                            if (why != null) { problems.Add($"{why}: {sub.FullName}"); continue; }
                            stack.Push(sub.FullName);
                        }
                        foreach (var f in di.EnumerateFiles())
                        {
                            var why = SkipReason(f.FullName, f.Attributes, false);
                            if (why != null) { problems.Add($"{why}: {f.FullName}"); continue; }
                            if (seen.Add(f.FullName)) list.Add(f);
                        }
                    }
                    catch (Exception ex) { problems.Add($"Couldn't read folder: {d} ({ex.Message})"); }
                }
            }
            return list;
        }

        // ---- destination ---------------------------------------------------------------------
        /// <summary>This program's folder if it isn't on C:, else the non-C: drive with the most free space (D: preferred), in "PC Backups".</summary>
        public static string DefaultDestination()
        {
            var here = Paths.ExeDir;
            var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            if (!string.IsNullOrEmpty(here) && !here.StartsWith(sys.Substring(0, 2), StringComparison.OrdinalIgnoreCase) && Paths.IsWritable(here)) return here;
            var drives = DriveInfo.GetDrives().Where(d =>
            {
                try { return (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) && d.IsReady && !d.Name.StartsWith(sys.Substring(0, 2), StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToList();
            var best = drives.OrderByDescending(d => d.Name.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
                             .ThenByDescending(d => { try { return d.AvailableFreeSpace; } catch { return 0L; } })
                             .FirstOrDefault();
            if (best != null) return Path.Combine(best.RootDirectory.FullName, "PC Backups");
            return here;
        }

        public static bool IsSystemDrive(string path)
        {
            var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
            return !string.IsNullOrEmpty(path) && path.StartsWith(sys.Substring(0, 2), StringComparison.OrdinalIgnoreCase);
        }

        public static long FreeSpace(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; } catch { return -1; }
        }

        // ---- backup --------------------------------------------------------------------------
        public sealed class BackupInfo
        {
            public string Created;
            public string Computer;
            public string User;
            public string Program;
            public Dictionary<string, string> Roots = new Dictionary<string, string>();
            public List<string> Selected = new List<string>();
            public int Files;
            public long Bytes;
            public List<string> Problems = new List<string>();
        }

        public sealed class BackupResult
        {
            public string ZipPath;
            public int Saved;
            public long Bytes;
            public List<string> Problems = new List<string>();
            public string SkippedLog;
        }

        static DateTime ClampZipTime(DateTime t)
        {
            if (t.Year < 1980) return new DateTime(1980, 1, 1, 0, 0, 0);
            if (t.Year > 2107) return new DateTime(2107, 12, 31, 0, 0, 0);
            return t;
        }

        public static BackupResult Backup(List<string> selected, string destDir, Action<string> log, Action<double, string> progress, CancellationToken cancel)
        {
            var result = new BackupResult();
            var roots = Roots();
            progress(0, "Listing files...");
            var files = ListFiles(selected, result.Problems, cancel);
            long total = files.Sum(f => { try { return f.Length; } catch { return 0L; } });
            log($"{files.Count} files, {Web.FormatSize(total)}");

            Directory.CreateDirectory(destDir);
            long free = FreeSpace(destDir);
            if (free >= 0 && free < total)
                throw new IOException($"Not enough space on {Path.GetPathRoot(destDir)}: needs up to {Web.FormatSize(total)}, only {Web.FormatSize(free)} free.");

            result.ZipPath = Path.Combine(destDir, $"{Prefix}-{DateTime.Now:yyyy-MM-dd_HHmm}.zip");
            for (int n = 2; File.Exists(result.ZipPath); n++) result.ZipPath = Path.Combine(destDir, $"{Prefix}-{DateTime.Now:yyyy-MM-dd_HHmm}-{n}.zip");
            log("Saving to " + result.ZipPath);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            int count = 0;
            var buf = new byte[1 << 20];
            var lastReport = DateTime.MinValue;
            bool ok = false;
            try
            {
                using (var fs = new FileStream(result.ZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: Encoding.UTF8))
                {
                    foreach (var f in files)
                    {
                        cancel.ThrowIfCancellationRequested();
                        count++;
                        var name = ToEntry(f.FullName, roots);
                        if (!names.Add(name)) continue;
                        long len = 0;
                        try { len = f.Length; } catch { }
                        try
                        {
                            using (var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                            {
                                var entry = zip.CreateEntry(name, LevelFor(f.FullName));
                                entry.LastWriteTime = ClampZipTime(f.LastWriteTime);
                                using (var output = entry.Open())
                                {
                                    int n;
                                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        output.Write(buf, 0, n);
                                        bytes += n;
                                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                                        {
                                            lastReport = DateTime.UtcNow;
                                            progress(total > 0 ? 100.0 * bytes / total : 100, $"{count} of {files.Count} files, {Web.FormatSize(bytes)} of {Web.FormatSize(total)}");
                                        }
                                    }
                                }
                            }
                            result.Saved++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Note: an entry that failed half-way stays in the zip as a partial file; the info file lists it.
                            result.Problems.Add($"Skipped (in use or no access): {f.FullName} ({ex.Message})");
                            bytes += len;
                        }
                    }
                    result.Bytes = bytes;
                    var info = new BackupInfo
                    {
                        Created = DateTime.Now.ToString("s"), Computer = Environment.MachineName, User = Environment.UserName,
                        Program = Edition.ProductName + " " + typeof(BackupEngine).Assembly.GetName().Version,
                        Selected = selected, Files = result.Saved, Bytes = bytes, Problems = result.Problems,
                    };
                    foreach (var kv in roots) info.Roots[kv.Key] = kv.Value;
                    var ie = zip.CreateEntry(InfoEntry, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(ie.Open(), new UTF8Encoding(false))) w.Write(Json.Serialize(info));
                }
                ok = true;
            }
            finally
            {
                if (!ok) { try { File.Delete(result.ZipPath); } catch { } }
            }

            if (result.Problems.Count > 0)
            {
                result.SkippedLog = Path.ChangeExtension(result.ZipPath, ".skipped.txt");
                File.WriteAllLines(result.SkippedLog, result.Problems, new UTF8Encoding(false));
            }
            progress(100, $"Saved {result.Saved} files");
            return result;
        }

        // ---- restore ---------------------------------------------------------------------------
        public static string FindNewestBackup()
        {
            var places = new[] { Paths.ExeDir, DefaultDestination() }.Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase);
            return places.SelectMany(p => { try { return Directory.GetFiles(p, Prefix + "-*.zip"); } catch { return new string[0]; } })
                         .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }

        public sealed class RestoreGroup
        {
            public string Key;
            public string Target;
            public List<ZipArchiveEntry> Entries = new List<ZipArchiveEntry>();
            public long Bytes => Entries.Sum(e => e.Length);
        }

        /// <summary>Groups entries by top folder (Documents/Work, _abs/D/Projects ...), like the original tool.</summary>
        public static List<RestoreGroup> Group(ZipArchive zip, List<KeyValuePair<string, string>> roots)
        {
            var groups = new Dictionary<string, RestoreGroup>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var e in zip.Entries)
            {
                var full = e.FullName.Replace('\\', '/');
                if (full == InfoEntry || full.EndsWith("/")) continue;
                var parts = full.Split('/');
                int depth = parts[0] == "_abs" ? (parts.Length > 1 && parts[1] == "UNC" ? 5 : 3) : 2;
                string key = parts.Length > depth ? string.Join("/", parts.Take(depth)) + "/" : string.Join("/", parts.Take(depth - 1)) + "/*";
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new RestoreGroup { Key = key };
                    try
                    {
                        if (key.EndsWith("/*"))
                        {
                            var k = key.Substring(0, key.Length - 2);
                            g.Target = (k.StartsWith("_abs/") ? FromEntry(k + "/", roots) : FromEntry(k + "/", roots)) + "  (loose files)";
                        }
                        else g.Target = FromEntry(key.TrimEnd('/'), roots);
                    }
                    catch (Exception ex) { g.Target = "(unknown: " + ex.Message + ")"; }
                    groups[key] = g;
                    order.Add(key);
                }
                g.Entries.Add(e);
            }
            return order.Select(k => groups[k]).ToList();
        }

        public enum Conflict { Skip, Overwrite, KeepBoth }

        public sealed class RestoreResult
        {
            public int Restored, Skipped;
            public List<string> Failed = new List<string>();
        }

        public static RestoreResult Restore(IEnumerable<ZipArchiveEntry> entries, Conflict conflict, List<KeyValuePair<string, string>> roots,
                                            Action<double, string> progress, CancellationToken cancel)
        {
            var list = entries.ToList();
            var res = new RestoreResult();
            long total = list.Sum(e => e.Length), done = 0;
            int n = 0;
            var lastReport = DateTime.MinValue;
            foreach (var e in list)
            {
                cancel.ThrowIfCancellationRequested();
                n++;
                string target = null;
                try
                {
                    target = FromEntry(e.FullName.Replace('\\', '/'), roots);
                    if (File.Exists(target))
                    {
                        if (conflict == Conflict.Skip) { res.Skipped++; done += e.Length; continue; }
                        if (conflict == Conflict.KeepBoth)
                        {
                            string dir = Path.GetDirectoryName(target), b = Path.GetFileNameWithoutExtension(target), x = Path.GetExtension(target);
                            target = Path.Combine(dir, $"{b} (restored){x}");
                            for (int c = 2; File.Exists(target); c++) target = Path.Combine(dir, $"{b} (restored {c}){x}");
                        }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    var tmp = target + ".wwh-restore";
                    using (var src = e.Open())
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        src.CopyTo(dst, 1 << 20);
                    if (File.Exists(target))
                    {
                        var a = File.GetAttributes(target);
                        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, a & ~FileAttributes.ReadOnly);
                        File.Delete(target);
                    }
                    File.Move(tmp, target);
                    File.SetLastWriteTime(target, e.LastWriteTime.DateTime);
                    res.Restored++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    res.Failed.Add($"{target ?? e.FullName} - {ex.Message}");
                    try { if (target != null) File.Delete(target + ".wwh-restore"); } catch { }
                }
                done += e.Length;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.UtcNow;
                    progress(total > 0 ? 100.0 * done / total : 100, $"{n} of {list.Count} files");
                }
            }
            progress(100, $"Restored {res.Restored} files");
            return res;
        }
    }
}
