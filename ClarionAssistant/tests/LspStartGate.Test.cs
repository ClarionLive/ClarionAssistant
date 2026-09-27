using System;
using System.Threading;
using ClarionAssistant.Services;

// Harness for LspStartGate (16d140e9, pipeline run 1) — the single-flight guard around LspService's
// background start, and the restart request a version change makes.
//
// THE BUG. RestartIfVersionChanged stopped the running client and then called EnsureRunningInBackground,
// whose plain CompareExchange guard was still held by the start that had launched that client (client
// running, guard not yet released). The restart was dropped; the starter released the guard without
// noticing; no LSP ran until an unrelated trigger. The model below replays LspService's protocol with a
// fake client: a starter (TryBegin -> start -> End, going round again when End says so) and a restarter
// (stop -> RequestRestart -> start if it won the gate).
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class LspStartGateTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    // The fake server: which version it runs, or null when stopped.
    static string running;
    static string wanted;
    static readonly object clientLock = new object();

    static void StartClient() { lock (clientLock) running = wanted; }
    static void StopClient() { lock (clientLock) running = null; }

    static int Main()
    {
        // (a) THE REPORTED RACE, step by step. The starter has started v1 but not yet released the gate;
        //     the version changes to v2; the restarter stops v1 and asks for a restart.
        {
            var gate = new LspStartGate();
            wanted = "C11"; running = null;
            Ok("(a) starter takes the gate", gate.TryBegin());
            StartClient();                                    // starter's EnsureRunning: client up on C11
            wanted = "C10";                                   // Build > Set Clarion Version -> C10
            StopClient();                                     // RestartIfVersionChanged: stop under the lock
            bool restarterStarts = gate.RequestRestart();     // ... and ask for a start
            Ok("(a) restart does not start while the starter holds the gate", !restarterStarts);
            bool again = gate.End();                          // starter's finally
            Ok("(a) the starter is told to start again", again);
            if (again && gate.TryBegin()) { StartClient(); gate.End(); }
            Ok("(a) the server ends up running on the NEW version", running == "C10", "running=" + (running ?? "(none)"));
            Ok("(a) gate idle afterwards", !gate.IsStarting);
        }

        // (b) Restart when no start is in flight: the restarter starts it itself.
        {
            var gate = new LspStartGate();
            wanted = "C12"; running = "C11";
            StopClient();
            bool mine = gate.RequestRestart();
            Ok("(b) restart with an idle gate -> restarter owns the start", mine);
            if (mine) { StartClient(); Ok("(b) ... and End asks for nothing more", !gate.End()); }
            Ok("(b) running on the new version", running == "C12");
        }

        // (c) A plain start with no restart: End asks for nothing, and a second TryBegin is refused meanwhile.
        {
            var gate = new LspStartGate();
            Ok("(c) first TryBegin wins", gate.TryBegin());
            Ok("(c) second TryBegin refused while held", !gate.TryBegin());
            Ok("(c) End without a restart -> no rerun", !gate.End());
            Ok("(c) TryBegin works again after End", gate.TryBegin());
            gate.End();
        }

        // (d) Stress: the restart lands at a random point in the starter's run. Whatever the interleaving,
        //     the final server runs the new version (the request is never lost) and the gate ends idle.
        {
            int lost = 0, iterations = 2000;
            var rnd = new Random(16140);
            for (int i = 0; i < iterations; i++)
            {
                var gate = new LspStartGate();
                wanted = "OLD"; running = null;
                int spinA = rnd.Next(0, 200), spinB = rnd.Next(0, 200);
                var go = new ManualResetEventSlim(false);

                Action starterLoop = null;
                starterLoop = () =>
                {
                    if (!gate.TryBegin()) return;
                    StartClient();
                    Thread.SpinWait(spinA);
                    if (gate.End()) starterLoop();
                };
                var starter = new Thread(() => { go.Wait(); starterLoop(); });
                var restarter = new Thread(() =>
                {
                    go.Wait();
                    Thread.SpinWait(spinB);
                    wanted = "NEW";
                    StopClient();
                    if (gate.RequestRestart()) { StartClient(); if (gate.End()) starterLoop(); }
                });
                starter.Start(); restarter.Start(); go.Set();
                starter.Join(); restarter.Join();
                if (running != "NEW" || gate.IsStarting) lost++;
            }
            Ok("(d) " + iterations + " randomized interleavings: restart never lost, gate always idle", lost == 0, lost + " lost");
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
