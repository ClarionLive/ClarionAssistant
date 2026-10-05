using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Path B M2 — save round-trip for the Modern Embeditor. Persists edits made in a Monaco snapshot
    /// tab back into the .app by re-opening the procedure's (transient) Clarion embeditor and writing the
    /// changed embed slots via WriteEmbedContentByLine, then SaveAndCloseEmbeditor.
    ///
    /// Safety-first (this writes real user code):
    ///   • Re-derive the fresh embed structure and match the snapshot's slots to it by identity
    ///     (<see cref="EmbedSavePlanner"/>, 1565ef7b): slots that merely moved are found at their new lines;
    ///     a changed slot count or generated skeleton ABORTS.
    ///   • For each changed slot, ABORT if the fresh text differs from what we opened
    ///     (someone edited it elsewhere) — never overwrite a slot we don't recognise.
    ///   • Write changed slots bottom-to-top (so earlier line numbers stay valid), verbatim
    ///     (no re-indent). If any write errors, CANCEL (persist nothing).
    /// Must run on the UI thread.
    /// </summary>
    public static class ModernEmbeditorSaver
    {
        /// <summary>Extract each editable slot's text from a source buffer. Ranges are 1-based inclusive.</summary>
        public static List<string> ExtractSlotTexts(string source, List<int[]> ranges)
        {
            // 1d8d1c49: a line walk instead of SplitLines(source) — the split cost 32 MB per open of a 3.2 MB
            // procedure. Same text (TextLines.Test compares it against the split version).
            return TextLines.ExtractRanges(source, ranges);
        }

        public static string Save(string procName, string originalSource, List<int[]> originalRanges,
            IList<string> originalSlotTexts, IList<string> currentSlotTexts, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(procName))
                return "Save unavailable: this view isn't bound to a procedure (opened in mirror mode).";
            if (originalRanges == null || originalSlotTexts == null || currentSlotTexts == null)
                return "Save aborted: missing slot data.";
            if (currentSlotTexts.Count != originalRanges.Count || originalSlotTexts.Count != originalRanges.Count)
                return "Save aborted: slot count mismatch (Monaco " + currentSlotTexts.Count +
                       ", original " + originalSlotTexts.Count + ", ranges " + originalRanges.Count + ").";

            // Which slots did the user actually change?
            var changed = new List<int>();
            for (int i = 0; i < originalRanges.Count; i++)
                if (!NLEqual(currentSlotTexts[i], originalSlotTexts[i]))
                    changed.Add(i);
            if (changed.Count == 0) { ok = true; return "No changes to save."; }

            var appTree = new AppTreeService();
            // Reliably re-open the correct procedure (fast Ctrl+V locator, verified, with typing fallback)
            // and mirror its current source + ranges; leaves the embeditor open for us to write into.
            string fsource, openErr;
            List<int[]> franges;
            if (!ModernEmbeditorLauncher.OpenAndMirror(appTree, procName, out fsource, out franges, out openErr))
                return "Save aborted: " + openErr;

            try
            {
                // Embeditor is open with the verified-correct procedure. Match the snapshot's slots to the
                // fresh ones by identity, not line number (1565ef7b): a save from a tab whose slots have since
                // moved still lands in the right slots, and a slot changed both here and elsewhere is refused.
                // Cancelling here is safe: this is the TRANSIENT embed we just opened, and the developer's
                // text stays in the tab.
                var plan = EmbedSavePlanner.Plan(originalSource, originalRanges, originalSlotTexts, currentSlotTexts,
                    null, fsource, franges);
                if (!plan.CanSave)
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    return "Save refused: " + plan.Refusal + " Your edits are still in this tab.";
                }

                // Planned writes are bottom-to-top so earlier slots' line numbers stay valid.
                var errors = new List<string>();
                foreach (var w in plan.Writes)
                {
                    string res = appTree.WriteEmbedContentByLine(w.Key, w.Value, false);
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errors.Add("  • slot@line " + w.Key + ": " + res);
                }

                if (errors.Count > 0)
                {
                    try { appTree.CancelEmbeditor(); } catch { } // discard — persist nothing on partial failure
                    return "Save FAILED — nothing persisted:\r\n" + string.Join("\r\n", errors);
                }

                string saveRes = appTree.SaveAndCloseEmbeditor();
                // Surface a save/close failure BEFORE waiting on the close — SaveAndCloseEmbeditor returns an
                // "Error"-prefixed string for every failure mode (unconfirmed persist, TryClose==false, throw).
                if (saveRes != null && saveRes.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    return "Save error: " + saveRes;

                // Confirm the native embeditor actually closed. If it didn't, the single-editor invariant is
                // broken (next open/save fails) — treat it as an error rather than reporting a phantom success.
                bool embedClosed = ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000);
                if (!embedClosed)
                    return "Save error: '" + procName + "' was written but the embeditor did not confirm closed — " +
                           "close it in the IDE before saving again.";

                ok = true;
                return "Saved " + changed.Count + " embed slot(s) to '" + procName + "'." + EmbedLiveSaveFlow.ForeignNote(plan);
            }
            catch (Exception ex)
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Save error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>Push the Monaco buffer's slots into the STILL-OPEN native embed WITHOUT saving or closing
        /// it — SaveLive's write loop, stopping short of SaveAndCloseEmbeditor.
        ///
        /// WHY THIS EXISTS (bcba6efb). Clarion raises its own "Save Changes in Embed Editor?" prompt from
        /// CommonGenEditor.TryClose(), which consults the NATIVE editor's IsDirty and, on Yes, runs
        /// SaveAndExit() over the NATIVE buffer. Our edits live in the page, so making Clarion prompt without
        /// this would produce a dialog whose Yes saves stale content — silently wrong data, which is strictly
        /// worse than the missing prompt it replaces.
        ///
        /// CALL THIS ONCE PER CLOSE GESTURE, NOT ON A TIMER. Driving the live PWEE editor repeatedly is a
        /// known instability (it is why ApplyLineEdits exists for large procedures), so a debounced background
        /// sync would trade a missing prompt for a flaky embeditor. One burst at close is the whole point.
        ///
        /// Failure is NON-DESTRUCTIVE by design: unlike SaveLive, a partial write does NOT cancel the embed.
        /// The user has not asked to discard anything yet — they are mid-close, and Clarion's own prompt has
        /// not even been shown. Report and let the caller decide.</summary>
        public static string SyncLive(string procName, string originalSource, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> currentSlotTexts, IList<string> nativeAlt, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(procName)) return "Sync skipped: no procedure bound.";
            if (ranges == null || originalSlotTexts == null || currentSlotTexts == null)
                return "Sync skipped: missing slot data.";
            if (currentSlotTexts.Count != ranges.Count || originalSlotTexts.Count != ranges.Count)
                return "Sync skipped: slot count mismatch (Monaco " + currentSlotTexts.Count +
                       ", original " + originalSlotTexts.Count + ", ranges " + ranges.Count + ").";

            var appTree = new AppTreeService();
            if (appTree.GetEmbedInfo() == null) return "Sync skipped: the live embeditor is no longer open.";

            // Same slot matching as SaveLive (1565ef7b): writing against stale ranges would corrupt the embed,
            // but slots that merely MOVED are matched by identity rather than refused.
            string ftitle, fsource, ferr;
            List<int[]> franges;
            if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out ftitle, out fsource, out franges, out ferr))
                return "Sync skipped: could not re-read the open embed buffer: " + ferr;
            var plan = EmbedSavePlanner.Plan(originalSource, ranges, originalSlotTexts, currentSlotTexts, nativeAlt,
                fsource, franges);
            if (!plan.CanSave) return "Sync skipped: " + plan.Refusal;
            if (plan.Writes.Count == 0) { ok = true; return "Nothing to sync."; }

            try
            {
                var errors = new List<string>();
                // Planned bottom-to-top so earlier slots' line numbers stay valid; verbatim, no re-indent.
                foreach (var w in plan.Writes)
                {
                    string res = appTree.WriteEmbedContentByLine(w.Key, w.Value, false);
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errors.Add("  • slot@line " + w.Key + ": " + res);
                }
                if (errors.Count > 0)
                    return "Sync FAILED (embed left open, nothing discarded):\r\n" + string.Join("\r\n", errors);

                ok = true;
                return "Synced " + plan.Writes.Count + " slot(s) into the live embed.";
            }
            catch (Exception ex)
            {
                return "Sync error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

        /// <summary>
        /// LIVE-LINKED fast-path save (ticket a5bbf005). The procedure's native embeditor is ALREADY open — the
        /// foreground live tab never cancelled it — so we SKIP OpenAndMirror entirely: no locator re-type, no
        /// re-find (the error-prone step this whole feature removes). The changed slots are written per slot,
        /// bottom-to-top and verbatim, into the SAME live Document, then SaveAndCloseEmbeditor (SAVE-AND-EXIT:
        /// it releases the single-embeditor lock and the caller closes the Monaco surface, as native Clarion does).
        ///
        /// Slots are matched to the CURRENT native buffer by identity, not line number, and the save decides
        /// whether it can go through before anything is torn down; see <see cref="EmbedLiveSaveFlow"/> (1565ef7b).
        /// A refusal or failed write leaves the native embed open and the CA Embeditor intact. <c>beforeClose</c>
        /// runs only when the save is committed, immediately before SaveAndCloseEmbeditor. Never a whole-buffer
        /// replace (that silently no-ops on PWEE embed regions and would clobber the read-only generated lines).
        ///
        /// The caller picks SaveLive vs <see cref="Save"/> up front via IsStillLive(); <c>NotLive</c> covers the
        /// razor-thin window after that check. UI thread only.
        /// </summary>
        public static EmbedLiveSaveOutcome SaveLive(string procName, string originalSource, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> currentSlotTexts, IList<string> nativeAlt, Action beforeClose)
        {
            // 1565ef7b: the flow decides whether the save can go through BEFORE beforeClose (the overlay detach)
            // and never cancels the native embed, so a refused or failed save keeps the developer's text.
            return EmbedLiveSaveFlow.SaveLive(new AppTreeLiveSaveOps(new AppTreeService()), procName, originalSource,
                ranges, originalSlotTexts, currentSlotTexts, nativeAlt, beforeClose);
        }

        /// <summary>The live IDE behind <see cref="IEmbedLiveSaveOps"/>. UI thread only.</summary>
        private sealed class AppTreeLiveSaveOps : IEmbedLiveSaveOps
        {
            private readonly AppTreeService _appTree;
            public AppTreeLiveSaveOps(AppTreeService appTree) { _appTree = appTree; }
            public bool EmbedOpen() { return _appTree.GetEmbedInfo() != null; }
            public bool ReadNative(out string source, out List<int[]> ranges, out string error)
            {
                string title;
                return EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out error);
            }
            public string WriteSlot(int line, string code) { return _appTree.WriteEmbedContentByLine(line, code, false); }
            public string SaveAndClose() { return _appTree.SaveAndCloseEmbeditor(); }
            public bool WaitClosed(int timeoutMs) { return ModernEmbeditorLauncher.WaitForEmbedClosed(_appTree, timeoutMs); }
        }

        /// <summary>
        /// Apply explicit per-slot edits to a procedure in ONE transient open-&gt;write-&gt;save-&gt;close round-trip,
        /// with NO interactive embeditor session left open. Robust for very large procedures where the live PWEE
        /// editor is unstable under repeated interactive driving (this reuses the proven Modern Embeditor save
        /// path: OpenAndMirror -&gt; WriteEmbedContentByLine -&gt; SaveAndCloseEmbeditor -&gt; WaitForEmbedClosed).
        ///
        /// Each edit is (1-based «E:N» slot-start line, COMPLETE replacement code for that slot). Every line is
        /// validated against the mirrored embed structure; if ANY line is not a current embed-slot start,
        /// NOTHING is written. Writes run bottom-to-top so earlier slots' line numbers stay valid, verbatim
        /// (no re-indent — the caller supplies fully-indented code). UI thread only.
        ///
        /// ADOPTION: when an embeditor is already open on this SAME procedure we write into it rather than
        /// refuse (see <see cref="ModernEmbeditorLauncher.TryAdoptOpenEmbeditor"/>) — on a large procedure the
        /// fresh open is the step that fails, so that editor is often the only working handle. The save still
        /// closes the tab, because <c>SaveAndCloseEmbeditor</c> is the only persist path the IDE exposes - and it
        /// persists the WHOLE buffer, so an editor is adopted only when that buffer holds nothing but saved code
        /// (native IsDirty == false) and no CA Embeditor sits over it (<see cref="EmbedAdoptPolicy"/>). Otherwise
        /// the call is refused and the editor left untouched, as is one open on a DIFFERENT procedure.
        ///
        /// Once an embeditor is open, the write -> commit -> save -> confirm-closed half is
        /// <see cref="EmbedApplyFlow"/>: every exit after the first write that does not end in a confirmed save
        /// discards the buffer (an adopted one was clean, so only our writes go), and a call McpDispatcher has
        /// abandoned on timeout rolls back instead of saving.
        /// </summary>
        public static string ApplyLineEdits(string procName, IList<KeyValuePair<int, string>> edits, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(procName))
                return "Error: procedure_name is required.";
            if (edits == null || edits.Count == 0)
                return "Error: no edits supplied.";

            var appTree = new AppTreeService();

            // Prefer an embeditor ALREADY open on this procedure over a fresh open. On a large procedure
            // the fresh open is the fragile step, so the developer-opened editor is frequently the only
            // handle that worked — refusing it (the old behaviour) made this tool unusable exactly where
            // it is most needed, and closing their editor to satisfy the precondition throws away that
            // handle. An editor open on a DIFFERENT procedure is still refused, and left untouched - as is one
            // with unsaved developer edits, or one the CA Embeditor is covering (see EmbedAdoptPolicy).
            string fsource, openErr;
            List<int[]> franges;
            bool adopted = ModernEmbeditorLauncher.TryAdoptOpenEmbeditor(
                appTree, procName, out fsource, out franges, out openErr);

            if (!adopted)
            {
                // A non-null error means something else is open — say that, don't try to open over it.
                if (!string.IsNullOrEmpty(openErr))
                    return "Apply aborted: " + openErr;

                // Nothing open: reliably open the correct procedure and mirror its current source +
                // ranges; leaves the embeditor open for us to write into (same entry point the
                // interactive save uses).
                if (!ModernEmbeditorLauncher.OpenAndMirror(appTree, procName, out fsource, out franges, out openErr))
                    return "Apply aborted: " + openErr;
            }

            return EmbedApplyFlow.Apply(new AppTreeApplyOps(appTree), procName, franges, edits, adopted,
                McpCallContext.Current, out ok);
        }

        /// <summary>The live IDE behind <see cref="IEmbedApplyOps"/>. UI thread only.</summary>
        private sealed class AppTreeApplyOps : IEmbedApplyOps
        {
            private readonly AppTreeService _appTree;
            public AppTreeApplyOps(AppTreeService appTree) { _appTree = appTree; }
            public string WriteSlot(int line, string code) { return _appTree.WriteEmbedContentByLine(line, code, false); }
            public string SaveAndClose() { return _appTree.SaveAndCloseEmbeditor(); }
            public bool WaitClosed(int timeoutMs) { return ModernEmbeditorLauncher.WaitForEmbedClosed(_appTree, timeoutMs); }
            // Success only when CancelEmbeditor reports no error AND the embeditor is confirmed closed - an
            // unconfirmed rollback must never be reported as one. "No embeditor is currently open" is success:
            // there is nothing left to hold our writes.
            public string Discard()
            {
                try
                {
                    string res = _appTree.CancelEmbeditor();
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                        && res.IndexOf("No embeditor is currently open", StringComparison.OrdinalIgnoreCase) < 0)
                        return res;
                    if (!ModernEmbeditorLauncher.WaitForEmbedClosed(_appTree, 3000))
                        return "the embeditor did not confirm closed";
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                }
            }
        }

        private static bool NLEqual(string x, string y)
        {
            return string.Equals(NormalizeNL(x), NormalizeNL(y), StringComparison.Ordinal);
        }

        private static string NormalizeNL(string s)
        {
            return (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }
    }
}
