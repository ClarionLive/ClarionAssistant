using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// One page's execution-line marker state, as the host believes it (CA-Debugger #26, task f022fb4e).
    ///
    /// The host re-asserts the marker on every tab activation, because a marker can go missing while a tab
    /// is in the background. For the overwhelmingly common case — no debug session at all — that meant
    /// every single tab switch posted "setExecutionLine 0" to a page that had never heard of the debugger.
    /// This gate is what makes the re-assert say nothing when there is nothing to say: a CLEAR is worth
    /// sending only to a page believed to be showing a marker. A SET is always worth sending (it moves,
    /// repairs or re-asserts the marker).
    ///
    /// Belief, not truth: the page can lose a marker on its own (a reload the host did not drive), which is
    /// exactly why a set is never suppressed. Everything that tells a page about the marker must report it
    /// here — the setExecutionLine messages AND the executionLine that setSource carries — or the host's
    /// belief drifts from what the page shows.
    ///
    /// One instance per editor, touched only on the IDE UI thread. Pulled out of MonacoClarionSourceEditor
    /// so it can be tested at all; see Terminal/test/DebuggerHookGuardsCheck.cs.
    /// </summary>
    internal sealed class ExecutionLineGate
    {
        private bool _marked;

        /// <summary>True when the page is believed to be showing a marker.</summary>
        internal bool PageIsMarked { get { return _marked; } }

        /// <summary>Would telling the page about <paramref name="line"/> tell it anything? (Lines &lt;= 0 all
        /// mean "clear".)</summary>
        internal bool WorthSending(int line)
        {
            return line > 0 || _marked;
        }

        /// <summary>Record what the page has just been told — by a setExecutionLine message, or by the
        /// executionLine inside a setSource payload.</summary>
        internal void PageNowShows(int line)
        {
            _marked = line > 0;
        }
    }
}
