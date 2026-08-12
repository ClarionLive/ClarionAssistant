using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ClarionCodeGraph.Graph;

namespace ClarionAssistant.Dialogs
{
    /// <summary>
    /// Modeless progress window for a CodeGraph index run (ticket 0d788f8b). Design agreed
    /// with the owner: never modal, single action button that morphs Cancel → Close, the
    /// complete state doubles as the index REPORT (totals + warnings + Open log / Copy
    /// summary), and a cancelled run is visually distinct — a partial database must never
    /// read as a finished index.
    ///
    /// Threading contract: every public member below must be called on the UI thread — the
    /// host marshals (BackgroundWorker.ReportProgress) before calling in. Native WinForms on
    /// purpose: a utility surface with no WebView2 startup cost, and Clipboard.SetText works
    /// here without the file:// clipboard.writeText failure a WebView2 page would hit.
    /// </summary>
    public sealed class IndexProgressForm : Form
    {
        // Phase weighting for the overall bar: symbol parsing is the fast fraction of
        // wall-clock, relationship resolution dominates. A uniform files/total would sprint
        // to a high % during parsing, then crawl — worse than no bar at all.
        private const double ParseWeight = 0.25;
        private const double ResolveWeight = 0.73;   // finishing tail gets the last 2%

        private readonly Label _phaseLabel;
        private readonly Label _fileLabel;
        private readonly Label _statsLabel;
        private readonly ProgressBar _bar;
        private readonly ListView _projectList;
        private readonly Button _actionButton;
        private readonly Button _openLogButton;
        private readonly Button _copyButton;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly Timer _clockTimer;
        private readonly Dictionary<string, ListViewItem> _projectItems =
            new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);

        private readonly string _logPath;
        private readonly long _lastRunMs;      // previous run's duration (ETA seed), 0 = unknown
        private double _fraction;              // 0..1 weighted progress
        private string _summaryText = "";
        private bool _finished;
        private bool _cancelRequested;
        private string _lastProjectMarkedActive;
        private string _lastPhaseSeen;
        private int _lastUiRefreshTick;
        private bool _projectsFinalized;
        // Latest per-project symbol delta, stashed BEFORE the UI throttle can drop the event —
        // a project's final event is throttleable (the always-refresh escape uses solution-wide
        // counters), and no later event carries that project's number again. Without the stash
        // the row freezes at whatever refresh happened to land (run-2 debugger finding: the
        // same wrong-number defect the delta fix removed, reintroduced by the throttle).
        private readonly Dictionary<string, int> _pendingProjectSymbols =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised on the UI thread when the user clicks Cancel. The host flips the
        /// cooperative flag the indexer polls; this form only changes its own label.</summary>
        public event Action CancelClicked;

        public IndexProgressForm(string solutionName, string logPath, long lastRunMs)
        {
            _logPath = logPath;
            _lastRunMs = lastRunMs;

            Text = "CodeGraph Index — " + solutionName;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            MaximizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(480, 340);
            Font = new Font("Segoe UI", 9f);

            _phaseLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Text = "Starting...",
                Padding = new Padding(10, 6, 10, 0)
            };
            _fileLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = SystemColors.GrayText,
                Text = "",
                Padding = new Padding(10, 2, 10, 0),
                AutoEllipsis = true
            };
            _bar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 18,
                Minimum = 0,
                Maximum = 1000,
                Style = ProgressBarStyle.Continuous
            };
            var barHost = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(10, 4, 10, 4) };
            _bar.Dock = DockStyle.Fill;
            barHost.Controls.Add(_bar);
            _statsLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = "Elapsed 0:00",
                Padding = new Padding(10, 0, 10, 0),
                AutoEllipsis = true // the completion summary outgrows a 560px window
            };

            _projectList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                UseCompatibleStateImageBehavior = false
            };
            _projectList.Columns.Add("Project", 260);
            _projectList.Columns.Add("State", 110);
            _projectList.Columns.Add("Symbols", 90, HorizontalAlignment.Right);
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
            listHost.Controls.Add(_projectList);

            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 40,
                Padding = new Padding(6)
            };
            _actionButton = new Button { Text = "Cancel", Width = 90, Height = 27 };
            _openLogButton = new Button { Text = "Open Log", Width = 90, Height = 27 };
            _copyButton = new Button { Text = "Copy Summary", Width = 110, Height = 27, Enabled = false };
            _actionButton.Click += OnActionClick;
            _openLogButton.Click += OnOpenLogClick;
            _copyButton.Click += OnCopyClick;
            buttonRow.Controls.Add(_actionButton);
            buttonRow.Controls.Add(_openLogButton);
            buttonRow.Controls.Add(_copyButton);

            Controls.Add(listHost);
            Controls.Add(buttonRow);
            Controls.Add(_statsLabel);
            Controls.Add(barHost);
            Controls.Add(_fileLabel);
            Controls.Add(_phaseLabel);

            // Closing the window while running means Cancel — a hidden run the dev believes
            // they stopped is worse than an honest prompt. But ONLY a user-initiated close
            // may prompt: an IDE or Windows shutdown must never block on a modal (this
            // machine has real shutdown-hang history) — request cancel and let it close.
            FormClosing += (s, e) =>
            {
                if (_finished || _cancelRequested) return; // cancel already in flight — free to go
                if (e.CloseReason != CloseReason.UserClosing)
                {
                    RaiseCancel();
                    return;
                }
                var answer = MessageBox.Show(this,
                    "The index is still running. Cancel it?",
                    "CodeGraph Index", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    RaiseCancel();
                    // Window stays up showing "Cancelling..." — but is closable from now on.
                    e.Cancel = true;
                }
                else
                {
                    e.Cancel = true;
                }
            };

            _clockTimer = new Timer { Interval = 1000 };
            _clockTimer.Tick += (s, e) => UpdateStats();
            _clockTimer.Start();
        }

        /// <summary>Seed the project list before the run starts (full inventory is known up
        /// front). Projects appear as Pending until their files start parsing.</summary>
        public void SetProjects(IEnumerable<string> projectNames)
        {
            _projectList.BeginUpdate();
            try
            {
                foreach (string name in projectNames)
                {
                    if (_projectItems.ContainsKey(name)) continue;
                    var item = new ListViewItem(new[] { name, "Pending", "" });
                    item.ForeColor = SystemColors.GrayText;
                    _projectItems[name] = item;
                    _projectList.Items.Add(item);
                }
            }
            finally { _projectList.EndUpdate(); }
        }

        public void OnEvent(IndexProgressEvent ev)
        {
            if (_finished || ev == null) return;

            string phaseText = null;
            if (ev.Phase == IndexProgressEvent.PhaseParsing)
            {
                phaseText = "Parsing symbols...";
                if (ev.FilesTotal > 0)
                    _fraction = ParseWeight * ev.FilesDone / ev.FilesTotal;
            }
            else if (ev.Phase == IndexProgressEvent.PhaseResolving)
            {
                phaseText = "Resolving relationships...";
                if (ev.FilesTotal > 0)
                    _fraction = ParseWeight + ResolveWeight * ev.FilesDone / ev.FilesTotal;
            }
            else if (ev.Phase == IndexProgressEvent.PhaseFinishing)
            {
                phaseText = "Finishing (inheritance, type usage)...";
                _fraction = ParseWeight + ResolveWeight;
            }

            // Stash the per-project symbol delta BEFORE the throttle can drop this event —
            // MarkProject and MarkAllProjectsParsed write rows from the stash, so a dropped
            // final event can't freeze a project's number short.
            if (ev.Phase == IndexProgressEvent.PhaseParsing && !string.IsNullOrEmpty(ev.ProjectName))
                _pendingProjectSymbols[ev.ProjectName] = ev.SymbolCount;

            // UI writes are gated to ~6/sec (review finding: the parse pass can emit tens of
            // events per second on solutions of many small files). Phase transitions,
            // phase-completion events, and PROJECT transitions always refresh so nothing
            // important is skipped — intermediate updates catch up on the next refresh.
            bool phaseChanged = !string.Equals(_lastPhaseSeen, ev.Phase, StringComparison.Ordinal);
            _lastPhaseSeen = ev.Phase;
            bool projectChanged = ev.Phase == IndexProgressEvent.PhaseParsing &&
                !string.IsNullOrEmpty(ev.ProjectName) &&
                !string.Equals(ev.ProjectName, _lastProjectMarkedActive, StringComparison.OrdinalIgnoreCase);
            int tick = Environment.TickCount;
            int sinceLast = tick - _lastUiRefreshTick; // wraps negative after 24.9 days — treat as due
            if (!phaseChanged && !projectChanged && ev.FilesDone < ev.FilesTotal && sinceLast >= 0 && sinceLast < 150)
                return;
            _lastUiRefreshTick = tick;

            if (phaseText != null && !_cancelRequested)
                _phaseLabel.Text = phaseText;
            if (ev.Phase == IndexProgressEvent.PhaseParsing && !string.IsNullOrEmpty(ev.ProjectName))
                MarkProject(ev.ProjectName, ev.SymbolCount);
            else if (ev.Phase != IndexProgressEvent.PhaseParsing)
                MarkAllProjectsParsed();

            if (!string.IsNullOrEmpty(ev.CurrentFile))
                _fileLabel.Text = ev.CurrentFile;

            int barValue = (int)(_fraction * 1000);
            if (barValue > _bar.Maximum) barValue = _bar.Maximum;
            _bar.Value = barValue;
            UpdateStats();
        }

        public void RunCompleted(IndexResult result, bool incremental)
        {
            _bar.Value = _bar.Maximum;
            MarkAllProjectsParsed();
            // Symbol/relationship counts describe what THIS RUN parsed, not the whole index —
            // an up-to-date incremental parses nothing and must not read as an empty database
            // (debugger finding: "27 projects, 2,780 files, 0 symbols" over a 130k-symbol db).
            // "Up to date" requires EVIDENCE of one: an incremental run that actually resolved
            // files. A FULL index yielding 0/0 (broken .red, missing .cwprojs) just cleared the
            // database and indexed nothing — reporting it as up to date would be the same lie
            // inverted (run-2 debugger finding), so it falls through to the honest wording.
            string summary = (incremental && result.FileCount > 0 &&
                              result.SymbolCount == 0 && result.RelationshipCount == 0)
                ? string.Format(
                    "Index already up to date — no source changes to parse ({0} projects, {1} files checked in {2}).",
                    result.ProjectCount, result.FileCount,
                    FormatSpan(TimeSpan.FromMilliseconds(result.DurationMs)))
                : string.Format(
                    "CodeGraph index complete: parsed {2:n0} symbols and {3:n0} relationships this run ({0} projects, {1} files) in {4}.",
                    result.ProjectCount, result.FileCount, result.SymbolCount, result.RelationshipCount,
                    FormatSpan(TimeSpan.FromMilliseconds(result.DurationMs)));
            EnterTerminalState("Index complete", Color.FromArgb(0, 128, 0), "", summary);
        }

        public void RunCancelled(string dbDisposition)
        {
            EnterTerminalState("Cancelled — index incomplete", Color.FromArgb(176, 32, 32),
                dbDisposition,
                "Index cancelled after " + FormatSpan(_elapsed.Elapsed) + ". " + dbDisposition);
        }

        public void RunFailed(string message)
        {
            EnterTerminalState("Index failed", Color.FromArgb(176, 32, 32),
                message,
                "Index failed after " + FormatSpan(_elapsed.Elapsed) + ": " + message);
        }

        private void EnterTerminalState(string headline, Color headlineColor, string detail, string summary)
        {
            _finished = true;
            _clockTimer.Stop();
            _phaseLabel.Text = headline;
            _phaseLabel.ForeColor = headlineColor;
            _fileLabel.Text = detail;
            _summaryText = summary;
            _statsLabel.Text = summary;
            _copyButton.Enabled = true;
            MorphToClose();
        }

        /// <summary>Cancel acknowledged but run still unwinding — reflect the click
        /// immediately, and let the window CLOSE from here on: the indexer polls at file
        /// boundaries and some stretches run minutes between polls; holding the window
        /// hostage until the poll lands punishes the user for cancelling (debugger finding).
        /// The host guards its callbacks with IsDisposed, so closing early is safe.</summary>
        public void CancelPending()
        {
            if (_finished) return;
            _cancelRequested = true;
            _phaseLabel.Text = "Cancelling...";
            _actionButton.Text = "Close";
        }

        private void MarkProject(string projectName, int symbolCount)
        {
            ListViewItem item;
            if (!_projectItems.TryGetValue(projectName, out item))
            {
                item = new ListViewItem(new[] { projectName, "", "" });
                _projectItems[projectName] = item;
                _projectList.Items.Add(item);
            }

            if (!string.Equals(_lastProjectMarkedActive, projectName, StringComparison.OrdinalIgnoreCase))
            {
                ListViewItem prev;
                if (_lastProjectMarkedActive != null && _projectItems.TryGetValue(_lastProjectMarkedActive, out prev))
                {
                    prev.SubItems[1].Text = "Done";
                    prev.ForeColor = SystemColors.ControlText;
                    // Final count from the stash — the previous project's LAST event may have
                    // been dropped by the throttle.
                    int prevFinal;
                    if (_pendingProjectSymbols.TryGetValue(_lastProjectMarkedActive, out prevFinal))
                        prev.SubItems[2].Text = prevFinal.ToString("n0");
                }
                _lastProjectMarkedActive = projectName;
                item.SubItems[1].Text = "Indexing...";
                item.ForeColor = SystemColors.HotTrack;
                item.EnsureVisible();
            }
            item.SubItems[2].Text = symbolCount.ToString("n0");
        }

        private void MarkAllProjectsParsed()
        {
            if (_projectsFinalized) return; // called per resolve-phase event otherwise
            _projectsFinalized = true;
            foreach (var entry in _projectItems)
            {
                var item = entry.Value;
                if (item.SubItems[1].Text != "Done")
                {
                    item.SubItems[1].Text = "Done";
                    item.ForeColor = SystemColors.ControlText;
                }
                // Flush every stashed final count — throttle-dropped last events land here.
                int finalCount;
                if (_pendingProjectSymbols.TryGetValue(entry.Key, out finalCount))
                    item.SubItems[2].Text = finalCount.ToString("n0");
            }
            _lastProjectMarkedActive = null;
        }

        private void UpdateStats()
        {
            if (_finished) return;
            string text = "Elapsed " + FormatSpan(_elapsed.Elapsed);

            // ETA: seed from the previous run's persisted duration until live progress is
            // meaningful (3%), then extrapolate from measured throughput.
            if (_fraction > 0.03)
            {
                var remaining = TimeSpan.FromMilliseconds(
                    _elapsed.Elapsed.TotalMilliseconds * (1 - _fraction) / _fraction);
                text += string.Format("   ·   {0:0}%   ·   about {1} remaining",
                    _fraction * 100, FormatSpan(remaining));
            }
            else if (_lastRunMs > 0)
            {
                text += string.Format("   ·   last full index took {0}",
                    FormatSpan(TimeSpan.FromMilliseconds(_lastRunMs)));
            }
            _statsLabel.Text = text;
        }

        private void MorphToClose()
        {
            _actionButton.Text = "Close";
            _actionButton.Enabled = true;
        }

        private void OnActionClick(object sender, EventArgs e)
        {
            if (_finished || _cancelRequested) { Close(); return; }
            RaiseCancel();
        }

        private void RaiseCancel()
        {
            CancelPending();
            var handler = CancelClicked;
            if (handler != null) handler();
        }

        private void OnOpenLogClick(object sender, EventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_logPath) && System.IO.File.Exists(_logPath))
                    Process.Start(_logPath);
                else
                    MessageBox.Show(this, "No log file was written for this run.", "CodeGraph Index",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the log: " + ex.Message, "CodeGraph Index",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnCopyClick(object sender, EventArgs e)
        {
            try { Clipboard.SetText(string.IsNullOrEmpty(_summaryText) ? Text : _summaryText); }
            catch { /* clipboard can be transiently locked by another process; non-fatal */ }
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalHours >= 1)
                return string.Format("{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds);
            return string.Format("{0}:{1:00}", (int)span.TotalMinutes, span.Seconds);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _clockTimer != null) _clockTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
