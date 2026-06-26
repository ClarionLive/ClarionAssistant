using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using ClarionCodeGraph.Parsing.Models;

namespace ClarionCodeGraph.Graph
{
    /// <summary>
    /// SQLite storage for the code graph. Creates schema, handles CRUD, and provides
    /// low-level data access for symbols, relationships, and projects.
    /// </summary>
    public class CodeGraphDatabase : IDisposable
    {
        private SQLiteConnection _connection;
        private string _dbPath;

        public string DatabasePath { get { return _dbPath; } }

        public void Open(string dbPath)
        {
            _dbPath = dbPath;
            string connStr = "Data Source=" + dbPath + ";Version=3;Journal Mode=WAL;";
            _connection = new SQLiteConnection(connStr);
            _connection.Open();
            CreateSchema();
        }

        public void Close()
        {
            if (_connection != null)
            {
                _connection.Close();
                _connection = null;
            }
        }

        public void Dispose()
        {
            Close();
        }

        private void CreateSchema()
        {
            string sql = @"
                CREATE TABLE IF NOT EXISTS projects (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    guid TEXT,
                    cwproj_path TEXT,
                    output_type TEXT,
                    sln_path TEXT
                );

                CREATE TABLE IF NOT EXISTS project_dependencies (
                    project_id INTEGER REFERENCES projects(id),
                    depends_on_id INTEGER REFERENCES projects(id),
                    PRIMARY KEY (project_id, depends_on_id)
                );

                CREATE TABLE IF NOT EXISTS symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    line_number INTEGER,
                    project_id INTEGER REFERENCES projects(id),
                    params TEXT,
                    return_type TEXT,
                    parent_name TEXT,
                    member_of TEXT,
                    scope TEXT,
                    source_preview TEXT
                );

                CREATE TABLE IF NOT EXISTS relationships (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_id INTEGER REFERENCES symbols(id),
                    to_id INTEGER REFERENCES symbols(id),
                    type TEXT NOT NULL,
                    file_path TEXT,
                    line_number INTEGER
                );

                CREATE TABLE IF NOT EXISTS index_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_sym_name ON symbols(name);
                CREATE INDEX IF NOT EXISTS idx_sym_type ON symbols(type);
                CREATE INDEX IF NOT EXISTS idx_sym_file ON symbols(file_path);
                CREATE INDEX IF NOT EXISTS idx_sym_project ON symbols(project_id);
                CREATE INDEX IF NOT EXISTS idx_rel_from ON relationships(from_id);
                CREATE INDEX IF NOT EXISTS idx_rel_to ON relationships(to_id);
                CREATE INDEX IF NOT EXISTS idx_rel_type ON relationships(type);
            ";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearAll()
        {
            string sql = @"
                DELETE FROM relationships;
                DELETE FROM symbols;
                DELETE FROM project_dependencies;
                DELETE FROM projects;
                DELETE FROM index_metadata;
            ";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public SQLiteTransaction BeginTransaction()
        {
            return _connection.BeginTransaction();
        }

        // --- Projects ---

        public int InsertProject(SolutionProject project)
        {
            string sql = @"INSERT INTO projects (name, guid, cwproj_path, output_type, sln_path)
                           VALUES (@name, @guid, @cwproj, @output, @sln);
                           SELECT last_insert_rowid();";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@name", project.Name);
                cmd.Parameters.AddWithValue("@guid", project.Guid);
                cmd.Parameters.AddWithValue("@cwproj", project.CwprojPath);
                cmd.Parameters.AddWithValue("@output", project.OutputType ?? "");
                cmd.Parameters.AddWithValue("@sln", project.SlnPath);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public void InsertProjectDependency(int projectId, int dependsOnId)
        {
            string sql = "INSERT OR IGNORE INTO project_dependencies (project_id, depends_on_id) VALUES (@pid, @did)";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@pid", projectId);
                cmd.Parameters.AddWithValue("@did", dependsOnId);
                cmd.ExecuteNonQuery();
            }
        }

        // --- Symbols ---

        public long InsertSymbol(ClarionSymbol symbol)
        {
            string sql = @"INSERT INTO symbols (name, type, file_path, line_number, project_id, params, return_type, parent_name, member_of, scope, source_preview)
                           VALUES (@name, @type, @file, @line, @proj, @params, @ret, @parent, @member, @scope, @preview);
                           SELECT last_insert_rowid();";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@name", symbol.Name);
                cmd.Parameters.AddWithValue("@type", symbol.Type);
                cmd.Parameters.AddWithValue("@file", symbol.FilePath);
                cmd.Parameters.AddWithValue("@line", symbol.LineNumber);
                cmd.Parameters.AddWithValue("@proj", symbol.ProjectId);
                cmd.Parameters.AddWithValue("@params", (object)symbol.Params ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ret", (object)symbol.ReturnType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@parent", (object)symbol.ParentName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@member", (object)symbol.MemberOf ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@scope", (object)symbol.Scope ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@preview", (object)symbol.SourcePreview ?? DBNull.Value);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        public long FindSymbolId(string name, int projectId)
        {
            string sql = "SELECT id FROM symbols WHERE name = @name AND project_id = @proj LIMIT 1";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@proj", projectId);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt64(result) : -1;
            }
        }

        public long FindSymbolIdByName(string name)
        {
            // Prefer the symbol that has relationships (the MEMBER file implementation),
            // not MAP declarations which share the same name but have no relationships.
            string sql = @"SELECT s.id FROM symbols s
                           LEFT JOIN relationships r ON s.id = r.from_id OR s.id = r.to_id
                           WHERE s.name = @name COLLATE NOCASE
                           ORDER BY CASE WHEN r.id IS NOT NULL THEN 0 ELSE 1 END,
                                    CASE WHEN s.scope = 'module' THEN 0 ELSE 1 END
                           LIMIT 1";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt64(result) : -1;
            }
        }

        // --- Relationships ---

        public void InsertRelationship(ClarionRelationship rel)
        {
            string sql = @"INSERT INTO relationships (from_id, to_id, type, file_path, line_number)
                           VALUES (@from, @to, @type, @file, @line)";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@from", rel.FromId);
                cmd.Parameters.AddWithValue("@to", rel.ToId);
                cmd.Parameters.AddWithValue("@type", rel.Type);
                cmd.Parameters.AddWithValue("@file", (object)rel.FilePath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@line", rel.LineNumber);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Delete all symbols and relationships for a specific project.
        /// </summary>
        public void ClearProject(int projectId)
        {
            // Delete relationships where either side belongs to this project's symbols
            string sqlRels = @"
                DELETE FROM relationships WHERE from_id IN (SELECT id FROM symbols WHERE project_id = @pid)
                   OR to_id IN (SELECT id FROM symbols WHERE project_id = @pid)";
            using (var cmd = new SQLiteCommand(sqlRels, _connection))
            {
                cmd.Parameters.AddWithValue("@pid", projectId);
                cmd.ExecuteNonQuery();
            }

            string sqlSyms = "DELETE FROM symbols WHERE project_id = @pid";
            using (var cmd = new SQLiteCommand(sqlSyms, _connection))
            {
                cmd.Parameters.AddWithValue("@pid", projectId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Delete all relationships (but keep symbols).
        /// Used during incremental re-index before rebuilding relationships.
        /// </summary>
        public void ClearRelationships()
        {
            using (var cmd = new SQLiteCommand("DELETE FROM relationships", _connection))
            {
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Check if a project already exists in the DB by name, return its ID or -1.
        /// </summary>
        public int FindProjectIdByName(string name)
        {
            string sql = "SELECT id FROM projects WHERE name = @name COLLATE NOCASE LIMIT 1";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : -1;
            }
        }

        // --- Metadata ---

        public void SetMetadata(string key, string value)
        {
            string sql = "INSERT OR REPLACE INTO index_metadata (key, value) VALUES (@key, @val)";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@val", value);
                cmd.ExecuteNonQuery();
            }
        }

        public string GetMetadata(string key)
        {
            string sql = "SELECT value FROM index_metadata WHERE key = @key";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@key", key);
                var result = cmd.ExecuteScalar();
                return result != null ? result.ToString() : null;
            }
        }

        // --- Raw Query ---

        public DataTable ExecuteQuery(string sql, Dictionary<string, object> parameters = null)
        {
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                    {
                        cmd.Parameters.AddWithValue(kvp.Key, kvp.Value ?? DBNull.Value);
                    }
                }

                var dt = new DataTable();
                using (var adapter = new SQLiteDataAdapter(cmd))
                {
                    adapter.Fill(dt);
                }
                return dt;
            }
        }
    }
}
