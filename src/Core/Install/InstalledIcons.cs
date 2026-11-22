using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinWipeHelper
{
    /// <summary>
    /// Finds the icon of an app that's already installed: its Start Menu shortcut or the icon its
    /// uninstall entry names. Built once from the registry and the Start Menu (well under a second).
    /// </summary>
    public sealed class InstalledIcons
    {
        public sealed class Entry
        {
            public string Name, Norm, Icon;
            public bool Shortcut;
        }

        public readonly List<Entry> Entries;
        public InstalledIcons(List<Entry> entries) { Entries = entries; }

        static InstalledIcons _shared;
        static readonly object Gate = new object();

        public static InstalledIcons Shared { get { lock (Gate) return _shared ?? (_shared = Build()); } }

        /// <summary>Forget what was found (after installing something).</summary>
        public static void Reset() { lock (Gate) _shared = null; }

        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        static readonly Regex InstallerExe = new Regex(@"^(unins\d*|uninst\w*|uninstall\w*|setup\w*|install\w*|update\w*|maintenancetool)\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>An icon location worth showing: the file exists, and it isn't an uninstaller or a Windows file.</summary>
        public static string UsableIcon(string location)
        {
            var (path, _) = AppIcons.ParseIconLocation(location);
            if (path == null || !Path.IsPathRooted(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                if (InstallerExe.IsMatch(Path.GetFileName(path))) return null;
                if (Paths.IsUnder(path, WinDir) && path.IndexOf(@"\Installer\", StringComparison.OrdinalIgnoreCase) < 0) return null;
            }
            catch { return null; }
            return location;
        }

        public static InstalledIcons Build()
        {
            var entries = new List<Entry>();
            try
            {
                foreach (var r in ScanSources.Registry(out _))
                {
                    var icon = UsableIcon(r.DisplayIcon);
                    if (icon != null) entries.Add(new Entry { Name = r.Name, Norm = Names.Normalize(r.Name), Icon = icon });
                }
            }
            catch { }
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
                foreach (var f in Shell.SafeFiles(dir, "*.lnk", true))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (Regex.IsMatch(name, @"uninst|readme|help|manual|license|website|release notes|changelog", RegexOptions.IgnoreCase)) continue;
                    entries.Add(new Entry { Name = name, Norm = Names.Normalize(name), Icon = f, Shortcut = true });
                }
            return new InstalledIcons(entries.Where(e => e.Norm.Length > 0).ToList());
        }

        /// <summary>Normalized names an app might be installed under, best first; the flag says whether a longer installed name may also match.</summary>
        public static List<(string name, bool prefix)> Candidates(AppItem a)
        {
            var list = new List<(string, bool)>();
            void Add(string s, bool prefix)
            {
                s = Names.Normalize(s);
                if (s.Length >= 3 && !list.Any(x => x.Item1 == s)) list.Add((s, prefix && s.Length >= 4));
                var trimmed = Regex.Replace(s, @"(\s\d+)+$", "");               // "gimp 3" -> "gimp"
                if (trimmed != s && trimmed.Length >= 3 && !list.Any(x => x.Item1 == trimmed)) list.Add((trimmed, false));
            }
            Add(a.Name, true);
            Add(Regex.Replace(a.Name ?? "", @"\s*[\(\[][^)\]]*[\)\]]", " "), true);   // "Everything (instant file search)" -> "Everything"
            if (a.Kind == AppKind.Winget && !string.IsNullOrEmpty(a.Id))
            {
                var parts = a.Id.Split('.');
                Add(string.Join(" ", parts), false);                                    // Mozilla.Firefox -> "mozilla firefox"
                if (parts.Length > 1) Add(string.Join(" ", parts.Skip(1)), false);       // -> "firefox"
            }
            if (a.Kind == AppKind.Office) { Add("Microsoft 365", true); Add("Microsoft Office", true); }
            return list;
        }

        /// <summary>The installed icon for a list item, or null when it doesn't look installed.</summary>
        public string Find(AppItem a)
        {
            var cands = Candidates(a);
            Entry best = null;
            int bestScore = 0;
            foreach (var e in Entries)
            {
                int score = 0;
                for (int i = 0; i < cands.Count && score == 0; i++)
                {
                    var (c, prefix) = cands[i];
                    if (e.Norm == c) score = 100 - i;
                    else if (prefix && e.Norm.StartsWith(c + " ")) score = 50 - i;
                }
                if (score == 0) continue;
                if (e.Shortcut) score += 1;    // shortcut icons are what the user sees in Start
                if (score > bestScore || (score == bestScore && best != null && e.Norm.Length < best.Norm.Length)) { best = e; bestScore = score; }
            }
            return best?.Icon;
        }

        /// <summary>The installed icon for a program known only by its name (an uninstall entry).</summary>
        public string FindByName(string name) => string.IsNullOrWhiteSpace(name) ? null : Find(new AppItem { Id = name, Name = name, Kind = AppKind.Download });

        /// <summary>The best icon among a scanned program's sightings: a shortcut, its uninstall entry's icon, then its exe.</summary>
        public static string Find(ScanItem it)
        {
            foreach (var r in it.Records)
                if (r.Detail != null && r.Detail.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(r.Detail)) return r.Detail;
            foreach (var r in it.Records) { var i = UsableIcon(r.DisplayIcon); if (i != null) return i; }
            foreach (var r in it.Records)
                if (r.TargetPath != null && r.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && UsableIcon(r.TargetPath) != null) return r.TargetPath;
            return Shared.FindByName(it.Name);
        }

        /// <summary>The website whose icon stands in for an app that isn't installed.</summary>
        public static string HomepageOf(AppItem a)
        {
            if (!string.IsNullOrWhiteSpace(a.Homepage)) return a.Homepage;
            if (a.Kind == AppKind.Office) return "https://www.microsoft.com/microsoft-365";
            if (!string.IsNullOrEmpty(a.Github)) return "https://github.com/" + a.Github;
            if (a.Kind == AppKind.Download) return a.Url;
            return null;
        }
    }
}
