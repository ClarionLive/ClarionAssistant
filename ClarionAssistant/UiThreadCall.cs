using System;
using System.Threading;

namespace ClarionAssistant
{
    /// <summary>
    /// Run a piece of IDE work on the UI thread from an MCP worker thread, with a BOUNDED wait. Shared by
    /// EditorLiveTextProvider (44a1b10c) and NativeTabSync (GH #205).
    ///
    /// The work is POSTED, never Invoke'd/Sent: if the UI thread is busy, or is itself blocked waiting on this very call
    /// (the sync-over-async stall, ~60 s freeze), the wait just times out and the caller degrades. A late-running posted
    /// call finishes and its result is discarded. Called ON the UI thread it runs inline.
    /// </summary>
    internal static class UiThreadCall
    {
        internal enum Outcome { Ran, TimedOut, Failed, NoUiThread }

        /// <summary>Runs <paramref name="work"/>; <paramref name="error"/> is its exception when Failed.</summary>
        internal static Outcome Run(SynchronizationContext ui, Action work, int waitMs, out Exception error)
        {
            error = null;
            if (ui == null) return Outcome.NoUiThread;
            Exception failure = null;
            var done = new ManualResetEventSlim(false);
            SendOrPostCallback cb = _ =>
            {
                try { work(); }
                catch (Exception ex) { failure = ex; }
                finally { try { done.Set(); } catch (ObjectDisposedException) { } }
            };
            if (SynchronizationContext.Current == ui) cb(null);
            else
            {
                ui.Post(cb, null);
                if (!done.Wait(waitMs)) return Outcome.TimedOut;   // not disposed: the late callback still Sets it
            }
            done.Dispose();
            error = failure;
            return failure != null ? Outcome.Failed : Outcome.Ran;
        }
    }
}
