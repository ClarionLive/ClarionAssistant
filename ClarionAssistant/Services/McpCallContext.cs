using System;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// One UI-thread tool call's lifecycle, shared between McpDispatcher (which may give up waiting) and the
    /// tool (which may be mid-way through mutating the IDE). PR #198 review: when the dispatcher timed out it
    /// told the caller the call failed while apply_embed_edits carried on and SAVED - a retry with the same
    /// line numbers then hit shifted slots. The token makes "gave up" and "about to save" one atomic race:
    ///
    ///   NotStarted -> Running     the UI thread picked the call up (TryStart)
    ///   Running    -> Committed   the tool is about to do its irreversible step (TryCommit)
    ///   *          -> Completed   the tool returned
    ///   NotStarted/Running -> Abandoned   the dispatcher timed out first (Abandon)
    ///
    /// Exactly one side wins each transition, so the dispatcher's timeout message can say truthfully whether
    /// the call never ran, was cancelled before saving, or was already saving and may still complete.
    /// </summary>
    public sealed class McpCallToken
    {
        public const int NotStarted = 0, Running = 1, Committed = 2, Completed = 3, Abandoned = 4;

        private int _state;

        public int State { get { return Volatile.Read(ref _state); } }
        public bool IsAbandoned { get { return State == Abandoned; } }

        /// <summary>The UI thread starts the call. False = already abandoned: do not run it at all.</summary>
        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref _state, Running, NotStarted) == NotStarted;
        }

        /// <summary>
        /// Claim the irreversible step (a save). True = go ahead; the dispatcher can no longer abandon this
        /// call and will report it as "may still complete". False = abandoned: roll back and return.
        /// </summary>
        public bool TryCommit()
        {
            int prior = Interlocked.CompareExchange(ref _state, Committed, Running);
            return prior == Running || prior == Committed;
        }

        /// <summary>The tool returned. Never overwrites Abandoned.</summary>
        public void Complete()
        {
            while (true)
            {
                int s = State;
                if (s == Abandoned || s == Completed) return;
                if (Interlocked.CompareExchange(ref _state, Completed, s) == s) return;
            }
        }

        /// <summary>
        /// The dispatcher gives up. Returns the state it found: NotStarted or Running = now Abandoned;
        /// Committed or Completed = too late, the state is left as it was.
        /// </summary>
        public int Abandon()
        {
            while (true)
            {
                int s = State;
                if (s == Committed || s == Completed || s == Abandoned) return s;
                if (Interlocked.CompareExchange(ref _state, Abandoned, s) == s) return s;
            }
        }
    }

    /// <summary>
    /// The token of the UI-thread tool call currently executing on THIS thread, set by McpDispatcher around
    /// ExecuteTool. Thread-static because UI-thread calls run one at a time per thread - and a call pumped
    /// re-entrantly (DoEvents) saves and restores the outer one. Null outside a dispatched UI-thread call
    /// (the standalone server, the Monaco save paths), where nothing can be abandoned.
    /// </summary>
    public static class McpCallContext
    {
        [ThreadStatic] private static McpCallToken _current;

        public static McpCallToken Current
        {
            get { return _current; }
            set { _current = value; }
        }

        public static bool IsAbandoned { get { var t = _current; return t != null && t.IsAbandoned; } }

        /// <summary>See McpCallToken.TryCommit. True when no call context exists.</summary>
        public static bool TryCommit() { var t = _current; return t == null || t.TryCommit(); }
    }
}
