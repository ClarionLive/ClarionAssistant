using System;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Logs Clarion code generation attempts and build results for the
    /// recursive self-improvement loop. Traces are stored in SQLite and
    /// analyzed by /clarion-analyze to find recurring failure patterns.
    /// </summary>
    public class ClarionTraceService
    {
        private readonly string _dbPath;

        public ClarionTraceService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "ClarionAssistant");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "clarion-traces.db");
            EnsureSchema();
        }

        private void EnsureSchema()
        {
            try
            {
                string connStr = "Data Source=" + _dbPath + ";Version=3;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS clarion_traces (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                timestamp TEXT DEFAULT (datetime('now')),
                                trace_type TEXT NOT NULL,
                                target_file TEXT,
                                code_snippet TEXT,
                                build_result TEXT,
                                error_count INTEGER DEFAULT 0,
                                warning_count INTEGER DEFAULT 0,
                                errors TEXT,
                                tool_name TEXT,
                                agent TEXT
                            );
                            CREATE INDEX IF NOT EXISTS idx_traces_type ON clarion_traces(trace_type);
                            CREATE INDEX IF NOT EXISTS idx_traces_timestamp ON clarion_traces(timestamp);
                            CREATE INDEX IF NOT EXISTS idx_traces_build_result ON clarion_traces(build_result);
                        ";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Log a code generation event (write_file, replace_text, etc. targeting .clw/.inc).
        /// </summary>
        public void LogCodeGeneration(string toolName, string targetFile, string codeSnippet)
        {
            if (!IsClarionFile(targetFile)) return;

            try
            {
                string connStr = "Data Source=" + _dbPath + ";Version=3;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"INSERT INTO clarion_traces
                            (trace_type, target_file, code_snippet, tool_name)
                            VALUES (@type, @file, @code, @tool)";
                        cmd.Parameters.AddWithValue("@type", "code_generation");
                        cmd.Parameters.AddWithValue("@file", targetFile ?? "");
                        cmd.Parameters.AddWithValue("@code", Truncate(codeSnippet, 4000));
                        cmd.Parameters.AddWithValue("@tool", toolName ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Log a build result from build_solution, build_app, or generate_source.
        /// </summary>
        public void LogBuildResult(string toolName, string targetFile, string buildOutput,
            bool success, int errorCount, int warningCount)
        {
            try
            {
                // Extract just the error lines for compact storage
                string errors = ExtractErrors(buildOutput);

                string connStr = "Data Source=" + _dbPath + ";Version=3;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"INSERT INTO clarion_traces
                            (trace_type, target_file, build_result, error_count, warning_count, errors, tool_name)
                            VALUES (@type, @file, @result, @errCount, @warnCount, @errors, @tool)";
                        cmd.Parameters.AddWithValue("@type", "build_result");
                        cmd.Parameters.AddWithValue("@file", targetFile ?? "");
                        cmd.Parameters.AddWithValue("@result", success ? "success" : "failure");
                        cmd.Parameters.AddWithValue("@errCount", errorCount);
                        cmd.Parameters.AddWithValue("@warnCount", warningCount);
                        cmd.Parameters.AddWithValue("@errors", Truncate(errors, 4000));
                        cmd.Parameters.AddWithValue("@tool", toolName ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Query traces for analysis. Returns tab-delimited results.
        /// </summary>
        public string QueryTraces(string sql)
        {
            try
            {
                string connStr = "Data Source=" + _dbPath + ";Version=3;Read Only=True;Journal Mode=WAL;";
                using (var conn = new SQLiteConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(sql, conn))
                    {
                        using (var reader = cmd.ExecuteReader())
                        {
                            var sb = new StringBuilder();
                            int colCount = reader.FieldCount;

                            for (int i = 0; i < colCount; i++)
                            {
                                if (i > 0) sb.Append("\t");
                                sb.Append(reader.GetName(i));
                            }
                            sb.AppendLine();

                            int rowCount = 0;
                            while (reader.Read() && rowCount < 500)
                            {
                                for (int i = 0; i < colCount; i++)
                                {
                                    if (i > 0) sb.Append("\t");
                                    sb.Append(reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString());
                                }
                                sb.AppendLine();
                                rowCount++;
                            }

                            if (rowCount == 0) return "No traces found.";
                            sb.AppendLine("(" + rowCount + " rows)");
                            return sb.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return "Trace query error: " + ex.Message;
            }
        }

        /// <summary>
        /// Get summary statistics for the trace database.
        /// </summary>
        public string GetStats()
        {
            return QueryTraces(@"
                SELECT
                    trace_type,
                    COUNT(*) as count,
                    SUM(CASE WHEN build_result = 'failure' THEN 1 ELSE 0 END) as failures,
                    SUM(error_count) as total_errors
                FROM clarion_traces
                GROUP BY trace_type
                ORDER BY count DESC
            ");
        }

        private static bool IsClarionFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".clw" || ext == ".inc" || ext == ".tpl" || ext == ".trn" || ext == ".equ";
        }

        private static string ExtractErrors(string buildOutput)
        {
            if (string.IsNullOrEmpty(buildOutput)) return "";
            var sb = new StringBuilder();
            foreach (var line in buildOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf(": error(", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.AppendLine(line);
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen);
        }
    }
}
