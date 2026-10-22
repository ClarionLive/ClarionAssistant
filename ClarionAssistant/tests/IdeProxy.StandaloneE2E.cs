// IdeProxy.StandaloneE2E.cs - ticket b8f9431c, end to end through BOTH processes.
//
// WHAT IT PROVES. A client that loads the clarion-assistant plugin (MultiTerminal, VS Code) runs ONLY the standalone
// clarion-mcp-server.exe. It must list every IdeOnly tool with its real schema and forward a call to a running Clarion
// IDE that has the SAME solution open, returning the IDE's MCP result verbatim (isError included); and explain itself
// when it cannot. This drives the REAL standalone exe over stdio, launched the way the plugin launcher does (no
// --ide-pid), against a stand-in IDE: an HttpListener on a localhost prefix (like IdeCallbackHost's McpServer) with a
// bearer token, found through the real endpoint and solution records in a temp CA_IDE_RECORD_DIR, with
// CA_IDE_IMAGE_NAME naming this driver's own image so the record trust rules (pid alive, image, start time) apply.
//
//   A  list        tools/list has all 120: the 59 IdeOnly ones with their real schemas and the forwarding note
//   A2 CA tab      launched with --ide-pid (a CA tab): the 61 own tools only - never listed twice
//   B  no IDE      no endpoint record                  -> isError, names the solution, "open it in Clarion", 6.2
//   C  match       the IDE has this solution open      -> the IDE's result VERBATIM (two blocks + isError false),
//                                                         the arguments arrived as sent; an IDE isError stays isError
//   C2 identity    the IDE names the solution through a JUNCTION -> still the same solution (FileIdentity)
//   D  other sln   the only IDE has another solution   -> isError naming both; the IDE is never contacted
//   E  older addin the IDE answers "Unknown tool"      -> isError "update Clarion Assistant in that Clarion"
//   F  several     two IDEs have this solution open    -> refused with the list, neither contacted;
//   F2 pinned      ...with CLARION_IDE_PID naming one  -> sent to that one only
//   G  bad token   the record's token is wrong (401)   -> isError naming HTTP 401
//   H  loopback    every request reached the IDE from 127.0.0.1 to 127.0.0.1 with Host: localhost:<port>
//   I  pid reuse   a record whose start time is not the process's -> not used, deleted, logged
//   J  no solution a server with no solution           -> isError "no solution to match an IDE to"
//   T  token       the bearer token appears in no tool output and no log line
//
// Args: <standalone exe> <work dir>. Exit 0 pass, 1 fail, 2 could-not-run. "--idle" makes this exe a further
// stand-in IDE PROCESS (same image name) for F and I.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class IdeProxyE2E
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static void Check(bool ok, string message) { _assertions++; if (!ok) Failures.Add(message); }

    // ---- the stand-in IDE (IdeCallbackHost's McpServer) ----
    static HttpListener _http;
    static int _port;
    static readonly string _token = "e2e-proxy-" + Guid.NewGuid().ToString("N");   // must never leak into any output
    static bool _olderAddin;
    static readonly object _seenLock = new object();
    static readonly List<string> _seen = new List<string>();          // "<tool> <args json>" per tools/call that ARRIVED
    static readonly List<string> _peers = new List<string>();         // "remote|local|host" per request
    static int _requests;

    static void StartIde()
    {
        for (_port = 19700; _port < 19790; _port++)
        {
            try { _http = new HttpListener(); _http.Prefixes.Add("http://localhost:" + _port + "/"); _http.Start(); break; }
            catch { try { _http.Close(); } catch { } _http = null; }
        }
        if (_http == null) throw new Exception("no free port for the stand-in IDE");
        new Thread(() =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _http.GetContext(); } catch { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
            }
        }) { IsBackground = true }.Start();
    }

    static void Serve(HttpListenerContext ctx)
    {
        try
        {
            Interlocked.Increment(ref _requests);
            lock (_seenLock) _peers.Add(ctx.Request.RemoteEndPoint.Address + "|" + ctx.Request.LocalEndPoint.Address + "|" + ctx.Request.Headers["Host"]);
            if (ctx.Request.Headers["Authorization"] != "Bearer " + _token) { Reply(ctx, 401, "{\"error\":\"unauthorized\"}", null); return; }
            string body = new StreamReader(ctx.Request.InputStream, new UTF8Encoding(false)).ReadToEnd();
            var req = Js.DeserializeObject(body) as Dictionary<string, object>;
            string method = req["method"] as string;
            if (method == "initialize") { Reply(ctx, 200, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", "sess-1"); return; }
            if (ctx.Request.Headers["Mcp-Session-Id"] != "sess-1") { Reply(ctx, 400, "{\"error\":\"missing or invalid Mcp-Session-Id\"}", null); return; }
            var prm = req["params"] as Dictionary<string, object>;
            string name = prm["name"] as string;
            var args = prm["arguments"] as Dictionary<string, object>;
            lock (_seenLock) _seen.Add(name + " " + Js.Serialize(args));

            Dictionary<string, object> result;
            if (_olderAddin)
                result = Text("Error executing tool '" + name + "': Unknown tool: " + name, true);
            else if (name == "save_file")
                result = Text("Error: simulated IDE refusal for save_file", true);
            else
                result = new Dictionary<string, object>
                {
                    { "content", new object[]
                        {
                            new Dictionary<string, object> { { "type", "text" }, { "text", "IDE ran " + name + " pid " + Process.GetCurrentProcess().Id } },
                            new Dictionary<string, object> { { "type", "text" }, { "text", "second block åé " + Js.Serialize(args) } }
                        }
                    },
                    { "isError", false }
                };
            Reply(ctx, 200, Js.Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", req["id"] }, { "result", result } }), null);
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    static Dictionary<string, object> Text(string t, bool isError)
    {
        return new Dictionary<string, object>
        {
            { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", t } } } },
            { "isError", isError }
        };
    }

    static void Reply(HttpListenerContext ctx, int status, string json, string session)
    {
        var b = new UTF8Encoding(false).GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        if (session != null) ctx.Response.Headers["Mcp-Session-Id"] = session;
        ctx.Response.ContentLength64 = b.Length;
        ctx.Response.OutputStream.Write(b, 0, b.Length);
        ctx.Response.Close();
    }

    // ---- the records the standalone discovers through ----
    static string _recDir, _sln, _otherSln;

    static void WriteEndpoint(Process ide, int port, string token, DateTime? startOverride = null)
    {
        string dir = Path.Combine(_recDir, "ide-endpoint");
        Directory.CreateDirectory(dir);
        var start = (startOverride ?? ide.StartTime).ToUniversalTime();
        File.WriteAllText(Path.Combine(dir, ide.Id + "-" + port + ".json"), Js.Serialize(new Dictionary<string, object>
        {
            { "pid", ide.Id }, { "port", port }, { "token", token },
            { "startTime", start.ToString("o") }, { "writtenAt", DateTime.UtcNow.ToString("o") }
        }), new UTF8Encoding(false));
    }

    static void WriteSolution(Process ide, string sln)
    {
        string dir = Path.Combine(_recDir, "ide-solution");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ide-" + ide.Id + ".json"), Js.Serialize(new Dictionary<string, object>
        {
            { "solution", sln }, { "pid", ide.Id }, { "writtenAt", DateTime.Now.ToString("o") }
        }), new UTF8Encoding(false));
    }

    static void ClearRecords()
    {
        foreach (var d in new[] { "ide-endpoint", "ide-solution" })
        {
            string p = Path.Combine(_recDir, d);
            if (Directory.Exists(p)) foreach (var f in Directory.GetFiles(p)) File.Delete(f);
        }
    }

    // ---- a standalone, over stdio ----
    sealed class Server
    {
        public Process P;
        public readonly StringBuilder Err = new StringBuilder();
        public readonly StringBuilder Out = new StringBuilder();
        public string Instructions;
        int _id = 10;

        public static Server Start(string exe, string args, string cwd, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true, WorkingDirectory = cwd
            };
            psi.EnvironmentVariables["CA_IDE_RECORD_DIR"] = _recDir;
            psi.EnvironmentVariables["CA_IDE_IMAGE_NAME"] = Process.GetCurrentProcess().ProcessName;   // the stand-in IDE is this process
            psi.EnvironmentVariables.Remove("CLARION_IDE_PID");
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var s = new Server { P = Process.Start(psi) };
            s.P.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (s.Err) s.Err.AppendLine(e.Data); };
            s.P.BeginErrorReadLine();
            var init = s.Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"e2e\",\"version\":\"1\"}}}", 1);
            object ins;
            var ir = init["result"] as Dictionary<string, object>;
            s.Instructions = ir != null && ir.TryGetValue("instructions", out ins) ? ins as string : null;
            s.P.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");
            s.P.StandardInput.Flush();
            return s;
        }

        public Dictionary<string, object> Send(string line, int id)
        {
            P.StandardInput.WriteLine(line);
            P.StandardInput.Flush();
            while (true)
            {
                string outLine = P.StandardOutput.ReadLine();
                if (outLine == null) throw new Exception("the standalone exited; stderr:\n" + Tail(Err.ToString()));
                lock (Out) Out.AppendLine(outLine);
                Dictionary<string, object> m;
                try { m = Js.DeserializeObject(outLine) as Dictionary<string, object>; } catch { continue; }
                if (m == null || !m.ContainsKey("id") || m["id"] == null || Convert.ToInt32(m["id"]) != id) continue;
                return m;
            }
        }

        public List<Dictionary<string, object>> Tools()
        {
            int id = _id++;
            var m = Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/list\",\"params\":{}}", id);
            var res = m["result"] as Dictionary<string, object>;
            return ((object[])res["tools"]).Cast<Dictionary<string, object>>().ToList();
        }

        /// <summary>The tools/call result object (content + isError), or a "jsonrpcError" entry.</summary>
        public Dictionary<string, object> Call(string tool, Dictionary<string, object> args)
        {
            int id = _id++;
            var m = Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":" + Js.Serialize(tool)
                         + ",\"arguments\":" + Js.Serialize(args ?? new Dictionary<string, object>()) + "}}", id);
            if (m.ContainsKey("error")) return new Dictionary<string, object> { { "jsonrpcError", Js.Serialize(m["error"]) } };
            return m["result"] as Dictionary<string, object>;
        }

        public void Stop()
        {
            try { P.StandardInput.Close(); if (!P.WaitForExit(15000)) P.Kill(); } catch { }
        }
    }

    static string Tail(string s) { return string.Join("\n", s.Split('\n').Reverse().Take(15).Reverse()); }
    static bool IsErr(Dictionary<string, object> r) { object e; return r != null && r.TryGetValue("isError", out e) && e is bool && (bool)e; }
    static string FirstText(Dictionary<string, object> r)
    {
        if (r == null) return null;
        object j; if (r.TryGetValue("jsonrpcError", out j)) return "JSON-RPC error " + j;
        object c; if (!r.TryGetValue("content", out c)) return null;
        var l = c as object[]; if (l == null || l.Length == 0) return null;
        object t; return (l[0] as Dictionary<string, object>).TryGetValue("text", out t) ? t as string : null;
    }
    static string Show(Dictionary<string, object> r) { return r == null ? "(null)" : Js.Serialize(r); }
    static int Seen() { lock (_seenLock) return _seen.Count; }
    static string LastSeen() { lock (_seenLock) return _seen.Count > 0 ? _seen[_seen.Count - 1] : null; }

    static Process Idle()
    {
        var p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--idle") { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(300);
        return p;
    }

    static int Main(string[] argv)
    {
        if (argv.Length > 0 && argv[0] == "--idle") { Thread.Sleep(180000); return 0; }
        string exe = argv[0], work = argv[1];
        _recDir = Path.Combine(work, "records");
        string src = Path.Combine(work, "src");
        string empty = Path.Combine(work, "empty");
        Directory.CreateDirectory(src); Directory.CreateDirectory(_recDir); Directory.CreateDirectory(empty);
        _sln = Path.Combine(src, "e2e.sln");
        File.WriteAllText(_sln, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n", new UTF8Encoding(false));
        _otherSln = Path.Combine(src, "other.sln");
        File.WriteAllText(_otherSln, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n", new UTF8Encoding(false));
        string slnArgs = "--stdio --debug --solution \"" + _sln + "\"";

        var me = Process.GetCurrentProcess();
        StartIde();
        Process idleA = null, idleB = null;
        var servers = new List<Server>();
        try
        {
            // ---------------- A: tools/list in plugin mode ----------------
            var srv = Server.Start(exe, slnArgs, src, null); servers.Add(srv);
            var tools = srv.Tools();
            var byName = tools.ToDictionary(t => (string)t["name"], t => t);
            Console.WriteLine("A  list      " + tools.Count + " tools");
            Check(tools.Count == 120, "A: expected 120 tools (61 own + 59 IDE), got " + tools.Count);
            int noted = tools.Count(t => ((string)t["description"]).Contains("This server forwards the call to that IDE"));
            Check(noted == 59, "A: expected the 59 IdeOnly tools to carry the forwarding note, got " + noted);
            foreach (var want in new[] { "get_app_info", "list_procedures", "open_procedure_embed", "search_embeditor_source",
                                         "write_embed_content", "save_and_close_embeditor", "replace_text", "open_file", "show_diff" })
                Check(byName.ContainsKey(want), "A: IdeOnly tool '" + want + "' is not listed");
            if (byName.ContainsKey("write_embed_content"))
            {
                var props = ((Dictionary<string, object>)byName["write_embed_content"]["inputSchema"])["properties"] as Dictionary<string, object>;
                Check(props != null && props.ContainsKey("line_number") && props.ContainsKey("code"),
                    "A: write_embed_content is listed without its real schema (line_number, code): " + Show(byName["write_embed_content"]));
            }
            if (byName.ContainsKey("open_procedure_embed"))
                Check(((string)byName["open_procedure_embed"]["description"]).Contains("Clarion Assistant 6.2 or later"),
                    "A: the forwarding note does not say which add-in is needed");
            Check((srv.Instructions ?? "").Contains("without the developer's explicit approval"),
                "A: the initialize instructions do not carry the write-approval rule for the IDE tools: " + (srv.Instructions ?? "(none)"));
            string err; lock (srv.Err) err = srv.Err.ToString();
            Check(err.Contains("IDE tools: 59 listed") && err.Contains("ide-proxy=1"), "A: no 'IDE tools: 59 listed ... ide-proxy=1' line on stderr:\n" + Tail(err));

            // ---------------- B: no IDE at all ----------------
            ClearRecords();
            int before = Seen();
            var rB = srv.Call("get_app_info", null);
            Console.WriteLine("B  no IDE    " + FirstText(rB));
            // (the server names the solution by its long path; %TEMP% may be a short 8.3 one, so match the file name)
            Check(IsErr(rB) && (FirstText(rB) ?? "").Contains("e2e.sln in Clarion") && (FirstText(rB) ?? "").Contains("Clarion Assistant 6.2 or later"),
                "B: expected isError naming the solution and 'Clarion Assistant 6.2 or later'; got " + Show(rB));
            Check(Seen() == before, "B: the IDE was contacted with no record");

            // ---------------- C: the matching IDE ----------------
            WriteEndpoint(me, _port, _token); WriteSolution(me, _sln);
            var callArgs = new Dictionary<string, object> { { "filter", "Upd*" }, { "n", 3 }, { "code", "  ! comment å\r\n" } };
            var rC = srv.Call("list_procedures", callArgs);
            Console.WriteLine("C  match     " + FirstText(rC));
            var cContent = rC != null && rC.ContainsKey("content") ? rC["content"] as object[] : null;
            Check(!IsErr(rC) && cContent != null && cContent.Length == 2
                  && FirstText(rC) == "IDE ran list_procedures pid " + me.Id
                  && ((cContent[1] as Dictionary<string, object>)["text"] as string) == "second block åé " + Js.Serialize(callArgs),
                "C: expected the IDE's two content blocks verbatim (non-ASCII intact) with isError false; got " + Show(rC));
            Check(LastSeen() == "list_procedures " + Js.Serialize(callArgs), "C: the IDE did not receive the arguments as sent: " + LastSeen());
            var rC2 = srv.Call("save_file", null);
            Check(IsErr(rC2) && FirstText(rC2) == "Error: simulated IDE refusal for save_file",
                "C: an IDE isError result must come back verbatim as isError; got " + Show(rC2));
            var rC3 = srv.Call("open_procedure_embed", new Dictionary<string, object> { { "procedure", "tes:TestProcedure" } });
            Check(!IsErr(rC3) && FirstText(rC3) == "IDE ran open_procedure_embed pid " + me.Id, "C: open_procedure_embed was not forwarded: " + Show(rC3));

            // ---------------- C2: same solution through a junction ----------------
            string alias = Path.Combine(work, "alias");
            var mk = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + alias + "\" \"" + src + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            mk.WaitForExit(10000);
            if (Directory.Exists(alias))
            {
                ClearRecords();
                WriteEndpoint(me, _port, _token); WriteSolution(me, Path.Combine(alias, "e2e.sln"));
                var rC4 = srv.Call("get_app_info", null);
                Console.WriteLine("C2 junction  " + FirstText(rC4));
                Check(!IsErr(rC4) && FirstText(rC4) == "IDE ran get_app_info pid " + me.Id,
                    "C2: an IDE naming this solution through a junction must match it (file identity); got " + Show(rC4));
            }
            else Check(false, "C2: could not make a junction to test file-identity matching");

            // ---------------- D: the only IDE has another solution ----------------
            ClearRecords();
            WriteEndpoint(me, _port, _token); WriteSolution(me, _otherSln);
            before = Seen();
            var rD = srv.Call("write_embed_content", new Dictionary<string, object> { { "line_number", 5 }, { "code", "! x" } });
            Console.WriteLine("D  other sln " + FirstText(rD));
            Check(IsErr(rD) && (FirstText(rD) ?? "").Contains("no running Clarion has e2e.sln open") && (FirstText(rD) ?? "").Contains("other.sln")
                  && (FirstText(rD) ?? "").Contains("never used"),
                "D: expected isError naming e2e.sln and the IDE's other.sln; got " + Show(rD));
            Check(Seen() == before, "D: a write reached an IDE that has a DIFFERENT solution open");

            // ---------------- E: an older add-in ----------------
            ClearRecords();
            WriteEndpoint(me, _port, _token); WriteSolution(me, _sln);
            _olderAddin = true;
            var rE = srv.Call("get_app_info", null);
            _olderAddin = false;
            Console.WriteLine("E  older     " + FirstText(rE));
            Check(IsErr(rE) && (FirstText(rE) ?? "").Contains("older Clarion Assistant") && (FirstText(rE) ?? "").Contains("Update Clarion Assistant in that Clarion")
                  && !(FirstText(rE) ?? "").Contains("Error executing tool"),
                "E: expected the 'update Clarion Assistant in that Clarion' message; got " + Show(rE));

            // ---------------- F: two IDEs on this solution ----------------
            ClearRecords();
            idleA = Idle(); idleB = Idle();
            WriteEndpoint(idleA, _port, _token); WriteSolution(idleA, _sln);
            WriteEndpoint(idleB, _port, _token); WriteSolution(idleB, _sln);
            before = Seen();
            var rF = srv.Call("replace_text", new Dictionary<string, object> { { "old_text", "a" }, { "new_text", "b" } });
            Console.WriteLine("F  several   " + FirstText(rF));
            Check(IsErr(rF) && (FirstText(rF) ?? "").Contains("2 Clarion IDEs have e2e.sln open") && (FirstText(rF) ?? "").Contains("pid " + idleA.Id)
                  && (FirstText(rF) ?? "").Contains("pid " + idleB.Id) && (FirstText(rF) ?? "").Contains("CLARION_IDE_PID"),
                "F: expected a refusal listing both IDEs and naming CLARION_IDE_PID; got " + Show(rF));
            Check(Seen() == before, "F: a write was sent although two IDEs have the solution open");

            // ---------------- F2: pinned with CLARION_IDE_PID ----------------
            var pinned = Server.Start(exe, slnArgs, src, new Dictionary<string, string> { { "CLARION_IDE_PID", idleB.Id.ToString() } }); servers.Add(pinned);
            var rF2 = pinned.Call("get_app_info", null);
            Console.WriteLine("F2 pinned    " + FirstText(rF2));
            Check(!IsErr(rF2) && (FirstText(rF2) ?? "").StartsWith("IDE ran get_app_info"), "F2: CLARION_IDE_PID did not pick the named IDE: " + Show(rF2));
            pinned.Stop();

            // ---------------- G: a wrong token ----------------
            ClearRecords();
            WriteEndpoint(me, _port, "wrong-" + _token); WriteSolution(me, _sln);
            var rG = srv.Call("get_app_info", null);
            Console.WriteLine("G  token     " + FirstText(rG));
            Check(IsErr(rG) && (FirstText(rG) ?? "").Contains("HTTP 401"), "G: expected isError naming HTTP 401; got " + Show(rG));

            // ---------------- I: pid reuse ----------------
            ClearRecords();
            WriteEndpoint(idleA, _port, _token, DateTime.Now.AddHours(-3)); WriteSolution(idleA, _sln);
            string reuseFile = Path.Combine(_recDir, "ide-endpoint", idleA.Id + "-" + _port + ".json");
            before = Seen();
            var rI = srv.Call("get_app_info", null);
            Console.WriteLine("I  pid reuse " + FirstText(rI));
            Thread.Sleep(200);
            lock (srv.Err) err = srv.Err.ToString();
            Check(IsErr(rI) && Seen() == before && !File.Exists(reuseFile) && err.Contains("pid reused"),
                "I: a record whose start time is not the process's must be deleted and logged, and nothing sent; got " + Show(rI)
                + " fileExists=" + File.Exists(reuseFile));

            // ---------------- J: no solution ----------------
            var noSln = Server.Start(exe, "--stdio --debug", empty, null); servers.Add(noSln);
            var rJ = noSln.Call("get_app_info", null);
            Console.WriteLine("J  no sln    " + FirstText(rJ));
            Check(IsErr(rJ) && (FirstText(rJ) ?? "").Contains("no solution to match an IDE to"), "J: expected the no-solution error; got " + Show(rJ));
            noSln.Stop();

            // ---------------- A2: a CA tab's standalone (--ide-pid) lists only its own tools ----------------
            ClearRecords();
            WriteSolution(me, _sln);
            var tab = Server.Start(exe, slnArgs + " --ide-pid " + me.Id, src, null); servers.Add(tab);
            var tabTools = tab.Tools();
            Console.WriteLine("A2 CA tab    " + tabTools.Count + " tools");
            Check(tabTools.Count == 61 && !tabTools.Any(t => (string)t["name"] == "open_procedure_embed"),
                "A2: a server launched by an IDE (--ide-pid) must not list the IDE tools its pane already serves; got " + tabTools.Count);
            Check(!(tab.Instructions ?? "").Contains("explicit approval"), "A2: a CA tab's server repeats the IDE-tool instructions its CLAUDE.md already states");
            tab.Stop();

            // ---------------- H: loopback only ----------------
            List<string> peers; lock (_seenLock) peers = _peers.ToList();
            var bad = peers.Where(p =>
            {
                var parts = p.Split('|');
                return parts[0] != "127.0.0.1" || parts[1] != "127.0.0.1" || parts[2] != "localhost:" + _port;
            }).ToList();
            Console.WriteLine("H  loopback  " + peers.Count + " requests, " + bad.Count + " not 127.0.0.1 -> 127.0.0.1 with Host localhost:" + _port);
            Check(peers.Count > 0 && bad.Count == 0, "H: requests reached the IDE other than from 127.0.0.1 to 127.0.0.1 with Host localhost:" + _port + ": " + string.Join(", ", bad));

            // ---------------- T: the token ----------------
            srv.Stop();
            Thread.Sleep(200);
            string allOut = string.Join("\n", servers.Select(s => { lock (s.Out) return s.Out.ToString(); }));
            string allErr = string.Join("\n", servers.Select(s => { lock (s.Err) return s.Err.ToString(); }));
            Check(!allOut.Contains(_token) && !allErr.Contains(_token), "T: the bearer token appeared in a tool output or a server log");
            Check(allErr.Contains("[ide-proxy] list_procedures -> IDE " + me.Id + ":" + _port + " ok"), "T: no [ide-proxy] log line for the forwarded call:\n" + Tail(allErr));
        }
        catch (Exception ex)
        {
            Console.WriteLine("COULD NOT RUN: " + ex.Message);
            return 2;
        }
        finally
        {
            foreach (var s in servers) s.Stop();
            foreach (var p in new[] { idleA, idleB }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            try { _http.Stop(); } catch { }
            // The junction goes first, on its own: a recursive delete of the work folder must never walk into src through it.
            try { string alias = Path.Combine(work, "alias"); if (Directory.Exists(alias)) Directory.Delete(alias, false); } catch { }
        }

        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
