using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Guard for CodeGraph index runs, keyed by database path. Two layers: an in-process claim
    /// and a cross-process file lock.
    ///
    /// WHY BOTH. Three entry points can start a run — AssistantChatControl.RunIndex (the header
    /// buttons and MCP index_solution), McpToolRegistry.ExecuteIndexCodeGraph (index_codegraph),
    /// and StandaloneWorkspace.RunIndex (the standalone MCP server). The first two share a
    /// process; the third does not, and that is new (ticket d051fbd1). A developer with the addin
    /// running and a friend's editor pointed at the same solution now have two PROCESSES that can
    /// index one .codegraph.db.
    ///
    /// THE DAMAGE IS SEMANTIC, NOT PHYSICAL, WHICH IS WHY NO DATABASE ENGINE FIXES IT. SQLite is
    /// already in WAL mode and will not let the file be corrupted. But a full index begins with
    /// ClearAll() — plain DELETE statements — and then inserts for minutes. Two overlapping runs
    /// give you: A clears, A inserts, B clears (destroying A's work), both interleave, and the
    /// relationships left behind point at symbols that no longer exist. Postgres would serialise
    /// every one of those statements perfectly and produce exactly the same garbage. It is a
    /// mutual-exclusion problem, so it needs a lock.
    ///
    /// Concurrent runs on DIFFERENT databases remain allowed — the key is the db file.
    /// </summary>
    public static class IndexRunGate
    {
        private static readonly ConcurrentDictionary<string, byte> Active =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Open handles for locks this process holds, so Exit can release them. Holding the handle
        // IS the lock; the file's contents are only ever diagnostics.
        private static readonly ConcurrentDictionary<string, FileStream> Held =
            new ConcurrentDictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Try to claim an index run on this database. False = a run already holds it.</summary>
        public static bool TryEnter(string dbPath)
        {
            string holder;
            return TryEnter(dbPath, out holder);
        }

        /// <summary>
        /// Try to claim an index run. On failure <paramref name="holder"/> describes who has it,
        /// so a caller can say "process 8123 on DEVBOX since 10:02" instead of the older message,
        /// which asserted the run was "in this IDE" — no longer true now that another process can
        /// be the one indexing.
        /// </summary>
        public static bool TryEnter(string dbPath, out string holder)
        {
            holder = null;
            string key = Normalize(dbPath);

            if (!Active.TryAdd(key, 1))
            {
                holder = "another index run in this process";
                return false;
            }

            FileStream stream;
            string blockedBy;
            LockResult result = TryAcquireFileLock(key, out stream, out blockedBy);

            if (result == LockResult.Blocked)
            {
                byte unused;
                Active.TryRemove(key, out unused);
                holder = blockedBy ?? "an index run in another process";
                return false;
            }

            // LockResult.Unavailable: the lock file could not be used for a reason that is NOT
            // contention — an unwritable directory, a path length limit, a full disk. FAIL OPEN.
            // Refusing to index would turn an environmental problem into "code intelligence is
            // broken", while proceeding merely leaves us where this project has been all along,
            // still covered by the in-process gate. The caller is not told, because there is
            // nothing it could usefully do differently.
            if (stream != null)
                Held[key] = stream;

            return true;
        }

        /// <summary>Release the claim. Safe to call for a path that isn't held.</summary>
        public static void Exit(string dbPath)
        {
            string key = Normalize(dbPath);

            FileStream stream;
            if (Held.TryRemove(key, out stream) && stream != null)
            {
                // Closing releases the OS lock. The FILE is deliberately left behind: deleting it
                // races a process that may already be waiting to claim it, and an unheld lock file
                // means nothing on its own. It is a few hundred bytes and it explains itself when
                // opened.
                try { stream.Dispose(); } catch { }
            }

            byte unused;
            Active.TryRemove(key, out unused);
        }

        private enum LockResult
        {
            Acquired,
            Blocked,
            Unavailable
        }

        /// <summary>
        /// Acquire the cross-process lock by HOLDING AN EXCLUSIVE HANDLE on a sentinel file.
        ///
        /// THE HELD HANDLE IS THE LOCK — not the PID written inside it, and that is deliberate.
        /// The obvious design is "write the PID, and treat the lock as stale if that PID is gone",
        /// but it has two holes: PIDs are reused, so a stale lock can be judged live by a
        /// coincidence, and a PID means nothing when the solution sits on a share and the holder
        /// is another machine. An open handle has neither problem, and Windows releases it on
        /// EVERY death including a hard kill — which is how CA processes usually die (deploy,
        /// crash, Task Manager). So there is no stale-lock state to detect, and no timeout to
        /// tune: a timeout either blocks a waiting run for far too long or steals the lock from a
        /// live one.
        ///
        /// The PID, machine and timestamp still go in, as DIAGNOSTICS: they are what a blocked
        /// caller reports, and what a human sees on opening the file.
        ///
        /// A SEPARATE FILE, not the database itself, because the cancelled-full-index path does
        /// File.Delete(dbPath) — locking the database would destroy the lock along with it.
        /// </summary>
        private static LockResult TryAcquireFileLock(string dbPath, out FileStream stream, out string blockedBy)
        {
            stream = null;
            blockedBy = null;

            string lockPath = dbPath + ".lock";
            try
            {
                // FileShare.Read, not None: a blocked contender must still be able to READ the
                // file to report who holds it. Write access stays exclusive, which is the lock.
                var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                try
                {
                    fs.SetLength(0);
                    byte[] info = Encoding.UTF8.GetBytes(DescribeThisProcess());
                    fs.Write(info, 0, info.Length);
                    fs.Flush(true);
                }
                catch
                {
                    // The handle is what matters; failing to write the note does not lose the lock.
                }
                stream = fs;
                return LockResult.Acquired;
            }
            catch (IOException)
            {
                // Sharing violation (someone holds it) and environmental faults share this type.
                // They are told apart by the file: a lock whose holder was killed is NOT held, so
                // OpenOrCreate above would have succeeded. An IOException with the file present
                // therefore means a live holder.
                bool exists;
                try { exists = File.Exists(lockPath); } catch { exists = false; }
                if (!exists) return LockResult.Unavailable;

                blockedBy = ReadHolder(lockPath);
                return LockResult.Blocked;
            }
            catch (UnauthorizedAccessException)
            {
                return LockResult.Unavailable;   // read-only directory, ACLs
            }
            catch (NotSupportedException)
            {
                return LockResult.Unavailable;   // exotic path
            }
            catch (ArgumentException)
            {
                return LockResult.Unavailable;   // path length / invalid characters
            }
        }

        private static string DescribeThisProcess()
        {
            string pid, machine;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture); }
            catch { pid = "?"; }
            try { machine = Environment.MachineName; }
            catch { machine = "?"; }

            return "Clarion Assistant CodeGraph index lock." + Environment.NewLine
                 + "A process is indexing the database next to this file. It is safe to delete"
                 + " once no index is running; it is re-created on the next run." + Environment.NewLine
                 + "pid=" + pid + Environment.NewLine
                 + "machine=" + machine + Environment.NewLine
                 + "started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                 + Environment.NewLine;
        }

        /// <summary>
        /// Read the holder's note for a diagnostic message. Best-effort by nature: the holder may
        /// release between the failed acquire and this read, so an unreadable or empty file gets a
        /// generic description rather than an error.
        /// </summary>
        private static string ReadHolder(string lockPath)
        {
            try
            {
                using (var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    if (string.IsNullOrEmpty(text)) return null;

                    string pid = ExtractField(text, "pid=");
                    string machine = ExtractField(text, "machine=");
                    string started = ExtractField(text, "started=");
                    if (pid == null && machine == null) return null;

                    var sb = new StringBuilder("process ");
                    sb.Append(pid ?? "?");
                    if (machine != null) sb.Append(" on ").Append(machine);
                    if (started != null) sb.Append(", indexing since ").Append(started);
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        private static string ExtractField(string text, string prefix)
        {
            int i = text.IndexOf(prefix, StringComparison.Ordinal);
            if (i < 0) return null;
            i += prefix.Length;
            int end = text.IndexOfAny(new[] { '\r', '\n' }, i);
            if (end < 0) end = text.Length;
            string value = text.Substring(i, end - i).Trim();
            return value.Length > 0 ? value : null;
        }

        private static string Normalize(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return "";
            try { return Path.GetFullPath(dbPath); }
            catch { return dbPath; }
        }
    }
}
