using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinWipeHelper
{
    public sealed class ScanOptions
    {
        public bool Store;
        public bool Chocolatey;
        public bool Scoop;
        public bool OtherProfiles;      // read HKU profiles elevated if needed
        public bool SearchWinget = true;
        public int SearchConcurrency = 4;
        public TimeSpan SearchBudget = TimeSpan.FromSeconds(75);
    }

    /// <summary>Elevated helper job: read other users' uninstall entries.</summary>
    public static class ScanJobs
    {
        public const string Hku = "scan-hku";
        public sealed class HkuPayload { public List<string> Sids = new List<string>(); public string OutPath; }

        public static void Register() => Worker.Register(Hku, (ctx, payload) =>
        {
            var p = (HkuPayload)Json.ConvertTo(payload, typeof(HkuPayload));
            var recs = ScanSources.RegistryUsers(p.Sids);
            File.WriteAllText(p.OutPath, Json.Serialize(recs));
            ctx.Log($"Read {recs.Count} entries from {p.Sids.Count} other profile(s).");
            return 0;
        });
    }

    /// <summary>"Scan this PC": collect (pass 1), merge (2), match to winget (3), classify (4).</summary>
    public sealed class Scanner
    {
        readonly ScanOptions _opt;
        readonly Action<string> _log;
        readonly Action<double, string> _progress;
        readonly CancellationToken _cancel;
        public readonly List<ScanRecord> AllRecords = new List<ScanRecord>();
        public readonly Dictionary<string, TimeSpan> Timings = new Dictionary<string, TimeSpan>();

        public Scanner(ScanOptions opt, Action<string> log, Action<double, string> progress, CancellationToken cancel)
        {
            _opt = opt; _log = log; _progress = progress; _cancel = cancel;
        }

        public List<ScanItem> Run()
        {
            var total = Stopwatch.StartNew();
            _progress(2, "Pass 1: collecting from every source...");
            Collect();
            _log($"Pass 1: {AllRecords.Count} records from {AllRecords.Select(r => r.Source).Distinct().Count()} sources.");
            foreach (var g in AllRecords.GroupBy(r => r.Source).OrderBy(g => g.Key)) _log($"  {g.Key}: {g.Count()}");

            _progress(45, "Pass 2: merging duplicates...");
            var items = Merge(AllRecords);
            _log($"Pass 2: merged into {items.Count} programs.");

            _progress(50, "Pass 4: classifying...");
            foreach (var it in items) Classify(it);

            _progress(55, "Pass 3: matching to winget...");
            if (_opt.SearchWinget) MatchWinget(items);
            foreach (var it in items) SetDefaults(it);

            _log($"Pass 3/4: {items.Count(i => !string.IsNullOrEmpty(i.WingetId))} with a winget id, " +
                 string.Join(", ", items.GroupBy(i => i.Category).Select(g => $"{g.Count()} {ScanItem.CategoryLabel(g.Key).ToLowerInvariant()}")));
            _log($"Scan took {total.Elapsed.TotalSeconds:N0} s.");
            _progress(100, "Scan finished");
            return items.OrderBy(i => i.Category).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---- pass 1 ---------------------------------------------------------------------------
        void Collect()
        {
            var tasks = new List<Task<List<ScanRecord>>>();
            Task<List<ScanRecord>> Go(string name, Func<List<ScanRecord>> f) => Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                List<ScanRecord> r;
                try { r = f(); } catch (Exception ex) { _log($"  {name} failed: {ex.Message}"); r = new List<ScanRecord>(); }
                lock (Timings) Timings[name] = sw.Elapsed;
                _log($"  {name}: {r.Count} found ({sw.Elapsed.TotalSeconds:N1} s)");
                return r;
            });

            bool haveWinget = Winget.FindExe() != null;
            if (haveWinget)
            {
                tasks.Add(Go("winget export", () => ScanSources.WingetExport(_cancel)));
                tasks.Add(Go("winget list", () => ScanSources.WingetList(_opt.Store, _cancel)));
            }
            else _log("  winget isn't available, so winget export/list were skipped (the other sources still run).");

            List<string> denied = null;
            tasks.Add(Go("Registry", () => ScanSources.Registry(out denied)));
            tasks.Add(Go("Shortcuts", ScanSources.Shortcuts));
            tasks.Add(Go("Program folders", ScanSources.ProgramFolders));
            tasks.Add(Go("Startup + scheduled tasks", ScanSources.Startup));
            tasks.Add(Go("Game launchers", ScanSources.Games));
            if (_opt.Store) tasks.Add(Go("Microsoft Store", () => ScanSources.StoreApps(_cancel)));
            if (_opt.Chocolatey) tasks.Add(Go("Chocolatey", ScanSources.Chocolatey));
            if (_opt.Scoop) tasks.Add(Go("Scoop", ScanSources.Scoop));

            int done = 0;
            foreach (var t in tasks)
            {
                t.Wait(_cancel);
                AllRecords.AddRange(t.Result);
                _progress(2 + 40.0 * ++done / tasks.Count, "Pass 1: collecting...");
            }

            if (denied != null && denied.Count > 0)
            {
                if (_opt.OtherProfiles)
                {
                    _log($"  {denied.Count} other user profile(s) need admin to read. Asking...");
                    var outPath = Path.Combine(Paths.TempDir, "hku-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                    var code = Worker.RunElevated(ScanJobs.Hku, new ScanJobs.HkuPayload { Sids = denied, OutPath = outPath }, m => _log("  " + m.Text), _cancel);
                    if (code == 0 && File.Exists(outPath))
                    {
                        try { AllRecords.AddRange(Json.Deserialize<List<ScanRecord>>(File.ReadAllText(outPath))); } catch { }
                        try { File.Delete(outPath); } catch { }
                    }
                }
                else _log($"  {denied.Count} other user profile(s) couldn't be read without admin (tick \"Other user profiles\" to include them).");
            }
        }

        // ---- pass 2 ---------------------------------------------------------------------------
        sealed class UnionFind
        {
            readonly int[] _p;
            public UnionFind(int n) { _p = Enumerable.Range(0, n).ToArray(); }
            public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
            public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[Math.Max(a, b)] = Math.Min(a, b); }
        }

        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        /// <summary>Folders too generic to say two records are the same program.</summary>
        static bool IsGenericDir(string d)
        {
            if (d == null) return true;
            if (Paths.IsUnder(d, WinDir)) return true;
            var parts = d.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return true;
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles)),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            };
            foreach (var r in roots) if (r != null && d.Equals(r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            if (d.IndexOf(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.IndexOf(@"\Common Files", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.IndexOf(@"\Installer\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Best guess of a registry entry's program folder: InstallLocation, else DisplayIcon / uninstaller folder.</summary>
        public static string LocationOf(ScanRecord r)
        {
            if (!IsGenericDir(r.InstallLocation)) return r.InstallLocation;
            foreach (var cmd in new[] { r.DisplayIcon, r.UninstallString })
            {
                // "steam.exe steam://uninstall/123": that's the launcher's folder, not the game's.
                if (cmd == null || cmd.Contains("://")) continue;
                var exe = ScanSources.ExtractExe(cmd);
                if (exe == null || !Path.IsPathRooted(exe) || exe.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var d = Paths.NormDir(Path.GetDirectoryName(exe));
                if (d != null && Regex.IsMatch(Path.GetFileName(d), @"^(uninstall|uninst|_uninst|bin|x64|x86)$", RegexOptions.IgnoreCase)) d = Paths.NormDir(Path.GetDirectoryName(d));
                if (!IsGenericDir(d)) return d;
            }
            return null;
        }

        static bool IsPlaceRecord(ScanRecord r) =>
            r.Source.StartsWith("Program Files") || r.Source.StartsWith("LocalAppData\\Programs") || r.Source == "AppData";
        static bool IsTargetRecord(ScanRecord r) => r.TargetPath != null && !IsPlaceRecord(r);

        public static List<ScanItem> Merge(List<ScanRecord> recs)
        {
            var uf = new UnionFind(recs.Count);
            var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Key(int i, string key)
            {
                if (string.IsNullOrEmpty(key)) return;
                if (byKey.TryGetValue(key, out var j)) uf.Union(i, j); else byKey[key] = i;
            }

            // Registered locations (registry, launchers, scoop) for the target / folder lookups.
            var locIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (IsPlaceRecord(r) || IsTargetRecord(r)) continue;
                var loc = r.Source.StartsWith("Registry") ? LocationOf(r) : (IsGenericDir(r.InstallLocation) ? null : r.InstallLocation);
                if (loc == null) continue;
                if (!locIndex.TryGetValue(loc, out var l)) locIndex[loc] = l = new List<int>();
                l.Add(i);
            }

            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (!string.IsNullOrEmpty(r.WingetId)) Key(i, "id:" + r.WingetId);
                if (!string.IsNullOrEmpty(r.StoreId)) Key(i, "store:" + r.StoreId);
                if (!string.IsNullOrEmpty(r.ArpKey)) Key(i, "arp:" + r.ArpKey);
                if (!string.IsNullOrEmpty(r.Launcher) && !string.IsNullOrEmpty(r.GameId)) Key(i, $"game:{r.Launcher}:{r.GameId}");
                var norm = Names.Normalize(r.Name);
                if (norm.Length >= 3 && !(IsPlaceRecord(r) && norm.Length < 4)) Key(i, "name:" + norm);
            }
            foreach (var kv in locIndex) for (int k = 1; k < kv.Value.Count; k++) uf.Union(kv.Value[0], kv.Value[k]);

            var extraSources = new Dictionary<int, List<string>>();
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (IsTargetRecord(r))
                {
                    // Walk up from the exe's folder to the nearest registered program folder.
                    var d = Paths.NormDir(Path.GetDirectoryName(r.TargetPath));
                    for (int up = 0; d != null && up < 4 && !IsGenericDir(d); up++)
                    {
                        if (locIndex.TryGetValue(d, out var hits)) { uf.Union(i, hits[0]); break; }
                        d = Paths.NormDir(Path.GetDirectoryName(d));
                    }
                }
                else if (IsPlaceRecord(r) && r.InstallLocation != null)
                {
                    // A top-level folder: same program if it is (or holds exactly one) registered location.
                    var under = locIndex.Where(kv => Paths.IsUnder(kv.Key, r.InstallLocation)).SelectMany(kv => kv.Value).Select(uf.Find).Distinct().ToList();
                    if (under.Count == 1) uf.Union(i, under[0]);
                    else if (under.Count > 1)
                    {
                        // A vendor folder holding several programs: note the source on each, don't list the folder itself.
                        foreach (var u in under) { if (!extraSources.TryGetValue(u, out var l)) extraSources[u] = l = new List<string>(); l.Add(r.Source); }
                        r.Detail = "covered";
                    }
                    // A folder under a target record's exe folder links them too (portable apps with a shortcut).
                }
            }
            // Portable apps: link shortcut/startup targets to the program folder that holds them.
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (!IsTargetRecord(r)) continue;
                for (int j = 0; j < recs.Count; j++)
                {
                    var f = recs[j];
                    if (!IsPlaceRecord(f) || f.Detail == "covered" || f.InstallLocation == null) continue;
                    if (Paths.IsUnder(r.TargetPath, f.InstallLocation)) { uf.Union(i, j); break; }
                }
            }

            var groups = new Dictionary<int, ScanItem>();
            for (int i = 0; i < recs.Count; i++)
            {
                if (recs[i].Detail == "covered") continue;
                int root = uf.Find(i);
                if (!groups.TryGetValue(root, out var item)) groups[root] = item = new ScanItem();
                item.Records.Add(recs[i]);
            }
            foreach (var kv in extraSources)
                if (groups.TryGetValue(uf.Find(kv.Key), out var it))
                    foreach (var s in kv.Value.Distinct())
                        it.Records.Add(new ScanRecord { Source = s, Name = it.Records[0].Name, Detail = "folder" });

            var items = new List<ScanItem>();
            foreach (var g in groups.Values) items.AddRange(SplitByWingetId(g));
            foreach (var it in items) Summarize(it);
            return items;
        }

        /// <summary>A group with two different installed winget ids (e.g. VC++ x64 and x86) is two programs.</summary>
        static IEnumerable<ScanItem> SplitByWingetId(ScanItem g)
        {
            var ids = g.Records.Where(r => !string.IsNullOrEmpty(r.WingetId)).Select(r => r.WingetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count <= 1) { yield return g; yield break; }
            var parts = ids.ToDictionary(id => id, id => new ScanItem(), StringComparer.OrdinalIgnoreCase);
            // Names that winget list reported for each id
            var namesById = g.Records.Where(r => r.Source == "winget list" && r.WingetId != null)
                                     .GroupBy(r => r.WingetId, StringComparer.OrdinalIgnoreCase)
                                     .ToDictionary(x => x.Key, x => x.Select(r => r.Name).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var r in g.Records)
            {
                string target = r.WingetId;
                if (string.IsNullOrEmpty(target))
                {
                    target = ids.OrderByDescending(id =>
                    {
                        var names = namesById.TryGetValue(id, out var n) ? n : new List<string> { id };
                        return names.Max(n => string.Equals(n, r.Name, StringComparison.OrdinalIgnoreCase) ? 2.0 : Names.Similarity(n, r.Name) + ArchBonus(n, r.Name));
                    }).First();
                }
                parts[target].Records.Add(r);
            }
            foreach (var p in parts.Values) if (p.Records.Count > 0) yield return p;
        }

        static double ArchBonus(string a, string b)
        {
            foreach (var arch in new[] { "x64", "x86", "arm64", "64-bit", "32-bit" })
            {
                bool x = (a ?? "").IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0, y = (b ?? "").IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0;
                if (x && y) return 0.5;
                if (x != y) return -0.2;
            }
            return 0;
        }

        static readonly string[] SourcePriority = { "Registry HKLM", "Registry HKLM (32-bit)", "Registry HKCU", "winget list", "Steam library", "Epic manifests", "Microsoft Store", "Start Menu (all users)", "Start Menu", "Desktop (public)", "Desktop" };

        static void Summarize(ScanItem it)
        {
            ScanRecord best = it.Records
                .OrderBy(r => { int i = Array.FindIndex(SourcePriority, s => r.Source.StartsWith(s)); return i < 0 ? 99 : i; })
                .ThenBy(r => r.SystemComponent ? 1 : 0)
                .First();
            it.Name = Names.Clean(best.Name);
            if (best.Source == "winget export") it.Name = best.WingetId;
            it.Publisher = it.Records.Select(r => r.Publisher).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            it.Homepage = it.Records.Select(r => r.Homepage).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            it.Launcher = it.Records.Select(r => r.Launcher).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            it.Optional = it.Records.All(r => r.Optional || r.Source == "winget list" && r.StoreId != null);
            var wl = it.Records.FirstOrDefault(r => r.Source == "winget list" && !string.IsNullOrEmpty(r.WingetId));
            var we = it.Records.FirstOrDefault(r => r.Source == "winget export");
            if (wl != null) { it.WingetId = wl.WingetId; it.Match = we != null ? "installed (winget list + export)" : "installed (winget list)"; }
            else if (we != null) { it.WingetId = we.WingetId; it.Match = "installed (winget export)"; }
            var regs = it.Records.Where(r => r.Source.StartsWith("Registry")).ToList();
            it.PerUser = regs.Count > 0 && regs.All(r => r.Hive != "HKLM");
        }

        // ---- pass 4 ---------------------------------------------------------------------------
        static readonly Regex RuntimeRx = new Regex(
            @"Visual C\+\+.*(Redistributable|Runtime)|\bvcredist|Microsoft Visual C\+\+ 20\d\d|\.NET( Framework| Desktop| Core| Host| Runtime|\s+\d)|ASP\.NET Core|Windows ?App ?Runtime|WindowsAppSDK|\bVCLibs\b|UI\.Xaml|DirectX|WebView2|Java\(TM\).*(Runtime|Update)|Java \d+ Update|\bOpenAL\b|PhysX|ODBC Driver|SQL Server.*(LocalDB|Native Client|Compact|Types)|Universal CRT|Windows Desktop Runtime|Windows SDK|Windows Software Development Kit|Visual Studio.*(Redistributable|Tools for Applications)|Steamworks Common|XNA Framework|Microsoft Edge WebView|vs_.*(runtime|minshell)|Microsoft Windows Desktop Runtime|Python.*(Launcher|Executables|Core Interpreter|Standard Library|Test Suite|Documentation|Development Libraries|pip Bootstrap|Tcl/Tk|Utility Scripts|Add to Path)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RuntimeIdRx = new Regex(@"^Microsoft\.(VCRedist|DotNet\..*(Runtime|Framework)|WindowsAppRuntime|VCLibs|UI\.Xaml|EdgeWebView2Runtime|DirectX)|^Nvidia\.PhysX|OpenAL|^EpicGames\.EpicOnlineServices", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Installed by games or launchers, not by you
        static readonly Regex GameRuntimeRx = new Regex(@"Epic Online Services|EasyAntiCheat|BattlEye|Anti-?Cheat|Steamworks|Ubisoft Game Launcher Redist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Part of Windows: reinstalling them yourself isn't needed
        static readonly Regex WindowsIdRx = new Regex(@"^Microsoft\.(Edge|EdgeWebView2Runtime|AppInstaller|WindowsTerminal\.Preview|Teams\.Classic|OneDrive\.Setup|UpdateHealthTools|WindowsPCHealthCheck)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DriverRx = new Regex(
            @"\bdriver|chipset|NVIDIA|GeForce|\bAMD\b|Radeon|Ryzen Master|Realtek|\bIntel\b|Intel\(R\)|Logitech|G HUB|Razer|Synapse|Corsair|iCUE|\bASUS\b|Armoury|ROG |\bMSI\b|Gigabyte|AORUS|ASRock|Elgato|Stream Deck|Focusrite|Synaptics|Thunderbolt|Wacom|Samsung Magician|Western Digital|Crucial|Bluetooth|Nahimic|Dolby|\bDTS\b|Killer|Qualcomm|MediaTek|Broadcom|Marvell|Tobii|HyperX|NZXT|Lian Li|Thermaltake|Cooler Master|Fractal|Brother|Canon|Epson|HP Smart|\bHP\b.*(Print|Scan)|Printer|Scanner|Firmware|BIOS|ICUE|Tablet|Webcam|Audio Control|Audio Console|USB|Battery|Asmedia",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex WindowsRx = new Regex(@"^(Microsoft Update Health Tools|Windows PC Health Check|Update for |Security Update|Hotfix|Microsoft Edge Update|Microsoft Edge$|Microsoft Edge WebView|Windows Subsystem for|Windows Driver Package|Microsoft Windows|Windows Malicious|Microsoft Policy Platform|Microsoft Intune|Office 16 Click-to-Run|Microsoft Office Click-to-Run|Microsoft 365 - |Microsoft OneDrive$|Teams Machine-Wide|Microsoft GameInput|Windows Assessment|Windows Deployment)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex GameUninstallRx = new Regex(@"steam://uninstall|EADesktop|EA Desktop\\|\\Origin\\|Ubisoft Game Launcher|uplay://|upc\.exe|Rockstar Games\\Launcher|Launcher\.exe.*-uninstall|BsgLauncher|GOG Galaxy|GalaxyClient|epicgameslauncher|com\.epicgames", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string LauncherFromUninstall(string cmd, string publisher)
        {
            cmd = cmd ?? "";
            if (cmd.IndexOf("steam://", StringComparison.OrdinalIgnoreCase) >= 0) return "Steam";
            if (Regex.IsMatch(cmd, @"EADesktop|EA Desktop|\\Origin\\", RegexOptions.IgnoreCase)) return "EA app";
            if (Regex.IsMatch(cmd, @"Ubisoft|uplay|upc\.exe", RegexOptions.IgnoreCase)) return "Ubisoft Connect";
            if (Regex.IsMatch(cmd, @"Rockstar Games", RegexOptions.IgnoreCase)) return "Rockstar Games Launcher";
            if (Regex.IsMatch(cmd, @"GOG Galaxy|GalaxyClient", RegexOptions.IgnoreCase)) return "GOG Galaxy";
            if (Regex.IsMatch(cmd, @"epicgames", RegexOptions.IgnoreCase)) return "Epic";
            if (Regex.IsMatch(cmd + " " + publisher, @"BsgLauncher|Battlestate", RegexOptions.IgnoreCase)) return "Battlestate Games Launcher";
            return null;
        }

        /// <summary>Games installed by a launcher live in that launcher's library folder.</summary>
        static string LauncherFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0) return "Steam";
            if (path.IndexOf(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf(@"\Launcher\", StringComparison.OrdinalIgnoreCase) < 0) return "Epic";
            if (path.IndexOf(@"\Rockstar Games\", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf(@"\Launcher\", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("Social Club", StringComparison.OrdinalIgnoreCase) < 0) return "Rockstar Games Launcher";
            if (path.IndexOf(@"\Ubisoft Game Launcher\games\", StringComparison.OrdinalIgnoreCase) >= 0) return "Ubisoft Connect";
            if (path.IndexOf(@"\EA Games\", StringComparison.OrdinalIgnoreCase) >= 0) return "EA app";
            if (path.IndexOf(@"\GOG Galaxy\Games\", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(@"\GOG Games\", StringComparison.OrdinalIgnoreCase) >= 0) return "GOG Galaxy";
            return null;
        }

        static void Classify(ScanItem it)
        {
            var regs = it.Records.Where(r => r.Source.StartsWith("Registry")).ToList();
            var names = it.Records.Select(r => r.Name).Where(n => n != null).ToList();
            bool AnyName(Regex rx) => names.Any(n => rx.IsMatch(n));

            // Games: from a launcher, or an uninstall entry handled by a launcher.
            if (it.Launcher == null)
                foreach (var r in regs)
                {
                    var l = LauncherFromUninstall(r.UninstallString, r.Publisher);
                    bool isLauncherItself = Regex.IsMatch(r.Name ?? "", @"launcher|galaxy|connect|EA app|^Origin$|^Steam$", RegexOptions.IgnoreCase);
                    if (l != null && !isLauncherItself) { it.Launcher = l; break; }
                }
            if (it.Launcher == null && it.WingetId == null)
                it.Launcher = it.Records.Select(r => LauncherFromPath(r.InstallLocation) ?? LauncherFromPath(r.TargetPath)).FirstOrDefault(l => l != null);
            if (it.Launcher != null) { it.Category = ScanCategory.Game; return; }

            if (AnyName(RuntimeRx) || AnyName(GameRuntimeRx) || (it.WingetId != null && RuntimeIdRx.IsMatch(it.WingetId))) { it.Category = ScanCategory.Runtime; return; }
            if (it.WingetId != null && WindowsIdRx.IsMatch(it.WingetId)) { it.Category = ScanCategory.Windows; it.Flag = "comes with Windows"; return; }

            if (regs.Count > 0 && regs.All(r => r.SystemComponent || !string.IsNullOrEmpty(r.ParentKeyName) ||
                                                 Regex.IsMatch(r.ReleaseType ?? "", "Update|Hotfix", RegexOptions.IgnoreCase))
                && it.Records.All(r => r.Source.StartsWith("Registry") || r.Source == "winget list" && r.WingetId == null))
            { it.Category = ScanCategory.Windows; it.Flag = "hidden system entry"; return; }
            if (AnyName(WindowsRx) && it.WingetId == null) { it.Category = ScanCategory.Windows; return; }
            if (it.Records.Any(r => r.StoreId != null && Regex.IsMatch(r.StoreId, @"_cw5n1h2txyewy$|^Microsoft\.Windows|^MicrosoftWindows|^Windows\.", RegexOptions.IgnoreCase)))
            { it.Category = ScanCategory.Windows; return; }

            // Leftovers: everything that points somewhere points at something that's gone.
            var pointers = it.Records.Where(r => r.Source.StartsWith("Registry") || r.TargetPath != null).ToList();
            if (pointers.Count > 0 && pointers.All(r => r.Missing) && it.WingetId == null &&
                !it.Records.Any(r => r.Source.StartsWith("Program Files") || r.Source.StartsWith("LocalAppData") || r.Source == "AppData"))
            {
                it.Category = ScanCategory.Leftover;
                it.Flag = pointers.Select(r => r.MissingWhat).FirstOrDefault(m => m != null) ?? "target missing";
                return;
            }
            var missing = it.Records.FirstOrDefault(r => r.Missing);
            if (missing != null) it.Flag = missing.MissingWhat + " (some entries)";

            if (AnyName(DriverRx) || DriverRx.IsMatch(it.Publisher ?? "")) { it.Category = ScanCategory.Driver; return; }
            it.Category = ScanCategory.App;
        }

        static bool IsFolderOnly(ScanItem it) => it.Records.All(IsPlaceRecord);

        static readonly HashSet<string> KnownPerUser = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Spotify.Spotify", "Discord.Discord" };

        static void SetDefaults(ScanItem it)
        {
            switch (it.Category)
            {
                case ScanCategory.Runtime:
                case ScanCategory.Windows:
                case ScanCategory.Leftover:
                    it.Include = false; break;
                case ScanCategory.Game:
                    it.Include = true; break;
                default:
                    // A bare program folder (no registry entry, shortcut or startup entry) is often a vendor
                    // folder or leftovers: list it for review, but don't tick it.
                    // Uncertain matches stay ticked but become manual links until you confirm the id.
                    it.Include = !it.Optional && !IsFolderOnly(it);
                    break;
            }
            if (it.WingetId != null && KnownPerUser.Contains(it.WingetId)) it.PerUser = true;
        }

        // ---- pass 3 ---------------------------------------------------------------------------
        void MatchWinget(List<ScanItem> items)
        {
            if (Winget.FindExe() == null) { _log("Pass 3: winget isn't available, so no searches were run."); return; }
            var todo = items.Where(i => string.IsNullOrEmpty(i.WingetId) && !i.Optional && !IsFolderOnly(i) &&
                                        (i.Category == ScanCategory.App || i.Category == ScanCategory.Driver)).ToList();
            _log($"Pass 3: {items.Count(i => i.WingetId != null)} matched from winget export/list; searching winget for {todo.Count} more...");
            if (todo.Count == 0) return;
            var sw = Stopwatch.StartNew();
            int done = 0, confident = 0, uncertain = 0, skipped = 0;
            var gate = new SemaphoreSlim(Math.Max(1, _opt.SearchConcurrency));
            var tasks = todo.Select(it => Task.Run(() =>
            {
                gate.Wait(_cancel);
                try
                {
                    if (sw.Elapsed > _opt.SearchBudget) { Interlocked.Increment(ref skipped); it.Match = "not searched (time limit)"; return; }
                    SearchOne(it);
                    if (it.WingetId != null) { if (it.Uncertain) Interlocked.Increment(ref uncertain); else Interlocked.Increment(ref confident); }
                }
                catch (Exception ex) { it.Match = "search failed: " + ex.Message; }
                finally
                {
                    gate.Release();
                    int d = Interlocked.Increment(ref done);
                    _progress(55 + 43.0 * d / todo.Count, $"Pass 3: winget search {d} of {todo.Count}...");
                }
            })).ToArray();
            Task.WaitAll(tasks, _cancel);
            _log($"  searches: {confident} confident, {uncertain} uncertain (check them), {todo.Count - confident - uncertain - skipped} no match" +
                 (skipped > 0 ? $", {skipped} skipped to stay within the time limit" : "") + $" ({sw.Elapsed.TotalSeconds:N0} s)");
        }

        void SearchOne(ScanItem it)
        {
            var name = it.Name;
            var norm = Names.Normalize(name);
            if (norm.Length < 2) { it.Match = "no match"; return; }
            var pub = Names.PublisherKey(it.Publisher);
            var rawNames = new HashSet<string>(it.Records.Where(r => r.Name != null).Select(r => r.Name.Trim()), StringComparer.OrdinalIgnoreCase);

            WingetRow Pick(List<WingetRow> rows, out bool sure)
            {
                sure = false;
                if (rows.Count == 0) return null;
                var exact = rows.Where(r => Names.Normalize(r.Name) == norm).ToList();
                if (exact.Count == 1) { sure = true; return exact[0]; }
                if (exact.Count > 1)
                {
                    // "Discord" vs "Discord (x86)" / "Discord (arm64)", "Google Chrome" vs "Google Chrome (EXE)":
                    // prefer the one whose name is literally the installed name, then the one without an arch suffix.
                    var literal = exact.Where(r => rawNames.Contains(r.Name.Trim()) || r.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (literal.Count == 1) { sure = true; return literal[0]; }
                    var pool = literal.Count > 1 ? literal : exact;
                    var noArch = pool.Where(r => !Regex.IsMatch(r.Id, @"\.(x86|x64|arm64|arm|EXE|MSI)$", RegexOptions.IgnoreCase) && !Regex.IsMatch(r.Name, @"\((x86|x64|arm64|arm|EXE|MSI)\)", RegexOptions.IgnoreCase)).ToList();
                    if (noArch.Count == 1) { sure = true; return noArch[0]; }
                    var byPub = (noArch.Count > 0 ? noArch : pool).Where(r => pub.Length > 0 && Names.Normalize(r.Id.Split('.')[0]).Replace(" ", "").StartsWith(pub.Replace(" ", ""))).ToList();
                    if (byPub.Count == 1) { sure = true; return byPub[0]; }
                    return (noArch.Count > 0 ? noArch : pool).OrderBy(r => r.Id.Length).First();
                }
                var best = rows.Select(r => (r, s: Names.Similarity(r.Name, name) + (pub.Length > 0 && r.Id.StartsWith(pub, StringComparison.OrdinalIgnoreCase) ? 0.25 : 0)))
                               .OrderByDescending(x => x.s).First();
                if (best.s >= 0.99) { sure = true; return best.r; }
                return best.s >= 0.5 ? best.r : null;
            }

            var first = Winget.Search(name, true, _cancel);
            var pick = Pick(first, out var confident);
            if (!confident)
            {
                // Versioned products ("Python 3.15.2" is the package "Python 3.15"): search with major.minor kept.
                var versioned = it.Records.Select(r => r.Name).Where(n => n != null && Regex.IsMatch(n, @"\b\d+\.\d+"))
                                  .Select(n => Regex.Replace(Regex.Replace(n, @"\([^)]*\)", ""), @"\b(\d+\.\d+)(\.\d+)+\b", "$1").Trim())
                                  .FirstOrDefault();
                if (!string.IsNullOrEmpty(versioned) && !versioned.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var hit = Winget.Search(versioned, true, _cancel).FirstOrDefault(r => r.Name.Trim().Equals(versioned, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) { pick = hit; confident = true; it.Name = hit.Name.Trim(); }
                }
            }
            if (!confident && !string.IsNullOrEmpty(it.Publisher))
            {
                // Second try: publisher + name, matched against the whole id/name/tags.
                var second = Winget.Search(Names.Clean(it.Publisher).Split(' ')[0] + " " + name, false, _cancel);
                var pick2 = Pick(second, out var confident2);
                if (pick2 != null && (confident2 || pick == null)) { pick = pick2; confident = confident2; }
            }
            if (pick == null) { it.Match = first.Count == 0 ? "no match (winget found nothing)" : $"no match ({first.Count} results, none close)"; return; }
            it.WingetId = pick.Id;
            it.Uncertain = !confident;
            it.Match = confident ? $"winget search: \"{pick.Name}\"" : $"UNCERTAIN: winget has \"{pick.Name}\"";
        }

        // ---- pass 5 output ---------------------------------------------------------------------

        /// <summary>Builds an apps.json from the reviewed items.</summary>
        /// <summary>
        /// An installed Microsoft 365 / Office 2024 becomes the Office (Deployment Tool) item: license from its name,
        /// apps from the Start menu shortcuts that are present.
        /// </summary>
        static OfficeOptions OfficeFrom(ScanItem i)
        {
            var names = string.Join(" | ", i.Records.Select(r => r.Name));
            bool isOffice = (i.WingetId ?? "").Equals("Microsoft.Office", StringComparison.OrdinalIgnoreCase) ||
                            Regex.IsMatch(names, @"^(Microsoft 365|Microsoft Office (Home|Professional|Standard)|Office (Home|Professional))", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!isOffice) return null;
            string product =
                Regex.IsMatch(names, @"Apps for enterprise|ProPlus|Professional Plus", RegexOptions.IgnoreCase) ? "O365ProPlusRetail" :
                Regex.IsMatch(names, @"Apps for business|Business", RegexOptions.IgnoreCase) && !Regex.IsMatch(names, "Home", RegexOptions.IgnoreCase) ? "O365BusinessRetail" :
                Regex.IsMatch(names, @"Home (and|&) Business 2024|HomeBusiness2024", RegexOptions.IgnoreCase) ? "HomeBusiness2024Retail" :
                Regex.IsMatch(names, @"Home 2024|Home2024", RegexOptions.IgnoreCase) ? "Home2024Retail" :
                "O365HomePremRetail";
            var apps = new List<string>();
            var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
            foreach (var app in OfficeOptions.AllApps)
            {
                var pattern = app == "Outlook" ? "Outlook*.lnk" : app + "*.lnk";
                try { if (app != "Teams" && app != "OneDrive" && Directory.GetFiles(start, pattern, SearchOption.TopDirectoryOnly).Length > 0) apps.Add(app); } catch { }
            }
            if (apps.Count == 0) apps.AddRange(new[] { "Word", "Excel", "PowerPoint", "Outlook", "OneNote" });
            var o = new OfficeOptions { Product = product, Apps = apps };
            o.Normalize();
            return o;
        }

        public static AppList ToAppList(IEnumerable<ScanItem> items)
        {
            var list = new AppList { Description = $"Built by \"Scan this PC\" on {DateTime.Now:yyyy-MM-dd}." };
            string CatName(ScanItem i)
            {
                switch (i.Category)
                {
                    case ScanCategory.Driver: return "Drivers & hardware tools";
                    case ScanCategory.Runtime: return "Runtimes";
                    case ScanCategory.Windows: return "Windows components";
                    case ScanCategory.Leftover: return "Leftovers";
                    default: return i.Optional ? "Store & other package managers" : "Apps";
                }
            }
            var chosen = items.Where(i => i.Include).ToList();
            bool HasId(ScanItem i) => !string.IsNullOrWhiteSpace(i.WingetId) && !i.WingetId.Contains("\\") && !i.Uncertain;
            foreach (var i in chosen.Where(i => i.Category != ScanCategory.Game && HasId(i)))
            {
                var id = i.WingetId.Trim();
                var office = OfficeFrom(i);
                if (office != null)
                {
                    if (!list.Apps.Any(a => a.Kind == AppKind.Office))
                        list.Apps.Add(new AppItem
                        {
                            Id = "office", Name = "Microsoft Office", Category = "Office", Default = true, Kind = AppKind.Office, Office = office,
                            Note = $"Found \"{i.Name}\". Installed with the Office Deployment Tool; check the license type and apps with Office options.",
                        });
                    continue;
                }
                if (list.Apps.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                list.Apps.Add(new AppItem
                {
                    Id = id, Name = i.Name, Category = CatName(i), Default = true, User = i.PerUser, Kind = AppKind.Winget,
                    Note = "Found in: " + i.Sources + (i.PerUser ? ". Installed per-user, so it installs as your normal user." : ""),
                });
            }
            foreach (var i in chosen.Where(i => i.Category != ScanCategory.Game && !HasId(i)))
                list.ManualLinks.Add(new ManualLink
                {
                    Name = i.Name, Url = i.Homepage,
                    Note = (i.Uncertain ? $"winget may have it as {i.WingetId} (not confirmed). " : "Not in winget. ") + "Found in: " + i.Sources,
                });
            foreach (var g in chosen.Where(i => i.Category == ScanCategory.Game).GroupBy(i => i.Launcher ?? "its launcher").OrderBy(g => g.Key))
                list.ManualLinks.Add(new ManualLink
                {
                    Name = $"Games: reinstall from {g.Key}",
                    Note = string.Join(", ", g.Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                });
            list.Normalize();
            return list;
        }
    }
}
