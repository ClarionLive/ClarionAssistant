using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 73bd1f03 fix (2): the embed tools' text work over a plain buffer plus its editable slot ranges, so the same
    /// answers can come from the CA Embeditor's Monaco buffer (routed) as from the native embeditor document.
    ///
    /// Formats match AppTreeService exactly (get_embeditor_source's «E:N» annotation and noise stripping,
    /// search_embeditor_source's merged match windows and 6 KB cap, get_embed_content's "(empty embed)",
    /// write_embed_content's reindent and line-delta report), so Claude sees one tool whichever editor answers.
    ///
    /// Line numbers: 1-based lines of <c>text</c>. Ranges: 1-based inclusive [start,end], in document order.
    /// Pure, so tests\EmbedSlotText.Test.cs drives it without an IDE.
    /// </summary>
    public static class EmbedSlotText
    {
        /// <summary>Put in front of every routed embed-tool answer: which line space «E:N» is in.</summary>
        public const string LineBaseNote =
            "lineBase: ca-embeditor. The CA Embeditor is open on this procedure, so «E:N» line numbers are the CA " +
            "Embeditor's (they include the developer's unsaved edits). Use only these numbers with write_embed_content " +
            "and get_embed_content while it stays open; numbers read before it opened may be off.";

        public static string[] Lines(string text)
        {
            return (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        /// <summary>
        /// One line of the annotated source: <see cref="Line"/> is its 1-based BUFFER line (the number get_line_text,
        /// replace_range, go_to_line and find_in_file use), or 0 for a «E:N» / «/E:N» / «E:N/» marker, which is not a
        /// buffer line.
        /// </summary>
        public struct AnnotatedLine
        {
            public int Line;
            public string Text;
            public AnnotatedLine(int line, string text) { Line = line; Text = text; }
            /// <summary>How it is printed: buffer lines carry their number ("265| code"), markers do not.</summary>
            public override string ToString() { return Line > 0 ? Line + "| " + Text : Text; }
        }

        /// <summary>The rule the annotated output follows, for tool descriptions and docs.</summary>
        public const string NumberingRule =
            "Every code line is prefixed with its buffer line number (\"265| ...\"): that is the line number get_line_text, " +
            "get_lines_range, replace_range, go_to_line and find_in_file use. «E:N» / «/E:N» / «E:N/» marker lines are NOT buffer " +
            "lines: N is the slot's first buffer line (the line_number for write_embed_content / get_embed_content).";

        /// <summary>
        /// The «E:N» annotated source as lines: slots wrapped in «E:N»..«/E:N» (or «E:N/» when empty), generated noise
        /// lines (! Start of, ! End of, ! [Priority N], !!!) and blank lines outside slots dropped, every kept buffer line
        /// tagged with its buffer line number. Both editors build their answers here (73bd1f03), so they cannot drift.
        /// </summary>
        public static List<AnnotatedLine> AnnotateLines(string text, IList<int[]> ranges)
        {
            var lines = Lines(text);
            var starts = new Dictionary<int, int>();   // 0-based start -> 0-based end
            if (ranges != null)
                foreach (var r in ranges)
                    if (r != null && r.Length >= 2 && r[0] >= 1) starts[r[0] - 1] = Math.Max(r[0], r[1]) - 1;

            var result = new List<AnnotatedLine>();
            int i = 0;
            while (i < lines.Length)
            {
                int end;
                if (starts.TryGetValue(i, out end))
                {
                    var slot = new List<AnnotatedLine>();
                    bool hasContent = false;
                    for (int j = i; j <= end && j < lines.Length; j++)
                    {
                        slot.Add(new AnnotatedLine(j + 1, lines[j]));
                        if (lines[j].Trim().Length > 0) hasContent = true;
                    }
                    if (hasContent)
                    {
                        result.Add(new AnnotatedLine(0, "\u00ABE:" + (i + 1) + "\u00BB"));
                        result.AddRange(slot);
                        result.Add(new AnnotatedLine(0, "\u00AB/E:" + (i + 1) + "\u00BB"));
                    }
                    else
                    {
                        result.Add(new AnnotatedLine(0, "\u00ABE:" + (i + 1) + "/\u00BB"));
                    }
                    i = end + 1;
                    continue;
                }

                string t = lines[i].Trim();
                if (!(t.Length == 0 || t.StartsWith("! Start of ") || t.StartsWith("! End of ") ||
                      t.StartsWith("! [Priority ") || t.StartsWith("!!!")))
                    result.Add(new AnnotatedLine(i + 1, lines[i]));
                i++;
            }
            return result;
        }

        /// <summary>get_embeditor_source: <see cref="AnnotateLines"/>, one per line.</summary>
        public static string Annotate(string text, IList<int[]> ranges)
        {
            var sb = new StringBuilder();
            foreach (var l in AnnotateLines(text, ranges)) sb.AppendLine(l.ToString());
            return sb.ToString();
        }

        /// <summary>
        /// search_embeditor_source: the pattern is matched against each annotated line's TEXT (never its number
        /// prefix), match windows of ±<paramref name="contextLines"/> annotated lines are merged, each block is headed
        /// with the BUFFER lines it spans, and the output is capped at ~6 KB.
        /// </summary>
        public static string Search(string text, IList<int[]> ranges, string pattern, int contextLines)
        {
            var lines = AnnotateLines(text, ranges);
            Regex rx;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
            catch (Exception ex) { return "Error: invalid pattern — " + ex.Message; }

            // Collect [start, end] windows for each match (with context), then merge overlaps
            var windows = new List<int[]>();
            for (int i = 0; i < lines.Count; i++)
            {
                if (rx.IsMatch(lines[i].Text))
                {
                    int from = Math.Max(0, i - contextLines);
                    int to   = Math.Min(lines.Count - 1, i + contextLines);
                    windows.Add(new[] { from, to });
                }
            }

            if (windows.Count == 0)
                return "No matches for: " + pattern;

            var merged = new List<int[]> { windows[0] };
            foreach (var r in windows)
            {
                var last = merged[merged.Count - 1];
                if (r[0] <= last[1] + 1) last[1] = Math.Max(last[1], r[1]);
                else merged.Add(new[] { r[0], r[1] });
            }

            const int MaxOutputChars = 6000;
            var sb = new StringBuilder();
            sb.AppendLine("Matches for: " + pattern);
            int blocksEmitted = 0;
            foreach (var m in merged)
            {
                int first = 0, lastLine = 0;
                for (int i = m[0]; i <= m[1]; i++)
                    if (lines[i].Line > 0) { if (first == 0) first = lines[i].Line; lastLine = lines[i].Line; }
                var block = new StringBuilder();
                block.AppendLine(first > 0 ? "--- buffer lines " + first + "–" + lastLine + " ---" : "--- (slot markers only) ---");
                for (int i = m[0]; i <= m[1]; i++)
                    block.AppendLine(lines[i].ToString());

                if (sb.Length + block.Length > MaxOutputChars)
                {
                    int remaining = merged.Count - blocksEmitted;
                    sb.AppendLine("... [" + remaining + " more block(s) truncated — use a more specific pattern]");
                    break;
                }
                sb.Append(block);
                blocksEmitted++;
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>The index of the slot that starts at 1-based <paramref name="line"/>, or -1.</summary>
        public static int SlotAt(IList<int[]> ranges, int line)
        {
            if (ranges == null) return -1;
            for (int i = 0; i < ranges.Count; i++)
                if (ranges[i] != null && ranges[i].Length >= 2 && ranges[i][0] == line) return i;
            return -1;
        }

        public static string NoSlotError(int line)
        {
            return "Error: No embed point found at line " + line +
                   ". Use get_embeditor_source to get current line numbers.";
        }

        /// <summary>get_embed_content: the slot's lines, or "(empty embed)".</summary>
        public static string SlotContent(string text, IList<int[]> ranges, int line)
        {
            int i = SlotAt(ranges, line);
            if (i < 0) return NoSlotError(line);
            var lines = Lines(text);
            int a = ranges[i][0] - 1, b = Math.Max(ranges[i][0], ranges[i][1]) - 1;
            var sb = new StringBuilder();
            bool hasContent = false;
            for (int j = a; j <= b && j < lines.Length; j++)
            {
                if (lines[j].Trim().Length > 0) hasContent = true;
                sb.AppendLine(lines[j]);
            }
            if (!hasContent) return "(empty embed)";
            return sb.ToString().TrimEnd();
        }

        /// <summary>A planned slot write: replace whole lines StartLine..EndLine (1-based) with NewText.</summary>
        public sealed class SlotWrite
        {
            public int SlotIndex;
            public int StartLine, EndLine;
            /// <summary>One past the last character of EndLine (Monaco end column).</summary>
            public int EndCol;
            /// <summary>The replacement, LF line breaks.</summary>
            public string NewText;
            public int LineDelta;
        }

        /// <summary>
        /// write_embed_content's edit, as AppTreeService.WriteEmbedContentByLine makes it: the code replaces every
        /// line of the slot; with <paramref name="reindent"/> each non-empty line gets the embed point's column
        /// indent (column 1 = none). Returns null with <paramref name="error"/> set when line is not a slot start.
        /// </summary>
        public static SlotWrite PlanWrite(string text, IList<int[]> ranges, int line, string code, int column,
            bool reindent, out string error)
        {
            error = null;
            int i = SlotAt(ranges, line);
            if (i < 0) { error = NoSlotError(line); return null; }
            var lines = Lines(text);
            int start = ranges[i][0], end = Math.Max(ranges[i][0], ranges[i][1]);
            if (end > lines.Length) { error = "Error: the embed at line " + line + " runs past the end of the buffer."; return null; }

            string body = (code ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (reindent)
            {
                string indent = column > 1 ? new string(' ', column - 1) : string.Empty;
                var codeLines = body.Split('\n');
                for (int k = 0; k < codeLines.Length; k++)
                    if (!string.IsNullOrEmpty(codeLines[k])) codeLines[k] = indent + codeLines[k];
                body = string.Join("\n", codeLines);
            }

            int newCount = 1;
            foreach (char c in body) if (c == '\n') newCount++;
            return new SlotWrite
            {
                SlotIndex = i,
                StartLine = start,
                EndLine = end,
                EndCol = lines[end - 1].Length + 1,
                NewText = body,
                LineDelta = newCount - (end - start + 1)
            };
        }

        /// <summary>write_embed_content's success text, as the native tool words it.</summary>
        public static string WriteReport(int line, int lineDelta)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Wrote to embed at line " + line + ".");
            if (lineDelta == 0)
                sb.AppendLine("Line count unchanged — get_embeditor_source tokens remain valid.");
            else
                sb.AppendLine("Line count changed by " + (lineDelta > 0 ? "+" : "") + lineDelta
                    + " — call get_embeditor_source again before writing to embeds after line " + line + ".");
            return sb.ToString().Trim();
        }
    }
}
