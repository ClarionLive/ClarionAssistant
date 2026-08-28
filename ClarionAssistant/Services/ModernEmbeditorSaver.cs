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
        public static string SyncLive(string procName, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> currentSlotTexts, out bool ok)
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

            // Same structure guard as SaveLive: writing against stale ranges would corrupt the embed.
            string ftitle, fsource, ferr;
            List<int[]> franges;
            if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out ftitle, out fsource, out franges, out ferr))
                return "Sync skipped: could not re-read the open embed buffer: " + ferr;
            if (!RangesMatch(franges, ranges))
                return "Sync skipped: embed structure changed since it was opened.";

            var changed = new List<int>();
            for (int i = 0; i < ranges.Count; i++)
                if (!NLEqual(currentSlotTexts[i], originalSlotTexts[i])) changed.Add(i);
            if (changed.Count == 0) { ok = true; return "Nothing to sync."; }

            try
            {
                var errors = new List<string>();
                // Bottom-to-top so earlier slots' line numbers stay valid; verbatim, no re-indent.
                foreach (int i in changed.OrderByDescending(x => ranges[x][0]))
                {
                    string res = appTree.WriteEmbedContentByLine(ranges[i][0], currentSlotTexts[i] ?? "", false);
                    if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                        errors.Add("  • slot@line " + ranges[i][0] + ": " + res);
                }
                if (errors.Count > 0)
                    return "Sync FAILED (embed left open, nothing discarded):\r\n" + string.Join("\r\n", errors);

                ok = true;
                return "Synced " + changed.Count + " slot(s) into the live embed.";
            }
            catch (Exception ex)
            {
                return "Sync error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

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
        /// validated against the mirrored embed structure; if ANY line is not a current embed-slot start,
        /// NOTHING is written. Writes run bottom-to-top so earlier slots' line numbers stay valid, verbatim
        /// (no re-indent — the caller supplies fully-indented code). UI thread only.
        ///
        /// ADOPTION: when an embeditor is already open on this SAME procedure we write into it rather than
        /// refuse (see <see cref="ModernEmbeditorLauncher.TryAdoptOpenEmbeditor"/>) — on a large procedure the
        /// fresh open is the step that fails, so that editor is often the only working handle. Two consequences,
        /// both deliberate:
        /// <list type="bullet">
        /// <item>the save still closes the tab, because <c>SaveAndCloseEmbeditor</c> is the only persist path the
        /// IDE exposes;</item>
        /// <item>an ADOPTED editor is never cancelled on a failure. Cancel would silently discard whatever the
        /// developer had unsaved in that buffer, and since nothing is persisted on any failure path anyway, the
        /// atomicity guarantee holds without it. We leave the buffer on screen and say so instead.</item>
        /// </list>
        /// An embeditor open on a DIFFERENT procedure is refused and left untouched.
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
            // handle. An editor open on a DIFFERENT procedure is still refused, and left untouched.
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

            // An editor WE opened is ours to cancel on failure; one we adopted is the developer's, and
            // cancelling it would throw away their unsaved buffer. Nothing is persisted on any failure
            // path either way, so skipping the cancel costs no atomicity.
            Action cancelIfOurs = () =>
            {
                if (!adopted) { try { appTree.CancelEmbeditor(); } catch { } }
            };
            // Two adoption notes, because the two failure stages leave the buffer in different states.
            string adoptedCleanNote = adopted
                ? " Your open embeditor on '" + procName + "' is untouched and still open."
                : "";
            string adoptedDirtyNote = adopted
                ? " NOTE: your open embeditor on '" + procName + "' now holds partially-written slots in its " +
                  "BUFFER — nothing was saved, so undo or cancel it in the IDE rather than saving it."
                : "";

            try
            {
                // Valid write targets = the slot-START lines of the mirrored structure.
                var slotStarts = new HashSet<int>();
                if (franges != null)
                    foreach (var r in franges)
                        if (r != null && r.Length >= 1) slotStarts.Add(r[0]);

                // Validate ALL edits BEFORE writing anything (all-or-nothing).
                foreach (var e in edits)
                {
                    if (e.Key <= 0 || !slotStarts.Contains(e.Key))
                    {
                        cancelIfOurs();
                        return "Apply aborted: line " + e.Key + " is not a current embed-slot start in '" +
                               procName + "'. Re-read with get_embeditor_source and retry. Nothing was written." +
                               adoptedCleanNote;
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
                    cancelIfOurs(); // discard — persist nothing on partial failure
                    return "Apply FAILED — nothing persisted:\r\n" + string.Join("\r\n", errors) + adoptedDirtyNote;
                }

                string saveRes = appTree.SaveAndCloseEmbeditor();
                if (saveRes != null && saveRes.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    return "Apply error: " + saveRes;

                if (!ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000))
                    return "Apply error: '" + procName + "' was written but the embeditor did not confirm closed — " +
                           "close it in the IDE before applying again.";

                ok = true;
                return "Applied " + edits.Count + " embed edit(s) to '" + procName + "'." + (adopted
                    ? " Adopted the embeditor you already had open on it; the save closed that tab (the IDE has " +
                      "no save-without-close) — re-open it if you were still working there."
                    : "");
            }
            catch (Exception ex)
            {
                cancelIfOurs();
                return "Apply error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message) +
                       adoptedDirtyNote;
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
