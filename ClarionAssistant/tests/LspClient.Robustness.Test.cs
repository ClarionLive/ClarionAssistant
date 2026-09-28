// LspClient.Robustness.Test.cs - 1c685f2e item 8: the bundled LspClient must not lie about a dead server.
//
// Compiles the REAL Services\LspClient.cs (+ LspTrace, EncodingHelper). The language server it starts is
// THIS exe, playing a scripted server: the harness copies itself to <work>\node.exe, which is where
// LspClient.ResolveNodeExe looks for a bundled node (three levels above server.js). Launched as
// `node.exe "<server.js>" --stdio` it answers `initialize` and then misbehaves as FAKE_LSP_MODE says.
// A C# stand-in, so the harness needs no node and scripts the server byte for byte.
//
//   8.3 crash     writes "FATAL: heap out of memory" to stderr and exits 3. Within 2 s the client reports
//                 IsRunning=false AND writes ONE lifecycle line naming code=3 and the stderr tail.
//                 (IsRunning alone passes without the fix, via HasExited, so the LINE decides this case.)
//   8.4 zombie    ends the client's reader (a frame header with no Content-Length, where ReadMessage gives
//                 up exactly as at EOF) and STAYS ALIVE. Within 2 s IsRunning must be false. HasExited is
//                 false here, so only the reader loop clearing _running can make this pass. (Closing the
//                 pipe itself could not be staged: after the child's CloseHandle on its stdout returned
//                 true, and after node's _handle.close()/fs.closeSync(1), the parent still saw no EOF.)
//   8.5 badframe  one well-framed 20-byte non-JSON body, then stays alive: pinned as "the reader logs the
//                 bad frame and keeps running" (IsRunning stays true, no crash / reader-stop line).
//   control       a healthy server stays IsRunning=true, and a deliberate Stop() is not called a crash.
//
// Run: tests\Run-Tests.ps1. Needs nothing but .NET: no node, no Clarion.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClarionAssistant.Services;

static class LspClientRobustnessTest
{
    static int _pass, _fail;
    static readonly List<string> Lines = new List<string>();

    static void Check(string name, bool cond, string detail = null)
    {
        if (cond) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name + (detail != null ? " - " + detail : "")); }
    }

    static bool WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(25); }
        return cond();
    }

    static string[] Snapshot() { lock (Lines) return Lines.ToArray(); }
    static string All() { return string.Join(" || ", Snapshot()); }

    static LspClient StartIn(string mode, string serverJs)
    {
        lock (Lines) Lines.Clear();
        Environment.SetEnvironmentVariable("FAKE_LSP_MODE", mode);   // inherited by the child
        var c = new LspClient();
        bool ok = c.Start(serverJs, new Uri(Path.GetDirectoryName(serverJs)).AbsoluteUri, "robustness");
        Check(mode + ": Start succeeds against the scripted server", ok);
        return c;
    }

    static int Main(string[] args)
    {
        if (args.Contains("--stdio")) return FakeServer.Run(Environment.GetEnvironmentVariable("FAKE_LSP_MODE") ?? "healthy");

        string work = Path.Combine(Path.GetTempPath(), "ca-lsprobust-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string jsDir = Path.Combine(work, "ext", "server", "out");
        Directory.CreateDirectory(jsDir);
        string serverJs = Path.Combine(jsDir, "server.js");
        File.WriteAllText(serverJs, "// placeholder: the scripted server is node.exe itself\r\n");
        File.Copy(Process.GetCurrentProcess().MainModule.FileName, Path.Combine(work, "node.exe"));

        LspClient.LifecycleLog = line => { lock (Lines) Lines.Add(line); };
        try
        {
            Console.WriteLine("\n8.3 crash: exit code + stderr tail logged, IsRunning false");
            {
                var c = StartIn("crash", serverJs);
                Check("IsRunning false within 2 s", WaitFor(() => !c.IsRunning, 2000));
                WaitFor(() => Snapshot().Any(l => l.Contains("node exited")), 2000);
                var exits = Snapshot().Where(l => l.Contains("node exited")).ToArray();
                Check("exactly one exit line", exits.Length == 1, All());
                Check("...with code=3", exits.Length == 1 && exits[0].Contains("code=3"), exits.FirstOrDefault());
                Check("...and the stderr tail", exits.Length == 1 && exits[0].Contains("FATAL: heap out of memory"), exits.FirstOrDefault());
                Check("...flagged UNEXPECTED (not a CA stop)", exits.Length == 1 && exits[0].Contains("UNEXPECTED"), exits.FirstOrDefault());
                c.Stop();
            }

            Console.WriteLine("\n8.4 zombie: stdout closed, process alive -> IsRunning false");
            {
                var c = StartIn("zombie", serverJs);
                Check("IsRunning false within 2 s (process still alive)", WaitFor(() => !c.IsRunning, 2000),
                    "IsRunning=" + c.IsRunning + " lines: " + All() + " stderr: " +
                    string.Join(" | ", ((System.Collections.IEnumerable)c.GetDebugStatus()["stderrTail"]).Cast<object>()));
                Check("...a 'reader stopped' line says the process is still alive",
                    Snapshot().Any(l => l.Contains("reader stopped while running") && l.Contains("still alive")), All());
                c.Stop();
                Thread.Sleep(1500);   // let the Exited line of this deliberate Stop() land here, not in the next case
            }

            Console.WriteLine("\n8.5 badframe: a non-JSON body is logged and the reader keeps running (pinned)");
            {
                var c = StartIn("badframe", serverJs);
                Thread.Sleep(1500);
                Check("IsRunning stays true", c.IsRunning);
                Check("no crash / reader-stop line (the reader did not stop)",
                    !Snapshot().Any(l => l.Contains("UNEXPECTED") || l.Contains("reader stopped")), All());
                c.Stop();
                Thread.Sleep(1500);
            }

            Console.WriteLine("\nK2: a publish for an older version is never served as the current answer");
            {
                var c = StartIn("healthy", serverJs);
                string file = Path.Combine(work, "mod.clw");
                Func<string> msgs = () =>
                {
                    var r = c.WaitForDiagnostics(file, 700, true);
                    return (r.Pending ? "pending" : "complete") + ":" + string.Join(",", r.Entries.Select(e => e.Message));
                };

                c.EnsureBufferSynced(file, "  CODE ! v1 text");                  // v1, published at once
                Check("K2 setup: the v1 publish is the answer while v1 is current", WaitFor(() => msgs() == "complete:for v1", 2000), msgs());
                c.EnsureBufferSynced(file, "  CODE ! v2 text NOPUB");            // v2: the server publishes nothing
                Check("K2 a publish for N, a sync to N+1, a wait timeout -> PENDING with no stale entries", msgs() == "pending:", msgs());
                Check("K2 ...and GetCachedDiagnostics gives null, not v1's entries", c.GetCachedDiagnostics(file) == null);

                c.EnsureBufferSynced(file, "  CODE ! v3 text DELAY300");         // v3 publishes within the wait
                Check("K2 a publish for N+1 arriving within the wait is returned", msgs() == "complete:for v3", msgs());

                c.EnsureBufferSynced(file, "  CODE ! v4 text STALEFIRST DELAY300");   // a v3-stamped publish lands first
                Check("K2 a stale publish landing mid-wait does not end it; the current one is returned",
                    msgs() == "complete:for v4", msgs());

                c.EnsureBufferSynced(file, "  CODE ! v5 text NOVERSION");        // no `version`: stamped with what CA sent
                Check("K2 (no version in the publish) it is stamped with the version CA sent: current -> served",
                    WaitFor(() => msgs() == "complete:for v5", 2000), msgs());
                c.EnsureBufferSynced(file, "  CODE ! v6 text NOPUB");
                Check("K2 (no version) ...and after a newer sync it is stale -> pending", msgs() == "pending:", msgs());

                c.EnsureBufferSynced(file, "  CODE ! v7 text");
                WaitFor(() => c.GetCachedDiagnostics(file) != null, 2000);
                c.ClearDiagnostics(file);
                Check("K2 ClearDiagnostics (RevertShadow) empties the URI's cache", c.GetCachedDiagnostics(file) == null);
                c.Stop();
                Thread.Sleep(1500);
            }

            Console.WriteLine("\ncontrol: a healthy server, then a deliberate Stop()");
            {
                var c = StartIn("healthy", serverJs);
                Thread.Sleep(500);
                Check("IsRunning true", c.IsRunning);
                c.Stop();
                Thread.Sleep(1500);
                Check("Stop() is not reported as a crash or a reader stop",
                    !Snapshot().Any(l => l.Contains("UNEXPECTED") || l.Contains("reader stopped")), All());
                Check("...its exit line says stopped by CA", Snapshot().Any(l => l.Contains("node exited") && l.Contains("stopped by CA")), All());
            }
        }
        finally
        {
            LspClient.LifecycleLog = null;
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine("\n" + _pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>The scripted language server (this exe, launched as node.exe by LspClient).</summary>
    static class FakeServer
    {
        static Stream _out;

        static void Send(byte[] body)
        {
            byte[] head = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
            lock (typeof(FakeServer)) { _out.Write(head, 0, head.Length); _out.Write(body, 0, body.Length); _out.Flush(); }
        }

        static string ReadMessage(Stream s)
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("\r\n\r\n"))
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                header.Append((char)b);
            }
            int len = 0;
            foreach (var l in header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(15).Trim(), out len);
            var buf = new byte[len];
            int read = 0;
            while (read < len) { int n = s.Read(buf, read, len - read); if (n <= 0) return null; read += n; }
            return Encoding.UTF8.GetString(buf);
        }

        /// <summary>
        /// K2: publish diagnostics for a didOpen/didChange as its TEXT directs (so each test case scripts the
        /// server by the buffer it syncs): NOPUB = publish nothing; DELAYnnn = publish after nnn ms; NOVERSION =
        /// omit `version` from the publish; STALEFIRST = first publish one entry stamped with version-1 ("stale"),
        /// then the real one. The real publish has one entry whose message is "for vN".
        /// </summary>
        static void PublishFor(string msg)
        {
            var um = System.Text.RegularExpressions.Regex.Match(msg, "\"uri\"\\s*:\\s*\"([^\"]+)\"");
            var vm = System.Text.RegularExpressions.Regex.Match(msg, "\"version\"\\s*:\\s*(\\d+)");
            if (!um.Success || !vm.Success || msg.Contains("NOPUB")) return;
            string uri = um.Groups[1].Value;
            int version = int.Parse(vm.Groups[1].Value);
            var dm = System.Text.RegularExpressions.Regex.Match(msg, "DELAY(\\d+)");
            int delay = dm.Success ? int.Parse(dm.Groups[1].Value) : 0;
            bool noVersion = msg.Contains("NOVERSION"), staleFirst = msg.Contains("STALEFIRST");
            Func<int, string, string> body = (v, text) =>
                "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":{\"uri\":\"" + uri + "\"" +
                (noVersion ? "" : ",\"version\":" + v) +
                ",\"diagnostics\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"severity\":1,\"message\":\"" + text + "\"}]}}";
            new Thread(() =>
            {
                if (staleFirst) { Thread.Sleep(100); Send(Encoding.UTF8.GetBytes(body(version - 1, "stale"))); }
                if (delay > 0) Thread.Sleep(delay);
                Send(Encoding.UTF8.GetBytes(body(version, "for v" + version)));
            }) { IsBackground = true }.Start();
        }

        static int? IdOf(string json)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"id\"\\s*:\\s*(\\d+)");
            return m.Success ? (int?)int.Parse(m.Groups[1].Value) : null;
        }

        public static int Run(string mode)
        {
            _out = Console.OpenStandardOutput();
            var input = Console.OpenStandardInput();
            bool stdoutClosed = false;
            for (;;)
            {
                string msg = ReadMessage(input);
                if (msg == null) { Thread.Sleep(Timeout.Infinite); }   // stdin closed: stay alive until killed
                int? id = IdOf(msg);
                if (msg.Contains("\"exit\"")) return 0;
                if (msg.Contains("textDocument/didOpen") || msg.Contains("textDocument/didChange"))
                {
                    PublishFor(msg);
                    continue;
                }
                if (msg.Contains("\"initialize\""))
                {
                    Send(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"capabilities\":{}}}"));
                    new Thread(() =>
                    {
                        Thread.Sleep(300);
                        if (mode == "crash")
                        {
                            var err = Console.Error;
                            err.WriteLine("FATAL: heap out of memory");
                            err.Flush();
                            Environment.Exit(3);
                        }
                        else if (mode == "zombie")
                        {
                            // End the reader while this process stays alive: a frame header with no
                            // Content-Length is where ReadMessage gives up (returns null), exactly as it does
                            // at EOF. Closing the pipe itself cannot be staged here: measured, the parent
                            // never saw EOF after CloseHandle(GetStdHandle(STD_OUTPUT_HANDLE)) returned true,
                            // nor after node's _handle.close() / fs.closeSync(1).
                            lock (typeof(FakeServer))
                            {
                                byte[] junk = Encoding.ASCII.GetBytes("X-Garbage: 1\r\n\r\n");
                                _out.Write(junk, 0, junk.Length); _out.Flush();
                                stdoutClosed = true;
                            }
                        }
                        else if (mode == "badframe")
                        {
                            Send(Encoding.ASCII.GetBytes("this is not json!!!!"));   // exactly 20 bytes
                        }
                    }) { IsBackground = true }.Start();
                    continue;
                }
                if (id.HasValue && !stdoutClosed)
                    Send(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":null}"));
            }
        }
    }
}
