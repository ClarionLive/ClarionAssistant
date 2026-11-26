using System;
using System.Collections;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Find All References for the two Monaco surfaces (Shift+F12, the right-click "Go to References" /
    /// "Peek References", and the hover footer's "Find References" link). Turns a textDocument/references
    /// result into the page's reply:
    ///   { locations: [{ path, line, column, endLine, endColumn }], files: { path: previewText } }
    /// 1-based lines and columns. A location in the editor's OWN document has path null and a line of that
    /// document (the host maps it, see <paramref name="selfLine1"/>), so the page points it at its own model.
    ///
    /// Monaco's references view shows a preview of each hit, which needs a model per file. A generated
    /// module can run to 60k lines, so a file is sent SPARSE: only the lines near a hit, the rest empty.
    /// Line numbers stay those of the file; the preview is read-only on the page.
    /// </summary>
    internal static class ReferenceLocations
    {
        private const int MaxLocations = 1000;
        private const int PreviewContextLines = 20;

        /// <summary>Build the page's reply. <paramref name="isSelf"/> says whether a path is the editor's own
        /// document; <paramref name="selfLine1"/> maps a 0-based server line in it to the editor's 1-based
        /// line, or 0 when the line is not in the editor's text (an embeditor's wrapper header).</summary>
        public static Dictionary<string, object> Build(Dictionary<string, object> result,
            Func<string, bool> isSelf, Func<int, int> selfLine1)
        {
            var locations = new List<Dictionary<string, object>>();
            var hitLines = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IDictionary loc in Enumerate(result))
            {
                if (locations.Count >= MaxLocations) break;
                string path = loc.Contains("uri") && loc["uri"] != null ? SharedLspBridge.UriToFilePath(loc["uri"].ToString()) : null;
                if (string.IsNullOrEmpty(path)) continue;
                int sl, sc, el, ec;
                ReadRange(loc["range"] as IDictionary, out sl, out sc, out el, out ec);

                bool self = isSelf(path);
                int line1 = sl + 1, endLine1 = el + 1;
                if (self)
                {
                    line1 = selfLine1(sl);
                    if (line1 < 1) continue;
                    endLine1 = line1 + (el - sl);
                }
                string key = (self ? "" : path) + "|" + line1 + "|" + sc;
                if (!seen.Add(key)) continue;

                locations.Add(new Dictionary<string, object>
                {
                    { "path", self ? null : path },
                    { "line", line1 }, { "column", sc + 1 },
                    { "endLine", endLine1 }, { "endColumn", ec + 1 }
                });
                if (!self)
                {
                    List<int> lines;
                    if (!hitLines.TryGetValue(path, out lines)) hitLines[path] = lines = new List<int>();
                    lines.Add(sl);
                }
            }

            var files = new Dictionary<string, object>();
            foreach (var kv in hitLines)
            {
                string text = SparsePreview(kv.Key, kv.Value);
                if (text != null) files[kv.Key] = text;
            }
            return new Dictionary<string, object> { { "locations", locations }, { "files", files } };
        }

        private static IEnumerable Enumerate(Dictionary<string, object> result)
        {
            object res;
            if (result == null || !result.TryGetValue("result", out res) || res == null) yield break;
            if (res is IDictionary) { yield return res; yield break; }
            var list = res as IEnumerable;
            if (list == null) yield break;
            foreach (var item in list)
                if (item is IDictionary) yield return item;
        }

        private static void ReadRange(IDictionary range, out int sl, out int sc, out int el, out int ec)
        {
            sl = sc = el = ec = 0;
            try
            {
                var start = range != null && range.Contains("start") ? range["start"] as IDictionary : null;
                var end = range != null && range.Contains("end") ? range["end"] as IDictionary : null;
                if (start != null)
                {
                    if (start.Contains("line")) sl = Convert.ToInt32(start["line"]);
                    if (start.Contains("character")) sc = Convert.ToInt32(start["character"]);
                }
                el = sl; ec = sc;
                if (end != null)
                {
                    if (end.Contains("line")) el = Convert.ToInt32(end["line"]);
                    if (end.Contains("character")) ec = Convert.ToInt32(end["character"]);
                }
            }
            catch { }
        }

        // The file as saved, with every line further than PreviewContextLines from a hit left empty.
        private static string SparsePreview(string path, List<int> hitLines0)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                string text = EncodingHelper.ReadAllText(path, out _);
                string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                var keep = new bool[lines.Length];
                foreach (int h in hitLines0)
                    for (int i = Math.Max(0, h - PreviewContextLines); i <= Math.Min(lines.Length - 1, h + PreviewContextLines); i++)
                        keep[i] = true;
                for (int i = 0; i < lines.Length; i++)
                    if (!keep[i]) lines[i] = "";
                return string.Join("\n", lines);
            }
            catch { return null; }
        }
    }
}
