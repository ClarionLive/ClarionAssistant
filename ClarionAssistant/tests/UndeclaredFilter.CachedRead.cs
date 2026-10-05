// UndeclaredFilter.CachedRead.cs - ticket 2abfbba2: a 'not declared' warning CA's filter clears must not come back
// through the cached-diagnostics read.
//
// THE DEFECT. SharedLspBridge.DropUndeclaredWeCanResolve drops the server's false "'X' is not declared in this
// file." for an .app global. When it dropped EVERY entry, the waited answer was empty, and
// ModernEmbeditorDiagnostics' settle loop (an empty answer may be a premature publish) read
// SharedLspBridge.GetCachedDiagnostics, which was the RAW cache, and painted the warnings back. That was the
// GlobalRequest squiggle in the CA Editor on PRM002004.clw. The status pill (AssistantChatControl.PollLspUi)
// counts warnings from the same accessor.
//
// Driven by UndeclaredFilter.CachedReadTest.ps1, which compiles this with the real, unmodified
// Services\ModernEmbeditorDiagnostics.cs against the real clarion-mcp-server.exe (SharedLspBridge, LspClient,
// the filter). The language server is fixtures\lsp-undeclared-filter\fake-lsp.js.
//
//   onlyglobal.clw  only 'GlobRes' (a global in prog.clw) -> 0 markers, 0 entries from the pill's read
//   mixed.clw       'GlobRes' + 'NoSuchThing'             -> NoSuchThing still shows (negative control)
//   mechanism       the raw LspClient cache does hold 'GlobRes', so the 0 is the filter's work
//
// Args: <fake-lsp.js> <fixture dir>. Exit: 0 pass, 1 fail, 2 could-not-run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;

namespace ClarionAssistant.Services
{
    // The Embeditor's real-module context; the CA Editor passes none. Only the two members ComputeAsync uses.
    public sealed class EmbedLspContext
    {
        public int LineOffsetFor(string buffer) { return 0; }
        public string WrapBuffer(string buffer) { return buffer; }
    }
}

static class UndeclaredFilterCachedRead
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) Failures.Add(message);
    }

    static string Messages(IEnumerable<LspClient.DiagnosticEntry> entries)
    {
        return entries == null ? "(null)" : "[" + string.Join(" | ", entries.Select(e => e.Message)) + "]";
    }

    static string Messages(List<Dictionary<string, object>> markers)
    {
        return markers == null ? "(null)" : "[" + string.Join(" | ", markers.Select(m => m["message"])) + "]";
    }

    static List<Dictionary<string, object>> Markers(string file, out string waitEnd)
    {
        string text = File.ReadAllText(file);
        int lines = text.Split('\n').Length;
        var timing = new ModernEmbeditorDiagnostics.Timing();
        // The CA Editor overlay's shape: the whole file is one range, no real-module context.
        var markers = ModernEmbeditorDiagnostics.ComputeAsync(file, text, new List<int[]> { new[] { 1, lines } },
                                                              null, timing).GetAwaiter().GetResult();
        waitEnd = timing.WaitEnd;
        return markers;
    }

    static int Main(string[] args)
    {
        string fakeJs = args[0], dir = args[1];
        string onlyGlobal = Path.Combine(dir, "onlyglobal.clw"), mixed = Path.Combine(dir, "mixed.clw");

        var client = new LspClient();
        if (!client.Start(fakeJs, new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri, "undeclared-filter"))
        {
            Console.WriteLine("COULD NOT RUN: the scripted language server did not start (" + client.LastSpawnError + ")");
            return 2;
        }
        try
        {
            if (!SharedLspBridge.IsRunning) { Console.WriteLine("COULD NOT RUN: SharedLspBridge does not see the client"); return 2; }

            // ---- onlyglobal.clw: the filter clears everything ----
            string end1;
            var m1 = Markers(onlyGlobal, out end1);
            var raw1 = client.GetCachedDiagnostics(onlyGlobal);
            var pill1 = SharedLspBridge.GetCachedDiagnostics(onlyGlobal);
            Console.WriteLine("onlyglobal.clw  waitEnd=" + end1 + " markers=" + Messages(m1) + " pill=" + Messages(pill1) + " raw=" + Messages(raw1));

            Check(raw1 != null && raw1.Count == 1 && raw1[0].Message.Contains("'GlobRes'"),
                "mechanism: the raw LspClient cache should hold the server's one 'GlobRes' warning, got " + Messages(raw1)
                + " - the fake did not publish, so nothing below is about the filter");
            Check(m1 != null && m1.Count == 0,
                "onlyglobal.clw: expected 0 markers (GlobRes is a global in prog.clw), got " + Messages(m1)
                + " - the settle loop served the unfiltered cache (waitEnd=" + end1 + ")");
            Check(pill1 != null && pill1.Count == 0,
                "onlyglobal.clw: the pill's read (SharedLspBridge.GetCachedDiagnostics) should give 0 entries, got " + Messages(pill1));

            // ---- mixed.clw: an unresolvable name still shows ----
            string end2;
            var m2 = Markers(mixed, out end2);
            var pill2 = SharedLspBridge.GetCachedDiagnostics(mixed);
            Console.WriteLine("mixed.clw       waitEnd=" + end2 + " markers=" + Messages(m2) + " pill=" + Messages(pill2));

            Check(m2 != null && m2.Count == 1 && ((string)m2[0]["message"]).Contains("'NoSuchThing'"),
                "mixed.clw: expected exactly the NoSuchThing marker, got " + Messages(m2));
            Check(pill2 != null && pill2.Count == 1 && pill2[0].Message.Contains("'NoSuchThing'"),
                "mixed.clw: the pill's read should give exactly NoSuchThing, got " + Messages(pill2));
        }
        finally { try { client.Stop(); } catch { } }

        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
