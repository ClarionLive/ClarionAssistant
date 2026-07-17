using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Single source of truth for WHICH Find/Replace UI answers Ctrl+F / Ctrl+H in the CA editors
    /// (GitHub #66 phase 2): the dockable CA Find/Replace PAD (default — the shipped 5.3.773
    /// behavior) or the in-editor OVERLAY (top-right quick find + Find-All left column, PR #104).
    /// Both front-ends drive the SAME match/replace engine in monaco-embeditor.html; this setting
    /// only picks the presentation. Exposed in Options &gt; Clarion Assistant &gt; Find / Replace
    /// (<see cref="ClarionAssistant.Options.CAFindOptionPanel"/>).
    ///
    /// Backed by the shared <see cref="SettingsService"/> (%APPDATA%\ClarionAssistant\settings.txt),
    /// same conventions as <see cref="CaEditorSettings"/>: values are read FRESH per get (the value
    /// rides into each editor page inside setSource, so a change takes effect on the NEXT editor /
    /// embeditor open — no live re-skin of already-open surfaces), reads swallow exceptions and
    /// fall back to the default, and never throw (read on the page-load path of every CA editor).
    /// </summary>
    public static class CaFindSettings
    {
        private const string KeyFindUiMode = "ClarionAssistant.FindUiMode";

        public const string ModePad = "Pad";
        public const string ModeOverlay = "Overlay";

        /// <summary>
        /// "Pad" (default) or "Overlay". Unknown/absent values normalize to Pad so a hand-edited
        /// settings.txt can never leave Ctrl+F answering to nothing.
        /// </summary>
        public static string FindUiMode
        {
            get
            {
                try
                {
                    string raw = new SettingsService().Get(KeyFindUiMode);
                    return string.Equals(raw, ModeOverlay, StringComparison.OrdinalIgnoreCase)
                        ? ModeOverlay : ModePad;
                }
                catch { return ModePad; }
            }
            set
            {
                try
                {
                    new SettingsService().Set(KeyFindUiMode,
                        string.Equals(value, ModeOverlay, StringComparison.OrdinalIgnoreCase)
                            ? ModeOverlay : ModePad);
                }
                catch { /* never throw from a settings write; next read falls back to Pad */ }
            }
        }

        /// <summary>Convenience for the hosts building setSource payloads ("pad"/"overlay", page-side casing).</summary>
        public static string FindUiModeForPage
        {
            get { return FindUiMode == ModeOverlay ? "overlay" : "pad"; }
        }
    }
}
