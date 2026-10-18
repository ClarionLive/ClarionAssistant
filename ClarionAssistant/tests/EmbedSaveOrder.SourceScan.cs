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

        // ---- GH #179 (254e4353): the save journal outlives ok=True ----
        int rtj = view.IndexOf("private void RunSaveRoundTripCore(", StringComparison.Ordinal);
        int rtjEnd = rtj >= 0 ? view.IndexOf("private bool IsStillLive()", rtj, StringComparison.Ordinal) : -1;
        string rtBody = rtj >= 0 && rtjEnd > rtj ? view.Substring(rtj, rtjEnd - rtj) : "";
        int bj = rtBody.IndexOf("BeginSaveJournal(current);", StringComparison.Ordinal);
        int firstSave = rtBody.IndexOf("ModernEmbeditorSaver.Save", StringComparison.Ordinal);
        Ok("the journal is written before ANY native save in the round-trip", bj > 0 && firstSave > bj,
            "journal@" + bj + " firstSave@" + firstSave);
        // Nothing on the success path retires it: the Generator faults AFTER refreshPadSources DONE. The only retires
        // in the round-trip are on a failure that left the text in the editor.
        Ok("the round-trip never touches the journal file directly", !rtBody.Contains("EmbedSaveJournal.Retire(") &&
            !rtBody.Contains("EmbedSaveJournal.Archive("));
        bool retiresGuarded = true;
        int nRetire = 0;
        for (int i = rtBody.IndexOf("RetireSaveJournal(", StringComparison.Ordinal); i >= 0;
             i = rtBody.IndexOf("RetireSaveJournal(", i + 1, StringComparison.Ordinal))
        {
            nRetire++;
            string before = rtBody.Substring(Math.Max(0, i - 220), Math.Min(220, i));
            bool failureOnly = before.Contains("else if (editorIntact)") || rtBody.Substring(Math.Max(0, i - 10), Math.Min(10, i)).Contains("if (!ok) ");
            if (!failureOnly) retiresGuarded = false;
        }
        Ok("the round-trip retires the journal ONLY on a failure that left the text in the editor (never on ok=True)",
            retiresGuarded && nRetire == 2, "retires=" + nRetire);
        Ok("the journal file is deleted (no copy) in exactly two places: failure-intact and persisted-at-open",
            Count(view, "EmbedSaveJournal.Retire(") == 2);
        int hso = view.IndexOf("private void HandleSavedEditsOffer(", StringComparison.Ordinal);
        int hsoEnd = hso >= 0 ? view.IndexOf("private void BeginSaveJournal(", hso, StringComparison.Ordinal) : -1;
        string hsoBody = hso >= 0 && hsoEnd > hso ? view.Substring(hso, hsoEnd - hso) : "";
        Ok("both offer buttons keep a dated copy (Restore and Discard archive, neither just deletes)",
            Count(hsoBody, "EmbedSaveJournal.Archive(") == 2 && !hsoBody.Contains("EmbedSaveJournal.Retire("));
        Ok("the next open checks the journal", view.Contains("CheckSaveJournal();   // a save that did not survive the Generator"));
        int ose = page.IndexOf("msg.type === 'offerSavedEdits'", StringComparison.Ordinal);
        int oseEnd = ose >= 0 ? page.IndexOf("} else if (msg.type", ose + 10, StringComparison.Ordinal) : -1;
        string oseBody = ose >= 0 && oseEnd > ose ? page.Substring(ose, oseEnd - ose) : "";
        int restoreBtn = oseBody.IndexOf("label: 'Restore'", StringComparison.Ordinal);
        int applyAt = oseBody.IndexOf("applyOrParkSlots(", StringComparison.Ordinal);
        Ok("the page OFFERS the lost save (restored only from the Restore button, never on arrival)",
            oseBody.Contains("showActionToast(") && restoreBtn > 0 && applyAt > restoreBtn &&
            !oseBody.Contains("restoreSlotContents(") && oseBody.Contains("label: 'Discard (a copy is kept)'"), "restore@" + restoreBtn + " apply@" + applyAt);
        Ok("the offer's buttons are exactly 'Restore' and 'Discard (a copy is kept)'",
            Count(oseBody, "label: '") == 2 && oseBody.Contains("label: 'Restore'") && oseBody.Contains("label: 'Discard (a copy is kept)'"));
        Ok("restoreSlots and the offer share applyOrParkSlots", page.Contains("function applyOrParkSlots(slots)") &&
            Count(page, "applyOrParkSlots(") == 3);

        // ---- pipeline run 1 ----
        int cj = view.IndexOf("private void CheckSaveJournal()", StringComparison.Ordinal);
        int cjEnd = cj >= 0 ? view.IndexOf("private void HandleSavedEditsOffer(", cj, StringComparison.Ordinal) : -1;
        string cjBody = cj >= 0 && cjEnd > cj ? view.Substring(cj, cjEnd - cj) : "";
        Ok("the open decides with the pid rule (EmbedSaveJournal.Decide), not the bare verdict",
            cjBody.Contains("EmbedSaveJournal.Decide(e, _originalSlotTexts, MyPid())") && !cjBody.Contains(".Assess("));
        // ---- pipeline run 2 (PM ruling) ----
        Ok("the offer's wording uses the .app's last-write time (it never decides whether to offer)",
            cjBody.Contains("appWrite = File.GetLastWriteTime(JournalApp())") &&
            cjBody.Contains("EmbedSaveJournal.OfferMessage(e, appWrite)") && !cjBody.Contains("IsSuperseded") &&
            !cjBody.Contains("AppWrittenSince"));
        // Every use of the .app time in the open check is its read, the log line, or the offer's wording: none may
        // branch on it (a later write is the #179 reporter's crash, and it must still be offered).
        string badUse = null;
        foreach (var line in cjBody.Split('\n'))
        {
            if (!line.Contains("appWrite")) continue;
            if (line.Contains("DateTime? appWrite = null;") || line.Contains("appWrite = File.GetLastWriteTime(JournalApp())") ||
                line.Contains("(appWrite.HasValue ? appWrite.Value.ToString(") || line.Contains("OfferMessage(e, appWrite)")) continue;
            badUse = line.Trim();
        }
        Ok("the .app time never decides whether a lost save is offered", badUse == null, badUse);
        // ---- pipeline run 3 ----
        int arcB = cjBody.IndexOf("OpenDecision.Archive)", StringComparison.Ordinal);
        int arcEnd = arcB >= 0 ? cjBody.IndexOf("return;", arcB, StringComparison.Ordinal) : -1;
        string arcBody = arcB >= 0 && arcEnd > arcB ? cjBody.Substring(arcB, arcEnd - arcB) : "";
        Ok("a changed-since entry is archived silently (log line, no toast)",
            arcBody.Contains("EmbedSaveJournal.Archive(") && arcBody.Contains("MonacoSpikeLog.Write(") && !arcBody.Contains("PostJson("),
            arcBody.Length == 0 ? "archive branch not found" : null);
        int sw = view.IndexOf("private static void SweepStaleJournalsOnce()", StringComparison.Ordinal);
        string swBody = sw >= 0 ? view.Substring(sw, Math.Min(1400, view.Length - sw)) : "";
        Ok("the once-per-session sweep also prunes old recovery copies",
            swBody.Contains("EmbedRecovery.PruneOlderThan(Services.EmbedSaveJournal.MaxAge, DateTime.Now)"));
        Ok("the first open of a session sweeps stale journals before reading this one",
            cjBody.IndexOf("SweepStaleJournalsOnce();", StringComparison.Ordinal) >= 0 &&
            cjBody.IndexOf("SweepStaleJournalsOnce();", StringComparison.Ordinal) < cjBody.IndexOf("EmbedSaveJournal.Read(", StringComparison.Ordinal) &&
            view.Contains("EmbedSaveJournal.SweepStale(DateTime.Now)"));
        int ja = view.IndexOf("private string JournalApp()", StringComparison.Ordinal);
        string jaBody = ja >= 0 ? view.Substring(ja, Math.Min(300, view.Length - ja)) : "";
        Ok("the journal key is the launcher-captured app, never the 'current app' guess",
            jaBody.Contains("EmbedAppFile") && !jaBody.Contains("TryGetCurrentAppKey"));
        string launcher = File.ReadAllText(Path.Combine(root, "Services", "ModernEmbeditorLauncher.cs"));
        Ok("both launcher paths capture the embed's own app and hand it to the view",
            Count(launcher, "appTree.GetOpenEmbedAppFileName()") == 2 &&
            launcher.Contains("view.EmbedAppFile = capApp;") && launcher.Contains("overlay.EmbedAppFile = capApp;"));

        // ---- GH #179: every monaco-spike.log line names its process ----
        string spike = File.ReadAllText(Path.Combine(root, "MonacoClarionSourceEditor.cs"));
        Ok("MonacoSpikeLog.Write tags each line with the pid, after the timestamp",
            spike.Contains("ToString(\"yyyy-MM-dd HH:mm:ss.fff\") + \"  \" + PidTag + message"));
        string cfgDir = File.ReadAllText(Path.Combine(root, "Services", "ClarionConfigDirectory.cs"));
        Ok("ClarionConfigDirectory.LogOnce writes the same [pid N] shape", cfgDir.Contains("\"  [pid \" +"));

        Console.WriteLine(fail == 0 ? "all passed" : fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
