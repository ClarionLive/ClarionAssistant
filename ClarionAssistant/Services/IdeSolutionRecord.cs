using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        public static string DirectoryPath
        {
            get
            {
                if (!string.IsNullOrEmpty(RootOverride)) return RootOverride;
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
                lock (_lock)
                {
                    if (_lastPublished != null && string.Equals(_lastPublished, value, StringComparison.OrdinalIgnoreCase))
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
                        // Write-then-copy so a reader never sees a half-written file.
                        string tmp = file + ".tmp";
                        File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(rec), new UTF8Encoding(false));
                        File.Copy(tmp, file, true);
                        File.Delete(tmp);
                    }
                    _lastPublished = value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[IdeSolutionRecord] Publish: " + ex.Message);
            }
        }

        /// <summary>
        /// Standalone side: the solution the IDE with process id <paramref name="idePid"/> last
        /// published, or null. Null when there is no record, the record is unreadable, that IDE is no
        /// longer running, or the .sln it names no longer exists. <paramref name="note"/> says which.
        /// </summary>
        public static string Read(int idePid, out string note)
        {
            note = null;
            try
            {
                string file = PathForPid(idePid);
                if (!File.Exists(file))
                {
                    note = "the IDE (pid " + idePid + ") has published no open solution";
                    return null;
                }

                var rec = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                object v;
                string sln = rec != null && rec.TryGetValue("solution", out v) ? v as string : null;
                int pid = rec != null && rec.TryGetValue("pid", out v) && v != null ? Convert.ToInt32(v) : 0;

                if (pid > 0)
                {
                    try { System.Diagnostics.Process.GetProcessById(pid); }
                    catch
                    {
                        note = "the IDE that published the record (pid " + pid + ") is no longer running";
                        return null;
                    }
                }
                if (string.IsNullOrEmpty(sln) || !File.Exists(sln))
                {
                    note = "the published solution '" + sln + "' does not exist";
                    return null;
                }
                note = "the IDE's open solution (pid " + idePid + ")";
                return sln;
            }
            catch (Exception ex)
            {
                note = "could not read the IDE's solution record: " + ex.Message;
                return null;
            }
        }

        /// <summary>Test hook: forget what this process last wrote, so Publish writes again.</summary>
        internal static void ResetForTest()
        {
            lock (_lock) { _lastPublished = null; }
        }
    }
}
