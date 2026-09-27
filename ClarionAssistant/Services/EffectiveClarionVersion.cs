using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The Clarion version EVERY CA component uses — the Assistant panel's VERSION/RED, the CodeGraph
    /// indexer, the LSP start, the ClarionGraph library root, the Data pad and the Explorer header (16d140e9).
    ///
    /// Before this, each consumer asked ClarionVersionService.Detect().GetCurrentConfig() on its own, and
    /// only the panel and the LSP applied CA's saved VERSION override — so with an override set, the panel,
    /// indexer and LSP used one version while the library graph, Data pad and Explorer header used the IDE's.
    /// And the override itself was one global value that beat the IDE's Build &gt; Set Clarion Version forever.
    ///
    /// The rule lives in <see cref="ClarionVersionSelector"/> (pure, harnessed); this class only reads and
    /// writes the two settings keys around it.
    /// </summary>
    public static class EffectiveClarionVersion
    {
        /// <summary>CA's VERSION dropdown choice (GH #32). Kept as the same key so existing choices carry over.</summary>
        public const string OverrideKey = "Clarion.Version.Override";

        /// <summary>The IDE's Build &gt; Set Clarion Version choice at the moment the override was saved.</summary>
        public const string OverrideBasisKey = "Clarion.Version.Override.IdeBasis";

        /// <summary>Detect and select. Never throws; the selection's Config is null when nothing is detected.</summary>
        public static ClarionVersionSelection Resolve()
        {
            ClarionVersionInfo info = null;
            try { info = ClarionVersionService.Detect(); } catch { }
            return Resolve(info);
        }

        /// <summary>
        /// Select from an already-detected <paramref name="info"/>. Persists what the selection says about the
        /// saved override (drop it / record its basis) — but ONLY when the IDE's choice was read live: outside
        /// the IDE (standalone MCP server) the XML's choice may be stale, and must not clear a developer's
        /// choice made in the IDE.
        /// </summary>
        public static ClarionVersionSelection Resolve(ClarionVersionInfo info)
        {
            SettingsService settings = null;
            string overrideName = null, basis = null;
            try
            {
                settings = new SettingsService();   // fresh: reads settings.txt now, not a cached copy
                overrideName = settings.Get(OverrideKey);
                basis = settings.Get(OverrideBasisKey);
            }
            catch { }

            var sel = ClarionVersionSelector.Select(info, overrideName, basis);

            if (settings != null && info != null && info.CurrentVersionFromLiveIde)
            {
                try
                {
                    if (sel.OverrideAction == SavedOverrideAction.Clear)
                    {
                        settings.Set(OverrideKey, "");
                        settings.Set(OverrideBasisKey, "");
                        LspTrace.Write("[EffectiveClarionVersion] " + sel.Note);
                        System.Diagnostics.Debug.WriteLine("[EffectiveClarionVersion] " + sel.Note);
                    }
                    else if (sel.OverrideAction == SavedOverrideAction.RecordBasis)
                    {
                        settings.Set(OverrideBasisKey, sel.OverrideBasisToRecord ?? "");
                    }
                }
                catch { }
            }
            return sel;
        }

        /// <summary>The effective version's config, or null.</summary>
        public static ClarionVersionConfig CurrentConfig()
        {
            return Resolve().Config;
        }

        /// <summary>
        /// Save the developer's VERSION dropdown choice against the IDE's current choice. Choosing what the
        /// IDE already resolves to is not an override at all, so it clears any saved one instead.
        /// </summary>
        public static void SaveOverride(string versionName, ClarionVersionInfo info)
        {
            try
            {
                var settings = new SettingsService();
                ClarionVersionTier ideTier;
                var ideConfig = info != null ? info.ResolveIdeChoice(out ideTier) : null;
                if (string.IsNullOrEmpty(versionName) || (ideConfig != null && ideConfig.Name == versionName))
                {
                    settings.Set(OverrideKey, "");
                    settings.Set(OverrideBasisKey, "");
                    return;
                }
                settings.Set(OverrideKey, versionName);
                settings.Set(OverrideBasisKey,
                    ClarionVersionSelector.NormalizeIdeChoice(info != null ? info.CurrentVersionName : null));
            }
            catch { }
        }

        /// <summary>Forget the VERSION dropdown choice (the panel's refresh button).</summary>
        public static void ClearOverride()
        {
            try
            {
                var settings = new SettingsService();
                settings.Set(OverrideKey, "");
                settings.Set(OverrideBasisKey, "");
            }
            catch { }
        }
    }
}
