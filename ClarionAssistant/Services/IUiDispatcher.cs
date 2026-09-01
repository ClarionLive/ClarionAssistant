using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Marshals work onto the host's UI thread (ticket d051fbd1).
    ///
    /// The registry called AssistantChatControl.BeginInvoke directly, which pulled a WinForms
    /// Control type into a file that otherwise imports nothing but System.*. One member is
    /// enough to remove that.
    ///
    /// WHY IT IS NOT SIMPLY DELETED FOR THE STANDALONE BUILD. I first wrote here that tools
    /// flagged RequiresUiThread are never registered outside the addin, so the concept could be
    /// dropped. THAT WAS WRONG, and measuring it is what corrected it: THREE such tools survive
    /// the IdeOnly gate and do register standalone — execute_command, get_solution_info and
    /// index_solution. They are flagged because in the addin they read _workspace, which there
    /// IS the WinForms chat control; standalone that same interface is a plain object with no
    /// thread affinity. So "there is no UI thread" is not a corner case to tolerate, it is the
    /// normal state for three tools that this ticket exists to deliver — index_solution being
    /// the one that builds the CodeGraph a non-IDE client would query.
    ///
    /// IMPLEMENTATIONS
    ///   addin       — marshals to the chat control, as today.
    ///   standalone  — runs the action inline on the calling thread. There is no UI to
    ///                 protect, so inline is correct rather than a stub.
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>
        /// True when there is a real UI thread to marshal to. Standalone hosts return false,
        /// which lets a caller choose a different path instead of discovering the absence by
        /// way of an exception.
        /// </summary>
        bool HasUiThread { get; }

        /// <summary>
        /// Run <paramref name="action"/> on the UI thread without waiting for it.
        /// Standalone runs it inline, so ordering guarantees differ: in the addin the action
        /// is queued, here it has already completed on return. No current caller depends on
        /// the queueing, and this note exists so the next one checks.
        /// </summary>
        void BeginInvokeOnUi(Action action);
    }
}
