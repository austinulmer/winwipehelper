using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>Office type picker: license, apps, language. Edition is 64-bit.</summary>
    public sealed class OfficeDialog : BaseForm
    {
        readonly ComboBox _product = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        readonly CheckedListBox _apps = new CheckedListBox { CheckOnClick = true, MultiColumn = false, Height = 190, Width = 380, IntegralHeight = false };
        readonly TextBox _language = new TextBox { Width = 160 };
        readonly TextBox _preview = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f) };
        public OfficeOptions Result { get; private set; }

        public OfficeDialog(OfficeOptions current)
        {
            Text = "Office options";
            Size = new Size(940, 600);
            MinimumSize = new Size(640, 480);
            var opts = Json.Deserialize<OfficeOptions>(Json.Serialize(current ?? new OfficeOptions()));
            opts.Normalize();

            foreach (var p in OfficeOptions.Products) _product.Items.Add($"{p.Label}  ({p.Id})");
            _product.SelectedIndex = Math.Max(0, Array.FindIndex(OfficeOptions.Products, p => p.Id == opts.Product));
            foreach (var a in OfficeOptions.AllApps) _apps.Items.Add(a, opts.Apps.Contains(a));
            _language.Text = opts.Language;

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
            left.Controls.Add(Ui.Label("License type:"));
            left.Controls.Add(_product);
            left.Controls.Add(Ui.Label("Apps to install (anything unticked is excluded):"));
            left.Controls.Add(_apps);
            left.Controls.Add(Ui.Label("Language (MatchOS = same as Windows, or e.g. en-us):"));
            left.Controls.Add(_language);
            var ed = Ui.Label("Edition: 64-bit. Activate by signing in to Office with the account that owns the license."); ed.MaximumSize = new Size(400, 0); left.Controls.Add(ed);

            var ok = Ui.Button("OK", (s, e) => { Result = Collect(); DialogResult = DialogResult.OK; Close(); }, 90);
            var cancel = Ui.Button("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; Close(); }, 90);
            AcceptButton = ok; CancelButton = cancel;

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 400, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(left);
            var right = Ui.Rows((Ui.Label("configuration.xml that will be used:"), false), (_preview, true));
            split.Panel2.Controls.Add(right);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);

            var root = Ui.Rows((split, true), (buttons, false));
            root.Padding = new Padding(10);
            Controls.Add(root);

            _product.SelectedIndexChanged += (s, e) => UpdatePreview();
            _apps.ItemCheck += (s, e) => BeginInvoke((Action)UpdatePreview);
            _language.TextChanged += (s, e) => UpdatePreview();
            Shown += (s, e) => { split.SplitterDistance = Math.Max(300, Math.Min(split.Width - 300, (int)(440 * DeviceDpi / 96f))); UpdatePreview(); };
        }

        OfficeOptions Collect()
        {
            var o = new OfficeOptions
            {
                Product = OfficeOptions.Products[Math.Max(0, _product.SelectedIndex)].Id,
                Apps = _apps.CheckedItems.Cast<string>().ToList(),
                Language = string.IsNullOrWhiteSpace(_language.Text) ? "MatchOS" : _language.Text.Trim(),
                Edition = "64",
            };
            o.Normalize();
            return o;
        }

        void UpdatePreview()
        {
            var xml = Collect().ToConfigurationXml();
            var problems = OfficeOptions.Validate(xml);
            _preview.Text = xml.Replace("\n", "\r\n").Replace("\r\r", "\r") + (problems.Count > 0 ? "\r\n\r\nProblems:\r\n" + string.Join("\r\n", problems) : "");
        }
    }
}
