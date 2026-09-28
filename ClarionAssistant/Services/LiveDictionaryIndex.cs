using System;
using System.Collections.Generic;
using System.IO;
using ClarionCodeGraph.Parsing;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Dictionary completion and hover ("INV:" -> the Inventory table's columns and keys, "Inv" -> the
    /// table name) answered from the LIVE dictionary snapshot the CA Embeditor already caches
    /// (ModernEmbeditorViewContent._liveTables, read from the IDE's object model on the UI thread), so
    /// no ingest_schema run and no SQLite open is needed per keystroke (1c685f2e).
    ///
    /// The snapshot owner PUSHES each new snapshot here (<see cref="Publish"/>) where it replaces its
    /// own; the Prefix -> table map is rebuilt lazily, once per new snapshot REFERENCE. When there is no
    /// live snapshot (not loaded yet, an app without a dictionary, the standalone MCP server) the caller's
    /// fallback - the ingested SchemaGraph - answers instead.
    ///
    /// Item shapes match SchemaGraphService's (label "PRE:Field", insert "Field", kind 5 field / 20 key /
    /// 9 table), so the page's dedupe treats the two sources alike. No IDE references.
    /// </summary>
    public static class LiveDictionaryIndex
    {
        private sealed class Index
        {
            public readonly Dictionary<string, List<ClarionAppDataReader.TableDef>> ByPrefix =
                new Dictionary<string, List<ClarionAppDataReader.TableDef>>(StringComparer.OrdinalIgnoreCase);
            public readonly List<ClarionAppDataReader.TableDef> Tables = new List<ClarionAppDataReader.TableDef>();
        }

        private static readonly object _lock = new object();
        private static IDictionary<string, ClarionAppDataReader.TableDef> _published;
        private static object _builtFrom;
        private static Index _index;
        private static int _buildCount;

        /// <summary>Test hook: how many times the prefix map was built (once per new snapshot reference).</summary>
        public static int BuildCount { get { return _buildCount; } }

        /// <summary>The snapshot owner hands over each new snapshot (null clears it, e.g. on an app switch).
        /// The map it names must not be mutated afterwards - the owner replaces it, never edits it.</summary>
        public static void Publish(IDictionary<string, ClarionAppDataReader.TableDef> snapshot)
        {
            lock (_lock) { _published = snapshot; }
            ClarionKeywordIndex.Preload();   // the keyword help loads in the background, before the first hover
        }

        /// <summary>True when a non-empty live snapshot is available (the fallback is then never used).</summary>
        public static bool HasSnapshot { get { return Current() != null; } }

        private static Index Current()
        {
            lock (_lock)
            {
                var snap = _published;
                if (snap == null || snap.Count == 0) return null;
                if (ReferenceEquals(_builtFrom, snap)) return _index;
                var idx = new Index();
                foreach (var t in snap.Values)
                {
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    idx.Tables.Add(t);
                    if (string.IsNullOrEmpty(t.Prefix)) continue;
                    List<ClarionAppDataReader.TableDef> list;
                    if (!idx.ByPrefix.TryGetValue(t.Prefix, out list)) idx.ByPrefix[t.Prefix] = list = new List<ClarionAppDataReader.TableDef>();
                    list.Add(t);
                }
                idx.Tables.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                _index = idx;
                _builtFrom = snap;
                _buildCount++;
                return idx;
            }
        }

        /// <summary>"PRE:partial" completion: the columns (GROUP members included, as Clarion addresses them
        /// with the table's prefix) and keys of every table whose PRE is <paramref name="qualifier"/>. With
        /// no live snapshot, returns <paramref name="fallback"/>'s answer (SchemaGraph); with one, the live
        /// dictionary alone decides. Never throws.</summary>
        public static List<LspClient.CompletionItemInfo> CompleteQualifier(
            string qualifier, string partial, Func<List<LspClient.CompletionItemInfo>> fallback = null)
        {
            var items = new List<LspClient.CompletionItemInfo>();
            try
            {
                var idx = Current();
                if (idx == null) return Fallback(fallback);
                List<ClarionAppDataReader.TableDef> tables;
                if (string.IsNullOrEmpty(qualifier) || !idx.ByPrefix.TryGetValue(qualifier, out tables)) return items;
                partial = partial ?? "";
                foreach (var t in tables)
                {
                    foreach (var f in Flatten(t.Fields))
                    {
                        if (partial.Length > 0 && !f.Name.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                        items.Add(new LspClient.CompletionItemInfo
                        {
                            Label = qualifier + ":" + f.Name,
                            Kind = 5,   // Field
                            Detail = FieldType(f) + "  (table '" + t.Name + "' field, dictionary)",
                            Documentation = string.IsNullOrEmpty(f.Description) ? null : f.Description,
                            InsertText = f.Name
                        });
                    }
                    foreach (var k in Keys(t))
                    {
                        if (partial.Length > 0 && !k.Name.StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                        string composition = null;
                        if (k.Components.Count > 0)
                        {
                            var names = new List<string>();
                            foreach (var c in k.Components) if (c != null && !string.IsNullOrEmpty(c.Name)) names.Add(c.Name);
                            composition = string.Join(", ", names);
                        }
                        items.Add(new LspClient.CompletionItemInfo
                        {
                            Label = qualifier + ":" + k.Name,
                            Kind = 20,   // EnumMember - visually distinct from a field
                            Detail = (k.Primary ? "primary key" : "key") + "  (table '" + t.Name + "', dictionary)",
                            Documentation = string.IsNullOrEmpty(composition) ? null : "Composition: " + composition,
                            InsertText = k.Name
                        });
                    }
                }
            }
            catch { }
            return items;
        }

        /// <summary>Bare-prefix table names ("Inv" -> Inventory). Live snapshot first; with none,
        /// <paramref name="fallback"/> (SchemaGraph). Never throws.</summary>
        public static List<LspClient.CompletionItemInfo> CompleteTableNames(
            string prefix, int limit = 25, Func<List<LspClient.CompletionItemInfo>> fallback = null)
        {
            var items = new List<LspClient.CompletionItemInfo>();
            try
            {
                var idx = Current();
                if (idx == null) return Fallback(fallback);
                if (string.IsNullOrEmpty(prefix)) return items;
                foreach (var t in idx.Tables)
                {
                    if (!t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string detail = string.IsNullOrEmpty(t.Driver) ? "table" : t.Driver + " table";
                    if (!string.IsNullOrEmpty(t.Prefix)) detail += "  PRE(" + t.Prefix + ")";
                    detail += "  (dictionary)";
                    items.Add(new LspClient.CompletionItemInfo
                    {
                        Label = t.Name, Kind = 9, Detail = detail,
                        Documentation = string.IsNullOrEmpty(t.Description) ? null : t.Description,
                        InsertText = t.Name
                    });
                    if (items.Count >= limit) break;
                }
            }
            catch { }
            return items;
        }

        /// <summary>Hover for "PRE:Field" / "PRE:Key" or a table name from the live snapshot, or null
        /// (also null with no snapshot - hover has no SchemaGraph fallback here). Not authoritative: a
        /// dictionary name is not declared in the buffer. Never throws.</summary>
        public static LocalHoverResult HoverWord(string word)
        {
            try
            {
                var idx = Current();
                if (idx == null || string.IsNullOrEmpty(word)) return null;
                int colon = word.IndexOf(':');
                if (colon > 0 && colon < word.Length - 1)
                {
                    string pre = word.Substring(0, colon), name = word.Substring(colon + 1);
                    List<ClarionAppDataReader.TableDef> tables;
                    if (!idx.ByPrefix.TryGetValue(pre, out tables)) return null;
                    foreach (var t in tables)
                    {
                        foreach (var f in Flatten(t.Fields))
                            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                                return Card(pre + ":" + f.Name + "  " + FieldType(f), "field of " + t.Name, f.Description);
                        foreach (var k in Keys(t))
                            if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
                                return Card(pre + ":" + k.Name + "  " + (k.Primary ? "primary key" : k.KeyType), "key of " + t.Name, k.Description);
                    }
                    return null;
                }
                foreach (var t in idx.Tables)
                    if (string.Equals(t.Name, word, StringComparison.OrdinalIgnoreCase))
                    {
                        string sig = t.Name + "  FILE" + (string.IsNullOrEmpty(t.Driver) ? "" : ",DRIVER('" + t.Driver + "')") +
                                     (string.IsNullOrEmpty(t.Prefix) ? "" : ",PRE(" + t.Prefix + ")");
                        return Card(sig, "table", t.Description);
                    }
            }
            catch { }
            return null;
        }

        private static LocalHoverResult Card(string sig, string what, string description)
        {
            string md = "```clarion\n" + sig + "\n```\n\n" + what + " · dictionary";
            if (!string.IsNullOrEmpty(description)) md += "\n\n" + description;
            return new LocalHoverResult { Markdown = md, Authoritative = false, Kind = "dictionary" };
        }

        private static string FieldType(ClarionAppDataReader.FieldDef f)
        {
            return string.IsNullOrEmpty(f.Type) ? "field" : f.Type.Trim();
        }

        /// <summary>Every column in declaration order, GROUP members after their GROUP (Clarion addresses
        /// a group member with the table's prefix, like any other column).</summary>
        private static IEnumerable<ClarionAppDataReader.FieldDef> Flatten(List<ClarionAppDataReader.FieldDef> fields)
        {
            if (fields == null) yield break;
            foreach (var f in fields)
            {
                if (f == null || string.IsNullOrEmpty(f.Name)) continue;
                yield return f;
                foreach (var c in Flatten(f.Children)) yield return c;
            }
        }

        /// <summary>The live reader's rich keys; the legacy name-only list when there are none.</summary>
        private static IEnumerable<ClarionAppDataReader.KeyDef> Keys(ClarionAppDataReader.TableDef t)
        {
            if (t.KeyDefs.Count > 0)
            {
                foreach (var k in t.KeyDefs) if (k != null && !string.IsNullOrEmpty(k.Name)) yield return k;
                yield break;
            }
            foreach (var n in t.Keys) if (!string.IsNullOrEmpty(n)) yield return new ClarionAppDataReader.KeyDef { Name = n };
        }

        private static List<LspClient.CompletionItemInfo> Fallback(Func<List<LspClient.CompletionItemInfo>> fallback)
        {
            if (fallback == null) return new List<LspClient.CompletionItemInfo>();
            try { return fallback() ?? new List<LspClient.CompletionItemInfo>(); }
            catch { return new List<LspClient.CompletionItemInfo>(); }
        }
    }

    /// <summary>
    /// Clarion keyword and built-in procedure NAMES with their categories (ClarionBuiltins), for instant
    /// completion rows and a minimal hover card. Help TEXT is out of Phase 1's scope (1c685f2e). No IDE
    /// references.
    /// </summary>
    public static class ClarionKeywordIndex
    {
        private static List<KeyValuePair<string, string>> _all;   // (NAME, "built-in · Category" | "keyword · Category")
        private static Dictionary<string, KeyValuePair<string, string>> _byName;

        private static List<KeyValuePair<string, string>> All()
        {
            var a = _all;
            if (a != null) return a;
            var list = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ClarionBuiltins.BuiltinsWithCategory())
                if (seen.Add(kv.Key)) list.Add(new KeyValuePair<string, string>(kv.Key, "built-in · " + kv.Value));
            foreach (var kv in ClarionBuiltins.KeywordsWithCategory())
                if (seen.Add(kv.Key)) list.Add(new KeyValuePair<string, string>(kv.Key, "keyword · " + kv.Value));
            return _all = list;
        }

        private static Dictionary<string, KeyValuePair<string, string>> ByName()
        {
            var byName = _byName;
            if (byName != null) return byName;
            byName = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in All()) byName[kv.Key] = kv;
            return _byName = byName;
        }

        /// <summary>Keywords/built-ins starting with <paramref name="prefix"/> (case-insensitive): label is
        /// the upper-case name, detail its category, documentation its help text once the LSP's language
        /// data has loaded (H3). Kind 14 (Keyword). Empty for an empty prefix.</summary>
        public static List<LspClient.CompletionItemInfo> Complete(string prefix)
        {
            var items = new List<LspClient.CompletionItemInfo>();
            if (string.IsNullOrEmpty(prefix)) return items;
            var docs = Docs();
            foreach (var kv in All())
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    KeywordDoc d = null;
                    if (docs != null) docs.TryGetValue(kv.Key, out d);
                    items.Add(new LspClient.CompletionItemInfo
                    {
                        Label = kv.Key, Kind = 14, Detail = kv.Value, InsertText = kv.Key,
                        Documentation = d == null ? null : d.Description
                    });
                }
            return items;
        }

        /// <summary>A card for a keyword, built-in, attribute, data type, directive, control or event: the
        /// name (or its signatures, when it takes parameters), the category, and - once the language data
        /// has loaded - its description. Until then (or with no data folder) name + category only; never
        /// blocks. Null for an unknown word. Never authoritative: a local declaration of the same name (e.g.
        /// a variable "Clip") must be asked first and wins.</summary>
        public static LocalHoverResult HoverWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;
            KeyValuePair<string, string> hit;
            bool known = ByName().TryGetValue(word, out hit);
            KeywordDoc d = null;
            var docs = Docs();
            if (docs != null) docs.TryGetValue(word, out d);
            if (!known && d == null) return null;

            string name = known ? hit.Key : d.Name;
            string category = known ? hit.Value : "keyword" + (string.IsNullOrEmpty(d.Category) ? "" : " · " + d.Category);
            var sb = new System.Text.StringBuilder("```clarion\n");
            if (d != null && d.Signatures.Count > 0) sb.Append(string.Join("\n", d.Signatures));
            else sb.Append(name);
            sb.Append("\n```\n\n").Append(category);
            if (d != null && !string.IsNullOrEmpty(d.ReturnType)) sb.Append(" · returns ").Append(d.ReturnType);
            if (d != null && !string.IsNullOrEmpty(d.Description)) sb.Append("\n\n").Append(d.Description);
            return new LocalHoverResult { Markdown = sb.ToString(), Authoritative = false, Kind = "keyword" };
        }

        /// <summary>True when the keyword data is loaded and holds a description for <paramref name="word"/>, i.e.
        /// <see cref="HoverWord"/> returns the full card rather than name + category only (1c685f2e H4).</summary>
        public static bool HasDescription(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            var docs = Docs();
            KeywordDoc d;
            return docs != null && docs.TryGetValue(word, out d) && d != null && !string.IsNullOrEmpty(d.Description);
        }

        // ============================================================== the LSP's language data (H3)
        // The bundled Clarion language server ships clean, structured language help as JSON beside
        // server.js: <addin>\lsp-server\out\server\src\data\clarion-*.json. The files' shapes differ (a
        // top-level "attributes" / "functions" / "keywords" / "dataTypes" / "directives" / "events" /
        // "windowControls"... array; signatures as {params|parameters, returnType, description|documentation,
        // syntax|label}), so every top-level array of named objects is read the same tolerant way.

        internal sealed class KeywordDoc
        {
            public string Name, Category, Description, ReturnType;
            public readonly List<string> Signatures = new List<string>();
        }

        /// <summary>Test hook / host override: the data folder. Null: resolved from the assembly's folder.</summary>
        internal static string DataDirOverride;

        private static readonly string[] DataFiles =
        {
            "clarion-keywords.json", "clarion-builtins.json", "clarion-attributes.json", "clarion-datatypes.json",
            "clarion-directives.json", "clarion-controls.json", "clarion-events.json"
        };

        private static volatile Dictionary<string, KeywordDoc> _docs;
        private static int _loadStarted;
        private static readonly System.Threading.ManualResetEvent _loaded = new System.Threading.ManualResetEvent(false);

        /// <summary>Start loading the language data on a pool thread (idempotent; the host may call it early).</summary>
        public static void Preload()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _loadStarted, 1, 0) != 0) return;
            if (!System.Threading.ThreadPool.QueueUserWorkItem(_ => Load())) Load();
        }

        /// <summary>The loaded docs, or null while loading (the first call starts the load).</summary>
        private static Dictionary<string, KeywordDoc> Docs()
        {
            var d = _docs;
            if (d == null) Preload();
            return d;
        }

        /// <summary>Test hook: wait up to <paramref name="ms"/> for the load; true when it has finished.</summary>
        internal static bool WaitForLoad(int ms) { Preload(); return _loaded.WaitOne(ms); }

        /// <summary>Test hook: forget the loaded data so the next use loads again.</summary>
        internal static void ResetForTest()
        {
            _docs = null;
            _loaded.Reset();
            System.Threading.Interlocked.Exchange(ref _loadStarted, 0);
        }

        /// <summary>The data folder: the override, else lsp-server\out\server\src\data beside this assembly
        /// (the addin and the standalone MCP server deploy to the same folder), else a dev tree's
        /// .lsp-build\&lt;tag&gt;\... up to four folders above it. Null when there is none.</summary>
        internal static string ResolveDataDir()
        {
            if (DataDirOverride != null) return Directory.Exists(DataDirOverride) ? DataDirOverride : null;
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(ClarionKeywordIndex).Assembly.Location);
                string beside = Path.Combine(asmDir, "lsp-server", "out", "server", "src", "data");
                if (Directory.Exists(beside)) return beside;
                var dir = new DirectoryInfo(asmDir);
                for (int up = 0; up < 5 && dir != null; up++, dir = dir.Parent)
                {
                    string build = Path.Combine(dir.FullName, ".lsp-build");
                    if (!Directory.Exists(build)) continue;
                    string best = null;
                    DateTime bestTime = DateTime.MinValue;
                    foreach (var tag in Directory.GetDirectories(build))
                    {
                        string data = Path.Combine(tag, "out", "server", "src", "data");
                        if (!Directory.Exists(data)) continue;
                        var t = Directory.GetLastWriteTimeUtc(data);
                        if (best == null || t > bestTime) { best = data; bestTime = t; }
                    }
                    if (best != null) return best;
                }
            }
            catch { }
            return null;
        }

        private static void Load()
        {
            var docs = new Dictionary<string, KeywordDoc>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = ResolveDataDir();
                if (dir == null) LspTrace.Write("[keyword-index] no lsp-server data folder; keyword hover stays name + category");
                else
                {
                    var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    foreach (var file in DataFiles)
                    {
                        try
                        {
                            string path = Path.Combine(dir, file);
                            if (!File.Exists(path)) continue;
                            var root = ser.DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
                            if (root == null) continue;
                            foreach (var kv in root)
                            {
                                var arr = kv.Value as object[];
                                if (arr == null) continue;
                                foreach (var o in arr) Merge(docs, o as IDictionary<string, object>);
                            }
                        }
                        catch (Exception ex) { LspTrace.Write("[keyword-index] " + file + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { LspTrace.Write("[keyword-index] load failed: " + ex.Message); }
            finally
            {
                _docs = docs;
                _loaded.Set();
            }
        }

        private static string Str(IDictionary<string, object> d, string key)
        {
            object o;
            return d != null && d.TryGetValue(key, out o) && o is string && ((string)o).Length > 0 ? (string)o : null;
        }

        private static void Merge(Dictionary<string, KeywordDoc> docs, IDictionary<string, object> e)
        {
            string name = Str(e, "name");
            if (name == null) return;
            KeywordDoc d;
            if (!docs.TryGetValue(name, out d)) docs[name] = d = new KeywordDoc { Name = name };
            if (d.Category == null) d.Category = Str(e, "category");
            if (d.ReturnType == null) d.ReturnType = Str(e, "returnType");
            string desc = Str(e, "description") ?? Str(e, "documentation");

            object so;
            var sigs = e.TryGetValue("signatures", out so) ? so as object[] : null;
            bool addSigs = d.Signatures.Count == 0;
            if (sigs != null)
                foreach (var s in sigs)
                {
                    var sd = s as IDictionary<string, object>;
                    if (sd == null) continue;
                    if (desc == null) desc = Str(sd, "description") ?? Str(sd, "documentation");
                    if (d.ReturnType == null) d.ReturnType = Str(sd, "returnType");
                    if (!addSigs) continue;
                    string label = Str(sd, "syntax") ?? Str(sd, "label");
                    var ps = ParamList(sd);
                    if (label == null && ps.Count == 0) continue;   // "no parameters": the bare name says it all
                    if (label == null || (label == name && ps.Count > 0)) label = name + "(" + string.Join(", ", ps) + ")";
                    if (ps.Count > 0 || label != name) d.Signatures.Add(label);
                }
            string syntax = Str(e, "syntax");
            if (addSigs && d.Signatures.Count == 0 && syntax != null && syntax != name) d.Signatures.Add(syntax);
            if (d.Description == null && desc != null && desc != "No parameters") d.Description = desc;
        }

        private static List<string> ParamList(IDictionary<string, object> sig)
        {
            var list = new List<string>();
            object po;
            var ps = (sig.TryGetValue("params", out po) || sig.TryGetValue("parameters", out po)) ? po as object[] : null;
            if (ps == null) return list;
            foreach (var p in ps)
            {
                string n = p as string;
                bool optional = false;
                var pd = p as IDictionary<string, object>;
                if (pd != null)
                {
                    n = Str(pd, "name") ?? Str(pd, "label");
                    object opt;
                    optional = pd.TryGetValue("optional", out opt) && opt is bool && (bool)opt;
                }
                if (n != null) list.Add(optional ? "[" + n + "]" : n);
            }
            return list;
        }
    }
}
