using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>Shows the Modern Data pad (locals/globals for the active Modern Embeditor tab).</summary>
    public class ShowModernDataPadCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return;
                var getPad = workbench.GetType().GetMethod("GetPad", new Type[] { typeof(Type) });
                var pad = getPad?.Invoke(workbench, new object[] { typeof(ModernDataPad) });
                pad?.GetType().GetMethod("BringPadToFront")?.Invoke(pad, null);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Error showing CA Data Pad: " + ex.Message,
                    "Clarion Assistant", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
