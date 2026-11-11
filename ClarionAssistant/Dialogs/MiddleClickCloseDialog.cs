using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClarionAssistant.Dialogs
{
    /// <summary>
    /// Asked before a middle-click (pressing the mouse wheel) on a tab closes it. Browsers close tabs that
    /// way, but not everyone knows it, and a tab closed by surprise takes its session with it. "Keep Tab"
    /// is the default, so an accidental click followed by Enter or Esc changes nothing. Ticking
    /// <see cref="DontAskAgain"/> and choosing Close Tab makes later middle-clicks close straight away.
    /// </summary>
    public class MiddleClickCloseDialog : Form
    {
        private readonly CheckBox _dontAsk;

        /// <summary>True when the user ticked "close without asking next time".</summary>
        public bool DontAskAgain { get { return _dontAsk.Checked; } }

        public MiddleClickCloseDialog(string tabName)
        {
            Text = "Close Tab";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16, 14, 16, 10);

            var root = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 3,
                Dock = DockStyle.Fill
            };

            var icon = new PictureBox
            {
                Image = SystemIcons.Question.ToBitmap(),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(0, 2, 12, 0)
            };

            var message = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(420, 0),
                Margin = new Padding(0, 0, 0, 10),
                Text = "You middle-clicked (pressed the mouse wheel on) the tab \"" + (tabName ?? "") + "\".\n\n"
                     + "A middle-click closes a tab. Close it now?"
            };

            _dontAsk = new CheckBox
            {
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 12),
                Text = "Close without asking next time"
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            var keep = new Button { Text = "Keep Tab", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
            var close = new Button { Text = "Close Tab", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.OK };
            buttons.Controls.Add(keep);
            buttons.Controls.Add(close);

            root.Controls.Add(icon, 0, 0);
            root.Controls.Add(message, 1, 0);
            root.Controls.Add(_dontAsk, 1, 1);
            root.Controls.Add(buttons, 0, 2);
            root.SetColumnSpan(buttons, 2);
            Controls.Add(root);

            // The click may well have been an accident: Enter and Esc both keep the tab.
            AcceptButton = keep;
            CancelButton = keep;
            ActiveControl = keep;
        }
    }
}
