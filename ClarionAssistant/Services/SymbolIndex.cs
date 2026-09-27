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
            "AND instr(s.name, '.') = 0 ORDER BY s.name COLLATE NOCASE LIMIT @limit";

        internal const string PrefixSqlNoIndex = CodeGraphProvider.SymbolSelect +
            "WHERE s.name LIKE @like ESCAPE '\\' AND " + ReachableScope + "AND instr(s.name, '.') = 0 LIMIT @limit";

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

        /// <summary>The index for <paramref name="dbPath"/>, or null for a null/empty path. The
        /// connection opens on the first query, not here.</summary>
        public static SymbolIndex For(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return null;
            string key;
            try { key = Path.GetFullPath(dbPath); } catch { return null; }
            lock (_registryLock)
            {
                SymbolIndex idx;
                if (!_registry.TryGetValue(key, out idx)) { idx = new SymbolIndex(key); _registry[key] = idx; }
                return idx;
            }
        }

        /// <summary>Close the held connection to <paramref name="dbPath"/> (if any) so the file can be
        /// deleted or rewritten. Waits for a running query to finish. The next query reopens.</summary>
        public static void Release(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return;
            SymbolIndex idx;
            string key;
            try { key = Path.GetFullPath(dbPath); } catch { return; }
            lock (_registryLock) { _registry.TryGetValue(key, out idx); }
            if (idx != null) idx.Close();
        }

        /// <summary>Close every held connection (solution switch/close).</summary>
        public static void ReleaseAll()
        {
            List<SymbolIndex> all;
            lock (_registryLock) { all = new List<SymbolIndex>(_registry.Values); }
            foreach (var idx in all) idx.Close();
        }

        /// <summary>Test hook: how many times a connection to this DB has been opened.</summary>
        public static int OpenCountFor(string dbPath) { var i = For(dbPath); return i == null ? 0 : i._openCount; }

        /// <summary>Test hook: how many queries this DB has served.</summary>
        public static int QueryCountFor(string dbPath) { var i = For(dbPath); return i == null ? 0 : i._queryCount; }

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
        /// busy, or the prefix is empty.</summary>
        public List<CodeGraphSymbol> ByPrefix(string prefix, int limit)
        {
            var results = new List<CodeGraphSymbol>();
            if (string.IsNullOrEmpty(prefix) || limit <= 0) return results;
            Query(noIndex => noIndex ? PrefixSqlNoIndex : PrefixSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@lo", prefix);
                cmd.Parameters.AddWithValue("@hi", prefix + "\uFFFF");
                cmd.Parameters.AddWithValue("@like", EscapeLike(prefix) + "%");
                cmd.Parameters.AddWithValue("@limit", limit);
            }, results);
            return results;
        }

        /// <summary>The DIRECT members ("Class.Member" rows) of <paramref name="className"/> in this DB.</summary>
        public List<CodeGraphSymbol> DirectMembers(string className, int limit)
        {
            var results = new List<CodeGraphSymbol>();
            if (string.IsNullOrEmpty(className) || limit <= 0) return results;
            Query(noIndex => noIndex ? MembersSqlNoIndex : MembersSql, cmd =>
            {
                cmd.Parameters.AddWithValue("@parent", className);
                cmd.Parameters.AddWithValue("@parentDot", EscapeLike(className) + ".%");
                cmd.Parameters.AddWithValue("@limit", limit);
            }, results);
            return results;
        }

        /// <summary>The first reachable (non-local, non-parameter) symbol named exactly <paramref
        /// name="name"/> (case-insensitive), or null.</summary>
        public CodeGraphSymbol FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var results = new List<CodeGraphSymbol>();
            Query(noIndex => noIndex ? ExactSqlNoIndex : ExactSql, cmd => cmd.Parameters.AddWithValue("@name", name), results);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>The base class named on <paramref name="className"/>'s own class row, or null.</summary>
        public string BaseClassOf(string className)
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
            });
            return found;
        }

        /// <summary>
        /// Members of <paramref name="className"/> from the project DB then the library DB. With
        /// <paramref name="includeInherited"/>, walks the class's parent_name chain (base, base's base,
        /// ...) across BOTH DBs - a project class usually derives from a library one - with a cycle
        /// guard. A member name is listed once: the most-derived declaration wins. Either path may be
        /// null. Never throws.
        /// </summary>
        public static List<CodeGraphSymbol> MembersOf(string className, bool includeInherited, string projectDb, string libraryDb)
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
                    foreach (var s in idx.DirectMembers(cls, 500))
                    {
                        string member = MemberName(s.Name);
                        if (member != null && seenMembers.Add(member)) result.Add(s);
                    }
                if (!includeInherited) break;
                string next = null;
                foreach (var idx in dbs)
                {
                    next = idx.BaseClassOf(cls);
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

        private void Query(Func<bool, string> sql, Action<SQLiteCommand> bind, List<CodeGraphSymbol> into)
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
            });
        }

        /// <summary>Runs <paramref name="work"/> on the held connection, (re)opening it first. Gives up
        /// (does nothing) when another query holds the connection past <see cref="LockWaitMs"/>, the DB
        /// is missing, or SQLite reports an error such as BUSY. Never throws.</summary>
        private void WithConnection(Action<SQLiteConnection> work)
        {
            if (!Monitor.TryEnter(_lock, LockWaitMs)) return;
            try
            {
                if (!EnsureOpen()) return;
                Interlocked.Increment(ref _queryCount);
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
            if (_noIndex) Log("[local-timing] noIndex db=" + Path.GetFileName(_path));
            return true;
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
