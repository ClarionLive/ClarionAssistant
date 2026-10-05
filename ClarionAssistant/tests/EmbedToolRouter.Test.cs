using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ClarionAssistant.Services;

// 73bd1f03 fix (2): EmbedToolRouter + EmbedOverlayOps, the embed tools against the CA Embeditor's Monaco buffer.
// Real router and ops; a fake UI thread (a dispatcher with its own thread) and a fake CA Embeditor page.
// Pinned:
//   * no CA Embeditor (no resolver, or it answers null) -> today's native call, ON the UI thread
//   * the target is resolved ON the UI thread; the page is asked OFF it (its reply arrives on the UI thread)
//   * FAIL CLOSED: page not ready / not answering / refusing -> an error, and the native call never runs
//   * reads come from the page's buffer, in its line space, prefixed with the lineBase note
//   * a write: one applyEdits over the slot's whole lines, guarded by the version it was planned on; indented to
//     the NATIVE embed column, looked up (on the UI thread) by the slot's NATIVE start line, which differs from the
//     CA Embeditor's once the developer has edited above it; retried once on "stale"
//   * a line number that is not a CA Embeditor slot start (e.g. one read from the native editor) writes nothing
//
// Run:  tests\Run-Tests.ps1
static class EmbedToolRouterTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    sealed class FakeUi : IUiDispatcher
    {
        readonly BlockingCollection<Action> _q = new BlockingCollection<Action>();
        public readonly Thread Thread;
        public volatile bool Blocked;
        public FakeUi()
        {
            Thread = new Thread(() => { foreach (var a in _q.GetConsumingEnumerable()) { while (Blocked) Thread.Sleep(20); a(); } }) { IsBackground = true };
            Thread.Start();
        }
        public bool HasUiThread { get { return true; } }
        public void BeginInvokeOnUi(Action action) { _q.Add(action); }
        public bool OnUi { get { return System.Threading.Thread.CurrentThread == Thread; } }
    }

    sealed class FakePage : IEmbedOverlayChannel
    {
        public FakeUi Ui;
        public bool Ready = true;
        public string Text;
        public List<int[]> Ranges;
        public List<int[]> Native;
        public long Version = 1;
        public int StaleTimes;
        public string Refuse;
        public bool Hang;
        public readonly List<string> Calls = new List<string>();
        public bool AnyRequestOnUi;
        public Dictionary<string, object> LastApply;
        public Dictionary<string, object> SaveReply;

        public string ProcedureName { get { return "UpdateCust"; } }
        public bool PageReady { get { return Ready; } }
        public IList<int[]> NativeRanges { get { return Native; } }

        public Dictionary<string, object> Request(string action, Dictionary<string, object> args, int timeoutMs)
        {
            Calls.Add(action);
            if (Ui.OnUi) AnyRequestOnUi = true;
            if (Hang) throw new TimeoutException("the CA Embeditor did not answer within " + (timeoutMs / 1000) + " s");
            if (action == "getSlots")
            {
                var rs = new List<object>();
                foreach (var r in Ranges) rs.Add(new object[] { r[0], r[1] });
                return new Dictionary<string, object> { { "text", Text }, { "versionId", Version }, { "ranges", rs.ToArray() } };
            }
            if (action == "save")
            {
                if (SaveReply == null) throw new TimeoutException("the CA Embeditor's save did not report back within 180 s; check the IDE before retrying");
                return SaveReply;
            }
            if (action == "applyEdits")
            {
                if (StaleTimes > 0) { StaleTimes--; Version++; throw new HostRequestBroker.RefusedException("stale"); }
                if (Refuse != null) throw new HostRequestBroker.RefusedException(Refuse);
                LastApply = args;
                Version++;
                return new Dictionary<string, object>();
            }
            throw new HostRequestBroker.RefusedException("unknownAction:" + action);
        }
    }

    static Dictionary<string, object> Try(Func<Dictionary<string, object>> f)
    {
        try { return f(); } catch (Exception ex) { Console.WriteLine("         (threw " + ex.GetType().Name + ": " + ex.Message + ")"); return null; }
    }

    static int Count(List<string> l, string s) { int n = 0; foreach (var x in l) if (x == s) n++; return n; }

    static int Main()
    {
        var ui = new FakeUi();
        EditorToolRouter.UiThreadId = ui.Thread.ManagedThreadId;
        var router = new EmbedToolRouter(() => ui);

        // The developer added two lines above: the CA Embeditor's slots sit 2 lines lower than the native ones.
        string text = string.Join("\r\n", new[] { "P PROCEDURE", "  ! dev line 1", "  ! dev line 2", "  ! gen", "", "  ! gen2",
                                                  "  CODE", "", "    x = 1", "    y = 2", "  RETURN" });
        Func<FakePage> page = () => new FakePage
        {
            Ui = ui, Text = text,
            Ranges = new List<int[]> { new[] { 5, 5 }, new[] { 9, 10 } },
            Native = new List<int[]> { new[] { 3, 3 }, new[] { 7, 8 } }
        };

        bool nativeRan = false, nativeOnUi = false;
        Func<object> native = () => { nativeRan = true; nativeOnUi = ui.OnUi; return "NATIVE"; };

        // --- no CA Embeditor -> native, on the UI thread ---
        EmbedToolRouter.LiveEmbedResolver = null;
        var r = router.Run("get_embeditor_source", native, ov => "OVERLAY");
        Ok("no resolver (standalone) -> native", (r as string) == "NATIVE" && nativeRan, r as string);
        Ok("native runs ON the UI thread", nativeOnUi, null);

        bool resolverOnUi = false;
        nativeRan = false;
        EmbedToolRouter.LiveEmbedResolver = () => { resolverOnUi = ui.OnUi; return null; };
        r = router.Run("get_embeditor_source", native, ov => "OVERLAY");
        Ok("resolver answers null (no CA Embeditor) -> native", (r as string) == "NATIVE" && nativeRan, r as string);
        Ok("the resolver runs ON the UI thread", resolverOnUi, null);

        // --- page not ready: fail closed ---
        var p = page(); p.Ready = false;
        EmbedToolRouter.LiveEmbedResolver = () => p;
        nativeRan = false;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(5, "x"));
        Ok("page not ready -> Error, native never runs, page not asked",
            (r as string ?? "").StartsWith("Error: the CA Embeditor for 'UpdateCust' is still loading") && !nativeRan && p.Calls.Count == 0, r as string);

        // --- reads come from the page, with the lineBase note ---
        p = page();
        EmbedToolRouter.LiveEmbedResolver = () => p;
        nativeRan = false;
        r = router.Run("get_embeditor_source", native, ov => ov.GetEmbeditorSource());
        string s = r as string ?? "";
        Ok("get_embeditor_source -> the page's buffer, CA Embeditor line numbers",
            s.Contains("«E:5/»") && s.Contains("«E:9»") && s.Contains("  ! dev line 1") && !nativeRan, s);
        Ok("...prefixed with the lineBase note", s.StartsWith(EmbedSlotText.LineBaseNote + "\n\n"), null);
        Ok("the page is asked OFF the UI thread", p.Calls.Count == 1 && !p.AnyRequestOnUi, string.Join(",", p.Calls));

        r = router.Run("search_embeditor_source", native, ov => ov.SearchEmbeditorSource("x = 1", 0));
        Ok("search_embeditor_source -> page buffer, noted", (r as string ?? "").StartsWith("lineBase:") && (r as string).Contains("    x = 1"), r as string);
        r = router.Run("get_embed_content", native, ov => ov.GetEmbedContent(9));
        Ok("get_embed_content(9) -> the slot's lines, noted", (r as string ?? "").EndsWith("    x = 1\r\n    y = 2"), r as string);
        r = router.Run("get_embed_content", native, ov => ov.GetEmbedContent(3));
        Ok("get_embed_content at a NATIVE line (3) -> no embed point there, not noted",
            (r as string ?? "").StartsWith("Error: No embed point found at line 3"), r as string);

        // --- write: indent by the NATIVE column of the slot's NATIVE start line ---
        var colAsked = new List<int>(); bool colOnUi = false;
        EmbedToolRouter.NativeEmbedColumn = line => { colAsked.Add(line); colOnUi = ui.OnUi; return 5; };
        p = page();
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(5, "IF a\nEND\n"));
        s = r as string ?? "";
        Ok("write ok, noted, the native tool's report", s.StartsWith("lineBase:") && s.Contains("Wrote to embed at line 5.")
            && s.Contains("Line count changed by +2") && s.Contains("CA Embeditor (unsaved)"), s);
        Ok("the write result names the procedure and slot (fc420c30 style)", s.EndsWith(" — UpdateCust «E:5» (CA Embeditor)"), s);
        Ok("Named: errors pass through unnamed", EmbedToolRouter.Named("Error: x", "P", 3, "native embeditor") == "Error: x", null);
        Ok("Named: native write, procedure unknown, still names the slot",
            EmbedToolRouter.Named("Wrote to embed at line 3.", null, 3, "native embeditor")
                == "Wrote to embed at line 3. — (procedure unknown) «E:3» (native embeditor)", null);
        Ok("the column is looked up by the slot's NATIVE start line (3, not 5), on the UI thread",
            colAsked.Count == 1 && colAsked[0] == 3 && colOnUi, string.Join(",", colAsked));
        var edits = p.LastApply != null ? p.LastApply["edits"] as List<Dictionary<string, object>> : null;
        var e0 = edits != null && edits.Count == 1 ? edits[0] : null;
        Ok("ONE applyEdits, guarded by the version it was planned on", e0 != null && Convert.ToInt64(p.LastApply["expectedVersionId"]) == 1, null);
        Ok("over the slot's whole lines (5:1 .. 5:1 for the empty slot)",
            e0 != null && (int)e0["startLine"] == 5 && (int)e0["startCol"] == 1 && (int)e0["endLine"] == 5 && (int)e0["endCol"] == 1,
            e0 == null ? "none" : e0["startLine"] + ":" + e0["startCol"] + ".." + e0["endLine"] + ":" + e0["endCol"]);
        Ok("indented to column 5", e0 != null && (string)e0["text"] == "    IF a\n    END\n", e0 == null ? null : (string)e0["text"]);
        Ok("never on the UI thread, and native never ran", !p.AnyRequestOnUi && !nativeRan, null);

        p = page();
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(3, "x = 1"));
        Ok("write at a NATIVE line number (3) -> error, nothing written",
            (r as string ?? "").StartsWith("Error: No embed point found at line 3") && Count(p.Calls, "applyEdits") == 0, r as string);

        // --- stale: retried once ---
        p = page(); p.StaleTimes = 1;
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(9, "z = 1"));
        Ok("stale once -> replanned from fresh slots and applied",
            (r as string ?? "").Contains("Wrote to embed at line 9.") && Count(p.Calls, "getSlots") == 2 && p.LastApply != null
            && Convert.ToInt64(p.LastApply["expectedVersionId"]) == 2, r as string);
        p = page(); p.StaleTimes = 5;
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(9, "z = 1"));
        Ok("stale twice -> Error, nothing applied", (r as string ?? "").StartsWith("Error: the CA Embeditor's text changed") && p.LastApply == null, r as string);

        p = page(); p.Refuse = "notEditable";
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(9, "z = 1"));
        Ok("refused notEditable -> Error naming it", (r as string ?? "").StartsWith("Error: that range is not an editable embed slot"), r as string);

        p = page(); p.Hang = true;
        EmbedToolRouter.LiveEmbedResolver = () => p;
        nativeRan = false;
        r = router.Run("get_embed_content", native, ov => ov.GetEmbedContent(9));
        Ok("page does not answer -> Error, native never runs (fail closed)",
            (r as string ?? "").StartsWith("Error: the CA Embeditor did not answer") && (r as string).Contains("nothing was done in the native") && !nativeRan, r as string);

        p = page(); p.Native = new List<int[]> { new[] { 3, 3 } };
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(9, "z = 1"));
        Ok("CA Embeditor slots no longer match the native ones -> Error, nothing written",
            (r as string ?? "").StartsWith("Error: the CA Embeditor's embed slots no longer match") && p.LastApply == null, r as string);

        // --- routed save: save_and_close_embeditor through the CA Embeditor's own save ---
        p = page(); p.SaveReply = new Dictionary<string, object> { { "saved", true }, { "message", "Saved 1 embed slot(s) to 'UpdateCust'." }, { "editorIntact", false } };
        EmbedToolRouter.LiveEmbedResolver = () => p;
        nativeRan = false;
        r = router.Run("save_and_close_embeditor", native, ov => ov.SaveAndClose());
        Ok("save ok -> 'Saved and closed the CA Embeditor', with the save's message, native never runs",
            (r as string) == "Saved and closed the CA Embeditor on 'UpdateCust': Saved 1 embed slot(s) to 'UpdateCust'." && !nativeRan, r as string);
        p = page(); p.SaveReply = new Dictionary<string, object> { { "saved", false }, { "message", "embed structure changed" }, { "editorIntact", true } };
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("save_and_close_embeditor", native, ov => ov.SaveAndClose());
        Ok("save failed, editor intact -> Error that says the edits are still open",
            (r as string ?? "").StartsWith("Error: the CA Embeditor's save failed: embed structure changed.") && (r as string).Contains("still open"), r as string);
        p = page(); p.SaveReply = new Dictionary<string, object> { { "saved", false }, { "message", "x" }, { "editorIntact", false } };
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("save_and_close_embeditor", native, ov => ov.SaveAndClose());
        Ok("save failed, editor gone -> Error that says so", (r as string ?? "").Contains("The CA Embeditor closed"), r as string);
        p = page(); p.SaveReply = null;
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("save_and_close_embeditor", native, ov => ov.SaveAndClose());
        Ok("save times out -> Error that says NOT to retry (it may still complete)",
            (r as string ?? "").Contains("do NOT retry") && !(r as string).Contains("Try again"), r as string);

        // --- EmbedSaveWait: the outcome comes from EmbedSaveFinished, not the page's reply ---
        Action<string, bool, string, bool> evt = null;
        Action<Action<string, bool, string, bool>> sub = h => evt += h;
        Action<Action<string, bool, string, bool>> unsub = h => evt -= h;
        Func<Action<string, bool, string, bool>> current = () => evt;
        Dictionary<string, object> d;
        // A successful overlay save: the page is disposed, its request never answers - only the event does.
        d = Try(() => EmbedSaveWait.Run("UpdateCust", sub, unsub, () =>
        {
            var e = current(); if (e != null) e("updatecust", true, "Saved 2 embed slot(s).", false);
            throw new TimeoutException("page gone");
        }, 5000));
        Ok("event (proc matched case-insensitively) is the answer, even though the page never replies",
            d != null && (bool)d["saved"] && (string)d["message"] == "Saved 2 embed slot(s)." && !(bool)d["editorIntact"], null);
        Ok("unsubscribed afterwards", evt == null, null);

        d = Try(() => EmbedSaveWait.Run("UpdateCust", sub, unsub, () =>
        {
            var e = current();
            if (e != null) { e("OtherProc", true, "not ours", false); e("UpdateCust", false, "refused: mirror mode", true); }
            return new Dictionary<string, object>();
        }, 5000));
        Ok("another procedure's save is ignored; ours (a failure) is reported with editorIntact",
            d != null && !(bool)d["saved"] && (string)d["message"] == "refused: mirror mode" && (bool)d["editorIntact"], null);

        string refusedCode = null;
        try { EmbedSaveWait.Run("UpdateCust", sub, unsub, () => { throw new HostRequestBroker.RefusedException("saveDisabled"); }, 5000); }
        catch (HostRequestBroker.RefusedException ex) { refusedCode = ex.Code; }
        Ok("the page refuses to start the save (no event will come) -> RefusedException, not a long wait", refusedCode == "saveDisabled", refusedCode);

        // ONE SAVE AT A TIME (1565ef7b final): a save asked for while another runs is refused at once, with its own
        // busy-refusal event AND (for the page's pending host save) a saveResult(ok=false) reply.
        const string busy = "A save is already in progress; try again in a moment.";
        string busyCode = null;
        var swb = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            EmbedSaveWait.Run("UpdateCust", sub, unsub, () =>
            {
                var e = current(); if (e != null) e("UpdateCust", false, busy, true);
                return new Dictionary<string, object> { { "saved", false }, { "message", busy } };
            }, 5000);
        }
        catch (HostRequestBroker.RefusedException ex) { busyCode = ex.Code; }
        swb.Stop();
        Ok("OUR save refused as busy -> RefusedException(saveInProgress) at once, not a wait",
            busyCode == EmbedSaveWait.InProgressCode && swb.ElapsedMilliseconds < 1000, busyCode + " in " + swb.ElapsedMilliseconds + " ms");

        // The developer's Ctrl+S is refused WHILE our save runs: its busy event is not our outcome; ours follows.
        d = Try(() => EmbedSaveWait.Run("UpdateCust", sub, unsub, () =>
        {
            var e = current();
            if (e != null) { e("UpdateCust", false, busy, true); e("UpdateCust", true, "Saved 1 embed slot(s).", false); }
            throw new TimeoutException("page gone");   // our successful overlay save disposed the page
        }, 5000));
        Ok("someone else's busy-refusal event is skipped; our save's own event is the answer",
            d != null && (bool)d["saved"] && (string)d["message"] == "Saved 1 embed slot(s).", d == null ? null : (string)d["message"]);

        p = page();
        EmbedToolRouter.LiveEmbedResolver = () => p;
        r = router.Run("save_and_close_embeditor", native, ov => { throw new HostRequestBroker.RefusedException(EmbedSaveWait.InProgressCode); });
        Ok("router words the busy refusal for Claude: in progress, try again shortly, nothing saved",
            (r as string ?? "").StartsWith("Error: a save is already in progress in the CA Embeditor; try again shortly."), r as string);

        bool timedOut = false;
        var swt = System.Diagnostics.Stopwatch.StartNew();
        try { EmbedSaveWait.Run("UpdateCust", sub, unsub, () => { Thread.Sleep(2000); return null; }, 300); }
        catch (TimeoutException) { timedOut = true; }
        Ok("no outcome in time -> TimeoutException, bounded", timedOut && swt.ElapsedMilliseconds < 1500 && evt == null, swt.ElapsedMilliseconds + " ms");

        // --- a blocked UI thread: bounded, says so, touches nothing ---
        p = page();
        EmbedToolRouter.LiveEmbedResolver = () => p;
        ui.Blocked = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r = router.Run("write_embed_content", native, ov => ov.WriteEmbedContent(9, "z = 1"));
        sw.Stop();
        ui.Blocked = false;
        Ok("UI thread blocked -> Error after the bounded resolve wait, nothing written",
            (r as string ?? "").StartsWith("Error: the IDE's UI thread did not answer") && p.Calls.Count == 0
            && sw.ElapsedMilliseconds < EmbedToolRouter.ResolveTimeoutMs + 2000, sw.ElapsedMilliseconds + " ms: " + r);

        Console.WriteLine();
        Console.WriteLine("EmbedToolRouter: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
