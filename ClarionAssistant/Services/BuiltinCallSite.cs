using ClarionCodeGraph.Parsing;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Whether the cursor is on a call of a Clarion built-in procedure: ERRORCODE(), CLIP(Name), OPEN(MyFile).
    /// Used by the definition and references fallbacks: when the language server answers nothing for such a
    /// call, there is no source declaration to find, and the CodeGraph name lookup must not guess one. A
    /// same-named variable or field (ErrorCode LONG in some QUEUE) cannot be called, so the lookup only finds
    /// an unrelated symbol.
    ///
    /// A built-in name WITHOUT a following '(' is left alone: Error, ErrorCode and Records are legal labels,
    /// and the fallback still helps when the server misses one of those.
    /// </summary>
    public static class BuiltinCallSite
    {
        /// <summary>
        /// True when <paramref name="character"/> (0-based) is on, or just after, a word in
        /// <paramref name="lineText"/> that names a built-in and is followed by '(' (spaces allowed between).
        /// False after a '.' (SELF.Error() is a method), a ':' (PRE:Clip is a prefixed label), a '?' (a field
        /// equate), inside a string literal and after a '!' comment.
        /// </summary>
        public static bool IsBuiltinCallAt(string lineText, int character)
        {
            if (string.IsNullOrEmpty(lineText) || character < 0 || character > lineText.Length) return false;

            int start = character, end = character;
            while (start > 0 && IsWordChar(lineText[start - 1])) start--;
            while (end < lineText.Length && IsWordChar(lineText[end])) end++;
            if (end == start || (!char.IsLetter(lineText[start]) && lineText[start] != '_')) return false;

            if (start > 0)
            {
                char before = lineText[start - 1];
                if (before == '.' || before == ':' || before == '?') return false;
            }

            int next = end;
            while (next < lineText.Length && (lineText[next] == ' ' || lineText[next] == '\t')) next++;
            if (next >= lineText.Length || lineText[next] != '(') return false;

            if (InStringOrComment(lineText, start)) return false;

            return ClarionBuiltins.IsBuiltIn(lineText.Substring(start, end - start));
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        // A Clarion string literal is quoted with ' and doubles a quote inside it (''), which toggles twice and so
        // needs no special case. A '!' outside a string starts a comment to the end of the line. (Local rather than
        // LocalScopeIndex.IsInsideStringOrComment, which would pull the service layer into the standalone test.)
        private static bool InStringOrComment(string lineText, int position)
        {
            bool inString = false;
            for (int i = 0; i < position; i++)
            {
                char c = lineText[i];
                if (c == '\'') inString = !inString;
                else if (c == '!' && !inString) return true;
            }
            return inString;
        }
    }
}
