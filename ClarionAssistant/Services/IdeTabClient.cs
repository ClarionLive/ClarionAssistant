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
        public const int CheckBudgetMs = 3000;
        /// <summary>Longer: a reload re-reads and re-highlights the file on the IDE's UI thread (NativeTabSync waits 8 s).</summary>
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
            var answers = _fanOut.Ask(eps, ep => ep.Pid, ep => CheckOne(ep, paths), CheckBudgetMs);
            for (int i = 0; i < answers.Count; i++)
            {
                var p = answers[i];   // IdeFanOut has stamped p.Ide
                p.Handle = eps[i];
                Write("[tab-sync] check path=" + path + " ide=" + eps[i].Label + " -> "
                      + (p.Reachable ? p.Kind + (p.Dirty ? " dirty" : " clean") : "unreachable: " + p.Reason));
            }
            Write("[tab-sync] check path=" + path + " ides=" + eps.Count + " ms=" + total.ElapsedMilliseconds);
            return answers;
        }

        private static TabProbe CheckOne(IdeEndpointRecord.Endpoint ep, IList<string> paths)
        {
            var one = Stopwatch.StartNew();
            try
            {
                string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync", Args(paths, "check"), one, CheckBudgetMs);
                // The IDE answered: a refusal is a FAILED check (Answered), which refuses the write (pipeline run 2).
                return text.StartsWith("Error", StringComparison.Ordinal)
                    ? new TabProbe { Reachable = false, Answered = true, Reason = "the IDE refused: " + text }
                    : OpenTabGuard.CheckFromWire(Parse(text), null);
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
                string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync", Args(paths, "reload"), sw, ReloadBudgetMs);
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

        private static Dictionary<string, object> Args(IList<string> paths, string action)
        {
            var a = new Dictionary<string, object> { { "file_path", paths != null && paths.Count > 0 ? paths[0] : "" }, { "action", action } };
            if (paths != null && paths.Count > 1) a[OpenTabGuard.AlsoPathArg] = paths[1];
            return a;
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
