using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace ClarionCodeGraph.Graph
{
    /// <summary>
    /// Read-only, C# port of the LSP server's <c>codegraph-bridge.ts</c> cross-project
    /// queries (definition / references / workspace-symbol / call-hierarchy) over a
    /// <c>.codegraph.db</c>. (GitHub #40, ticket 2ba0ee17 — companion split.)
    ///
    /// Why this exists: when we adopt Mark's PURE upstream server.js (no CodeGraph baked in),
    /// the bundled LSP no longer answers cross-project navigation. CA restores it by MERGING
    /// in C#: ask the upstream LSP first, then fall back to this provider. The C# addin already
    /// reads this exact DB (query_codegraph), so no second Node process is needed.
    ///
    /// SQL is a 1:1 port of codegraph-bridge.ts against the same schema
    /// (<see cref="CodeGraphDatabase"/>). Every method is fully defensive — on ANY error it
    /// returns empty/null and never throws, because it feeds a navigation FALLBACK that must
    /// never break the editor.
    /// </summary>
    public class CodeGraphProvider : IDisposable
    {
        private SQLiteConnection _connection;
        private string _dbPath;

        public bool IsOpen { get { return _connection != null; } }
        public string DatabasePath { get { return _dbPath; } }

        // Column list shared by the symbol-returning queries (matches MapSymbol).
        private const string SymbolSelect =
            "SELECT s.id, s.name, s.type, s.file_path, s.line_number, " +
            "       p.name AS project_name, s.params, s.return_type, " +
            "       s.parent_name, s.member_of, s.scope, s.project_id " +
            "FROM symbols s " +
            "LEFT JOIN projects p ON s.project_id = p.id ";

        /// <summary>
        /// Open a .codegraph.db read-only. Returns false (no throw) if the file is missing
        /// or can't be opened — callers treat false as "no CodeGraph fallback available".
        /// </summary>
        public bool Open(string dbPath)
        {
            try
            {
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                    return false;

                // Same read-only connection string used by query_codegraph (McpToolRegistry).
                string connStr = "Data Source=" + dbPath + ";Version=3;Read Only=True;Journal Mode=WAL;";
                _connection = new SQLiteConnection(connStr);
                _connection.Open();
                _dbPath = dbPath;
                return true;
            }
            catch
            {
                Close();
                return false;
            }
        }

        public void Close()
        {
            try
            {
                if (_connection != null)
                {
                    _connection.Close();
                    _connection.Dispose();
                }
            }
            catch { }
            finally
            {
                _connection = null;
                _dbPath = null;
            }
        }

        public void Dispose() { Close(); }

        // ===== Symbol queries =====

        /// <summary>
        /// Substring search by name (case-insensitive), exact-prefix ranked first.
        /// Backs workspace/symbol. Port of bridge.findSymbols.
        /// </summary>
        public List<CodeGraphSymbol> FindSymbols(string query, int limit = 100)
        {
            var results = new List<CodeGraphSymbol>();
            if (_connection == null || string.IsNullOrEmpty(query)) return results;
            try
            {
                string sql = SymbolSelect +
                    "WHERE s.name LIKE @q " +
                    "ORDER BY CASE WHEN s.name LIKE @pref THEN 0 ELSE 1 END, s.name " +
                    "LIMIT @limit";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@q", "%" + query + "%");
                    cmd.Parameters.AddWithValue("@pref", query + "%");
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) results.Add(MapSymbol(reader));
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// Exact name lookup (case-insensitive), first match. Port of bridge.findSymbolByName.
        /// </summary>
        public CodeGraphSymbol FindSymbolByName(string name)
        {
            if (_connection == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                string sql = SymbolSelect + "WHERE LOWER(s.name) = LOWER(@name) LIMIT 1";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@name", name);
                    using (var reader = cmd.ExecuteReader())
                        if (reader.Read()) return MapSymbol(reader);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// EVERY exact-name match (case-insensitive), not just the first. <see cref="FindSymbolByName"/>'s
        /// unordered `LIMIT 1` is fine when any one declaration will do, but it is the wrong primitive for
        /// "does a declaration of this name with property X exist anywhere?" — with several same-named symbols
        /// it answers with an arbitrary one, so the caller's test can pass or fail on index order alone.
        /// Callers that need to reason about the SET of declarations (e.g. is any of them non-local?) use this.
        /// Returns empty (never null/throws); ordered so broader scopes come first, then by file for stability.
        /// </summary>
        public List<CodeGraphSymbol> FindAllSymbolsByName(string name, int limit = 50)
        {
            var results = new List<CodeGraphSymbol>();
            if (_connection == null || string.IsNullOrEmpty(name)) return results;
            try
            {
                string sql = SymbolSelect +
                    "WHERE LOWER(s.name) = LOWER(@name) " +
                    "ORDER BY CASE LOWER(COALESCE(s.scope,'')) " +
                    "           WHEN 'global' THEN 0 WHEN 'module' THEN 1 WHEN 'class' THEN 2 " +
                    "           WHEN 'local' THEN 4 WHEN 'parameter' THEN 5 ELSE 3 END, " +
                    "         s.file_path, s.line_number " +
                    "LIMIT @limit";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) results.Add(MapSymbol(reader));
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// All members (methods + data members) declared under a CLASS, looked up by parent_name
        /// (case-insensitive). Backs ABC/library member-access completion (oInstance.Method).
        /// Member names are stored DOTTED as "Parent.Member" — callers slice the suffix after the dot.
        /// The "name LIKE Parent.%" filter is essential: parent_name is overloaded — a method/data member
        /// carries parent_name=OwningClass (membership), but a SUBCLASS symbol also carries
        /// parent_name=BaseClass (inheritance). Only true members are stored dotted, so the dotted-name
        /// filter excludes subclasses that merely inherit from <paramref name="parentName"/>.
        /// Returns empty (never null/throws); ordered by name.
        /// </summary>
        public List<CodeGraphSymbol> FindMembersOfParent(string parentName, int limit = 500)
        {
            var results = new List<CodeGraphSymbol>();
            if (_connection == null || string.IsNullOrEmpty(parentName)) return results;
            try
            {
                string sql = SymbolSelect +
                    "WHERE LOWER(s.parent_name) = LOWER(@parent) " +
                    "AND s.name LIKE @parentDot ESCAPE '\\' " +
                    "ORDER BY s.name LIMIT @limit";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@parent", parentName);
                    // Escape LIKE wildcards in the class name so e.g. a literal '_' isn't treated as a wildcard.
                    string escaped = parentName.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                    cmd.Parameters.AddWithValue("@parentDot", escaped + ".%");
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) results.Add(MapSymbol(reader));
                }
            }
            catch { }
            return results;
        }

        // ===== Reference / call-graph queries =====

        /// <summary>
        /// Who calls this symbol? Backs textDocument/references + callHierarchy/incomingCalls.
        /// Port of bridge.getCallers.
        /// </summary>
        public List<CallerInfo> GetCallers(long symbolId)
        {
            return QueryCalls(
                "SELECT s.id AS symbol_id, s.name, s.type, s.file_path, s.line_number, " +
                "       r.line_number AS call_line, r.file_path AS call_file " +
                "FROM relationships r JOIN symbols s ON r.from_id = s.id " +
                "WHERE r.to_id = @id AND r.type = 'calls' " +
                "ORDER BY s.name",
                symbolId);
        }

        /// <summary>
        /// What does this symbol call? Backs callHierarchy/outgoingCalls. Port of bridge.getCallees.
        /// </summary>
        public List<CallerInfo> GetCallees(long symbolId)
        {
            return QueryCalls(
                "SELECT s.id AS symbol_id, s.name, s.type, s.file_path, s.line_number, " +
                "       r.line_number AS call_line, r.file_path AS call_file " +
                "FROM relationships r JOIN symbols s ON r.to_id = s.id " +
                "WHERE r.from_id = @id AND r.type = 'calls' " +
                "ORDER BY r.line_number",
                symbolId);
        }

        private List<CallerInfo> QueryCalls(string sql, long symbolId)
        {
            var results = new List<CallerInfo>();
            if (_connection == null) return results;
            try
            {
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@id", symbolId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new CallerInfo
                            {
                                SymbolId = GetLong(reader, "symbol_id"),
                                Name = GetString(reader, "name"),
                                Type = GetString(reader, "type"),
                                FilePath = GetString(reader, "file_path"),
                                LineNumber = GetInt(reader, "line_number"),
                                CallLine = GetInt(reader, "call_line"),
                                CallFile = GetString(reader, "call_file")
                            });
                        }
                    }
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// All references to a symbol name: its definition (isDefinition=true) plus every call
        /// site. Backs textDocument/references. Port of bridge.getReferences.
        /// </summary>
        public List<ReferenceLocation> GetReferences(string symbolName)
        {
            return GetReferences(symbolName, null, 0);
        }

        /// <summary>
        /// References to <paramref name="symbolName"/> AS SEEN FROM a request position.
        ///
        /// The name alone does not identify a symbol: a real index holds thousands of same-named
        /// locals (FilesOpened / LocalRequest: 3,278 rows in one production db) and a template
        /// procedure copied into every app (5,426 names with per-app duplicates). Merging them
        /// reported unrelated definitions and cross-app callers (77aceec5, pipeline run 1). So the
        /// declarations are chosen from the requester's position (see <see cref="SelectDeclarations"/>),
        /// and when that cannot be proven the answer narrows to ONE row rather than widening.
        /// </summary>
        /// <param name="requestFile">The file the request came from, or null (then: a single row).</param>
        /// <param name="requestLine1Based">1-based line of the request, or 0 when unknown.</param>
        public List<ReferenceLocation> GetReferences(string symbolName, string requestFile, int requestLine1Based)
        {
            var refs = new List<ReferenceLocation>();
            if (_connection == null) return refs;
            try
            {
                var decls = SelectDeclarations(symbolName, requestFile, requestLine1Based);
                if (decls.Count == 0) return refs;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lineCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                var caseCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var d in decls)
                    AddReference(refs, seen, lineCache, caseCache, d.FilePath, d.LineNumber, symbolName, true);

                foreach (var d in decls)
                {
                    foreach (var caller in GetCallers(d.Id))
                    {
                        if (!string.IsNullOrEmpty(caller.CallFile) && caller.CallLine > 0)
                            AddReference(refs, seen, lineCache, caseCache, caller.CallFile, caller.CallLine, symbolName, false);
                    }
                }
            }
            catch { }
            return refs;
        }

        private static bool IsLocalScope(CodeGraphSymbol s)
        {
            string sc = (s.Scope ?? "").ToLowerInvariant();
            return sc == "local" || sc == "parameter";
        }

        private static string NormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            try { p = Path.GetFullPath(p); } catch { }
            return p.Replace('/', '\\').ToLowerInvariant();
        }

        /// <summary>
        /// The declaration rows a reference request means, chosen from where it was made:
        ///   1. a LOCAL/PARAMETER declared in the requester's own routine or procedure (same
        ///      file_path AND parent_name) - and only that one;
        ///   2. otherwise the non-local rows of the requester's PROJECT with the same type as the
        ///      one visible from the requesting file - the MAP prototype + implementation pair of THAT
        ///      app, never another app's copy;
        ///   3. if the project cannot be proven (no request file, or the file maps to zero or several
        ///      projects): ONE row, deterministically - the one in the requesting file, else the first
        ///      by scope/file/line. Narrow, never a broad union.
        /// Locals of OTHER procedures are never candidates.
        /// </summary>
        internal List<CodeGraphSymbol> SelectDeclarations(string name, string requestFile, int requestLine1Based)
        {
            var result = new List<CodeGraphSymbol>();
            var all = FindAllSymbolsByName(name, 100000);
            if (all.Count == 0) return result;

            string file = NormPath(requestFile);
            var inFile = file == null ? new List<CodeGraphSymbol>()
                : all.FindAll(s => NormPath(s.FilePath) == file);

            // 1. The requester's own local.
            if (file != null && requestLine1Based > 0)
            {
                foreach (string parent in EnclosingScopes(file, requestLine1Based))
                {
                    var mine = inFile.FindAll(s => IsLocalScope(s)
                        && string.Equals(s.ParentName, parent, StringComparison.OrdinalIgnoreCase));
                    if (mine.Count > 0) return mine;
                }
            }

            var nonLocal = all.FindAll(s => !IsLocalScope(s));
            if (nonLocal.Count == 0) return result;   // only other procedures' locals: none of ours

            CodeGraphSymbol anchor = inFile.Find(s => !IsLocalScope(s)) ?? nonLocal[0];

            // 2. The requester's project.
            long project = file != null ? ProjectOfFile(file) : 0;
            if (project > 0)
            {
                CodeGraphSymbol typeAnchor = inFile.Find(s => !IsLocalScope(s) && s.ProjectId == project)
                    ?? nonLocal.Find(s => s.ProjectId == project);
                if (typeAnchor != null)
                {
                    result = nonLocal.FindAll(s => s.ProjectId == project
                        && string.Equals(s.Type, typeAnchor.Type, StringComparison.OrdinalIgnoreCase));
                    if (result.Count > 0) return result;
                }
            }

            // 3. Not provable: a single row.
            result = new List<CodeGraphSymbol> { anchor };
            return result;
        }

        /// <summary>
        /// Names of the routine/procedure enclosing a line (innermost first): the nearest preceding
        /// procedure/function/method/routine row in the file, plus the routine's parent procedure.
        /// Prototype rows are skipped where the index records decl_kind.
        /// </summary>
        private List<string> EnclosingScopes(string normFile, int line1Based)
        {
            var names = new List<string>();
            const string baseSql =
                "SELECT name, type, parent_name FROM symbols " +
                "WHERE LOWER(file_path) = @f AND line_number <= @l " +
                "AND LOWER(type) IN ('procedure','function','method','routine') ";
            foreach (string extra in new[] { "AND COALESCE(decl_kind,'') <> 'prototype' ", "" })
            {
                try
                {
                    using (var cmd = new SQLiteCommand(baseSql + extra + "ORDER BY line_number DESC LIMIT 1", _connection))
                    {
                        cmd.Parameters.AddWithValue("@f", normFile);
                        cmd.Parameters.AddWithValue("@l", line1Based);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (r.Read())
                            {
                                names.Add(r.IsDBNull(0) ? "" : r.GetValue(0).ToString());
                                string type = r.IsDBNull(1) ? "" : r.GetValue(1).ToString();
                                if (string.Equals(type, "routine", StringComparison.OrdinalIgnoreCase) && !r.IsDBNull(2))
                                    names.Add(r.GetValue(2).ToString());
                            }
                        }
                    }
                    return names;   // the query ran (with or without decl_kind): done
                }
                catch { names.Clear(); }   // no decl_kind column in an older index: retry without it
            }
            return names;
        }

        /// <summary>
        /// The ONE project a file belongs to, or 0 when it is none or several (an .inc shared by apps).
        /// From the symbols declared in it, else the indexed_files audit table where present.
        /// </summary>
        private long ProjectOfFile(string normFile)
        {
            foreach (string sql in new[] {
                "SELECT DISTINCT project_id FROM symbols WHERE LOWER(file_path) = @f AND project_id IS NOT NULL",
                "SELECT DISTINCT project_id FROM indexed_files WHERE LOWER(resolved_path) = @f AND project_id IS NOT NULL" })
            {
                try
                {
                    var ids = new List<long>();
                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Parameters.AddWithValue("@f", normFile);
                        using (var r = cmd.ExecuteReader())
                            while (r.Read()) ids.Add(Convert.ToInt64(r.GetValue(0)));
                    }
                    if (ids.Count == 1) return ids[0];
                    if (ids.Count > 1) return 0;
                }
                catch { }
            }
            return 0;
        }

        private static void AddReference(List<ReferenceLocation> refs, HashSet<string> seen,
            Dictionary<string, string[]> lineCache, Dictionary<string, string> caseCache,
            string filePath, int line1Based, string name, bool isDefinition)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            if (!seen.Add(filePath + "|" + line1Based)) return;

            string onDisk;
            if (!caseCache.TryGetValue(filePath, out onDisk))
            {
                onDisk = OnDiskPath(filePath);
                caseCache[filePath] = onDisk;
            }

            int col = -1;
            string[] lines;
            if (!lineCache.TryGetValue(filePath, out lines))
            {
                try { lines = File.Exists(filePath) ? File.ReadAllLines(filePath) : null; }
                catch { lines = null; }
                lineCache[filePath] = lines;
            }
            if (lines != null && line1Based >= 1 && line1Based <= lines.Length)
                col = FindNameColumn(lines[line1Based - 1], name);

            refs.Add(new ReferenceLocation
            {
                FilePath = onDisk,
                LineNumber = line1Based,
                IsDefinition = isDefinition,
                Character = col < 0 ? 0 : col,
                Length = col < 0 ? 0 : name.Length
            });
        }

        /// <summary>
        /// 0-based column of <paramref name="name"/> as a whole word in <paramref name="text"/>
        /// (case-insensitive, Clarion identifier chars incl. ':' - labels carry colons), or -1.
        /// Lets a reference cover the symbol's real width instead of a zero-width range at column 0.
        /// </summary>
        internal static int FindNameColumn(string text, string name)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) return -1;
            int from = 0;
            while (from <= text.Length - name.Length)
            {
                int i = text.IndexOf(name, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                bool startOk = i == 0 || !IsIdentChar(text[i - 1]);
                int end = i + name.Length;
                bool endOk = end >= text.Length || !IsIdentChar(text[end]);
                if (startOk && endOk)
                {
                    // Skip a match inside a trailing '!' comment when the line has one before it.
                    int bang = text.IndexOf('!');
                    if (bang < 0 || bang > i) return i;
                }
                from = i + 1;
            }
            return -1;
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == ':';
        }

        /// <summary>
        /// The path as the file system spells it. The index stores file_path lowercased, and a
        /// reference returned as '...\source\x.clw' for '...\Source\X.clw' reads as a different file
        /// to any case-sensitive consumer. Walks each segment once; returns the input unchanged if the
        /// file is missing or anything fails.
        /// </summary>
        internal static string OnDiskPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return path;
                string cur = root.ToUpperInvariant();
                foreach (string seg in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var matches = new DirectoryInfo(cur).GetFileSystemInfos(seg);
                    cur = Path.Combine(cur, matches.Length == 1 ? matches[0].Name : seg);
                }
                return cur;
            }
            catch { return path; }
        }

        /// <summary>
        /// Definition location for a symbol name. Fallback for textDocument/definition when the
        /// upstream LSP returns nothing. Port of bridge.getDefinition.
        /// </summary>
        public DefinitionLocation GetDefinition(string symbolName)
        {
            if (_connection == null) return null;
            try
            {
                var sym = FindSymbolByName(symbolName);
                if (sym == null) return null;
                return new DefinitionLocation { FilePath = sym.FilePath, LineNumber = sym.LineNumber };
            }
            catch { }
            return null;
        }

        // ===== Helpers =====

        /// <summary>
        /// Walk up from a directory looking for the first *.codegraph.db. Mirrors
        /// bridge.findDatabase; provided for callers that don't already have a db path.
        /// </summary>
        public static string FindDatabase(string startDir)
        {
            try
            {
                var dir = startDir;
                while (!string.IsNullOrEmpty(dir))
                {
                    try
                    {
                        var dbs = Directory.GetFiles(dir, "*.codegraph.db");
                        if (dbs.Length > 0) return dbs[0];
                    }
                    catch { }

                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }
            }
            catch { }
            return null;
        }

        private static CodeGraphSymbol MapSymbol(SQLiteDataReader reader)
        {
            return new CodeGraphSymbol
            {
                Id = GetLong(reader, "id"),
                Name = GetString(reader, "name"),
                Type = GetString(reader, "type"),
                FilePath = GetString(reader, "file_path"),
                LineNumber = GetInt(reader, "line_number"),
                ProjectName = GetString(reader, "project_name"),
                Params = GetString(reader, "params"),
                ReturnType = GetString(reader, "return_type"),
                ParentName = GetString(reader, "parent_name"),
                MemberOf = GetString(reader, "member_of"),
                Scope = GetString(reader, "scope"),
                ProjectId = GetLong(reader, "project_id")
            };
        }

        private static string GetString(SQLiteDataReader reader, string col)
        {
            int i = reader.GetOrdinal(col);
            return reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
        }

        private static long GetLong(SQLiteDataReader reader, string col)
        {
            int i = reader.GetOrdinal(col);
            return reader.IsDBNull(i) ? 0L : Convert.ToInt64(reader.GetValue(i));
        }

        private static int GetInt(SQLiteDataReader reader, string col)
        {
            int i = reader.GetOrdinal(col);
            return reader.IsDBNull(i) ? 0 : Convert.ToInt32(reader.GetValue(i));
        }
    }

    // ===== DTOs (mirror codegraph-bridge.ts interfaces) =====

    public class CodeGraphSymbol
    {
        public long Id;
        public string Name;
        public string Type;
        public string FilePath;
        public int LineNumber;
        public string ProjectName;
        public string Params;
        public string ReturnType;
        public string ParentName;
        public string MemberOf;
        public string Scope;
        /// <summary>symbols.project_id (0 when null).</summary>
        public long ProjectId;
    }

    public class CallerInfo
    {
        public long SymbolId;
        public string Name;
        public string Type;
        public string FilePath;
        public int LineNumber;
        public int CallLine;
        public string CallFile;
    }

    public class ReferenceLocation
    {
        public string FilePath;
        public int LineNumber;
        public bool IsDefinition;
        /// <summary>0-based start column of the name on the line; 0 when it could not be found.</summary>
        public int Character;
        /// <summary>Width of the name; 0 when the column could not be found (zero-width range).</summary>
        public int Length;
    }

    public class DefinitionLocation
    {
        public string FilePath;
        public int LineNumber;
    }
}
