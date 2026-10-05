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
            // A save arriving while one runs is REFUSED with its own answer (Charlie's scope cut after pipeline Run 3):
            // it posts the busy refusal to the page and raises its own event — so every return raises.
            int busyAt = body.IndexOf("_saveGate.TryEnter(", StringComparison.Ordinal);
            int returns = Count(body, "return;"), raises = Count(body, "RaiseEmbedSaveFinished(");
            Ok("every early return in HandleSave raises EmbedSaveFinished (" + returns + " returns, " + raises + " raises)",
                returns == raises && raises > 0);
            int busyEnd = busyAt >= 0 ? body.IndexOf("return;", busyAt, StringComparison.Ordinal) : -1;
            string busyBlock = busyAt >= 0 && busyEnd > busyAt ? body.Substring(busyAt, busyEnd - busyAt) : "";
            Ok("a second save while one runs is refused: page told, own event raised, editor intact",
                busyBlock.Contains("if (token == 0)") &&
                busyBlock.Contains("PostSaveResult(false, Services.EmbedSaveGate.BusyMessage)") &&
                busyBlock.Contains("RaiseEmbedSaveFinished(false, Services.EmbedSaveGate.BusyMessage, true)") &&
                !busyBlock.Contains("RunSaveRoundTrip(") && !busyBlock.Contains("PostCloseTab") && !busyBlock.Contains("Cancel"));
            int handOff = body.IndexOf("BeginInvoke((Action)(() => RunSaveRoundTrip(captured, token)))", StringComparison.Ordinal);
            Ok("one save at a time: the gate is taken before the round-trip is handed off", busyAt >= 0 && handOff > busyAt);
            Ok("the round-trip hand-off can't be dropped silently (inline fallback)", body.Contains("if (!posted) RunSaveRoundTrip(captured, token);"));
        }
        int rt = view.IndexOf("private void RunSaveRoundTrip(List<string> current, int token)", StringComparison.Ordinal);
        int st = rt >= 0 ? view.IndexOf("if (!_saveGate.Start(token))", rt, StringComparison.Ordinal) : -1;
        int tr = rt >= 0 ? view.IndexOf("try { RunSaveRoundTripCore(", rt, StringComparison.Ordinal) : -1;
        string startBlock = st > rt && tr > st ? view.Substring(st, tr - st) : "";
        // Pipeline Run 3 (Codex security): a superseded callback is OLDER than the save that superseded it, so it never
        // saves — it is refused (its own event) and nothing else.
        Ok("a superseded queued save never saves; it is refused with its own event",
            startBlock.Length > 0 && startBlock.Contains("return;") && startBlock.Contains("RaiseEmbedSaveFinished(false,") &&
            startBlock.Contains("PostSaveResult(false, superseded)") &&   // final run, Codex adversary: its own page answer
            !startBlock.Contains("TryEnter("));
        int core = rt >= 0 ? view.IndexOf("private void RunSaveRoundTripCore(", rt, StringComparison.Ordinal) : -1;
        int fin = rt >= 0 ? view.IndexOf("finally", rt, StringComparison.Ordinal) : -1;
        string finBody = fin > rt && core > fin ? view.Substring(fin, core - fin) : "";
        Ok("RunSaveRoundTrip raises in a finally, and releases ITS OWN gate token there", finBody.Contains("RaiseEmbedSaveFinished(") &&
            finBody.Contains("_saveGate.Exit(token)"));
        // The follow-up/replay design is DELETED, not left dormant (Charlie, after pipeline Run 3).
        string[] gone = { "TakeFollowUp", "HasNewerPending", "JoinRunningSave", "PostFollowUpSave", "_closeAfterFollowUp",
                          "FollowUpPending", "KeepUnsavedRequest", "_saveGate.Join(", "public void Join(" };
        string flow = File.ReadAllText(Path.Combine(root, "Services", "EmbedSaveFlow.cs"));
        string left = string.Join(", ", Array.FindAll(gone, g => view.Contains(g) || flow.Contains(g)));
        Ok("no follow-up/replay machinery left (view or gate)", left.Length == 0, left);
        // Ctrl+Q during a save must never close with lost edits: a live save-and-exit does not close the tab when text
        // was typed during it, and that text also goes to disk (the page marks itself clean on the older save).
        Ok("a save-and-exit keeps text typed during the save (tab stays open; recovery copy)",
            view.Contains("if (live && ok && !typedDuring) { PostCloseTab(); editorIntact = false; }") &&
            view.Contains("if (typedDuring) msg += KeepTypedDuringSave(current, false);") &&
            view.Contains("PostSaveResult(ok, msg, typedDuring ? -1 : 0);") &&   // the page keeps its unsaved marker
            view.Contains("msg += KeepTypedDuringSave(current, true);"));        // the overlay's success path
        // Final run (Codex adversary HIGH): with the overlay gone, the recovery FILE must not be the only copy — a full
        // disk would lose the text. KeepTypedDuringSave also stashes it in memory for the next CA Embeditor open.
        int kt = view.IndexOf("private string KeepTypedDuringSave(", StringComparison.Ordinal);
        int ktEnd = kt >= 0 ? view.IndexOf("private void RunSaveRoundTripCore(", kt, StringComparison.Ordinal) : -1;
        string ktBody = kt >= 0 && ktEnd > kt ? view.Substring(kt, ktEnd - kt) : "";
        Ok("text typed during an overlay save-and-exit is also stashed in memory (not only a recovery file)",
            ktBody.Contains("if (surfaceGone") && ktBody.Contains("PutStash(new EmbedEditStash") &&
            ktBody.Contains("Original = new List<string>(saved)"));
        // ...and that stash is PER PROCEDURE: saving or tearing down procedure B must never discard procedure A's
        // unrestored edits (final-run delta, Codex adversary HIGH). No single static slot remains.
        Ok("the edit stash is per procedure; a save drops only its own procedure's entry",
            !view.Contains("EmbedEditStash _editStash;") && !view.Contains("_editStash = ") &&
            view.Contains("Dictionary<string, EmbedEditStash>") &&
            !view.Contains("_editStashes.Clear(") && !view.Contains("TakeStash("));
        // ...and a restore removes the stash only AFTER delivering it: it may be the last copy (re-check, Codex adversary).
        int tre = view.IndexOf("private void TryRestoreStashedEdits()", StringComparison.Ordinal);
        int treEnd = tre >= 0 ? view.IndexOf("private void FocusIfActiveTab()", tre, StringComparison.Ordinal) : -1;
        string treBody = tre >= 0 && treEnd > tre ? view.Substring(tre, treEnd - tre) : "";
        int peek = treBody.IndexOf("PeekStash(_procedureName)", StringComparison.Ordinal);
        int post = treBody.IndexOf("\\\"restoreSlots\\\",\\\"slots\\\"", StringComparison.Ordinal);
        int drop = post >= 0 ? treBody.IndexOf("DropStash(_procedureName);", post, StringComparison.Ordinal) : -1;
        Ok("a restore peeks the stash and drops it only after delivering it", peek >= 0 && post > peek && drop > post,
            "peek@" + peek + " post@" + post + " drop@" + drop);
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
