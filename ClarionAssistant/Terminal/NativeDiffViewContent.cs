using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Pure WinForms diff viewer — no WebView2, no CDN, no JavaScript.
    /// Uses DataGridView with LCS-based diff computation and color-coded rows.
    /// Opens as a tab in the IDE's main editor panel.
    /// </summary>
    public class NativeDiffViewContent : AbstractViewContent
    {
        // Catppuccin Mocha palette
        private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
        private static readonly Color FgColor = Color.FromArgb(205, 214, 244);
        private static readonly Color GutterBg = Color.FromArgb(24, 24, 37);
        private static readonly Color GutterFg = Color.FromArgb(166, 173, 200);
        private static readonly Color GridLine = Color.FromArgb(49, 50, 68);
        private static readonly Color HeaderBg = Color.FromArgb(24, 24, 37);
        private static readonly Color HeaderFg = Color.FromArgb(166, 173, 200);
        private static readonly Color SelectBg = Color.FromArgb(69, 71, 90);
        private static readonly Color AccentFg = Color.FromArgb(137, 180, 250);

        // Diff highlight colors
        private static readonly Color DelBg = Color.FromArgb(65, 30, 35);
        private static readonly Color DelGutter = Color.FromArgb(85, 35, 40);
        private static readonly Color AddBg = Color.FromArgb(30, 55, 35);
        private static readonly Color AddGutter = Color.FromArgb(35, 70, 40);

        private Panel _panel;
        private DataGridView _grid;
        private ToolStripLabel _counterLabel;
        private ToolStripLabel _fontSizeLabel;
        private float _fontSize = 10f;
        private const float MinFontSize = 8f;
        private const float MaxFontSize = 30f;

        private List<DiffRow> _rows = new List<DiffRow>();
        private List<int> _hunkStarts = new List<int>();
        private int _currentHunk = -1;

        public event Action<string> Applied;
        public event Action Cancelled;

        public override Control Control { get { return _panel; } }

        public NativeDiffViewContent(string title, string origText, string modText,
            string language = "clarion", bool ignoreWhitespace = false)
        {
            TitleName = "Diff: " + (title ?? "Untitled");
            _panel = new Panel { Dock = DockStyle.Fill, BackColor = BgColor };

            BuildToolbar(title);
            BuildGrid();

            ComputeDiff(origText ?? "", modText ?? "", ignoreWhitespace);
            PopulateGrid();
            FindHunks();
            UpdateCounter();

            if (_hunkStarts.Count > 0)
                GoToHunk(0);
        }

        #region UI Construction

        private void BuildToolbar(string title)
        {
            var bar = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                RenderMode = ToolStripRenderMode.Professional,
                Renderer = new DarkRenderer(),
                Padding = new Padding(6, 3, 6, 3),
                Height = 32
            };

            var titleLabel = new ToolStripLabel("Diff: " + (title ?? ""))
            {
                ForeColor = FgColor,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            bar.Items.Add(titleLabel);
            bar.Items.Add(new ToolStripSeparator());

            var prev = new ToolStripButton("\u25B2 Prev") { ForeColor = AccentFg, ToolTipText = "Previous change (Shift+F7)" };
            prev.Click += (s, e) => GoPrev();
            bar.Items.Add(prev);

            _counterLabel = new ToolStripLabel("--") { ForeColor = GutterFg, AutoSize = true };
            bar.Items.Add(_counterLabel);

            var next = new ToolStripButton("Next \u25BC") { ForeColor = AccentFg, ToolTipText = "Next change (F7)" };
            next.Click += (s, e) => GoNext();
            bar.Items.Add(next);

            bar.Items.Add(new ToolStripSeparator());

            var smaller = new ToolStripButton("A\u2212") { ForeColor = AccentFg, ToolTipText = "Decrease font size (Ctrl+minus)" };
            smaller.Click += (s, e) => ChangeFontSize(-1f);
            bar.Items.Add(smaller);

            _fontSizeLabel = new ToolStripLabel(_fontSize + "px") { ForeColor = GutterFg, AutoSize = true };
            bar.Items.Add(_fontSizeLabel);

            var larger = new ToolStripButton("A+") { ForeColor = AccentFg, ToolTipText = "Increase font size (Ctrl+plus)" };
            larger.Click += (s, e) => ChangeFontSize(1f);
            bar.Items.Add(larger);

            bar.Items.Add(new ToolStripSeparator());

            var close = new ToolStripButton("Close") { ForeColor = FgColor, Alignment = ToolStripItemAlignment.Right };
            close.Click += (s, e) => { Cancelled?.Invoke(); };
            bar.Items.Add(close);

            _panel.Controls.Add(bar);
        }

        private void BuildGrid()
        {
            _grid = new BufferedGrid
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleVertical,
                BackgroundColor = BgColor,
                GridColor = GridLine,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26,
                RowTemplate = { Height = 20 },
                Font = new Font("Consolas", 10f),
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = HeaderBg, ForeColor = HeaderFg,
                    SelectionBackColor = HeaderBg, SelectionForeColor = HeaderFg,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Padding = new Padding(4, 0, 4, 0)
                },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = BgColor, ForeColor = FgColor,
                    SelectionBackColor = SelectBg, SelectionForeColor = FgColor,
                    WrapMode = DataGridViewTriState.False,
                    Padding = new Padding(2, 0, 2, 0)
                }
            };

            var gutterStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleRight,
                BackColor = GutterBg, ForeColor = GutterFg,
                SelectionBackColor = GutterBg, SelectionForeColor = GutterFg,
                Font = new Font("Consolas", 9f)
            };

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "LNo", HeaderText = "#", Width = 50,
                DefaultCellStyle = gutterStyle, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "LTxt", HeaderText = "Original",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "RNo", HeaderText = "#", Width = 50,
                DefaultCellStyle = gutterStyle, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "RTxt", HeaderText = "Modified",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });

            _grid.CellFormatting += OnCellFormatting;
            _grid.KeyDown += OnGridKeyDown;
            _grid.MouseWheel += OnGridMouseWheel;

            _panel.Controls.Add(_grid);
        }

        #endregion

        #region Diff Computation

        private void ComputeDiff(string origText, string modText, bool ignoreWs)
        {
            string[] oLines = SplitLines(origText);
            string[] mLines = SplitLines(modText);

            int n = oLines.Length, m = mLines.Length;

            // Trim common prefix
            int pre = 0;
            while (pre < n && pre < m && Eq(oLines[pre], mLines[pre], ignoreWs))
                pre++;

            // Trim common suffix (not overlapping prefix)
            int suf = 0;
            while (suf < n - pre && suf < m - pre &&
                   Eq(oLines[n - 1 - suf], mLines[m - 1 - suf], ignoreWs))
                suf++;

            // Emit prefix
            for (int i = 0; i < pre; i++)
                _rows.Add(new DiffRow(i + 1, oLines[i], i + 1, mLines[i], DiffType.Equal));

            // Middle section
            int os = pre, oe = n - suf;
            int ms = pre, me = m - suf;
            int oc = oe - os, mc = me - ms;

            if (oc > 0 || mc > 0)
            {
                // Guard against huge inputs — fall back to full replacement
                if ((long)oc * mc > 10_000_000)
                {
                    for (int i = os; i < oe; i++)
                        _rows.Add(DiffRow.Left(i + 1, oLines[i]));
                    for (int i = ms; i < me; i++)
                        _rows.Add(DiffRow.Right(i + 1, mLines[i]));
                }
                else
                {
                    LcsDiff(oLines, os, oc, mLines, ms, mc, ignoreWs);
                }
            }

            // Emit suffix
            for (int i = 0; i < suf; i++)
            {
                int oi = n - suf + i, mi = m - suf + i;
                _rows.Add(new DiffRow(oi + 1, oLines[oi], mi + 1, mLines[mi], DiffType.Equal));
            }
        }

        private void LcsDiff(string[] oLines, int os, int oc, string[] mLines, int ms, int mc, bool ignoreWs)
        {
            // Build LCS DP table
            int[,] dp = new int[oc + 1, mc + 1];
            for (int i = 1; i <= oc; i++)
                for (int j = 1; j <= mc; j++)
                    dp[i, j] = Eq(oLines[os + i - 1], mLines[ms + j - 1], ignoreWs)
                        ? dp[i - 1, j - 1] + 1
                        : Math.Max(dp[i - 1, j], dp[i, j - 1]);

            // Backtrack to produce diff rows (in reverse)
            var mid = new List<DiffRow>();
            int oi = oc, mi = mc;

            while (oi > 0 || mi > 0)
            {
                if (oi > 0 && mi > 0 && Eq(oLines[os + oi - 1], mLines[ms + mi - 1], ignoreWs))
                {
                    mid.Add(new DiffRow(os + oi, oLines[os + oi - 1], ms + mi, mLines[ms + mi - 1], DiffType.Equal));
                    oi--; mi--;
                }
                else if (mi > 0 && (oi == 0 || dp[oi, mi - 1] >= dp[oi - 1, mi]))
                {
                    mid.Add(DiffRow.Right(ms + mi, mLines[ms + mi - 1]));
                    mi--;
                }
                else
                {
                    mid.Add(DiffRow.Left(os + oi, oLines[os + oi - 1]));
                    oi--;
                }
            }

            mid.Reverse();
            _rows.AddRange(mid);
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            // Remove trailing empty element from a final newline
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
                Array.Resize(ref lines, lines.Length - 1);
            return lines;
        }

        private static bool Eq(string a, string b, bool ignoreWs)
        {
            if (ignoreWs)
                return string.Equals(NormalizeWs(a), NormalizeWs(b), StringComparison.Ordinal);
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>Collapse runs of whitespace to single space and trim.</summary>
        private static string NormalizeWs(string s)
        {
            if (s == null) return null;
            var sb = new System.Text.StringBuilder(s.Length);
            bool lastWasSpace = true; // true to trim leading
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t')
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            // Trim trailing space
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                sb.Length--;
            return sb.ToString();
        }

        #endregion

        #region Grid Population & Formatting

        private void PopulateGrid()
        {
            _grid.SuspendLayout();
            var gridRows = new DataGridViewRow[_rows.Count];

            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                var gr = new DataGridViewRow { Height = Math.Max(16, (int)(_fontSize * 1.8f)) };
                gr.CreateCells(_grid,
                    r.LeftNo.HasValue ? r.LeftNo.Value.ToString() : "",
                    r.LeftText ?? "",
                    r.RightNo.HasValue ? r.RightNo.Value.ToString() : "",
                    r.RightText ?? "");
                gridRows[i] = gr;
            }

            _grid.Rows.AddRange(gridRows);
            _grid.ResumeLayout();
            _grid.ClearSelection();
        }

        private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            var row = _rows[e.RowIndex];
            if (row.Type == DiffType.Equal) return;

            bool gutter = (e.ColumnIndex == 0 || e.ColumnIndex == 2);
            bool isDel = (row.Type == DiffType.Deleted);

            Color bg = isDel ? (gutter ? DelGutter : DelBg) : (gutter ? AddGutter : AddBg);
            e.CellStyle.BackColor = bg;
            e.CellStyle.SelectionBackColor = bg;
        }

        #endregion

        #region Navigation

        private void FindHunks()
        {
            _hunkStarts.Clear();
            bool inHunk = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                bool changed = _rows[i].Type != DiffType.Equal;
                if (changed && !inHunk)
                {
                    _hunkStarts.Add(i);
                    inHunk = true;
                }
                else if (!changed)
                {
                    inHunk = false;
                }
            }
        }

        private void GoNext()
        {
            if (_hunkStarts.Count == 0) return;
            _currentHunk = (_currentHunk + 1) % _hunkStarts.Count;
            GoToHunk(_currentHunk);
        }

        private void GoPrev()
        {
            if (_hunkStarts.Count == 0) return;
            _currentHunk = (_currentHunk - 1 + _hunkStarts.Count) % _hunkStarts.Count;
            GoToHunk(_currentHunk);
        }

        private void GoToHunk(int index)
        {
            _currentHunk = index;
            int row = _hunkStarts[index];
            int ctx = Math.Max(0, row - 3);

            if (ctx < _grid.Rows.Count)
                _grid.FirstDisplayedScrollingRowIndex = ctx;

            _grid.ClearSelection();
            if (row < _grid.Rows.Count)
                _grid.Rows[row].Selected = true;

            UpdateCounter();
        }

        private void UpdateCounter()
        {
            if (_counterLabel == null) return;
            if (_hunkStarts.Count == 0)
                _counterLabel.Text = "No changes";
            else if (_currentHunk < 0)
                _counterLabel.Text = _hunkStarts.Count + " changes";
            else
                _counterLabel.Text = (_currentHunk + 1) + " of " + _hunkStarts.Count;
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F7 && e.Shift) { GoPrev(); e.Handled = true; }
            else if (e.KeyCode == Keys.F7) { GoNext(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { Cancelled?.Invoke(); e.Handled = true; }
            else if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add))
            { ChangeFontSize(1f); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract))
            { ChangeFontSize(-1f); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.D0)
            { ChangeFontSize(10f - _fontSize); e.Handled = true; e.SuppressKeyPress = true; }
        }

        private void OnGridMouseWheel(object sender, MouseEventArgs e)
        {
            if (Control.ModifierKeys != Keys.Control) return;
            ChangeFontSize(e.Delta > 0 ? 1f : -1f);
            ((HandledMouseEventArgs)e).Handled = true;
        }

        private void ChangeFontSize(float delta)
        {
            float newSize = Math.Max(MinFontSize, Math.Min(MaxFontSize, _fontSize + delta));
            if (Math.Abs(newSize - _fontSize) < 0.01f) return;
            _fontSize = newSize;

            _grid.SuspendLayout();

            var newFont = new Font("Consolas", _fontSize);
            var oldFont = _grid.Font;
            _grid.Font = newFont;

            var gutterFont = new Font("Consolas", _fontSize - 1f);
            _grid.Columns["LNo"].DefaultCellStyle.Font = gutterFont;
            _grid.Columns["RNo"].DefaultCellStyle.Font = gutterFont;

            int rowH = Math.Max(16, (int)(_fontSize * 1.8f));
            _grid.RowTemplate.Height = rowH;
            foreach (DataGridViewRow row in _grid.Rows)
                row.Height = rowH;

            oldFont.Dispose();
            _grid.ResumeLayout();

            if (_fontSizeLabel != null)
                _fontSizeLabel.Text = _fontSize + "px";
        }

        #endregion

        public override void Dispose()
        {
            if (_grid != null) { _grid.Dispose(); _grid = null; }
            if (_panel != null) { _panel.Dispose(); _panel = null; }
            base.Dispose();
        }

        #region Helper Types

        private enum DiffType { Equal, Deleted, Added }

        private class DiffRow
        {
            public int? LeftNo;
            public string LeftText;
            public int? RightNo;
            public string RightText;
            public DiffType Type;

            public DiffRow() { }

            public DiffRow(int leftNo, string leftText, int rightNo, string rightText, DiffType type)
            {
                LeftNo = leftNo; LeftText = leftText;
                RightNo = rightNo; RightText = rightText;
                Type = type;
            }

            public static DiffRow Left(int lineNo, string text)
            {
                return new DiffRow { LeftNo = lineNo, LeftText = text, Type = DiffType.Deleted };
            }

            public static DiffRow Right(int lineNo, string text)
            {
                return new DiffRow { RightNo = lineNo, RightText = text, Type = DiffType.Added };
            }
        }

        private class BufferedGrid : DataGridView
        {
            public BufferedGrid()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
        }

        private class DarkRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(Color.FromArgb(24, 24, 37));
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(49, 50, 68)))
                    e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Height - 1,
                        e.AffectedBounds.Width, e.AffectedBounds.Height - 1);
            }
        }

        #endregion
    }
}
