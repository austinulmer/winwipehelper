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
    /// <summary>The "Back up / restore files" tab.</summary>
    public sealed class BackupTab : UserControl
    {
        sealed class NodeInfo { public string Path; public bool Dir; public bool Loaded; }

        readonly TreeView _tree = new TreeView { CheckBoxes = true, HideSelection = false };
        readonly Label _size = new Label { AutoSize = true, Margin = new Padding(8, 9, 0, 0), ForeColor = Color.DimGray };
        readonly TextBox _dest = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 6, 3) };
        readonly ComboBox _profiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(0, 4, 6, 3) };
        readonly ProgressBar _bar = new ProgressBar { Height = 20 };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly TextBox _logBox = Ui.LogBox();
        readonly List<Control> _lock = new List<Control>();
        readonly Button _cancel;
        readonly List<KeyValuePair<string, string>> _roots = BackupEngine.Roots();
        readonly List<string> _extraFolders = new List<string>();
        BackupProfiles _store = BackupProfiles.Load();
        CancellationTokenSource _cts;
        bool _busy;

        public BackupTab()
        {
            var hint = Ui.Label("Tick a folder to save all of it, or expand it and tick individual items. Unticking something inside a ticked folder unticks the folder, so only what's still ticked is saved.");
            hint.MaximumSize = new Size(1000, 0);

            var add = Ui.Button("Add another folder...", (s, e) => AddFolder());
            var calc = Ui.Button("Calculate size", (s, e) => CalcSize());
            var row1 = Ui.Flow(add, calc, _size);

            var loadP = Ui.Button("Load", (s, e) => LoadProfile(), 70);
            var saveP = Ui.Button("Save selection as...", (s, e) => SaveProfile(), 150);
            var delP = Ui.Button("Delete", (s, e) => DeleteProfile(), 70);
            var row2 = Ui.Flow(Ui.Label("Saved selections:"), _profiles, loadP, saveP, delP);

            var destRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
            destRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            destRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            destRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            destRow.Controls.Add(Ui.Label("Save the zip to:"), 0, 0);
            destRow.Controls.Add(_dest, 1, 0);
            var browse = Ui.Button("Browse...", (s, e) => BrowseDest(), 90);
            destRow.Controls.Add(browse, 2, 0);

            var backup = Ui.Button("Back up", (s, e) => StartBackup(), 140);
            var restore = Ui.Button("Restore from a backup...", (s, e) => StartRestore(), 180);
            _cancel = Ui.Button("Cancel", (s, e) => _cts?.Cancel(), 90);
            _cancel.Enabled = false;
            var row3 = Ui.Flow(backup, restore, _cancel);

            var root = Ui.Rows((hint, false), (_tree, true), (row1, false), (row2, false), (destRow, false), (row3, false), (_status, false), (_bar, false), (_logBox, false));
            Controls.Add(root);
            _lock.AddRange(new Control[] { _tree, add, calc, loadP, saveP, delP, _profiles, _dest, browse, backup, restore });

            _tree.BeforeExpand += (s, e) => LoadChildren(e.Node);
            _tree.AfterCheck += OnAfterCheck;

            foreach (var kv in _roots) AddRoot(BackupEngine.RootLabel(kv.Key), kv.Value);
            _dest.Text = BackupEngine.DefaultDestination();
            RefreshProfiles();
            if (_store.Last != null) HandleCreated += (s, e) => BeginInvoke((Action)(() => ApplySelection(_store.Last, quiet: true)));
        }

        // ---- tree -----------------------------------------------------------------------------
        TreeNode AddRoot(string label, string path)
        {
            if (!Directory.Exists(path)) return null;
            foreach (TreeNode n in _tree.Nodes) if (((NodeInfo)n.Tag).Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return n;
            var node = _tree.Nodes.Add($"{label}   ({path})");
            node.Tag = new NodeInfo { Path = path, Dir = true };
            AddPlaceholder(node, path);
            return node;
        }

        static void AddPlaceholder(TreeNode node, string path)
        {
            try { if (Directory.EnumerateFileSystemEntries(path).Any()) node.Nodes.Add("..."); } catch { }
        }

        void LoadChildren(TreeNode node)
        {
            if (!(node.Tag is NodeInfo info) || info.Loaded || !info.Dir) return;
            bool was = _busy;
            _busy = true;
            _tree.BeginUpdate();
            node.Nodes.Clear();
            try
            {
                var di = new DirectoryInfo(info.Path);
                foreach (var d in di.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (BackupEngine.SkipReason(d.FullName, d.Attributes, true) != null) continue;
                    var c = node.Nodes.Add(d.Name);
                    c.Tag = new NodeInfo { Path = d.FullName, Dir = true };
                    c.Checked = node.Checked;
                    AddPlaceholder(c, d.FullName);
                }
                foreach (var f in di.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var c = node.Nodes.Add($"{f.Name}   ({Web.FormatSize(f.Length)})");
                    c.Tag = new NodeInfo { Path = f.FullName, Dir = false, Loaded = true };
                    c.Checked = node.Checked;
                    c.ForeColor = Color.DimGray;
                }
            }
            catch { node.Nodes.Add("(can't open this folder)"); }
            info.Loaded = true;
            _tree.EndUpdate();
            _busy = was;
        }

        void OnAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            var stack = new Stack<TreeNode>();
            foreach (TreeNode c in e.Node.Nodes) stack.Push(c);
            while (stack.Count > 0) { var c = stack.Pop(); c.Checked = e.Node.Checked; foreach (TreeNode g in c.Nodes) stack.Push(g); }
            if (!e.Node.Checked) for (var p = e.Node.Parent; p != null; p = p.Parent) p.Checked = false;
            _busy = false;
        }

        List<string> Selected()
        {
            var list = new List<string>();
            void Walk(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (!(n.Tag is NodeInfo info)) continue;
                    if (n.Checked) list.Add(info.Path);
                    else if (info.Loaded) Walk(n.Nodes);
                }
            }
            Walk(_tree.Nodes);
            return list;
        }

        void AddFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose a folder to add to the list", ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var p = dlg.SelectedPath.TrimEnd('\\');
                if (p.Length == 2) p += "\\";
                var label = Path.GetFileName(p.TrimEnd('\\'));
                if (string.IsNullOrEmpty(label)) label = p;
                var n = AddRoot(label, p);
                if (n != null && !_extraFolders.Contains(p, StringComparer.OrdinalIgnoreCase)) _extraFolders.Add(p);
                n?.EnsureVisible();
            }
        }

        /// <summary>Ticks saved paths, expanding the tree down to them.</summary>
        void ApplySelection(BackupSelection sel, bool quiet = false)
        {
            if (sel == null) return;
            _busy = true;
            foreach (TreeNode n in _tree.Nodes) Uncheck(n);
            _busy = false;
            foreach (var f in sel.ExtraFolders ?? new List<string>())
                if (Directory.Exists(f) && AddRoot(Path.GetFileName(f.TrimEnd('\\')) is string l && l.Length > 0 ? l : f, f) != null && !_extraFolders.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _extraFolders.Add(f);
            var missing = new List<string>();
            foreach (var p in sel.Paths ?? new List<string>())
            {
                var node = FindOrExpand(p);
                if (node == null)
                {
                    if (Directory.Exists(p)) node = AddRoot(Path.GetFileName(p.TrimEnd('\\')), p);
                    else if (File.Exists(p))
                    {
                        var parent = Path.GetDirectoryName(p);
                        AddRoot(Path.GetFileName(parent), parent);
                        if (!_extraFolders.Contains(parent, StringComparer.OrdinalIgnoreCase)) _extraFolders.Add(parent);
                        node = FindOrExpand(p);
                    }
                }
                if (node != null) { node.Checked = true; node.EnsureVisible(); }
                else missing.Add(p);
            }
            if (!string.IsNullOrEmpty(sel.Destination) && !quiet) _dest.Text = sel.Destination;
            if (missing.Count > 0 && !quiet) Ui.Warn(FindForm(), "These saved items aren't on this PC:\n\n" + string.Join("\n", missing.Take(20)));
            if (_tree.Nodes.Count > 0) _tree.Nodes[0].EnsureVisible();
        }

        void Uncheck(TreeNode n) { n.Checked = false; foreach (TreeNode c in n.Nodes) Uncheck(c); }

        TreeNode FindOrExpand(string path)
        {
            foreach (TreeNode root in _tree.Nodes.Cast<TreeNode>().OrderByDescending(n => ((NodeInfo)n.Tag).Path.Length))
            {
                var rp = ((NodeInfo)root.Tag).Path.TrimEnd('\\');
                if (path.Equals(rp, StringComparison.OrdinalIgnoreCase) || path.Equals(rp + "\\", StringComparison.OrdinalIgnoreCase)) return root;
                if (!path.StartsWith(rp + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var node = root;
                foreach (var part in path.Substring(rp.Length + 1).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    LoadChildren(node);
                    node = node.Nodes.Cast<TreeNode>().FirstOrDefault(c => c.Tag is NodeInfo i && Path.GetFileName(i.Path).Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (node == null) break;
                }
                if (node != null) return node;
            }
            return null;
        }

        BackupSelection CurrentSelection() => new BackupSelection { Paths = Selected(), ExtraFolders = _extraFolders.ToList(), Destination = _dest.Text.Trim() };

        // ---- profiles -----------------------------------------------------------------------------
        void RefreshProfiles()
        {
            _profiles.Items.Clear();
            foreach (var k in _store.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) _profiles.Items.Add(k);
            if (_profiles.Items.Count > 0) _profiles.SelectedIndex = 0;
        }

        void LoadProfile()
        {
            if (_profiles.SelectedItem is string name && _store.Profiles.TryGetValue(name, out var sel)) ApplySelection(sel);
        }

        void SaveProfile()
        {
            var sel = CurrentSelection();
            if (sel.Paths.Count == 0) { Ui.Info(FindForm(), "Tick something first."); return; }
            var name = Prompt("Name for this selection:", _profiles.SelectedItem as string ?? "My files");
            if (string.IsNullOrWhiteSpace(name)) return;
            _store.Profiles[name.Trim()] = sel;
            _store.Save();
            RefreshProfiles();
            _profiles.SelectedItem = name.Trim();
            _status.Text = $"Saved selection \"{name.Trim()}\" to {BackupProfiles.FilePath}";
        }

        void DeleteProfile()
        {
            if (!(_profiles.SelectedItem is string name)) return;
            if (!Ui.Ask(FindForm(), $"Delete the saved selection \"{name}\"?")) return;
            _store.Profiles.Remove(name);
            _store.Save();
            RefreshProfiles();
        }

        string Prompt(string text, string value)
        {
            using (var f = new BaseForm { Text = Edition.ProductName, Size = new Size(420, 170), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var box = new TextBox { Text = value, Dock = DockStyle.Fill };
                var ok = Ui.Button("OK", (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); }, 80);
                var cancel = Ui.Button("Cancel", (s, e) => f.Close(), 80);
                f.AcceptButton = ok; f.CancelButton = cancel;
                var rows = Ui.Rows((Ui.Label(text), false), (box, false), (Ui.Flow(ok, cancel), false));
                rows.Padding = new Padding(10);
                f.Controls.Add(rows);
                return f.ShowDialog(FindForm()) == DialogResult.OK ? box.Text : null;
            }
        }

        // ---- actions -----------------------------------------------------------------------------
        void BrowseDest()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where should the backup zip be saved?", SelectedPath = Directory.Exists(_dest.Text) ? _dest.Text : "" })
                if (dlg.ShowDialog(this) == DialogResult.OK) _dest.Text = dlg.SelectedPath;
        }

        void SetBusy(bool busy) { foreach (var c in _lock) c.Enabled = !busy; _cancel.Enabled = busy; }

        void CalcSize()
        {
            var sel = Selected();
            if (sel.Count == 0) { _size.Text = "Nothing ticked."; return; }
            _size.Text = "Counting...";
            SetBusy(true);
            Task.Run(() =>
            {
                var probs = new List<string>();
                var files = BackupEngine.ListFiles(sel, probs);
                return (files.Count, files.Sum(f => { try { return f.Length; } catch { return 0L; } }), probs.Count);
            }).ContinueWith(t => Ui.OnUi(this, () =>
            {
                SetBusy(false);
                if (t.Exception != null) { _size.Text = "Couldn't count: " + t.Exception.GetBaseException().Message; return; }
                var (count, bytes, skipped) = t.Result;
                var free = BackupEngine.FreeSpace(_dest.Text.Trim());
                _size.Text = $"{count} files, {Web.FormatSize(bytes)}" + (skipped > 0 ? $" ({skipped} links/online-only items skipped)" : "") +
                             (free >= 0 ? $"   |   {Web.FormatSize(free)} free at the destination" : "");
            }));
        }

        void StartBackup()
        {
            var sel = Selected();
            if (sel.Count == 0) { Ui.Warn(FindForm(), "Nothing is ticked."); return; }
            var dest = _dest.Text.Trim();
            if (dest.Length == 0) { Ui.Warn(FindForm(), "Choose where to save the zip."); return; }
            if (BackupEngine.IsSystemDrive(dest) &&
                !Ui.Ask(FindForm(), "You're saving the backup to the C: drive, which gets wiped when you reinstall Windows.\n\nSave there anyway?", MessageBoxIcon.Warning))
                return;
            if (Elevation.IsElevated &&
                !Ui.Ask(FindForm(), "This program is running as admin. Backups work, but it's better to run it normally so files stay owned by you.\n\nContinue anyway?", MessageBoxIcon.Warning))
                return;

            _store.Last = CurrentSelection();
            _store.Save();
            _logBox.Clear();
            var log = Log.Start("Backup");
            log.Line += l => Ui.Append(_logBox, l);
            log.Write("Selected:\n  " + string.Join("\n  ", sel));
            _cts = new CancellationTokenSource();
            SetBusy(true);
            Task.Run(() => BackupEngine.Backup(sel, dest, log.Write, Progress, _cts.Token)).ContinueWith(t => Ui.OnUi(this, () =>
            {
                SetBusy(false);
                if (t.Exception != null)
                {
                    var ex = t.Exception.GetBaseException();
                    log.Error(ex is OperationCanceledException ? "Cancelled." : ex.ToString());
                    log.Dispose();
                    _status.Text = ex is OperationCanceledException ? "Cancelled (the partial zip was deleted)." : "Backup failed.";
                    if (!(ex is OperationCanceledException)) Ui.Error(FindForm(), "Backup failed:\n" + ex.Message);
                    return;
                }
                var r = t.Result;
                var msg = $"Saved {r.Saved} files ({Web.FormatSize(r.Bytes)}) to:\n{r.ZipPath}";
                if (r.Problems.Count > 0) msg += $"\n\n{r.Problems.Count} items were skipped (open in another program, links, or online-only files). The list is in:\n{r.SkippedLog}";
                log.Write(msg);
                log.Dispose();
                Ui.OpenFolder(r.ZipPath, true);
                Ui.Info(FindForm(), msg);
            }));
        }

        void Progress(double pct, string text) => Ui.OnUi(this, () => { _bar.Value = Math.Max(0, Math.Min(100, (int)pct)); _status.Text = text; });

        void StartRestore()
        {
            if (Elevation.IsElevated &&
                !Ui.Ask(FindForm(), "This program is running as admin, so restored files would be owned by the Administrators group instead of you.\n\nClose it and run it normally (recommended), or continue anyway?\n\nYes = continue anyway", MessageBoxIcon.Warning))
                return;
            string zip = BackupEngine.FindNewestBackup();
            if (zip != null)
            {
                var a = MessageBox.Show(FindForm(), $"Found this backup:\n\n{zip}\n({File.GetLastWriteTime(zip)})\n\nUse it? Choose No to pick a different file.", Edition.ProductName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (a == DialogResult.Cancel) return;
                if (a == DialogResult.No) zip = null;
            }
            if (zip == null)
            {
                using (var dlg = new OpenFileDialog { Title = "Choose your backup zip", Filter = "Backup zip (*.zip)|*.zip", InitialDirectory = BackupEngine.DefaultDestination() })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    zip = dlg.FileName;
                }
            }
            try { using (var f = new RestoreForm(zip)) f.ShowDialog(FindForm()); }
            catch (Exception ex) { Ui.Error(FindForm(), "Couldn't open that backup:\n" + ex.Message); }
        }
    }
}
