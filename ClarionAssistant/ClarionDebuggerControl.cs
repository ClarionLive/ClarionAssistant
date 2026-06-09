using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// CA Debugger pad (Phase 1e). Non-invasive front-end over the standalone ClarionDbg engine:
    /// pick a target EXE + a module/line breakpoint, run, and watch live hits. On a hit it resolves
    /// the generated-source path via the active .red and lights the editor's current-line bar.
    /// Coexists with Clarion's built-in debugger — it does not touch the Debug button.
    /// </summary>
    public sealed class ClarionDebuggerControl : UserControl
    {
        private readonly TextBox _exe = new TextBox();
        private readonly TextBox _module = new TextBox();
        private readonly TextBox _line = new TextBox();
        private readonly Button _browse = new Button();
        private readonly Button _start = new Button();
        private readonly Button _stop = new Button();
        private readonly Button _lines = new Button();
        private readonly CheckBox _once = new CheckBox();
        private readonly RichTextBox _log = new RichTextBox();
        private readonly ClarionDebuggerService _svc = new ClarionDebuggerService();

        public ClarionDebuggerControl()
        {
            BuildLayout();

            _svc.HitReceived += OnHit;
            _svc.LogReceived += s => AppendLog(s, Color.Gray);
            _svc.Exited += code => UI(() => { AppendLog("— session ended (exit " + code + ") —", Color.DimGray); SetRunning(false); });

            _browse.Click += (s, e) => BrowseExe();
            _start.Click += (s, e) => StartSession();
            _stop.Click += (s, e) => _svc.Stop();
            _lines.Click += (s, e) => ShowBreakableLines();
            SetRunning(false);
        }

        private void BuildLayout()
        {
            var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, Padding = new Padding(6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _browse.Text = "..."; _browse.Width = 30;
            _start.Text = "Start"; _stop.Text = "Stop";
            _lines.Text = "Lines"; _lines.Width = 50;
            _once.Text = "Stop after first hit"; _once.Checked = true; _once.AutoSize = true;

            top.Controls.Add(new Label { Text = "Target EXE", Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
            _exe.Dock = DockStyle.Fill;
            top.Controls.Add(_exe, 1, 0);
            top.Controls.Add(_browse, 2, 0);
            top.SetColumnSpan(_exe, 1);

            top.Controls.Add(new Label { Text = "Module", Anchor = AnchorStyles.Left, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, 1);
            _module.Dock = DockStyle.Fill;
            top.Controls.Add(_module, 1, 1);

            var lineRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            lineRow.Controls.Add(new Label { Text = "Line", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            _line.Width = 60;
            lineRow.Controls.Add(_line);
            lineRow.Controls.Add(_once);
            lineRow.Controls.Add(_lines);
            lineRow.Controls.Add(_start);
            lineRow.Controls.Add(_stop);
            top.Controls.Add(lineRow, 1, 2);

            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.Font = new Font(FontFamily.GenericMonospace, 9f);
            _log.BackColor = Color.FromArgb(30, 30, 30);
            _log.ForeColor = Color.Gainsboro;

            Controls.Add(_log);
            Controls.Add(top);
        }

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
                int line;
                if (!int.TryParse(_line.Text.Trim(), out line) || line <= 0)
                {
                    MessageBox.Show(this, "Enter a positive source line number.", "CA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string module = _module.Text.Trim();
                if (string.IsNullOrEmpty(module))
                {
                    MessageBox.Show(this, "Enter the module name (e.g. clbrws011.clw).", "CA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _log.Clear();
                AppendLog("starting: " + Path.GetFileName(_exe.Text) + "  break " + module + ":" + line, Color.SkyBlue);
                SetRunning(true);
                _svc.Start(_exe.Text.Trim(), module, line, _once.Checked);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR: " + ex.Message, Color.Salmon);
                SetRunning(false);
            }
        }

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

        private void OnHit(DebugHit hit)
        {
            UI(() =>
            {
                string where = hit.Resolved
                    ? hit.Module + " line " + hit.Line + (hit.Exact ? " (exact)" : " (+0x" + hit.Gap.ToString("X") + ")")
                    : "(unresolved address)";
                AppendLog("*** HIT  " + hit.Va + "  ->  " + where, Color.Lime);

                if (!string.IsNullOrEmpty(hit.ResolvedPath))
                {
                    AppendLog("   source: " + hit.ResolvedPath, Color.DarkGray);
                    TryJump(hit.ResolvedPath, hit.Line);
                }
                else if (hit.Resolved)
                {
                    AppendLog("   (could not resolve " + hit.Module + " via the active .red — open the app's solution to enable the jump)", Color.Goldenrod);
                }
            });
        }

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

        private void SetRunning(bool running)
        {
            _start.Enabled = !running;
            _stop.Enabled = running;
            _exe.Enabled = _module.Enabled = _line.Enabled = _browse.Enabled = _once.Enabled = _lines.Enabled = !running;
        }

        private void AppendLog(string text, Color color)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = color;
            _log.AppendText(text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void UI(Action a)
        {
            if (IsHandleCreated && InvokeRequired) BeginInvoke(a);
            else a();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _svc.Stop(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
