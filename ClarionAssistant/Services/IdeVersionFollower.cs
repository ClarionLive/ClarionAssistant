using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Notices the IDE's Build &gt; Set Clarion Version moving (905928c7). The only reaction to it used to be
    /// AssistantChatControl.SyncVersionWithIde, so with no CA chat tab a version switch left the language
    /// server and the .red on the old version. LspAutostartCommand owns one of these from addin start and
    /// feeds it from both the Clarion.Version PropertyChanged event and its 5 s tick.
    ///
    /// The first observation only SEEDS: at addin start nothing is running on a version yet, and whatever
    /// starts next resolves the current one itself. Every later observation that differs (after
    /// <see cref="ClarionVersionSelector.NormalizeIdeChoice"/>, case-insensitive) fires
    /// <see cref="Changed"/> once. Pure (no IDE types), so tests\IdeVersionFollower.Test.cs drives it directly.
    /// </summary>
    public sealed class IdeVersionFollower
    {
        private readonly object _lock = new object();
        private string _last;
        private bool _seeded;

        /// <summary>Raised with (previous, now), both normalized, once per observed change.</summary>
        public event Action<string, string> Changed;

        /// <summary>The last normalized IDE choice seen, or null before the first observation.</summary>
        public string Last { get { lock (_lock) return _last; } }

        /// <summary>
        /// Record the IDE's live Build &gt; Set Clarion Version (raw, as ClarionVersionService reads it; empty
        /// or "(Current Version)" means the running version). True when it changed and <see cref="Changed"/>
        /// was raised.
        /// </summary>
        public bool Observe(string liveIdeChoice)
        {
            string now = ClarionVersionSelector.NormalizeIdeChoice(liveIdeChoice);
            string was;
            lock (_lock)
            {
                if (!_seeded)
                {
                    _seeded = true;
                    _last = now;
                    return false;
                }
                if (string.Equals(now, _last, StringComparison.OrdinalIgnoreCase)) return false;
                was = _last;
                _last = now;
            }
            var h = Changed;
            if (h != null) h(was, now);
            return true;
        }
    }
}
