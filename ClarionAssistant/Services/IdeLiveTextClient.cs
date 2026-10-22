using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 44a1b10c: the standalone clarion-mcp-server's SharedLspBridge.LiveTextProvider. lsp_diagnostics is served by the
    /// standalone (it is not IdeOnly), a separate process with no editors, so it asks the IDE pane that has the same
    /// solution open for the editor's current text through the pane's own MCP endpoint: tools/call get_live_text
    /// (an IdeOnly tool). Discovery: IdeEndpointRecord.
    ///
    /// Bounded: initialize (once per endpoint, the Mcp-Session-Id cached) plus the call, all inside TotalBudgetMs. Any
    /// failure (no matching IDE, timeout, HTTP error, refusal, file not open) is a LiveText with only a Reason, which
    /// lsp_diagnostics reports as analysed: disk plus analysedReason. Never throws.
    ///
    /// 127.0.0.1, not "localhost": .NET Framework's resolver can try ::1 first and stall (memory gotcha); and Proxy =
    /// null so the default proxy cannot eat a short timeout. The bearer token goes in the Authorization header only;
    /// it is never logged and never part of a Reason.
    ///
    /// IDE-free: linked into the standalone only.
    /// </summary>
    public static class IdeLiveTextClient
    {
        public const int TotalBudgetMs = 3000;

        /// <summary>The standalone's current solution (the one its LSP uses). Set by the standalone's Program.</summary>
        public static Func<string> SolutionProvider;
        /// <summary>--ide-pid, the IDE that launched this server, preferred among several matches.</summary>
        public static int? PreferredIdePid;
        /// <summary>Log sink (stderr trace in the standalone). Never receives the token.</summary>
        public static Action<string> Log;

        private static readonly object _sessionLock = new object();
        private static readonly Dictionary<string, string> _sessions = new Dictionary<string, string>();   // "pid:port" -> Mcp-Session-Id

        public static SharedLspBridge.LiveText Get(string path)
        {
            var sw = Stopwatch.StartNew();
            string reason;
            string sln = null;
            try { sln = SolutionProvider != null ? SolutionProvider() : null; } catch { }
            var ep = IdeEndpointRecord.Discover(sln, PreferredIdePid, out reason, Log);
            if (ep == null)
            {
                Write("[live-text] path=" + path + " ide=none reason=" + reason + " ms=" + sw.ElapsedMilliseconds);
                return new SharedLspBridge.LiveText { Reason = reason };
            }

            try
            {
                var result = CallGetLiveText(ep, path, sw);
                Write("[live-text] path=" + path + " ide=" + ep.Label + " found=" + (result.Text != null)
                    + (result.Text != null ? " origin=" + result.Origin + " chars=" + result.Text.Length : " reason=" + result.Reason)
                    + " ms=" + sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                string why = "the IDE (" + ep.Label + ") did not answer get_live_text: " + Describe(ex, sw);
                Write("[live-text] path=" + path + " ide=" + ep.Label + " failed: " + why);
                return new SharedLspBridge.LiveText { Reason = why };
            }
        }

        private static SharedLspBridge.LiveText CallGetLiveText(IdeEndpointRecord.Endpoint ep, string path, Stopwatch sw)
        {
            var args = new Dictionary<string, object> { { "file_path", path } };
            string text = CallTool(ep, "get_live_text", args, sw, TotalBudgetMs);
            if (text.StartsWith("Error", StringComparison.Ordinal)) return new SharedLspBridge.LiveText { Reason = "the IDE refused: " + text };

            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var a = js.DeserializeObject(text) as Dictionary<string, object>;
            if (a == null) throw new IOException("get_live_text did not return JSON");
            object found;
            if (!(a.TryGetValue("found", out found) && found is bool && (bool)found))
            {
                object why;
                return new SharedLspBridge.LiveText
                {
                    Reason = a.TryGetValue("reason", out why) && why is string ? "the IDE: " + (string)why : "the file is not open in the IDE"
                };
            }
            object o;
            return new SharedLspBridge.LiveText
            {
                Text = a.TryGetValue("text", out o) ? o as string : null,
                Origin = a.TryGetValue("origin", out o) ? o as string : null,
                LineOffset = a.TryGetValue("lineOffset", out o) && o != null ? Convert.ToInt32(o) : 0,
                Procedure = a.TryGetValue("procedure", out o) ? o as string : null
            };
        }

        /// <summary>
        /// One tools/call of an IdeOnly tool on the pane <paramref name="ep"/>, inside <paramref name="budgetMs"/> counted
        /// from <paramref name="sw"/> (initialize included when the session is not cached). Returns the tool's text result;
        /// throws on any transport failure (HTTP status, timeout, no result). Shared with IdeTabClient (GH #205).
        /// </summary>
        internal static string CallTool(IdeEndpointRecord.Endpoint ep, string toolName, Dictionary<string, object> args,
                                        Stopwatch sw, int budgetMs)
        {
            var res = CallToolRaw(ep, toolName, args, sw, budgetMs);
            object content;
            var list = res.TryGetValue("content", out content) ? content as object[] : null;
            var first = list != null && list.Length > 0 ? list[0] as Dictionary<string, object> : null;
            object textObj;
            string text = first != null && first.TryGetValue("text", out textObj) ? textObj as string : null;
            if (text == null) throw new IOException("empty tool result");
            return text;
        }

        /// <summary>
        /// b8f9431c: one tools/call on <paramref name="ep"/>, returning the WHOLE MCP result ({content, isError}) as the
        /// IDE sent it, for the standalone's forwarded IDE tools. Same transport and budget rules as <see cref="CallTool"/>;
        /// a non-200 reply throws <see cref="IdeHttpStatusException"/> (its Status says which), a missing result IOException.
        /// The one retry is on HTTP 400 only - the endpoint rejected the session BEFORE running anything - so a tool that
        /// changes the IDE is never sent twice.
        /// </summary>
        internal static Dictionary<string, object> CallToolRaw(IdeEndpointRecord.Endpoint ep, string toolName,
                                                               Dictionary<string, object> args, Stopwatch sw, int budgetMs)
        {
            string session = SessionFor(ep, sw, false, budgetMs);
            // MaxJsonLength: the default 2 MB cap would throw on a large forwarded write (apply_embed_edits, b8f9431c).
            string body = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" }, { "id", 2 }, { "method", "tools/call" },
                { "params", new Dictionary<string, object> { { "name", toolName }, { "arguments", args } } }
            });

            string sessionOut;
            int status;
            string reply = Post(ep, body, session, sw, budgetMs, out sessionOut, out status);
            if (status == 400)   // the pane forgot the session (restarted, or swept it): one fresh initialize
            {
                session = SessionFor(ep, sw, true, budgetMs);
                reply = Post(ep, body, session, sw, budgetMs, out sessionOut, out status);
            }
            if (status != 200) throw new IdeHttpStatusException(status);

            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
            var env = js.DeserializeObject(reply) as Dictionary<string, object>;
            object r;
            var res = env != null && env.TryGetValue("result", out r) ? r as Dictionary<string, object> : null;
            if (res == null) throw new IOException("no result in the reply");
            return res;
        }

        private static string SessionFor(IdeEndpointRecord.Endpoint ep, Stopwatch sw, bool renew, int budgetMs)
        {
            lock (_sessionLock)
            {
                string s;
                if (!renew && _sessions.TryGetValue(ep.Label, out s)) return s;
            }
            string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\","
                + "\"capabilities\":{},\"clientInfo\":{\"name\":\"clarion-mcp-server-live-text\",\"version\":\"1\"}}}";
            string sessionOut;
            int status;
            Post(ep, body, null, sw, budgetMs, out sessionOut, out status);
            if (status != 200 || string.IsNullOrEmpty(sessionOut)) throw new IOException("initialize failed (HTTP " + status + ")");
            lock (_sessionLock) { _sessions[ep.Label] = sessionOut; }
            return sessionOut;
        }

        // One POST, inside what is left of the budget. Returns the body; status and the session header via out.
        private static string Post(IdeEndpointRecord.Endpoint ep, string body, string session, Stopwatch sw, int budgetMs,
                                   out string sessionOut, out int status)
        {
            int left = budgetMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0) throw new TimeoutException("the " + budgetMs + " ms budget ran out");

            // Connect to the IP (no name resolution, so no ::1 attempt), but send Host: localhost:<port>, the host the
            // pane's HttpListener prefix is registered under (http.sys matches the prefix on the Host header).
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + ep.Port + "/mcp");
            req.Host = "localhost:" + ep.Port;
            req.Method = "POST";
            req.Proxy = null;
            req.Timeout = left;
            req.ReadWriteTimeout = left;
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.KeepAlive = false;
            req.Headers["Authorization"] = "Bearer " + ep.Token;
            if (session != null) req.Headers["Mcp-Session-Id"] = session;
            byte[] bytes = new UTF8Encoding(false).GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException wex) when (wex.Response is HttpWebResponse)
            {
                resp = (HttpWebResponse)wex.Response;   // 400/401/...: report the status, not the exception text
            }
            using (resp)
            {
                status = (int)resp.StatusCode;
                sessionOut = resp.Headers["Mcp-Session-Id"];
                using (var reader = new StreamReader(resp.GetResponseStream(), new UTF8Encoding(false)))
                    return reader.ReadToEnd();
            }
        }

        // An exception as a Reason: type and message. The token only ever travels in a request header, which no
        // WebException/IOException message includes, and HTTP error replies are reported by status, not by body.
        internal static string Describe(Exception ex, Stopwatch sw)
        {
            return ex is WebException && ((WebException)ex).Status == WebExceptionStatus.Timeout
                ? "timed out after " + sw.ElapsedMilliseconds + " ms"
                : ex.GetType().Name + ": " + ex.Message;
        }

        private static void Write(string line) { if (Log != null) try { Log(line); } catch { } }
    }

    /// <summary>b8f9431c: the IDE endpoint answered with a non-200 HTTP status. The message stays "HTTP &lt;status&gt;", as the
    /// IOException it replaces said; the body is never part of it.</summary>
    public sealed class IdeHttpStatusException : IOException
    {
        public readonly int Status;
        public IdeHttpStatusException(int status) : base("HTTP " + status) { Status = status; }
    }
}
