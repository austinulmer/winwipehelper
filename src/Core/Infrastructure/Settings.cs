using System;
using System.IO;
using System.Text;

namespace WinWipeHelper
{
    /// <summary>Small preferences kept in settings.json (next to the EXE, or in %LOCALAPPDATA%\WinWipeHelper).</summary>
    public sealed class Settings
    {
        /// <summary>Download the icon from an app's own website when the app isn't installed.</summary>
        [JsonAlways] public bool WebIcons = true;

        public static string FilePath => Path.Combine(Paths.DataDir, "settings.json");

        static Settings _current;
        public static Settings Current
        {
            get
            {
                if (_current != null) return _current;
                try { if (File.Exists(FilePath)) _current = Json.Deserialize<Settings>(File.ReadAllText(FilePath)); } catch { }
                return _current ?? (_current = new Settings());
            }
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, Json.Serialize(this), new UTF8Encoding(false)); } catch { }
        }
    }
}
