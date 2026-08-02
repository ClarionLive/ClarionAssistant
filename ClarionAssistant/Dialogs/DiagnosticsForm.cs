using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClarionAssistant.Services;

namespace ClarionAssistant.Dialogs
{
    /// <summary>
    /// Stay-on-top diagnostics viewer. Shows all LSP diagnostics for the
    /// active file in a resizable ListView. Clicking a row navigates the
    /// IDE editor to that line. Stays visible until explicitly closed.
    /// </summary>
    public class DiagnosticsForm : Form
    {
        // Win32 common-control ListView headers don't follow BackColor/ForeColor, and the
        // "DarkMode_Explorer"/"DarkMode_ItemsView" SetWindowTheme trick only reliably recolors the
        // header BACKGROUND, not its text — leaving black-on-near-black in dark mode. Owner-drawing
        // the header (see OnDrawColumnHeader) gives full control over both, so use that instead.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        // Makes the native title bar itself follow dark/light mode (Windows 10 20H1+ / Windows 11),
        // so the caption looks like a normal OS window caption in either theme instead of always
        // rendering in the OS default (light) regardless of the client area's theme.
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        // Windows 11 22H2+: force the exact caption background/text color instead of relying on
        // the OS's generic dark-mode default, which renders caption text in a duller gray than
        // our own client-area text.
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        private ListView _listView;
        private Button _refreshButton;
        private ToolTip _toolTip;
        private string _currentFileName = "";
        private readonly Action<int> _goToLine;
        private readonly Func<List<LspClient.DiagnosticEntry>> _refreshData;
        // Refresh must re-read the file identity from the same source as the entries, not
        // reuse the cached _currentFileName — see RefreshFromSource.
        private readonly Func<string> _refreshFile;
        private bool _isDark = true;
        private Color _headerBackColor = Color.White;
        private Color _headerForeColor = Color.Black;
        private Color _gridLineColor = Color.FromArgb(220, 220, 220);

        public DiagnosticsForm(Action<int> goToLine,
                               Func<List<LspClient.DiagnosticEntry>> refreshData,
                               Func<string> refreshFile)
        {
            _goToLine = goToLine;
            _refreshData = refreshData;
            _refreshFile = refreshFile;
            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = "Diagnostics";
            Size = new Size(520, 340);
            MinimumSize = new Size(360, 200);
            TopMost = true;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            // Slim single strip: just the icon-only refresh button, right-aligned. The filename
            // used to live in its own row here — it's now folded into the window title instead
            // (see UpdateDiagnostics), so this row only needs to hold the refresh action.
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 24 };
            _refreshButton = new Button
            {
                Text = "⟳",
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f)
            };
            _refreshButton.Click += (s, e) => RefreshFromSource();
            toolbar.Controls.Add(_refreshButton);
            // ShowAlways is required here: a ToolTip only shows by default when its parent form
            // is the active foreground window, and this non-modal tool window (shown via
            // Show(this) from the docked chat pane) apparently never becomes active.
            _toolTip = new ToolTip { ShowAlways = true };
            _toolTip.SetToolTip(_refreshButton, "Refresh");

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                // Native gridlines always use a fixed system color (bright, non-theme-aware) —
                // drawn manually in OnDrawSubItem instead so they can match the theme.
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                OwnerDraw = true,
                Font = new Font("Cascadia Code", 9f, FontStyle.Regular,
                    GraphicsUnit.Point, 0, false)
            };
            _listView.Columns.Add("", 24);       // severity icon
            _listView.Columns.Add("Line", 50);
            _listView.Columns.Add("Message", 400);
            _listView.DoubleClick += OnListDoubleClick;
            _listView.KeyDown += OnListKeyDown;
            _listView.DrawColumnHeader += OnDrawColumnHeader;
            // Details view: draw everything (background, text, and the custom grid lines) in
            // DrawSubItem — DrawDefault=true there would paint AFTER any custom drawing here,
            // overwriting it, so DrawItem must fully defer instead of drawing its own default.
            _listView.DrawItem += (s, e) => e.DrawDefault = false;
            _listView.DrawSubItem += OnDrawSubItem;

            // ListView doesn't expose DoubleBuffered publicly. Without it, every owner-drawn
            // repaint (including the ones Windows triggers just from mouse-move hover tracking,
            // even with no hover-selection behavior configured) flashes visibly before painting.
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_listView, true, null);

            Controls.Add(_listView);
            Controls.Add(toolbar);

            ApplyTheme(_isDark);
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (isDark)
            {
                // Plain near-black, deliberately NOT the same blue-tinted black as the Monaco
                // editor chrome, so the list reads as a distinct panel rather than blending in.
                // Kept the same shade as the window/caption background per feedback, rather than
                // a darker shade of its own.
                BackColor = Color.FromArgb(32, 32, 32);
                ForeColor = Color.FromArgb(235, 235, 235);
                _listView.BackColor = Color.FromArgb(32, 32, 32);
                _listView.ForeColor = Color.FromArgb(235, 235, 235);
                _refreshButton.ForeColor = Color.FromArgb(235, 235, 235);
                _refreshButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
                _gridLineColor = Color.FromArgb(70, 70, 70);
                // Header matches the list body exactly, same as it does natively in light mode.
                _headerBackColor = _listView.BackColor;
                _headerForeColor = _listView.ForeColor;
            }
            else
            {
                BackColor = Color.FromArgb(240, 240, 240);
                ForeColor = Color.FromArgb(32, 32, 32);
                _listView.BackColor = Color.White;
                _listView.ForeColor = Color.FromArgb(32, 32, 32);
                _refreshButton.ForeColor = Color.FromArgb(32, 32, 32);
                _refreshButton.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
                _gridLineColor = Color.FromArgb(220, 220, 220);
                _headerBackColor = _listView.BackColor;
                _headerForeColor = _listView.ForeColor;
            }
            _listView.Invalidate();

            try { SetWindowTheme(_listView.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null); }
            catch { /* best-effort — scrollbar just stays whatever the OS default is */ }

            try
            {
                // Accessing Handle forces early creation if needed — safe pre-Show, and required
                // so the caption is already themed correctly on first display (no dark→light flash).
                int useDark = isDark ? 1 : 0;
                if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));

                int captionColor = ToColorRef(BackColor);
                int textColor = ToColorRef(ForeColor);
                DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { /* best-effort — caption just stays whatever the OS default is */ }
        }

        // GDI+ paints exactly the requested color with normal alpha blending against whatever's
        // already on the surface. GDI's TextRenderer.DrawText, even given the correct background
        // color, still renders ClearType-composited text visibly paler than the literal RGB value
        // for these saturated severity colors — DrawString avoids that ambiguity entirely.
        private static readonly StringFormat CellTextFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        // TextRenderer.DrawText added a couple pixels of left padding automatically; DrawString
        // doesn't, so without this the text would sit flush against the cell's left edge.
        private static RectangleF Inset(Rectangle bounds) =>
            new RectangleF(bounds.X + 2, bounds.Y, Math.Max(0, bounds.Width - 2), bounds.Height);

        private void OnDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var brush = new SolidBrush(_headerBackColor))
                e.Graphics.FillRectangle(brush, e.Bounds);
            using (var foreBrush = new SolidBrush(_headerForeColor))
                e.Graphics.DrawString(e.Header.Text, _listView.Font, foreBrush, Inset(e.Bounds), CellTextFormat);
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            bool selected = e.Item.Selected;
            // A flat middle gray sits in the worst contrast zone for the pastel/saturated
            // severity colors (red/amber/blue) used for the text — it's roughly as bright as
            // the amber text itself. A subtle per-theme tint close to the row's own background
            // keeps the same contrast the text already had while still reading as "selected".
            Color back = selected
                ? (_isDark ? Color.FromArgb(60, 60, 68) : Color.FromArgb(210, 210, 218))
                : _listView.BackColor;
            Color fore = e.Item.ForeColor;

            using (var backBrush = new SolidBrush(back))
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            using (var foreBrush = new SolidBrush(fore))
                e.Graphics.DrawString(e.SubItem.Text, _listView.Font, foreBrush, Inset(e.Bounds), CellTextFormat);

            using (var pen = new Pen(_gridLineColor))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            }
        }

        public void UpdateDiagnostics(string filePath, List<LspClient.DiagnosticEntry> entries)
        {
            _currentFileName = string.IsNullOrEmpty(filePath) ? "" : System.IO.Path.GetFileName(filePath);
            Text = string.IsNullOrEmpty(_currentFileName) ? "Diagnostics" : "Diagnostics — " + _currentFileName;

            _listView.BeginUpdate();
            _listView.Items.Clear();

            if (entries != null)
            {
                foreach (var e in entries)
                {
                    string icon = e.Severity == 1 ? "\u2716" :   // ✖ error
                                  e.Severity == 2 ? "\u26A0" :   // ⚠ warning
                                  e.Severity == 3 ? "\u2139" :   // ℹ info
                                  "\u2022";                       // • hint
                    var item = new ListViewItem(icon);
                    item.SubItems.Add((e.Line + 1).ToString());   // LSP 0-based → display 1-based
                    item.SubItems.Add(e.Message ?? "");
                    item.Tag = e.Line;                             // store 0-based for GoToLine

                    if (_isDark)
                    {
                        item.ForeColor = e.Severity == 1 ? Color.FromArgb(243, 139, 168) :  // red
                                         e.Severity == 2 ? Color.FromArgb(250, 179, 135) :  // amber
                                         Color.FromArgb(137, 180, 250);                      // blue
                    }
                    else
                    {
                        item.ForeColor = e.Severity == 1 ? Color.FromArgb(210, 15, 57) :
                                         e.Severity == 2 ? Color.FromArgb(254, 100, 11) :
                                         Color.FromArgb(30, 102, 245);
                    }

                    _listView.Items.Add(item);
                }
            }

            _listView.EndUpdate();

            if (_listView.Columns.Count > 2)
                _listView.Columns[2].Width = -1; // auto-size message column
        }

        private void RefreshFromSource()
        {
            if (_refreshData == null) return;

            // Both the file and the entries must come from the current source. Passing the
            // cached _currentFileName here instead captioned the window with whatever file
            // was showing when it last updated, while the rows below were the newly-fetched
            // entries — which belong to whichever file the LSP has since moved on to. The
            // two reads are safe as a pair: the source updates them together on the UI
            // thread (AssistantChatControl.PollLspUi), and this runs on the UI thread too.
            var entries = _refreshData();
            string filePath = _refreshFile != null ? _refreshFile() : _currentFileName;
            UpdateDiagnostics(filePath, entries);
        }

        private void OnListDoubleClick(object sender, EventArgs e)
        {
            NavigateToSelected();
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                NavigateToSelected();
                e.Handled = true;
            }
        }

        private void NavigateToSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var item = _listView.SelectedItems[0];
            if (item.Tag is int line)
            {
                try { _goToLine(line + 1); } // LSP 0-based → IDE 1-based
                catch { }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Hide instead of close — reuse the form
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }
    }
}
