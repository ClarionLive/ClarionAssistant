using System;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Which files a diff pane was built from, and what they looked like on disk at the time.
    ///
    /// MOVED OUT OF DiffService.cs (ticket d051fbd1). It is a plain value type - strings,
    /// Encodings, timestamps - with no IDE dependency, but it lived in a file importing
    /// ICSharpCode and System.Windows.Forms, so the shared McpToolRegistry could not name it
    /// without dragging the whole IDE in. Its own file, so both builds can.
    ///
    /// The staleness guards below are load-bearing, not bookkeeping: the diff page sends only a
    /// side token and a buffer, never a path, so a compromised or buggy page cannot redirect a
    /// save at an arbitrary file, and a save is refused if someone else wrote the file while the
    /// compare was open.
    /// </summary>
    public class DiffFileContext
    {
        public string OriginalPath { get; private set; }
        public Encoding OriginalEncoding { get; private set; }
        public string ModifiedPath { get; private set; }
        public Encoding ModifiedEncoding { get; private set; }

        // Disk timestamps as they were when each side was READ into the diff. A save compares against these
        // to catch "something else changed this file while the compare was open" — writing then would
        // silently discard the other change, since the pane was built from the older content.
        private DateTime _originalStampUtc;
        private DateTime _modifiedStampUtc;

        public DiffFileContext(string originalPath, Encoding originalEncoding,
                               string modifiedPath, Encoding modifiedEncoding)
        {
            OriginalPath = originalPath;
            OriginalEncoding = originalEncoding;
            ModifiedPath = modifiedPath;
            ModifiedEncoding = modifiedEncoding;
            _originalStampUtc = SafeStamp(originalPath);
            _modifiedStampUtc = SafeStamp(modifiedPath);
        }

        private static DateTime SafeStamp(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Resolve a side name ("original"/"modified") coming FROM THE PAGE to a real path+encoding.
        /// The page sends only a side token and its buffer — never a path — so a compromised or buggy page
        /// cannot redirect a save at an arbitrary file. Returns false for anything else.</summary>
        public bool TryResolveSide(string side, out string path, out Encoding encoding)
        {
            if (side == "original") { path = OriginalPath; encoding = OriginalEncoding; return true; }
            if (side == "modified") { path = ModifiedPath; encoding = ModifiedEncoding; return true; }
            path = null; encoding = null; return false;
        }

        /// <summary>True if the file on disk still looks like the one this side was read from. A mismatch
        /// means someone else wrote it while the compare was open, so our pane is based on stale content and
        /// saving it would drop their change. Unknown stamps (MinValue) don't block — a guard that can't
        /// read the timestamp should not make the feature unusable.</summary>
        public bool IsSideUnchangedOnDisk(string side)
        {
            string path; DateTime captured;
            if (side == "original") { path = OriginalPath; captured = _originalStampUtc; }
            else if (side == "modified") { path = ModifiedPath; captured = _modifiedStampUtc; }
            else return false;

            if (captured == DateTime.MinValue) return true;
            DateTime now = SafeStamp(path);
            if (now == DateTime.MinValue) return true;
            return now == captured;
        }

        /// <summary>Adopt the on-disk state as the new baseline for a side we just wrote — otherwise our own
        /// write would make the next save of that side look like someone else's interference.</summary>
        public void NoteSideSaved(string side)
        {
            if (side == "original") _originalStampUtc = SafeStamp(OriginalPath);
            else if (side == "modified") _modifiedStampUtc = SafeStamp(ModifiedPath);
        }
    }
}
