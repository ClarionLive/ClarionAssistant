// Follow-up to GH #271 / #278 (integ-1023 review D1-D5): Pass 2b must not hide a real INCLUDE gap behind a
// "conditional" block that is not really conditional, and the .red {include} lines in the index log must say why.
//
//   D1  Clarion's include guard - OMIT('_EndOfInclude_',_XPresent_) / _XPresent_ EQUATE(1) ... _EndOfInclude_, the
//       shape of every LibSrc .inc (ABUTIL.INC) - wraps the whole file, which compiles on its first read. An INCLUDE
//       inside it that does not resolve is a gap, not unresolved_conditional.
//   D2  an .inc the .cwproj lists is judged by how it is REACHED, like any other: what it INCLUDEs is required when
//       the .inc is INCLUDEd unconditionally, and not a gap when it is only INCLUDEd conditionally.
//   D3  an outer conditional block whose terminator sits inside an unconditional OMIT('t') block ends there.
//   D4  a | inside a comment is not a line continuation: "! note |" does not swallow the INCLUDE on the next line.
//   D5  the .red {include} log line says why each one was not followed; "already loaded" (harmless) is no warning;
//       a project's own version-named .red is reported too.
//
// Runs the REAL CodeGraphIndexer (+ parser, resolver, .red service, SQLite database) over a solution written to a temp
// dir and reads only the database and the progress log, so the same file compiles against the old indexer, where the
// feature checks must fail.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClarionCodeGraph.Graph;

static class CodeGraphIndexerIncludeGuardTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    // Clarion source is ANSI; the fixture is plain ASCII and written without a BOM.
    static void Write(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }

    const string G1 = "33333333-0000-0000-0000-000000000001";
    const string G2 = "33333333-0000-0000-0000-000000000002";

    static string Cwproj(string guid, string name, params string[] compile)
    {
        return "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
               "  <PropertyGroup>\r\n    <ProjectGuid>{" + guid + "}</ProjectGuid>\r\n    <OutputType>WinExe</OutputType>\r\n" +
               "    <AssemblyName>" + name + "</AssemblyName>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n" +
               string.Concat(compile.Select(c => "    <Compile Include=\"" + c + "\" />\r\n")) +
               "  </ItemGroup>\r\n</Project>";
    }

    /// <summary>Returns the .sln path.</summary>
    static string BuildFixture(string root)
    {
        string sln = Path.Combine(root, "sln");
        string app = Path.Combine(sln, "App");
        string lib = Path.Combine(sln, "Lib");

        Write(Path.Combine(app, "App.cwproj"), Cwproj(G1, "App", "App.clw", "AppMod.clw", "Listed.inc", "ListedU.inc", "ListedOrphan.inc"));

        Write(Path.Combine(app, "App.clw"),
            "  PROGRAM",
            "  INCLUDE('Guarded.inc'),ONCE",                  // D1: include-guarded, INCLUDEs a missing base
            "  INCLUDE('IncPath.inc'),ONCE",                  // D3/D4 in the .inc parser
            "  COMPILE('***',_NOT_DEFINED_)",
            "  INCLUDE('Listed.inc'),ONCE",                   // D2: .cwproj-listed, reached only conditionally
            "  ***",
            "  INCLUDE('ListedU.inc'),ONCE",                  // D2: .cwproj-listed, reached unconditionally
            "  MAP",
            "    MODULE('AppMod.clw')",
            "      DoWork  PROCEDURE()",
            "    END",
            "  END",
            "  CODE",
            "  DoWork()");

        // D1: the real ABUTIL.INC shape (C:\Clarion12\LibSrc\win\ABUTIL.INC lines 1-8).
        Write(Path.Combine(app, "Guarded.inc"),
            "!ABCIncludeFile",
            "",
            " OMIT('_EndOfInclude_',_GuardedPresent_)",
            "",
            "_GuardedPresent_ EQUATE(1)",
            "",
            "  INCLUDE('GuardBase.inc'),ONCE",                // missing: a real gap
            "  INCLUDE('GuardReal.inc'),ONCE",                // exists, guarded too, INCLUDEs a missing one
            "GuardedThing        CLASS,TYPE",
            "  Go                  PROCEDURE()",
            "                    END",
            "  COMPILE('!x!',_NOT_DEFINED_)",                 // a real conditional inside the guard stays conditional
            "  INCLUDE('GuardCond.inc'),ONCE",
            "  !x!",
            "_EndOfInclude_");
        Write(Path.Combine(app, "GuardReal.inc"),
            "  OMIT('_EndOfInclude_',_GuardRealPresent_)",
            "_GuardRealPresent_  EQUATE(1)",
            "  INCLUDE('GuardDeep.inc'),ONCE",                // missing, two guarded files down: a gap
            "_EndOfInclude_");

        // D3 / D4 through ParseIncFile.
        Write(Path.Combine(app, "IncPath.inc"),
            "  COMPILE('!o2!',_NOT_DEFINED_)",
            "  INCLUDE('IncOuterCond.inc'),ONCE",             // inside the outer conditional: not a gap
            "  OMIT('##')",
            "  dead text that ends the outer block: !o2!",
            "  ##",
            "  INCLUDE('IncAfterOuter.inc'),ONCE",            // D3: the outer block ended inside the OMIT: a gap
            "  ! a note that ends with a bar |",
            "  INCLUDE('IncAfterComment.inc'),ONCE");         // D4: a gap, not swallowed by the comment

        // D3 / D4 through ParseMemberFileCore, plus precision cases.
        Write(Path.Combine(app, "AppMod.clw"),
            "  MEMBER('App.clw')",
            "  COMPILE('!outer!',_NOT_DEFINED_)",
            "  INCLUDE('OuterCond.inc'),ONCE",                // inside the outer conditional: not a gap
            "  OMIT('**')",
            "  dead text that ends the outer block: !outer!",
            "  **",
            "  INCLUDE('AfterOuter.inc'),ONCE",               // D3: a gap
            "  ! a note that ends with a bar |",
            "  INCLUDE('AfterComment.inc'),ONCE",             // D4: a gap
            "  COMPILE('@@',_CG_)",                           // COMPILE is never a guard, even with an EQUATE after it
            "_CG_ EQUATE(1)",
            "  INCLUDE('CompileLike.inc'),ONCE",
            "  @@",
            "  OMIT('%%',_OTHER_)",                           // an OMIT whose block EQUATEs a DIFFERENT symbol: conditional
            "_NOT_OTHER_ EQUATE(1)",
            "  INCLUDE('OmitOther.inc'),ONCE",
            "  %%",
            "DoWork PROCEDURE()",
            "  CODE");

        // D2
        Write(Path.Combine(app, "Listed.inc"),
            "  INCLUDE('ListedChild.inc'),ONCE");
        Write(Path.Combine(app, "ListedU.inc"),
            "  INCLUDE('ListedUChild.inc'),ONCE");
        Write(Path.Combine(app, "ListedOrphan.inc"),          // listed, INCLUDEd by nothing: not compiled, no gap
            "  INCLUDE('OrphanChild.inc'),ONCE");

        // D5: a second project with its own version-named .red.
        Write(Path.Combine(lib, "Lib.cwproj"), Cwproj(G2, "Lib", "Lib.clw"));
        Write(Path.Combine(lib, "Lib.clw"),
            "  MEMBER()");
        Write(Path.Combine(lib, "Clarion110.red"),
            "[Common]",
            "*.clw = " + lib,
            "{include %LOCAL_UNDEFINED%\\LocalGone.red}");

        Write(Path.Combine(sln, "Clarion110.red"),
            "[Common]",
            "*.inc = " + app,
            "*.clw = " + app,
            "{include %UNDEFINED_MACRO%\\Gone.red}",          // not found: a warning
            "{include " + Path.Combine(sln, "Clarion110.red") + "}"); // includes itself: already loaded, harmless

        string slnPath = Path.Combine(sln, "IncludeGuard.sln");
        File.WriteAllText(slnPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"App\", \"App\\App.cwproj\", \"{" + G1 + "}\"\r\nEndProject\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"Lib\", \"Lib\\Lib.cwproj\", \"{" + G2 + "}\"\r\nEndProject\r\n",
            new UTF8Encoding(false));
        return slnPath;
    }

    static string Outcome(CodeGraphDatabase db, string fileName, string pass = "pass2b-include")
    {
        var dt = db.ExecuteQuery("SELECT outcome FROM indexed_files WHERE pass='" + pass + "' AND file_name='" + fileName + "'");
        return dt.Rows.Count == 0 ? "(no row)" : string.Join(",", dt.Rows.Cast<System.Data.DataRow>().Select(r => r[0].ToString()));
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-incguard-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        CodeGraphDatabase db = null;
        try
        {
            string slnPath = BuildFixture(root);
            var red = new ClarionAssistant.Services.RedFileService();
            if (!red.Load(Path.Combine(Path.GetDirectoryName(slnPath), "Clarion110.red"),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["THISDIR"] = Path.GetDirectoryName(slnPath) }))
                throw new Exception("fixture .red did not load");

            db = new CodeGraphDatabase();
            db.Open(Path.Combine(root, "index.db"));
            var indexer = new CodeGraphIndexer(db) { RedService = red, BuildConfiguration = "Release" };
            var log = new List<string>();
            indexer.OnProgress += m => log.Add(m);
            var result = indexer.IndexSolution(slnPath, false, null);
            string warning = result.Warnings.FirstOrDefault(w => w.Contains("INCLUDE()d")) ?? "";

            Console.WriteLine("-- D1: the include guard does not make the file conditional");
            Check(Outcome(db, "GuardBase.inc") == "unresolved", "D1a",
                  "a missing INCLUDE inside an include-guarded .inc is a gap: GuardBase.inc is " + Outcome(db, "GuardBase.inc"));
            Check(Outcome(db, "GuardDeep.inc") == "unresolved", "D1b",
                  "two guarded files down: GuardDeep.inc is " + Outcome(db, "GuardDeep.inc"));
            Check(warning.Contains("GuardBase.inc (Guarded.inc:7)"), "D1c", "the warning names it and its includer: '" + warning + "'");
            Check(Outcome(db, "GuardBase.inc", "pass2d-global-data") == "unresolved", "D1d",
                  "Pass 2d (global data, the member parser) agrees: GuardBase.inc is " + Outcome(db, "GuardBase.inc", "pass2d-global-data"));
            Check(Outcome(db, "GuardCond.inc") == "unresolved_conditional", "D1p1",
                  "precision: a real COMPILE block inside the guard stays conditional: GuardCond.inc is " + Outcome(db, "GuardCond.inc"));
            Check(Outcome(db, "CompileLike.inc") == "unresolved_conditional", "D1p2",
                  "precision: COMPILE('t',SYM) followed by SYM EQUATE is no guard: CompileLike.inc is " + Outcome(db, "CompileLike.inc"));
            Check(Outcome(db, "OmitOther.inc") == "unresolved_conditional", "D1p3",
                  "precision: an OMIT whose block EQUATEs another symbol is no guard: OmitOther.inc is " + Outcome(db, "OmitOther.inc"));
            Check(Outcome(db, "GuardReal.inc") == "resolved_parsed" &&
                  db.ExecuteQuery("SELECT 1 FROM symbols WHERE name='GuardedThing' AND type='class'").Rows.Count == 1,
                  "D1p4", "precision: the guarded files are still parsed (GuardReal.inc " + Outcome(db, "GuardReal.inc") + ", class GuardedThing)");

            Console.WriteLine("-- D2: an .inc the .cwproj lists is judged by how it is reached");
            Check(Outcome(db, "ListedUChild.inc") == "unresolved", "D2a",
                  "INCLUDEd by a listed .inc that is INCLUDEd unconditionally: ListedUChild.inc is " + Outcome(db, "ListedUChild.inc"));
            Check(Outcome(db, "ListedChild.inc") == "unresolved_conditional", "D2b",
                  "INCLUDEd by a listed .inc that is only INCLUDEd conditionally: ListedChild.inc is " + Outcome(db, "ListedChild.inc"));
            Check(Outcome(db, "ListedChild.inc", "pass2d-global-data") == Outcome(db, "ListedChild.inc"), "D2c",
                  "Pass 2d gives the same outcome (" + Outcome(db, "ListedChild.inc", "pass2d-global-data") + ")");
            Check(!Outcome(db, "OrphanChild.inc").Contains("unresolved") || Outcome(db, "OrphanChild.inc") == "unresolved_conditional", "D2p",
                  "precision: a listed .inc nothing INCLUDEs makes no gap: OrphanChild.inc is " + Outcome(db, "OrphanChild.inc"));
            Check(Outcome(db, "Listed.inc", "pass1-inc") == "resolved_parsed" || Outcome(db, "Listed.inc", "pass1-inc") == "resolved_no_symbols", "D2q",
                  "precision: the listed .inc keeps its single pass1-inc row (" + Outcome(db, "Listed.inc", "pass1-inc") + ", pass2b: " + Outcome(db, "Listed.inc") + ")");

            Console.WriteLine("-- D3: an outer conditional block can end inside an unconditional OMIT");
            Check(Outcome(db, "AfterOuter.inc") == "unresolved", "D3a", "MEMBER parser: AfterOuter.inc is " + Outcome(db, "AfterOuter.inc"));
            Check(Outcome(db, "IncAfterOuter.inc") == "unresolved", "D3b", ".inc parser: IncAfterOuter.inc is " + Outcome(db, "IncAfterOuter.inc"));
            Check(Outcome(db, "OuterCond.inc") == "unresolved_conditional" && Outcome(db, "IncOuterCond.inc") == "unresolved_conditional", "D3p",
                  "precision: what is inside the outer block stays conditional (" + Outcome(db, "OuterCond.inc") + ", " + Outcome(db, "IncOuterCond.inc") + ")");

            Console.WriteLine("-- D4: a | inside a comment is not a continuation");
            Check(Outcome(db, "AfterComment.inc") == "unresolved", "D4a", "MEMBER parser: AfterComment.inc is " + Outcome(db, "AfterComment.inc"));
            Check(Outcome(db, "IncAfterComment.inc") == "unresolved", "D4b", ".inc parser: IncAfterComment.inc is " + Outcome(db, "IncAfterComment.inc"));

            Console.WriteLine("-- D5: the .red {include} lines say why, and only warn when it matters");
            var redLines = log.Where(l => l.Contains(".red {include}")).ToList();
            foreach (var l in redLines) Console.WriteLine("       log: " + l);
            Check(redLines.Any(l => l.StartsWith("WARNING:") && l.Contains("%UNDEFINED_MACRO%\\Gone.red") && l.Contains("does not exist")), "D5a",
                  "a file that is not there is a warning that says so");
            Check(!redLines.Any(l => l.StartsWith("WARNING:") && l.Contains("already loaded")), "D5b",
                  "an {include} of a file already loaded is not a warning");
            Check(!redLines.Any(l => l.Contains("its search paths are missing") && l.Contains("already loaded")), "D5c",
                  "nor does it claim search paths are missing");
            Check(redLines.Any(l => l.StartsWith("WARNING:") && l.Contains("%LOCAL_UNDEFINED%\\LocalGone.red") && l.Contains("Lib")), "D5d",
                  "a project's own .red is reported too, naming the project");
        }
        catch (Exception ex)
        {
            Check(false, "X", "harness threw: " + ex);
        }
        finally
        {
            try { if (db != null) db.Close(); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "CodeGraphIndexer.IncludeGuard: " + _assertions + " assertions, all passed"
            : "CodeGraphIndexer.IncludeGuard: " + Failures.Count + " of " + _assertions + " FAILED");
        return Failures.Count == 0 ? 0 : 1;
    }
}
