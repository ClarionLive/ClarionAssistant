using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ClarionAssistant.Dialogs;
using ClarionAssistant.Services;
using ClarionAssistant.TaskLifecycleBoard;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    public class ClaudeChatControl : UserControl
    {
        private WebViewTerminalRenderer _renderer;
        private ConPtyTerminal _terminal;

        // Solution bar controls
        private Panel _solutionBar;
        private ComboBox _versionCombo;
        private ComboBox _solutionCombo;
        private Button _browseSolutionButton;
        private Button _fullIndexButton;
        private Button _updateIndexButton;
        private Label _indexStatusLabel;

        // Toolbar controls
        private ToolStrip _toolbar;
        private ToolStripLabel _statusLabel;

        private McpServer _mcpServer;
        private McpToolRegistry _toolRegistry;
        private readonly EditorService _editorService;
        private readonly ClarionClassParser _parser;
        private readonly SettingsService _settings;

        private string _mcpConfigPath;
        private bool _claudeLaunched;
        private MultiTerminalApiClient _multiTerminalApi;
        private string _currentSlnPath;
        private string _indexerPath;
        private ClarionVersionInfo _versionInfo;
        private ClarionVersionConfig _currentVersionConfig;

        public string CurrentSolutionPath { get { return _currentSlnPath; } }
        public ClarionVersionConfig CurrentVersionConfig { get { return _currentVersionConfig; } }
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

            // === Solution Bar (top panel) ===
            _solutionBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(40, 40, 40),
                Padding = new Padding(6, 4, 6, 4)
            };

            // Row 1: Version
            var versionLabel = new Label
            {
                Text = "Version",
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(6, 7),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f)
            };
            _versionCombo = new ComboBox
            {
                Location = new Point(60, 4),
                Width = 350,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };

            // Row 2: Solution + buttons
            var solutionLabel = new Label
            {
                Text = "Solution",
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(6, 35),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f)
            };
            _solutionCombo = new ComboBox
            {
                Location = new Point(60, 32),
                Width = 350,
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            _solutionCombo.SelectedIndexChanged += OnSolutionChanged;

            _browseSolutionButton = new Button
            {
                Text = "...",
                Location = new Point(414, 31),
                Width = 30,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f)
            };
            _browseSolutionButton.Click += OnBrowseSolution;

            _fullIndexButton = new Button
            {
                Text = "Full Index",
                Location = new Point(450, 31),
                Width = 70,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 100, 180),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold)
            };
            _fullIndexButton.Click += (s, e) => RunIndex(false);

            _updateIndexButton = new Button
            {
                Text = "Update",
                Location = new Point(524, 31),
                Width = 60,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f)
            };
            _updateIndexButton.Click += (s, e) => RunIndex(true);

            var refreshButton = new Button
            {
                Text = "\u21BB",
                Location = new Point(588, 4),
                Width = 26,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f),
                Tag = "refresh"
            };
            refreshButton.FlatAppearance.BorderSize = 0;
            refreshButton.Click += (s, e) => DetectFromIde();

            _indexStatusLabel = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(120, 200, 120),
                Location = new Point(590, 35),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f)
            };

            _solutionBar.Controls.AddRange(new Control[]
            {
                versionLabel, _versionCombo, refreshButton,
                solutionLabel, _solutionCombo, _browseSolutionButton,
                _fullIndexButton, _updateIndexButton, _indexStatusLabel
            });

            // === Toolbar ===
            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                Renderer = new DarkToolStripRenderer()
            };

            var newChatButton = new ToolStripButton("New Chat") { ForeColor = Color.White, ToolTipText = "Stop current session and start fresh" };
            newChatButton.Click += OnNewChat;

            var settingsButton = new ToolStripButton("Settings") { ForeColor = Color.White, ToolTipText = "Terminal and Claude settings" };
            settingsButton.Click += OnSettings;

            var taskBoardButton = new ToolStripButton("Task Board") { ForeColor = Color.White, ToolTipText = "Open lifecycle board for active task" };
            taskBoardButton.Click += OnOpenTaskBoard;

            _statusLabel = new ToolStripLabel("Starting...") { Alignment = ToolStripItemAlignment.Right, ForeColor = Color.Gray };

            _toolbar.Items.Add(newChatButton);
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(settingsButton);
            _toolbar.Items.Add(taskBoardButton);
            _toolbar.Items.Add(_statusLabel);

            // === Terminal renderer ===
            _renderer = new WebViewTerminalRenderer { Dock = DockStyle.Fill };
            _renderer.DataReceived += OnRendererDataReceived;
            _renderer.TerminalResized += OnRendererResized;
            _renderer.Initialized += OnRendererInitialized;

            // Add in correct order (bottom to top for docking)
            Controls.Add(_renderer);
            Controls.Add(_toolbar);
            Controls.Add(_solutionBar);

            BackColor = Color.FromArgb(12, 12, 12);

            // Handle resize to reflow solution bar
            _solutionBar.Resize += (s, e) => ReflowSolutionBar();

            ResumeLayout(false);
        }

        private void ReflowSolutionBar()
        {
            int w = _solutionBar.ClientSize.Width;
            int comboW = Math.Max(150, w - 290);

            _versionCombo.Width = comboW;

            // Position refresh button after version combo
            foreach (Control c in _solutionBar.Controls)
            {
                if (c.Tag as string == "refresh")
                    c.Left = 60 + comboW + 4;
            }

            _solutionCombo.Width = comboW;
            _browseSolutionButton.Left = 60 + comboW + 4;
            _fullIndexButton.Left = _browseSolutionButton.Right + 4;
            _updateIndexButton.Left = _fullIndexButton.Right + 4;
            _indexStatusLabel.Left = _updateIndexButton.Right + 8;
        }

        #endregion

        #region Solution Bar Logic

        private void LoadVersions()
        {
            _versionCombo.Items.Clear();
            _versionInfo = ClarionVersionService.Detect();

            if (_versionInfo == null || _versionInfo.Versions.Count == 0)
            {
                _versionCombo.Items.Add("(not detected)");
                _versionCombo.SelectedIndex = 0;
                return;
            }

            _currentVersionConfig = _versionInfo.GetCurrentConfig();

            foreach (var config in _versionInfo.Versions)
            {
                string label = config.Name;
                // Mark the resolved current version
                if (_currentVersionConfig != null && config.Name == _currentVersionConfig.Name
                    && _versionInfo.CurrentVersionName != null
                    && _versionInfo.CurrentVersionName.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0)
                    label += " (active)";

                _versionCombo.Items.Add(label);
                if (_currentVersionConfig != null && config.Name == _currentVersionConfig.Name)
                    _versionCombo.SelectedIndex = _versionCombo.Items.Count - 1;
            }

            if (_versionCombo.SelectedIndex < 0 && _versionCombo.Items.Count > 0)
                _versionCombo.SelectedIndex = 0;

            _versionCombo.SelectedIndexChanged += (s, e) =>
            {
                string selected = _versionCombo.SelectedItem?.ToString();
                if (_versionInfo != null && !string.IsNullOrEmpty(selected))
                    _currentVersionConfig = _versionInfo.Versions.Find(v => v.Name == selected);
            };
        }

        private void LoadSolutionHistory()
        {
            string history = _settings.Get("SolutionHistory") ?? "";
            _solutionCombo.Items.Clear();
            foreach (string path in history.Split('|'))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    _solutionCombo.Items.Add(path);
            }

            string last = _settings.Get("LastSolutionPath");
            if (!string.IsNullOrEmpty(last) && File.Exists(last))
            {
                _solutionCombo.Text = last;
                _currentSlnPath = last;
                UpdateIndexStatus();
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
        private void DetectFromIde()
        {
            // Detect open solution from the IDE
            string slnPath = EditorService.GetOpenSolutionPath();
            if (!string.IsNullOrEmpty(slnPath) && File.Exists(slnPath))
            {
                _currentSlnPath = slnPath;
                _solutionCombo.Text = slnPath;
                AddToSolutionHistory(slnPath);
                UpdateIndexStatus();
            }

            // Always re-detect version (user may have changed build in IDE)
            LoadVersions();
        }

        private void OnSolutionChanged(object sender, EventArgs e)
        {
            string path = _solutionCombo.Text;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _currentSlnPath = path;
                AddToSolutionHistory(path);
                UpdateIndexStatus();
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
                    _solutionCombo.Text = dlg.FileName;
                    AddToSolutionHistory(dlg.FileName);
                    LoadSolutionHistory();
                    UpdateIndexStatus();
                }
            }
        }

        private void UpdateIndexStatus()
        {
            string dbPath = CurrentDbPath;
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                var fi = new FileInfo(dbPath);
                _indexStatusLabel.Text = "Indexed: " + fi.LastWriteTime.ToString("MMM d HH:mm");
                _indexStatusLabel.ForeColor = Color.FromArgb(120, 200, 120);
            }
            else
            {
                _indexStatusLabel.Text = "Not indexed";
                _indexStatusLabel.ForeColor = Color.FromArgb(200, 150, 80);
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

            _fullIndexButton.Enabled = false;
            _updateIndexButton.Enabled = false;
            _indexStatusLabel.Text = incremental ? "Updating..." : "Indexing...";
            _indexStatusLabel.ForeColor = Color.FromArgb(100, 180, 255);

            var worker = new BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                string args = $"\"{_currentSlnPath}\"";
                if (incremental) args += " --incremental";

                var psi = new ProcessStartInfo
                {
                    FileName = _indexerPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                e.Result = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(300000); // 5 min max
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                _fullIndexButton.Enabled = true;
                _updateIndexButton.Enabled = true;
                UpdateIndexStatus();

                if (e.Error != null)
                {
                    _indexStatusLabel.Text = "Error: " + e.Error.Message;
                    _indexStatusLabel.ForeColor = Color.FromArgb(255, 100, 100);
                }
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

        private float GetFontSize()
        {
            string val = _settings.Get("Claude.FontSize");
            float size;
            if (!string.IsNullOrEmpty(val) && float.TryParse(val, out size))
                return Math.Max(6f, Math.Min(32f, size));
            return 14f;
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

        private void OnOpenTaskBoard(object sender, EventArgs e)
        {
            if (_multiTerminalApi == null)
                _multiTerminalApi = new MultiTerminalApiClient();
            var api = _multiTerminalApi;
            string agentName = Environment.GetEnvironmentVariable("MULTITERMINAL_NAME");
            if (string.IsNullOrEmpty(agentName)) agentName = "ClarionAssistant";

            System.Threading.Tasks.Task.Run(() =>
            {
                if (!api.IsAvailable()) return new { taskId = (string)null, error = "MultiTerminal is not running." };
                var result = api.GetActiveTask(agentName);
                if (result.Success && result.Data != null && result.Data.Task != null)
                    return new { taskId = result.Data.Task.Id, error = (string)null };
                var tasksResult = api.ListTasks("in_progress");
                if (tasksResult.Success && tasksResult.Data != null && tasksResult.Data.Count > 0)
                    return new { taskId = tasksResult.Data[0].Id, error = (string)null };
                return new { taskId = (string)null, error = "No active tasks found." };
            }).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted)
                {
                    MessageBox.Show("Error opening task board: " + t.Exception.InnerException.Message,
                        "Task Board", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (t.Result.error != null)
                {
                    MessageBox.Show(t.Result.error, "Task Board", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                bool isDark = BackColor.GetBrightness() < 0.5f;
                TaskLifecycleBoardForm.OpenForTask(t.Result.taskId, api, isDark);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        #endregion

        #region MCP Server (auto-start)

        private void StartMcpServer()
        {
            _mcpServer = new McpServer(this);
            _toolRegistry = new McpToolRegistry(_editorService, _parser);

            // Give the tool registry a reference back so it can access solution context and run indexing
            _toolRegistry.SetChatControl(this);

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
            LoadVersions();
            LoadSolutionHistory();
            DetectFromIde();
            StartMcpServer();
            LaunchClaude();
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
            string claudeCmd = $"cd '{safeWorkDir}'; claude{mcpArg} --strict-mcp-config --allowedTools '{allowedTools}'";
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
            _statusLabel.Text = text;
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

    internal class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        private static readonly SolidBrush _backgroundBrush = new SolidBrush(Color.FromArgb(30, 30, 30));

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.FillRectangle(_backgroundBrush, e.AffectedBounds);
        }
    }
}
