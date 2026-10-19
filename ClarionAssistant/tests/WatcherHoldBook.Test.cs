using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

// Harness for WatcherHoldBook (GH #205 review: SEC MED, F1-2, F1-3): the bookkeeping of NativeTabSync's IDE file-watcher
// holds, driven with a fake clock. NativeTabSync's UI-thread flips (Disable / SetWatcher / the replayed change notice)
// need a live IDE; their wiring is pinned by tests\OpenTabGuard.SourceScan.ps1.
//
//   * holders are COUNTED: concurrent writes share one hold, the watcher comes back only when the last one is done (F1-2)
//   * the cap is NEVER renewed: a hold ends CapMs after its FIRST holder, whoever joined later; a capped tab cools down
//     for as long, so back-to-back checks cannot keep the watcher off (SEC MED)
//   * GENERATIONS: a stale cap timer or a stale release (an older generation) does nothing; a hold being released takes
//     no new holder (F1-3)
//   * the change test compares against the FIRST holder's stamp (SEC LOW)
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
static class WatcherHoldBookTest
{
    static int pass = 0, fail = 0;
    static DateTime now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static int Main()
    {
        const int Cap = 30000;
        int flips = 0;
        Action flip = () => flips++;
        WatcherHoldBook.Entry e, e2;
        WatcherHoldBook.Take t;
        bool still;
        WatcherHoldBook.Entry ended;

        Console.WriteLine("-- counted holders (F1-2)");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            flips = 0;
            t = book.Hold(tab, true, "C:\\a.clw", 100, 10, flip, out e);
            Ok("the first check takes a new hold and disables the watcher", t == WatcherHoldBook.Take.NewHold && flips == 1 && book.Holders(tab) == 1);
            t = book.Hold(tab, false, "C:\\a.clw", 200, 20, flip, out e2);
            Ok("a second concurrent check JOINS it (no second disable while it is off)", t == WatcherHoldBook.Take.Joined && e2 == e && flips == 1 && book.Holders(tab) == 2);
            Ok("a joined hold keeps the FIRST holder's stamp", e.Ticks == 100 && e.Length == 10);
            Ok("the first reload drops one holder and keeps the hold (the caller disables the watcher again)",
               book.AfterReload(tab, out still, out ended) && still && ended == null && book.Holders(tab) == 1 && book.IsHeld(tab));
            Ok("the last reload ends the hold", book.AfterReload(tab, out still, out ended) && !still && ended == e && !book.IsHeld(tab));

            t = book.Hold(tab, true, "C:\\a.clw", 300, 30, flip, out e);
            book.Hold(tab, false, "C:\\a.clw", 300, 30, flip, out e2);
            Ok("one refused write's release drops one holder: no release while another write still holds", book.Drop(tab, "refused") == null && book.Holders(tab) == 1);
            var last = book.Drop(tab, "failed");
            Ok("the last holder's release hands the hold over for release (Releasing)", last == e && last.Releasing && last.Why == "failed");
            Ok("a release that reaches the UI thread for that generation finishes it", book.Finish(tab, e.Gen) && !book.IsHeld(tab));
            Ok("a drop with no hold is a no-op", book.Drop(tab, "x") == null && book.Drop(null, "x") == null);

            t = book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e);
            int before = flips;
            t = book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("a joining check whose watcher the IDE re-armed meanwhile (a save) disables it again", t == WatcherHoldBook.Take.Joined && flips == before + 1);
            book.AfterReload(tab, out still, out ended); book.AfterReload(tab, out still, out ended);
            Ok("not watching and not held: nothing to hold", book.Hold(tab, false, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NotWatching && !book.IsHeld(tab));
            bool threw = false;
            try { book.Hold(tab, true, "C:\\a.clw", 1, 1, () => { throw new InvalidOperationException("boom"); }, out e); } catch (InvalidOperationException) { threw = true; }
            Ok("a watcher flip that throws records no hold", threw && !book.IsHeld(tab));
        }

        Console.WriteLine("-- the cap is never renewed (SEC MED)");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            var start = now;
            book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e);
            Ok("a new hold is due in the full cap", book.DueMs(e) == Cap);
            now = start.AddSeconds(20);
            book.Hold(tab, false, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("a check 20 s later joins, and the deadline stays at the FIRST hold + 30 s", e2 == e && book.DueMs(e) == 10000, "due " + book.DueMs(e));
            now = start.AddSeconds(29);
            book.Hold(tab, false, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("repeated checks never push it out", book.DueMs(e) == 1000, "due " + book.DueMs(e));
            now = start.AddSeconds(31);
            Ok("past the cap it is due now", book.DueMs(e) == 0);
            var x = book.Expire(tab, e.Gen);
            Ok("the cap releases it whatever the count (3 holders)", x == e && x.Releasing && x.Why.Contains("3 holder"), x != null ? x.Why : "null");
            Ok("a hold being released takes no new holder (F1-3)", book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.Releasing && e2 == null);
            Ok("the release finishes", book.Finish(tab, e.Gen));
            Ok("a capped tab cools down: a check right after takes no hold", book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.CoolingDown && !book.IsHeld(tab));
            now = now.AddSeconds(29);
            Ok("still cooling 29 s on", book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.CoolingDown);
            now = now.AddSeconds(2);
            Ok("after the cool-down a check holds again (a fresh 30 s)", book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.NewHold && book.DueMs(e2) == Cap);
            object other = new object();
            book.Hold(other, true, "C:\\b.clw", 1, 1, flip, out e);
            book.AfterReload(other, out still, out ended);
            Ok("a hold that ended by its reload does NOT cool the tab down", book.Hold(other, true, "C:\\b.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NewHold);
        }

        Console.WriteLine("-- generations (F1-3)");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            book.Hold(tab, true, "C:\\a.clw", 1, 1, flip, out e);
            long g1 = e.Gen;
            book.AfterReload(tab, out still, out ended);              // hold 1 ended by its reload
            book.Hold(tab, true, "C:\\a.clw", 2, 2, flip, out e2);   // hold 2
            Ok("each hold gets a new generation", e2.Gen != g1);
            Ok("hold 1's cap timer firing late does nothing to hold 2", book.Expire(tab, g1) == null && !e2.Releasing && book.Holders(tab) == 1);
            Ok("a release posted for hold 1 does nothing to hold 2", !book.Finish(tab, g1) && book.IsHeld(tab));
            var d = book.Drop(tab, "refused");
            Ok("a release of the current generation acts", d == e2 && book.Finish(tab, e2.Gen) && !book.IsHeld(tab));
            book = new WatcherHoldBook(Cap, () => now);
            book.Hold(tab, true, "C:\\a.clw", 3, 3, flip, out e);
            Ok("a release that was not released (not Releasing) is not finished", !book.Finish(tab, e.Gen) && book.IsHeld(tab));
            var x = book.Expire(tab, e.Gen);
            Ok("a late reload while it is being released ends it, so the stale release is a no-op",
               x == e && book.AfterReload(tab, out still, out ended) && !still && ended == e && !book.Finish(tab, e.Gen));
            // The F1-3 race: hold 1 is capped and its release is on its way to the UI thread; a late reload ends it; hold 2
            // is taken and is itself being released. Hold 1's stale release must not finish hold 2.
            book = new WatcherHoldBook(Cap, () => now);
            book.Hold(tab, true, "C:\\a.clw", 3, 3, flip, out e);
            long stale = e.Gen;
            book.Expire(tab, stale);
            book.AfterReload(tab, out still, out ended);
            now = now.AddMilliseconds(Cap + 1000);   // past the cap's cool-down
            book.Hold(tab, true, "C:\\a.clw", 4, 4, flip, out e2);
            var r2 = book.Drop(tab, "refused");
            Ok("a stale release (older generation) does not finish a NEWER hold that is being released",
               e2 != null && r2 == e2 && !book.Finish(tab, stale) && book.IsHeld(tab) && book.Finish(tab, e2.Gen));

            book = new WatcherHoldBook(Cap, () => now);
            book.Hold(tab, true, "C:\\a.clw", 3, 3, flip, out e);
            book.Expire(tab, e.Gen);
            Ok("a second expiry of the same generation does nothing", book.Expire(tab, e.Gen) == null);
            book.Forget(tab, e.Gen + 99);
            Ok("forgetting another generation leaves it", book.IsHeld(tab));
            book.Forget(tab, e.Gen);
            Ok("a release that could not be posted forgets its generation", !book.IsHeld(tab));
        }

        Console.WriteLine("-- changed since the hold BEGAN (SEC LOW)");
        {
            var hold = new WatcherHoldBook.Entry { Ticks = 100, Length = 10 };
            Ok("same time and size: unchanged", !WatcherHoldBook.Changed(hold, true, 100, 10));
            Ok("newer time: changed", WatcherHoldBook.Changed(hold, true, 101, 10));
            Ok("other size: changed", WatcherHoldBook.Changed(hold, true, 100, 11));
            Ok("deleted (length -1): changed", WatcherHoldBook.Changed(hold, true, 0, -1));
            Ok("unreadable stamp counts as changed (replay rather than swallow)", WatcherHoldBook.Changed(hold, false, 100, 10));
        }

        Console.WriteLine();
        Console.WriteLine("WatcherHoldBook.Test: " + pass + " passed, " + fail + " failed");
        return fail > 0 ? 1 : 0;
    }
}
