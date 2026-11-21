using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace WinWipeHelper
{
    /// <summary>Pass 1 of the scan: every place a program can show up.</summary>
    public static class ScanSources
    {
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // ---- winget ----------------------------------------------------------------------------
        public static List<ScanRecord> WingetList(bool includeStore, CancellationToken cancel)
        {
            var list = new List<ScanRecord>();
            foreach (var row in Winget.List(cancel))
            {
                var r = new ScanRecord { Source = "winget list", Name = row.Name, Version = row.Version };
                var id = row.Id ?? "";
                if (row.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)) r.WingetId = id;
                else if (row.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)) { if (!includeStore) continue; r.StoreId = id; r.Optional = true; }
                else if (id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase))
                {
                    // ARP\Machine\X64\<key>: the registry entry winget saw, but didn't map to a package.
                    var parts = id.Split('\\');
                    r.ArpKey = parts.Length >= 4 ? string.Join("\\", parts.Skip(3)) : null;
                }
                else if (id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (!includeStore) continue;
                    r.StoreId = MsixFamily(id.Substring(5));
                    r.Optional = true;
                }
                list.Add(r);
            }
            return list;
        }

        /// <summary>Name_Version_Arch_Resource_PublisherId -> Name_PublisherId</summary>
        public static string MsixFamily(string fullName)
        {
            var p = (fullName ?? "").Split('_');
            return p.Length >= 5 ? p[0] + "_" + p[p.Length - 1] : fullName;
        }

        public static List<ScanRecord> WingetExport(CancellationToken cancel)
        {
            var list = new List<ScanRecord>();
            foreach (var (id, source) in Winget.Export(cancel))
            {
                if (!source.Equals("winget", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ScanRecord { Source = "winget export", Name = id, WingetId = id });
            }
            return list;
        }

        // ---- registry uninstall entries ------------------------------------------------------
        const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<ScanRecord> Registry(out List<string> deniedProfiles)
        {
            var list = new List<ScanRecord>();
            deniedProfiles = new List<string>();
            void Read(RegistryHive hive, RegistryView view, string label)
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, view))
                    using (var k = b.OpenSubKey(UninstallPath))
                        if (k != null) list.AddRange(ReadUninstall(k, label, hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU"));
                }
                catch { }
            }
            Read(RegistryHive.LocalMachine, RegistryView.Registry64, "Registry HKLM");
            if (Environment.Is64BitOperatingSystem) Read(RegistryHive.LocalMachine, RegistryView.Registry32, "Registry HKLM (32-bit)");
            Read(RegistryHive.CurrentUser, RegistryView.Default, "Registry HKCU");

            // Other users' profiles that are loaded right now.
            string me = null;
            try { me = WindowsIdentity.GetCurrent().User?.Value; } catch { }
            foreach (var sid in OtherUserSids(me))
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.Users.OpenSubKey(sid + "\\" + UninstallPath))
                        if (k != null) list.AddRange(ReadUninstall(k, "Registry HKU\\" + sid, "HKU\\" + sid));
                }
                catch (System.Security.SecurityException) { deniedProfiles.Add(sid); }
                catch (UnauthorizedAccessException) { deniedProfiles.Add(sid); }
                catch { }
            }
            return list;
        }

        public static IEnumerable<string> OtherUserSids(string me)
        {
            string[] names;
            try { names = Microsoft.Win32.Registry.Users.GetSubKeyNames(); } catch { yield break; }
            foreach (var n in names)
                if (n.StartsWith("S-1-5-21-") && !n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) && !string.Equals(n, me, StringComparison.OrdinalIgnoreCase))
                    yield return n;
        }

        /// <summary>Reads HKU profiles (run elevated when the normal user can't).</summary>
        public static List<ScanRecord> RegistryUsers(IEnumerable<string> sids)
        {
            var list = new List<ScanRecord>();
            foreach (var sid in sids)
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.Users.OpenSubKey(sid + "\\" + UninstallPath))
                        if (k != null) list.AddRange(ReadUninstall(k, "Registry HKU\\" + sid, "HKU\\" + sid));
                }
                catch { }
            }
            return list;
        }

        static IEnumerable<ScanRecord> ReadUninstall(RegistryKey root, string label, string hive)
        {
            foreach (var sub in SafeSubKeys(root))
            {
                ScanRecord r = null;
                try
                {
                    using (var k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        var name = k.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        r = new ScanRecord
                        {
                            Source = label, Hive = hive, ArpKey = sub, Name = name.Trim(),
                            Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                            Version = k.GetValue("DisplayVersion") as string,
                            InstallLocation = Paths.NormDir(k.GetValue("InstallLocation") as string),
                            UninstallString = k.GetValue("UninstallString") as string,
                            DisplayIcon = k.GetValue("DisplayIcon") as string,
                            Homepage = FirstUrl(k.GetValue("URLInfoAbout") as string, k.GetValue("HelpLink") as string, k.GetValue("URLUpdateInfo") as string),
                            SystemComponent = ToInt(k.GetValue("SystemComponent")) == 1,
                            ParentKeyName = k.GetValue("ParentKeyName") as string,
                            WindowsInstaller = ToInt(k.GetValue("WindowsInstaller")) == 1,
                            ReleaseType = k.GetValue("ReleaseType") as string,
                            Detail = root.Name + "\\" + sub,
                        };
                        if (sub.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) { r.Launcher = "Steam"; r.GameId = sub.Substring(10).Trim(); }
                        CheckMissing(r);
                    }
                }
                catch { }
                if (r != null) yield return r;
            }
        }

        static IEnumerable<string> SafeSubKeys(RegistryKey k)
        {
            try { return k.GetSubKeyNames(); } catch { return Enumerable.Empty<string>(); }
        }

        static int ToInt(object v) { try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; } }

        static string FirstUrl(params string[] urls)
        {
            foreach (var u in urls)
            {
                var s = (u ?? "").Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s;
                if (Regex.IsMatch(s, @"^[\w-]+(\.[\w-]+)+(/.*)?$")) return "https://" + s;
            }
            return null;
        }

        /// <summary>Flags uninstall entries whose uninstaller or install folder is gone.</summary>
        public static void CheckMissing(ScanRecord r)
        {
            var exe = ExtractExe(r.UninstallString);
            bool msi = (r.UninstallString ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!msi && exe != null && Path.IsPathRooted(exe) && !File.Exists(exe))
            {
                r.Missing = true; r.MissingWhat = "uninstaller missing";
            }
            if (r.InstallLocation != null && !Directory.Exists(r.InstallLocation))
            {
                r.Missing = true; r.MissingWhat = r.MissingWhat == null ? "install folder missing" : "uninstaller and install folder missing";
            }
        }

        /// <summary>The program path at the start of a command line ("C:\x\y.exe" -args, or C:\x\y.exe -args).</summary>
        public static string ExtractExe(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            string path;
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                path = end > 1 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
            }
            else
            {
                var m = Regex.Match(cmd, @"^(.+?\.(exe|com|bat|cmd|msi|lnk))(\s|,|$)", RegexOptions.IgnoreCase);
                path = m.Success ? m.Groups[1].Value : cmd.Split(' ')[0];
            }
            path = path.Trim().TrimEnd(',');
            int comma = path.LastIndexOf(',');            // DisplayIcon "C:\app.exe,0"
            if (comma > 2 && !File.Exists(path)) path = path.Substring(0, comma);
            return path.Length > 0 ? path : null;
        }

        // ---- shortcuts ------------------------------------------------------------------------
        static readonly Regex SkipShortcut = new Regex(@"uninst|readme|read me|help|manual|documentation|license|licence|website|web site|release notes|changelog|support|faq|what's new|register|online", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<ScanRecord> Shortcuts()
        {
            var list = new List<ScanRecord>();
            var places = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Start Menu (all users)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Start Menu"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Desktop (public)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop"),
            };
            foreach (var (dir, label) in places)
            {
                foreach (var f in Shell.SafeFiles(dir, "*.lnk", true).Concat(Shell.SafeFiles(dir, "*.url", true)))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (SkipShortcut.IsMatch(name)) continue;
                    var info = Shell.ReadLink(f);
                    if (info == null) continue;
                    var r = FromTarget(label, name, info, f);
                    if (r != null) list.Add(r);
                }
            }
            return list;
        }

        static ScanRecord FromTarget(string label, string name, Shell.LinkInfo info, string file)
        {
            if (!string.IsNullOrEmpty(info.Url))
            {
                var m = Regex.Match(info.Url, @"^steam://rungameid/(\d+)", RegexOptions.IgnoreCase);
                if (m.Success) return new ScanRecord { Source = label, Name = name, Launcher = "Steam", GameId = m.Groups[1].Value, Detail = file };
                m = Regex.Match(info.Url, @"^com\.epicgames\.launcher://apps/([^?/]+)", RegexOptions.IgnoreCase);
                if (m.Success) return new ScanRecord { Source = label, Name = name, Launcher = "Epic", GameId = Uri.UnescapeDataString(m.Groups[1].Value), Detail = file };
                return null; // web links etc.
            }
            var target = info.Target;
            if (string.IsNullOrWhiteSpace(target)) return null;   // Store-app shortcuts have no file target
            var ext = Path.GetExtension(target).ToLowerInvariant();
            if (ext != ".exe" && ext != ".cmd" && ext != ".bat") return null;
            if (Paths.IsUnder(target, WinDir)) return null;       // Windows tools
            if (SkipShortcut.IsMatch(Path.GetFileName(target))) return null;
            var r = new ScanRecord { Source = label, Name = name, TargetPath = target, Detail = file };
            if (!File.Exists(target)) { r.Missing = true; r.MissingWhat = "shortcut target missing"; }
            // Launcher shortcuts that start a game: steam.exe -applaunch 123
            var a = info.Arguments ?? "";
            var sm = Regex.Match(a, @"-applaunch\s+(\d+)");
            if (sm.Success && target.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)) { r.Launcher = "Steam"; r.GameId = sm.Groups[1].Value; r.TargetPath = null; }
            return r;
        }

        // ---- program folders ------------------------------------------------------------------
        static readonly HashSet<string> SkipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Common Files", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player",
            "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsApps",
            "WindowsPowerShell", "Microsoft Update Health Tools", "Internet Explorer", "Reference Assemblies", "MSBuild",
            "ModifiableWindowsApps", "Uninstall Information", "Microsoft.NET", "dotnet", "Package Cache", "Microsoft", "Temp",
            "Windows Kits", "Microsoft SDKs", "IIS", "IIS Express", "Microsoft Analysis Services", "Microsoft SQL Server",
            "desktop.ini", "Packages", "WinGet", "Programs", "Microsoft Visual Studio", "NuGet", "CrashDumps", "D3DSCache",
        };

        public static List<ScanRecord> ProgramFolders()
        {
            var list = new List<ScanRecord>();
            var pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var places = new List<(string, string)> { (pf, "Program Files") };
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86) && !pf86.Equals(pf, StringComparison.OrdinalIgnoreCase)) places.Add((pf86, "Program Files (x86)"));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            places.Add((Path.Combine(local, "Programs"), "LocalAppData\\Programs"));
            places.Add((Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppData"));

            foreach (var (root, label) in places)
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); } catch { continue; }
                foreach (var d in dirs)
                {
                    var name = Path.GetFileName(d);
                    if (SkipFolders.Contains(name) || name.StartsWith(".")) continue;
                    try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    var exe = FindExe(d, 3);
                    if (exe == null) continue;
                    list.Add(new ScanRecord { Source = label, Name = name, InstallLocation = Paths.NormDir(d), TargetPath = exe, Detail = d });
                }
            }
            return list;
        }

        /// <summary>First .exe within a few levels (skipping uninstallers), or null.</summary>
        static string FindExe(string dir, int depth)
        {
            var queue = new Queue<(string, int)>();
            queue.Enqueue((dir, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 60)
            {
                var (d, lvl) = queue.Dequeue();
                visited++;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d, "*.exe"))
                        if (!Regex.IsMatch(Path.GetFileName(f), @"unins|uninstall|setup|update|crash|helper|install", RegexOptions.IgnoreCase))
                            return f;
                    if (lvl < depth)
                        foreach (var s in Directory.EnumerateDirectories(d))
                        {
                            try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                            queue.Enqueue((s, lvl + 1));
                        }
                }
                catch { }
            }
            return null;
        }

        // ---- startup entries -------------------------------------------------------------------
        public static List<ScanRecord> Startup()
        {
            var list = new List<ScanRecord>();
            foreach (var (dir, label) in new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)"),
            })
            {
                foreach (var f in Shell.SafeFiles(dir, "*.lnk", false))
                {
                    var info = Shell.ReadLink(f);
                    if (info == null || string.IsNullOrEmpty(info.Target)) continue;
                    if (Paths.IsUnder(info.Target, WinDir)) continue;
                    var r = new ScanRecord { Source = label, Name = Path.GetFileNameWithoutExtension(f), TargetPath = info.Target, Detail = f };
                    if (!File.Exists(info.Target)) { r.Missing = true; r.MissingWhat = "startup target missing"; }
                    list.Add(r);
                }
            }

            foreach (var (hive, view, path, label) in new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run key (HKCU)"),
                (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce key (HKCU)"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run key (HKLM)"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce key (HKLM)"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run key (HKLM 32-bit)"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "RunOnce key (HKLM 32-bit)"),
            })
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(hive, view))
                    using (var k = b.OpenSubKey(path))
                    {
                        if (k == null) continue;
                        foreach (var v in k.GetValueNames())
                        {
                            var exe = ExtractExe(k.GetValue(v) as string);
                            if (exe == null || !Path.IsPathRooted(exe) || Paths.IsUnder(exe, WinDir)) continue;
                            var r = new ScanRecord { Source = label, Name = v, TargetPath = exe, Detail = k.Name + " : " + v };
                            if (!File.Exists(exe)) { r.Missing = true; r.MissingWhat = "startup target missing"; }
                            list.Add(r);
                        }
                    }
                }
                catch { }
            }

            foreach (var t in ScheduledTasks.All())
            {
                if (t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                var exe = t.Exe;
                if (string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe) || Paths.IsUnder(exe, WinDir)) continue;
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var r = new ScanRecord { Source = "Scheduled task", Name = t.Name, TargetPath = exe, Detail = t.Path };
                if (!File.Exists(exe)) { r.Missing = true; r.MissingWhat = "task target missing"; }
                list.Add(r);
            }
            return list;
        }

        // ---- game launchers ---------------------------------------------------------------------
        public static List<ScanRecord> Games()
        {
            var list = new List<ScanRecord>();
            try { list.AddRange(Steam()); } catch { }
            try { list.AddRange(Epic()); } catch { }
            try { list.AddRange(RegistryGames()); } catch { }
            return list;
        }

        static IEnumerable<ScanRecord> Steam()
        {
            string steam = null;
            try { steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
            if (string.IsNullOrEmpty(steam))
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    using (var k = b.OpenSubKey(@"SOFTWARE\Valve\Steam")) steam = k?.GetValue("InstallPath") as string;
                }
                catch { }
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam.Replace('/', '\\');
            var libs = new List<string> { steam };
            var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string text = "";
                try { text = File.ReadAllText(vdf); } catch { }
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            var seen = new HashSet<string>();
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var acf in Shell.SafeFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf", false))
                {
                    string text;
                    try { text = File.ReadAllText(acf); } catch { continue; }
                    var id = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    var name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (id.Length == 0 || !seen.Add(id)) continue;
                    if (id == "228980") continue; // Steamworks Common Redistributables
                    yield return new ScanRecord
                    {
                        Source = "Steam library", Name = name.Length > 0 ? name : "Steam app " + id, Launcher = "Steam", GameId = id,
                        InstallLocation = dir.Length > 0 ? Paths.NormDir(Path.Combine(lib, "steamapps", "common", dir)) : null, Detail = acf,
                    };
                }
            }
        }

        static IEnumerable<ScanRecord> Epic()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            foreach (var f in Shell.SafeFiles(dir, "*.item", false))
            {
                object j;
                try { j = Json.Parse(File.ReadAllText(f)); } catch { continue; }
                var name = Json.Str(j, "DisplayName");
                if (string.IsNullOrEmpty(name)) continue;
                yield return new ScanRecord
                {
                    Source = "Epic manifests", Name = name, Launcher = "Epic", GameId = Json.Str(j, "AppName"),
                    InstallLocation = Paths.NormDir(Json.Str(j, "InstallLocation")), Detail = f,
                };
            }
        }

        static IEnumerable<ScanRecord> RegistryGames()
        {
            var found = new List<ScanRecord>();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                // Ubisoft Connect
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs"))
                    if (k != null)
                        foreach (var id in SafeSubKeys(k))
                            using (var g = k.OpenSubKey(id))
                            {
                                var d = Paths.NormDir((g?.GetValue("InstallDir") as string)?.Replace('/', '\\'));
                                if (d != null) found.Add(new ScanRecord { Source = "Ubisoft Connect", Name = Path.GetFileName(d), Launcher = "Ubisoft Connect", GameId = id, InstallLocation = d });
                            }
                // Rockstar Games Launcher
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Rockstar Games"))
                    if (k != null)
                        foreach (var n in SafeSubKeys(k))
                        {
                            if (Regex.IsMatch(n, "Launcher|Social Club", RegexOptions.IgnoreCase)) continue;
                            using (var g = k.OpenSubKey(n))
                            {
                                var d = Paths.NormDir(g?.GetValue("InstallFolder") as string);
                                if (d != null) found.Add(new ScanRecord { Source = "Rockstar Games Launcher", Name = n, Launcher = "Rockstar Games Launcher", InstallLocation = d });
                            }
                        }
                // EA app / Origin
                foreach (var root in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts" })
                    using (var k = hklm.OpenSubKey(root))
                        if (k != null)
                            foreach (var n in SafeSubKeys(k))
                            {
                                if (Regex.IsMatch(n, @"^(EA Desktop|EA Core|EADM|Origin|EA app)$", RegexOptions.IgnoreCase)) continue;
                                using (var g = k.OpenSubKey(n))
                                {
                                    var d = Paths.NormDir(g?.GetValue("Install Dir") as string);
                                    if (d != null) found.Add(new ScanRecord { Source = "EA app", Name = n, Launcher = "EA app", InstallLocation = d });
                                }
                            }
                // GOG Galaxy
                using (var k = hklm.OpenSubKey(@"SOFTWARE\GOG.com\Games"))
                    if (k != null)
                        foreach (var id in SafeSubKeys(k))
                            using (var g = k.OpenSubKey(id))
                            {
                                var name = g?.GetValue("gameName") as string;
                                if (!string.IsNullOrEmpty(name))
                                    found.Add(new ScanRecord { Source = "GOG Galaxy", Name = name, Launcher = "GOG Galaxy", GameId = id, InstallLocation = Paths.NormDir(g.GetValue("path") as string) });
                            }
            }
            return found;
        }

        // ---- optional sources --------------------------------------------------------------------
        public static List<ScanRecord> StoreApps(CancellationToken cancel)
        {
            var list = new List<ScanRecord>();
            // PackageManager through PowerShell's Appx module: non-framework, Store-signed packages only.
            const string cmd =
                "Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -eq 'Store' -and -not $_.IsResourcePackage } | ForEach-Object {" +
                " $n = $null; try { $n = (Get-AppxPackageManifest $_).Package.Properties.DisplayName } catch {};" +
                " if (-not $n -or $n -like 'ms-resource*') { $n = $_.Name };" +
                " '{0}|{1}|{2}|{3}' -f $n, $_.PackageFamilyName, $_.Version, $_.InstallLocation }";
            var r = ProcessRunner.PowerShell(cmd, null, 120000, cancel);
            foreach (var line in (r.Output ?? "").Split('\n'))
            {
                var p = line.Trim().Split('|');
                if (p.Length < 4 || p[0].Length == 0) continue;
                list.Add(new ScanRecord { Source = "Microsoft Store", Name = p[0], StoreId = p[1], Version = p[2], Optional = true, Detail = p[3] });
            }
            return list;
        }

        public static List<ScanRecord> Chocolatey()
        {
            var list = new List<ScanRecord>();
            var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
            try
            {
                foreach (var d in Directory.GetDirectories(Path.Combine(root, "lib")))
                {
                    var n = Path.GetFileName(d);
                    if (n.StartsWith("chocolatey", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".install", StringComparison.OrdinalIgnoreCase) && Directory.Exists(d.Substring(0, d.Length - 8))) continue;
                    list.Add(new ScanRecord { Source = "Chocolatey", Name = Regex.Replace(n, @"\.(install|portable)$", "", RegexOptions.IgnoreCase), Optional = true, Detail = d });
                }
            }
            catch { }
            return list;
        }

        public static List<ScanRecord> Scoop()
        {
            var list = new List<ScanRecord>();
            var roots = new[]
            {
                Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"),
                Environment.GetEnvironmentVariable("SCOOP_GLOBAL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop"),
            };
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var d in Directory.GetDirectories(Path.Combine(root, "apps")))
                    {
                        var n = Path.GetFileName(d);
                        if (n.Equals("scoop", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new ScanRecord { Source = "Scoop", Name = n, Optional = true, InstallLocation = Paths.NormDir(Path.Combine(d, "current")), Detail = d });
                    }
                }
                catch { }
            }
            return list;
        }
    }
}
