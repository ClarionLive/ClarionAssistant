using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Standalone knowledge database for the Clarion IDE addin.
    /// Stores knowledge entries with attention decay and session history.
    /// Database: %APPDATA%/ClarionAssistant/memory.db
    /// </summary>
    public class KnowledgeService : IDisposable
    {
        private readonly SQLiteConnection _conn;
        private bool _disposed;

        public KnowledgeService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "ClarionAssistant");
            Directory.CreateDirectory(dir);

            string dbPath = Path.Combine(dir, "memory.db");
            _conn = new SQLiteConnection("Data Source=" + dbPath + ";Version=3;Journal Mode=WAL;");
            _conn.Open();

            // FTS5 is a loadable extension in this SQLite build (same as DocGraphService)
            _conn.EnableExtensions(true);
            _conn.LoadExtension("SQLite.Interop.dll", "sqlite3_fts5_init");

            CreateTables();
        }

        private void CreateTables()
        {
            // Execute each DDL statement separately (proven pattern from DocGraphService)
            string[] ddl = new[]
            {
                @"CREATE TABLE IF NOT EXISTS knowledge_entries (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    category        TEXT NOT NULL,
                    title           TEXT NOT NULL,
                    content         TEXT NOT NULL,
                    tags            TEXT,
                    confidence      TEXT DEFAULT 'confirmed',
                    source          TEXT,
                    superseded_by   INTEGER,
                    last_referenced TEXT,
                    reference_count INTEGER DEFAULT 0,
                    created_at      TEXT DEFAULT (datetime('now')),
                    updated_at      TEXT DEFAULT (datetime('now'))
                )",

                @"CREATE TABLE IF NOT EXISTS session_history (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    summary     TEXT,
                    work_dir    TEXT,
                    started_at  TEXT DEFAULT (datetime('now')),
                    ended_at    TEXT
                )",

                // Standalone FTS5 table — stores its own data, no content-sync triggers needed.
                // Populated explicitly in AddEntry/DeleteEntry.
                @"CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(
                    entry_id,
                    title,
                    content,
                    tags,
                    tokenize='porter unicode61'
                )"
            };

            foreach (string sql in ddl)
            {
                using (var cmd = new SQLiteCommand(sql, _conn))
                    cmd.ExecuteNonQuery();
            }
        }

        // ── Knowledge CRUD ──────────────────────────────────

        public int AddEntry(string title, string content, string category,
            string tags = null, string confidence = "confirmed", string source = null)
        {
            const string sql = @"
                INSERT INTO knowledge_entries (title, content, category, tags, confidence, source, last_referenced, reference_count)
                VALUES (@title, @content, @category, @tags, @confidence, @source, datetime('now'), 0)";

            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@content", content);
                cmd.Parameters.AddWithValue("@category", category);
                cmd.Parameters.AddWithValue("@tags", (object)tags ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@confidence", confidence);
                cmd.Parameters.AddWithValue("@source", (object)source ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            int id;
            using (var cmd = new SQLiteCommand("SELECT last_insert_rowid()", _conn))
                id = Convert.ToInt32(cmd.ExecuteScalar());

            // Insert into standalone FTS table
            using (var cmd = new SQLiteCommand(
                "INSERT INTO knowledge_fts(entry_id, title, content, tags) VALUES (@id, @title, @content, @tags)", _conn))
            {
                cmd.Parameters.AddWithValue("@id", id.ToString());
                cmd.Parameters.AddWithValue("@title", title);
                cmd.Parameters.AddWithValue("@content", content);
                cmd.Parameters.AddWithValue("@tags", (object)tags ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            return id;
        }

        public List<KnowledgeEntry> Search(string query, int limit = 20)
        {
            string sql;
            if (string.IsNullOrWhiteSpace(query))
            {
                sql = @"SELECT id, category, title, content, tags, confidence, last_referenced, reference_count, created_at
                        FROM knowledge_entries
                        WHERE superseded_by IS NULL
                        ORDER BY updated_at DESC
                        LIMIT @limit";
            }
            else
            {
                sql = @"SELECT k.id, k.category, k.title, k.content, k.tags, k.confidence, k.last_referenced, k.reference_count, k.created_at
                        FROM knowledge_entries k
                        JOIN knowledge_fts f ON CAST(f.entry_id AS INTEGER) = k.id
                        WHERE knowledge_fts MATCH @query
                          AND k.superseded_by IS NULL
                        ORDER BY rank
                        LIMIT @limit";
            }

            var results = new List<KnowledgeEntry>();
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                if (!string.IsNullOrWhiteSpace(query))
                    cmd.Parameters.AddWithValue("@query", query);
                cmd.Parameters.AddWithValue("@limit", limit);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadEntry(reader));
                }
            }

            // Bump references for returned entries
            if (results.Count > 0)
                BumpReferences(results);

            return results;
        }

        // ── Attention Decay ─────────────────────────────────

        public List<KnowledgeEntry> GetDecayRanked(int limit = 15)
        {
            const string sql = @"
                SELECT id, category, title, content, tags, confidence, last_referenced, reference_count, created_at,
                       (COALESCE(reference_count, 0) + 1.0)
                       / (julianday('now') - julianday(COALESCE(last_referenced, updated_at)) + 1.0)
                       AS decay_score
                FROM knowledge_entries
                WHERE superseded_by IS NULL
                ORDER BY decay_score DESC
                LIMIT @limit";

            var results = new List<KnowledgeEntry>();
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results.Add(ReadEntry(reader));
                }
            }

            return results;
        }

        public void BumpReferences(List<KnowledgeEntry> entries)
        {
            using (var tx = _conn.BeginTransaction())
            {
                try
                {
                    foreach (var e in entries)
                    {
                        using (var cmd = new SQLiteCommand(
                            "UPDATE knowledge_entries SET last_referenced = datetime('now'), reference_count = reference_count + 1 WHERE id = @id",
                            _conn))
                        {
                            cmd.Parameters.AddWithValue("@id", e.Id);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
                catch { tx.Rollback(); }
            }
        }

        /// <summary>
        /// Returns pre-formatted markdown for context injection.
        /// Top 10 get full content preview, next 5 get title-only.
        /// </summary>
        public string GetInjectionMarkdown(int limit = 15)
        {
            var entries = GetDecayRanked(limit);
            if (entries.Count == 0) return null;

            BumpReferences(entries);

            var sb = new StringBuilder();
            sb.AppendLine("# Project Knowledge (auto-injected)");
            sb.AppendLine();

            int fullCount = Math.Min(10, entries.Count);
            for (int i = 0; i < fullCount; i++)
            {
                var e = entries[i];
                string preview = e.Content != null && e.Content.Length > 200
                    ? e.Content.Substring(0, 200) + "..."
                    : e.Content ?? "";
                sb.AppendLine("- **[" + e.Category + "] " + e.Title + "**: " + preview);
            }

            if (entries.Count > fullCount)
            {
                sb.AppendLine();
                sb.AppendLine("_Also available (use query_knowledge for details):_");
                for (int i = fullCount; i < entries.Count; i++)
                    sb.AppendLine("- [" + entries[i].Category + "] " + entries[i].Title);
            }

            return sb.ToString();
        }

        // ── Session History ─────────────────────────────────

        public int StartSession(string workDir)
        {
            const string sql = "INSERT INTO session_history (work_dir) VALUES (@workDir)";
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@workDir", (object)workDir ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = new SQLiteCommand("SELECT last_insert_rowid()", _conn))
                return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void EndSession(int sessionId, string summary)
        {
            const string sql = "UPDATE session_history SET summary = @summary, ended_at = datetime('now') WHERE id = @id";
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                cmd.Parameters.AddWithValue("@summary", (object)summary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.ExecuteNonQuery();
            }
        }

        public string GetLastSessionSummary(string workDir = null)
        {
            string sql = "SELECT summary FROM session_history WHERE summary IS NOT NULL";
            if (workDir != null) sql += " AND work_dir = @workDir";
            sql += " ORDER BY id DESC LIMIT 1";

            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                if (workDir != null)
                    cmd.Parameters.AddWithValue("@workDir", workDir);

                var result = cmd.ExecuteScalar();
                return result as string;
            }
        }

        // ── JSONL Session Recap ──────────────────────────────

        /// <summary>
        /// Reads the most recent Claude Code JSONL session file for a working directory
        /// and extracts the last N user/assistant messages as a recap.
        /// Path: ~/.claude/projects/{encoded-path}/*.jsonl
        /// </summary>
        public static string GetSessionRecapFromJsonl(string workDir, int maxMessages = 10)
        {
            try
            {
                // Build the Claude project folder path: replace \ and : with -
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string encoded = workDir.Replace(":\\", "--").Replace("\\", "-").Replace("/", "-");
                // Handle drive letter: "H:\DevLaptop\Foo" -> "H--DevLaptop-Foo"
                if (encoded.Length > 1 && encoded[1] == ':')
                    encoded = encoded[0] + "-" + encoded.Substring(2);
                string projectDir = Path.Combine(userHome, ".claude", "projects", encoded);

                if (!Directory.Exists(projectDir)) return null;

                // Find the most recent .jsonl file
                var files = Directory.GetFiles(projectDir, "*.jsonl");
                if (files.Length == 0) return null;

                // Sort by modification time descending, skip tiny files (< 10KB = trivial sessions)
                var sorted = new List<string>(files);
                sorted.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));

                string newest = null;
                foreach (var f in sorted)
                {
                    if (new FileInfo(f).Length >= 10000) { newest = f; break; }
                }
                if (newest == null) return null;

                // Read all lines and extract user/assistant messages from the end
                var lines = File.ReadAllLines(newest, Encoding.UTF8);
                var messages = new List<string>();

                for (int i = lines.Length - 1; i >= 0 && messages.Count < maxMessages; i--)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    try
                    {
                        // Lightweight JSON parsing — look for "type":"user" or "type":"assistant"
                        // and extract text content
                        string msgType = ExtractJsonField(line, "type");
                        if (msgType != "user" && msgType != "assistant") continue;

                        string text = ExtractMessageText(line);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        // Truncate long messages
                        if (text.Length > 300) text = text.Substring(0, 300) + "...";
                        messages.Add("**" + msgType + "**: " + text);
                    }
                    catch { continue; }
                }

                if (messages.Count == 0) return null;

                messages.Reverse(); // chronological order
                return string.Join("\n", messages);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Extract a top-level string field from a JSON line without a full parser.</summary>
        private static string ExtractJsonField(string json, string field)
        {
            string needle = "\"" + field + "\":\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + needle.Length;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        /// <summary>Extract text content from a JSONL message line.</summary>
        private static string ExtractMessageText(string json)
        {
            // Try "content":"..." (simple string content)
            string simple = ExtractJsonField(json, "content");
            if (simple != null && simple.Length > 5) return simple;

            // Try to find "type":"text","text":"..." in content array
            string needle = "\"type\":\"text\",\"text\":\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0)
            {
                // Also try reversed order: "text":"...","type":"text"
                needle = "\"text\":\"";
                idx = json.IndexOf(needle, StringComparison.Ordinal);
            }
            if (idx < 0) return null;

            int start = idx + needle.Length;
            // Find the closing quote, handling escaped quotes
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[i + 1]);
                    i++;
                }
                else if (json[i] == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(json[i]);
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // ── Internal ────────────────────────────────────────

        private static KnowledgeEntry ReadEntry(SQLiteDataReader reader)
        {
            return new KnowledgeEntry
            {
                Id             = reader.GetInt32(0),
                Category       = reader.IsDBNull(1) ? null : reader.GetString(1),
                Title          = reader.IsDBNull(2) ? null : reader.GetString(2),
                Content        = reader.IsDBNull(3) ? null : reader.GetString(3),
                Tags           = reader.IsDBNull(4) ? null : reader.GetString(4),
                Confidence     = reader.IsDBNull(5) ? null : reader.GetString(5),
                LastReferenced = reader.IsDBNull(6) ? null : reader.GetString(6),
                ReferenceCount = reader.IsDBNull(7) ? 0    : reader.GetInt32(7),
                CreatedAt      = reader.IsDBNull(8) ? null : reader.GetString(8)
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _conn?.Close();
                _conn?.Dispose();
                _disposed = true;
            }
        }
    }

    public class KnowledgeEntry
    {
        public int Id { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public string Tags { get; set; }
        public string Confidence { get; set; }
        public string LastReferenced { get; set; }
        public int ReferenceCount { get; set; }
        public string CreatedAt { get; set; }
    }
}
