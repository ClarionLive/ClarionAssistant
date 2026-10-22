using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// b8f9431c: the standalone clarion-mcp-server's IDE PROXY. Outside a CA tab (MultiTerminal, VS Code, any client that
    /// loads the clarion-assistant plugin) the standalone lists every IdeOnly tool with its real schema
    /// (McpToolRegistry.AddForwardedIdeTools) and sends each call here, which hands it to the running Clarion IDE that has
    /// THIS server's solution open, over that IDE's call-back endpoint (IdeCallbackHost, ports 19390-19399; or the chat
    /// pane's server when that one holds the endpoint record), and returns the IDE's MCP result verbatim, isError included.
    ///
    /// WHICH IDE. Only an endpoint record that IdeEndpointRecord trusts (its pid is running, is Clarion, and was started
    /// when the record says), and only an IDE whose open solution (IdeSolutionRecord, verified the same way) is this
    /// server's solution by FILE IDENTITY (FileIdentity.SameFile: the same file under a junction, subst drive or 8.3 name
    /// still matches; a network path is compared as text and never opened). An IDE with a different solution, or none, is
    /// never used. With several matching IDEs the call is REFUSED with the list, unless CLARION_IDE_PID names one of them:
    /// there is no reliable cross-process "most recently active" signal (the endpoint record is written when a server
    /// starts, the solution record when the solution changes), and a guess could send write_embed_content or import_txa
    /// to the IDE the developer is not looking at.
    ///
    /// HOW LONG. The IDE's own UI-dispatch timeout decides (McpDispatcher there: McpUiTimeoutPolicy.Resolve of the
    /// Mcp.UiToolTimeoutSeconds setting and the tool's declared minimum, e.g. 180 s for open_procedure_embed); this side
    /// waits that long PLUS <see cref="MarginSeconds"/>, so the IDE's timeout message (which says whether the call ran)
    /// arrives before this side gives up. A tool with a "timeout" argument (build_app, generate_source) waits that long
    /// plus the margin too.
    ///
    /// SECURITY. The connection is to 127.0.0.1 only (IdeLiveTextClient.Post: never a host from the record, never
    /// "localhost", Proxy = null). The token is the record's, sent in the Authorization header only; it is never logged
    /// and never part of an error text. A call is never re-sent after it may have reached the IDE (the only retry is
    /// IdeLiveTextClient's on HTTP 400, a rejected session, before anything ran).
    ///
    /// IDE-free (linked into the standalone; compiled by the harnesses). Never throws: every failure is an isError result.
    /// </summary>
    public static class IdeToolForwarder
    {
        /// <summary>Set to an IDE's process id to pick it when several IDEs have this solution open.</summary>
        public const string PinPidEnv = "CLARION_IDE_PID";

        /// <summary>What this side waits beyond the IDE's own UI-dispatch timeout.</summary>
        public const int MarginSeconds = 30;

        /// <summary>The longest any one forwarded call is waited for, whatever a setting or argument says.</summary>
        public const int MaxSeconds = 3600;

        /// <summary>The add-in release that serves the IDE tools on its call-back endpoint.</summary>
        public const string MinAddinVersion = "6.2";

        /// <summary>Appended to every forwarded tool's description in tools/list.</summary>
        public const string DescriptionNote = "[Runs in the Clarion IDE: needs Clarion open on this server's solution, with Clarion Assistant "
            + MinAddinVersion + " or later installed in that Clarion. This server forwards the call to that IDE.]";

        /// <summary>The server's current solution. Set by the standalone's Program.</summary>
        public static Func<string> SolutionProvider;
        /// <summary>Why there is no solution (the workspace's resolution note), for the no-solution error. Optional.</summary>
        public static Func<string> SolutionNote;
        /// <summary>Log sink (stderr trace in the standalone). Never receives the token.</summary>
        public static Action<string> Log;
        /// <summary>The raw Mcp.UiToolTimeoutSeconds setting, or null. Default: %APPDATA%\ClarionAssistant\settings.txt.</summary>
        public static Func<string> UiTimeoutSettingReader = ReadSetting;

        // ---- test seams (production defaults) ----
        /// <summary>Every trusted IDE endpoint, one per IDE; the reason when there is none.</summary>
        public static Func<Tuple<List<IdeEndpointRecord.Endpoint>, string>> Endpoints = () =>
        {
            string reason;
            var all = IdeEndpointRecord.DiscoverAll(out reason, Log);
            return Tuple.Create(all, reason);
        };
        /// <summary>The verified open solution of the IDE with this pid, or null.</summary>
        public static Func<int, string> OpenSolutionOf = pid => { string note; return IdeSolutionRecord.Read(pid, out note); };
        /// <summary>One tools/call: the whole MCP result. Throws on transport failure.</summary>
        public static Func<IdeEndpointRecord.Endpoint, string, Dictionary<string, object>, int, Dictionary<string, object>> Caller =
            (ep, name, args, budgetMs) => IdeLiveTextClient.CallToolRaw(ep, name, args, Stopwatch.StartNew(), budgetMs);
        /// <summary>"C:\Clarion12" for an IDE pid (its Clarion.exe two folders up), or null.</summary>
        public static Func<int, string> ClarionRootOf = ReadClarionRoot;
        /// <summary>The CLARION_IDE_PID value. A seam so a test does not have to set the process environment.</summary>
        public static Func<string> PinnedPid = () => Environment.GetEnvironmentVariable(PinPidEnv);

        /// <summary>The IDE a call would go to, or why none.</summary>
        public sealed class Selection
        {
            public IdeEndpointRecord.Endpoint Ide;
            public string Solution;
            /// <summary>Set when <see cref="Ide"/> is null: the complete error text (starts "Error:").</summary>
            public string Error;
        }

        /// <summary>The forwarded tool's handler. Returns an McpRawToolResult: the IDE's result as it came, or an error.</summary>
        public static object Forward(McpTool tool, Dictionary<string, object> args)
        {
            string name = tool != null ? tool.Name : "(unnamed)";
            var sw = Stopwatch.StartNew();
            Selection sel;
            try { sel = Select(name); }
            catch (Exception ex) { sel = new Selection { Error = "Error: " + name + " was not sent: the Clarion IDE could not be looked up (" + ex.GetType().Name + ": " + ex.Message + ")." }; }
            if (sel.Ide == null)
            {
                Write("[ide-proxy] " + name + " -> not sent: " + FirstLine(sel.Error));
                return McpRawToolResult.Error(sel.Error);
            }

            string setting = null;
            try { setting = UiTimeoutSettingReader != null ? UiTimeoutSettingReader() : null; } catch { setting = null; }
            int budgetMs = BudgetMs(tool, args, setting);
            var ep = sel.Ide;
            Dictionary<string, object> res;
            try
            {
                res = Caller(ep, name, args ?? new Dictionary<string, object>(), budgetMs);
            }
            catch (Exception ex)
            {
                string why = TransportError(name, ep, sel.Solution, ex, budgetMs, sw);
                Write("[ide-proxy] " + name + " -> IDE " + ep.Label + " failed: " + FirstLine(why) + " ms=" + sw.ElapsedMilliseconds);
                return McpRawToolResult.Error(why);
            }

            var raw = new McpRawToolResult { Result = res };
            if (raw.IsError && IsUnknownToolReply(raw.FirstText, name))
            {
                string older = OlderAddinError(name, ep, sel.Solution);
                Write("[ide-proxy] " + name + " -> IDE " + ep.Label + ": older add-in (Unknown tool) ms=" + sw.ElapsedMilliseconds);
                return McpRawToolResult.Error(older);
            }
            Write("[ide-proxy] " + name + " -> IDE " + ep.Label + (raw.IsError ? " isError" : " ok") + " ms=" + sw.ElapsedMilliseconds);
            return raw;
        }

        /// <summary>Which IDE gets a call of <paramref name="toolName"/>: the rules in the class summary.</summary>
        public static Selection Select(string toolName)
        {
            string sln = null;
            try { sln = SolutionProvider != null ? SolutionProvider() : null; } catch { sln = null; }
            if (string.IsNullOrEmpty(sln))
            {
                string note = null;
                try { note = SolutionNote != null ? SolutionNote() : null; } catch { }
                if (note != null) note = note.Trim().TrimEnd('.');
                return new Selection
                {
                    Error = "Error: " + toolName + " runs in the Clarion IDE, and this server has no solution to match an IDE to"
                        + (string.IsNullOrEmpty(note) ? "" : " (" + note + ")")
                        + ". Start this client in the solution's folder (or pass --solution), open that solution in Clarion "
                        + "(with Clarion Assistant " + MinAddinVersion + " or later installed in that Clarion), then call it again."
                };
            }

            var found = Endpoints();
            var all = found != null && found.Item1 != null ? found.Item1 : new List<IdeEndpointRecord.Endpoint>();
            string slnName = Path.GetFileName(sln);
            string openIt = " Open " + sln + " in Clarion (with Clarion Assistant " + MinAddinVersion
                + " or later installed in that Clarion), then call it again.";
            if (all.Count == 0)
            {
                return new Selection
                {
                    Solution = sln,
                    Error = "Error: " + toolName + " runs in the Clarion IDE, and no running Clarion IDE can be reached ("
                        + (found != null && !string.IsNullOrEmpty(found.Item2) ? found.Item2 : "no IDE endpoint found") + ")." + openIt
                };
            }

            var matches = new List<IdeEndpointRecord.Endpoint>();
            var others = new List<string>();
            foreach (var ep in all)
            {
                string ideSln = null;
                try { ideSln = OpenSolutionOf(ep.Pid); } catch { ideSln = null; }
                bool same = !string.IsNullOrEmpty(ideSln) && FileIdentity.SameFile(ideSln, sln);
                Write("[ide-proxy] candidate IDE " + ep.Label + " solution=" + (ideSln ?? "none") + (same ? " MATCH" : " (other)"));
                if (same) matches.Add(ep);
                else others.Add("Clarion pid " + ep.Pid + (string.IsNullOrEmpty(ideSln) ? " has no solution open" : " has " + Path.GetFileName(ideSln) + " open"));
            }

            if (matches.Count == 0)
            {
                return new Selection
                {
                    Solution = sln,
                    Error = "Error: " + toolName + " runs in the Clarion IDE, and no running Clarion has " + slnName + " open ("
                        + string.Join("; ", others.ToArray()) + "). An IDE with a different solution is never used." + openIt
                };
            }

            string pinRaw = null;
            try { pinRaw = PinnedPid != null ? PinnedPid() : null; } catch { }
            int pin;
            if (!string.IsNullOrEmpty(pinRaw) && int.TryParse(pinRaw.Trim(), out pin) && pin > 0)
            {
                var pinned = matches.Find(m => m.Pid == pin);
                if (pinned != null) return new Selection { Ide = pinned, Solution = sln };
                return new Selection
                {
                    Solution = sln,
                    Error = "Error: " + toolName + " was not sent: " + PinPidEnv + "=" + pin + " names no running Clarion that has "
                        + slnName + " open (" + Describe(matches) + " " + (matches.Count == 1 ? "has" : "have") + " it open). "
                        + "Correct or clear " + PinPidEnv + " in this client's environment and restart the client."
                };
            }

            if (matches.Count == 1) return new Selection { Ide = matches[0], Solution = sln };

            return new Selection
            {
                Solution = sln,
                Error = "Error: " + toolName + " was not sent: " + matches.Count + " Clarion IDEs have " + slnName + " open ("
                    + Describe(matches) + "), and it could change the wrong one. Close all but one, or set " + PinPidEnv
                    + "=<pid> in this client's environment and restart the client."
            };
        }

        /// <summary>How long to wait for one forwarded call, in ms: the IDE's UI budget for the tool plus the margin, or
        /// the tool's own "timeout" argument plus the margin when that is longer; at most <see cref="MaxSeconds"/>.</summary>
        public static int BudgetMs(McpTool tool, Dictionary<string, object> args, string uiTimeoutSettingRaw)
        {
            int declared = tool != null ? tool.UiTimeoutSeconds : 0;
            int seconds = McpUiTimeoutPolicy.Resolve(uiTimeoutSettingRaw, declared) + MarginSeconds;
            if (HasProperty(tool, "timeout"))
            {
                int argSeconds = McpJsonRpc.GetInt(args, "timeout", 120);
                if (argSeconds > 0 && argSeconds + MarginSeconds > seconds) seconds = argSeconds + MarginSeconds;
            }
            if (seconds > MaxSeconds) seconds = MaxSeconds;
            return seconds * 1000;
        }

        /// <summary>True for the IDE's answer to a tool it does not have: "Error executing tool 'x': Unknown tool: x".</summary>
        public static bool IsUnknownToolReply(string text, string toolName)
        {
            return !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(toolName)
                && text.IndexOf("Unknown tool: " + toolName, StringComparison.Ordinal) >= 0;
        }

        private static string OlderAddinError(string toolName, IdeEndpointRecord.Endpoint ep, string sln)
        {
            string root = null;
            try { root = ClarionRootOf != null ? ClarionRootOf(ep.Pid) : null; } catch { }
            return "Error: " + toolName + " is not available from the Clarion IDE that has " + Path.GetFileName(sln) + " open (pid "
                + ep.Pid + "): it runs an older Clarion Assistant" + (string.IsNullOrEmpty(root) ? "" : " (in " + root + ")")
                + " that does not serve the IDE tools to other clients. Update Clarion Assistant in that Clarion to "
                + MinAddinVersion + " or later, restart Clarion, and call it again. (A Clarion Assistant tab inside that IDE "
                + "has these tools already.)";
        }

        private static string TransportError(string toolName, IdeEndpointRecord.Endpoint ep, string sln, Exception ex,
                                             int budgetMs, Stopwatch sw)
        {
            string ide = "the Clarion IDE that has " + Path.GetFileName(sln) + " open (pid " + ep.Pid + ")";
            var status = ex as IdeHttpStatusException;
            if (status != null)
            {
                if (status.Status == 401)
                    return "Error: " + toolName + " was refused by " + ide + " (HTTP 401): this server's credentials for it are out "
                        + "of date (its endpoint record changed). Call it again; if it persists, restart Clarion.";
                if (status.Status == 413)
                    return "Error: " + toolName + " was refused by " + ide + " (HTTP 413): the request is larger than the IDE "
                        + "accepts. Nothing was run. Send less in one call.";
                return "Error: " + toolName + " was refused by " + ide + " (HTTP " + status.Status + "). Nothing was run.";
            }
            var wex = ex as WebException;
            if (wex != null && (wex.Status == WebExceptionStatus.ConnectFailure || wex.Status == WebExceptionStatus.NameResolutionFailure))
                return "Error: " + toolName + " was not sent: " + ide + " did not accept the connection (" + wex.Message + "). "
                    + "It may be starting up or closing; call it again in a moment.";
            if (ex is TimeoutException || (wex != null && wex.Status == WebExceptionStatus.Timeout))
                return "Error: " + ide + " did not answer " + toolName + " within " + (budgetMs / 1000) + " s. The call may still be "
                    + "running in the IDE, or may have finished: check the IDE (re-read with get_embeditor_source, get_active_file "
                    + "or similar) before calling it again.";
            return "Error: " + toolName + " failed on the way to or from " + ide + ": " + ex.GetType().Name + ": " + ex.Message
                + ". The call may or may not have run in the IDE: check it before calling again.";
        }

        private static string Describe(List<IdeEndpointRecord.Endpoint> eps)
        {
            var parts = new List<string>();
            foreach (var e in eps)
            {
                string root = null;
                try { root = ClarionRootOf != null ? ClarionRootOf(e.Pid) : null; } catch { }
                parts.Add("pid " + e.Pid + (string.IsNullOrEmpty(root) ? "" : " in " + root));
            }
            return string.Join(", ", parts.ToArray());
        }

        private static bool HasProperty(McpTool tool, string prop)
        {
            if (tool == null || tool.InputSchema == null) return false;
            object p;
            var props = tool.InputSchema.TryGetValue("properties", out p) ? p as Dictionary<string, object> : null;
            return props != null && props.ContainsKey(prop);
        }

        private static string ReadSetting()
        {
            try { return new SettingsService().Get(McpUiTimeoutPolicy.SettingKey); }
            catch { return null; }
        }

        private static string ReadClarionRoot(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return Path.GetDirectoryName(Path.GetDirectoryName(p.MainModule.FileName));   // <Clarion>\bin\Clarion.exe
            }
            catch { return null; }
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        private static void Write(string line) { var l = Log; if (l != null) try { l(line); } catch { } }
    }
}
