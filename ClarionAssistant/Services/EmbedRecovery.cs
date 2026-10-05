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
            var now = DateTime.Now;
            string text = Format(procName, reason, ranges, originalSlotTexts, editedSlotTexts, now);
            if (text == null) return null;

            string dir = Folder;
            Directory.CreateDirectory(dir);
            string stem = SafeName(procName) + "-" + now.ToString("yyyyMMdd-HHmmss");
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

        private static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "embed";
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
