using System;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// Dockable pad for Claude Code chat in the Clarion IDE.
    /// </summary>
    public class ClaudeChatPad : AbstractPadContent
    {
        private AssistantChatControl _control;

        public override Control Control
        {
            get
            {
                if (_control == null)
                {
                    _control = new AssistantChatControl();
                }
                return _control;
            }
        }

        public override void Dispose()
        {
            if (_control != null)
            {
                _control.Dispose();
                _control = null;
            }
            base.Dispose();
        }

        public override void RedrawContent()
        {
            _control?.Refresh();
        }
    }
}
