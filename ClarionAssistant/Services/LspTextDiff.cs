using System;

namespace ClarionAssistant.Services
{
    /// <summary>One LSP TextDocumentContentChangeEvent with a range: replace [Start, End) of the OLD text with Text.</summary>
    public sealed class LspTextChange
    {
        public int StartLine, StartCharacter, EndLine, EndCharacter;
        public string Text;
        /// <summary>The same range as UTF-16 offsets into the old text (for logging and tests).</summary>
        public int OldStart, OldEnd;
    }

    /// <summary>
    /// The single ranged change that turns one text into another, for LSP incremental sync (LspClient sends it instead
    /// of the whole buffer when the server advertises TextDocumentSyncKind.Incremental).
    ///
    /// The change is everything between the longest common prefix and the longest common suffix — one small range for
    /// the edits an editor actually makes between two syncs (typing, a paste, a delete), whatever the document size.
    /// Positions follow the LSP: lines end at \r\n, \r or \n, and characters are UTF-16 code units, which is what a .NET
    /// string indexes. Both boundaries are pulled back so neither lands inside a \r\n or a surrogate pair, in either
    /// text: a position there is ambiguous (the server may clamp it to the line end or place it on the other side), and
    /// a change the server places differently leaves it silently holding a different buffer from the editor.
    /// tests\LspTextDiff.Test.cs applies every change with the LSP's rules and checks it reproduces the new text.
    /// </summary>
    public static class LspTextDiff
    {
        /// <summary>The change, or null when the texts are identical.</summary>
        public static LspTextChange Compute(string oldText, string newText)
        {
            oldText = oldText ?? ""; newText = newText ?? "";
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) return null;

            int oldLen = oldText.Length, newLen = newText.Length, max = Math.Min(oldLen, newLen);
            int prefix = 0;
            while (prefix < max && oldText[prefix] == newText[prefix]) prefix++;
            int suffix = 0;
            while (suffix < max - prefix && oldText[oldLen - 1 - suffix] == newText[newLen - 1 - suffix]) suffix++;

            // Never split a \r\n or a surrogate pair at either boundary, in either text.
            while (prefix > 0 && (SplitsPair(oldText, prefix) || SplitsPair(newText, prefix))) prefix--;
            while (suffix > 0 && (SplitsPair(oldText, oldLen - suffix) || SplitsPair(newText, newLen - suffix))) suffix--;

            int oldEnd = oldLen - suffix, newEnd = newLen - suffix;
            int sl, sc, el, ec;
            Positions(oldText, prefix, oldEnd, out sl, out sc, out el, out ec);
            return new LspTextChange
            {
                StartLine = sl, StartCharacter = sc, EndLine = el, EndCharacter = ec,
                Text = newText.Substring(prefix, newEnd - prefix),
                OldStart = prefix, OldEnd = oldEnd
            };
        }

        /// <summary>True when a boundary at <paramref name="offset"/> falls between the two halves of a \r\n or of a
        /// surrogate pair.</summary>
        static bool SplitsPair(string t, int offset)
        {
            if (offset <= 0 || offset >= t.Length) return false;
            char before = t[offset - 1], after = t[offset];
            return (before == '\r' && after == '\n') || (char.IsHighSurrogate(before) && char.IsLowSurrogate(after));
        }

        /// <summary>LSP positions of two offsets (start &lt;= end) in one pass over the text up to the end offset.</summary>
        static void Positions(string text, int start, int end, out int startLine, out int startChar, out int endLine, out int endChar)
        {
            int line = 0, lineStart = 0;
            startLine = -1; startChar = 0;
            for (int i = 0; i < end; i++)
            {
                if (i == start) { startLine = line; startChar = i - lineStart; }
                char c = text[i];
                if (c == '\r')
                {
                    // A \r\n is one line break; the boundary rules guarantee neither offset falls between its halves.
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    line++; lineStart = i + 1;
                }
                else if (c == '\n') { line++; lineStart = i + 1; }
            }
            if (startLine < 0) { startLine = line; startChar = start - lineStart; }
            endLine = line; endChar = end - lineStart;
        }
    }
}
