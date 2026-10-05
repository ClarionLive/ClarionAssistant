// LspDiagnostics.LiveText.cs - ticket 44a1b10c: lsp_diagnostics checks the open editor's text, never clobbering it with disk.
//
// THE DEFECT. lsp_diagnostics always sent the DISK text under the URI the CA Editor and CA Embeditor sync their live
// buffers to. It checked the saved file, not the edit just made, and replaced the server's copy of the editor's
// text with the disk text until the editor's next request.
//
// Driven by LspDiagnostics.LiveTextTest.ps1, compiled against the real clarion-mcp-server.exe (SharedLspBridge,
// LspClient). The language server is fixtures\lsp-live-text\fake-lsp.js, which logs every text it receives to
// FAKE_LOG, skips identical-content changes like the real server (#359), and reports one error per line with "BAD".
// SharedLspBridge.LiveTextProvider is set here to stand in for the addin's editor lookup.
//
//   A no provider                -> disk text analysed and received (today's behaviour, unchanged)
//   B CA Editor buffer           -> the BUFFER reaches the server, the disk text does not; the server still holds it after
//   C the same buffer again      -> nothing re-sent (no #359 skip), and the call completes instead of hanging
//   D embeditor document         -> lines in the editor's numbering (header offset removed), header-line entry dropped,
//                                   inEmbed from the slot ranges
//   E source "disk"              -> the disk, even with a buffer open
//   F source "buffer", none open -> refused
//   G the provider times out     -> disk, with the reason
//
// Args: <fake-lsp.js> <work dir>. Exit: 0 pass, 1 fail, 2 could-not-run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

static class LspDiagnosticsLiveText
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();
    static string _log;

    static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) Failures.Add(message);
    }

    static List<Dictionary<string, object>> Events(string file)
    {
        var all = new List<Dictionary<string, object>>();
        if (!File.Exists(_log)) return all;
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string uriTail = Path.GetFileName(file).ToLowerInvariant();
        foreach (var line in File.ReadAllLines(_log))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var d = js.Deserialize<Dictionary<string, object>>(line);
            if (((string)d["uri"]).ToLowerInvariant().EndsWith("/" + uriTail)) all.Add(d);
        }
        return all;
    }

    static string Texts(List<Dictionary<string, object>> evs)
    {
        return string.Join(" ; ", evs.Select(e => e["event"] + (e.ContainsKey("text") ? "[" + ((string)e["text"]).Replace("\r\n", "|") + "]" : "")));
    }

    static string Show(SharedLspBridge.ToolDiagnostics t)
    {
        if (t == null || t.Result == null) return "(null)";
        return "analysed=" + t.Analysed + " lineBase=" + t.LineBase + " pending=" + t.Result.Pending + " entries=["
            + string.Join(" | ", t.Result.Entries.Select((e, i) => (e.Line + 1) + ":" + e.Message
                + (t.InEmbed != null ? " inEmbed=" + t.InEmbed[i] : ""))) + "]"
            + (t.FallbackReason != null ? " reason=" + t.FallbackReason : "") + (t.Error != null ? " error=" + t.Error : "");
    }

    static int Main(string[] args)
    {
        string fakeJs = args[0], dir = args[1];
        _log = Environment.GetEnvironmentVariable("FAKE_LOG");
        string disk = "  MEMBER('app')\r\nP PROCEDURE\r\n  CODE\r\n  x = 1\r\n";
        string buffer = "  MEMBER('app')\r\nP PROCEDURE\r\n  CODE\r\n  x = 1\r\n  BAD unsaved edit\r\n";
        string mod = Path.Combine(dir, "mod.clw"), emb = Path.Combine(dir, "embmod.clw"), other = Path.Combine(dir, "other.clw");
        File.WriteAllText(mod, disk);
        File.WriteAllText(emb, disk);
        File.WriteAllText(other, disk);

        var client = new LspClient();
        if (!client.Start(fakeJs, new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri, "live-text"))
        {
            Console.WriteLine("COULD NOT RUN: the scripted language server did not start (" + client.LastSpawnError + ")");
            return 2;
        }
        try
        {
            // ---- A: no provider -> disk ----
            SharedLspBridge.LiveTextProvider = null;
            var a = SharedLspBridge.GetDiagnosticsForTool(mod, 5000, "auto");
            Console.WriteLine("A " + Show(a));
            Check(a.Analysed == "disk" && a.LineBase == "file" && !a.Result.Pending && a.Result.Entries.Count == 0,
                "A (no provider): expected analysed=disk, complete, 0 entries; got " + Show(a));
            Check(Events(mod).Any(e => (string)e["event"] == "open" && (string)e["text"] == disk),
                "A: the server should have been given the DISK text; it got " + Texts(Events(mod)));

            // ---- B: an open CA Editor buffer -> the buffer, never the disk ----
            SharedLspBridge.LiveTextProvider = p => string.Equals(p, mod, StringComparison.OrdinalIgnoreCase)
                ? new SharedLspBridge.LiveText { Text = buffer, Origin = "ca-editor-buffer" } : null;
            int before = Events(mod).Count;
            var b = SharedLspBridge.GetDiagnosticsForTool(mod, 5000, "auto");
            Console.WriteLine("B " + Show(b));
            var bEvents = Events(mod).Skip(before).ToList();
            Check(b.Analysed == "ca-editor-buffer" && b.LineBase == "file" && !b.Result.Pending
                  && b.Result.Entries.Count == 1 && b.Result.Entries[0].Line == 4 && b.Result.Entries[0].Message == "BAD at 4",
                "B (CA Editor buffer): expected analysed=ca-editor-buffer, complete, the unsaved line's 'BAD at 4'; got " + Show(b));
            Check(bEvents.Count == 1 && (string)bEvents[0]["event"] == "change" && (string)bEvents[0]["text"] == buffer,
                "B: exactly one change carrying the BUFFER should reach the server (and no disk text); got " + Texts(bEvents));

            // ---- C: the same buffer again -> nothing re-sent, and the call completes ----
            before = Events(mod).Count;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var c = SharedLspBridge.GetDiagnosticsForTool(mod, 4000, "auto");
            long cMs = sw.ElapsedMilliseconds;
            Console.WriteLine("C " + Show(c) + " in " + cMs + " ms");
            var cEvents = Events(mod).Skip(before).ToList();
            Check(cEvents.Count == 0,
                "C: an unchanged buffer must not be re-sent (the server would skip it, #359); the server got " + Texts(cEvents));
            Check(!c.Result.Pending && c.Result.Entries.Count == 1 && cMs < 3000,
                "C: the second call on an unchanged buffer should complete at once from the recorded answer; got " + Show(c) + " in " + cMs + " ms");
            var lastText = Events(mod).Where(e => e.ContainsKey("text")).Select(e => (string)e["text"]).LastOrDefault();
            Check(lastText == buffer, "B/C: after the calls the server must still hold the BUFFER (no clobber); it holds "
                + (lastText ?? "(nothing)").Replace("\r\n", "|"));

            // ---- D: the embeditor document, wrapped with one header line ----
            string doc = "P PROCEDURE\r\n  CODE\r\n  BAD in embed\r\n  x = 1\r\n";
            string wrapped = "  MEMBER('app') ! BAD header\r\n" + doc;   // the header line itself carries a BAD (must be dropped)
            SharedLspBridge.LiveTextProvider = p => string.Equals(p, emb, StringComparison.OrdinalIgnoreCase)
                ? new SharedLspBridge.LiveText { Text = wrapped, Origin = "embeditor-document", LineOffset = 1,
                                                 EmbedRanges = new List<int[]> { new[] { 3, 3 } } } : null;
            var d = SharedLspBridge.GetDiagnosticsForTool(emb, 5000, "auto");
            Console.WriteLine("D " + Show(d));
            Check(d.Analysed == "embeditor-document" && d.LineBase == "embeditor-document" && !d.Result.Pending
                  && d.Result.Entries.Count == 1 && d.Result.Entries[0].Line + 1 == 3 && d.Result.Entries[0].Message == "BAD at 3",
                "D (embeditor): expected one entry on editor line 3 (wrapped line 4, 'BAD at 3'), the header entry dropped; got " + Show(d));
            Check(d.InEmbed != null && d.InEmbed.Count == 1 && d.InEmbed[0] == true,
                "D: the entry on editor line 3 lies in the slot [3,3] and should be inEmbed=true; got " + Show(d));
            Check(Events(emb).Any(e => e.ContainsKey("text") && (string)e["text"] == wrapped) && !Events(emb).Any(e => e.ContainsKey("text") && (string)e["text"] == disk),
                "D: the server should get the wrapped embeditor document and never the module's disk text; got " + Texts(Events(emb)));

            // ---- E: source "disk" -> the disk, even with a buffer open ----
            SharedLspBridge.LiveTextProvider = p => new SharedLspBridge.LiveText { Text = buffer, Origin = "ca-editor-buffer" };
            var e2 = SharedLspBridge.GetDiagnosticsForTool(other, 5000, "disk");
            Console.WriteLine("E " + Show(e2));
            Check(e2.Analysed == "disk" && !e2.Result.Pending && e2.Result.Entries.Count == 0
                  && Events(other).All(ev => !ev.ContainsKey("text") || (string)ev["text"] == disk),
                "E (source disk): expected the disk text only; got " + Show(e2) + " / " + Texts(Events(other)));

            // ---- F: source "buffer" with nothing open -> refused ----
            SharedLspBridge.LiveTextProvider = p => null;
            var f = SharedLspBridge.GetDiagnosticsForTool(other, 2000, "buffer");
            Console.WriteLine("F " + Show(f));
            Check(f.Error != null, "F (source buffer, nothing open): expected an error; got " + Show(f));

            // ---- G: the editor lookup timed out -> disk, saying why ----
            SharedLspBridge.LiveTextProvider = p => new SharedLspBridge.LiveText { Text = null, Reason = "the IDE did not answer within 2 s" };
            var g = SharedLspBridge.GetDiagnosticsForTool(other, 5000, "auto");
            Console.WriteLine("G " + Show(g));
            Check(g.Analysed == "disk" && g.FallbackReason != null && g.FallbackReason.Contains("2 s"),
                "G (lookup timed out): expected analysed=disk with the reason; got " + Show(g));
        }
        finally { try { client.Stop(); } catch { } }

        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
