using System;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #205 review (F1-1..F1-3, SEC MED): the BOOKKEEPING of NativeTabSync's IDE file-watcher holds, kept free of IDE
    /// types so tests\WatcherHoldBook.Test.cs can drive it. NativeTabSync does the watcher flips (Disable / SetWatcher /
    /// the replayed change notice) on the UI thread; this decides WHEN.
    ///
    /// One entry per tab (key = the view):
    ///   - HOLDERS ARE COUNTED (F1-2). Concurrent writes share one hold: each check that holds adds one; each reload
    ///     (WatcherReset), refused reload or explicit release removes one. The watcher is given back only when the count
    ///     reaches 0, or at the cap.
    ///   - THE CAP IS NEVER RENEWED (SEC MED). A hold ends <see cref="CapMs"/> after the FIRST holder took it, however many
    ///     holders join later, so repeated checks cannot keep the IDE's watcher off. A hold that ran into the cap also puts
    ///     the tab on a cool-down of the same length: no new hold until it ends, so back-to-back checks cannot chain holds
    ///     either.
    ///   - A GENERATION per hold (F1-3). A release is posted for one generation and acts only while that generation is
    ///     still the tab's current hold; a hold that is RELEASING takes no new holders (the watcher is about to come back).
    ///   - The STAMP (file time + size) is the FIRST holder's: a release replays the IDE's change notice when the file
    ///     differs from it, so a change made during the hold is never swallowed.
    /// Thread-safe: every method locks.
    /// </summary>
    public sealed class WatcherHoldBook
    {
        public sealed class Entry
        {
            public object Key;
            public long Gen;
            public int Count;
            public DateTime FirstUtc;
            public bool Releasing;
            /// <summary>Why it is being released (for the log).</summary>
            public string Why;
            /// <summary>The file as it was when the FIRST holder took the hold.</summary>
            public string StatPath;
            public long Ticks, Length;
            /// <summary>Host-private (NativeTabSync keeps its expiry timer here).</summary>
            public object Tag;
        }

        public enum Take { NewHold, Joined, NotWatching, Releasing, CoolingDown }

        public readonly int CapMs;
        private readonly Func<DateTime> _now;
        private readonly object _lock = new object();
        private readonly Dictionary<object, Entry> _holds = new Dictionary<object, Entry>();
        private readonly Dictionary<object, DateTime> _coolUntil = new Dictionary<object, DateTime>();
        private long _gen;

        public WatcherHoldBook(int capMs, Func<DateTime> now = null)
        {
            CapMs = capMs;
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// A check wants to hold <paramref name="key"/>'s watcher. <paramref name="watching"/>: the watcher is armed now.
        /// <paramref name="flip"/> runs UNDER THE BOOK'S LOCK when the caller must disable the watcher (a new hold, or a
        /// joined hold whose watcher the IDE re-armed meanwhile, say by a save); if it throws, nothing is recorded.
        /// A joined hold keeps its first stamp and its deadline.
        /// </summary>
        public Take Hold(object key, bool watching, string statPath, long ticks, long length, Action flip, out Entry entry)
        {
            lock (_lock)
            {
                entry = null;
                Entry h;
                if (_holds.TryGetValue(key, out h))
                {
                    if (h.Releasing) return Take.Releasing;
                    if (watching && flip != null) flip();
                    h.Count++;
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
                    Key = key, Gen = ++_gen, Count = 1, FirstUtc = _now(),
                    StatPath = statPath, Ticks = ticks, Length = length, Why = "it reached the " + (CapMs / 1000) + " s cap"
                };
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
        /// A holder is done without a reload (its write was refused or failed, its reload did not happen, an explicit
        /// release). Returns the entry when that was the last holder: the caller then releases it (it is marked Releasing).
        /// </summary>
        public Entry Drop(object key, string why)
        {
            lock (_lock)
            {
                Entry h;
                if (key == null || !_holds.TryGetValue(key, out h) || h.Releasing) return null;
                if (--h.Count > 0) return null;
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
        /// Right after OUR reload (Load re-armed the watcher and the tab now shows the file): one holder fewer. The hold
        /// ends (no replay - the tab has just loaded the file) when that was the last holder, or when it was already being
        /// released. True with <paramref name="stillHeld"/> when other holders remain: the caller disables the watcher again.
        /// </summary>
        public bool AfterReload(object key, out bool stillHeld, out Entry ended)
        {
            lock (_lock)
            {
                stillHeld = false; ended = null;
                Entry h;
                if (key == null || !_holds.TryGetValue(key, out h)) return false;
                if (!h.Releasing && --h.Count > 0) { stillHeld = true; return true; }
                _holds.Remove(key);
                ended = h;
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
                return true;
            }
        }

        /// <summary>A release that could not be posted at all: forget generation <paramref name="gen"/>.</summary>
        public void Forget(object key, long gen)
        {
            lock (_lock)
            {
                Entry h;
                if (key != null && _holds.TryGetValue(key, out h) && h.Gen == gen) _holds.Remove(key);
            }
        }

        /// <summary>Holders of <paramref name="key"/> (0 when not held). For logs and tests.</summary>
        public int Holders(object key)
        {
            lock (_lock) { Entry h; return key != null && _holds.TryGetValue(key, out h) ? h.Count : 0; }
        }

        public bool IsHeld(object key) { lock (_lock) { return key != null && _holds.ContainsKey(key); } }

        /// <summary>Did the file change since the hold began (a stamp that cannot be read counts as changed).</summary>
        public static bool Changed(Entry e, bool stampOk, long ticks, long length)
        {
            return !stampOk || ticks != e.Ticks || length != e.Length;
        }
    }
}
