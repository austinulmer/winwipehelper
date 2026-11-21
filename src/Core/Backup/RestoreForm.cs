using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>Choose what to restore (grouped by folder, with each target path) and how to handle conflicts.</summary>
    public sealed class RestoreForm : BaseForm
    {
        readonly ZipArchive _zip;
        readonly List<BackupEngine.RestoreGroup> _groups;
        readonly List<KeyValuePair<string, string>> _roots = BackupEngine.Roots();
        readonly CheckedListBox _list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
        readonly RadioButton _skip = new RadioButton { Text = "Skip it", Checked = true, AutoSize = true };
        readonly RadioButton _over = new RadioButton { Text = "Overwrite it", AutoSize = true };
        readonly RadioButton _both = new RadioButton { Text = "Keep both (adds \"restored\" to the name)", AutoSize = true };
        readonly ProgressBar _bar = new ProgressBar { Height = 20 };
        readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly Button _go, _close;
        CancellationTokenSource _cts;
        Task _running;
        bool _closeWhenDone;

        public RestoreForm(string zipPath)
        {
            Text = "Restore files";
            Size = new Size(900, 640);
            MinimumSize = new Size(640, 460);
            _zip = ZipFile.OpenRead(zipPath);
            if (_zip.GetEntry(BackupEngine.InfoEntry) == null)
            {
                _zip.Dispose();
                throw new InvalidDataException("That zip wasn't made by this program (or the PowerShell version), so I don't know where its files belong.");
            }
            string about = "";
            try
            {
                using (var r = new StreamReader(_zip.GetEntry(BackupEngine.InfoEntry).Open(), Encoding.UTF8))
                {
                    var info = Json.Parse(r.ReadToEnd());
                    about = $"Made {Json.Str(info, "Created")?.Replace('T', ' ')} on {Json.Str(info, "Computer")}.";
                }
            }
            catch { }
            _groups = BackupEngine.Group(_zip, _roots);
            foreach (var g in _groups) _list.Items.Add($"{g.Target}   -   {g.Entries.Count} files, {Web.FormatSize(g.Bytes)}", true);

            var top = Ui.Label($"From: {zipPath}\n{about} Tick what to restore. Each line shows where it will go on this PC.");
            var conflicts = new GroupBox { Text = "If a file already exists", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
            conflicts.Controls.Add(Ui.Flow(_skip, _over, _both));
            _go = Ui.Button("Restore", (s, e) => Start(), 120);
            _close = Ui.Button("Close", (s, e) => Close(), 90);
            var all = Ui.Button("Tick all", (s, e) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true); }, 80);
            var none = Ui.Button("Tick none", (s, e) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false); }, 80);
            var root = Ui.Rows((top, false), (_list, true), (Ui.Flow(all, none), false), (conflicts, false), (_status, false), (_bar, false), (Ui.Flow(_go, _close), false));
            root.Padding = new Padding(10);
            Controls.Add(root);
            // Closing while a restore runs: cancel, wait for the task to stop, then close (the zip stays open until then).
            FormClosing += (s, e) =>
            {
                if (_running != null && !_running.IsCompleted)
                {
                    e.Cancel = true;
                    _closeWhenDone = true;
                    _status.Text = "Stopping...";
                    _cts?.Cancel();
                }
            };
            FormClosed += (s, e) => _zip.Dispose();
        }

        void Start()
        {
            var chosen = _list.CheckedIndices.Cast<int>().SelectMany(i => _groups[i].Entries).ToList();
            if (chosen.Count == 0) return;
            var mode = _skip.Checked ? BackupEngine.Conflict.Skip : _over.Checked ? BackupEngine.Conflict.Overwrite : BackupEngine.Conflict.KeepBoth;
            _go.Enabled = false; _list.Enabled = false;
            _cts = new CancellationTokenSource();
            var log = Log.Start("Restore");
            log.Write($"Restoring {chosen.Count} files ({mode})");
            _running = Task.Run(() => BackupEngine.Restore(chosen, mode, _roots, (p, t) => Ui.OnUi(this, () => { _bar.Value = Math.Max(0, Math.Min(100, (int)p)); _status.Text = t; }), _cts.Token))
                .ContinueWith(t => Ui.OnUi(this, () =>
                {
                    _go.Enabled = true; _list.Enabled = true;
                    if (_closeWhenDone)
                    {
                        if (t.Exception != null) log.Write("Stopped: " + t.Exception.GetBaseException().Message);
                        else log.Write($"Stopped after restoring {t.Result.Restored} files.");
                        log.Dispose();
                        BeginInvoke((Action)Close);
                        return;
                    }
                    if (t.Exception != null)
                    {
                        var ex = t.Exception.GetBaseException();
                        log.Error(ex.ToString()); log.Dispose();
                        if (!(ex is OperationCanceledException)) Ui.Error(this, "Restore failed:\n" + ex.Message);
                        return;
                    }
                    var r = t.Result;
                    var msg = $"Restored {r.Restored} files.";
                    if (r.Skipped > 0) msg += $"\nSkipped {r.Skipped} that already existed.";
                    foreach (var f in r.Failed) log.Write("FAILED: " + f);
                    if (r.Failed.Count > 0) msg += $"\n{r.Failed.Count} failed. Details are in the log:\n{log.FilePath}";
                    log.Write(msg);
                    log.Dispose();
                    Ui.Info(this, msg);
                }));
        }
    }
}
