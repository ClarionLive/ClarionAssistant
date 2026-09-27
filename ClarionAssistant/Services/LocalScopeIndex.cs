using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ClarionCodeGraph.Parsing;

namespace ClarionAssistant.Services
{
    /// <summary>A buffer-local hover answer. <see cref="Authoritative"/> is true when the word resolved to
    /// something declared right here (a local, a parameter, a routine, or a module-local procedure), so no
    /// other source can name a better declaration and the caller may skip the language server.</summary>
    public sealed class LocalHoverResult
    {
        public string Markdown;
        public bool Authoritative;
        /// <summary>"local", "parameter", "routine", "procedure" or "member" - what resolved it.</summary>
        public string Kind;
    }

    /// <summary>What sits left of the caret in an "instance.partial" context, as far as the buffer can
    /// tell. <see cref="LocalClass"/> is set when the instance is (or SELF means) a CLASS declared in
    /// scope; <see cref="BaseType"/> is the type a library/project index must supply members for (the
    /// local class's parent, or a local variable's declared class type). Either may be null.</summary>
    public sealed class LocalMemberAccess
    {
        public string Instance;
        public string Partial;
        public string LocalClass;
        public string BaseType;
    }

    /// <summary>
    /// Buffer-local completion and hover for Clarion source: the enclosing procedure's and routine's
    /// DATA, its PROTOTYPE parameters, module data, MAP procedures, routines, GROUP/QUEUE fields and the
    /// members of a CLASS declared in scope. Answers in milliseconds from the text alone - no language
    /// server, no SQLite, no IDE (1c685f2e).
    ///
    /// NEVER SPLITS THE WHOLE BUFFER. The CA Embeditor's buffer is a whole generated module (86k lines,
    /// 3.2 MB on InventoryTable), and it is a new string on every edit. So:
    ///  - the caret's line is found by counting newlines from a per-instance anchor, not by Split;
    ///  - the enclosing PROCEDURE/ROUTINE header and its DATA are found by walking OUTWARD from the caret,
    ///    line by line, by index; only the DATA lines that can matter (column-1 labels and END lines)
    ///    are ever turned into strings;
    ///  - the module header (MEMBER to the first procedure: module data + MAP) is parsed once per
    ///    distinct header text, keyed by a content hash, so a new buffer instance with an unchanged
    ///    header costs one hash walk over the header and nothing else;
    ///  - the list of procedure implementations in the buffer (for "local procedure" completion/hover)
    ///    is one allocation-free walk per buffer INSTANCE, cached by reference.
    ///
    /// SharedLspBridge's late merge (after the LSP) calls this same code, so the local layer and the
    /// merged list cannot disagree about what is in scope.
    ///
    /// Two scope rules differ from the code this was extracted from (both covered by the golden parity
    /// harness, tests\CompletionMerge.LocalParity.ps1):
    ///  - A column-1 "Name PROCEDURE(...)" line inside a CLASS/INTERFACE/MAP/MODULE block is a
    ///    PROTOTYPE, not a procedure header. Every ABC procedure declares "ThisWindow CLASS(...)" with
    ///    column-1 method prototypes in its DATA, and treating those as headers cut the procedure's DATA
    ///    off at the class, hid every local declared above it, and offered "Init"/"Kill" as local
    ///    procedures.
    ///  - Inside a local class's method ("ThisWindow.Init PROCEDURE"), the owning procedure's DATA and
    ///    parameters are in scope too, which is where most embeds are edited.
    /// </summary>
    public static class LocalScopeIndex
    {
        // ============================================================================ public API

        /// <summary>Completion items from the buffer alone at (0-based line, 0-based column). Covers bare
        /// prefixes (locals, parameters, module data, local procedures, no-PRE fields), "DO " (routines of
        /// the current procedure only), "PRE:" (fields of a PRE'd GROUP/QUEUE in scope) and "x." (fields of
        /// a GROUP/QUEUE, or members of a CLASS declared in scope, SELF included). Empty inside a string or
        /// comment and in a "?" field-equate context. Never throws.</summary>
        public static List<LspClient.CompletionItemInfo> Complete(string buffer, int line0, int col0, char? trigger)
        {
            var items = new List<LspClient.CompletionItemInfo>();
            try
            {
                var scope = GetScope(buffer, line0);
                if (scope == null) return items;
                string lineText = scope.CaretLine;
                int col = col0 < 0 ? 0 : (col0 > lineText.Length ? lineText.Length : col0);
                if (IsInsideStringOrComment(lineText, col)) return items;
                string upTo = lineText.Substring(0, col);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // "x." / "PRE:" qualified contexts.
                var q = QualifierPattern.Match(upTo);
                if (q.Success)
                {
                    string qualifier = q.Groups[1].Value;
                    char sep = q.Groups[2].Value[0];
                    string partial = q.Groups[3].Value;
                    if (sep == ':')
                    {
                        scope.AddQualifiedFields(qualifier, ':', partial, seen, items);
                        // Clarion labels carry colons ("LOC:Count"): offer the in-scope labels that start
                        // with the typed colon run too. The page widens the replace range over that run.
                        string colonPrefix = qualifier + ":" + partial;
                        scope.AddLocals(colonPrefix, seen, items, includeParams: true);
                        scope.AddModuleVars(colonPrefix, seen, items);
                        return items;
                    }
                    // Dotted: a GROUP/QUEUE's fields, else a CLASS in scope (or SELF).
                    if (q.Index > 0 && upTo[q.Index - 1] == '.') return items;   // multi-level chain
                    scope.AddQualifiedFields(qualifier, '.', partial, seen, items);
                    if (items.Count == 0) scope.AddClassMembers(qualifier, partial, seen, items);
                    return items;
                }

                var m = PrefixPattern.Match(upTo);
                string prefix = m.Success ? m.Value : "";
                if (m.Success && m.Index > 0)
                {
                    char before = upTo[m.Index - 1];
                    if (before == '?' || before == '.' || before == ':') return items;
                }

                if (DoStatement.IsMatch(upTo) || DoStatementEmpty.IsMatch(upTo))
                {
                    scope.AddRoutines(prefix, seen, items, null);
                    return items;
                }
                if (prefix.Length < 1) return items;

                scope.AddLocals(prefix, seen, items, includeParams: true);
                scope.AddModuleVars(prefix, seen, items);
                scope.AddLocalProcedures(prefix, seen, items);
                scope.AddNoPreFields(prefix, seen, items);
            }
            catch { }
            return items;
        }

        /// <summary>Hover from the buffer alone at (0-based line, 0-based column), or null. Resolves, in
        /// order: the enclosing routine's, procedure's (and owning procedure's) DATA and parameters, module
        /// data, MAP procedures, procedure implementations in the buffer, routines of the current
        /// procedure, and "x.member" on a CLASS declared in scope. Keywords are NOT answered here.
        /// <paramref name="fileName"/> (optional) is shown in the card's detail line. Never throws.</summary>
        public static LocalHoverResult Hover(string buffer, int line0, int col0, string fileName = null)
        {
            try
            {
                var scope = GetScope(buffer, line0);
                if (scope == null) return null;
                string lineText = scope.CaretLine;
                int col = col0 < 0 ? 0 : (col0 > lineText.Length ? lineText.Length : col0);
                if (IsInsideStringOrComment(lineText, col)) return null;

                Match tok = null;
                foreach (Match mm in WordPattern.Matches(lineText))
                    if (col >= mm.Index && col <= mm.Index + mm.Length) { tok = mm; break; }
                if (tok == null) return null;

                string token = tok.Value;
                int rel = col - tok.Index;
                int dotBefore = rel > 0 ? token.LastIndexOf('.', Math.Min(rel, token.Length) - 1) : -1;
                if (dotBefore > 0)
                {
                    // Caret on a member segment: "instance.member" (single level only).
                    string instance = token.Substring(0, dotBefore);
                    if (instance.IndexOf('.') >= 0) return null;
                    int nextDot = token.IndexOf('.', dotBefore + 1);
                    string member = nextDot < 0 ? token.Substring(dotBefore + 1) : token.Substring(dotBefore + 1, nextDot - dotBefore - 1);
                    return scope.MemberHover(instance, member, fileName);
                }
                int firstDot = token.IndexOf('.');
                string word = firstDot > 0 ? token.Substring(0, firstDot) : token;
                return scope.HoverWord(word, fileName);
            }
            catch { return null; }
        }

        /// <summary>The "instance.partial" context left of the caret, resolved against the buffer: which
        /// local CLASS it names (SELF inside a local class's method included) and the type whose members
        /// only an index can supply (that class's parent, or a local's declared class type). Null when the
        /// caret is not in a single-level member-access context. Never throws.</summary>
        public static LocalMemberAccess GetMemberAccess(string buffer, int line0, int col0)
        {
            try
            {
                var scope = GetScope(buffer, line0);
                if (scope == null) return null;
                string lineText = scope.CaretLine;
                int col = col0 < 0 ? 0 : (col0 > lineText.Length ? lineText.Length : col0);
                if (IsInsideStringOrComment(lineText, col)) return null;
                string upTo = lineText.Substring(0, col);
                var m = MemberAccessPattern.Match(upTo);
                if (!m.Success || (m.Index > 0 && upTo[m.Index - 1] == '.')) return null;
                var r = new LocalMemberAccess { Instance = m.Groups[1].Value, Partial = m.Groups[2].Value };
                var cls = scope.ResolveClassBlock(r.Instance);
                if (cls != null) { r.LocalClass = cls.Name; r.BaseType = cls.Parent; }
                else r.BaseType = scope.DeclaredTypeOf(r.Instance);
                return r;
            }
            catch { return null; }
        }

        /// <summary>The Clarion word (dotted/colon labels included, e.g. "INV:Qty", "SELF.Init") spanning
        /// 0-based column <paramref name="col0"/> of <paramref name="lineText"/>, or null. For the
        /// dictionary and keyword hovers, which take a word rather than a caret.</summary>
        public static string WordAt(string lineText, int col0)
        {
            if (string.IsNullOrEmpty(lineText)) return null;
            foreach (Match m in WordPattern.Matches(lineText))
                if (col0 >= m.Index && col0 <= m.Index + m.Length) return m.Value;
            return null;
        }

        /// <summary>Test hook: how many times a module header has been parsed (a cache miss).</summary>
        public static int HeaderParseCount { get { return _headerParseCount; } }
        private static int _headerParseCount;

        /// <summary>Test hook: drop every cache (header, per-instance line anchors and procedure lists).</summary>
        public static void ResetCaches()
        {
            lock (_headerLock) { _headers.Clear(); _headerOrder.Clear(); }
            lock (_instLock) { for (int i = 0; i < _instances.Length; i++) _instances[i] = null; }
        }

        // ============================================================================ late-merge entry points
        // SharedLspBridge's merges (run after the LSP) call these, so the two paths share one parser.

        /// <summary>The scope at (0-based) <paramref name="line0"/>, or null when the line is out of range.</summary>
        internal static Scope GetScope(string buffer, int line0)
        {
            if (string.IsNullOrEmpty(buffer) || line0 < 0) return null;
            int origin = Origin(buffer);
            int cls = LineStartOf(buffer, line0, origin);
            if (cls < 0) return null;
            return new Scope(buffer, origin, cls);
        }

        // ============================================================================ regexes (per line)

        // Identifier prefix immediately left of the cursor.
        private static readonly Regex PrefixPattern = new Regex(@"[A-Za-z_][A-Za-z0-9_]*$");
        // Clarion identifier under the cursor (dotted/colon labels included) - mirrors server.ts getWordAtPosition.
        private static readonly Regex WordPattern = new Regex(@"[A-Za-z_][A-Za-z0-9_:.]*");
        // "DO Refr|": DO takes a ROUTINE label and nothing else.
        internal static readonly Regex DoStatement = new Regex(@"^\s*DO\s+[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.IgnoreCase);
        private static readonly Regex DoStatementEmpty = new Regex(@"^\s*DO\s+$", RegexOptions.IgnoreCase);
        // A data declaration: a column-1 label (group 1) followed by its type/rest-of-line (group 2).
        internal static readonly Regex DataLabelPattern = new Regex(@"^([A-Za-z_][A-Za-z0-9_:]*)\s+(\S.*)$");
        private static readonly Regex GroupQueueOpen = new Regex(@"^([A-Za-z_][A-Za-z0-9_:]*)\s+(GROUP|QUEUE)\b(.*)$", RegexOptions.IgnoreCase);
        private static readonly Regex ClassOpen = new Regex(@"^([A-Za-z_][A-Za-z0-9_:]*)\s+(CLASS|INTERFACE)\b(.*)$", RegexOptions.IgnoreCase);
        // The type argument right after GROUP/QUEUE/CLASS - "(SomeType)". Anchored so a later attribute's
        // parentheses (PRE(q), NAME('x')) are never read as the base type.
        private static readonly Regex BaseTypeArg = new Regex(@"^\s*\(\s*([A-Za-z_][A-Za-z0-9_:]*)\s*\)", RegexOptions.IgnoreCase);
        private static readonly Regex EndLine = new Regex(@"^\s*END\b", RegexOptions.IgnoreCase);
        private static readonly Regex PeriodEnd = new Regex(@"^\s*\.\s*$");
        private static readonly Regex StructLiteral = new Regex(@"'(?:[^']|'')*'");
        private static readonly Regex LineComment = new Regex(@"!.*$");
        private static readonly Regex SelfClosingStructure = new Regex(@"(?:\bEND\b|\.)\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex MapOpen = new Regex(@"^\s*MAP\b", RegexOptions.IgnoreCase);
        private static readonly Regex MapModuleOpen = new Regex(@"^\s*MODULE\b", RegexOptions.IgnoreCase);
        private static readonly Regex MapDirective = new Regex(@"^\s*(INCLUDE|OMIT|COMPILE|SECTION|PRAGMA|!)", RegexOptions.IgnoreCase);
        private static readonly Regex MapProtoName = new Regex(@"^\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
        private static readonly Regex PreAttr = new Regex(@",\s*PRE\(\s*([A-Za-z_][A-Za-z0-9_]*)?\s*\)", RegexOptions.IgnoreCase);
        // Qualifier immediately before the cursor: <identifier><':' or '.'><partial>. The identifier may
        // contain ':' (template queues like "Queue:Browse:1"); greedy backtracking keeps PRE ("Cus:Name" ->
        // "Cus") and plain-dotted ("Group." -> "Group") intact.
        internal static readonly Regex QualifierPattern = new Regex(@"([A-Za-z_][A-Za-z0-9_:]*)([:.])([A-Za-z0-9_]*)$");
        private static readonly Regex MemberAccessPattern = new Regex(@"([A-Za-z_][A-Za-z0-9_:]*)\.([A-Za-z0-9_]*)$");
        private static readonly Regex ClassParen = new Regex(@"^\s*CLASS\s*\(\s*([A-Za-z_][A-Za-z0-9_:]*)\s*\)", RegexOptions.IgnoreCase);
        private static readonly Regex TypeToken = new Regex(@"^\s*&?\s*([A-Za-z_][A-Za-z0-9_:]*)");
        private static readonly Regex ParamName = new Regex(@"^[A-Za-z_][A-Za-z0-9_:]*$");

        // ============================================================================ the scope

        internal sealed class StructField { public string Name; public string Type; }

        /// <summary>BaseType is the type argument of "Name QUEUE(SomeType)" / "Name GROUP(SomeType)". Such a
        /// structure has the named type's fields PLUS any declared inline, and the type lives elsewhere, so
        /// when BaseType is set Fields is known to be INCOMPLETE.</summary>
        internal sealed class Struct { public string Name; public string Pre; public string BaseType; public readonly List<StructField> Fields = new List<StructField>(); }

        internal sealed class ClassBlock { public string Name; public string Parent; public readonly List<KeyValuePair<string, string>> Members = new List<KeyValuePair<string, string>>(); }

        internal sealed class Param { public string Name; public string Type; }

        /// <summary>One DATA range: its relevant lines only (column-1 labels, and END lines once a label
        /// has been seen) - every other line is inert to the label/structure walks below.</summary>
        private sealed class DataRange { public List<string> Lines; public string Kind; }

        /// <summary>Everything in scope at one caret line. Built by walking outward from the caret.</summary>
        internal sealed class Scope
        {
            private readonly string _buf;
            private readonly int _origin;
            internal readonly string CaretLine;
            private readonly List<DataRange> _ranges = new List<DataRange>();   // routine, proc, owner, then module
            private readonly List<Param> _params = new List<Param>();
            private readonly List<Param> _ownerParams = new List<Param>();
            private readonly int _procHeader = -1;      // enclosing implementation header (caret line or above)
            private readonly string _procLabel;
            private readonly Header _header;
            private List<Struct> _structs;

            internal Scope(string buf, int origin, int caretLineStart)
            {
                _buf = buf;
                _origin = origin;
                int cle = LineEnd(buf, caretLineStart);
                CaretLine = buf.Substring(caretLineStart, cle - caretLineStart);
                _header = GetHeader(buf, origin);

                // A column-1 declaration in progress ("Test PRO", about to become "Test PROCEDURE") that is
                // not yet a complete header: it belongs to the construct above whose DATA is still open, or
                // - past any CODE line - to nothing but module scope.
                int from = caretLineStart;
                if (IsDataLabel(buf, from, cle) && !IsImplHeader(buf, from, origin) && !IsRoutineHeader(buf, from))
                {
                    int open = FindOpenDataSectionHeader(buf, from, origin);
                    if (open < 0) { AddModuleRanges(); return; }
                    from = open;
                }

                int routineHdr = -1;
                for (int p = from; p >= 0; p = PrevLine(buf, p, origin))
                {
                    if (routineHdr < 0 && IsRoutineHeader(buf, p)) { routineHdr = p; continue; }
                    if (IsImplHeader(buf, p, origin)) { _procHeader = p; break; }
                }
                if (routineHdr >= 0) _ranges.Add(ReadRange(routineHdr, "routine"));
                if (_procHeader >= 0)
                {
                    _procLabel = LabelAt(buf, _procHeader);
                    _ranges.Add(ReadRange(_procHeader, "proc"));
                    ParseParams(_procHeader, _params);

                    // A local class's method sees its owning procedure's DATA and parameters.
                    int dot = _procLabel.LastIndexOf('.');
                    if (dot > 0)
                    {
                        string cls = _procLabel.Substring(0, dot);
                        for (int p = PrevLine(buf, _procHeader, origin); p >= 0; p = PrevLine(buf, p, origin))
                        {
                            if (!IsImplHeader(buf, p, origin)) continue;
                            string lbl = LabelAt(buf, p);
                            if (lbl.IndexOf('.') >= 0) continue;          // a sibling method
                            var owner = ReadRange(p, "owner");
                            if (DeclaresClass(owner.Lines, cls))
                            {
                                _ranges.Add(owner);
                                ParseParams(p, _ownerParams);
                            }
                            break;
                        }
                    }
                }
                AddModuleRanges();
            }

            private void AddModuleRanges()
            {
                foreach (var r in _header.ModuleRanges) _ranges.Add(new DataRange { Lines = r, Kind = "module" });
            }

            private DataRange ReadRange(int headerStart, string kind)
            {
                var lines = new List<string>();
                bool sawLabel = false;
                for (int p = NextLine(_buf, headerStart); p >= 0; p = NextLine(_buf, p))
                {
                    if (IsCodeLine(_buf, p)) break;
                    if (IsImplHeader(_buf, p, _origin) || IsRoutineHeader(_buf, p)) break;
                    if (p < _buf.Length && IsLabelStart(_buf[p])) { sawLabel = true; lines.Add(LineText(_buf, p)); }
                    else if (sawLabel && IsEndLineAt(_buf, p)) lines.Add(LineText(_buf, p));
                }
                return new DataRange { Lines = lines, Kind = kind };
            }

            private void ParseParams(int headerStart, List<Param> into)
            {
                // The header line plus any '|' continuation lines.
                var sb = new StringBuilder(LineText(_buf, headerStart));
                int p = headerStart;
                for (int guard = 0; guard < 50 && EndsWithContinuation(sb.ToString()); guard++)
                {
                    p = NextLine(_buf, p);
                    if (p < 0) break;
                    string s = sb.ToString();
                    int bar = s.LastIndexOf('|');
                    sb.Length = 0;
                    sb.Append(s.Substring(0, bar)).Append(' ').Append(LineText(_buf, p).Trim());
                }
                string label;
                var ps = ParsePrototypeParams(sb.ToString(), out label);
                List<Param> proto = null;
                for (int i = 0; i < ps.Count; i++)
                {
                    var prm = ps[i];
                    if (prm.Name == null) continue;
                    if (prm.Type == null)
                    {
                        // An implementation may list names only; its MAP prototype carries the types.
                        if (proto == null) proto = MapPrototypeParams(label) ?? new List<Param>();
                        if (i < proto.Count) prm.Type = proto[i].Type;
                    }
                    into.Add(prm);
                }
            }

            private List<Param> MapPrototypeParams(string procLabel)
            {
                foreach (var kv in _header.MapProcs)
                {
                    if (!string.Equals(kv.Key, procLabel, StringComparison.OrdinalIgnoreCase)) continue;
                    string ignored;
                    return ParsePrototypeParams(kv.Value, out ignored);
                }
                return null;
            }

            // ---- completion

            internal void AddLocals(string prefix, HashSet<string> seen, List<LspClient.CompletionItemInfo> items, bool includeParams)
            {
                foreach (var r in _ranges)
                {
                    if (r.Kind == "routine") CollectLabels(r.Lines, prefix, seen, items, "(routine var)");
                    else if (r.Kind == "proc")
                    {
                        CollectLabels(r.Lines, prefix, seen, items, "(local)");
                        if (includeParams) AddParams(_params, prefix, seen, items);
                    }
                    else if (r.Kind == "owner")
                    {
                        CollectLabels(r.Lines, prefix, seen, items, "(local)");
                        if (includeParams) AddParams(_ownerParams, prefix, seen, items);
                    }
                }
            }

            private static void AddParams(List<Param> ps, string prefix, HashSet<string> seen, List<LspClient.CompletionItemInfo> items)
            {
                foreach (var p in ps)
                {
                    if (!p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(p.Name)) continue;
                    items.Add(new LspClient.CompletionItemInfo
                    {
                        Label = p.Name, Kind = 6 /*Variable*/,
                        Detail = string.IsNullOrEmpty(p.Type) ? "(parameter)" : p.Type + "  (parameter)",
                        InsertText = p.Name
                    });
                }
            }

            internal void AddModuleVars(string prefix, HashSet<string> seen, List<LspClient.CompletionItemInfo> items)
            {
                // Module (file) scope reads as "global" to match the CodeGraph cross-file wording.
                foreach (var r in _header.ModuleRanges) CollectLabels(r, prefix, seen, items, "(global)");
            }

            internal void AddLocalProcedures(string prefix, HashSet<string> seen, List<LspClient.CompletionItemInfo> items)
            {
                // (a) Inline MAP prototypes - the signature goes in the detail column.
                foreach (var kv in _header.MapProcs)
                {
                    string name = kv.Key;
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                    string detail = string.IsNullOrEmpty(kv.Value) ? "(local procedure)" : kv.Value + "  (local procedure)";
                    items.Add(new LspClient.CompletionItemInfo { Label = name, Kind = 3 /*Function*/, Detail = detail, InsertText = name });
                }
                // (b) Procedure implementations in this buffer (Class.Method / prefixed labels excluded).
                foreach (var pe in ProcList(_buf, _origin))
                {
                    if (!pe.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(pe.Key)) continue;
                    items.Add(new LspClient.CompletionItemInfo { Label = pe.Key, Kind = 3 /*Function*/, Detail = "(local procedure)", InsertText = pe.Key });
                }
            }

            internal void AddNoPreFields(string prefix, HashSet<string> seen, List<LspClient.CompletionItemInfo> items)
            {
                foreach (var s in Structures)
                {
                    if (s.Pre != null) continue;
                    foreach (var f in s.Fields)
                    {
                        if (!f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(f.Name)) continue;
                        items.Add(new LspClient.CompletionItemInfo
                        {
                            Label = f.Name, Kind = 5 /*Field*/,
                            Detail = string.IsNullOrEmpty(f.Type) ? "(field)" : f.Type + "  (field)",
                            InsertText = f.Name
                        });
                    }
                }
            }

            /// <summary>PRE prefix ("Cus:partial" -> fields of GROUP,PRE(Cus)) and dotted access
            /// ("Group.partial" -> its direct fields), for structures in scope.</summary>
            internal void AddQualifiedFields(string qualifier, char sep, string partial, HashSet<string> seen, List<LspClient.CompletionItemInfo> items)
            {
                foreach (var s in Structures)
                {
                    bool match = sep == ':'
                        ? (s.Pre != null && string.Equals(s.Pre, qualifier, StringComparison.OrdinalIgnoreCase))
                        : string.Equals(s.Name, qualifier, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    foreach (var f in s.Fields)
                    {
                        if (partial.Length > 0 && !f.Name.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                        // PRE label shows the full "Cus:Field"; insert just the field name (the range breaks
                        // on ':'/'.', so the qualifier already typed stays put).
                        string label = sep == ':' ? qualifier + ":" + f.Name : f.Name;
                        if (!seen.Add(label)) continue;
                        items.Add(new LspClient.CompletionItemInfo
                        {
                            Label = label, Kind = 5 /*Field*/,
                            Detail = string.IsNullOrEmpty(f.Type) ? "(field)" : f.Type + "  (field)",
                            InsertText = f.Name
                        });
                    }
                }
            }

            internal void AddClassMembers(string instance, string partial, HashSet<string> seen, List<LspClient.CompletionItemInfo> items)
            {
                var cls = ResolveClassBlock(instance);
                if (cls == null) return;
                foreach (var kv in cls.Members)
                {
                    if (partial.Length > 0 && !kv.Key.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(kv.Key)) continue;
                    bool method = IsProcedureDecl(kv.Value);
                    items.Add(new LspClient.CompletionItemInfo
                    {
                        Label = kv.Key, Kind = method ? 2 /*Method*/ : 5 /*Field*/,
                        Detail = kv.Value + "  (" + cls.Name + ")",
                        InsertText = kv.Key
                    });
                }
            }

            /// <summary>ROUTINE labels of the current procedure (the DO context). <paramref name="found"/>
            /// receives every prefix-matching routine, added or not, for the caller's scoping pass.</summary>
            internal void AddRoutines(string prefix, HashSet<string> seen, List<LspClient.CompletionItemInfo> items, HashSet<string> found)
            {
                foreach (var name in RoutineNames())
                {
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found != null) found.Add(name);
                    if (!seen.Add(name)) continue;
                    items.Add(new LspClient.CompletionItemInfo { Label = name, Kind = 2 /*Method*/, Detail = "(routine)", InsertText = name });
                }
            }

            /// <summary>Routine labels between the enclosing implementation header (or the top) and the
            /// next one - a routine's actual visibility in Clarion.</summary>
            private List<string> RoutineNames()
            {
                var names = new List<string>();
                int start = _procHeader >= 0 ? _procHeader : _origin;
                for (int p = start; p >= 0; p = NextLine(_buf, p))
                {
                    if (p != start && IsImplHeader(_buf, p, _origin)) break;
                    if (!IsRoutineHeader(_buf, p)) continue;
                    string name = LabelAt(_buf, p);
                    // Dotted/prefixed labels are member-access targets, not DO-callable routines.
                    if (name.IndexOf('.') >= 0 || name.IndexOf(':') >= 0) continue;
                    names.Add(name);
                }
                return names;
            }

            // ---- structures and classes

            internal List<Struct> Structures
            {
                get
                {
                    if (_structs != null) return _structs;
                    var all = new List<Struct>();
                    foreach (var r in _ranges) ParseStructures(r.Lines, all);
                    return _structs = all;
                }
            }

            /// <summary>The GROUP/QUEUE named <paramref name="name"/> in scope, or null.</summary>
            internal Struct FindStructure(string name)
            {
                foreach (var s in Structures)
                    if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
                return null;
            }

            internal ClassBlock ResolveClassBlock(string instance)
            {
                string name = instance;
                if (string.Equals(instance, "SELF", StringComparison.OrdinalIgnoreCase))
                {
                    int dot = _procLabel == null ? -1 : _procLabel.LastIndexOf('.');
                    if (dot <= 0) return null;
                    name = _procLabel.Substring(0, dot);
                }
                else if (string.Equals(instance, "PARENT", StringComparison.OrdinalIgnoreCase)) return null;
                foreach (var r in _ranges)
                {
                    var cb = ReadClassBlock(r.Lines, name);
                    if (cb != null) return cb;
                }
                return null;
            }

            /// <summary>The declared class type of a local/module variable or parameter ("obj &amp;MyClass",
            /// "obj MyClass", "&amp;MyClass pObj"), or null.</summary>
            internal string DeclaredTypeOf(string instance)
            {
                foreach (var r in _ranges)
                {
                    string rest = FindDataLabel(r.Lines, instance);
                    if (rest == null) continue;
                    var tk = TypeToken.Match(rest);
                    return tk.Success ? tk.Groups[1].Value : null;
                }
                foreach (var p in AllParams())
                    if (string.Equals(p.Name, instance, StringComparison.OrdinalIgnoreCase) && p.Type != null)
                    {
                        var tk = TypeToken.Match(p.Type.TrimStart('<', '*'));
                        return tk.Success ? tk.Groups[1].Value : null;
                    }
                return null;
            }

            private IEnumerable<Param> AllParams()
            {
                foreach (var p in _params) yield return p;
                foreach (var p in _ownerParams) yield return p;
            }

            // ---- hover

            /// <summary>Hover for an exact word declared in this buffer, or null. Order: the scope's DATA
            /// (routine, procedure, parameters, owning procedure, module), MAP procedures, procedure
            /// implementations, routines of the current procedure.</summary>
            internal LocalHoverResult HoverWord(string word, string fileName)
            {
                if (string.IsNullOrEmpty(word)) return null;
                foreach (var r in _ranges)
                {
                    string rest = FindDataLabel(r.Lines, word);
                    if (rest != null)
                    {
                        string detail, doc;
                        BuildVarDetail(rest, null, out detail, out doc);
                        string sig = string.IsNullOrEmpty(detail) ? word : word + "  " + detail;
                        return Card(sig, "local", fileName, "local");
                    }
                    var ps = r.Kind == "proc" ? _params : (r.Kind == "owner" ? _ownerParams : null);
                    if (ps != null)
                        foreach (var p in ps)
                            if (string.Equals(p.Name, word, StringComparison.OrdinalIgnoreCase))
                                return Card(string.IsNullOrEmpty(p.Type) ? p.Name : p.Name + "  " + p.Type, "parameter", fileName, "parameter");
                }
                foreach (var kv in _header.MapProcs)
                    if (string.Equals(kv.Key, word, StringComparison.OrdinalIgnoreCase))
                        return Card(string.IsNullOrEmpty(kv.Value) ? word : kv.Value, "local procedure", fileName, "procedure");
                foreach (var pe in ProcList(_buf, _origin))
                    if (string.Equals(pe.Key, word, StringComparison.OrdinalIgnoreCase))
                        return Card(pe.Value, "local procedure", fileName, "procedure");
                foreach (var name in RoutineNames())
                    if (string.Equals(name, word, StringComparison.OrdinalIgnoreCase))
                        return Card(name + " ROUTINE", "routine", fileName, "routine");
                return null;
            }

            internal LocalHoverResult MemberHover(string instance, string member, string fileName)
            {
                var cls = ResolveClassBlock(instance);
                if (cls == null) return null;
                foreach (var kv in cls.Members)
                    if (string.Equals(kv.Key, member, StringComparison.OrdinalIgnoreCase))
                    {
                        var r = Card(kv.Key + "  " + kv.Value, "member of " + cls.Name, fileName, "member");
                        r.Authoritative = false;   // inherited overrides/docs may still come from the LSP
                        return r;
                    }
                return null;
            }

            private static LocalHoverResult Card(string sig, string what, string fileName, string kind)
            {
                var bits = new List<string> { what };
                if (!string.IsNullOrEmpty(fileName)) bits.Add(fileName);
                return new LocalHoverResult
                {
                    Markdown = "```clarion\n" + sig + "\n```\n\n" + string.Join(" · ", bits),
                    Authoritative = true,
                    Kind = kind
                };
            }
        }

        // ============================================================================ range walkers (per DATA range)

        private static bool IsStructOpen(string ln)
        {
            var m = GroupQueueOpen.Match(ln);
            if (!m.Success) m = ClassOpen.Match(ln);
            return m.Success && !ClosesOnSameLine(m.Groups[3].Value);
        }

        private static bool IsEnd(string ln) { return EndLine.IsMatch(ln) || PeriodEnd.IsMatch(ln); }

        /// <summary>Depth-0 labels of one DATA range matching <paramref name="prefix"/>: plain locals plus a
        /// GROUP/QUEUE/CLASS container's own label, never its fields or members.</summary>
        private static void CollectLabels(List<string> lines, string prefix, HashSet<string> seen,
                                          List<LspClient.CompletionItemInfo> items, string scopeMarker)
        {
            int depth = 0;
            foreach (var ln in lines)
            {
                bool isEnd = IsEnd(ln);
                if (depth == 0 && !isEnd)
                {
                    var lm = DataLabelPattern.Match(ln);
                    if (lm.Success)
                    {
                        string label = lm.Groups[1].Value;
                        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(label))
                        {
                            string detail, doc;
                            BuildVarDetail(lm.Groups[2].Value, scopeMarker, out detail, out doc);
                            items.Add(new LspClient.CompletionItemInfo
                            { Label = label, Kind = 6 /*Variable*/, Detail = detail, Documentation = doc, InsertText = label });
                        }
                    }
                }
                if (IsStructOpen(ln)) depth++;
                else if (isEnd && depth > 0) depth--;
            }
        }

        /// <summary>First depth-0 declaration labelled <paramref name="word"/> - its rest-of-line - or null.</summary>
        private static string FindDataLabel(List<string> lines, string word)
        {
            int depth = 0;
            foreach (var ln in lines)
            {
                bool isEnd = IsEnd(ln);
                if (depth == 0 && !isEnd)
                {
                    var lm = DataLabelPattern.Match(ln);
                    if (lm.Success && string.Equals(lm.Groups[1].Value, word, StringComparison.OrdinalIgnoreCase))
                        return lm.Groups[2].Value;
                }
                if (IsStructOpen(ln)) depth++;
                else if (isEnd && depth > 0) depth--;
            }
            return null;
        }

        /// <summary>GROUP/QUEUE structures (nesting + PRE inheritance) of one range, appended to <paramref name="all"/>.</summary>
        private static void ParseStructures(List<string> lines, List<Struct> all)
        {
            var stack = new List<Struct>();
            foreach (var ln in lines)
            {
                var gq = GroupQueueOpen.Match(ln);
                if (gq.Success)
                {
                    string pre = ExtractPre(gq.Groups[3].Value) ?? (stack.Count > 0 ? stack[stack.Count - 1].Pre : null);
                    var s = new Struct { Name = gq.Groups[1].Value, Pre = pre, BaseType = ExtractBaseType(gq.Groups[3].Value) };
                    if (stack.Count > 0)   // a nested group is also a field of its parent
                        stack[stack.Count - 1].Fields.Add(new StructField { Name = s.Name, Type = gq.Groups[2].Value });
                    all.Add(s);
                    // "Settings GROUP(SomeType) END" declares no inline fields - never leave it open.
                    if (!ClosesOnSameLine(gq.Groups[3].Value)) stack.Add(s);
                    continue;
                }
                if (stack.Count > 0 && IsEnd(ln)) { stack.RemoveAt(stack.Count - 1); continue; }
                if (stack.Count > 0)
                {
                    var fm = DataLabelPattern.Match(ln);
                    if (fm.Success)
                        stack[stack.Count - 1].Fields.Add(new StructField
                        { Name = fm.Groups[1].Value, Type = StripTrailingComment(fm.Groups[2].Value).Trim() });
                }
            }
        }

        private static bool DeclaresClass(List<string> lines, string name) { return ReadClassBlock(lines, name) != null; }

        /// <summary>The depth-0 CLASS labelled <paramref name="name"/> in one range and its members.</summary>
        private static ClassBlock ReadClassBlock(List<string> lines, string name)
        {
            int depth = 0;
            ClassBlock cb = null;
            foreach (var ln in lines)
            {
                bool isEnd = IsEnd(ln);
                if (cb != null)
                {
                    if (depth == 1 && isEnd) return cb;
                    if (depth == 1)
                    {
                        var mm = DataLabelPattern.Match(ln);
                        if (mm.Success) cb.Members.Add(new KeyValuePair<string, string>(mm.Groups[1].Value, StripTrailingComment(mm.Groups[2].Value).Trim()));
                    }
                    if (IsStructOpen(ln)) depth++;
                    else if (isEnd && depth > 0) depth--;
                    continue;
                }
                if (depth == 0)
                {
                    var cm = ClassOpen.Match(ln);
                    if (cm.Success && string.Equals(cm.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(cm.Groups[2].Value, "CLASS", StringComparison.OrdinalIgnoreCase))
                    {
                        cb = new ClassBlock { Name = cm.Groups[1].Value, Parent = ExtractBaseType(cm.Groups[3].Value) };
                        if (ClosesOnSameLine(cm.Groups[3].Value)) return cb;
                        depth = 1;
                        continue;
                    }
                }
                if (IsStructOpen(ln)) depth++;
                else if (isEnd && depth > 0) depth--;
            }
            return cb;
        }

        private static bool IsProcedureDecl(string rest)
        {
            var m = Regex.Match(rest ?? "", @"^\s*(PROCEDURE|FUNCTION)\b", RegexOptions.IgnoreCase);
            return m.Success;
        }

        private static string ExtractPre(string attrs)
        {
            var m = PreAttr.Match(attrs ?? "");
            return (m.Success && m.Groups[1].Success && m.Groups[1].Value.Length > 0) ? m.Groups[1].Value : null;
        }

        private static string ExtractBaseType(string afterKeyword)
        {
            if (string.IsNullOrEmpty(afterKeyword)) return null;
            var m = BaseTypeArg.Match(afterKeyword);
            return (m.Success && m.Groups[1].Value.Length > 0) ? m.Groups[1].Value : null;
        }

        /// <summary>True when a GROUP/QUEUE/CLASS declaration terminates on its own line (a trailing END or
        /// '.'). String literals and a trailing comment are removed first, so NAME('APPEND') or "! ... end"
        /// is never read as a terminator.</summary>
        private static bool ClosesOnSameLine(string afterKeyword)
        {
            if (string.IsNullOrEmpty(afterKeyword)) return false;
            string tail = StructLiteral.Replace(afterKeyword, "''");
            tail = LineComment.Replace(tail, "");
            return SelfClosingStructure.IsMatch(tail);
        }

        // ============================================================================ text helpers

        /// <summary>Index of the '!' starting a trailing comment - the first '!' outside a string literal
        /// that follows whitespace - or -1.</summary>
        private static int CommentStart(string s)
        {
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'') inString = !inString;   // '' toggles twice: still correct
                else if (c == '!' && !inString && i > 0 && char.IsWhiteSpace(s[i - 1])) return i;
            }
            return -1;
        }

        /// <summary>The text with a trailing " ! comment" removed (string-literal aware).</summary>
        internal static string StripTrailingComment(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = CommentStart(s);
            return i < 0 ? s : s.Substring(0, i).TrimEnd();
        }

        /// <summary>A variable's completion Detail ("TYPE  (scope)", short and single-line) and
        /// Documentation (the trailing '!' comment - Clarion's only per-variable description). A '!'
        /// inside a string literal ("STRING('a ! b')") is part of the type, not a comment.</summary>
        internal static void BuildVarDetail(string restOfLine, string scopeTag, out string detail, out string documentation)
        {
            string typeText = restOfLine ?? "";
            string description = null;
            int c = CommentStart(typeText);
            if (c >= 0) { description = typeText.Substring(c + 1).Trim(); typeText = typeText.Substring(0, c); }
            typeText = typeText.Trim();
            if (description != null && description.Length == 0) description = null;

            var sb = new StringBuilder();
            if (typeText.Length > 0) sb.Append(typeText);
            if (!string.IsNullOrEmpty(scopeTag)) { if (sb.Length > 0) sb.Append("  "); sb.Append(scopeTag); }
            detail = sb.Length > 0 ? sb.ToString() : null;
            documentation = description;
        }

        /// <summary>True when column <paramref name="character"/> sits inside a single-quoted string
        /// ('' is an escaped quote; both delimiters count as inside) or on/after an unquoted '!'.</summary>
        internal static bool IsInsideStringOrComment(string lineText, int character)
        {
            if (string.IsNullOrEmpty(lineText)) return false;
            bool inString = false;
            int stringStart = -1;
            for (int i = 0; i < lineText.Length; i++)
            {
                char ch = lineText[i];
                if (!inString && ch == '!') return character >= i;
                if (ch == '\'')
                {
                    if (inString && i + 1 < lineText.Length && lineText[i + 1] == '\'') { i++; continue; }
                    if (inString)
                    {
                        if (character >= stringStart && character <= i) return true;
                        inString = false;
                    }
                    else { inString = true; stringStart = i; }
                }
            }
            return inString && character >= stringStart;
        }

        private static bool EndsWithContinuation(string line)
        {
            string s = StripTrailingComment(line).TrimEnd();
            return s.EndsWith("|", StringComparison.Ordinal);
        }

        /// <summary>The parameters of a "Label PROCEDURE(...)" header or MAP prototype. A parameter with no
        /// name (a MAP prototype's "(LONG)") yields Name=null; one with no type (an implementation that
        /// lists names only) yields Type=null.</summary>
        internal static List<Param> ParsePrototypeParams(string header, out string label)
        {
            var list = new List<Param>();
            label = null;
            if (string.IsNullOrEmpty(header)) return list;
            string h = StripTrailingComment(header);
            var lm = Regex.Match(h, @"^\s*([A-Za-z_][A-Za-z0-9_.:]*)\s+(?:PROCEDURE|FUNCTION)\b", RegexOptions.IgnoreCase);
            if (!lm.Success) return list;
            label = lm.Groups[1].Value;
            int i = lm.Index + lm.Length;
            while (i < h.Length && char.IsWhiteSpace(h[i])) i++;
            if (i >= h.Length || h[i] != '(') return list;

            // Split the balanced (...) at top-level commas, string-literal aware.
            var parts = new List<string>();
            var cur = new StringBuilder();
            int depth = 0;
            bool inStr = false;
            for (i = i + 1; i < h.Length; i++)
            {
                char c = h[i];
                if (c == '\'') inStr = !inStr;
                if (!inStr)
                {
                    if (c == '(' || c == '<') depth++;
                    else if (c == '>' && depth > 0) depth--;
                    else if (c == ')') { if (depth == 0) break; depth--; }
                    else if (c == ',' && depth == 0) { parts.Add(cur.ToString()); cur.Length = 0; continue; }
                }
                cur.Append(c);
            }
            if (cur.ToString().Trim().Length > 0 || parts.Count > 0) parts.Add(cur.ToString());

            foreach (var raw in parts)
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                bool optional = false;
                if (t.StartsWith("<") && t.EndsWith(">")) { optional = true; t = t.Substring(1, t.Length - 2).Trim(); }
                string def = null;
                int eq = IndexOutsideString(t, '=');
                if (eq >= 0) { def = t.Substring(eq + 1).Trim(); t = t.Substring(0, eq).Trim(); }
                if (t.StartsWith("<") && t.EndsWith(">")) { optional = true; t = t.Substring(1, t.Length - 2).Trim(); }

                string name = null, type = null;
                int sp = LastWhitespace(t);
                if (sp > 0)
                {
                    name = t.Substring(sp + 1).Trim();
                    type = t.Substring(0, sp).Trim();
                }
                else if (!LooksLikeType(t)) name = t;
                else type = t;
                if (name != null && !ParamName.IsMatch(name)) { name = null; }
                if (type != null)
                {
                    if (optional) type = "<" + type + ">";
                    if (def != null) type += " = " + def;
                }
                list.Add(new Param { Name = name, Type = type });
            }
            return list;
        }

        private static bool LooksLikeType(string t)
        {
            if (t.Length == 0) return true;
            char c = t[0];
            if (c == '*' || c == '&' || c == '?') return true;
            return ClarionBuiltins.IsClarionType(t) || ClarionBuiltins.IsKeyword(t);
        }

        private static int LastWhitespace(string t)
        {
            for (int i = t.Length - 1; i >= 0; i--) if (char.IsWhiteSpace(t[i])) return i;
            return -1;
        }

        private static int IndexOutsideString(string t, char target)
        {
            bool inStr = false;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\'') inStr = !inStr;
                else if (!inStr && t[i] == target) return i;
            }
            return -1;
        }

        // ============================================================================ the buffer, by index
        // A "line" is the text between '\n's with trailing '\r's dropped - the same lines the old
        // whole-buffer split gave, without building them. A leading BOM is skipped.

        private static int Origin(string s) { return s.Length > 0 && s[0] == '\uFEFF' ? 1 : 0; }

        private static int NextLine(string s, int ls)
        {
            int nl = s.IndexOf('\n', ls);
            return nl < 0 ? -1 : nl + 1;
        }

        private static int PrevLine(string s, int ls, int origin)
        {
            if (ls <= origin) return -1;
            if (ls - 2 < 0) return origin;
            int p = s.LastIndexOf('\n', ls - 2) + 1;
            return p < origin ? origin : p;
        }

        private static int LineEnd(string s, int ls)
        {
            int nl = s.IndexOf('\n', ls);
            int e = nl < 0 ? s.Length : nl;
            while (e > ls && s[e - 1] == '\r') e--;
            return e;
        }

        private static string LineText(string s, int ls) { return s.Substring(ls, LineEnd(s, ls) - ls); }

        private static bool IsLabelStart(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'; }

        private static bool IsLabelChar(char c, bool allowDot)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == ':' || (allowDot && c == '.');
        }

        private static bool IsWordChar(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        private static bool MatchWord(string s, int i, int le, string kw)
        {
            if (le - i < kw.Length) return false;
            if (string.Compare(s, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            int after = i + kw.Length;
            return after >= le || !IsWordChar(s[after]);
        }

        private static int SkipWs(string s, int i, int le)
        {
            while (i < le && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        /// <summary>"^label\s+KEYWORD\b" on the line at <paramref name="ls"/>: the index just past the
        /// keyword, or -1. The label may carry '.' and ':' (ThisWindow.Init, Queue:Browse).</summary>
        private static int HeaderKeyword(string s, int ls, string kw)
        {
            if (ls >= s.Length || !IsLabelStart(s[ls])) return -1;
            int le = LineEnd(s, ls);
            int i = ls + 1;
            while (i < le && IsLabelChar(s[i], true)) i++;
            int ws = i;
            i = SkipWs(s, i, le);
            if (i == ws || !MatchWord(s, i, le, kw)) return -1;
            return i + kw.Length;
        }

        private static string LabelAt(string s, int ls)
        {
            int i = ls;
            while (i < s.Length && IsLabelChar(s[i], true)) i++;
            return s.Substring(ls, i - ls);
        }

        private static bool IsRoutineHeader(string s, int ls) { return HeaderKeyword(s, ls, "ROUTINE") >= 0; }

        /// <summary>A procedure IMPLEMENTATION header: "Label PROCEDURE[(...)]" at column 1 that is not a
        /// prototype - no attribute list after the parameters, and not inside a CLASS/INTERFACE/MAP/MODULE
        /// block (every ABC procedure's "ThisWindow CLASS" has column-1 method prototypes).</summary>
        private static bool IsImplHeader(string s, int ls, int origin)
        {
            int k = HeaderKeyword(s, ls, "PROCEDURE");
            if (k < 0) return false;
            if (HasAttributesAfterParams(s, k, LineEnd(s, ls))) return false;
            return !InsideDeclarationBlock(s, ls, origin);
        }

        private static bool HasAttributesAfterParams(string s, int i, int le)
        {
            i = SkipWs(s, i, le);
            if (i < le && s[i] == '(')
            {
                int depth = 0;
                bool inStr = false;
                for (; i < le; i++)
                {
                    char c = s[i];
                    if (c == '\'') inStr = !inStr;
                    if (inStr) continue;
                    if (c == '(') depth++;
                    else if (c == ')') { depth--; if (depth == 0) { i++; break; } }
                    else if (c == '!') return false;
                }
                if (depth != 0) return false;   // continued on the next line - can't tell, assume header
                i = SkipWs(s, i, le);
            }
            return i < le && s[i] == ',';
        }

        /// <summary>Walks up from a candidate header over the lines a declaration block is made of (column-1
        /// labels, blanks, comments, '|' continuations). Reaching a CLASS/INTERFACE opener or MAP/MODULE
        /// means the candidate is a prototype inside that block; anything else (an indented statement, END,
        /// CODE) means it is not. Bounded.</summary>
        private static bool InsideDeclarationBlock(string s, int ls, int origin)
        {
            int steps = 0;
            for (int p = PrevLine(s, ls, origin); p >= 0 && steps < 20000; p = PrevLine(s, p, origin), steps++)
            {
                int le = LineEnd(s, p);
                int i = SkipWs(s, p, le);
                if (i >= le || s[i] == '!') continue;                            // blank / comment
                if (i == p && IsLabelStart(s[p]))
                {
                    if (IsClassOpenerLine(s, p, le)) return true;
                    continue;                                                     // sibling prototype / member / data
                }
                if (MatchWord(s, i, le, "MAP") || MatchWord(s, i, le, "MODULE")) return true;
                int pp = PrevLine(s, p, origin);
                if (pp >= 0 && EndsWithContinuation(LineText(s, pp))) continue;  // a '|' continuation line
                return false;
            }
            return false;
        }

        private static bool IsClassOpenerLine(string s, int ls, int le)
        {
            int i = ls;
            while (i < le && IsLabelChar(s[i], false)) i++;
            int ws = i;
            i = SkipWs(s, i, le);
            if (i == ws) return false;
            string kw = MatchWord(s, i, le, "CLASS") ? "CLASS" : (MatchWord(s, i, le, "INTERFACE") ? "INTERFACE" : null);
            if (kw == null) return false;
            return !ClosesOnSameLine(s.Substring(i + kw.Length, le - i - kw.Length));
        }

        private static bool IsCodeLine(string s, int ls)
        {
            int le = LineEnd(s, ls);
            return MatchWord(s, SkipWs(s, ls, le), le, "CODE");
        }

        private static bool IsEndLineAt(string s, int ls)
        {
            int le = LineEnd(s, ls);
            int i = SkipWs(s, ls, le);
            if (MatchWord(s, i, le, "END")) return true;
            if (i < le && s[i] == '.') return SkipWs(s, i + 1, le) >= le;
            return false;
        }

        private static bool IsDataLabel(string s, int ls, int le)
        {
            if (ls >= le || !IsLabelStart(s[ls])) return false;
            int i = ls + 1;
            while (i < le && IsLabelChar(s[i], false)) i++;
            int ws = i;
            i = SkipWs(s, i, le);
            return i > ws && i < le;
        }

        private static int FindOpenDataSectionHeader(string s, int from, int origin)
        {
            for (int p = PrevLine(s, from, origin); p >= 0; p = PrevLine(s, p, origin))
            {
                if (IsImplHeader(s, p, origin) || IsRoutineHeader(s, p)) return p;
                if (IsCodeLine(s, p)) return -1;
            }
            return -1;
        }

        // ============================================================================ per-instance cache
        // The buffer string for one content version is the SAME instance for every request against it
        // (MonacoBufferCache), so its line anchor and procedure list are cached by reference.

        private sealed class InstanceInfo
        {
            public WeakReference Ref;
            public int AnchorLine = -1, AnchorOffset;
            public List<KeyValuePair<string, string>> Procs;
        }

        private static readonly object _instLock = new object();
        private static readonly InstanceInfo[] _instances = new InstanceInfo[4];
        private static int _instNext;

        private static InstanceInfo InfoFor(string buf)
        {
            lock (_instLock)
            {
                foreach (var ii in _instances)
                    if (ii != null && ReferenceEquals(ii.Ref.Target, buf)) return ii;
                var n = new InstanceInfo { Ref = new WeakReference(buf) };
                _instances[_instNext] = n;
                _instNext = (_instNext + 1) % _instances.Length;
                return n;
            }
        }

        /// <summary>Offset of 0-based line <paramref name="line0"/>, or -1 past the end. Counts newlines
        /// from the nearest cached anchor of this buffer instance.</summary>
        private static int LineStartOf(string s, int line0, int origin)
        {
            var info = InfoFor(s);
            int curLine = 0, off = origin;
            lock (_instLock)
            {
                if (info.AnchorLine >= 0 && info.AnchorLine <= line0) { curLine = info.AnchorLine; off = info.AnchorOffset; }
                else if (info.AnchorLine > line0 && info.AnchorLine - line0 < line0)
                {
                    curLine = info.AnchorLine; off = info.AnchorOffset;
                    while (curLine > line0) { off = PrevLine(s, off, origin); curLine--; }
                }
            }
            while (curLine < line0)
            {
                int nl = s.IndexOf('\n', off);
                if (nl < 0) return -1;
                off = nl + 1;
                curLine++;
            }
            lock (_instLock) { info.AnchorLine = line0; info.AnchorOffset = off; }
            return off;
        }

        /// <summary>(label, header signature) of every procedure implementation in the buffer whose label
        /// has no '.'/':' (a Class.Method is a member-access target, not a callable module procedure).</summary>
        private static List<KeyValuePair<string, string>> ProcList(string s, int origin)
        {
            var info = InfoFor(s);
            lock (_instLock) { if (info.Procs != null) return info.Procs; }
            var list = new List<KeyValuePair<string, string>>();
            for (int p = origin; p >= 0; p = NextLine(s, p))
            {
                if (p >= s.Length || !IsLabelStart(s[p])) continue;
                if (!IsImplHeader(s, p, origin)) continue;
                string label = LabelAt(s, p);
                if (label.IndexOf('.') >= 0 || label.IndexOf(':') >= 0) continue;
                list.Add(new KeyValuePair<string, string>(label, StripTrailingComment(LineText(s, p).Trim()).Trim()));
            }
            lock (_instLock) { info.Procs = list; }
            return list;
        }

        // ============================================================================ module header cache

        internal sealed class Header
        {
            public readonly List<List<string>> ModuleRanges = new List<List<string>>();
            public readonly List<KeyValuePair<string, string>> MapProcs = new List<KeyValuePair<string, string>>();
        }

        private static readonly object _headerLock = new object();
        private static readonly Dictionary<string, Header> _headers = new Dictionary<string, Header>();
        private static readonly LinkedList<string> _headerOrder = new LinkedList<string>();
        private const int HeaderCacheSize = 8;

        /// <summary>The module header - MEMBER down to the first procedure implementation, MAP-aware -
        /// parsed once per distinct header text (FNV-1a over the header's characters plus its length).</summary>
        private static Header GetHeader(string s, int origin)
        {
            int end = HeaderEnd(s, origin);
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < end; i++) { h ^= s[i]; h *= 1099511628211UL; }
            string key = h.ToString("x16") + ":" + end;
            lock (_headerLock)
            {
                Header hit;
                if (_headers.TryGetValue(key, out hit)) return hit;
            }
            var parsed = ParseHeader(s.Substring(origin, end - origin));
            System.Threading.Interlocked.Increment(ref _headerParseCount);
            lock (_headerLock)
            {
                if (!_headers.ContainsKey(key))
                {
                    _headers[key] = parsed;
                    _headerOrder.AddLast(key);
                    while (_headerOrder.Count > HeaderCacheSize) { _headers.Remove(_headerOrder.First.Value); _headerOrder.RemoveFirst(); }
                }
            }
            return parsed;
        }

        private static int HeaderEnd(string s, int origin)
        {
            int mapDepth = 0;
            int firstCandidateInMap = -1;
            for (int p = origin; p >= 0; p = NextLine(s, p))
            {
                if (mapDepth == 0)
                {
                    if (p < s.Length && IsLabelStart(s[p]) && IsImplHeader(s, p, origin)) return p;
                    int le = LineEnd(s, p);
                    int i = SkipWs(s, p, le);
                    if (MatchWord(s, i, le, "MAP")) mapDepth = 1;
                }
                else
                {
                    int le = LineEnd(s, p);
                    int i = SkipWs(s, p, le);
                    if (MatchWord(s, i, le, "MODULE")) mapDepth++;
                    else if (IsEndLineAt(s, p)) mapDepth--;
                    else if (firstCandidateInMap < 0 && i == p && HeaderKeyword(s, p, "PROCEDURE") >= 0 &&
                             !HasAttributesAfterParams(s, HeaderKeyword(s, p, "PROCEDURE"), le))
                        firstCandidateInMap = p;
                }
            }
            // A MAP left unterminated mid-edit: stop at the first implementation-shaped line inside it
            // rather than treating the whole module as its header.
            if (mapDepth > 0 && firstCandidateInMap >= 0) return firstCandidateInMap;
            return s.Length;
        }

        private static Header ParseHeader(string text)
        {
            var hdr = new Header();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');

            // Module data ranges, split around MAP blocks (prototypes declare procedures, not data).
            int rangeStart = 0, mapDepth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (mapDepth == 0)
                {
                    if (MapOpen.IsMatch(ln))
                    {
                        if (i > rangeStart) hdr.ModuleRanges.Add(Slice(lines, rangeStart, i));
                        mapDepth = 1;
                    }
                }
                else if (MapModuleOpen.IsMatch(ln)) mapDepth++;
                else if (IsEnd(ln)) { mapDepth--; if (mapDepth == 0) rangeStart = i + 1; }
            }
            // A MAP left unterminated mid-edit is dropped rather than read as data.
            if (mapDepth == 0 && lines.Length > rangeStart)
            {
                int end = lines.Length;
                if (end > rangeStart && lines[end - 1].Length == 0 && text.EndsWith("\n", StringComparison.Ordinal)) end--;   // the header's own trailing newline
                if (end > rangeStart) hdr.ModuleRanges.Add(Slice(lines, rangeStart, end));
            }

            // MAP prototypes (nested MODULE(...)...END counted; directives and comments skipped).
            mapDepth = 0;
            foreach (var ln in lines)
            {
                if (mapDepth == 0) { if (MapOpen.IsMatch(ln)) mapDepth = 1; continue; }
                if (MapModuleOpen.IsMatch(ln)) { mapDepth++; continue; }
                if (IsEnd(ln)) { mapDepth--; continue; }
                if (MapDirective.IsMatch(ln)) continue;
                var m = MapProtoName.Match(ln);
                if (!m.Success) continue;
                hdr.MapProcs.Add(new KeyValuePair<string, string>(m.Groups[1].Value, StripTrailingComment(ln.Trim()).Trim()));
            }
            return hdr;
        }

        private static List<string> Slice(string[] lines, int start, int end)
        {
            var l = new List<string>(end - start);
            for (int i = start; i < end; i++) l.Add(lines[i]);
            return l;
        }
    }
}
