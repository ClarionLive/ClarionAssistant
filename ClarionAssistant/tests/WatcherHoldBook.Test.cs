using System;
using System.Collections.Generic;
using System.Threading;
using ClarionAssistant.Services;

// Harness for WatcherHoldBook (GH #205 review: SEC MED, F1-2, F1-3; re-review: holder tokens, D1, D2, D4, D5, D6): the
// bookkeeping of NativeTabSync's IDE file-watcher holds, driven with a fake clock. NativeTabSync's UI-thread flips
// (Disable / SetWatcher / the replayed change notice) need a live IDE; their wiring is pinned by
// tests\OpenTabGuard.SourceScan.ps1.
//
//   * holders are TOKENS: concurrent writes share one hold, each drops only its OWN holder, the watcher comes back only
//     when the last one is done (F1-2); a writer that never held drops nobody's (D4); a stale or unknown token is a no-op
//   * a released token is WITHDRAWN: a hold for it that lands later is refused (D1); a release by token needs no tab (D2)
//   * an older standalone (no token): anonymous holders; its reload drops one of THOSE, never a token holder
//   * a reload that leaves holders re-stamps the hold (D5)
//   * a check can wait for a RELEASING hold to finish, then hold afresh (D6)
//   * the cap is NEVER renewed: a hold ends CapMs after its FIRST holder, whoever joined later; a capped tab cools down
//     for as long, so back-to-back checks cannot keep the watcher off (SEC MED)
//   * GENERATIONS: a stale cap timer or a stale release (an older generation) does nothing; a hold being released takes
//     no new holder (F1-3)
//   * the change test compares against the hold's stamp (SEC LOW)
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

    const string A = "token-aaaaaaaa", B = "token-bbbbbbbb", C = "token-cccccccc";

    static int Main()
    {
        const int Cap = 30000;
        int flips = 0;
        Action flip = () => flips++;
        WatcherHoldBook.Entry e, e2;
        WatcherHoldBook.Take t;
        bool still;
        WatcherHoldBook.Entry ended;
        List<WatcherHoldBook.Entry> last;

        Console.WriteLine("-- counted holders, by token (F1-2)");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            flips = 0;
            t = book.Hold(tab, A, false, true, "C:\\a.clw", 100, 10, flip, out e);
            Ok("the first check takes a new hold and disables the watcher", t == WatcherHoldBook.Take.NewHold && flips == 1 && book.Holders(tab) == 1);
            t = book.Hold(tab, B, false, false, "C:\\a.clw", 200, 20, flip, out e2);
            Ok("a second concurrent check JOINS it (no second disable while it is off)", t == WatcherHoldBook.Take.Joined && e2 == e && flips == 1 && book.Holders(tab) == 2);
            Ok("a joined hold keeps the FIRST holder's stamp", e.Ticks == 100 && e.Length == 10);
            t = book.Hold(tab, B, false, false, "C:\\a.clw", 200, 20, flip, out e2);
            Ok("the same token again is still ONE holder", t == WatcherHoldBook.Take.Joined && book.Holders(tab) == 2);
            Ok("A's reload drops A only, and keeps the hold (the caller disables the watcher again)",
               book.AfterReload(tab, A, false, 0, 0, out still, out ended) && still && ended == null && book.Holders(tab) == 1 && book.HasHolder(tab, B) && !book.HasHolder(tab, A));
            Ok("B's reload ends the hold", book.AfterReload(tab, B, false, 0, 0, out still, out ended) && !still && ended == e && !book.IsHeld(tab));

            // Every check has a fresh token (A and B are withdrawn now that their reloads happened).
            const string A2 = "token-a2a2a2a2", B2 = "token-b2b2b2b2";
            t = book.Hold(tab, A2, false, true, "C:\\a.clw", 300, 30, flip, out e);
            book.Hold(tab, B2, false, false, "C:\\a.clw", 300, 30, flip, out e2);
            last = book.DropToken(A2, "refused");
            Ok("one refused write's release drops its holder: no release while another write still holds", last.Count == 0 && book.Holders(tab) == 1 && book.HasHolder(tab, B2));
            last = book.DropToken(B2, "failed");
            Ok("the last holder's release hands the hold over for release (Releasing)", last.Count == 1 && last[0] == e && e.Releasing && e.Why == "failed");
            Ok("a release that reaches the UI thread for that generation finishes it", book.Finish(tab, e.Gen) && !book.IsHeld(tab));
            Ok("a drop with no hold, or no token, is a no-op", book.DropToken(C, "x").Count == 0 && book.DropToken(null, "x").Count == 0 && book.DropToken("", "x").Count == 0);
            Ok("a hold needs a token", book.Hold(tab, null, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NoToken
               && book.Hold(tab, "", false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NoToken && !book.IsHeld(tab));

            t = book.Hold(tab, "t1-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e);
            int before = flips;
            t = book.Hold(tab, "t2-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("a joining check whose watcher the IDE re-armed meanwhile (a save) disables it again", t == WatcherHoldBook.Take.Joined && flips == before + 1);
            book.AfterReload(tab, "t1-xxxxxxxx", false, 0, 0, out still, out ended); book.AfterReload(tab, "t2-xxxxxxxx", false, 0, 0, out still, out ended);
            Ok("not watching and not held: nothing to hold", book.Hold(tab, "t3-xxxxxxxx", false, false, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NotWatching && !book.IsHeld(tab));
            bool threw = false;
            try { book.Hold(tab, "t4-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, () => { throw new InvalidOperationException("boom"); }, out e); } catch (InvalidOperationException) { threw = true; }
            Ok("a watcher flip that throws records no hold", threw && !book.IsHeld(tab));
        }

        Console.WriteLine("-- D4: a writer that never held cannot drop another writer's hold");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            book.Hold(tab, A, false, true, "C:\\a.clw", 1, 1, flip, out e);
            // B's check got no hold (cooling down, releasing, timed out, dirty); B then wrote and reloaded.
            Ok("B's reload (B never held) drops nothing: A still holds, and the caller disables the watcher again",
               book.AfterReload(tab, B, false, 0, 0, out still, out ended) && still && ended == null && book.Holders(tab) == 1 && book.HasHolder(tab, A));
            Ok("B's release drops nothing either", book.DropToken(B, "refused").Count == 0 && book.HasHolder(tab, A) && !e.Releasing);
            Ok("A's own reload still ends it", book.AfterReload(tab, A, false, 0, 0, out still, out ended) && !still && ended == e);
            Ok("a malformed token (\"\") on a reload drops nothing",
               book.Hold(tab, C, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NewHold
               && book.AfterReload(tab, "", false, 0, 0, out still, out ended) && still && book.HasHolder(tab, C));
        }

        Console.WriteLine("-- an older standalone (no token): anonymous holders");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            book.Hold(tab, "anon-1111111111", true, true, "C:\\a.clw", 1, 1, flip, out e);
            book.Hold(tab, A, false, false, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("a tokenless reload drops one ANONYMOUS holder, never the token holder",
               book.AfterReload(tab, null, false, 0, 0, out still, out ended) && still && book.Holders(tab) == 1 && book.HasHolder(tab, A));
            Ok("with no anonymous holder left, a tokenless reload drops nothing",
               book.AfterReload(tab, null, false, 0, 0, out still, out ended) && still && book.HasHolder(tab, A));
            Ok("a tokenless refused reload (DropAnonymous) drops nothing from a token holder", book.DropAnonymous(tab, "refused") == null && book.HasHolder(tab, A));
            book.DropToken(A, "x"); book.Finish(tab, e.Gen);
            book.Hold(tab, "anon-2222222222", true, true, "C:\\a.clw", 1, 1, flip, out e);
            var d = book.DropAnonymous(tab, "refused");
            Ok("an anonymous holder's refused reload drops it, and the last one releases", d == e && e.Releasing);
        }

        Console.WriteLine("-- D1: release by request id, before or after the hold lands");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab1 = new object(), tab2 = new object();
            book.Hold(tab1, A, false, true, "C:\\a.clw", 1, 1, flip, out e);
            book.Hold(tab2, A, false, true, "C:\\a.clw", 1, 1, flip, out e2);
            last = book.DropToken(A, "the writer's check timed out");
            Ok("a release by token drops that writer from EVERY tab it holds - no tab lookup needed (D2)", last.Count == 2 && e.Releasing && e2.Releasing);
            book.Finish(tab1, e.Gen); book.Finish(tab2, e2.Gen);
            Ok("a hold for a RELEASED token that lands late is refused (Withdrawn)",
               book.Hold(tab1, A, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.Withdrawn && e == null && !book.IsHeld(tab1));
            Ok("a release that arrives BEFORE its hold withdraws the token, so the hold never happens",
               book.DropToken(B, "timed out").Count == 0 && book.Hold(tab2, B, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.Withdrawn && !book.IsHeld(tab2));
            Ok("another writer is unaffected", book.Hold(tab2, C, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NewHold);
            book.AfterReload(tab2, C, false, 0, 0, out still, out ended);
            Ok("a token whose reload happened is withdrawn too (a duplicate late hold cannot linger)",
               book.Hold(tab2, C, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.Withdrawn);
            now = now.AddMilliseconds(2.0 * Cap + 1);
            Ok("a withdrawn token is forgotten after twice the cap", book.Hold(tab1, A, false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NewHold);
            DateTime clock = now;
            var many = new WatcherHoldBook(Cap, () => clock);
            for (int i = 0; i < 1100; i++) { clock = clock.AddMilliseconds(1); many.DropToken("bulk-" + i.ToString("D8"), "x"); }
            Ok("the withdrawn set is bounded (an old one is forgotten, the newest kept)",
               many.Hold(tab1, "bulk-00000000", false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NewHold
               && many.Hold(tab2, "bulk-00001099", false, true, "C:\\a.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.Withdrawn);
        }

        Console.WriteLine("-- D5: a reload that leaves holders re-stamps the hold");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            book.Hold(tab, A, false, true, "C:\\a.clw", 100, 10, flip, out e);
            book.Hold(tab, B, false, false, "C:\\a.clw", 100, 10, flip, out e2);
            book.AfterReload(tab, A, true, 500, 50, out still, out ended);
            Ok("A's reload (B still holds) re-stamps with the file as A's reload loaded it", still && e.Ticks == 500 && e.Length == 50);
            Ok("...so B's later release replays NO change the tab already shows", !WatcherHoldBook.Changed(e, true, 500, 50));
            Ok("...but a change after that reload still replays", WatcherHoldBook.Changed(e, true, 600, 60));
            book.Hold(tab, C, false, false, "C:\\a.clw", 1, 1, flip, out e2);
            book.AfterReload(tab, B, false, 999, 99, out still, out ended);
            Ok("a stamp that could not be taken keeps the old one", still && e.Ticks == 500 && e.Length == 50);
            book.DropToken(C, "x");
            object tab2 = new object();
            book.Hold(tab2, "d5-xxxxxxxx", false, true, "C:\\a.clw", 7, 7, flip, out e);
            book.Expire(tab2, e.Gen);
            book.AfterReload(tab2, "nobody-xxxxxxxx", true, 8, 8, out still, out ended);
            Ok("a hold being released is ended by a reload, not re-stamped", !still && ended == e && e.Ticks == 7);
        }

        Console.WriteLine("-- D6: a check waits for a RELEASING hold, then holds afresh");
        {
            var book = new WatcherHoldBook(Cap);
            object tab = new object();
            book.Hold(tab, A, false, true, "C:\\a.clw", 1, 1, flip, out e);
            book.DropToken(A, "refused");
            Ok("while it is releasing a check takes no holder", book.Hold(tab, B, false, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.Releasing);
            long gen = e.Gen;
            var finisher = new Thread(() => { Thread.Sleep(80); book.Finish(tab, gen); });
            finisher.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool done = book.WaitNotReleasing(tab, 2000);
            long waited = sw.ElapsedMilliseconds;
            finisher.Join();
            Ok("the wait returns as soon as the release finishes (not at its limit)", done && waited < 1500, waited + " ms");
            Ok("...and the check then takes a FRESH hold (a new generation)",
               book.Hold(tab, B, false, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.NewHold && e2.Gen != gen);
            book.DropToken(B, "refused");
            sw.Restart();
            done = book.WaitNotReleasing(tab, 150);
            Ok("a release that does not finish in time: the wait gives up at its limit (not held)", !done && sw.ElapsedMilliseconds >= 140, sw.ElapsedMilliseconds + " ms");
            Ok("not releasing (or not held): no wait at all", book.WaitNotReleasing(new object(), 5000));
            book.Forget(tab, e2.Gen);
            Ok("a forgotten release ends the wait too", book.WaitNotReleasing(tab, 10));
        }

        Console.WriteLine("-- the cap is never renewed (SEC MED)");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            var start = now;
            book.Hold(tab, "c1-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e);
            Ok("a new hold is due in the full cap", book.DueMs(e) == Cap);
            now = start.AddSeconds(20);
            book.Hold(tab, "c2-xxxxxxxx", false, false, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("a check 20 s later joins, and the deadline stays at the FIRST hold + 30 s", e2 == e && book.DueMs(e) == 10000, "due " + book.DueMs(e));
            now = start.AddSeconds(29);
            book.Hold(tab, "c3-xxxxxxxx", false, false, "C:\\a.clw", 1, 1, flip, out e2);
            Ok("repeated checks never push it out", book.DueMs(e) == 1000, "due " + book.DueMs(e));
            now = start.AddSeconds(31);
            Ok("past the cap it is due now", book.DueMs(e) == 0);
            var x = book.Expire(tab, e.Gen);
            Ok("the cap releases it whatever the count (3 holders)", x == e && x.Releasing && x.Why.Contains("3 holder"), x != null ? x.Why : "null");
            Ok("a hold being released takes no new holder (F1-3)", book.Hold(tab, "c4-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.Releasing && e2 == null);
            Ok("the release finishes", book.Finish(tab, e.Gen));
            Ok("a capped tab cools down: a check right after takes no hold", book.Hold(tab, "c5-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.CoolingDown && !book.IsHeld(tab));
            now = now.AddSeconds(29);
            Ok("still cooling 29 s on", book.Hold(tab, "c6-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.CoolingDown);
            now = now.AddSeconds(2);
            Ok("after the cool-down a check holds again (a fresh 30 s)", book.Hold(tab, "c7-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e2) == WatcherHoldBook.Take.NewHold && book.DueMs(e2) == Cap);
            object other = new object();
            book.Hold(other, "c8-xxxxxxxx", false, true, "C:\\b.clw", 1, 1, flip, out e);
            book.AfterReload(other, "c8-xxxxxxxx", false, 0, 0, out still, out ended);
            Ok("a hold that ended by its reload does NOT cool the tab down", book.Hold(other, "c9-xxxxxxxx", false, true, "C:\\b.clw", 1, 1, flip, out e) == WatcherHoldBook.Take.NewHold);
        }

        Console.WriteLine("-- generations (F1-3)");
        {
            var book = new WatcherHoldBook(Cap, () => now);
            object tab = new object();
            book.Hold(tab, "g1-xxxxxxxx", false, true, "C:\\a.clw", 1, 1, flip, out e);
            long g1 = e.Gen;
            book.AfterReload(tab, "g1-xxxxxxxx", false, 0, 0, out still, out ended);              // hold 1 ended by its reload
            book.Hold(tab, "g2-xxxxxxxx", false, true, "C:\\a.clw", 2, 2, flip, out e2);   // hold 2
            Ok("each hold gets a new generation", e2.Gen != g1);
            Ok("hold 1's cap timer firing late does nothing to hold 2", book.Expire(tab, g1) == null && !e2.Releasing && book.Holders(tab) == 1);
            Ok("a release posted for hold 1 does nothing to hold 2", !book.Finish(tab, g1) && book.IsHeld(tab));
            Ok("hold 1's holder token does nothing to hold 2 (a stale token is a no-op)",
               book.AfterReload(tab, "g1-xxxxxxxx", false, 0, 0, out still, out ended) && still && book.HasHolder(tab, "g2-xxxxxxxx"));
            var d = book.DropToken("g2-xxxxxxxx", "refused");
            Ok("a release of the current generation acts", d.Count == 1 && d[0] == e2 && book.Finish(tab, e2.Gen) && !book.IsHeld(tab));
            book = new WatcherHoldBook(Cap, () => now);
            book.Hold(tab, "g3-xxxxxxxx", false, true, "C:\\a.clw", 3, 3, flip, out e);
            Ok("a release that was not released (not Releasing) is not finished", !book.Finish(tab, e.Gen) && book.IsHeld(tab));
            var x = book.Expire(tab, e.Gen);
            Ok("a late reload while it is being released ends it, so the stale release is a no-op",
               x == e && book.AfterReload(tab, "g3-xxxxxxxx", false, 0, 0, out still, out ended) && !still && ended == e && !book.Finish(tab, e.Gen));
            // The F1-3 race: hold 1 is capped and its release is on its way to the UI thread; a late reload ends it; hold 2
            // is taken and is itself being released. Hold 1's stale release must not finish hold 2.
            book = new WatcherHoldBook(Cap, () => now);
            book.Hold(tab, "g4-xxxxxxxx", false, true, "C:\\a.clw", 3, 3, flip, out e);
            long stale = e.Gen;
            book.Expire(tab, stale);
            book.AfterReload(tab, "g4-xxxxxxxx", false, 0, 0, out still, out ended);
            now = now.AddMilliseconds(Cap + 1000);   // past the cap's cool-down
            book.Hold(tab, "g5-xxxxxxxx", false, true, "C:\\a.clw", 4, 4, flip, out e2);
            var r2 = book.DropToken("g5-xxxxxxxx", "refused");
            Ok("a stale release (older generation) does not finish a NEWER hold that is being released",
               e2 != null && r2.Count == 1 && r2[0] == e2 && !book.Finish(tab, stale) && book.IsHeld(tab) && book.Finish(tab, e2.Gen));

            book = new WatcherHoldBook(Cap, () => now);
            book.Hold(tab, "g6-xxxxxxxx", false, true, "C:\\a.clw", 3, 3, flip, out e);
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
