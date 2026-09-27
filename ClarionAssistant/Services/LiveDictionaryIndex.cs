using System;
using System.Collections.Generic;
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

        /// <summary>Keywords/built-ins starting with <paramref name="prefix"/> (case-insensitive): label is
        /// the upper-case name, detail its category. Kind 14 (Keyword). Empty for an empty prefix.</summary>
        public static List<LspClient.CompletionItemInfo> Complete(string prefix)
        {
            var items = new List<LspClient.CompletionItemInfo>();
            if (string.IsNullOrEmpty(prefix)) return items;
            foreach (var kv in All())
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    items.Add(new LspClient.CompletionItemInfo { Label = kv.Key, Kind = 14, Detail = kv.Value, InsertText = kv.Key });
            return items;
        }

        /// <summary>A name + category card for a keyword/built-in, or null. Never authoritative: a local
        /// declaration of the same name (e.g. a variable "Clip") must be asked first and wins.</summary>
        public static LocalHoverResult HoverWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;
            foreach (var kv in All())
                if (string.Equals(kv.Key, word, StringComparison.OrdinalIgnoreCase))
                    return new LocalHoverResult
                    {
                        Markdown = "```clarion\n" + kv.Key + "\n```\n\n" + kv.Value,
                        Authoritative = false,
                        Kind = "keyword"
                    };
            return null;
        }
    }
}
