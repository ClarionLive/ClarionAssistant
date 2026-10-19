using System;
using System.Collections.Generic;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #205 review (F1-1..F1-3, SEC MED): the BOOKKEEPING of NativeTabSync's IDE file-watcher holds, kept free of IDE
    /// types so tests\WatcherHoldBook.Test.cs can drive it. NativeTabSync does the watcher flips (Disable / SetWatcher /
    /// the replayed change notice) on the UI thread; this decides WHEN.
    ///
    /// One entry per tab (key = the view):
    ///   - HOLDERS ARE TOKENS (re-review D4, PM ruling). Every write-path check that holds is one holder, named by its
    ///     TOKEN: the writer's request id (hold_id), or one this IDE made up for an older standalone that sent none
    ///     (an ANONYMOUS holder). A reload or release names its token and drops ONLY that holder, and only while it is in
    ///     the tab's current hold (a new hold starts with no holders, so a token from an older generation is a no-op). An
    ///     unknown token is a silent no-op: a writer that never held can never drop another writer's hold. A reload
    ///     WITHOUT a token (an older standalone) drops one ANONYMOUS holder, never a token holder. The same token twice is
    ///     one holder.
    ///   - A RELEASED TOKEN IS WITHDRAWN (D1): once a writer released (or its reload failed), a hold for that token that
    ///     reaches the book LATE - its check had timed out on the writer's side, so the writer released by request id
    ///     before the hold landed - is refused, so it cannot be left behind until the cap.
    ///   - THE CAP IS NEVER RENEWED (SEC MED). A hold ends <see cref="CapMs"/> after the FIRST holder took it, however many
    ///     holders join later, so repeated checks cannot keep the IDE's watcher off. A hold that ran into the cap also puts
    ///     the tab on a cool-down of the same length: no new hold until it ends, so back-to-back checks cannot chain holds
    ///     either.
    ///   - A GENERATION per hold (F1-3). A release is posted for one generation and acts only while that generation is
    ///     still the tab's current hold; a hold that is RELEASING takes no new holders (the watcher is about to come back);
    ///     a check can wait briefly for that release to finish (<see cref="WaitNotReleasing"/>, D6) and then hold afresh.
    ///   - The STAMP (file time + size) is the FIRST holder's; a reload that leaves other holders re-stamps it with the file
    ///     as that reload loaded it (D5). A release replays the IDE's change notice when the file differs from it, so a
    ///     change made during the hold is never swallowed, and one the tab already shows is not replayed.
    /// Thread-safe: every method locks.
    /// </summary>
    public sealed class WatcherHoldBook
    {
        public sealed class Entry
        {
            public object Key;
            public long Gen;
            /// <summary>The holders' tokens; the value is true for an ANONYMOUS holder (no hold_id was sent).</summary>
            public readonly Dictionary<string, bool> Holders = new Dictionary<string, bool>(StringComparer.Ordinal);
            public int Count { get { return Holders.Count; } }
            public DateTime FirstUtc;
            public bool Releasing;
            /// <summary>Why it is being released (for the log).</summary>
            public string Why;
            /// <summary>The file as it was when the FIRST holder took the hold (or as the last reload loaded it, D5).</summary>
            public string StatPath;
            public long Ticks, Length;
            /// <summary>Host-private (NativeTabSync keeps its expiry timer here).</summary>
            public object Tag;
        }

        public enum Take { NewHold, Joined, NotWatching, Releasing, CoolingDown, Withdrawn, NoToken }

        public readonly int CapMs;
        private readonly Func<DateTime> _now;
        private readonly object _lock = new object();
        private readonly Dictionary<object, Entry> _holds = new Dictionary<object, Entry>();
        private readonly Dictionary<object, DateTime> _coolUntil = new Dictionary<object, DateTime>();
        // D1: tokens whose writer is done (released, or its reload did not happen), so a hold that lands late is refused.
        private readonly Dictionary<string, DateTime> _withdrawn = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private const int MaxWithdrawn = 1024;
        private long _gen;

        public WatcherHoldBook(int capMs, Func<DateTime> now = null)
        {
            CapMs = capMs;
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// A check wants to hold <paramref name="key"/>'s watcher for holder <paramref name="token"/> (never empty;
        /// <paramref name="anonymous"/>: this IDE made it up, the writer sent no hold_id). <paramref name="watching"/>: the
        /// watcher is armed now. <paramref name="flip"/> runs UNDER THE BOOK'S LOCK when the caller must disable the watcher
        /// (a new hold, or a joined hold whose watcher the IDE re-armed meanwhile, say by a save); if it throws, nothing is
        /// recorded. A joined hold keeps its stamp and its deadline.
        /// </summary>
        public Take Hold(object key, string token, bool anonymous, bool watching, string statPath, long ticks, long length, Action flip, out Entry entry)
        {
            lock (_lock)
            {
                entry = null;
                if (string.IsNullOrEmpty(token)) return Take.NoToken;
                if (IsWithdrawnLocked(token)) return Take.Withdrawn;
                Entry h;
                if (_holds.TryGetValue(key, out h))
                {
                    if (h.Releasing) return Take.Releasing;
                    if (watching && flip != null) flip();
                    h.Holders[token] = anonymous;   // the same token twice is one holder
                    entry = h;
                    return Take.Joined;
                }
                DateTime until;
                if (_coolUntil.TryGetValue(key, out until))
                {
                    if (_now() < until) return Take.CoolingDown;
                    _coolUntil.Remove(key);
                }
                if (!watching) return Take.NotWatching;
                if (flip != null) flip();
                h = new Entry
                {
                    Key = key, Gen = ++_gen, FirstUtc = _now(),
                    StatPath = statPath, Ticks = ticks, Length = length, Why = "it reached the " + (CapMs / 1000) + " s cap"
                };
                h.Holders[token] = anonymous;
                _holds[key] = h;
                entry = h;
                return Take.NewHold;
            }
        }

        /// <summary>Milliseconds until <paramref name="e"/>'s cap (never more than <see cref="CapMs"/>, never below 0).</summary>
        public int DueMs(Entry e)
        {
            double left = CapMs - (_now() - e.FirstUtc).TotalMilliseconds;
            return left <= 0 ? 0 : (int)Math.Min(CapMs, Math.Ceiling(left));
        }

        /// <summary>
        /// Holder <paramref name="token"/> is done without a reload (its write was refused or failed, its reload did not
        /// happen, an explicit release): drop it from EVERY tab it holds - no view is needed, so this works when the tabs
        /// could not even be listed (D2) - and withdraw the token (D1: a hold for it that lands later is refused). Returns
        /// the entries whose LAST holder that was: the caller releases them (they are marked Releasing). An unknown or
        /// empty token drops nothing.
        /// </summary>
        public List<Entry> DropToken(string token, string why)
        {
            var last = new List<Entry>();
            lock (_lock)
            {
                if (string.IsNullOrEmpty(token)) return last;
                Withdraw(token);
                foreach (var h in _holds.Values)
                {
                    if (h.Releasing || !h.Holders.Remove(token) || h.Holders.Count > 0) continue;
                    h.Releasing = true;
                    h.Why = why;
                    last.Add(h);
                }
            }
            return last;
        }

        /// <summary>An older standalone's refused reload (no token): drop ONE anonymous holder of <paramref name="key"/>,
        /// never a token holder. Returns the entry when that was its last holder (marked Releasing).</summary>
        public Entry DropAnonymous(object key, string why)
        {
            lock (_lock)
            {
                Entry h;
                if (key == null || !_holds.TryGetValue(key, out h) || h.Releasing) return null;
                string anon = FirstAnonymous(h);
                if (anon == null) return null;
                h.Holders.Remove(anon);
                if (h.Holders.Count > 0) return null;
                h.Releasing = true;
                h.Why = why;
                return h;
            }
        }

        /// <summary>The cap timer of generation <paramref name="gen"/> fired: release it whatever the count, and cool the
        /// tab down. Null when that generation is no longer the tab's hold, or is already being released.</summary>
        public Entry Expire(object key, long gen)
        {
            lock (_lock)
            {
                Entry h;
                if (key == null || !_holds.TryGetValue(key, out h) || h.Gen != gen || h.Releasing) return null;
                h.Releasing = true;
                h.Why = "it reached the " + (CapMs / 1000) + " s cap with " + h.Count + " holder(s) left";
                _coolUntil[key] = _now().AddMilliseconds(CapMs);
                return h;
            }
        }

        /// <summary>
        /// Right after a reload of <paramref name="key"/> (Load re-armed the watcher and the tab now shows the file).
        /// <paramref name="token"/>: the reloading writer's; null for an older standalone (drops one ANONYMOUS holder). A
        /// token that is not a holder here drops nothing (D4). The hold ends (no replay - the tab has just loaded the file)
        /// when no holder is left, or when it was already being released. True with <paramref name="stillHeld"/> when other
        /// holders remain: the caller disables the watcher again, and the stamp becomes <paramref name="ticks"/>/<paramref
        /// name="length"/> (D5: taken BEFORE the Load, so it is never newer than what the tab shows) when
        /// <paramref name="stampOk"/>. False when the tab was not held.
        /// </summary>
        public bool AfterReload(object key, string token, bool stampOk, long ticks, long length, out bool stillHeld, out Entry ended)
        {
            lock (_lock)
            {
                stillHeld = false; ended = null;
                Entry h;
                if (key == null || !_holds.TryGetValue(key, out h)) return false;
                if (token != null) { if (token.Length > 0) { h.Holders.Remove(token); Withdraw(token); } }
                else { string anon = FirstAnonymous(h); if (anon != null) h.Holders.Remove(anon); }
                if (!h.Releasing && h.Holders.Count > 0)
                {
                    stillHeld = true;
                    if (stampOk) { h.Ticks = ticks; h.Length = length; }
                    return true;
                }
                _holds.Remove(key);
                ended = h;
                Monitor.PulseAll(_lock);
                return true;
            }
        }

        /// <summary>The release of generation <paramref name="gen"/> reached the UI thread: true (and the entry is gone)
        /// only when that generation is still the tab's hold and is being released. Else it is stale: do nothing.</summary>
        public bool Finish(object key, long gen)
        {
            lock (_lock)
            {
                Entry h;
                if (key == null || !_holds.TryGetValue(key, out h) || h.Gen != gen || !h.Releasing) return false;
                _holds.Remove(key);
                Monitor.PulseAll(_lock);
                return true;
            }
        }

        /// <summary>A release that could not be posted at all: forget generation <paramref name="gen"/>.</summary>
        public void Forget(object key, long gen)
        {
            lock (_lock)
            {
                Entry h;
                if (key != null && _holds.TryGetValue(key, out h) && h.Gen == gen) { _holds.Remove(key); Monitor.PulseAll(_lock); }
            }
        }

        /// <summary>
        /// D6: a check found <paramref name="key"/>'s hold RELEASING (its watcher is about to come back). Wait up to
        /// <paramref name="ms"/> for that release to finish (Finish / Forget / a reload ending it) so the check can take a
        /// fresh hold. Call OFF the UI thread: the release itself runs there. True when the tab is no longer releasing.
        /// </summary>
        public bool WaitNotReleasing(object key, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            lock (_lock)
            {
                while (true)
                {
                    Entry h;
                    if (key == null || !_holds.TryGetValue(key, out h) || !h.Releasing) return true;
                    int left = ms - (int)sw.ElapsedMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(_lock, left);
                }
            }
        }

        /// <summary>Holders of <paramref name="key"/> (0 when not held). For logs and tests.</summary>
        public int Holders(object key)
        {
            lock (_lock) { Entry h; return key != null && _holds.TryGetValue(key, out h) ? h.Count : 0; }
        }

        /// <summary>Is <paramref name="token"/> one of <paramref name="key"/>'s holders. For tests.</summary>
        public bool HasHolder(object key, string token)
        {
            lock (_lock) { Entry h; return key != null && token != null && _holds.TryGetValue(key, out h) && h.Holders.ContainsKey(token); }
        }

        public bool IsHeld(object key) { lock (_lock) { return key != null && _holds.ContainsKey(key); } }

        public bool IsReleasing(object key) { lock (_lock) { Entry h; return key != null && _holds.TryGetValue(key, out h) && h.Releasing; } }

        /// <summary>The held entry's stamp path (null when not held). For the reload's re-stamp (D5).</summary>
        public string StatPathOf(object key) { lock (_lock) { Entry h; return key != null && _holds.TryGetValue(key, out h) ? h.StatPath : null; } }

        /// <summary>Did the file change since the hold began (a stamp that cannot be read counts as changed).</summary>
        public static bool Changed(Entry e, bool stampOk, long ticks, long length)
        {
            return !stampOk || ticks != e.Ticks || length != e.Length;
        }

        private static string FirstAnonymous(Entry h)
        {
            foreach (var kv in h.Holders) if (kv.Value) return kv.Key;
            return null;
        }

        // Under the lock. A withdrawn token outlives any hold it could still race with (twice the cap), then is forgotten.
        private bool IsWithdrawnLocked(string token)
        {
            DateTime until;
            if (!_withdrawn.TryGetValue(token, out until)) return false;
            if (_now() < until) return true;
            _withdrawn.Remove(token);
            return false;
        }

        private void Withdraw(string token)
        {
            DateTime now = _now();
            if (_withdrawn.Count >= MaxWithdrawn)
            {
                var stale = new List<string>();
                foreach (var kv in _withdrawn) if (kv.Value <= now) stale.Add(kv.Key);
                foreach (var k in stale) _withdrawn.Remove(k);
                if (_withdrawn.Count >= MaxWithdrawn)
                {
                    // Still full: forget the one due soonest (a late hold for it is then bounded by the cap, as before D1).
                    string soonest = null; DateTime best = DateTime.MaxValue;
                    foreach (var kv in _withdrawn) if (kv.Value < best) { best = kv.Value; soonest = kv.Key; }
                    if (soonest != null) _withdrawn.Remove(soonest);
                }
            }
            _withdrawn[token] = now.AddMilliseconds(2.0 * CapMs);
        }
    }
}
