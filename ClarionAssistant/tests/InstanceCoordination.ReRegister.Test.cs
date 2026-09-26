using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ClarionAssistant.Services;

// Harness for PR #208 — InstanceCoordinationService's heartbeat after a peer's sweep deleted our row.
// Compiles the REAL InstanceCoordinationService.cs against the vendored System.Data.SQLite (x86) and a
// throwaway instances.db; nothing touches %APPDATA%\ClarionAssistant\instances.db.
//
//   1. Re-register decision: a HUNG instance (SelfResponsive=false) stays deleted, beat after beat; a
//      busy one comes back on the first beat after its UI frees up (SelfResponsive=true).
//   2. A beat in flight during Stop() does not re-insert the row Deregister() removed.
//   3. Beats never overlap even when the sweep's per-peer check is slow (it can take ~5s on a hung peer).
//
// The liveness checks are injected through the service's internal seams; what Process.Responding itself
// returns for a hung Clarion window can't be staged here without a real hung IDE.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class InstanceCoordinationReRegisterTest
{
    static int pass = 0, fail = 0;
    static string DbPath;
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

    static long Exec(string sql, int pid)
    {
        using (var c = Open())
        using (var cmd = new SQLiteCommand(sql, c))
        {
            cmd.Parameters.AddWithValue("@pid", pid);
            object r = cmd.ExecuteScalar();
            return r == null || r is DBNull ? 0 : Convert.ToInt64(r);
        }
    }

    static bool HasRow(int pid) { return Exec("SELECT COUNT(*) FROM instances WHERE pid = @pid", pid) > 0; }
    static void PeerSweepsUs() { Exec("DELETE FROM instances WHERE pid = @pid", Self); }

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-instcoord-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "instances.db");
        try
        {
            // ---- 1. re-register decision -------------------------------------------------------------
            bool uiAlive = false;
            var svc = new InstanceCoordinationService(DbPath);
            svc.HeartbeatInterval = 3600 * 1000;           // the timer must not beat on its own here
            svc.SelfResponsive = () => uiAlive;
            svc.PeerAliveAndResponding = pid => true;
            svc.Start();
            Ok("Start() registers this process", HasRow(Self));

            PeerSweepsUs();
            svc.Heartbeat();
            Ok("hung (UI not responding): the heartbeat does NOT re-register", !HasRow(Self));
            svc.Heartbeat();
            svc.Heartbeat();
            Ok("hung: still deleted after further beats", !HasRow(Self));

            uiAlive = true;                                  // the long generation/build finished
            svc.Heartbeat();
            Ok("responsive again: the next heartbeat re-registers", HasRow(Self));

            svc.Heartbeat();
            Ok("responsive with the row present: stays registered (UPDATE path)", HasRow(Self));

            PeerSweepsUs();
            svc.Heartbeat();
            Ok("busy-but-healthy swept while responding: re-registers at once", HasRow(Self));

            // ---- 2. no resurrection after Stop() -----------------------------------------------------
            svc.Stop();
            Ok("Stop() deregisters", !HasRow(Self));
            svc.Heartbeat();                                 // a beat that was already in flight
            Ok("a beat in flight during Stop() does not re-insert the row", !HasRow(Self));

            // ---- 3. beats never overlap --------------------------------------------------------------
            const int FakePeer = 999999;
            using (var c = Open())
            using (var cmd = new SQLiteCommand(
                "INSERT OR REPLACE INTO instances (pid, heartbeat_at) VALUES (@pid, datetime('now'))", c))
            {
                cmd.Parameters.AddWithValue("@pid", FakePeer);
                cmd.ExecuteNonQuery();
            }

            int inFlight = 0, maxInFlight = 0, calls = 0;
            var slow = new InstanceCoordinationService(DbPath);
            slow.HeartbeatInterval = 50;                     // far shorter than one slow sweep
            slow.SelfResponsive = () => true;
            slow.PeerAliveAndResponding = pid =>
            {
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = maxInFlight) < now && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
                Interlocked.Increment(ref calls);
                Thread.Sleep(300);                           // a peer whose Responding check is slow
                Interlocked.Decrement(ref inFlight);
                return true;
            };
            slow.Start();
            Thread.Sleep(2000);
            slow.Stop();
            Thread.Sleep(700);                               // let a beat that was mid-sweep drain

            Ok("the timer kept beating with a slow sweep", calls >= 3, "calls=" + calls);
            Ok("beats never overlap (max concurrent sweeps = 1)", maxInFlight == 1, "max=" + maxInFlight);
            Ok("the fake peer (responding) was never swept", HasRow(FakePeer));
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }
}
