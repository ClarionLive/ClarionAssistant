using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ClarionAssistant.Services
{
    /// <summary>What a host tells the local layer about its surface (1c685f2e item 4).</summary>
    public sealed class LocalLayerOptions
    {
        /// <summary>The embeditor's procedure (for the routine set of the slot checks); null for a whole file.</summary>
        public string ProcedureName;
        /// <summary>Whether slotDiagnostics runs the slot checks at all: embed mode and the CA Editor overlay
        /// do, the CA Embeditor's plain-source FILE MODE tab does not (ticket 564aa142). False = empty list.</summary>
        public bool SlotChecks;
        /// <summary>Slot ranges to use when the request carries none (the embeditor's load-time ranges).</summary>
        public List<int[]> DefaultRanges;
        /// <summary>The LSP line offset for a buffer (the embeditor's MEMBER header: 0 or 1); null = 0. Passed
        /// to <see cref="LocalLayerHandlers.Handle"/> as lineOffset.</summary>
        public Func<string, int> LineOffsetFor;
        /// <summary>The project's .codegraph.db path (solution globals, the app's classes); null = none. Resolved
        /// at most every few seconds (it may walk directories), never per keystroke.</summary>
        public Func<string> ProjectDbPath;
        /// <summary>The ClarionGraph library DB path (ABC classes); null = none. Cached like ProjectDbPath.</summary>
        public Func<string> LibraryDbPath;
        /// <summary>Shown in a local hover card's detail line; optional.</summary>
        public string FileName;
        /// <summary>For the [local-timing] line.</summary>
        public string Surface;
        /// <summary>Where the [local-timing] line goes (monaco-spike.log in the IDE; a list in tests).</summary>
        public Action<string> Log;
    }

    /// <summary>
    /// The R11 slice a localCompletion / localHover request carries instead of a synced-buffer `v`
    /// (1c685f2e, gate failed: a full sync cost ~100 ms per keystroke on InventoryTable):
    /// <c>slice:{headerHash, span:{start,text}, ownerData:{start,text}|null, routines:[names]}</c>.
    /// Starts are 1-based Monaco lines of the unwrapped buffer.
    /// </summary>
    public sealed class LocalSlice
    {
        public string HeaderHash;
        public int SpanStart;
        public string SpanText;
        public int OwnerStart;
        public string OwnerText;
        public List<string> Routines = new List<string>();

        /// <summary>Chars of text the page shipped (for [local-timing] sliceChars).</summary>
        public int Chars { get { return (SpanText != null ? SpanText.Length : 0) + (OwnerText != null ? OwnerText.Length : 0); } }

        /// <summary>The request's "slice", or null when it has none (the `v` form).</summary>
        public static LocalSlice From(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("slice", out o)) return null;
            var d = o as IDictionary<string, object>;
            if (d == null) return null;
            var s = new LocalSlice();
            object h;
            if (d.TryGetValue("headerHash", out h) && h != null) s.HeaderHash = Convert.ToString(h, System.Globalization.CultureInfo.InvariantCulture);
            ReadPart(d, "span", out s.SpanStart, out s.SpanText);
            ReadPart(d, "ownerData", out s.OwnerStart, out s.OwnerText);
            s.Routines = LocalLayerHandlers.ReadStrings(d, "routines");
            return s.SpanText != null ? s : null;
        }

        private static void ReadPart(IDictionary<string, object> d, string key, out int start, out string text)
        {
            start = 0; text = null;
            object o;
            var part = d.TryGetValue(key, out o) ? o as IDictionary<string, object> : null;
            if (part == null) return;
            start = LocalLayerHandlers.ReadInt(part, "start");
            object t;
            if (part.TryGetValue("text", out t)) text = t as string;
            if (start < 1) text = null;
        }
    }

    /// <summary>
    /// The instant local layer's page requests, answered WITHOUT the language server (1c685f2e item 4):
    /// <c>localCompletion</c>, <c>localHover</c> and <c>slotDiagnostics</c>. Both Monaco hosts route the three
    /// actions here and add only the lane and the reply (MonacoEditorControl.RunLocalAction), so the answer
    /// is the same in the CA Embeditor and the CA Editor overlay.
    ///
    /// The rules that keep it instant, pinned by tests\LocalLayer.SourceScan.ps1 (which lists the exact
    /// forbidden calls): never start the language server, never push the buffer to it, never wait on it or
    /// make any SharedLspBridge request, and never open a database per request. It therefore answers
    /// while the server is starting, busy or down.
    ///
    /// Coordinates: requests carry Monaco positions (1-based) on the UNWRAPPED buffer, and every local
    /// lookup works in exactly those. Only LSP positions use the embeditor's MEMBER-header offset, so the
    /// local layer never applies <c>lineOffset</c> to a lookup.
    ///
    /// No IDE references: compiles under the csc harnesses.
    /// </summary>
    public static class LocalLayerHandlers
    {
        public const string LocalCompletion = "localCompletion";
        public const string LocalHover = "localHover";
        public const string SlotDiagnostics = "slotDiagnostics";

        /// <summary>
        /// Answer one local request. <paramref name="args"/> is the parsed page request (line, column,
        /// triggerCharacter, ranges ...). <paramref name="lineOffset"/> is the LSP line offset for this buffer,
        /// passed for completeness; local lookups do not use it (see the class comment). Never throws: an
        /// unknown action or a failure answers the action's empty shape. Writes one
        /// <c>[local-timing] action= ms= items=</c> line to <see cref="LocalLayerOptions.Log"/>.
        /// </summary>
        public static Dictionary<string, object> Handle(string action, string buffer, IDictionary<string, object> args,
            int lineOffset, LocalLayerOptions options)
        {
            options = options ?? new LocalLayerOptions();
            var sw = Stopwatch.StartNew();
            Dictionary<string, object> reply;
            int items = 0;
            int sliceChars = -1;   // -1 = the request used the synced buffer (`v`), not a slice
            string error = null;
            try
            {
                switch (action)
                {
                    case SlotDiagnostics:
                        {
                            var markers = Slot(buffer, args, options, out sliceChars);
                            items = markers.Count;
                            reply = new Dictionary<string, object> { { "markers", markers } };
                            break;
                        }
                    case LocalCompletion:
                    case LocalHover:
                        {
                            var slice = LocalSlice.From(args);
                            if (slice != null) sliceChars = slice.Chars;
                            // The slice path lands with Coder-A's slice overloads (R11); until then a slice
                            // request answers its empty shape and the `v` form below does the work.
                            if (slice != null || string.IsNullOrEmpty(buffer)) { reply = EmptyReply(action); break; }
                            int line0 = ReadInt(args, "line") - 1, col0 = ReadInt(args, "column") - 1;
                            if (action == LocalCompletion)
                            {
                                object t;
                                string trig = args != null && args.TryGetValue("triggerCharacter", out t) ? t as string : null;
                                var list = CompleteAt(buffer, line0, col0, string.IsNullOrEmpty(trig) ? (char?)null : trig[0], options);
                                items = list.Count;
                                reply = new Dictionary<string, object> { { "items", ToPage(list) }, { "source", "local" } };
                            }
                            else
                            {
                                var h = HoverAt(buffer, line0, col0, options);
                                items = h != null ? 1 : 0;
                                reply = new Dictionary<string, object> { { "contents", h != null ? h.Markdown : null }, { "authoritative", h != null && h.Authoritative } };
                            }
                            break;
                        }
                    default:
                        reply = EmptyReply(action);
                        error = "unknown action";
                        break;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                reply = EmptyReply(action);
                items = 0;
            }
            long ms = sw.ElapsedMilliseconds;
            reply["ms"] = ms;
            WriteTiming(options, action, ms, items, sliceChars, error);
            return reply;
        }

        // ====================================================================== completion and hover
        // Composition order (the contract): the buffer first, then the indexes, and a local name always
        // wins a label clash. Every source here is in memory or a held-open, index-backed SQLite handle
        // that never waits more than 50 ms (SymbolIndex); a DB without the NOCASE indexes is skipped.

        private const int DbLimit = 50;
        private static readonly System.Text.RegularExpressions.Regex BarePrefix =
            new System.Text.RegularExpressions.Regex(@"[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly System.Text.RegularExpressions.Regex PreQualifier =
            new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_:.])([A-Za-z_][A-Za-z0-9_]*):([A-Za-z0-9_]*)$");
        private static readonly System.Text.RegularExpressions.Regex DoContext =
            new System.Text.RegularExpressions.Regex(@"(?:^|\s|;)DO\s+[A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>localCompletion over the synced buffer at 0-based (line, column).</summary>
        internal static List<LspClient.CompletionItemInfo> CompleteAt(string buffer, int line0, int col0, char? trigger, LocalLayerOptions options)
        {
            var result = new List<LspClient.CompletionItemInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 1. The buffer: locals, parameters, module data, local procedures, routines, PRE'd structures,
            //    members of an in-scope CLASS.
            AddAll(result, seen, LocalScopeIndex.Complete(buffer, line0, col0, trigger));

            var scope = LocalScopeIndex.GetScope(buffer, line0);
            if (scope == null) return result;
            string lineText = scope.CaretLine;
            int col = Math.Max(0, Math.Min(col0, lineText.Length));
            if (LocalScopeIndex.IsInsideStringOrComment(lineText, col)) return result;
            string upTo = lineText.Substring(0, col);
            if (upTo.EndsWith("?")) return result;

            // 2. "x." member access: the parent/declared class's members from the DBs (inherited included).
            var ma = LocalScopeIndex.GetMemberAccess(buffer, line0, col0);
            if (ma != null)
            {
                if (!string.IsNullOrEmpty(ma.BaseType))
                {
                    string proj = UsableDb(ProjectDb(options)), lib = UsableDb(LibraryDb(options));
                    if (proj != null || lib != null)
                    {
                        string partial = ma.Partial ?? "";
                        foreach (var s in SymbolIndex.MembersOf(ma.BaseType, true, proj, lib))
                        {
                            string member = s == null ? null : SymbolIndex.MemberName(s.Name);
                            if (member == null || (partial.Length > 0 && !member.StartsWith(partial, StringComparison.OrdinalIgnoreCase))) continue;
                            if (seen.Add(member)) result.Add(SymbolIndex.ToMemberItem(s));
                        }
                    }
                }
                return result;
            }

            // 3. "PRE:" dictionary fields and keys (live snapshot only: no SQLite fallback in this lane).
            var q = PreQualifier.Match(upTo);
            if (q.Success)
            {
                AddAll(result, seen, LiveDictionaryIndex.CompleteQualifier(q.Groups[1].Value, q.Groups[2].Value, null));
                return result;
            }

            // 4. A bare prefix of 2+ characters: solution and library symbols, keywords, dictionary tables.
            if (DoContext.IsMatch(upTo)) return result;   // DO: routines only (step 1)
            var m = BarePrefix.Match(upTo);
            if (!m.Success || m.Length < 2) return result;
            if (m.Index > 0 && (upTo[m.Index - 1] == '.' || upTo[m.Index - 1] == ':')) return result;
            string prefix = m.Value;
            foreach (string db in new[] { UsableDb(ProjectDb(options)), UsableDb(LibraryDb(options)) })
            {
                if (db == null) continue;
                var idx = SymbolIndex.For(db);
                if (idx == null) continue;
                foreach (var s in idx.ByPrefix(prefix, DbLimit))
                    if (s != null && !string.IsNullOrEmpty(s.Name) && seen.Add(s.Name)) result.Add(SymbolIndex.ToCompletionItem(s));
            }
            AddAll(result, seen, ClarionKeywordIndex.Complete(prefix));
            AddAll(result, seen, LiveDictionaryIndex.CompleteTableNames(prefix, 25, null));
            return result;
        }

        /// <summary>localHover: the buffer (authoritative for locals, parameters, routines, local procedures),
        /// then the live dictionary, then the solution and library DBs, then keywords/built-ins.</summary>
        internal static LocalHoverResult HoverAt(string buffer, int line0, int col0, LocalLayerOptions options)
        {
            var local = LocalScopeIndex.Hover(buffer, line0, col0, options.FileName);
            if (local != null) return local;
            var scope = LocalScopeIndex.GetScope(buffer, line0);
            if (scope == null) return null;
            string lineText = scope.CaretLine;
            int col = Math.Max(0, Math.Min(col0, lineText.Length));
            if (LocalScopeIndex.IsInsideStringOrComment(lineText, col)) return null;
            string word = LocalScopeIndex.WordAt(lineText, col);
            if (string.IsNullOrEmpty(word)) return null;

            var dict = LiveDictionaryIndex.HoverWord(word);
            if (dict != null) return dict;

            if (word.IndexOf('.') < 0)
            {
                foreach (string db in new[] { UsableDb(ProjectDb(options)), UsableDb(LibraryDb(options)) })
                {
                    if (db == null) continue;
                    var idx = SymbolIndex.For(db);
                    var s = idx != null ? idx.FindByName(word) : null;
                    if (s != null) return new LocalHoverResult { Markdown = SymbolCard(s), Authoritative = false, Kind = "index" };
                }
            }
            return ClarionKeywordIndex.HoverWord(word);
        }

        private static string SymbolCard(ClarionCodeGraph.Graph.CodeGraphSymbol s)
        {
            string sig = s.Name + (string.IsNullOrEmpty(s.Params) ? "" : " " + s.Params) +
                         (string.IsNullOrEmpty(s.ReturnType) ? "" : " : " + s.ReturnType);
            string where = string.IsNullOrEmpty(s.FilePath) ? "" :
                "\n\n" + System.IO.Path.GetFileName(s.FilePath) + (s.LineNumber > 0 ? ":" + s.LineNumber : "");
            return "```clarion\n" + sig + "\n```\n" + SymbolIndex.CompletionDetail(s) + where;
        }

        private static void AddAll(List<LspClient.CompletionItemInfo> into, HashSet<string> seen, List<LspClient.CompletionItemInfo> from)
        {
            if (from == null) return;
            foreach (var it in from)
                if (it != null && !string.IsNullOrEmpty(it.Label) && seen.Add(it.Label)) into.Add(it);
        }

        private static List<Dictionary<string, object>> ToPage(List<LspClient.CompletionItemInfo> list)
        {
            var items = new List<Dictionary<string, object>>(list.Count);
            foreach (var c in list)
                items.Add(new Dictionary<string, object>
                {
                    { "label", c.Label }, { "kind", c.Kind }, { "detail", c.Detail },
                    { "documentation", c.Documentation }, { "insertText", c.InsertText }
                });
            return items;
        }

        // DB paths come from host providers that may walk directories or ask the IDE, so they are resolved at
        // most every PathTtlMs, never per keystroke. A DB without the NOCASE indexes (an older index build) is
        // skipped here once SymbolIndex knows it (after its first query, which pays the ~150 ms fallback once):
        // that scan is too slow for this lane. The late merge still uses it.
        private const int PathTtlMs = 5000;
        private static readonly object PathGate = new object();
        private static string _projPath, _libPath;
        private static long _projAt = -PathTtlMs, _libAt = -PathTtlMs;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static string ProjectDb(LocalLayerOptions o) { return Cached(o.ProjectDbPath, ref _projPath, ref _projAt); }
        private static string LibraryDb(LocalLayerOptions o) { return Cached(o.LibraryDbPath, ref _libPath, ref _libAt); }

        private static string Cached(Func<string> provider, ref string path, ref long at)
        {
            if (provider == null) return null;
            lock (PathGate)
            {
                long now = Clock.ElapsedMilliseconds;
                if (now - at >= PathTtlMs)
                {
                    string p = null;
                    try { p = provider(); } catch { }
                    path = !string.IsNullOrEmpty(p) && System.IO.File.Exists(p) ? p : null;
                    at = now;
                }
                return path;
            }
        }

        /// <summary>Test hook: forget the cached DB paths.</summary>
        internal static void ResetPathCache() { lock (PathGate) { _projAt = _libAt = -PathTtlMs; _projPath = _libPath = null; } }

        private static string UsableDb(string path)
        {
            if (path == null) return null;
            var idx = SymbolIndex.For(path);
            return idx != null && !idx.NoIndex ? path : null;
        }

        private static Dictionary<string, object> EmptyReply(string action)
        {
            switch (action)
            {
                case SlotDiagnostics: return new Dictionary<string, object> { { "markers", new List<Dictionary<string, object>>() } };
                case LocalCompletion: return new Dictionary<string, object> { { "items", new List<Dictionary<string, object>>() }, { "source", "local" } };
                case LocalHover: return new Dictionary<string, object> { { "contents", null }, { "authoritative", false } };
                default: return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// slotDiagnostics. The R11 slice form <c>{procedureName, routines:[..], slots:[{start,text}]}</c> carries the
        /// slots' own text (no synced buffer); without <c>slots</c> the request refers to the synced buffer by `v`
        /// and its <c>ranges</c>, as before.
        /// </summary>
        private static List<Dictionary<string, object>> Slot(string buffer, IDictionary<string, object> args,
            LocalLayerOptions options, out int sliceChars)
        {
            sliceChars = -1;
            var slots = ReadSlots(args);
            if (slots != null)
            {
                sliceChars = 0;
                foreach (var s in slots) sliceChars += s.Text != null ? s.Text.Length : 0;
            }
            if (!options.SlotChecks) return new List<Dictionary<string, object>>();
            if (slots != null) return ModernEmbeditorDiagnostics.ComputeSlotChecks(slots, ReadStrings(args, "routines"));
            var ranges = ReadRanges(args);
            if (ranges == null || ranges.Count == 0) ranges = options.DefaultRanges;
            return ModernEmbeditorDiagnostics.ComputeSlotChecks(buffer, ranges, options.ProcedureName);
        }

        /// <summary>The request's "ranges": an array of [start,end] 1-based line pairs; null when absent.</summary>
        public static List<int[]> ReadRanges(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("ranges", out o) || o == null) return null;
            var ranges = new List<int[]>();
            foreach (var item in AsArray(o))
            {
                var pair = AsArray(item);
                if (pair.Length >= 2)
                {
                    try { ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) }); } catch { }
                }
            }
            return ranges;
        }

        /// <summary>
        /// The <c>{type:'spanMap', v, headerHash, procs:[{name,start,dataEnd,end,owner,routines}]}</c> push for a
        /// fully synced buffer (R11), or null when none can be built. PENDING Coder-A's merge: the map comes from
        /// LocalScopeIndex.BuildSpanMap, which also caches the module header by hash. Until then no map is
        /// pushed, and the page keeps using `v` for its local requests (the R11 fallback).
        /// </summary>
        public static string SpanMapMessage(long v, string buffer, Action<string> log)
        {
            return null;
        }

        /// <summary>
        /// The page's <c>{action:'headerSync', hash, text}</c>: the module header for a hash the host answered
        /// {needHeader:true} for. PENDING Coder-A's merge for the store (LocalScopeIndex's header cache); for now
        /// it is parsed and logged. Never throws.
        /// </summary>
        public static bool AcceptHeaderSync(string json, Action<string> log)
        {
            string hash = null, text = null;
            try
            {
                var d = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as IDictionary<string, object>;
                object o;
                if (d != null && d.TryGetValue("hash", out o) && o != null) hash = Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture);
                if (d != null && d.TryGetValue("text", out o)) text = o as string;
            }
            catch { }
            bool ok = !string.IsNullOrEmpty(hash) && text != null;
            if (log != null)
            {
                try { log("[local-timing] action=headerSync " + (ok ? "hash=" + hash + " chars=" + text.Length : "refused (no hash or text)")); }
                catch { }
            }
            return ok;
        }

        /// <summary>True when the request carries its own text (R11 slice form) and needs no synced buffer.</summary>
        public static bool CarriesSlice(IDictionary<string, object> args)
        {
            return args != null && (args.ContainsKey("slice") || args.ContainsKey("slots"));
        }

        /// <summary>The slotDiagnostics slice's "slots": [{start, text}]; null when absent (the `v` form).</summary>
        public static List<ModernEmbeditorDiagnostics.SlotText> ReadSlots(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("slots", out o)) return null;
            var slots = new List<ModernEmbeditorDiagnostics.SlotText>();
            foreach (var item in AsArray(o))
            {
                var d = item as IDictionary<string, object>;
                if (d == null) continue;
                int start = ReadInt(d, "start");
                object t;
                string text = d.TryGetValue("text", out t) ? t as string : null;
                if (start >= 1 && text != null) slots.Add(new ModernEmbeditorDiagnostics.SlotText { Start = start, Text = text });
            }
            return slots;
        }

        /// <summary>A string array field (e.g. "routines"); empty when absent.</summary>
        public static List<string> ReadStrings(IDictionary<string, object> args, string key)
        {
            var list = new List<string>();
            object o;
            if (args == null || !args.TryGetValue(key, out o)) return list;
            foreach (var item in AsArray(o)) { var s = item as string; if (!string.IsNullOrEmpty(s)) list.Add(s); }
            return list;
        }

        internal static object[] AsArray(object o)
        {
            var arr = o as object[];
            if (arr != null) return arr;
            var list = o as System.Collections.IList;
            if (list == null) return new object[0];
            arr = new object[list.Count];
            list.CopyTo(arr, 0);
            return arr;
        }

        internal static int ReadInt(IDictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return 0;
            try { return Convert.ToInt32(o); } catch { return 0; }
        }

        private static void WriteTiming(LocalLayerOptions options, string action, long ms, int items, int sliceChars, string error)
        {
            var log = options.Log;
            if (log == null) return;
            try
            {
                log("[local-timing] action=" + (action ?? "?") + " ms=" + ms + " items=" + items +
                    " sliceChars=" + (sliceChars >= 0 ? sliceChars.ToString() : "none(v)") +
                    (options.Surface != null ? " surface=" + options.Surface : "") +
                    (error != null ? " error=" + error : ""));
            }
            catch { }
        }
    }
}
