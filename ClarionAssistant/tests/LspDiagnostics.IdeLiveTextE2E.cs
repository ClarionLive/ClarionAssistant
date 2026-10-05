// LspDiagnostics.IdeLiveTextE2E.cs - ticket 44a1b10c, end to end through BOTH processes.
//
// THE GAP THIS CLOSES. lsp_diagnostics is served by the standalone clarion-mcp-server.exe (it is not IdeOnly), a
// separate process with no editors. 03b55cb wired the editor lookup into the ADDIN only, its harness tested the bridge
// in-process, and the live test answered analysed:disk for every open editor. This drives the REAL standalone exe over
// stdio, the way an IDE pane's Claude does, against a stand-in IDE: an HttpListener on a localhost prefix (like the
// pane's McpServer) serving initialize + get_live_text with a bearer token, found through the real endpoint and
// solution records in a temp CA_IDE_RECORD_DIR. The language server is fixtures\lsp-live-text\fake-lsp.js, which logs
// every text it receives (FAKE_LOG).
//
//   1 buffer        IDE has an unsaved buffer      -> analysed ca-editor-buffer; the fake LSP received the BUFFER
//   2 embed         IDE has the embeditor document -> embeditor-document, procedure, lineNumber in editor lines
//   3 not open      IDE answers found:false        -> disk + analysedReason (not an error)
//   4 no IDE        no endpoint records            -> disk + "no IDE"
//   5 other sln     the IDE has another solution   -> disk + "no IDE has this solution open"
//   6 ambiguous     two IDEs (neither ours) match  -> disk + "not guessing"
//   7 slow          IDE takes 5 s                  -> disk + reason, the call back inside ~4 s
//   8 bad token     record token wrong (401)       -> disk + reason; the token never appears anywhere
//   9 pid reuse     record start time is wrong     -> record deleted + logged, disk
//  10 size          a 2.3 MB module's buffer       -> analysed buffer, all of it received; round trip timed
//
// Args: <standalone exe> <fake-lsp.js> <work dir> [<big module>]. Exit 0 pass, 1 fail, 2 could-not-run.
// "--idle" makes this exe a second stand-in IDE PROCESS (same image name) for case 6 and 9.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class IdeLiveTextE2E
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static void Check(bool ok, string message) { _assertions++; if (!ok) Failures.Add(message); }

    // ---- the stand-in IDE pane ----
    static HttpListener _http;
    static int _port;
    static string _token = "e2e-" + Guid.NewGuid().ToString("N");   // must never leak into any output
    static Func<string, Dictionary<string, object>> _answer = p => new Dictionary<string, object> { { "found", false }, { "reason", "no editor has this file open" } };
    static int _delayMs;

    static void StartIde()
    {
        for (_port = 19900; _port < 19990; _port++)
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
            if (ctx.Request.Headers["Authorization"] != "Bearer " + _token) { Reply(ctx, 401, "{\"error\":\"unauthorized\"}", null); return; }
            string body = new StreamReader(ctx.Request.InputStream, new UTF8Encoding(false)).ReadToEnd();
            var req = Js.DeserializeObject(body) as Dictionary<string, object>;
            string method = req["method"] as string;
            if (method == "initialize") { Reply(ctx, 200, "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", "sess-1"); return; }
            if (ctx.Request.Headers["Mcp-Session-Id"] != "sess-1") { Reply(ctx, 400, "{\"error\":\"missing or invalid Mcp-Session-Id\"}", null); return; }
            var prm = req["params"] as Dictionary<string, object>;
            var args = prm["arguments"] as Dictionary<string, object>;
            if (_delayMs > 0) Thread.Sleep(_delayMs);
            string inner = Js.Serialize(_answer((string)args["file_path"]));
            string reply = Js.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" }, { "id", req["id"] },
                { "result", new Dictionary<string, object> { { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", inner } } } } } }
            });
            Reply(ctx, 200, reply, null);
        }
        catch { try { ctx.Response.Abort(); } catch { } }
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

    // ---- the standalone, over stdio ----
    static Process _srv;
    static readonly StringBuilder _stderr = new StringBuilder();
    static int _nextId = 10;

    static Dictionary<string, object> Call(string file, string extraArgs, out long ms, out string raw)
    {
        int id = _nextId++;
        string line = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"lsp_diagnostics\",\"arguments\":{\"file_path\":"
            + Js.Serialize(file) + (extraArgs ?? "") + "}}}";
        var sw = Stopwatch.StartNew();
        _srv.StandardInput.WriteLine(line);
        _srv.StandardInput.Flush();
        while (true)
        {
            string outLine = _srv.StandardOutput.ReadLine();
            if (outLine == null) throw new Exception("the standalone exited");
            Dictionary<string, object> m;
            try { m = Js.DeserializeObject(outLine) as Dictionary<string, object>; } catch { continue; }
            if (m == null || !m.ContainsKey("id") || Convert.ToInt32(m["id"]) != id) continue;
            ms = sw.ElapsedMilliseconds;
            raw = outLine;
            var res = m["result"] as Dictionary<string, object>;
            string text = ((res["content"] as object[])[0] as Dictionary<string, object>)["text"] as string;
            if (text.StartsWith("Error")) return new Dictionary<string, object> { { "error", text } };
            return Js.DeserializeObject(text) as Dictionary<string, object>;
        }
    }

    static string S(Dictionary<string, object> r, string k) { object v; return r != null && r.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null; }
    static string Show(Dictionary<string, object> r) { return r == null ? "(null)" : string.Join(" ", r.Where(kv => kv.Key != "diagnostics").Select(kv => kv.Key + "=" + kv.Value)) + " diags=" + Diags(r); }
    static string Diags(Dictionary<string, object> r)
    {
        object d; if (r == null || !r.TryGetValue("diagnostics", out d) || d == null) return "[]";
        return "[" + string.Join(" | ", ((object[])d).Cast<Dictionary<string, object>>().Select(x => x["lineNumber"] + ":" + x["message"])) + "]";
    }

    static List<string> Received(string fakeLog, string file)
    {
        var got = new List<string>();
        if (!File.Exists(fakeLog)) return got;
        string tail = "/" + Path.GetFileName(file).ToLowerInvariant();
        foreach (var l in File.ReadAllLines(fakeLog))
        {
            var e = Js.DeserializeObject(l) as Dictionary<string, object>;
            if (e != null && ((string)e["uri"]).ToLowerInvariant().EndsWith(tail) && e.ContainsKey("text")) got.Add((string)e["text"]);
        }
        return got;
    }

    static Process Idle()
    {
        var p = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--idle") { UseShellExecute = false, CreateNoWindow = true });
        Thread.Sleep(300);
        return p;
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--idle") { Thread.Sleep(120000); return 0; }
        string exe = args[0], fakeJs = args[1], work = args[2];
        string big = args.Length > 3 ? args[3] : null;
        _recDir = Path.Combine(work, "records");
        string src = Path.Combine(work, "src");
        string ext = Path.Combine(work, "ext", "msarson.clarion-extensions-99.0.0", "out", "server", "src");
        Directory.CreateDirectory(src); Directory.CreateDirectory(ext); Directory.CreateDirectory(_recDir);
        File.Copy(fakeJs, Path.Combine(ext, "server.js"), true);
        string fakeLog = Path.Combine(work, "received.jsonl");

        _sln = Path.Combine(src, "e2e.sln");
        File.WriteAllText(_sln, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n", new UTF8Encoding(false));
        _otherSln = Path.Combine(src, "other.sln");
        File.WriteAllText(_otherSln, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n", new UTF8Encoding(false));
        string disk = "  MEMBER('e2e')\r\nP PROCEDURE\r\n  CODE\r\n  x = 1\r\n";
        string buffer = disk + "  BAD unsaved edit\r\n";
        string[] files = { "buf.clw", "emb.clw", "notopen.clw", "noide.clw", "othersln.clw", "ambig.clw", "slow.clw", "token.clw", "reuse.clw", "big.clw" };
        foreach (var f in files) File.WriteAllText(Path.Combine(src, f), disk, new UTF8Encoding(false));
        Func<string, string> F = n => Path.Combine(src, n);

        var me = Process.GetCurrentProcess();
        StartIde();
        Process idleA = null, idleB = null;

        var psi = new ProcessStartInfo(exe, "--stdio --debug --solution \"" + _sln + "\" --ide-pid " + me.Id)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), CreateNoWindow = true
        };
        psi.EnvironmentVariables["VSCODE_EXTENSIONS"] = Path.Combine(work, "ext");
        psi.EnvironmentVariables["FAKE_LOG"] = fakeLog;
        psi.EnvironmentVariables["CA_IDE_RECORD_DIR"] = _recDir;
        psi.EnvironmentVariables["CA_IDE_IMAGE_NAME"] = me.ProcessName;   // the stand-in IDE is this process
        _srv = Process.Start(psi);
        _srv.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
        _srv.BeginErrorReadLine();
        var outputs = new StringBuilder();
        try
        {
            _srv.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"e2e\",\"version\":\"1\"}}}");
            _srv.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");
            _srv.StandardInput.Flush();
            long ms; string raw;

            // The pane this standalone belongs to: our pid, our solution.
            WriteEndpoint(me, _port, _token);
            WriteSolution(me, _sln);

            // 1 buffer
            _answer = p => p.EndsWith("buf.clw", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, object> { { "found", true }, { "text", buffer }, { "origin", "ca-editor-buffer" }, { "lineOffset", 0 } }
                : new Dictionary<string, object> { { "found", false }, { "reason", "no editor has this file open" } };
            var r1 = Call(F("buf.clw"), ",\"timeout_ms\":8000", out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("1 buffer   " + Show(r1) + " (" + ms + " ms)");
            Check(S(r1, "analysed") == "ca-editor-buffer" && S(r1, "pending") == "False" && Diags(r1).Contains("5:BAD at 4"),
                "1: expected analysed=ca-editor-buffer with the unsaved line's error at line 5; got " + Show(r1));
            var got1 = Received(fakeLog, F("buf.clw"));
            Check(got1.Count > 0 && got1.Last() == buffer && !got1.Contains(disk),
                "1: the standalone's language server should have received the BUFFER (and never the disk text); got " + got1.Count + " texts");

            // 2 embed, with the procedure
            string doc = "P PROCEDURE\r\n  CODE\r\n  BAD in embed\r\n";
            _answer = p => new Dictionary<string, object> { { "found", true }, { "text", "  MEMBER('e2e')\r\n" + doc }, { "origin", "embeditor-document" }, { "lineOffset", 1 }, { "procedure", "CheckComma" } };
            var r2 = Call(F("emb.clw"), ",\"timeout_ms\":8000", out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("2 embed    " + Show(r2));
            Check(S(r2, "analysed") == "embeditor-document" && S(r2, "lineBase") == "embeditor-document" && S(r2, "procedure") == "CheckComma"
                  && Diags(r2) == "[3:BAD at 3]" && (S(r2, "lineNote") ?? "").Contains("CheckComma"),
                "2: expected embeditor-document, procedure CheckComma, the error on editor line 3, and a lineNote naming the procedure; got " + Show(r2));

            // 3 not open in the IDE
            _answer = p => new Dictionary<string, object> { { "found", false }, { "reason", "no editor has this file open" } };
            var r3 = Call(F("notopen.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("3 notopen  " + Show(r3));
            Check(S(r3, "error") == null && S(r3, "analysed") == "disk" && (S(r3, "analysedReason") ?? "").Contains("no editor has this file open"),
                "3: expected disk with the IDE's reason (not an error); got " + Show(r3));

            // 4 no IDE at all
            ClearRecords();
            var r4 = Call(F("noide.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("4 noide    " + Show(r4));
            Check(S(r4, "analysed") == "disk" && (S(r4, "analysedReason") ?? "").Contains("no IDE"),
                "4: expected disk with a 'no IDE' reason; got " + Show(r4));

            // 5 the IDE has ANOTHER solution open
            WriteEndpoint(me, _port, _token);
            WriteSolution(me, _otherSln);
            var r5 = Call(F("othersln.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("5 othersln " + Show(r5));
            Check(S(r5, "analysed") == "disk" && (S(r5, "analysedReason") ?? "").Contains("no IDE has this solution open"),
                "5: expected disk, 'no IDE has this solution open'; got " + Show(r5));

            // 6 two IDEs with this solution, neither the one that launched the server
            ClearRecords();
            idleA = Idle(); idleB = Idle();
            WriteEndpoint(idleA, _port, _token); WriteSolution(idleA, _sln);
            WriteEndpoint(idleB, _port, _token); WriteSolution(idleB, _sln);
            var r6 = Call(F("ambig.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("6 ambig    " + Show(r6));
            Check(S(r6, "analysed") == "disk" && (S(r6, "analysedReason") ?? "").Contains("not guessing"),
                "6: expected disk, 'not guessing'; got " + Show(r6));

            // 7 a slow IDE: the 3 s budget, then disk
            ClearRecords();
            WriteEndpoint(me, _port, _token); WriteSolution(me, _sln);
            _answer = p => new Dictionary<string, object> { { "found", true }, { "text", buffer }, { "origin", "ca-editor-buffer" }, { "lineOffset", 0 } };
            _delayMs = 5000;
            var r7 = Call(F("slow.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            _delayMs = 0;
            Console.WriteLine("7 slow     " + Show(r7) + " (" + ms + " ms)");
            Check(S(r7, "analysed") == "disk" && (S(r7, "analysedReason") ?? "").Length > 0 && ms < 3000 + 3000 + 1500,
                "7: expected disk with a reason, the IDE wait bounded at ~3 s (+ the 3 s diagnostics budget); got " + Show(r7) + " in " + ms + " ms");

            // 8 a wrong token: 401, disk, and the token nowhere
            ClearRecords();
            WriteEndpoint(me, _port, "wrong-" + _token); WriteSolution(me, _sln);
            var r8 = Call(F("token.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("8 token    " + Show(r8));
            Check(S(r8, "analysed") == "disk" && (S(r8, "analysedReason") ?? "").Contains("401"),
                "8: expected disk with an HTTP 401 reason; got " + Show(r8));

            // 9 pid reuse: the record's start time is not the process's
            ClearRecords();
            WriteEndpoint(idleA, _port, _token, DateTime.Now.AddHours(-3)); WriteSolution(idleA, _sln);
            string reuseFile = Path.Combine(_recDir, "ide-endpoint", idleA.Id + "-" + _port + ".json");
            var r9 = Call(F("reuse.clw"), null, out ms, out raw); outputs.AppendLine(raw);
            Console.WriteLine("9 reuse    " + Show(r9));
            Thread.Sleep(200);
            string err9; lock (_stderr) err9 = _stderr.ToString();
            Check(S(r9, "analysed") == "disk" && !File.Exists(reuseFile) && err9.Contains("pid reused"),
                "9: a record whose start time is not the process's must be deleted and logged ('pid reused'), and the call go to disk; got "
                + Show(r9) + " fileExists=" + File.Exists(reuseFile));

            // 10 size: a real 2.3 MB module's text through both hops
            ClearRecords();
            WriteEndpoint(me, _port, _token); WriteSolution(me, _sln);
            string bigText = (big != null && File.Exists(big)) ? File.ReadAllText(big) : string.Concat(Enumerable.Repeat(disk, 60000));
            string bigBuffer = bigText + "\r\n  BAD at the end\r\n";
            _answer = p => new Dictionary<string, object> { { "found", true }, { "text", bigBuffer }, { "origin", "ca-editor-buffer" }, { "lineOffset", 0 } };
            var swBig = Stopwatch.StartNew();
            var r10 = Call(F("big.clw"), ",\"timeout_ms\":30000", out ms, out raw);
            Console.WriteLine("10 size    analysed=" + S(r10, "analysed") + " count=" + S(r10, "count") + " chars=" + bigBuffer.Length + " total " + ms + " ms");
            var got10 = Received(fakeLog, F("big.clw"));
            Check(S(r10, "analysed") == "ca-editor-buffer" && got10.Count > 0 && got10.Last().Length == bigBuffer.Length,
                "10: the " + bigBuffer.Length + "-char buffer should reach the language server whole; got " + S(r10, "analysed")
                + ", received " + (got10.Count > 0 ? got10.Last().Length.ToString() : "nothing"));
            string err10; lock (_stderr) err10 = _stderr.ToString();
            var hop = err10.Split('\n').Where(l => l.Contains("[live-text]") && l.Contains("big.clw") && l.Contains("found=True")).LastOrDefault();
            Console.WriteLine("   IDE hop: " + (hop ?? "(no [live-text] line)").Trim());

            // The token: never in any tool output or log.
            string allErr; lock (_stderr) allErr = _stderr.ToString();
            Check(!outputs.ToString().Contains(_token) && !allErr.Contains(_token),
                "the bearer token appeared in a tool output or the standalone's log");
            Check(allErr.Contains("[live-text]"), "the standalone wrote no [live-text] log lines");
        }
        catch (Exception ex)
        {
            Console.WriteLine("COULD NOT RUN: " + ex.Message);
            string e; lock (_stderr) e = _stderr.ToString();
            Console.WriteLine(string.Join("\n", e.Split('\n').Reverse().Take(15).Reverse()));
            return 2;
        }
        finally
        {
            try { _srv.StandardInput.Close(); if (!_srv.WaitForExit(15000)) _srv.Kill(); } catch { }
            foreach (var p in new[] { idleA, idleB }) try { if (p != null && !p.HasExited) p.Kill(); } catch { }
            try { _http.Stop(); } catch { }
        }

        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
