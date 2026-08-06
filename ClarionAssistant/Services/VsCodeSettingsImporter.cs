using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// One-shot importer for a developer's Visual Studio Code editor settings, mapped onto the CA
    /// Monaco gear-panel settings (<see cref="ModernEmbeditorSettings"/>).
    ///
    /// READ-ONLY BY DESIGN. This service locates, parses and maps — it never writes. Applying the
    /// result goes back through the page's existing save path (gear panel → `saveSettings` →
    /// <see cref="MonacoSettingsBroadcaster"/>), so there is exactly one write path in the product
    /// and the import inherits its persistence, clamping and cross-surface broadcast for free.
    ///
    /// Only the settings with a genuine VS Code counterpart are mapped (see <see cref="MapEditorKeys"/>).
    /// The 20 Smart Formatter options, `splitOrientation` and `followIdeIndentation` are Clarion-specific
    /// and are deliberately left alone — the import UI says so rather than leaving the user to wonder.
    ///
    /// THREE THINGS REAL settings.json FILES DO THAT NAIVE PARSING GETS WRONG:
    ///  1. It is JSONC, not JSON — `//` and `/* */` comments and trailing commas are all legal, and
    ///     <see cref="JavaScriptSerializer"/> throws on every one of them. <see cref="StripJsonComments"/>
    ///     removes them, and it is string-literal aware: the `//` in "C://tools" is NOT a comment.
    ///  2. `editor.fontFamily` is a CSS font STACK ("Cascadia Code, Consolas, 'Courier New', monospace"),
    ///     while CA stores a single family and <c>ModernEmbeditorSettings.SanitizeFontFamily</c> rejects
    ///     any value containing a double quote and blanks anything over 64 chars. Handing the stack over
    ///     verbatim would silently wipe the user's font on every import, so we take the first family and
    ///     strip its quoting — and omit the key entirely if what's left still wouldn't survive.
    ///  3. Settings can be scoped per language: a `"[clarion]": { ... }` block overrides the globals for
    ///     Clarion files specifically. Since every CA surface IS a Clarion file, that block wins.
    /// </summary>
    public static class VsCodeSettingsImporter
    {
        /// <summary>Refuse absurd files rather than reading them into memory — settings.json is a few KB.</summary>
        private const int MaxFileBytes = 1024 * 1024;

        /// <summary>Bound the flatten walk so a deeply nested / huge settings object can't blow the stack or the map.</summary>
        private const int MaxFlattenDepth = 6;
        private const int MaxFlattenEntries = 5000;

        /// <summary>The language-scope block whose values beat the globals (VS Code Clarion language id).</summary>
        private const string ClarionScopePrefix = "[clarion].";

        /// <summary>Outcome of an import read. Never throws — every failure lands in <see cref="Error"/>.</summary>
        public sealed class Result
        {
            /// <summary>True when a settings.json was located AND read (even if it then failed to parse).</summary>
            public bool Found;

            /// <summary>Absolute path of the file that was read, or null when none was found.</summary>
            public string Path;

            /// <summary>Friendly name of the install the file came from ("VS Code", "VS Code Insiders", ...).</summary>
            public string Source;

            /// <summary>Human-readable failure reason, or null on success.</summary>
            public string Error;

            /// <summary>
            /// Mapped CA settings, keyed the way the gear panel and <c>ModernEmbeditorSettings.ToDict()</c>
            /// name them (camelCase: tabSize, insertSpaces, ...). Only keys actually present and usable in
            /// the VS Code file appear — an absent key means "propose no change", never "reset to default".
            /// </summary>
            public Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);

            /// <summary>
            /// VS Code keys that were present but could not be imported, with the reason. Surfaced in the
            /// preview so an ignored setting reads as a deliberate decision rather than a silent drop.
            /// </summary>
            public List<KeyValuePair<string, string>> Skipped = new List<KeyValuePair<string, string>>();
        }

        /// <summary>
        /// Locate (or use <paramref name="explicitPath"/>), read, and map a VS Code settings.json.
        /// Never throws.
        /// </summary>
        /// <param name="explicitPath">
        /// A file the user picked by hand — used for portable / WSL installs, which have no discoverable
        /// %APPDATA% location. When null the well-known install locations are probed in order.
        /// </param>
        public static Result Read(string explicitPath)
        {
            var result = new Result();
            try
            {
                string path = explicitPath, source = null;
                if (string.IsNullOrEmpty(path)) LocateSettingsFile(out path, out source);
                else source = "Selected file";

                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;   // Found stays false

                result.Path = path;
                result.Source = source;

                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                {
                    result.Found = true;
                    result.Error = "settings.json is unexpectedly large (" + (info.Length / 1024) + " KB) — not read.";
                    return result;
                }

                string raw = File.ReadAllText(path);
                result.Found = true;

                Dictionary<string, object> root;
                try
                {
                    root = new JavaScriptSerializer { MaxJsonLength = MaxFileBytes }
                        .DeserializeObject(StripJsonComments(raw)) as Dictionary<string, object>;
                }
                catch (Exception ex)
                {
                    result.Error = "Could not parse settings.json: " + ex.Message;
                    return result;
                }

                if (root == null)
                {
                    result.Error = "settings.json did not contain a settings object.";
                    return result;
                }

                var flat = Flatten(root);
                MapEditorKeys(flat, result);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                System.Diagnostics.Debug.WriteLine("[VsCodeSettingsImporter] Read: " + ex.Message);
                return result;
            }
        }

        /// <summary>
        /// Shape the result for the `readVsCodeSettings` bridge response. Kept here (not in the hosts) so
        /// both Monaco hosts stay one-line delegates and the payload can't drift between them.
        /// </summary>
        public static Dictionary<string, object> ToBridgePayload(Result r)
        {
            if (r == null) r = new Result();
            var skipped = new List<Dictionary<string, object>>();
            foreach (var kv in r.Skipped)
                skipped.Add(new Dictionary<string, object> { { "key", kv.Key }, { "reason", kv.Value } });

            return new Dictionary<string, object>
            {
                { "found",   r.Found },
                { "path",    r.Path ?? "" },
                { "source",  r.Source ?? "" },
                { "error",   r.Error ?? "" },
                { "values",  r.Values },
                { "skipped", skipped }
            };
        }

        // ── locating ─────────────────────────────────────────────────────────

        /// <summary>
        /// Probe the well-known per-user settings locations, most-standard first. Portable and
        /// Remote/WSL installs keep their user data elsewhere and are NOT discoverable — those users
        /// pick the file by hand (the caller's Browse… path).
        /// </summary>
        private static void LocateSettingsFile(out string path, out string source)
        {
            path = null; source = null;
            string appData;
            try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
            catch { return; }
            if (string.IsNullOrEmpty(appData)) return;

            // folder name under %APPDATA%  →  friendly label
            var candidates = new[]
            {
                new[] { "Code",            "VS Code" },
                new[] { "Code - Insiders", "VS Code Insiders" },
                new[] { "VSCodium",        "VSCodium" }
            };

            foreach (var c in candidates)
            {
                try
                {
                    string p = Path.Combine(Path.Combine(Path.Combine(appData, c[0]), "User"), "settings.json");
                    if (File.Exists(p)) { path = p; source = c[1]; return; }
                }
                catch { /* keep probing the rest */ }
            }
        }

        // ── JSONC ────────────────────────────────────────────────────────────

        /// <summary>
        /// Strip `//` line comments, `/* */` block comments and trailing commas so a real-world VS Code
        /// settings.json parses as strict JSON.
        ///
        /// STRING-LITERAL AWARE — this is the whole point. A naive strip turns
        /// <c>"editor.fontLigatures": "C://x"</c> into a truncated line, and a naive trailing-comma regex
        /// mangles <c>"a": "foo,"</c>. The scanner tracks whether it is inside a string (honouring
        /// backslash escapes) and only treats comment markers as comments outside one.
        ///
        /// Trailing commas are removed by looking BACK over the already-emitted output when a `}` or `]`
        /// arrives: the last emitted non-whitespace char can only be a comma if that comma was emitted
        /// outside a string, because a string's closing quote is always emitted after its contents.
        /// </summary>
        internal static string StripJsonComments(string src)
        {
            if (string.IsNullOrEmpty(src)) return src ?? string.Empty;

            var sb = new StringBuilder(src.Length);
            bool inString = false, escaped = false;

            for (int i = 0; i < src.Length; i++)
            {
                char ch = src[i];

                if (inString)
                {
                    sb.Append(ch);
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }

                // Outside a string: comments are comments.
                if (ch == '/' && i + 1 < src.Length)
                {
                    char next = src[i + 1];
                    if (next == '/')
                    {
                        while (i < src.Length && src[i] != '\n' && src[i] != '\r') i++;
                        i--;                       // let the loop's i++ land on the newline so it is preserved
                        continue;
                    }
                    if (next == '*')
                    {
                        i += 2;
                        while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                        i++;                       // skip to the '/' of the terminator; loop's i++ steps past it
                        continue;                  // an unterminated block comment simply runs to EOF
                    }
                }

                if (ch == '"') { inString = true; sb.Append(ch); continue; }

                // A '}' or ']' cannot legally follow a comma in strict JSON — drop the trailing comma.
                if (ch == '}' || ch == ']') TrimTrailingComma(sb);

                sb.Append(ch);
            }

            return sb.ToString();
        }

        /// <summary>Remove a trailing comma (and any whitespace after it) from the tail of <paramref name="sb"/>.</summary>
        private static void TrimTrailingComma(StringBuilder sb)
        {
            int j = sb.Length - 1;
            while (j >= 0 && char.IsWhiteSpace(sb[j])) j--;
            if (j >= 0 && sb[j] == ',') sb.Remove(j, 1);
        }

        // ── flattening ───────────────────────────────────────────────────────

        /// <summary>
        /// Flatten nested objects into dotted paths so both spellings VS Code accepts for the same setting
        /// resolve to one lookup: the canonical flat form <c>"editor.minimap.enabled": true</c> and the
        /// partial-object form <c>"editor.minimap": { "enabled": true }</c> both become
        /// <c>editor.minimap.enabled</c>. Language-scope blocks flatten the same way, which is what makes
        /// <c>[clarion].editor.tabSize</c> a plain lookup.
        /// </summary>
        private static Dictionary<string, object> Flatten(Dictionary<string, object> root)
        {
            var flat = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            FlattenInto(root, "", flat, 0);
            return flat;
        }

        private static void FlattenInto(Dictionary<string, object> map, string prefix,
                                        Dictionary<string, object> outp, int depth)
        {
            if (map == null || depth > MaxFlattenDepth) return;
            foreach (var kv in map)
            {
                if (outp.Count >= MaxFlattenEntries) return;
                string key = prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key;
                var child = kv.Value as Dictionary<string, object>;
                if (child != null) FlattenInto(child, key, outp, depth + 1);
                else outp[key] = kv.Value;
            }
        }

        /// <summary>
        /// Look a VS Code key up with Clarion-scope precedence: a value inside <c>"[clarion]": { ... }</c>
        /// beats the same key at global scope, because every surface this import feeds is a Clarion editor.
        /// </summary>
        private static bool TryLookup(Dictionary<string, object> flat, string key, out object value)
        {
            if (flat.TryGetValue(ClarionScopePrefix + key, out value) && value != null) return true;
            return flat.TryGetValue(key, out value) && value != null;
        }

        // ── mapping ──────────────────────────────────────────────────────────

        /// <summary>
        /// Map the VS Code editor keys that have a genuine CA counterpart. An absent key proposes NO change
        /// (it is never treated as "reset to default"); a present-but-unusable value is recorded in
        /// <see cref="Result.Skipped"/> so the preview can explain itself.
        /// </summary>
        private static void MapEditorKeys(Dictionary<string, object> flat, Result r)
        {
            object v;

            // editor.tabSize → tabSize (clamped to the gear panel's 1..16)
            if (TryLookup(flat, "editor.tabSize", out v))
            {
                int n;
                if (TryInt(v, out n)) r.Values["tabSize"] = Clamp(n, 1, 16);
                else Skip(r, "editor.tabSize", "not a number");
            }

            // editor.insertSpaces → insertSpaces. VS Code also accepts the string "auto" (detect from file).
            if (TryLookup(flat, "editor.insertSpaces", out v))
            {
                bool b;
                if (TryBool(v, out b)) r.Values["insertSpaces"] = b;
                else Skip(r, "editor.insertSpaces", "value is '" + AsText(v) + "' — CA has no auto-detect equivalent");
            }

            // editor.fontSize → fontSize (clamped to the gear panel's 6..48)
            if (TryLookup(flat, "editor.fontSize", out v))
            {
                int n;
                if (TryInt(v, out n)) r.Values["fontSize"] = Clamp(n, 6, 48);
                else Skip(r, "editor.fontSize", "not a number");
            }

            // editor.fontFamily → fontFamily (CSS stack → first family; see the class remarks)
            if (TryLookup(flat, "editor.fontFamily", out v))
            {
                string family = FirstFontFamily(v as string);
                if (family != null) r.Values["fontFamily"] = family;
                else Skip(r, "editor.fontFamily", "no usable family name in '" + AsText(v) + "'");
            }

            // editor.wordWrap → wordWrap. "off" is the only false case; the three wrapping modes all mean on.
            if (TryLookup(flat, "editor.wordWrap", out v))
            {
                bool b;
                if (TryBool(v, out b)) r.Values["wordWrap"] = b;                       // legacy boolean form
                else
                {
                    string s = AsText(v);
                    if (EqIc(s, "off")) r.Values["wordWrap"] = false;
                    else if (EqIc(s, "on") || EqIc(s, "wordWrapColumn") || EqIc(s, "bounded")) r.Values["wordWrap"] = true;
                    else Skip(r, "editor.wordWrap", "unrecognised value '" + s + "'");
                }
            }

            // editor.minimap.enabled → minimap (Flatten() already unified the flat and object spellings)
            if (TryLookup(flat, "editor.minimap.enabled", out v))
            {
                bool b;
                if (TryBool(v, out b)) r.Values["minimap"] = b;
                else Skip(r, "editor.minimap.enabled", "not a boolean");
            }

            // editor.autoIndent → autoIndent. CA's toggle is Monaco 'full' vs 'keep', so anything at or
            // above 'brackets' maps to on; 'none'/'keep' map to off.
            if (TryLookup(flat, "editor.autoIndent", out v))
            {
                bool b;
                if (TryBool(v, out b)) r.Values["autoIndent"] = b;                     // legacy boolean form
                else
                {
                    string s = AsText(v);
                    if (EqIc(s, "none") || EqIc(s, "keep")) r.Values["autoIndent"] = false;
                    else if (EqIc(s, "brackets") || EqIc(s, "advanced") || EqIc(s, "full")) r.Values["autoIndent"] = true;
                    else Skip(r, "editor.autoIndent", "unrecognised value '" + s + "'");
                }
            }

            // editor.occurrencesHighlight → occurrenceHighlight. CA has a single-file highlight only, so
            // both 'singleFile' and 'multiFile' map to on.
            if (TryLookup(flat, "editor.occurrencesHighlight", out v))
            {
                bool b;
                if (TryBool(v, out b)) r.Values["occurrenceHighlight"] = b;            // legacy boolean form
                else
                {
                    string s = AsText(v);
                    if (EqIc(s, "off")) r.Values["occurrenceHighlight"] = false;
                    else if (EqIc(s, "singleFile") || EqIc(s, "multiFile")) r.Values["occurrenceHighlight"] = true;
                    else Skip(r, "editor.occurrencesHighlight", "unrecognised value '" + s + "'");
                }
            }

            // editor.scrollbar.horizontal → horizontalScrollbar (same three Monaco values on both sides)
            if (TryLookup(flat, "editor.scrollbar.horizontal", out v))
            {
                string s = AsText(v);
                if (EqIc(s, "auto") || EqIc(s, "visible") || EqIc(s, "hidden")) r.Values["horizontalScrollbar"] = s.ToLowerInvariant();
                else Skip(r, "editor.scrollbar.horizontal", "unrecognised value '" + s + "'");
            }

            // editor.acceptSuggestionOnCommitCharacter → completeOnInsertKey
            if (TryLookup(flat, "editor.acceptSuggestionOnCommitCharacter", out v))
            {
                bool b;
                if (TryBool(v, out b)) r.Values["completeOnInsertKey"] = b;
                else Skip(r, "editor.acceptSuggestionOnCommitCharacter", "not a boolean");
            }
        }

        /// <summary>
        /// First family from a CSS font stack, unquoted — "Cascadia Code, Consolas, 'Courier New'" →
        /// "Cascadia Code". Returns null when nothing usable survives, so the caller omits the key rather
        /// than proposing a blank that would reset the user's font to Monaco's default.
        ///
        /// The length and character rules mirror <c>ModernEmbeditorSettings.SanitizeFontFamily</c> on
        /// purpose: a value this returns must round-trip through Save() unchanged, or the preview would
        /// promise a font the editor then refuses.
        /// </summary>
        internal static string FirstFontFamily(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return null;

            int comma = stack.IndexOf(',');
            string first = (comma >= 0 ? stack.Substring(0, comma) : stack).Trim();
            if (first.Length == 0) return null;

            // Strip one layer of matched quoting: 'Courier New' / "Courier New".
            if (first.Length >= 2 &&
                ((first[0] == '\'' && first[first.Length - 1] == '\'') ||
                 (first[0] == '"' && first[first.Length - 1] == '"')))
                first = first.Substring(1, first.Length - 2).Trim();

            if (first.Length == 0 || first.Length > 64) return null;
            if (first.IndexOf('\r') >= 0 || first.IndexOf('\n') >= 0) return null;
            if (first.IndexOf('<') >= 0 || first.IndexOf('>') >= 0 || first.IndexOf('"') >= 0) return null;
            return first;
        }

        // ── small helpers ────────────────────────────────────────────────────

        private static void Skip(Result r, string key, string reason)
        {
            r.Skipped.Add(new KeyValuePair<string, string>(key, reason));
        }

        private static bool TryInt(object v, out int n)
        {
            n = 0;
            if (v == null || v is bool || v is string) return false;   // "4" is a config error, not a tab size
            try { n = Convert.ToInt32(v); return true; }
            catch { return false; }
        }

        private static bool TryBool(object v, out bool b)
        {
            b = false;
            if (!(v is bool)) return false;
            b = (bool)v;
            return true;
        }

        private static string AsText(object v)
        {
            return v == null ? "" : v.ToString();
        }

        private static bool EqIc(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
