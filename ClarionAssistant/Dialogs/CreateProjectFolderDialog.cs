using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClarionAssistant.Dialogs
{
    /// <summary>
    /// Opening a COM / Addin project whose folder doesn't exist yet (d4e941e3). A project is usually added to the
    /// list before anything is on disk, so offer to create the folder instead of a dead-end "not found" box.
    /// OK = Create Folder. MessageBox can't relabel its buttons, hence a small form.
    /// </summary>
    public class CreateProjectFolderDialog : Form
    {
        public CreateProjectFolderDialog(string projectName, string folder)
        {
            Text = "Open Project";
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
                RowCount = 2,
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
                Margin = new Padding(0, 0, 0, 12),
                Text = "The folder for \"" + (projectName ?? "") + "\" doesn't exist yet:\n\n"
                     + (string.IsNullOrEmpty(folder) ? "(no folder set)" : folder) + "\n\n"
                     + "Create it now? Clarion Assistant will then open the project there."
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            var create = new Button { Text = "Create Folder", AutoSize = true, MinimumSize = new Size(100, 0), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(create);

            root.Controls.Add(icon, 0, 0);
            root.Controls.Add(message, 1, 0);
            root.Controls.Add(buttons, 0, 1);
            root.SetColumnSpan(buttons, 2);
            Controls.Add(root);

            AcceptButton = create;
            CancelButton = cancel;
            // A folder that can't be created (no path set) leaves only Cancel.
            create.Enabled = !string.IsNullOrEmpty(folder);
        }
    }
}
