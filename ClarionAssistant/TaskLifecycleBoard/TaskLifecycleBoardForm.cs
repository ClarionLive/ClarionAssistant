using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ClarionAssistant.Models;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant.TaskLifecycleBoard
{
    /// <summary>
    /// Floating window that displays a mini kanban board for a single task's lifecycle.
    /// Adapted from MultiTerminal's TaskLifecycleBoardForm to use REST API instead of MessageBroker.
    /// </summary>
    public class TaskLifecycleBoardForm : Form
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private readonly MultiTerminalApiClient _api;
        private readonly string _taskId;
        private TaskDetail _taskDetail;
        private readonly SettingsService _settings;
        private readonly JavaScriptSerializer _json;
        private Timer _pollTimer;
        private string _lastTaskHash;
        private int _errorCount;
        private List<string> _cachedTerminalNames = new List<string>();
        private List<object> _cachedMembers = new List<object>();

        private static readonly Dictionary<string, TaskLifecycleBoardForm> _openWindows =
            new Dictionary<string, TaskLifecycleBoardForm>();

        /// <summary>
        /// Opens or focuses the lifecycle board for a given task.
        /// </summary>
        public static void OpenForTask(string taskId, MultiTerminalApiClient api, bool isDarkTheme)
        {
            if (_openWindows.TryGetValue(taskId, out var existing) && !existing.IsDisposed)
            {
                existing.BringToFront();
                existing.Focus();
                return;
            }

            var form = new TaskLifecycleBoardForm(taskId, api, isDarkTheme);
            _openWindows[taskId] = form;
            form.Show();
        }

        public TaskLifecycleBoardForm(string taskId, MultiTerminalApiClient api, bool isDarkTheme)
        {
            _taskId = taskId ?? throw new ArgumentNullException("taskId");
            _api = api ?? throw new ArgumentNullException("api");
            _json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
            _settings = new SettingsService();

            InitializeComponent(isDarkTheme);
            RestoreWindowBounds();
            _ = InitializeAsync();
        }

        private void RestoreWindowBounds()
        {
            string boundsStr = _settings.Get("LifecycleBoardBounds");
            if (!string.IsNullOrEmpty(boundsStr))
            {
                var parts = boundsStr.Split(',');
                if (parts.Length == 4)
                {
                    int x, y, w, h;
                    if (int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) &&
                        int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h))
                    {
                        var rect = new Rectangle(x, y, w, h);
                        foreach (var screen in Screen.AllScreens)
                        {
                            if (screen.WorkingArea.IntersectsWith(rect))
                            {
                                StartPosition = FormStartPosition.Manual;
                                Left = x; Top = y; Width = w; Height = h;
                                return;
                            }
                        }
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            // Fetch initial task data off the UI thread
            var result = await System.Threading.Tasks.Task.Run(() => _api.GetTaskDetail(_taskId));
            if (result.Success && result.Data != null)
            {
                _taskDetail = result.Data;
                Text = "Lifecycle Board - " + (result.Data.Task != null ? result.Data.Task.Title : _taskId);
            }

            RefreshCachedContext();
            await InitializeWebView2Async();
            StartPolling();
        }

        private void InitializeComponent(bool isDarkTheme)
        {
            string title = _taskId;
            Text = "Lifecycle Board - " + title;
            Size = new Size(1100, 700);
            MinimumSize = new Size(700, 500);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            Font = new Font("Segoe UI", 9f);
            BackColor = isDarkTheme ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            _webView.WebMessageReceived += OnWebMessageReceived;
            Controls.Add(_webView);
        }

        private void StartPolling()
        {
            _pollTimer = new Timer { Interval = 5000 };
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();
        }

        private bool _polling;

        private void OnPollTick(object sender, EventArgs e)
        {
            if (IsDisposed || !_isInitialized || _polling) return;
            if (WindowState == FormWindowState.Minimized) return;

            _polling = true;
            System.Threading.Tasks.Task.Run(() => _api.GetTaskDetail(_taskId))
                .ContinueWith(t =>
                {
                    _polling = false;
                    if (IsDisposed || t.IsFaulted) return;

                    if (t.Result.Success && t.Result.Data != null)
                    {
                        _errorCount = 0;
                        if (_pollTimer.Interval != 5000) _pollTimer.Interval = 5000;

                        string hash = ComputeTaskHash(t.Result.Data);
                        if (hash != _lastTaskHash)
                        {
                            _lastTaskHash = hash;
                            _taskDetail = t.Result.Data;
                            SendTaskToWebView();
                        }
                    }
                    else
                    {
                        _errorCount++;
                        if (_errorCount >= 3 && _pollTimer.Interval < 30000)
                            _pollTimer.Interval = 15000;
                        if (_errorCount >= 10 && _pollTimer.Interval < 60000)
                            _pollTimer.Interval = 30000;
                    }
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private string ComputeTaskHash(TaskDetail detail)
        {
            if (detail == null || detail.Task == null) return "";
            // Lightweight change detection: status + checklist summary + continuation notes
            var sb = new StringBuilder();
            sb.Append(detail.Task.Status).Append('|');
            sb.Append(detail.Task.Title).Append('|');
            if (detail.ChecklistSummary != null)
            {
                sb.Append(detail.ChecklistSummary.Done).Append(',');
                sb.Append(detail.ChecklistSummary.Coding).Append(',');
                sb.Append(detail.ChecklistSummary.Testing).Append(',');
                sb.Append(detail.ChecklistSummary.Pending).Append('|');
            }
            sb.Append(detail.ContinuationNotes ?? "").Append('|');
            sb.Append(detail.Plan ?? "").Append('|');
            sb.Append(detail.ImplementationSummary ?? "").Append('|');
            sb.Append(detail.TestResults ?? "").Append('|');
            if (detail.Checklist != null)
            {
                foreach (var ci in detail.Checklist)
                {
                    sb.Append(ci.Item).Append(':').Append(ci.Status).Append(':');
                    sb.Append(ci.AssignedTo ?? "").Append(':');
                    sb.Append(ci.CycleCount).Append(':');
                    sb.Append(ci.Notes != null ? ci.Notes.Count : 0).Append(';');
                }
            }
            return sb.ToString();
        }

        private async System.Threading.Tasks.Task InitializeWebView2Async()
        {
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lifecycle board WebView2 init failed: " + ex.Message);
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;

            _isInitialized = true;

            string htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "TaskLifecycleBoard", "lifecycle-board.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "lifecycle-board.html");
            if (File.Exists(path)) return path;

            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "TaskLifecycleBoard", "lifecycle-board.html");
                if (File.Exists(path)) return path;
            }

            return Path.Combine(assemblyDir, "TaskLifecycleBoard", "lifecycle-board.html");
        }

        // ── Send data to WebView ────────────────────────────

        /// <summary>Refresh cached terminal and member lists on a background thread.</summary>
        private void RefreshCachedContext()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var terminalNames = new List<string>();
                var allMembers = new List<object>();
                var memberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var terminalsResult = _api.ListTerminals();
                if (terminalsResult.Success && terminalsResult.Data != null)
                {
                    foreach (var t in terminalsResult.Data)
                        if (t.Name != null) terminalNames.Add(t.Name);
                }

                var profilesResult = _api.GetTeamProfiles();
                if (profilesResult.Success && profilesResult.Data != null)
                {
                    foreach (var p in profilesResult.Data)
                    {
                        string name = p.Name ?? "";
                        if (string.IsNullOrEmpty(name)) continue;
                        memberNames.Add(name);
                        allMembers.Add(new Dictionary<string, object> { { "name", name }, { "isOnline", p.IsOnline } });
                    }
                }
                foreach (var name in terminalNames)
                {
                    if (!memberNames.Contains(name))
                        allMembers.Add(new Dictionary<string, object> { { "name", name }, { "isOnline", true } });
                }

                if (!IsDisposed)
                {
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            _cachedTerminalNames = terminalNames;
                            _cachedMembers = allMembers;
                        }));
                    }
                    catch (ObjectDisposedException) { }
                }
            });
        }

        private static List<Dictionary<string, object>> BuildChecklistForApi(List<ChecklistItem> checklist)
        {
            var items = new List<Dictionary<string, object>>();
            if (checklist == null) return items;
            foreach (var ci in checklist)
            {
                var notes = new List<object>();
                if (ci.Notes != null)
                {
                    foreach (var n in ci.Notes)
                    {
                        notes.Add(new Dictionary<string, object>
                        {
                            { "By", n.By }, { "At", n.At },
                            { "Transition", n.Transition }, { "Text", n.Text }
                        });
                    }
                }
                items.Add(new Dictionary<string, object>
                {
                    { "item", ci.Item ?? "" },
                    { "status", ci.Status ?? "pending" },
                    { "done", ci.Done },
                    { "notes", notes },
                    { "assignedTo", ci.AssignedTo },
                    { "cycleCount", ci.CycleCount }
                });
            }
            return items;
        }

        private void SendTaskToWebView()
        {
            if (!_isInitialized || _taskDetail == null || _taskDetail.Task == null) return;

            var task = _taskDetail.Task;
            string checklistJson = _json.Serialize(BuildChecklistForApi(_taskDetail.Checklist));

            var taskData = new Dictionary<string, object>
            {
                { "id", task.Id },
                { "title", task.Title },
                { "description", task.Description },
                { "status", task.Status },
                { "assignee", task.Assignee },
                { "priority", task.Priority ?? "normal" },
                { "helpers", task.Helpers ?? new List<string>() },
                { "checklistJson", checklistJson },
                { "plan", _taskDetail.Plan },
                { "implementationSummary", _taskDetail.ImplementationSummary },
                { "testResults", _taskDetail.TestResults },
                { "continuationNotes", _taskDetail.ContinuationNotes },
                { "projectId", task.ProjectId }
            };

            var message = new Dictionary<string, object>
            {
                { "type", "task_data" },
                { "task", taskData },
                { "terminals", _cachedTerminalNames },
                { "members", _cachedMembers },
                { "projects", new List<object>() },
                { "attachments", new List<object>() }
            };

            PostMessage(_json.Serialize(message));
        }

        // ── Handle messages from WebView ────────────────────

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                var raw = _json.DeserializeObject(json) as Dictionary<string, object>;
                if (raw == null) return;

                object typeObj;
                if (!raw.TryGetValue("type", out typeObj)) return;
                string messageType = typeObj as string;
                if (messageType == null) return;

                switch (messageType)
                {
                    case "ready":
                        SendTaskToWebView();
                        break;

                    case "update_card_status":
                        HandleCardStatusUpdate(raw);
                        break;

                    case "move_card":
                        HandleMoveCard(raw);
                        break;

                    case "update_checklist":
                        HandleChecklistUpdate(raw);
                        break;

                    case "update_card_text":
                        HandleCardTextUpdate(raw);
                        break;

                    case "add_card":
                        HandleAddCard(raw);
                        break;

                    case "delete_card":
                        HandleDeleteCard(raw);
                        break;

                    case "update_phase_notes":
                        HandlePhaseNotesUpdate(raw);
                        break;

                    case "update_continuation_notes":
                        HandleContinuationNotesUpdate(raw);
                        break;

                    case "update_title":
                        HandleTitleUpdate(raw);
                        break;

                    case "reorder_cards":
                        HandleChecklistUpdate(raw);
                        break;

                    case "update_priority":
                        HandlePriorityUpdate(raw);
                        break;

                    case "update_assignee":
                        HandleAssigneeUpdate(raw);
                        break;

                    case "update_description":
                        HandleDescriptionUpdate(raw);
                        break;

                    case "update_card_assignee":
                        HandleCardAssigneeUpdate(raw);
                        break;

                    case "add_card_note":
                        HandleAddCardNote(raw);
                        break;

                    case "update_helpers":
                        HandleHelpersUpdate(raw);
                        break;

                    case "close_form":
                        Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lifecycle board message error: " + ex.Message);
            }
        }

        // ── Message handlers (delegate to REST API) ─────────

        private void HandleCardStatusUpdate(Dictionary<string, object> raw)
        {
            int cardIndex = GetInt(raw, "cardIndex", -1);
            string newStatus = GetString(raw, "newStatus");
            if (cardIndex < 0 || newStatus == null) return;

            string assignee = _taskDetail.Task != null ? _taskDetail.Task.Assignee : "User";
            _api.TransitionChecklistItem(_taskId, cardIndex, newStatus, null, assignee ?? "User");
        }

        private void HandleMoveCard(Dictionary<string, object> raw)
        {
            int cardIndex = GetInt(raw, "cardIndex", -1);
            string newStatus = GetString(raw, "newStatus");
            int dropPosition = GetInt(raw, "dropPosition", -1);
            string notes = GetString(raw, "notes");
            if (cardIndex < 0 || newStatus == null || dropPosition < 0) return;

            // For status transitions, use the transition API (handles validation, cycle counts, notes)
            if (_taskDetail.Checklist != null && cardIndex < _taskDetail.Checklist.Count)
            {
                var item = _taskDetail.Checklist[cardIndex];
                string oldStatus = item.Status ?? "pending";

                if (oldStatus != newStatus)
                {
                    string assignee = _taskDetail.Task != null ? _taskDetail.Task.Assignee : "User";
                    var result = _api.TransitionChecklistItem(_taskId, cardIndex, newStatus, notes, assignee ?? "User");
                    if (!result.Success)
                    {
                        // Invalid transition — refresh to revert
                        RefreshTask();
                        return;
                    }
                }
            }

            // Reorder is handled by refreshing — the server manages the full checklist state
            RefreshTask();
        }

        private void HandleChecklistUpdate(Dictionary<string, object> raw)
        {
            string checklistJson = GetString(raw, "checklistJson");
            if (checklistJson == null) return;

            // Parse and send as full checklist replacement
            var items = ParseChecklistForApi(checklistJson);
            if (items != null)
                _api.UpdateChecklist(_taskId, items);
        }

        private void HandleCardTextUpdate(Dictionary<string, object> raw)
        {
            // Card text updates need to go through full checklist update
            // since the REST API doesn't have a single-card-text endpoint
            int cardIndex = GetInt(raw, "cardIndex", -1);
            string text = GetString(raw, "text");
            if (cardIndex < 0 || text == null || _taskDetail.Checklist == null) return;
            if (cardIndex >= _taskDetail.Checklist.Count) return;

            _taskDetail.Checklist[cardIndex].Item = text;
            SendFullChecklist();
        }

        private void HandleAddCard(Dictionary<string, object> raw)
        {
            string status = GetString(raw, "status") ?? "pending";
            string text = GetString(raw, "text") ?? "New item";

            if (_taskDetail.Checklist == null)
                _taskDetail.Checklist = new List<ChecklistItem>();

            _taskDetail.Checklist.Add(new ChecklistItem
            {
                Item = text,
                Status = status,
                Done = status == "done",
                Notes = new List<ChecklistNote>(),
                CycleCount = 0
            });
            SendFullChecklist();
        }

        private void HandleDeleteCard(Dictionary<string, object> raw)
        {
            int cardIndex = GetInt(raw, "cardIndex", -1);
            if (cardIndex < 0 || _taskDetail.Checklist == null) return;
            if (cardIndex >= _taskDetail.Checklist.Count) return;

            _taskDetail.Checklist.RemoveAt(cardIndex);
            SendFullChecklist();
        }

        private void HandlePhaseNotesUpdate(Dictionary<string, object> raw)
        {
            string phase = GetString(raw, "phase");
            string notes = GetString(raw, "notes");
            if (phase == null) return;

            string assignee = _taskDetail.Task != null ? (_taskDetail.Task.Assignee ?? "User") : "User";

            switch (phase)
            {
                case "planning":
                    _api.UpdatePlan(_taskId, notes, assignee);
                    break;
                case "coding":
                    _api.UpdateSummary(_taskId, implementationSummary: notes, updatedBy: assignee);
                    break;
                case "testing":
                    _api.UpdateSummary(_taskId, testResults: notes, updatedBy: assignee);
                    break;
            }
        }

        private void HandleContinuationNotesUpdate(Dictionary<string, object> raw)
        {
            string notes = GetString(raw, "notes");
            if (notes == null) return;
            string assignee = _taskDetail.Task != null ? (_taskDetail.Task.Assignee ?? "User") : "User";
            _api.UpdateContinuation(_taskId, notes, assignee);
        }

        private void HandleTitleUpdate(Dictionary<string, object> raw)
        {
            // TODO: No dedicated REST endpoint for title update yet. Revert WebView to server state.
            RefreshTask();
        }

        private void HandlePriorityUpdate(Dictionary<string, object> raw)
        {
            // TODO: No dedicated REST endpoint for priority update yet. Revert WebView to server state.
            RefreshTask();
        }

        private void HandleAssigneeUpdate(Dictionary<string, object> raw)
        {
            string assignee = GetString(raw, "assignee");
            if (assignee == null) return;
            _api.ClaimTask(_taskId, assignee);
        }

        private void HandleDescriptionUpdate(Dictionary<string, object> raw)
        {
            // TODO: No dedicated REST endpoint for description update yet. Revert WebView to server state.
            RefreshTask();
        }

        private void HandleCardAssigneeUpdate(Dictionary<string, object> raw)
        {
            int cardIndex = GetInt(raw, "cardIndex", -1);
            string assignee = GetString(raw, "assignee");
            if (cardIndex < 0 || _taskDetail.Checklist == null) return;
            if (cardIndex >= _taskDetail.Checklist.Count) return;

            _api.AssignChecklistItem(_taskId, cardIndex, assignee ?? "");
        }

        private void HandleAddCardNote(Dictionary<string, object> raw)
        {
            int cardIndex = GetInt(raw, "cardIndex", -1);
            string text = GetString(raw, "text");
            if (cardIndex < 0 || text == null || _taskDetail.Checklist == null) return;
            if (cardIndex >= _taskDetail.Checklist.Count) return;

            var item = _taskDetail.Checklist[cardIndex];
            if (item.Notes == null) item.Notes = new List<ChecklistNote>();

            string by = GetString(raw, "by");
            if (by == null && _taskDetail.Task != null) by = _taskDetail.Task.Assignee;
            if (by == null) by = "User";

            item.Notes.Add(new ChecklistNote
            {
                By = by,
                At = DateTime.UtcNow.ToString("o"),
                Text = text
            });
            SendFullChecklist();
        }

        private void HandleHelpersUpdate(Dictionary<string, object> raw)
        {
            object helpersObj;
            if (!raw.TryGetValue("helpers", out helpersObj)) return;
            var helpersList = helpersObj as ArrayList;
            if (helpersList == null) return;

            var currentHelpers = _taskDetail.Task != null && _taskDetail.Task.Helpers != null
                ? _taskDetail.Task.Helpers : new List<string>();
            var newHelpers = new List<string>();
            foreach (var h in helpersList)
            {
                string name = h as string;
                if (!string.IsNullOrEmpty(name)) newHelpers.Add(name);
            }

            // Add new helpers
            foreach (var helper in newHelpers)
            {
                bool found = false;
                foreach (var c in currentHelpers)
                    if (string.Equals(c, helper, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found) _api.AddHelper(_taskId, helper);
            }

            // Remove old helpers
            foreach (var helper in currentHelpers)
            {
                bool found = false;
                foreach (var n in newHelpers)
                    if (string.Equals(n, helper, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found) _api.RemoveHelper(_taskId, helper);
            }
        }

        // ── Helpers ─────────────────────────────────────────

        private void RefreshTask()
        {
            System.Threading.Tasks.Task.Run(() => _api.GetTaskDetail(_taskId))
                .ContinueWith(t =>
                {
                    if (IsDisposed) return;
                    if (t.Result.Success && t.Result.Data != null)
                    {
                        _taskDetail = t.Result.Data;
                        SendTaskToWebView();
                    }
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void SendFullChecklist()
        {
            if (_taskDetail.Checklist == null) return;
            _api.UpdateChecklist(_taskId, BuildChecklistForApi(_taskDetail.Checklist));
        }

        private List<Dictionary<string, object>> ParseChecklistForApi(string checklistJson)
        {
            try
            {
                var raw = _json.DeserializeObject(checklistJson);
                var arr = raw as ArrayList;
                if (arr == null) return null;

                var items = new List<Dictionary<string, object>>();
                foreach (var obj in arr)
                {
                    var dict = obj as Dictionary<string, object>;
                    if (dict != null) items.Add(dict);
                }
                return items;
            }
            catch { return null; }
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict.TryGetValue(key, out val) ? val as string : null;
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int defaultValue)
        {
            object val;
            if (dict.TryGetValue(key, out val))
            {
                if (val is int) return (int)val;
                int result;
                if (int.TryParse(val.ToString(), out result)) return result;
            }
            return defaultValue;
        }

        private void PostMessage(string jsonMessage)
        {
            if (_webView != null && _webView.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsJson(jsonMessage);
        }

        // ── Theme ───────────────────────────────────────────

        public void ApplyTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            if (!_isInitialized) return;
            if (_webView != null && _webView.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString(isDark ? "theme:dark" : "theme:light");
        }

        public static void ApplyThemeToAll(bool isDark)
        {
            foreach (var kvp in _openWindows)
            {
                if (kvp.Value != null && !kvp.Value.IsDisposed)
                    kvp.Value.ApplyTheme(isDark);
            }
        }

        // ── Cleanup ─────────────────────────────────────────

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer.Dispose();
                _pollTimer = null;
            }

            if (WindowState == FormWindowState.Normal)
                _settings.Set("LifecycleBoardBounds", string.Format("{0},{1},{2},{3}", Left, Top, Width, Height));
            else
                _settings.Set("LifecycleBoardBounds", string.Format("{0},{1},{2},{3}",
                    RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height));

            _openWindows.Remove(_taskId);

            if (_webView != null)
            {
                _webView.CoreWebView2InitializationCompleted -= OnWebViewInitialized;
                _webView.WebMessageReceived -= OnWebMessageReceived;
                _webView.Dispose();
                _webView = null;
            }

            base.OnFormClosed(e);
        }
    }
}
