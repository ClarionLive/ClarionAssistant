using System;
using System.Collections.Generic;
using System.Linq;
using ClarionAssistant.Services;

// Colon-qualified completion dropped every language-server item once CodeGraph supplied one match.
//
// For "Glob:S" the server answers with the name after the qualifier as the label ("Svc") and the
// qualified name as the detail ("Glob:Svc"). The host then merged CodeGraph's "Glob:*" symbols (full
// labels) and kept only labels starting with "Glob:" - so "Svc" went, and with it every PROGRAM global
// CodeGraph does not know about (e.g. one declared in an INCLUDEd header). Hover still resolved it.
// SharedLspBridge.GetCompletion now calls ColonQualifierScope.NormalizeServerItems before its host
// merges and ColonQualifierScope.Scope after them; both are exercised here with no server.
//
// Run:  tests\Run-Tests.ps1
static class ColonQualifierScopeTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static LspClient.CompletionItemInfo Item(string label, int kind, string detail, string insert)
    {
        return new LspClient.CompletionItemInfo { Label = label, Kind = kind, Detail = detail, InsertText = insert };
    }

    static string Dump(IEnumerable<LspClient.CompletionItemInfo> items)
    {
        return string.Join(", ", items.Select(i => i.Label + "/" + i.InsertText));
    }

    static int Main()
    {
        // --- the bug: server qualifier items alongside a CodeGraph full-label item -------------------
        var list = new List<LspClient.CompletionItemInfo>
        {
            Item("Svc", 6, "Glob:Svc", "vc"),            // server: only it knows this one
            Item("Count", 6, "Glob:Count", "ount"),
            Item("ACOS", 3, "ACOS(real)", "ACOS"),       // an item without the qualifier
        };
        ColonQualifierScope.NormalizeServerItems(list, "Glob:");
        list.Add(Item("Glob:Own", 6, "LONG", "Own"));     // what the CodeGraph merge adds
        var result = ColonQualifierScope.Scope(list, "Glob:");
        var svc = result.FirstOrDefault(i => i.Label == "Glob:Svc");
        Ok("a server-only qualifier item survives scoping next to a CodeGraph match", svc != null, Dump(result));
        Ok("  ... relabelled to the qualified name, inserting the part after the qualifier",
            svc != null && svc.InsertText == "Svc" && svc.Detail == "Glob:Svc", svc == null ? null : svc.InsertText);
        Ok("  ... and the CodeGraph item is kept too", result.Any(i => i.Label == "Glob:Own"), Dump(result));
        Ok("  ... while an item without the qualifier is scoped away", !result.Any(i => i.Label == "ACOS"), Dump(result));

        // --- the host merges dedupe by label: normalizing first must make them skip their own copy --------
        // MergeQualifiedFieldCompletions seeds a label set from the list, and LocalScopeIndex.AddQualifiedFields
        // adds `pre + ":" + field` only when that label is new (pre passed WITHOUT the colon). Before the server
        // item is normalized its bare label "Name" never matches, so a GROUP,PRE(Cus) field was listed twice.
        var field = new List<LspClient.CompletionItemInfo> { Item("Name", 6, "Cus:Name", "ame") };
        ColonQualifierScope.NormalizeServerItems(field, "Cus:");
        var seen = new HashSet<string>(field.Select(i => i.Label), StringComparer.OrdinalIgnoreCase);
        Ok("a normalized server field already holds the label a qualified-field merge would add",
            !seen.Add("Cus" + ":" + "Name"), string.Join(", ", seen));

        // --- what it was: scoping the raw server labels loses them (documents the defect) -----------
        var raw = new List<LspClient.CompletionItemInfo> { Item("Svc", 6, "Glob:Svc", "vc"), Item("Glob:Own", 6, "LONG", "Own") };
        Ok("without normalizing, scoping drops the server item (the reported symptom)",
            !ColonQualifierScope.Scope(raw, "Glob:").Any(i => i.Detail == "Glob:Svc"), null);

        // --- the merge dedupes by label: the normalized label must equal the CodeGraph name ---------
        var one = new List<LspClient.CompletionItemInfo> { Item("Svc", 6, "Glob:Svc", "vc") };
        ColonQualifierScope.NormalizeServerItems(one, "glob:");
        Ok("the qualified label keeps the server's casing (case-insensitive qualifier match)",
            one[0].Label == "Glob:Svc", one[0].Label);

        // --- only true qualifier items are rewritten ---------------------------------------------------
        var keep = new List<LspClient.CompletionItemInfo>
        {
            Item("Glob:Own", 6, "Glob:Own", "Own"),      // already qualified
            Item("Local", 6, "LONG", "Local"),           // detail is a type, not the qualified name
            Item("Other", 6, "Pre:Other", "ther"),       // another qualifier's item
            Item("NoDetail", 6, null, "NoDetail"),
        };
        ColonQualifierScope.NormalizeServerItems(keep, "Glob:");
        Ok("an already-qualified label is left alone", keep[0].Label == "Glob:Own" && keep[0].InsertText == "Own", Dump(keep));
        Ok("an item whose detail is a type is left alone", keep[1].Label == "Local" && keep[1].InsertText == "Local", Dump(keep));
        Ok("another qualifier's item is left alone", keep[2].Label == "Other" && keep[2].InsertText == "ther", Dump(keep));
        Ok("an item without a detail is left alone", keep[3].Label == "NoDetail", Dump(keep));

        // --- a nested prefixed field: the server's label itself carries a colon ------------------------
        var nested = new List<LspClient.CompletionItemInfo> { Item("GLO:SessionId", 6, "TGLO:GLO:SessionId", "GLO:SessionId") };
        ColonQualifierScope.NormalizeServerItems(nested, "TGLO:");
        Ok("a nested field becomes TGLO:GLO:SessionId, inserting GLO:SessionId",
            nested[0].Label == "TGLO:GLO:SessionId" && nested[0].InsertText == "GLO:SessionId", Dump(nested));

        // --- Scope never blanks a list -----------------------------------------------------------------
        var none = new List<LspClient.CompletionItemInfo> { Item("ABS", 3, "ABS(real)", "ABS") };
        Ok("no qualifier match -> the list comes back unchanged", ColonQualifierScope.Scope(none, "Glob:").Count == 1, null);
        Ok("null and empty inputs are tolerated",
            ColonQualifierScope.Scope(null, "Glob:") == null &&
            ColonQualifierScope.Scope(new List<LspClient.CompletionItemInfo>(), "Glob:").Count == 0, null);
        ColonQualifierScope.NormalizeServerItems(null, "Glob:");
        ColonQualifierScope.NormalizeServerItems(new List<LspClient.CompletionItemInfo> { null }, "Glob:");
        Ok("NormalizeServerItems tolerates null list and null items", true, null);

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
