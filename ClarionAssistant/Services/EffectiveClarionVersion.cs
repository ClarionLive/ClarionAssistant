using System;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The Clarion version EVERY CA component uses — the Assistant panel's VERSION/RED, the CodeGraph
    /// indexer, the LSP start, the ClarionGraph library root and key, the Data pad and the Explorer header
    /// (16d140e9).
    ///
    /// Before this, each consumer asked ClarionVersionService.Detect().GetCurrentConfig() on its own, and
    /// only the panel and the LSP applied CA's saved VERSION override — so with an override set, the panel,
    /// indexer and LSP used one version while the library graph, Data pad and Explorer header used the IDE's.
    /// And the override itself was one global value that beat the IDE's Build &gt; Set Clarion Version forever.
    ///
    /// The override is now stored PER SOLUTION (the IDE keeps its own choice per solution too) as one record
    /// holding the chosen name and the IDE choice it was made against. Resolution only READS it — it is applied
    /// or suspended, never deleted — and only the panel's VERSION change / refresh button write it. The rules
    /// live in <see cref="ClarionVersionSelector"/> (pure, harnessed).
    /// </summary>
    public static class EffectiveClarionVersion
    {
        /// <summary>
        /// The IDE's open solution, for the per-solution override key. Set by the addin; unset (null) in the
        /// standalone server, where only the legacy global value is read.
        /// </summary>
        public static Func<string> SolutionPathProvider;

        private static int _generation;

        /// <summary>
        /// Bumped by the panel whenever the effective version (or the tier that chose it) changes. A cheap
        /// in-memory signal for watchers such as the Data pad's environment key — resolving is an XML parse.
        /// </summary>
        public static int Generation { get { return Volatile.Read(ref _generation); } }

        public static void NotifyChanged() { Interlocked.Increment(ref _generation); }

        private static string CurrentSolution()
        {
            try { return SolutionPathProvider != null ? SolutionPathProvider() : null; }
            catch { return null; }
        }

        /// <summary>Detect and select. Never throws; the selection's Config is null when nothing is detected.</summary>
        public static ClarionVersionSelection Resolve()
        {
            ClarionVersionInfo info = null;
            try { info = ClarionVersionService.Detect(); } catch { }
            return Resolve(info);
        }

        /// <summary>Select from an already-detected <paramref name="info"/> for the open solution. Read-only.</summary>
        public static ClarionVersionSelection Resolve(ClarionVersionInfo info)
        {
            string record = null, legacy = null;
            try
            {
                var settings = new SettingsService();   // fresh: reads settings.txt now, not a cached copy
                record = settings.Get(ClarionVersionSelector.OverrideKeyFor(CurrentSolution()));
                legacy = settings.Get(ClarionVersionSelector.LegacyOverrideKey);
            }
            catch { }
            return ClarionVersionSelector.SelectForSolution(info, record, legacy);
        }

        /// <summary>The effective version's config, or null.</summary>
        public static ClarionVersionConfig CurrentConfig()
        {
            return Resolve().Config;
        }

        /// <summary>
        /// Save the developer's VERSION dropdown choice for the open solution, against the IDE's current
        /// choice, as ONE record in one Set. Choosing what the IDE already resolves to clears the record.
        /// Only the panel's own VERSION change calls this.
        /// </summary>
        public static void SaveOverride(string versionName, ClarionVersionInfo info)
        {
            try
            {
                string key = ClarionVersionSelector.OverrideKeyFor(CurrentSolution());
                ClarionVersionTier ideTier;
                var ideConfig = info != null ? info.ResolveIdeChoice(out ideTier) : null;
                bool same = string.IsNullOrEmpty(versionName) || (ideConfig != null && ideConfig.Name == versionName);
                new SettingsService().Set(key, same ? ""
                    : ClarionVersionSelector.EncodeOverride(versionName, ClarionVersionSelector.IdeChoiceKey(info)));
            }
            catch { }
        }

        /// <summary>Forget the VERSION dropdown choice for the open solution (the panel's refresh button), and
        /// the legacy global one, which would otherwise still apply while the IDE is on "Current".</summary>
        public static void ClearOverride()
        {
            try
            {
                var settings = new SettingsService();
                settings.Set(ClarionVersionSelector.OverrideKeyFor(CurrentSolution()), "");
                settings.Set(ClarionVersionSelector.LegacyOverrideKey, "");
            }
            catch { }
        }
    }
}
