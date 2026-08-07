using System;
using System.Globalization;
using System.Text.RegularExpressions;
using ICSharpCode.Core;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Monaco-shaped snapshot of the Clarion IDE's Options → Text Editor settings. Every field is already
    /// translated into the value Monaco expects, so callers ship it straight to the page without knowing
    /// anything about SharpDevelop's property names or enums.
    /// </summary>
    internal sealed class IdeEditorOptionValues
    {
        // GH #126 (indentation) — the original pair, semantics unchanged.
        public int TabSize = 4;
        public bool InsertSpaces = false;

        // "Can move caret behind EOL". Monaco has no virtual space, so this one is NOT an editor option —
        // the page emulates it. Shipped as a plain flag; see monaco-embeditor.html's pad/trim engine.
        public bool CursorBehindEOL = false;

        public string LineNumbers = "on";            // lineNumbers: 'on' | 'off'
        public bool Folding = true;                  // folding
        public string RenderWhitespace = "none";     // renderWhitespace: 'none' | 'all'
        public bool MouseWheelZoom = true;           // mouseWheelZoom
        public int[] Rulers = new int[0];            // rulers: [] | [column]
        public string MatchBrackets = "always";      // matchBrackets: 'always' | 'never'
        public bool EmptySelectionClipboard = true;  // emptySelectionClipboard (Cut/Copy whole line)
        public string AutoIndent = "full";           // autoIndent: 'none' | 'keep' | 'full'
        public string RenderLineHighlight = "none";  // renderLineHighlight: 'none' | 'line'

        // Parsed out of the IDE's DefaultFont descriptor. FontFamily is "" when the descriptor can't be
        // parsed — callers MUST then fall back to the stored pref rather than inventing a font.
        public string FontFamily = "";
        public int FontSize = 0;                     // 0 = "not available", same fall-back rule as FontFamily
    }

    /// <summary>
    /// GH #126 (+ follow-up sweep): reads the Clarion IDE's own Options → Text Editor settings so the CA
    /// Monaco surface follows what the developer already configured instead of hardcoding its own answers.
    ///
    /// Source of truth is the fork's PropertyService container "TextEditorSettings" (verified against
    /// %APPDATA%\SoftVelocity\Clarion\12.0\ClarionProperties.xml). Reads are live (PropertyService is the
    /// IDE's in-memory store, updated when the Options dialog is OK'd), so callers should re-read per
    /// payload rather than cache.
    ///
    /// Everything is clamped/whitelisted here: a hand-edited ClarionProperties.xml must not be able to push
    /// an arbitrary string into a Monaco option. Any single key that fails to read falls back to that
    /// field's default and does NOT sink the whole read — a garbled font shouldn't cost you line numbers.
    /// </summary>
    internal static class IdeEditorOptions
    {
        /// <summary>
        /// Back-compat shim for the original indentation-only call. Kept so GH #126's behaviour is reachable
        /// unchanged; new callers should use <see cref="TryReadAll"/>.
        /// </summary>
        public static bool TryRead(out int tabSize, out bool insertSpaces)
        {
            tabSize = 4;
            insertSpaces = false;
            IdeEditorOptionValues v;
            if (!TryReadAll(out v)) return false;
            tabSize = v.TabSize;
            insertSpaces = v.InsertSpaces;
            return true;
        }

        /// <summary>Read the IDE's full Monaco-shaped option set. False (and a null out) when the property
        /// bundle can't be read at all — callers then just omit every ide* key.</summary>
        public static bool TryReadAll(out IdeEditorOptionValues values)
        {
            values = null;
            Properties tes;
            try
            {
                tes = PropertyService.Get<Properties>("TextEditorSettings", null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[IdeEditorOptions] bundle read failed: " + ex.Message);
                return false;
            }
            if (tes == null) return false;

            var v = new IdeEditorOptionValues();

            // --- Indentation (GH #126). Monaco has a single tabSize driving both display and indent, so the
            // mapping picks the value that governs what actually lands in the buffer: IndentationSize when
            // indenting with spaces, TabIndent when real tabs are inserted (their display width is what you
            // see and align to). Semantics deliberately identical to the original TryRead. ---
            bool toSpaces = GetBool(tes, "TabsToSpaces", false);
            int tabIndent = Clamp(GetInt(tes, "TabIndent", 4), 1, 16);
            int indentSize = Clamp(GetInt(tes, "IndentationSize", 4), 1, 16);
            v.TabSize = toSpaces ? indentSize : tabIndent;
            v.InsertSpaces = toSpaces;

            // --- Caret behind EOL (emulated page-side; no Monaco option exists) ---
            v.CursorBehindEOL = GetBool(tes, "CursorBehindEOL", false);

            // --- Straight 1:1 Monaco mappings ---
            v.LineNumbers = GetBool(tes, "ShowLineNumbers", true) ? "on" : "off";
            v.Folding = GetBool(tes, "EnableFolding", true);
            v.MouseWheelZoom = GetBool(tes, "MouseWheelTextZoom", true);
            v.EmptySelectionClipboard = GetBool(tes, "CutCopyWholeLine", true);
            v.MatchBrackets = GetBool(tes, "ShowBracketHighlight", true) ? "always" : "never";

            // Monaco can't render spaces and tabs independently — it has one renderWhitespace switch. Either
            // Clarion flag on means "show me the whitespace", so both map onto 'all'; neither means 'none'.
            // Note this is NOT Monaco's 'selection' default: following the IDE means following it exactly.
            bool showSpaces = GetBool(tes, "ShowSpaces", false);
            bool showTabs = GetBool(tes, "ShowTabs", false);
            v.RenderWhitespace = (showSpaces || showTabs) ? "all" : "none";

            // Vertical ruler: Clarion carries visibility and column separately; Monaco takes a column list.
            if (GetBool(tes, "ShowVRuler", false))
                v.Rulers = new int[] { Clamp(GetInt(tes, "VRulerRow", 80), 1, 500) };

            // IndentStyle is SharpDevelop's None | Auto | Smart. Monaco's autoIndent ladder is
            // none | keep | brackets | advanced | full — 'full' is what drives the Clarion indentationRules
            // already registered in the page, so Smart maps there and Auto degrades to plain "keep previous".
            switch ((GetString(tes, "IndentStyle", "Smart") ?? "").Trim().ToLowerInvariant())
            {
                case "none": v.AutoIndent = "none"; break;
                case "auto": v.AutoIndent = "keep"; break;
                default: v.AutoIndent = "full"; break;   // "smart" and anything unrecognised
            }

            // LineViewerStyle is None | FullRow in the fork. Anything that isn't an explicit None lights the
            // current line, which is the closer of the two Monaco behaviours.
            v.RenderLineHighlight =
                string.Equals((GetString(tes, "LineViewerStyle", "None") ?? "").Trim(), "None",
                              StringComparison.OrdinalIgnoreCase) ? "none" : "line";

            // --- Font. Parse failures leave FontFamily=""/FontSize=0 so the caller falls back to the stored
            // pref; we never substitute a font of our own choosing. ---
            ParseFont(GetString(tes, "DefaultFont", null), v);

            values = v;
            return true;
        }

        /// <summary>
        /// Parse the IDE's font descriptor, e.g.
        ///   [Font: Name=Courier New, Size=11, Units=3, GdiCharSet=1, GdiVerticalFont=False]
        /// Units is a System.Drawing.GraphicsUnit: 3 = Point, which is how the Options dialog stores it.
        /// Monaco's fontSize is CSS pixels, so points are converted at the CSS-standard 96dpi (px = pt*4/3);
        /// any other unit is already pixel-ish and passes through. Leaves the fields untouched (== "not
        /// available") on anything unexpected.
        /// </summary>
        private static void ParseFont(string descriptor, IdeEditorOptionValues v)
        {
            if (string.IsNullOrEmpty(descriptor)) return;
            try
            {
                var name = Regex.Match(descriptor, @"Name\s*=\s*([^,\]]+)");
                if (name.Success)
                {
                    string fam = name.Groups[1].Value.Trim();
                    // Same shape-guard the settings model applies to a hand-typed family: no control chars,
                    // no quotes/braces that could break out of the CSS font-family the page builds.
                    if (fam.Length > 0 && fam.Length <= 64 && fam.IndexOfAny(new[] { '"', '\'', '{', '}', ';', '\r', '\n' }) < 0)
                        v.FontFamily = fam;
                }

                var size = Regex.Match(descriptor, @"Size\s*=\s*([0-9]+(?:\.[0-9]+)?)");
                if (size.Success)
                {
                    double pts;
                    if (double.TryParse(size.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pts) && pts > 0)
                    {
                        var units = Regex.Match(descriptor, @"Units\s*=\s*([0-9]+)");
                        int u;
                        bool isPoint = units.Success && int.TryParse(units.Groups[1].Value, out u) && u == 3;
                        double px = isPoint ? pts * 4.0 / 3.0 : pts;
                        v.FontSize = Clamp((int)Math.Round(px), 6, 48);
                    }
                }
            }
            catch (Exception ex)
            {
                // Leave whatever parsed successfully; the caller falls back per-field.
                System.Diagnostics.Debug.WriteLine("[IdeEditorOptions] font parse failed: " + ex.Message);
            }
        }

        // Per-key readers: one bad key must not sink the whole bundle.
        private static bool GetBool(Properties p, string key, bool fallback)
        {
            try { return p.Get(key, fallback); } catch { return fallback; }
        }

        private static int GetInt(Properties p, string key, int fallback)
        {
            try { return p.Get(key, fallback); } catch { return fallback; }
        }

        private static string GetString(Properties p, string key, string fallback)
        {
            try { return p.Get(key, fallback); } catch { return fallback; }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
