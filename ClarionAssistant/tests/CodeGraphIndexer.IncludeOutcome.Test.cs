// Pass 2b reads INCLUDE() the way the compiler does (GH #271): a commented-out INCLUDE is not followed, an INCLUDE
// that does not resolve inside a conditional COMPILE/OMIT block is not reported as a gap, and the warning names
// each INCLUDE that did not resolve and the file that INCLUDEs it.
//
// Runs the REAL CodeGraphIndexer (+ parser, resolver, .red service, SQLite database) over a solution written to
// a temp dir, then queries the database it produced. Every check reads only the database and IndexResult.Warnings,
// so the same file compiles against an indexer without the fix - where the feature checks must fail.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClarionCodeGraph.Graph;

static class CodeGraphIndexerIncludeOutcomeTest
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

    const string G1 = "22222222-0000-0000-0000-000000000001";

    /// <summary>Returns the .sln path.</summary>
    static string BuildFixture(string root)
    {
        string sln = Path.Combine(root, "sln");
        string app = Path.Combine(sln, "App");

        Write(Path.Combine(app, "App.cwproj"),
            "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">",
            "  <PropertyGroup>",
            "    <ProjectGuid>{" + G1 + "}</ProjectGuid>",
            "    <OutputType>WinExe</OutputType>",
            "    <AssemblyName>App</AssemblyName>",
            "  </PropertyGroup>",
            "  <ItemGroup>",
            "    <Compile Include=\"App.clw\" />",
            "    <Compile Include=\"AppMod.clw\" />",
            "  </ItemGroup>",
            "</Project>");

        Write(Path.Combine(app, "App.clw"),
            "  PROGRAM",
            "!  INCLUDE('Phantom.inc'),ONCE",                 // commented out: exists, must not be followed
            "  ! INCLUDE('GoneCommented.inc')",               // commented out: does not exist, must not be reported
            "  INCLUDE('Real.inc'),ONCE",
            "  INCLUDE('Missing.inc'),ONCE",                  // a real gap
            "  COMPILE('***',_NOT_DEFINED_)",
            "  INCLUDE('CondMissing.inc'),ONCE",              // conditional: not a gap
            "  INCLUDE('CondParent.inc'),ONCE",               // conditional, exists; what IT includes is conditional too
            "  ***",
            "  INCLUDE('AfterBlock.inc'),ONCE",               // after the terminator: unconditional again
            "  COMPILE('!end!')",
            "  INCLUDE('UncondCompile.inc'),ONCE",            // COMPILE without a condition is always compiled
            "  !end!",
            "  INCLUDE('Both.inc'),ONCE",                     // also INCLUDEd conditionally below: still a gap
            "  INCLUDE('Wrap.inc'),ONCE",                     // an unconditional route found only deeper in the walk
            "  MAP",
            "    INCLUDE('MapProto.inc')",                    // inside the MAP: Pass 1's, must not be stored twice
            "    MODULE('AppMod.clw')",
            "      DoWork  PROCEDURE()",
            "    END",
            "  END",
            "  CODE",
            "  DoWork()");
        Write(Path.Combine(app, "AppMod.clw"),
            "  MEMBER('App.clw')",
            "  OMIT('**',_SOME_SWITCH_)",
            "  INCLUDE('CondMissing2.inc'),ONCE",
            "  INCLUDE('Both.inc'),ONCE",
            "  INCLUDE('Deep.inc'),ONCE",                     // conditional here, but Wrap.inc INCLUDEs it unconditionally
            "  INCLUDE('Shared.inc'),ONCE",                   // conditional here, but Wrap.inc INCLUDEs it unconditionally
            "  **",
            "DoWork PROCEDURE()",
            "  CODE");

        Write(Path.Combine(app, "Real.inc"),
            "  !INCLUDE('Phantom2.inc'),ONCE",                // a commented INCLUDE inside an .inc
            "RealThing           CLASS,TYPE",
            "  Go                  PROCEDURE()",
            "                    END");
        Write(Path.Combine(app, "CondParent.inc"),
            "  INCLUDE('Nested.inc'),ONCE",
            "CondThing           CLASS,TYPE",
            "                    END");
        Write(Path.Combine(app, "Wrap.inc"),
            "  INCLUDE('Deep.inc'),ONCE",
            "  INCLUDE('Shared.inc'),ONCE");
        Write(Path.Combine(app, "Shared.inc"),
            "  INCLUDE('SharedMissing.inc'),ONCE");
        Write(Path.Combine(app, "MapProto.inc"),
            "MapProtoProc        PROCEDURE()");
        Write(Path.Combine(app, "Phantom.inc"),
            "PhantomThing        CLASS,TYPE,MODULE('Phantom.clw'),LINK('Phantom.clw')",
            "  Go                  PROCEDURE()",
            "                    END");
        Write(Path.Combine(app, "Phantom.clw"),
            "  MEMBER()",
            "  INCLUDE('Phantom.inc'),ONCE",
            "PhantomThing.Go PROCEDURE()",
            "  CODE");
        Write(Path.Combine(app, "Phantom2.inc"),
            "Phantom2Thing       CLASS,TYPE",
            "                    END");

        // Includes resolve through the project's folder only; no library path holds the fixture's .inc files.
        Write(Path.Combine(sln, "Clarion110.red"),
            "[Common]",
            "*.inc = " + app,
            "*.clw = " + app);

        string slnPath = Path.Combine(sln, "IncludeOutcome.sln");
        File.WriteAllText(slnPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"App\", \"App\\App.cwproj\", \"{" + G1 + "}\"\r\nEndProject\r\n",
            new UTF8Encoding(false));
        return slnPath;
    }

    static int Scalar(CodeGraphDatabase db, string sql)
    {
        var dt = db.ExecuteQuery(sql);
        return dt.Rows.Count == 0 || dt.Rows[0][0] == DBNull.Value ? 0 : Convert.ToInt32(dt.Rows[0][0]);
    }

    static string Outcome(CodeGraphDatabase db, string fileName)
    {
        var dt = db.ExecuteQuery("SELECT outcome FROM indexed_files WHERE pass='pass2b-include' AND file_name='" + fileName + "'");
        return dt.Rows.Count == 0 ? "(no row)" : string.Join(",", dt.Rows.Cast<System.Data.DataRow>().Select(r => r[0].ToString()));
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-incoutcome-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

            Console.WriteLine("-- commented-out INCLUDE is not followed");
            Check(Scalar(db, "SELECT COUNT(*) FROM symbols WHERE type='include' AND name IN ('Phantom.inc','GoneCommented.inc','Phantom2.inc')") == 0,
                  "F1", "no include symbol for a commented INCLUDE (PROGRAM data, and inside an .inc)");
            Check(Scalar(db, "SELECT COUNT(*) FROM indexed_files WHERE pass='pass2b-include' AND file_name IN ('Phantom.inc','GoneCommented.inc','Phantom2.inc')") == 0,
                  "F2", "Pass 2b neither parses nor reports them (" + Outcome(db, "Phantom.inc") + ", " + Outcome(db, "GoneCommented.inc") + ")");
            Check(Scalar(db, "SELECT COUNT(*) FROM symbols WHERE name='PhantomThing.Go' AND decl_kind='implementation'") == 0,
                  "F3", "the MODULE() .clw of a class in a commented INCLUDE is not indexed");

            Console.WriteLine("-- conditional COMPILE/OMIT INCLUDE that does not resolve");
            Check(Outcome(db, "CondMissing.inc") == "unresolved_conditional", "F4", "COMPILE('t', expr) block: CondMissing.inc is " + Outcome(db, "CondMissing.inc"));
            Check(Outcome(db, "CondMissing2.inc") == "unresolved_conditional", "F5", "OMIT('t', expr) block in a MEMBER: CondMissing2.inc is " + Outcome(db, "CondMissing2.inc"));
            Check(Outcome(db, "Nested.inc") == "unresolved_conditional", "F6", "INCLUDEd by a conditionally INCLUDEd .inc: Nested.inc is " + Outcome(db, "Nested.inc"));
            Check(Outcome(db, "Deep.inc") == "unresolved", "F6b",
                  "conditional in a MEMBER, unconditional through Wrap.inc (found later in the walk): Deep.inc is " + Outcome(db, "Deep.inc"));
            Check(Outcome(db, "SharedMissing.inc") == "unresolved", "F6c",
                  "under an .inc INCLUDEd conditionally AND through Wrap.inc unconditionally: SharedMissing.inc is " + Outcome(db, "SharedMissing.inc"));

            Console.WriteLine("-- the warning names the real gaps, and only those");
            Check(!warning.Contains("CondMissing") && !warning.Contains("Nested.inc"), "F7", "conditional targets are not in the warning");
            Check(warning.StartsWith("6 INCLUDE()d"), "F8", "the warning counts the 6 real gaps: '" + warning + "'");
            Check(warning.Contains("Missing.inc (App.clw:5)") && warning.Contains("AfterBlock.inc (App.clw:10)"),
                  "F9", "the warning names each gap and the line that INCLUDEs it");
            Check(log.Any(l => l.Contains("INCLUDE not resolved: CondMissing.inc (App.clw:7)") && l.Contains("conditional")),
                  "F10", "the log names a conditional target and where it is INCLUDEd");

            Console.WriteLine("-- precision: what must stay as it was");
            Check(Outcome(db, "Missing.inc") == "unresolved", "P1", "an unconditional INCLUDE that does not resolve is unresolved");
            Check(Outcome(db, "AfterBlock.inc") == "unresolved", "P2", "a line after the block's terminator is unconditional again");
            Check(Outcome(db, "UncondCompile.inc") == "unresolved", "P3", "COMPILE('t') without a condition is not a conditional block");
            Check(Outcome(db, "Both.inc") == "unresolved", "P4", "INCLUDEd unconditionally once and conditionally once: unresolved");
            Check(Outcome(db, "Real.inc") == "resolved_parsed" &&
                  Scalar(db, "SELECT COUNT(*) FROM symbols WHERE name='RealThing' AND type='class'") == 1,
                  "P5", "a live INCLUDE is still parsed (Real.inc)");
            Check(Outcome(db, "CondParent.inc") == "resolved_parsed", "P6", "a conditional INCLUDE that resolves is still parsed");
            Check(warning.Contains("SELECT * FROM indexed_files WHERE outcome='unresolved' AND pass='pass2b-include'"),
                  "P7", "the warning keeps the query");
            Check(Scalar(db, "SELECT COUNT(*) FROM symbols WHERE type='include' AND name='MapProto.inc'") == 1,
                  "P8", "an INCLUDE inside the PROGRAM's MAP is stored once (Pass 1), not again by the whole-file parse");
            Check(Scalar(db, "SELECT COUNT(*) FROM symbols WHERE type='include' AND name='Missing.inc'") == 1,
                  "P9", "an INCLUDE in the PROGRAM's global data is stored, once");

            Console.WriteLine("-- an incremental run with nothing changed");
            var again = indexer.IndexSolution(slnPath, true, null);
            string warning2 = again.Warnings.FirstOrDefault(w => w.Contains("INCLUDE()d")) ?? "";
            Check(warning2 == warning, "I1", "still names the same gaps, from the database: '" + warning2 + "'");
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
            ? "CodeGraphIndexer.IncludeOutcome: " + _assertions + " assertions, all passed"
            : "CodeGraphIndexer.IncludeOutcome: " + Failures.Count + " of " + _assertions + " FAILED");
        return Failures.Count == 0 ? 0 : 1;
    }
}
