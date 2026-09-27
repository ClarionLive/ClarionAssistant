// MonacoBufferSync.Test.cs - 16d140e9: the host side of "the buffer crosses to the host ONCE per content
// version". Compiles the REAL Terminal\MonacoBufferSync.cs standalone (it has no IDE or WebView2 references).
//
// What MonacoEditorControl relies on, pinned here:
//   * MonacoBufferCache: Store/Resolve - a matching v returns THE cached string instance, a mismatched v
//     returns null, a newer sync REPLACES the old one (never accumulates), Clear empties it
//   * ResolveRequest: an inline "buffer" (older page) wins, a cached v resolves, an unknown v is Missing
//     (the control then answers null + asks the page to resync), no buffer and no v is None
//   * TryParseSync: the page's own shape (text LAST) read without a full deserialise, every JSON escape
//     round-trips, a message in another key order still parses (fallback), no v / no text is refused
//   * FileState header: dirty/seq/v read from the small prefix
//   * LatestOnlyWorker: one job at a time, only the NEWEST waiting job runs, displaced ones get their
//     dropped callback (the control answers them null), and the worker drains and goes idle
//   * MonacoRequestStamp: queued/late arithmetic for the timing log
//
// Run: tests\Run-Tests.ps1 (or csc this file + Terminal\MonacoBufferSync.cs, /r:System.Web.Extensions.dll)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ClarionAssistant.Terminal;

static class MonacoBufferSyncTest
{
    static int _pass, _fail;
    static readonly List<string> Failures = new List<string>();

    static void Check(string name, bool cond, string detail = null)
    {
        if (cond) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Failures.Add(name + (detail != null ? " - " + detail : "")); Console.WriteLine("  FAIL  " + name + (detail != null ? " - " + detail : "")); }
    }

    static Dictionary<string, object> Parse(string json)
    {
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
    }

    static string Json(object o) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o); }

    static int Main()
    {
        Console.WriteLine("\nMonacoBufferCache: store / resolve / replace");
        {
            var c = new MonacoBufferCache();
            Check("empty cache resolves nothing", c.Resolve(1) == null && c.CurrentBuffer == null && c.CurrentBufferVersion == -1);
            string b1 = "PROGRAM\r\n  CODE\r\n";
            c.Store(1, b1);
            Check("a matching v returns the cached string", c.Resolve(1) == b1);
            Check("...the SAME instance (no copy per request)", ReferenceEquals(c.Resolve(1), b1));
            Check("a mismatched v returns null", c.Resolve(2) == null && c.Resolve(0) == null);
            string b2 = "PROGRAM\r\n  CODE\r\n  x = 1\r\n";
            c.Store(2, b2);
            Check("a newer sync replaces the old one", ReferenceEquals(c.Resolve(2), b2) && c.CurrentBufferVersion == 2);
            Check("...and the old version is gone (one copy, never accumulated)", c.Resolve(1) == null);
            c.Clear();
            Check("Clear empties it", c.Resolve(2) == null && c.CurrentBuffer == null);
        }

        Console.WriteLine("\nResolveRequest: inline / cached / missing / none");
        {
            var c = new MonacoBufferCache();
            c.Store(7, "cached text");
            string buf;
            var how = c.ResolveRequest(Parse("{\"action\":\"hover\",\"reqId\":3,\"v\":7}"), out buf);
            Check("v that matches -> Cached + the cached text", how == MonacoBufferCache.Lookup.Cached && buf == "cached text");
            how = c.ResolveRequest(Parse("{\"action\":\"hover\",\"reqId\":3,\"v\":8}"), out buf);
            Check("v the cache lacks -> Missing, null buffer", how == MonacoBufferCache.Lookup.Missing && buf == null);
            how = c.ResolveRequest(Parse("{\"action\":\"hover\",\"reqId\":3,\"buffer\":\"inline text\"}"), out buf);
            Check("older page: inline buffer -> Inline, used as-is", how == MonacoBufferCache.Lookup.Inline && buf == "inline text");
            how = c.ResolveRequest(Parse("{\"action\":\"hover\",\"reqId\":3,\"buffer\":\"inline\",\"v\":999}"), out buf);
            Check("inline buffer wins over a v", how == MonacoBufferCache.Lookup.Inline && buf == "inline");
            how = c.ResolveRequest(Parse("{\"action\":\"hover\",\"reqId\":3}"), out buf);
            Check("no buffer and no v -> classified None (refused by TryResolveForRequest below)", how == MonacoBufferCache.Lookup.None && buf == null);
            how = c.ResolveRequest(null, out buf);
            Check("null data -> None, never throws", how == MonacoBufferCache.Lookup.None);
        }

        Console.WriteLine("\nTryResolveForRequest: only an inline buffer or a cached v is servable (pipeline HIGH on a49f411)");
        {
            var c = new MonacoBufferCache();
            c.Store(7, "cached text");
            string buf; MonacoBufferCache.Lookup how;
            Check("cached v -> served", c.TryResolveForRequest(Parse("{\"action\":\"hover\",\"reqId\":1,\"v\":7}"), out buf, out how)
                && buf == "cached text" && how == MonacoBufferCache.Lookup.Cached);
            Check("inline buffer (older page) -> served", c.TryResolveForRequest(Parse("{\"action\":\"hover\",\"reqId\":1,\"buffer\":\"x\"}"), out buf, out how)
                && buf == "x" && how == MonacoBufferCache.Lookup.Inline);
            Check("unknown v -> refused (Missing)", !c.TryResolveForRequest(Parse("{\"action\":\"hover\",\"reqId\":1,\"v\":8}"), out buf, out how)
                && buf == null && how == MonacoBufferCache.Lookup.Missing);
            Check("no buffer and no v -> REFUSED, not served with a null buffer", !c.TryResolveForRequest(Parse("{\"action\":\"diagnostics\",\"reqId\":1}"), out buf, out how)
                && buf == null && how == MonacoBufferCache.Lookup.None);
            Check("an unparseable v -> refused", !c.TryResolveForRequest(Parse("{\"action\":\"completion\",\"reqId\":1,\"v\":\"abc\"}"), out buf, out how) && buf == null);
            Check("a null v -> refused", !c.TryResolveForRequest(Parse("{\"action\":\"completion\",\"reqId\":1,\"v\":null}"), out buf, out how) && buf == null);
            Check("buffer:null inline -> refused", !c.TryResolveForRequest(Parse("{\"action\":\"completion\",\"reqId\":1,\"buffer\":null}"), out buf, out how) && buf == null);
            Check("null data -> refused, never throws", !c.TryResolveForRequest(null, out buf, out how) && buf == null);
        }

        Console.WriteLine("\nThe hosts route every buffer-dependent request through that gate (source scan)");
        {
            string repo = Environment.GetCommandLineArgs().Length > 1 ? Environment.GetCommandLineArgs()[1] : null;
            if (repo != null && System.IO.Directory.Exists(repo))
            {
                string ctl = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, @"Terminal\MonacoEditorControl.cs"));
                string view = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, @"Terminal\ModernEmbeditorViewContent.cs"));
                Check("the control's accessor uses TryResolveForRequest (None is refused)", ctl.Contains("_bufferCache.TryResolveForRequest("));
                Check("the embeditor's diagnostics no longer falls back to load-time _sourceText", !view.Contains("buffer ?? _sourceText"));
                string overlay = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, @"MonacoClarionSourceEditor.cs"));
                string ctx = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, @"Services\EmbedLspContext.cs"));
                Check("CA Embeditor/tab diagnostics run in the newest-wins lane", view.Contains("RunLatestOrNow(\"diagnostics\""));
                Check("CA Editor overlay diagnostics run in the newest-wins lane", overlay.Contains("editor.RunLatest(\"diagnostics\""));
                Check("a (re)loaded page's 'ready' clears the buffer cache",
                    System.Text.RegularExpressions.Regex.IsMatch(ctl, "case \"ready\":\\s*(//[^\\n]*\\s*)*_bufferCache\\.Clear\\(\\);"));
                Check("RevertShadow releases the cached wrapped buffer",
                    System.Text.RegularExpressions.Regex.IsMatch(ctx, "public void RevertShadow\\(\\)\\s*\\{\\s*_lastWrap = null;"));
            }
            else Check("repo dir passed for the source scan", false, "arg: " + (repo ?? "(none)"));
        }

        Console.WriteLine("\nTryParseSync: the page's shape, escapes, fallback");
        {
            string text = "  IF A = 'x' THEN DO R.\r\n\t\"quoted\" \\back\\ / slash \u0001 ctl é 中 😀 end";
            // Exactly what JSON.stringify({action:'bufferSync', v:12, buffer:text}) produces (JavaScriptSerializer
            // escapes a few more characters as \uXXXX, which only exercises the unescaper harder).
            string json = "{\"action\":\"bufferSync\",\"v\":12,\"buffer\":" + Json(text) + "}";
            long v; string got;
            Check("page shape parses", MonacoBufferCache.TryParseSync(json, "buffer", out v, out got));
            Check("...v read", v == 12);
            Check("...every escape round-trips", got == text, got == null ? "null" : got.Length + " vs " + text.Length);

            string js = "{\"action\":\"bufferSync\",\"v\":3,\"buffer\":\"a\\r\\nb\\\"c\\\\d\\/e\\u0041\\tf\"}";
            Check("JSON.stringify-style escapes (incl. \\/ and \\u0041)", MonacoBufferCache.TryParseSync(js, "buffer", out v, out got) && got == "a\r\nb\"c\\d/eA\tf", got);

            string plain = "{\"action\":\"bufferSync\",\"v\":4,\"buffer\":\"no escapes at all\"}";
            Check("no escapes -> substring fast path", MonacoBufferCache.TryParseSync(plain, "buffer", out v, out got) && got == "no escapes at all" && v == 4);

            string reordered = "{\"buffer\":\"x\\r\\ny\",\"action\":\"bufferSync\",\"v\":5}";
            Check("another key order still parses (fallback)", MonacoBufferCache.TryParseSync(reordered, "buffer", out v, out got) && got == "x\r\ny" && v == 5);

            Check("no v -> refused", !MonacoBufferCache.TryParseSync("{\"action\":\"bufferSync\",\"buffer\":\"x\"}", "buffer", out v, out got));
            Check("no text -> refused", !MonacoBufferCache.TryParseSync("{\"action\":\"bufferSync\",\"v\":1}", "buffer", out v, out got));
            Check("malformed -> refused, never throws", !MonacoBufferCache.TryParseSync("{\"action\":\"bufferSync\",\"v\":1,\"buffer\":\"unterminated", "buffer", out v, out got));
            Check("empty -> refused", !MonacoBufferCache.TryParseSync("", "buffer", out v, out got));

            // A realistic big buffer: 3.2 MB of Clarion-ish text.
            var sb = new System.Text.StringBuilder();
            while (sb.Length < 3198532) sb.Append("    IF LOC:Count > 0 THEN DO SomeRoutine. ! \"q\" \\ x\r\n");
            string big = sb.ToString(0, 3198532);
            string bigJson = "{\"action\":\"bufferSync\",\"v\":99,\"buffer\":" + Json(big) + "}";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = MonacoBufferCache.TryParseSync(bigJson, "buffer", out v, out got);
            Check("3.2 MB buffer parses exactly (" + sw.ElapsedMilliseconds + " ms)", ok && v == 99 && got == big);
        }

        Console.WriteLine("\nfileState: header fields + text as the sync");
        {
            string text = "FILE\r\nTEXT";
            string json = "{\"action\":\"fileState\",\"dirty\":true,\"seq\":41,\"v\":6,\"text\":" + Json(text) + "}";
            int start;
            var head = MonacoBufferCache.ParseHeader(json, "text", out start);
            Check("header parsed without the text", head != null && !head.ContainsKey("text"));
            Check("...dirty/seq/v read", head != null && Convert.ToBoolean(head["dirty"]) && Convert.ToInt64(head["seq"]) == 41 && Convert.ToInt64(head["v"]) == 6);
            long v; string got;
            Check("fileState text parses as the sync for its v", MonacoBufferCache.TryParseSync(json, "text", out v, out got) && v == 6 && got == text);
            var c = new MonacoBufferCache();
            c.Store(v, got);
            string buf;
            Check("a request naming that v resolves to the SAME string (one copy, not two)",
                c.ResolveRequest(Parse("{\"action\":\"completion\",\"v\":6}"), out buf) == MonacoBufferCache.Lookup.Cached && ReferenceEquals(buf, got));
            Check("an older fileState (no v) yields no header v", MonacoBufferCache.ParseHeader("{\"action\":\"fileState\",\"dirty\":false,\"seq\":1,\"text\":\"t\"}", "text", out start) != null);
        }

        Console.WriteLine("\nLatestOnlyWorker: one at a time, newest waiting job wins");
        {
            // A controllable starter: the drain loop runs on a dedicated thread we can observe.
            var gate = new ManualResetEventSlim(false);
            var ran = new List<int>();
            var dropped = new List<int>();
            var done = new ManualResetEventSlim(false);
            var w = new LatestOnlyWorker(a => Task.Factory.StartNew(a, TaskCreationOptions.LongRunning));
            w.Submit(() => { gate.Wait(5000); lock (ran) ran.Add(1); }, () => { lock (dropped) dropped.Add(1); });
            Thread.Sleep(50);   // job 1 is running and blocked on the gate
            for (int i = 2; i <= 5; i++)
            {
                int id = i;
                w.Submit(() => { lock (ran) ran.Add(id); if (id == 5) done.Set(); }, () => { lock (dropped) dropped.Add(id); });
            }
            lock (dropped) Check("jobs 2..4 displaced while waiting get their dropped callback", string.Join(",", dropped) == "2,3,4", string.Join(",", dropped));
            lock (ran) Check("...and nothing else has run yet", ran.Count == 0);
            gate.Set();
            Check("the newest waiting job runs after the current one", done.Wait(5000));
            lock (ran) Check("...run order is 1 then 5 only", string.Join(",", ran) == "1,5", string.Join(",", ran));

            // Idle again: the next submit starts at once.
            var again = new ManualResetEventSlim(false);
            w.Submit(() => again.Set(), () => { });
            Check("the worker goes idle and a later submit runs", again.Wait(5000));

            // A throwing job does not wedge the lane.
            var after = new ManualResetEventSlim(false);
            w.Submit(() => { throw new InvalidOperationException("boom"); }, () => { });
            Thread.Sleep(50);
            w.Submit(() => after.Set(), () => { });
            Check("a job that throws does not wedge the lane", after.Wait(5000));
        }

        Console.WriteLine("\nMonacoRequestStamp: queued / late");
        {
            long now = MonacoRequestStamp.NowMs();
            var s = MonacoRequestStamp.From(Parse("{\"sentAt\":" + (now - 5000) + ",\"timeoutMs\":4000}"));
            Check("queued time measured from sentAt", s.QueuedMs >= 5000 && s.QueuedMs < 6000, s.QueuedMs.ToString());
            Check("older than its timeout -> late", s.IsLate(now));
            Check("describe says the page gave up", s.Describe(now).Contains("late=YES"));
            var fresh = MonacoRequestStamp.From(Parse("{\"sentAt\":" + now + ",\"timeoutMs\":4000}"));
            Check("inside its timeout -> not late", !fresh.IsLate(now) && fresh.Describe(now).Contains("late=no"));
            var none = MonacoRequestStamp.From(Parse("{\"reqId\":1}"));
            Check("unstamped (older page) -> never late", !none.IsLate(now) && none.QueuedMs == -1);
        }

        Console.WriteLine("\n" + _pass + " passed, " + _fail + " failed");
        if (_fail > 0) { Console.WriteLine("\nFailures:\n  " + string.Join("\n  ", Failures)); return 1; }
        return 0;
    }
}
