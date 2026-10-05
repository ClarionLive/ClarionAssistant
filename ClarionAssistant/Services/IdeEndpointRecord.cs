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

        public sealed class Endpoint
        {
            public int Pid;
            public int Port;
            public string Token;
            /// <summary>"pid:port", the only form that may appear in a log line.</summary>
            public string Label { get { return Pid + ":" + Port; } }
        }

        public static string DirectoryPath
        {
            get
            {
                string env = Environment.GetEnvironmentVariable(RecordDirEnv);
                if (!string.IsNullOrEmpty(env)) return Path.Combine(env, "ide-endpoint");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "ClarionAssistant", "ide-endpoint");
            }
        }

        private static string FileFor(int pid, int port) { return Path.Combine(DirectoryPath, pid + "-" + port + ".json"); }

        /// <summary>Addin side (McpServer.Start): publish this process's pane endpoint.</summary>
        public static void Publish(int port, string token)
        {
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
                    { "writtenAt", DateTime.UtcNow.ToString("o") }
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
                reason = records == 0 ? "no IDE has a Clarion Assistant pane running" : "no IDE has this solution open";
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

        // A record, or null with why ("stale ..." when it must be deleted).
        private static Endpoint ReadTrusted(string file, out string why)
        {
            why = null;
            Dictionary<string, object> rec;
            try { rec = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file)); }
            catch (Exception ex) { why = "unreadable (" + ex.GetType().Name + ")"; return null; }   // e.g. mid-replace: leave it
            if (rec == null) { why = "empty"; return null; }

            object v;
            int pid = rec.TryGetValue("pid", out v) && v != null ? Convert.ToInt32(v) : 0;
            int port = rec.TryGetValue("port", out v) && v != null ? Convert.ToInt32(v) : 0;
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
                string image = Environment.GetEnvironmentVariable(ImageNameEnv);
                if (string.IsNullOrEmpty(image)) image = "Clarion";
                if (!string.Equals(p.ProcessName, image, StringComparison.OrdinalIgnoreCase))
                { why = "stale: pid " + pid + " is now '" + p.ProcessName + "', not the IDE (pid reused)"; return null; }
                if (Math.Abs((p.StartTime.ToUniversalTime() - start.ToUniversalTime()).TotalSeconds) > 2)
                { why = "stale: pid " + pid + " was started at " + p.StartTime.ToUniversalTime().ToString("o")
                        + ", not the recorded " + start.ToUniversalTime().ToString("o") + " (pid reused)"; return null; }
            }
            catch (Exception ex) { why = "unverifiable (" + ex.GetType().Name + ")"; return null; }   // e.g. access denied: skip, keep

            return new Endpoint { Pid = pid, Port = port, Token = token };
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\'); } catch { return path; }
        }

        private static void Log(Action<string> log, string line) { if (log != null) try { log(line); } catch { } }
    }
}
