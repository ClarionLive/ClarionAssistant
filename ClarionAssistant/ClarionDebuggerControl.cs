using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// CA Debugger pad (Phase 2). Front-end over the standalone ClarionDbg interactive engine:
    /// pick a target EXE, manage module:line breakpoints, run, and drive execution with
    /// Continue / Step Over / Step Into / Step Out. On every pause it resolves the generated-source
    /// path via the active .red, lights the editor's current-line bar, and shows registers.
    /// Coexists with Clarion's built-in debugger — it does not touch the Debug button.
    /// </summary>
    public sealed class ClarionDebuggerControl : UserControl
    {
        private readonly TextBox _exe = new TextBox();
        private readonly Button _browse = new Button();

        private readonly ToolStrip _toolbar = new ToolStrip();
        private readonly ToolStripButton _start = new ToolStripButton();
        private readonly ToolStripButton _continue = new ToolStripButton();
        private readonly ToolStripButton _stepOver = new ToolStripButton();
        private readonly ToolStripButton _stepInto = new ToolStripButton();
        private readonly ToolStripButton _stepOut = new ToolStripButton();
        private readonly ToolStripButton _stopBtn = new ToolStripButton();
        private readonly ToolStripButton _bpCaret = new ToolStripButton();
        private readonly ToolStripLabel _status = new ToolStripLabel();
        private readonly ToolStripButton _fontMinus = new ToolStripButton();
        private readonly ToolStripButton _fontPlus = new ToolStripButton();

        private readonly TextBox _module = new TextBox();
        private readonly TextBox _line = new TextBox();
        private readonly Button _addBp = new Button();
        private readonly Button _delBp = new Button();
        private readonly Button _lines = new Button();
        private readonly ListBox _bpList = new ListBox();

        private readonly RichTextBox _log = new RichTextBox();
        private readonly ClarionDebuggerService _svc = new ClarionDebuggerService();
        private readonly EditorBreakpointService _gutter = new EditorBreakpointService();

        // watch pane (minimal Phase 2 scope: registers + address-based typed memory watches)
        private readonly SplitContainer _split = new SplitContainer();
        private readonly TextBox _regsBox = new TextBox();
        private readonly ListView _watchList = new ListView();
        private readonly TextBox _watchAddr = new TextBox();
        private readonly ComboBox _watchType = new ComboBox();
        private readonly TextBox _watchLen = new TextBox();
        private readonly Button _addWatch = new Button();
        private readonly Button _delWatch = new Button();
        private readonly List<WatchEntry> _watches = new List<WatchEntry>();

        // desired breakpoints while idle; engine-confirmed (snapped) entries while running
        private readonly List<DebugBreakpoint> _bps = new List<DebugBreakpoint>();
        private float _logFontSize = 9f;

        public ClarionDebuggerControl()
        {
            BuildLayout();

            _svc.StateChanged += s => UI(() => ApplyState(s));
            _svc.HitReceived += OnHit;
            _svc.Paused += OnPaused;
            _svc.Resumed += mode => UI(() => { AppendLog("resumed (" + mode + ")", Color.SkyBlue); TryRemoveMarker(); });
            _svc.BreakpointSet += bp => UI(() => OnBpSet(bp));
            _svc.BreakpointRemoved += (m, l) => UI(() => OnBpRemoved(m, l));
            _svc.BreakpointError += (m, l, err) => UI(() => AppendLog("breakpoint " + m + ":" + l + " — " + err, Color.Goldenrod));
            _svc.BreakpointListReceived += list => UI(() => { _bps.Clear(); _bps.AddRange(list); RefreshBpList(); });
            _svc.EngineError += msg => UI(() => AppendLog("engine: " + msg, Color.Salmon));
            _svc.LogReceived += s => AppendLog(s, Color.Gray);
            _svc.Exited += code => UI(() =>
            {
                AppendLog("— session ended (exit " + code + ") —", Color.DimGray);
                TryRemoveMarker();
                ApplyState(DebugSessionState.Idle);
            });

            _browse.Click += (s, e) => BrowseExe();
            _start.Click += (s, e) => StartSession();
            _continue.Click += (s, e) => _svc.Continue();
            _stepOver.Click += (s, e) => _svc.StepOver();
            _stepInto.Click += (s, e) => _svc.StepInto();
            _stepOut.Click += (s, e) => _svc.StepOut();
            _stopBtn.Click += (s, e) => _svc.Stop();
            _addBp.Click += (s, e) => AddBreakpoint();
            _delBp.Click += (s, e) => RemoveSelectedBreakpoint();
            _lines.Click += (s, e) => ShowBreakableLines();
            _fontMinus.Click += (s, e) => SetLogFont(_logFontSize - 1f);
            _fontPlus.Click += (s, e) => SetLogFont(_logFontSize + 1f);
            _addWatch.Click += (s, e) => AddWatch();
            _delWatch.Click += (s, e) => RemoveSelectedWatch();
            _svc.MemoryReceived += (addr, len, bytes) => UI(() => OnWatchMemory(addr, len, bytes));

            _bpCaret.Click += (s, e) => ToggleBreakpointAtCaret();
            _gutter.GutterBreakpointAdded += (m, l, f) => UI(() => OnGutterBpAdded(m, l, f));
            _gutter.GutterBreakpointRemoved += (m, l, f) => UI(() => OnGutterBpRemoved(m, l));

            ApplyState(DebugSessionState.Idle);
        }

        private void BuildLayout()
        {
            // --- target row ---
            var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Padding = new Padding(6, 6, 6, 0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _browse.Text = "..."; _browse.Width = 30;
            top.Controls.Add(new Label { Text = "Target EXE", Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
            _exe.Dock = DockStyle.Fill;
            top.Controls.Add(_exe, 1, 0);
            top.Controls.Add(_browse, 2, 0);

            // --- debug toolbar ---
            _toolbar.GripStyle = ToolStripGripStyle.Hidden;
            _toolbar.Dock = DockStyle.Top;
            _start.Text = "▶ Start";
            _continue.Text = "▶ Continue";
            _stepOver.Text = "⤵ Step Over";
            _stepInto.Text = "↓ Step Into";
            _stepOut.Text = "↑ Step Out";
            _stopBtn.Text = "■ Stop";
            _fontMinus.Text = "A−"; _fontMinus.ToolTipText = "Smaller log font";
            _fontPlus.Text = "A+"; _fontPlus.ToolTipText = "Larger log font";
            _status.Text = "idle";
            _status.ForeColor = Color.DimGray;
            _toolbar.Items.Add(_start);
            _toolbar.Items.Add(_continue);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_stepOver);
            _toolbar.Items.Add(_stepInto);
            _toolbar.Items.Add(_stepOut);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_stopBtn);
            _toolbar.Items.Add(new ToolStripSeparator());
            _bpCaret.Text = "● BP @ Caret";
            _bpCaret.ToolTipText = "Toggle a breakpoint on the active editor's caret line";
            _toolbar.Items.Add(_bpCaret);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(_status);
            _fontPlus.Alignment = ToolStripItemAlignment.Right;
            _fontMinus.Alignment = ToolStripItemAlignment.Right;
            _toolbar.Items.Add(_fontPlus);
            _toolbar.Items.Add(_fontMinus);

            // --- breakpoints row ---
            var bpPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            bpPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bpPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));

            var bpInput = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            bpInput.Controls.Add(new Label { Text = "Module", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            _module.Width = 130;
            bpInput.Controls.Add(_module);
            bpInput.Controls.Add(new Label { Text = "Line", AutoSize = true, Padding = new Padding(4, 6, 4, 0) });
            _line.Width = 55;
            bpInput.Controls.Add(_line);
            _addBp.Text = "Add BP"; _addBp.Width = 60;
            _delBp.Text = "Remove"; _delBp.Width = 60;
            _lines.Text = "Lines"; _lines.Width = 50;
            bpInput.Controls.Add(_addBp);
            bpInput.Controls.Add(_delBp);
            bpInput.Controls.Add(_lines);
            bpPanel.Controls.Add(bpInput, 0, 0);

            _bpList.Height = 56;
            _bpList.IntegralHeight = false;
            _bpList.Dock = DockStyle.Fill;
            bpPanel.Controls.Add(_bpList, 1, 0);

            // --- log (top of split) ---
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.Font = new Font(FontFamily.GenericMonospace, _logFontSize);
            _log.BackColor = Color.FromArgb(30, 30, 30);
            _log.ForeColor = Color.Gainsboro;

            // --- watch pane (bottom of split): registers strip + typed memory watches ---
            var watchPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            watchPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // regs
            watchPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // watch input
            watchPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // watch list

            _regsBox.Dock = DockStyle.Fill;
            _regsBox.ReadOnly = true;
            _regsBox.BorderStyle = BorderStyle.None;
            _regsBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            _regsBox.BackColor = Color.FromArgb(40, 40, 40);
            _regsBox.ForeColor = Color.Khaki;
            _regsBox.Text = "(registers appear when paused)";
            watchPanel.Controls.Add(_regsBox, 0, 0);

            var watchInput = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0), Padding = new Padding(4, 2, 4, 0) };
            watchInput.Controls.Add(new Label { Text = "Addr 0x", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
            _watchAddr.Width = 80;
            watchInput.Controls.Add(_watchAddr);
            _watchType.DropDownStyle = ComboBoxStyle.DropDownList;
            _watchType.Width = 80;
            _watchType.Items.AddRange(new object[] { "STRING", "CSTRING", "BYTE", "SHORT", "USHORT", "LONG", "ULONG", "SREAL", "REAL", "DECIMAL", "HEX" });
            _watchType.SelectedIndex = 5; // LONG
            watchInput.Controls.Add(_watchType);
            watchInput.Controls.Add(new Label { Text = "Len", AutoSize = true, Padding = new Padding(4, 6, 2, 0) });
            _watchLen.Width = 45;
            _watchLen.Text = "4";
            watchInput.Controls.Add(_watchLen);
            _addWatch.Text = "Watch"; _addWatch.Width = 55;
            _delWatch.Text = "Remove"; _delWatch.Width = 60;
            watchInput.Controls.Add(_addWatch);
            watchInput.Controls.Add(_delWatch);
            watchPanel.Controls.Add(watchInput, 0, 1);

            _watchList.Dock = DockStyle.Fill;
            _watchList.View = View.Details;
            _watchList.FullRowSelect = true;
            _watchList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _watchList.Columns.Add("Address", 90);
            _watchList.Columns.Add("Type", 80);
            _watchList.Columns.Add("Len", 40);
            _watchList.Columns.Add("Value", 260);
            watchPanel.Controls.Add(_watchList, 0, 2);

            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;
            _split.Panel1.Controls.Add(_log);
            _split.Panel2.Controls.Add(watchPanel);
            _split.SplitterDistance = 200;
            _split.Panel2MinSize = 90;

            Controls.Add(_split);
            Controls.Add(bpPanel);
            Controls.Add(_toolbar);
            Controls.Add(top);
        }

        // ------------------------------------------------------------------ session control

        private void BrowseExe()
        {
            using (var dlg = new OpenFileDialog { Filter = "Clarion executables (*.exe)|*.exe|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) _exe.Text = dlg.FileName;
            }
        }

        private void StartSession()
        {
            try
            {
                string exe = _exe.Text.Trim();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    MessageBox.Show(this, "Pick a target executable first.", "CA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // merge any IDE gutter bookmarks (red dots) the user toggled before starting
                foreach (var gb in _gutter.Snapshot())
                {
                    bool known = false;
                    foreach (var b in _bps)
                        if (SameBp(b, gb.Module, gb.RequestedLine)) { known = true; break; }
                    if (!known) _bps.Add(gb);
                }
                RefreshBpList();

                _log.Clear();
                AppendLog("starting: " + Path.GetFileName(exe) + "  (" + _bps.Count + " breakpoint(s))", Color.SkyBlue);
                if (_bps.Count == 0)
                    AppendLog("   no breakpoints — add one above (or ● BP @ Caret); they plant live", Color.DarkGray);
                _svc.StartSession(exe, _bps.ToArray());
            }
            catch (Exception ex)
            {
                AppendLog("ERROR: " + ex.Message, Color.Salmon);
                ApplyState(DebugSessionState.Idle);
            }
        }

        // ------------------------------------------------------------------ breakpoints

        /// <summary>Breakpoint identity: same module (case-insensitive) and the line matches either
        /// the requested or the planted (snapped) line — callers shouldn't care which side snapped.</summary>
        private static bool SameBp(DebugBreakpoint b, string module, int line)
        {
            return string.Equals(b.Module, module, StringComparison.OrdinalIgnoreCase)
                && (b.RequestedLine == line || b.Line == line);
        }

        private void AddBreakpoint()
        {
            int line;
            string module = _module.Text.Trim();
            if (string.IsNullOrEmpty(module) || !int.TryParse(_line.Text.Trim(), out line) || line <= 0)
            {
                MessageBox.Show(this, "Enter a module name (e.g. clbrws011.clw) and a positive line number.",
                    "CA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_svc.IsRunning)
            {
                _svc.AddBreakpoint(module, line);   // bp-set event updates the list (with snapping)
            }
            else
            {
                foreach (var b in _bps)
                    if (SameBp(b, module, line)) return;
                _bps.Add(new DebugBreakpoint { Module = module, RequestedLine = line, Line = line });
                RefreshBpList();
            }
        }

        private void RemoveSelectedBreakpoint()
        {
            var bp = _bpList.SelectedItem as BpItem;
            if (bp == null) return;
            if (_svc.IsRunning)
            {
                _svc.RemoveBreakpoint(bp.Bp.Module, bp.Bp.Line);   // bp-del event updates the list
            }
            else
            {
                _bps.Remove(bp.Bp);
                RefreshBpList();
            }
        }

        private void OnBpSet(DebugBreakpoint bp)
        {
            // replace a pending entry for the same module+requested line, else add
            for (int i = 0; i < _bps.Count; i++)
            {
                if (SameBp(_bps[i], bp.Module, bp.RequestedLine) || SameBp(_bps[i], bp.Module, bp.Line))
                {
                    _bps[i] = bp;
                    RefreshBpList();
                    if (bp.Line != bp.RequestedLine)
                        AppendLog("breakpoint " + bp.Module + ":" + bp.RequestedLine + " snapped to line " + bp.Line, Color.Goldenrod);
                    return;
                }
            }
            _bps.Add(bp);
            RefreshBpList();
            AppendLog("breakpoint set: " + bp.Module + ":" + bp.Line
                + (bp.Line != bp.RequestedLine ? "  (snapped from " + bp.RequestedLine + ")" : ""), Color.SkyBlue);
        }

        private void OnBpRemoved(string module, int line)
        {
            _bps.RemoveAll(b => SameBp(b, module, line));
            RefreshBpList();
            AppendLog("breakpoint removed: " + module + ":" + line, Color.SkyBlue);
        }

        // ---- IDE gutter (BreakpointBookmark) sync ----

        private void ToggleBreakpointAtCaret()
        {
            string err = _gutter.ToggleAtCaret();
            if (err != null) AppendLog("BP @ caret: " + err, Color.Goldenrod);
        }

        private void OnGutterBpAdded(string module, int line, string file)
        {
            AppendLog("gutter breakpoint: " + module + ":" + line, Color.SkyBlue);
            if (_svc.IsRunning)
            {
                _svc.AddBreakpoint(module, line);   // bp-set confirms (and may snap)
            }
            else
            {
                foreach (var b in _bps)
                    if (SameBp(b, module, line)) return;
                _bps.Add(new DebugBreakpoint { Module = module, RequestedLine = line, Line = line });
                RefreshBpList();
            }
        }

        private void OnGutterBpRemoved(string module, int line)
        {
            if (_svc.IsRunning)
            {
                _svc.RemoveBreakpoint(module, line);
            }
            else
            {
                _bps.RemoveAll(b => SameBp(b, module, line));
                RefreshBpList();
            }
        }

        private sealed class BpItem
        {
            public readonly DebugBreakpoint Bp;
            public BpItem(DebugBreakpoint bp) { Bp = bp; }
            public override string ToString()
            {
                return Bp.Module + ":" + Bp.Line + (Bp.RequestedLine > 0 && Bp.RequestedLine != Bp.Line ? " (req " + Bp.RequestedLine + ")" : "");
            }
        }

        private void RefreshBpList()
        {
            _bpList.BeginUpdate();
            _bpList.Items.Clear();
            foreach (var b in _bps) _bpList.Items.Add(new BpItem(b));
            _bpList.EndUpdate();
        }

        // ------------------------------------------------------------------ pause / hit handling

        private void OnHit(DebugHit hit)
        {
            UI(() =>
            {
                string where = hit.Resolved
                    ? hit.Module + " line " + hit.Line + (hit.Exact ? " (exact)" : " (+0x" + hit.Gap.ToString("X") + ")")
                    : "(unresolved address)";
                AppendLog("*** HIT  " + hit.Va + "  ->  " + where, Color.Lime);
            });
        }

        private void OnPaused(DebugPause p)
        {
            UI(() =>
            {
                string where = p.Resolved ? p.Module + " line " + p.Line : "(unresolved address " + p.Va + ")";
                AppendLog("paused [" + p.Reason + "]  " + where, Color.Lime);
                if (p.Regs != null)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var name in new[] { "eip", "esp", "ebp", "eax", "ebx", "ecx", "edx", "esi", "edi", "eflags" })
                    {
                        string v;
                        if (p.Regs.TryGetValue(name, out v))
                            sb.Append(name.ToUpperInvariant()).Append('=').Append(v).Append("  ");
                    }
                    _regsBox.Text = sb.ToString();
                }

                RefreshWatches();

                if (!string.IsNullOrEmpty(p.ResolvedPath))
                {
                    TryJump(p.ResolvedPath, p.Line);
                }
                else if (p.Resolved)
                {
                    AppendLog("   (could not resolve " + p.Module + " via the active .red — open the app's solution to enable the jump)", Color.Goldenrod);
                }

                _status.Text = "paused — " + where;
                _status.ForeColor = Color.DarkOrange;
            });
        }

        // ------------------------------------------------------------------ editor integration

        private void TryJump(string path, int line)
        {
            try
            {
                ICSharpCode.SharpDevelop.Debugging.DebuggerService.JumpToCurrentLine(path, line, 1, line, 1);
            }
            catch (Exception ex)
            {
                AppendLog("   (jump failed: " + ex.Message + ")", Color.Goldenrod);
            }
        }

        private void TryRemoveMarker()
        {
            try { ICSharpCode.SharpDevelop.Debugging.DebuggerService.RemoveCurrentLineMarker(); }
            catch { }
        }

        // ------------------------------------------------------------------ breakable lines helper

        private void ShowBreakableLines()
        {
            string exe = _exe.Text.Trim();
            string module = _module.Text.Trim();
            if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(module))
            {
                AppendLog("Enter a target EXE and module first, then click Lines.", Color.Goldenrod);
                return;
            }
            int[] lines = ClarionDebuggerService.GetBreakableLines(exe, module);
            if (lines.Length == 0)
            {
                AppendLog(module + ": no breakable lines (unknown module, or the EXE has no debug info)", Color.Goldenrod);
                return;
            }
            AppendLog(module + ": " + lines.Length + " breakable lines — " + CompactRanges(lines), Color.SkyBlue);
            AppendLog("   (a breakpoint on any other line snaps to the nearest one of these)", Color.DarkGray);
        }

        // Render a sorted line list as compact ranges, e.g. "17,19-30,171-216".
        private static string CompactRanges(int[] xs)
        {
            if (xs.Length == 0) return "(none)";
            var sb = new System.Text.StringBuilder();
            int start = xs[0], prev = xs[0];
            for (int i = 1; i <= xs.Length; i++)
            {
                if (i < xs.Length && xs[i] == prev + 1) { prev = xs[i]; continue; }
                if (sb.Length > 0) sb.Append(',');
                sb.Append(start == prev ? start.ToString() : start + "-" + prev);
                if (i < xs.Length) { start = xs[i]; prev = xs[i]; }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ watches

        /// <summary>One address-based memory watch with a manual Clarion type for rendering.</summary>
        private sealed class WatchEntry
        {
            public uint Addr;
            public string Type;      // STRING/CSTRING/BYTE/SHORT/USHORT/LONG/ULONG/SREAL/REAL/DECIMAL/HEX
            public int Len;          // bytes to read
            public ListViewItem Item;
        }

        /// <summary>Fixed byte size for a type, or 0 if the user supplies the length.</summary>
        private static int FixedTypeLen(string type)
        {
            switch (type)
            {
                case "BYTE": return 1;
                case "SHORT": case "USHORT": return 2;
                case "LONG": case "ULONG": case "SREAL": return 4;
                case "REAL": return 8;
                default: return 0; // STRING/CSTRING/DECIMAL/HEX — length from the Len box
            }
        }

        private void AddWatch()
        {
            string type = (string)(_watchType.SelectedItem ?? "LONG");
            uint addr;
            string a = _watchAddr.Text.Trim();
            if (a.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) a = a.Substring(2);
            if (!uint.TryParse(a, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out addr) || addr == 0)
            {
                MessageBox.Show(this, "Enter a hex address (e.g. 12FF40).", "CA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int len = FixedTypeLen(type);
            if (len == 0)
            {
                if (!int.TryParse(_watchLen.Text.Trim(), out len) || len <= 0 || len > 4096)
                {
                    MessageBox.Show(this, "Enter a length between 1 and 4096 for " + type + ".", "CA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var entry = new WatchEntry { Addr = addr, Type = type, Len = len };
            entry.Item = new ListViewItem(new[] { "0x" + addr.ToString("X8"), type, len.ToString(), "" });
            entry.Item.Tag = entry;
            _watches.Add(entry);
            _watchList.Items.Add(entry.Item);

            if (_svc.State == DebugSessionState.Paused)
                _svc.ReadMemory(entry.Addr, entry.Len);
        }

        private void RemoveSelectedWatch()
        {
            if (_watchList.SelectedItems.Count == 0) return;
            var item = _watchList.SelectedItems[0];
            var entry = item.Tag as WatchEntry;
            if (entry != null) _watches.Remove(entry);
            _watchList.Items.Remove(item);
        }

        /// <summary>Re-read every watch (engine answers each with a mem event).</summary>
        private void RefreshWatches()
        {
            foreach (var w in _watches)
            {
                w.Item.SubItems[3].Text = "…";
                _svc.ReadMemory(w.Addr, w.Len);
            }
        }

        private void OnWatchMemory(uint addr, int len, byte[] bytes)
        {
            // match on addr+len so two watches at the same address with different lengths each
            // render from their own mem reply (the engine echoes the requested len)
            foreach (var w in _watches)
            {
                if (w.Addr != addr || w.Len != len) continue;
                w.Item.SubItems[3].Text = bytes.Length < w.Len
                    ? "(short read: " + bytes.Length + " of " + w.Len + " bytes)"
                    : RenderValue(w.Type, bytes, w.Len);
            }
        }

        /// <summary>Render raw target memory as a Clarion-typed value.</summary>
        private static string RenderValue(string type, byte[] b, int len)
        {
            try
            {
                switch (type)
                {
                    case "BYTE": return b[0].ToString() + "  (0x" + b[0].ToString("X2") + ")";
                    case "SHORT": return BitConverter.ToInt16(b, 0).ToString();
                    case "USHORT": return BitConverter.ToUInt16(b, 0).ToString();
                    case "LONG": return BitConverter.ToInt32(b, 0).ToString() + "  (0x" + BitConverter.ToUInt32(b, 0).ToString("X8") + ")";
                    case "ULONG": return BitConverter.ToUInt32(b, 0).ToString() + "  (0x" + BitConverter.ToUInt32(b, 0).ToString("X8") + ")";
                    case "SREAL": return BitConverter.ToSingle(b, 0).ToString("R");
                    case "REAL": return BitConverter.ToDouble(b, 0).ToString("R");
                    case "STRING":
                        // Windows-1252 for deterministic rendering of Clarion ANSI byte strings
                        return "'" + System.Text.Encoding.GetEncoding(1252).GetString(b, 0, Math.Min(len, b.Length)).TrimEnd(' ') + "'";
                    case "CSTRING":
                        {
                            int n = Array.IndexOf(b, (byte)0);
                            if (n < 0 || n > len) n = Math.Min(len, b.Length);
                            return "'" + System.Text.Encoding.GetEncoding(1252).GetString(b, 0, n) + "'";
                        }
                    case "DECIMAL":
                        return RenderPackedDecimal(b, Math.Min(len, b.Length)) + "  [" + ToHex(b, Math.Min(len, b.Length)) + "]";
                    default: // HEX
                        return ToHex(b, Math.Min(len, b.Length));
                }
            }
            catch (Exception ex)
            {
                return "(render failed: " + ex.Message + ")";
            }
        }

        /// <summary>
        /// Best-effort Clarion DECIMAL render: packed BCD, sign in the high nibble of the first
        /// byte (0=+, D/B=−), digits follow. Shown alongside the raw hex so a wrong guess is visible.
        /// </summary>
        private static string RenderPackedDecimal(byte[] b, int len)
        {
            if (len == 0) return "?";
            var sb = new System.Text.StringBuilder();
            int signNibble = (b[0] >> 4) & 0xF;
            if (signNibble == 0xD || signNibble == 0xB) sb.Append('-');
            sb.Append((b[0] & 0xF).ToString("X"));
            for (int i = 1; i < len; i++)
            {
                sb.Append(((b[i] >> 4) & 0xF).ToString("X"));
                sb.Append((b[i] & 0xF).ToString("X"));
            }
            return sb.ToString().TrimStart('0').Length == 0 ? "0" : sb.ToString().TrimStart('0');
        }

        private static string ToHex(byte[] b, int len)
        {
            var sb = new System.Text.StringBuilder(len * 3);
            for (int i = 0; i < len; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ state + log plumbing

        private void ApplyState(DebugSessionState s)
        {
            bool idle = s == DebugSessionState.Idle;
            bool paused = s == DebugSessionState.Paused;
            bool live = s == DebugSessionState.Running || s == DebugSessionState.Paused || s == DebugSessionState.Launching;

            _start.Enabled = idle;
            _continue.Enabled = paused;
            _stepOver.Enabled = paused;
            _stepInto.Enabled = paused;
            _stepOut.Enabled = paused;
            _stopBtn.Enabled = live;
            _exe.Enabled = _browse.Enabled = idle;
            _lines.Enabled = idle;
            // breakpoints can be managed while idle (pending) and while live (engine add/del)
            _addBp.Enabled = _delBp.Enabled = _module.Enabled = _line.Enabled = true;

            switch (s)
            {
                case DebugSessionState.Idle: _status.Text = "idle"; _status.ForeColor = Color.DimGray; break;
                case DebugSessionState.Launching: _status.Text = "launching…"; _status.ForeColor = Color.SkyBlue; break;
                case DebugSessionState.Running: _status.Text = "running"; _status.ForeColor = Color.LimeGreen; break;
                case DebugSessionState.Paused: /* set in OnPaused with location */ break;
            }
        }

        private void SetLogFont(float size)
        {
            if (size < 6f) size = 6f;
            if (size > 24f) size = 24f;
            _logFontSize = size;
            // ZoomFactor scales like Ctrl+MouseWheel without touching per-character formatting —
            // reassigning Font would reset the RichTextBox's colors on existing lines
            _log.ZoomFactor = _logFontSize / 9f;
        }

        private void AppendLog(string text, Color color)
        {
            UI(() =>
            {
                _log.SelectionStart = _log.TextLength;
                _log.SelectionColor = color;
                _log.AppendText(text + Environment.NewLine);
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            });
        }

        private void UI(Action a)
        {
            if (IsHandleCreated && InvokeRequired) BeginInvoke(a);
            else a();
        }

        // ------------------------------------------------------------------ pad-close detection
        // Closing a SharpDevelop pad HIDES its WeifenLuo DockContent host — the pad is never
        // disposed until IDE shutdown, so Dispose alone leaves a live session (and its target
        // process) running invisibly. Hook the host's DockStateChanged by reflection (no hard
        // WeifenLuo reference) and stop the session when the pad is hidden/closed. Tab switching
        // within a dock group does not change DockState, so it won't kill the session.

        private bool _padCloseHooked;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HookPadCloseDetection();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            HookPadCloseDetection();
        }

        private void HookPadCloseDetection()
        {
            if (_padCloseHooked) return;
            try
            {
                var form = FindForm(); // SdiWorkbenchLayout+PadContentWrapper : WeifenLuo DockContent
                if (form == null) return;
                var evt = form.GetType().GetEvent("DockStateChanged");
                var hiddenProp = form.GetType().GetProperty("IsHidden");
                if (evt == null || hiddenProp == null) return;

                EventHandler handler = (s, args) =>
                {
                    try
                    {
                        if ((bool)hiddenProp.GetValue(form, null) && _svc.IsRunning)
                        {
                            _svc.Stop();
                            AppendLog("— pad closed: debug session stopped —", Color.DimGray);
                        }
                    }
                    catch { }
                };
                evt.AddEventHandler(form, handler);
                _padCloseHooked = true;
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _svc.Stop(); } catch { }
                try { _gutter.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
