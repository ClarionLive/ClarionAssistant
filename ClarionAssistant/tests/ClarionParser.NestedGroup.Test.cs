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
                "ErrQType            QUEUE,TYPE                 ! a TYPE: its fields never become variables; the row itself is kept",
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
                "Tab                      LONG",
                "Option                   STRING(5)",
                "Second                   LONG",
                "                       END",
                "After                  LONG",
                "  CODE",
                "  After = 1");
            var l = Variables(new ClarionParser().ParseMemberFile(clw, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Symbols, "local");
            Expect("local", "procedure-local DATA", l, new[] { "Rec", "After" }, new[] { "Head", "First", "Second", "Tab", "Option" });

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

            var noCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 4) A FILE whose RECORD (unlabelled, and labelled) ends before the FILE does: what follows the
            //    RECORD's END still belongs to the FILE, and the variable after the FILE's END is a variable.
            string rec = Path.Combine(dir, "Record.clw");
            Write(rec,
                "  MEMBER('Prog')",
                "Orders               FILE,DRIVER('TOPSPEED'),PRE(Ord)",
                "ByNumber               KEY(Ord:Number),PRIMARY",
                "                       RECORD",
                "Number                   LONG",
                "Descr                    STRING(20)",
                "                       END",
                "Notes                  MEMO(200)",
                "Photo                  BLOB",
                "Archive              FILE,DRIVER('TOPSPEED'),PRE(Arc)",
                "Record                 RECORD",
                "ArcNumber                LONG",
                "                       END",
                "ArcMemo                MEMO(200)",
                "                     END",
                "                     END",
                "AfterFiles           LONG",
                "  MAP",
                "  END",
                "MyProc               PROCEDURE",
                "  CODE");
            var rv = Variables(new ClarionParser().ParseMemberFile(rec, 1, noCalls).Symbols, "module");
            Expect("record", "FILE with RECORD", rv, new[] { "AfterFiles" },
                new[] { "Number", "Descr", "Notes", "Photo", "ArcNumber", "ArcMemo", "ByNumber" });

            // 5) A VIEW with nested JOINs (unlabelled), then a variable.
            string view = Path.Combine(dir, "View.clw");
            Write(view,
                "  MEMBER('Prog')",
                "MyProc               PROCEDURE",
                "Joined               VIEW(Orders),PRE(Jn)",
                "                       PROJECT(Ord:Number)",
                "                       JOIN(Cus:ByNumber,Ord:Customer)",
                "                         PROJECT(Cus:Name)",
                "                         JOIN(Reg:ByCode,Cus:Region)",
                "                           PROJECT(Reg:Name)",
                "                         END",
                "                         JOIN(Rep:ByCode,Cus:Rep)    ! a sibling JOIN",
                "                           PROJECT(Rep:Name)",
                "                         END",
                "RepLabel                 STRING(20)",
                "                       END",
                "                     END",
                "AfterView            LONG",
                "  CODE");
            var vv = Variables(new ClarionParser().ParseMemberFile(view, 1, noCalls).Symbols, "local");
            Expect("view", "VIEW with nested JOINs", vv, new[] { "AfterView" }, new[] { "RepLabel" });

            // 6) A WINDOW with SHEET/TAB/OPTION nesting, a GROUP control, and a variable after it. Only controls
            //    that take an END nest; the BUTTON/ENTRY/RADIO rows do not.
            string win = Path.Combine(dir, "Window.clw");
            Write(win,
                "  MEMBER('Prog')",
                "MyProc               PROCEDURE",
                "Window               WINDOW('Caption'),AT(,,260,140),GRAY",
                "                       SHEET,AT(4,4,250,110),USE(?Sheet)",
                "                         TAB('General'),USE(?General)",
                "                           OPTION('Kind'),AT(8,20,100,40),USE(Loc:Kind)",
                "                             RADIO('One'),AT(12,30),USE(?R1)",
                "                             RADIO('Two'),AT(12,40),USE(?R2)",
                "                           END",
                "AfterOption               STRING('after OPTION'),AT(8,62)",
                "                           GROUP('Box'),AT(8,70,100,30)",
                "                             ENTRY(@s20),AT(12,80),USE(Loc:Name)",
                "                           END",
                "AfterGroup                 STRING('after GROUP'),AT(8,102)",
                "                         END",
                "AfterTab                 STRING('after TAB'),AT(8,106)",
                "                         TAB('More'),USE(?More)",
                "                           BUTTON('OK'),AT(8,100),USE(?Ok)",
                "                         END",
                "                       END",
                "                       MENUBAR",
                "                         MENU('&File'),USE(?File)",
                "                           ITEM('Exit'),USE(?Exit)",
                "                         END",
                "AfterMenu                STRING('after MENU'),AT(8,110)",
                "                       END",
                "AfterSheet             STRING('after SHEET'),AT(8,114)",
                "                     END",
                "AfterWindow          LONG",
                "  CODE");
            var wv = Variables(new ClarionParser().ParseMemberFile(win, 1, noCalls).Symbols, "local");
            Expect("window", "WINDOW with SHEET/TAB/OPTION", wv, new[] { "AfterWindow" },
                new[] { "AfterOption", "AfterGroup", "AfterTab", "AfterMenu", "AfterSheet" });

            // 7a) REPORT bands (HEADER/DETAIL/FOOTER/BREAK/FORM) each have an END of their own.
            string rpt = Path.Combine(dir, "Report.clw");
            Write(rpt,
                "  MEMBER('Prog')",
                "MyProc               PROCEDURE",
                "Report               REPORT,AT(500,1000,7500,9000),PRE(Rpt)",
                "Brk                    BREAK(Loc:Group)",
                "                         HEADER,AT(,,,300)",
                "                           STRING('Group'),AT(10,10)",
                "                         END",
                "AfterHeader              STRING('after HEADER'),AT(10,20)",
                "Det                      DETAIL,AT(,,,250)",
                "                           STRING('Row'),AT(10,10)",
                "                         END",
                "AfterDetail              STRING('after DETAIL'),AT(10,30)",
                "                       END",
                "AfterBreak             STRING('after BREAK'),AT(10,40)",
                "                       FOOTER,AT(,,,200)",
                "                       END",
                "AfterFooter            STRING('after FOOTER'),AT(10,50)",
                "                     END",
                "AfterReport          LONG",
                "  CODE");
            var pv = Variables(new ClarionParser().ParseMemberFile(rpt, 1, noCalls).Symbols, "local");
            Expect("report", "REPORT with bands", pv, new[] { "AfterReport" },
                new[] { "AfterHeader", "AfterDetail", "AfterBreak", "AfterFooter" });

            // 7) A local CLASS closed by a '.' that carries a trailing comment, then a variable.
            string cls = Path.Combine(dir, "Class.clw");
            Write(cls,
                "  MEMBER('Prog')",
                "MyProc               PROCEDURE",
                "Helper               CLASS",
                "Value                  LONG",
                "                     .  ! end of Helper",
                "AfterClass           LONG",
                "  CODE");
            var cv = Variables(new ClarionParser().ParseMemberFile(cls, 1, noCalls).Symbols, "local");
            Expect("class", "CLASS closed by '.  ! comment'", cv, new[] { "AfterClass" }, new string[0]);
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
