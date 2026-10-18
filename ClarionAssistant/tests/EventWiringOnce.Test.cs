using System;
using System.IO;
using System.Reflection;
using ClarionAssistant.Services;

// Regression harness for GH #263 run-2 review: GeneratedSourceWatch subscribed ActiveWorkbenchWindowChanged
// before its reflection loop, so a throwing AddEventHandler left it unwired and the next setSource subscribed
// the handler AGAIN. Compiles the REAL Services\EventWiringOnce.cs against a fake workbench whose events
// count their subscribers, one of which throws on add.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir. GeneratedSourceWatch.cs itself is
// IDE-coupled, so the "it goes through EventWiringOnce and marks before subscribing" part is a source check.
public class FakeWorkbench
{
    public int OpenedSubs, ClosedSubs, ThrowAttempts;
    private EventHandler _opened, _closed;
    public event EventHandler ViewOpened { add { OpenedSubs++; _opened += value; } remove { OpenedSubs--; _opened -= value; } }
    public event EventHandler ViewClosed { add { ThrowAttempts++; throw new InvalidOperationException("add failed"); } remove { } }
    public event EventHandler Other { add { ClosedSubs++; _closed += value; } remove { ClosedSubs--; _closed -= value; } }
    public int Fire() { int n = 0; if (_opened != null) n = _opened.GetInvocationList().Length; _opened?.Invoke(this, EventArgs.Empty); return n; }
}

static class EventWiringOnceTest
{
    static int pass = 0, fail = 0, calls = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }
    static void Handler(object s, EventArgs e) { calls++; }

    static int Main(string[] args)
    {
        Console.WriteLine("EventWiringOnce (GH #263 run 2: GeneratedSourceWatch wiring is idempotent)");
        var h = typeof(EventWiringOnceTest).GetMethod("Handler", BindingFlags.NonPublic | BindingFlags.Static);
        var names = new[] { "ViewOpened", "ViewClosed", "Missing" };
        var wb = new FakeWorkbench();
        var w = new EventWiringOnce();

        Ok("a null workbench attempts nothing (retried later)", w.WireAll(null, names, h, null) == 0 && !w.AllAttempted(names));
        int first = w.WireAll(wb, names, h, s => Console.WriteLine("    log: " + s));
        Ok("first call subscribes the one event that can be", first == 1 && wb.OpenedSubs == 1, "wired " + first);
        Ok("the throwing add was attempted once and did not escape", wb.ThrowAttempts == 1);
        Ok("every name counts as attempted (subscribed, failed, or missing)", w.AllAttempted(names));
        for (int i = 0; i < 3; i++) w.WireAll(wb, names, h, null);   // the next setSources call again
        Ok("repeated calls add NO second copy of the handler", wb.OpenedSubs == 1 && wb.Fire() == 1 && calls == 1,
           "subs " + wb.OpenedSubs);
        Ok("...and never retry the throwing add", wb.ThrowAttempts == 1, "attempts " + wb.ThrowAttempts);
        var w2 = new EventWiringOnce();
        w2.WireAll(wb, new[] { "Other" }, h, null);
        w2.WireAll(wb, new[] { "Other", "Other" }, h, null);
        Ok("a name repeated in one call is subscribed once", wb.ClosedSubs == 1);

        if (args.Length > 0)
        {
            string src = File.ReadAllText(Path.Combine(args[0], "GeneratedSourceWatch.cs"));
            int mark = src.IndexOf("_activeAttempted = true;", StringComparison.Ordinal);
            int sub = src.IndexOf("ActiveWorkbenchWindowChanged += OnWorkbenchChanged", StringComparison.Ordinal);
            Ok("GeneratedSourceWatch marks ActiveWorkbenchWindowChanged attempted BEFORE subscribing", mark > 0 && sub > mark,
               "mark@" + mark + " sub@" + sub);
            Ok("GeneratedSourceWatch wires the view events through EventWiringOnce",
               src.Contains("_viewWiring.WireAll(") && !src.Contains("evt.AddEventHandler"));
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
