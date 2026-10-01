using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

// Harness for IdeVersionFollower (905928c7) — the chat-independent watch on Build > Set Clarion Version
// that LspAutostartCommand feeds from the Clarion.Version PropertyChanged event and its 5 s tick.
//
// THE BUG. Only AssistantChatControl.SyncVersionWithIde reacted to a version switch, so with no CA chat
// tab the language server and the .red stayed on the old version. The follower must: stay quiet on the
// first observation (addin start: nothing to restart), fire exactly once per real change (the event AND
// the tick both observe the same switch), and treat the IDE's spellings of "the running version" (empty,
// "Current", "(Current Version)") and case differences as no change.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class IdeVersionFollowerTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static int Main()
    {
        const string C11 = "Clarion 11.1.13855";
        const string C12 = "Clarion 12.0.13908";

        // (a) Seed, then a switch, observed by the event AND the tick: one Changed.
        {
            var f = new IdeVersionFollower();
            var fired = new List<string>();
            f.Changed += (was, now) => fired.Add(was + " -> " + now);

            Ok("first observation seeds without firing", !f.Observe(C11) && fired.Count == 0, string.Join("; ", fired));
            Ok("same version again does not fire", !f.Observe(C11) && fired.Count == 0);
            Ok("a switch fires", f.Observe(C12) && fired.Count == 1, string.Join("; ", fired));
            Ok("...with (was, now)", fired.Count == 1 && fired[0] == C11 + " -> " + C12, fired.Count > 0 ? fired[0] : "none");
            Ok("the tick re-observing the same switch does not fire again", !f.Observe(C12) && fired.Count == 1);
            Ok("switching back fires again", f.Observe(C11) && fired.Count == 2, string.Join("; ", fired));
        }

        // (b) The IDE's spellings of "the running version" are one choice; case and padding don't count.
        {
            var f = new IdeVersionFollower();
            int fired = 0;
            f.Changed += (was, now) => fired++;
            f.Observe("");
            Ok("\"\" -> \"(Current Version)\" is no change", !f.Observe("(Current Version)"));
            Ok("-> null is no change", !f.Observe(null));
            Ok("-> \"Current\" is no change", !f.Observe("Current"));
            Ok("Current -> a named version fires", f.Observe(C12) && fired == 1, "fired=" + fired);
            Ok("case difference is no change", !f.Observe(C12.ToUpperInvariant()));
            Ok("padding is no change", !f.Observe("  " + C12 + " "));
            Ok("named -> Current fires", f.Observe("") && fired == 2, "fired=" + fired);
            Ok("Last is normalized", f.Last == ClarionVersionSelector.CurrentChoice, f.Last);
        }

        // (c) A seed that is itself a change from nothing must not fire: at addin start the LSP is not yet
        //     running, and firing would run a .red reload + restart request for no reason on every launch.
        {
            var f = new IdeVersionFollower();
            int fired = 0;
            f.Changed += (was, now) => fired++;
            f.Observe(C12);
            Ok("seed on a named version does not fire", fired == 0, "fired=" + fired);
        }

        Console.WriteLine();
        Console.WriteLine("IdeVersionFollower: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
