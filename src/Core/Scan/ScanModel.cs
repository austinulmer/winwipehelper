using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinWipeHelper
{
    /// <summary>One sighting of a program in one source (a registry entry, a shortcut, a winget row...).</summary>
    public sealed class ScanRecord
    {
        public string Source;           // e.g. "Registry HKLM", "Start Menu", "winget list"
        public string Name;
        public string Publisher;
        public string Version;
        public string InstallLocation;
        public string UninstallString;
        public string DisplayIcon;
        public string Homepage;
        public string TargetPath;       // exe for shortcuts, startup entries, tasks
        public string WingetId;         // from winget list/export (source winget)
        public string StoreId;          // msstore id or MSIX family name
        public string ArpKey;           // registry subkey name (matches winget's ARP\...\<key> ids)
        public string Hive;             // HKLM / HKCU / HKU\SID
        public bool SystemComponent;
        public string ParentKeyName;
        public bool WindowsInstaller;
        public string ReleaseType;
        public bool Missing;            // the thing it points at is gone
        public string MissingWhat;
        public string Launcher;         // Steam, Epic, ...
        public string GameId;
        public bool Optional;           // from an optional source (Store, Chocolatey, Scoop)
        public string Detail;           // e.g. the shortcut's path

        public override string ToString() => $"[{Source}] {Name}";
    }

    public enum ScanCategory { App, Driver, Game, Runtime, Windows, Leftover }

    /// <summary>A program after merging every record that refers to it.</summary>
    public sealed class ScanItem
    {
        public readonly List<ScanRecord> Records = new List<ScanRecord>();
        public string Name;
        public string Publisher;
        public string WingetId;
        public string Match = "";           // how the winget id was found
        public bool Uncertain;
        public ScanCategory Category;
        public bool Include;
        public string Homepage;
        public string Launcher;
        public bool Optional;
        public bool PerUser;
        public string Flag = "";            // e.g. "uninstaller missing"

        public string Sources => string.Join(", ", Records.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase));

        public static string CategoryLabel(ScanCategory c)
        {
            switch (c)
            {
                case ScanCategory.App: return "App";
                case ScanCategory.Driver: return "Driver / hardware";
                case ScanCategory.Game: return "Game";
                case ScanCategory.Runtime: return "Runtime";
                case ScanCategory.Windows: return "Windows component";
                case ScanCategory.Leftover: return "Leftover";
            }
            return c.ToString();
        }
    }

    /// <summary>Name normalization used to merge records and compare against winget.</summary>
    public static class Names
    {
        // A parenthesized group is dropped when every word in it is noise: (x64), (64-bit x64), (x64 en-US), (User), (Remove only)...
        static readonly Regex ParenGroup = new Regex(@"[\(\[]([^()\[\]]*)[\)\]]", RegexOptions.Compiled);
        static readonly Regex NoiseWord = new Regex(@"^(x64|x86|amd64|arm64|arm|ia64|64|32|bit|64bit|32bit|win64|win32|user|machine|per|all|users|current|edition|remove|only|[a-z]{2}|[a-z]{2}-[a-z]{2,4}|v?\d+(\.\d+)*[a-z]?|version|build|release|portable|installer|msi|exe|setup)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static string StripParens(string s) => ParenGroup.Replace(s, m =>
        {
            var words = Regex.Split(m.Groups[1].Value, @"[\s,;/_-]+").Where(w => w.Length > 0).ToList();
            return words.Count == 0 || words.All(w => NoiseWord.IsMatch(w)) ? " " : m.Value;
        });
        static readonly Regex ArchWords = new Regex(@"\b(x64|x86|amd64|arm64|64[- ]?bit|32[- ]?bit|win64|win32|edition)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Version = new Regex(@"(\bv|\bversion\s*)?\b\d+(\.\d+){1,4}([a-z]|[-+][\w.]+)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Junk = new Regex(@"[™®©]", RegexOptions.Compiled);
        static readonly Regex NonWord = new Regex(@"[^\p{L}\p{N}+#]+", RegexOptions.Compiled);

        /// <summary>"7-Zip 26.06 (x64 edition)" -> "7 zip"; "Discord (User)" -> "discord".</summary>
        static readonly Regex ShortcutNoise = new Regex(@"(\.exe)?(\s*-\s*Shortcut)?\s*$|^Shortcut to\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var s = Junk.Replace(ShortcutNoise.Replace(name.Trim(), ""), "");
            s = StripParens(s);
            s = Version.Replace(s, " ");
            s = ArchWords.Replace(s, " ");
            s = NonWord.Replace(s.ToLowerInvariant(), " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        /// <summary>Display name with version / arch noise removed, but case kept.</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            name = ShortcutNoise.Replace(name.Trim(), "");
            var s = Junk.Replace(name, "");
            s = StripParens(s);
            s = Version.Replace(s, " ");
            s = Regex.Replace(s, @"\s+-\s*$", "");
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', ',');
            return s.Length >= 2 ? s : name.Trim();
        }

        public static HashSet<string> Tokens(string name) =>
            new HashSet<string>(Normalize(name).Split(' ').Where(t => t.Length > 0));

        /// <summary>Jaccard similarity of the normalized word sets (0..1).</summary>
        public static double Similarity(string a, string b)
        {
            var x = Tokens(a); var y = Tokens(b);
            if (x.Count == 0 || y.Count == 0) return 0;
            return x.Intersect(y).Count() / (double)x.Union(y).Count();
        }

        /// <summary>First meaningful word of a publisher, for checking winget ids like "Mozilla.Firefox".</summary>
        public static string PublisherKey(string publisher)
        {
            var n = Normalize(Regex.Replace(publisher ?? "", @"\b(inc|llc|ltd|gmbh|corp|corporation|co|limited|s\.?a|ag|bv|software|technologies|the)\b\.?", "", RegexOptions.IgnoreCase));
            return n.Split(' ').FirstOrDefault(t => t.Length >= 2) ?? "";
        }
    }
}
