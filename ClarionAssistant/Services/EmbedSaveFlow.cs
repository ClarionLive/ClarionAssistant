using System;
using System.Collections.Generic;
using System.Linq;

namespace ClarionAssistant.Services
{
    /// <summary>What <see cref="EmbedSavePlanner.Plan"/> decided: the slot writes to make, or why the save
    /// cannot go through safely.</summary>
    public sealed class EmbedSavePlan
    {
        /// <summary>Null when the save can go ahead; otherwise the reason it cannot.</summary>
        public string Refusal;
        /// <summary>(native slot start line, complete slot text), bottom-to-top so each write leaves the
        /// earlier lines valid. Slots the native buffer already holds verbatim are left out.</summary>
        public List<KeyValuePair<int, string>> Writes = new List<KeyValuePair<int, string>>();
        /// <summary>The slot index of each entry in <see cref="Writes"/>, same order.</summary>
        public List<int> WriteSlots = new List<int>();
        /// <summary>Slots the developer changed in Monaco (whether or not a write was needed).</summary>
        public int Changed;
        /// <summary>Slots the developer did NOT change that hold different text natively — changed outside the
        /// CA Embeditor. They are persisted along with everything else, because SaveAndCloseEmbeditor saves
        /// the whole native buffer (John's D1: save, and say so).</summary>
        public int Foreign;
        /// <summary>True when the native ranges had moved and the slots were matched by identity.</summary>
        public bool Remapped;
        public bool CanSave { get { return Refusal == null; } }
    }

    /// <summary>
    /// Decides where each CA Embeditor slot belongs in the native embed buffer AS IT IS NOW (1565ef7b).
    ///
    /// THE BUG THIS REPLACES. The save compared the native slot ranges with the open-time snapshot and refused
    /// on any difference, by line number. Any multi-line write anywhere in the procedure moves every later
    /// slot, so the save refused, and the overlay path had already torn the editor down: the developer's
    /// text was lost.
    ///
    /// SLOT IDENTITY IS THE READ-ONLY SKELETON, NOT THE LINE NUMBER. Slots only move when the editable text
    /// inside them changes size. The generated, read-only lines around them stay put. So when the slot count is
    /// equal and every read-only gap (before the first slot, between consecutive slots, after the last)
    /// is identical, slot i is slot i at its new lines, with no guessing involved. Anything else
    /// (a regenerated procedure, an added or removed embed) is ambiguous and refused.
    ///
    /// PER-SLOT, the native text must be one we can account for: the open-time text (untouched), the
    /// developer's text (already written — a retry after a partial write is idempotent), or the last
    /// SyncLive push (<c>nativeAlt</c>, the Ctrl+F4 prompt path). A slot the developer changed that holds
    /// anything else was changed in both places: refused, never overwritten. A slot the developer did NOT
    /// change that holds anything else is foreign: left alone, and reported.
    ///
    /// Line walks only (TextLines.LineBounds + ordinal compares): no Split of a multi-megabyte buffer,
    /// which is exactly what runs a 32-bit Clarion out of address space (1d8d1c49). Pure BCL so
    /// tests\EmbedSaveFlow.Test.cs compiles it without an IDE.
    /// </summary>
    public static class EmbedSavePlanner
    {
        public static EmbedSavePlan Plan(string originalSource, List<int[]> originalRanges,
            IList<string> originalSlotTexts, IList<string> currentSlotTexts, IList<string> nativeAlt,
            string freshSource, List<int[]> freshRanges)
        {
            var plan = new EmbedSavePlan();
            if (originalRanges == null || originalSlotTexts == null || currentSlotTexts == null || freshRanges == null)
            { plan.Refusal = "missing slot data."; return plan; }
            if (currentSlotTexts.Count != originalRanges.Count || originalSlotTexts.Count != originalRanges.Count)
            {
                plan.Refusal = "slot count mismatch (CA Embeditor " + currentSlotTexts.Count + ", original " +
                    originalSlotTexts.Count + ", ranges " + originalRanges.Count + ").";
                return plan;
            }
            if (nativeAlt != null && nativeAlt.Count != originalRanges.Count) nativeAlt = null;   // stale push: ignore

            if (freshRanges.Count != originalRanges.Count)
            {
                plan.Refusal = "the procedure's embed structure changed since it was opened (" + originalRanges.Count +
                    " editable regions then, " + freshRanges.Count + " now).";
                return plan;
            }
            // Identity comes from the skeleton, ALWAYS — equal coordinates are not proof on their own: a regenerated
            // procedure can put a different embed at the same lines (pipeline Run 1, cross-model adversary). Without
            // the open-time source only the old line-number rule is available, so moved slots are refused then.
            bool moved = !RangesEqual(originalRanges, freshRanges);
            if (originalSource != null || moved)
            {
                string why = SkeletonDiff(originalSource, originalRanges, freshSource, freshRanges);
                if (why != null)
                {
                    plan.Refusal = "the procedure's generated code changed since it was opened (" + why + "), so the " +
                        "embed slots can't be matched safely.";
                    return plan;
                }
            }
            plan.Remapped = moved;

            var fresh = TextLines.ExtractRanges(freshSource ?? "", freshRanges);
            var writes = new List<KeyValuePair<int, string>>();
            var slots = new List<int>();
            for (int i = 0; i < originalRanges.Count; i++)
            {
                string orig = originalSlotTexts[i], cur = currentSlotTexts[i], nat = fresh[i];
                string alt = nativeAlt != null ? nativeAlt[i] : null;
                bool devChanged = !NLEqual(cur, orig);
                if (devChanged) plan.Changed++;

                if (NLEqual(nat, cur)) continue;   // the native buffer already holds the developer's text
                bool accountedFor = NLEqual(nat, orig) || (alt != null && NLEqual(nat, alt));
                if (!accountedFor)
                {
                    if (devChanged)
                    {
                        plan.Refusal = "the embed slot at line " + freshRanges[i][0] + " was also changed outside the " +
                            "CA Embeditor since you opened it, so saving would overwrite that change.";
                        return plan;
                    }
                    plan.Foreign++;   // not ours: leave it alone (it is saved with the buffer)
                    continue;
                }
                writes.Add(new KeyValuePair<int, string>(freshRanges[i][0], cur ?? ""));
                slots.Add(i);
            }
            var order = Enumerable.Range(0, writes.Count).OrderByDescending(k => writes[k].Key).ToList();
            plan.Writes = order.Select(k => writes[k]).ToList();
            plan.WriteSlots = order.Select(k => slots[k]).ToList();
            return plan;
        }

        /// <summary>
        /// Perform a plan's writes in order through <paramref name="writeSlot"/> (WriteEmbedContentByLine), stopping
        /// at the first "Error"-prefixed result or throw. Every slot whose write SUCCEEDED is added to
        /// <paramref name="written"/>, failure or not — the caller records them as native text that is ours
        /// (<c>nativeAlt</c>), so a save that failed partway and was followed by more typing is not later mistaken
        /// for a change made outside the CA Embeditor (pipeline Run 1, debugger). Returns null, or what failed.
        /// </summary>
        public static string WriteAll(EmbedSavePlan plan, Func<int, string, string> writeSlot, List<int> written)
        {
            for (int k = 0; k < plan.Writes.Count; k++)
            {
                var w = plan.Writes[k];
                string res;
                try { res = writeSlot(w.Key, w.Value); }
                catch (Exception ex) { return "the embed slot at line " + w.Key + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
                if (res != null && res.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    return "the embed slot at line " + w.Key + ": " + res;
                if (written != null && k < plan.WriteSlots.Count) written.Add(plan.WriteSlots[k]);
            }
            return null;
        }

        /// <summary>The last known native text per slot after some of them were written: <paramref name="known"/>
        /// (or the open-time baseline) with each slot in <paramref name="written"/> set to the text written. Null when
        /// nothing was written.</summary>
        public static List<string> RecordWrites(IList<string> known, IList<string> baseline, IList<string> current, IList<int> written)
        {
            if (written == null || written.Count == 0 || current == null) return known != null ? new List<string>(known) : null;
            var src = known != null && known.Count == current.Count ? known : baseline;
            if (src == null || src.Count != current.Count) return null;
            var rec = new List<string>(src);
            foreach (int i in written) if (i >= 0 && i < rec.Count) rec[i] = current[i];
            return rec;
        }

        /// <summary>
        /// The slot texts to restore into a re-opened CA Embeditor from an edit stash, or null when it can't be
        /// done safely. The stash was taken against <paramref name="stashOriginal"/>; the re-opened editor's
        /// baseline is <paramref name="freshOriginal"/>. Each slot the developer EDITED must still have the same
        /// baseline (else their edit was made against text that no longer exists: refuse, the recovery file has
        /// it). Slots they did not edit take the fresh baseline, so a change made elsewhere in the meantime is
        /// kept rather than reverted.
        /// </summary>
        public static List<string> MergeStash(IList<string> stashOriginal, IList<string> stashEdited, IList<string> freshOriginal)
        {
            if (stashOriginal == null || stashEdited == null || freshOriginal == null) return null;
            if (stashOriginal.Count != stashEdited.Count || stashOriginal.Count != freshOriginal.Count) return null;
            var merged = new List<string>(freshOriginal.Count);
            for (int i = 0; i < freshOriginal.Count; i++)
            {
                if (NLEqual(stashEdited[i], stashOriginal[i])) { merged.Add(freshOriginal[i]); continue; }
                if (!NLEqual(stashOriginal[i], freshOriginal[i])) return null;
                merged.Add(stashEdited[i]);
            }
            return merged;
        }

        /// <summary>Null when every read-only gap is identical between the two sources; else a short reason.</summary>
        internal static string SkeletonDiff(string a, List<int[]> ra, string b, List<int[]> rb)
        {
            if (a == null || b == null) return "no open-time source to compare";
            int[] aS, aE, bS, bE;
            TextLines.LineBounds(a, out aS, out aE);
            TextLines.LineBounds(b, out bS, out bE);
            var ga = Gaps(ra, aS.Length);
            var gb = Gaps(rb, bS.Length);
            if (ga == null || gb == null) return "editable regions out of order";
            if (ga.Count != gb.Count) return "gap count";
            for (int g = 0; g < ga.Count; g++)
            {
                int la = ga[g][1] - ga[g][0] + 1, lb = gb[g][1] - gb[g][0] + 1;
                if (la < 0) la = 0;
                if (lb < 0) lb = 0;
                if (la != lb) return "read-only block " + (g + 1) + " is " + la + " lines then, " + lb + " now";
                for (int k = 0; k < la; k++)
                {
                    int x = ga[g][0] - 1 + k, y = gb[g][0] - 1 + k;
                    int lenA = aE[x] - aS[x], lenB = bE[y] - bS[y];
                    if (lenA != lenB || string.CompareOrdinal(a, aS[x], b, bS[y], lenA) != 0)
                        return "read-only line " + (ga[g][0] + k) + " differs";
                }
            }
            return null;
        }

        // The 1-based inclusive line spans NOT covered by the ranges (count = ranges + 1; a span may be
        // empty, end = start - 1). Null if the ranges are not ascending and disjoint.
        private static List<int[]> Gaps(List<int[]> ranges, int lineCount)
        {
            var gaps = new List<int[]>();
            int next = 1;
            foreach (var r in ranges)
            {
                if (r == null || r.Length < 2 || r[0] < next || r[1] < r[0]) return null;
                gaps.Add(new[] { next, Math.Min(r[0] - 1, lineCount) });
                next = r[1] + 1;
            }
            gaps.Add(new[] { next, lineCount });
            return gaps;
        }

        private static bool RangesEqual(List<int[]> a, List<int[]> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] == null || b[i] == null || a[i][0] != b[i][0] || a[i][1] != b[i][1]) return false;
            return true;
        }

        internal static bool NLEqual(string x, string y)
        {
            return string.Equals((x ?? "").Replace("\r\n", "\n").Replace("\r", "\n"),
                                 (y ?? "").Replace("\r\n", "\n").Replace("\r", "\n"), StringComparison.Ordinal);
        }
    }

    /// <summary>The IDE operations of the CA Embeditor's live save (seam for tests).</summary>
    public interface IEmbedLiveSaveOps
    {
        /// <summary>The native embeditor is still open (AppTreeService.GetEmbedInfo() != null).</summary>
        bool EmbedOpen();
        /// <summary>Re-read the OPEN native buffer and its editable ranges (no typing, no re-open).</summary>
        bool ReadNative(out string source, out List<int[]> ranges, out string error);
        /// <summary>AppTreeService.WriteEmbedContentByLine, verbatim; "Error"-prefixed on failure.</summary>
        string WriteSlot(int line, string code);
        /// <summary>AppTreeService.SaveAndCloseEmbeditor; "Error"-prefixed on failure.</summary>
        string SaveAndClose();
        /// <summary>ModernEmbeditorLauncher.WaitForEmbedClosed.</summary>
        bool WaitClosed(int timeoutMs);
    }

    public sealed class EmbedLiveSaveOutcome
    {
        public bool Ok;
        public string Message;
        /// <summary>The CA Embeditor surface was never torn down, so the developer's text is still in it.</summary>
        public bool EditorIntact = true;
        /// <summary>The native embed was gone before we started: the caller should use the re-open save.</summary>
        public bool NotLive;
        public EmbedSavePlan Plan;
        /// <summary>Slots whose native write succeeded, even when the save then failed. The caller records them
        /// (<see cref="EmbedSavePlanner.RecordWrites"/>) as native text that is the developer's own.</summary>
        public List<int> WrittenSlots = new List<int>();
    }

    /// <summary>
    /// One CA Embeditor save at a time (pipeline Run 1: Codex security HIGH + adversary). The save pumps
    /// Application.DoEvents, so a second Ctrl+S, or a routed save racing the developer's, could otherwise
    /// re-enter and plan, write or close against the same native embed mid-save. A request arriving while
    /// one runs JOINS it: it is not run separately, and it shares that save's single outcome
    /// (one saveResult, one EmbedSaveFinished). An entry older than <see cref="StaleAfter"/> is presumed
    /// dead (its deferred callback was dropped with a destroyed handle) and no longer blocks.
    /// </summary>
    public sealed class EmbedSaveGate
    {
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private DateTime? _enteredUtc;

        /// <summary>True when this caller may run a save now (and the gate is taken).</summary>
        public bool TryEnter(DateTime nowUtc)
        {
            if (_enteredUtc.HasValue && nowUtc - _enteredUtc.Value < StaleAfter) return false;
            _enteredUtc = nowUtc;
            return true;
        }

        public void Exit() { _enteredUtc = null; }

        public bool Busy(DateTime nowUtc) { return _enteredUtc.HasValue && nowUtc - _enteredUtc.Value < StaleAfter; }
    }

    /// <summary>
    /// The CA Embeditor's live save (ModernEmbeditorSaver.SaveLive), pure over <see cref="IEmbedLiveSaveOps"/>.
    ///
    /// THE ORDERING IS THE FIX (1565ef7b). Everything that can refuse or fail runs BEFORE
    /// <c>beforeClose</c> (the overlay detach), and nothing here ever cancels the native embed:
    ///   re-read → plan → write the slots (overlay still attached, as SyncLive already does) → beforeClose →
    ///   SaveAndCloseEmbeditor → confirm closed.
    /// A refusal or a write failure therefore leaves the CA Embeditor open with the developer's text in it.
    /// A partial write is left in the native buffer, not rolled back: the slots that were written come back in
    /// <see cref="EmbedLiveSaveOutcome.WrittenSlots"/> for the caller to record as ours (nativeAlt), so the next
    /// attempt accepts them even if the developer typed more in the meantime, and Cancel still discards the lot. Only a failure AFTER beforeClose can leave the text outside an open editor,
    /// and the caller writes a recovery file for exactly that case (EditorIntact == false and !Ok).
    /// </summary>
    public static class EmbedLiveSaveFlow
    {
        public static EmbedLiveSaveOutcome SaveLive(IEmbedLiveSaveOps ops, string procName, string originalSource,
            List<int[]> ranges, IList<string> originalSlotTexts, IList<string> currentSlotTexts,
            IList<string> nativeAlt, Action beforeClose)
        {
            var o = new EmbedLiveSaveOutcome();
            const string kept = " Your edits are still in the CA Embeditor.";
            if (string.IsNullOrWhiteSpace(procName)) { o.Message = "Save unavailable: this view isn't bound to a procedure."; return o; }

            if (!ops.EmbedOpen())
            {
                o.NotLive = true;
                o.Message = "Save aborted: the live embeditor is no longer open (fall back to re-open save).";
                return o;
            }

            string fsource, ferr;
            List<int[]> franges;
            if (!ops.ReadNative(out fsource, out franges, out ferr))
            { o.Message = "Save aborted: could not re-read the open embed buffer: " + ferr + "." + kept; return o; }

            var plan = EmbedSavePlanner.Plan(originalSource, ranges, originalSlotTexts, currentSlotTexts, nativeAlt,
                fsource, franges);
            o.Plan = plan;
            if (!plan.CanSave)
            {
                o.Message = "Save refused: " + plan.Refusal + kept + " Copy what you need, or Cancel to discard them.";
                return o;
            }

            string writeErr = EmbedSavePlanner.WriteAll(plan, ops.WriteSlot, o.WrittenSlots);
            if (writeErr != null)
            {
                o.Message = "Save failed writing " + writeErr + "." + kept + " Save again to retry, or Cancel to discard them.";
                return o;
            }

            // Point of no return for the surface: the overlay must be off the host before the native close
            // (a5bbf005 freeze rule). From here a failure is reported with EditorIntact = false.
            if (beforeClose != null)
            {
                o.EditorIntact = false;
                try { beforeClose(); } catch { }
            }

            try
            {
                string saveRes = ops.SaveAndClose();
                if (saveRes != null && saveRes.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                { o.Message = "Save error: " + saveRes; return o; }
                if (!ops.WaitClosed(3000))
                {
                    o.Message = "Save error: '" + procName + "' was written but the embeditor did not confirm closed — " +
                        "close it in the IDE before saving again.";
                    return o;
                }
            }
            catch (Exception ex)
            {
                o.Message = "Save error: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return o;
            }

            o.Ok = true;
            o.Message = (plan.Changed == 0 ? "No changes to save." : "Saved " + plan.Changed + " embed slot(s) to '" + procName + "'.")
                + ForeignNote(plan);
            return o;
        }

        /// <summary>John's D1 wording: name the slots saved that the developer never edited here.</summary>
        public static string ForeignNote(EmbedSavePlan plan)
        {
            return plan == null || plan.Foreign == 0 ? ""
                : " Also saved " + plan.Foreign + " slot(s) changed outside the CA Embeditor.";
        }
    }
}
