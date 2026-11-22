using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>"Scan this PC": options, progress, then the review screen (pass 5).</summary>
    public sealed class ScanForm : BaseForm
    {
        readonly CheckBox _store = new CheckBox { Text = "Microsoft Store / MSIX apps", AutoSize = true };
        readonly CheckBox _choco = new CheckBox { Text = "Chocolatey", AutoSize = true };
        readonly CheckBox _scoop = new CheckBox { Text = "Scoop", AutoSize = true };
        readonly CheckBox _hku = new CheckBox { Text = "Other user profiles (asks for admin if needed)", AutoSize = true };
        readonly Button _start;
        readonly ProgressBar _bar = new ProgressBar { Height = 18 };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly TextBox _logBox = Ui.LogBox();
        readonly DataGridView _grid = new DataGridView();
        readonly TextBox _search = new TextBox { Width = 220, Margin = new Padding(0, 5, 12, 3) };
        readonly FlowLayoutPanel _filters = Ui.Flow();
        readonly Dictionary<ScanCategory, CheckBox> _show = new Dictionary<ScanCategory, CheckBox>();
        readonly Button _save, _report, _tickAll, _untickAll;
        List<ScanItem> _items = new List<ScanItem>();
        CancellationTokenSource _cts;
        bool _filling;
        readonly IconLoader _icons;

        public AppList Result { get; private set; }
        public string SavedPath { get; private set; }

        public ScanForm()
        {
            Text = "Scan this PC";
            Size = new Size(1240, 860);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterParent;

            _start = Ui.Button("Start scan", (s, e) => StartScan(), 130);
            var optional = Ui.Flow(Ui.Label("Also include (optional, unticked in the results):"), _store, _choco, _scoop, _hku, _start);
            var intro = Ui.Label("Finds installed programs from winget, the registry, shortcuts, program folders, startup entries, scheduled tasks and game launchers, then matches them to winget. Runs without admin.");
            intro.MaximumSize = new Size(1150, 0);

            SetupGrid();
            _icons = new IconLoader(_grid);
            _icons.Updated += (key, index) =>
            {
                _filling = true;
                foreach (DataGridViewRow row in _grid.Rows)
                    if (row.Tag is ScanItem it && IconKey(it) == key) row.Cells["icon"].Value = _icons.ImageFor(key);
                _filling = false;
            };
            foreach (ScanCategory c in Enum.GetValues(typeof(ScanCategory)))
            {
                var cb = new CheckBox { Text = ScanItem.CategoryLabel(c), AutoSize = true, Checked = c != ScanCategory.Runtime && c != ScanCategory.Windows, Margin = new Padding(0, 7, 10, 3) };
                cb.CheckedChanged += (s, e) => Fill();
                _show[c] = cb;
                _filters.Controls.Add(cb);
            }
            _filters.Controls.Add(Ui.Label("Search:"));
            _filters.Controls.Add(_search);
            _search.TextChanged += (s, e) => Fill();

            _tickAll = Ui.Button("Tick all shown", (s, e) => TickShown(true));
            _untickAll = Ui.Button("Untick all shown", (s, e) => TickShown(false));
            var confirm = Ui.Button("Confirm selected matches", (s, e) => ConfirmSelected(), 180);
            _report = Ui.Button("Save report (CSV)...", (s, e) => SaveReport(), 150);
            _save = Ui.Button("Save as apps.json...", (s, e) => SaveList(), 160);
            var close = Ui.Button("Close", (s, e) => Close(), 90);
            var bottom = Ui.Flow(_tickAll, _untickAll, confirm, _report, _save, close);
            foreach (var b in new[] { _tickAll, _untickAll, _report, _save }) b.Enabled = false;

            var hint = Ui.Label("Yellow rows are uncertain matches: select them and click Confirm, or type the right winget ID. Ticked items with a confirmed winget ID go into the app list; other ticked items become manual links (with the homepage from the registry). Games become \"reinstall from <launcher>\" links.");
            hint.MaximumSize = new Size(1150, 0);
            hint.ForeColor = Color.DimGray;

            _logBox.Height = 110;
            var root = Ui.Rows((intro, false), (optional, false), (_status, false), (_bar, false), (_logBox, false), (_filters, false), (_grid, true), (hint, false), (bottom, false));
            root.Padding = new Padding(10);
            Controls.Add(root);
            FormClosing += (s, e) => _cts?.Cancel();
        }

        void SetupGrid()
        {
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "inc", HeaderText = "", Width = 30, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            var icon = new DataGridViewImageColumn { Name = "icon", HeaderText = "", Width = 28, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ReadOnly = true, ImageLayout = DataGridViewImageCellLayout.Normal };
            icon.DefaultCellStyle.NullValue = null;   // no red "missing image" cross while loading
            icon.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.Columns.Add(icon);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", FillWeight = 26 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", HeaderText = "Winget ID (editable)", FillWeight = 22 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "match", HeaderText = "Match", FillWeight = 16, ReadOnly = true });
            var cat = new DataGridViewComboBoxColumn { Name = "cat", HeaderText = "Category", FillWeight = 11, FlatStyle = FlatStyle.Flat };
            foreach (ScanCategory c in Enum.GetValues(typeof(ScanCategory))) cat.Items.Add(ScanItem.CategoryLabel(c));
            _grid.Columns.Add(cat);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "src", HeaderText = "Found in", FillWeight = 22, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "flag", HeaderText = "Notes", FillWeight = 14, ReadOnly = true });
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += OnCellChanged;
            _grid.DataError += (s, e) => e.ThrowException = false;
            _grid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || !(_grid.Rows[e.RowIndex].Tag is ScanItem it)) return;
                e.ToolTipText = string.Join("\n", it.Records.Take(12).Select(r => $"{r.Source}: {r.Name}" + (r.Detail != null && r.Detail.Length < 160 ? "  (" + r.Detail + ")" : "")));
            };
        }

        void OnCellChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_filling || e.RowIndex < 0) return;
            var row = _grid.Rows[e.RowIndex];
            if (!(row.Tag is ScanItem it)) return;
            switch (_grid.Columns[e.ColumnIndex].Name)
            {
                case "inc": it.Include = row.Cells["inc"].Value is bool b && b; break;
                case "name": it.Name = (row.Cells["name"].Value as string ?? "").Trim(); break;
                case "id":
                    var id = (row.Cells["id"].Value as string ?? "").Trim();
                    it.WingetId = id.Length == 0 ? null : id;
                    it.Uncertain = false;
                    it.Match = id.Length == 0 ? "cleared" : "set by you";
                    _filling = true; row.Cells["match"].Value = it.Match; _filling = false;
                    Colorize(row, it);
                    break;
                case "cat":
                    var label = row.Cells["cat"].Value as string;
                    foreach (ScanCategory c in Enum.GetValues(typeof(ScanCategory))) if (ScanItem.CategoryLabel(c) == label) it.Category = c;
                    break;
            }
        }

        void StartScan()
        {
            _start.Enabled = false;
            foreach (var cb in new[] { _store, _choco, _scoop, _hku }) cb.Enabled = false;
            _cts = new CancellationTokenSource();
            var log = Log.Start("Scan");
            log.Line += line => Ui.Append(_logBox, line);
            var opt = new ScanOptions { Store = _store.Checked, Chocolatey = _choco.Checked, Scoop = _scoop.Checked, OtherProfiles = _hku.Checked };
            var scanner = new Scanner(opt, log.Write, (p, t) => Ui.OnUi(this, () => { _bar.Value = Math.Max(0, Math.Min(100, (int)p)); _status.Text = t; }), _cts.Token);
            Task.Run(() => scanner.Run()).ContinueWith(t => Ui.OnUi(this, () =>
            {
                if (t.Exception != null) { log.Error(t.Exception.GetBaseException().ToString()); _status.Text = "The scan failed. See the log."; }
                else
                {
                    _items = t.Result;
                    log.Write("Log file: " + log.FilePath);
                    Fill();
                    foreach (var b in new[] { _tickAll, _untickAll, _report, _save }) b.Enabled = true;
                }
                log.Dispose();
                _start.Enabled = true; _start.Text = "Scan again";
                foreach (var cb in new[] { _store, _choco, _scoop, _hku }) cb.Enabled = true;
            }));
        }

        IEnumerable<ScanItem> VisibleItems()
        {
            var f = _search.Text.Trim();
            return _items.Where(i => _show[i.Category].Checked &&
                (f.Length == 0 || (i.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (i.WingetId ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || i.Sources.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        void Fill()
        {
            _filling = true;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            _grid.Columns["icon"].Width = _icons.IconSize + 12;
            foreach (var it in VisibleItems())
            {
                var item = it;
                var key = IconKey(it);
                _icons.Add(new IconLoader.Request { Key = key, Name = it.Name, Homepage = it.Homepage, FindInstalled = () => InstalledIcons.Find(item) });
                int idx = _grid.Rows.Add(it.Include, _icons.ImageFor(key), it.Name, it.WingetId, it.Match, ScanItem.CategoryLabel(it.Category), it.Sources,
                    string.Join("; ", new[] { it.Flag, it.Launcher != null ? "reinstall from " + it.Launcher : null, it.Homepage }.Where(x => !string.IsNullOrEmpty(x))));
                var row = _grid.Rows[idx];
                row.Tag = it;
                Colorize(row, it);
            }
            _grid.ResumeLayout();
            _filling = false;
            foreach (var kv in _show) kv.Value.Text = $"{ScanItem.CategoryLabel(kv.Key)} ({_items.Count(i => i.Category == kv.Key)})";
            _status.Text = $"{_items.Count} programs found, {_items.Count(i => i.Include)} ticked. Runtimes and Windows components are hidden until you tick them above.";
        }

        static string IconKey(ScanItem it) => "scan|" + (it.WingetId ?? "") + "|" + Names.Normalize(it.Name);

        static void Colorize(DataGridViewRow row, ScanItem it)
        {
            row.DefaultCellStyle.BackColor = it.Uncertain ? Color.FromArgb(255, 248, 205) : SystemColors.Window;
            row.DefaultCellStyle.ForeColor = it.Category == ScanCategory.Leftover ? Color.FromArgb(160, 30, 30) :
                                             it.Category == ScanCategory.Runtime || it.Category == ScanCategory.Windows ? Color.DimGray : SystemColors.WindowText;
        }

        void ConfirmSelected()
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (row.Tag is ScanItem it && it.Uncertain && !string.IsNullOrEmpty(it.WingetId))
                {
                    it.Uncertain = false;
                    it.Match = "confirmed by you";
                    it.Include = true;
                }
            Fill();
        }

        void TickShown(bool value)
        {
            foreach (var it in VisibleItems()) it.Include = value;
            Fill();
        }

        void SaveList()
        {
            _grid.EndEdit();
            var list = Scanner.ToAppList(_items);
            using (var dlg = new SaveFileDialog { Title = "Save the scanned list", Filter = "App list (*.json)|*.json", FileName = Catalog.FileName, InitialDirectory = Paths.DataDir })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Catalog.Save(list, dlg.FileName);
                    Result = list;
                    SavedPath = dlg.FileName;
                    Ui.Info(this, $"Saved {list.Apps.Count} winget apps and {list.ManualLinks.Count} manual links to:\n{dlg.FileName}");
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex) { Ui.Error(this, "Couldn't save:\n" + ex.Message); }
            }
        }

        void SaveReport()
        {
            using (var dlg = new SaveFileDialog { Title = "Save scan report", Filter = "CSV (*.csv)|*.csv", FileName = $"scan-report-{DateTime.Now:yyyy-MM-dd}.csv", InitialDirectory = Paths.DataDir })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, ReportCsv(_items), new UTF8Encoding(true)); Ui.OpenFolder(dlg.FileName, true); }
                catch (Exception ex) { Ui.Error(this, "Couldn't save:\n" + ex.Message); }
            }
        }

        public static string ReportCsv(IEnumerable<ScanItem> items)
        {
            string C(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder("Include,Name,WingetId,Match,Category,Launcher,Sources,Notes,Homepage,Publisher\r\n");
            foreach (var i in items)
                sb.Append(string.Join(",", new[] { i.Include ? "yes" : "no", C(i.Name), C(i.WingetId), C(i.Match), C(ScanItem.CategoryLabel(i.Category)), C(i.Launcher), C(i.Sources), C(i.Flag), C(i.Homepage), C(i.Publisher) })).Append("\r\n");
            return sb.ToString();
        }
    }
}
