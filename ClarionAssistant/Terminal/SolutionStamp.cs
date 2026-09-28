using System;
using System.IO;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// The key a solution-level view is drawn under (82938fc7): the solution path plus a generation that moves
    /// on every actual solution change. The Schema Sources modal and the Source Control fields echo both on
    /// every write, and a write is applied only while both still match - the path alone cannot see a switch
    /// away and back (A->B->A) that happened while a Select or a repo save was queued.
    /// Pure (no IDE types), so tests\SolutionStamp.Test.cs compiles it straight from the tree.
    /// </summary>
    internal sealed class SolutionStamp
    {
        /// <summary>The current generation; stamped into every solution-keyed view.</summary>
        public long Gen { get; private set; }

        /// <summary>The solution changed: views drawn before now are stale, whatever path comes next.</summary>
        public void Advance() { Gen++; }

        /// <summary>True only when the echoed solution AND generation are the current ones.</summary>
        public bool Matches(string shownSln, long shownGen, string currentSln)
        {
            return shownGen == Gen && SamePath(shownSln, currentSln);
        }

        /// <summary>Same solution file: normalised, case-insensitive; an empty path never matches.</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.Trim(); }
        }
    }
}
