using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #205: the standalone clarion-mcp-server's <see cref="OpenTabGuard.Probe"/> / <see cref="OpenTabGuard.Reload"/>.
    /// write_file and append_to_file are served by the standalone, which has no editors, so before writing it asks every
    /// running IDE (IdeEndpointRecord.DiscoverAll: trusted records only, any solution) through the pane's own MCP endpoint:
    /// tools/call editor_tab_sync {action: check}; after a successful write it asks the IDE that reported a clean native
    /// tab to {action: reload}. The transport is IdeLiveTextClient's (127.0.0.1, bearer token in the header only, cached
    /// Mcp-Session-Id, bounded).
    ///
    /// Both spellings of the target go along (file_path = the gated, link-resolved path; also_path = the caller's), and the
    /// IDE matches its tabs by file identity, so an alias does not hide a tab (pipeline run 1).
    ///
    /// The IDEs are asked IN PARALLEL inside one <see cref="CheckBudgetMs"/> budget (IdeFanOut), and an IDE that just
    /// timed out is skipped for a minute instead of adding its timeout to every write (pipeline run 1).
    ///
    /// Any failure for one IDE is an unreachable TabProbe with a reason (never a throw), which the guard turns into
    /// "could not be checked". The token never appears in a log line or a reason.
    ///
    /// IDE-free: linked into the standalone only.
    /// </summary>
    public static class IdeTabClient
    {
        /// <summary>GH #205 re-review D1: the IDE gives up HOLDING 2.4 s into its check (NativeTabSync.HoldDeadlineMs), well
        /// inside this; a check that still times out here is released by its hold_id anyway.</summary>
        public const int CheckBudgetMs = 3000;
        /// <summary>Longer: a reload re-reads and re-highlights the file on the IDE's UI thread. NativeTabSync's two UI
        /// waits total 1.5 + 7.5 = 9 s, so the IDE's own "did not answer" arrives inside this budget with 1 s for HTTP.</summary>
        public const int ReloadBudgetMs = 10000;

        public static Action<string> Log;
        private static readonly IdeFanOut _fanOut = new IdeFanOut();

        public static List<TabProbe> Probe(IList<string> paths)
        {
            string path = paths != null && paths.Count > 0 ? paths[0] : null;
            string reason;
            var eps = IdeEndpointRecord.DiscoverAll(out reason, Log);
            if (eps.Count == 0)
            {
                Write("[tab-sync] check path=" + path + " ide=none reason=" + reason);
                return new List<TabProbe> { new TabProbe { Reachable = false, Reason = reason } };
            }
            var total = Stopwatch.StartNew();
            // GH #205 re-review (holder tokens, D1): one request id per IDE, made HERE so it survives a check that times
            // out on this side - the IDE may still have held under it (even a check whose task started after the budget
            // ran out), and the release names it. Only an IDE that IdeFanOut skips (it timed out recently) is never asked.
            var ids = new string[eps.Count];
            var quiet = new bool[eps.Count];
            var idOf = new Dictionary<IdeEndpointRecord.Endpoint, string>();
            for (int i = 0; i < eps.Count; i++)
            {
                ids[i] = OpenTabGuard.NewHoldId();
                quiet[i] = _fanOut.IsQuiet(eps[i].Pid);
                idOf[eps[i]] = ids[i];
            }
            var answers = _fanOut.Ask(eps, ep => ep.Pid, ep => CheckOne(ep, paths, idOf[ep]), CheckBudgetMs);
            for (int i = 0; i < answers.Count; i++)
            {
                var p = answers[i];   // IdeFanOut has stamped p.Ide
                p.Handle = eps[i];
                if (!quiet[i]) p.HoldId = ids[i];
                Write("[tab-sync] check path=" + path + " ide=" + eps[i].Label + " -> "
                      + (p.Reachable ? p.Kind + (p.Dirty ? " dirty" : " clean") : "unreachable: " + p.Reason)
                      + (p.HeldTabs.Count > 0 ? " held=" + p.HeldTabs.Count : ""));
            }
            Write("[tab-sync] check path=" + path + " ides=" + eps.Count + " ms=" + total.ElapsedMilliseconds);
            return answers;
        }

        private static TabProbe CheckOne(IdeEndpointRecord.Endpoint ep, IList<string> paths, string holdId)
        {
            var one = Stopwatch.StartNew();
            try
            {
                // hold: this check is the write path's (review SEC MED): only it may hold the IDE's file watcher, and the
                // IDE holds only spellings its #266 WRITE gate allows. hold_id: the holder token its reload or release
                // names. An older addin ignores the extra arguments.
                var args = Args(paths, "check", holdId);
                args[OpenTabGuard.HoldArg] = true;
                string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync", args, one, CheckBudgetMs);
                // Pipeline run 4: an IDE on a pre-#205 Clarion Assistant answers "Unknown tool": a note naming it (the write
                // proceeds); any OTHER error is a failed check (pipeline run 2). OpenTabGuard.FromCheckReply decides.
                return OpenTabGuard.FromCheckReply(text, Parse(text), () => OlderAddinReason(ep));
            }
            catch (Exception ex)
            {
                return new TabProbe { Reachable = false, TimedOut = IsTimeout(ex), Reason = "did not answer: " + IdeLiveTextClient.Describe(ex, one) };
            }
        }

        public static TabReload Reload(IList<string> paths, TabProbe tab)
        {
            var ep = tab != null ? tab.Handle as IdeEndpointRecord.Endpoint : null;
            if (ep == null) return new TabReload { Reason = "no IDE to ask" };
            string path = paths != null && paths.Count > 0 ? paths[0] : null;
            var sw = Stopwatch.StartNew();
            TabReload r;
            try
            {
                string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync", Args(paths, "reload", tab.HoldId), sw, ReloadBudgetMs);
                r = text.StartsWith("Error", StringComparison.Ordinal)
                    ? new TabReload { Reason = "the IDE refused: " + text }
                    : OpenTabGuard.ReloadFromWire(Parse(text));
            }
            catch (Exception ex)
            {
                r = new TabReload { Reason = "the IDE did not answer: " + IdeLiveTextClient.Describe(ex, sw) };
            }
            Write("[tab-sync] reload path=" + path + " ide=" + ep.Label + " -> "
                  + (r.Reloaded ? "reloaded line=" + r.Line : "not reloaded: " + r.Reason) + " ms=" + sw.ElapsedMilliseconds);
            return r;
        }

        /// <summary>Short: a release only drops a holder (the IDE does the rest asynchronously), and a lost one is given
        /// back by the IDE's own 30 s cap anyway.</summary>
        public const int ReleaseBudgetMs = 2000;

        /// <summary>
        /// GH #205 review F1-1: the write was refused or failed (or no reload will follow) after <paramref name="tab"/>'s
        /// IDE was asked to hold its file watcher; ask that IDE to give it back now (editor_tab_sync action 'release',
        /// naming the check's hold_id: the IDE drops only that holder, and refuses a hold under it that lands later - D1,
        /// a check that timed out here). Silent: an older addin's "Unknown tool" or unknown-action reply (it never held
        /// anything) is ignored, and any failure leaves the hold to the IDE's cap.
        /// </summary>
        public static void Release(IList<string> paths, TabProbe tab)
        {
            var ep = tab != null ? tab.Handle as IdeEndpointRecord.Endpoint : null;
            if (ep == null || string.IsNullOrEmpty(tab.HoldId)) return;
            string path = paths != null && paths.Count > 0 ? paths[0] : null;
            var sw = Stopwatch.StartNew();
            string outcome;
            try
            {
                string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync", Args(paths, "release", tab.HoldId), sw, ReleaseBudgetMs);
                outcome = OpenTabGuard.IsOlderAddinReleaseReply(text) ? "older addin, nothing to release"
                    : (text ?? "").StartsWith("Error", StringComparison.Ordinal) ? "the IDE refused: " + text : "released";
            }
            catch (Exception ex) { outcome = "the IDE did not answer: " + IdeLiveTextClient.Describe(ex, sw) + " (its cap gives it back)"; }
            Write("[tab-sync] release path=" + path + " ide=" + ep.Label + " -> " + outcome + " ms=" + sw.ElapsedMilliseconds);
        }

        private static Dictionary<string, object> Args(IList<string> paths, string action, string holdId)
        {
            var a = new Dictionary<string, object> { { "file_path", paths != null && paths.Count > 0 ? paths[0] : "" }, { "action", action } };
            if (paths != null && paths.Count > 1) a[OpenTabGuard.AlsoPathArg] = paths[1];
            if (!string.IsNullOrEmpty(holdId)) a[OpenTabGuard.HoldIdArg] = holdId;
            return a;
        }

        /// <summary>"it runs an older Clarion Assistant (in C:\Clarion11) ..." - the Clarion folder from the IDE's own exe.</summary>
        public static string OlderAddinReason(IdeEndpointRecord.Endpoint ep)
        {
            string root = null;
            try
            {
                using (var p = Process.GetProcessById(ep.Pid))
                {
                    string exe = p.MainModule.FileName;                                   // <Clarion>\bin\Clarion.exe
                    root = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(exe));
                }
            }
            catch { }
            // The guard prefixes "IDE <pid>: ".
            return "it runs an older Clarion Assistant" + (string.IsNullOrEmpty(root) ? "" : " (in " + root + ")")
                + " that cannot check its editor tabs; update Clarion Assistant in that Clarion";
        }

        private static bool IsTimeout(Exception ex)
        {
            return ex is TimeoutException
                || (ex is WebException && ((WebException)ex).Status == WebExceptionStatus.Timeout);
        }

        private static Dictionary<string, object> Parse(string text)
        {
            try { return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>; }
            catch { return null; }
        }

        private static void Write(string line) { if (Log != null) try { Log(line); } catch { } }
    }
}
