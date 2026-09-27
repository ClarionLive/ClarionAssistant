// 1c685f2e item 1: golden parity of SharedLspBridge's late merge across the LocalScopeIndex extraction.
//
// Driven by CompletionMerge.LocalParity.ps1, which compiles this against the REAL clarion-mcp-server.exe
// (SharedLspBridge + LocalScopeIndex + LspClient from their one home) and runs the bundled LspClient
// against fake-lsp.js answering an EMPTY completion list and a null hover. So everything in the output
// comes from the host's own merges: buffer-local items (the code that moved into LocalScopeIndex),
// ABC standard globals, and nothing from any DB (no .codegraph.db is reachable from the temp dir, and
// items whose detail marks them as a ClarionGraph/CodeGraph row are dropped so a machine's library DB
// cannot decide the outcome).
//
// The golden file was generated ONCE at master 2fcb940, before the refactor, with "generate". "check"
// compares the current output to it. EXPECTED DIFFS are listed in ExpectedAdded below: items the
// refactor adds on purpose (procedure parameters; the owning procedure's locals inside a local-class
// method, test 1.5). Everything else must be byte-identical, in order.
//
// Args: <fake-lsp.js> <work dir> <two-procs.clw> <golden.json> generate|check
// Exit: 0 pass, 1 fail, 2 could-not-run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

static class CompletionMergeLocalParity
{
    // Completion carets: the caret sits at the END of the first line whose trimmed text equals the key.
    static readonly string[] CompletionCarets =
    {
        "Loc", "Mo", "Gr", "PG:", "MyGrp.", "pI", "Pr", "MG:",   // ProcA CODE
        "Rt",                                                      // RtnA body
        "LO", "pM", "In", "SELF.",                                 // ThisWindow.Init body
        "Lo", "DO R", "DO RtnB",                                   // ProcB CODE
    };

    // Hover carets: (trimmed line text, word) - the caret sits on the word's first character + 1.
    static readonly string[][] HoverCarets =
    {
        new[] { "LOC:Count = pId + ModCounter", "LOC:Count" },
        new[] { "LOC:Count = pId + ModCounter", "ModCounter" },
        new[] { "LOC:Count = pId + ModCounter", "pId" },
        new[] { "ReturnValue = LocalHelper(pMode)", "LocalHelper" },
        new[] { "ReturnValue = LocalHelper(pMode)", "pMode" },
        new[] { "ReturnValue = LocalHelper(pMode)", "ReturnValue" },
        new[] { "DO RtnB", "RtnB" },
        new[] { "RtnOnly = 1", "RtnOnly" },
    };

    // Items the refactor ADDS on purpose, per completion caret (label). Everything else must match.
    static readonly Dictionary<string, string[]> ExpectedAdded = new Dictionary<string, string[]>
    {
        // Procedure PROTOTYPE parameters (never parsed locally before 1c685f2e).
        { "pI", new[] { "pId" } },
        // Inside ThisWindow.Init: its own parameter, and ProcA's locals (test 1.5: a local class's
        // methods see the owning procedure's data).
        { "pM", new[] { "pMode" } },
        { "LO", new[] { "LOC:Count" } },
    };

    // Hover results the refactor changes on purpose: key "line|word" -> the new contents must contain this.
    static readonly Dictionary<string, string> ExpectedHoverChange = new Dictionary<string, string>
    {
        { "LOC:Count = pId + ModCounter|pId", "pId  LONG" },
        { "ReturnValue = LocalHelper(pMode)|pMode", "pMode  BYTE" },
        { "DO RtnB|RtnB", "RtnB" },
    };

    static int Main(string[] args)
    {
        string serverJs = args[0], work = args[1], fixture = args[2], golden = args[3];
        bool generate = args.Length > 4 && args[4] == "generate";

        string buffer = File.ReadAllText(fixture);
        string file = Path.Combine(work, "two-procs.clw");
        File.WriteAllText(file, buffer);
        string[] lines = buffer.Replace("\r\n", "\n").Split('\n');

        SharedLspBridge.CodeGraphDbPathProvider = () => null;
        NoLibraryDb();

        var client = new LspClient();
        bool started;
        try { started = client.Start(serverJs, new Uri(work + "\\").AbsoluteUri.TrimEnd('/'), "parity"); }
        catch (Exception ex) { Console.WriteLine("COULD NOT RUN: LspClient.Start threw: " + ex.Message); return 2; }
        if (!started) { Console.WriteLine("COULD NOT RUN: LspClient did not start the stand-in server (" + (client.LastSpawnError ?? "handshake failed") + ")."); return 2; }
        typeof(LspClient).GetField("_active", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, client);

        var result = new Dictionary<string, object>();
        try
        {
            if (!SharedLspBridge.IsRunning || SharedLspBridge.IsSharedActive)
            { Console.WriteLine("COULD NOT RUN: the bundled client is not the active LSP."); return 2; }

            var comp = new Dictionary<string, object>();
            foreach (var key in CompletionCarets)
            {
                int line = Array.FindIndex(lines, l => l.Trim() == key);
                if (line < 0) { Console.WriteLine("COULD NOT RUN: caret line '" + key + "' not in the fixture."); return 2; }
                int ch = lines[line].TrimEnd('\r').Length;
                NoLibraryDb();
                var items = SharedLspBridge.GetCompletion(file, line, ch, 5000, buffer) ?? new List<LspClient.CompletionItemInfo>();
                comp[key] = items.Where(it => !FromDb(it)).Select(Row).ToList();
            }
            result["completion"] = comp;

            var hov = new Dictionary<string, object>();
            foreach (var hc in HoverCarets)
            {
                int line = Array.FindIndex(lines, l => l.Trim() == hc[0]);
                if (line < 0) { Console.WriteLine("COULD NOT RUN: hover line '" + hc[0] + "' not in the fixture."); return 2; }
                int ch = lines[line].IndexOf(hc[1], StringComparison.Ordinal) + 1;
                NoLibraryDb();
                var h = SharedLspBridge.GetHover(file, line, ch, buffer);
                hov[hc[0] + "|" + hc[1]] = HoverText(h);
            }
            result["hover"] = hov;
        }
        finally { try { client.Dispose(); } catch { } }

        var ser = new JavaScriptSerializer();
        string json = ser.Serialize(result);
        if (generate)
        {
            File.WriteAllText(golden, Pretty(result), new UTF8Encoding(false));
            Console.WriteLine("GENERATED " + golden);
            Console.WriteLine(Pretty(result));
            return 0;
        }

        var gold = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(golden));
        var failures = new List<string>();
        int assertions = 0;

        var gComp = (Dictionary<string, object>)gold["completion"];
        var cComp = (Dictionary<string, object>)result["completion"];
        foreach (var key in CompletionCarets)
        {
            assertions++;
            var g = ((System.Collections.ArrayList)gComp[key]).Cast<string>().ToList();
            var c = ((List<string>)cComp[key]).ToList();
            string[] added;
            ExpectedAdded.TryGetValue(key, out added);
            added = added ?? new string[0];
            foreach (var a in added)
                if (!c.Any(r => r.StartsWith(a + " |", StringComparison.Ordinal)))
                    failures.Add("caret '" + key + "': expected new item '" + a + "' is missing");
            var stripped = c.Where(r => !added.Any(a => r.StartsWith(a + " |", StringComparison.Ordinal))).ToList();
            if (!stripped.SequenceEqual(g))
                failures.Add("caret '" + key + "': merged list drifted from master.\n      golden:  " +
                             string.Join("\n               ", g) + "\n      current: " + string.Join("\n               ", c));
        }

        var gHov = (Dictionary<string, object>)gold["hover"];
        var cHov = (Dictionary<string, object>)result["hover"];
        foreach (var hc in HoverCarets)
        {
            assertions++;
            string key = hc[0] + "|" + hc[1];
            string g = gHov[key] as string, c = cHov[key] as string;
            string want;
            if (ExpectedHoverChange.TryGetValue(key, out want))
            {
                if (c == null || c.IndexOf(want, StringComparison.Ordinal) < 0)
                    failures.Add("hover '" + key + "': expected the new hover to contain '" + want + "', got: " + (c ?? "(null)"));
            }
            else if (g != c)
                failures.Add("hover '" + key + "' drifted from master.\n      golden:  " + (g ?? "(null)") + "\n      current: " + (c ?? "(null)"));
        }

        Console.WriteLine(Pretty(result));
        Console.WriteLine();
        if (failures.Count == 0) { Console.WriteLine("PASS - " + assertions + " carets match master 2fcb940 plus the listed expected diff"); return 0; }
        Console.WriteLine("FAIL - " + failures.Count + " of " + assertions + ":");
        foreach (var f in failures) Console.WriteLine("  - " + f);
        return 1;
    }

    // Pin the ClarionGraph version memo to a key no DB file exists for, so this machine's library DB
    // (if any) never reaches the merged list. Re-pinned before every call: the memo has a 20s TTL.
    static void NoLibraryDb()
    {
        var t = typeof(ClarionGraphService);
        var f = BindingFlags.NonPublic | BindingFlags.Static;
        t.GetField("_cachedVersion", f).SetValue(null, "parity-no-library-db");
        t.GetField("_cachedRoot", f).SetValue(null, null);
        t.GetField("_cachedVersionAtTicks", f).SetValue(null, DateTime.UtcNow.Ticks);
    }

    static bool FromDb(LspClient.CompletionItemInfo it)
    {
        string d = it.Detail ?? "";
        return d.IndexOf("ClarionGraph", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string Row(LspClient.CompletionItemInfo it)
    {
        return (it.Label ?? "") + " | kind=" + it.Kind + " | detail=" + (it.Detail ?? "") +
               " | doc=" + (it.Documentation ?? "") + " | insert=" + (it.InsertText ?? "");
    }

    static string HoverText(Dictionary<string, object> r)
    {
        if (r == null) return null;
        object res;
        if (!r.TryGetValue("result", out res) || res == null) return null;
        var d = res as System.Collections.IDictionary;
        if (d == null) return null;
        object c = d.Contains("contents") ? d["contents"] : null;
        if (c is string) return (string)c;
        var cd = c as System.Collections.IDictionary;
        if (cd != null && cd.Contains("value")) return cd["value"] as string;
        return c == null ? null : new JavaScriptSerializer().Serialize(c);
    }

    // Stable, diff-friendly JSON: one completion row / hover per line.
    static string Pretty(Dictionary<string, object> result)
    {
        var ser = new JavaScriptSerializer();
        var sb = new StringBuilder();
        sb.Append("{\n  \"completion\": {\n");
        var comp = (Dictionary<string, object>)result["completion"];
        int i = 0;
        foreach (var kv in comp)
        {
            sb.Append("    ").Append(ser.Serialize(kv.Key)).Append(": [");
            var rows = ((IEnumerable<string>)kv.Value).ToList();
            for (int j = 0; j < rows.Count; j++)
                sb.Append(j == 0 ? "\n" : ",\n").Append("      ").Append(ser.Serialize(rows[j]));
            sb.Append(rows.Count > 0 ? "\n    ]" : "]").Append(++i < comp.Count ? ",\n" : "\n");
        }
        sb.Append("  },\n  \"hover\": {\n");
        var hov = (Dictionary<string, object>)result["hover"];
        i = 0;
        foreach (var kv in hov)
            sb.Append("    ").Append(ser.Serialize(kv.Key)).Append(": ").Append(ser.Serialize(kv.Value))
              .Append(++i < hov.Count ? ",\n" : "\n");
        sb.Append("  }\n}\n");
        return sb.ToString();
    }
}
