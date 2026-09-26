using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The diagnostic sink for the LSP subsystem — LspClient, LspService and SharedLspBridge
    /// (ticket d051fbd1, checklist item 0).
    ///
    /// WHY THIS EXISTS. Those three files carry 61 diagnostics between them (30 / 6 / 25), and
    /// every one of them used to be a <c>Debug.WriteLine</c>. <c>Debug.WriteLine</c> is
    /// <c>[Conditional("DEBUG")]</c>, so the compiler deletes the call — argument expression and
    /// all — from any Release build. Inside the IDE that was merely inconvenient. For
    /// clarion-mcp-server it was fatal: the server SHIPS as Release, its <c>--debug</c> flag
    /// attached a listener to a Debug channel that no longer had any callers, and so the flag
    /// printed nothing at all. LspClient reports its entire start sequence — including every
    /// reason it can fail — through these lines, so a user outside the IDE could learn exactly
    /// one fact about the language server: "not running".
    ///
    /// WHAT CHANGED. The 61 call sites now go through <see cref="Write"/>, which is an ordinary
    /// method and therefore survives Release. It still forwards to <c>Debug.WriteLine</c>, so a
    /// DEBUG build keeps landing in the IDE's Debug Output window exactly as before, and it
    /// additionally forwards to whatever sink the host installed (see <see cref="SetSink"/>).
    ///
    /// THE COST, STATED RATHER THAN HIDDEN. Because the calls are real now, the string
    /// concatenation at each call site is paid in Release even when no sink is installed —
    /// previously it was free. That is the price of a diagnostic that survives shipping. It is
    /// affordable here because the sites are overwhelmingly exception handlers and one-shot
    /// start-sequence lines; the only per-message ones are publishDiagnostics, ignored
    /// notifications, and relayed node stderr, none of which run per keystroke more than a
    /// handful of times. If a genuinely hot site ever appears, guard it with
    /// <see cref="Enabled"/> rather than reaching back for <c>Debug.WriteLine</c>.
    ///
    /// HOSTS. clarion-mcp-server installs a stderr sink under <c>--debug</c> (stdout is the
    /// JSON-RPC channel and must stay clean). The addin installs none, so its behaviour is
    /// unchanged; when it wants a durable trace, the pattern to copy is
    /// <see cref="ShutdownLog"/> — a flush-per-line disk file — not another listener.
    /// </summary>
    public static class LspTrace
    {
        // Written once at host start-up, read from LspClient's ReadLoop thread and from the
        // thread pool. volatile so a sink installed on the main thread is visible to those
        // without a lock on the read path, which is the whole point of keeping this a bare field.
        private static volatile Action<string> _sink;

        /// <summary>
        /// True when a host has installed a sink. Only worth testing at a call site whose
        /// message is expensive to BUILD; <see cref="Write"/> already returns immediately
        /// when there is no sink.
        /// </summary>
        public static bool Enabled { get { return _sink != null; } }

        /// <summary>
        /// Installs the host's diagnostic sink, or clears it with null. Last caller wins —
        /// there is deliberately no listener collection here, because the two hosts each want
        /// exactly one destination and a collection would only invite double-writing.
        /// </summary>
        public static void SetSink(Action<string> sink)
        {
            _sink = sink;
        }

        /// <summary>
        /// Emits one diagnostic line. Never throws: a sink that fails must not take down the
        /// LSP path it is only observing.
        /// </summary>
        public static void Write(string message)
        {
            // Elided in Release, kept in Debug — this is what preserves the in-IDE
            // Debug Output window behaviour the 61 sites had before they moved here.
            System.Diagnostics.Debug.WriteLine(message);

            Action<string> sink = _sink;
            if (sink == null) return;
            try { sink(message); }
            catch { }
        }
    }
}
