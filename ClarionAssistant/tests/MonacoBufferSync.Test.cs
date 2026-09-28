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

    /// <summary>The source of the method that starts with <paramref name="signature"/>, up to the first line
    /// closing at the method's own indentation ("" when absent, which fails the caller's Contains checks).</summary>
    static string MethodBody(string src, string signature)
    {
        int s = src.IndexOf(signature, StringComparison.Ordinal);
        if (s < 0) return "";
        int lineStart = src.LastIndexOf('\n', s) + 1;
        string indent = src.Substring(lineStart, s - lineStart);
        int e = src.IndexOf("\n" + indent + "}", s, StringComparison.Ordinal);
        return e < 0 ? src.Substring(s) : src.Substring(s, e - s);
    }

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
                Check("K2 RevertShadow clears the URI's cached diagnostics after pushing the disk text",
                    System.Text.RegularExpressions.Regex.IsMatch(ctx, @"SharedLspBridge\.EnsureBufferSynced\(path, [^;]+;\s*(//[^\n]*\s*)*SharedLspBridge\.ClearDiagnostics\(path\);"));
                Check("K2 both hosts reply through DiagnosticsReply (null markers -> {markers:null, pending:true})",
                    view.Contains("PostResponse(reqId, MonacoEditorControl.DiagnosticsReply(markers))") &&
                    overlay.Contains("editor.PostResponse(reqId, MonacoEditorControl.DiagnosticsReply(markers))") &&
                    System.Text.RegularExpressions.Regex.IsMatch(ctl, @"markers == null\s*\?\s*new Dictionary<string, object> \{ \{ ""markers"", null \}, \{ ""pending"", true \} \}"));
                // L2 (pre-existing): the LSP must start with a solution open and NO CA chat tab. The solution
                // hooks were set only by AssistantChatControl; the addin's autostart command sets them now.
                string auto = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, "LspAutostartCommand.cs"));
                string svc = System.IO.File.ReadAllText(System.IO.Path.Combine(repo, @"Services\LspService.cs"));
                string run = MethodBody(auto, "public void Run(");
                Check("L2 the autostart command installs LspService.SolutionPathProvider (no chat needed)",
                    System.Text.RegularExpressions.Regex.IsMatch(run, @"LspService\.SolutionPathProvider\s*=\s*\(\)\s*=>\s*EditorService\.GetOpenSolutionPath\(\)"));
                // 286f2e57: the per-solution VERSION override (and its EffectiveClarionVersion.SolutionPathProvider)
                // is gone; addin start deletes the retired override keys once instead.
                Check("L2 ...and deletes CA's retired VERSION overrides once (286f2e57)",
                    run.Contains("EffectiveClarionVersion.DeleteRetiredOverridesOnce();")
                    && !run.Contains("EffectiveClarionVersion.SolutionPathProvider"));
                Check("L2 ...before the first start is attempted",
                    run.IndexOf("LspService.SolutionPathProvider =", StringComparison.Ordinal) >= 0 &&
                    run.IndexOf("LspService.SolutionPathProvider =", StringComparison.Ordinal) < run.IndexOf("LspService.EnsureRunningInBackground()", StringComparison.Ordinal));
                Check("L2 every background start logs `[lsp-autostart] start|skip reason=` (to monaco-spike.log)",
                    run.Contains("LspService.StartLog = MonacoSpikeLog.Write;") &&
                    MethodBody(svc, "private static void StartOnPoolHoldingGate(").Contains("LogStartResult(EnsureRunning())") &&
                    svc.Contains("\"[lsp-autostart] \" + (started ? \"start\" : \"skip\") + \" reason=\""));
                Check("RevertShadow releases the cached wrapped buffer",
                    System.Text.RegularExpressions.Regex.IsMatch(ctx, "public void RevertShadow\\(\\)\\s*\\{\\s*_lastWrap = null;"));

                // 1c685f2e 8.1b: bufferSync (and the page's log line) are handled BEFORE the host check.
                string dispatch = MethodBody(ctl, "private void OnWebMessageReceived(");
                int hostCheck = dispatch.IndexOf("if (h == null) return;", StringComparison.Ordinal);
                int syncAt = dispatch.IndexOf("_bufferCache.AcceptSync(json, \"buffer\"", StringComparison.Ordinal);
                int logAt = dispatch.IndexOf("case \"log\":", StringComparison.Ordinal);
                Check("8.1b bufferSync is cached before the _host==null return", hostCheck > 0 && syncAt > 0 && syncAt < hostCheck,
                    "sync@" + syncAt + " hostCheck@" + hostCheck);
                Check("item 0: the page's log action is handled before the _host==null return", logAt > 0 && logAt < hostCheck,
                    "log@" + logAt + " hostCheck@" + hostCheck);
                int headerAt = dispatch.IndexOf("case \"headerSync\":", StringComparison.Ordinal);
                Check("R11: headerSync is handled before the _host==null return", headerAt > 0 && headerAt < hostCheck,
                    "headerSync@" + headerAt + " hostCheck@" + hostCheck);
                Check("R11: a full bufferSync / fileState that was cached pushes the span map",
                    System.Text.RegularExpressions.Regex.IsMatch(dispatch, @"if \(_bufferCache\.AcceptSync\(json, ""buffer"", getMs, MonacoSpikeLog\.Write\)\)\s*\{[^}]*PushSpanMap\(\);") &&
                    dispatch.Contains("if (_bufferCache.AcceptSync(json, \"text\", getMs, MonacoSpikeLog.Write)) _fileStateSpanMap.Trigger(PushSpanMap);"));
                string push = MethodBody(ctl, "private void PushSpanMap(");
                Check("R11: the span map is built off the UI thread in the newest-wins \"span-map\" lane",
                    push.Contains("_lanes.Submit(\"span-map\"") && push.Contains("_bufferCache.Resolve(v)"), push.Length + " chars");
                string runLocal = MethodBody(ctl, "public void RunLocalAction(");
                Check("R11: a request carrying a slice skips the `v` lookup; one without still goes through it",
                    runLocal.Contains("!Services.LocalLayerHandlers.CarriesSlice(data) && !TryResolveRequestBuffer(data, out buffer)"));
                int capAt = dispatch.IndexOf("WebMessageGuard.CheckOverall(json.Length)", StringComparison.Ordinal);
                int actionAt = dispatch.IndexOf("ExtractJsonValue(json, \"action\")", StringComparison.Ordinal);
                int sizeAt = dispatch.IndexOf("WebMessageGuard.CheckSize(action, json.Length)", StringComparison.Ordinal);
                int firstParse = dispatch.IndexOf("_bufferCache.AcceptSync(", StringComparison.Ordinal);
                Check("F6: the overall cap precedes even the action scan, and the per-action cap precedes any parsing",
                    capAt > 0 && capAt < actionAt && sizeAt > actionAt && sizeAt < firstParse && dispatch.Contains("RejectMessage(action, json, tooBig); return;"),
                    "cap@" + capAt + " action@" + actionAt + " size@" + sizeAt + " parse@" + firstParse);
                Check("F7: fileState debounces the span map; bufferSync builds it at once",
                    dispatch.Contains("_fileStateSpanMap.Trigger(PushSpanMap)") && !dispatch.Contains("\"text\", getMs, MonacoSpikeLog.Write)) PushSpanMap();"));
                Check("8.2 message errors go to the log, not Debug.WriteLine",
                    dispatch.Contains("MonacoSpikeLog.Write(\"[MonacoEditorControl] message error") && !dispatch.Contains("Debug.WriteLine(\"[MonacoEditorControl] Message error"));

                // 1c685f2e 8.8: foldingRanges runs in the newest-wins "folding" lane in BOTH hosts.
                string foldView = MethodBody(view, "private void HandleFoldingRanges(");
                string foldOverlay = MethodBody(overlay, "void IMonacoFoldingHost.OnFoldingRanges(");
                Check("8.8 CA Embeditor folding uses the \"folding\" lane, no Task.Run",
                    foldView.Contains("RunLatestOrNow(\"folding\"") && !foldView.Contains("Task.Run("), foldView.Length + " chars");
                Check("8.8 CA Editor overlay folding uses the \"folding\" lane, no Task.Run",
                    foldOverlay.Contains("editor.RunLatest(\"folding\"") && !foldOverlay.Contains("Task.Run("), foldOverlay.Length + " chars");
                Check("F8: the overlay's folding lane logs a dropped request",
                    System.Text.RegularExpressions.Regex.IsMatch(foldOverlay, @"\}, \(\) => MonacoSpikeLog\.Write\(dropLine\.Add\(""dropped"""));
            }
            else Check("repo dir passed for the source scan", false, "arg: " + (repo ?? "(none)"));
        }

        Console.WriteLine("\nItem 0: the page's log line ({action:'log', line}) - verbatim, one line, capped");
        {
            string line = "[local-rt] action=hover rtMs=137 syncBytes=0 syncMs=0 v=4";
            Check("0.9 a normal line comes through verbatim",
                PageLogLine.FromMessage("{\"action\":\"log\",\"line\":" + Json(line) + "}") == line);
            string forged = PageLogLine.FromMessage("{\"action\":\"log\",\"line\":\"a\\r\\n2026-01-01 00:00:00.000  [buffer-sync] fake\"}");
            Check("0.9 CR/LF become spaces (a page cannot forge a second log line)",
                forged != null && forged.IndexOf('\n') < 0 && forged.IndexOf('\r') < 0 && forged.StartsWith("a  2026"), forged);
            string huge = new string('x', 2 * 1024 * 1024);
            string capped = PageLogLine.FromMessage("{\"action\":\"log\",\"line\":\"" + huge + "\"}");
            Check("0.9 a 2 MB line is capped at " + PageLogLine.MaxChars + " chars and marked",
                capped != null && capped.Length == PageLogLine.MaxChars && capped.EndsWith("...(truncated)"), capped == null ? "null" : capped.Length.ToString());
            Check("0.9 no line field -> nothing to write", PageLogLine.FromMessage("{\"action\":\"log\"}") == null);
            Check("0.9 malformed -> nothing, never throws", PageLogLine.FromMessage("{\"action\":\"log\",\"line\":\"unterminated") == null);
        }

        Console.WriteLine("\nItems 0 + 8: AcceptSync caches and logs every sync, success or failure");
        {
            var c = new MonacoBufferCache();
            var log = new List<string>();
            string text = "  CODE\r\n  x = 1\r\n";
            bool ok = c.AcceptSync("{\"action\":\"bufferSync\",\"v\":5,\"buffer\":" + Json(text) + "}", "buffer", 7, log.Add);
            Check("8.1 a sync is cached (no host involved)", ok && c.Resolve(5) == text);
            Check("item 0: one [buffer-sync] recv line with v, chars, getMs and parseMs",
                log.Count == 1 && log[0].StartsWith("[buffer-sync] recv v=5 key=buffer chars=" + text.Length + " getMs=7 parseMs="), string.Join(" | ", log));

            log.Clear();
            ok = c.AcceptSync("{\"action\":\"bufferSync\",\"v\":3,\"buffer\":\"unterminated", "buffer", 0, log.Add);
            Check("8.2 a malformed sync is refused and the cache keeps v=5", !ok && c.Resolve(5) == text && c.Resolve(3) == null);
            Check("8.2 ...and says so: [buffer-sync] parse failed v=3",
                log.Count == 1 && log[0].StartsWith("[buffer-sync] parse failed v=3 "), string.Join(" | ", log));

            log.Clear();
            long v; string got;
            Check("8.2 no v -> a parse failed line with v=?",
                !MonacoBufferCache.TryParseSync("{\"action\":\"bufferSync\",\"buffer\":\"x\"}", "buffer", out v, out got, log.Add)
                && log.Count == 1 && log[0].StartsWith("[buffer-sync] parse failed v=? "), string.Join(" | ", log));
        }

        Console.WriteLine("\nF6: every page message is size-checked before any parsing");
        {
            var limits = new[] {
                new KeyValuePair<string, int>("bufferSync", 16000000), new KeyValuePair<string, int>("fileState", 16000000),
                new KeyValuePair<string, int>("log", 4096), new KeyValuePair<string, int>("headerSync", 1000000),
                new KeyValuePair<string, int>("localCompletion", 2000000), new KeyValuePair<string, int>("localHover", 2000000),
                new KeyValuePair<string, int>("slotDiagnostics", 16000000), new KeyValuePair<string, int>("saveCursor", 65536),
                new KeyValuePair<string, int>("somethingElse", 16000000) };
            foreach (var l in limits)
                Check("F6 " + l.Key + ": " + l.Value + " chars accepted, " + (l.Value + 1) + " rejected",
                    ClarionAssistant.Services.WebMessageGuard.CheckSize(l.Key, l.Value) == null &&
                    ClarionAssistant.Services.WebMessageGuard.CheckSize(l.Key, l.Value + 1) != null);

            // G1 (pipeline run 2): actions that carry the whole buffer or a selection must pass at module size.
            const int Module = 3200000;
            foreach (var a in new[] { "save", "diffWithDisk", "clipboard", "embedState", "selectionChanged", "caFindUpdate",
                                      "caFindOpenDoc", "snippetCommand", "saveSettings", "saveHistory", "saveFolds", "saveBookmarks",
                                      "completion", "hover", "diagnostics", "openDesigner" })
                Check("G1 " + a + " with a 3.2 MB payload is ACCEPTED", ClarionAssistant.Services.WebMessageGuard.CheckSize(a, Module) == null);
            Check("G1 an unknown action at 1.5M is accepted (the default is the sync cap)",
                ClarionAssistant.Services.WebMessageGuard.CheckSize("someNewAction", 1500000) == null);
            Check("G1 log over 4096 is still rejected", ClarionAssistant.Services.WebMessageGuard.CheckSize("log", 4097) != null);
            Check("G1 the pre-scan cap is the helper's", ClarionAssistant.Services.WebMessageGuard.CheckOverall(16000000) == null &&
                ClarionAssistant.Services.WebMessageGuard.CheckOverall(16000001) != null);

            // The audit, re-checked against the real page: every action posted by monaco-embeditor.html that gets
            // the small scalar cap must carry no buffer or selection text in its message literal.
            string repoDir = Environment.GetCommandLineArgs().Length > 1 ? Environment.GetCommandLineArgs()[1] : null;
            string page = repoDir != null ? System.IO.File.ReadAllText(System.IO.Path.Combine(repoDir, @"Terminal\monaco-embeditor.html")) : "";
            var posted = new SortedSet<string>(StringComparer.Ordinal);
            var bad = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(page, @"action:\s*'([A-Za-z]+)'"))
            {
                string name = m.Groups[1].Value;
                posted.Add(name);
                if (ClarionAssistant.Services.WebMessageGuard.MaxChars(name) != ClarionAssistant.Services.WebMessageGuard.MaxScalarChars) continue;
                int close = page.IndexOf('}', m.Index);
                string literal = close < 0 ? "" : page.Substring(m.Index, close - m.Index);
                if (System.Text.RegularExpressions.Regex.IsMatch(literal, @"getValue|text\s*:|gatherSlots|slots\s*:|settings\s*:|data\s*:"))
                    bad.Add(name + ": " + literal.Replace("\r", " ").Replace("\n", " "));
            }
            Check("G1 the page posts actions (the scan found them)", posted.Count >= 30 && posted.Contains("save") && posted.Contains("clipboard"), string.Join(",", posted));
            Check("G1 no scalar-capped action carries buffer/selection text in the page", bad.Count == 0, string.Join(" || ", bad));
            string ctlSrc = repoDir != null ? System.IO.File.ReadAllText(System.IO.Path.Combine(repoDir, @"Terminal\MonacoEditorControl.cs")) : "";
            string viewSrc = repoDir != null ? System.IO.File.ReadAllText(System.IO.Path.Combine(repoDir, @"Terminal\ModernEmbeditorViewContent.cs")) : "";
            string ovSrc = repoDir != null ? System.IO.File.ReadAllText(System.IO.Path.Combine(repoDir, @"MonacoClarionSourceEditor.cs")) : "";
            Check("G1 both hosts receive through MonacoEditorControl (the one size gate), neither adds its own",
                viewSrc.Contains("new MonacoEditorControl(this") && ovSrc.Contains("new MonacoEditorControl(this") &&
                !viewSrc.Contains("WebMessageGuard") && !ovSrc.Contains("WebMessageGuard") &&
                ctlSrc.Contains("Services.WebMessageGuard.CheckOverall(json.Length)"));

            long now = 0;
            ClarionAssistant.Services.WebMessageGuard.NowMs = () => now;
            ClarionAssistant.Services.WebMessageGuard.ResetRateLimit();
            var lines = new List<string>();
            ClarionAssistant.Services.WebMessageGuard.LogReject(lines.Add, "hover", 5, "r");
            now = 4999;
            ClarionAssistant.Services.WebMessageGuard.LogReject(lines.Add, "hover", 5, "r");
            ClarionAssistant.Services.WebMessageGuard.LogReject(lines.Add, "log", 5, "r");
            now = 5000;
            ClarionAssistant.Services.WebMessageGuard.LogReject(lines.Add, "hover", 5, "r");
            Check("F6 rejects log `[webmsg] rejected action= chars= reason=`, one per action per 5 s",
                lines.Count == 3 && lines[0] == "[webmsg] rejected action=hover chars=5 reason=r" && lines[1].Contains("action=log"),
                string.Join(" | ", lines));
            ClarionAssistant.Services.WebMessageGuard.NowMs = null;
            ClarionAssistant.Services.WebMessageGuard.ResetRateLimit();
        }

        Console.WriteLine("\nF7: a burst of fileStates builds the span map ONCE (Debouncer)");
        {
            int runs = 0;
            var d = new Debouncer(400);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastTrigger = 0, ranAt = -1;
            for (int i = 0; i < 10; i++)
            {
                d.Trigger(() => { Interlocked.Increment(ref runs); ranAt = sw.ElapsedMilliseconds; });
                lastTrigger = sw.ElapsedMilliseconds;
                Thread.Sleep(50);
            }
            Thread.Sleep(700);
            Check("F7 10 triggers 50 ms apart -> ONE build", runs == 1, "runs=" + runs);
            Check("...about 400 ms after the last trigger", ranAt - lastTrigger >= 350, "ran " + (ranAt - lastTrigger) + " ms after the last");
            d.Trigger(() => Interlocked.Increment(ref runs));
            d.Cancel();
            Thread.Sleep(600);
            Check("F7 Cancel drops the pending build (a bufferSync builds it now)", runs == 1, "runs=" + runs);
            d.Dispose();
        }

        Console.WriteLine("\nR11: headerSync {action, hash, text} parsed text-last (no whole-message deserialise)");
        {
            Dictionary<string, object> f; string t;
            Check("text-last shape: hash and the unescaped text",
                MonacoBufferCache.TryParseTextMessage("{\"action\":\"headerSync\",\"hash\":\"abc\",\"text\":\"  MEMBER()\\r\\nX LONG\"}", "text", out f, out t)
                && (string)f["hash"] == "abc" && t == "  MEMBER()\r\nX LONG", t);
            Check("another key order still parses (fallback)",
                MonacoBufferCache.TryParseTextMessage("{\"text\":\"h\",\"hash\":\"x\",\"action\":\"headerSync\"}", "text", out f, out t) && t == "h" && (string)f["hash"] == "x");
            Check("no text -> false, never throws",
                !MonacoBufferCache.TryParseTextMessage("{\"action\":\"headerSync\",\"hash\":\"x\"}", "text", out f, out t)
                && !MonacoBufferCache.TryParseTextMessage("garbage", "text", out f, out t));
        }

        Console.WriteLine("\n4.11 / 8.7: every lane is independent of every other (LaneSet)");
        {
            var lanes = new LaneSet();
            var block = new ManualResetEventSlim(false);
            var blockedStarted = new ManualResetEventSlim(false);
            lanes.Submit("completion", () => { blockedStarted.Set(); block.Wait(10000); }, () => { });
            Check("the completion lane is busy", blockedStarted.Wait(2000));
            foreach (var lane in new[] { "local-completion", "local-hover", "slot-diagnostics", "folding", "span-map", "hover", "diagnostics" })
            {
                var done = new ManualResetEventSlim(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                lanes.Submit(lane, () => done.Set(), () => { });
                bool ran = done.Wait(200);
                Check("'" + lane + "' completes within 200 ms while 'completion' is blocked", ran, sw.ElapsedMilliseconds + " ms");
            }
            block.Set();
        }

        Console.WriteLine("\n4.12: slot-diagnostics is newest-wins (R8 relies on it)");
        {
            var lanes = new LaneSet(a => Task.Factory.StartNew(a, TaskCreationOptions.LongRunning));
            var gate = new ManualResetEventSlim(false);
            var started = new ManualResetEventSlim(false);
            var ran = new List<int>(); var dropped = new List<int>();
            var last = new ManualResetEventSlim(false);
            lanes.Submit("slot-diagnostics", () => { started.Set(); gate.Wait(5000); lock (ran) ran.Add(1); }, () => { lock (dropped) dropped.Add(1); });
            started.Wait(2000);
            for (int i = 2; i <= 5; i++)
            {
                int id = i;
                lanes.Submit("slot-diagnostics", () => { lock (ran) ran.Add(id); if (id == 5) last.Set(); }, () => { lock (dropped) dropped.Add(id); });
            }
            gate.Set();
            Check("the newest runs after the running one", last.Wait(5000));
            lock (ran) Check("5 quick submissions: 1 running + 1 newest ran", string.Join(",", ran) == "1,5", string.Join(",", ran));
            lock (dropped) Check("...and the 3 in between were answered as dropped", string.Join(",", dropped) == "2,3,4", string.Join(",", dropped));
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
