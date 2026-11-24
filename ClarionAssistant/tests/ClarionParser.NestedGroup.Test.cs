// A GROUP/QUEUE nested inside a GROUP/QUEUE/FILE body in a DATA section must not end the outer structure.
//
// THE BUG. The DATA scan skipped every line inside a structure body, the nested GROUP's opening line
// included, without counting it - but the nested GROUP's END still lowered the depth. So that END closed the
// OUTER structure, and every field after it was emitted as a variable of the enclosing scope:
//
//   ErrQType     QUEUE,TYPE
//   Stamp          GROUP
//   Hour             SHORT
//                  END              <- closed ErrQType here
//   Number         LONG             <- emitted as a global variable "Number"
//                END
//
// A global-data INCLUDE (pass 2d) turned such fields into scope='global' rows, so a name lookup for
// "Number" found a field that is never addressable on its own. Procedure-local and module-level DATA had the
// same leak.
//
// Compiles the REAL ClarionParser.cs and covers ParseGlobalDataInclude (global data), ParseMemberFile
// procedure-local DATA and ParseMemberFile module-level DATA (a FILE whose RECORD holds a GROUP).
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionCodeGraph.Parsing;
using ClarionCodeGraph.Parsing.Models;

static class ClarionParserNestedGroupTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static void Write(string path, params string[] lines)
    {
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
    }

    static HashSet<string> Variables(IEnumerable<ClarionSymbol> symbols, string scope)
    {
        return new HashSet<string>(
            symbols.Where(s => s.Type == "variable" && s.Scope == scope).Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    static void Expect(string id, string where, HashSet<string> vars, string[] present, string[] absent)
    {
        foreach (string p in present)
            Check(vars.Contains(p), id + "." + p, where + ": " + p + " is a variable");
        foreach (string a in absent)
            Check(!vars.Contains(a), id + ".no-" + a, where + ": the field " + a + " is not a variable");
    }

    static int Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-nestedgroup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            // 1) A file INCLUDEd into a PROGRAM's global data section.
            string inc = Path.Combine(dir, "Globals.inc");
            Write(inc,
                "ErrQType            QUEUE,TYPE                 ! a type: the indexer drops the row itself",
                "Stamp                 GROUP",
                "Hour                    SHORT",
                "Minute                  SHORT",
                "                      END",
                "Source                STRING(64)",
                "Number                LONG",
                "                    END",
                "Counter             LONG",
                "Totals              GROUP,PRE(Tot)",
                "Inner                 GROUP,DIM(2)             ! nested, with an attribute",
                "Amount                  DECIMAL(9,2)",
                "                      END",
                "Count                 LONG",
                "                    END",
                "Ready               BYTE",
                "Holder              QUEUE",
                "Sub                   GROUP(SomeGroupType) END ! nested, closes on its own line",
                "Tail                  LONG",
                "                    END",
                "Others              QUEUE",
                "Pair                  GROUP",
                "Left                    LONG",
                "                      .                        ! period terminator",
                "Right                 LONG",
                "                    .",
                "Last                LONG");
            var g = Variables(new ClarionParser().ParseGlobalDataInclude(inc, 1).Symbols, "global");
            Expect("global", "global-data include", g,
                new[] { "ErrQType", "Counter", "Totals", "Ready", "Holder", "Others", "Last" },
                new[] { "Stamp", "Hour", "Minute", "Source", "Number", "Inner", "Amount", "Count", "Sub", "Tail",
                        "Pair", "Left", "Right" });

            // 2) Procedure-local DATA.
            string clw = Path.Combine(dir, "Locals.clw");
            Write(clw,
                "  MEMBER('Prog')",
                "  MAP",
                "  END",
                "MyProc               PROCEDURE",
                "Rec                    GROUP",
                "Head                     GROUP",
                "First                      LONG",
                "                         END",
                "Second                   LONG",
                "                       END",
                "After                  LONG",
                "  CODE",
                "  After = 1");
            var l = Variables(new ClarionParser().ParseMemberFile(clw, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Symbols, "local");
            Expect("local", "procedure-local DATA", l, new[] { "Rec", "After" }, new[] { "Head", "First", "Second" });

            // 3) Module-level DATA: a FILE whose RECORD holds a GROUP, and a variable after it.
            string mod = Path.Combine(dir, "Module.clw");
            Write(mod,
                "  MEMBER('Prog')",
                "Customers            FILE,DRIVER('TOPSPEED'),PRE(Cus)",
                "Record                 RECORD",
                "Address                  GROUP",
                "Street                     STRING(40)",
                "                         END",
                "Phone                    STRING(20)",
                "                       END",
                "                     END",
                "ModuleVar            LONG",
                "  MAP",
                "  END",
                "MyProc               PROCEDURE",
                "  CODE");
            var m = Variables(new ClarionParser().ParseMemberFile(mod, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Symbols, "module");
            Expect("module", "module-level DATA", m, new[] { "ModuleVar" }, new[] { "Address", "Street", "Phone" });
        }
        catch (Exception ex)
        {
            Check(false, "threw", ex.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "PASSED - " + _assertions + " assertions"
            : "FAILED - " + Failures.Count + " of " + _assertions);
        return Failures.Count == 0 ? 0 : 1;
    }
}
