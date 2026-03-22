using System;
using System.Data.SQLite;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    public class LibraryIndexResult
    {
        public int SymbolCount { get; set; }
        public string DbPath { get; set; }
        public string Error { get; set; }
        public bool Success { get { return string.IsNullOrEmpty(Error); } }
    }

    public static class LibraryIndexer
    {
        private static readonly Regex EquateRegex = new Regex(
            @"^([\w:]+)\s+EQUATE\s*\(([^)]*)\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string GetDefaultDbPath()
        {
            string asmDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(asmDir, "ClarionLib.codegraph.db");
        }

        public static LibraryIndexResult Build(string clarionRoot)
        {
            string dbPath = GetDefaultDbPath();
            string libSrc = Path.Combine(clarionRoot, "LibSrc", "win");

            if (!Directory.Exists(libSrc))
                return new LibraryIndexResult { Error = "LibSrc not found: " + libSrc };

            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);

                int totalSymbols = 0;
                string connStr = "Data Source=" + dbPath + ";Version=3;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    CreateSchema(conn);
                    int projectId = InsertProject(conn, "ClarionLibrary", clarionRoot);

                    using (var tx = conn.BeginTransaction())
                    {
                        string[] files = {
                            Path.Combine(libSrc, "equates.clw"),
                            Path.Combine(libSrc, "property.clw"),
                            Path.Combine(libSrc, "builtins.clw"),
                            Path.Combine(libSrc, "winerr.inc")
                        };

                        foreach (string filePath in files)
                        {
                            if (File.Exists(filePath))
                                totalSymbols += IndexEquateFile(conn, filePath, projectId);
                        }

                        SetMetadata(conn, "indexed_at", DateTime.Now.ToString("o"));
                        SetMetadata(conn, "clarion_root", clarionRoot);
                        SetMetadata(conn, "symbol_count", totalSymbols.ToString());
                        tx.Commit();
                    }
                }

                return new LibraryIndexResult { SymbolCount = totalSymbols, DbPath = dbPath };
            }
            catch (Exception ex)
            {
                return new LibraryIndexResult { Error = ex.Message };
            }
        }

        public static string GetStatus()
        {
            string dbPath = GetDefaultDbPath();
            if (!File.Exists(dbPath))
                return "Not built";

            try
            {
                string connStr = "Data Source=" + dbPath + ";Version=3;Read Only=True;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT value FROM index_metadata WHERE key='symbol_count'", conn))
                    {
                        var result = cmd.ExecuteScalar();
                        return (result?.ToString() ?? "?") + " symbols indexed";
                    }
                }
            }
            catch
            {
                return "Error reading database";
            }
        }

        private static int IndexEquateFile(SQLiteConnection conn, string filePath, int projectId)
        {
            int count = 0;
            string[] lines = File.ReadAllLines(filePath);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("!"))
                    continue;

                int commentIdx = line.IndexOf('!');
                string codePart = commentIdx >= 0 ? line.Substring(0, commentIdx).Trim() : line;

                var match = EquateRegex.Match(codePart);
                if (match.Success)
                {
                    string name = match.Groups[1].Value;
                    string value = match.Groups[2].Value.Trim();

                    InsertSymbol(conn, name, "variable", filePath, i + 1, projectId,
                        "EQUATE", null, null, null, "global",
                        name + " EQUATE(" + value + ")");
                    count++;
                }
            }

            return count;
        }

        private static void CreateSchema(SQLiteConnection conn)
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
                CREATE TABLE IF NOT EXISTS symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    file_path TEXT,
                    line_number INTEGER,
                    project_id INTEGER,
                    params TEXT,
                    return_type TEXT,
                    parent_name TEXT,
                    member_of TEXT,
                    scope TEXT,
                    source_preview TEXT,
                    FOREIGN KEY (project_id) REFERENCES projects(id)
                );
                CREATE TABLE IF NOT EXISTS relationships (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_id INTEGER NOT NULL,
                    to_id INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    file_path TEXT,
                    line_number INTEGER,
                    FOREIGN KEY (from_id) REFERENCES symbols(id),
                    FOREIGN KEY (to_id) REFERENCES symbols(id)
                );
                CREATE TABLE IF NOT EXISTS project_dependencies (
                    project_id INTEGER NOT NULL,
                    depends_on_id INTEGER NOT NULL,
                    PRIMARY KEY (project_id, depends_on_id),
                    FOREIGN KEY (project_id) REFERENCES projects(id),
                    FOREIGN KEY (depends_on_id) REFERENCES projects(id)
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
                CREATE INDEX IF NOT EXISTS idx_rel_type ON relationships(type);";

            using (var cmd = new SQLiteCommand(sql, conn))
                cmd.ExecuteNonQuery();
        }

        private static int InsertProject(SQLiteConnection conn, string name, string rootPath)
        {
            string sql = @"INSERT INTO projects (name, cwproj_path, sln_path)
                           VALUES (@name, @path, @path);
                           SELECT last_insert_rowid();";
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@path", rootPath);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static void InsertSymbol(SQLiteConnection conn, string name, string type,
            string filePath, int lineNumber, int projectId, string paramStr,
            string returnType, string parentName, string memberOf, string scope,
            string sourcePreview)
        {
            string sql = @"INSERT INTO symbols (name, type, file_path, line_number, project_id,
                           params, return_type, parent_name, member_of, scope, source_preview)
                           VALUES (@name, @type, @file, @line, @proj,
                                   @params, @ret, @parent, @member, @scope, @preview)";
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@file", filePath);
                cmd.Parameters.AddWithValue("@line", lineNumber);
                cmd.Parameters.AddWithValue("@proj", projectId);
                cmd.Parameters.AddWithValue("@params", (object)paramStr ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ret", (object)returnType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@parent", (object)parentName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@member", (object)memberOf ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@scope", (object)scope ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@preview", (object)sourcePreview ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private static void SetMetadata(SQLiteConnection conn, string key, string value)
        {
            string sql = "INSERT OR REPLACE INTO index_metadata (key, value) VALUES (@key, @value)";
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@value", value);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
