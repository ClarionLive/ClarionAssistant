using System;
using System.Collections.Generic;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Single source of truth for the "which CA editor surfaces are active" toggles exposed in the
    /// IDE Options dialog (Options &gt; Clarion Assistant &gt; Editor Surfaces, see
    /// <see cref="ClarionAssistant.Options.CAEditorSurfacesOptionPanel"/>). Backed by the shared
    /// <see cref="SettingsService"/> (%APPDATA%\ClarionAssistant\settings.txt) so the panel, the
    /// editor/embeditor attach code, and the legacy Tools-menu toggle all read/write the SAME keys.
    ///
    /// Values are read FRESH on each get (cheap parse of a tiny file) so a toggle takes effect on the
    /// NEXT file open / right-click — no live teardown of already-open surfaces (decision: ticket
    /// 1c0862e1, John). All reads swallow exceptions and fall back to defaults; these getters run on
    /// hot paths (per file-open, per right-click popup, and on the native message-hook thread), so
    /// they must never throw.
    /// </summary>
    public static class CaEditorSettings
    {
        // Keys live in the shared settings.txt alongside every other CA setting.
        private const string KeySourceEnabled       = "ClarionAssistant.MonacoSourceEnabled";
        private const string KeyEmbeditorEnabled    = "ClarionAssistant.MonacoEmbeditorEnabled";
        private const string KeySourceFileTypes     = "ClarionAssistant.MonacoSourceFileTypes";

        /// <summary>Default extensions the Monaco SOURCE overlay applies to (when enabled).</summary>
        public const string DefaultSourceFileTypes = ".clw;.inc;.equ;.txa";

        // Legacy flag-file the experimental overlay used before unification (Tools-menu toggle wrote
        // it). LocalApplicationData\ClarionAssistant\monaco-overlay.enabled containing "1"/"0".
        // Migrated lazily: when the settings key is absent we fall back to this so a user who had
        // turned the overlay ON via the old Tools menu keeps it on. First Set() (panel save or the
        // Tools toggle) writes the real key and this stops mattering.
        private static string LegacyOverlayFlagPath
        {
            get
            {
                return Path.Combine(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClarionAssistant"),
                    "monaco-overlay.enabled");
            }
        }

        /// <summary>
        /// Master switch for the Monaco SOURCE-editor overlay (replaces the native text editor).
        /// Default OFF (the overlay remains opt-in — decision: John, ticket 1c0862e1). Honors the
        /// legacy flag file when the settings key has never been written.
        /// </summary>
        public static bool MonacoSourceEnabled
        {
            get
            {
                try
                {
                    string raw = new SettingsService().Get(KeySourceEnabled);
                    if (raw == null) return ReadLegacyOverlayFlag();   // default OFF if no legacy flag
                    return ParseBool(raw, false);
                }
                catch { return false; }
            }
            set { TrySet(KeySourceEnabled, value ? "true" : "false"); }
        }

        /// <summary>
        /// Switch for the Monaco EMBEDITOR (the injected right-click "Open in CA Embeditor" item).
        /// Default ON — it is a shipped feature today, so absence of the key means enabled. Turning
        /// it off simply suppresses the injected menu item; the native embeditor is unaffected.
        /// </summary>
        public static bool MonacoEmbeditorEnabled
        {
            get
            {
                try
                {
                    string raw = new SettingsService().Get(KeyEmbeditorEnabled);
                    if (raw == null) return true;   // default ON
                    return ParseBool(raw, true);
                }
                catch { return true; }
            }
            set { TrySet(KeyEmbeditorEnabled, value ? "true" : "false"); }
        }

        /// <summary>
        /// Semicolon-separated list of file extensions the source overlay applies to (e.g.
        /// ".clw;.inc;.equ;.txa"). Empty string means "all files". Default
        /// <see cref="DefaultSourceFileTypes"/>.
        /// </summary>
        public static string MonacoSourceFileTypes
        {
            get
            {
                try
                {
                    string raw = new SettingsService().Get(KeySourceFileTypes);
                    return raw ?? DefaultSourceFileTypes;
                }
                catch { return DefaultSourceFileTypes; }
            }
            set { TrySet(KeySourceFileTypes, value ?? string.Empty); }
        }

        /// <summary>
        /// True if the Monaco source overlay should apply to <paramref name="filePath"/> given the
        /// configured file-type filter. An empty filter (or an unknown/extension-less path) is
        /// treated as "applies" so the master <see cref="MonacoSourceEnabled"/> switch stays the
        /// real gate; only a populated filter with a present extension can exclude a file.
        /// </summary>
        public static bool SourceAppliesTo(string filePath)
        {
            try
            {
                var exts = ParseExtensions(MonacoSourceFileTypes);
                if (exts.Count == 0) return true;                 // "all files"
                if (string.IsNullOrEmpty(filePath)) return true;  // can't filter -> honor master switch
                string ext = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(ext)) return true;       // extension-less -> don't exclude
                return exts.Contains(ext.ToLowerInvariant());
            }
            catch { return true; }
        }

        // ── helpers ──────────────────────────────────────────────────────────

        // Writes never propagate to UI hot paths; SettingsService can throw SettingsLockedException
        // under cross-process contention. Setters are called from the Options panel's StorePanelContents
        // (OK button) and the Tools-menu toggle, where a swallowed write is acceptable (the user can
        // retry); we log via Debug only.
        private static void TrySet(string key, string value)
        {
            try { new SettingsService().Set(key, value); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CaEditorSettings] set '" + key + "' failed: " + ex.Message);
            }
        }

        private static bool ParseBool(string raw, bool fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            raw = raw.Trim();
            if (raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (raw == "0" || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static bool ReadLegacyOverlayFlag()
        {
            try
            {
                string p = LegacyOverlayFlagPath;
                return File.Exists(p) && File.ReadAllText(p).Trim() == "1";
            }
            catch { return false; }
        }

        // Split on ';' (and ',' / whitespace for forgiveness), normalize each token to a lowercase
        // extension starting with '.'.
        private static HashSet<string> ParseExtensions(string raw)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(raw)) return set;
            foreach (var tokRaw in raw.Split(new[] { ';', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tok = tokRaw.Trim().ToLowerInvariant();
                if (tok.Length == 0) continue;
                if (tok[0] != '.') tok = "." + tok;
                set.Add(tok);
            }
            return set;
        }
    }
}
