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
    ///   • Re-derive the fresh embed structure and ABORT if it no longer matches the snapshot
    ///     (slot count or start lines differ → the procedure changed underneath).
    ///   • For each changed slot, ABORT if the fresh on-disk text differs from what we opened
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
            var result = new List<string>();
            if (ranges == null) return result;
            var lines = SplitLines(source ?? "");
            foreach (var r in ranges)
            {
                if (r == null || r.Length < 2) { result.Add(""); continue; }
                int s = Math.Max(1, r[0]), e = Math.Min(lines.Length, r[1]);
                if (e < s) { result.Add(""); continue; }
                var sb = new StringBuilder();
                for (int i = s; i <= e; i++)
                {
                    if (i > s) sb.Append('\n');
                    sb.Append(lines[i - 1]);
                }
                result.Add(sb.ToString());
            }
            return result;
        }

        public static string Save(string procName, List<int[]> originalRanges,
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
                // Embeditor is open with the verified-correct procedure; confirm structure matches snapshot.
                if (!RangesMatch(franges, originalRanges))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    return "Save aborted: '" + procName + "' has changed since you opened it (embed structure " +
                           "differs). Reload the tab and re-apply your edits.";
                }

                var freshSlotTexts = ExtractSlotTexts(fsource, franges);
                foreach (int i in changed)
                {
                    if (!NLEqual(freshSlotTexts[i], originalSlotTexts[i]))
                    {
                        try { appTree.CancelEmbeditor(); } catch { }
                        return "Save aborted: the embed slot near line " + originalRanges[i][0] +
                               " was changed elsewhere since you opened it. Reload the tab and re-apply.";
                    }
                }

                // Write changed slots bottom-to-top so earlier slots' line numbers stay valid.
                var errors = new List<string>();
                foreach (int i in changed.OrderByDescending(x => originalRanges[x][0]))
                {
                    string res = appTree.WriteEmbedContentByLine(originalRanges[i][0], currentSlotTexts[i] ?? "", false);
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errors.Add("  • slot@line " + originalRanges[i][0] + ": " + res);
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
                return "Saved " + changed.Count + " embed slot(s) to '" + procName + "'.";
            }
            catch (Exception ex)
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Save error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        /// <summary>
        /// LIVE-LINKED fast-path save (ticket a5bbf005). The procedure's native embeditor is ALREADY open — the
        /// foreground live tab never cancelled it — so we SKIP OpenAndMirror entirely: no locator re-type, no
        /// re-find (the error-prone step this whole feature removes). We confirm the embed is still open and its
        /// structure still matches the snapshot (a cheap re-read of the OPEN buffer — no typing), then write the
        /// changed slots per-slot bottom-to-top (verbatim) into the SAME live Document and SaveAndCloseEmbeditor.
        ///
        /// SAVE-AND-EXIT semantics: SaveAndCloseEmbeditor closes the native embed (releasing the single-embeditor
        /// lock); the caller then closes the Monaco tab — matching native Clarion embed editing.
        ///
        /// The caller (RunSaveRoundTrip) picks SaveLive-vs-<see cref="Save"/> UP FRONT via IsStillLive()
        /// (this==_liveInstance AND the native embed is still open), so the normal demoted-tab case never reaches
        /// here — it goes straight to <see cref="Save"/>. SaveLive's own "no longer open" return is a belt-and-
        /// suspenders for the razor-thin window between that check and this call; it does NOT trigger an in-line
        /// retry (RRT self-heals on the next save, when IsStillLive sees the embed gone and routes to Save).
        /// Structure-mismatch is a genuine user-facing "reload and re-apply", never a silent fallback.
        /// Never does a whole-buffer replace (that silently no-ops on PWEE embed regions and would clobber the
        /// read-only generated lines) — always per changed slot. UI thread only.
        /// </summary>
        public static string SaveLive(string procName, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> currentSlotTexts, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(procName))
                return "Save unavailable: this view isn't bound to a procedure.";
            if (ranges == null || originalSlotTexts == null || currentSlotTexts == null)
                return "Save aborted: missing slot data.";
            if (currentSlotTexts.Count != ranges.Count || originalSlotTexts.Count != ranges.Count)
                return "Save aborted: slot count mismatch (Monaco " + currentSlotTexts.Count +
                       ", original " + originalSlotTexts.Count + ", ranges " + ranges.Count + ").";

            var appTree = new AppTreeService();
            // The live embed must still be open. If not, we're not actually live — signal fallback to Save().
            if (appTree.GetEmbedInfo() == null)
                return "Save aborted: the live embeditor is no longer open (fall back to re-open save).";

            // Cheap re-read of the OPEN buffer (NO locator typing) + structure match. Held-open under a disabled
            // IDE can't drift, so this virtually always matches; it's a belt-and-suspenders guard, not a re-open.
            string ftitle, fsource, ferr;
            List<int[]> franges;
            if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out ftitle, out fsource, out franges, out ferr))
                return "Save aborted: could not re-read the open embed buffer: " + ferr;
            if (!RangesMatch(franges, ranges))
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Save aborted: '" + procName + "' embed structure changed since it was opened. " +
                       "Reload the tab and re-apply your edits.";
            }

            var changed = new List<int>();
            for (int i = 0; i < ranges.Count; i++)
                if (!NLEqual(currentSlotTexts[i], originalSlotTexts[i]))
                    changed.Add(i);

            try
            {
                // Write changed slots bottom-to-top so earlier slots' line numbers stay valid; verbatim.
                var errors = new List<string>();
                foreach (int i in changed.OrderByDescending(x => ranges[x][0]))
                {
                    string res = appTree.WriteEmbedContentByLine(ranges[i][0], currentSlotTexts[i] ?? "", false);
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errors.Add("  • slot@line " + ranges[i][0] + ": " + res);
                }
                if (errors.Count > 0)
                {
                    try { appTree.CancelEmbeditor(); } catch { } // discard — persist nothing on partial failure
                    return "Save FAILED — nothing persisted:\r\n" + string.Join("\r\n", errors);
                }

                // Save-AND-EXIT: SaveAndCloseEmbeditor persists AND closes the native embed (releasing the lock)
                // even when nothing changed (keeps the lock lifecycle uniform — the caller always closes the tab).
                string saveRes = appTree.SaveAndCloseEmbeditor();
                if (saveRes != null && saveRes.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    return "Save error: " + saveRes;

                if (!ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000))
                    return "Save error: '" + procName + "' was written but the embeditor did not confirm closed — " +
                           "close it in the IDE before saving again.";

                ok = true;
                return changed.Count == 0
                    ? "No changes to save."
                    : "Saved " + changed.Count + " embed slot(s) to '" + procName + "'.";
            }
            catch (Exception ex)
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Save error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

        /// <summary>
        /// Apply explicit per-slot edits to a procedure in ONE transient open-&gt;write-&gt;save-&gt;close round-trip,
        /// with NO interactive embeditor session left open. Robust for very large procedures where the live PWEE
        /// editor is unstable under repeated interactive driving (this reuses the proven Modern Embeditor save
        /// path: OpenAndMirror -&gt; WriteEmbedContentByLine -&gt; SaveAndCloseEmbeditor -&gt; WaitForEmbedClosed).
        ///
        /// Each edit is (1-based «E:N» slot-start line, COMPLETE replacement code for that slot). Every line is
        /// validated against the freshly-opened embed structure; if ANY line is not a current embed-slot start,
        /// NOTHING is written (the embeditor is cancelled). Writes run bottom-to-top so earlier slots' line
        /// numbers stay valid, verbatim (no re-indent — the caller supplies fully-indented code). UI thread only.
        /// </summary>
        public static string ApplyLineEdits(string procName, IList<KeyValuePair<int, string>> edits, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(procName))
                return "Error: procedure_name is required.";
            if (edits == null || edits.Count == 0)
                return "Error: no edits supplied.";

            var appTree = new AppTreeService();
            // Reliably re-open the correct procedure and mirror its current source + ranges; leaves the
            // embeditor open for us to write into (same entry point the interactive save uses).
            string fsource, openErr;
            List<int[]> franges;
            if (!ModernEmbeditorLauncher.OpenAndMirror(appTree, procName, out fsource, out franges, out openErr))
                return "Apply aborted: " + openErr;

            try
            {
                // Valid write targets = the slot-START lines of the freshly-opened structure.
                var slotStarts = new HashSet<int>();
                if (franges != null)
                    foreach (var r in franges)
                        if (r != null && r.Length >= 1) slotStarts.Add(r[0]);

                // Validate ALL edits BEFORE writing anything (all-or-nothing).
                foreach (var e in edits)
                {
                    if (e.Key <= 0 || !slotStarts.Contains(e.Key))
                    {
                        try { appTree.CancelEmbeditor(); } catch { }
                        return "Apply aborted: line " + e.Key + " is not a current embed-slot start in '" +
                               procName + "'. Re-read with get_embeditor_source and retry. Nothing was written.";
                    }
                }

                // Write changed slots bottom-to-top so earlier slots' line numbers stay valid; verbatim.
                var errors = new List<string>();
                foreach (var e in edits.OrderByDescending(x => x.Key))
                {
                    string res = appTree.WriteEmbedContentByLine(e.Key, e.Value ?? "", false);
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errors.Add("  • slot@line " + e.Key + ": " + res);
                }

                if (errors.Count > 0)
                {
                    try { appTree.CancelEmbeditor(); } catch { } // discard — persist nothing on partial failure
                    return "Apply FAILED — nothing persisted:\r\n" + string.Join("\r\n", errors);
                }

                string saveRes = appTree.SaveAndCloseEmbeditor();
                if (saveRes != null && saveRes.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    return "Apply error: " + saveRes;

                if (!ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000))
                    return "Apply error: '" + procName + "' was written but the embeditor did not confirm closed — " +
                           "close it in the IDE before applying again.";

                ok = true;
                return "Applied " + edits.Count + " embed edit(s) to '" + procName + "'.";
            }
            catch (Exception ex)
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Apply error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

        private static bool RangesMatch(List<int[]> a, List<int[]> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] == null || b[i] == null || a[i][0] != b[i][0] || a[i][1] != b[i][1]) return false;
            return true;
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
