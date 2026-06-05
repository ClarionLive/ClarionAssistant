using System;
using System.Diagnostics;
using ICSharpCode.Core;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command — starts the right-click → "Open in Modern Embeditor" native-hook
    /// service (ticket 4b82f1de) at workbench load, pane-independently (mirrors LspAutostartCommand).
    ///
    /// RightClickHookService.Start() installs a self-healing UI-thread timer + SolutionLoaded/Closed and
    /// ActiveWorkbenchWindowChanged nudges that re-arm the tid-keyed hook roster. It is fully guarded —
    /// this MUST NOT throw at workbench load.
    /// </summary>
    public class RightClickHookAutostartCommand : ICommand
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
            try { RightClickHookService.Start(); }
            catch (Exception ex) { Debug.WriteLine("[RightClickHookAutostart] start failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// /Workspace/Terminate command — tears the native-hook service down at workbench shutdown:
    /// latches _shuttingDown (gates append + launch), stops the timer, and unhooks every roster pair.
    /// Guarded; harmless if Start() never ran.
    /// </summary>
    public class RightClickHookTerminateCommand : ICommand
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
            try { RightClickHookService.Terminate(); }
            catch (Exception ex) { Debug.WriteLine("[RightClickHookTerminate] terminate failed: " + ex.Message); }
        }
    }
}
