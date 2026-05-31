using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Reads the global table (FILE) declarations from a generated Clarion app's PROGRAM module
    /// (&lt;app&gt;.clw). That file lists every dictionary file the app uses, with its driver, prefix,
    /// keys, and record fields — all plain text, no native/TPS access needed. Used by the Modern Data
    /// pad to show the tables/columns a procedure references.
    /// </summary>
    public static class ClarionAppDataReader
    {
        public sealed class FieldDef { public string Name; public string Type; }

        public sealed class TableDef
        {
            public string Name;
            public string Prefix = "";
            public readonly List<FieldDef> Fields = new List<FieldDef>();
            public readonly List<string> Keys = new List<string>();
        }

        /// <summary>Locate the generated &lt;app&gt;.clw (PROGRAM module) for the currently-open app, or null.</summary>
        public static string FindAppClwPath()
        {
            try
            {
                var info = new AppTreeService().GetAppInfo();
                if (info == null || !info.ContainsKey("fileName")) return null;
                string appFile = info["fileName"]?.ToString();
                if (string.IsNullOrEmpty(appFile)) return null;

                string dir = Path.GetDirectoryName(appFile);
                string baseName = Path.GetFileNameWithoutExtension(appFile);
                if (string.IsNullOrEmpty(baseName)) return null;
                string clwName = baseName + ".clw";

                // Primary: use the loaded .red redirection to find where generation puts the .clw
                // (this is the same resolution used to feed the CodeGraph). Try the common config sections.
                var red = RedFileService.Active;
                if (red != null)
                {
                    string viaRed = red.Resolve(clwName, "Debug", "Release", "Common")
                                 ?? red.Resolve(clwName);
                    if (!string.IsNullOrEmpty(viaRed) && File.Exists(viaRed)) return viaRed;
                }

                // Fallback: same directory as the .app.
                if (!string.IsNullOrEmpty(dir))
                {
                    string candidate = Path.Combine(dir, clwName);
                    if (File.Exists(candidate)) return candidate;
                    foreach (var f in Directory.GetFiles(dir, "*.clw"))
                        if (string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase))
                            return f;
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Parse FILE...RECORD...END blocks from a generated .clw. Tracks structure depth so nested
        /// GROUP/QUEUE members are flattened into the field list and the table closes on the FILE's END.
        /// </summary>
        public static List<TableDef> ParseTables(string clwPath)
        {
            var tables = new List<TableDef>();
            string[] lines;
            try { lines = File.ReadAllLines(clwPath); }
            catch { return tables; }

            TableDef cur = null;
            int depth = 0; // structure depth inside a FILE: FILE=1, RECORD/GROUP/QUEUE each +1
            foreach (var raw in lines)
            {
                string line = StripComment(raw);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var m = Regex.Match(line, @"^(\s*)(\S+)\s*(.*)$");
                if (!m.Success) continue;
                string label = m.Groups[2].Value;
                string rest = m.Groups[3].Value.Trim();
                string restU = rest.ToUpperInvariant();

                if (cur == null)
                {
                    if (restU.StartsWith("FILE,") || restU == "FILE" || restU.StartsWith("FILE "))
                    {
                        cur = new TableDef { Name = label, Prefix = ExtractPre(rest) };
                        depth = 1;
                    }
                    continue;
                }

                if (restU.StartsWith("RECORD") || restU.StartsWith("GROUP") || restU.StartsWith("QUEUE"))
                {
                    depth++;
                    continue;
                }
                if (label.ToUpperInvariant() == "END" && rest.Length == 0)
                {
                    depth--;
                    if (depth <= 0) { tables.Add(cur); cur = null; depth = 0; }
                    continue;
                }
                if (restU.StartsWith("KEY(") || restU.StartsWith("INDEX("))
                {
                    cur.Keys.Add(label);
                    continue;
                }
                if (depth >= 2 && rest.Length > 0)
                {
                    cur.Fields.Add(new FieldDef { Name = label, Type = rest });
                }
            }
            return tables;
        }

        // Clarion statement/control keywords that can appear at column 1 in code but are NOT data.
        private static readonly HashSet<string> StatementKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DO","CASE","OF","OROF","IF","ELSIF","ELSE","END","LOOP","WHILE","UNTIL","EXIT","RETURN",
            "BREAK","CYCLE","BEGIN","EXECUTE","THEN","NEW","DISPOSE","ACCEPT","ASSERT","COMPILE","OMIT","SECTION"
        };

        private static readonly Regex StructOpener = new Regex(
            @"^(WINDOW|REPORT|MENUBAR|TOOLBAR|SHEET|TAB|MENU|OPTION|GROUP|QUEUE|RECORD|CLASS|VIEW|JOIN|MAP|MODULE|ITEMIZE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Parse a procedure's local data declarations from its assembled embeditor source — the
        /// "Label TYPE" lines in the data section (between "&lt;Proc&gt; PROCEDURE" and CODE). Structure
        /// interiors (WINDOW/CLASS/QUEUE members) are skipped (the structure's own label is kept), comments
        /// are stripped, and names are deduped. This is reliable (unlike LSP documentSymbol on this buffer,
        /// which emits code-token noise).
        /// </summary>
        public static List<FieldDef> ParseLocalData(string source, string procName)
        {
            var outp = new List<FieldDef>();
            if (string.IsNullOrEmpty(source)) return outp;
            var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            int start = 0;
            if (!string.IsNullOrEmpty(procName))
            {
                var rx = new Regex(@"^\s*" + Regex.Escape(procName) + @"\s+(PROCEDURE|FUNCTION)\b", RegexOptions.IgnoreCase);
                for (int i = 0; i < lines.Length; i++)
                    if (rx.IsMatch(lines[i])) { start = i + 1; break; }
            }

            int depth = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < lines.Length && outp.Count < 1000; i++)
            {
                string line = StripComment(lines[i]);
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var m = Regex.Match(line, @"^(\s*)(\S+)\s*(.*)$");
                if (!m.Success) continue;
                string label = m.Groups[2].Value;
                string rest = m.Groups[3].Value.Trim();
                string restU = rest.ToUpperInvariant();
                string labelU = label.ToUpperInvariant();

                if (depth > 0)
                {
                    if (StructOpener.IsMatch(restU)) depth++;
                    else if (labelU == "END" && rest.Length == 0) depth--;
                    continue;
                }

                if (labelU == "CODE") break;                 // end of the data section (start of code)
                if (restU.StartsWith("ROUTINE")) break;      // routines follow the code — nothing past here is data
                if (labelU == "END") continue;

                if (StructOpener.IsMatch(restU))
                {
                    if (IsIdent(label) && seen.Add(label))
                        outp.Add(new FieldDef { Name = label, Type = FirstWord(rest) });
                    depth = 1; // skip the structure's members
                    continue;
                }

                if (StatementKeywords.Contains(labelU)) continue; // DO/CASE/OF/IF/LOOP/… are code, not data
                if (rest.Length > 0 && IsIdent(label) && seen.Add(label))
                    outp.Add(new FieldDef { Name = label, Type = rest });
            }
            return outp;
        }

        /// <summary>Collect the procedure's ROUTINE names from its source (the "&lt;name&gt; ROUTINE" lines).</summary>
        public static List<string> ParseRoutines(string source, string procName)
        {
            var outp = new List<string>();
            if (string.IsNullOrEmpty(source)) return outp;
            var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            int start = 0;
            if (!string.IsNullOrEmpty(procName))
            {
                var rx = new Regex(@"^\s*" + Regex.Escape(procName) + @"\s+(PROCEDURE|FUNCTION)\b", RegexOptions.IgnoreCase);
                for (int i = 0; i < lines.Length; i++)
                    if (rx.IsMatch(lines[i])) { start = i + 1; break; }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < lines.Length; i++)
            {
                string label, rest;
                SplitLabelRest(StripComment(lines[i]), out label, out rest);
                if (label == null) continue;
                if (rest.ToUpperInvariant().StartsWith("ROUTINE") && IsIdent(label) && seen.Add(label))
                    outp.Add(label);
            }
            return outp;
        }

        /// <summary>
        /// Parse global variable declarations from the generated &lt;app&gt;.clw — the top-level "Label TYPE"
        /// items after the global MAP and outside FILE/structure blocks (those are shown as Tables).
        /// </summary>
        public static List<FieldDef> ParseGlobalData(string clwPath)
        {
            var outp = new List<FieldDef>();
            string[] lines;
            try { lines = File.ReadAllLines(clwPath); }
            catch { return outp; }

            // Skip the global MAP block (MAP ... nested MODULE...END ... END).
            int i = 0;
            bool mapSeen = false;
            int mapDepth = 0;
            for (; i < lines.Length; i++)
            {
                string label, rest;
                SplitLabelRest(StripComment(lines[i]), out label, out rest);
                if (label == null) continue;
                string lu = label.ToUpperInvariant();
                if (!mapSeen)
                {
                    if (lu == "MAP") { mapSeen = true; mapDepth = 1; }
                    continue;
                }
                if (lu.StartsWith("MODULE") || lu == "MAP") mapDepth++;
                else if (lu == "END") { mapDepth--; if (mapDepth <= 0) { i++; break; } }
            }
            if (!mapSeen) i = 0;

            int depth = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (; i < lines.Length && outp.Count < 2000; i++)
            {
                string label, rest;
                SplitLabelRest(StripComment(lines[i]), out label, out rest);
                if (label == null) continue;
                string restU = rest.ToUpperInvariant();
                string labelU = label.ToUpperInvariant();

                if (depth > 0)
                {
                    if (restU.StartsWith("FILE") || StructOpener.IsMatch(restU)) depth++;
                    else if (labelU == "END" && rest.Length == 0) depth--;
                    continue;
                }

                if (labelU == "CODE") break;            // program code begins
                if (labelU == "END") continue;
                if (restU.StartsWith("FILE")) { depth = 1; continue; }   // a table — shown in the Tables scope
                if (StructOpener.IsMatch(restU))
                {
                    if (IsIdent(label) && seen.Add(label)) outp.Add(new FieldDef { Name = label, Type = FirstWord(rest) });
                    depth = 1;
                    continue;
                }
                if (StatementKeywords.Contains(labelU)) continue;
                if (rest.Length > 0 && IsIdent(label) && seen.Add(label))
                    outp.Add(new FieldDef { Name = label, Type = rest });
            }
            return outp;
        }

        private static void SplitLabelRest(string line, out string label, out string rest)
        {
            label = null; rest = "";
            var m = Regex.Match(line ?? "", @"^(\s*)(\S+)\s*(.*)$");
            if (m.Success) { label = m.Groups[2].Value; rest = m.Groups[3].Value.Trim(); }
        }

        private static bool IsIdent(string s)
        {
            return !string.IsNullOrEmpty(s) && Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*$");
        }

        private static string FirstWord(string s)
        {
            var m = Regex.Match(s ?? "", @"^[A-Za-z]+");
            return m.Success ? m.Value : (s ?? "");
        }

        private static string ExtractPre(string fileAttrs)
        {
            var m = Regex.Match(fileAttrs, @"PRE\(\s*(\w*)\s*\)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string StripComment(string line)
        {
            if (line == null) return "";
            int bang = line.IndexOf('!');
            return bang >= 0 ? line.Substring(0, bang) : line;
        }
    }
}
