using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Tiny append-only DISK logger dedicated to the IDE shutdown path. The shutdown hang is INTERMITTENT
    /// (days between occurrences), so Debug.WriteLine / DebugView traces are useless — nobody has a debugger
    /// attached at the random moment it wedges. This writes a durable, flush-per-line trace to
    /// %APPDATA%\ClarionAssistant\shutdown.log so the NEXT hang leaves evidence on disk showing exactly which
    /// teardown step it died on — or whether ShutdownService.Terminate() ran at all.
    ///
    /// Design for hang-survival (this is the whole point — do not "optimize" it):
    ///   - Every Log() OPENS, writes, and CLOSES the file (File.AppendAllText). No StreamWriter buffering: if
    ///     the process is force-killed or wedged mid-step, the last line is already flushed to disk. The value
    ///     of this log IS that final line, and shutdown is not a hot path, so we never trade durability for speed.
    ///   - Every call is exception-swallowing: logging must NEVER throw on the teardown path.
    ///   - A one-time-per-process roll keeps the file bounded across sessions (keeps one .prev).
    ///
    /// Reading a captured hang: find the last "session start" block; the LAST line tells the story —
    ///   • a "dispose X ..." with no following "dispose X done"  => that WebView2 owner's dispose deadlocked.
    ///   • a "WATCHDOG FIRED ..." line but the process is still alive => Kill couldn't reap it (a thread stuck
    ///     in an uninterruptible kernel-mode wait), not a missed owner.
    ///   • NO "Terminate() begin" line at all => the close path never invoked teardown (trigger gap).
    /// </summary>
    public static class ShutdownLog
    {
        private static readonly object _gate = new object();
        private static string _path;
        private static bool _resolved;
        private static bool _rolled;
        private const long MaxBytes = 512 * 1024;   // roll when the log passes ~0.5 MB

        private static string ResolvePath()
        {
            if (_resolved) return _path;
            _resolved = true;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "shutdown.log");
            }
            catch { _path = null; }
            return _path;
        }

        /// <summary>Append one durable, timestamped line (pid + managed-thread id + message). Never throws.</summary>
        public static void Log(string message)
        {
            try
            {
                string p = ResolvePath();
                if (p == null) return;
                lock (_gate)
                {
                    RollIfLargeOnce(p);
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " pid=" + GetPid()
                        + " t=" + Thread.CurrentThread.ManagedThreadId
                        + "  " + message + Environment.NewLine;
                    File.AppendAllText(p, line);
                }
            }
            catch { /* logging must never break shutdown */ }
        }

        // Close timing (4d63b995): Stopwatch ticks of the user's close (the workbench's FormClosing); 0 until then.
        private static long _closeStartTicks;

        /// <summary>True once the workbench's main form began closing (the IDE is exiting, not switching solutions).</summary>
        public static bool IdeClosing { get { return Interlocked.Read(ref _closeStartTicks) != 0; } }

        /// <summary>
        /// One close-timing line, "[close +N ms] step": N is the time since the user's close (the workbench's
        /// FormClosing, which passes <paramref name="startsClose"/>). Before that - a solution switch, say -
        /// the line has no elapsed time. Never throws.
        /// </summary>
        public static void Close(string step, bool startsClose = false)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (startsClose) Interlocked.CompareExchange(ref _closeStartTicks, now, 0);
                long start = Interlocked.Read(ref _closeStartTicks);
                Log(start == 0
                    ? "[close] " + step
                    : "[close +" + ((now - start) * 1000 / Stopwatch.Frequency) + " ms] " + step);
            }
            catch { }
        }

        /// <summary>Session delimiter, written when the backstop arms (addin load). Bounds each IDE run as a
        /// clear block so a post-mortem can locate the last session's trace quickly.</summary>
        public static void LogSessionStart(string detail)
        {
            Log("================ session start " + (detail ?? string.Empty) + " ================");
        }

        private static int GetPid()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        // One-time-per-process roll: if the log is already large from prior sessions, move it aside once so it
        // can't grow without bound. Keeps exactly one previous file (shutdown.log.prev). Caller holds _gate.
        private static void RollIfLargeOnce(string p)
        {
            if (_rolled) return;
            _rolled = true;
            try
            {
                var fi = new FileInfo(p);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string prev = p + ".prev";
                    try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                    File.Move(p, prev);
                }
            }
            catch { }
        }
    }
}
