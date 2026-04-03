using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Timers;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Coordinates multiple ClarionAssistant instances running in separate Clarion IDE processes.
    /// Uses a shared SQLite database for presence, heartbeat, conflict detection, and messaging.
    /// </summary>
    public class InstanceCoordinationService : IDisposable
    {
        private readonly string _dbPath;
        private readonly int _pid;
        private Timer _heartbeatTimer;
        private bool _disposed;

        // Stale threshold — instances that haven't heartbeated in this many seconds are considered dead
        private const int StaleSeconds = 30;
        private const int HeartbeatIntervalMs = 10000; // 10 seconds

        // Current state — updated by the host before each heartbeat
        public string SolutionPath { get; set; }
        public string AppFile { get; set; }
        public string ActiveFile { get; set; }
        public string ActiveProcedure { get; set; }
        public string WorkingOn { get; set; }

        public static string GetDbPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", "instances.db");
        }

        public InstanceCoordinationService(string dbPath = null)
        {
            _dbPath = dbPath ?? GetDbPath();
            _pid = Process.GetCurrentProcess().Id;

            string dir = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            EnsureDatabase();
        }

        #region Schema

        private void EnsureDatabase()
        {
            using (var conn = OpenConnection())
            {
                string[] ddl = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS instances (
                        pid INTEGER PRIMARY KEY,
                        solution_path TEXT,
                        app_file TEXT,
                        active_file TEXT,
                        active_procedure TEXT,
                        working_on TEXT,
                        started_at TEXT DEFAULT (datetime('now')),
                        heartbeat_at TEXT DEFAULT (datetime('now'))
                    )",

                    @"CREATE TABLE IF NOT EXISTS messages (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        from_pid INTEGER NOT NULL,
                        to_pid INTEGER,
                        type TEXT NOT NULL DEFAULT 'info',
                        subject TEXT,
                        payload TEXT,
                        created_at TEXT DEFAULT (datetime('now')),
                        read_at TEXT
                    )",

                    @"CREATE INDEX IF NOT EXISTS idx_messages_to ON messages(to_pid, read_at)"
                };

                foreach (string sql in ddl)
                {
                    using (var cmd = new SQLiteCommand(sql, conn))
                        cmd.ExecuteNonQuery();
                }
            }
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Register this instance and start the heartbeat timer.
        /// </summary>
        public void Start()
        {
            CleanupStale();
            Register();

            _heartbeatTimer = new Timer(HeartbeatIntervalMs);
            _heartbeatTimer.Elapsed += (s, e) => Heartbeat();
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
        }

        /// <summary>
        /// Deregister this instance and stop the heartbeat.
        /// </summary>
        public void Stop()
        {
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
            Deregister();
        }

        private void Register()
        {
            using (var conn = OpenConnection())
            using (var cmd = new SQLiteCommand(@"
                INSERT OR REPLACE INTO instances (pid, solution_path, app_file, active_file, active_procedure, working_on, started_at, heartbeat_at)
                VALUES (@pid, @sln, @app, @file, @proc, @work, datetime('now'), datetime('now'))", conn))
            {
                cmd.Parameters.AddWithValue("@pid", _pid);
                cmd.Parameters.AddWithValue("@sln", (object)SolutionPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@app", (object)AppFile ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@file", (object)ActiveFile ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@proc", (object)ActiveProcedure ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@work", (object)WorkingOn ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private void Deregister()
        {
            try
            {
                using (var conn = OpenConnection())
                using (var cmd = new SQLiteCommand("DELETE FROM instances WHERE pid = @pid", conn))
                {
                    cmd.Parameters.AddWithValue("@pid", _pid);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { /* best-effort on shutdown */ }
        }

        private void Heartbeat()
        {
            try
            {
                using (var conn = OpenConnection())
                using (var cmd = new SQLiteCommand(@"
                    UPDATE instances SET
                        solution_path = @sln, app_file = @app, active_file = @file,
                        active_procedure = @proc, working_on = @work,
                        heartbeat_at = datetime('now')
                    WHERE pid = @pid", conn))
                {
                    cmd.Parameters.AddWithValue("@pid", _pid);
                    cmd.Parameters.AddWithValue("@sln", (object)SolutionPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@app", (object)AppFile ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@file", (object)ActiveFile ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@proc", (object)ActiveProcedure ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@work", (object)WorkingOn ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                CleanupStale();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[InstanceCoord] Heartbeat error: " + ex.Message);
            }
        }

        private void CleanupStale()
        {
            try
            {
                using (var conn = OpenConnection())
                {
                    // Remove entries for processes that are no longer running
                    var stalePids = new List<int>();
                    using (var cmd = new SQLiteCommand(
                        "SELECT pid FROM instances WHERE heartbeat_at < datetime('now', '-' || @sec || ' seconds')", conn))
                    {
                        cmd.Parameters.AddWithValue("@sec", StaleSeconds);
                        using (var reader = cmd.ExecuteReader())
                            while (reader.Read())
                                stalePids.Add(reader.GetInt32(0));
                    }

                    foreach (int pid in stalePids)
                    {
                        // Double-check the process is actually dead
                        bool alive = false;
                        try { alive = Process.GetProcessById(pid) != null; }
                        catch { /* process doesn't exist */ }

                        if (!alive)
                        {
                            using (var cmd = new SQLiteCommand("DELETE FROM instances WHERE pid = @pid", conn))
                            {
                                cmd.Parameters.AddWithValue("@pid", pid);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[InstanceCoord] Cleanup error: " + ex.Message);
            }
        }

        #endregion

        #region Queries

        /// <summary>
        /// Get all live peer instances (excludes self).
        /// </summary>
        public List<InstanceInfo> GetPeers()
        {
            var peers = new List<InstanceInfo>();
            using (var conn = OpenConnection())
            using (var cmd = new SQLiteCommand(@"
                SELECT pid, solution_path, app_file, active_file, active_procedure, working_on, started_at, heartbeat_at
                FROM instances
                WHERE pid != @pid AND heartbeat_at >= datetime('now', '-' || @sec || ' seconds')
                ORDER BY app_file", conn))
            {
                cmd.Parameters.AddWithValue("@pid", _pid);
                cmd.Parameters.AddWithValue("@sec", StaleSeconds);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        peers.Add(new InstanceInfo
                        {
                            Pid = reader.GetInt32(0),
                            SolutionPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                            AppFile = reader.IsDBNull(2) ? null : reader.GetString(2),
                            ActiveFile = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ActiveProcedure = reader.IsDBNull(4) ? null : reader.GetString(4),
                            WorkingOn = reader.IsDBNull(5) ? null : reader.GetString(5),
                            StartedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                            HeartbeatAt = reader.IsDBNull(7) ? null : reader.GetString(7)
                        });
                    }
                }
            }
            return peers;
        }

        /// <summary>
        /// Check if any other instance has the given procedure open.
        /// Returns the conflicting instance info, or null if no conflict.
        /// </summary>
        public InstanceInfo CheckProcedureConflict(string appFile, string procedureName)
        {
            if (string.IsNullOrEmpty(procedureName)) return null;

            using (var conn = OpenConnection())
            using (var cmd = new SQLiteCommand(@"
                SELECT pid, solution_path, app_file, active_file, active_procedure, working_on, started_at, heartbeat_at
                FROM instances
                WHERE pid != @pid
                  AND app_file = @app AND active_procedure = @proc
                  AND heartbeat_at >= datetime('now', '-' || @sec || ' seconds')
                LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("@pid", _pid);
                cmd.Parameters.AddWithValue("@app", appFile);
                cmd.Parameters.AddWithValue("@proc", procedureName);
                cmd.Parameters.AddWithValue("@sec", StaleSeconds);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new InstanceInfo
                        {
                            Pid = reader.GetInt32(0),
                            SolutionPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                            AppFile = reader.IsDBNull(2) ? null : reader.GetString(2),
                            ActiveFile = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ActiveProcedure = reader.IsDBNull(4) ? null : reader.GetString(4),
                            WorkingOn = reader.IsDBNull(5) ? null : reader.GetString(5),
                            StartedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                            HeartbeatAt = reader.IsDBNull(7) ? null : reader.GetString(7)
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get a summary of all instances for display (including self).
        /// </summary>
        public List<InstanceInfo> GetAllInstances()
        {
            var instances = new List<InstanceInfo>();
            using (var conn = OpenConnection())
            using (var cmd = new SQLiteCommand(@"
                SELECT pid, solution_path, app_file, active_file, active_procedure, working_on, started_at, heartbeat_at
                FROM instances
                WHERE heartbeat_at >= datetime('now', '-' || @sec || ' seconds')
                ORDER BY started_at", conn))
            {
                cmd.Parameters.AddWithValue("@sec", StaleSeconds);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var info = new InstanceInfo
                        {
                            Pid = reader.GetInt32(0),
                            SolutionPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                            AppFile = reader.IsDBNull(2) ? null : reader.GetString(2),
                            ActiveFile = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ActiveProcedure = reader.IsDBNull(4) ? null : reader.GetString(4),
                            WorkingOn = reader.IsDBNull(5) ? null : reader.GetString(5),
                            StartedAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                            HeartbeatAt = reader.IsDBNull(7) ? null : reader.GetString(7)
                        };
                        info.IsSelf = info.Pid == _pid;
                        instances.Add(info);
                    }
                }
            }
            return instances;
        }

        #endregion

        #region Messaging

        /// <summary>
        /// Send a message to a specific instance or broadcast to all.
        /// </summary>
        public void SendMessage(int? toPid, string type, string subject, string payload)
        {
            using (var conn = OpenConnection())
            using (var cmd = new SQLiteCommand(@"
                INSERT INTO messages (from_pid, to_pid, type, subject, payload)
                VALUES (@from, @to, @type, @subject, @payload)", conn))
            {
                cmd.Parameters.AddWithValue("@from", _pid);
                cmd.Parameters.AddWithValue("@to", toPid.HasValue ? (object)toPid.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@type", type ?? "info");
                cmd.Parameters.AddWithValue("@subject", (object)subject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@payload", (object)payload ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Get unread messages for this instance (direct + broadcasts). Marks them as read.
        /// </summary>
        public List<InstanceMessage> GetMessages()
        {
            var messages = new List<InstanceMessage>();
            using (var conn = OpenConnection())
            {
                // Fetch unread messages addressed to us or broadcast
                using (var cmd = new SQLiteCommand(@"
                    SELECT id, from_pid, to_pid, type, subject, payload, created_at
                    FROM messages
                    WHERE read_at IS NULL AND (to_pid = @pid OR to_pid IS NULL) AND from_pid != @pid
                    ORDER BY created_at", conn))
                {
                    cmd.Parameters.AddWithValue("@pid", _pid);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            messages.Add(new InstanceMessage
                            {
                                Id = reader.GetInt64(0),
                                FromPid = reader.GetInt32(1),
                                ToPid = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                                Type = reader.GetString(3),
                                Subject = reader.IsDBNull(4) ? null : reader.GetString(4),
                                Payload = reader.IsDBNull(5) ? null : reader.GetString(5),
                                CreatedAt = reader.GetString(6)
                            });
                        }
                    }
                }

                // Mark them read
                if (messages.Count > 0)
                {
                    using (var cmd = new SQLiteCommand(@"
                        UPDATE messages SET read_at = datetime('now')
                        WHERE read_at IS NULL AND (to_pid = @pid OR to_pid IS NULL) AND from_pid != @pid", conn))
                    {
                        cmd.Parameters.AddWithValue("@pid", _pid);
                        cmd.ExecuteNonQuery();
                    }
                }

                // Purge old messages (older than 1 hour)
                using (var cmd = new SQLiteCommand(
                    "DELETE FROM messages WHERE created_at < datetime('now', '-1 hour')", conn))
                    cmd.ExecuteNonQuery();
            }
            return messages;
        }

        /// <summary>
        /// Peek at unread message count without marking them read.
        /// </summary>
        public int GetUnreadCount()
        {
            using (var conn = OpenConnection())
            using (var cmd = new SQLiteCommand(@"
                SELECT COUNT(*) FROM messages
                WHERE read_at IS NULL AND (to_pid = @pid OR to_pid IS NULL) AND from_pid != @pid", conn))
            {
                cmd.Parameters.AddWithValue("@pid", _pid);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        #endregion

        #region Connection

        private SQLiteConnection OpenConnection()
        {
            var conn = new SQLiteConnection("Data Source=" + _dbPath + ";Version=3;Journal Mode=WAL;Busy Timeout=3000;");
            conn.Open();
            // Enable WAL mode and foreign keys
            using (var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", conn))
                cmd.ExecuteNonQuery();
            return conn;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Stop();
            }
        }

        #endregion
    }

    #region Models

    public class InstanceInfo
    {
        public int Pid { get; set; }
        public string SolutionPath { get; set; }
        public string AppFile { get; set; }
        public string ActiveFile { get; set; }
        public string ActiveProcedure { get; set; }
        public string WorkingOn { get; set; }
        public string StartedAt { get; set; }
        public string HeartbeatAt { get; set; }
        public bool IsSelf { get; set; }
    }

    public class InstanceMessage
    {
        public long Id { get; set; }
        public int FromPid { get; set; }
        public int? ToPid { get; set; }
        public string Type { get; set; }
        public string Subject { get; set; }
        public string Payload { get; set; }
        public string CreatedAt { get; set; }
    }

    #endregion
}
