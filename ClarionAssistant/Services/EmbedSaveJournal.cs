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
            /// <summary>Neither: the embed changed since (or its slot count did). Keep a dated copy, don't offer.</summary>
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
            try
            {
                string path = PathFor(app, proc);
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>What the freshly opened slots say about this entry. Compared per changed slot, ignoring line
        /// endings and trailing whitespace (so a normalisation by Clarion on save is not mistaken for a loss).</summary>
        public static Verdict Assess(Entry e, IList<string> fresh)
        {
            if (e == null || fresh == null || fresh.Count != e.SlotCount) return Verdict.Diverged;
            bool allSaved = true, allBaseline = true;
            for (int k = 0; k < e.Index.Count; k++)
            {
                int i = e.Index[k];
                if (i < 0 || i >= fresh.Count) return Verdict.Diverged;
                string f = Loose(fresh[i]);
                string s = Loose(e.Saved[k]);
                string b = Loose(e.Baseline[k]);
                if (s == b) continue;   // a whitespace-only edit: says nothing either way
                if (f != s) allSaved = false;
                if (f != b) allBaseline = false;
            }
            if (allSaved) return Verdict.Persisted;
            if (allBaseline) return Verdict.Lost;
            return Verdict.Diverged;
        }

        /// <summary>The slot texts to offer for a <see cref="Verdict.Lost"/> entry: the fresh slots with each
        /// changed slot replaced by its saved text. Null when the entry does not fit <paramref name="fresh"/>.</summary>
        public static List<string> RestoredSlots(Entry e, IList<string> fresh)
        {
            if (e == null || fresh == null || fresh.Count != e.SlotCount) return null;
            var r = new List<string>(fresh);
            for (int k = 0; k < e.Index.Count; k++)
            {
                int i = e.Index[k];
                if (i < 0 || i >= r.Count) return null;
                r[i] = e.Saved[k];
            }
            return r;
        }

        /// <summary>Keep the entry's saved text as an ordinary, dated, human-readable recovery file
        /// (<see cref="EmbedRecovery"/>) and retire the journal. Returns the recovery file's path. Throws on an
        /// I/O failure, in which case the journal is left in place.</summary>
        public static string Archive(Entry e, string reason)
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
            if (e.Path != null) try { File.Delete(e.Path); } catch { }
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

        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "embed";
            var bad = System.IO.Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
