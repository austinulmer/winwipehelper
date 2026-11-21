using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinWipeHelper
{
    /// <summary>Base window: Segoe UI, DPI scaling, follows per-monitor DPI changes.</summary>
    public class BaseForm : Form
    {
        int _dpi = 96;

        public BaseForm()
        {
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = true;
            try { Icon = Icon.ExtractAssociatedIcon(Paths.ExePath); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { _dpi = (int)GetDpiForWindow(Handle); } catch { _dpi = DeviceDpi; }
            if (_dpi <= 0) _dpi = 96;
        }

        const int WM_DPICHANGED = 0x02E0;

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (int)((long)m.WParam & 0xFFFF);
                if (newDpi > 0 && newDpi != _dpi)
                {
                    float f = newDpi / (float)_dpi;
                    _dpi = newDpi;
                    var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    SuspendLayout();
                    var oldFont = Font;
                    ScaleFonts(this, oldFont, f);
                    Font = new Font(oldFont.FontFamily, oldFont.Size * f, oldFont.Style);
                    Scale(new SizeF(f, f));
                    ResumeLayout(true);
                    SetBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        static void ScaleFonts(Control parent, Font formFont, float f)
        {
            foreach (Control c in parent.Controls)
            {
                if (!ReferenceEquals(c.Font, formFont) && !ReferenceEquals(c.Font, c.Parent?.Font))
                    c.Font = new Font(c.Font.FontFamily, c.Font.Size * f, c.Font.Style);
                ScaleFonts(c, formFont, f);
            }
        }
    }

    public static class Ui
    {
        public static Button Button(string text, EventHandler onClick = null, int minWidth = 110)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowOnly,
                MinimumSize = new Size(minWidth, 30),
                Padding = new Padding(6, 2, 6, 2),
                Margin = new Padding(0, 3, 8, 3),
                UseVisualStyleBackColor = true,
            };
            if (onClick != null) b.Click += onClick;
            return b;
        }

        public static Label Label(string text, bool autoSize = true)
        {
            return new Label { Text = text, AutoSize = autoSize, Margin = new Padding(0, 8, 6, 3) };
        }

        public static Label Header(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 6) };
            l.Font = new Font("Segoe UI Semibold", 10.5f);
            return l;
        }

        public static FlowLayoutPanel Flow(params Control[] controls)
        {
            var p = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                WrapContents = true,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            p.Controls.AddRange(controls);
            return p;
        }

        /// <summary>A vertical stack: each row auto-sized except the ones marked to fill.</summary>
        public static TableLayoutPanel Rows(params (Control control, bool fill)[] rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = new Padding(0), Padding = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var (c, fill) in rows)
            {
                t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
                c.Dock = DockStyle.Fill;
                t.Controls.Add(c);
            }
            return t;
        }

        public static TextBox LogBox()
        {
            return new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Font = new Font("Consolas", 9f),
                BackColor = SystemColors.Window,
                MinimumSize = new Size(10, 90),
                Height = 120,
            };
        }

        public static void Append(TextBox box, string text)
        {
            if (box.IsDisposed) return;
            if (box.InvokeRequired) { try { box.BeginInvoke((Action)(() => Append(box, text))); } catch { } return; }
            if (box.TextLength > 400000) box.Text = box.Text.Substring(box.TextLength - 200000);
            box.AppendText(text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
        }

        public static void OnUi(Control c, Action a)
        {
            if (c.IsDisposed) return;
            if (c.InvokeRequired) { try { c.BeginInvoke(a); } catch { } }
            else a();
        }

        public static void Info(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, Edition.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        public static void Warn(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, Edition.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        public static void Error(IWin32Window owner, string text) =>
            MessageBox.Show(owner, text, Edition.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        public static bool Ask(IWin32Window owner, string text, MessageBoxIcon icon = MessageBoxIcon.Question) =>
            MessageBox.Show(owner, text, Edition.ProductName, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;

        public static void OpenFolder(string path, bool select = false)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", select ? "/select,\"" + path + "\"" : "\"" + path + "\"");
            }
            catch { }
        }

        public static void OpenFile(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }
    }
}
