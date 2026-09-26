using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// Main user control for the Class Helper addin.
    /// Provides: Analyze current file, show missing stubs, generate stubs, sync check.
    /// </summary>
    public partial class ClassHelperControl : UserControl
    {
        private readonly EditorService _editorService;
        private readonly SettingsService _settingsService;
        private readonly ClarionClassParser _parser;
        private McpServer _mcpServer;
        private ToolStripButton _btnMcp;

        // UI Controls
        private ToolStrip _toolbar;
        private SplitContainer _splitContainer;
        private TreeView _classTree;
        private RichTextBox _outputBox;
        private StatusStrip _statusBar;
        private ToolStripStatusLabel _statusLabel;

        public ClassHelperControl()
        {
            InitializeComponent();
            _editorService = new EditorService();
            _settingsService = new SettingsService();
            _parser = new ClarionClassParser();
            BuildUI();
        }

        private void BuildUI()
        {
            this.SuspendLayout();

            // Toolbar
            _toolbar = new ToolStrip();
            _toolbar.GripStyle = ToolStripGripStyle.Hidden;

            var btnAnalyze = new ToolStripButton("Analyze", null, OnAnalyzeClick)
            {
                ToolTipText = "Analyze the current .inc or .clw file for CLASS definitions"
            };

            var btnGenerateStubs = new ToolStripButton("Generate Stubs", null, OnGenerateStubsClick)
            {
                ToolTipText = "Generate missing method implementation stubs in the .clw file"
            };

            var btnSyncCheck = new ToolStripButton("Sync Check", null, OnSyncCheckClick)
            {
                ToolTipText = "Compare .inc declarations with .clw implementations"
            };

            var btnGenerateClw = new ToolStripButton("New .clw", null, OnGenerateClwClick)
            {
                ToolTipText = "Generate a complete .clw implementation file from the .inc"
            };

            _btnMcp = new ToolStripButton("Start MCP", null, OnMcpToggleClick)
            {
                ToolTipText = "Start/Stop the MCP server for Claude Code integration"
            };

            _toolbar.Items.AddRange(new ToolStripItem[] {
                btnAnalyze,
                new ToolStripSeparator(),
                btnGenerateStubs,
                btnSyncCheck,
                new ToolStripSeparator(),
                btnGenerateClw,
                new ToolStripSeparator(),
                _btnMcp
            });

            // Split container: tree on left, output on right
            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 200
            };

            // Class tree view
            _classTree = new TreeView
            {
                Dock = DockStyle.Fill,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                Font = new Font("Consolas", 9f)
            };
            _classTree.NodeMouseDoubleClick += OnTreeNodeDoubleClick;

            // Output box
            _outputBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                WordWrap = false
            };

            _splitContainer.Panel1.Controls.Add(_classTree);
            _splitContainer.Panel2.Controls.Add(_outputBox);

            // Status bar
            _statusBar = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("Ready — open a .inc or .clw file and click Analyze");
            _statusBar.Items.Add(_statusLabel);

            // Layout
            this.Controls.Add(_splitContainer);
            this.Controls.Add(_toolbar);
            this.Controls.Add(_statusBar);

            _toolbar.Dock = DockStyle.Top;
            _statusBar.Dock = DockStyle.Bottom;

            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #region Button Handlers

        private void OnAnalyzeClick(object sender, EventArgs e)
        {
            try
            {
                _classTree.Nodes.Clear();
                _outputBox.Clear();

                // Diagnostic: try to discover what the IDE exposes
                string filePath = _editorService.GetActiveDocumentPath();

                if (string.IsNullOrEmpty(filePath))
                {
                    AppendOutput("EditorService.GetActiveDocumentPath() returned null.", Color.Yellow);
                    AppendOutput("Running IDE diagnostics...\n", Color.Gray);

                    // Run diagnostics to help debug
                    string diagnostics = _editorService.GetDiagnosticInfo();
                    AppendOutput(diagnostics, Color.Gray);

                    SetStatus("Could not detect active document — see diagnostics output.");
                    return;
                }

                AppendOutput($"Detected file: {filePath}\n", Color.Gray);

                string ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".inc")
                {
                    AnalyzeIncFile(filePath);
                }
                else if (ext == ".clw")
                {
                    AnalyzeClwFile(filePath);
                }
                else
                {
                    SetStatus($"Not a Clarion class file: {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}\n{ex.StackTrace}", Color.Red);
            }
        }

        private void OnGenerateStubsClick(object sender, EventArgs e)
        {
            try
            {
                string filePath = _editorService.GetActiveDocumentPath();
                if (string.IsNullOrEmpty(filePath))
                {
                    SetStatus("No active document.");
                    return;
                }

                // Determine .inc and .clw paths
                string incPath, clwPath;
                string ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".inc")
                {
                    incPath = filePath;
                    var classes = _parser.ParseIncFile(incPath);
                    if (classes.Count == 0) { SetStatus("No CLASS found in .inc file."); return; }
                    clwPath = _parser.ResolveClwPath(incPath, classes[0]);
                }
                else if (ext == ".clw")
                {
                    clwPath = filePath;
                    incPath = _parser.FindIncFromClw(clwPath);
                    if (incPath == null) { SetStatus("Cannot find paired .inc file."); return; }
                }
                else
                {
                    SetStatus("Open a .inc or .clw file first.");
                    return;
                }

                var classes2 = _parser.ParseIncFile(incPath);
                if (classes2.Count == 0) { SetStatus("No CLASS found."); return; }

                // Generate stubs for each class
                int totalGenerated = 0;
                foreach (var classDef in classes2)
                {
                    string resolvedClw = _parser.ResolveClwPath(incPath, classDef);
                    if (!File.Exists(resolvedClw))
                    {
                        AppendOutput($"  .clw file not found: {resolvedClw}", Color.Yellow);
                        continue;
                    }

                    var syncResult = _parser.CompareIncWithClw(incPath, resolvedClw, classDef.ClassName);
                    if (syncResult.MissingImplementations.Count == 0)
                    {
                        AppendOutput($"  {classDef.ClassName}: All methods already implemented.", Color.LightGreen);
                        continue;
                    }

                    string stubs = _parser.GenerateAllMissingStubs(syncResult);

                    // Confirm with user
                    var result = MessageBox.Show(
                        $"Generate {syncResult.MissingImplementations.Count} method stub(s) for {classDef.ClassName}?\n\n" +
                        $"Methods:\n" +
                        string.Join("\n", syncResult.MissingImplementations.Select(m => $"  - {m.Name}")) +
                        $"\n\nStubs will be appended to:\n{resolvedClw}",
                        "Generate Method Stubs",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.OK)
                    {
                        var insertResult = _editorService.AppendTextToFile(resolvedClw, stubs);
                        if (insertResult.Success)
                        {
                            totalGenerated += syncResult.MissingImplementations.Count;
                            AppendOutput($"  {classDef.ClassName}: Generated {syncResult.MissingImplementations.Count} stub(s).", Color.LightGreen);

                            foreach (var m in syncResult.MissingImplementations)
                            {
                                AppendOutput($"    + {classDef.ClassName}.{m.Name} {m.FullSignature}", Color.White);
                            }
                        }
                        else
                        {
                            AppendOutput($"  Error: {insertResult.ErrorMessage}", Color.Red);
                        }
                    }
                }

                SetStatus($"Generated {totalGenerated} method stub(s).");
                AppendOutput($"\nDone. Generated {totalGenerated} stub(s) total.", Color.Cyan);
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}", Color.Red);
            }
        }

        private void OnSyncCheckClick(object sender, EventArgs e)
        {
            try
            {
                _outputBox.Clear();

                string filePath = _editorService.GetActiveDocumentPath();
                if (string.IsNullOrEmpty(filePath))
                {
                    AppendOutput("No active document detected.", Color.Red);
                    return;
                }

                AppendOutput($"Active file: {filePath}", Color.Gray);

                string incPath, clwPath;
                string ext = Path.GetExtension(filePath).ToLower();

                if (ext == ".inc")
                {
                    incPath = filePath;
                    AppendOutput($"Parsing .inc: {incPath}", Color.Gray);
                    var classes = _parser.ParseIncFile(incPath);
                    if (classes.Count == 0) { AppendOutput("No CLASS found in .inc file.", Color.Red); return; }
                    clwPath = _parser.ResolveClwPath(incPath, classes[0]);
                }
                else if (ext == ".clw")
                {
                    clwPath = filePath;
                    AppendOutput($"Looking for INCLUDE statement in .clw...", Color.Gray);
                    incPath = _parser.FindIncFromClw(clwPath);
                    if (incPath == null)
                    {
                        AppendOutput($"Cannot find paired .inc file.", Color.Red);
                        AppendOutput($"Searched in: {Path.GetDirectoryName(clwPath)}", Color.Yellow);
                        return;
                    }
                    AppendOutput($"Found .inc: {incPath}", Color.Gray);
                }
                else
                {
                    AppendOutput($"Not a .inc or .clw file: {filePath}", Color.Red);
                    return;
                }

                AppendOutput($"\nSync Check: {Path.GetFileName(incPath)} <-> {Path.GetFileName(clwPath)}\n", Color.Cyan);

                var classes2 = _parser.ParseIncFile(incPath);
                foreach (var classDef in classes2)
                {
                    string resolvedClw = _parser.ResolveClwPath(incPath, classDef);
                    var syncResult = _parser.CompareIncWithClw(incPath, resolvedClw, classDef.ClassName);

                    AppendOutput($"\n{syncResult.ClassName}:", Color.White);

                    if (syncResult.IsInSync)
                    {
                        AppendOutput($"  All {syncResult.ImplementedMethods.Count} methods in sync.", Color.LightGreen);
                    }
                    else
                    {
                        if (syncResult.MissingImplementations.Count > 0)
                        {
                            AppendOutput($"\n  Missing implementations ({syncResult.MissingImplementations.Count}):", Color.Yellow);
                            foreach (var m in syncResult.MissingImplementations)
                            {
                                AppendOutput($"    - {m.Name} {m.FullSignature}", Color.Yellow);
                            }
                        }

                        if (syncResult.OrphanedImplementations.Count > 0)
                        {
                            AppendOutput($"\n  Orphaned implementations ({syncResult.OrphanedImplementations.Count}):", Color.Orange);
                            foreach (var m in syncResult.OrphanedImplementations)
                            {
                                AppendOutput($"    - {m.ClassName}.{m.MethodName} (line {m.LineNumber + 1})", Color.Orange);
                            }
                        }

                        AppendOutput($"\n  Implemented: {syncResult.ImplementedMethods.Count}", Color.LightGreen);
                    }
                }

                SetStatus("Sync check complete.");
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}", Color.Red);
            }
        }

        private void OnGenerateClwClick(object sender, EventArgs e)
        {
            try
            {
                string filePath = _editorService.GetActiveDocumentPath();
                if (string.IsNullOrEmpty(filePath) || !filePath.ToLower().EndsWith(".inc"))
                {
                    SetStatus("Open a .inc file first.");
                    return;
                }

                var classes = _parser.ParseIncFile(filePath);
                if (classes.Count == 0) { SetStatus("No CLASS found in .inc file."); return; }

                var classDef = classes[0];
                string clwPath = _parser.ResolveClwPath(filePath, classDef);

                if (File.Exists(clwPath))
                {
                    var result = MessageBox.Show(
                        $"{Path.GetFileName(clwPath)} already exists.\n\nOverwrite it with a fresh implementation file?",
                        "Generate .clw File",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (result != DialogResult.Yes) return;
                }

                string content = _parser.GenerateClwFile(classDef);

                var confirmResult = MessageBox.Show(
                    $"Generate {Path.GetFileName(clwPath)} with {classDef.Methods.Count} method stub(s) for {classDef.ClassName}?",
                    "Generate .clw File",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question);

                if (confirmResult == DialogResult.OK)
                {
                    // Clarion source rules (GH #203): CRLF, no BOM, and an overwritten .clw keeps its
                    // own encoding; a new one is ANSI.
                    ClarionSourceText.WriteFile(clwPath, content);
                    AppendOutput($"Generated: {clwPath}", Color.LightGreen);
                    AppendOutput($"  CLASS: {classDef.ClassName}", Color.White);
                    AppendOutput($"  Methods: {classDef.Methods.Count}", Color.White);
                    SetStatus($"Generated {Path.GetFileName(clwPath)} with {classDef.Methods.Count} method stubs.");

                    // Open the generated file
                    _editorService.NavigateToFileAndLine(clwPath, 1);
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}", Color.Red);
            }
        }

        private void OnMcpToggleClick(object sender, EventArgs e)
        {
            try
            {
                if (_mcpServer != null && _mcpServer.IsRunning)
                {
                    _mcpServer.Stop();
                    _mcpServer.Dispose();
                    _mcpServer = null;
                    _btnMcp.Text = "Start MCP";
                    AppendOutput("MCP server stopped.", Color.Gray);
                    SetStatus("MCP server stopped.");
                }
                else
                {
                    _mcpServer = new McpServer(this, _settingsService);
                    var toolRegistry = new McpToolRegistry(_editorService, _parser);
                    _mcpServer.SetToolRegistry(toolRegistry);

                    _mcpServer.OnToolCall += (name, summary) =>
                    {
                        AppendOutput($"  MCP tool: {name} -> {summary}", Color.FromArgb(180, 180, 255));
                    };

                    _mcpServer.OnError += (msg) =>
                    {
                        AppendOutput($"  MCP error: {msg}", Color.Red);
                    };

                    if (_mcpServer.Start())
                    {
                        string configPath = _mcpServer.WriteMcpConfigFile();
                        _btnMcp.Text = $"MCP :{_mcpServer.Port}";
                        _outputBox.Clear();
                        AppendOutput($"MCP server running on port {_mcpServer.Port}", Color.LightGreen);
                        AppendOutput($"Tools registered: {toolRegistry.GetToolCount()}", Color.Gray);
                        AppendOutput($"", Color.Gray);
                        AppendOutput($"Config written to: {configPath}", Color.Gray);
                        AppendOutput($"", Color.Gray);
                        AppendOutput($"To connect Claude Code, run:", Color.Cyan);
                        AppendOutput($"  claude --mcp-config \"{configPath}\"", Color.White);
                        AppendOutput($"", Color.Gray);
                        AppendOutput($"Or add to your .claude.json:", Color.Cyan);
                        AppendOutput($"  {_mcpServer.GenerateMcpConfig()}", Color.White);
                        SetStatus($"MCP server running on localhost:{_mcpServer.Port} with {toolRegistry.GetToolCount()} tools");
                    }
                    else
                    {
                        AppendOutput("Failed to start MCP server.", Color.Red);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"MCP error: {ex.Message}", Color.Red);
            }
        }

        #endregion

        #region Analysis

        private void AnalyzeIncFile(string filePath)
        {
            var classes = _parser.ParseIncFile(filePath);

            if (classes.Count == 0)
            {
                SetStatus($"No CLASS definitions found in {Path.GetFileName(filePath)}");
                return;
            }

            AppendOutput($"File: {Path.GetFileName(filePath)}\n", Color.Cyan);

            foreach (var classDef in classes)
            {
                // Add to tree
                var classNode = new TreeNode($"{classDef.ClassName} ({classDef.Methods.Count} methods)")
                {
                    Tag = classDef,
                    ImageIndex = 0
                };

                foreach (var method in classDef.Methods)
                {
                    string attrs = method.Attributes.Count > 0
                        ? $" [{string.Join(", ", method.Attributes)}]"
                        : "";

                    var methodNode = new TreeNode($"{method.Name} {method.FullSignature}{attrs}")
                    {
                        Tag = method
                    };
                    classNode.Nodes.Add(methodNode);
                }

                _classTree.Nodes.Add(classNode);
                classNode.Expand();

                // Output summary
                AppendOutput($"CLASS: {classDef.ClassName}", Color.White);
                if (!string.IsNullOrEmpty(classDef.ParentClass))
                    AppendOutput($"  Inherits: {classDef.ParentClass}", Color.LightBlue);
                AppendOutput($"  Module: {classDef.ModuleFile}", Color.Gray);
                AppendOutput($"  Methods: {classDef.Methods.Count}", Color.Gray);
                AppendOutput($"  Data members: {classDef.DataMembers.Count}", Color.Gray);

                // Check for .clw file
                string clwPath = _parser.ResolveClwPath(filePath, classDef);
                if (File.Exists(clwPath))
                {
                    var syncResult = _parser.CompareIncWithClw(filePath, clwPath, classDef.ClassName);
                    AppendOutput($"  Implemented: {syncResult.ImplementedMethods.Count}/{classDef.Methods.Count}", Color.LightGreen);

                    if (syncResult.MissingImplementations.Count > 0)
                    {
                        AppendOutput($"  Missing: {syncResult.MissingImplementations.Count}", Color.Yellow);
                        foreach (var m in syncResult.MissingImplementations)
                        {
                            AppendOutput($"    - {m.Name}", Color.Yellow);
                        }
                    }
                }
                else
                {
                    AppendOutput($"  .clw file not found: {clwPath}", Color.Yellow);
                    AppendOutput($"  Use 'New .clw' to generate it.", Color.Gray);
                }

                AppendOutput("", Color.White);
            }

            SetStatus($"Found {classes.Count} class(es) with {classes.Sum(c => c.Methods.Count)} methods total.");
        }

        private void AnalyzeClwFile(string filePath)
        {
            var implementations = _parser.ParseClwFile(filePath);

            if (implementations.Count == 0)
            {
                SetStatus($"No method implementations found in {Path.GetFileName(filePath)}");
                return;
            }

            // Group by class
            var byClass = implementations.GroupBy(m => m.ClassName, StringComparer.OrdinalIgnoreCase);

            AppendOutput($"File: {Path.GetFileName(filePath)}\n", Color.Cyan);

            foreach (var group in byClass)
            {
                var classNode = new TreeNode($"{group.Key} ({group.Count()} implementations)")
                {
                    Tag = group.Key
                };

                foreach (var impl in group)
                {
                    var node = new TreeNode($"{impl.MethodName} (line {impl.LineNumber + 1})")
                    {
                        Tag = impl
                    };
                    classNode.Nodes.Add(node);
                }

                _classTree.Nodes.Add(classNode);
                classNode.Expand();

                AppendOutput($"CLASS: {group.Key} — {group.Count()} implementations", Color.White);
            }

            // Try to find paired .inc file for sync check
            string incPath = _parser.FindIncFromClw(filePath);
            if (incPath != null)
            {
                AppendOutput($"\nPaired .inc: {Path.GetFileName(incPath)}", Color.Gray);
                AppendOutput("Use 'Sync Check' to compare declarations vs implementations.", Color.Gray);
            }

            SetStatus($"Found {implementations.Count} implementation(s) across {byClass.Count()} class(es).");
        }

        #endregion

        #region Tree Events

        private void OnTreeNodeDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Tag is MethodImplementation impl)
            {
                // Navigate to implementation line in .clw
                string filePath = _editorService.GetActiveDocumentPath();
                if (filePath != null)
                {
                    _editorService.NavigateToFileAndLine(filePath, impl.LineNumber + 1);
                }
            }
            else if (e.Node.Tag is MethodDeclaration decl)
            {
                // Navigate to declaration line in .inc
                string filePath = _editorService.GetActiveDocumentPath();
                if (filePath != null)
                {
                    _editorService.NavigateToFileAndLine(filePath, decl.LineNumber + 1);
                }
            }
        }

        #endregion

        #region Helpers

        private void AppendOutput(string text, Color color)
        {
            _outputBox.SelectionStart = _outputBox.TextLength;
            _outputBox.SelectionLength = 0;
            _outputBox.SelectionColor = color;
            _outputBox.AppendText(text + "\r\n");
            _outputBox.ScrollToCaret();
        }

        private void SetStatus(string text)
        {
            _statusLabel.Text = text;
        }

        public void RefreshContent()
        {
            // Could auto-analyze when the active document changes
        }

        #endregion
    }
}
