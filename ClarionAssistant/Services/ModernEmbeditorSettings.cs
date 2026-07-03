using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Path B — Modern Embeditor: dev-controllable editor settings. A small typed model persisted via
    /// SettingsService (%APPDATA%\ClarionAssistant\settings.txt, keys "ModernEmbeditor.*") and shipped to
    /// Monaco as a JSON dict. These map to Monaco editor/model options:
    ///   TabSize/InsertSpaces -> model.updateOptions; FontSize/WordWrap/Minimap/AutoIndent -> editor.updateOptions.
    /// AutoIndent toggles Monaco's autoIndent between 'full' (uses the Clarion indentationRules in the HTML —
    /// indent after IF/LOOP/CASE/structures, outdent on END/'.') and 'keep' (just match the previous line).
    /// Format-on-demand ("Format Document") is intentionally NOT here — it needs a real Clarion formatter.
    /// </summary>
    public sealed class ModernEmbeditorSettings
    {
        public int TabSize = 2;
        public bool InsertSpaces = true;
        public bool AutoIndent = true;
        public bool WordWrap = false;
        public bool Minimap = true;
        // "Complete word on insert key" (space, '.', '('): accept the highlighted suggestion and insert the
        // typed char, the way the native Clarion editor does. Default ON. Maps to commitCharacters in Monaco.
        public bool CompleteOnInsertKey = true;
        public int FontSize = 13;
        // Horizontal scrollbar visibility → Monaco editor option scrollbar.horizontal:
        //   "auto" (show when needed) | "visible" (show always) | "hidden" (show never). Default auto.
        public string HorizontalScrollbar = "auto";

        /// <summary>
        /// User key-binding OVERRIDES only: command id → canonical chord string (e.g. "Ctrl+Shift+Y").
        /// The default chord for every command lives in the HTML command table — C# stores only the
        /// chords the dev has changed, so re-defaulting a command is just removing its entry. Persisted
        /// as a single compact-JSON value (no CR/LF) under "ModernEmbeditor.KeyBindings".
        /// </summary>
        public Dictionary<string, string> KeyBindings = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Smart Formatter gear-panel options (deac3d16). Pass-through bag: the host stays agnostic to
        /// formatter semantics — the HTML's formatterOptions() chokepoint re-clamps/coerces every value on
        /// the way into the engine — so C# only needs to ferry these through Save/Load AND the cross-tab
        /// broadcast. Persisting them ONLY here was the gap that left formatter settings not carrying across
        /// tabs (and fragile across reopen): the editor keys round-tripped but the formatter keys were dropped
        /// from FromDict/ToDict. Whitelisted (see FormatterKeys) so a crafted payload can't smuggle arbitrary
        /// keys into settings.txt, and value-sanitized to bool/number/short-string with no CR/LF.
        /// </summary>
        public Dictionary<string, object> Formatter = new Dictionary<string, object>(StringComparer.Ordinal);

        // The 18 formatter option keys the gear panel round-trips (mirrors FORMATTER_SETTING_KEYS +
        // formatLineOnEnter in monaco-embeditor.html). Keep in sync if the panel gains/loses an option.
        private static readonly string[] FormatterKeys = {
            "preferredColumn", "contLineMultiplier", "indentComments", "dontIndentCol1Comments",
            "indentFromCode", "indentCaseSubKeywords", "colonAsLabel", "formatBlockAfterEnd", "preferredKeywordIndent",
            "alignAssignments", "spacesBeforeAssignment", "spacesAfterAssignment", "treatBlankAsContiguous",
            "treatCommentAsContiguous", "alignScope", "keywordCase", "otherNameCase", "formatLineOnEnter"
        };
        private static readonly HashSet<string> FormatterKeySet = new HashSet<string>(FormatterKeys, StringComparer.Ordinal);
        private const int MaxFormatterStringLen = 32;

        // Safety caps for the untrusted JS payload: bound how many overrides and how long a chord can be
        // so a crafted settings.txt / postMessage can't bloat the file or the binding map.
        private const int MaxKeyBindings = 64;
        private const int MaxChordLength = 40;

        private const string Prefix = "ModernEmbeditor.";

        /// <summary>Load from SettingsService, falling back to defaults for any missing/invalid key.</summary>
        public static ModernEmbeditorSettings Load()
        {
            var s = new ModernEmbeditorSettings();
            try
            {
                var sv = new SettingsService();
                s.TabSize = GetInt(sv, "TabSize", s.TabSize, 1, 16);
                s.InsertSpaces = GetBool(sv, "InsertSpaces", s.InsertSpaces);
                s.AutoIndent = GetBool(sv, "AutoIndent", s.AutoIndent);
                s.WordWrap = GetBool(sv, "WordWrap", s.WordWrap);
                s.Minimap = GetBool(sv, "Minimap", s.Minimap);
                s.CompleteOnInsertKey = GetBool(sv, "CompleteOnInsertKey", s.CompleteOnInsertKey);
                s.FontSize = GetInt(sv, "FontSize", s.FontSize, 6, 48);
                s.HorizontalScrollbar = NormalizeScrollbar(sv.Get(Prefix + "HorizontalScrollbar"));
                s.KeyBindings = ParseKeyBindings(sv.Get(Prefix + "KeyBindings"));
                s.Formatter = ParseFormatter(sv.Get(Prefix + "Formatter"));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditorSettings] Load: " + ex.Message); }
            return s;
        }

        /// <summary>Persist all values. May throw SettingsLockedException (cross-process contention) — the caller surfaces it.</summary>
        public void Save()
        {
            var sv = new SettingsService();
            sv.Set(Prefix + "TabSize", Clamp(TabSize, 1, 16).ToString());
            sv.Set(Prefix + "InsertSpaces", InsertSpaces ? "true" : "false");
            sv.Set(Prefix + "AutoIndent", AutoIndent ? "true" : "false");
            sv.Set(Prefix + "WordWrap", WordWrap ? "true" : "false");
            sv.Set(Prefix + "Minimap", Minimap ? "true" : "false");
            sv.Set(Prefix + "CompleteOnInsertKey", CompleteOnInsertKey ? "true" : "false");
            sv.Set(Prefix + "FontSize", Clamp(FontSize, 6, 48).ToString());
            sv.Set(Prefix + "HorizontalScrollbar", NormalizeScrollbar(HorizontalScrollbar));
            // Compact JSON, single line — SettingsService rejects CR/LF in values, and the serializer
            // never emits them. Empty map persists as "{}" (clears any prior overrides).
            sv.Set(Prefix + "KeyBindings", new JavaScriptSerializer().Serialize(SanitizeBindings(KeyBindings)));
            // Compact JSON, single line (the serializer never emits CR/LF, which SettingsService rejects).
            // Empty map persists as "{}" — the panel then seeds every formatter control from DEFAULTS.
            sv.Set(Prefix + "Formatter", new JavaScriptSerializer().Serialize(SanitizeFormatter(Formatter)));
        }

        /// <summary>Build a settings instance from the JS payload dict (validated + clamped).</summary>
        public static ModernEmbeditorSettings FromDict(IDictionary<string, object> d)
        {
            var s = new ModernEmbeditorSettings();
            if (d == null) return s;
            s.TabSize = Clamp(ToInt(d, "tabSize", s.TabSize), 1, 16);
            s.InsertSpaces = ToBool(d, "insertSpaces", s.InsertSpaces);
            s.AutoIndent = ToBool(d, "autoIndent", s.AutoIndent);
            s.WordWrap = ToBool(d, "wordWrap", s.WordWrap);
            s.Minimap = ToBool(d, "minimap", s.Minimap);
            s.CompleteOnInsertKey = ToBool(d, "completeOnInsertKey", s.CompleteOnInsertKey);
            s.FontSize = Clamp(ToInt(d, "fontSize", s.FontSize), 6, 48);
            object hs;
            if (d.TryGetValue("horizontalScrollbar", out hs) && hs != null)
                s.HorizontalScrollbar = NormalizeScrollbar(hs.ToString());
            object kb;
            if (d.TryGetValue("keyBindings", out kb) && kb is IDictionary<string, object>)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in (IDictionary<string, object>)kb)
                    if (kv.Value != null) map[kv.Key] = kv.Value.ToString();
                s.KeyBindings = SanitizeBindings(map);
            }
            // Collect the whitelisted formatter keys present in the payload (sanitized to safe scalars).
            var fm = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var k in FormatterKeys)
            {
                object fv;
                if (d.TryGetValue(k, out fv)) fm[k] = fv;
            }
            s.Formatter = SanitizeFormatter(fm);
            return s;
        }

        /// <summary>Serialize for the JS bridge (keys match the HTML's applyEditorSettings).</summary>
        public Dictionary<string, object> ToDict()
        {
            var dict = new Dictionary<string, object>
            {
                { "tabSize", TabSize },
                { "insertSpaces", InsertSpaces },
                { "autoIndent", AutoIndent },
                { "wordWrap", WordWrap },
                { "minimap", Minimap },
                { "completeOnInsertKey", CompleteOnInsertKey },
                { "fontSize", FontSize },
                { "horizontalScrollbar", HorizontalScrollbar },
                { "keyBindings", SanitizeBindings(KeyBindings) }
            };
            // Merge the formatter pass-through bag so the bridge payload (load + cross-tab broadcast) carries
            // the gear-panel formatter options the same way it carries the editor keys.
            foreach (var kv in SanitizeFormatter(Formatter)) dict[kv.Key] = kv.Value;
            return dict;
        }

        /// <summary>
        /// Host-side clamp for the four numeric formatter options, mirroring the HTML formatterOptions() ranges
        /// (preferredColumn [1,120], contLineMultiplier [1,8], spacesBefore/AfterAssignment [1,12]). Non-numeric
        /// keys never reach here. Returns an int within range — the JS num() always emits ints, so rounding a
        /// fractional hand-edit is correct, not lossy. A numeric value landing on a non-numeric key (malformed
        /// payload) passes through unchanged; the page coerces it harmlessly.
        /// </summary>
        private static object ClampFormatterNumeric(string key, double dv, object original)
        {
            int lo, hi;
            switch (key)
            {
                case "preferredColumn": lo = 1; hi = 120; break;
                case "contLineMultiplier": lo = 1; hi = 8; break;
                case "spacesBeforeAssignment":
                case "spacesAfterAssignment": lo = 1; hi = 12; break;
                default: return original;
            }
            long iv = (long)Math.Round(dv, MidpointRounding.AwayFromZero);
            if (iv < lo) iv = lo; else if (iv > hi) iv = hi;
            return (int)iv;
        }

        /// <summary>Deserialize the persisted compact-JSON formatter bag; tolerant of null/garbage.</summary>
        private static Dictionary<string, object> ParseFormatter(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                return SanitizeFormatter(d);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorSettings] ParseFormatter: " + ex.Message);
                return new Dictionary<string, object>(StringComparer.Ordinal);
            }
        }

        /// <summary>
        /// Defensive copy of the formatter bag: keep only whitelisted keys whose value is a safe scalar —
        /// bool, number, or a short string with no CR/LF (would corrupt the line-based settings file) and no
        /// HTML metacharacters (defense in depth; the panel sets these via &lt;select&gt;.value/&lt;input&gt;.value,
        /// never innerHTML). Validation of ranges/enums lives in the HTML; C# only ensures nothing stored here
        /// can break settings.txt or balloon.
        /// </summary>
        private static Dictionary<string, object> SanitizeFormatter(IDictionary<string, object> map)
        {
            var outp = new Dictionary<string, object>(StringComparer.Ordinal);
            if (map == null) return outp;
            foreach (var kv in map)
            {
                if (!FormatterKeySet.Contains(kv.Key)) continue;
                object v = kv.Value;
                if (v is bool) { outp[kv.Key] = (bool)v; continue; }
                if (v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint
                    || v is long || v is ulong || v is float || v is double || v is decimal)
                {
                    double dv;
                    try { dv = Convert.ToDouble(v); } catch { continue; }
                    // Reject non-finite — a hand-edited settings.txt literal can deserialize to ±Infinity/NaN,
                    // which would throw in Save()'s JSON serialize (JSON has no Infinity) and is never a valid
                    // option value. Drop it so the engine DEFAULT applies. (deac3d16 security LOW)
                    // (double.IsFinite is .NET Core only — this is .NET Framework, so test the complement.)
                    if (double.IsNaN(dv) || double.IsInfinity(dv)) continue;
                    // Independent host-side clamp of the engine's numeric options, so the bound doesn't rely
                    // solely on the HTML formatterOptions() chokepoint (defense in depth: a future host consumer
                    // or new engine call site would otherwise reintroduce the Array(col+1).join hang risk with
                    // no host floor). (deac3d16 security LOW)
                    outp[kv.Key] = ClampFormatterNumeric(kv.Key, dv, v);
                    continue;
                }
                var str = v as string;
                if (str == null) continue;                       // drop null / arrays / nested objects
                if (str.Length > MaxFormatterStringLen) continue;
                if (str.IndexOf('\r') >= 0 || str.IndexOf('\n') >= 0) continue;
                if (str.IndexOf('<') >= 0 || str.IndexOf('>') >= 0 || str.IndexOf('"') >= 0) continue;
                outp[kv.Key] = str;
            }
            return outp;
        }

        /// <summary>Deserialize the persisted compact-JSON binding map; tolerant of null/garbage.</summary>
        private static Dictionary<string, string> ParseKeyBindings(string raw)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw)) return map;
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                if (d != null)
                    foreach (var kv in d)
                        if (kv.Value != null) map[kv.Key] = kv.Value.ToString();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditorSettings] ParseKeyBindings: " + ex.Message); }
            return SanitizeBindings(map);
        }

        /// <summary>
        /// Defensive copy: drop empty keys/values, reject CR/LF (would break the line-based settings file)
        /// and anything past the length/count caps. The HTML owns the canonical command-id list and
        /// chord grammar; C# only enforces that what it stores can't corrupt settings.txt or balloon.
        /// </summary>
        private static Dictionary<string, string> SanitizeBindings(Dictionary<string, string> map)
        {
            var outp = new Dictionary<string, string>(StringComparer.Ordinal);
            if (map == null) return outp;
            foreach (var kv in map)
            {
                if (outp.Count >= MaxKeyBindings) break;
                string id = kv.Key, chord = kv.Value;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(chord)) continue;
                if (chord.Length > MaxChordLength) continue;
                if (id.IndexOf('\r') >= 0 || id.IndexOf('\n') >= 0) continue;
                if (chord.IndexOf('\r') >= 0 || chord.IndexOf('\n') >= 0) continue;
                // Reject HTML metacharacters that are never part of a real chord token. Defense in depth
                // against a stored-XSS payload reaching the gear panel's keybinding <input value="...">
                // (the JS escapes too, but a crafted/edited settings.txt shouldn't even hold such a value).
                if (chord.IndexOf('<') >= 0 || chord.IndexOf('>') >= 0 || chord.IndexOf('"') >= 0) continue;
                outp[id] = chord;
            }
            return outp;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>Whitelist the horizontal-scrollbar mode to Monaco's three legal scrollbar.horizontal
        /// values ("visible"/"hidden"); anything else (null, missing, or a crafted payload) → "auto".</summary>
        private static string NormalizeScrollbar(string v)
        {
            return (v == "visible" || v == "hidden") ? v : "auto";
        }

        private static int GetInt(SettingsService sv, string key, int dflt, int lo, int hi)
        {
            int v;
            string raw = sv.Get(Prefix + key);
            return (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out v)) ? Clamp(v, lo, hi) : dflt;
        }

        private static bool GetBool(SettingsService sv, string key, bool dflt)
        {
            string raw = sv.Get(Prefix + key);
            if (string.IsNullOrEmpty(raw)) return dflt;
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ToInt(IDictionary<string, object> d, string key, int dflt)
        {
            object o;
            if (d.TryGetValue(key, out o) && o != null)
            {
                try { return Convert.ToInt32(o); } catch { }
            }
            return dflt;
        }

        private static bool ToBool(IDictionary<string, object> d, string key, bool dflt)
        {
            object o;
            if (d.TryGetValue(key, out o) && o != null)
            {
                try { return Convert.ToBoolean(o); } catch { }
            }
            return dflt;
        }
    }

    /// <summary>
    /// Persists the Modern Embeditor's Find/Replace dropdown history to a dedicated per-Clarion-version,
    /// per-solution JSON file:
    ///   %APPDATA%\ClarionAssistant\&lt;ClarionVersion&gt;\&lt;Solution&gt;\find-history.json
    /// (e.g. ...\Clarion12\MyApp\find-history.json). The file holds solution-wide "find"/"replace" lists
    /// plus a "procFind" map of per-procedure recent search terms, which powers the layered Find dropdown
    /// ("This procedure" group on top, full solution list below). Solution-wide lists are authoritative on
    /// save (so delete/clear stick) and broadcast across tabs; the procFind map is merged per-procedure-key
    /// so concurrent tabs (different procedures) don't clobber each other.
    /// Version folder: running IDE config (ClarionVersionService) → addin install root → "Default".
    /// </summary>
    public static class ModernEmbeditorHistory
    {
        private const int Cap = 25;       // solution-wide list cap
        private const int ProcCap = 10;   // per-procedure recent cap
        private static string _versionTag;

        /// <summary>Load solution-wide find/replace plus this procedure's recent terms.</summary>
        public static void Load(string solutionPath, string procKey,
            out List<string> find, out List<string> replace, out List<string> proc)
        {
            find = new List<string>(); replace = new List<string>(); proc = new List<string>();
            try
            {
                string path = FilePath(solutionPath);
                if (!File.Exists(path)) return;
                var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (d == null) return;
                find = Clean(ToList(d, "find"), Cap);
                replace = Clean(ToList(d, "replace"), Cap);
                var procMap = GetMap(d, "procFind");
                object pv;
                if (!string.IsNullOrEmpty(procKey) && procMap.TryGetValue(procKey, out pv) && pv is object[])
                    proc = Clean(ToList((object[])pv), ProcCap);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditorHistory] Load: " + ex.Message); }
        }

        /// <summary>
        /// Persist solution-wide find/replace (authoritative) and this procedure's recent terms (merged into
        /// the procFind map, preserving every other procedure's list). Outputs the cleaned solution-wide
        /// lists for the cross-tab broadcast.
        /// </summary>
        public static void Save(string solutionPath, string procKey,
            IList<string> find, IList<string> replace, IList<string> proc,
            out List<string> savedFind, out List<string> savedReplace)
        {
            savedFind = Clean(find, Cap);
            savedReplace = Clean(replace, Cap);
            try
            {
                string path = FilePath(solutionPath);

                // Preserve other procedures' lists: start from the existing procFind map, update only ours.
                var procFind = new Dictionary<string, object>();
                try
                {
                    if (File.Exists(path))
                    {
                        var existing = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                            .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                        var map = GetMap(existing, "procFind");
                        foreach (var kv in map)
                            if (kv.Value is object[]) procFind[kv.Key] = Clean(ToList((object[])kv.Value), ProcCap);
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(procKey)) procFind[procKey] = Clean(proc, ProcCap);

                var payload = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "find", savedFind },
                    { "replace", savedReplace },
                    { "procFind", procFind }
                });
                File.WriteAllText(path, payload, Encoding.UTF8);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditorHistory] Save: " + ex.Message); }
        }

        public static string ToJson(IList<string> list)
        {
            try { return new JavaScriptSerializer().Serialize(list ?? new List<string>()); }
            catch { return "[]"; }
        }

        /// <summary>Absolute path to the version+solution history file (folders created on demand).</summary>
        private static string FilePath(string solutionPath)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant", VersionTag(), SolutionTag(solutionPath));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return Path.Combine(dir, "find-history.json");
        }

        /// <summary>Folder-safe tag for the open solution (its file name without extension), or "NoSolution".</summary>
        public static string SolutionTag(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath)) return "NoSolution";
            string leaf;
            try { leaf = Path.GetFileNameWithoutExtension(solutionPath.TrimEnd('\\', '/')); }
            catch { leaf = null; }
            if (string.IsNullOrEmpty(leaf)) { try { leaf = Path.GetFileName(solutionPath.TrimEnd('\\', '/')); } catch { } }
            return Sanitize(string.IsNullOrEmpty(leaf) ? "NoSolution" : leaf);
        }

        /// <summary>
        /// Folder-safe tag for the active Clarion version (e.g. "Clarion12"). Prefers the running IDE's
        /// current version config; falls back to the addin's install root; then "Default".
        /// </summary>
        public static string VersionTag()
        {
            if (_versionTag != null) return _versionTag;
            string tag = null;
            try
            {
                var cfg = ClarionVersionService.Detect()?.GetCurrentConfig();
                string root = cfg != null ? cfg.RootPath : null;
                if (!string.IsNullOrEmpty(root)) tag = Path.GetFileName(root.TrimEnd('\\', '/'));
            }
            catch { }
            if (string.IsNullOrEmpty(tag))
            {
                try
                {
                    // Deployed layout: <ClarionRoot>\accessory\addins\ClarionAssistant\ClarionAssistant.dll
                    var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var d = new DirectoryInfo(dir);
                    if (d != null && d.Parent != null && d.Parent.Parent != null &&
                        string.Equals(d.Parent.Name, "addins", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(d.Parent.Parent.Name, "accessory", StringComparison.OrdinalIgnoreCase) &&
                        d.Parent.Parent.Parent != null)
                        tag = d.Parent.Parent.Parent.Name;
                }
                catch { }
            }
            _versionTag = Sanitize(string.IsNullOrEmpty(tag) ? "Default" : tag);
            return _versionTag;
        }

        private static string Sanitize(string name)
        {
            if (name == null) return "Default";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return string.IsNullOrEmpty(name) ? "Default" : name;
        }

        private static Dictionary<string, object> GetMap(IDictionary<string, object> d, string key)
        {
            object o;
            if (d != null && d.TryGetValue(key, out o) && o is Dictionary<string, object>)
                return (Dictionary<string, object>)o;
            return new Dictionary<string, object>();
        }

        private static List<string> ToList(IDictionary<string, object> d, string key)
        {
            object o;
            if (d != null && d.TryGetValue(key, out o) && o is object[]) return ToList((object[])o);
            return new List<string>();
        }

        private static List<string> ToList(object[] arr)
        {
            var res = new List<string>();
            if (arr != null) foreach (var item in arr) if (item != null) res.Add(item.ToString());
            return res;
        }

        private static List<string> Clean(IList<string> list, int cap)
        {
            var outp = new List<string>();
            if (list == null) return outp;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in list)
            {
                if (outp.Count >= cap) break;
                if (string.IsNullOrEmpty(s) || seen.Contains(s)) continue;
                seen.Add(s); outp.Add(s);
            }
            return outp;
        }
    }
}
