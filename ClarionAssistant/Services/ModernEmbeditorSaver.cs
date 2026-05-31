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
            if (!ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 2000))
                return "Save aborted: another embeditor is open — close it and try again.";

            // The Modern Embeditor tab is the active window when the user saves; bring the app tree to
            // the front so OpenProcedureEmbed's native automation can actually open the embeditor.
            appTree.ActivateAppView();
            appTree.OpenProcedureEmbed(procName);
            if (!ModernEmbeditorLauncher.WaitForEmbedOpen(appTree, 45000))
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Save aborted: couldn't open the embeditor for '" + procName + "'.";
            }

            try
            {
                // Re-derive the fresh structure and verify it matches the snapshot.
                string ft, fsource, ferr;
                List<int[]> franges;
                if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out ft, out fsource, out franges, out ferr))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    return "Save aborted: couldn't read the re-opened embeditor (" + ferr + ").";
                }

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
                ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000);
                if (saveRes != null && saveRes.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    return "Save error: " + saveRes;

                ok = true;
                return "Saved " + changed.Count + " embed slot(s) to '" + procName + "'.";
            }
            catch (Exception ex)
            {
                try { appTree.CancelEmbeditor(); } catch { }
                return "Save error: " + (ex.InnerException?.Message ?? ex.Message);
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
