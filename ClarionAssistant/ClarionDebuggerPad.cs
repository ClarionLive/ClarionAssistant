using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>Dockable pad hosting the CA Debugger front-end (Phase 1e).</summary>
    public class ClarionDebuggerPad : AbstractPadContent
    {
        private ClarionDebuggerControl _control;

        public override Control Control
        {
            get { return _control ?? (_control = new ClarionDebuggerControl()); }
        }

        public override void Dispose()
        {
            if (_control != null) { _control.Dispose(); _control = null; }
            base.Dispose();
        }
    }
}
