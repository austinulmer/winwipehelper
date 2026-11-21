using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinWipeHelper
{
    /// <summary>The apps.json file: categories, apps and manual links.</summary>
    public sealed class AppList
    {
        [JsonAlways] public int Version = 1;
        public string Description;
        public List<string> Categories = new List<string>();
        public List<AppItem> Apps = new List<AppItem>();
        public List<ManualLink> ManualLinks = new List<ManualLink>();

        public void Normalize()
        {
            Categories = Categories ?? new List<string>();
            Apps = (Apps ?? new List<AppItem>()).Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id)).ToList();
            ManualLinks = (ManualLinks ?? new List<ManualLink>()).Where(m => m != null && !string.IsNullOrWhiteSpace(m.Name)).ToList();
            foreach (var a in Apps)
            {
                if (string.IsNullOrWhiteSpace(a.Kind)) a.Kind = AppKind.Winget;
                a.Kind = a.Kind.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(a.Name)) a.Name = a.Id;
                if (string.IsNullOrWhiteSpace(a.Category)) a.Category = a.Kind == AppKind.Download ? "Download only (you run the installer)" : "Other";
                if (a.Kind == AppKind.Office) { if (a.Office == null) a.Office = new OfficeOptions(); a.Office.Normalize(); }
            }
            // Categories used by apps but not listed go at the end, in first-use order.
            foreach (var c in Apps.Select(a => a.Category).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!Categories.Contains(c, StringComparer.OrdinalIgnoreCase)) Categories.Add(c);
            // Duplicate ids: keep the first.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Apps = Apps.Where(a => seen.Add(a.Id)).ToList();
        }

        public static AppList Parse(string json)
        {
            var list = Json.Deserialize<AppList>(json);
            if (list == null) throw new InvalidDataException("The file is empty.");
            list.Normalize();
            return list;
        }

        public string ToJson() => Json.Serialize(this);

        public AppList Clone() => Parse(ToJson());
    }

    public static class AppKind
    {
        public const string Winget = "winget";
        public const string Download = "download";
        public const string Office = "office";
    }

    public sealed class AppItem
    {
        public string Id;
        public string Name;
        public string Category;
        [JsonAlways] public bool Default;
        public string Note;
        public bool User;               // install as the logged-in user without elevation
        public string Override;         // winget --override arguments
        public string Kind = AppKind.Winget;
        // download-only items
        public string Url;
        public string FileName;
        public string Signer;           // signer subject must contain this
        public string Github;           // "owner/repo": take the latest release asset matching AssetPattern
        public string AssetPattern;
        // office item
        public OfficeOptions Office;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Name).Append("   [").Append(Kind == AppKind.Winget ? Id : Kind == AppKind.Office ? "Office Deployment Tool" : "download").Append(']');
            if (User) sb.Append("   installs as your normal user");
            if (!string.IsNullOrEmpty(Note)) sb.Append("\r\n").Append(Note);
            if (Kind == AppKind.Office && Office != null) sb.Append("\r\n").Append(Office.Summary());
            if (Kind == AppKind.Download) sb.Append("\r\n").Append(string.IsNullOrEmpty(Github) ? Url : $"Latest release of github.com/{Github} ({AssetPattern})");
            return sb.ToString();
        }
    }

    public sealed class ManualLink
    {
        public string Name;
        public string Url;
        public string Note;
    }

    /// <summary>Where the current list came from, and loading/saving.</summary>
    public static class Catalog
    {
        public const string FileName = "apps.json";
        public const string ResourceName = "WinWipeHelper.apps.json";

        public static string NextToExe => Path.Combine(Paths.ExeDir, FileName);

        public static string EmbeddedJson()
        {
            using (var s = typeof(Catalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) return "{\"apps\":[]}";
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static AppList LoadEmbedded() => AppList.Parse(EmbeddedJson());

        /// <summary>apps.json next to the EXE wins; otherwise the list built into the EXE.</summary>
        public static AppList LoadStartup(out string source, out string warning)
        {
            warning = null;
            if (File.Exists(NextToExe))
            {
                try
                {
                    source = NextToExe;
                    return AppList.Parse(File.ReadAllText(NextToExe));
                }
                catch (Exception ex)
                {
                    warning = $"The {FileName} next to this program couldn't be read, so the built-in list is used instead.\n\n{ex.Message}";
                }
            }
            source = "built-in list";
            return LoadEmbedded();
        }

        public static void Save(AppList list, string path)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, list.ToJson(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
