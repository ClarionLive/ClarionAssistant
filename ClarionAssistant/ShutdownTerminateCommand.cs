using System;
using System.Diagnostics;
using ICSharpCode.Core;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Terminate command — runs the ordered addin teardown (stop MCP, kill ConPty child-process
    /// trees, dispose WebView2 instances on the UI thread) BEFORE native IDE teardown, to fix Clarion hanging
    /// on close (ticket: addin shutdown hardening). Registered ahead of RightClickHookTerminateCommand so our
    /// heavy teardown runs first. Idempotent with the Application.ApplicationExit backstop in ShutdownService.
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
            try { ShutdownService.Terminate(); }
            catch (Exception ex) { Debug.WriteLine("[ShutdownTerminate] failed: " + ex.Message); }
        }
    }
}
