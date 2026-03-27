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
        private WebViewTerminalRenderer _renderer;
        private ConPtyTerminal _terminal;

        // Header (WebView2)
        private HeaderWebView _header;
        private Splitter _splitter;

        private McpServer _mcpServer;
        private McpToolRegistry _toolRegistry;
        private readonly EditorService _editorService;
        private readonly ClarionClassParser _parser;
        private readonly SettingsService _settings;

        private string _mcpConfigPath;
        private bool _claudeLaunched;
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

            // === Splitter between header and terminal ===
            _splitter = new Splitter
            {
                Dock = DockStyle.Top,
                Height = 4,
                BackColor = Color.FromArgb(49, 50, 68),
                MinSize = 60,
                Cursor = Cursors.SizeNS
            };
            _splitter.SplitterMoved += OnSplitterMoved;

            // === Terminal renderer ===
            _renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
            _renderer.DataReceived += OnRendererDataReceived;
            _renderer.TerminalResized += OnRendererResized;
            _renderer.Initialized += OnRendererInitialized;

            // Add in correct order (bottom to top for docking)
            Controls.Add(_renderer);
            Controls.Add(_splitter);
            Controls.Add(_header);

            BackColor = Color.FromArgb(12, 12, 12);

            ResumeLayout(false);
        }

        private void OnHeaderReady(object sender, EventArgs e)
        {
            LoadVersions();
            LoadSolutionHistory();
            SyncHeaderFontSettings();
        }

        private void SyncHeaderFontSettings()
        {
            string family = GetFontFamily();
            int size = (int)Math.Round(GetFontSize());
            _header.SendMessage("{\"type\":\"setFontFamily\",\"value\":\"" + HeaderWebView.EscapeJsonStatic(family) + "\"}");
            _header.SendMessage("{\"type\":\"setFontSize\",\"value\":\"" + size + "\"}");
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
                case "fontFamilyChanged": OnFontFamilyChanged(e.Data); break;
                case "fontSizeChanged": OnFontSizeChangedFromHeader(e.Data); break;
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
        private void DetectFromIde()
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
        }

        private void OnSolutionChanged(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _currentSlnPath = path;
                AddToSolutionHistory(path);
                UpdateIndexStatus();
                LoadRedFile();
            }
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

        private void OnFontFamilyChanged(string family)
        {
            if (string.IsNullOrEmpty(family)) return;
            _settings.Set("Claude.FontFamily", family);
            _renderer.SetFontFamily(family);
        }

        private void OnFontSizeChangedFromHeader(string sizeStr)
        {
            float size;
            if (!float.TryParse(sizeStr, out size)) return;
            size = Math.Max(6f, Math.Min(32f, size));
            _settings.Set("Claude.FontSize", size.ToString());
            _renderer.SetFontSize(size);
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
            using (var dlg = new ClaudeChatSettingsDialog(_settings))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    _renderer.SetFontSize(dlg.FontSize);
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

            if (_terminal == null || !_terminal.IsRunning)
            {
                MessageBox.Show("Claude is not running. Please wait for it to start.",
                    "Create COM Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string command = "/ClarionCOM Create a new COM control in " + comFolder + "\r";
            _terminal.Write(Encoding.UTF8.GetBytes(command));
        }

        private void OnEvaluateCode(object sender, EventArgs e)
        {
            if (_terminal == null || !_terminal.IsRunning)
            {
                MessageBox.Show("Claude is not running. Please wait for it to start.",
                    "Evaluate Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _terminal.Write(Encoding.UTF8.GetBytes("/evaluate-code\r"));
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
        }

        #endregion

        #region Terminal Lifecycle

        private void OnRendererInitialized(object sender, EventArgs e)
        {
            _renderer.SetFontSize(GetFontSize());
            _renderer.SetFontFamily(GetFontFamily());

            // Sync header dropdowns with saved font settings
            _renderer.FontSizeChangedByUser += OnFontSizeChangedByWheel;

            LoadVersions();
            LoadSolutionHistory();
            DetectFromIde();
            StartMcpServer();
            LaunchClaude();
        }

        private void OnFontSizeChangedByWheel(object sender, float size)
        {
            _settings.Set("Claude.FontSize", size.ToString());
            // Update header dropdown to match
            int rounded = (int)Math.Round(size);
            _header.SendMessage("{\"type\":\"setFontSize\",\"value\":\"" + rounded + "\"}");
        }

        private void LaunchClaude()
        {
            if (_claudeLaunched) return;
            _claudeLaunched = true;

            _terminal = new ConPtyTerminal();
            _terminal.DataReceived += OnTerminalDataReceived;
            _terminal.ProcessExited += OnTerminalProcessExited;

            string pwsh = FindPowerShell();
            string workDir = GetWorkingDirectory();

            string mcpArg = "";
            if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
            {
                string safePath = _mcpConfigPath.Replace("'", "''");
                mcpArg = $" --mcp-config '{safePath}'";
            }

            DeployClaudeMd(workDir);

            string envSetup = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8; ";
            string safeWorkDir = workDir.Replace("'", "''");
            string allowedTools = "mcp__clarion-assistant__*,Read,Edit,Write,Bash,Glob,Grep";
            if (_mcpServer.IncludeMultiTerminal)
                allowedTools += ",mcp__multiterminal__*";

            // CLARION_ASSISTANT_EMBEDDED suppresses global hooks that inject /session-start, /project-management
            // instructions — those tools aren't in the addin's allowedTools and confuse the model
            string claudeCmd = $"cd '{safeWorkDir}'; $env:CLARION_ASSISTANT_EMBEDDED='1'; claude{mcpArg} --strict-mcp-config --allowedTools '{allowedTools}'";
            string commandLine = $"\"{pwsh}\" -NoLogo -ExecutionPolicy Bypass -NoExit -Command \"{envSetup}{claudeCmd}\"";

            _terminal.Start(_renderer.VisibleCols, _renderer.VisibleRows, commandLine, workDir);
            UpdateStatus("MCP: port " + (_mcpServer?.Port ?? 0) + " | Claude Code running");
        }

        private void OnRendererDataReceived(byte[] data)
        {
            if (_terminal != null && _terminal.IsRunning)
                _terminal.Write(data);
        }

        private void OnTerminalDataReceived(byte[] data)
        {
            _renderer.WriteToTerminal(data);
        }

        private void OnRendererResized(object sender, TerminalSizeEventArgs e)
        {
            if (_terminal != null && _terminal.IsRunning)
                _terminal.Resize(e.Columns, e.Rows);
        }

        private void OnTerminalProcessExited(object sender, EventArgs e)
        {
            _claudeLaunched = false;
            if (InvokeRequired)
                BeginInvoke((Action)(() => UpdateStatus("Claude Code exited")));
            else
                UpdateStatus("Claude Code exited");
        }

        private void OnNewChat(object sender, EventArgs e)
        {
            if (_terminal != null)
            {
                _terminal.Stop();
                _terminal.Dispose();
                _terminal = null;
            }
            _claudeLaunched = false;
            _renderer.Clear();
            LaunchClaude();
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
                if (!File.Exists(dest) || File.GetLastWriteTime(source) > File.GetLastWriteTime(dest))
                    File.Copy(source, dest, true);
            }
            catch { }
        }

        private string FindPowerShell()
        {
            string pwsh7 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh7)) return pwsh7;
            return "powershell.exe";
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
                if (_terminal != null) _terminal.Dispose();
                if (_mcpServer != null) _mcpServer.Dispose();
                if (_renderer != null) _renderer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
