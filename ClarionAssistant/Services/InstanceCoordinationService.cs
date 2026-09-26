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

        // How long a peer whose heartbeat is still CURRENT must stay continuously not-responding before
        // this instance deletes its row. The trade-off: too short and a healthy IDE vanishes from every
        // peer during a code generation, a build or a big dictionary load (its UI thread is blocked well
        // past Process.Responding's 5s, while its threadpool heartbeat keeps the row fresh) — and
        // CheckProcedureConflict goes blind to it exactly while it is working. Too long and a real zombie
        // (GH #179: hung inside a native modal, heart-beating forever) squats that much longer. Two
        // minutes outlasts ordinary generations and builds; a zombie is there for hours, so a couple of
        // minutes is nothing next to "until reboot". A busy IDE that runs past it is swept and puts
        // itself back on the first heartbeat after its UI frees up.
        private const int HungPeerGraceSeconds = 120;

        // Wall-clock budget for the Responding checks in one sweep. Each check can wait ~5s on a hung
        // peer; with several peers in a row the heartbeat itself stalled. Peers past the budget are
        // checked on the next beat (the sweep resumes where it stopped, so none is starved).
        private const int SweepBudgetMs = 3000;

        // Set by Stop() so a heartbeat already in flight neither re-arms the timer nor re-inserts the
        // row Deregister() just removed. Written and re-checked under _registrationLock.
        private volatile bool _stopping;
        private readonly object _registrationLock = new object();

        // Fresh-row peers seen not responding: pid -> (their row's started_at, first time seen hung).
        // Per sweeping instance, in memory; touched only by the (non-overlapping) sweep.
        // Monotonic (Stopwatch ticks), NOT wall clock: a Windows time correction between sweeps must neither
        // satisfy the grace at once (a forward jump evicting a busy IDE) nor postpone it indefinitely (a backward one).
        private readonly Dictionary<int, KeyValuePair<string, long>> _hungSince = new Dictionary<int, KeyValuePair<string, long>>();
        private int _sweepCursor;

        internal enum PeerState { Gone, Responding, NotResponding }

        // Test seams (tests\InstanceCoordination.ReRegister.Test.cs). Production uses the defaults.
        /// <summary>Is THIS process's UI alive? Gates re-registering a row a peer's sweep deleted.</summary>
        internal Func<bool> SelfResponsive = CurrentProcessResponding;
        /// <summary>The per-peer liveness check the sweep uses.</summary>
        internal Func<int, PeerState> PeerCheck = CheckPeer;
        /// <summary>Runs in Heartbeat after the re-register decision, before taking the registration lock.</summary>
        internal Action BeforeReRegister = null;   // explicit: only the harness sets it (CS0649)
        /// <summary>Monotonic clock in Stopwatch ticks (Stopwatch.Frequency per second) for hang durations.</summary>
        internal Func<long> MonotonicTicks = Stopwatch.GetTimestamp;
        internal TimeSpan HungPeerGrace = TimeSpan.FromSeconds(HungPeerGraceSeconds);
        internal TimeSpan SweepBudget = TimeSpan.FromMilliseconds(SweepBudgetMs);
        internal int HeartbeatInterval = HeartbeatIntervalMs;

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
            _stopping = false;
            // No CleanupStale() here: Start() runs on the IDE's UI thread (AssistantChatControl), and the
            // sweep's Process.Responding checks can wait ~5s per hung peer. The first timer beat sweeps.
            Register();

            // AutoReset=false, re-armed at the END of each beat: beats never overlap. The sweep calls
            // Process.Responding on every peer, and each call can wait up to ~5s on a hung one; with
            // AutoReset=true a slow sweep let the next Elapsed start on another threadpool thread while
            // the last was still running, and they piled up. Now a slow beat only delays the next one.
            _heartbeatTimer = new Timer(HeartbeatInterval);
            _heartbeatTimer.Elapsed += OnHeartbeatElapsed;
            _heartbeatTimer.AutoReset = false;
            _heartbeatTimer.Start();
        }

        private void OnHeartbeatElapsed(object sender, ElapsedEventArgs e)
        {
            try { Heartbeat(); }
            finally
            {
                var t = sender as Timer;
                if (!_stopping && t != null)
                {
                    try { t.Start(); }
                    catch (ObjectDisposedException) { /* Stop() disposed it while this beat ran */ }
                }
            }
        }

        /// <summary>
        /// Deregister this instance and stop the heartbeat.
        /// </summary>
        public void Stop()
        {
            // Under the lock, so a heartbeat that already decided to re-register either finishes its
            // INSERT before this DELETE, or sees _stopping and skips it — never inserts after it.
            lock (_registrationLock)
            {
                _stopping = true;
                Deregister();
            }
            var t = _heartbeatTimer;
            _heartbeatTimer = null;
            if (t != null)
            {
                t.Stop();
                t.Dispose();
            }
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
                    DeleteInstance(conn, _pid);
            }
            catch { /* best-effort on shutdown */ }
        }

        internal void Heartbeat()
        {
            try
            {
                int rows;
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
                    rows = cmd.ExecuteNonQuery();
                }

                // SELF-HEAL: the UPDATE touching 0 rows means our row is gone — a peer's sweep decided we
                // were dead (Process.Responding is also false for a HEALTHY instance whose UI thread is
                // blocked past the timeout: a long generation, a build, a big dictionary load), or the db
                // was cleared out from under us. Before this, an UPDATE-only heartbeat left that instance
                // invisible to every peer until it was restarted; re-registering costs one INSERT on a
                // path that already runs every 10s. (PR #208 review.)
                //
                // ...but ONLY when our own UI is alive. This heartbeat runs on a threadpool timer, so a HUNG
                // Clarion keeps beating too: without this gate a zombie that a peer's sweep just deleted
                // put itself straight back within one interval, and the sweep never won. The check is the
                // same predicate the peers' sweep deletes us by (Process.Responding on this process), so
                // we re-register exactly when a sweep would keep us: a busy IDE comes back on the first
                // beat after its UI frees up, a hung one stays deleted.
                if (rows == 0 && !_stopping)
                {
                    // SelfResponsive can wait ~5s, so it runs OUTSIDE the lock (Stop() on the UI thread
                    // must not wait on it); _stopping is re-checked inside.
                    if (SelfResponsive())
                    {
                        BeforeReRegister?.Invoke();
                        lock (_registrationLock)
                        {
                            if (!_stopping)
                            {
                                Debug.WriteLine("[InstanceCoord] heartbeat found no row for pid " + _pid + " — re-registering");
                                Register();
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[InstanceCoord] heartbeat found no row for pid " + _pid + ", UI not responding — staying deregistered");
                    }
                }

                CleanupStale();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[InstanceCoord] Heartbeat error: " + ex.Message);
            }
        }

        private sealed class SweepRow
        {
            public int Pid;
            public string HeartbeatAt;
            public string StartedAt;
            public bool Stale;
        }

        internal void CleanupStale()
        {
            try
            {
                using (var conn = OpenConnection())
                {
                    // Every peer row (never our own — we are plainly running this code), with what we saw of
                    // it, so each DELETE below can be conditional on the row still being the one we judged.
                    var rows = new List<SweepRow>();
                    using (var cmd = new SQLiteCommand(@"
                        SELECT pid, heartbeat_at, started_at,
                               heartbeat_at < datetime('now', '-' || @sec || ' seconds')
                        FROM instances WHERE pid != @pid ORDER BY pid", conn))
                    {
                        cmd.Parameters.AddWithValue("@pid", _pid);
                        cmd.Parameters.AddWithValue("@sec", StaleSeconds);
                        using (var reader = cmd.ExecuteReader())
                            while (reader.Read())
                                rows.Add(new SweepRow
                                {
                                    Pid = reader.GetInt32(0),
                                    HeartbeatAt = reader.IsDBNull(1) ? null : reader.GetString(1),
                                    StartedAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    Stale = !reader.IsDBNull(3) && reader.GetInt64(3) != 0
                                });
                    }

                    // Forget hung-tracking for peers whose row is gone (deleted, or they exited cleanly).
                    var present = new HashSet<int>();
                    foreach (var r in rows) present.Add(r.Pid);
                    foreach (int pid in new List<int>(_hungSince.Keys))
                        if (!present.Contains(pid)) _hungSince.Remove(pid);

                    if (rows.Count == 0) { _sweepCursor = 0; return; }

                    // Bounded: stop once SweepBudget is spent and resume from that peer next beat, so a
                    // run of slow Responding checks can neither stall this beat nor starve a later peer.
                    var budget = Stopwatch.StartNew();
                    int start = _sweepCursor % rows.Count;
                    int next = start;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int idx = (start + i) % rows.Count;
                        if (i > 0 && budget.Elapsed >= SweepBudget) { next = idx; break; }
                        SweepOne(conn, rows[idx]);
                        next = (idx + 1) % rows.Count;
                    }
                    _sweepCursor = next;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[InstanceCoord] Cleanup error: " + ex.Message);
            }
        }

        private void SweepOne(SQLiteConnection conn, SweepRow row)
        {
            PeerState state = PeerCheck(row.Pid);

            if (row.Stale)
            {
                // PASS 1 — timestamp-stale rows (no heartbeat in StaleSeconds): the owner exited, or is
                // hung badly enough that even its threadpool timer stopped. Delete unless it answers —
                // and only if its heartbeat has not moved since we looked (it may have recovered while
                // we waited on Responding) and the pid was not reused by a newly registered process.
                _hungSince.Remove(row.Pid);
                if (state != PeerState.Responding)
                    DeleteIfUnchanged(conn, row, requireSameHeartbeat: true);
                return;
            }

            // PASS 2 — GH #179 (2026-08-29 report): the heartbeat is a System.Timers.Timer, so it fires
            // on a threadpool thread and is completely unaffected by a blocked UI thread. A Clarion.exe
            // hung inside a native modal therefore KEEPS heart-beating: its row never goes stale by
            // timestamp, ever — it squats as a permanently "live" peer that every CheckProcedureConflict/
            // GetPeers call collides with, until the process is killed (the user needed a reboot).
            // But a single not-responding answer is also what a BUSY IDE gives mid-build, so a fresh row
            // is deleted only after its owner stays not-responding for HungPeerGrace (see the constant).
            switch (state)
            {
                case PeerState.Responding:
                    _hungSince.Remove(row.Pid);
                    break;

                case PeerState.Gone:
                    _hungSince.Remove(row.Pid);
                    DeleteIfUnchanged(conn, row, requireSameHeartbeat: false);
                    break;

                case PeerState.NotResponding:
                    long now = MonotonicTicks();
                    KeyValuePair<string, long> seen;
                    if (!_hungSince.TryGetValue(row.Pid, out seen) || seen.Key != row.StartedAt)
                    {
                        // First sighting — or the pid now belongs to a different registration.
                        _hungSince[row.Pid] = new KeyValuePair<string, long>(row.StartedAt, now);
                    }
                    else if (TimeSpan.FromSeconds((now - seen.Value) / (double)Stopwatch.Frequency) >= HungPeerGrace)
                    {
                        // Its heartbeat keeps moving (that is the zombie's signature), so match on the
                        // registration (started_at), not on heartbeat_at.
                        if (DeleteIfUnchanged(conn, row, requireSameHeartbeat: false))
                        {
                            Debug.WriteLine("[InstanceCoord] swept pid " + row.Pid + ": not responding for " +
                                            (int)((now - seen.Value) / Stopwatch.Frequency) + "s");
                            _hungSince.Remove(row.Pid);
                        }
                    }
                    break;
            }
        }

        /// <summary>Delete the peer's row only if it is still the registration we judged: same started_at
        /// (a reused pid that registered anew is left alone) and, for a stale row, a heartbeat no newer than
        /// the one we saw (a peer that recovered meanwhile is left alone).</summary>
        private static bool DeleteIfUnchanged(SQLiteConnection conn, SweepRow row, bool requireSameHeartbeat)
        {
            string sql = "DELETE FROM instances WHERE pid = @pid AND started_at IS @started";
            if (requireSameHeartbeat) sql += " AND heartbeat_at <= @observed";
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@pid", row.Pid);
                cmd.Parameters.AddWithValue("@started", (object)row.StartedAt ?? DBNull.Value);
                if (requireSameHeartbeat)
                    cmd.Parameters.AddWithValue("@observed", (object)row.HeartbeatAt ?? DBNull.Value);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        private static PeerState CheckPeer(int pid)
        {
            return ClassifyPeer(() =>
            {
                using (var p = Process.GetProcessById(pid))
                    return p.Responding;
            });
        }

        /// <summary>
        /// Existence AND a responding UI — existence alone (the pre-#208 check) is also true for a
        /// hung-but-not-exited zombie. No such process (GetProcessById's ArgumentException) or one that
        /// exited mid-check (InvalidOperationException) is Gone. Anything else that stops us asking — access
        /// denied on an elevated peer, UIPI (Win32Exception), or an error we did not foresee — is treated
        /// as ALIVE: not being allowed to look is no evidence of a hang, and deleting a live peer's row
        /// blinds conflict detection to it.
        /// </summary>
        internal static PeerState ClassifyPeer(Func<bool> responding)
        {
            try
            {
                return responding() ? PeerState.Responding : PeerState.NotResponding;
            }
            catch (ArgumentException) { return PeerState.Gone; }
            catch (InvalidOperationException) { return PeerState.Gone; }
            catch (Exception) { return PeerState.Responding; }
        }

        /// <summary>
        /// Is THIS process's UI answering? Process.Responding on ourselves: SendMessageTimeout(WM_NULL,
        /// SMTO_ABORTIFHUNG) to our own main window, sent from the heartbeat's threadpool thread, so it can
        /// never deadlock against the UI thread it is asking about. Chosen over a UI-thread timestamp
        /// because (1) it is exactly the predicate a peer's sweep deletes our row by, so the two sides
        /// cannot disagree and ping-pong the row; (2) it is the OS's own "Not Responding" test, not a
        /// threshold we tune; (3) this file is also linked into the headless clarion-mcp-server, which has
        /// no UI thread and must not reference WinForms: with no main window Responding is true, and a
        /// peer's sweep never deletes such a row either. A Process object caches MainWindowHandle, so
        /// take a fresh one each time. Can't tell -> true: re-registering is the pre-#208 behaviour.
        /// </summary>
        private static bool CurrentProcessResponding()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                    return p.Responding;
            }
            catch { return true; }
        }

        private static void DeleteInstance(SQLiteConnection conn, int pid)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM instances WHERE pid = @pid", conn))
            {
                cmd.Parameters.AddWithValue("@pid", pid);
                cmd.ExecuteNonQuery();
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
