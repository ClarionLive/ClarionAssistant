using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 44a1b10c: how the standalone clarion-mcp-server finds the IDE pane's MCP endpoint, so lsp_diagnostics can ask the IDE
    /// for an open editor's text (get_live_text) instead of checking the disk.
    ///
    /// The addin's McpServer writes one record per running pane server, <c>&lt;pid&gt;-&lt;port&gt;.json</c> =
    /// { pid, port, token, startTime, writtenAt }, and removes it on Stop. The standalone keeps the records whose
    /// IDE is provably the process that wrote them, and whose OPEN solution (IdeSolutionRecord, which the pane keeps
    /// current) is the standalone's own solution. It never picks an IDE that has another solution open.
    ///
    /// A RECORD IS TRUSTED ONLY WHEN: the file names its own pid; that pid is running; the process is the Clarion IDE
    /// (image name); and its start time matches the record's startTime, which precedes writtenAt. That last check is
    /// what defeats pid reuse after a crash: a new process on a recycled pid has a later start time. A record failing
    /// any check is stale, so it is deleted and the deletion logged.
    ///
    /// The token is a secret (the pane's per-session bearer token, the same one its MCP config file carries). It is never
    /// logged and never put in an error message; the logs name pid:port only.
    ///
    /// Written BOM-free (EncodingHelper.Utf8NoBom) to a temp file and moved into place, so a reader never sees half a
    /// file. IDE-free: compiled into both the addin and the standalone. Never throws.
    /// </summary>
    public static class IdeEndpointRecord
    {
        /// <summary>Cross-process test override for the record root (both ide-endpoint and ide-solution live under it).</summary>
        public const string RecordDirEnv = "CA_IDE_RECORD_DIR";
        /// <summary>Test override for the expected IDE image name (production: "Clarion").</summary>
        public const string ImageNameEnv = "CA_IDE_IMAGE_NAME";
        /// <summary>
        /// b8f9431c review L2: <see cref="RecordDirEnv"/> and <see cref="ImageNameEnv"/> (and IdeSolutionRecord's use of
        /// them) are honoured ONLY when this is also set to "1". Without it a stray CA_IDE_IMAGE_NAME in a user's
        /// environment cannot make the trust rules accept a non-Clarion process, nor CA_IDE_RECORD_DIR point discovery
        /// at another folder. The harnesses set it.
        /// </summary>
        public const string TestHooksEnv = "CA_TEST_HOOKS";

        /// <summary>The value of a test-only environment hook, or null unless <see cref="TestHooksEnv"/>=1.</summary>
        public static string TestHook(string name)
        {
            try
            {
                if (Environment.GetEnvironmentVariable(TestHooksEnv) != "1") return null;
                return Environment.GetEnvironmentVariable(name);
            }
            catch { return null; }
        }

        /// <summary>
        /// b8f9431c review L5: the IDE-tool forwarding protocol this add-in serves, written into its endpoint record as
        /// "ideTools". 1 = every IdeOnly tool on the call-back endpoint, with the params._meta solution check. A record
        /// without it is an add-in that serves no forwarded tools (only the call-backs, or a CA tab's pane).
        /// </summary>
        public const int IdeToolsProtocolVersion = 1;

        public sealed class Endpoint
        {
            public int Pid;
            public int Port;
            public string Token;
            /// <summary>The record's "ideTools" (see <see cref="IdeToolsProtocolVersion"/>); 0 when absent (an older add-in).</summary>
            public int IdeToolsProtocol;
            /// <summary>"pid:port", the only form that may appear in a log line.</summary>
            public string Label { get { return Pid + ":" + Port; } }
        }

        public static string DirectoryPath
        {
            get
            {
                string env = TestHook(RecordDirEnv);
                if (!string.IsNullOrEmpty(env)) return Path.Combine(env, "ide-endpoint");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "ClarionAssistant", "ide-endpoint");
            }
        }

        private static string FileFor(int pid, int port) { return Path.Combine(DirectoryPath, pid + "-" + port + ".json"); }

        /// <summary>Addin side (McpServer.Start): publish this process's pane endpoint.</summary>
        public static void Publish(int port, string token)
        {
            // GH #205 pipeline run 2: records (with their tokens) of IDEs that died without Stop - a deploy kill, a crash -
            // used to linger until some standalone server happened to scan. Every publish sweeps them first.
            SweepStale();
            try
            {
                var me = Process.GetCurrentProcess();
                string file = FileFor(me.Id, port);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var rec = new Dictionary<string, object>
                {
                    { "pid", me.Id },
                    { "port", port },
                    { "token", token },
                    { "startTime", me.StartTime.ToUniversalTime().ToString("o") },
                    { "writtenAt", DateTime.UtcNow.ToString("o") },
                    { "ideTools", IdeToolsProtocolVersion }
                };
                string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(rec), EncodingHelper.Utf8NoBom);
                if (File.Exists(file)) File.Replace(tmp, file, null);
                else File.Move(tmp, file);
            }
            catch (Exception ex) { Debug.WriteLine("[IdeEndpointRecord] Publish: " + ex.Message); }
        }

        /// <summary>Addin side (McpServer.Stop): withdraw it.</summary>
        public static void Remove(int port)
        {
            try
            {
                string file = FileFor(Process.GetCurrentProcess().Id, port);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) { Debug.WriteLine("[IdeEndpointRecord] Remove: " + ex.Message); }
        }

        /// <summary>
        /// Standalone side: the pane endpoint of the IDE that has <paramref name="solutionPath"/> open, or null with
        /// <paramref name="reason"/> saying why. <paramref name="preferredPid"/> (--ide-pid) wins among several matches;
        /// with several and none preferred, null ("not guessing"). <paramref name="log"/> gets one line per record.
        /// </summary>
        public static Endpoint Discover(string solutionPath, int? preferredPid, out string reason, Action<string> log = null)
        {
            reason = null;
            if (string.IsNullOrEmpty(solutionPath)) { reason = "this server has no solution, so no IDE can be matched to it"; return null; }
            string wantSln = Full(solutionPath);
            var matches = new List<Endpoint>();
            int records = 0;
            try
            {
                string dir = DirectoryPath;
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.json"))
                    {
                        records++;
                        string why;
                        var ep = ReadTrusted(file, out why);
                        if (ep == null)
                        {
                            if (why != null && why.StartsWith("stale", StringComparison.Ordinal))
                            {
                                try { File.Delete(file); } catch { }
                                Log(log, "[live-text] deleted " + why + " (" + Path.GetFileName(file) + ")");
                            }
                            else Log(log, "[live-text] skipped " + Path.GetFileName(file) + ": " + why);
                            continue;
                        }

                        string note;
                        string ideSln = IdeSolutionRecord.Read(ep.Pid, out note);
                        bool same = ideSln != null && string.Equals(Full(ideSln), wantSln, StringComparison.OrdinalIgnoreCase);
                        Log(log, "[live-text] candidate " + ep.Label + " solution=" + (ideSln ?? "none") + (same ? " MATCH" : " (other)"));
                        if (same) matches.Add(ep);
                    }
                }
            }
            catch (Exception ex) { reason = "the IDE endpoint records could not be read: " + ex.Message; return null; }

            if (matches.Count == 0)
            {
                reason = records == 0 ? "no Clarion with Clarion Assistant is running" : "no IDE has this solution open";
                return null;
            }
            if (preferredPid.HasValue)
                foreach (var m in matches) if (m.Pid == preferredPid.Value) return m;
            if (matches.Count == 1) return matches[0];

            // Several panes of the SAME IDE are one IDE; several IDEs are ambiguous.
            int firstPid = matches[0].Pid;
            if (matches.TrueForAll(m => m.Pid == firstPid)) return matches[0];
            reason = matches.Count + " IDEs have this solution open and none launched this server; not guessing";
            return null;
        }

        /// <summary>
        /// Delete every record whose IDE is provably gone: the pid is not running, is not the IDE, or was started at
        /// another time than recorded (the same "stale" test <see cref="Discover"/> applies). A record that is only
        /// unreadable (mid-replace) or unverifiable (access denied) is left alone. Returns how many were deleted. Never throws.
        /// </summary>
        public static int SweepStale(Action<string> log = null)
        {
            int deleted = 0;
            try
            {
                string dir = DirectoryPath;
                if (!Directory.Exists(dir)) return 0;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    string why;
                    if (ReadTrusted(file, out why) != null) continue;
                    if (why == null || !why.StartsWith("stale", StringComparison.Ordinal)) continue;
                    try { File.Delete(file); deleted++; Log(log, "[endpoint] deleted " + why + " (" + Path.GetFileName(file) + ")"); }
                    catch { }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[IdeEndpointRecord] SweepStale: " + ex.Message); }
            return deleted;
        }

        /// <summary>
        /// GH #205: EVERY trusted IDE pane, whatever solution it has open, one per IDE process (several panes of one IDE
        /// are one IDE). For write_file/append_to_file's open-tab check: a native tab in ANY IDE clobbers the file on its
        /// next save, so the check is not limited to the IDE of this server's solution. The same trust rules as
        /// <see cref="Discover"/> (stale records are deleted). Empty with <paramref name="reason"/> when there is none.
        /// </summary>
        public static List<Endpoint> DiscoverAll(out string reason, Action<string> log = null)
        {
            reason = null;
            var all = new List<Endpoint>();
            int records = 0;
            try
            {
                string dir = DirectoryPath;
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.json"))
                    {
                        records++;
                        string why;
                        var ep = ReadTrusted(file, out why);
                        if (ep == null)
                        {
                            if (why != null && why.StartsWith("stale", StringComparison.Ordinal))
                            {
                                try { File.Delete(file); } catch { }
                                Log(log, "[tab-sync] deleted " + why + " (" + Path.GetFileName(file) + ")");
                            }
                            else Log(log, "[tab-sync] skipped " + Path.GetFileName(file) + ": " + why);
                            continue;
                        }
                        if (!all.Exists(e => e.Pid == ep.Pid)) all.Add(ep);
                    }
                }
            }
            catch (Exception ex) { reason = "the IDE endpoint records could not be read: " + ex.Message; return new List<Endpoint>(); }
            if (all.Count == 0)
                reason = records == 0 ? "no Clarion with Clarion Assistant is running" : "no running IDE could be verified";
            return all;
        }

        // A record, or null with why ("stale ..." when it must be deleted).
        private static Endpoint ReadTrusted(string file, out string why)
        {
            why = null;
            Dictionary<string, object> rec;
            try { rec = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); }
            catch (Exception ex) { why = "unreadable (" + ex.GetType().Name + ")"; return null; }   // e.g. mid-replace: leave it
            if (rec == null) { why = "empty"; return null; }

            object v;
            // GH #205 pipeline run 3: parsed, never converted. Convert.ToInt32 threw on "abc" or 1e12 here, outside any try,
            // and one bad record aborted discovery for EVERY IDE. An unparseable record is skipped, and deleted only when
            // provably stale (its file name's pid is no longer running).
            int pid, port;
            bool pidOk = TryInt(rec, "pid", out pid), portOk = TryInt(rec, "port", out port);
            if (!pidOk || !portOk)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                int namePid;
                int dash = stem.IndexOf('-');
                if (dash > 0 && int.TryParse(stem.Substring(0, dash), out namePid) && namePid > 0 && !IsRunning(namePid))
                    why = "stale: malformed record " + stem + " of a pid that is no longer running";
                else
                    why = "malformed record " + stem + " (pid/port not a number), skipped";
                return null;
            }
            string token = rec.TryGetValue("token", out v) ? v as string : null;
            DateTime start = DateTime.MinValue, written = DateTime.MinValue;
            bool hasStart = rec.TryGetValue("startTime", out v) && DateTime.TryParse(v as string, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out start);
            if (!hasStart) start = DateTime.MinValue;
            bool hasWritten = rec.TryGetValue("writtenAt", out v) && DateTime.TryParse(v as string, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out written);
            if (!hasWritten) written = DateTime.MinValue;

            string expectName = Path.GetFileNameWithoutExtension(file);
            if (pid <= 0 || port <= 0 || string.IsNullOrEmpty(token) || expectName != pid + "-" + port)
            { why = "stale: malformed record for " + expectName; return null; }
            if (!hasStart || !hasWritten || start.ToUniversalTime() > written.ToUniversalTime())
            { why = "stale: record " + pid + ":" + port + " lacks a start time before its write time"; return null; }

            Process p;
            try { p = Process.GetProcessById(pid); }
            catch { why = "stale: IDE " + pid + ":" + port + " is no longer running"; return null; }
            try
            {
                if (p.HasExited) { why = "stale: IDE " + pid + ":" + port + " has exited"; return null; }
                string image = TestHook(ImageNameEnv);
                if (string.IsNullOrEmpty(image)) image = "Clarion";
                if (!string.Equals(p.ProcessName, image, StringComparison.OrdinalIgnoreCase))
                { why = "stale: pid " + pid + " is now '" + p.ProcessName + "', not the IDE (pid reused)"; return null; }
                if (Math.Abs((p.StartTime.ToUniversalTime() - start.ToUniversalTime()).TotalSeconds) > 2)
                { why = "stale: pid " + pid + " was started at " + p.StartTime.ToUniversalTime().ToString("o")
                        + ", not the recorded " + start.ToUniversalTime().ToString("o") + " (pid reused)"; return null; }
            }
            catch (Exception ex) { why = "unverifiable (" + ex.GetType().Name + ")"; return null; }   // e.g. access denied: skip, keep

            int ideTools;
            if (!TryInt(rec, "ideTools", out ideTools) || ideTools < 0) ideTools = 0;
            return new Endpoint { Pid = pid, Port = port, Token = token, IdeToolsProtocol = ideTools };
        }

        private static bool TryInt(Dictionary<string, object> rec, string key, out int value)
        {
            value = 0;
            object v;
            if (!rec.TryGetValue(key, out v) || v == null) return true;   // absent = 0: the "malformed" checks below judge it
            if (v is int) { value = (int)v; return true; }
            return int.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static bool IsRunning(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch { return true; }   // cannot tell (access denied): not provably gone
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\'); } catch { return path; }
        }

        private static void Log(Action<string> log, string line) { if (log != null) try { log(line); } catch { } }
    }
}
