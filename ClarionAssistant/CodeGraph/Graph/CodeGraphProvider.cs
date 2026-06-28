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
            "       s.parent_name, s.member_of, s.scope " +
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
        /// All members (methods/properties) declared under a CLASS, looked up by parent_name
        /// (case-insensitive). Backs ABC/library member-access completion (oInstance.Method).
        /// Member names are stored as "Parent.Member" — callers slice the suffix after the dot.
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
                    "ORDER BY s.name LIMIT @limit";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@parent", parentName);
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
            var refs = new List<ReferenceLocation>();
            if (_connection == null) return refs;
            try
            {
                var sym = FindSymbolByName(symbolName);
                if (sym == null) return refs;

                refs.Add(new ReferenceLocation
                {
                    FilePath = sym.FilePath,
                    LineNumber = sym.LineNumber,
                    IsDefinition = true
                });

                foreach (var caller in GetCallers(sym.Id))
                {
                    if (!string.IsNullOrEmpty(caller.CallFile) && caller.CallLine > 0)
                    {
                        refs.Add(new ReferenceLocation
                        {
                            FilePath = caller.CallFile,
                            LineNumber = caller.CallLine,
                            IsDefinition = false
                        });
                    }
                }
            }
            catch { }
            return refs;
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
                Scope = GetString(reader, "scope")
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
    }

    public class DefinitionLocation
    {
        public string FilePath;
        public int LineNumber;
    }
}
