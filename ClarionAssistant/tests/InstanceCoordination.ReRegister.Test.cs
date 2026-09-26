using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ClarionAssistant.Services;
using PeerState = ClarionAssistant.Services.InstanceCoordinationService.PeerState;

// Harness for PR #208 — InstanceCoordinationService's heartbeat and peer sweep. Compiles the REAL
// InstanceCoordinationService.cs against the vendored System.Data.SQLite (x86) and throwaway databases;
// nothing touches %APPDATA%\ClarionAssistant\instances.db.
//
//   1. Re-register decision: a HUNG instance stays deleted; a busy one comes back once its UI frees up.
//   2. Start() does not sweep (it runs on the IDE's UI thread; the first timer beat sweeps).
//   3. Stop() racing a heartbeat that already decided to re-register: the row stays deleted.
//   4. A fresh (still heart-beating) peer is deleted only after HungPeerGrace of CONTINUOUS not-responding;
//      a response in between resets the clock.
//   5. Conditional deletes: a stale peer that heart-beats while we check it, or a pid reused by a new
//      registration, is not removed.
//   6. The sweep stops at its time budget and resumes with the next peer on the following beat.
//   7. Access denied / Win32Exception from the peer check means ALIVE, not hung.
//   8. Beats never overlap even when the sweep is slow.
//
// Liveness and time are injected through the service's internal seams; what Process.Responding itself
// returns for a hung Clarion window can't be staged here without a real hung IDE.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class InstanceCoordinationReRegisterTest
{
    static int pass = 0, fail = 0;
    static string Dir, DbPath;
    static int dbSeq = 0;
    static readonly int Self = Process.GetCurrentProcess().Id;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static SQLiteConnection Open()
    {
        var c = new SQLiteConnection("Data Source=" + DbPath + ";Version=3;Busy Timeout=3000;");
        c.Open();
        return c;
    }

    static long Exec(string sql, int pid, string arg = null)
    {
        using (var c = Open())
        using (var cmd = new SQLiteCommand(sql, c))
        {
            cmd.Parameters.AddWithValue("@pid", pid);
            cmd.Parameters.AddWithValue("@arg", (object)arg ?? DBNull.Value);
            object r = cmd.ExecuteScalar();
            return r == null || r is DBNull ? 0 : Convert.ToInt64(r);
        }
    }

    static bool HasRow(int pid) { return Exec("SELECT COUNT(*) FROM instances WHERE pid = @pid", pid) > 0; }
    static void PeerSweepsUs() { Exec("DELETE FROM instances WHERE pid = @pid", Self); }

    /// <summary>A peer row. ageSeconds 0 = fresh heartbeat; 60 = stale (StaleSeconds is 30).</summary>
    static void AddPeer(int pid, int ageSeconds = 0, string startedAt = "2026-01-01 00:00:00")
    {
        Exec("INSERT OR REPLACE INTO instances (pid, started_at, heartbeat_at) VALUES (@pid, @arg, datetime('now', '-" +
             ageSeconds + " seconds'))", pid, startedAt);
    }

    /// <summary>A fresh database and a service on it whose timer never beats on its own.</summary>
    static InstanceCoordinationService NewService()
    {
        DbPath = Path.Combine(Dir, "instances" + (++dbSeq) + ".db");
        var svc = new InstanceCoordinationService(DbPath);
        svc.HeartbeatInterval = 3600 * 1000;
        svc.SelfResponsive = () => true;
        svc.PeerCheck = pid => PeerState.Responding;
        return svc;
    }

    static int Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "ca-instcoord-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Dir);
        try
        {
            ReRegisterDecision();
            StartDoesNotSweep();
            StopRacingReRegister();
            HungGrace();
            ConditionalDeletes();
            SweepBudget();
            ClassifyPeer();
            NoOverlap();
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(Dir, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }

    // ---- 1 ----------------------------------------------------------------------------------------
    static void ReRegisterDecision()
    {
        bool uiAlive = false;
        var svc = NewService();
        svc.SelfResponsive = () => uiAlive;
        svc.Start();
        Ok("Start() registers this process", HasRow(Self));

        PeerSweepsUs();
        svc.Heartbeat();
        Ok("hung (UI not responding): the heartbeat does NOT re-register", !HasRow(Self));
        svc.Heartbeat();
        svc.Heartbeat();
        Ok("hung: still deleted after further beats", !HasRow(Self));

        uiAlive = true;
        svc.Heartbeat();
        Ok("responsive again: the next heartbeat re-registers", HasRow(Self));
        svc.Heartbeat();
        Ok("responsive with the row present: stays registered (UPDATE path)", HasRow(Self));

        svc.Stop();
        Ok("Stop() deregisters", !HasRow(Self));
        svc.Heartbeat();
        Ok("a heartbeat after Stop() does not re-insert the row", !HasRow(Self));
    }

    // ---- 2 ----------------------------------------------------------------------------------------
    static void StartDoesNotSweep()
    {
        var svc = NewService();
        AddPeer(900001);
        int checks = 0;
        svc.PeerCheck = pid => { Interlocked.Increment(ref checks); return PeerState.Responding; };
        svc.Start();
        Ok("Start() runs no peer Responding check (it is on the UI thread)", checks == 0, "checks=" + checks);
        svc.Heartbeat();
        Ok("the first heartbeat does the sweep", checks == 1, "checks=" + checks);
        svc.Stop();
    }

    // ---- 3 ----------------------------------------------------------------------------------------
    static void StopRacingReRegister()
    {
        var svc = NewService();
        svc.Start();
        PeerSweepsUs();

        var reached = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        svc.BeforeReRegister = () => { reached.Set(); release.Wait(5000); };

        var beat = new Thread(() => svc.Heartbeat());
        beat.Start();
        bool gotThere = reached.Wait(5000);          // the beat has decided to re-register...
        var stop = new Thread(() => svc.Stop());
        stop.Start();
        bool stopReturned = stop.Join(5000);         // ...and Stop() runs to completion meanwhile
        release.Set();                               // now the beat carries on
        beat.Join(5000);

        Ok("race staged: heartbeat paused between its decision and Register", gotThere);
        Ok("Stop() did not wait on the paused heartbeat", stopReturned);
        Ok("Stop() then an in-flight re-register: the row stays deleted", !HasRow(Self));
    }

    // ---- 4 ----------------------------------------------------------------------------------------
    static void HungGrace()
    {
        var svc = NewService();
        DateTime now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        svc.UtcNow = () => now;
        const int Hung = 900010, Flaky = 900011;
        var states = new Dictionary<int, PeerState> { { Hung, PeerState.NotResponding }, { Flaky, PeerState.NotResponding } };
        svc.PeerCheck = pid => states.ContainsKey(pid) ? states[pid] : PeerState.Responding;
        AddPeer(Hung);
        AddPeer(Flaky);

        svc.CleanupStale();
        Ok("a busy peer (one not-responding answer, heartbeat fresh) is NOT deleted", HasRow(Hung));

        now = now.AddSeconds(60);
        states[Flaky] = PeerState.Responding;       // Flaky's build finishes
        svc.CleanupStale();
        Ok("still not responding at 60s: not deleted yet", HasRow(Hung));

        now = now.AddSeconds(40);
        states[Flaky] = PeerState.NotResponding;    // Flaky busy again at 100s
        svc.CleanupStale();

        now = now.AddSeconds(30);                   // 130s
        AddPeer(Hung);                              // it keeps heart-beating: row stays fresh
        svc.CleanupStale();
        Ok("continuously not responding past the grace: deleted", !HasRow(Hung));
        Ok("a peer that responded in between restarts its grace (30s < 120s): kept", HasRow(Flaky));

        now = now.AddSeconds(125);                  // Flaky: 155s since it went quiet again
        svc.CleanupStale();
        Ok("...and is deleted once its own grace runs out", !HasRow(Flaky));

        Ok("HungPeerGrace defaults to 2 minutes",
           new InstanceCoordinationService(DbPath).HungPeerGrace == TimeSpan.FromMinutes(2));
    }

    // ---- 5 ----------------------------------------------------------------------------------------
    static void ConditionalDeletes()
    {
        var svc = NewService();
        const int Dead = 900020, Recovers = 900021, Reused = 900022;
        AddPeer(Dead, ageSeconds: 60);
        AddPeer(Recovers, ageSeconds: 60);
        svc.PeerCheck = pid =>
        {
            if (pid == Recovers) AddPeer(Recovers, ageSeconds: 0);   // beats while we wait on Responding
            return PeerState.Gone;
        };
        svc.CleanupStale();
        Ok("control: a stale row whose process is gone is deleted", !HasRow(Dead));
        Ok("a stale peer that heart-beats during the check is NOT deleted", HasRow(Recovers));

        DateTime now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var svc2 = NewService();
        svc2.UtcNow = () => now;
        AddPeer(Reused, startedAt: "2026-01-01 00:00:00");
        bool replaced = false;
        svc2.PeerCheck = pid =>
        {
            if (pid == Reused && replaced) AddPeer(Reused, startedAt: "2026-09-26 12:02:00");  // new process, same pid
            return PeerState.NotResponding;
        };
        svc2.CleanupStale();
        now = now.AddSeconds(130);
        replaced = true;
        svc2.CleanupStale();
        Ok("a pid reused by a new registration is NOT deleted on the old one's grace", HasRow(Reused));
    }

    // ---- 6 ----------------------------------------------------------------------------------------
    static void SweepBudget()
    {
        var svc = NewService();
        svc.SweepBudget = TimeSpan.FromMilliseconds(1000);
        for (int i = 0; i < 5; i++) AddPeer(900030 + i);
        var checkedPids = new List<int>();
        svc.PeerCheck = pid => { lock (checkedPids) checkedPids.Add(pid); Thread.Sleep(400); return PeerState.Responding; };

        var sw = Stopwatch.StartNew();
        svc.CleanupStale();
        long ms = sw.ElapsedMilliseconds;
        int first = checkedPids.Count;
        Ok("one sweep stops at its budget (5 peers x 400ms, 1s budget)", first >= 1 && first <= 3, "checked=" + first);
        Ok("so the beat is bounded", ms < 2000, ms + "ms");

        svc.CleanupStale();
        var distinct = new HashSet<int>(checkedPids);
        Ok("the next sweep resumes where it stopped: every peer checked within two beats", distinct.Count == 5,
           "distinct=" + distinct.Count);
    }

    // ---- 7 ----------------------------------------------------------------------------------------
    static void ClassifyPeer()
    {
        Ok("responding -> Responding", InstanceCoordinationService.ClassifyPeer(() => true) == PeerState.Responding);
        Ok("not responding -> NotResponding", InstanceCoordinationService.ClassifyPeer(() => false) == PeerState.NotResponding);
        Ok("no such process (ArgumentException) -> Gone",
           InstanceCoordinationService.ClassifyPeer(() => { throw new ArgumentException("not running"); }) == PeerState.Gone);
        Ok("exited mid-check (InvalidOperationException) -> Gone",
           InstanceCoordinationService.ClassifyPeer(() => { throw new InvalidOperationException("exited"); }) == PeerState.Gone);
        Ok("access denied (Win32Exception, elevated peer / UIPI) -> alive",
           InstanceCoordinationService.ClassifyPeer(() => { throw new Win32Exception(5); }) == PeerState.Responding);

        var svc = NewService();
        AddPeer(900040, ageSeconds: 60);
        AddPeer(900041);
        svc.PeerCheck = pid => InstanceCoordinationService.ClassifyPeer(() => { throw new Win32Exception(5); });
        svc.CleanupStale();
        Ok("an access-denied peer's rows are not swept", HasRow(900040) && HasRow(900041));
    }

    // ---- 8 ----------------------------------------------------------------------------------------
    static void NoOverlap()
    {
        var svc = NewService();
        AddPeer(999999);
        int inFlight = 0, maxInFlight = 0, calls = 0;
        svc.HeartbeatInterval = 50;
        svc.SweepBudget = TimeSpan.FromSeconds(10);
        svc.PeerCheck = pid =>
        {
            int now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = maxInFlight) < now && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
            Interlocked.Increment(ref calls);
            Thread.Sleep(300);
            Interlocked.Decrement(ref inFlight);
            return PeerState.Responding;
        };
        svc.Start();
        Thread.Sleep(2000);
        svc.Stop();
        Thread.Sleep(700);

        Ok("the timer kept beating with a slow sweep", calls >= 3, "calls=" + calls);
        Ok("beats never overlap (max concurrent sweeps = 1)", maxInFlight == 1, "max=" + maxInFlight);
        Ok("the responding peer was never swept", HasRow(999999));
    }
}
