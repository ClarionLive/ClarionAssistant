using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Is a line number that arrived FROM THE PAGE inside the document it claims to be in? (task f022fb4e.)
    ///
    /// The Monaco page is our own bundled code, so this is not an attacker model — it is the ordinary
    /// discipline of not writing a position that cannot exist into host state. A line that survives this
    /// check ends up in the editor's mirrored cursor, which the CA Debugger reads back through
    /// MonacoSourceNavigator.TryGetActiveCursor and which is persisted as the file's saved cursor position.
    ///
    /// Pulled out of MonacoClarionSourceEditor so it can be tested at all: that class needs the whole IDE
    /// to load, while the decision here is pure arithmetic. See Terminal/test/DebuggerHookGuardsCheck.cs.
    /// </summary>
    internal static class DocumentLineGuard
    {
        /// <summary>Lines in <paramref name="text"/>, counted the way Monaco counts them: a trailing newline
        /// ends a line and opens a last, empty one, and "" is one empty line. Null is 0 — no text, nothing
        /// counted (callers distinguish that from a genuinely empty buffer by passing null only when they
        /// have no buffer).</summary>
        internal static int CountLines(string text)
        {
            if (text == null) return 0;
            int n = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') n++;
            return n;
        }

        /// <summary>
        /// Does <paramref name="line"/> (1-based, as the page sends it) exist in this document?
        ///
        /// <paramref name="liveText"/> is the page's own mirror of its buffer, which it re-sends on EVERY
        /// edit (pushFileState is deliberately not debounced), so when present it is the live truth and
        /// beats <paramref name="nativeLineCount"/> — the captured native document, which Monaco was seeded
        /// from and which it never writes back to. With NEITHER available the answer is yes for any positive
        /// line: unknown is not the same as empty, and refusing a legal Run to Cursor because the host has
        /// not been told the buffer yet would be a worse bug than the one this guard prevents.
        /// </summary>
        internal static bool Contains(int line, string liveText, int nativeLineCount)
        {
            if (line < 1) return false;
            int count = liveText != null ? CountLines(liveText) : nativeLineCount;
            return count <= 0 || line <= count;
        }
    }
}
