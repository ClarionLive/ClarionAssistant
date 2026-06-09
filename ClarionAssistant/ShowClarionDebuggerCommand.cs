using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>Opens (and focuses) the CA Debugger pad.</summary>
    public class ShowClarionDebuggerCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            var pad = WorkbenchSingleton.Workbench.GetPad(typeof(ClarionDebuggerPad));
            if (pad != null) pad.BringPadToFront();
        }
    }
}
