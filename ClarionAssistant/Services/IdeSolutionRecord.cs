using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// "Which solution does the IDE have open RIGHT NOW", handed from the addin to the standalone
    /// clarion-mcp-server it launched (ticket 77aceec5).
    ///
    /// WHY. The addin injects --solution into the standalone's launch args, but only the solution
    /// open AT LAUNCH. A plain Chat tab started before a solution was opened therefore runs a
    /// standalone with no solution for its whole life - measured live: the tab's clarion-tools log
    /// opened with "no solution: none found in 'H:\...\ClarionAssistant'", and 46 minutes later, with
    /// clbrws.sln open in the IDE, lsp_start still had nothing to start on. The standalone cannot ask
    /// the IDE's ProjectService (it is a separate process with no IDE assemblies), so the addin
    /// PUBLISHES the answer here and the standalone READS it.
    ///
    /// KEYED ON THE IDE PROCESS ID, which the addin already passes as --ide-pid for the same reason
    /// the open-app record is (McpToolRegistry.OpenAppRecord, GitHub #210): it cannot drift while the
    /// IDE lives. A record whose pid is no longer running is ignored, so an IDE that crashed without
    /// cleaning up cannot hand a stale solution to anyone.
    ///
    /// SCOPE, deliberately narrow: the standalone uses this ONLY as the LSP's fallback when it was
    /// given no --solution. It does not re-point the CodeGraph/solution tools - those keep the
    /// launch-time --solution semantics they document.
    ///
    /// IDE-free (compiled into both the addin and the standalone), and never throws.
    /// </summary>
    public static class IdeSolutionRecord
    {
        /// <summary>Test hook: redirects the record directory. Null in production.</summary>
        internal static string RootOverride { get; set; }

        private static readonly object _lock = new object();
        private static string _lastPublished;   // what this process last wrote ("" = removed)
        private static string _lastSolution;    // the solution in it, for Republish

        /// <summary>
        /// 0ce0b5e2: addin side, the IDE's live Build &gt; Set Clarion Version choice ("Current" for the running
        /// version), or null when it can't be read. Published with the solution, because the IDE restores that
        /// choice from the solution's own preferences when it opens it - so it is the solution's Clarion, which
        /// the standalone cannot read for itself. A hook (set by the addin) rather than a direct call, so the
        /// files that compile this one alone need nothing new.
        /// </summary>
        public static Func<string> VersionChoiceProvider { get; set; }

        /// <summary>0ce0b5e2: addin side, the IDE's configuration directory (its ClarionProperties.xml and
        /// preferences\ folder), or null. Lets the standalone read the solution's saved choice from the IDE's
        /// own preferences when the live choice is unavailable.</summary>
        public static Func<string> ConfigDirProvider { get; set; }

        public static string DirectoryPath
        {
            get
            {
                if (!string.IsNullOrEmpty(RootOverride)) return RootOverride;
                // 44a1b10c: a cross-PROCESS test override (RootOverride is in-process only), shared with
                // IdeEndpointRecord (its RecordDirEnv), so a harness can point a real standalone exe at a temp
                // directory. A literal here so the files that compile this one alone need nothing new.
                string env = Environment.GetEnvironmentVariable("CA_IDE_RECORD_DIR");
                if (!string.IsNullOrEmpty(env)) return Path.Combine(env, "ide-solution");
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClarionAssistant", "ide-solution");
            }
        }

        public static string PathForPid(int pid)
        {
            return Path.Combine(DirectoryPath, "ide-" + pid + ".json");
        }

        /// <summary>
        /// Addin side: record the IDE's open solution for this process. Writes only when the value
        /// changed since this process last wrote it, so a 10-second poll costs a string compare. A
        /// null, empty or missing .sln REMOVES the record - "the IDE has nothing open" must read as
        /// nothing, never as whatever was open before.
        /// </summary>
        public static void Publish(string solutionPath)
        {
            try
            {
                string value = (!string.IsNullOrEmpty(solutionPath) && File.Exists(solutionPath))
                    ? solutionPath : "";
                string version = value.Length == 0 ? null : Ask(VersionChoiceProvider);
                string configDir = value.Length == 0 ? null : Ask(ConfigDirProvider);
                // The change key covers the version too: a Set Clarion Version switch with the same solution
                // open must reach the standalone (0ce0b5e2).
                string key = value.Length == 0 ? "" : value + "|" + version + "|" + configDir;
                lock (_lock)
                {
                    if (_lastPublished != null && string.Equals(_lastPublished, key, StringComparison.OrdinalIgnoreCase))
                        return;

                    int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    string file = PathForPid(pid);
                    if (value.Length == 0)
                    {
                        if (File.Exists(file)) File.Delete(file);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        var rec = new Dictionary<string, object>
                        {
                            { "solution", value },
                            { "pid", pid },
                            { "writtenAt", DateTime.Now.ToString("o") }
                        };
                        if (version != null) rec["clarionVersion"] = version;
                        if (configDir != null) rec["configDir"] = configDir;
                        // Write-then-copy so a reader never sees a half-written file.
                        string tmp = file + ".tmp";
                        File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(rec), EncodingHelper.Utf8NoBom);
                        File.Copy(tmp, file, true);
                        File.Delete(tmp);
                    }
                    _lastPublished = key;
                    _lastSolution = value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[IdeSolutionRecord] Publish: " + ex.Message);
            }
        }

        /// <summary>
        /// Addin side, 0ce0b5e2: publish again for the solution last published, so a Build &gt; Set Clarion
        /// Version switch reaches the standalone at once rather than on the next solution poll. No-op when
        /// nothing was published.
        /// </summary>
        public static void Republish()
        {
            string sln;
            lock (_lock) { sln = _lastSolution; }
            if (!string.IsNullOrEmpty(sln)) Publish(sln);
        }

        private static string Ask(Func<string> provider)
        {
            try
            {
                string s = provider != null ? provider() : null;
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        /// <summary>
        /// Standalone side: the solution the IDE with process id <paramref name="idePid"/> last
        /// published, or null. Null when there is no record, the record is unreadable, that IDE is no
        /// longer running, or the .sln it names no longer exists. <paramref name="note"/> says which.
        /// </summary>
        public static string Read(int idePid, out string note)
        {
            bool transient;
            return Read(idePid, out note, out transient);
        }

        /// <summary>
        /// As <see cref="Read(int, out string)"/>, and says whether a null is only TRANSIENT: the
        /// record exists but could not be read or parsed right now - a sharing violation or a
        /// half-copied file while Publish replaces it. A transient null says nothing about whether the
        /// IDE still has a solution open, so a follower must not act on it (pipeline run 2, R4).
        /// Every other null (no record, IDE gone, pid mismatch, .sln gone) is DEFINITE.
        /// </summary>
        public static string Read(int idePid, out string note, out bool transient)
        {
            var d = ReadDetails(idePid, out note, out transient);
            return d != null ? d.Solution : null;
        }

        /// <summary>What a record carries beyond the solution (0ce0b5e2).</summary>
        public sealed class Details
        {
            /// <summary>The IDE's open solution.</summary>
            public string Solution { get; internal set; }
            /// <summary>The IDE's live Build &gt; Set Clarion Version choice ("Current" = the running version), or
            /// null when the record does not carry one (an addin from before 0ce0b5e2).</summary>
            public string VersionChoice { get; internal set; }
            /// <summary>The IDE's configuration directory, or null.</summary>
            public string ConfigDir { get; internal set; }
            /// <summary>The publishing IDE's process id.</summary>
            public int Pid { get; internal set; }
        }

        /// <summary>As <see cref="Read(int, out string, out bool)"/>, with everything the record carries.</summary>
        public static Details ReadDetails(int idePid, out string note, out bool transient)
        {
            note = null;
            transient = false;
            try
            {
                string file = PathForPid(idePid);
                if (!File.Exists(file))
                {
                    note = "the IDE (pid " + idePid + ") has published no open solution";
                    return null;
                }

                Dictionary<string, object> rec;
                try
                {
                    rec = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    if (rec == null) throw new InvalidOperationException("empty record");
                }
                catch (Exception ex)
                {
                    if (!File.Exists(file))
                    {
                        note = "the IDE (pid " + idePid + ") has published no open solution";
                        return null;
                    }
                    transient = true;
                    note = "the IDE's solution record could not be read right now (" + ex.GetType().Name + ": "
                        + ex.Message + ") - treated as unchanged";
                    return null;
                }

                object v;
                string sln = rec.TryGetValue("solution", out v) ? v as string : null;
                int pid = rec.TryGetValue("pid", out v) && v != null ? Convert.ToInt32(v) : 0;

                // The record must be the one THAT IDE wrote about itself: a payload naming another
                // pid (a copied or planted file) is refused, not trusted (pipeline run 1).
                if (pid != idePid)
                {
                    note = "the record for pid " + idePid + " names pid " + pid + " - ignored";
                    return null;
                }
                if (!IsAlive(pid))
                {
                    note = "the IDE that published the record (pid " + pid + ") is no longer running";
                    return null;
                }
                if (string.IsNullOrEmpty(sln) || !File.Exists(sln))
                {
                    note = "the published solution '" + sln + "' does not exist";
                    return null;
                }
                note = "the IDE's open solution (pid " + idePid + ")";
                return new Details
                {
                    Solution = sln,
                    VersionChoice = rec.TryGetValue("clarionVersion", out v) ? v as string : null,
                    ConfigDir = rec.TryGetValue("configDir", out v) ? v as string : null,
                    Pid = pid
                };
            }
            catch (Exception ex)
            {
                transient = true;
                note = "could not read the IDE's solution record: " + ex.Message;
                return null;
            }
        }
        private static bool IsAlive(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch { return false; }
        }

        private static readonly object _cacheLock = new object();
        private static int _cachePid;
        private static long _cacheStamp = -1;      // mtime ticks ^ length of the file last parsed; 0 = absent
        private static DateTime _cacheCheckedAt;
        private static string _cacheNote;
        private static Details _cacheValue;

        /// <summary>
        /// <see cref="Read"/> for a caller that asks on EVERY lsp_* call (LspService's followed
        /// solution). Costs one file stat per call; the record is re-parsed only when its mtime or
        /// size changes (or it appears/disappears), and the IDE's liveness is re-checked at most every
        /// few seconds. Returns the same answer Read would, modulo that liveness window - except that a
        /// TRANSIENT failure returns the last definite answer (see Read's transient overload).
        /// </summary>
        public static string ReadCached(int idePid, out string note)
        {
            var d = ReadDetailsCached(idePid, out note);
            return d != null ? d.Solution : null;
        }

        /// <summary>As <see cref="ReadCached"/>, with everything the record carries (0ce0b5e2).</summary>
        public static Details ReadDetailsCached(int idePid, out string note)
        {
            long stamp = 0;
            try
            {
                var fi = new FileInfo(PathForPid(idePid));
                if (fi.Exists) stamp = fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 1) ^ 1;
            }
            catch { stamp = -2; }

            lock (_cacheLock)
            {
                bool fresh = _cachePid == idePid && _cacheStamp == stamp && stamp != -2
                    && (DateTime.UtcNow - _cacheCheckedAt).TotalSeconds < 3;
                if (!fresh)
                {
                    string readNote = null;
                    bool transient = false;
                    Details value = stamp == -2 ? null : ReadDetails(idePid, out readNote, out transient);
                    if (stamp == -2) { transient = true; readNote = "the IDE's solution record could not be examined right now"; }
                    if (transient && _cachePid == idePid)
                    {
                        // Unreadable right now: keep the LAST DEFINITE answer, and retry on the next
                        // call rather than trusting a stamp we could not act on (R4). A follower
                        // therefore keeps its server instead of stopping on a sharing violation.
                        _cacheStamp = -3;
                        note = readNote;
                        return _cacheValue;
                    }
                    _cacheValue = value;
                    _cacheNote = readNote;
                    _cachePid = idePid;
                    _cacheStamp = transient ? -3 : stamp;
                    _cacheCheckedAt = DateTime.UtcNow;
                }
                note = _cacheNote;
                return _cacheValue;
            }
        }
        /// <summary>Test hook: forget what this process last wrote, so Publish writes again.</summary>
        internal static void ResetForTest()
        {
            lock (_lock) { _lastPublished = null; }
        }
    }
}
