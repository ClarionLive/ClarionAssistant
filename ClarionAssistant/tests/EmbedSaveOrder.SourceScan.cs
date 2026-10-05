using System;
using System.IO;

// 1565ef7b: pins the two ORDERING facts EmbedSaveFlow.Test cannot see, because they live in IDE-coupled code:
//   1. RunSaveRoundTrip's overlay branch must not detach the CA Embeditor before the save has decided. The only
//      DetachOverlay() in that branch is the one inside the beforeClose callback handed to SaveLive.
//   2. ModernEmbeditorSaver.SaveLive must never CancelEmbeditor (a cancel there discarded the developer's text).
//   3. Every embed save raises EmbedSaveFinished exactly once (EmbedSave's routed save waits on it), including
//      HandleSave's early refusals and the page's mirror-mode refusal.
// 1 and 2 were violated on master 3904549 (the live repro of 73bd1f03 Case A); this scan is red there.
//
// Run:  tests\Run-Tests.ps1   (arg 0 = the ClarionAssistant project dir)
static class EmbedSaveOrderSourceScan
{
    static int fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        Console.WriteLine((cond ? "  [ok]   " : "  [FAIL] ") + name + (cond || detail == null ? "" : "  -> " + detail));
        if (!cond) fail++;
    }

    static int Count(string s, string needle)
    {
        int n = 0;
        for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : ".";
        string view = File.ReadAllText(Path.Combine(root, "Terminal", "ModernEmbeditorViewContent.cs"));
        string saver = File.ReadAllText(Path.Combine(root, "Services", "ModernEmbeditorSaver.cs"));

        // ---- 1. the overlay branch ----
        int branch = view.IndexOf("if (_embedOverlay && live)", StringComparison.Ordinal);
        Ok("overlay save branch found", branch >= 0);
        if (branch >= 0)
        {
            int end = view.IndexOf("return;", branch, StringComparison.Ordinal);
            string body = view.Substring(branch, (end > branch ? end : view.Length) - branch);
            int save = body.IndexOf("ModernEmbeditorSaver.SaveLive(", StringComparison.Ordinal);
            int detach = body.IndexOf("DetachOverlay()", StringComparison.Ordinal);
            Ok("overlay branch calls SaveLive", save >= 0);
            Ok("no DetachOverlay() before SaveLive has run (detach only via beforeClose)",
                save >= 0 && (detach < 0 || detach > save),
                "DetachOverlay at +" + detach + ", SaveLive at +" + save);
            Ok("detach is handed to SaveLive as its beforeClose callback",
                save >= 0 && body.IndexOf("=>", save, StringComparison.Ordinal) > save && detach > save);
        }

        // ---- 2. SaveLive never cancels ----
        int sl = saver.IndexOf("SaveLive(string procName", StringComparison.Ordinal);
        Ok("SaveLive found", sl >= 0);
        if (sl >= 0)
        {
            int next = saver.IndexOf("public static", sl + 1, StringComparison.Ordinal);
            string body = saver.Substring(sl, (next > sl ? next : saver.Length) - sl);
            Ok("SaveLive never calls CancelEmbeditor", body.IndexOf("CancelEmbeditor", StringComparison.Ordinal) < 0);
        }

        // ---- 3. every embed save raises EmbedSaveFinished exactly once ----
        // EmbedSave's routed save_and_close_embeditor (73bd1f03) posts the page's save and waits on the event;
        // an exit that doesn't raise it leaves that caller waiting out its whole budget.
        int hs = view.IndexOf("private void HandleSave(string json)", StringComparison.Ordinal);
        Ok("HandleSave found", hs >= 0);
        if (hs >= 0)
        {
            int next = view.IndexOf("private void ", hs + 1, StringComparison.Ordinal);
            string body = view.Substring(hs, (next > hs ? next : view.Length) - hs)
                .Replace("if (_fileMode) { HandleFileSave(json); return; }", "");   // CA Editor file saves are out of scope
            // The one return allowed without a raise is the JOIN: a save arriving while one is in flight shares that
            // save's single outcome (pipeline Run 1), so raising for it too would be a second event for one save.
            int joinAt = body.IndexOf("_saveGate.TryEnter(", StringComparison.Ordinal);
            int joins = body.Contains("JoinRunningSave(current);") ? 1 : 0;
            int returns = Count(body, "return;"), raises = Count(body, "RaiseEmbedSaveFinished(");
            Ok("every early return in HandleSave raises EmbedSaveFinished, bar the in-flight join (" + returns + " returns, " +
                raises + " raises, " + joins + " join)", returns == raises + joins && raises > 0);
            int handOff = body.IndexOf("BeginInvoke((Action)(() => RunSaveRoundTrip(captured, token)))", StringComparison.Ordinal);
            Ok("one save at a time: the gate is taken before the round-trip is handed off", joinAt >= 0 && handOff > joinAt);
            Ok("the round-trip hand-off can't be dropped silently (inline fallback)", body.Contains("if (!posted) RunSaveRoundTrip(captured, token);"));
        }
        int rt = view.IndexOf("private void RunSaveRoundTrip(List<string> current, int token)", StringComparison.Ordinal);
        int st = rt >= 0 ? view.IndexOf("if (!_saveGate.Start(token))", rt, StringComparison.Ordinal) : -1;
        int tr = rt >= 0 ? view.IndexOf("try { RunSaveRoundTripCore(", rt, StringComparison.Ordinal) : -1;
        string startBlock = st > rt && tr > st ? view.Substring(st, tr - st) : "";
        // Pipeline Run 3 (Codex security): a superseded callback is OLDER than the save that superseded it, so it is
        // discarded — never saved and never made pending (either would put older text on top of newer).
        Ok("a superseded queued save is discarded: it never runs, saves or becomes pending",
            startBlock.Length > 0 && startBlock.Contains("return;") && !startBlock.Contains("RunSaveRoundTripCore") &&
            !startBlock.Contains("JoinRunningSave(") && !startBlock.Contains("TryEnter("));
        int core = rt >= 0 ? view.IndexOf("private void RunSaveRoundTripCore(", rt, StringComparison.Ordinal) : -1;
        int fin = rt >= 0 ? view.IndexOf("finally", rt, StringComparison.Ordinal) : -1;
        string finBody = fin > rt && core > fin ? view.Substring(fin, core - fin) : "";
        Ok("RunSaveRoundTrip raises in a finally, and releases ITS OWN gate token there", finBody.Contains("RaiseEmbedSaveFinished(") &&
            finBody.Contains("_saveGate.Exit(token)"));
        Ok("newer text requested during a save is saved by a follow-up, or kept on disk — never dropped",
            finBody.Contains("_saveGate.TakeFollowUp(current)") && finBody.Contains("KeepUnsavedRequest(followUp)"));
        // Pipeline Run 3 (debugger + Codex adversary): the follow-up runs INLINE under a token reserved right after the
        // release — never deferred (a gap let older text land last; a dropped callback left the cycle unanswered).
        int fx = finBody.IndexOf("_saveGate.Exit(token)", StringComparison.Ordinal);
        int fr = finBody.IndexOf("int ft = _saveGate.TryEnter(", StringComparison.Ordinal);
        int fc = finBody.IndexOf("RunSaveRoundTrip(followUp, ft);", StringComparison.Ordinal);
        Ok("the follow-up runs inline under a token reserved right after the release", fx >= 0 && fr > fx && fc > fr &&
            finBody.IndexOf("BeginInvoke", StringComparison.Ordinal) < 0 && view.IndexOf("PostFollowUpSave(", StringComparison.Ordinal) < 0);
        Ok("a live save-and-exit deferred to a follow-up still closes the tab (Ctrl+Q on a live tab)",
            view.Contains("if (live && ok && newerPending) _closeAfterFollowUp = true;") &&
            view.Contains("if (exitNow && !newerPending) { _closeAfterFollowUp = false; PostCloseTab();"));
        // Cancel and the Ctrl+F4 sync must not drive the native embed mid-save (pipeline Run 2).
        int hc = view.IndexOf("private void HandleCancel()", StringComparison.Ordinal);
        int hsn = view.IndexOf("private void HandleSyncNativeForClose()", StringComparison.Ordinal);
        Ok("Cancel is held off while a save runs", hc >= 0 &&
            view.IndexOf("_saveGate.Busy(", hc, StringComparison.Ordinal) > hc &&
            view.IndexOf("_saveGate.Busy(", hc, StringComparison.Ordinal) < view.IndexOf("CancelEmbeditor()", hc, StringComparison.Ordinal));
        Ok("the Ctrl+F4 sync is held off while a save runs", hsn >= 0 &&
            view.IndexOf("_saveGate.Busy(", hsn, StringComparison.Ordinal) > hsn &&
            view.IndexOf("_saveGate.Busy(", hsn, StringComparison.Ordinal) < view.IndexOf("ModernEmbeditorSaver.SyncLive(", hsn, StringComparison.Ordinal));
        string page = File.ReadAllText(Path.Combine(root, "Terminal", "monaco-embeditor.html"));
        int ds = page.IndexOf("function doSave()", StringComparison.Ordinal);
        int gate = ds >= 0 ? page.IndexOf("if (!saveEnabled)", ds, StringComparison.Ordinal) : -1;
        int gateEnd = gate >= 0 ? page.IndexOf("return;", gate, StringComparison.Ordinal) : -1;
        Ok("the page's mirror-mode refusal still tells the host (embed mode)",
            gate > ds && gateEnd > gate && page.Substring(gate, gateEnd - gate).Contains("postToHost({ action: 'save'"));

        Console.WriteLine(fail == 0 ? "all passed" : fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
