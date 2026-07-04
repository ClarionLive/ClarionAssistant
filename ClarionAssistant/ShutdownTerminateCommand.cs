using System;
using System.Diagnostics;
using ICSharpCode.Core;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command — arms the ApplicationExit shutdown backstop unconditionally at workbench
    /// load (independent of whether the chat MCP server ever starts), so the addin teardown runs even if
    /// /Workspace/Terminate doesn't fire on a problematic close path. Mirrors RightClickHookAutostartCommand.
    /// </summary>
    public class ShutdownAutostartCommand : ICommand
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
            try { ShutdownService.ArmBackstop(); }
            catch (Exception ex) { Debug.WriteLine("[ShutdownAutostart] failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// /Workspace/Terminate command — runs the ordered addin teardown (stop MCP, kill ConPty child-process
    /// trees, dispose WebView2 instances on the UI thread) BEFORE native IDE teardown, to fix Clarion hanging
    /// on close (ticket: addin shutdown hardening). Registered FIRST in /Workspace/Terminate so our heavy
    /// teardown runs ahead of the other terminate commands. Idempotent with the Application.ApplicationExit
    /// backstop in ShutdownService.
    /// </summary>
    public class ShutdownTerminateCommand : ICommand
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
            ShutdownLog.Log("/Workspace/Terminate dispatched -> Terminate()");
            try { ShutdownService.Terminate(); }
            catch (Exception ex) { Debug.WriteLine("[ShutdownTerminate] failed: " + ex.Message); ShutdownLog.Log("/Workspace/Terminate Terminate() threw: " + ex.Message); }
        }
    }
}
