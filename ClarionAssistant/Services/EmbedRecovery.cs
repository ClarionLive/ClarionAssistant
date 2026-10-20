using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Last-resort copy of a developer's unsaved CA Embeditor edits (1565ef7b). Written ONLY where the text
    /// leaves the editor without being persisted: a save that failed after the overlay detached, a Ctrl+F4
    /// sync that could not copy the edits into Clarion's buffer, and an external teardown (the in-memory
    /// stash, which a regenerated procedure can make unrestorable).
    ///
    /// One plain-text file per event under %APPDATA%\ClarionAssistant\embed-recovery, holding each slot the
    /// developer changed. UTF-8 WITHOUT a BOM (a BOM breaks Clarion if the text is pasted back via a file),
    /// written to a temp file in the same folder and then moved to a name nothing else holds, so a crash
    /// mid-write never leaves a half file under the final name and an earlier recovery is never overwritten.
    /// </summary>
    public static class EmbedRecovery
    {
        /// <summary>Overridable for tests; null means %APPDATA%\ClarionAssistant\embed-recovery.</summary>
        public static string FolderOverride;

        public static string Folder
        {
            get
            {
                return FolderOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant", "embed-recovery");
            }
        }

        /// <summary>The file's text, or null when no slot differs from its baseline (nothing to recover).</summary>
        public static string Format(string procName, string reason, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> editedSlotTexts, DateTime when)
        {
            return Format(procName, reason, ranges, originalSlotTexts, editedSlotTexts, when, null);
        }

        /// <param name="header">The file's opening comment lines ("! ..." lines, each ending in CRLF), built from the
        /// time it is written; null = the unsaved-edits header. A copy that does NOT hold the developer's unsaved
        /// edits (the procedure's current text, kept before a Restore replaces it: GH #179 live test 2026-10-19, G2)
        /// says what it holds instead, and <paramref name="reason"/> is then not used.</param>
        public static string Format(string procName, string reason, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> editedSlotTexts, DateTime when, Func<DateTime, string> header)
        {
            if (editedSlotTexts == null) return null;
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < editedSlotTexts.Count; i++)
            {
                string orig = originalSlotTexts != null && i < originalSlotTexts.Count ? originalSlotTexts[i] : null;
                if (orig != null && EmbedSavePlanner.NLEqual(orig, editedSlotTexts[i])) continue;
                int line = ranges != null && i < ranges.Count && ranges[i] != null ? ranges[i][0] : 0;
                sb.Append("! ===== embed slot ").Append(i + 1)
                  .Append(line > 0 ? " (at line " + line + " when the CA Embeditor opened)" : "")
                  .Append(" =====\r\n");
                sb.Append((editedSlotTexts[i] ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"));
                sb.Append("\r\n");
                n++;
            }
            if (n == 0) return null;
            if (header != null) return header(when) + "\r\n" + sb;
            return "! CA Embeditor recovery: unsaved edits to procedure '" + procName + "'\r\n" +
                   "! Written " + when.ToString("yyyy-MM-dd HH:mm:ss") + " because " + reason + "\r\n" +
                   "! Each block below is one embed slot as you last had it. Copy it back into the embed to restore it.\r\n" +
                   "\r\n" + sb;
        }

        /// <summary>Write the recovery file and return its full path, or null when there is nothing to recover.
        /// Throws on an I/O failure — callers report that rather than claim a copy exists.</summary>
        public static string Write(string procName, string reason, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> editedSlotTexts)
        {
            return Write(procName, reason, ranges, originalSlotTexts, editedSlotTexts, null);
        }

        /// <param name="nameTag">Appended to the file name after the date (letters and digits), so a caller can
        /// find a copy it already wrote (<see cref="FindTagged"/>) instead of writing another (GH #179 ruling 5).</param>
        public static string Write(string procName, string reason, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> editedSlotTexts, string nameTag)
        {
            return Write(procName, reason, ranges, originalSlotTexts, editedSlotTexts, nameTag, null);
        }

        /// <param name="header">The file's own header (see the <c>Format</c> overload that takes one); null = the
        /// unsaved-edits header.</param>
        public static string Write(string procName, string reason, List<int[]> ranges,
            IList<string> originalSlotTexts, IList<string> editedSlotTexts, string nameTag, Func<DateTime, string> header)
        {
            var now = DateTime.Now;
            string text = Format(procName, reason, ranges, originalSlotTexts, editedSlotTexts, now, header);
            if (text == null) return null;

            string dir = Folder;
            Directory.CreateDirectory(dir);
            string stem = SafeName(procName) + "-" + now.ToString("yyyyMMdd-HHmmss") +
                          (string.IsNullOrEmpty(nameTag) ? "" : "-" + SafeName(nameTag));
            string tmp = Path.Combine(dir, "." + stem + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));   // inside the try: a failed write leaves no .tmp

                for (int i = 0; ; i++)
                {
                    string target = Path.Combine(dir, stem + (i == 0 ? "" : "-" + i) + ".txt");
                    if (File.Exists(target)) continue;
                    try { File.Move(tmp, target); return target; }   // Move never overwrites
                    catch (IOException) when (i < 1000 && File.Exists(target)) { }   // lost a race for the name
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// Delete recovery copies (the top-level <c>*.txt</c> files this class writes) last written more than
        /// <paramref name="maxAge"/> before <paramref name="now"/>, so Restore/Discard archives and failure copies
        /// do not pile up forever (254e4353 pipeline run 3). Never descends into subfolders, so the save journal's
        /// <c>pending\*.pending</c> files are never touched. Returns how many were deleted. Never throws.
        /// </summary>
        public static int PruneOlderThan(TimeSpan maxAge, DateTime now)
        {
            int n = 0;
            try
            {
                string dir = Folder;
                if (!Directory.Exists(dir)) return 0;
                foreach (var f in Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (!string.Equals(Path.GetExtension(f), ".txt", StringComparison.OrdinalIgnoreCase)) continue;   // "*.txt" also matches *.txtx
                        if (now - File.GetLastWriteTime(f) <= maxAge) continue;
                        File.Delete(f);
                        n++;
                    }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        /// <summary>A recovery copy of this procedure written with <paramref name="nameTag"/>, or null when there is
        /// none (or the folder cannot be read). Top-level <c>*.txt</c> only, like <see cref="PruneOlderThan"/>.</summary>
        public static string FindTagged(string procName, string nameTag)
        {
            if (string.IsNullOrEmpty(nameTag)) return null;
            try
            {
                string dir = Folder;
                if (!Directory.Exists(dir)) return null;
                string suffix = "-" + SafeName(nameTag) + ".txt";
                foreach (var f in Directory.GetFiles(dir, SafeName(procName) + "-*" + suffix, SearchOption.TopDirectoryOnly))
                    if (f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return f;   // "*.txt" patterns also match *.txtx
            }
            catch { }
            return null;
        }

        internal static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "embed";
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
