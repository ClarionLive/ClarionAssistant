using System;
using System.Drawing;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;

namespace ClarionAssistant.Options
{
    /// <summary>
    /// IDE Options pane: Options &gt; Clarion Assistant &gt; Chat Transcripts. One switch: save each CA chat as a
    /// Markdown file in the solution folder's <c>.CAChats</c> when its tab closes or the IDE does.
    ///
    /// Same contract as <see cref="CAFindOptionPanel"/>: AbstractOptionPanel IS a UserControl;
    /// LoadPanelContents() fires on first display; StorePanelContents() fires ONLY on dialog OK.
    /// Persistence via <see cref="CaChatTranscriptSettings"/> (shared settings.txt).
    /// </summary>
    public class CAChatTranscriptsOptionPanel : AbstractOptionPanel
    {
        private CheckBox _chkSave;
        private bool _built;

        public override void LoadPanelContents()
        {
            BuildUi();
            _chkSave.Checked = CaChatTranscriptSettings.Enabled;
        }

        public override bool StorePanelContents()
        {
            // Only called on OK.
            CaChatTranscriptSettings.Enabled = _chkSave.Checked;
            return true;   // allow the dialog to close
        }

        private void BuildUi()
        {
            if (_built) return;
            _built = true;

            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12),
            };

            _chkSave = new CheckBox
            {
                Text = "Save each chat as a Markdown file in the solution folder (.CAChats)",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
            };

            var lblNote = new Label
            {
                Text = "When a CA chat tab closes, or Clarion closes, the conversation is written to\r\n" +
                       "<solution folder>\\.CAChats\\session_<id>_<first day>_to_<last day>.md.\r\n" +
                       "A chat where you never typed a message is not saved. One chat is one file: saving it\r\n" +
                       "again after it reached another day replaces the earlier file.\r\n\r\n" +
                       "The file holds everything typed or shown in the chat, passwords and keys included.\r\n" +
                       "A .gitignore with * is written into the folder so it is not committed by accident.\r\n\r\n" +
                       "Takes effect the next time a chat tab closes. If Clarion crashes or is killed, chats still\r\n" +
                       "open at that moment are not saved.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 0),
            };

            layout.Controls.Add(_chkSave);
            layout.Controls.Add(lblNote);
            Controls.Add(layout);
        }
    }
}
