using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant
{
    /// <summary>
    /// Reads Clarion's "New Structure" templates from &lt;ClarionRoot&gt;\LibSrc\win\DEFAULTS.CLW — the
    /// SAME file the native editor's Ctrl+D picker uses ("Used by Format New Structure (Ctrl-D) in CW
    /// editor", per its header), so our CA Embeditor picker shows exactly the native list INCLUDING any
    /// user-added templates. Format: a "!!> Title" comment line, then the structure source until the
    /// next "!!>" (task 1f10aa51).
    /// </summary>
    public static class DefaultStructuresReader
    {
        public sealed class StructureTemplate
        {
            public string Title;
            public string Source;   // the structure block, CRLF lines, trailing blanks trimmed
            public string Kind;     // "WINDOW" | "APPLICATION" | "REPORT" — drives the designer flags
        }

        /// <summary>Walk up from the addin assembly (…\accessory\addins\ClarionAssistant) to the install root.</summary>
        public static string FindDefaultsPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(DefaultStructuresReader).Assembly.Location);
                for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                {
                    string candidate = Path.Combine(d.FullName, "LibSrc", "win", "DEFAULTS.CLW");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Parse the templates. Returns an empty list when the file is missing/unreadable —
        /// callers fall back to their hardcoded seed (Ctrl+D keeps working without the picker).</summary>
        public static List<StructureTemplate> Load()
        {
            var outp = new List<StructureTemplate>();
            try
            {
                string path = FindDefaultsPath();
                if (path == null) return outp;

                string title = null;
                var body = new List<string>();
                void Flush()
                {
                    if (title != null)
                    {
                        while (body.Count > 0 && body[body.Count - 1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
                        if (body.Count > 0)
                            outp.Add(new StructureTemplate { Title = title, Source = string.Join("\r\n", body), Kind = Classify(body) });
                    }
                    title = null;
                    body.Clear();
                }

                foreach (string raw in Services.EncodingHelper.ReadAllLines(path, out _))
                {
                    var m = Regex.Match(raw, @"^\s*!!>\s*(.+?)\s*$");
                    if (m.Success) { Flush(); title = m.Groups[1].Value; continue; }
                    if (title == null) continue;   // file preamble before the first !!>
                    body.Add(raw);
                }
                Flush();
            }
            catch { outp.Clear(); }
            return outp;
        }

        // The block's opening declaration decides the designer flags: REPORT -> report designer;
        // APPLICATION (MDI frame) -> window designer with isWindowWindow=false; else a plain WINDOW.
        private static string Classify(List<string> body)
        {
            foreach (string line in body)
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("!")) continue;
                string u = " " + t.ToUpperInvariant();
                if (Regex.IsMatch(u, @"[\s]REPORT[\s,(]")) return "REPORT";
                if (Regex.IsMatch(u, @"[\s]APPLICATION[\s,(]")) return "APPLICATION";
                return "WINDOW";
            }
            return "WINDOW";
        }
    }
}
