using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ClarionAssistant.Dialogs;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    public class AssistantChatControl : UserControl
    {
        // Tab system (MultiTerminal Panel-based pattern)
        private TabManager _tabManager;
        private Panel _tabStrip;    // custom-painted tab header strip (hidden when 1 tab)
        private Panel _contentArea; // holds all tab content controls
        private HomeWebView _homeView;

        // Header (WebView2)
        private HeaderWebView _header;
        private Splitter _splitter;
        private Form _logForm;

        private McpServer _mcpServer;
        private McpToolRegistry _toolRegistry;
        private Services.KnowledgeService _knowledgeService;
        private Services.InstanceCoordinationService _instanceCoord;
        private readonly EditorService _editorService;
        private readonly ClarionClassParser _parser;
        private readonly SettingsService _settings;

        private string _mcpConfigPath;
        private bool _isDarkTheme = true;
        private System.Windows.Forms.Timer _instanceStateTimer;
        private System.Windows.Forms.Timer _statusLineTimer;
        private string _currentSlnPath;

        // Backend override captured from the dashboard dropdown on the most recent
        // launch-flavored action. Consumed by LaunchAssistantForTab when the async
        // renderer init completes. Not protected against a double-click race (user
        // dispatches action A then action B before A's WebView2 renderer finishes
        // initializing); if that becomes an issue, move consumption into the
        // synchronous per-handler tab-creation paths.
        private string _pendingLaunchBackend;
        private ClarionVersionInfo _versionInfo;
        private ClarionVersionConfig _currentVersionConfig;
        private RedFileService _redFileService;
        private DiffService _diffService;

        // Counter used when a tab's display name is empty, to give the
        // multiterminal-channel plugin a unique agent name.
        private int _caTabCounter;

        // LSP UI state: bottom status bar + stay-on-top diagnostics form
        private System.Windows.Forms.Timer _lspUiTimer;
        private Terminal.LspStatusBar _lspStatusBar;
        private Dialogs.DiagnosticsForm _diagForm;
        private readonly List<string> _lspActivityBuffer = new List<string>();
        private string _lastDiagFile;
        private int _lastDiagErrors = -1;
        private int _lastDiagWarnings = -1;
        private List<Services.LspClient.DiagnosticEntry> _lastDiagEntries;

        public string CurrentSolutionPath { get { return _currentSlnPath; } }
        public SettingsService Settings { get { return _settings; } }
        public ClarionVersionConfig CurrentVersionConfig { get { return _currentVersionConfig; } }
        public RedFileService RedFile { get { return _redFileService; } }
        public TabManager TabManager { get { return _tabManager; } }
        public string CurrentDbPath
        {
            get
            {
                if (string.IsNullOrEmpty(_currentSlnPath)) return null;
                return Path.Combine(Path.GetDirectoryName(_currentSlnPath),
                    Path.GetFileNameWithoutExtension(_currentSlnPath) + ".codegraph.db");
            }
        }

        public AssistantChatControl()
        {
            _editorService = new EditorService();
            _parser = new ClarionClassParser();
            _settings = new SettingsService();
            _isDarkTheme = (_settings.Get("Theme") ?? "dark") != "light";
            InitializeComponents();
        }

        #region UI Setup

        private void InitializeComponents()
        {
            SuspendLayout();

            // === Header (WebView2) ===
            _header = new HeaderWebView();
            _header.ActionReceived += OnHeaderAction;
            _header.HeaderReady += OnHeaderReady;

            // Restore saved header height
            int savedHeight;
            string heightStr = _settings.Get("Header.Height");
            if (!string.IsNullOrEmpty(heightStr) && int.TryParse(heightStr, out savedHeight))
                _header.Height = Math.Max(60, Math.Min(400, savedHeight));

            // === Splitter between header and content ===
            _splitter = new Splitter
            {
                Dock = DockStyle.Top,
                Height = 4,
                BackColor = Color.FromArgb(49, 50, 68),
                MinSize = 60,
                Cursor = Cursors.SizeNS
            };
            _splitter.SplitterMoved += OnSplitterMoved;

            // === Tab strip (custom-painted, hidden when only 1 tab — MultiTerminal pattern) ===
            _tabStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = _isDarkTheme ? Color.FromArgb(24, 24, 37) : Color.FromArgb(210, 214, 222),
                Visible = false  // hidden until 2+ tabs
            };

            // === Content area (tab pages shown/hidden via Visible) ===
            _contentArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _isDarkTheme ? Color.FromArgb(12, 12, 12) : Color.White
            };

            // === Home page ===
            _homeView = new HomeWebView();
            _homeView.ActionReceived += OnHomeAction;
            _homeView.HomeReady += OnHomeReady;

            // === LSP status bar (bottom of terminal content area) ===
            _lspStatusBar = new Terminal.LspStatusBar();
            _lspStatusBar.DiagnosticsClicked += OnDiagnosticsBarClicked;
            _contentArea.Controls.Add(_lspStatusBar);

            // === Tab manager ===
            _tabManager = new TabManager(_tabStrip, _contentArea);
            _tabManager.ActiveTabChanged += OnActiveTabChanged;

            // Add in correct order (Fill first, then Top items from bottom to top)
            Controls.Add(_contentArea);
            Controls.Add(_tabStrip);
            Controls.Add(_splitter);
            Controls.Add(_header);

            // Create Home tab — HomeWebView added to _contentArea, visible immediately
            _tabManager.CreateHomeTab(_homeView);

            ApplyThemeColors();

            ResumeLayout(false);
        }

        private void OnHeaderReady(object sender, EventArgs e)
        {
            LoadVersions();
            LoadSolutionHistory();
            DetectFromIde();
            StartMcpServer();
            _header.SetTheme(_isDarkTheme);
            SyncTabBarToHeader();
            // Solutions now auto-detected from IDE, no longer shown on home page
        }

        private void OnHomeReady(object sender, EventArgs e)
        {
            _homeView.SetTheme(_isDarkTheme);
            _homeView.SetBackend(_settings.Get("Assistant.Backend") ?? "Claude");
            LoadProjects();
            SendProjectsToHome();
            SendGitHubAccountsToHome();
            SendDefaultProjectFolderToHome();
        }

        private void SendDefaultProjectFolderToHome()
        {
            if (!_homeView.IsReady) return;
            try
            {
                string folder = _settings.Get("COM.ProjectsFolder") ?? "";
                _homeView.SetDefaultProjectFolder(folder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] SendDefaultProjectFolderToHome error: " + ex.Message);
            }
        }

        private void SendGitHubAccountsToHome()
        {
            if (!_homeView.IsReady) return;
            try
            {
                var accounts = Services.SchemaGraphService.GetAllGitHubAccounts();
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < accounts.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = accounts[i];
                    string prov = a.ContainsKey("provider") ? (string)a["provider"] : "github";
                    sb.AppendFormat("{{\"id\":\"{0}\",\"displayName\":\"{1}\",\"username\":\"{2}\",\"provider\":\"{3}\"}}",
                        EscJson((string)a["id"]), EscJson((string)a["displayName"]), EscJson((string)a["username"]), EscJson(prov));
                }
                sb.Append("]");
                _homeView.SetGitHubAccounts(sb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] SendGitHubAccountsToHome error: " + ex.Message);
            }
        }

        private void OnHomeAction(object sender, HomeActionEventArgs e)
        {
            // Capture the dashboard's backend choice for launch-flavored actions.
            // LaunchAssistantForTab picks it up when the renderer finishes initializing.
            // Non-launch actions (addProject, settings, etc.) leave _pendingLaunchBackend
            // untouched since they don't result in a new terminal tab.
            if (!string.IsNullOrEmpty(e.Backend) && IsLaunchAction(e.Action))
            {
                _pendingLaunchBackend = e.Backend;
            }

            switch (e.Action)
            {
                case "openFolder": OpenFolder(e.Data); break;
                case "addProject": OnAddProject(e.Data); break;
                case "editProject": OnEditProject(e.Data); break;
                case "deleteProject": OnDeleteProject(e.Data); break;
                case "openProject": OnOpenProject(e.Data); break;
                case "browseProjectFolder": OnBrowseProjectFolder(e.Data); break;
                case "workWithSolution": OnWorkWithSolution(); break;
                case "newChat": OnNewChat(sender, EventArgs.Empty); break;
                case "evaluateCode": OnEvaluateCode(sender, EventArgs.Empty); break;
                case "settings": OnSettings(sender, EventArgs.Empty); break;
                case "createClass": OnCreateClass(); break;
                case "openGitHub": OnOpenGitHub(); break;
            }
        }

        private static bool IsLaunchAction(string action)
        {
            return action == "newChat"
                || action == "workWithSolution"
                || action == "evaluateCode"
                || action == "createClass"
                || action == "openProject";
        }

        private void OnOpenGitHub()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("https://github.com/clarionlive/clarionassistant")
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open GitHub page: " + ex.Message,
                    "Clarion Assistant", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OnActiveTabChanged(object sender, TerminalTab tab)
        {
            SyncTabBarToHeader();
            if (tab != null && !tab.IsHome && tab.Renderer != null)
                tab.Renderer.Focus();
        }

        private void SyncTabBarToHeader()
        {
            // Tab bar is now managed by the WinForms TabControl directly
        }

        private void OnHeaderAction(object sender, HeaderActionEventArgs e)
        {
            switch (e.Action)
            {
                case "newChat": OnNewChat(sender, EventArgs.Empty); break;
                case "settings": OnSettings(sender, EventArgs.Empty); break;
                case "createCom": OnCreateCom(sender, EventArgs.Empty); break;
                case "createClass": OnCreateClass(); break;
                case "evaluateCode": OnEvaluateCode(sender, EventArgs.Empty); break;
                case "refresh": DetectFromIde(); break;
                case "browse": OnBrowseSolution(sender, EventArgs.Empty); break;
                case "fullIndex": RunIndex(false); break;
                case "updateIndex": RunIndex(true); break;
                case "versionChanged": OnVersionChanged(e.Data); break;
                case "solutionChanged": OnSolutionChanged(e.Data); break;
                case "themeChanged": OnThemeChanged(e.Data); break;
                case "cheatSheet": OnCheatSheet(); break;
                case "docs": OnDocs(); break;
                case "showLog": ShowIndexLog(); break;
                case "openGitHub": OnOpenGitHub(); break;
            }
        }

        #endregion

        #region Projects

        private class ProjectEntry
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }   // "COM Control", "Addin", "Other"
            public string Folder { get; set; }
            public long LastAccessed { get; set; }  // Unix ms
            public string GitHubAccountId { get; set; }
            public string RepoName { get; set; }
        }

        private List<ProjectEntry> _projects = new List<ProjectEntry>();

        private string GetProjectsJsonPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", "projects.json");
        }

        private void LoadProjects()
        {
            _projects.Clear();
            string path = GetProjectsJsonPath();
            if (!File.Exists(path)) return;
            try
            {
                string json = File.ReadAllText(path);
                // JSON array parser — quote-aware brace matching to handle } inside string values
                int idx = json.IndexOf('[');
                if (idx < 0) return;
                idx++;
                while (idx < json.Length)
                {
                    int objStart = json.IndexOf('{', idx);
                    if (objStart < 0) break;
                    int objEnd = FindClosingBrace(json, objStart);
                    if (objEnd < 0) break;
                    string obj = json.Substring(objStart, objEnd - objStart + 1);
                    var entry = new ProjectEntry
                    {
                        Id = ExtractJsonString(obj, "id"),
                        Name = ExtractJsonString(obj, "name"),
                        Type = ExtractJsonString(obj, "type"),
                        Folder = ExtractJsonString(obj, "folder"),
                        LastAccessed = ExtractJsonLong(obj, "lastAccessed"),
                        GitHubAccountId = ExtractJsonString(obj, "githubAccountId"),
                        RepoName = ExtractJsonString(obj, "repoName")
                    };
                    if (!string.IsNullOrEmpty(entry.Id))
                        _projects.Add(entry);
                    idx = objEnd + 1;
                }
            }
            catch { }
        }

        private void SaveProjects()
        {
            try
            {
                string dir = Path.GetDirectoryName(GetProjectsJsonPath());
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("[");
                for (int i = 0; i < _projects.Count; i++)
                {
                    var p = _projects[i];
                    if (i > 0) sb.AppendLine(",");
                    sb.Append("  {");
                    sb.AppendFormat("\"id\":\"{0}\",", EscJson(p.Id));
                    sb.AppendFormat("\"name\":\"{0}\",", EscJson(p.Name));
                    sb.AppendFormat("\"type\":\"{0}\",", EscJson(p.Type));
                    sb.AppendFormat("\"folder\":\"{0}\",", EscJson(p.Folder));
                    sb.AppendFormat("\"lastAccessed\":{0}", p.LastAccessed);
                    if (!string.IsNullOrEmpty(p.GitHubAccountId))
                        sb.AppendFormat(",\"githubAccountId\":\"{0}\"", EscJson(p.GitHubAccountId));
                    if (!string.IsNullOrEmpty(p.RepoName))
                        sb.AppendFormat(",\"repoName\":\"{0}\"", EscJson(p.RepoName));
                    sb.Append("}");
                }
                sb.AppendLine();
                sb.AppendLine("]");
                File.WriteAllText(GetProjectsJsonPath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void SendProjectsToHome()
        {
            if (!_homeView.IsReady) return;
            var sb = new StringBuilder("[");
            for (int i = 0; i < _projects.Count; i++)
            {
                var p = _projects[i];
                if (i > 0) sb.Append(",");
                sb.AppendFormat("{{\"id\":\"{0}\",\"name\":\"{1}\",\"type\":\"{2}\",\"folder\":\"{3}\",\"lastAccessed\":{4},\"githubAccountId\":\"{5}\",\"repoName\":\"{6}\"}}",
                    EscJson(p.Id), EscJson(p.Name), EscJson(p.Type), EscJson(p.Folder), p.LastAccessed,
                    EscJson(p.GitHubAccountId ?? ""), EscJson(p.RepoName ?? ""));
            }
            sb.Append("]");
            _homeView.SetProjectsJson(sb.ToString());
        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static long NowUnixMs()
        {
            return (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
        }

        private static string EscJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r")
                    .Replace("\t", "\\t").Replace("\b", "\\b").Replace("\f", "\\f");
        }

        private static string ExtractJsonString(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++; // skip opening quote
            var sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length) { sb.Append(json[idx + 1]); idx += 2; continue; }
                if (c == '"') break;
                sb.Append(c);
                idx++;
            }
            return sb.ToString();
        }

        private static long ExtractJsonLong(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += search.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t')) idx++;
            int start = idx;
            while (idx < json.Length && ((json[idx] >= '0' && json[idx] <= '9') || json[idx] == '-')) idx++;
            long val;
            long.TryParse(json.Substring(start, idx - start), out val);
            return val;
        }

        /// <summary>
        /// Find the closing } that matches the { at position start,
        /// skipping over characters inside double-quoted strings.
        /// </summary>
        private static int FindClosingBrace(string json, int start)
        {
            int depth = 0;
            bool inString = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length) { i++; continue; } // skip escaped char
                    if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}') { depth--; if (depth == 0) return i; }
                }
            }
            return -1; // malformed JSON
        }

        private void OnAddProject(string json)
        {
            string name = ExtractJsonString(json, "name");
            string type = ExtractJsonString(json, "type");
            string folder = ExtractJsonString(json, "folder");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folder)) return;

            var entry = new ProjectEntry
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                Type = type ?? "Other",
                Folder = folder,
                LastAccessed = NowUnixMs(),
                GitHubAccountId = ExtractJsonString(json, "githubAccountId"),
                RepoName = ExtractJsonString(json, "repoName")
            };
            _projects.Add(entry);
            SaveProjects();
            SendProjectsToHome();
        }

        private void OnEditProject(string json)
        {
            string id = ExtractJsonString(json, "id");
            string name = ExtractJsonString(json, "name");
            string folder = ExtractJsonString(json, "folder");
            if (string.IsNullOrEmpty(id)) return;

            var entry = _projects.Find(p => p.Id == id);
            if (entry == null) return;

            if (!string.IsNullOrEmpty(name)) entry.Name = name;
            if (!string.IsNullOrEmpty(folder)) entry.Folder = folder;
            entry.GitHubAccountId = ExtractJsonString(json, "githubAccountId");
            entry.RepoName = ExtractJsonString(json, "repoName");
            SaveProjects();
            SendProjectsToHome();
        }

        private void OnDeleteProject(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _projects.RemoveAll(p => p.Id == id);
            SaveProjects();
            SendProjectsToHome();
        }

        private void OnOpenProject(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var entry = _projects.Find(p => p.Id == id);
            if (entry == null) return;

            // Update lastAccessed
            entry.LastAccessed = NowUnixMs();
            SaveProjects();
            SendProjectsToHome();

            OpenProjectInNewTab(entry);
        }

        private void OnBrowseProjectFolder(string editId)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select Project Folder";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _homeView.SendBrowseResult(dlg.SelectedPath, editId ?? "");
                }
            }
        }

        private void OpenProjectInNewTab(ProjectEntry project)
        {
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OpenProjectInNewTab: " + project.Name + " (" + project.Type + ") -> " + project.Folder);
            string folder = project.Folder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OpenProjectInNewTab ABORTED: folder empty or not found");
                MessageBox.Show("Project folder not found:\n" + (folder ?? "(empty)"),
                    "Open Project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string name = project.Name;
            var renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
            var tab = _tabManager.CreateTerminalTab(name, renderer);
            tab.WorkingDirectory = folder;
            tab.VersionConfig = _currentVersionConfig;

            // Set startup command based on project type
            switch (project.Type)
            {
                case "COM Control":
                    tab.StartupCommand = "/ClarionCOM";
                    break;
                case "Addin":
                    tab.StartupCommand = "/clarion-ide-addin";
                    break;
                // "Other" — no startup command
            }

            renderer.DataReceived += data => OnTabRendererDataReceived(tab, data);
            renderer.TerminalResized += (s, ev) => OnTabRendererResized(tab, ev);
            renderer.Initialized += (s, ev) => OnTabRendererInitialized(tab);
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] Events wired for project tab " + tab.Id + ", StartupCommand=" + (tab.StartupCommand ?? "(none)"));

            // Schema sources panel (above terminal)
            AttachSchemaSourcesView(tab);

            _tabManager.ActivateTab(tab.Id);
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] ActivateTab completed for project tab " + tab.Id);
        }

        #endregion

        #region Solution Bar Logic

        private void LoadVersions()
        {
            _versionInfo = ClarionVersionService.Detect();

            if (_versionInfo == null || _versionInfo.Versions.Count == 0)
            {
                _header.SetVersions(new[] { "(not detected)" }, new[] { "" }, 0);
                return;
            }

            _currentVersionConfig = _versionInfo.GetCurrentConfig();

            var labels = new System.Collections.Generic.List<string>();
            var values = new System.Collections.Generic.List<string>();
            int selectedIdx = 0;

            for (int i = 0; i < _versionInfo.Versions.Count; i++)
            {
                var config = _versionInfo.Versions[i];
                string label = config.Name;
                if (_currentVersionConfig != null && config.Name == _currentVersionConfig.Name
                    && _versionInfo.CurrentVersionName != null
                    && _versionInfo.CurrentVersionName.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0)
                    label += " (active)";

                labels.Add(label);
                values.Add(config.Name);
                if (_currentVersionConfig != null && config.Name == _currentVersionConfig.Name)
                    selectedIdx = i;
            }

            _header.SetVersions(labels.ToArray(), values.ToArray(), selectedIdx);
        }

        private void OnVersionChanged(string value)
        {
            if (_versionInfo != null && !string.IsNullOrEmpty(value))
            {
                _currentVersionConfig = _versionInfo.Versions.Find(v => v.Name == value);
                LoadRedFile();
            }
        }

        private void LoadRedFile()
        {
            _redFileService = new RedFileService();
            if (_currentVersionConfig == null) return;

            string projectDir = null;
            if (!string.IsNullOrEmpty(_currentSlnPath))
                projectDir = Path.GetDirectoryName(_currentSlnPath);

            _redFileService.LoadForProject(projectDir, _currentVersionConfig);
        }

        private void LoadSolutionHistory()
        {
            string history = _settings.Get("SolutionHistory") ?? "";
            var paths = new System.Collections.Generic.List<string>();
            foreach (string path in history.Split('|'))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    paths.Add(path);
            }

            string last = _settings.Get("LastSolutionPath");
            int selectedIdx = -1;
            if (!string.IsNullOrEmpty(last) && File.Exists(last))
            {
                selectedIdx = paths.IndexOf(last);
                if (selectedIdx < 0)
                {
                    paths.Insert(0, last);
                    selectedIdx = 0;
                }
                _currentSlnPath = last;
            }

            _header.SetSolutions(paths.ToArray(), selectedIdx);
            UpdateIndexStatus();

            // Auto-index on startup if a solution is loaded
            if (!string.IsNullOrEmpty(_currentSlnPath))
            {
                string dbPath = Path.Combine(
                    Path.GetDirectoryName(_currentSlnPath),
                    Path.GetFileNameWithoutExtension(_currentSlnPath) + ".codegraph.db");
                if (!File.Exists(dbPath))
                    RunIndex(false);
                else
                    RunIndex(true);
            }
        }

        private void AddToSolutionHistory(string path)
        {
            _settings.Set("LastSolutionPath", path);

            string history = _settings.Get("SolutionHistory") ?? "";
            var paths = new System.Collections.Generic.List<string>(history.Split('|'));
            paths.Remove(path);
            paths.Insert(0, path);
            if (paths.Count > 10) paths.RemoveRange(10, paths.Count - 10);
            _settings.Set("SolutionHistory", string.Join("|", paths));
        }

        /// <summary>
        /// Auto-detect the currently loaded solution from the IDE.
        /// Version detection is handled by LoadVersions() via ClarionVersionService.
        /// </summary>
        /// <summary>
        /// Called every 10s by the instance state timer. Checks if the IDE's open solution
        /// has changed and triggers a full refresh if so.
        /// </summary>
        private void PollForSolutionChange()
        {
            try
            {
                string slnPath = EditorService.GetOpenSolutionPath();
                if (!string.IsNullOrEmpty(slnPath) && File.Exists(slnPath) &&
                    !string.Equals(slnPath, _currentSlnPath, StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine("[AssistantChatControl] Solution changed: " + slnPath);
                    DetectFromIde();
                }
            }
            catch { }
        }

        public void DetectFromIde()
        {
            // Detect open solution from the IDE
            string slnPath = EditorService.GetOpenSolutionPath();
            if (!string.IsNullOrEmpty(slnPath) && File.Exists(slnPath))
            {
                _currentSlnPath = slnPath;
                AddToSolutionHistory(slnPath);
                LoadSolutionHistory();
            }

            // Always re-detect version (user may have changed build in IDE)
            LoadVersions();
            LoadRedFile();
            UpdateInstanceState();
        }

        private void OnSolutionChanged(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _currentSlnPath = path;
                AddToSolutionHistory(path);
                UpdateIndexStatus();
                LoadRedFile();
                UpdateInstanceState();

                // Ensure active tab has schema sources panel
                var activeTab = _tabManager.ActiveTab;
                if (activeTab != null && !activeTab.IsHome)
                {
                    activeTab.SolutionPath = path;
                    if (activeTab.SchemaSourcesView == null)
                        AttachSchemaSourcesView(activeTab);
                    else
                        SendSchemaSourcesForTab(activeTab);
                }

                // Auto-index in background if no codegraph.db exists yet
                string dbPath = Path.Combine(
                    Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + ".codegraph.db");
                if (!File.Exists(dbPath))
                    RunIndex(false); // full index
                else
                    RunIndex(true); // incremental update
            }
        }

        /// <summary>
        /// Push current IDE context into the instance coordination service.
        /// The heartbeat timer will broadcast it to the shared DB.
        /// Also updates the status line with peer count.
        /// </summary>
        private void UpdateInstanceState()
        {
            if (_instanceCoord == null) return;
            try
            {
                _instanceCoord.SolutionPath = _currentSlnPath;
                _instanceCoord.ActiveFile = _editorService.GetActiveDocumentPath();

                // Pull app file and active procedure from AppTreeService
                if (_toolRegistry != null)
                {
                    try
                    {
                        var appInfo = _toolRegistry.GetAppTreeService()?.GetAppInfo();
                        if (appInfo != null && appInfo.ContainsKey("fileName"))
                            _instanceCoord.AppFile = appInfo["fileName"]?.ToString();

                        var embedInfo = _toolRegistry.GetAppTreeService()?.GetEmbedInfo();
                        if (embedInfo != null && embedInfo.ContainsKey("fileName"))
                            _instanceCoord.ActiveProcedure = embedInfo["fileName"]?.ToString();
                        else
                            _instanceCoord.ActiveProcedure = null;
                    }
                    catch { /* AppTree reflection may fail — non-fatal */ }
                }

                // Append peer count to MCP status line
                int peerCount = _instanceCoord.GetPeers().Count;
                if (_mcpServer != null && _mcpServer.IsRunning)
                {
                    string status = "MCP: port " + _mcpServer.Port + " | " + _toolRegistry.GetToolCount() + " tools";
                    if (peerCount > 0)
                        status += " | " + peerCount + " peer" + (peerCount > 1 ? "s" : "");
                    _header?.SetStatus(status, "connected");
                }
            }
            catch { /* non-fatal */ }
        }

        private string _lastStatusLineJson;
        private string _lastStatusLineTabId;

        // ── LSP UI: bottom status bar + stay-on-top diagnostics form ─────

        private bool _lspEventWired;

        private void PollLspUi()
        {
            try
            {
                if (_lspStatusBar == null) return;

                // Wire up the OnLspRequest event once the LspClient is available
                var lsp = _toolRegistry?.LspClientInstance;
                if (lsp != null && !_lspEventWired)
                {
                    _lspEventWired = true;
                    lsp.OnLspRequest += OnLspRequest;
                }

                if (lsp == null || !lsp.IsRunning)
                {
                    _lspStatusBar.SetDiagnostics(0, 0, hidden: true);
                    return;
                }

                // Read diagnostics for the last file the LSP operated on
                string filePath = lsp.LastActiveFilePath;
                if (string.IsNullOrEmpty(filePath))
                {
                    _lspStatusBar.SetDiagnostics(0, 0, hidden: true);
                    return;
                }

                // Deduplicate diagnostics by (line, message) — the Clarion LSP server
                // can emit the same diagnostic dozens of times per analysis cycle.
                var raw = lsp.GetCachedDiagnostics(filePath);
                List<Services.LspClient.DiagnosticEntry> entries = null;
                int errors = 0, warnings = 0;
                if (raw != null && raw.Count > 0)
                {
                    var seen = new HashSet<string>();
                    entries = new List<Services.LspClient.DiagnosticEntry>();
                    foreach (var e in raw)
                    {
                        string key = e.Line + "|" + e.Severity + "|" + (e.Message ?? "");
                        if (seen.Add(key))
                        {
                            entries.Add(e);
                            if (e.Severity == 1) errors++;
                            else if (e.Severity == 2) warnings++;
                        }
                    }
                }

                // Only update if the file or counts changed (avoid flicker)
                if (filePath != _lastDiagFile || errors != _lastDiagErrors || warnings != _lastDiagWarnings)
                {
                    _lastDiagFile = filePath;
                    _lastDiagErrors = errors;
                    _lastDiagWarnings = warnings;
                    _lastDiagEntries = entries;
                    _lspStatusBar.SetDiagnostics(errors, warnings, hidden: false);

                    // Update the diagnostics form if it's visible
                    if (_diagForm != null && _diagForm.Visible)
                        _diagForm.UpdateDiagnostics(filePath, entries);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] PollLspUi error: " + ex.Message);
            }
        }

        private void OnLspRequest(string tool, string target)
        {
            try
            {
                string item = tool + ": " + target;

                // Dedupe: skip if identical to the most recent entry
                lock (_lspActivityBuffer)
                {
                    if (_lspActivityBuffer.Count > 0 && _lspActivityBuffer[0] == item)
                        return;
                    _lspActivityBuffer.Insert(0, item);
                    while (_lspActivityBuffer.Count > 5)
                        _lspActivityBuffer.RemoveAt(5);
                }

                // Marshal to UI thread to update the status bar
                if (!IsDisposed && _lspStatusBar != null)
                {
                    string[] snapshot;
                    lock (_lspActivityBuffer) { snapshot = _lspActivityBuffer.ToArray(); }
                    try { BeginInvoke((Action)(() => _lspStatusBar.SetActivity(snapshot))); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch { }
        }

        private void OnDiagnosticsBarClicked(object sender, EventArgs e)
        {
            if (_diagForm == null)
            {
                _diagForm = new Dialogs.DiagnosticsForm(
                    line => _editorService.GoToLine(line),
                    () => _lastDiagEntries);
                _diagForm.ApplyTheme(_isDarkTheme);
            }
            _diagForm.UpdateDiagnostics(_lastDiagFile, _lastDiagEntries);
            if (!_diagForm.Visible)
                _diagForm.Show(this);
            else
                _diagForm.BringToFront();
        }

        private void PollStatusLine()
        {
            try
            {
                var tab = _tabManager?.ActiveTab;
                if (tab == null || tab.IsHome || !tab.AssistantLaunched || tab.Renderer == null) return;
                if (!string.Equals(tab.AssistantBackend, "Claude", StringComparison.OrdinalIgnoreCase)) return;

                string filePath = Path.Combine(Path.GetTempPath(), "ca-statusline-" + tab.Id + ".json");
                if (!File.Exists(filePath)) return;

                string json = File.ReadAllText(filePath);
                // Only send if data changed or we switched tabs
                if (json == _lastStatusLineJson && tab.Id == _lastStatusLineTabId) return;
                _lastStatusLineJson = json;
                _lastStatusLineTabId = tab.Id;

                tab.Renderer.UpdateStatusLine(json);
            }
            catch { }
        }

        private void OnBrowseSolution(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Clarion Solution (*.sln)|*.sln";
                dlg.Title = "Select Clarion Solution";
                if (!string.IsNullOrEmpty(_currentSlnPath))
                    dlg.InitialDirectory = Path.GetDirectoryName(_currentSlnPath);

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _currentSlnPath = dlg.FileName;
                    AddToSolutionHistory(dlg.FileName);
                    LoadSolutionHistory();
                }
            }
        }

        private void UpdateIndexStatus()
        {
            if (!_header.IsReady) return;
            string dbPath = CurrentDbPath;
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                var fi = new FileInfo(dbPath);
                _header.SetIndexStatus("Indexed: " + fi.LastWriteTime.ToString("MMM d HH:mm"));
            }
            else
            {
                _header.SetIndexStatus("Not indexed", "warning");
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                string dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        private void RemoveSolutionFromHistory(string path)
        {
            // Add to suppressed list so it stays hidden from the home page
            // (we don't modify Clarion's own RecentOpen.xml)
            string suppressed = _settings.Get("SuppressedSolutions") ?? "";
            var suppressedList = new System.Collections.Generic.List<string>(suppressed.Split('|'));
            suppressedList.RemoveAll(p => string.IsNullOrEmpty(p));
            if (!suppressedList.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                suppressedList.Add(path);
            _settings.Set("SuppressedSolutions", string.Join("|", suppressedList));

            // Also remove from internal SolutionHistory if present
            string history = _settings.Get("SolutionHistory") ?? "";
            var histList = new System.Collections.Generic.List<string>(history.Split('|'));
            histList.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _settings.Set("SolutionHistory", string.Join("|", histList));

            LoadSolutionHistory(); // also refresh header dropdown
        }

        private void OnBrowseSolutionForNewTab()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Clarion Solution (*.sln)|*.sln";
                dlg.Title = "Select Clarion Solution";
                if (!string.IsNullOrEmpty(_currentSlnPath))
                    dlg.InitialDirectory = Path.GetDirectoryName(_currentSlnPath);

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    AddToSolutionHistory(dlg.FileName);
                    LoadSolutionHistory();
                            OpenSolutionInNewTab(dlg.FileName);
                }
            }
        }

        private void OpenSolutionInNewTab(string slnPath)
        {
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OpenSolutionInNewTab: " + slnPath);
            if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath))
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OpenSolutionInNewTab ABORTED: path empty or not found");
                return;
            }

            // Update global solution state
            _currentSlnPath = slnPath;
            AddToSolutionHistory(slnPath);
            LoadSolutionHistory();

            string name = Path.GetFileNameWithoutExtension(slnPath);
            var renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
            var tab = _tabManager.CreateTerminalTab(name, renderer);
            tab.SolutionPath = slnPath;
            tab.WorkingDirectory = Path.GetDirectoryName(slnPath);
            tab.VersionConfig = _currentVersionConfig;

            // Wire renderer events to per-tab handlers
            renderer.DataReceived += data => OnTabRendererDataReceived(tab, data);
            renderer.TerminalResized += (s, ev) => OnTabRendererResized(tab, ev);
            renderer.Initialized += (s, ev) => OnTabRendererInitialized(tab);
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] Events wired for tab " + tab.Id + ", calling ActivateTab");

            // Schema sources panel (above terminal)
            AttachSchemaSourcesView(tab);

            _tabManager.ActivateTab(tab.Id);
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] ActivateTab completed for tab " + tab.Id);
        }

        private void CloseTerminalTab(string tabId)
        {
            var tab = _tabManager.FindTab(tabId);
            if (tab == null || tab.IsHome) return;

            if (_knowledgeService != null && tab.SessionId > 0)
            {
                try { _knowledgeService.EndSession(tab.SessionId, null); }
                catch { }
            }

            // Clean up status line temp file
            try
            {
                string statusFile = Path.Combine(Path.GetTempPath(), "ca-statusline-" + tabId + ".json");
                if (File.Exists(statusFile)) File.Delete(statusFile);
            }
            catch { }

            _tabManager.CloseTab(tabId);
        }

        #endregion

        #region Schema Sources

        private void AttachSchemaSourcesView(TerminalTab tab)
        {
            var schemaView = new SchemaSourcesView();
            schemaView.ActionReceived += (s, ev) => OnSchemaSourceAction(tab, ev);
            schemaView.Ready += (s, ev) => OnSchemaSourcesReady(tab);
            tab.SchemaSourcesView = schemaView;

            // TabManager adds renderer directly to _contentArea (no TabPage).
            // Wrap renderer + SchemaSourcesView in a container Panel so
            // ActivateTab's Visible toggle controls both together.
            var renderer = tab.Renderer;
            if (renderer == null) return;
            var parent = renderer.Parent;
            if (parent == null) return;

            bool wasVisible = renderer.Visible;

            var container = new Panel { Dock = DockStyle.Fill };
            container.SuspendLayout();
            parent.SuspendLayout();

            parent.Controls.Remove(renderer);
            renderer.Visible = true;
            renderer.Dock = DockStyle.Fill;

            // Add Fill control first, then Top — WinForms docks later-added controls first
            container.Controls.Add(renderer);
            container.Controls.Add(schemaView);

            container.Visible = wasVisible;
            parent.Controls.Add(container);
            tab.ContentControl = container;

            parent.ResumeLayout(true);
            container.ResumeLayout(true);
        }

        private void OnSchemaSourcesReady(TerminalTab tab)
        {
            if (tab.SchemaSourcesView == null) return;
            tab.SchemaSourcesView.SetTheme(_isDarkTheme);

            // Check if collapse state was saved
            string collapsed = _settings.Get("SchemaSourcesCollapsed");
            if (collapsed == "true")
                tab.SchemaSourcesView.SetCollapsed(true);

            // Send linked sources for this tab's solution
            SendSchemaSourcesForTab(tab);

            // Send source control accounts and current repo link
            SendRepoDataForTab(tab);
        }

        private void SendSchemaSourcesForTab(TerminalTab tab)
        {
            if (tab.SchemaSourcesView == null || !tab.SchemaSourcesView.IsReady) return;

            string slnPath = tab.SolutionPath ?? tab.WorkingDirectory ?? "";
            if (string.IsNullOrEmpty(slnPath)) { tab.SchemaSourcesView.SetSources("[]"); return; }

            try
            {
                var sources = Services.SchemaGraphService.GetSourcesForSolution(slnPath);
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < sources.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var src = sources[i];
                    string id = (string)src["id"];
                    string name = (string)src["name"];
                    string type = (string)src["type"];
                    string connInfo = (string)src["connectionInfo"];

                    // Get index status
                    var status = Services.SchemaGraphService.GetSourceStatus(id, type, connInfo);
                    bool indexed = (bool)status["indexed"];
                    int tableCount = status.ContainsKey("tableCount") ? (int)status["tableCount"] : 0;
                    string lastIndexed = status.ContainsKey("lastIndexed") ? (string)status["lastIndexed"] : null;

                    sb.AppendFormat("{{\"id\":\"{0}\",\"name\":\"{1}\",\"type\":\"{2}\",\"indexed\":{3},\"tableCount\":{4},\"lastIndexed\":{5}}}",
                        EscJson(id), EscJson(name), EscJson(type),
                        indexed ? "true" : "false", tableCount,
                        lastIndexed != null ? "\"" + EscJson(lastIndexed) + "\"" : "null");
                }
                sb.Append("]");
                tab.SchemaSourcesView.SetSources(sb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] SendSchemaSourcesForTab error: " + ex.Message);
                tab.SchemaSourcesView.SetSources("[]");
            }
        }

        private void SendRepoDataForTab(TerminalTab tab)
        {
            if (tab.SchemaSourcesView == null || !tab.SchemaSourcesView.IsReady) return;

            // Send accounts list
            try
            {
                var accounts = Services.SchemaGraphService.GetAllGitHubAccounts();
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < accounts.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = accounts[i];
                    string prov = a.ContainsKey("provider") ? (string)a["provider"] : "github";
                    sb.AppendFormat("{{\"id\":\"{0}\",\"displayName\":\"{1}\",\"username\":\"{2}\",\"provider\":\"{3}\"}}",
                        EscJson((string)a["id"]), EscJson((string)a["displayName"]),
                        EscJson((string)a["username"]), EscJson(prov));
                }
                sb.Append("]");
                tab.SchemaSourcesView.SendMessage("{\"type\":\"setRepoAccounts\",\"accounts\":" + sb + "}");
            }
            catch { }

            // Send current repo link
            string slnPath = tab.SolutionPath ?? tab.WorkingDirectory ?? "";
            if (!string.IsNullOrEmpty(slnPath))
            {
                try
                {
                    var repo = Services.SchemaGraphService.GetSolutionRepo(slnPath);
                    if (repo != null)
                    {
                        tab.SchemaSourcesView.SendMessage(
                            "{\"type\":\"setSolutionRepo\",\"accountId\":\"" + EscJson(repo["accountId"]) +
                            "\",\"repoName\":\"" + EscJson(repo["repoName"]) + "\"}");
                    }
                }
                catch { }
            }
        }

        private void OnSchemaSourceAction(TerminalTab tab, SchemaSourceActionEventArgs e)
        {
            switch (e.Action)
            {
                case "schemaSourcesReady":
                    OnSchemaSourcesReady(tab);
                    break;

                case "toggleCollapse":
                    // Save collapse state
                    bool isCollapsed = tab.SchemaSourcesView != null && tab.SchemaSourcesView.Height <= 32;
                    _settings.Set("SchemaSourcesCollapsed", isCollapsed ? "true" : "false");
                    break;

                case "getGlobalSources":
                    SendGlobalSourcesToModal(tab);
                    break;

                case "addSource":
                    HandleAddSource(tab, e.Data);
                    break;

                case "editSource":
                    HandleEditSource(tab, e.Data);
                    break;

                case "deleteSource":
                    HandleDeleteSource(tab, e.Data);
                    break;

                case "applySourceSelection":
                    HandleApplySelection(tab, e.Data);
                    break;

                case "indexSource":
                    HandleIndexSource(tab, e.Data);
                    break;

                case "testConnection":
                    HandleTestConnection(tab, e.Data);
                    break;

                case "setSolutionRepo":
                    HandleSetSolutionRepo(tab, e.Data);
                    break;

                case "browseFile":
                    HandleBrowseFile(tab, e.Data);
                    break;
            }
        }

        private void SendGlobalSourcesToModal(TerminalTab tab)
        {
            if (tab.SchemaSourcesView == null) return;
            try
            {
                string slnPath = tab.SolutionPath ?? tab.WorkingDirectory ?? "";
                var allSources = Services.SchemaGraphService.GetAllSources();
                var linkedSources = Services.SchemaGraphService.GetSourcesForSolution(slnPath);
                var linkedIdSet = new System.Collections.Generic.HashSet<string>();
                foreach (var ls in linkedSources)
                    linkedIdSet.Add((string)ls["id"]);

                // Build global sources JSON — mask passwords before sending to WebView
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < allSources.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var src = allSources[i];
                    string connInfo = (string)src["connectionInfo"];
                    string maskedConnInfo = MaskPassword(connInfo);
                    sb.AppendFormat("{{\"id\":\"{0}\",\"name\":\"{1}\",\"type\":\"{2}\",\"connectionInfo\":\"{3}\"}}",
                        EscJson((string)src["id"]), EscJson((string)src["name"]),
                        EscJson((string)src["type"]), EscJson(maskedConnInfo));
                }
                sb.Append("]");

                // Build linked IDs JSON
                var idSb = new System.Text.StringBuilder("[");
                int idx = 0;
                foreach (var id in linkedIdSet)
                {
                    if (idx++ > 0) idSb.Append(",");
                    idSb.Append("\"" + EscJson(id) + "\"");
                }
                idSb.Append("]");

                tab.SchemaSourcesView.SetGlobalSources(sb.ToString(), idSb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] SendGlobalSources error: " + ex.Message);
            }
        }

        private void HandleAddSource(TerminalTab tab, string data)
        {
            try
            {
                string name = ExtractJsonField(data, "name");
                string type = ExtractJsonField(data, "type");
                string connInfo = ExtractJsonField(data, "connectionInfo");
                Services.SchemaGraphService.AddSource(name, type, connInfo);
                SendGlobalSourcesToModal(tab);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] AddSource error: " + ex.Message);
            }
        }

        private void HandleEditSource(TerminalTab tab, string data)
        {
            try
            {
                string id = ExtractJsonField(data, "id");
                string name = ExtractJsonField(data, "name");
                string type = ExtractJsonField(data, "type");
                string connInfo = ExtractJsonField(data, "connectionInfo");
                connInfo = RestorePasswordIfPlaceholder(connInfo, id);
                Services.SchemaGraphService.UpdateSource(id, name, type, connInfo);
                SendGlobalSourcesToModal(tab);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] EditSource error: " + ex.Message);
            }
        }

        private void HandleDeleteSource(TerminalTab tab, string data)
        {
            try
            {
                Services.SchemaGraphService.DeleteSource(data);
                SendGlobalSourcesToModal(tab);
                SendSchemaSourcesForTab(tab);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] DeleteSource error: " + ex.Message);
            }
        }

        private void HandleApplySelection(TerminalTab tab, string selectedIdsJson)
        {
            try
            {
                string slnPath = tab.SolutionPath ?? tab.WorkingDirectory ?? "";
                if (string.IsNullOrEmpty(slnPath)) return;

                // Parse selected IDs from JSON array
                var selectedIds = new System.Collections.Generic.List<string>();
                string inner = selectedIdsJson.Trim().TrimStart('[').TrimEnd(']');
                if (!string.IsNullOrEmpty(inner))
                {
                    foreach (string part in inner.Split(','))
                    {
                        string id = part.Trim().Trim('"');
                        if (!string.IsNullOrEmpty(id)) selectedIds.Add(id);
                    }
                }

                // Get current linked IDs
                var currentLinked = Services.SchemaGraphService.GetSourcesForSolution(slnPath);
                var currentIds = new System.Collections.Generic.HashSet<string>();
                foreach (var src in currentLinked)
                    currentIds.Add((string)src["id"]);

                // Add new links
                foreach (string id in selectedIds)
                {
                    if (!currentIds.Contains(id))
                        Services.SchemaGraphService.LinkSourceToSolution(slnPath, id);
                }

                // Remove unlinked
                foreach (string id in currentIds)
                {
                    if (!selectedIds.Contains(id))
                        Services.SchemaGraphService.UnlinkSourceFromSolution(slnPath, id);
                }

                SendSchemaSourcesForTab(tab);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] ApplySelection error: " + ex.Message);
            }
        }

        private void HandleIndexSource(TerminalTab tab, string sourceId)
        {
            // Run indexing on a background thread to avoid blocking UI
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string result = Services.SchemaGraphService.IndexSource(sourceId);
                    bool isError = result != null && result.StartsWith("Error");

                    // Get updated status
                    var source = Services.SchemaGraphService.GetSource(sourceId);
                    string type = source != null ? (string)source["type"] : "";
                    string connInfo = source != null ? (string)source["connectionInfo"] : "{}";
                    var status = Services.SchemaGraphService.GetSourceStatus(sourceId, type, connInfo);

                    // Build status JSON
                    string statusJson;
                    if (isError)
                    {
                        statusJson = "{\"error\":\"" + EscJson(result) + "\"}";
                    }
                    else
                    {
                        int tCount = status.ContainsKey("tableCount") ? (int)status["tableCount"] : 0;
                        string lastIdx = status.ContainsKey("lastIndexed") ? (string)status["lastIndexed"] : null;
                        statusJson = string.Format("{{\"tableCount\":{0},\"lastIndexed\":{1}}}",
                            tCount, lastIdx != null ? "\"" + EscJson(lastIdx) + "\"" : "null");
                    }

                    // Send back to UI on UI thread
                    if (!IsDisposed && tab.SchemaSourcesView != null)
                    {
                        string sid = sourceId;
                        string sj = statusJson;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (tab.SchemaSourcesView != null)
                                    tab.SchemaSourcesView.SetIndexStatus(sid, sj);
                            }));
                        }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AssistantChatControl] IndexSource error: " + ex.Message);
                    if (!IsDisposed && tab.SchemaSourcesView != null)
                    {
                        string sid = sourceId;
                        string msg = ex.Message;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (tab.SchemaSourcesView != null)
                                    tab.SchemaSourcesView.SetIndexStatus(sid, "{\"error\":\"" + EscJson(msg) + "\"}");
                            }));
                        }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    }
                }
            });
        }

        private void HandleSetSolutionRepo(TerminalTab tab, string data)
        {
            try
            {
                string slnPath = tab.SolutionPath ?? tab.WorkingDirectory ?? "";
                if (string.IsNullOrEmpty(slnPath)) return;
                string accountId = ExtractJsonField(data, "accountId");
                string repoName = ExtractJsonField(data, "repoName");
                Services.SchemaGraphService.SetSolutionRepo(slnPath, accountId, repoName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] SetSolutionRepo error: " + ex.Message);
            }
        }

        private void HandleBrowseFile(TerminalTab tab, string data)
        {
            try
            {
                string editId = ExtractJsonField(data, "editId") ?? "new";
                string type = ExtractJsonField(data, "type") ?? "dctx";

                if (type == "dctx")
                {
                    using (var dlg = new OpenFileDialog())
                    {
                        dlg.Filter = "Clarion Dictionary (*.dctx)|*.dctx|All files (*.*)|*.*";
                        dlg.Title = "Select Clarion Dictionary";
                        if (dlg.ShowDialog() == DialogResult.OK && tab.SchemaSourcesView != null)
                            tab.SchemaSourcesView.SendBrowseResult(dlg.FileName, editId);
                    }
                }
                else if (type == "sqlite")
                {
                    using (var dlg = new OpenFileDialog())
                    {
                        dlg.Filter = "SQLite Database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*";
                        dlg.Title = "Select SQLite Database";
                        if (dlg.ShowDialog() == DialogResult.OK && tab.SchemaSourcesView != null)
                            tab.SchemaSourcesView.SendBrowseResult(dlg.FileName, editId);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] BrowseFile error: " + ex.Message);
            }
        }

        private void HandleTestConnection(TerminalTab tab, string data)
        {
            string type = ExtractJsonField(data, "type") ?? "";
            string connInfo = ExtractJsonField(data, "connectionInfo") ?? "{}";

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool success = false;
                string message;
                try
                {
                    if (type == "mssql")
                    {
                        string connStr = Services.SchemaGraphService.BuildMssqlConnectionString(connInfo);
                        using (var conn = new System.Data.SqlClient.SqlConnection(connStr))
                        {
                            conn.Open();
                            message = "Connected to " + conn.Database;
                            success = true;
                        }
                    }
                    else if (type == "postgres")
                    {
                        string connStr = Services.SchemaGraphService.BuildPostgresConnectionString(connInfo);
                        var asm = System.Reflection.Assembly.Load("Npgsql");
                        var connType = asm.GetType("Npgsql.NpgsqlConnection");
                        using (var conn = (System.Data.Common.DbConnection)Activator.CreateInstance(connType, connStr))
                        {
                            conn.Open();
                            message = "Connected to " + conn.Database;
                            success = true;
                        }
                    }
                    else
                    {
                        message = "Test not supported for type: " + type;
                    }
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                }

                if (!IsDisposed && tab.SchemaSourcesView != null)
                {
                    bool s = success;
                    string m = message;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (tab.SchemaSourcesView != null)
                                tab.SchemaSourcesView.SendMessage(
                                    "{\"type\":\"testConnectionResult\",\"success\":" + (s ? "true" : "false") +
                                    ",\"message\":\"" + EscJson(m) + "\"}");
                        }));
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            });
        }

        private const string PasswordPlaceholder = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";

        /// <summary>Replace password value with placeholder for safe display in WebView.</summary>
        private static string MaskPassword(string connectionInfoJson)
        {
            if (string.IsNullOrEmpty(connectionInfoJson)) return connectionInfoJson;
            string password = ExtractJsonField(connectionInfoJson, "password");
            if (string.IsNullOrEmpty(password)) return connectionInfoJson;
            // Replace the password value with a placeholder
            return connectionInfoJson.Replace("\"password\":\"" + password.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
                "\"password\":\"" + PasswordPlaceholder + "\"");
        }

        /// <summary>If the password is the placeholder, preserve the existing stored password.</summary>
        private static string RestorePasswordIfPlaceholder(string newConnInfo, string sourceId)
        {
            string newPassword = ExtractJsonField(newConnInfo, "password");
            if (newPassword != PasswordPlaceholder) return newConnInfo;

            // Get existing password from stored source
            var existing = Services.SchemaGraphService.GetSource(sourceId);
            if (existing == null) return newConnInfo;
            string existingConnInfo = (string)existing["connectionInfo"];
            string existingPassword = ExtractJsonField(existingConnInfo, "password");
            if (string.IsNullOrEmpty(existingPassword)) return newConnInfo;

            return newConnInfo.Replace("\"password\":\"" + PasswordPlaceholder + "\"",
                "\"password\":\"" + existingPassword.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        }

        private static string ExtractJsonField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pattern = "\"" + key + "\":";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += pattern.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == '"')
            {
                idx++;
                var sb = new System.Text.StringBuilder();
                while (idx < json.Length)
                {
                    char c = json[idx];
                    if (c == '\\' && idx + 1 < json.Length) { sb.Append(json[idx + 1]); idx += 2; continue; }
                    if (c == '"') break;
                    sb.Append(c);
                    idx++;
                }
                return sb.ToString();
            }
            int start = idx;
            while (idx < json.Length && json[idx] != ',' && json[idx] != '}') idx++;
            return json.Substring(start, idx - start).Trim();
        }

        #endregion

        #region Indexing

        public void RunIndex(bool incremental)
        {
            if (string.IsNullOrEmpty(_currentSlnPath) || !File.Exists(_currentSlnPath))
            {
                MessageBox.Show("Please select a solution first.", "Index", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _header.SetIndexButtonsEnabled(false);
            _header.SetIndexStatus(incremental ? "Updating..." : "Indexing...", "active");

            string slnPath = _currentSlnPath;

            // Build library paths from RED file .inc search paths
            List<string> libPaths = null;
            if (_redFileService != null)
            {
                var incPaths = _redFileService.GetSearchPaths(".inc");
                if (incPaths.Count > 0)
                    libPaths = incPaths;
            }

            _header.ClearIndexLog();

            var worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += (s, e) =>
            {
                string dbPath = Path.Combine(
                    Path.GetDirectoryName(slnPath),
                    Path.GetFileNameWithoutExtension(slnPath) + ".codegraph.db");

                var db = new ClarionCodeGraph.Graph.CodeGraphDatabase();
                db.Open(dbPath);

                var indexer = new ClarionCodeGraph.Graph.CodeGraphIndexer(db);
                indexer.OnProgress += msg =>
                {
                    ((BackgroundWorker)s).ReportProgress(0, msg);
                };
                var result = indexer.IndexSolution(slnPath, incremental, libPaths);
                db.Close();
                e.Result = result;
            };
            worker.ProgressChanged += (s, e) =>
            {
                string msg = e.UserState as string;
                if (msg != null)
                    _header.AppendIndexLog(msg);
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                _header.SetIndexButtonsEnabled(true);
                UpdateIndexStatus();

                if (e.Error != null)
                    _header.SetIndexStatus("Error: " + e.Error.Message, "error");
            };
            worker.RunWorkerAsync();
        }

        private System.Windows.Forms.TextBox _logTextBox;

        private void ShowIndexLog()
        {
            if (_logForm != null && !_logForm.IsDisposed)
            {
                RefreshLogContent();
                _logForm.BringToFront();
                return;
            }

            string slnName = !string.IsNullOrEmpty(_currentSlnPath)
                ? Path.GetFileNameWithoutExtension(_currentSlnPath)
                : "No solution";

            var headerLabel = new Label
            {
                Text = "CodeGraph log for: " + slnName,
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            _logTextBox = new System.Windows.Forms.TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Cascadia Code", 9f)
            };

            if (_isDarkTheme)
            {
                headerLabel.BackColor = Color.FromArgb(40, 40, 58);
                headerLabel.ForeColor = Color.FromArgb(137, 180, 250);
                _logTextBox.BackColor = Color.FromArgb(30, 30, 46);
                _logTextBox.ForeColor = Color.FromArgb(166, 173, 200);
            }

            _logForm = new Form
            {
                Text = "CodeGraph Activity Log",
                Width = 600,
                Height = 350,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.SizableToolWindow
            };
            _logForm.Controls.Add(_logTextBox);
            _logForm.Controls.Add(headerLabel);

            // Center on this control's screen position
            var screenBounds = RectangleToScreen(ClientRectangle);
            _logForm.Location = new Point(
                screenBounds.Left + (screenBounds.Width - _logForm.Width) / 2,
                screenBounds.Top + (screenBounds.Height - _logForm.Height) / 2);

            _logForm.FormClosed += (s, ev) =>
            {
                _logTextBox = null;
                _logForm = null;
            };

            _header.LogLineAppended += OnLogLineAppended;

            RefreshLogContent();
            _logForm.Show(FindForm());
        }

        private void RefreshLogContent()
        {
            if (_logTextBox == null || _logTextBox.IsDisposed) return;
            var lines = _header.GetLogLines();
            _logTextBox.Text = lines.Length > 0 ? string.Join(Environment.NewLine, lines) : "(No log entries)";
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
        }

        private void OnLogLineAppended(object sender, string line)
        {
            if (_logTextBox == null || _logTextBox.IsDisposed) return;
            if (_logTextBox.InvokeRequired)
            {
                _logTextBox.BeginInvoke((Action<object, string>)OnLogLineAppended, sender, line);
                return;
            }
            if (_logTextBox.TextLength > 0)
                _logTextBox.AppendText(Environment.NewLine);
            _logTextBox.AppendText(line);
        }

        #endregion

        #region Settings

        private void OnSplitterMoved(object sender, SplitterEventArgs e)
        {
            _settings.Set("Header.Height", _header.Height.ToString());
        }

        private void OnThemeChanged(string theme)
        {
            _isDarkTheme = theme != "light";
            _settings.Set("Theme", _isDarkTheme ? "dark" : "light");
            ApplyThemeColors();
            _header.SetTheme(_isDarkTheme);
            _homeView.SetTheme(_isDarkTheme);
            foreach (var tab in _tabManager.Tabs)
            {
                if (tab.Renderer != null) tab.Renderer.SetTheme(_isDarkTheme);
                if (tab.SchemaSourcesView != null) tab.SchemaSourcesView.SetTheme(_isDarkTheme);
                if (tab.ContentControl is CreateClassWebView ccv) ccv.SetTheme(_isDarkTheme);
            }
            Terminal.DiffViewContent.ApplyThemeToAll(_isDarkTheme);
            _diffService?.SetTheme(_isDarkTheme);
        }

        private void ApplyThemeColors()
        {
            BackColor = _isDarkTheme ? Color.FromArgb(12, 12, 12) : Color.White;
            _splitter.BackColor = _isDarkTheme ? Color.FromArgb(49, 50, 68) : Color.FromArgb(204, 208, 218);
            if (_tabStrip != null) _tabManager?.ApplyTheme(_isDarkTheme);
            if (_contentArea != null) _contentArea.BackColor = _isDarkTheme ? Color.FromArgb(12, 12, 12) : Color.White;
            if (_lspStatusBar != null) _lspStatusBar.ApplyTheme(_isDarkTheme);
            if (_diagForm != null) _diagForm.ApplyTheme(_isDarkTheme);
        }

        private float GetFontSize()
        {
            string val = _settings.Get("Claude.FontSize");
            float size;
            if (!string.IsNullOrEmpty(val) && float.TryParse(val, out size))
                return Math.Max(6f, Math.Min(32f, size));
            return 14f;
        }

        private string GetFontFamily()
        {
            string val = _settings.Get("Claude.FontFamily");
            return string.IsNullOrEmpty(val) ? "Cascadia Mono" : val;
        }

        private string GetWorkingDirectory() => GetWorkingDirectoryFor("Claude");

        private string GetWorkingDirectoryFor(string backend)
        {
            // Per-backend override wins; fall back to Claude's value (the original
            // single-backend setting) so legacy installs don't break; last-resort is
            // the user's profile folder. Backend-keyed lookup so any new backend
            // (Codex, future ones) is picked up automatically without editing this.
            string primaryKey = (backend ?? "Claude") + ".WorkingDirectory";
            string dir = _settings.Get(primaryKey);
            if (string.IsNullOrEmpty(dir) && backend != "Claude")
                dir = _settings.Get("Claude.WorkingDirectory");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                return dir;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void OnSettings(object sender, EventArgs e)
        {
            var parent = FindForm();
            var dlg = new ClaudeChatSettingsDialog(_settings, _isDarkTheme);

            dlg.SettingsSaved += (d) =>
            {
                foreach (var tab in _tabManager.Tabs)
                {
                    if (tab.Renderer != null)
                    {
                        tab.Renderer.SetFontSize(d.FontSize);
                        tab.Renderer.SetFontFamily(d.FontFamily);
                    }
                }
                if (d.ThemeChanged)
                    OnThemeChanged(d.IsDarkTheme ? "dark" : "light");
            };

            dlg.FormClosed += (s2, e2) =>
            {
                if (parent != null) parent.Enabled = true;
                SendGitHubAccountsToHome(); // Refresh home dropdown after settings changes
                SendDefaultProjectFolderToHome(); // COM.ProjectsFolder may have changed
                dlg.Dispose();
            };

            // Show non-modal with parent disabled — WebView2 cannot init inside ShowDialog()
            if (parent != null) parent.Enabled = false;
            dlg.Show(parent);
        }

        private void OnCheatSheet()
        {
            var parent = FindForm();
            var dlg = new Dialogs.CheatSheetDialog(_isDarkTheme);

            dlg.FormClosed += (s, e2) =>
            {
                if (parent != null) parent.Enabled = true;
                dlg.Dispose();
            };

            if (parent != null) parent.Enabled = false;
            dlg.Show(parent);
        }

        private void OnDocs()
        {
            string basePath = Path.GetDirectoryName(GetType().Assembly.Location);
            string docsPath = Path.Combine(basePath, "docs", "ClarionAssistant-Guide.html");
            if (File.Exists(docsPath))
            {
                System.Diagnostics.Process.Start(docsPath);
            }
            else
            {
                MessageBox.Show("Documentation file not found:\n" + docsPath,
                    "Documentation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnCreateCom(object sender, EventArgs e)
        {
            string comFolder = _settings.Get("COM.ProjectsFolder");
            if (string.IsNullOrEmpty(comFolder) || !Directory.Exists(comFolder))
            {
                MessageBox.Show(
                    "Please configure the COM Projects Folder in Settings first.",
                    "Create COM Control",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                OnSettings(sender, e);
                // Re-read after settings dialog
                comFolder = _settings.Get("COM.ProjectsFolder");
                if (string.IsNullOrEmpty(comFolder) || !Directory.Exists(comFolder))
                    return;
            }

            var active = _tabManager.ActiveTab;
            if (active == null || active.IsHome || active.Terminal == null || !active.Terminal.IsRunning)
            {
                MessageBox.Show("Claude is not running. Please open a terminal first.",
                    "Create COM Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string safeFolder = comFolder.Replace("'", "''");
            string command = "/ClarionCOM Create a new COM control in '" + safeFolder + "'\r";
            active.Terminal.Write(Encoding.UTF8.GetBytes(command));
        }

        private void OnEvaluateCode(object sender, EventArgs e)
        {
            var active = _tabManager.ActiveTab;
            if (active != null && !active.IsHome && active.Terminal != null && active.Terminal.IsRunning)
            {
                // Terminal already running — send command directly
                active.Terminal.Write(Encoding.UTF8.GetBytes("/evaluate-code\r"));
                return;
            }

            // No terminal — open one with /evaluate-code as startup command
            var renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
            var tab = _tabManager.CreateTerminalTab("Evaluate Code", renderer);
            tab.StartupCommand = "/evaluate-code";
            renderer.DataReceived += data => OnTabRendererDataReceived(tab, data);
            renderer.TerminalResized += (s, ev) => OnTabRendererResized(tab, ev);
            renderer.Initialized += (s, ev) => OnTabRendererInitialized(tab);
            AttachSchemaSourcesView(tab);
            _tabManager.ActivateTab(tab.Id);
        }

        #endregion

        #region Create Class

        private void OnCreateClass()
        {
            var view = new CreateClassWebView { Dock = DockStyle.Fill };
            view.SetTheme(_isDarkTheme);
            var tab = _tabManager.CreateContentTab("Create Class", view);

            view.ActionReceived += (s, e) => OnCreateClassAction(tab, view, e);
            view.Initialized += (s, ev) => SendClassModelsToView(view);

            _tabManager.ActivateTab(tab.Id);
        }

        private void SendClassModelsToView(CreateClassWebView view)
        {
            try
            {
                string folder = GetClassModelsFolder();
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var incPath in Directory.GetFiles(folder, "*.inc"))
                {
                    string baseName = Path.GetFileNameWithoutExtension(incPath);
                    string clwPath = Path.Combine(folder, baseName + ".clw");
                    if (!File.Exists(clwPath)) continue;
                    if (!first) sb.Append(",");
                    sb.Append("{\"name\":\"").Append(JsonEscape(baseName))
                      .Append("\",\"incFile\":\"").Append(JsonEscape(baseName + ".inc"))
                      .Append("\",\"clwFile\":\"").Append(JsonEscape(baseName + ".clw")).Append("\"}");
                    first = false;
                }
                sb.Append("]");

                string outputFolder = _settings.Get("Class.OutputFolder") ?? "";
                view.SetModels(sb.ToString(), outputFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] SendClassModels error: " + ex.Message);
            }
        }

        private void OnCreateClassAction(TerminalTab tab, CreateClassWebView view, CreateClassActionEventArgs e)
        {
            switch (e.Action)
            {
                case "createClassReady":
                    SendClassModelsToView(view);
                    break;

                case "previewModel":
                    HandlePreviewModel(view, e.Data);
                    break;

                case "createClass":
                    HandleCreateClass(tab, view, e.Data);
                    break;

                case "browseOutputFolder":
                    using (var dlg = new FolderBrowserDialog())
                    {
                        dlg.Description = "Select Class Output Folder";
                        string cur = _settings.Get("Class.OutputFolder");
                        if (!string.IsNullOrEmpty(cur) && Directory.Exists(cur))
                            dlg.SelectedPath = cur;
                        if (dlg.ShowDialog() == DialogResult.OK)
                        {
                            _settings.Set("Class.OutputFolder", dlg.SelectedPath);
                            view.SendBrowseResult(dlg.SelectedPath);
                        }
                    }
                    break;

                case "cancel":
                    _tabManager.CloseTab(tab.Id);
                    break;
            }
        }

        private void HandlePreviewModel(CreateClassWebView view, string modelName)
        {
            try
            {
                string folder = GetClassModelsFolder();
                string incPath = Path.Combine(folder, modelName + ".inc");
                string clwPath = Path.Combine(folder, modelName + ".clw");

                string incContent = File.Exists(incPath) ? File.ReadAllText(incPath) : "";
                string clwContent = File.Exists(clwPath) ? File.ReadAllText(clwPath) : "";

                view.SendPreviewResult(incContent, clwContent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] PreviewModel error: " + ex.Message);
                view.SendPreviewResult("Error reading file: " + ex.Message, "");
            }
        }

        private void HandleCreateClass(TerminalTab createTab, CreateClassWebView view, string dataJson)
        {
            try
            {
                // Parse JSON data
                string modelName = ExtractJsonVal(dataJson, "modelName");
                string newClassName = ExtractJsonVal(dataJson, "newClassName");
                string outputFolder = ExtractJsonVal(dataJson, "outputFolder");

                if (string.IsNullOrEmpty(modelName) || string.IsNullOrEmpty(newClassName) || string.IsNullOrEmpty(outputFolder))
                {
                    view.SendCreateResult(false, "Missing required fields.", newClassName);
                    return;
                }

                // Ensure output folder exists
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                string modelsDir = GetClassModelsFolder();
                string srcInc = Path.Combine(modelsDir, modelName + ".inc");
                string srcClw = Path.Combine(modelsDir, modelName + ".clw");
                string dstInc = Path.Combine(outputFolder, newClassName + ".inc");
                string dstClw = Path.Combine(outputFolder, newClassName + ".clw");

                // Check if files already exist
                if (File.Exists(dstInc) || File.Exists(dstClw))
                {
                    view.SendCreateResult(false,
                        "File already exists: " + (File.Exists(dstInc) ? dstInc : dstClw), newClassName);
                    return;
                }

                // Read model files, replace class name, write new files
                string incContent = File.ReadAllText(srcInc);
                incContent = incContent.Replace(modelName, newClassName);

                string clwContent = File.ReadAllText(srcClw);
                clwContent = clwContent.Replace(modelName, newClassName);
                // Also replace INCLUDE reference to .INC file
                clwContent = clwContent.Replace(
                    "INCLUDE('" + newClassName + ".INC')",
                    "INCLUDE('" + newClassName + ".INC')");

                File.WriteAllText(dstInc, incContent);
                File.WriteAllText(dstClw, clwContent);

                // Save output folder as default for next time
                _settings.Set("Class.OutputFolder", outputFolder);

                // Open both files in the IDE editor
                try
                {
                    _editorService.NavigateToFileAndLine(dstInc, 1);
                    _editorService.NavigateToFileAndLine(dstClw, 1);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OpenFile error: " + ex.Message);
                }

                // Send success result to the Create Class page
                view.SendCreateResult(true, "Class created successfully!", newClassName);

                // Open a terminal tab for working with the new class
                var renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
                var termTab = _tabManager.CreateTerminalTab(newClassName, renderer);
                termTab.WorkingDirectory = outputFolder;
                termTab.StartupCommand = "I just created a new Clarion class " + newClassName
                    + " (.inc and .clw are open in the editor). Help me develop it.";
                renderer.DataReceived += data => OnTabRendererDataReceived(termTab, data);
                renderer.TerminalResized += (s, ev) => OnTabRendererResized(termTab, ev);
                renderer.Initialized += (s, ev) => OnTabRendererInitialized(termTab);
                AttachSchemaSourcesView(termTab);
                _tabManager.ActivateTab(termTab.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] CreateClass error: " + ex.Message);
                view.SendCreateResult(false, "Error: " + ex.Message, "");
            }
        }

        private static string GetClassModelsFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", "ClassModels");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                try
                {
                    string assemblyDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string bundled = Path.Combine(assemblyDir, "Terminal", "ClassModels");
                    if (Directory.Exists(bundled))
                    {
                        foreach (var f in Directory.GetFiles(bundled))
                            File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), false);
                    }
                }
                catch { }
            }
            return folder;
        }

        /// <summary>Simple JSON string escape for building messages.</summary>
        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        /// <summary>Extract a string value from a simple JSON object.</summary>
        private static string ExtractJsonVal(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            var sb = new StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length) { sb.Append(json[idx + 1]); idx += 2; continue; }
                if (c == '"') break;
                sb.Append(c);
                idx++;
            }
            return sb.ToString();
        }

        #endregion

        #region MCP Server (auto-start)

        private void StartMcpServer()
        {
            _mcpServer = new McpServer(this);
            _toolRegistry = new McpToolRegistry(_editorService, _parser);

            // Give the tool registry a reference back so it can access solution context and run indexing
            _toolRegistry.SetChatControl(this);

            // Set up diff viewer service
            _diffService = new DiffService();
            _toolRegistry.SetDiffService(_diffService);

            // Set up standalone knowledge/memory service
            try
            {
                _knowledgeService = new Services.KnowledgeService();
                _toolRegistry.SetKnowledgeService(_knowledgeService);
            }
            catch { /* non-fatal: knowledge tools won't be available */ }

            // Set up instance coordination for multi-IDE awareness
            try
            {
                _instanceCoord = new Services.InstanceCoordinationService();
                _toolRegistry.SetInstanceCoordination(_instanceCoord);
                _instanceCoord.Start();
            }
            catch { /* non-fatal: coordination tools won't be available */ }

            _mcpServer.SetToolRegistry(_toolRegistry);

            _mcpServer.OnStatusChanged += (running, port) =>
            {
                UpdateStatus(running ? "MCP: port " + port : "MCP stopped");
            };

            _mcpServer.OnError += error =>
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] MCP error: " + error);
            };

            // Configure MultiTerminal integration
            bool mtEnabled = (_settings.Get("MultiTerminal.Enabled") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase)
                          || (_settings.Get("MultiTerminal.Enabled") == null && Dialogs.ClaudeChatSettingsDialog.IsMultiTerminalAvailable());
            _mcpServer.IncludeMultiTerminal = mtEnabled;
            _mcpServer.MultiTerminalMcpPath = Dialogs.ClaudeChatSettingsDialog.GetMultiTerminalMcpPath();

            if (_mcpServer.Start())
            {
                _mcpConfigPath = _mcpServer.WriteMcpConfigFile();
                string status = "MCP: port " + _mcpServer.Port + " | " + _toolRegistry.GetToolCount() + " tools";
                if (mtEnabled) status += " | MT";
                UpdateStatus(status);
            }
            else
            {
                UpdateStatus("MCP failed to start");
            }

            // Periodic UI-thread timer to refresh instance state (app, procedure, peers)
            if (_instanceCoord != null)
            {
                _instanceStateTimer = new System.Windows.Forms.Timer { Interval = 10000 };
                _instanceStateTimer.Tick += (s, ev) => { PollForSolutionChange(); UpdateInstanceState(); };
                _instanceStateTimer.Start();
            }

            // Poll for Claude Code status line data (model, context, rate limits, git)
            _statusLineTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _statusLineTimer.Tick += (s, ev) => PollStatusLine();
            _statusLineTimer.Start();

            // Poll LSP diagnostics for the header pill (2s interval, cache-only reads)
            _lspUiTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _lspUiTimer.Tick += (s, ev) => PollLspUi();
            _lspUiTimer.Start();
        }

        #endregion

        #region Terminal Lifecycle

        private void OnTabRendererInitialized(TerminalTab tab)
        {
            System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OnTabRendererInitialized for tab " + tab.Id + " (" + tab.Name + ")");
            if (tab.Renderer == null)
            {
                System.Diagnostics.Debug.WriteLine("[AssistantChatControl] OnTabRendererInitialized: renderer is null!");
                return;
            }
            tab.Renderer.SetTheme(_isDarkTheme);
            tab.Renderer.SetFontSize(GetFontSize());
            tab.Renderer.SetFontFamily(GetFontFamily());
            tab.Renderer.FontSizeChangedByUser += OnFontSizeChangedByWheel;
            LaunchAssistantForTab(tab);
            tab.Renderer.Focus();
        }

        private void OnFontSizeChangedByWheel(object sender, float size)
        {
            _settings.Set("Claude.FontSize", size.ToString());
        }

        private void LaunchAssistantForTab(TerminalTab tab)
        {
            // Backend selection order:
            //   1. tab.RequestedBackend — set by dashboard dropdown at dispatch time
            //   2. _pendingLaunchBackend — fallback path for tabs created by handlers
            //      that don't set the tab field directly before async init (e.g. project
            //      open flows that hand off via ActivateTab)
            //   3. Saved default in settings (Assistant.Backend)
            //   4. Claude (first-run fallback)
            if (tab.RequestedBackend == null && _pendingLaunchBackend != null)
            {
                tab.RequestedBackend = _pendingLaunchBackend;
            }
            _pendingLaunchBackend = null;

            string backend = tab.RequestedBackend
                ?? _settings.Get("Assistant.Backend")
                ?? "Claude";

            // Annotate the tab with the backend abbreviation so the tab strip
            // makes it obvious at a glance which assistant is driving each tab.
            // Idempotent: strips any prior suffix before appending.
            _tabManager.RenameTab(tab, ApplyBackendSuffix(tab.Name, backend));

            if (string.Equals(backend, "Copilot", StringComparison.OrdinalIgnoreCase))
                LaunchCopilotForTab(tab);
            else if (string.Equals(backend, "Codex", StringComparison.OrdinalIgnoreCase))
                LaunchCodexForTab(tab);
            else
                LaunchClaudeForTab(tab);
        }

        /// <summary>Short backend abbreviation for tab labels.</summary>
        private static string BackendSuffix(string backend)
        {
            if (string.Equals(backend, "Claude",  StringComparison.OrdinalIgnoreCase)) return "CC";
            if (string.Equals(backend, "Copilot", StringComparison.OrdinalIgnoreCase)) return "CP";
            if (string.Equals(backend, "Codex",   StringComparison.OrdinalIgnoreCase)) return "CO";
            return "";
        }

        /// <summary>Append the backend's 2-letter suffix to the tab name, stripping
        /// any prior known suffix first so relaunches don't stack "CC CP CC".</summary>
        private static string ApplyBackendSuffix(string currentName, string backend)
        {
            string suffix = BackendSuffix(backend);
            if (string.IsNullOrEmpty(suffix) || string.IsNullOrEmpty(currentName))
                return currentName;

            string stripped = currentName;
            foreach (string prior in new[] { " CC", " CP", " CO" })
            {
                if (stripped.EndsWith(prior, StringComparison.Ordinal))
                {
                    stripped = stripped.Substring(0, stripped.Length - prior.Length);
                    break;
                }
            }
            return stripped + " " + suffix;
        }

        private void LaunchClaudeForTab(TerminalTab tab)
        {
            System.Diagnostics.Debug.WriteLine("[LaunchClaude] ENTER tab=" + tab.Id + ", AssistantLaunched=" + tab.AssistantLaunched);
            if (tab.AssistantLaunched) return;
            tab.AssistantLaunched = true;
            tab.AssistantBackend = "Claude";

            try
            {
                var ctx = PrepareBackendLaunch(tab, "Claude", requirePwsh7: false);
                if (ctx == null) return;
                System.Diagnostics.Debug.WriteLine("[LaunchClaude] pwsh=" + ctx.Pwsh + ", workDir=" + ctx.WorkDir);

                var built = BuildClaudeCommand(tab, ctx);
                if (built == null) return; // abort already handled inside the builder

                StartTabTerminal(tab, ctx, built.Cmd, "Claude Code running", "[LaunchClaude]");

                // Clean up temp prompt files after Claude Code has read them
                if (built.TempFiles.Count > 0)
                {
                    var filesToDelete = new System.Collections.Generic.List<string>(built.TempFiles);
                    System.Threading.Tasks.Task.Delay(30000).ContinueWith(_ =>
                    {
                        foreach (var f in filesToDelete)
                            try { File.Delete(f); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LaunchClaude] EXCEPTION: " + ex);
            }
        }

        private void LaunchCopilotForTab(TerminalTab tab)
        {
            System.Diagnostics.Debug.WriteLine("[LaunchCopilot] ENTER tab=" + tab.Id + ", AssistantLaunched=" + tab.AssistantLaunched);
            if (tab.AssistantLaunched) return;
            tab.AssistantLaunched = true;
            tab.AssistantBackend = "Copilot";

            try
            {
                var ctx = PrepareBackendLaunch(tab, "Copilot", requirePwsh7: true);
                if (ctx == null) return;

                var built = BuildCopilotCommand(tab, ctx);
                if (built == null) return; // abort already handled inside the builder

                StartTabTerminal(tab, ctx, built.Cmd, "Copilot CLI running", "[LaunchCopilot]");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LaunchCopilot] EXCEPTION: " + ex);
            }
        }

        private void LaunchCodexForTab(TerminalTab tab)
        {
            System.Diagnostics.Debug.WriteLine("[LaunchCodex] ENTER tab=" + tab.Id + ", AssistantLaunched=" + tab.AssistantLaunched);
            if (tab.AssistantLaunched) return;
            tab.AssistantLaunched = true;
            tab.AssistantBackend = "Codex";

            try
            {
                var ctx = PrepareBackendLaunch(tab, "Codex", requirePwsh7: false);
                if (ctx == null) return;

                var built = BuildCodexCommand(tab, ctx);
                if (built == null) return; // abort already handled inside the builder

                StartTabTerminal(tab, ctx, built.Cmd, "Codex CLI running", "[LaunchCodex]");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LaunchCodex] EXCEPTION: " + ex);
            }
        }

        /// <summary>Shared launch state threaded from the prepare helper to the
        /// per-backend command builder and the start helper.</summary>
        private class LaunchContext
        {
            public string Pwsh;
            public string WorkDir;
            public string SafeWorkDir;
            public string EnvSetup;
        }

        /// <summary>Per-backend builder output. Cmd is the pwsh inner command;
        /// TempFiles is optional (Claude writes per-tab prompt files it wants
        /// deleted after the CLI reads them).</summary>
        private class BuiltBackendCommand
        {
            public string Cmd;
            public System.Collections.Generic.List<string> TempFiles = new System.Collections.Generic.List<string>();
        }

        /// <summary>
        /// Shared pre-launch scaffolding: create ConPtyTerminal + wire events,
        /// locate pwsh (optionally require PS7), resolve per-backend workDir,
        /// and return a LaunchContext for the builder to consume. Returns null
        /// if pwsh is required-and-missing (handler shows a message + calls
        /// AbortLaunch before returning).
        /// </summary>
        private LaunchContext PrepareBackendLaunch(TerminalTab tab, string backendName, bool requirePwsh7)
        {
            tab.Terminal = new ConPtyTerminal();
            tab.Terminal.DataReceived += data => OnTabTerminalDataReceived(tab, data);
            tab.Terminal.ProcessExited += (s, ev) => OnTabTerminalProcessExited(tab);

            string pwsh = FindPowerShell(requirePwsh: requirePwsh7);
            if (requirePwsh7 && (string.IsNullOrEmpty(pwsh) || !File.Exists(pwsh)))
            {
                UpdateStatus(backendName + " requires pwsh.exe");
                try
                {
                    MessageBox.Show(
                        "GitHub " + backendName + " CLI integration requires PowerShell 7 (pwsh.exe).\n\nInstall PowerShell 7 and try again.",
                        backendName + " CLI", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
                AbortLaunch(tab);
                return null;
            }

            string workDir = !string.IsNullOrEmpty(tab.WorkingDirectory) && Directory.Exists(tab.WorkingDirectory)
                ? tab.WorkingDirectory
                : GetWorkingDirectoryFor(backendName);

            return new LaunchContext
            {
                Pwsh = pwsh,
                WorkDir = workDir,
                SafeWorkDir = workDir.Replace("'", "''"),
                EnvSetup = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8; ",
            };
        }

        /// <summary>Reset tab state and dispose the half-initialized terminal
        /// created in PrepareBackendLaunch. Safe to call after any failure in
        /// the prepare or builder phases.</summary>
        private void AbortLaunch(TerminalTab tab)
        {
            tab.AssistantLaunched = false;
            tab.AssistantBackend = null;
            try { if (tab.Terminal != null) tab.Terminal.Dispose(); } catch { }
            tab.Terminal = null;
        }

        /// <summary>
        /// Wrap the backend-specific inner command in pwsh's <c>-Command "..."</c>
        /// shell, start ConPTY at the renderer's current grid size, and surface
        /// the status line. The <paramref name="logTag"/> is a short prefix
        /// (e.g. "[LaunchClaude]") used only for Debug.WriteLine context.
        /// </summary>
        private void StartTabTerminal(TerminalTab tab, LaunchContext ctx, string backendCmd, string statusLabel, string logTag)
        {
            string commandLine = $"\"{ctx.Pwsh}\" -NoLogo -ExecutionPolicy Bypass -NoExit -Command \"{ctx.EnvSetup}{backendCmd}\"";
            System.Diagnostics.Debug.WriteLine(logTag + " cols=" + tab.Renderer.VisibleCols + ", rows=" + tab.Renderer.VisibleRows);
            System.Diagnostics.Debug.WriteLine(logTag + " Starting ConPTY: " + commandLine.Substring(0, Math.Min(200, commandLine.Length)));
            tab.Terminal.Start(tab.Renderer.VisibleCols, tab.Renderer.VisibleRows, commandLine, ctx.WorkDir);
            System.Diagnostics.Debug.WriteLine(logTag + " ConPTY started OK");
            UpdateStatus("MCP: port " + (_mcpServer?.Port ?? 0) + " | " + statusLabel);
        }

        /// <summary>
        /// Claude-specific command assembly. Side effects: deploys CLAUDE.md to
        /// workDir, starts a knowledge-service session on the tab, writes per-tab
        /// temp prompt files (returned in TempFiles for deferred cleanup).
        /// Returns null if the user-configured Claude command fails validation;
        /// AbortLaunch is called in that case.
        /// </summary>
        private BuiltBackendCommand BuildClaudeCommand(TerminalTab tab, LaunchContext ctx)
        {
            string mcpArg = "";
            if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
            {
                string safePath = _mcpConfigPath.Replace("'", "''");
                mcpArg = $" --mcp-config '{safePath}'";
            }
            System.Diagnostics.Debug.WriteLine("[LaunchClaude] mcpConfigPath=" + _mcpConfigPath + ", mcpArg=" + mcpArg);

            DeployClaudeMd(ctx.WorkDir);

            if (_knowledgeService != null)
            {
                try { tab.SessionId = _knowledgeService.StartSession(ctx.WorkDir); }
                catch { }
            }

            string systemPromptExtra = BuildSystemPromptInjection(ctx.WorkDir);
            string initialPrompt = BuildInitialPrompt(ctx.WorkDir);
            System.Diagnostics.Debug.WriteLine("[LaunchClaude] prompts built");

            string tempDir = Path.Combine(Path.GetTempPath(), "ClarionAssistant");
            Directory.CreateDirectory(tempDir);

            string tabSuffix = tab.Id;
            string extraFlags = "";
            var tempFiles = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrEmpty(systemPromptExtra))
            {
                string promptFile = Path.Combine(tempDir, "system-prompt-extra-" + tabSuffix + ".md");
                File.WriteAllText(promptFile, systemPromptExtra, System.Text.Encoding.UTF8);
                extraFlags += $" --append-system-prompt-file '{promptFile.Replace("'", "''")}'";
                tempFiles.Add(promptFile);
            }

            string initialPromptFile = null;
            if (!string.IsNullOrEmpty(initialPrompt))
            {
                initialPromptFile = Path.Combine(tempDir, "initial-prompt-" + tabSuffix + ".txt");
                File.WriteAllText(initialPromptFile, initialPrompt, System.Text.Encoding.UTF8);
                tempFiles.Add(initialPromptFile);
            }

            string allowedTools = "mcp__clarion-assistant__*,Read,Edit,Write,Bash,Glob,Grep";
            if (_mcpServer != null && _mcpServer.IncludeMultiTerminal)
                allowedTools += ",mcp__multiterminal__*";
            // Allow the multiterminal-channel plugin's tools when it's loaded
            // via mcp-config — prefix is different than when loaded as a plugin.
            if (_mcpServer != null && _mcpServer.IncludeMultiTerminalChannel)
                allowedTools += ",mcp__multiterminal-channel__*";

            string pluginArg = "";
            string pluginDir = GetClarionAssistantPluginPath();
            if (pluginDir != null)
            {
                string safePluginDir = pluginDir.Replace("'", "''");
                pluginArg = $" --plugin-dir '{safePluginDir}'";
            }

            string colorfgbg = _isDarkTheme ? "$env:COLORFGBG='15;0'" : "$env:COLORFGBG='0;15'";

            // Pass the user's selected model via --model. Empty value means "no
            // override" — the CLI will use the account's default (Sonnet 4.6 on
            // Pro, Opus 4.7 on Max). Single-quote any apostrophes for safety.
            string claudeModelVal = (_settings.Get("Claude.Model") ?? "").Trim();
            string claudeModelFlag = string.IsNullOrEmpty(claudeModelVal)
                ? string.Empty
                : $" --model '{claudeModelVal.Replace("'", "''")}'";

            // Build the base invocation from the user-selected Claude command.
            // For bare "claude", resolve to the full path; for anything else,
            // tokenize and quote so shell metacharacters in settings can't
            // chain additional commands into the pwsh -Command payload.
            string claudeCmdRaw = _settings.GetDefaultClaudeCommand();
            string claudeBase;
            if (claudeCmdRaw == "claude")
            {
                string resolved = Services.ClaudeProcessManager.FindClaudePathStatic();
                claudeBase = resolved != null
                    ? "& " + Services.PwshCommandQuoter.QuoteLiteral(resolved)
                    : "claude";
            }
            else
            {
                try
                {
                    claudeBase = Services.PwshCommandQuoter.BuildInvocation(claudeCmdRaw);
                }
                catch (ArgumentException ex)
                {
                    UpdateStatus("Claude launch aborted: " + ex.Message);
                    AbortLaunch(tab);
                    return null;
                }
            }
            // Set CA tab ID so the statusline script can write per-tab status
            string tabEnv = $"$env:CLARIONASSISTANT_TAB='{tab.Id}'";

            // Compute the CA-prefixed agent name + stable docId for this tab and export
            // them so the multiterminal-channel MCP server (loaded via mcp-config) registers
            // with the MultiTerminal broker under the right identity.
            _caTabCounter++;
            string agentName = Services.CaAgentIdentity.NormalizeAgentName(tab.Name, _caTabCounter);
            string docId = Services.CaAgentIdentity.ComputeStableDocId(agentName);
            string safeAgentName = Services.CaAgentIdentity.EscapeForPowerShellSingleQuote(agentName);
            string safeDocId = Services.CaAgentIdentity.EscapeForPowerShellSingleQuote(docId);
            string channelEnv = $"$env:MULTITERMINAL_NAME='{safeAgentName}'; $env:MULTITERMINAL_DOC_ID='{safeDocId}'";
            System.Diagnostics.Debug.WriteLine(
                "[LaunchClaude] Channel identity: name=" + agentName + ", docId=" + docId);

            // Authorize the multiterminal-channel MCP server for inbound channel notifications.
            // Without this flag, mcp.notification('notifications/claude/channel') is silently ignored.
            // Using --dangerously-load-development-channels skips the interactive approval prompt,
            // which is appropriate for a controlled embedded environment where we control which servers load.
            string channelFlag = (_mcpServer != null && _mcpServer.IncludeMultiTerminalChannel)
                ? " --dangerously-load-development-channels server:multiterminal-channel"
                : "";
            // Auto-update Claude Code before launching if enabled in settings
            string updatePrefix = "";
            if ((_settings.Get("Claude.AutoUpdate") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                // Resolve claude path for the update command too
                string updateCmd = "claude";
                string resolvedUpdate = Services.ClaudeProcessManager.FindClaudePathStatic();
                if (resolvedUpdate != null)
                    updateCmd = "& '" + resolvedUpdate.Replace("'", "''") + "'";
                updatePrefix = $"Write-Host 'Checking for Claude Code updates...' -ForegroundColor Cyan; {updateCmd} update; ";
            }

            string claudeCmd = $"cd '{ctx.SafeWorkDir}'; $env:CLARION_ASSISTANT_EMBEDDED='1'; {tabEnv}; {channelEnv}; {colorfgbg}; {updatePrefix}{claudeBase}{mcpArg}{pluginArg}{claudeModelFlag} --strict-mcp-config{channelFlag} --allowedTools '{allowedTools}'{extraFlags}";

            if (initialPromptFile != null)
            {
                string safeFile = initialPromptFile.Replace("'", "''");
                claudeCmd += $" (Get-Content -Raw '{safeFile}')";
            }

            return new BuiltBackendCommand { Cmd = claudeCmd, TempFiles = tempFiles };
        }

        /// <summary>
        /// Copilot-specific command assembly. Side effects: writes per-session
        /// MCP config + AGENTS.md to the Copilot home dir. Returns null if the
        /// user-configured Copilot command or Copilot.ExtraFlags fails validation;
        /// AbortLaunch is called in that case.
        /// </summary>
        private BuiltBackendCommand BuildCopilotCommand(TerminalTab tab, LaunchContext ctx)
        {
            string copilotHome = GetCopilotHomeDir();
            string mcpConfig = null;
            try
            {
                if (_mcpServer != null)
                    mcpConfig = _mcpServer.WriteMcpConfigFile(copilotHome, Services.McpServer.McpConfigFormat.Copilot);
            }
            catch { }

            string instructionsDir = DeployCopilotInstructions(copilotHome);

            string safeCopilotHome = copilotHome.Replace("'", "''");
            string safeInstrDir = (instructionsDir ?? "").Replace("'", "''");
            string safeMcpConfig = (mcpConfig ?? "").Replace("'", "''");

            // Build the base invocation from the user-selected Copilot command.
            // For bare "copilot", resolve to the full path; for anything else,
            // tokenize and quote so shell metacharacters in settings can't
            // chain additional commands into the pwsh -Command payload.
            string copilotCmdRaw = _settings.GetDefaultCopilotCommand();
            string copilotBase;
            if (copilotCmdRaw == "copilot")
            {
                string resolved = Services.CopilotProcessManager.FindCopilotPathStatic();
                copilotBase = resolved != null
                    ? "& " + Services.PwshCommandQuoter.QuoteLiteral(resolved)
                    : "copilot";
            }
            else
            {
                try
                {
                    copilotBase = Services.PwshCommandQuoter.BuildInvocation(copilotCmdRaw);
                }
                catch (ArgumentException ex)
                {
                    UpdateStatus("Copilot launch aborted: " + ex.Message);
                    AbortLaunch(tab);
                    return null;
                }
            }

            // Copilot.ExtraFlags is user-controllable; tokenize + quote each
            // token so a settings value like "--verbose; Remove-Item ..." can't
            // break out of the pwsh payload.
            string extraFlagsRaw = _settings.Get("Copilot.ExtraFlags") ?? "";
            string extraFlags;
            try
            {
                string builtFlags = Services.PwshCommandQuoter.BuildFlags(extraFlagsRaw);
                extraFlags = string.IsNullOrEmpty(builtFlags) ? string.Empty : " " + builtFlags;
            }
            catch (ArgumentException ex)
            {
                UpdateStatus("Copilot launch aborted: " + ex.Message);
                AbortLaunch(tab);
                return null;
            }

            string copilotModelVal = (_settings.Get("Copilot.Model") ?? "").Trim();
            string modelFlag = string.IsNullOrEmpty(copilotModelVal)
                ? string.Empty
                : $" --model '{copilotModelVal.Replace("'", "''")}'";

            // Codex backend uses the same --model flag pattern; helper is defined
            // below for the future Codex launcher (not yet wired). Empty value or
            // "Auto" means: let the CLI pick.

            string permissionMode = (_settings.Get("Copilot.PermissionMode") ?? "prompt").Trim();
            string permissionFlags = string.Equals(permissionMode, "allow", StringComparison.OrdinalIgnoreCase)
                ? " --allow-all-tools"
                : string.Empty;
            // The '@' prefix on the path tells Copilot CLI to load the MCP
            // config from a file rather than parse the argument as inline
            // JSON. Copilot's own docs are quiet on this sigil; keep this
            // comment if a future CLI upgrade changes the convention.
            string mcpConfigArg = string.IsNullOrEmpty(safeMcpConfig)
                ? string.Empty
                : $" --additional-mcp-config '@{safeMcpConfig}'";

            // Copilot CLI picks up custom instructions via COPILOT_CUSTOM_INSTRUCTIONS_DIRS.
            // For MCP, pass the generated config explicitly because `--config-dir`/COPILOT_HOME
            // did not reliably surface the clarion-assistant server in practice.
            //
            // Note on the `--add-dir` below: Copilot's CWD already covers workDir
            // via the preceding `cd`, but `--add-dir` additionally puts the path
            // on Copilot's allowed-paths list for cross-directory tool operations.
            // The two are intentionally both set, not redundant.
            string cmd =
                $"cd '{ctx.SafeWorkDir}'; " +
                "$env:CLARION_ASSISTANT_EMBEDDED='1'; " +
                $"$env:COPILOT_HOME='{safeCopilotHome}'; " +
                (string.IsNullOrEmpty(safeInstrDir) ? "" : $"$env:COPILOT_CUSTOM_INSTRUCTIONS_DIRS='{safeInstrDir}'; ") +
                copilotBase +
                $" --config-dir '{safeCopilotHome}'" +
                mcpConfigArg +
                $" --add-dir '{ctx.SafeWorkDir}'" +
                modelFlag +
                permissionFlags +
                extraFlags;

            System.Diagnostics.Debug.WriteLine("[LaunchCopilot] mcpConfig=" + (mcpConfig ?? "(none)") + ", home=" + copilotHome);
            return new BuiltBackendCommand { Cmd = cmd };
        }

        // Build the --model flag for the Codex CLI.
        //
        // Empty / "Auto" → fall back to the registry's recommended default. We
        // pass it explicitly rather than letting Codex CLI pick, because Codex
        // honours a top-level `model = "..."` in ~/.codex/config.toml that may
        // pin an older version (e.g. from a previous /models switch). Without
        // an explicit --model, our "Default" choice silently inherits that.
        //
        // Update CodexDefaultModelId together with the matching first entry in
        // Terminal\models.json when the recommended default shifts (Oracle DMs
        // John when this happens — see reference_oracle_model_watch memory).
        private const string CodexDefaultModelId = "gpt-5.5";

        private static string BuildCodexModelFlag(string codexModelVal)
        {
            string val = (codexModelVal ?? "").Trim();
            if (string.IsNullOrEmpty(val) ||
                string.Equals(val, "Auto", StringComparison.OrdinalIgnoreCase))
                val = CodexDefaultModelId;
            return $" --model '{val.Replace("'", "''")}'";
        }

        /// <summary>
        /// Codex-specific command assembly. Side effects:
        /// <list type="bullet">
        ///   <item>Refreshes <c>~/.codex/config.toml</c> so Codex sees CA's MCP server.</item>
        ///   <item>Deploys AGENTS.md to the working dir from the same
        ///         <c>clarion-assistant-prompt.md</c> source CA ships for Claude/Copilot.</item>
        /// </list>
        /// Returns null if the user-configured Codex command fails validation;
        /// AbortLaunch is called in that case. MCP registration failure does NOT abort —
        /// Codex still launches in a usable shell, just without IDE tools (we surface
        /// a status message instead of refusing to start).
        /// </summary>
        private BuiltBackendCommand BuildCodexCommand(TerminalTab tab, LaunchContext ctx)
        {
            // MCP wiring: write CA's HTTP endpoint + bearer token into ~/.codex/config.toml.
            // Best-effort — if the write fails or the server isn't running yet, Codex
            // launches in a bare shell. The terminal surfaces the specific reason so
            // the user knows whether to install mcp-remote, restart MCP, etc.
            string codexConfigPath = null;
            string mcpFailureReason = null;
            try
            {
                if (_mcpServer != null && _mcpServer.IsRunning)
                {
                    codexConfigPath = Services.CodexConfigService.EnsureMcpRegistration(
                        _mcpServer.McpUrl, _mcpServer.SessionToken, out mcpFailureReason);
                }
                else
                {
                    mcpFailureReason = "ClarionAssistant MCP server not running.";
                }
            }
            catch (Exception ex)
            {
                mcpFailureReason = "config.toml write failed: " + ex.Message;
                System.Diagnostics.Debug.WriteLine("[LaunchCodex] " + mcpFailureReason);
            }

            DeployCodexAgentsMd(ctx.WorkDir);

            // Build the base invocation from the user-selected Codex command.
            // For bare "codex", resolve to the full path; for anything else,
            // tokenize and quote so shell metacharacters in settings can't
            // chain additional commands into the pwsh -Command payload.
            string codexCmdRaw = _settings.GetDefaultCodexCommand();
            string codexBase;
            if (codexCmdRaw == "codex")
            {
                string resolved = Services.CodexProcessManager.FindCodexPathStatic();
                codexBase = resolved != null
                    ? "& " + Services.PwshCommandQuoter.QuoteLiteral(resolved)
                    : "codex";
            }
            else
            {
                try
                {
                    codexBase = Services.PwshCommandQuoter.BuildInvocation(codexCmdRaw);
                }
                catch (ArgumentException ex)
                {
                    UpdateStatus("Codex launch aborted: " + ex.Message);
                    AbortLaunch(tab);
                    return null;
                }
            }

            string modelFlag = BuildCodexModelFlag(_settings.Get("Codex.Model"));

            // Codex CLI takes -c / --config key=value pairs to override config.toml
            // entries at launch time. We use this to pin model_reasoning_effort when
            // the user picked one in Settings (low/medium/high). Empty / "Auto"
            // means let Codex pick.
            string effortVal = (_settings.Get("Codex.Effort") ?? "").Trim();
            string effortFlag = string.Empty;
            if (!string.IsNullOrEmpty(effortVal) &&
                !string.Equals(effortVal, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                string safeEffort = effortVal.Replace("'", "''");
                effortFlag = $" -c model_reasoning_effort='{safeEffort}'";
            }

            string mcpStatusMarker;
            if (codexConfigPath != null)
            {
                mcpStatusMarker = string.Empty;
            }
            else
            {
                // Sanitize the reason before splicing into a `Write-Host '...'` literal
                // inside the outer pwsh `-Command "..."` payload. The reason can contain
                // arbitrary text from caught exception messages (File.Replace, File.Move,
                // Directory.CreateDirectory, etc.). Three threats to neutralize:
                //   1. CR/LF — would break out of the single-quoted line and inject pwsh
                //      tokens past the banner.
                //   2. Embedded double quotes — would terminate the outer `-Command "..."`
                //      argument at the CommandLineToArgvW boundary.
                //   3. Single quote — handled by doubling, the standard pwsh-literal escape.
                // Truncate to keep the banner readable when an exception message is verbose.
                string reason = string.IsNullOrEmpty(mcpFailureReason)
                    ? "ClarionAssistant MCP server not registered with Codex; IDE tools unavailable."
                    : mcpFailureReason
                        .Replace("\r", " ")
                        .Replace("\n", " ")
                        .Replace("\"", "'")
                        .Replace("'", "''");
                if (reason.Length > 240) reason = reason.Substring(0, 240) + "…";
                mcpStatusMarker = $"Write-Host 'WARNING: {reason}' -ForegroundColor Yellow; ";
            }

            string cmd =
                $"cd '{ctx.SafeWorkDir}'; " +
                "$env:CLARION_ASSISTANT_EMBEDDED='1'; " +
                mcpStatusMarker +
                codexBase +
                modelFlag +
                effortFlag;

            System.Diagnostics.Debug.WriteLine("[LaunchCodex] codexConfig=" + (codexConfigPath ?? "(not written)") + ", workDir=" + ctx.WorkDir);
            return new BuiltBackendCommand { Cmd = cmd };
        }

        /// <summary>
        /// Copies the shared CA briefing (<c>Terminal\clarion-assistant-prompt.md</c>)
        /// into the working dir as <c>AGENTS.md</c>. Codex CLI auto-discovers AGENTS.md
        /// in cwd — no flag needed.
        ///
        /// Refuses to overwrite an existing <c>AGENTS.md</c>. Codex CLI's working dir
        /// is typically the user's open Clarion solution, which may already have a
        /// repo-tracked AGENTS.md authoring repo-specific agent policy. Silently
        /// clobbering that file would invert the trust boundary and could land in a
        /// commit. If the user wants CA's briefing, they can delete their AGENTS.md
        /// or add a managed-block convention in a future iteration.
        ///
        /// Best-effort otherwise; if the source is missing or the write fails, Codex
        /// still launches but without the IDE briefing.
        /// </summary>
        private static void DeployCodexAgentsMd(string workDir)
        {
            try
            {
                if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) return;
                string assemblyDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string source = Path.Combine(assemblyDir, "Terminal", "clarion-assistant-prompt.md");
                if (!File.Exists(source)) return;

                string dest = Path.Combine(workDir, "AGENTS.md");
                if (File.Exists(dest))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[DeployCodexAgentsMd] Existing AGENTS.md preserved at " + dest);
                    return;
                }
                File.Copy(source, dest, overwrite: false);
            }
            catch { }
        }

        private void OnTabRendererDataReceived(TerminalTab tab, byte[] data)
        {
            if (tab.Terminal != null && tab.Terminal.IsRunning)
                tab.Terminal.Write(data);
        }

        private static int _dataRecvCount;
        private void OnTabTerminalDataReceived(TerminalTab tab, byte[] data)
        {
            _dataRecvCount++;
            if (_dataRecvCount <= 5)
                System.Diagnostics.Debug.WriteLine("[DataFlow] Terminal→Renderer: " + data.Length + " bytes, renderer=" + (tab.Renderer != null) + ", disposed=" + (tab.Renderer?.IsDisposed) + ", initialized=" + (tab.Renderer?.IsInitialized));
            var renderer = tab.Renderer;
            if (renderer != null && !renderer.IsDisposed)
                renderer.WriteToTerminal(data);

            // Auto-send startup command once Claude is ready for human input.
            // Detection: look for the prompt character (> or ❯) at a line boundary,
            // but only after Claude has been running long enough to finish initialization.
            if (!string.IsNullOrEmpty(tab.StartupCommand) && tab.Terminal != null && tab.Terminal.IsRunning)
            {
                try
                {
                    // Skip early output — Claude Code takes several seconds to initialize
                    if (!tab.AssistantLaunched) { /* terminal not ready yet */ }
                    else
                    {
                        string text = Encoding.UTF8.GetString(data);
                        // Strip ANSI escape sequences before checking for prompt
                        string clean = System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;]*[a-zA-Z]|\x1b\][^\x07]*\x07|\x1b[()][0-2]|\x1b\[[\?]?[0-9;]*[hlm]", "");
                        // Look for prompt at line boundary: newline followed by > or ❯ (with optional space)
                        bool hasPrompt = System.Text.RegularExpressions.Regex.IsMatch(clean, @"(^|[\r\n])[\s]*[>\u276f]\s");
                        if (hasPrompt)
                        {
                            string cmd = tab.StartupCommand;
                            tab.StartupCommand = null; // only send once
                            // Delay to ensure Claude is fully ready for input
                            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                            {
                                try
                                {
                                    if (tab.Terminal != null && tab.Terminal.IsRunning)
                                        tab.Terminal.Write(Encoding.UTF8.GetBytes(cmd + "\r"));
                                }
                                catch { }
                            });
                        }
                    }
                }
                catch { }
            }
        }

        private void OnTabRendererResized(TerminalTab tab, TerminalSizeEventArgs e)
        {
            if (tab.Terminal != null && tab.Terminal.IsRunning)
                tab.Terminal.Resize(e.Columns, e.Rows);
        }

        private void OnTabTerminalProcessExited(TerminalTab tab)
        {
            tab.AssistantLaunched = false;

            if (_knowledgeService != null && tab.SessionId > 0)
            {
                try { _knowledgeService.EndSession(tab.SessionId, null); }
                catch { }
            }

            string label;
            if (string.Equals(tab.AssistantBackend, "Copilot", StringComparison.OrdinalIgnoreCase))
                label = "Copilot CLI exited";
            else if (string.Equals(tab.AssistantBackend, "Codex", StringComparison.OrdinalIgnoreCase))
                label = "Codex CLI exited";
            else
                label = "Claude Code exited";
            if (InvokeRequired)
                BeginInvoke((Action)(() => UpdateStatus(label)));
            else
                UpdateStatus(label);
        }

        private void OnWorkWithSolution()
        {
            // Detect current solution from the IDE
            DetectFromIde();

            if (string.IsNullOrEmpty(_currentSlnPath) || !File.Exists(_currentSlnPath))
            {
                MessageBox.Show("No solution is currently open in the IDE.\nOpen a solution in Clarion first.",
                    "Work With Solution", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Open a terminal tab for the detected solution
            OpenSolutionInNewTab(_currentSlnPath);
        }

        private void OnNewChat(object sender, EventArgs e)
        {
            // Always open a new terminal tab so the user doesn't lose existing work
            var renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
            var tab = _tabManager.CreateTerminalTab(null, renderer);
            renderer.DataReceived += data => OnTabRendererDataReceived(tab, data);
            renderer.TerminalResized += (s, ev) => OnTabRendererResized(tab, ev);
            renderer.Initialized += (s, ev) => OnTabRendererInitialized(tab);

            // Schema sources panel
            AttachSchemaSourcesView(tab);

            _tabManager.ActivateTab(tab.Id);
        }

        #endregion

        #region Helpers

        private void DeployClaudeMd(string workDir)
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string source = Path.Combine(assemblyDir, "Terminal", "clarion-assistant-prompt.md");
                if (!File.Exists(source)) return;

                string claudeDir = Path.Combine(workDir, ".claude");
                if (!Directory.Exists(claudeDir))
                    Directory.CreateDirectory(claudeDir);

                string dest = Path.Combine(claudeDir, "CLAUDE.md");
                // Always overwrite — the dynamic context from last session needs to be cleared
                File.Copy(source, dest, true);

                // Deploy statusLine config so Claude Code writes status data for this tab
                DeployStatusLineConfig(claudeDir, assemblyDir);
            }
            catch { }
        }

        private void DeployStatusLineConfig(string claudeDir, string assemblyDir)
        {
            try
            {
                string scriptPath = Path.Combine(assemblyDir, "Terminal", "ca-statusline.js");
                if (!File.Exists(scriptPath)) return;

                // Resolve a concrete node.exe path. Standalone Claude Code installs bundle
                // node at ~/.claude/local/node.exe and have no system-wide `node` on PATH,
                // so a bare `node` command breaks the terminal for those users (issue #11).
                string nodeExe = ResolveNodeExe();
                if (nodeExe == null) return;

                string settingsPath = Path.Combine(claudeDir, "settings.local.json");
                string safeScript = scriptPath.Replace("\\", "/");
                string safeNode = nodeExe.Replace("\\", "/");
                string json = "{\"statusLine\":{\"type\":\"command\",\"command\":\"\\\"" + safeNode + "\\\" \\\"" + safeScript + "\\\"\"}}";
                File.WriteAllText(settingsPath, json, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private static string ResolveNodeExe()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string bundled = Path.Combine(userProfile, ".claude", "local", "node.exe");
                if (File.Exists(bundled)) return bundled;
            }
            catch { }

            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string candidate = Path.Combine(dir.Trim(), "node.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Appends host-controlled knowledge and session recap to CLAUDE.md.
        /// Reads from the addin's own SQLite database — no external dependencies.
        /// </summary>
        /// <summary>
        /// Builds the full system prompt injection file containing knowledge + session recap.
        /// Everything goes into the system prompt (invisible to the user).
        /// </summary>
        private string BuildSystemPromptInjection(string workDir)
        {
            var sb = new System.Text.StringBuilder();

            // 1. Knowledge entries
            if (_knowledgeService != null)
            {
                try
                {
                    string knowledge = _knowledgeService.GetInjectionMarkdown(15);
                    if (!string.IsNullOrEmpty(knowledge))
                    {
                        sb.AppendLine(knowledge);
                        sb.AppendLine();
                    }
                }
                catch { }
            }

            // 2. Session recap from JSONL (primary) or DB (fallback)
            try
            {
                string recap = Services.KnowledgeService.GetSessionRecapFromJsonl(workDir, 10);
                if (string.IsNullOrEmpty(recap) && _knowledgeService != null)
                    recap = _knowledgeService.GetLastSessionSummary(workDir);

                if (!string.IsNullOrEmpty(recap))
                {
                    sb.AppendLine("# Last Session Recap");
                    sb.AppendLine("When the session starts, briefly greet the developer and summarize what you were working on last session in 1-2 sentences based on the recap below. Then ask what they'd like to work on.");
                    sb.AppendLine();
                    sb.AppendLine(recap);
                }
            }
            catch { }

            string result = sb.ToString().Trim();
            return result.Length > 5 ? result : null;
        }

        /// <summary>
        /// Builds a clean initial prompt — just a short greeting trigger.
        /// The actual context is in the system prompt file.
        /// </summary>
        private string BuildInitialPrompt(string workDir)
        {
            int hour = DateTime.Now.Hour;
            string[] timeGreetings;

            if (hour < 12)
                timeGreetings = new[] { "Good morning!", "Morning!", "Top of the morning!" };
            else if (hour < 17)
                timeGreetings = new[] { "Good afternoon!", "Afternoon!" };
            else
                timeGreetings = new[] { "Good evening!", "Evening!" };

            string[] funGreetings = new[]
            {
                "Clarion Assistant is on-line!",
                "Greetings, Clarion Developer!",
                "Ready to write some Clarion!",
                "Let's build something!",
                "Reporting for duty!",
                "At your service!",
            };

            // Combine time-based and fun greetings, pick one randomly
            var all = new System.Collections.Generic.List<string>();
            all.AddRange(timeGreetings);
            all.AddRange(funGreetings);

            var rng = new Random();
            string greeting = all[rng.Next(all.Count)];

            return greeting;
        }

        private string FindPowerShell(bool requirePwsh = false)
        {
            // Clarion loads this addin as an x86 process, so SpecialFolder.ProgramFiles can
            // resolve to "C:\Program Files (x86)" even when pwsh is installed in the 64-bit path.
            // Check both standard locations and then fall back to PATH resolution.
            var candidates = new System.Collections.Generic.List<string>();

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));

            string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(programW6432))
                candidates.Add(Path.Combine(programW6432, "PowerShell", "7", "pwsh.exe"));

            string programFilesEnv = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFilesEnv))
                candidates.Add(Path.Combine(programFilesEnv, "PowerShell", "7", "pwsh.exe"));

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    return candidate;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "pwsh",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(3000);
                    if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        return output;
                }
            }
            catch { }

            return requirePwsh ? null : "powershell.exe";
        }

        private static string GetCopilotHomeDir()
        {
            // Shared across all tabs — the mcp-config.json and instructions/AGENTS.md
            // we write here are launch-identical per-tab, so an earlier per-tab layout
            // just accumulated orphan directories. Keep it flat.
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", "copilot");
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            // One-shot cleanup of the legacy per-tab layout. Best-effort; ignore
            // failures (another process might be using a dir, or ACLs might
            // block removal — not worth blocking the launch over).
            try
            {
                foreach (string legacy in Directory.GetDirectories(root, "tab-*"))
                {
                    try { Directory.Delete(legacy, recursive: true); }
                    catch { }
                }
            }
            catch { }

            return root;
        }

        private static string DeployCopilotInstructions(string copilotHome)
        {
            try
            {
                // Single source of truth: clarion-assistant-prompt.md is the
                // authoritative IDE / MCP / tool-usage instructions shared by both
                // backends. Claude reads it directly via --append-system-prompt-file;
                // Copilot expects an AGENTS.md file inside its instructions dir, so
                // we copy-rename at deploy time.
                string assemblyDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                string source = Path.Combine(assemblyDir, "Terminal", "clarion-assistant-prompt.md");
                if (!File.Exists(source)) return null;

                string instrDir = Path.Combine(copilotHome, "instructions");
                if (!Directory.Exists(instrDir)) Directory.CreateDirectory(instrDir);

                string dest = Path.Combine(instrDir, "AGENTS.md");
                File.Copy(source, dest, true);
                return instrDir;
            }
            catch { }
            return null;
        }

        private static string GetClarionAssistantPluginPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string marketplacePath = Path.Combine(userProfile, ".claude", "plugins", "marketplaces",
                "clarionassistant-marketplace", "plugins", "clarion-assistant");
            if (Directory.Exists(marketplacePath))
                return marketplacePath;
            string cachePath = Path.Combine(userProfile, ".claude", "plugins", "cache",
                "clarionassistant-marketplace", "clarion-assistant", "1.0.0");
            if (Directory.Exists(cachePath))
                return cachePath;
            return null;
        }

        private void UpdateStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => UpdateStatus(text))); return; }
            string css = "";
            if (text.Contains("port")) css = "connected";
            else if (text.Contains("failed") || text.Contains("exited")) css = "error";
            _header.SetStatus(text, css);
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_tabManager != null) _tabManager.Dispose();
                if (_mcpServer != null) _mcpServer.Dispose();
                if (_knowledgeService != null) _knowledgeService.Dispose();
                if (_lspUiTimer != null) { _lspUiTimer.Stop(); _lspUiTimer.Dispose(); }
                if (_diagForm != null) { try { _diagForm.Close(); _diagForm.Dispose(); } catch { } }
                if (_instanceStateTimer != null) { _instanceStateTimer.Stop(); _instanceStateTimer.Dispose(); }
                if (_statusLineTimer != null) { _statusLineTimer.Stop(); _statusLineTimer.Dispose(); }
                if (_instanceCoord != null) _instanceCoord.Dispose();
                if (_homeView != null) _homeView.Dispose();
                if (_header != null) _header.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
