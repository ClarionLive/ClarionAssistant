using System;
using ICSharpCode.Core;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #126: reads the Clarion IDE's own Options → Text Editor → Behavior indentation values so the
    /// CA Monaco surfaces (and their in-page Smart Formatter, which takes tabSize/insertSpaces from the
    /// live Monaco model) follow what the developer already configured instead of assuming 4.
    ///
    /// Source of truth is the fork's PropertyService container "TextEditorSettings" (verified against
    /// %APPDATA%\SoftVelocity\Clarion\12.0\ClarionProperties.xml):
    ///   TabIndent        — display width of a tab character,
    ///   IndentationSize  — the indent unit,
    ///   TabsToSpaces     — Convert Tabs to Spaces.
    /// Monaco has a single tabSize driving both display and indent, so the mapping picks the value that
    /// governs what actually lands in the buffer: IndentationSize when indenting with spaces, TabIndent
    /// when real tabs are inserted (their display width is what you see and align to).
    ///
    /// Reads are live (PropertyService is the IDE's in-memory store, updated when the Options dialog is
    /// OK'd), so callers should re-read per payload rather than cache.
    /// </summary>
    internal static class IdeEditorOptions
    {
        /// <summary>Read the IDE's effective Monaco-shaped pair. False (and untouched outs) when the
        /// property bundle can't be read — callers then just omit the IDE values.</summary>
        public static bool TryRead(out int tabSize, out bool insertSpaces)
        {
            tabSize = 4;
            insertSpaces = false;
            try
            {
                var tes = PropertyService.Get<Properties>("TextEditorSettings", null);
                if (tes == null) return false;
                int tabIndent = Clamp(tes.Get("TabIndent", 4), 1, 16);
                int indentSize = Clamp(tes.Get("IndentationSize", 4), 1, 16);
                bool toSpaces = tes.Get("TabsToSpaces", false);
                tabSize = toSpaces ? indentSize : tabIndent;
                insertSpaces = toSpaces;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[IdeEditorOptions] read failed: " + ex.Message);
                return false;
            }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
