// A CLASS / INTERFACE whose label contains a colon must be indexed (GH #246).
//
// Compiles the REAL CodeGraph\Parsing\ClarionParser.cs (+ ClarionBuiltins, the models and EncodingHelper) and
// parses a synthetic .inc and a synthetic MEMBER .clw written to a temp dir. Clarion labels may contain ':'
// (never '.'), and v5.8 taught the PROCEDURE/FUNCTION regexes that; the CLASS/INTERFACE definition regexes and
// the type slot of instance / reference declarations still matched \w+ only, so
//
//   ctQ_ActiveThreads:ThreadSafe CLASS(ctQ_ActiveThreads),TYPE,...
//
// produced no class row, its prototypes no "Class.Method" rows, and "Obj ctQ_ActiveThreads:ThreadSafe" no
// variable row - while the "ctQ_ActiveThreads:ThreadSafe.Add PROCEDURE" implementations WERE indexed, so the
// implementations had nothing to link to.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClarionCodeGraph.Parsing;
using ClarionCodeGraph.Parsing.Models;

static class ClarionParserColonClassTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static ClarionSymbol One(List<ClarionSymbol> syms, string name, string type)
    {
        return syms.FirstOrDefault(s => s.Name == name && s.Type == type);
    }

    static int Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-colonclass-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string inc = Path.Combine(dir, "Shapes.inc");
            File.WriteAllText(inc, string.Join("\r\n", new[]
            {
                "My:Shape        CLASS,TYPE,MODULE('Shapes.clw'),LINK('Shapes.clw')",   // 1
                "  Name            STRING(20)",                                          // 2
                "  Area            PROCEDURE(),LONG,VIRTUAL",                            // 3
                "                END",                                                   // 4
                "",                                                                      // 5
                "My:Circle       CLASS(My:Shape),TYPE",                                  // 6
                "  Area            PROCEDURE(),LONG,DERIVED",                            // 7
                "                END",                                                   // 8
                "",                                                                      // 9
                "My:Drawable     INTERFACE",                                             // 10
                "  Draw            PROCEDURE()",                                         // 11
                "                END",                                                   // 12
                "",                                                                      // 13
                "PlainShape      CLASS,TYPE",                                            // 14 control: no colon
                "  Area            PROCEDURE()",                                         // 15
                "                END",                                                   // 16
                ""
            }));

            foreach (bool lib in new[] { false, true })
            {
                string m = lib ? "b" : "a";
                string mode = lib ? "library mode" : "project mode";
                var syms = new ClarionParser { LibraryMode = lib }.ParseIncFile(inc, 1).Symbols;

                var shape = One(syms, "My:Shape", "class");
                Check(shape != null && shape.LineNumber == 1 && shape.Scope == "global" && shape.ParentName == null,
                      "1" + m, mode + ": 'My:Shape CLASS,TYPE' is a class at line 1");
                var circle = One(syms, "My:Circle", "class");
                Check(circle != null && circle.ParentName == "My:Shape",
                      "2" + m, mode + ": 'My:Circle CLASS(My:Shape)' is a class whose parent is My:Shape - got " +
                      (circle == null ? "no class" : "parent '" + circle.ParentName + "'"));
                Check(One(syms, "My:Drawable", "interface") != null, "3" + m, mode + ": 'My:Drawable INTERFACE' is an interface");
                Check(One(syms, "My:Shape.Area", "procedure") != null && One(syms, "My:Circle.Area", "procedure") != null,
                      "4" + m, mode + ": the colon classes' method prototypes are owner-qualified (My:Shape.Area, My:Circle.Area)");
                Check(One(syms, "My:Drawable.Draw", "procedure") != null, "5" + m, mode + ": the colon interface's method is My:Drawable.Draw");
                Check(syms.Any(s => s.Name == "My:Shape.Name"), "6" + m, mode + ": the colon class's data member is My:Shape.Name");
                Check(!syms.Any(s => s.Name == "Area" || s.Name == "Draw" || s.Name == "Name"),
                      "7" + m, mode + ": no member leaks out as an unqualified symbol");
                Check(One(syms, "PlainShape", "class") != null && One(syms, "PlainShape.Area", "procedure") != null,
                      "8" + m, mode + ": control - a colon-free class is still indexed with its method");
            }

            string clw = Path.Combine(dir, "Shapes.clw");
            File.WriteAllText(clw, string.Join("\r\n", new[]
            {
                "  MEMBER()",                                        // 1
                "  INCLUDE('Shapes.inc'),ONCE",                      // 2
                "  MAP",                                             // 3
                "  END",                                             // 4
                "ModObj          My:Shape",                          // 5  module instance of a colon class
                "ModRef          &My:Shape",                         // 6  module reference to a colon class
                "",                                                  // 7
                "My:Shape.Area   PROCEDURE()",                       // 8  implementation (worked since v5.8)
                "  CODE",                                            // 9
                "  RETURN 0",                                        // 10
                "",                                                  // 11
                "UseShapes       PROCEDURE()",                       // 12
                "LocObj          My:Shape",                          // 13 local instance
                "Loc:Derived     CLASS(My:Shape)",                   // 14 procedure-local derived colon class
                "Area              PROCEDURE(),LONG,DERIVED",        // 15 column-0 prototype inside it
                "                END",                               // 16
                "AfterVar        LONG",                              // 17 still UseShapes' data
                "  CODE",                                            // 18
                "  AfterVar = LocObj.Area()",                        // 19
                ""
            }));

            var ms = new ClarionParser().ParseMemberFile(clw, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UseShapes" }).Symbols;

            var impl = ms.FirstOrDefault(s => s.Name == "My:Shape.Area" && s.DeclKind == "implementation");
            Check(impl != null && impl.LineNumber == 8, "20", "control: 'My:Shape.Area PROCEDURE' is an implementation at line 8");
            var modObj = ms.FirstOrDefault(s => s.Name == "ModObj");
            Check(modObj != null && modObj.Type == "variable" && modObj.Params == "MY:SHAPE",
                  "21", "'ModObj My:Shape' is a variable typed MY:SHAPE - got " + (modObj == null ? "no symbol" : "'" + modObj.Params + "'"));
            var modRef = ms.FirstOrDefault(s => s.Name == "ModRef");
            Check(modRef != null && modRef.Params == "&MY:SHAPE",
                  "22", "'ModRef &My:Shape' is a reference typed &MY:SHAPE - got " + (modRef == null ? "no symbol" : "'" + modRef.Params + "'"));
            var locObj = ms.FirstOrDefault(s => s.Name == "LocObj");
            Check(locObj != null && locObj.ParentName == "UseShapes" && locObj.Scope == "local" && locObj.Params == "MY:SHAPE",
                  "23", "'LocObj My:Shape' is a local of UseShapes typed MY:SHAPE");
            var locDerived = ms.FirstOrDefault(s => s.Name == "Loc:Derived");
            Check(locDerived != null && locDerived.Type == "variable" && locDerived.Scope == "local" &&
                  locDerived.ParentName == "UseShapes" && locDerived.Params == "MY:SHAPE",
                  "24", "'Loc:Derived CLASS(My:Shape)' is a local class variable of UseShapes - got " +
                  (locDerived == null ? "no symbol" : locDerived.Type + "/" + locDerived.Scope + "/" + locDerived.Params));
            Check(!ms.Any(s => s.Name == "Area" && s.Type == "procedure"),
                  "25", "the local class's column-0 prototype is not read as a procedure named Area");
            var afterVar = ms.FirstOrDefault(s => s.Name == "AfterVar");
            Check(afterVar != null && afterVar.ParentName == "UseShapes",
                  "26", "data after the local class body still belongs to UseShapes - got " +
                  (afterVar == null ? "no symbol" : "parent '" + afterVar.ParentName + "'"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
