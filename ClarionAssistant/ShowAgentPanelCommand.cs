using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// Command to show the Agent Panel pad from the Tools menu.
    /// </summary>
    public class ShowAgentPanelCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench != null)
                {
                    var getPadMethod = workbench.GetType().GetMethod("GetPad", new Type[] { typeof(Type) });
                    if (getPadMethod != null)
                    {
                        var pad = getPadMethod.Invoke(workbench, new object[] { typeof(AgentPanelPad) });
                        if (pad != null)
                        {
                            var bringToFrontMethod = pad.GetType().GetMethod("BringPadToFront");
                            bringToFrontMethod?.Invoke(pad, null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Error showing Agent Panel: " + ex.Message,
                    "Agent Panel",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
