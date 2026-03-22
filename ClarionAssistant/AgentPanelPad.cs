using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// Dockable pad showing connected MultiTerminal agents and their active tasks.
    /// </summary>
    public class AgentPanelPad : AbstractPadContent
    {
        private AgentPanelControl _control;

        public override Control Control
        {
            get
            {
                if (_control == null)
                {
                    _control = new AgentPanelControl();
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
