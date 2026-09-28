using System;
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    /// <summary>Small helpers for acting on the IDE from a WebView2 page's message callback.</summary>
    internal static class IdeUi
    {
        /// <summary>
        /// Run <paramref name="work"/> OUT of the WebView2 message callback, on the next UI turn of
        /// <paramref name="owner"/>, with the IDE main window activated first. A WebView2 holding focus while
        /// the IDE opens a document can deadlock the native embeditor close, and modal pickers must not run
        /// re-entrantly inside the message pump. With no owner the work runs now; with an owner that has no
        /// handle (or is gone) it is dropped. Failures are logged under <paramref name="logTag"/>.
        /// </summary>
        public static void DeferWithMainFormActivated(Control owner, Action work, string logTag)
        {
            if (owner == null) { Run(work, logTag); return; }
            try
            {
                owner.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        var mainForm = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench as Form;
                        if (mainForm != null) { mainForm.Activate(); Application.DoEvents(); }
                    }
                    catch { }
                    Run(work, logTag);
                }));
            }
            catch (InvalidOperationException) { }   // no handle yet, or disposed (ObjectDisposedException derives from it)
        }

        private static void Run(Action work, string logTag)
        {
            try { work(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[" + logTag + "] deferred action: " + ex.Message); }
        }
    }
}
