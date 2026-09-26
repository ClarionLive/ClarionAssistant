using System;
using System.Collections.Generic;
using System.Linq;

namespace ClarionAssistant.Services
{
    /// <summary>The four IDE operations apply_embed_edits performs once an embeditor is open (seam for tests).</summary>
    public interface IEmbedApplyOps
    {
        /// <summary>Write one slot verbatim (AppTreeService.WriteEmbedContentByLine, no re-indent).</summary>
        string WriteSlot(int line, string code);
        /// <summary>AppTreeService.SaveAndCloseEmbeditor - "Error"-prefixed on every failure.</summary>
        string SaveAndClose();
        /// <summary>ModernEmbeditorLauncher.WaitForEmbedClosed.</summary>
        bool WaitClosed(int timeoutMs);
        /// <summary>AppTreeService.CancelEmbeditor - discard the buffer's unsaved changes and close. Never throws.</summary>
        void Discard();
    }

    /// <summary>
    /// The write -> commit -> save -> confirm-closed half of ModernEmbeditorSaver.ApplyLineEdits, after the
    /// embeditor has been opened (or adopted) and mirrored. Pure over <see cref="IEmbedApplyOps"/> so
    /// tests\EmbedApplyFlow.Test.cs can drive every exit without an IDE.
    ///
    /// ROLLBACK RULE. An adopted editor is only ever adopted CLEAN (EmbedAdoptPolicy), so once we have written,
    /// its buffer holds saved code plus our writes and nothing else. Every exit after the first write that does
    /// not end in a confirmed save therefore Discards - for an adopted editor exactly as for one we opened - so
    /// no half-written, dirty tab is left for the developer to save by accident. Before the first write, an
    /// editor we opened is closed; an adopted one is left exactly as it was.
    ///
    /// ABANDON RULE (PR #198 review). When McpDispatcher gives up waiting it abandons the call's
    /// <see cref="McpCallToken"/>. The save is claimed with TryCommit first: if the call was abandoned, we
    /// roll back instead of saving, so the caller's "timed out" is never followed by a silent save that
    /// shifts the line numbers its retry will use.
    /// </summary>
    public static class EmbedApplyFlow
    {
        public static string Apply(IEmbedApplyOps ops, string procName, List<int[]> ranges,
            IList<KeyValuePair<int, string>> edits, bool adopted, McpCallToken token, out bool ok)
        {
            ok = false;
            bool wrote = false;

            Action closeIfOurs = () => { if (!adopted) ops.Discard(); };
            string untouchedNote = adopted
                ? " Your open embeditor on '" + procName + "' is untouched and still open."
                : "";
            string discardNote = adopted
                ? " The embeditor you had open on '" + procName + "' had no unsaved changes; it was closed " +
                  "without saving our edits - re-open it if you were still working there."
                : "";

            try
            {
                if (token != null && token.IsAbandoned)
                {
                    closeIfOurs();
                    return "Apply cancelled: the MCP call timed out before anything was written. Nothing was " +
                           "written." + untouchedNote;
                }

                // Valid write targets = the slot-START lines of the mirrored structure.
                var slotStarts = new HashSet<int>();
                if (ranges != null)
                    foreach (var r in ranges)
                        if (r != null && r.Length >= 1) slotStarts.Add(r[0]);

                // Validate ALL edits BEFORE writing anything (all-or-nothing).
                foreach (var e in edits)
                {
                    if (e.Key <= 0 || !slotStarts.Contains(e.Key))
                    {
                        closeIfOurs();
                        return "Apply aborted: line " + e.Key + " is not a current embed-slot start in '" +
                               procName + "'. Re-read with get_embeditor_source and retry. Nothing was written." +
                               untouchedNote;
                    }
                }

                // Write bottom-to-top so earlier slots' line numbers stay valid; verbatim.
                var errors = new List<string>();
                foreach (var e in edits.OrderByDescending(x => x.Key))
                {
                    wrote = true;
                    string res = ops.WriteSlot(e.Key, e.Value ?? "");
                    if (IsError(res))
                        errors.Add("  • slot@line " + e.Key + ": " + res);
                }

                if (errors.Count > 0)
                {
                    ops.Discard(); // persist nothing on partial failure
                    return "Apply FAILED — nothing persisted:\r\n" + string.Join("\r\n", errors) + discardNote;
                }

                // COMMIT POINT: past here the dispatcher can no longer abandon us.
                if (token != null && !token.TryCommit())
                {
                    ops.Discard();
                    return "Apply cancelled: the MCP call timed out before saving, so the edits were rolled back " +
                           "and nothing was persisted. Re-read with get_embeditor_source before retrying." + discardNote;
                }

                string saveRes = ops.SaveAndClose();
                if (IsError(saveRes))
                {
                    // Whatever is still unsaved is ours: drop it and close, rather than leave a dirty tab open.
                    ops.Discard();
                    return "Apply error: " + saveRes + " Any of these edits still unsaved were discarded and the " +
                           "embeditor closed; if the error above says the changes WERE saved, they stand. Re-read " +
                           "with get_embeditor_source before retrying.";
                }

                if (!ops.WaitClosed(3000))
                {
                    ops.Discard(); // saved already - this only closes it
                    return "Apply error: '" + procName + "' was saved but the embeditor did not confirm closed; " +
                           "tried to close it without further changes. Check the IDE before applying again.";
                }

                ok = true;
                return "Applied " + edits.Count + " embed edit(s) to '" + procName + "'." + (adopted
                    ? " Adopted the embeditor you already had open on it; the save closed that tab (the IDE has " +
                      "no save-without-close) — re-open it if you were still working there."
                    : "");
            }
            catch (Exception ex)
            {
                if (wrote) ops.Discard(); else closeIfOurs();
                return "Apply error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message) +
                       (wrote ? discardNote : untouchedNote);
            }
        }

        private static bool IsError(string res)
        {
            return res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        }
    }
}
