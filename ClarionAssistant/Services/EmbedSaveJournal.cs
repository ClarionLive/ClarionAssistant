using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The text of a CA Embeditor save, kept on disk until we can SEE that it landed (GH #179, 254e4353).
    ///
    /// <c>SaveLive ok=True</c> means the text was handed to Clarion, not that it was persisted: on large procedures
    /// Clarion's Generator (Clagen.dll) can access-violate ~60 ms later, after even <c>refreshPadSources DONE</c>,
    /// and the IDE's UI dies with the edit. <see cref="EmbedRecovery"/> only writes when a save FAILS, so on that
    /// path nothing was kept. This journal is written BEFORE the native save round-trip and is NOT retired on
    /// ok=True. It is retired by evidence instead: on the next CA Embeditor open of that procedure, the freshly
    /// loaded slots either hold the saved text (it persisted: retire silently, once the .app was written after it),
    /// are back at their pre-save text
    /// (the save was lost: offer it), or hold something else (changed since: archive it as a dated recovery file).
    /// A timer or "the save sequence finished" would prove nothing: the reporter's fault lands after the sequence
    /// ends, and the zombie process that follows still consolidates the .app, without the embed.
    ///
    /// One file per app + procedure under embed-recovery\pending, so a new save of the same procedure replaces
    /// the previous one rather than piling up. It holds only the slots the save changed (both their pre-save and
    /// saved text), so a 330k-char embed with one edited slot writes a small file. UTF-8 WITHOUT a BOM, like
    /// EmbedRecovery. Written to a temp file in the same folder, then swapped in (File.Replace / File.Move), so a
    /// crash mid-write never leaves a torn file under the final name; the reader also requires the END trailer
    /// and exact lengths, so a damaged file is ignored, never offered.
    /// </summary>
    public static class EmbedSaveJournal
    {
        public enum Verdict
        {
            /// <summary>Every slot the save changed now holds the saved text: it landed. Retire silently.</summary>
            Persisted,
            /// <summary>Every slot the save changed is back at its pre-save text: the save was lost. Offer it.</summary>
            Lost,
            /// <summary>Neither: the embed changed since (or its slot count did). From an earlier session: offered
            /// (the app may have been reverted past the save); from this session: a dated copy, not offered.</summary>
            Diverged
        }

        public sealed class Entry
        {
            public string Path;
            public string App;
            public string Proc;
            /// <summary>When the save was journaled. UTC from <see cref="Begin"/> and <see cref="Parse"/> (ruling F2-3);
            /// a value of another Kind (a hand-built entry) is read as local time, never as UTC.</summary>
            public DateTime Written;
            public int Pid;
            public int SlotCount;
            public readonly List<int> Index = new List<int>();          // the changed slots, 0-based
            public readonly List<string> Baseline = new List<string>();  // their text before the save
            public readonly List<string> Saved = new List<string>();     // the text the save handed to Clarion
        }

        private const string Magic = "CA-EMBED-PENDING-SAVE 1";

        /// <summary>The most slots an entry may claim. A file saying more (or fewer than 0) is damaged or hostile:
        /// <see cref="Parse"/> rejects it, so nothing is ever allocated from it. The largest real embed list seen
        /// (InventoryTable, 86k lines) has a few hundred slots.</summary>
        public const int MaxSlots = 100000;

        /// <summary>UTC, and a Kind other than Utc read as local (a legacy entry, or a value from DateTime.Now).</summary>
        internal static DateTime ToUtc(DateTime d)
        {
            return d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime();
        }

        /// <summary>Local time for display; a non-UTC value is already local.</summary>
        internal static DateTime ToLocal(DateTime d)
        {
            return d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;
        }

        private static string Shown(DateTime d) { return ToLocal(d).ToString("yyyy-MM-dd HH:mm"); }

        /// <summary>Overridable for tests; null means EmbedRecovery's folder + "\pending".</summary>
        public static string FolderOverride;

        public static string Folder
        {
            get { return FolderOverride ?? System.IO.Path.Combine(EmbedRecovery.Folder, "pending"); }
        }

        /// <summary>The journal file for this app + procedure (it need not exist).</summary>
        public static string PathFor(string app, string proc)
        {
            return System.IO.Path.Combine(Folder, SafeName(proc) + "-" + AppHash(app) + ".pending");
        }

        /// <summary>
        /// Record a save that is about to be handed to Clarion. Returns the journal path, or null when no slot
        /// differs from <paramref name="baseline"/> (nothing to protect; any earlier entry is left alone).
        /// If an earlier entry for this procedure is still there and the embed does NOT show it landed (it was
        /// offered and left, or the embed changed since), it is archived as a dated recovery file first, so a new
        /// save never silently replaces the only copy of an older one; <paramref name="archived"/> names it.
        /// Throws on an I/O failure: the caller logs it and saves anyway (refusing to save would be worse).
        ///
        /// An earlier entry that the embed DOES show (Persisted) is not simply replaced unless the .app was written
        /// after it (G1, live test 2026-10-19): until then its text may live only in Clarion's memory (this session,
        /// or Clarion's Recover), and replacing it with a journal of only the newer save's slots lost it when Clarion
        /// stopped again. So its changed slots that the newer save does not touch are CARRIED into the new entry
        /// (<see cref="CarryUnsaved"/>), with their own pre-save and saved text. One file per procedure stays the
        /// rule, and every open-time path (Assess, PlanRestore, the offer) sees one entry covering both saves. The
        /// .app's write time is read from <paramref name="app"/> itself (unknown = carried).
        /// </summary>
        public static string Begin(string app, string proc, IList<string> baseline, IList<string> saved, out string archived)
        {
            return Begin(app, proc, baseline, saved, AppLastWrite(app), out archived);
        }

        /// <param name="appLastWrite">When the .app on disk was last written (UTC), or null when unknown.</param>
        public static string Begin(string app, string proc, IList<string> baseline, IList<string> saved, DateTime? appLastWrite,
            out string archived)
        {
            archived = null;
            // No app, no journal (pipeline run 1): an entry filed under a guessed app is read back by the wrong one.
            if (string.IsNullOrWhiteSpace(app)) return null;
            if (baseline == null || saved == null || baseline.Count != saved.Count) return null;
            if (saved.Count > MaxSlots) return null;   // Parse would refuse it: never write a journal that cannot be read back
            var e = new Entry { App = app ?? "", Proc = proc ?? "", Written = DateTime.UtcNow, SlotCount = saved.Count };
            try { e.Pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            for (int i = 0; i < saved.Count; i++)
            {
                if (EmbedSavePlanner.NLEqual(baseline[i], saved[i])) continue;
                e.Index.Add(i); e.Baseline.Add(baseline[i] ?? ""); e.Saved.Add(saved[i] ?? "");
            }
            if (e.Index.Count == 0) return null;

            var old = Read(app, proc);
            if (old != null)
            {
                if (Assess(old, baseline) != Verdict.Persisted)
                    archived = Archive(old, "an earlier save of it was never confirmed, and a newer save replaced it");
                else if (!AppWrittenSince(old, appLastWrite))
                    CarryUnsaved(old, baseline, e);   // shown, but maybe only in memory: never dropped (G1)
            }

            string target = PathFor(app, proc);
            WriteAtomic(target, Serialize(e));
            return target;
        }

        /// <summary>The .app's last write time in UTC, or null when it cannot be read.</summary>
        internal static DateTime? AppLastWrite(string app)
        {
            try { return !string.IsNullOrWhiteSpace(app) && File.Exists(app) ? File.GetLastWriteTimeUtc(app) : (DateTime?)null; }
            catch { return null; }
        }

        /// <summary>
        /// G1: add to <paramref name="e"/> (the newer save) each changed slot of <paramref name="old"/> (an entry the
        /// newer save's baseline shows as Persisted) that <paramref name="e"/> does not already cover, with OLD's
        /// pre-save text as its baseline and OLD's saved text as its saved text. That is what the .app on disk may
        /// still hold, and what the slot shows if Clarion stops before the .app is saved, so a later open reads it
        /// as Lost there and offers it back. A slot the newer save changed again keeps the newer save's own texts
        /// (its baseline is what the procedure showed). Positions: the old entry's own while the slot count is
        /// unchanged, else where <see cref="LandedAt"/> proved the old text is (Persisted needs that proof).
        /// Whitespace-only slots are not carried. The result is in slot order.
        /// </summary>
        internal static void CarryUnsaved(Entry old, IList<string> baseline, Entry e)
        {
            if (old == null || baseline == null || e == null) return;
            int[] at = old.SlotCount == baseline.Count ? null : LandedAt(old, baseline);
            var rows = new List<KeyValuePair<int, string[]>>();
            var taken = new HashSet<int>();
            for (int k = 0; k < e.Index.Count; k++)
            {
                rows.Add(new KeyValuePair<int, string[]>(e.Index[k], new[] { e.Baseline[k], e.Saved[k] }));
                taken.Add(e.Index[k]);
            }
            int carried = 0;
            for (int k = 0; k < old.Index.Count; k++)
            {
                if (WhitespaceOnly(old, k)) continue;
                int i = at == null ? old.Index[k] : at[k];
                if (i < 0 || i >= e.SlotCount || !taken.Add(i)) continue;
                rows.Add(new KeyValuePair<int, string[]>(i, new[] { old.Baseline[k] ?? "", old.Saved[k] ?? "" }));
                carried++;
            }
            if (carried == 0) return;
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            e.Index.Clear(); e.Baseline.Clear(); e.Saved.Clear();
            foreach (var r in rows) { e.Index.Add(r.Key); e.Baseline.Add(r.Value[0]); e.Saved.Add(r.Value[1]); }
        }

        /// <summary>The journal entry for this app + procedure, or null when there is none or it is damaged
        /// (torn, truncated, wrong format, or for a different app/procedure): such a file is never offered.</summary>
        public static Entry Read(string app, string proc)
        {
            if (string.IsNullOrWhiteSpace(app)) return null;
            string path = PathFor(app, proc);
            try
            {
                if (!File.Exists(path)) return null;
                var e = Parse(File.ReadAllText(path, new UTF8Encoding(false)));
                if (e == null) return null;
                if (!string.Equals(e.App, app ?? "", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(e.Proc, proc ?? "", StringComparison.OrdinalIgnoreCase)) return null;
                e.Path = path;
                return e;
            }
            catch { return null; }
        }

        /// <summary>Delete the journal for this app + procedure. Never throws; true when nothing is left.</summary>
        public static bool Retire(string app, string proc)
        {
            if (string.IsNullOrWhiteSpace(app)) return true;   // never journaled without an app
            try
            {
                string path = PathFor(app, proc);
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>What to do with a journal entry when its procedure opens (pipeline run 1).</summary>
        public enum OpenDecision
        {
            /// <summary>It landed, was read back by a later process, AND the .app was written after it (G1): delete.</summary>
            Retire,
            /// <summary>It landed, but possibly only in an in-memory .app (this session's, or one Clarion's Recover
            /// rebuilt after a crash, not saved since), which a crash can still lose: keep it, say nothing. A later
            /// open decides.</summary>
            Keep,
            /// <summary>It did not land: offer it back.</summary>
            Offer,
            /// <summary>The embed changed since, in THIS session: keep a dated copy, do not offer.</summary>
            Archive
        }

        /// <summary>
        /// The open-time decision. A reopen in the SAME process showing the saved text proves only that Clarion
        /// took it into the app in memory; the .app on disk may still lack it (pipeline run 1, adversary), so that
        /// entry is kept. No IDE app-saved notification is hooked, so "a different process read it back" is the
        /// proof that it reached the saved .app. An entry that did not land in a later process means Clarion
        /// stopped before the app was saved, or the app was closed without saving: both are offered.
        ///
        /// A Lost entry is OFFERED even when the .app was written after it (PM ruling, pipeline run 2). That is
        /// exactly the #179 reporter's sequence: the crashed process lingered and consolidated the .app, without
        /// the embed, minutes after the save. A deliberate revert (git checkout, import_txa, a native edit) leaves
        /// the same trace, and no timestamp tells the two apart, so the offer is made in both cases and its
        /// wording says so (<see cref="AppWrittenSince"/>). Restore is never automatic, and the developer decides.
        ///
        /// A Diverged entry (the saved text is not there, and neither is the pre-save text) from an EARLIER session
        /// is OFFERED too (PM ruling, live test 2026-10-19). Clarion's "Last Saved" on a crash reopen reverts the
        /// procedure further back than the save's own baseline (to the last saved .app), so the reopened embed
        /// matches neither text; archiving that silently left the only copy of the work in a .txt nobody knew
        /// about, which is exactly the #179 loss. Only an entry from THIS session is archived silently: there the
        /// divergence is the developer's own later edit (in Clarion's native embeditor), not a loss.
        /// </summary>
        public static OpenDecision Decide(Entry e, IList<string> fresh, int currentPid)
        {
            return Decide(e, fresh, currentPid, null);
        }

        /// <param name="currentProcessStart">When the current process started (UTC; a non-UTC value is read as local),
        /// or null when unknown.
        /// With it, an entry is from this session only when its pid matches AND it was written after this process
        /// started, so a recycled pid is not mistaken for this session.</param>
        public static OpenDecision Decide(Entry e, IList<string> fresh, int currentPid, DateTime? currentProcessStart)
        {
            return Decide(e, fresh, currentPid, currentProcessStart, null);
        }

        /// <param name="appLastWrite">When the .app on disk was last written (UTC; a non-UTC value is read as local),
        /// or null when unknown.
        ///
        /// Live test 2026-10-19 (G1, PM ruling): an entry from an EARLIER session that reads as Persisted is retired
        /// only when the .app was written AFTER the entry (<see cref="AppWrittenSince"/>). Clarion's "Recover" brings
        /// the saved text back into the app in MEMORY: the open shows it, but the .app on disk does not have it until
        /// the developer saves. Retiring then left nothing when Clarion stopped again and "Last Saved" was chosen.
        /// So such an entry is KEPT (an unknown .app time keeps it too), a later open after the .app was saved retires
        /// it, and a later open that finds the pre-save text again offers it (Lost). The same-session rule is
        /// unchanged: Persisted in this session is always Keep. <see cref="KeepReason"/> says which applies.</param>
        public static OpenDecision Decide(Entry e, IList<string> fresh, int currentPid, DateTime? currentProcessStart,
            DateTime? appLastWrite)
        {
            bool sameSession = SameSession(e, currentPid, currentProcessStart);
            switch (Assess(e, fresh))
            {
                case Verdict.Persisted:
                    return sameSession || !AppWrittenSince(e, appLastWrite) ? OpenDecision.Keep : OpenDecision.Retire;
                case Verdict.Lost: return OpenDecision.Offer;
                default: return sameSession ? OpenDecision.Archive : OpenDecision.Offer;
            }
        }

        /// <summary>Why <see cref="Decide(Entry, IList{string}, int, DateTime?, DateTime?)"/> keeps a Persisted entry,
        /// for the log line (G1).</summary>
        public static string KeepReason(Entry e, int currentPid, DateTime? currentProcessStart, DateTime? appLastWrite)
        {
            if (SameSession(e, currentPid, currentProcessStart))
                return "saved in this session; the .app on disk may not have it yet";
            if (!appLastWrite.HasValue) return "the .app's last write time is unknown; its text may be only in memory";
            return "the .app has not been saved since; Recover's text is only in memory";
        }

        /// <summary>True when the entry was written by the current process: same pid (a 0 pid is never "same") and,
        /// when the process start is known, written no earlier than a few seconds before it.</summary>
        public static bool SameSession(Entry e, int currentPid, DateTime? currentProcessStart)
        {
            if (e == null || e.Pid == 0 || e.Pid != currentPid) return false;
            // Compared in UTC (ruling F2-3): a local-time compare is off by an hour across a DST change.
            return !currentProcessStart.HasValue || ToUtc(e.Written) >= ToUtc(currentProcessStart.Value).AddSeconds(-5);
        }

        /// <summary>True when the .app file was last written after the entry was taken (compared in UTC). It changes
        /// the offer's wording, never whether it is offered; for a Persisted entry it decides whether it may be
        /// retired (G1), and in <see cref="Begin"/> whether a kept entry's slots must be carried into a newer save.</summary>
        public static bool AppWrittenSince(Entry e, DateTime? appLastWrite)
        {
            return e != null && appLastWrite.HasValue && ToUtc(appLastWrite.Value) > ToUtc(e.Written);
        }

        /// <summary>The offer's text. When the .app was written after the save it names that time and both
        /// possible causes; otherwise the crash/closed-unsaved wording.</summary>
        public static string OfferMessage(Entry e, DateTime? appLastWrite)
        {
            return OfferMessage(e, appLastWrite, null, null);
        }

        /// <summary>The offer's text for what <paramref name="fresh"/> shows (null = the slots are back at their
        /// pre-save text, the Lost wording). A Diverged reopen gets the PM's "lost when Clarion stopped, or the app
        /// was reverted since" wording. When the restore plan could not place every changed slot,
        /// <paramref name="copyPath"/> names the file that holds them.
        ///
        /// Ruling F2-2: when the embed differs from BOTH texts and the .app was written after the save, the procedure
        /// was changed and saved since (a native edit, most likely), so the toast says so plainly and says Restore
        /// replaces that text; <paramref name="currentCopyPath"/> (<see cref="WriteCurrentCopy"/>) names where the
        /// current text is kept. Ruling 6: slots already showing their saved text (<see cref="RestorePlan.Present"/>)
        /// are never called missing.</summary>
        public static string OfferMessage(Entry e, DateTime? appLastWrite, IList<string> fresh, RestorePlan plan, string copyPath = null,
            string currentCopyPath = null)
        {
            string when = Shown(e.Written);
            bool diverged = fresh != null && Assess(e, fresh) != Verdict.Lost;
            bool since = AppWrittenSince(e, appLastWrite);
            bool partial = plan != null && plan.Present > 0;
            string notIn = partial ? "is only partly in this procedure" : "is not in this procedure";
            bool replacesNewer = diverged && since;
            string head;
            if (replacesNewer)
                head = "Your CA Embeditor save of " + e.Proc + " on " + when + " differs from this procedure, which was changed " +
                       "and saved on " + Shown(appLastWrite.Value) + ".";
            else if (since)
                head = "Your CA Embeditor save of " + e.Proc + " on " + when + " " + notIn + ". The app was last " +
                       "written on " + Shown(appLastWrite.Value) + " without it: Clarion may have stopped " +
                       "before saving it, or it was changed or reverted since, or another IDE has this app open.";
            else if (diverged)
                head = "Your CA Embeditor save of " + e.Proc + " on " + when + " " + notIn + ": it may have been " +
                       "lost when Clarion stopped, or the app was reverted since.";
            else
                head = "Your save of " + e.Proc + " on " + when + " " + notIn + ": Clarion stopped before it " +
                       "was saved, or the app was closed without saving.";
            if (partial)
                head += " " + plan.Present + " of its " + ChangedCount(e) + " changed embed(s) " + (plan.Present == 1 ? "is" : "are") +
                        " already there and " + (plan.Present == 1 ? "is left as it is." : "are left as they are.");
            if (plan != null && plan.Unplaced.Count > 0 && copyPath != null)
                head += " " + plan.Unplaced.Count + " of its " + e.Index.Count + " changed embed(s) cannot be put back " +
                        "because the procedure's embed points changed; their text is in " + copyPath + ".";
            if (replacesNewer)
                return head + (currentCopyPath != null ? " A copy of the current text is in " + currentCopyPath + "." : "") +
                       " Restore your CA save as unsaved edits (replacing the current text)?";
            return head + " Restore it as unsaved edits?";
        }

        /// <summary>How many of the entry's slots changed by more than trailing whitespace / line endings (the ones
        /// <see cref="Assess"/> and <see cref="PlanRestore"/> account for).</summary>
        internal static int ChangedCount(Entry e)
        {
            int n = 0;
            for (int k = 0; k < e.Index.Count; k++) if (!WhitespaceOnly(e, k)) n++;
            return n;
        }

        private static bool WhitespaceOnly(Entry e, int k) { return Loose(e.Saved[k]) == Loose(e.Baseline[k]); }

        /// <summary>The toast when nothing of an offered entry can be placed: it names the copy instead.</summary>
        public static string NotPlaceableMessage(Entry e, string copyPath)
        {
            return "Your CA Embeditor save of " + e.Proc + " on " + Shown(e.Written) + " is not in " +
                   "this procedure, and it cannot be put back automatically because the procedure's embed points changed. " +
                   "Its text is in " + copyPath + ".";
        }

        /// <summary>Entries older than this are archived unoffered by <see cref="SweepStale"/>.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

        /// <summary>
        /// Archive (dated recovery .txt, never offered) every pending entry older than <see cref="MaxAge"/>, so a
        /// journal for a procedure nobody reopened does not wait months to surface as a stale "lost save"
        /// (pipeline run 2). Damaged files are left alone (they are never offered anyway). Returns the archive
        /// paths. Never throws.
        /// </summary>
        public static List<string> SweepStale(DateTime now)
        {
            var archived = new List<string>();
            try
            {
                if (!Directory.Exists(Folder)) return archived;
                foreach (var path in Directory.GetFiles(Folder, "*.pending"))
                {
                    try
                    {
                        var e = Parse(File.ReadAllText(path, new UTF8Encoding(false)));
                        if (e == null || ToUtc(now) - ToUtc(e.Written) <= MaxAge) continue;
                        e.Path = path;
                        string rec = Archive(e, "it was never confirmed and is more than " + (int)MaxAge.TotalDays + " days old");
                        if (rec != null) archived.Add(rec);
                    }
                    catch { }
                }
            }
            catch { }
            return archived;
        }

        /// <summary>What the freshly opened slots say about this entry. Compared per changed slot, ignoring line
        /// endings and trailing whitespace (so a normalisation by Clarion on save is not mistaken for a loss).
        /// Slots are matched by POSITION only while the slot count is unchanged. The slot list holds every embed
        /// point, so adding a control in the window designer shifts it (pipeline run 1, debugger); the slot model
        /// carries no embed identity, so then each changed slot's saved text is looked for among ALL fresh slots.
        /// Only a UNIQUE, NON-EMPTY match counts (pipeline run 2, debugger): an emptied slot, or a one-liner several
        /// slots share, would match some other slot and call a lost save landed. Every changed slot matched that
        /// way means it landed; anything else cannot be told or put back by position: Diverged.
        ///
        /// Clarion re-indents embed code it stores (live test 2026-10-19: a line saved at column 1 came back
        /// indented by two spaces after Clarion's "Recover"), so a slot whose text differs from the saved text ONLY
        /// in its lines' leading whitespace counts as the saved text, provided it does not equally match the
        /// pre-save text that way (an indentation-only edit then proves nothing: Diverged, never "landed").
        ///
        /// With the slot count changed, a changed slot counts as landed only when ALL of these hold (PM ruling
        /// F2-1, see <see cref="LandedAt"/>): its saved text is not also one of the entry's pre-save texts (exactly
        /// or ignoring indentation), its match is unique, and no other changed slot claims the same fresh slot.
        /// Otherwise Diverged: a Persisted verdict deletes the journal with no copy, so it needs proof.</summary>
        public static Verdict Assess(Entry e, IList<string> fresh)
        {
            if (e == null || fresh == null) return Verdict.Diverged;
            if (fresh.Count != e.SlotCount)
            {
                var at = LandedAt(e, fresh);
                for (int k = 0; k < e.Index.Count; k++)
                {
                    if (WhitespaceOnly(e, k)) continue;   // whitespace-only edit: says nothing
                    if (at[k] < 0) return Verdict.Diverged;
                }
                return Verdict.Persisted;
            }
            bool allSaved = true, allBaseline = true;
            for (int k = 0; k < e.Index.Count; k++)
            {
                int i = e.Index[k];
                if (i < 0 || i >= fresh.Count) return Verdict.Diverged;
                if (Loose(e.Saved[k]) == Loose(e.Baseline[k])) continue;   // a whitespace-only edit: says nothing either way
                int m = SlotState(fresh[i], e.Saved[k], e.Baseline[k]);
                if (m != 1) allSaved = false;
                if (m != 2) allBaseline = false;
            }
            if (allSaved) return Verdict.Persisted;
            if (allBaseline) return Verdict.Lost;
            return Verdict.Diverged;
        }

        // 1 = the slot holds the saved text, 2 = the pre-save text, 0 = neither (or both, by indentation alone).
        // Exact (Loose) first; only then ignoring each line's leading whitespace, and only when that singles one out.
        private static int SlotState(string fresh, string saved, string baseline)
        {
            string f = Loose(fresh);
            if (f == Loose(saved)) return 1;
            if (f == Loose(baseline)) return 2;
            string fi = Indentless(fresh), si = Indentless(saved), bi = Indentless(baseline);
            bool isS = fi == si, isB = fi == bi;
            if (isS && !isB) return 1;
            if (isB && !isS) return 2;
            return 0;
        }

        /// <summary>The index of the ONE fresh slot holding <paramref name="text"/>, or -1 when none or several do,
        /// or the text is empty. Exact (Loose) matches decide first; the indentation-blind compare is a fallback
        /// used only when there is no exact match, and it must be unique too.</summary>
        internal static int UniqueMatch(IList<string> fresh, string text)
        {
            string t = Loose(text);
            if (t.Length == 0 || fresh == null) return -1;
            int at = -1, n = 0;
            for (int i = 0; i < fresh.Count; i++) if (Loose(fresh[i]) == t) { at = i; n++; }
            if (n == 1) return at;
            if (n > 1) return -1;
            string ti = Indentless(text);
            for (int i = 0; i < fresh.Count; i++) if (Indentless(fresh[i]) == ti) { at = i; n++; }
            return n == 1 ? at : -1;
        }

        /// <summary>
        /// Slot count changed (ruling F2-1): for each changed slot k, the fresh slot it is PROVEN to show its saved
        /// text at, or -1. Proof needs all three: (a) the saved text is not also among the entry's pre-save texts,
        /// exactly or ignoring indentation (a swap, or a revert to another slot's old text, would otherwise read as
        /// landed); (b) it matches exactly one fresh slot (<see cref="UniqueMatch"/>); (c) no other changed slot
        /// claims that same fresh slot (two slots saved with the same text cannot both be there).
        /// Whitespace-only slots are left at -1 (they prove nothing either way).
        /// </summary>
        internal static int[] LandedAt(Entry e, IList<string> fresh)
        {
            var at = new int[e.Index.Count];
            for (int k = 0; k < at.Length; k++)
            {
                at[k] = -1;
                if (WhitespaceOnly(e, k)) continue;
                if (AmongTexts(e.Saved[k], e.Baseline)) continue;    // (a)
                at[k] = UniqueMatch(fresh, e.Saved[k]);             // (b)
            }
            DropSharedClaims(at, null);                              // (c)
            return at;
        }

        // True when text equals one of the list's texts, by Loose or ignoring indentation.
        private static bool AmongTexts(string text, List<string> list)
        {
            string l = Loose(text), i = Indentless(text);
            foreach (var t in list) if (Loose(t) == l || Indentless(t) == i) return true;
            return false;
        }

        // A fresh slot claimed by two or more changed slots (or already in `taken`) proves none of them: all -1.
        private static void DropSharedClaims(int[] at, HashSet<int> taken)
        {
            var count = new Dictionary<int, int>();
            foreach (int s in at) if (s >= 0) { int c; count.TryGetValue(s, out c); count[s] = c + 1; }
            for (int k = 0; k < at.Length; k++)
                if (at[k] >= 0 && (count[at[k]] > 1 || (taken != null && taken.Contains(at[k])))) at[k] = -1;
        }

        /// <summary>How an offered entry goes back into the freshly opened slots.</summary>
        public sealed class RestorePlan
        {
            /// <summary>The fresh slots with each placed slot replaced by its saved text (the page's restoreSlots list).</summary>
            public List<string> Slots;
            /// <summary>How many slots the restore changes.</summary>
            public int Placed;
            /// <summary>The fresh slot numbers the restore replaces (<see cref="Placed"/> of them), in entry order:
            /// what <see cref="WriteCurrentCopy"/> keeps before Restore applies.</summary>
            public readonly List<int> Replaced = new List<int>();
            /// <summary>How many changed slots already show their saved text (re-indented counts) and are left alone.</summary>
            public int Present;
            /// <summary>Entry slot numbers (0-based, as saved) that could not be identified among the fresh slots.</summary>
            public readonly List<int> Unplaced = new List<int>();
        }

        /// <summary>
        /// Where each changed slot of an offered entry goes. While the slot count is unchanged, by position (the
        /// slot model has no other identity, and it is the identity the save itself used). When it changed, a slot
        /// is placed only where it is identified UNAMBIGUOUSLY: its saved text already uniquely present (nothing to
        /// do), or its pre-save text uniquely present (replaced by the saved text there). Anything else, including
        /// an empty pre-save text (one empty slot among many), is NOT guessed: it is listed in
        /// <see cref="RestorePlan.Unplaced"/> so the caller names the copy file. Null for a null entry/fresh list.
        ///
        /// "Already there" needs the same proof as <see cref="Assess"/> (ruling F2-1, <see cref="LandedAt"/>), and a
        /// pre-save text identifies a slot only under the mirror rule (not also among the saved texts, unique, not
        /// claimed twice). A changed slot that is neither proven present nor placeable is listed as Unplaced, so it
        /// goes to the copy: never skipped. With the count unchanged, a slot whose saved text is there re-indented
        /// counts as already present (ruling 6): it is not re-placed.
        /// </summary>
        public static RestorePlan PlanRestore(Entry e, IList<string> fresh)
        {
            if (e == null || fresh == null) return null;
            var plan = new RestorePlan { Slots = new List<string>(fresh) };
            if (fresh.Count == e.SlotCount)
            {
                for (int k = 0; k < e.Index.Count; k++)
                {
                    int i = e.Index[k];
                    if (i < 0 || i >= fresh.Count) { plan.Unplaced.Add(i); continue; }
                    if (Loose(fresh[i]) == Loose(e.Saved[k]) || SlotState(fresh[i], e.Saved[k], e.Baseline[k]) == 1)
                    {
                        if (!WhitespaceOnly(e, k)) plan.Present++;   // already there (re-indented counts: ruling 6)
                        continue;
                    }
                    plan.Slots[i] = e.Saved[k];
                    plan.Placed++;
                    plan.Replaced.Add(i);
                }
                return plan;
            }
            var landed = LandedAt(e, fresh);
            var taken = new HashSet<int>();
            foreach (int s in landed) if (s >= 0) taken.Add(s);
            var byBase = new int[e.Index.Count];
            for (int k = 0; k < byBase.Length; k++)
            {
                byBase[k] = -1;
                if (WhitespaceOnly(e, k) || landed[k] >= 0) continue;
                if (AmongTexts(e.Baseline[k], e.Saved)) continue;   // that text may be another slot's landed save
                byBase[k] = UniqueMatch(fresh, e.Baseline[k]);
            }
            DropSharedClaims(byBase, taken);
            for (int k = 0; k < e.Index.Count; k++)
            {
                if (WhitespaceOnly(e, k)) continue;   // nothing beyond trailing whitespace to put back
                if (landed[k] >= 0) { plan.Present++; continue; }
                int b = byBase[k];
                if (b >= 0)
                {
                    plan.Slots[b] = e.Saved[k];
                    plan.Placed++;
                    plan.Replaced.Add(b);
                    continue;
                }
                plan.Unplaced.Add(e.Index[k]);
            }
            return plan;
        }

        /// <summary>The slot texts to offer for a <see cref="Verdict.Lost"/> entry: the fresh slots with each
        /// changed slot replaced by its saved text. Null when the entry does not fit <paramref name="fresh"/>.</summary>
        public static List<string> RestoredSlots(Entry e, IList<string> fresh)
        {
            if (e == null || fresh == null || fresh.Count != e.SlotCount) return null;
            var p = PlanRestore(e, fresh);
            return p == null || p.Unplaced.Count > 0 ? null : p.Slots;
        }

        /// <summary>Keep the entry's saved text as an ordinary, dated, human-readable recovery file
        /// (<see cref="EmbedRecovery"/>) and retire the journal. Returns the recovery file's path. Throws on an
        /// I/O failure, in which case the journal is left in place.</summary>
        public static string Archive(Entry e, string reason)
        {
            string path = WriteCopy(e, reason);
            if (e != null && e.Path != null) try { File.Delete(e.Path); } catch { }
            return path;
        }

        /// <summary>Write the entry's saved text as a dated recovery file WITHOUT retiring the journal (used when an
        /// offer must name a copy before the developer answers). Returns its path; throws on an I/O failure.
        /// ONE copy per entry (ruling 5): the file name carries a hash of the entry, and when a copy with that hash
        /// is already there it is returned instead of writing another, so an offer nobody answers does not add a
        /// file on every reopen (and Restore/Discard/Archive of that entry reuse it).</summary>
        public static string WriteCopy(Entry e, string reason)
        {
            if (e == null) return null;
            string tag = "j" + Hash(Serialize(e));
            string existing = EmbedRecovery.FindTagged(e.Proc, tag);
            if (existing != null) return existing;
            // Sized by the highest changed slot, never by the entry's slot count (ruling 4: a count is not a size).
            int size = 0;
            foreach (int i in e.Index) if (i >= 0 && i < e.SlotCount && i + 1 > size) size = i + 1;
            var orig = new List<string>(size);
            var edit = new List<string>(size);
            for (int i = 0; i < size; i++) { orig.Add(""); edit.Add(""); }
            for (int k = 0; k < e.Index.Count; k++)
            {
                int i = e.Index[k];
                if (i < 0 || i >= size) continue;
                orig[i] = e.Baseline[k] ?? "";
                edit[i] = e.Saved[k] ?? "";
            }
            string path = EmbedRecovery.Write(e.Proc,
                "the CA Embeditor save of " + ToLocal(e.Written).ToString("yyyy-MM-dd HH:mm:ss") + " (process " + e.Pid +
                ") could not be confirmed: " + reason, null, orig, edit, tag);
            return path;
        }

        /// <summary>
        /// Ruling F2-2: before an offered Restore can replace anything, keep a dated copy of the CURRENT text of
        /// every slot the plan replaces (<see cref="RestorePlan.Replaced"/>), as the procedure was opened, so work
        /// done natively after the CA save can always be recovered. Null when the plan replaces nothing. One copy per
        /// entry and current text (deduplicated like <see cref="WriteCopy"/>). Throws on an I/O failure: the caller
        /// then does not offer the Restore.
        /// </summary>
        public static string WriteCurrentCopy(Entry e, IList<string> fresh, RestorePlan plan)
        {
            if (e == null || fresh == null || plan == null || plan.Replaced.Count == 0) return null;
            var key = new StringBuilder(Serialize(e)).Append("\ncurrent\n");
            int size = 0;
            foreach (int i in plan.Replaced)
            {
                if (i < 0 || i >= fresh.Count) continue;
                if (i + 1 > size) size = i + 1;
                string t = fresh[i] ?? "";
                key.Append(i).Append(' ').Append(t.Length).Append(':').Append(t).Append('\n');
            }
            if (size == 0) return null;
            string tag = "c" + Hash(key.ToString());
            string existing = EmbedRecovery.FindTagged(e.Proc, tag);
            if (existing != null) return existing;
            // Only the replaced slots are written: the others carry an equal "original", which Format skips.
            var orig = new List<string>(size);
            var edit = new List<string>(size);
            for (int i = 0; i < size; i++) { orig.Add(""); edit.Add(""); }
            foreach (int i in plan.Replaced)
            {
                if (i < 0 || i >= size) continue;
                orig[i] = null;                // never equal: always written, even when empty
                edit[i] = fresh[i] ?? "";
            }
            return EmbedRecovery.Write(e.Proc, null, null, orig, edit, tag, w => CurrentCopyHeader(e, w));
        }

        /// <summary>The header of <see cref="WriteCurrentCopy"/>'s file (live test 2026-10-19, G2). That copy holds the
        /// procedure's CURRENT text, which may be work already saved in the .app, so it must not carry the
        /// "unsaved edits ... as you last had it" header of a copy of the developer's own edits.</summary>
        internal static string CurrentCopyHeader(Entry e, DateTime written)
        {
            return "! CA Embeditor recovery: the text of procedure '" + e.Proc + "' as it was before restoring your CA Embeditor " +
                   "save of " + ToLocal(e.Written).ToString("yyyy-MM-dd HH:mm:ss") + " (process " + e.Pid + ")\r\n" +
                   "! Written " + written.ToString("yyyy-MM-dd HH:mm:ss") + " so the text that Restore replaces can be copied back.\r\n" +
                   "! Each block below is one embed slot as the procedure held it when it was opened.\r\n";
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // ---------------- format ----------------

        internal static string Serialize(Entry e)
        {
            var sb = new StringBuilder(64 + Total(e.Baseline) + Total(e.Saved) + e.Index.Count * 48);
            sb.Append(Magic).Append('\n');
            Field(sb, "app", e.App);
            Field(sb, "proc", e.Proc);
            // UTC with a trailing Z (ruling F2-3). A file without the Z is from before it: Parse reads that as local.
            sb.Append("written ").Append(ToUtc(e.Written).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
              .Append('Z').Append('\n');
            sb.Append("pid ").Append(e.Pid).Append('\n');
            sb.Append("slots ").Append(e.SlotCount).Append('\n');
            sb.Append("changed ").Append(e.Index.Count).Append('\n');
            for (int k = 0; k < e.Index.Count; k++)
            {
                sb.Append("slot ").Append(e.Index[k]).Append('\n');
                Field(sb, "base", e.Baseline[k]);
                Field(sb, "saved", e.Saved[k]);
            }
            sb.Append("END\n");
            return sb.ToString();
        }

        // label <length>:<text>\n — the length is in chars, so any text (newlines included) round-trips.
        private static void Field(StringBuilder sb, string label, string value)
        {
            value = value ?? "";
            sb.Append(label).Append(' ').Append(value.Length).Append(':').Append(value).Append('\n');
        }

        internal static Entry Parse(string s)
        {
            if (s == null) return null;
            int p = 0;
            if (Line(s, ref p) != Magic) return null;
            var e = new Entry();
            if ((e.App = ReadField(s, ref p, "app")) == null) return null;
            if ((e.Proc = ReadField(s, ref p, "proc")) == null) return null;
            DateTime w;
            string wv = Value(Line(s, ref p), "written");
            if (wv == null) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (wv.EndsWith("Z", StringComparison.Ordinal))
            {
                if (!DateTime.TryParseExact(wv.Substring(0, wv.Length - 1), "yyyy-MM-ddTHH:mm:ss", inv,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out w)) return null;
                e.Written = DateTime.SpecifyKind(w, DateTimeKind.Utc);
            }
            else
            {
                // Written before the UTC marker (ruling F2-3): the time is local. Kept as UTC from here on.
                if (!DateTime.TryParseExact(wv, "yyyy-MM-ddTHH:mm:ss", inv, System.Globalization.DateTimeStyles.None, out w)) return null;
                e.Written = ToUtc(DateTime.SpecifyKind(w, DateTimeKind.Local));
            }
            int pid, slots, changed;
            if (!int.TryParse(Value(Line(s, ref p), "pid"), out pid)) return null;
            // A slot count outside 0..MaxSlots is a damaged or hostile file (ruling 4): rejected, never allocated from.
            if (!int.TryParse(Value(Line(s, ref p), "slots"), out slots) || slots < 0 || slots > MaxSlots) return null;
            if (!int.TryParse(Value(Line(s, ref p), "changed"), out changed) || changed < 0 || changed > slots) return null;
            e.Pid = pid; e.SlotCount = slots;
            for (int k = 0; k < changed; k++)
            {
                int idx;
                if (!int.TryParse(Value(Line(s, ref p), "slot"), out idx) || idx < 0 || idx >= slots) return null;
                string b = ReadField(s, ref p, "base"); if (b == null) return null;
                string v = ReadField(s, ref p, "saved"); if (v == null) return null;
                e.Index.Add(idx); e.Baseline.Add(b); e.Saved.Add(v);
            }
            if (Line(s, ref p) != "END" || p != s.Length) return null;   // the trailer, and nothing after it
            return e;
        }

        private static string Line(string s, ref int p)
        {
            if (p >= s.Length) return null;
            int nl = s.IndexOf('\n', p);
            if (nl < 0) return null;   // a line without its newline is a torn tail
            string l = s.Substring(p, nl - p);
            p = nl + 1;
            return l;
        }

        private static string Value(string line, string label)
        {
            if (line == null || !line.StartsWith(label + " ", StringComparison.Ordinal)) return null;
            return line.Substring(label.Length + 1);
        }

        private static string ReadField(string s, ref int p, string label)
        {
            string head = label + " ";
            if (string.CompareOrdinal(s, p, head, 0, head.Length) != 0) return null;
            int colon = s.IndexOf(':', p + head.Length);
            int len;
            if (colon < 0 || !int.TryParse(s.Substring(p + head.Length, colon - p - head.Length), out len) || len < 0) return null;
            int start = colon + 1;
            if (start + len + 1 > s.Length || s[start + len] != '\n') return null;
            p = start + len + 1;
            return s.Substring(start, len);
        }

        private static void WriteAtomic(string target, string text)
        {
            string dir = System.IO.Path.GetDirectoryName(target);
            Directory.CreateDirectory(dir);
            string tmp = System.IO.Path.Combine(dir, "." + System.IO.Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));   // inside the try: a failed write leaves no .tmp
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(target)) File.Replace(tmp, target, null);
                        else File.Move(tmp, target);
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        // Measured in the test run: ReplaceFile can fail with "Unable to remove the file to be
                        // replaced" while a scanner/indexer still holds the file just written. Wait and retry.
                        System.Threading.Thread.Sleep(25 * (attempt + 1));
                    }
                    catch (IOException) when (attempt == 4)
                    {
                        // Still held: fall back to delete + move. Not atomic for the OLD file, which this save
                        // supersedes anyway (Begin archived it first if it was unconfirmed); the NEW text is
                        // still never visible half-written under the final name.
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(tmp, target);
                        return;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // Line endings and trailing whitespace per line, and trailing blank lines, do not count.
        private static string Loose(string s)
        {
            var lines = (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var sb = new StringBuilder(s == null ? 0 : s.Length);
            int last = lines.Length - 1;
            while (last >= 0 && lines[last].Trim().Length == 0) last--;
            for (int i = 0; i <= last; i++) { if (i > 0) sb.Append('\n'); sb.Append(lines[i].TrimEnd()); }
            return sb.ToString();
        }

        // Loose, and each line's leading whitespace and leading blank lines do not count either (Clarion re-indents
        // stored embed code). Used only as a fallback that must single out one candidate.
        private static string Indentless(string s)
        {
            var lines = (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int first = 0, last = lines.Length - 1;
            while (first <= last && lines[first].Trim().Length == 0) first++;
            while (last >= first && lines[last].Trim().Length == 0) last--;
            var sb = new StringBuilder(s == null ? 0 : s.Length);
            for (int i = first; i <= last; i++) { if (i > first) sb.Append('\n'); sb.Append(lines[i].Trim()); }
            return sb.ToString();
        }

        private static int Total(List<string> l) { int n = 0; foreach (var s in l) n += s == null ? 0 : s.Length; return n; }

        private static string AppHash(string app)
        {
            if (string.IsNullOrEmpty(app)) return "noapp";
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(app.Trim().ToLowerInvariant()));
                var sb = new StringBuilder(10);
                for (int i = 0; i < 5; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string SafeName(string s) { return EmbedRecovery.SafeName(s); }
    }
}
