using System;
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
        private string _currentSlnPath;
        private string _indexerPath;
        private ClarionVersionInfo _versionInfo;
        private ClarionVersionConfig _currentVersionConfig;
        private RedFileService _redFileService;
        private DiffService _diffService;

        public string CurrentSolutionPath { get { return _currentSlnPath; } }
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
            _indexerPath = FindIndexer();
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
            RefreshHomePageData(); // home may have initialized before _versionInfo was set
        }

        private void OnHomeReady(object sender, EventArgs e)
        {
            _homeView.SetTheme(_isDarkTheme);
            RefreshHomePageData();
        }

        private void OnHomeAction(object sender, HomeActionEventArgs e)
        {
            switch (e.Action)
            {
                case "openSolution": OpenSolutionInNewTab(e.Data); break;
                case "removeSolution": RemoveSolutionFromHistory(e.Data); break;
                case "openFolder": OpenFolder(e.Data); break;
                case "browseSolution": OnBrowseSolutionForNewTab(); break;
                case "createCom": OnCreateCom(sender, EventArgs.Empty); break;
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

        private void RefreshHomePageData()
        {
            if (!_homeView.IsReady) return;

            // Prefer Clarion's own RecentOpen.xml; fall back to internal SolutionHistory
            var rawPaths = new System.Collections.Generic.List<string>();
            if (_versionInfo != null && !string.IsNullOrEmpty(_versionInfo.PropertiesXmlPath))
                rawPaths = ClarionVersionService.GetRecentSolutionPaths(_versionInfo.PropertiesXmlPath);
            if (rawPaths.Count == 0)
            {
                string history = _settings.Get("SolutionHistory") ?? "";
                foreach (string p in history.Split('|'))
                    if (!string.IsNullOrEmpty(p)) rawPaths.Add(p);
            }

            // Filter out paths the user has suppressed from the home view
            string suppressed = _settings.Get("SuppressedSolutions") ?? "";
            var suppressedSet = new System.Collections.Generic.HashSet<string>(
                suppressed.Split('|'), StringComparer.OrdinalIgnoreCase);

            var names = new System.Collections.Generic.List<string>();
            var paths = new System.Collections.Generic.List<string>();
            var modified = new System.Collections.Generic.List<string>();
            var sizes = new System.Collections.Generic.List<long>();
            var modifiedTs = new System.Collections.Generic.List<long>();
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            foreach (string path in rawPaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                if (suppressedSet.Contains(path)) continue;
                try
                {
                    var fi = new FileInfo(path);
                    names.Add(Path.GetFileNameWithoutExtension(path));
                    paths.Add(path);
                    modified.Add(fi.LastWriteTime.ToString("M/d/yyyy"));
                    sizes.Add(fi.Length);
                    modifiedTs.Add((long)(fi.LastWriteTime.ToUniversalTime() - epoch).TotalMilliseconds);
                }
                catch { /* skip unreadable files */ }
            }
            _homeView.SetSolutionHistory(names.ToArray(), paths.ToArray(), modified.ToArray(), sizes.ToArray(), modifiedTs.ToArray());
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

            RefreshHomePageData();
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
                    RefreshHomePageData();
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
            tab.WorkingDirectory = Path.GetDirectoryName(slnPath);
            tab.VersionConfig = _currentVersionConfig;

            // Wire renderer events to per-tab handlers
            renderer.DataReceived += data => OnTabRendererDataReceived(tab, data);
            renderer.TerminalResized += (s, ev) => OnTabRendererResized(tab, ev);
            renderer.Initialized += (s, ev) => OnTabRendererInitialized(tab);
            System.Diagnostics.Debug.WriteLine("[ClaudeChatControl] Events wired for tab " + tab.Id + ", calling ActivateTab");

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

            _tabManager.CloseTab(tabId);
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

            if (string.IsNullOrEmpty(_indexerPath) || !File.Exists(_indexerPath))
            {
                MessageBox.Show("Indexer not found: " + (_indexerPath ?? "(null)"), "Index", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _header.SetIndexButtonsEnabled(false);
            _header.SetIndexStatus(incremental ? "Updating..." : "Indexing...", "active");

            // Build library paths from RED file .inc search paths
            string libPathsArg = null;
            if (_redFileService != null)
            {
                var incPaths = _redFileService.GetSearchPaths(".inc");
                if (incPaths.Count > 0)
                    libPathsArg = string.Join(";", incPaths);
            }

            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                string args = $"index \"{_currentSlnPath}\"";
                if (incremental) args += " --incremental";
                if (!string.IsNullOrEmpty(libPathsArg))
                {
                    // Escape double-quotes in paths to prevent argument injection
                    string safePaths = libPathsArg.Replace("\"", "\\\"");
                    args += $" --lib-paths \"{safePaths}\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = _indexerPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    // Read stdout asynchronously so WaitForExit timeout is effective
                    var readTask = System.Threading.Tasks.Task.Run(
                        () => proc.StandardOutput.ReadToEnd());
                    bool exited = proc.WaitForExit(300000); // 5 min max
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                    }
                    e.Result = readTask.Wait(5000) ? readTask.Result : "";
                }
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

        private string FindIndexer()
        {
            // Check next to our assembly first
            string assemblyDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "clarion-indexer.exe");
            if (File.Exists(path)) return path;

            // Check the LSP indexer build output
            path = @"H:\DevLaptop\ClarionLSP\indexer\bin\Release\clarion-indexer.exe";
            if (File.Exists(path)) return path;

            path = @"H:\DevLaptop\ClarionLSP\indexer\bin\Debug\clarion-indexer.exe";
            if (File.Exists(path)) return path;

            return null;
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
            if (active == null || active.IsHome || active.Terminal == null || !active.Terminal.IsRunning)
            {
                MessageBox.Show("Claude is not running. Please open a terminal first.",
                    "Evaluate Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            active.Terminal.Write(Encoding.UTF8.GetBytes("/evaluate-code\r"));
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
                _instanceStateTimer.Tick += (s, ev) => UpdateInstanceState();
                _instanceStateTimer.Start();
            }
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
                string claudeCmd = $"cd '{safeWorkDir}'; $env:CLARION_ASSISTANT_EMBEDDED='1'; {colorfgbg}; {claudeBase}{mcpArg}{pluginArg} --strict-mcp-config --allowedTools '{allowedTools}'{extraFlags}";

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

        private void OnNewChat(object sender, EventArgs e)
        {
            var active = _tabManager.ActiveTab;
            if (active == null || active.IsHome)
            {
                // From Home tab — open a new terminal without a specific solution
                var renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
                var tab = _tabManager.CreateTerminalTab(null, renderer);
                renderer.DataReceived += data => OnTabRendererDataReceived(tab, data);
                renderer.TerminalResized += (s, ev) => OnTabRendererResized(tab, ev);
                renderer.Initialized += (s, ev) => OnTabRendererInitialized(tab);
                _tabManager.ActivateTab(tab.Id);
                return;
            }

            // Active tab is a terminal — restart Claude in it
            if (active.Terminal != null)
            {
                active.Terminal.Stop();
                active.Terminal.Dispose();
                active.Terminal = null;
            }
            active.ClaudeLaunched = false;
            active.Renderer.Clear();
            LaunchClaudeForTab(active);
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
            }
            catch { }
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
                if (_instanceCoord != null) _instanceCoord.Dispose();
                if (_homeView != null) _homeView.Dispose();
                if (_header != null) _header.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
