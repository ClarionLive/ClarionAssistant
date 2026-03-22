using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ClarionAssistant.Models;
using ClarionAssistant.Services;
using ClarionAssistant.TaskLifecycleBoard;

namespace ClarionAssistant
{
    /// <summary>
    /// WinForms control showing connected MultiTerminal agents, their active tasks,
    /// and actions like opening the lifecycle board.
    /// </summary>
    public class AgentPanelControl : UserControl
    {
        private MultiTerminalApiClient _api;
        private ListView _agentList;
        private Button _refreshButton;
        private Button _openBoardButton;
        private Label _statusLabel;
        private Timer _pollTimer;
        private List<AgentEntry> _agents = new List<AgentEntry>();
        private bool _refreshing;

        private class AgentEntry
        {
            public string Name;
            public string TaskId;
            public string TaskTitle;
            public string TaskStatus;
        }

        public AgentPanelControl()
        {
            _api = new MultiTerminalApiClient();
            InitializeUI();
            StartPolling();
        }

        private void InitializeUI()
        {
            // Status label at top
            _statusLabel = new Label
            {
                Text = "Connecting to MultiTerminal...",
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(4, 4, 0, 0),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.Gray
            };

            // Toolbar panel
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 30,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2)
            };

            _refreshButton = new Button
            {
                Text = "Refresh",
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            _refreshButton.Click += (s, e) => RefreshData();

            _openBoardButton = new Button
            {
                Text = "Open Board",
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                Enabled = false
            };
            _openBoardButton.Click += OnOpenBoardClick;

            toolbar.Controls.Add(_refreshButton);
            toolbar.Controls.Add(_openBoardButton);

            // Agent list
            _agentList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 9f),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _agentList.Columns.Add("Agent", 120);
            _agentList.Columns.Add("Active Task", 280);
            _agentList.Columns.Add("Status", 80);
            _agentList.SelectedIndexChanged += OnAgentSelectionChanged;
            _agentList.DoubleClick += OnAgentDoubleClick;

            Controls.Add(_agentList);
            Controls.Add(toolbar);
            Controls.Add(_statusLabel);
        }

        private void StartPolling()
        {
            _pollTimer = new Timer { Interval = 8000 };
            _pollTimer.Tick += (s, e) => RefreshData();
            _pollTimer.Start();

            // Initial load
            RefreshData();
        }

        private void RefreshData()
        {
            if (!Visible || _refreshing) return;
            _refreshing = true;

            // Run HTTP calls on background thread
            System.Threading.Tasks.Task.Run(() =>
            {
                var terminalsResult = _api.ListTerminals();
                var tasksResult = _api.ListTasks("in_progress");
                return new { terminalsResult, tasksResult };
            }).ContinueWith(t =>
            {
                _refreshing = false;
                if (IsDisposed || t.IsFaulted) return;

                var terminalsResult = t.Result.terminalsResult;
                var tasksResult = t.Result.tasksResult;

                if (!terminalsResult.Success)
                {
                    _statusLabel.Text = "MultiTerminal not available";
                    _statusLabel.ForeColor = Color.IndianRed;
                    if (_pollTimer.Interval < 30000) _pollTimer.Interval = 15000;
                    return;
                }

                if (_pollTimer.Interval != 8000) _pollTimer.Interval = 8000;
                _statusLabel.ForeColor = Color.Green;

                _agents.Clear();
                var terminals = terminalsResult.Data ?? new List<TerminalInfo>();
                var tasks = tasksResult.Success ? tasksResult.Data : new List<KanbanTaskSummary>();

                var taskByAssignee = new Dictionary<string, KanbanTaskSummary>(StringComparer.OrdinalIgnoreCase);
                if (tasks != null)
                {
                    foreach (var task in tasks)
                    {
                        if (!string.IsNullOrEmpty(task.Assignee) && !taskByAssignee.ContainsKey(task.Assignee))
                            taskByAssignee[task.Assignee] = task;
                    }
                }

                foreach (var terminal in terminals)
                {
                    var entry = new AgentEntry { Name = terminal.Name ?? terminal.Id };
                    KanbanTaskSummary task;
                    if (taskByAssignee.TryGetValue(entry.Name, out task))
                    {
                        entry.TaskId = task.Id;
                        entry.TaskTitle = task.Title;
                        entry.TaskStatus = task.Status;
                    }
                    _agents.Add(entry);
                }

                _statusLabel.Text = string.Format("{0} agent(s) connected", terminals.Count);

                _agentList.BeginUpdate();
                _agentList.Items.Clear();
                foreach (var agent in _agents)
                {
                    var item = new ListViewItem(agent.Name);
                    item.SubItems.Add(agent.TaskTitle ?? "(idle)");
                    item.SubItems.Add(agent.TaskStatus ?? "");
                    if (agent.TaskId != null)
                        item.ForeColor = Color.Black;
                    else
                        item.ForeColor = Color.Gray;
                    _agentList.Items.Add(item);
                }
                _agentList.EndUpdate();

                OnAgentSelectionChanged(null, null);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnAgentSelectionChanged(object sender, EventArgs e)
        {
            var selected = GetSelectedAgent();
            _openBoardButton.Enabled = selected != null && selected.TaskId != null;
        }

        private void OnAgentDoubleClick(object sender, EventArgs e)
        {
            var selected = GetSelectedAgent();
            if (selected != null && selected.TaskId != null)
                OpenBoardForAgent(selected);
        }

        private void OnOpenBoardClick(object sender, EventArgs e)
        {
            var selected = GetSelectedAgent();
            if (selected != null && selected.TaskId != null)
                OpenBoardForAgent(selected);
        }

        private void OpenBoardForAgent(AgentEntry agent)
        {
            TaskLifecycleBoardForm.OpenForTask(agent.TaskId, _api, IsDarkTheme());
        }

        private AgentEntry GetSelectedAgent()
        {
            if (_agentList.SelectedIndices.Count == 0) return null;
            int idx = _agentList.SelectedIndices[0];
            return idx >= 0 && idx < _agents.Count ? _agents[idx] : null;
        }

        private bool IsDarkTheme()
        {
            return BackColor.GetBrightness() < 0.5f;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_pollTimer != null)
                {
                    _pollTimer.Stop();
                    _pollTimer.Dispose();
                    _pollTimer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
