// UndeclaredFilter.ProgramGlobals.cs - ticket e2f87efb: CA's 'not declared' filter must clear the global FILE
// labels of a PROGRAM whose CLASS method prototypes sit at column 1.
//
// THE DEFECT. SharedLspBridge.ResolveNamesFromProgramGlobals looks for a column-1 label in the PROGRAM's
// declaration section, which it took to end at ClarionParser.FindMainTailStart. That also stops at the first
// column-1 "X PROCEDURE" line, and a template-generated PROGRAM writes CLASS method prototypes there
// (PRM002.clw line 93, inside a CLASS,TYPE). So the range ended at line 93 and the FILE labels from line 2682
// on (ADDONS, PINVDET, ...) stayed flagged on PRM002022.clw / PRM002023.clw.
//
// Driven by UndeclaredFilter.ProgramGlobalsTest.ps1 against the real clarion-mcp-server.exe (SharedLspBridge,
// LspClient, the filter). The language server is fixtures\lsp-undeclared-filter\fake-lsp.js, publishing the
// names listed on bigmodule.clw's "! fake-lsp-undeclared:" line; bigprog.clw is the PROGRAM.
//
//   GlobBefore     declared before the CLASS                 -> cleared (passes on master too)
//   ADDONS         FILE after the column-1 method prototype -> cleared (red on master)
//   PINVDET        FILE 'Pinvdet', after a column-1 'CODE STRING(16)' field -> cleared, case-insensitively
//   FxTailLocal    declared only after the global CODE      -> still flagged (negative control)
//   FxNoSuchThing  declared nowhere                         -> still flagged (negative control)
//
// Args: <fake-lsp.js> <fixture dir>. Exit: 0 pass, 1 fail, 2 could-not-run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;

static class UndeclaredFilterProgramGlobals
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) Failures.Add(message);
    }

    static bool Flags(IEnumerable<LspClient.DiagnosticEntry> entries, string name)
    {
        return entries != null && entries.Any(e => e.Message != null && e.Message.Contains("'" + name + "'"));
    }

    static string Messages(IEnumerable<LspClient.DiagnosticEntry> entries)
    {
        return entries == null ? "(null)" : "[" + string.Join(" | ", entries.Select(e => e.Message)) + "]";
    }

    static int Main(string[] args)
    {
        string fakeJs = args[0], dir = args[1];
        string module = Path.Combine(dir, "bigmodule.clw");

        var client = new LspClient();
        if (!client.Start(fakeJs, new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri, "undeclared-program-globals"))
        {
            Console.WriteLine("COULD NOT RUN: the scripted language server did not start (" + client.LastSpawnError + ")");
            return 2;
        }
        try
        {
            if (!SharedLspBridge.IsRunning) { Console.WriteLine("COULD NOT RUN: SharedLspBridge does not see the client"); return 2; }

            var filtered = SharedLspBridge.GetDiagnostics(module, 10000);
            var raw = client.GetCachedDiagnostics(module);
            Console.WriteLine("raw      = " + Messages(raw));
            Console.WriteLine("filtered = " + Messages(filtered == null ? null : filtered.Entries)
                + " pending=" + (filtered != null && filtered.Pending));

            var all = new[] { "GlobBefore", "ADDONS", "PINVDET", "FxTailLocal", "FxNoSuchThing" };
            Check(raw != null && all.All(n => Flags(raw, n)),
                "mechanism: the raw cache should hold all five warnings, got " + Messages(raw)
                + " - the fake did not publish them, so nothing below is about the filter");
            if (filtered == null || filtered.Pending)
            {
                Console.WriteLine("COULD NOT RUN: the diagnostics wait did not complete");
                return 2;
            }
            var f = filtered.Entries;

            Check(!Flags(f, "GlobBefore"), "GlobBefore (declared before the CLASS) should be cleared, got " + Messages(f));
            Check(!Flags(f, "ADDONS"),
                "ADDONS (a global FILE after a CLASS whose method prototype is at column 1) should be cleared, got "
                + Messages(f) + " - the declaration range ended at the column-1 PROCEDURE line");
            Check(!Flags(f, "PINVDET"),
                "PINVDET (FILE 'Pinvdet', after a column-1 'CODE STRING(16)' field) should be cleared, got " + Messages(f));
            Check(Flags(f, "FxTailLocal"),
                "negative control: FxTailLocal is declared only after the global CODE and must stay flagged, got " + Messages(f));
            Check(Flags(f, "FxNoSuchThing"),
                "negative control: FxNoSuchThing is declared nowhere and must stay flagged, got " + Messages(f));
        }
        finally { try { client.Stop(); } catch { } }

        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var x in Failures) Console.WriteLine("  - " + x);
        return 1;
    }
}
