// ITEMIZE member EQUATEs must be indexed under the name the compiler gives them.
//
//   ITEMIZE,PRE(Px)                 -> Px:Name     (the prefix may contain colons: PRE(OP:RESET))
//   Label ITEMIZE,PRE / ,PRE()      -> Label:Name  (empty prefix: the ITEMIZE label is used)
//   no PRE, or blank label + PRE()  -> Name
//   a member already spelled Px:Name is not prefixed twice
//
// Before the fix every member was indexed under its bare label (None, Text, ... - names that do not
// exist), and a member without a value ("Name EQUATE", auto-numbered) was not indexed at all.
//
// Compiles the REAL ClarionParser.cs and ClarionGraphService.cs and covers all three paths that emit
// equates: ParseIncFile (file-level, project and library mode), ParseMemberFile (module-level and
// procedure-local DATA), and the library build's flat-equate scan (ClarionGraphService.Build over a
// synthetic LibSrc folder holding a .EQU file, read back from the SQLite DB it writes).
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;
using ClarionCodeGraph.Parsing;
using ClarionCodeGraph.Parsing.Models;

static class ClarionParserItemizeTest
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

    // The ITEMIZE shapes shared by the .inc and the .EQU cases. Line numbers are 1-based.
    static readonly string[] Blocks =
    {
        "                  ITEMIZE, PRE(Fmt)            ! blank label, space after the comma",   // 1
        "Text                EQUATE(0)                  ! Fmt:Text",                            // 2
        "Base64              EQUATE                     ! Fmt:Base64 (no value)",               // 3
        "                  END",                                                                // 4
        "                  ITEMIZE,PRE(OP:RESET)        ! colon-qualified prefix",              // 5
        "None                EQUATE(-1)                 ! OP:RESET:None",                       // 6
        "Value               EQUATE(01H)                ! OP:RESET:Value",                      // 7
        "                  END",                                                                // 8
        "Shade             ITEMIZE(0),PRE               ! empty prefix: label used",            // 9
        "Flat                EQUATE                     ! Shade:Flat",                          // 10
        "                  END",                                                                // 11
        "BtnState          ITEMIZE,PRE()                ! empty prefix, members pre-qualified", // 12
        "BtnState:Normal     EQUATE(1)                  ! BtnState:Normal",                     // 13
        "Hot                 EQUATE                     ! BtnState:Hot",                        // 14
        "                  END",                                                                // 15
        "Mode              ITEMIZE                      ! no PRE: own labels",                  // 16
        "ModeOne             EQUATE                     ! ModeOne",                             // 17
        "                  END",                                                                // 18
        "                  ITEMIZE(10),PRE(Px)          ! period terminator",                   // 19
        "First               EQUATE                     ! Px:First",                            // 20
        "                  .",                                                                  // 21
        "!Commented          EQUATE(9)",                                                        // 22
        "PlainEqu            EQUATE(3)                  ! control: not in any ITEMIZE",          // 23
    };

    static readonly string[] Expected =
        { "Fmt:Text", "Fmt:Base64", "OP:RESET:None", "OP:RESET:Value", "Shade:Flat", "BtnState:Normal", "BtnState:Hot", "ModeOne", "Px:First", "PlainEqu" };
    static readonly string[] Bogus =
        { "Text", "Base64", "None", "Value", "Flat", "Hot", "First", "BtnState:BtnState:Normal", "Fmt", "Shade", "Mode", "Commented" };

    static void CheckNames(string id, string where, Dictionary<string, int> names)
    {
        foreach (string e in Expected)
            Check(names.ContainsKey(e), id + "." + e, where + ": " + e + " is indexed");
        foreach (string b in Bogus)
            Check(!names.ContainsKey(b), id + "!" + b, where + ": no '" + b + "' symbol");
        int line;
        Check(names.TryGetValue("Fmt:Base64", out line) && line == 3, id + ".line", where + ": a member keeps its own line (Fmt:Base64 at 3)");
    }

    static int Main(string[] args)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-itemize-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            // 1) ParseIncFile, both modes.
            string inc = Path.Combine(dir, "Opts.inc");
            Write(inc, Blocks);
            foreach (bool lib in new[] { false, true })
            {
                var syms = new ClarionParser { LibraryMode = lib }.ParseIncFile(inc, 1).Symbols;
                var names = syms.Where(s => s.Params == "EQUATE" && s.Scope == "global")
                                .ToDictionary(s => s.Name, s => s.LineNumber, StringComparer.Ordinal);
                CheckNames(lib ? "inc-lib" : "inc-prj", "ParseIncFile " + (lib ? "library" : "project") + " mode", names);
                Check(names.Count == Expected.Length, (lib ? "inc-lib" : "inc-prj") + ".count",
                      "exactly " + Expected.Length + " equates - got " + names.Count + " (" + string.Join(", ", names.Keys) + ")");
            }

            // 2) ParseMemberFile: module-level and procedure-local DATA.
            string clw = Path.Combine(dir, "Opts.clw");
            Write(clw,
                "  MEMBER('Main.clw')",                                       // 1
                "                  ITEMIZE,PRE(ModP)",                        // 2
                "Alpha               EQUATE",                                 // 3
                "Beta                EQUATE(5)",                              // 4
                "                  END",                                      // 5
                "AfterModule       LONG",                                     // 6
                "UseOpts PROCEDURE()",                                        // 7
                "Shade2            ITEMIZE(1),PRE",                           // 8
                "Blue                EQUATE",                                 // 9
                "                  .",                                        // 10
                "AfterLocal        LONG",                                     // 11
                "  CODE",                                                     // 12
                "  AfterLocal = ModP:Alpha + Shade2:Blue");                   // 13
            var msyms = new ClarionParser().ParseMemberFile(clw, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Symbols;
            Func<string, ClarionSymbol> sym = n => msyms.FirstOrDefault(s => s.Name == n && s.Type == "variable");
            var a = sym("ModP:Alpha"); var b = sym("ModP:Beta"); var blue = sym("Shade2:Blue");
            Check(a != null && a.Scope == "module" && a.LineNumber == 3 && a.Params == "EQUATE", "clw.1", "module-level value-less member is ModP:Alpha, scope module, line 3");
            Check(b != null && b.Scope == "module", "clw.2", "module-level member with a value is ModP:Beta");
            Check(blue != null && blue.Scope == "local" && blue.ParentName == "UseOpts" && blue.LineNumber == 9, "clw.3", "procedure-local member is Shade2:Blue, owned by UseOpts");
            Check(sym("Alpha") == null && sym("Beta") == null && sym("Blue") == null, "clw.4", "no bare Alpha / Beta / Blue");
            Check(sym("AfterModule") != null && sym("AfterModule").Scope == "module", "clw.5", "a declaration after the module-level block is still indexed");
            Check(sym("AfterLocal") != null && sym("AfterLocal").ParentName == "UseOpts", "clw.6", "a declaration after the period-closed local block is still indexed");

            // 3) Library build: the .EQU flat-equate scan, read back from the DB.
            string libSrc = Path.Combine(dir, "libsrc");
            Directory.CreateDirectory(libSrc);
            Write(Path.Combine(libSrc, "OPTS.EQU"), Blocks);
            string db = Path.Combine(dir, "lib.db");
            var res = ClarionGraphService.Build("test", db, libSrc);
            Check(res.Error == null, "equ.build", "library build succeeds" + (res.Error != null ? " - " + res.Error : ""));
            var equNames = new Dictionary<string, int>(StringComparer.Ordinal);
            string parserVersion = null;
            using (var conn = new SQLiteConnection("Data Source=" + db + ";Read Only=True"))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand("SELECT name, line_number FROM symbols WHERE params = 'EQUATE' AND file_path LIKE '%OPTS.EQU'", conn))
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) equNames[r.GetString(0)] = r.GetInt32(1);
                using (var cmd = new SQLiteCommand("SELECT value FROM index_metadata WHERE key = 'parser_version'", conn))
                    parserVersion = cmd.ExecuteScalar() as string;
            }
            CheckNames("equ", "library .EQU scan", equNames);
            Check(equNames.Count == Expected.Length, "equ.count", "exactly " + Expected.Length + " equates - got " + equNames.Count + " (" + string.Join(", ", equNames.Keys) + ")");
            Check(parserVersion == "5", "equ.ver", "the library DB is stamped parser_version 5 (older caches rebuild) - got " + (parserVersion ?? "null"));
            SQLiteConnection.ClearAllPools();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
