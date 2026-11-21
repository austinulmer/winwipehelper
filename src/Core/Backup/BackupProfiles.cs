using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinWipeHelper
{
    public sealed class BackupSelection
    {
        public List<string> Paths = new List<string>();
        public List<string> ExtraFolders = new List<string>();   // folders added with "Add another folder..."
        public string Destination;
    }

    /// <summary>Saved backup selections ("profiles") and the last selection, stored next to the EXE.</summary>
    public sealed class BackupProfiles
    {
        public BackupSelection Last;
        public Dictionary<string, BackupSelection> Profiles = new Dictionary<string, BackupSelection>(StringComparer.OrdinalIgnoreCase);

        public static string FilePath => Path.Combine(WinWipeHelper.Paths.DataDir, "backup-profiles.json");

        public static BackupProfiles Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var p = Json.Deserialize<BackupProfiles>(File.ReadAllText(FilePath));
                    if (p.Profiles == null) p.Profiles = new Dictionary<string, BackupSelection>(StringComparer.OrdinalIgnoreCase);
                    return p;
                }
            }
            catch { }
            return new BackupProfiles();
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, Json.Serialize(this), new UTF8Encoding(false)); } catch { }
        }
    }
}
