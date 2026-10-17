using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        public static List<TabProbe> Probe(string path)
        {
            string reason;
            var eps = IdeEndpointRecord.DiscoverAll(out reason, Log);
            var answers = new List<TabProbe>();
            if (eps.Count == 0)
            {
                Write("[tab-sync] check path=" + path + " ide=none reason=" + reason);
                answers.Add(new TabProbe { Reachable = false, Reason = reason });
                return answers;
            }
            foreach (var ep in eps)
            {
                var one = Stopwatch.StartNew();
                TabProbe p;
                try
                {
                    string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync",
                        new Dictionary<string, object> { { "file_path", path }, { "action", "check" } }, one, CheckBudgetMs);
                    p = text.StartsWith("Error", StringComparison.Ordinal)
                        ? new TabProbe { Reachable = false, Reason = "the IDE refused: " + text }
                        : OpenTabGuard.CheckFromWire(Parse(text), null);
                }
                catch (Exception ex)
                {
                    p = new TabProbe { Reachable = false, Reason = "did not answer: " + IdeLiveTextClient.Describe(ex, one) };
                }
                p.Ide = "IDE " + ep.Pid;
                p.Handle = ep;
                answers.Add(p);
                Write("[tab-sync] check path=" + path + " ide=" + ep.Label + " -> "
                      + (p.Reachable ? p.Kind + (p.Dirty ? " dirty" : " clean") : "unreachable: " + p.Reason)
                      + " ms=" + one.ElapsedMilliseconds);
            }
            return answers;
        }

        public static TabReload Reload(string path, TabProbe tab)
        {
            var ep = tab != null ? tab.Handle as IdeEndpointRecord.Endpoint : null;
            if (ep == null) return new TabReload { Reason = "no IDE to ask" };
            var sw = Stopwatch.StartNew();
            TabReload r;
            try
            {
                string text = IdeLiveTextClient.CallTool(ep, "editor_tab_sync",
                    new Dictionary<string, object> { { "file_path", path }, { "action", "reload" } }, sw, ReloadBudgetMs);
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

        private static Dictionary<string, object> Parse(string text)
        {
            try { return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>; }
            catch { return null; }
        }

        private static void Write(string line) { if (Log != null) try { Log(line); } catch { } }
    }
}
