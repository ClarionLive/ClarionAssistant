using System;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The Clarion version EVERY CA component uses — the Assistant panel's VERSION/RED, the CodeGraph
    /// indexer, the LSP start, the ClarionGraph library root and key, the Data pad and the Explorer header
    /// (16d140e9).
    ///
    /// It is the IDE's Build &gt; Set Clarion Version and nothing else (286f2e57): CA DISPLAYS the version and
    /// has no picker of its own. Before, CA's VERSION dropdown saved an override (GH #32, per solution since
    /// 16d140e9) that could outrank the IDE, and a legacy one written by another IDE made a Clarion 12 IDE on
    /// "(Current Version)" show "Clarion 10 Active And Updated (saved)". The rules live in
    /// <see cref="ClarionVersionSelector"/> (pure, harnessed).
    /// </summary>
    public static class EffectiveClarionVersion
    {
        private static int _generation;

        /// <summary>
        /// Bumped by the panel whenever the effective version (or the tier that chose it) changes. A cheap
        /// in-memory signal for watchers such as the Data pad's environment key — resolving is an XML parse.
        /// </summary>
        public static int Generation { get { return Volatile.Read(ref _generation); } }

        public static void NotifyChanged() { Interlocked.Increment(ref _generation); }

        /// <summary>Detect and select. Never throws; the selection's Config is null when nothing is detected.</summary>
        public static ClarionVersionSelection Resolve()
        {
            ClarionVersionInfo info = null;
            try { info = ClarionVersionService.Detect(); } catch { }
            return Resolve(info);
        }

        /// <summary>Select from an already-detected <paramref name="info"/>: the IDE's choice. Read-only.</summary>
        public static ClarionVersionSelection Resolve(ClarionVersionInfo info)
        {
            return ClarionVersionSelector.Select(info);
        }

        /// <summary>The effective version's config, or null.</summary>
        public static ClarionVersionConfig CurrentConfig()
        {
            return Resolve().Config;
        }

        private static int _retiredOverridesDeleted;

        /// <summary>
        /// Delete CA's retired VERSION overrides from settings.txt — the legacy global key and every per-solution
        /// record — once per process, and log what went. Nothing reads them any more; deleting them means an
        /// older CA build sharing settings.txt cannot resurrect a stale choice later either.
        /// </summary>
        public static void DeleteRetiredOverridesOnce()
        {
            if (Interlocked.Exchange(ref _retiredOverridesDeleted, 1) != 0) return;
            try
            {
                var removed = new SettingsService().RemoveWhere(ClarionVersionSelector.StaleOverrideKeys);
                if (removed.Count > 0)
                    LspTrace.Write("[EffectiveClarionVersion] deleted " + removed.Count
                        + " retired CA VERSION override(s) - CA now shows the IDE's Build > Set Clarion Version only: "
                        + string.Join(", ", removed));
            }
            catch (Exception ex)
            {
                LspTrace.Write("[EffectiveClarionVersion] could not delete the retired VERSION overrides: " + ex.Message);
            }
        }
    }
}
