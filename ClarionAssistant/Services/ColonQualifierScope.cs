using System;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Colon-qualified completion ("Glob:S", "Cus:Na", "PROP:Be") - the two steps SharedLspBridge.GetCompletion
    /// runs around its host merges, kept here so they can be tested without a language server.
    ///
    /// The language server answers a qualified partial with the name AFTER the qualifier as the label
    /// ("Svc") and the qualified name as the detail ("Glob:Svc"). Every host-side source labels the
    /// same kind of item with the full qualified name, and the scoping step keeps only labels that start
    /// with the qualifier - so once any host source supplied a single match, every server item was dropped.
    /// A PROGRAM global only the server knows about (one declared in an INCLUDEd header, which CodeGraph does
    /// not index) therefore never reached the list, although hover resolved it.
    /// </summary>
    public static class ColonQualifierScope
    {
        /// <summary>
        /// Rewrite the server's qualifier items to the host's shape, in place: label = the qualified name,
        /// insert text = the part after the qualifier (the Monaco replace-range breaks on ':'). An item is
        /// only rewritten when its detail is exactly the qualifier followed by its label, which is how the
        /// server marks a qualifier item - anything else, including items already carrying the qualifier,
        /// is left alone. Call this BEFORE the host merges (qualified fields, IDENT:* symbols), which dedupe
        /// by label: they then see the server's copy of a name (the one carrying the declared type) and
        /// skip their own instead of adding a second row.
        /// </summary>
        public static void NormalizeServerItems(List<LspClient.CompletionItemInfo> items, string qualifier)
        {
            if (items == null || string.IsNullOrEmpty(qualifier)) return;
            foreach (var it in items)
            {
                if (it == null || string.IsNullOrEmpty(it.Label) || string.IsNullOrEmpty(it.Detail)) continue;
                if (it.Label.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(it.Detail, qualifier + it.Label, StringComparison.OrdinalIgnoreCase)) continue;
                it.InsertText = it.Label;
                it.Label = it.Detail;
            }
        }

        /// <summary>
        /// The items whose label starts with the qualifier, or the list unchanged when none do (never blanks
        /// an otherwise-working list). Drops anything that reached the list without the qualifier.
        /// </summary>
        public static List<LspClient.CompletionItemInfo> Scope(List<LspClient.CompletionItemInfo> items, string qualifier)
        {
            if (items == null || items.Count == 0 || string.IsNullOrEmpty(qualifier)) return items;
            var scoped = items.FindAll(it =>
                it != null && !string.IsNullOrEmpty(it.Label) &&
                it.Label.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase));
            return scoped.Count > 0 ? scoped : items;
        }
    }
}
