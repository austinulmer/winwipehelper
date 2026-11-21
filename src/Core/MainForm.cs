using System.Drawing;
using System.Windows.Forms;

namespace WinWipeHelper
{
    public sealed class MainForm : BaseForm
    {
        public MainForm()
        {
            var v = typeof(MainForm).Assembly.GetName().Version;
            Text = $"{Edition.ProductName}  {v.Major}.{v.Minor}.{v.Build}" + (Elevation.IsElevated ? "  (running as admin)" : "");
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1060, 820);
            MinimumSize = new Size(760, 600);

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            foreach (var t in Modules.CreateTabs()) tabs.TabPages.Add(t);
            Controls.Add(tabs);
        }
    }
}
