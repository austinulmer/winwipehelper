using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace WinWipeHelper
{
    public sealed class WingetRow
    {
        public string Name;
        public string Id;
        public string Version;
        public string Available;
        public string Source;
        public string Match;
        public override string ToString() => $"{Name} | {Id} | {Version} | {Source}";
    }

    /// <summary>Finding, repairing and running winget.</summary>
    public static class Winget
    {
        /// <summary>0 = ok, 3010/1641 = ok (restart needed), the two negatives = already installed / no newer version.</summary>
        public static readonly int[] OkCodes = { 0, 3010, 1641, -1978335189, -1978335135 };

        public static readonly string[] CommonArgs =
            { "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" };

        public const string FamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

        public static bool IsOk(int code) => OkCodes.Contains(code);

        public static string DescribeCode(int code)
        {
            switch (code)
            {
                case 0: return "installed";
                case 3010: case 1641: return "installed (restart needed)";
                case -1978335189: return "already installed (no newer version)";
                case -1978335135: return "already installed";
                case -1978335212: return "package not found in winget";
                case -1978335216: return "no installer for this PC";
                case -1978334975: return "installer failed";
                case Worker.ExitUacDeclined: return "admin permission declined";
                default: return "exit code " + code + " (0x" + ((uint)code).ToString("X8") + ")";
            }
        }

        static string _path;

        /// <summary>The winget.exe path (the per-user App Execution Alias), or null.</summary>
        public static string FindExe()
        {
            if (_path != null && File.Exists(_path)) return _path;
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return _path = alias;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "winget.exe");
                    if (dir.Trim().Length > 0 && File.Exists(p)) return _path = p;
                }
                catch { }
            }
            // Elevated as a different admin account (no alias in that profile): use the installed package directly.
            if (Elevation.IsElevated)
            {
                try
                {
                    var apps = Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                    var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                    var best = Directory.GetDirectories(apps, $"Microsoft.DesktopAppInstaller_*_{arch}__8wekyb3d8bbwe")
                                        .Select(d => Path.Combine(d, "winget.exe")).Where(File.Exists)
                                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (best != null) return _path = best;
                }
                catch { }
            }
            return null;
        }

        /// <summary>True when winget exists and answers --version.</summary>
        public static bool IsWorking(out string version)
        {
            version = null;
            var exe = FindExe();
            if (exe == null) return false;
            var r = ProcessRunner.Capture(exe, "--version", 30000);
            version = r.Output?.Trim();
            return r.ExitCode == 0 && !string.IsNullOrEmpty(version) && version.StartsWith("v");
        }

        public static string InstallArgs(AppItem app)
        {
            var args = new List<string> { "install", "--id", app.Id };
            args.AddRange(CommonArgs);
            if (!string.IsNullOrWhiteSpace(app.Override)) { args.Add("--override"); args.Add(app.Override); }
            return ProcessRunner.JoinArgs(args);
        }

        public static string InstallArgs(string id)
        {
            var args = new List<string> { "install", "--id", id };
            args.AddRange(CommonArgs);
            return ProcessRunner.JoinArgs(args);
        }

        // ---- Bootstrap (no Microsoft Store) ------------------------------------------------

        /// <summary>
        /// Makes winget work on a fresh PC without the Store:
        /// 1) re-register the App Installer package that ships with Windows;
        /// 2) install App Installer from the official microsoft/winget-cli GitHub release, signatures checked first.
        /// </summary>
        public static bool EnsureWorking(Action<string> log, CancellationToken cancel)
        {
            if (IsWorking(out var v)) { log($"winget {v} is ready."); return true; }

            log("winget is missing or not working. Step 1: registering the App Installer that comes with Windows...");
            var r = ProcessRunner.PowerShell($"Add-AppxPackage -RegisterByFamilyName -MainPackage {FamilyName} -ErrorAction Stop", s => log("  " + s), 180000, cancel);
            if (WaitForWinget(TimeSpan.FromSeconds(r.ExitCode == 0 ? 60 : 5), cancel)) { IsWorking(out v); log($"winget {v} is ready."); return true; }

            log("Step 2: installing App Installer from Microsoft's official GitHub release (github.com/microsoft/winget-cli)...");
            try
            {
                if (InstallFromGitHub(log, cancel) && WaitForWinget(TimeSpan.FromSeconds(90), cancel))
                {
                    IsWorking(out v);
                    log($"winget {v} is ready.");
                    return true;
                }
            }
            catch (Exception ex) { log("  " + ex.Message); }
            log("winget still isn't working. See the README's \"winget\" section for what to try next.");
            return false;
        }

        static bool WaitForWinget(TimeSpan time, CancellationToken cancel)
        {
            var end = DateTime.UtcNow + time;
            do
            {
                _path = null;
                if (IsWorking(out _)) return true;
                if (cancel.WaitHandle.WaitOne(2000)) return false;
            } while (DateTime.UtcNow < end);
            return false;
        }

        static bool InstallFromGitHub(Action<string> log, CancellationToken cancel)
        {
            if (!DownloadAndVerify(log, cancel, out var bundlePath, out var depFiles)) return false;
            string Q(string p) => "'" + p.Replace("'", "''") + "'";
            var cmd = "Add-AppxPackage -Path " + Q(bundlePath) + (depFiles.Count > 0 ? " -DependencyPath " + string.Join(",", depFiles.Select(Q)) : "") + " -ErrorAction Stop";
            log("  Installing App Installer...");
            var r = ProcessRunner.PowerShell(cmd, s => log("  " + s), 600000, cancel);
            if (r.ExitCode != 0) { log("  Add-AppxPackage failed (exit " + r.ExitCode + ")."); return false; }
            return true;
        }

        /// <summary>
        /// Downloads App Installer (+ dependencies) from the latest microsoft/winget-cli release and checks every file is
        /// validly signed by Microsoft Corporation. Installs nothing.
        /// </summary>
        public static bool DownloadAndVerify(Action<string> log, CancellationToken cancel, out string bundlePath, out List<string> depFiles)
        {
            bundlePath = null;
            depFiles = new List<string>();
            var assets = Web.LatestReleaseAssets("microsoft/winget-cli", out var tag);
            var bundle = assets.FirstOrDefault(a => a.Name.Equals(FamilyName + ".msixbundle", StringComparison.OrdinalIgnoreCase))
                         ?? Web.FindAsset(assets, @"\.msixbundle$");
            var deps = assets.FirstOrDefault(a => a.Name.Equals("DesktopAppInstaller_Dependencies.zip", StringComparison.OrdinalIgnoreCase))
                       ?? Web.FindAsset(assets, @"Dependencies\.zip$");
            if (bundle == null) { log("  The latest release has no .msixbundle."); return false; }
            log($"  Release {tag}: {bundle.Name} ({Web.FormatSize(bundle.Size)})" + (deps != null ? $" + {deps.Name}" : ""));

            var dir = Path.Combine(Paths.TempDir, "winget-bootstrap-" + Paths.Timestamp());
            Directory.CreateDirectory(dir);
            bundlePath = Path.Combine(dir, bundle.Name);
            Web.Download(bundle.Url, bundlePath, null, cancel);
            var sig = Signature.Check(bundlePath);
            log($"  {bundle.Name}: {sig}");
            if (!sig.Matches("Microsoft Corporation")) { log("  Signature check failed, so it won't be installed."); return false; }

            if (deps != null)
            {
                var zip = Path.Combine(dir, deps.Name);
                Web.Download(deps.Url, zip, null, cancel);
                var unzip = Path.Combine(dir, "deps");
                ZipFile.ExtractToDirectory(zip, unzip);
                var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : Environment.Is64BitOperatingSystem ? "x64" : "x86";
                foreach (var f in Directory.GetFiles(unzip, "*.*", SearchOption.AllDirectories)
                                           .Where(f => f.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)))
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";
                    var name = Path.GetFileName(f);
                    bool forArch = folder.Equals(arch, StringComparison.OrdinalIgnoreCase) ||
                                   (!new[] { "x64", "x86", "arm64", "arm" }.Contains(folder.ToLowerInvariant()) && name.IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!forArch) continue;
                    var s = Signature.Check(f);
                    log($"  {name}: {s}");
                    if (!s.Matches("Microsoft Corporation")) { log("  A dependency failed its signature check, so nothing will be installed."); return false; }
                    depFiles.Add(f);
                }
            }
            return true;
        }

        // ---- Table output parsing (winget list / search) ------------------------------------

        /// <summary>Parses winget's fixed-width table. Column positions come from the header line.</summary>
        public static List<WingetRow> ParseTable(string output)
        {
            var rows = new List<WingetRow>();
            if (string.IsNullOrEmpty(output)) return rows;
            var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Contains('\r') ? l.Substring(l.LastIndexOf('\r') + 1) : l).ToList();
            int dash = lines.FindIndex(l => l.Length > 10 && Regex.IsMatch(l, @"^-{10,}\s*$"));
            if (dash < 1) return rows;
            var header = lines[dash - 1];
            // Column starts: first char, and every char that follows 1+ spaces.
            var starts = new List<int> { 0 };
            for (int i = 1; i < header.Length; i++) if (header[i] != ' ' && header[i - 1] == ' ') starts.Add(i);
            var names = starts.Select((s, k) => header.Substring(s, (k + 1 < starts.Count ? starts[k + 1] : header.Length) - s).Trim()).ToList();

            int Col(string english, int fallback) { int i = names.FindIndex(n => n.Equals(english, StringComparison.OrdinalIgnoreCase)); return i >= 0 ? i : fallback; }
            int cName = Col("Name", 0), cId = Col("Id", 1), cVer = Col("Version", 2);
            int cAvail = Col("Available", -1), cSource = Col("Source", names.Count >= 4 ? names.Count - 1 : -1), cMatch = Col("Match", -1);

            for (int li = dash + 1; li < lines.Count; li++)
            {
                var line = lines[li];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length < (starts.Count > 1 ? starts[1] : 1)) continue;
                if (Regex.IsMatch(line, @"^\d+ (upgrades|packages?) ")) continue;
                string Cell(int c)
                {
                    if (c < 0 || c >= starts.Count || starts[c] >= line.Length) return "";
                    int end = c + 1 < starts.Count ? Math.Min(starts[c + 1], line.Length) : line.Length;
                    return line.Substring(starts[c], end - starts[c]).Trim();
                }
                var row = new WingetRow { Name = Cell(cName), Id = Cell(cId), Version = Cell(cVer), Available = Cell(cAvail), Source = Cell(cSource), Match = Cell(cMatch) };
                // Wide characters can shift columns; recover the id as the token that looks like one.
                // (ARP\ and MSIX\ ids are registry key / package names and may legitimately contain spaces.)
                if (row.Id.Contains(' ') && !row.Id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) && !row.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(line, @"\s((?:ARP|MSIX)\\\S+|[A-Za-z0-9][\w\-+]*\.[\w.\-+]+|9[A-Z0-9]{11}|XP[A-Z0-9]{10})\s");
                    if (m.Success) row.Id = m.Groups[1].Value;
                }
                if (row.Id.Length == 0) continue;
                rows.Add(row);
            }
            return rows;
        }

        public static List<WingetRow> List(CancellationToken cancel = default)
        {
            var exe = FindExe();
            if (exe == null) return new List<WingetRow>();
            var r = ProcessRunner.Capture(exe, "list --accept-source-agreements --disable-interactivity", 180000, cancel);
            return ParseTable(r.Output);
        }

        /// <summary>winget export: package ids by source name.</summary>
        public static List<(string Id, string Source)> Export(CancellationToken cancel = default)
        {
            var list = new List<(string, string)>();
            var exe = FindExe();
            if (exe == null) return list;
            var file = Path.Combine(Paths.TempDir, "winget-export-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            try
            {
                ProcessRunner.Capture(exe, "export -o " + ProcessRunner.Quote(file) + " --accept-source-agreements --disable-interactivity", 180000, cancel);
                if (!File.Exists(file)) return list;
                var json = Json.Parse(File.ReadAllText(file));
                foreach (var src in Json.Arr(json, "Sources"))
                {
                    var name = Json.Str(src, "SourceDetails", "Name") ?? "";
                    foreach (var pkg in Json.Arr(src, "Packages"))
                    {
                        var id = Json.Str(pkg, "PackageIdentifier");
                        if (!string.IsNullOrEmpty(id)) list.Add((id, name));
                    }
                }
            }
            catch { }
            finally { try { File.Delete(file); } catch { } }
            return list;
        }

        public static List<WingetRow> Search(string query, bool byName, CancellationToken cancel = default, int timeoutMs = 45000)
        {
            var exe = FindExe();
            if (exe == null) return new List<WingetRow>();
            var args = new List<string> { "search" };
            if (byName) args.Add("--name");
            args.Add(query);
            args.AddRange(new[] { "--source", "winget", "--accept-source-agreements", "--disable-interactivity", "--count", "15" });
            // Several winget processes starting at the same moment sometimes return nothing (they contend for the
            // source index). A real "nothing found" says so; anything else empty is retried.
            for (int attempt = 1; ; attempt++)
            {
                var r = ProcessRunner.Capture(exe, ProcessRunner.JoinArgs(args), timeoutMs, cancel);
                var rows = ParseTable(r.Output).Where(x => !x.Name.StartsWith("<")).ToList();
                bool realEmpty = r.ExitCode == -1978335212 || (r.Output ?? "").IndexOf("No package found", StringComparison.OrdinalIgnoreCase) >= 0;
                if (rows.Count > 0 || realEmpty || attempt >= 4 || cancel.IsCancellationRequested) { LastSearchOutput = r.Output; return rows; }
                cancel.WaitHandle.WaitOne(400 * attempt + new Random().Next(300));
            }
        }

        [ThreadStatic] public static string LastSearchOutput;
    }
}
