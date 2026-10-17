using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ClarionAssistant.Services;

// Harness for IdeFanOut and EndpointOwnership (GH #205 pipeline run 1).
//
// IdeFanOut: the standalone's open-tab check asks every IDE IN PARALLEL inside ONE budget, and an IDE that just timed
// out is skipped for a while instead of adding its timeout to every write. Driven with real threads and real sleeps
// (slow fake IDEs) and a settable clock for the quiet window.
//
// EndpointOwnership: one endpoint record per IDE process whichever of its McpServers runs; the record moves when the
// publisher stops, so closing the chat pane never leaves the IDE unreachable, and never two records at once.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class IdeFanOutTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    sealed class FakeIde { public int Pid; public int DelayMs; public bool Throw; public int Asked; }

    static TabProbe Answer(FakeIde f)
    {
        Interlocked.Increment(ref f.Asked);
        if (f.DelayMs > 0) Thread.Sleep(f.DelayMs);
        if (f.Throw) throw new InvalidOperationException("connection refused");
        return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNone, Ide = "IDE " + f.Pid };
    }

    sealed class Srv { public string Name; public bool Running = true; }

    static int Main()
    {
        try
        {
            Console.WriteLine("-- IdeFanOut: parallel, one budget");
            var clock = DateTime.UtcNow;
            var fan = new IdeFanOut { Now = () => clock };
            var three = new List<FakeIde> { new FakeIde { Pid = 1, DelayMs = 700 }, new FakeIde { Pid = 2, DelayMs = 700 }, new FakeIde { Pid = 3, DelayMs = 700 } };
            var sw = Stopwatch.StartNew();
            var r = fan.Ask(three, f => f.Pid, Answer, 3000);
            long ms = sw.ElapsedMilliseconds;
            Ok("three 700 ms IDEs answer in well under their serial 2100 ms", ms < 1600, ms + " ms");
            Ok("all three answered, in endpoint order", r.Count == 3 && r.TrueForAll(p => p.Reachable) && r[0].Ide == "IDE 1" && r[2].Ide == "IDE 3");

            Console.WriteLine("-- IdeFanOut: a hung IDE costs the budget once, then is skipped");
            var hung = new FakeIde { Pid = 7, DelayMs = 5000 };
            var fast = new FakeIde { Pid = 8 };
            sw.Restart();
            r = fan.Ask(new List<FakeIde> { hung, fast }, f => f.Pid, Answer, 800);
            ms = sw.ElapsedMilliseconds;
            Ok("the overall budget bounds the call (800 ms + grace)", ms < 1600, ms + " ms");
            Ok("the hung IDE is unreachable and marked timed out", !r[0].Reachable && r[0].TimedOut && r[0].Reason.Contains("did not answer within 800 ms"), r[0].Reason);
            Ok("the fast IDE still answered", r[1].Reachable);
            Ok("the hung IDE is now quiet", fan.IsQuiet(7));
            int askedBefore = hung.Asked;
            sw.Restart();
            r = fan.Ask(new List<FakeIde> { hung, fast }, f => f.Pid, Answer, 800);
            ms = sw.ElapsedMilliseconds;
            Ok("while quiet it is NOT asked again", hung.Asked == askedBefore, hung.Asked + " asks");
            Ok("...so the next write does not wait for it", ms < 400, ms + " ms");
            Ok("...and it is reported unreachable, saying when it is asked again", !r[0].Reachable && r[0].Reason.Contains("not asked"), r[0].Reason);
            clock = clock.AddMilliseconds(fan.QuietMs + 1);
            hung.DelayMs = 0;
            r = fan.Ask(new List<FakeIde> { hung, fast }, f => f.Pid, Answer, 800);
            Ok("after the quiet window it is asked again", hung.Asked == askedBefore + 1 && r[0].Reachable);
            // (The quiet entry also expires on its own; IdeFanOut drops it on a real answer only as housekeeping, so a
            // mutant that skips that Remove is equivalent and stays green - by design, not a gap.)
            Ok("...and is no longer quiet", !fan.IsQuiet(7));

            Console.WriteLine("-- IdeFanOut: fast failures are not 'quiet'");
            var refused = new FakeIde { Pid = 9, Throw = true };
            r = fan.Ask(new List<FakeIde> { refused }, f => f.Pid, Answer, 800);
            Ok("a throwing ask is an unreachable answer with the reason", !r[0].Reachable && r[0].Reason.Contains("connection refused"), r[0].Reason);
            Ok("...and does not make the IDE quiet", !fan.IsQuiet(9));
            var timedOutAnswer = new FakeIde { Pid = 10 };
            r = fan.Ask(new List<FakeIde> { timedOutAnswer }, f => f.Pid,
                        f => new TabProbe { Reachable = false, TimedOut = true, Reason = "timed out after 3000 ms" }, 800);
            Ok("an answer flagged TimedOut (the per-call HTTP timeout) makes it quiet too", fan.IsQuiet(10));
            r = fan.Ask(new List<FakeIde>(), f => f.Pid, Answer, 800);
            Ok("no IDEs: no answers, no wait", r.Count == 0);

            Console.WriteLine("-- EndpointOwnership");
            var own = new EndpointOwnership<Srv>();
            var log = new List<string>();
            Func<Srv, bool> running = s => s.Running;
            Action<Srv> pub = s => log.Add("publish " + s.Name);
            Action<Srv> wd = s => log.Add("withdraw " + s.Name);
            var callback = new Srv { Name = "callback" };
            var chat = new Srv { Name = "chat" };
            own.Join(callback, running, pub);
            own.Join(chat, running, pub);
            Ok("the first server publishes; the second stands by (one record per IDE)", string.Join(",", log.ToArray()) == "publish callback", string.Join(",", log.ToArray()));
            Ok("the owner is the first", own.Owner == callback);
            log.Clear();
            own.Leave(chat, running, pub, wd);
            Ok("a stand-by leaving touches no record", log.Count == 0, string.Join(",", log.ToArray()));
            own.Join(chat, running, pub);
            log.Clear();
            callback.Running = false;
            own.Leave(callback, running, pub, wd);
            Ok("the owner leaving withdraws its record and hands it to the running stand-by",
               string.Join(",", log.ToArray()) == "withdraw callback,publish chat", string.Join(",", log.ToArray()));
            Ok("the owner is now the stand-by", own.Owner == chat);
            log.Clear();
            chat.Running = false;
            own.Leave(chat, running, pub, wd);
            Ok("the last one leaving withdraws, publishes nothing", string.Join(",", log.ToArray()) == "withdraw chat" && own.Owner == null);
            var a = new Srv { Name = "a" }; var b = new Srv { Name = "b" };
            log.Clear();
            own.Join(a, running, pub);
            a.Running = false;   // stopped without leaving (crash path)
            own.Join(b, running, pub);
            Ok("a joiner takes over from an owner that is no longer running", own.Owner == b && log.Contains("publish b"));
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] harness threw: " + ex);
        }

        Console.WriteLine();
        Console.WriteLine("IdeFanOut/EndpointOwnership: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
