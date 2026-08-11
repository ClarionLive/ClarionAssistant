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
        private string _lastProjectMarkedActive;

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
                Padding = new Padding(10, 0, 10, 0)
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
            // they stopped is worse than an honest prompt.
            FormClosing += (s, e) =>
            {
                if (_finished) return;
                var answer = MessageBox.Show(this,
                    "The index is still running. Cancel it?",
                    "CodeGraph Index", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    RaiseCancel();
                    e.Cancel = true; // stay visible until the run acknowledges the cancel
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

            if (ev.Phase == IndexProgressEvent.PhaseParsing)
            {
                _phaseLabel.Text = "Parsing symbols...";
                if (ev.FilesTotal > 0)
                    _fraction = ParseWeight * ev.FilesDone / ev.FilesTotal;
                if (!string.IsNullOrEmpty(ev.ProjectName))
                    MarkProject(ev.ProjectName, ev.SymbolCount);
            }
            else if (ev.Phase == IndexProgressEvent.PhaseResolving)
            {
                _phaseLabel.Text = "Resolving relationships...";
                MarkAllProjectsParsed();
                if (ev.FilesTotal > 0)
                    _fraction = ParseWeight + ResolveWeight * ev.FilesDone / ev.FilesTotal;
            }
            else if (ev.Phase == IndexProgressEvent.PhaseFinishing)
            {
                _phaseLabel.Text = "Finishing (inheritance, type usage)...";
                _fraction = ParseWeight + ResolveWeight;
            }

            if (!string.IsNullOrEmpty(ev.CurrentFile))
                _fileLabel.Text = ev.CurrentFile;

            int barValue = (int)(_fraction * 1000);
            if (barValue > _bar.Maximum) barValue = _bar.Maximum;
            _bar.Value = barValue;
            UpdateStats();
        }

        public void RunCompleted(IndexResult result)
        {
            _finished = true;
            _clockTimer.Stop();
            _bar.Value = _bar.Maximum;
            MarkAllProjectsParsed();
            _phaseLabel.Text = "Index complete";
            _phaseLabel.ForeColor = Color.FromArgb(0, 128, 0);
            _fileLabel.Text = "";

            _summaryText = string.Format(
                "CodeGraph index complete: {0} projects, {1} files, {2:n0} symbols, {3:n0} relationships in {4}.",
                result.ProjectCount, result.FileCount, result.SymbolCount, result.RelationshipCount,
                FormatSpan(TimeSpan.FromMilliseconds(result.DurationMs)));
            _statsLabel.Text = _summaryText;
            _copyButton.Enabled = true;
            MorphToClose();
        }

        public void RunCancelled(string dbDisposition)
        {
            _finished = true;
            _clockTimer.Stop();
            _phaseLabel.Text = "Cancelled — index incomplete";
            _phaseLabel.ForeColor = Color.FromArgb(176, 32, 32);
            _fileLabel.Text = dbDisposition;
            _summaryText = "Index cancelled after " + FormatSpan(_elapsed.Elapsed) + ". " + dbDisposition;
            _statsLabel.Text = _summaryText;
            _copyButton.Enabled = true;
            MorphToClose();
        }

        public void RunFailed(string message)
        {
            _finished = true;
            _clockTimer.Stop();
            _phaseLabel.Text = "Index failed";
            _phaseLabel.ForeColor = Color.FromArgb(176, 32, 32);
            _fileLabel.Text = message;
            _summaryText = "Index failed after " + FormatSpan(_elapsed.Elapsed) + ": " + message;
            _statsLabel.Text = _summaryText;
            _copyButton.Enabled = true;
            MorphToClose();
        }

        /// <summary>Cancel acknowledged but run still unwinding — reflect the click
        /// immediately so it never feels ignored.</summary>
        public void CancelPending()
        {
            if (_finished) return;
            _phaseLabel.Text = "Cancelling...";
            _actionButton.Enabled = false;
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
            foreach (var item in _projectItems.Values)
            {
                if (item.SubItems[1].Text != "Done")
                {
                    item.SubItems[1].Text = "Done";
                    item.ForeColor = SystemColors.ControlText;
                }
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
            if (_finished) { Close(); return; }
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
