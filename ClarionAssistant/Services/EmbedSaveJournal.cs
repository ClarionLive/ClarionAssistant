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
    /// loaded slots either hold the saved text (it persisted: retire silently), are back at their pre-save text
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
            public DateTime Written;
            public int Pid;
            public int SlotCount;
            public readonly List<int> Index = new List<int>();          // the changed slots, 0-based
            public readonly List<string> Baseline = new List<string>();  // their text before the save
            public readonly List<string> Saved = new List<string>();     // the text the save handed to Clarion
        }

        private const string Magic = "CA-EMBED-PENDING-SAVE 1";

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
        /// </summary>
        public static string Begin(string app, string proc, IList<string> baseline, IList<string> saved, out string archived)
        {
            archived = null;
            // No app, no journal (pipeline run 1): an entry filed under a guessed app is read back by the wrong one.
            if (string.IsNullOrWhiteSpace(app)) return null;
            if (baseline == null || saved == null || baseline.Count != saved.Count) return null;
            var e = new Entry { App = app ?? "", Proc = proc ?? "", Written = DateTime.Now, SlotCount = saved.Count };
            try { e.Pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
            for (int i = 0; i < saved.Count; i++)
            {
                if (EmbedSavePlanner.NLEqual(baseline[i], saved[i])) continue;
                e.Index.Add(i); e.Baseline.Add(baseline[i] ?? ""); e.Saved.Add(saved[i] ?? "");
            }
            if (e.Index.Count == 0) return null;

            var old = Read(app, proc);
            if (old != null && Assess(old, baseline) != Verdict.Persisted)
                archived = Archive(old, "an earlier save of it was never confirmed, and a newer save replaced it");

            string target = PathFor(app, proc);
            WriteAtomic(target, Serialize(e));
            return target;
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
            /// <summary>It landed AND was read back by a later process, so it came from the saved .app: delete.</summary>
            Retire,
            /// <summary>It landed, but only in THIS session's in-memory .app, which a crash can still lose: keep it,
            /// say nothing. A later session decides.</summary>
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

        /// <param name="currentProcessStart">When the current process started (local time), or null when unknown.
        /// With it, an entry is from this session only when its pid matches AND it was written after this process
        /// started, so a recycled pid is not mistaken for this session.</param>
        public static OpenDecision Decide(Entry e, IList<string> fresh, int currentPid, DateTime? currentProcessStart)
        {
            bool sameSession = SameSession(e, currentPid, currentProcessStart);
            switch (Assess(e, fresh))
            {
                case Verdict.Persisted: return sameSession ? OpenDecision.Keep : OpenDecision.Retire;
                case Verdict.Lost: return OpenDecision.Offer;
                default: return sameSession ? OpenDecision.Archive : OpenDecision.Offer;
            }
        }

        /// <summary>True when the entry was written by the current process: same pid (a 0 pid is never "same") and,
        /// when the process start is known, written no earlier than a few seconds before it.</summary>
        public static bool SameSession(Entry e, int currentPid, DateTime? currentProcessStart)
        {
            if (e == null || e.Pid == 0 || e.Pid != currentPid) return false;
            return !currentProcessStart.HasValue || e.Written >= currentProcessStart.Value.AddSeconds(-5);
        }

        /// <summary>True when the .app file was last written after the entry was taken. It changes only the
        /// offer's wording ("the app was last written on ... without it"), never whether it is offered.</summary>
        public static bool AppWrittenSince(Entry e, DateTime? appLastWrite)
        {
            return e != null && appLastWrite.HasValue && appLastWrite.Value > e.Written;
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
        /// <paramref name="copyPath"/> names the file that holds them.</summary>
        public static string OfferMessage(Entry e, DateTime? appLastWrite, IList<string> fresh, RestorePlan plan, string copyPath = null)
        {
            string when = e.Written.ToString("yyyy-MM-dd HH:mm");
            bool diverged = fresh != null && Assess(e, fresh) != Verdict.Lost;
            string head;
            if (AppWrittenSince(e, appLastWrite))
                head = "Your CA Embeditor save of " + e.Proc + " on " + when + " is not in this procedure. The app was last " +
                       "written on " + appLastWrite.Value.ToString("yyyy-MM-dd HH:mm") + " without it: Clarion may have stopped " +
                       "before saving it, or it was changed or reverted since, or another IDE has this app open.";
            else if (diverged)
                head = "Your CA Embeditor save of " + e.Proc + " on " + when + " is not in this procedure: it may have been " +
                       "lost when Clarion stopped, or the app was reverted since.";
            else
                head = "Your save of " + e.Proc + " on " + when + " is not in this procedure: Clarion stopped before it " +
                       "was saved, or the app was closed without saving.";
            if (plan != null && plan.Unplaced.Count > 0 && copyPath != null)
                head += " " + plan.Unplaced.Count + " of its " + e.Index.Count + " changed embed(s) cannot be put back " +
                        "because the procedure's embed points changed; their text is in " + copyPath + ".";
            return head + " Restore it as unsaved edits?";
        }

        /// <summary>The toast when nothing of an offered entry can be placed: it names the copy instead.</summary>
        public static string NotPlaceableMessage(Entry e, string copyPath)
        {
            return "Your CA Embeditor save of " + e.Proc + " on " + e.Written.ToString("yyyy-MM-dd HH:mm") + " is not in " +
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
                        if (e == null || now - e.Written <= MaxAge) continue;
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
        /// pre-save text that way (an indentation-only edit then proves nothing: Diverged, never "landed").</summary>
        public static Verdict Assess(Entry e, IList<string> fresh)
        {
            if (e == null || fresh == null) return Verdict.Diverged;
            if (fresh.Count != e.SlotCount)
            {
                for (int k = 0; k < e.Index.Count; k++)
                {
                    if (Loose(e.Saved[k]) == Loose(e.Baseline[k])) continue;   // whitespace-only edit: says nothing
                    if (UniqueMatch(fresh, e.Saved[k]) < 0) return Verdict.Diverged;
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

        /// <summary>How an offered entry goes back into the freshly opened slots.</summary>
        public sealed class RestorePlan
        {
            /// <summary>The fresh slots with each placed slot replaced by its saved text (the page's restoreSlots list).</summary>
            public List<string> Slots;
            /// <summary>How many slots the restore changes.</summary>
            public int Placed;
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
                    if (Loose(fresh[i]) == Loose(e.Saved[k])) continue;   // already there
                    plan.Slots[i] = e.Saved[k];
                    plan.Placed++;
                }
                return plan;
            }
            var taken = new HashSet<int>();
            for (int k = 0; k < e.Index.Count; k++)
            {
                if (Loose(e.Saved[k]) == Loose(e.Baseline[k])) continue;   // whitespace-only edit
                int s = UniqueMatch(fresh, e.Saved[k]);
                if (s >= 0 && taken.Add(s)) continue;                       // already there
                int b = UniqueMatch(fresh, e.Baseline[k]);
                if (b >= 0 && taken.Add(b))
                {
                    plan.Slots[b] = e.Saved[k];
                    plan.Placed++;
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
        /// offer must name a copy before the developer answers). Returns its path; throws on an I/O failure.</summary>
        public static string WriteCopy(Entry e, string reason)
        {
            if (e == null) return null;
            var orig = new List<string>(e.SlotCount);
            var edit = new List<string>(e.SlotCount);
            for (int i = 0; i < e.SlotCount; i++) { orig.Add(""); edit.Add(""); }
            for (int k = 0; k < e.Index.Count; k++)
            {
                int i = e.Index[k];
                if (i < 0 || i >= e.SlotCount) continue;
                orig[i] = e.Baseline[k] ?? "";
                edit[i] = e.Saved[k] ?? "";
            }
            string path = EmbedRecovery.Write(e.Proc,
                "the CA Embeditor save of " + e.Written.ToString("yyyy-MM-dd HH:mm:ss") + " (process " + e.Pid +
                ") could not be confirmed: " + reason, null, orig, edit);
            return path;
        }

        // ---------------- format ----------------

        internal static string Serialize(Entry e)
        {
            var sb = new StringBuilder(64 + Total(e.Baseline) + Total(e.Saved) + e.Index.Count * 48);
            sb.Append(Magic).Append('\n');
            Field(sb, "app", e.App);
            Field(sb, "proc", e.Proc);
            sb.Append("written ").Append(e.Written.ToString("yyyy-MM-ddTHH:mm:ss")).Append('\n');
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
            if (!DateTime.TryParseExact(Value(Line(s, ref p), "written"), "yyyy-MM-ddTHH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out w)) return null;
            e.Written = w;
            int pid, slots, changed;
            if (!int.TryParse(Value(Line(s, ref p), "pid"), out pid)) return null;
            if (!int.TryParse(Value(Line(s, ref p), "slots"), out slots) || slots < 0) return null;
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
