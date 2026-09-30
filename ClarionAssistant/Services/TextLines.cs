using System;
using System.Collections.Generic;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 1d8d1c49: line access to a big buffer WITHOUT string.Split. On .NET Framework, Split allocates an
    /// int[Length] scratch array (two with string separators) plus every line, so one split of a 3.2M-char
    /// procedure costs ~25-34 MB of large-object heap, the space a 32-bit Clarion runs out of. These walk
    /// the string once and copy only the lines asked for.
    ///
    /// Line rules match <c>text.Replace("\r\n","\n").Replace("\r","\n").Split('\n')</c> exactly: CRLF, a
    /// lone CR and a lone LF each end a line, and the text always has (breaks + 1) lines, so a trailing
    /// break yields a final empty line. Pure BCL, so test harnesses can compile it on its own.
    /// </summary>
    public static class TextLines
    {
        /// <summary>Start and end (exclusive, before the terminator) of every line.</summary>
        public static void LineBounds(string text, out int[] starts, out int[] ends)
        {
            text = text ?? "";
            int breaks = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n') breaks++;
                else if (c == '\r') { breaks++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            }
            starts = new int[breaks + 1];
            ends = new int[breaks + 1];
            int line = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') continue;
                starts[line] = start; ends[line] = i; line++;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            starts[line] = start; ends[line] = text.Length;
        }

        /// <summary>
        /// For each 1-based inclusive [start,end] range, the lines joined with "\n"; "" for a null/short/empty
        /// range. Same result as splitting the whole text and joining lines[start-1..end-1], with the end
        /// clamped to the line count and the start to 1.
        /// </summary>
        public static List<string> ExtractRanges(string text, List<int[]> ranges)
        {
            var result = new List<string>();
            if (ranges == null) return result;
            text = text ?? "";
            int[] starts, ends;
            LineBounds(text, out starts, out ends);
            int lineCount = starts.Length;
            foreach (var r in ranges)
            {
                if (r == null || r.Length < 2) { result.Add(""); continue; }
                int s = Math.Max(1, r[0]), e = Math.Min(lineCount, r[1]);
                if (e < s) { result.Add(""); continue; }
                int len = 0;
                for (int i = s; i <= e; i++) len += (ends[i - 1] - starts[i - 1]) + (i > s ? 1 : 0);
                var sb = new StringBuilder(len);
                for (int i = s; i <= e; i++)
                {
                    if (i > s) sb.Append('\n');
                    sb.Append(text, starts[i - 1], ends[i - 1] - starts[i - 1]);
                }
                result.Add(sb.ToString());
            }
            return result;
        }
    }
}
