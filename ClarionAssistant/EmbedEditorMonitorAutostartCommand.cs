using System;
using System.Diagnostics;
using ICSharpCode.Core;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command — starts the poll-detect embed monitor (ticket 4d16b53a) at workbench
    /// load, pane-independently (mirrors RightClickHookAutostartCommand / LspAutostartCommand).
    ///
    /// EmbedEditorMonitorService.Start() runs a self-deferring 1.5s UI-thread poll that auto-attaches the live
    /// CA Embeditor overlay whenever a native PWEE embed opens (incl. via Clarion's own "Embeditor Source"
    /// menu). Fully guarded — this MUST NOT throw at workbench load.
    /// </summary>
    public class EmbedEditorMonitorAutostartCommand : ICommand
    {
        private object _owner;
        public object Owner
        {
            get { return _owner; }
            set { _owner = value; var h = OwnerChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        public event EventHandler OwnerChanged;

        public void Run()
        {
            try { EmbedEditorMonitorService.Start(); }
            catch (Exception ex) { Debug.WriteLine("[EmbedEditorMonitorAutostart] start failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// /Workspace/Terminate command — stops the poll timer and latches shutdown at workbench close.
    /// Guarded; harmless if Start() never ran.
    /// </summary>
    public class EmbedEditorMonitorTerminateCommand : ICommand
    {
        private object _owner;
        public object Owner
        {
            get { return _owner; }
            set { _owner = value; var h = OwnerChanged; if (h != null) h(this, EventArgs.Empty); }
        }

        public event EventHandler OwnerChanged;

        public void Run()
        {
            try { EmbedEditorMonitorService.Terminate(); }
            catch (Exception ex) { Debug.WriteLine("[EmbedEditorMonitorTerminate] terminate failed: " + ex.Message); }
        }
    }
}
