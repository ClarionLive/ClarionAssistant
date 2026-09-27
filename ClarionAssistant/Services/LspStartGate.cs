using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Single-flight guard for LspService's background start, with a restart request that cannot be lost
    /// (16d140e9, pipeline run 1).
    ///
    /// The race it closes: a version change stops the running client and asks for a fresh start while the
    /// background starter that launched that client is still in its tail (client running, guard not yet
    /// released). A plain CompareExchange guard rejected the restart, the starter then released the guard
    /// without knowing its client had been disposed, and no LSP ran until some unrelated trigger.
    ///
    /// Protocol: a restart raises <c>pending</c> BEFORE trying the guard. Either it wins the guard (and starts
    /// itself), or the guard is held — and the holder, which releases the guard BEFORE reading
    /// <c>pending</c>, is guaranteed to see the flag and start again. Whichever order the two threads run
    /// in, one of them starts. Pure (no LSP types), so tests\LspStartGate.Test.cs drives it directly.
    /// </summary>
    public sealed class LspStartGate
    {
        private int _starting;   // 1 while a background start owns the gate
        private int _pending;    // 1 when a restart was requested and not yet served

        public bool IsStarting { get { return Volatile.Read(ref _starting) == 1; } }

        /// <summary>Take the gate for a start. False when another start holds it.</summary>
        public bool TryBegin()
        {
            return Interlocked.CompareExchange(ref _starting, 1, 0) == 0;
        }

        /// <summary>
        /// Release the gate after a start. True when a restart was requested meanwhile: the caller must start
        /// again (normally by going back through <see cref="TryBegin"/>).
        /// </summary>
        public bool End()
        {
            Interlocked.Exchange(ref _starting, 0);
            return Interlocked.Exchange(ref _pending, 0) == 1;
        }

        /// <summary>
        /// Ask for a fresh start. True: the caller now holds the gate and must start (then call
        /// <see cref="End"/>). False: a start in flight holds the gate and will see the request when it ends.
        /// </summary>
        public bool RequestRestart()
        {
            Interlocked.Exchange(ref _pending, 1);
            if (!TryBegin()) return false;
            Interlocked.Exchange(ref _pending, 0);   // we serve it ourselves
            return true;
        }
    }
}
