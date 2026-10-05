using System;
using System.Collections.Generic;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// fc420c30: the text rules behind the MCP editor tools, as pure functions over a string. EditorService (the native
    /// document) and OverlayEditorOps (the CA Editor's Monaco text) both use these, so a tool answers the same way
    /// whichever editor holds the file. Every rule here is the one EditorService already had; none is new.
    ///
    /// Positions are 1-based lines and columns, as the MCP tools take them. Lines split on '\n'; a line's text excludes
    /// its line break ("\r\n" or "\n"). IDE-free.
    /// </summary>
    public static class EditorTextOps
    {
        /// <summary>The editor's word characters (EditorService.IsWordChar): letters, digits, '_' and ':'.</summary>
        public static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == ':';
        }

        /// <summary>Normalize line breaks to CRLF (EditorService.NormalizeCrLf).</summary>
        public static string NormalizeCrLf(string text)
        {
            if (text == null) return text;
            return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        /// <summary>Offsets where each line starts.</summary>
        public static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            if (text == null) return starts;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        /// <summary>The 1-based line's text without its line break, or null when the line does not exist.</summary>
        public static string LineText(string text, int line)
        {
            var starts = LineStarts(text);
            if (line < 1 || line > starts.Count) return null;
            int s = starts[line - 1];
            int e = line < starts.Count ? starts[line] - 1 : text.Length;   // at the '\n' (or the end)
            if (e > s && text[e - 1] == '\r') e--;
            return text.Substring(s, Math.Max(0, e - s));
        }

        /// <summary>
        /// get_lines_range's format (EditorService.GetLinesRange): "N\ttext" per line, Environment.NewLine-terminated;
        /// the range clamped to [1, line count]; null when it is empty after clamping.
        /// </summary>
        public static string LinesRange(string text, int startLine, int endLine)
        {
            int total = LineStarts(text).Count;
            if (total == 0) return null;
            if (startLine < 1) startLine = 1;
            if (endLine > total) endLine = total;
            if (startLine > endLine) return null;
            var sb = new StringBuilder();
            for (int i = startLine; i <= endLine; i++) sb.AppendLine(i + "\t" + (LineText(text, i) ?? ""));
            return sb.ToString();
        }

        /// <summary>
        /// find_in_file's matches (EditorService.FindInFile): every occurrence, overlapping ones included (the search
        /// resumes one past each hit), as [line, column] 1-based.
        /// </summary>
        public static List<int[]> FindAll(string text, string search, bool caseSensitive)
        {
            var results = new List<int[]>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(search)) return results;
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int pos = 0;
            // Line counting carries on from the previous match instead of rescanning from the top (the same answers;
            // on a 62k-line module with 155 matches that was ~0.5 s of rescans).
            int line = 1, lastLineStart = 0, scanned = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(search, pos, cmp);
                if (idx < 0) break;
                for (int i = scanned; i < idx; i++) if (text[i] == '\n') { line++; lastLineStart = i + 1; }
                scanned = idx;
                results.Add(new[] { line, idx - lastLineStart + 1 });
                pos = idx + 1;
            }
            return results;
        }

        /// <summary>replace_text's occurrences (EditorService.ReplaceText): ordinal, non-overlapping, left to right.</summary>
        public static List<int> ReplaceOffsets(string text, string oldText)
        {
            var offsets = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldText)) return offsets;
            int from = 0;
            while (from < text.Length)
            {
                int idx = text.IndexOf(oldText, from, StringComparison.Ordinal);
                if (idx < 0) break;
                offsets.Add(idx);
                from = idx + oldText.Length;
            }
            return offsets;
        }

        /// <summary>The word around <paramref name="offset"/> (EditorService.GetWordUnderCursor), or null.</summary>
        public static string WordAt(string text, int offset)
        {
            if (text == null || offset < 0 || offset > text.Length) return null;
            int start = offset;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            int end = offset;
            while (end < text.Length && IsWordChar(text[end])) end++;
            return start < end ? text.Substring(start, end - start) : null;
        }

        /// <summary>Offset of a 1-based line/column, the column clamped to the line's length (EditorService.GetOffset);
        /// -1 when the line does not exist.</summary>
        public static int Offset(string text, int line, int col)
        {
            var starts = LineStarts(text);
            if (line < 1 || line > starts.Count) return -1;
            string lineText = LineText(text, line) ?? "";
            int c = Math.Max(0, Math.Min(col - 1, lineText.Length));
            return starts[line - 1] + c;
        }

        /// <summary>1-based [line, column] of an offset.</summary>
        public static int[] Position(string text, int offset)
        {
            int line = 1, lastLineStart = 0;
            for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') { line++; lastLineStart = i + 1; }
            return new[] { line, offset - lastLineStart + 1 };
        }

        /// <summary>
        /// toggle_comment (EditorService.ToggleComment): if every non-blank line in [startLine, endLine] already starts
        /// with '!', remove the first '!' from each; otherwise put '!' before the first non-space character of each.
        /// Returns the replaced lines (joined with '\n', exactly as the native path rebuilds them), or null with
        /// <paramref name="error"/> when the range is out of bounds.
        /// </summary>
        public static string ToggleCommentLines(string text, int startLine, int endLine, out string error)
        {
            error = null;
            var lines = (text ?? "").Split('\n');
            if (startLine < 1 || endLine > lines.Length) { error = "Line range out of bounds"; return null; }

            bool allCommented = true;
            for (int i = startLine - 1; i < endLine; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("!")) { allCommented = false; break; }
            }
            for (int i = startLine - 1; i < endLine; i++)
            {
                if (allCommented)
                {
                    int bang = lines[i].IndexOf('!');
                    if (bang >= 0) lines[i] = lines[i].Substring(0, bang) + lines[i].Substring(bang + 1);
                }
                else
                {
                    int first = 0;
                    while (first < lines[i].Length && lines[i][first] == ' ') first++;
                    lines[i] = lines[i].Substring(0, first) + "!" + lines[i].Substring(first);
                }
            }
            var block = new string[endLine - startLine + 1];
            Array.Copy(lines, startLine - 1, block, 0, block.Length);
            return string.Join("\n", block);
        }

        /// <summary>The whole text with toggle_comment applied (what the native path writes back).</summary>
        public static string ToggleComment(string text, int startLine, int endLine, out string error)
        {
            string block = ToggleCommentLines(text, startLine, endLine, out error);
            if (block == null) return null;
            var lines = (text ?? "").Split('\n');
            var head = new string[startLine - 1];
            Array.Copy(lines, 0, head, 0, head.Length);
            var tail = new string[lines.Length - endLine];
            Array.Copy(lines, endLine, tail, 0, tail.Length);
            var all = new List<string>(head);
            all.AddRange(block.Split('\n'));
            all.AddRange(tail);
            return string.Join("\n", all);
        }
    }
}
