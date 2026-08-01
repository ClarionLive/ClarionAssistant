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
        // Win32 common-control ListView headers don't follow BackColor/ForeColor — without this,
        // the column header band stays light even in dark mode. "DarkMode_Explorer" is the standard
        // uxtheme.dll trick for making a ListView's native header (and scrollbar) follow dark mode.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private ListView _listView;
        private Button _refreshButton;
        private ToolTip _toolTip;
        private string _currentFileName = "";
        private readonly Action<int> _goToLine;
        private readonly Func<List<LspClient.DiagnosticEntry>> _refreshData;
        private bool _isDark = true;

        public DiagnosticsForm(Action<int> goToLine, Func<List<LspClient.DiagnosticEntry>> refreshData)
        {
            _goToLine = goToLine;
            _refreshData = refreshData;
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
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(_refreshButton, "Refresh");

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Cascadia Code", 9f, FontStyle.Regular,
                    GraphicsUnit.Point, 0, false)
            };
            _listView.Columns.Add("", 24);       // severity icon
            _listView.Columns.Add("Line", 50);
            _listView.Columns.Add("Message", 400);
            _listView.DoubleClick += OnListDoubleClick;
            _listView.KeyDown += OnListKeyDown;

            Controls.Add(_listView);
            Controls.Add(toolbar);

            ApplyTheme(_isDark);
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (isDark)
            {
                BackColor = Color.FromArgb(30, 30, 46);
                ForeColor = Color.FromArgb(205, 214, 244);
                _listView.BackColor = Color.FromArgb(24, 24, 37);
                _listView.ForeColor = Color.FromArgb(205, 214, 244);
                _refreshButton.ForeColor = Color.FromArgb(205, 214, 244);
                _refreshButton.FlatAppearance.BorderColor = Color.FromArgb(69, 71, 90);
            }
            else
            {
                BackColor = Color.FromArgb(239, 241, 245);
                ForeColor = Color.FromArgb(76, 79, 105);
                _listView.BackColor = Color.FromArgb(230, 233, 239);
                _listView.ForeColor = Color.FromArgb(76, 79, 105);
                _refreshButton.ForeColor = Color.FromArgb(76, 79, 105);
                _refreshButton.FlatAppearance.BorderColor = Color.FromArgb(188, 192, 204);
            }

            try { SetWindowTheme(_listView.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null); }
            catch { /* best-effort — header just stays whatever the OS default is */ }
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
            if (_refreshData != null)
            {
                var entries = _refreshData();
                UpdateDiagnostics(_currentFileName, entries);
            }
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
