using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// 7116020b: a small NATIVE warning at the bottom-right of the IDE, for problems that happen exactly when a
    /// WebView2 page is not available to toast in (a CA editor that failed to start, low memory). It is
    /// non-modal and never takes focus: a modal on this IDE can deadlock the native embeditor, and stealing
    /// focus mid-typing is worse than the problem being reported. One notice per key; Post with a live key
    /// updates it in place. It stays up until the user closes it or the caller calls Dismiss.
    /// </summary>
    internal sealed class CaNotice : Form
    {
        private static readonly Dictionary<string, CaNotice> _open = new Dictionary<string, CaNotice>(StringComparer.Ordinal);

        private readonly string _key;
        private readonly Label _title;
        private readonly Label _body;

        internal static void Post(string key, string title, string text)
        {
            try
            {
                CaNotice n;
                if (_open.TryGetValue(key, out n) && !n.IsDisposed)
                {
                    n._title.Text = title;
                    n._body.Text = text;
                    n.Reposition();
                    return;
                }
                n = new CaNotice(key, title, text);
                _open[key] = n;
                var owner = FindOwner();
                n.Reposition(owner);
                if (owner != null) n.Show(owner); else n.Show();
                MonacoSpikeLog.Write("[notice] shown key=" + key + " title=" + title);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[notice] show failed key=" + key + ": " + ex.Message); }
        }

        internal static void Dismiss(string key)
        {
            try
            {
                CaNotice n;
                if (_open.TryGetValue(key, out n)) { _open.Remove(key); if (!n.IsDisposed) n.Close(); }
            }
            catch { }
        }

        private static Form FindOwner()
        {
            try
            {
                var f = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.MainForm;
                if (f != null && !f.IsDisposed) return f;
            }
            catch { }
            return null;
        }

        private CaNotice(string key, string title, string text)
        {
            _key = key;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(0xFF, 0xF4, 0xCE);          // amber: a warning in both IDE themes
            ForeColor = Color.FromArgb(0x3A, 0x2E, 0x00);
            Width = 400;
            Padding = new Padding(12, 10, 12, 12);

            var close = new Label
            {
                Text = "×", Font = new Font("Segoe UI", 12f), AutoSize = false, Size = new Size(24, 24),
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand, Dock = DockStyle.Right
            };
            close.Click += (s, e) => Dismiss(_key);

            _title = new Label
            {
                Text = title, Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoSize = false, Height = 24,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
            };
            var header = new Panel { Dock = DockStyle.Top, Height = 24 };
            header.Controls.Add(_title);
            header.Controls.Add(close);

            _body = new Label
            {
                Text = text, Font = new Font("Segoe UI", 9.5f), AutoSize = true, Dock = DockStyle.Top,
                MaximumSize = new Size(Width - Padding.Horizontal, 0), Padding = new Padding(0, 6, 0, 0)
            };

            Controls.Add(_body);
            Controls.Add(header);   // added last so it docks above the body
            Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(0xC9, 0x8A, 0x00)))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
                return cp;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CaNotice n;
            if (_open.TryGetValue(_key, out n) && ReferenceEquals(n, this)) _open.Remove(_key);
            base.OnFormClosed(e);
        }

        private void Reposition(Form owner = null)
        {
            try
            {
                Height = Padding.Vertical + 24 + _body.PreferredSize.Height + 4;
                owner = owner ?? (Owner as Form) ?? FindOwner();
                Rectangle area = owner != null ? owner.RectangleToScreen(owner.ClientRectangle)
                                               : Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Right - Width - 16, area.Bottom - Height - 40);
            }
            catch { }
        }
    }
}
