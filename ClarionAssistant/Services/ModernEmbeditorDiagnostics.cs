using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Path B — Modern Embeditor diagnostics (squiggles). Produces a unified marker list for Monaco
    /// from a HYBRID of three sources, because the Clarion LSP alone is near-blind to embed-slot errors:
    ///
    ///   1. The Clarion LSP's STRUCTURAL diagnostics (unterminated IF/LOOP/CASE, missing RETURN, FILE
    ///      without DRIVER/RECORD, …), CLAMPED to editable embed slots. The server validates the whole
    ///      generated buffer, where a user's unterminated IF in a slot is balanced/masked by the
    ///      surrounding generated ENDs (so it reports 0, or flags the wrong line near EOF), and any
    ///      marker landing on a read-only generated line is noise. We keep only entries inside a slot.
    ///      (Confirmed from source — DiagnosticProvider.validateDocument; see docs/ModernEmbeditor-PathA.md.)
    ///
    ///   2. PER-SLOT structure balance — an opener (IF/LOOP/CASE/GROUP/QUEUE/…) without a matching END
    ///      or '.' WITHIN the same slot, or a stray END/'.'. This is the high-value check the
    ///      whole-buffer LSP cannot do, and is exactly the case the developer cares about.
    ///
    ///   3. UNDEFINED ROUTINE — a 'DO &lt;name&gt;' whose &lt;name&gt; is not a ROUTINE declared in this
    ///      procedure (routine set parsed from the assembled buffer via ClarionAppDataReader).
    ///
    /// Markers are 1-based {line,column,endLine,endColumn,message,severity} carrying Monaco's
    /// MarkerSeverity (Error=8, Warning=4, Info=2, Hint=1) so the HTML renders them with no translation.
    /// </summary>
    public static class ModernEmbeditorDiagnostics
    {
        // Monaco MarkerSeverity values (rendered directly by setModelMarkers).
        private const int SevError = 8;
        private const int SevWarning = 4;
        private const int SevInfo = 2;
        private const int SevHint = 1;

        // Structures that open a block needing END or '.' — mirrors the folding STRUCT set in
        // monaco-embeditor.html so balance detection matches what the editor folds.
        // Matched ONLY in DECLARATION/STATEMENT position — line start, an optional label, then the
        // keyword as a whole token (followed by whitespace / ',' / '(' / end-of-line). Without this
        // anchor the keyword matched ANYWHERE on the line, so a reference like BIND(AUT:RECORD) or a
        // prefixed name like Loop:Counter was treated as an opener — pushing bogus entries that then
        // swallowed real ENDs and made every genuine IF read as "not terminated" (GitHub #40 follow-up).
        // The lookahead (not [,(]|$ like BandOpen) deliberately allows code structures that take an
        // expression: LOOP I = 1 TO 10, CASE SomeVar, EXECUTE n.
        private static readonly Regex StructOpen = new Regex(
            @"^\s*(?:[A-Za-z_][A-Za-z0-9_:]*\s+)?(GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|MENUBAR|MENU|TOOLBAR|SHEET|TAB|OPTION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|LOOP|CASE|BEGIN|EXECUTE|ACCEPT)(?=\s|,|\(|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StructWordRx = new Regex(
            @"\b(IF|GROUP|QUEUE|RECORD|FILE|VIEW|REPORT|WINDOW|APPLICATION|MENUBAR|MENU|TOOLBAR|SHEET|TAB|OPTION|CLASS|INTERFACE|MAP|MODULE|ITEMIZE|JOIN|LOOP|CASE|BEGIN|EXECUTE|ACCEPT)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // REPORT band structures (HEADER/FOOTER/FORM/DETAIL) — each takes an END, so the balance check
        // must count them or their ENDs read as stray ("END has no matching structure"). Matched ONLY in
        // declaration position — line start, an optional label, then the keyword immediately followed by
        // ',' / '(' / end-of-line — so it never trips on a control's USE(?Header) or a 'Header ROUTINE'
        // label. BREAK is deliberately excluded (a bare BREAK is a loop statement in code slots).
        private static readonly Regex BandOpen = new Regex(
            @"^\s*(?:[A-Za-z_][A-Za-z0-9_:]*\s+)?(HEADER|FOOTER|FORM|DETAIL)\s*(?:[,(]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EndRx = new Regex(@"^END\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IfRx = new Regex(@"^IF\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // 'DO RoutineName' — DO must start the statement (line start, whitespace, or after ';').
        private static readonly Regex DoStmt = new Regex(
            @"(?:^|\s|;)DO\s+([A-Za-z_][A-Za-z0-9_:]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TrailingDot = new Regex(@"\.\s*$", RegexOptions.Compiled);
        private static readonly Regex InlineEnd = new Regex(@"\bEND\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Build the marker list. <paramref name="ranges"/> are 1-based inclusive [start,end] editable
        /// slot ranges (live — passed from Monaco's tracked decorations so they reflect edits that grew
        /// a slot). Returns an empty list in mirror mode (no editable slots).
        ///
        /// <paramref name="embedSlotChecks"/>: when false (plain-source FILE MODE — ticket 564aa142),
        /// only Pass 1 (the real Clarion LSP, spanning the whole file) runs. The per-slot structure-balance
        /// heuristic (Passes 2 &amp; 3) is designed for tiny embed fragments and mis-reads a full class/.inc:
        /// it matches FILE/GROUP/QUEUE/etc. used as PARAMETER TYPES (e.g. <c>Procedure(*File pTable)</c>)
        /// or labels as if a structure opened, producing bogus "FILE is not terminated with END" errors.
        /// The LSP/compiler does real whole-file structure validation, so the heuristic adds only noise.
        /// </summary>
        public static List<Dictionary<string, object>> Compute(
            string lspFileName, string buffer, List<int[]> ranges, string procedureName,
            bool embedSlotChecks = true)
        {
            var markers = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(buffer) || ranges == null || ranges.Count == 0) return markers;

            string[] lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            // ---- Pass 1: LSP structural diagnostics, clamped to editable slots ----
            try
            {
                // Route through SharedLspBridge: shared ClarionLsp when active, else the bundled LspClient.
                if (SharedLspBridge.IsRunning && !string.IsNullOrEmpty(lspFileName))
                {
                    SharedLspBridge.EnsureBufferSynced(lspFileName, buffer);
                    var wait = SharedLspBridge.WaitForDiagnostics(lspFileName, 1500, true);
                    List<LspClient.DiagnosticEntry> entries =
                        (wait != null && !wait.Pending && wait.Entries != null)
                            ? wait.Entries
                            : (SharedLspBridge.GetCachedDiagnostics(lspFileName) ?? new List<LspClient.DiagnosticEntry>());

                    foreach (var d in entries)
                    {
                        // DiagnosticEntry is 0-based; ranges are 1-based inclusive.
                        int line1 = d.Line + 1;
                        if (!InAnyRange(line1, ranges)) continue; // drop generated-line noise / mislocations
                        markers.Add(Marker(line1, d.Character + 1, d.EndLine + 1, d.EndCharacter + 1,
                            string.IsNullOrEmpty(d.Message) ? "Clarion diagnostic" : d.Message,
                            LspSevToMonaco(d.Severity)));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorDiagnostics] LSP pass: " + ex.Message);
            }

            // File mode (whole-source): stop here. The LSP pass above already covers the whole file;
            // the per-slot heuristics below mis-fire on declaration files (FILE/GROUP/... as param types).
            if (!embedSlotChecks) return markers;

            // Routine set for the undefined-routine check (only flag when we actually parsed routines,
            // so a parse failure never produces false positives).
            HashSet<string> routines;
            try
            {
                routines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parsed = ClarionAppDataReader.ParseRoutines(buffer, procedureName);
                if (parsed != null) foreach (var r in parsed) routines.Add(r.Name);
            }
            catch { routines = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

            // ---- Passes 2 & 3: per-slot structure balance + undefined routine ----
            foreach (var r in ranges)
            {
                if (r == null || r.Length < 2) continue;
                int s = Math.Max(1, r[0]);
                int e = Math.Min(lines.Length, r[1]);
                if (e < s) continue;

                var open = new Stack<int[]>(); // [line1, col1] for each unmatched opener within this slot
                for (int ln = s; ln <= e; ln++)
                {
                    string code = Sanitize(lines[ln - 1]); // blanks comments + string interiors, preserves columns
                    string trimmed = code.Trim();
                    if (trimmed.Length == 0) continue;
                    string u = trimmed.ToUpperInvariant();

                    // Close: a line beginning with END..., or a lone '.'
                    if (EndRx.IsMatch(u) || u == ".")
                    {
                        if (open.Count > 0) open.Pop();
                        else
                            markers.Add(Marker(ln, FirstNonWs(code) + 1, ln, code.Length + 1,
                                "END has no matching structure in this embed slot.", SevWarning));
                        continue;
                    }

                    // Block IF only — skip one-liners: 'IF .. THEN stmt' or a trailing '.' terminator.
                    if (IfRx.IsMatch(u))
                    {
                        int thenIdx = u.IndexOf(" THEN", StringComparison.Ordinal);
                        string afterThen = thenIdx >= 0 ? trimmed.Substring(thenIdx + 5).Trim() : "";
                        bool oneLiner = afterThen.Length > 0 || TrailingDot.IsMatch(trimmed);
                        if (!oneLiner) open.Push(new[] { ln, FirstNonWs(code) + 1 });
                        // fall through so a 'DO' on the same line is still checked
                    }
                    else if (StructOpen.IsMatch(u) || BandOpen.IsMatch(u))
                    {
                        // Skip a self-terminated inline structure (trailing '.' or an END later on the
                        // same line, e.g. "EXECUTE n; a; b END") — only multi-line openers are tracked.
                        bool selfTerminated = TrailingDot.IsMatch(trimmed) || InlineEnd.IsMatch(u);
                        if (!selfTerminated) open.Push(new[] { ln, FirstNonWs(code) + 1 });
                    }

                    // Undefined routine: DO <name>
                    if (routines.Count > 0)
                    {
                        var m = DoStmt.Match(code);
                        if (m.Success)
                        {
                            string name = m.Groups[1].Value;
                            if (!routines.Contains(name))
                            {
                                int col = m.Groups[1].Index + 1;
                                markers.Add(Marker(ln, col, ln, col + name.Length,
                                    "Routine '" + name + "' is not defined in this procedure.", SevWarning));
                            }
                        }
                    }
                }

                // Unmatched openers left on the stack → unterminated within this slot.
                while (open.Count > 0)
                {
                    var o = open.Pop();
                    string word = StructWord(lines[o[0] - 1]);
                    markers.Add(Marker(o[0], o[1], o[0], o[1] + Math.Max(1, word.Length),
                        word + " is not terminated with END or '.' in this embed slot.", SevError));
                }
            }

            return markers;
        }

        private static bool InAnyRange(int line1, List<int[]> ranges)
        {
            foreach (var r in ranges)
                if (r != null && r.Length >= 2 && line1 >= r[0] && line1 <= r[1]) return true;
            return false;
        }

        private static Dictionary<string, object> Marker(int line, int col, int endLine, int endCol, string msg, int sev)
        {
            return new Dictionary<string, object>
            {
                { "line", line }, { "column", col }, { "endLine", endLine }, { "endColumn", endCol },
                { "message", msg }, { "severity", sev }
            };
        }

        private static int LspSevToMonaco(int lspSeverity)
        {
            switch (lspSeverity)
            {
                case 1: return SevError;
                case 2: return SevWarning;
                case 3: return SevInfo;
                case 4: return SevHint;
                default: return SevWarning;
            }
        }

        // Returns the structure keyword on a line (for the unterminated-structure message), or "Structure".
        private static string StructWord(string rawLine)
        {
            var m = StructWordRx.Match(Sanitize(rawLine ?? "").Trim());
            return m.Success ? m.Value.ToUpperInvariant() : "Structure";
        }

        private static int FirstNonWs(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (!char.IsWhiteSpace(s[i])) return i;
            return 0;
        }

        // Blank out '!' line-comments and Clarion string-literal interiors while preserving the line's
        // length, so structure/DO detection never trips on the word "END"/"IF"/"DO" inside a string or
        // comment, and column positions of real code stay accurate.
        private static string Sanitize(string line)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            var sb = new StringBuilder(line.Length);
            bool inStr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\'')
                    {
                        // Doubled '' is an escaped quote inside the string — stays inside.
                        if (i + 1 < line.Length && line[i + 1] == '\'') { sb.Append(' '); sb.Append(' '); i++; continue; }
                        inStr = false; sb.Append('\''); continue;
                    }
                    sb.Append(' '); continue;
                }
                if (c == '\'') { inStr = true; sb.Append('\''); continue; }
                if (c == '!') { for (int j = i; j < line.Length; j++) sb.Append(' '); break; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
