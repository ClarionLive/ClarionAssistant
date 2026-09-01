using System;
using ClarionAssistant.Services;

namespace ClarionAssistant.McpServer
{
    /// <summary>
    /// IUiDispatcher for a host with no UI thread (ticket d051fbd1).
    ///
    /// Running the action inline is the CORRECT behaviour here, not a stub standing in for real
    /// marshalling. The marshalling in the addin exists because the objects those tools touch are
    /// thread-affine to a WinForms control; in this process the same interface is backed by plain
    /// objects with no affinity, so "run it on the calling thread" is the whole answer.
    ///
    /// HasUiThread reports false honestly rather than pretending, which is what lets McpDispatcher
    /// skip the marshal-and-wait path for the three UI-flagged tools that do register standalone
    /// (execute_command, get_solution_info, index_solution). Claiming true here would send them
    /// through a BeginInvoke that resolves to this same inline call anyway, plus a pointless
    /// 30-second timeout wait wrapped around it.
    /// </summary>
    internal sealed class StandaloneUiDispatcher : IUiDispatcher
    {
        public bool HasUiThread { get { return false; } }

        public void BeginInvokeOnUi(Action action)
        {
            if (action != null) action();
        }
    }
}
