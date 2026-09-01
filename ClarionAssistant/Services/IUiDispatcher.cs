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
    /// WHY IT IS NOT SIMPLY DELETED FOR THE STANDALONE BUILD. Tools flagged RequiresUiThread
    /// are not registered outside the addin, so it would be tempting to drop the concept.
    /// But the dispatcher is also used by streaming handlers that marshal their own UI work,
    /// and "there is no UI thread" is a real, valid state that code should be able to ask
    /// about rather than crash into.
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
