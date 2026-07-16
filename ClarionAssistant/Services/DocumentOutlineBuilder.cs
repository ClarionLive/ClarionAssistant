using System;
using System.Collections;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Turns an LSP textDocument/documentSymbol result (hierarchical DocumentSymbol[] OR flat
    /// SymbolInformation[]) into the JSON-ready outline tree the Monaco page renders:
    /// each node = { name, kind, detail, line, children:[...] }.
    ///
    /// `line` is a 1-based Monaco line produced by the caller's lsp0-&gt;monaco1 mapper — identity+1 for
    /// whole-file editors, embed-aware in the slot embeditor (which prepends a MEMBER header). Hierarchy
    /// is preserved from DocumentSymbol.children; SymbolInformation (no children) yields a flat list.
    /// (Prototype for the document-structure fly-out — Phase 1, pull-based.)
    /// </summary>
    public static class DocumentOutlineBuilder
    {
        public static List<Dictionary<string, object>> Build(object symbolsResult, Func<int, int> lspLine0ToMonaco1)
        {
            var into = new List<Dictionary<string, object>>();
            Walk(symbolsResult, into, lspLine0ToMonaco1);
            return into;
        }

        private static void Walk(object node, List<Dictionary<string, object>> into, Func<int, int> map)
        {
            var list = node as IEnumerable;
            if (list == null) return;
            foreach (var item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;

                string name = d.ContainsKey("name") ? d["name"] as string : null;
                if (string.IsNullOrEmpty(name))
                {
                    if (d.ContainsKey("children")) Walk(d["children"], into, map);   // skip an unnamed group, keep its kids
                    continue;
                }

                int kind = 0;
                if (d.ContainsKey("kind")) { try { kind = Convert.ToInt32(d["kind"]); } catch { } }
                string detail = d.ContainsKey("detail") ? d["detail"] as string : null;

                var entry = new Dictionary<string, object>
                {
                    { "name", name }, { "kind", kind }, { "detail", detail }, { "line", ExtractLine1(d, map) }
                };

                if (d.ContainsKey("children"))
                {
                    var kids = new List<Dictionary<string, object>>();
                    Walk(d["children"], kids, map);
                    if (kids.Count > 0) entry["children"] = kids;
                }
                into.Add(entry);
            }
        }

        // DocumentSymbol carries range.start.line; SymbolInformation carries location.range.start.line.
        // Both are 0-based LSP lines; map to a 1-based Monaco line (fall back to 1 on any shape surprise).
        private static int ExtractLine1(Dictionary<string, object> d, Func<int, int> map)
        {
            try
            {
                var range = d.ContainsKey("range") ? d["range"] as Dictionary<string, object> : null;
                if (range == null && d.ContainsKey("location"))
                {
                    var loc = d["location"] as Dictionary<string, object>;
                    if (loc != null && loc.ContainsKey("range")) range = loc["range"] as Dictionary<string, object>;
                }
                if (range != null && range.ContainsKey("start"))
                {
                    var start = range["start"] as Dictionary<string, object>;
                    if (start != null && start.ContainsKey("line"))
                    {
                        int lsp0 = Convert.ToInt32(start["line"]);
                        return map != null ? map(lsp0) : lsp0 + 1;
                    }
                }
            }
            catch { }
            return 1;
        }
    }
}
