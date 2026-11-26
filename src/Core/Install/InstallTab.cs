using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>The "Install apps" tab: categorized checklist, search, Office options, scan, dry run.</summary>
    public sealed class InstallTab : UserControl
    {
        AppList _list;
        string _source;
        readonly Dictionary<string, bool> _ticks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool _busy;

        readonly TextBox _search = new TextBox();
        readonly TreeView _tree = new TreeView { CheckBoxes = true, HideSelection = false, ShowNodeToolTips = true };
        readonly Label _note = new Label { AutoSize = false, Height = 44, Dock = DockStyle.Fill, ForeColor = SystemColors.ControlText };
        readonly Label _count = new Label { AutoSize = true, Margin = new Padding(12, 9, 0, 0), ForeColor = Color.DimGray };
        readonly Label _sourceLabel = new Label { AutoSize = true, Margin = new Padding(12, 9, 0, 0), ForeColor = Color.DimGray };
        readonly ComboBox _category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(0, 4, 6, 3) };
        readonly CheckBox _dryRun = new CheckBox { Text = "Dry run (show the commands, don't run them)", AutoSize = true, Margin = new Padding(0, 9, 16, 3) };
        readonly CheckBox _webIcons = new CheckBox { Text = "Icons from app websites", AutoSize = true, Margin = new Padding(8, 9, 0, 3) };
        readonly ToolTip _tips = new ToolTip();
        readonly IconLoader _icons;
        readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Fill, Height = 20, Margin = new Padding(0, 4, 0, 2) };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 2) };
        readonly TextBox _logBox = Ui.LogBox();
        readonly Button _install, _cancel;
        readonly List<Control> _lockWhileBusy = new List<Control>();
        CancellationTokenSource _cts;

        public InstallTab()
        {
            var load = Ui.Button("Load list...", (s, e) => LoadList());
            var export = Ui.Button("Export list", (s, e) => ExportList());
            var scan = Ui.Button("Scan this PC...", (s, e) => ScanPc());
            var office = Ui.Button("Office options...", (s, e) => EditOffice());
            _webIcons.Checked = Settings.Current.WebIcons;
            _tips.SetToolTip(_webIcons, "Apps that are already installed show their own icon.\nFor the others, download the icon from the app's own website (saved, so only once).\nUntick to never go online for icons.");
            _webIcons.CheckedChanged += (s, e) =>
            {
                Settings.Current.WebIcons = _webIcons.Checked;
                Settings.Current.Save();
                if (_webIcons.Checked) _icons.Refresh(webOnly: true);
            };
            var toolbar = Ui.Flow(load, export, scan, office, _webIcons, _sourceLabel);

            var searchRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.Controls.Add(Ui.Label("Search:"), 0, 0);
            _search.Dock = DockStyle.Fill; _search.Margin = new Padding(0, 5, 0, 3);
            searchRow.Controls.Add(_search, 1, 0);

            var selAll = Ui.Button("Select all shown", (s, e) => SetShown(true));
            var selNone = Ui.Button("Select none", (s, e) => SetShown(false));
            var only = Ui.Button("Tick only this category", (s, e) => OnlyCategory());
            var selRow = Ui.Flow(selAll, selNone, Ui.Label("Only category:"), _category, only, _count);

            _install = Ui.Button("Install selected", (s, e) => StartInstall(), 150);
            _cancel = Ui.Button("Cancel", (s, e) => _cts?.Cancel(), 90);
            _cancel.Enabled = false;
            var goRow = Ui.Flow(_dryRun, _install, _cancel);


            _tree.Margin = new Padding(0, 4, 0, 4);
            _icons = new IconLoader(_tree, l => _tree.ImageList = l);
            _icons.Changed += () => _tree.Invalidate();
            var root = Ui.Rows((toolbar, false), (searchRow, false), (_tree, true), (_note, false), (selRow, false), (goRow, false), (_status, false), (_bar, false), (_logBox, false));
            Controls.Add(root);

            _lockWhileBusy.AddRange(new Control[] { load, export, scan, office, selAll, selNone, only, _category, _tree, _search, _dryRun, _install });

            _search.TextChanged += (s, e) => BuildTree();
            _tree.AfterCheck += OnAfterCheck;
            _tree.AfterSelect += (s, e) => _note.Text = e.Node.Tag is AppItem a ? a.Describe() : e.Node.Text;
            _tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is AppItem a && a.Kind == AppKind.Office) EditOffice(); };

            _list = Catalog.LoadStartup(out _source, out var warning);
            ApplyList(_list, _source);
            if (warning != null) HandleCreated += (s, e) => BeginInvoke((Action)(() => Ui.Warn(FindForm(), warning)));
            if (!Settings.Current.WebIconsAsked) HandleCreated += (s, e) => BeginInvoke((Action)AskAboutWebIcons);
        }

        /// <summary>First run: nothing goes online for icons until the user agrees.</summary>
        void AskAboutWebIcons()
        {
            if (Settings.Current.WebIconsAsked) return;
            bool yes = Ui.Ask(FindForm(),
                "Show app icons from the internet?\n\n" +
                "Apps that are already installed show their own icon. For the others, WinWipeHelper can download " +
                "the icon from each app's own website (once per site, then it's kept on this PC).\n\n" +
                "Yes = download icons\nNo = never go online for icons (apps get a colored letter instead)\n\n" +
                "You can change this any time with \"Icons from app websites\".");
            Settings.Current.WebIconsAsked = true;
            Settings.Current.WebIcons = yes;
            Settings.Current.Save();
            _webIcons.Checked = yes;   // when this changes the box, its handler fetches the icons
        }

        void ApplyList(AppList list, string source)
        {
            _list = list;
            _source = source;
            _ticks.Clear();
            foreach (var a in _list.Apps) _ticks[a.Id] = a.Default;
            _category.Items.Clear();
            foreach (var c in _list.Categories) _category.Items.Add(c);
            if (_category.Items.Count > 0) _category.SelectedIndex = 0;
            _sourceLabel.Text = "List: " + source;
            _search.Text = "";
            BuildTree();
        }

        bool Matches(AppItem a, string f) =>
            f.Length == 0 || new[] { a.Name, a.Id, a.Category, a.Note }.Any(x => x != null && x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);

        void BuildTree()
        {
            var filter = _search.Text.Trim();
            _busy = true;
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            foreach (var cat in _list.Categories)
            {
                var items = _list.Apps.Where(a => string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase) && Matches(a, filter)).ToList();
                if (items.Count == 0) continue;
                var parent = _tree.Nodes.Add(cat);
                parent.ImageIndex = parent.SelectedImageIndex = IconLoader.NoIcon;
                foreach (var a in items)
                {
                    var label = a.Name + (string.IsNullOrEmpty(a.Note) && a.Kind != AppKind.Office ? "" : "  *");
                    if (a.Kind == AppKind.Office && a.Office != null) label = $"{a.Name}  ({a.Office.ProductLabel}: {string.Join(", ", a.Office.Apps)})";
                    var n = parent.Nodes.Add(label);
                    n.Tag = a;
                    var item = a;
                    n.ImageIndex = n.SelectedImageIndex = _icons.Add(new IconLoader.Request
                    {
                        Key = "app|" + a.Id, Name = a.Name, Homepage = InstalledIcons.HomepageOf(a),
                        FindInstalled = () => InstalledIcons.Shared.Find(item),
                    });
                    n.ToolTipText = a.Describe();
                    n.Checked = _ticks.TryGetValue(a.Id, out var t) && t;
                }
                parent.Checked = items.All(a => _ticks.TryGetValue(a.Id, out var t) && t);
                parent.Expand();
            }
            _tree.EndUpdate();
            if (_tree.Nodes.Count > 0) _tree.TopNode = _tree.Nodes[0];
            _busy = false;
            _note.Text = _tree.Nodes.Count > 0 ? "Click an item to see its notes (* = has notes). Ticking a category ticks everything shown in it. Double-click the Office item to change it." : "No matches.";
            UpdateCount();
        }

        void OnAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            if (e.Node.Tag is AppItem a)
            {
                _ticks[a.Id] = e.Node.Checked;
                var p = e.Node.Parent;
                if (p != null) p.Checked = p.Nodes.Cast<TreeNode>().All(n => n.Checked);
            }
            else
            {
                foreach (TreeNode c in e.Node.Nodes) { c.Checked = e.Node.Checked; _ticks[((AppItem)c.Tag).Id] = e.Node.Checked; }
            }
            _busy = false;
            UpdateCount();
        }

        void UpdateCount() => _count.Text = $"{_ticks.Count(t => t.Value)} selected";

        void SetShown(bool value)
        {
            foreach (TreeNode g in _tree.Nodes) foreach (TreeNode c in g.Nodes) _ticks[((AppItem)c.Tag).Id] = value;
            BuildTree();
        }

        void OnlyCategory()
        {
            if (!(_category.SelectedItem is string cat)) return;
            foreach (var a in _list.Apps) _ticks[a.Id] = string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase);
            _search.Text = "";
            BuildTree();
        }

        // ---- list files -----------------------------------------------------------------------

        AppList CurrentWithTicks()
        {
            var copy = _list.Clone();
            foreach (var a in copy.Apps) a.Default = _ticks.TryGetValue(a.Id, out var t) && t;
            return copy;
        }

        void ExportList()
        {
            using (var dlg = new SaveFileDialog { Title = "Export list", Filter = "App list (*.json)|*.json", FileName = Catalog.FileName, InitialDirectory = Paths.DataDir })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Catalog.Save(CurrentWithTicks(), dlg.FileName);
                    _sourceLabel.Text = "List: " + dlg.FileName;
                    var hint = string.Equals(Path.GetFullPath(dlg.FileName), Catalog.NextToExe, StringComparison.OrdinalIgnoreCase)
                        ? "\n\nBecause it's next to the program, it will be used automatically next time."
                        : $"\n\nTo use it automatically, save it as {Catalog.FileName} next to the program.";
                    Ui.Info(FindForm(), "Saved your list and ticks to:\n" + dlg.FileName + hint);
                }
                catch (Exception ex) { Ui.Error(FindForm(), "Couldn't save the list:\n" + ex.Message); }
            }
        }

        void LoadList()
        {
            using (var dlg = new OpenFileDialog { Title = "Load an app list", Filter = "App list (*.json)|*.json|All files (*.*)|*.*", InitialDirectory = Paths.DataDir })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { ApplyList(AppList.Parse(File.ReadAllText(dlg.FileName)), dlg.FileName); }
                catch (Exception ex) { Ui.Error(FindForm(), "That file isn't a valid app list:\n" + ex.Message); }
            }
        }

        void EditOffice()
        {
            var item = _list.Apps.FirstOrDefault(a => a.Kind == AppKind.Office);
            if (item == null)
            {
                if (!Ui.Ask(FindForm(), "This list has no Office item. Add one?")) return;
                item = new AppItem { Id = "office", Name = "Microsoft Office", Category = "Office", Kind = AppKind.Office, Office = new OfficeOptions(),
                                     Note = "Installed with Microsoft's Office Deployment Tool. Sign in to Office to activate." };
                _list.Apps.Add(item);
                _list.Normalize();
                _category.Items.Clear(); foreach (var c in _list.Categories) _category.Items.Add(c);
            }
            using (var dlg = new OfficeDialog(item.Office))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                item.Office = dlg.Result;
                _ticks[item.Id] = true;
                BuildTree();
                _note.Text = "Office set to " + item.Office.Summary() + ". Use Export list to save it in apps.json.";
            }
        }

        void ScanPc()
        {
            using (var f = new ScanForm())
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK || f.Result == null) return;
                if (Ui.Ask(FindForm(), "Use the scanned list in this window now?\n\n(It was saved to " + (f.SavedPath ?? "a file") + ".)"))
                    ApplyList(f.Result, f.SavedPath ?? "scan");
            }
        }

        // ---- install ----------------------------------------------------------------------------

        void SetBusy(bool busy)
        {
            foreach (var c in _lockWhileBusy) c.Enabled = !busy;
            _cancel.Enabled = busy;
        }

        void StartInstall()
        {
            var selected = _list.Apps.Where(a => _ticks.TryGetValue(a.Id, out var t) && t).ToList();
            if (selected.Count == 0) { Ui.Info(FindForm(), "Nothing is ticked."); return; }
            bool dry = _dryRun.Checked;
            _logBox.Clear();
            var log = Log.Start(dry ? "Install-DryRun" : "Install");
            log.Line += line => Ui.Append(_logBox, line);
            log.Write($"{selected.Count} selected: {string.Join(", ", selected.Select(s => s.Name))}");
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _bar.Value = 0;
            var runner = new InstallRunner(log.Write, (pct, text) => Ui.OnUi(this, () =>
            {
                _bar.Value = Math.Max(0, Math.Min(100, (int)pct));
                _status.Text = text;
            }), _cts.Token);

            Task.Run(() => runner.Run(selected, dry)).ContinueWith(t =>
            {
                Ui.OnUi(this, () =>
                {
                    SetBusy(false);
                    _bar.Value = 100;
                    List<InstallResult> results = t.Status == TaskStatus.RanToCompletion ? t.Result : new List<InstallResult>();
                    if (t.Exception != null) log.Error(t.Exception.GetBaseException().ToString());
                    var failed = results.Where(r => !r.Ok).ToList();
                    if (!dry && results.Count > failed.Count) { InstalledIcons.Reset(); _icons.Refresh(); }
                    log.Write($"\n==== {(dry ? "Dry run" : "Done")}: {results.Count - failed.Count} of {results.Count} OK ====");
                    foreach (var f in failed) log.Write($"  FAILED  {f.App}: {f.Detail}");
                    log.Write("Log file: " + log.FilePath);
                    _status.Text = $"{results.Count - failed.Count} of {results.Count} OK" + (dry ? " (dry run)" : "");

                    string page = null;
                    bool hadDownloads = selected.Any(a => a.Kind == AppKind.Download);
                    if (!dry)
                    {
                        try { page = InstallRunner.WriteManualPage(results, _list.ManualLinks, hadDownloads); } catch (Exception ex) { log.Error("Couldn't write the manual-links page: " + ex.Message); }
                    }
                    else if (_list.ManualLinks.Count > 0) log.Write($"A real run ends by opening a page with {_list.ManualLinks.Count} manual link(s).");
                    log.Dispose();

                    var msg = $"{results.Count - failed.Count} of {results.Count} {(dry ? "checked (dry run, nothing was installed)" : "finished OK")}.";
                    if (failed.Count > 0) msg += "\n\nThese need another try (or a manual install):\n" + string.Join("\n", failed.Select(f => $"  {f.App}: {f.Detail}"));
                    if (page != null) msg += "\n\nThe manual-installs page will open next.";
                    if (failed.Count > 0) Ui.Warn(FindForm(), msg); else Ui.Info(FindForm(), msg);
                    if (page != null) Ui.OpenFile(page);
                    if (!dry && hadDownloads && Directory.Exists(InstallRunner.InstallersDir)) Ui.OpenFolder(InstallRunner.InstallersDir);
                });
            });
        }
    }
}
