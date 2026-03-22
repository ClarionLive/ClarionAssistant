using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ClarionAssistant.Services;

namespace ClarionAssistant.Dialogs
{
    public class ClaudeChatSettingsDialog : Form
    {
        private readonly SettingsService _settings;

        private NumericUpDown _fontSizeInput;
        private ComboBox _modelCombo;
        private TextBox _workingDirInput;
        private Button _browseButton;
        private CheckBox _multiTerminalCheck;
        private Label _multiTerminalStatus;
        private TextBox _agentNameInput;
        private Button _buildLibButton;
        private Label _libStatus;
        private Button _okButton;
        private Button _cancelButton;

        private static readonly string MultiTerminalMcpPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "multiterminal", "mcp", "index.js");

        public ClaudeChatSettingsDialog(SettingsService settings)
        {
            _settings = settings;
            InitializeComponents();
            LoadSettings();
        }

        private void InitializeComponents()
        {
            Text = "Claude Chat Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(450, 420);
            BackColor = Color.FromArgb(45, 45, 45);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            int labelX = 20;
            int inputX = 160;
            int inputW = 250;
            int y = 20;
            int rowH = 35;

            // Font Size
            var fontLabel = new Label
            {
                Text = "Font Size:",
                Location = new Point(labelX, y + 3),
                AutoSize = true
            };
            _fontSizeInput = new NumericUpDown
            {
                Location = new Point(inputX, y),
                Width = 80,
                Minimum = 6,
                Maximum = 32,
                Value = 10,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };

            y += rowH;

            // Model
            var modelLabel = new Label
            {
                Text = "Claude Model:",
                Location = new Point(labelX, y + 3),
                AutoSize = true
            };
            _modelCombo = new ComboBox
            {
                Location = new Point(inputX, y),
                Width = inputW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _modelCombo.Items.AddRange(new object[] { "sonnet", "opus", "haiku" });

            y += rowH;

            // Working Directory
            var dirLabel = new Label
            {
                Text = "Working Directory:",
                Location = new Point(labelX, y + 3),
                AutoSize = true
            };
            _workingDirInput = new TextBox
            {
                Location = new Point(inputX, y),
                Width = inputW - 35,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _browseButton = new Button
            {
                Text = "...",
                Location = new Point(inputX + inputW - 30, y - 1),
                Width = 30,
                Height = _workingDirInput.Height + 2,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White
            };
            _browseButton.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = _workingDirInput.Text;
                    if (dlg.ShowDialog() == DialogResult.OK)
                        _workingDirInput.Text = dlg.SelectedPath;
                }
            };

            y += rowH + 10;

            // === MultiTerminal Section ===
            var separator = new Label
            {
                Text = "MultiTerminal Integration",
                Location = new Point(labelX, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 180, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            y += 22;

            _multiTerminalCheck = new CheckBox
            {
                Text = "Enable MultiTerminal connection",
                Location = new Point(labelX, y),
                AutoSize = true,
                ForeColor = Color.White
            };
            _multiTerminalCheck.CheckedChanged += (s, e) => UpdateMultiTerminalStatus();

            bool mtAvailable = File.Exists(MultiTerminalMcpPath);
            _multiTerminalStatus = new Label
            {
                Text = mtAvailable ? "Detected" : "Not installed",
                Location = new Point(inputX + 120, y + 2),
                AutoSize = true,
                ForeColor = mtAvailable ? Color.FromArgb(120, 200, 120) : Color.FromArgb(200, 150, 80),
                Font = new Font("Segoe UI", 8f)
            };

            y += rowH - 5;

            var agentLabel = new Label
            {
                Text = "Agent Name:",
                Location = new Point(labelX + 20, y + 3),
                AutoSize = true
            };
            _agentNameInput = new TextBox
            {
                Location = new Point(inputX, y),
                Width = 150,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            y += rowH + 10;

            // === Library CodeGraph Section ===
            var libSeparator = new Label
            {
                Text = "Library CodeGraph",
                Location = new Point(labelX, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 180, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            y += 22;

            _buildLibButton = new Button
            {
                Text = "Build",
                Location = new Point(labelX, y),
                Width = 80,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White
            };
            _buildLibButton.Click += OnBuildLibrary;

            _libStatus = new Label
            {
                Text = LibraryIndexer.GetStatus(),
                Location = new Point(labelX + 90, y + 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180),
                Font = new Font("Segoe UI", 8f)
            };

            y += rowH + 15;

            // OK / Cancel
            _okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(230, y),
                Width = 90,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White
            };
            _okButton.Click += OnOk;

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(330, y),
                Width = 90,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 70, 70),
                ForeColor = Color.White
            };

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            Controls.AddRange(new Control[]
            {
                fontLabel, _fontSizeInput,
                modelLabel, _modelCombo,
                dirLabel, _workingDirInput, _browseButton,
                separator,
                _multiTerminalCheck, _multiTerminalStatus,
                agentLabel, _agentNameInput,
                libSeparator, _buildLibButton, _libStatus,
                _okButton, _cancelButton
            });
        }

        private void LoadSettings()
        {
            // Font size
            string fontSize = _settings.Get("Claude.FontSize");
            decimal size;
            if (!string.IsNullOrEmpty(fontSize) && decimal.TryParse(fontSize, out size))
                _fontSizeInput.Value = Math.Max(6, Math.Min(32, size));
            else
                _fontSizeInput.Value = 14;

            // Model
            string model = _settings.Get("Claude.Model") ?? "sonnet";
            int idx = _modelCombo.Items.IndexOf(model);
            _modelCombo.SelectedIndex = idx >= 0 ? idx : 0;

            // Working directory
            _workingDirInput.Text = _settings.Get("Claude.WorkingDirectory")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // MultiTerminal
            bool mtAvailable = File.Exists(MultiTerminalMcpPath);
            string mtEnabled = _settings.Get("MultiTerminal.Enabled");
            // Auto-enable if available and no explicit setting
            if (mtEnabled == null)
                _multiTerminalCheck.Checked = mtAvailable;
            else
                _multiTerminalCheck.Checked = mtEnabled.Equals("true", StringComparison.OrdinalIgnoreCase);

            _multiTerminalCheck.Enabled = mtAvailable;

            _agentNameInput.Text = _settings.Get("MultiTerminal.AgentName") ?? "ClarionIDE";

            UpdateMultiTerminalStatus();
        }

        private void UpdateMultiTerminalStatus()
        {
            _agentNameInput.Enabled = _multiTerminalCheck.Checked;
        }

        private void OnOk(object sender, EventArgs e)
        {
            _settings.Set("Claude.FontSize", _fontSizeInput.Value.ToString());
            _settings.Set("Claude.Model", _modelCombo.SelectedItem?.ToString() ?? "sonnet");
            _settings.Set("Claude.WorkingDirectory", _workingDirInput.Text);
            _settings.Set("MultiTerminal.Enabled", _multiTerminalCheck.Checked.ToString().ToLower());
            _settings.Set("MultiTerminal.AgentName", _agentNameInput.Text);
        }

        private void OnBuildLibrary(object sender, EventArgs e)
        {
            var info = ClarionVersionService.Detect();
            var config = info?.GetCurrentConfig();
            string clarionRoot = config?.RootPath;

            if (string.IsNullOrEmpty(clarionRoot))
            {
                MessageBox.Show("Could not detect Clarion installation path.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _buildLibButton.Enabled = false;
            _libStatus.Text = "Building...";
            _libStatus.ForeColor = Color.FromArgb(255, 200, 100);
            _libStatus.Refresh();

            var result = LibraryIndexer.Build(clarionRoot);

            _buildLibButton.Enabled = true;
            if (result.Success)
            {
                _libStatus.Text = result.SymbolCount + " symbols indexed";
                _libStatus.ForeColor = Color.FromArgb(120, 200, 120);
            }
            else
            {
                _libStatus.Text = "Error: " + result.Error;
                _libStatus.ForeColor = Color.FromArgb(255, 120, 120);
            }
        }

        public float FontSize { get { return (float)_fontSizeInput.Value; } }
        public string Model { get { return _modelCombo.SelectedItem?.ToString() ?? "sonnet"; } }
        public string WorkingDirectory { get { return _workingDirInput.Text; } }
        public bool MultiTerminalEnabled { get { return _multiTerminalCheck.Checked; } }
        public string AgentName { get { return _agentNameInput.Text; } }

        public static bool IsMultiTerminalAvailable()
        {
            return File.Exists(MultiTerminalMcpPath);
        }

        public static string GetMultiTerminalMcpPath()
        {
            return MultiTerminalMcpPath;
        }
    }
}
