// 1c685f2e item 3: LiveDictionaryIndex (PRE:Field / table names / hover from the live dictionary
// snapshot, SchemaGraph as the fallback) and ClarionKeywordIndex (keyword/built-in names + categories).
// phase1-tests.md 3.1-3.10. Compiles the REAL Services\LiveDictionaryIndex.cs; the SchemaGraph fallback
// is a counting stub delegate, so the live layer alone decides whenever a snapshot exists.
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClarionAssistant.Services;

static class LiveDictionaryIndexTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static int _schemaCalls;

    static List<LspClient.CompletionItemInfo> Schema()
    {
        Interlocked.Increment(ref _schemaCalls);
        return new List<LspClient.CompletionItemInfo> { new LspClient.CompletionItemInfo { Label = "INV:OnlyInSchema", Kind = 5, InsertText = "OnlyInSchema" } };
    }

    static ClarionAppDataReader.FieldDef F(string name, string type, params ClarionAppDataReader.FieldDef[] children)
    {
        var f = new ClarionAppDataReader.FieldDef { Name = name, Type = type };
        if (children.Length > 0) f.Children = children.ToList();
        return f;
    }

    static Dictionary<string, ClarionAppDataReader.TableDef> Snapshot(bool withReorder)
    {
        var inv = new ClarionAppDataReader.TableDef { Name = "Inventory", Prefix = "INV", Driver = "TOPSPEED" };
        inv.Fields.Add(F("Qty", "LONG"));
        inv.Fields.Add(F("Descr", "STRING(40)"));
        inv.Fields.Add(F("Addr", "GROUP", F("Street", "STRING(30)")));
        if (withReorder) inv.Fields.Add(F("Reorder", "BYTE"));
        var key = new ClarionAppDataReader.KeyDef { Name = "KeyQty", Primary = true };
        key.Components.Add(F("Qty", "LONG"));
        inv.KeyDefs.Add(key);
        var cus = new ClarionAppDataReader.TableDef { Name = "Customer", Prefix = "CUS" };
        cus.Fields.Add(F("Name", "STRING(30)"));
        return new Dictionary<string, ClarionAppDataReader.TableDef>(StringComparer.OrdinalIgnoreCase)
        { { inv.Name, inv }, { cus.Name, cus } };
    }

    static HashSet<string> Labels(IEnumerable<LspClient.CompletionItemInfo> items)
    {
        return new HashSet<string>(items.Select(i => i.Label), StringComparer.OrdinalIgnoreCase);
    }

    static int Main()
    {
        var s1 = Snapshot(false);
        var s2 = Snapshot(true);

        Console.WriteLine("dictionary");
        LiveDictionaryIndex.Publish(s1);
        int b0 = LiveDictionaryIndex.BuildCount;
        var q = LiveDictionaryIndex.CompleteQualifier("inv", "", Schema);
        var qty = q.FirstOrDefault(i => i.Label == "inv:Qty");
        Check(Labels(q).IsSupersetOf(new[] { "inv:Qty", "inv:Descr", "inv:Addr", "inv:Street", "inv:KeyQty" }) && qty != null && qty.InsertText == "Qty" && qty.Kind == 5,
              "3.1", "'inv:' (typed lower-case) -> Qty Descr Addr Street + KeyQty, label PRE:Name / insert Name: [" + string.Join(", ", q.Select(i => i.Label)) + "]");
        Check(!Labels(q).Contains("INV:OnlyInSchema") && _schemaCalls == 0, "3.2", "live present: SchemaGraph not consulted (" + _schemaCalls + " calls)");
        var partial = LiveDictionaryIndex.CompleteQualifier("INV", "st", Schema);
        Check(Labels(partial).SetEquals(new[] { "INV:Street" }), "3.1b", "partial 'st' -> INV:Street only");
        var tn = LiveDictionaryIndex.CompleteTableNames("inv", 25, Schema);
        Check(Labels(tn).SetEquals(new[] { "Inventory" }) && tn[0].Kind == 9 && _schemaCalls == 0, "3.1c", "table names from the live snapshot: " + string.Join(",", Labels(tn)));

        for (int i = 0; i < 100; i++) LiveDictionaryIndex.CompleteQualifier("INV", "", Schema);
        Check(LiveDictionaryIndex.BuildCount - b0 == 1, "3.5", "same snapshot reference: built once across 101 queries (" + (LiveDictionaryIndex.BuildCount - b0) + ")");

        LiveDictionaryIndex.Publish(s2);
        var q2 = LiveDictionaryIndex.CompleteQualifier("INV", "", Schema);
        Check(Labels(q2).Contains("INV:Reorder") && LiveDictionaryIndex.BuildCount - b0 == 2, "3.4", "a new snapshot reference: INV:Reorder, BuildCount +2 (" + (LiveDictionaryIndex.BuildCount - b0) + ")");

        var h = LiveDictionaryIndex.HoverWord("INV:Qty");
        Check(h != null && h.Markdown.Contains("LONG") && h.Markdown.Contains("Inventory") && !h.Authoritative, "3.6",
              "hover INV:Qty -> LONG + table name, not authoritative: " + (h == null ? "(null)" : h.Markdown.Replace("\n", "\\n")));
        var hw = LiveDictionaryIndex.HoverWord(LocalScopeIndex.WordAt("  x = INV:Street + 1", 10));
        Check(hw != null && hw.Markdown.Contains("STRING(30)"), "3.6b", "a GROUP member hovers via WordAt: " + (hw == null ? "(null)" : hw.Markdown.Replace("\n", "\\n")));

        LiveDictionaryIndex.Publish(null);
        var q3 = LiveDictionaryIndex.CompleteQualifier("INV", "", Schema);
        LiveDictionaryIndex.Publish(new Dictionary<string, ClarionAppDataReader.TableDef>());
        var q3b = LiveDictionaryIndex.CompleteQualifier("INV", "", Schema);
        Check(Labels(q3).Contains("INV:OnlyInSchema") && Labels(q3b).Contains("INV:OnlyInSchema"), "3.3", "no / an empty snapshot -> the SchemaGraph fallback answers");

        // 3.7: swapping snapshots under concurrent readers - every answer is exactly one snapshot's.
        LiveDictionaryIndex.Publish(s1);
        var want1 = Labels(LiveDictionaryIndex.CompleteQualifier("INV", "", Schema));
        LiveDictionaryIndex.Publish(s2);
        var want2 = Labels(LiveDictionaryIndex.CompleteQualifier("INV", "", Schema));
        int bad = 0, errors = 0, reads = 0;
        bool stop = false;
        var readers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    var got = Labels(LiveDictionaryIndex.CompleteQualifier("INV", "", Schema));
                    if (!got.SetEquals(want1) && !got.SetEquals(want2)) Interlocked.Increment(ref bad);
                    Interlocked.Increment(ref reads);
                }
                catch { Interlocked.Increment(ref errors); }
            }
        })).ToList();
        readers.ForEach(t => t.Start());
        var until = DateTime.UtcNow.AddSeconds(2);
        int swaps = 0;
        while (DateTime.UtcNow < until) { LiveDictionaryIndex.Publish(swaps % 2 == 0 ? Snapshot(false) : Snapshot(true)); swaps++; Thread.Sleep(1); }
        Volatile.Write(ref stop, true);
        readers.ForEach(t => t.Join());
        Check(bad == 0 && errors == 0 && reads > 0, "3.7", swaps + " swaps under " + reads + " reads: " + bad + " torn answers, " + errors + " exceptions");

        Console.WriteLine("keywords / built-ins");
        var kw = ClarionKeywordIndex.Complete("POPB");
        var pb = kw.FirstOrDefault(i => i.Label == "POPBIND");
        Check(pb != null && (pb.Detail ?? "").Contains("Runtime expressions") && pb.Kind == 14, "3.8", "'POPB' -> POPBIND with its category: " + (pb == null ? "(missing)" : pb.Detail));
        var hr = ClarionKeywordIndex.HoverWord("RETURN");
        var hp = ClarionKeywordIndex.HoverWord("popbind");
        Check(hr != null && hr.Markdown.Contains("RETURN") && hr.Markdown.Contains("Statement") && !hr.Authoritative &&
              hp != null && hp.Markdown.Contains("POPBIND") && hp.Markdown.Contains("Runtime expressions") && !hp.Authoritative,
              "3.9", "RETURN / POPBIND cards carry name + category, not authoritative: " + (hr == null ? "(null)" : hr.Markdown.Replace("\n", "\\n")));
        Check(ClarionKeywordIndex.HoverWord("Clip") != null && ClarionKeywordIndex.HoverWord("NotAKeyword") == null, "3.10",
              "CLIP is a built-in (so a local 'Clip' must be asked first - LocalScopeIndex answers it, test 3.10a); an unknown word is null");
        Check(ClarionBuiltins_Membership(), "3.11", "the grouped ClarionBuiltins keep the indexer's membership (IsBuiltIn/IsKeyword spot checks)");

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }

    static bool ClarionBuiltins_Membership()
    {
        return ClarionCodeGraph.Parsing.ClarionBuiltins.IsBuiltIn("popbind") && ClarionCodeGraph.Parsing.ClarionBuiltins.IsBuiltIn("GOTOXYABS")
            && ClarionCodeGraph.Parsing.ClarionBuiltins.IsKeyword("ROUTINE") && ClarionCodeGraph.Parsing.ClarionBuiltins.IsKeyword("PASCAL")
            && !ClarionCodeGraph.Parsing.ClarionBuiltins.IsBuiltIn("ROUTINE") && !ClarionCodeGraph.Parsing.ClarionBuiltins.IsKeyword("POPBIND")
            && ClarionCodeGraph.Parsing.ClarionBuiltins.CategoryOf("DATE") == "Date/Time";
    }
}
