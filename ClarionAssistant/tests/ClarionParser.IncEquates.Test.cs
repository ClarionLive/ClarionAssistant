// File-level EQUATEs in a .inc (declared outside any CLASS body) must become symbols.
//
// Compiles the REAL CodeGraph\Parsing\ClarionParser.cs (+ ClarionBuiltins, the models and EncodingHelper) and
// runs ParseIncFile over a synthetic .inc written to a temp dir. Before the fix, ParseIncFile only recognised
// CLASS / INTERFACE definitions, their bodies and INCLUDE lines outside a class, so an equate declared above
// (or after) a CLASS never reached the index and so never reached bare-prefix completion.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionCodeGraph.Parsing;
using ClarionCodeGraph.Parsing.Models;

static class ClarionParserIncEquatesTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static int Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-incequ-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string inc = Path.Combine(dir, "Widgets.inc");
            File.WriteAllText(inc, string.Join("\r\n", new[]
            {
                "! Option flags for the widget class",                 // 1
                "WIDGET_OPT_ENCRYPT    EQUATE( 0001h )  ! must be encrypted",   // 2
                "WIDGET_OPT_CACHED     EQUATE(2)",                       // 3
                "!WIDGET_OPT_COMMENTED EQUATE(3)",                       // 4
                "  WIDGET_OPT_INDENTED EQUATE(4)",                       // 5  indented: not a label
                "EVENT:WidgetChanged   EQUATE(0600h)",                   // 6  colon-qualified name
                "",                                                      // 7
                "  OMIT('***')",                                         // 8
                "WIDGET_OPT_OMITTED    EQUATE(5)",                       // 9
                "***",                                                   // 10
                "",                                                      // 11
                "WidgetClass           CLASS,TYPE",                      // 12
                "  Count                 LONG",                          // 13  (project mode wants members indented, as app-generated .inc does)
                "  CLASS_MODE            EQUATE(9)",                     // 14  an equate INSIDE the class body
                "  Init                  PROCEDURE()",                   // 15
                "                      END",                             // 16
                "",                                                      // 17
                "WIDGET_OPT_AFTER      EQUATE(6)",                       // 18 after the class closed
                ""
            }));

            foreach (bool lib in new[] { false, true })
            {
                string mode = lib ? "library mode" : "project mode";
                var parser = new ClarionParser { LibraryMode = lib };
                var syms = parser.ParseIncFile(inc, 1).Symbols;
                var equ = syms.Where(s => s.Params == "EQUATE" && s.Scope == "global").ToDictionary(s => s.Name, StringComparer.Ordinal);

                Check(equ.ContainsKey("WIDGET_OPT_ENCRYPT") && equ["WIDGET_OPT_ENCRYPT"].LineNumber == 2, "1" + (lib ? "b" : "a"),
                      mode + ": an equate above the CLASS is captured at its own line");
                Check(equ.ContainsKey("WIDGET_OPT_CACHED"), "2" + (lib ? "b" : "a"), mode + ": a second one, no inner spaces");
                Check(equ.ContainsKey("EVENT:WidgetChanged"), "3" + (lib ? "b" : "a"), mode + ": a colon-qualified name");
                Check(equ.ContainsKey("WIDGET_OPT_AFTER") && equ["WIDGET_OPT_AFTER"].LineNumber == 18, "4" + (lib ? "b" : "a"),
                      mode + ": an equate after the CLASS has closed");
                Check(equ.Values.All(s => s.Type == "variable" && s.Scope == "global" && s.FilePath == inc && s.ProjectId == 1),
                      "5" + (lib ? "b" : "a"), mode + ": each is a global variable symbol of this file and project");
                ClarionSymbol first;
                Check(equ.TryGetValue("WIDGET_OPT_ENCRYPT", out first) && first.SourcePreview != null && first.SourcePreview.StartsWith("WIDGET_OPT_ENCRYPT"),
                      "6" + (lib ? "b" : "a"), mode + ": the declaration line is kept as the preview");
                Check(!equ.ContainsKey("WIDGET_OPT_COMMENTED"), "7" + (lib ? "b" : "a"), mode + ": a commented-out equate is not a symbol");
                Check(!equ.ContainsKey("WIDGET_OPT_INDENTED"), "8" + (lib ? "b" : "a"), mode + ": an indented line is not a label, so not a symbol");
                Check(!equ.ContainsKey("WIDGET_OPT_OMITTED"), "9" + (lib ? "b" : "a"), mode + ": an equate inside an OMIT block is skipped");
                Check(equ.Count == 4, "10" + (lib ? "b" : "a"), mode + ": exactly the four real equates - got " + equ.Count);
                Check(!equ.ContainsKey("CLASS_MODE") && syms.Any(s => s.Name == "WidgetClass.CLASS_MODE" && s.Scope == "class"),
                      "12" + (lib ? "b" : "a"), mode + ": an equate inside the CLASS body stays a class member, not a global");
                Check(syms.Any(s => s.Name == "WidgetClass" && s.Type == "class") &&
                      syms.Any(s => s.Name == "WidgetClass.Init" && s.Type == "procedure") &&
                      syms.Any(s => s.Name == "WidgetClass.Count"),
                      "11" + (lib ? "b" : "a"), mode + ": the CLASS, its method and its member are still indexed");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
