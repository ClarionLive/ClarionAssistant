using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Thin status bar docked at the bottom of the terminal content area.
    /// Shows LSP diagnostics count (clickable → opens DiagnosticsForm) and
    /// recent LSP activity. Hidden when LSP is not running.
    /// </summary>
    public class LspStatusBar : Panel
    {
        private Label _diagLabel;
        private Label _activityLabel;
        private Label _closeButton;
        private bool _isDark = true;

        /// <summary>Fired when the user clicks the diagnostics count to open the full list.</summary>
        public event EventHandler DiagnosticsClicked;

        public LspStatusBar()
        {
            Height = 20;
            Dock = DockStyle.Bottom;
            Visible = false; // hidden until LSP data arrives

            _diagLabel = new Label
            {
                AutoSize = false,
                Width = 90,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 4, 0),
                Font = new Font("Cascadia Code", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Text = ""
            };
            _diagLabel.Click += (s, e) => DiagnosticsClicked?.Invoke(this, EventArgs.Empty);

            _closeButton = new Label
            {
                AutoSize = false,
                Width = 20,
                Dock = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8f),
                Text = "\u2715",
                Cursor = Cursors.Hand
            };
            _closeButton.Click += (s, e) => { Visible = false; };

            _activityLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 4, 0),
                Font = new Font("Cascadia Code", 8f),
                Text = ""
            };

            Controls.Add(_activityLabel);
            Controls.Add(_closeButton);
            Controls.Add(_diagLabel);

            ApplyTheme(true);
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (isDark)
            {
                BackColor = Color.FromArgb(24, 24, 37);
                _diagLabel.BackColor = Color.FromArgb(24, 24, 37);
                _activityLabel.BackColor = Color.FromArgb(24, 24, 37);
                _activityLabel.ForeColor = Color.FromArgb(88, 91, 112);
                _closeButton.BackColor = Color.FromArgb(24, 24, 37);
                _closeButton.ForeColor = Color.FromArgb(88, 91, 112);
            }
            else
            {
                BackColor = Color.FromArgb(230, 233, 239);
                _diagLabel.BackColor = Color.FromArgb(230, 233, 239);
                _activityLabel.BackColor = Color.FromArgb(230, 233, 239);
                _activityLabel.ForeColor = Color.FromArgb(140, 143, 161);
                _closeButton.BackColor = Color.FromArgb(230, 233, 239);
                _closeButton.ForeColor = Color.FromArgb(140, 143, 161);
            }
        }

        /// <summary>
        /// Update the diagnostics count display. Sets color based on severity.
        /// </summary>
        public void SetDiagnostics(int errors, int warnings, bool hidden)
        {
            if (hidden)
            {
                Visible = false;
                return;
            }

            Visible = true;

            if (errors > 0)
            {
                _diagLabel.Text = "\u25CF " + errors + "E" + (warnings > 0 ? " " + warnings + "W" : "");
                _diagLabel.ForeColor = _isDark
                    ? Color.FromArgb(243, 139, 168)   // Catppuccin red
                    : Color.FromArgb(210, 15, 57);
            }
            else if (warnings > 0)
            {
                _diagLabel.Text = "\u25CF " + warnings + "W";
                _diagLabel.ForeColor = _isDark
                    ? Color.FromArgb(250, 179, 135)   // Catppuccin peach
                    : Color.FromArgb(254, 100, 11);
            }
            else
            {
                _diagLabel.Text = "\u2713 OK";
                _diagLabel.ForeColor = _isDark
                    ? Color.FromArgb(166, 227, 161)   // Catppuccin green
                    : Color.FromArgb(64, 160, 43);
            }
        }

        /// <summary>
        /// Update the activity strip showing recent LSP queries.
        /// </summary>
        public void SetActivity(string[] items)
        {
            if (items == null || items.Length == 0)
            {
                _activityLabel.Text = "";
                return;
            }
            _activityLabel.Text = string.Join("  ", items);
        }
    }
}
