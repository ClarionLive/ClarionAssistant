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
    public class ClaudeChatControl : UserControl
    {
        // Tab system (MultiTerminal Panel-based pattern)
        private TabManager _tabManager;
        private Panel _tabStrip;    // custom-painted tab header strip (hidden when 1 tab)
        private Panel _contentArea; // holds all tab content controls
        private HomeWebView _homeView;

        // Header (WebView2)
        private HeaderWebView _header;
        private Splitter _splitter;
        private int _preIndexHeaderHeight = -1;

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
        private ClarionVersionInfo _versionInfo;
        private ClarionVersionConfig _currentVersionConfig;
        private RedFileService _redFileService;
        private DiffService _diffService;

        public string CurrentSolutionPath { get { return _currentSlnPath; } }
        public SettingsService Settings { get { return _settings; } }
        public ClarionVersionConfig CurrentVersionConfig { get { return _currentVersionConfig; } }
        public RedFileService RedFile { get { return _redFileService; } }
        public string CurrentDbPath
        {
            get
            {
                if (string.IsNullOrEmpty(_currentSlnPath)) return null;
                return Path.Combine(Path.GetDirectoryName(_currentSlnPath),
                    Path.GetFileNameWithoutExtension(_currentSlnPath) + ".codegraph.db");
            }
        }

        public ClaudeChatControl()
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
            LoadProjects();
            SendProjectsToHome();
            SendGitHubAccountsToHome();
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] SendGitHubAccountsToHome error: " + ex.Message);
            }
        }

        private void OnHomeAction(object sender, HomeActionEventArgs e)
        {
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
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OpenProjectInNewTab: " + project.Name + " (" + project.Type + ") -> " + project.Folder);
            string folder = project.Folder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OpenProjectInNewTab ABORTED: folder empty or not found");
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
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] Events wired for project tab " + tab.Id + ", StartupCommand=" + (tab.StartupCommand ?? "(none)"));

            // Schema sources panel (above terminal)
            AttachSchemaSourcesView(tab);

            _tabManager.ActivateTab(tab.Id);
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] ActivateTab completed for project tab " + tab.Id);
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
                    System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] Solution changed: " + slnPath);
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

        private void PollStatusLine()
        {
            try
            {
                var tab = _tabManager?.ActiveTab;
                if (tab == null || tab.IsHome || !tab.ClaudeLaunched || tab.Renderer == null) return;

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
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OpenSolutionInNewTab: " + slnPath);
            if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath))
            {
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OpenSolutionInNewTab ABORTED: path empty or not found");
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
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] Events wired for tab " + tab.Id + ", calling ActivateTab");

            // Schema sources panel (above terminal)
            AttachSchemaSourcesView(tab);

            _tabManager.ActivateTab(tab.Id);
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] ActivateTab completed for tab " + tab.Id);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] SendSchemaSourcesForTab error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] SendGlobalSources error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] AddSource error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] EditSource error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] DeleteSource error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] ApplySelection error: " + ex.Message);
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
                    System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] IndexSource error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] SetSolutionRepo error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] BrowseFile error: " + ex.Message);
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

            // Expand header so the progress log is visible
            _preIndexHeaderHeight = _header.Height;
            if (_header.Height < 300)
                _header.Height = 300;

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

                // Restore header to its pre-index height
                if (_preIndexHeaderHeight > 0)
                {
                    _header.Height = _preIndexHeaderHeight;
                    _preIndexHeaderHeight = -1;
                }
            };
            worker.RunWorkerAsync();
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

        private string GetWorkingDirectory()
        {
            string dir = _settings.Get("Claude.WorkingDirectory");
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] SendClassModels error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] PreviewModel error: " + ex.Message);
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
                    System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OpenFile error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] CreateClass error: " + ex.Message);
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
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] MCP error: " + error);
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
        }

        #endregion

        #region Terminal Lifecycle

        private void OnTabRendererInitialized(TerminalTab tab)
        {
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OnTabRendererInitialized for tab " + tab.Id + " (" + tab.Name + ")");
            if (tab.Renderer == null)
            {
                System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] OnTabRendererInitialized: renderer is null!");
                return;
            }
            tab.Renderer.SetTheme(_isDarkTheme);
            tab.Renderer.SetFontSize(GetFontSize());
            tab.Renderer.SetFontFamily(GetFontFamily());
            tab.Renderer.FontSizeChangedByUser += OnFontSizeChangedByWheel;
            LaunchClaudeForTab(tab);
            tab.Renderer.Focus();
        }

        private void OnFontSizeChangedByWheel(object sender, float size)
        {
            _settings.Set("Claude.FontSize", size.ToString());
        }

        private void LaunchClaudeForTab(TerminalTab tab)
        {
            System.Diagnostics.Debug.WriteLine("[LaunchClaude] ENTER tab=" + tab.Id + ", ClaudeLaunched=" + tab.ClaudeLaunched);
            if (tab.ClaudeLaunched) return;
            tab.ClaudeLaunched = true;

            try
            {
                tab.Terminal = new ConPtyTerminal();
                tab.Terminal.DataReceived += data => OnTabTerminalDataReceived(tab, data);
                tab.Terminal.ProcessExited += (s, ev) => OnTabTerminalProcessExited(tab);

                string pwsh = FindPowerShell();
                string workDir = !string.IsNullOrEmpty(tab.WorkingDirectory) && Directory.Exists(tab.WorkingDirectory)
                    ? tab.WorkingDirectory
                    : GetWorkingDirectory();
                System.Diagnostics.Debug.WriteLine("[LaunchClaude] pwsh=" + pwsh + ", workDir=" + workDir);

                string mcpArg = "";
                if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
                {
                    string safePath = _mcpConfigPath.Replace("'", "''");
                    mcpArg = $" --mcp-config '{safePath}'";
                }
                System.Diagnostics.Debug.WriteLine("[LaunchClaude] mcpConfigPath=" + _mcpConfigPath + ", mcpArg=" + mcpArg);

                DeployClaudeMd(workDir);

                if (_knowledgeService != null)
                {
                    try { tab.SessionId = _knowledgeService.StartSession(workDir); }
                    catch { }
                }

                string systemPromptExtra = BuildSystemPromptInjection(workDir);
                string initialPrompt = BuildInitialPrompt(workDir);
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

                string envSetup = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8; ";
                string safeWorkDir = workDir.Replace("'", "''");
                string allowedTools = "mcp__clarion-assistant__*,Read,Edit,Write,Bash,Glob,Grep";
                if (_mcpServer != null && _mcpServer.IncludeMultiTerminal)
                    allowedTools += ",mcp__multiterminal__*";

                string pluginArg = "";
                string pluginDir = GetClarionAssistantPluginPath();
                if (pluginDir != null)
                {
                    string safePluginDir = pluginDir.Replace("'", "''");
                    pluginArg = $" --plugin-dir '{safePluginDir}'";
                }

                string colorfgbg = _isDarkTheme ? "$env:COLORFGBG='15;0'" : "$env:COLORFGBG='0;15'";
                string claudeBase = _settings.GetDefaultClaudeCommand();
                // If the command is bare "claude", resolve to full path so it works
                // even when the CLI isn't on the inherited PATH
                if (claudeBase == "claude")
                {
                    string resolved = Services.ClaudeProcessManager.FindClaudePathStatic();
                    if (resolved != null)
                        claudeBase = "& '" + resolved.Replace("'", "''") + "'";
                }
                // Set CA tab ID so the statusline script can write per-tab status
                string tabEnv = $"$env:CLARIONASSISTANT_TAB='{tab.Id}'";
                string claudeCmd = $"cd '{safeWorkDir}'; $env:CLARION_ASSISTANT_EMBEDDED='1'; {tabEnv}; {colorfgbg}; {claudeBase}{mcpArg}{pluginArg} --strict-mcp-config --allowedTools '{allowedTools}'{extraFlags}";

                if (initialPromptFile != null)
                {
                    string safeFile = initialPromptFile.Replace("'", "''");
                    claudeCmd += $" (Get-Content -Raw '{safeFile}')";
                }

                string commandLine = $"\"{pwsh}\" -NoLogo -ExecutionPolicy Bypass -NoExit -Command \"{envSetup}{claudeCmd}\"";

                System.Diagnostics.Debug.WriteLine("[LaunchClaude] cols=" + tab.Renderer.VisibleCols + ", rows=" + tab.Renderer.VisibleRows);
                System.Diagnostics.Debug.WriteLine("[LaunchClaude] Starting ConPTY: " + commandLine.Substring(0, Math.Min(200, commandLine.Length)));
                tab.Terminal.Start(tab.Renderer.VisibleCols, tab.Renderer.VisibleRows, commandLine, workDir);
                System.Diagnostics.Debug.WriteLine("[LaunchClaude] ConPTY started OK");
                UpdateStatus("MCP: port " + (_mcpServer?.Port ?? 0) + " | Claude Code running");

                // Clean up temp prompt files after Claude Code has read them
                if (tempFiles.Count > 0)
                {
                    var filesToDelete = new System.Collections.Generic.List<string>(tempFiles);
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
                    if (!tab.ClaudeLaunched) { /* terminal not ready yet */ }
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
            tab.ClaudeLaunched = false;

            if (_knowledgeService != null && tab.SessionId > 0)
            {
                try { _knowledgeService.EndSession(tab.SessionId, null); }
                catch { }
            }

            if (InvokeRequired)
                BeginInvoke((Action)(() => UpdateStatus("Claude Code exited")));
            else
                UpdateStatus("Claude Code exited");
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

        private string FindPowerShell()
        {
            string pwsh7 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh7)) return pwsh7;
            return "powershell.exe";
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
