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
        /// <summary>Shown in a local hover card's detail line; optional.</summary>
        public string FileName;
        /// <summary>For the [local-timing] line.</summary>
        public string Surface;
        /// <summary>Where the [local-timing] line goes (monaco-spike.log in the IDE; a list in tests).</summary>
        public Action<string> Log;
    }

    /// <summary>
    /// The R11b slice a localCompletion / localHover request carries instead of a synced-buffer `v`
    /// (1c685f2e, gate failed: a full sync cost ~100 ms per keystroke on InventoryTable):
    /// <c>slice:{headerHash, routines:[names], pieces:[{start, text?, hash?}]}</c>. Starts are 1-based Monaco
    /// lines of the unwrapped buffer. A piece the page has not edited since the span map arrived travels as
    /// its hash only; the host resolves it from the text it cached when it built that map.
    /// </summary>
    public sealed class LocalSlice
    {
        public string HeaderHash;
        /// <summary>The header's CURRENT text, sent while the page's header is edited since the last span map
        /// (pipeline F2). When present it is used instead of the cached header, and never answers needHeader.</summary>
        public string HeaderText;
        public List<string> Routines = new List<string>();
        /// <summary>How many pieces the request carried before filtering (for the F6 bound).</summary>
        public int RawPieceCount;
        /// <summary>A hash-only piece (Text null) is resolved inside LocalScopeIndex's list overloads.</summary>
        public readonly List<SlicePiece> Pieces = new List<SlicePiece>();

        /// <summary>Chars of text the page shipped (for [local-timing] sliceChars); hash-only pieces cost 0.</summary>
        public int Chars
        {
            get { return LocalLayerHandlers.TextChars(Pieces, p => p.Text); }
        }

        /// <summary>The request's "slice", or null when it has none (the `v` form).</summary>
        public static LocalSlice From(IDictionary<string, object> args)
        {
            object o;
            if (args == null || !args.TryGetValue("slice", out o)) return null;
            var d = o as IDictionary<string, object>;
            if (d == null) return null;
            var s = new LocalSlice { HeaderHash = Str(d, "headerHash"), HeaderText = Str(d, "headerText"), Routines = LocalLayerHandlers.ReadStrings(d, "routines") };
            object ps;
            var raw = d.TryGetValue("pieces", out ps) ? LocalLayerHandlers.AsArray(ps) : new object[0];
            s.RawPieceCount = raw.Length;
            if (raw.Length <= WebMessageGuard.MaxPieces)   // over the bound: rejected by the caller, not parsed
                foreach (var item in raw)
                {
                    var pd = item as IDictionary<string, object>;
                    if (pd == null) continue;
                    var p = new SlicePiece { Start = LocalLayerHandlers.ReadInt(pd, "start"), Text = Str(pd, "text"), Hash = Str(pd, "hash") };
                    if (p.Start >= 1 && (p.Text != null || !string.IsNullOrEmpty(p.Hash))) s.Pieces.Add(p);
                }
            return s;
        }

        private static string Str(IDictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        /// <summary>
        /// The header text, and whether everything the slice names is in the host's caches. False when something
        /// is missing: <paramref name="needHeader"/> when the header hash is unknown, <paramref name="needPieces"/>
        /// with the hashes of hash-only pieces the host cannot resolve. The page then resends and retries once.
        /// </summary>
        public bool TryResolve(out string headerText, out bool needHeader, out List<string> needPieces)
        {
            headerText = HeaderText;
            needHeader = headerText == null && !LocalScopeIndex.TryGetHeaderText(HeaderHash, out headerText);
            needPieces = LocalScopeIndex.MissingPieces(Pieces) ?? new List<string>();
            return !needHeader && needPieces.Count == 0;
        }
    }

    /// <summary>What a local answer is computed over: the synced buffer (`v` form) or a resolved slice.</summary>
    internal sealed class LocalSource
    {
        private readonly string _buffer;
        private readonly string _header;
        private readonly List<SlicePiece> _pieces;
        private readonly List<string> _routines;

        public static LocalSource OfBuffer(string buffer) { return new LocalSource(buffer, null, null, null); }
        public static LocalSource OfSlice(string header, List<SlicePiece> pieces, List<string> routines) { return new LocalSource(null, header, pieces, routines); }

        private LocalSource(string buffer, string header, List<SlicePiece> pieces, List<string> routines)
        {
            _buffer = buffer; _header = header; _pieces = pieces; _routines = routines;
        }

        public List<LspClient.CompletionItemInfo> Complete(int line0, int col0, char? trigger)
        {
            return _buffer != null ? LocalScopeIndex.Complete(_buffer, line0, col0, trigger)
                                   : LocalScopeIndex.Complete(_header, _pieces, _routines, line0, col0, trigger);
        }

        public LocalHoverResult Hover(int line0, int col0, string fileName)
        {
            return _buffer != null ? LocalScopeIndex.Hover(_buffer, line0, col0, fileName)
                                   : LocalScopeIndex.Hover(_header, _pieces, _routines, line0, col0, fileName);
        }

        public LocalMemberAccess MemberAccess(int line0, int col0)
        {
            return _buffer != null ? LocalScopeIndex.GetMemberAccess(_buffer, line0, col0)
                                   : LocalScopeIndex.GetMemberAccess(_header, _pieces, _routines, line0, col0);
        }

        /// <summary>The caret's line text, or null when neither the buffer nor any piece holds that line.</summary>
        public string CaretLine(int line0)
        {
            if (_buffer != null)
            {
                var scope = LocalScopeIndex.GetScope(_buffer, line0);
                return scope != null ? scope.CaretLine : null;
            }
            if (_pieces != null)
                foreach (var p in _pieces)
                {
                    string text = p.Text;
                    if (text == null) LocalScopeIndex.TryGetPieceText(p.Hash, out text);   // a hash-only piece
                    string line = LineOf(text, line0 - (p.Start - 1));
                    if (line != null) return line;
                }
            return null;
        }

        /// <summary>Line <paramref name="index"/> (0-based) of <paramref name="text"/> without its EOL, or null.</summary>
        internal static string LineOf(string text, int index)
        {
            if (text == null || index < 0) return null;
            int at = 0;
            for (int i = 0; i < index; i++)
            {
                at = text.IndexOf('\n', at);
                if (at < 0) return null;
                at++;
            }
            int end = text.IndexOf('\n', at);
            string line = end < 0 ? text.Substring(at) : text.Substring(at, end - at);
            return line.TrimEnd('\r');
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
    /// lookup works in exactly those. Only LSP positions use the embeditor's MEMBER-header offset, which the
    /// local layer therefore never takes.
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
        /// triggerCharacter, ranges, slice, slots ...); <paramref name="buffer"/> is the synced buffer for a `v`
        /// request, null for a slice. Never throws: an unknown action or a failure answers the action's empty
        /// shape. Writes one <c>[local-timing] action= ms= items= sliceChars=</c> line to <see cref="LocalLayerOptions.Log"/>.
        /// </summary>
        public static Dictionary<string, object> Handle(string action, string buffer, IDictionary<string, object> args,
            LocalLayerOptions options)
        {
            options = options ?? new LocalLayerOptions();
            var sw = Stopwatch.StartNew();
            Dictionary<string, object> reply;
            int items = 0;
            int sliceChars = -1;   // -1 = the request used the synced buffer (`v`), not a slice
            string error = null;
            try
            {
                LocalSlice slice = action == SlotDiagnostics ? null : LocalSlice.From(args);
                string reject = CheckBounds(action, buffer, args, slice);
                if (reject != null)
                {
                    WebMessageGuard.LogReject(options.Log, action,
                        slice != null ? slice.Chars : buffer != null ? buffer.Length : 0, reject);
                    reply = EmptyReply(action);
                    error = "rejected: " + reject;
                }
                else switch (action)
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
                            LocalSource source;
                            if (slice != null)
                            {
                                sliceChars = slice.Chars;
                                string header; bool needHeader; List<string> needPieces;
                                if (!slice.TryResolve(out header, out needHeader, out needPieces))
                                {
                                    // The page resends what is missing (headerSync / the pieces with text) and retries once.
                                    reply = EmptyReply(action);
                                    if (needHeader) reply["needHeader"] = true;
                                    if (needPieces.Count > 0) reply["needPieces"] = needPieces;
                                    error = (needHeader ? "needHeader " : "") + (needPieces.Count > 0 ? "needPieces=" + needPieces.Count : "");
                                    break;
                                }
                                source = LocalSource.OfSlice(header, slice.Pieces, slice.Routines);
                            }
                            else if (!string.IsNullOrEmpty(buffer)) source = LocalSource.OfBuffer(buffer);
                            else { reply = EmptyReply(action); break; }

                            int line0 = ReadInt(args, "line") - 1, col0 = ReadInt(args, "column") - 1;
                            if (action == LocalCompletion)
                            {
                                object t;
                                string trig = args != null && args.TryGetValue("triggerCharacter", out t) ? t as string : null;
                                var list = CompleteAt(source, line0, col0, string.IsNullOrEmpty(trig) ? (char?)null : trig[0], options);
                                items = list.Count;
                                reply = new Dictionary<string, object> { { "items", ToPage(list) }, { "source", "local" } };
                            }
                            else
                            {
                                var h = HoverAt(source, line0, col0, options);
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

        /// <summary>
        /// 1c685f2e F6: the parsed payload's bounds. Null when acceptable, else the reason. Pieces at most 8,
        /// routines at most 10,000 of at most 256 chars, slots/ranges at most 2,000, and for completion/hover a
        /// caret (1-based line and column) that lies inside the buffer or inside a piece of the slice (a hash-only
        /// piece is read from the cache; one the host lacks leaves the check to the needPieces reply).
        /// </summary>
        internal static string CheckBounds(string action, string buffer, IDictionary<string, object> args, LocalSlice slice)
        {
            List<string> routines;
            if (action == SlotDiagnostics)
            {
                object o;
                if (args != null && args.TryGetValue("slots", out o) && AsArray(o).Length > WebMessageGuard.MaxSlots) return "more than " + WebMessageGuard.MaxSlots + " slots";
                if (args != null && args.TryGetValue("ranges", out o) && AsArray(o).Length > WebMessageGuard.MaxSlots) return "more than " + WebMessageGuard.MaxSlots + " ranges";
                routines = ReadStrings(args, "routines");
            }
            else
            {
                if (slice != null && slice.RawPieceCount > WebMessageGuard.MaxPieces) return "more than " + WebMessageGuard.MaxPieces + " pieces";
                routines = slice != null ? slice.Routines : null;
            }
            if (routines != null)
            {
                if (routines.Count > WebMessageGuard.MaxRoutines) return "more than " + WebMessageGuard.MaxRoutines + " routines";
                foreach (var r in routines) if (r.Length > WebMessageGuard.MaxRoutineNameChars) return "a routine name over " + WebMessageGuard.MaxRoutineNameChars + " chars";
            }
            if (action != LocalCompletion && action != LocalHover) return null;

            int line = ReadInt(args, "line"), column = ReadInt(args, "column");
            if (line < 1 || column < 1) return "line/column below 1";
            string lineText = null;
            if (slice != null)
            {
                bool unresolved = false;   // a hash-only piece the host lacks: the needPieces reply comes first
                foreach (var p in slice.Pieces)
                {
                    string text = p.Text;
                    if (text == null && !LocalScopeIndex.TryGetPieceText(p.Hash, out text)) { unresolved = true; continue; }
                    if (line >= p.Start && (lineText = LocalSource.LineOf(text, line - p.Start)) != null) break;
                }
                if (lineText == null && !unresolved) return "caret line " + line + " outside the slice's text";
            }
            else if (buffer != null && (lineText = LocalSource.LineOf(buffer, line - 1)) == null) return "line " + line + " past the buffer end";
            if (lineText != null && column - 1 > lineText.Length) return "column " + column + " past the line end";
            return null;
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

        /// <summary>localCompletion over the buffer or slice at 0-based (line, column).</summary>
        internal static List<LspClient.CompletionItemInfo> CompleteAt(LocalSource source, int line0, int col0, char? trigger, LocalLayerOptions options)
        {
            var result = new List<LspClient.CompletionItemInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 1. The buffer: locals, parameters, module data, local procedures, routines, PRE'd structures,
            //    members of an in-scope CLASS.
            AddAll(result, seen, source.Complete(line0, col0, trigger));

            string lineText = source.CaretLine(line0);
            if (lineText == null) return result;
            int col = Math.Max(0, Math.Min(col0, lineText.Length));
            if (LocalScopeIndex.IsInsideStringOrComment(lineText, col)) return result;
            string upTo = lineText.Substring(0, col);
            if (upTo.EndsWith("?")) return result;

            // 2. "x." member access: the parent/declared class's members from the DBs (inherited included).
            var ma = source.MemberAccess(line0, col0);
            if (ma != null)
            {
                if (!string.IsNullOrEmpty(ma.BaseType))
                {
                    string proj = ProjectDb(), lib = LibraryDb();
                    if (proj != null || lib != null)
                    {
                        string partial = ma.Partial ?? "";
                        foreach (var s in SymbolIndex.MembersOf(ma.BaseType, true, proj, lib, fastOnly: true))
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
            foreach (string db in new[] { ProjectDb(), LibraryDb() })
            {
                var idx = SymbolIndex.For(db);
                if (idx == null) continue;
                foreach (var s in idx.ByPrefix(prefix, DbLimit, fastOnly: true))
                    if (s != null && !string.IsNullOrEmpty(s.Name) && seen.Add(s.Name)) result.Add(SymbolIndex.ToCompletionItem(s));
            }
            AddAll(result, seen, ClarionKeywordIndex.Complete(prefix));
            AddAll(result, seen, LiveDictionaryIndex.CompleteTableNames(prefix, 25, null));
            return result;
        }

        /// <summary>localHover: the buffer (authoritative for locals, parameters, routines, local procedures),
        /// then the live dictionary, then the solution and library DBs, then keywords/built-ins.</summary>
        internal static LocalHoverResult HoverAt(LocalSource source, int line0, int col0, LocalLayerOptions options)
        {
            var local = source.Hover(line0, col0, options.FileName);
            if (local != null) return local;
            string lineText = source.CaretLine(line0);
            if (lineText == null) return null;
            int col = Math.Max(0, Math.Min(col0, lineText.Length));
            if (LocalScopeIndex.IsInsideStringOrComment(lineText, col)) return null;
            string word = LocalScopeIndex.WordAt(lineText, col);
            if (string.IsNullOrEmpty(word)) return null;

            var dict = LiveDictionaryIndex.HoverWord(word);
            if (dict != null) return dict;

            if (word.IndexOf('.') < 0)
            {
                foreach (string db in new[] { ProjectDb(), LibraryDb() })
                {
                    var idx = SymbolIndex.For(db);
                    var s = idx != null ? idx.FindByName(word, fastOnly: true) : null;
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
        // most every PathTtlMs, never per keystroke. Every query here passes fastOnly: a DB without the NOCASE
        // indexes (an older index build) answers nothing rather than running its ~150 ms fallback scan. The
        // late merge still uses it.
        private const int PathTtlMs = 5000;
        private static readonly object PathGate = new object();
        private static string _projPath, _libPath;
        private static long _projAt = -PathTtlMs, _libAt = -PathTtlMs;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>The project's .codegraph.db path (solution globals, the app's classes); null = none. Set once
        /// by the host at startup, like SymbolIndex.LogSink: every surface uses the same databases.</summary>
        public static Func<string> ProjectDbPath;
        /// <summary>The ClarionGraph library DB path (ABC classes); null = none.</summary>
        public static Func<string> LibraryDbPath;

        private static string ProjectDb() { return Cached(ProjectDbPath, ref _projPath, ref _projAt); }
        private static string LibraryDb() { return Cached(LibraryDbPath, ref _libPath, ref _libAt); }

        private static string Cached(Func<string> provider, ref string path, ref long at)
        {
            if (provider == null) return null;
            lock (PathGate)
            {
                if (Clock.ElapsedMilliseconds - at < PathTtlMs) return path;
                at = Clock.ElapsedMilliseconds;   // claim the refresh: other lanes keep the cached value meanwhile
            }
            // Resolve outside the lock: the provider may walk directories, and the other local lanes must not wait.
            string p = null;
            try { p = provider(); } catch { }
            if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) p = null;
            lock (PathGate) { path = p; return p; }
        }

        /// <summary>Test hook: forget the cached DB paths.</summary>
        internal static void ResetPathCache() { lock (PathGate) { _projAt = _libAt = -PathTtlMs; _projPath = _libPath = null; } }

        public static bool IsLocalAction(string action)
        {
            return action == LocalCompletion || action == LocalHover || action == SlotDiagnostics;
        }

        /// <summary>The action's reply with nothing in it (what a refused or unanswerable request gets).</summary>
        public static Dictionary<string, object> EmptyReply(string action)
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
            if (slots != null) sliceChars = TextChars(slots, s => s.Text);
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
        /// The R11b <c>{type:'spanMap', v, headerHash, procs:[{name, start, dataEnd, end, owner, dataHash, routines,
        /// routineSpans:[{name, start, dataEnd, dataHash}]}]}</c> push for a fully synced buffer, or null when the
        /// buffer has no procedures. Building it (LocalScopeIndex.BuildSpanMap) also caches the module header and
        /// every DATA piece under their hashes, which is what lets the page send those pieces as a hash only.
        /// Never throws. One <c>[local-timing] action=spanMap</c> line.
        /// </summary>
        public static string SpanMapMessage(long v, string buffer, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            string json = null;
            int procs = 0;
            string error = null;
            try
            {
                var map = LocalScopeIndex.BuildSpanMap(buffer);
                procs = map.Procs.Count;
                if (procs > 0 && !string.IsNullOrEmpty(map.HeaderHash))
                {
                    var list = new List<Dictionary<string, object>>(procs);
                    foreach (var p in map.Procs)
                    {
                        var spans = new List<Dictionary<string, object>>(p.RoutineSpans.Count);
                        foreach (var r in p.RoutineSpans)
                            spans.Add(new Dictionary<string, object>
                            {
                                { "name", r.Name }, { "start", r.Start }, { "dataEnd", r.DataEnd }, { "dataHash", r.DataHash }
                            });
                        list.Add(new Dictionary<string, object>
                        {
                            { "name", p.Name }, { "start", p.Start }, { "dataEnd", p.DataEnd }, { "end", p.End },
                            { "owner", p.Owner }, { "dataHash", p.DataHash }, { "routines", p.Routines }, { "routineSpans", spans }
                        });
                    }
                    json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(
                        new Dictionary<string, object> { { "type", "spanMap" }, { "v", v }, { "headerHash", map.HeaderHash }, { "procs", list } });
                }
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; json = null; }
            if (log != null)
            {
                try
                {
                    log("[local-timing] action=spanMap v=" + v + " ms=" + sw.ElapsedMilliseconds + " procs=" + procs +
                        " msgChars=" + (json != null ? json.Length : 0) + (error != null ? " error=" + error : ""));
                }
                catch { }
            }
            return json;
        }

        /// <summary>
        /// The page's <c>{action:'headerSync', hash, text}</c> (parsed by the control): the module header for a
        /// hash the host answered {needHeader:true} for. Cached under that hash (LocalScopeIndex.RegisterHeader)
        /// so the page's retry resolves. Never throws.
        /// </summary>
        public static bool AcceptHeader(string hash, string text, Action<string> log)
        {
            bool matched = false;
            bool ok = !string.IsNullOrEmpty(hash) && text != null;
            if (ok) { try { matched = LocalScopeIndex.RegisterHeader(hash, text); } catch { ok = false; } }
            if (log != null)
            {
                try
                {
                    log("[local-timing] action=headerSync " + (ok
                        ? "hash=" + hash + " chars=" + text.Length + (matched ? "" : " (edited since the map: cached under both hashes)")
                        : "refused (no hash or text)"));
                }
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

        /// <summary>Total text a slice carried, for [local-timing] sliceChars (text-less pieces count 0).</summary>
        internal static int TextChars<T>(IEnumerable<T> items, Func<T, string> text)
        {
            int n = 0;
            foreach (var it in items) { string t = text(it); n += t != null ? t.Length : 0; }
            return n;
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
