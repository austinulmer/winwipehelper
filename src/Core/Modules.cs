using System.Collections.Generic;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>
    /// Which tabs and worker jobs this build has. The core adds its own. Another build can add more
    /// by including extra source files that implement the partial methods below; when nothing
    /// implements them, the compiler removes the calls entirely.
    /// </summary>
    static partial class Modules
    {
        public static List<TabPage> CreateTabs()
        {
            var tabs = new List<TabPage>
            {
                MakeTab("Install apps", new InstallTab()),
                MakeTab("Back up / restore files", new BackupTab()),
            };
            AddPersonalTabs(tabs);
            return tabs;
        }

        public static TabPage MakeTab(string title, Control content)
        {
            var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        static bool _registered;
        public static void RegisterWorkers()
        {
            if (_registered) return;
            _registered = true;
            InstallJobs.Register();
            ScanJobs.Register();
            RegisterPersonalWorkers();
        }

        static partial void AddPersonalTabs(List<TabPage> tabs);
        static partial void RegisterPersonalWorkers();
    }
}
