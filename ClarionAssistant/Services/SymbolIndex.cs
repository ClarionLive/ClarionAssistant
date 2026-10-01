using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using ClarionCodeGraph.Graph;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Keystroke-speed symbol lookups over a CodeGraph-schema database (the solution's .codegraph.db
    /// or the ClarionGraph library DB), through ONE held-open read-only connection per DB path
    /// (1c685f2e). The merges it replaces opened a fresh connection per merge per keystroke and ran
    /// full-table LIKE scans: 150-500 ms each on the 422k-symbol v61POSitive db.
    ///
    /// QUERIES use the NOCASE indexes <see cref="NameIndex"/> and <see cref="ParentIndex"/>, which
    /// CodeGraphDatabase's schema now creates (the indexer applies them on its next write open). A DB
    /// written by an older build has neither: the queries then fall back to the old LIKE/LOWER forms,
    /// capped by LIMIT, and <see cref="NoIndex"/> is set and logged once per open.
    ///
    /// LIFECYCLE. A held handle blocks File.Delete on Windows, and a cancelled full reindex deletes
    /// the DB. So the connection is released:
    ///  - when the DB file's size or write time changes (checked on every query),
    ///  - by <see cref="Release"/> / <see cref="ReleaseAll"/>, which the reindex and delete paths call
    ///    first, as do the solution-switch/close paths.
    ///
    /// NEVER BLOCKS FOR LONG. A query that cannot take the connection within
    /// <see cref="LockWaitMs"/> (another query is running) returns empty, and SQLITE_BUSY is not
    /// retried. Completion must not wait on a lookup.
    ///
    /// No IDE references: compiles under the csc-shim harness and into the standalone MCP server.
    /// </summary>
    public sealed class SymbolIndex
    {
        public const string NameIndex = "idx_sym_name_nocase";
        public const string ParentIndex = "idx_sym_parent_nocase";

        /// <summary>How long a query waits for the connection another query holds.</summary>
        public const int LockWaitMs = 50;

        // Scope filter shared by the bare-name queries. 'local' (procedure-private) and 'parameter'
        // rows are never reachable from another procedure, so they are never completion candidates.
        // Before 1c685f2e only 'local' was filtered, and every same-prefix PARAMETER in the solution
        // (77k rows in v61POSitive) was offered as a "global".
        private const string ReachableScope = "(s.scope IS NULL OR LOWER(s.scope) NOT IN ('local', 'parameter')) ";

        /// <summary>Bare-prefix range query over the NOCASE name index. @lo is the prefix, @hi the prefix
        /// followed by U+FFFF, so the range holds exactly the names starting with the prefix.</summary>
        internal const string PrefixSql = CodeGraphProvider.SymbolSelect +
            "WHERE s.name >= @lo COLLATE NOCASE AND s.name < @hi COLLATE NOCASE AND " + ReachableScope +
            "AND instr(s.name, '.') = 0 ORDER BY s.name COLLATE NOCASE LIMIT @limit OFFSET @offset";

        internal const string PrefixSqlNoIndex = CodeGraphProvider.SymbolSelect +
            "WHERE s.name LIKE @like ESCAPE '\\' AND " + ReachableScope + "AND instr(s.name, '.') = 0 LIMIT @limit";

        /// <summary>The fallback for a FILTERED <see cref="ByPrefix"/>: paging needs a stable order, which the
        /// plain fallback (a LIMIT scan that stops early) does not have.</summary>
        internal const string PrefixSqlNoIndexPaged = CodeGraphProvider.SymbolSelect +
            "WHERE s.name LIKE @like ESCAPE '\\' AND " + ReachableScope + "AND instr(s.name, '.') = 0 ORDER BY s.name COLLATE NOCASE LIMIT @limit OFFSET @offset";

        /// <summary>Direct members of a class: rows whose parent_name is the class AND whose name is
        /// "Class.Member" (the parent_name column alone also holds a derived class's base).</summary>
        internal const string MembersSql = CodeGraphProvider.SymbolSelect +
            "WHERE s.parent_name = @parent COLLATE NOCASE AND s.name LIKE @parentDot ESCAPE '\\' ORDER BY s.name LIMIT @limit";

        internal const string MembersSqlNoIndex = CodeGraphProvider.SymbolSelect +
            "WHERE LOWER(s.parent_name) = LOWER(@parent) AND s.name LIKE @parentDot ESCAPE '\\' ORDER BY s.name LIMIT @limit";

        /// <summary>A class's base class: its own row's parent_name.</summary>
        internal const string BaseClassSql =
            "SELECT s.parent_name FROM symbols s WHERE s.name = @name COLLATE NOCASE AND LOWER(s.type) IN ('class', 'interface') " +
            "AND s.parent_name IS NOT NULL AND s.parent_name <> '' LIMIT 1";

        internal const string BaseClassSqlNoIndex =
            "SELECT s.parent_name FROM symbols s WHERE LOWER(s.name) = LOWER(@name) AND LOWER(s.type) IN ('class', 'interface') " +
            "AND s.parent_name IS NOT NULL AND s.parent_name <> '' LIMIT 1";

        internal const string ExactSql = CodeGraphProvider.SymbolSelect +
            "WHERE s.name = @name COLLATE NOCASE AND " + ReachableScope + "LIMIT 1";

        internal const string ExactSqlNoIndex = CodeGraphProvider.SymbolSelect +
            "WHERE LOWER(s.name) = LOWER(@name) AND " + ReachableScope + "LIMIT 1";

        // ================================================================== registry and test hooks

        private static readonly object _registryLock = new object();
        private static readonly Dictionary<string, SymbolIndex> _registry =
            new Dictionary<string, SymbolIndex>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where the one-per-open "[local-timing] noIndex" line goes. Null: LspTrace.</summary>
        public static Action<string> LogSink;

        /// <summary>The index for <paramref name="dbPath"/>, or null for a null/empty path. The first
        /// call for a path opens the connection and probes its indexes on a pool thread, so the first
        /// keystroke neither pays the open nor - with fastOnly - an old DB's full scan.</summary>
        public static SymbolIndex For(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return null;
            string key;
            try { key = Path.GetFullPath(dbPath); } catch { return null; }
            SymbolIndex idx;
            lock (_registryLock)
            {
                if (_registry.TryGetValue(key, out idx))
                {
                    if (idx._noIndex) idx.ScheduleIndexBuild();   // a retry once the backoff has passed
                    return idx;
                }
                idx = new SymbolIndex(key);
                _registry[key] = idx;
            }
            var warm = idx;
            ThreadPool.QueueUserWorkItem(_ => warm.Warm());
            return idx;
        }

        /// <summary>Open the connection and probe its indexes now (idempotent; never throws).</summary>
        public void Warm()
        {
            WithConnection(conn => { }, fastOnly: false, count: false);
        }

        /// <summary>True once the connection is open and <see cref="NoIndex"/> is known.</summary>
        public bool IsOpen { get { return _conn != null; } }

        /// <summary>Close the held connection to <paramref name="dbPath"/> (if any) so the file can be
        /// deleted or rewritten. Waits for a running query to finish. The next query reopens.</summary>
        public static void Release(string dbPath)
        {
            var idx = Existing(dbPath);
            if (idx != null) idx.Close();
        }

        /// <summary>The index already registered for <paramref name="dbPath"/>, or null - never creates one.</summary>
        private static SymbolIndex Existing(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return null;
            string key;
            try { key = Path.GetFullPath(dbPath); } catch { return null; }
            SymbolIndex idx;
            lock (_registryLock) { _registry.TryGetValue(key, out idx); }
            return idx;
        }

        /// <summary>Close every held connection (solution switch/close).</summary>
        public static void ReleaseAll()
        {
            List<SymbolIndex> all;
            lock (_registryLock) { all = new List<SymbolIndex>(_registry.Values); }
            foreach (var idx in all) idx.Close();
        }

        /// <summary>Test hook: how many times a connection to this DB has been opened (0 when never used;
        /// asking does not create an index or open anything).</summary>
        public static int OpenCountFor(string dbPath) { var i = Existing(dbPath); return i == null ? 0 : i._openCount; }

        /// <summary>Test hook: how many SQL queries this DB has actually run (0 when never used).</summary>
        public static int QueryCountFor(string dbPath) { var i = Existing(dbPath); return i == null ? 0 : i._queryCount; }

        /// <summary>Test hook: whether an index is registered for this path.</summary>
        public static bool IsRegistered(string dbPath) { return Existing(dbPath) != null; }

        // ================================================================== instance

        private readonly string _path;
        private readonly object _lock = new object();
        private SQLiteConnection _conn;
        private long _stampTicks, _stampLength;
        private bool _noIndex;
        private int _openCount, _queryCount;

        private SymbolIndex(string path) { _path = path; }

        public string DatabasePath { get { return _path; } }

        /// <summary>True when the open DB lacks the NOCASE indexes (written by an older build): the
        /// queries run the slower fallback forms.</summary>
        public bool NoIndex { get { return _noIndex; } }

        /// <summary>Up to <paramref name="limit"/> symbols whose name starts with <paramref name="prefix"/>
        /// (case-insensitive), excluding procedure-private scopes ('local', 'parameter') and dotted
        /// Class.Member rows, ordered by name. Empty (never null, never throws) when the DB is missing,
        /// busy, or the prefix is empty - and, with <paramref name="fastOnly"/>, when the DB has no NOCASE
        /// index (the local lanes pass it: an old DB's fallback scan is ~150 ms on v61POSitive).</summary>
        public List<CodeGraphSymbol> ByPrefix(string prefix, int limit, bool fastOnly = false, ISet<string> equateFiles = null)
        {
            var results = new List<CodeGraphSymbol>();
            if (string.IsNullOrEmpty(prefix) || limit <= 0) return results;
            // Without a filter one page of <limit> rows is the answer. With one, rows are dropped AFTER the
            // query, so page on until <limit> survive - otherwise a prefix that is mostly equates from files
            // the current file never includes would fill the page and leave nothing to show.
            // OFFSET re-walks every skipped row, so the page doubles each time: the total work stays linear
            // in the rows read instead of quadratic.
            int pageSize = equateFiles == null ? limit : Math.Max(limit * 4, 200);
            int offset = 0;
            for (int page = 0; page < MaxPrefixPages && results.Count < limit; page++)
            {
                var rows = new List<CodeGraphSymbol>();
                int off = offset, size = pageSize;
                Query(noIndex => noIndex ? (equateFiles == null ? PrefixSqlNoIndex : PrefixSqlNoIndexPaged) : PrefixSql, cmd =>
                {
                    cmd.Parameters.AddWithValue("@lo", prefix);
                    cmd.Parameters.AddWithValue("@hi", prefix + "\uFFFF");
                    cmd.Parameters.AddWithValue("@like", EscapeLike(prefix) + "%");
                    cmd.Parameters.AddWithValue("@limit", size);
                    cmd.Parameters.AddWithValue("@offset", off);
                }, rows, fastOnly);
                foreach (var s in rows)
                {
                    if (results.Count >= limit) break;
                    if (equateFiles == null || !IsEquateOutside(s, equateFiles)) results.Add(s);
                }
                if (equateFiles == null || rows.Count < size) break;
                offset += rows.Count;
                pageSize = Math.Min(pageSize * 2, 8000);
            }
            return results;
        }

        /// <summary>The most pages one filtered <see cref="ByPrefix"/> reads (bounds a one-letter prefix).</summary>
        private const int MaxPrefixPages = 40;

        /// <summary>True for a file-level EQUATE declared in a .inc whose file name is not in
        /// <paramref name="includedFiles"/> - an equate the current file cannot see. Everything else
        /// (procedures, variables, equates in .clw files, class members) is never excluded.</summary>
        internal static bool IsEquateOutside(CodeGraphSymbol s, ISet<string> includedFiles)
        {
            if (s == null || !string.Equals(s.Params, "EQUATE", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(s.Scope, "global", StringComparison.OrdinalIgnoreCase)) return false;
            string file = s.FilePath;
            if (string.IsNullOrEmpty(file) || !file.EndsWith(".inc", StringComparison.OrdinalIgnoreCase)) return false;
            string name = BaseName(file);
            return name != null && !includedFiles.Contains(name);
        }

        /// <summary>The file name of <paramref name="path"/>, or null when it has characters .NET Framework's
        /// Path.GetFileName rejects (one malformed include row must not abort the whole load).</summary>
        private static string BaseName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path); }
            catch (ArgumentException) { return null; }
        }

        // ================================================================== include closure
        // File-level equates live in .inc files and are only visible to a file that INCLUDEs that .inc,
        // directly or through another include - or through the PROGRAM, for a MEMBER file. The closure of
        // include names (not paths: an 'include' row holds just the name written in the INCLUDE) is what
        // ByPrefix filters equates against.

        private sealed class IncludeData
        {
            public readonly Dictionary<string, List<string>> Edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public readonly List<KeyValuePair<long, string>> Programs = new List<KeyValuePair<long, string>>();
            public int Generation;   // _openCount of the connection the rows were read from
        }

        private IncludeData _includeData;   // dropped whenever the connection closes (DB replaced/rewritten)

        private IncludeData GetIncludeData(bool fastOnly)
        {
            IncludeData data = null;
            WithConnection(conn =>
            {
                if (_includeData != null) { data = _includeData; return; }
                var d = new IncludeData { Generation = _openCount };
                using (var cmd = new SQLiteCommand("SELECT file_path, name FROM symbols WHERE type = 'include'", conn))
                {
                    cmd.CommandTimeout = 0;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
                            string from = BaseName(r.GetString(0)), to = BaseName(r.GetString(1));
                            if (from == null || to == null) continue;
                            List<string> list;
                            if (!d.Edges.TryGetValue(from, out list)) d.Edges[from] = list = new List<string>();
                            list.Add(to);
                        }
                }
                using (var cmd = new SQLiteCommand("SELECT project_id, file_path FROM symbols WHERE type = 'program'", conn))
                {
                    cmd.CommandTimeout = 0;
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            if (!r.IsDBNull(1)) d.Programs.Add(new KeyValuePair<long, string>(r.IsDBNull(0) ? -1 : r.GetInt64(0), r.GetString(1)));
                }
                _includeData = data = d;
            }, fastOnly);
            return data;
        }

        /// <summary>The project that owns <paramref name="file"/>, or -1 when no symbol is indexed from it
        /// (<paramref name="ran"/> tells a miss from a query that never ran). An exact-path lookup on
        /// idx_sym_file only: a case-insensitive fallback cannot use that index and would scan the whole
        /// table on every miss. A miss just means "every PROGRAM's chain", which is over-inclusive, never
        /// hiding.</summary>
        private long ProjectOf(string file, bool fastOnly, out bool ran)
        {
            long found = -1;
            bool done = false;
            WithConnection(conn =>
            {
                using (var cmd = new SQLiteCommand("SELECT project_id FROM symbols WHERE file_path = @f LIMIT 1", conn))
                {
                    cmd.CommandTimeout = 0;
                    cmd.Parameters.AddWithValue("@f", file);
                    object v = cmd.ExecuteScalar();
                    if (v != null && !(v is DBNull)) found = Convert.ToInt64(v);
                }
                done = true;
            }, fastOnly);
            ran = done;
            return found;
        }

        private static readonly object _closureLock = new object();
        private static readonly Dictionary<string, HashSet<string>> _closureCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The file names (case-insensitive) whose file-level equates <paramref name="contextFile"/> can
        /// see: itself, every file it INCLUDEs transitively, and - so a MEMBER file sees the globals it
        /// inherits - the same for its project's PROGRAM file. Edges come from every DB in
        /// <paramref name="dbPaths"/> (project first, then library). Null - meaning "do not filter" - when
        /// <paramref name="contextFile"/> is empty, there is no project DB (a missing first path must not
        /// promote the library DB to project DB: it has no PROGRAM and no include edges from user files, so
        /// every visible equate would be hidden), the project DB cannot be read, or - with
        /// <paramref name="fastOnly"/> - it lacks the NOCASE indexes (the local lane skips such a DB).
        /// Cached per file until a DB is rewritten; a closure built from an incomplete read (a busy DB) is
        /// returned but not cached. Never throws.
        /// </summary>
        public static HashSet<string> IncludeClosure(string contextFile, string[] dbPaths, bool fastOnly = false)
        {
            try
            {
                if (string.IsNullOrEmpty(contextFile) || dbPaths == null || dbPaths.Length == 0) return null;
                var idxs = new List<SymbolIndex>();
                foreach (var p in dbPaths) { var i = For(p); if (i != null) idxs.Add(i); }
                if (idxs.Count == 0 || string.IsNullOrEmpty(dbPaths[0]) || !ReferenceEquals(idxs[0], For(dbPaths[0]))) return null;

                var datas = new List<IncludeData>();
                foreach (var i in idxs) datas.Add(i.GetIncludeData(fastOnly));
                if (datas[0] == null) return null;   // project DB busy/missing/old: show everything rather than nothing

                bool ran;
                long project = idxs[0].ProjectOf(contextFile, fastOnly, out ran);
                bool complete = ran && !datas.Contains(null);

                var key = new System.Text.StringBuilder(contextFile);
                for (int k = 0; k < idxs.Count; k++)
                    key.Append('|').Append(idxs[k]._path).Append('#').Append(datas[k] == null ? -1 : datas[k].Generation);
                string cacheKey = key.ToString();
                HashSet<string> cached;
                lock (_closureLock) { if (_closureCache.TryGetValue(cacheKey, out cached)) return cached; }

                var roots = new List<string>();
                string self = BaseName(contextFile);
                if (self != null) roots.Add(self);
                foreach (var prog in datas[0].Programs)
                    if (project < 0 || prog.Key == project) { string pn = BaseName(prog.Value); if (pn != null) roots.Add(pn); }

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var work = new Stack<string>(roots);
                while (work.Count > 0)
                {
                    string f = work.Pop();
                    if (string.IsNullOrEmpty(f) || !set.Add(f)) continue;
                    foreach (var d in datas)
                    {
                        List<string> inc;
                        if (d != null && d.Edges.TryGetValue(f, out inc))
                            foreach (var n in inc) work.Push(n);
                    }
                }
                if (complete)
                    lock (_closureLock)
                    {
                        if (_closureCache.Count > 200) _closureCache.Clear();
                        _closureCache[cacheKey] = set;
                    }
                return set;
            }
            catch (Exception ex)
            {
                LspTrace.Write("[SymbolIndex] include closure failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>The DIRECT members ("Class.Member" rows) of <paramref name="className"/> in this DB.</summary>
        public List<CodeGraphSymbol> DirectMembers(string className, int limit, bool fastOnly = false)
        {
            var results = new List<CodeGraphSymbol>();
            if (string.IsNullOrEmpty(className) || limit <= 0) return results;
            Query(noIndex => noIndex ? MembersSqlNoIndex : MembersSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@parent", className);
                cmd.Parameters.AddWithValue("@parentDot", EscapeLike(className) + ".%");
                cmd.Parameters.AddWithValue("@limit", limit);
            }, results, fastOnly);
            return results;
        }

        /// <summary>The first reachable (non-local, non-parameter) symbol named exactly <paramref
        /// name="name"/> (case-insensitive), or null.</summary>
        public CodeGraphSymbol FindByName(string name, bool fastOnly = false)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var results = new List<CodeGraphSymbol>();
            Query(noIndex => noIndex ? ExactSqlNoIndex : ExactSql, cmd => cmd.Parameters.AddWithValue("@name", name), results, fastOnly);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>The base class named on <paramref name="className"/>'s own class row, or null.</summary>
        public string BaseClassOf(string className, bool fastOnly = false)
        {
            if (string.IsNullOrEmpty(className)) return null;
            string found = null;
            WithConnection(conn =>
            {
                using (var cmd = new SQLiteCommand(_noIndex ? BaseClassSqlNoIndex : BaseClassSql, conn))
                {
                    cmd.CommandTimeout = 0;
                    cmd.Parameters.AddWithValue("@name", className);
                    object v = cmd.ExecuteScalar();
                    found = (v == null || v is DBNull) ? null : v.ToString();
                }
            }, fastOnly);
            return found;
        }

        /// <summary>
        /// Members of <paramref name="className"/> from the project DB then the library DB. With
        /// <paramref name="includeInherited"/>, walks the class's parent_name chain (base, base's base,
        /// ...) across BOTH DBs - a project class usually derives from a library one - with a cycle
        /// guard. A member name is listed once: the most-derived declaration wins. Either path may be
        /// null. Never throws.
        /// </summary>
        public static List<CodeGraphSymbol> MembersOf(string className, bool includeInherited, string projectDb, string libraryDb,
                                                      bool fastOnly = false)
        {
            var result = new List<CodeGraphSymbol>();
            if (string.IsNullOrEmpty(className)) return result;
            var dbs = new List<SymbolIndex>();
            foreach (var p in new[] { projectDb, libraryDb })
            {
                var idx = For(p);
                if (idx != null) dbs.Add(idx);
            }
            var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cls = className;
            for (int depth = 0; cls != null && depth < 32 && seenClasses.Add(cls); depth++)
            {
                foreach (var idx in dbs)
                    foreach (var s in idx.DirectMembers(cls, 500, fastOnly))
                    {
                        string member = MemberName(s.Name);
                        if (member != null && seenMembers.Add(member)) result.Add(s);
                    }
                if (!includeInherited) break;
                string next = null;
                foreach (var idx in dbs)
                {
                    next = idx.BaseClassOf(cls, fastOnly);
                    if (!string.IsNullOrEmpty(next)) break;
                }
                cls = next;
            }
            return result;
        }

        /// <summary>"Class.Member" -> "Member"; null for a malformed "Class." row.</summary>
        public static string MemberName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            int dot = fullName.LastIndexOf('.');
            if (dot == fullName.Length - 1) return null;
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        // ================================================================== completion item shapes
        // One home for how a CodeGraph row reads in the completion list, shared by the late merge
        // (SharedLspBridge) and the local layer.

        /// <summary>A bare-prefix completion item for a CodeGraph symbol.</summary>
        public static LspClient.CompletionItemInfo ToCompletionItem(CodeGraphSymbol s)
        {
            return new LspClient.CompletionItemInfo
            {
                Label = s.Name, Kind = CompletionKind(s.Type), Detail = CompletionDetail(s), InsertText = s.Name
            };
        }

        /// <summary>A member-access completion item ("oInstance." already typed: insert the bare member).</summary>
        public static LspClient.CompletionItemInfo ToMemberItem(CodeGraphSymbol s)
        {
            string member = MemberName(s.Name) ?? s.Name;
            return new LspClient.CompletionItemInfo
            {
                Label = member,
                Kind = s.Type == "procedure" || s.Type == "function" ? 2 /*Method*/ : CompletionKind(s.Type),
                Detail = CompletionDetail(s),
                InsertText = member
            };
        }

        /// <summary>CodeGraph symbol type -> LSP CompletionItemKind int (Monaco icon).</summary>
        public static int CompletionKind(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "class": return 7;       // Class
                case "interface": return 8;   // Interface
                case "procedure": return 3;   // Function
                case "function": return 3;    // Function
                case "routine": return 2;     // Method
                case "variable": return 6;    // Variable
                default: return 6;            // Variable
            }
        }

        public static string CompletionDetail(CodeGraphSymbol s)
        {
            // Variables: Params holds the ACTUAL declared Clarion type (e.g. "STRING(30)") - s.Type is just
            // the generic kind ("variable"). Scope reads Local/Global, the same wording the live-buffer
            // variable merges use.
            if (string.Equals(s.Type, "variable", StringComparison.OrdinalIgnoreCase))
            {
                string vt = !string.IsNullOrEmpty(s.Params) ? s.Params : "variable";
                bool isLocal = string.Equals(s.Scope, "local", StringComparison.OrdinalIgnoreCase);
                vt += "  (" + (isLocal ? "local" : "global") + ")";
                if (!string.IsNullOrEmpty(s.ProjectName)) vt += "  (" + s.ProjectName + ")";
                return vt;
            }
            string t = string.IsNullOrEmpty(s.Type) ? "" : s.Type;
            if (!string.IsNullOrEmpty(s.ReturnType)) t += " : " + s.ReturnType;
            if (!string.IsNullOrEmpty(s.ProjectName)) t += "  (" + s.ProjectName + ")";
            return string.IsNullOrEmpty(t) ? null : t;
        }

        // ================================================================== plumbing

        private static string EscapeLike(string s)
        {
            return s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        }

        private void Query(Func<bool, string> sql, Action<SQLiteCommand> bind, List<CodeGraphSymbol> into, bool fastOnly)
        {
            WithConnection(conn =>
            {
                using (var cmd = new SQLiteCommand(sql(_noIndex), conn))
                {
                    cmd.CommandTimeout = 0;
                    bind(cmd);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) into.Add(CodeGraphProvider.MapSymbol(reader));
                }
            }, fastOnly);
        }

        /// <summary>Runs <paramref name="work"/> on the held connection, (re)opening it first. Gives up
        /// (does nothing) when another query holds the connection past <see cref="LockWaitMs"/>, the DB
        /// is missing, <paramref name="fastOnly"/> meets a DB without the NOCASE indexes, or SQLite
        /// reports an error such as BUSY. Never throws.</summary>
        private void WithConnection(Action<SQLiteConnection> work, bool fastOnly, bool count = true)
        {
            if (!Monitor.TryEnter(_lock, LockWaitMs)) return;
            try
            {
                if (!EnsureOpen()) return;
                if (fastOnly && _noIndex) return;   // no index: the fallback scan is not keystroke-fast
                if (count) Interlocked.Increment(ref _queryCount);
                work(_conn);
            }
            catch (Exception ex)
            {
                LspTrace.Write("[SymbolIndex] query failed on " + Path.GetFileName(_path) + ": " + ex.Message);
            }
            finally { Monitor.Exit(_lock); }
        }

        private bool EnsureOpen()
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists) { CloseLocked(); return false; }
            long ticks = fi.LastWriteTimeUtc.Ticks, length = fi.Length;
            if (_conn != null && (ticks != _stampTicks || length != _stampLength)) CloseLocked();   // replaced or rewritten
            if (_conn != null) return true;

            var conn = new SQLiteConnection("Data Source=" + _path + ";Version=3;Read Only=True;Pooling=False;");
            conn.Open();
            conn.BusyTimeout = 0;   // SQLITE_BUSY surfaces at once instead of waiting on a writer
            _conn = conn;
            _stampTicks = ticks;
            _stampLength = length;
            Interlocked.Increment(ref _openCount);

            bool hasName = false, hasParent = false;
            using (var cmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'symbols'", conn))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    string n = r.IsDBNull(0) ? null : r.GetString(0);
                    if (string.Equals(n, NameIndex, StringComparison.OrdinalIgnoreCase)) hasName = true;
                    else if (string.Equals(n, ParentIndex, StringComparison.OrdinalIgnoreCase)) hasParent = true;
                }
            _noIndex = !(hasName && hasParent);
            if (_noIndex)
            {
                Log("[local-timing] noIndex db=" + Path.GetFileName(_path));
                ScheduleIndexBuild();
            }
            return true;
        }

        // ================================================================== background index build (H2)
        // A DB an older build wrote has no NOCASE indexes, and they would only arrive with the indexer's next
        // write - which may be weeks away. Until then the local lanes (fastOnly) cannot use the DB at all. So
        // the first open that finds them missing builds them, ONCE, on a pool thread: under the cross-process
        // IndexRunGate (never beside a reindex), on its own read-write connection, then the read connection is
        // released so the next query re-probes. Failures (gate busy, read-only file, locked DB) are logged,
        // never thrown, and retried no sooner than BuildBackoffMs later, at the next For() or open.

        /// <summary>False: never build indexes in the background (tests of the old-schema fallback).</summary>
        internal static bool AutoIndex = true;
        /// <summary>The least time between two build attempts on one path.</summary>
        internal static int BuildBackoffMs = 60000;
        /// <summary>Test hook: successful background index builds, all paths.</summary>
        internal static int IndexBuildCount;

        private int _building;          // 1 while a build is queued or running
        private long _nextBuildTicks;   // no attempt before this (UTC ticks)

        private void ScheduleIndexBuild()
        {
            if (!AutoIndex) return;
            if (Interlocked.CompareExchange(ref _building, 1, 0) != 0) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < Interlocked.Read(ref _nextBuildTicks)) { Interlocked.Exchange(ref _building, 0); return; }
            Interlocked.Exchange(ref _nextBuildTicks, now + TimeSpan.FromMilliseconds(BuildBackoffMs).Ticks);
            if (!ThreadPool.QueueUserWorkItem(_ => BuildIndexes())) Interlocked.Exchange(ref _building, 0);
        }

        private void BuildIndexes()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string result = "failed";
            bool gate = false;
            try
            {
                string holder;
                if (!IndexRunGate.TryEnter(_path, out holder)) { result = "busy (" + holder + ")"; return; }
                gate = true;
                var fi = new FileInfo(_path);
                if (!fi.Exists) { result = "missing"; return; }
                if (fi.IsReadOnly) { result = "readonly"; return; }
                using (var rw = new SQLiteConnection("Data Source=" + _path + ";Version=3;Pooling=False;"))
                {
                    rw.Open();
                    rw.BusyTimeout = 5000;
                    using (var cmd = new SQLiteCommand(CodeGraphDatabase.NoCaseIndexSql, rw)) { cmd.CommandTimeout = 120; cmd.ExecuteNonQuery(); }
                }
                result = "ok";
                Interlocked.Increment(ref IndexBuildCount);
            }
            catch (Exception ex) { result = "failed (" + ex.Message.Replace('\r', ' ').Replace('\n', ' ') + ")"; }
            finally
            {
                if (gate) try { IndexRunGate.Exit(_path); } catch { }
                if (result == "ok") Close();   // the next query reopens, re-probes and clears NoIndex
                Log("[local-timing] indexCreate db=" + Path.GetFileName(_path) + " ms=" + sw.ElapsedMilliseconds + " result=" + result);
                Interlocked.Exchange(ref _building, 0);
            }
        }

        private void Close()
        {
            lock (_lock) { CloseLocked(); }
        }

        private void CloseLocked()
        {
            if (_conn == null) return;
            try { _conn.Close(); _conn.Dispose(); } catch { }
            _conn = null;
            _includeData = null;
        }

        private static void Log(string line)
        {
            try
            {
                var sink = LogSink;
                if (sink != null) sink(line); else LspTrace.Write(line);
            }
            catch { }
        }
    }
}
