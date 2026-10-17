// CodeGraph indexes global data declared in an .inc INCLUDEd into the PROGRAM's global data, and links calls
// made through such a global object to the class's method implementation (GH #246 part 2).
//
// Mark's case, end to end through the REAL CodeGraphIndexer (+ parser, resolver, .red service, SQLite):
//
//   ctQ_ActiveThreads.inc     ctQ_ActiveThreads:ThreadSafe CLASS(ctQ_ActiveThreads),TYPE,MODULE(...),LINK(...)
//   GlobalMirroredData.inc    INCLUDE('ctQ_ActiveThreads.inc'),ONCE
//                             ActiveThreads   ctQ_ActiveThreads:ThreadSafe
//   b4.clw (PROGRAM)          INCLUDE('GlobalMirroredData.inc'),ONCE        <- global data
//   N member modules          ActiveThreads.Add('...', ..., THREAD())
//
// Before: no variable row for ActiveThreads, and "who calls ctQ_ActiveThreads:ThreadSafe.Add" found only the
// class's own internal caller. Every feature check is re-run with IndexGlobalDataIncludes=false, where it MUST
// fail; the call-resolution half is proven separately by mutation (see the ticket notes).
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClarionCodeGraph.Graph;

static class CodeGraphIndexerGlobalObjectTest
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
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }

    const string G1 = "22222222-0000-0000-0000-000000000001";
    const string G2 = "22222222-0000-0000-0000-000000000002";

    static readonly string[] CallingModules = { "Mod1", "Mod2", "Mod3", "Mod4", "Mod5" };

    static string BuildFixture(string root)
    {
        string sln = Path.Combine(root, "sln");
        string lib = Path.Combine(root, "shared", "libsrc");
        string app = Path.Combine(sln, "b4");

        Write(Path.Combine(lib, "ctQ_ActiveThreads.inc"),
            "ctQ_ActiveThreads   CLASS,TYPE,MODULE('ctQ_ActiveThreads.clw'),LINK('ctQ_ActiveThreads.clw')",
            "  Add                 PROCEDURE(STRING Name, STRING Category, SIGNED ThreadNum),VIRTUAL",
            "  Count               PROCEDURE(),LONG",
            "  CopyTo              PROCEDURE()",
            "                    END",
            "ctQ_ActiveThreads:ThreadSafe CLASS(ctQ_ActiveThreads),TYPE,MODULE('ctQ_ActiveThreads.clw'),LINK('ctQ_ActiveThreads.clw')",
            "  Add                 PROCEDURE(STRING Name, STRING Category, SIGNED ThreadNum),VIRTUAL",
            "                    END");
        Write(Path.Combine(lib, "ctQ_ActiveThreads.clw"),
            "  MEMBER()",
            "  INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "ctQ_ActiveThreads.Add PROCEDURE(STRING Name, STRING Category, SIGNED ThreadNum)",
            "  CODE",
            "ctQ_ActiveThreads.Count PROCEDURE()",
            "  CODE",
            "  RETURN 0",
            "ctQ_ActiveThreads.CopyTo PROCEDURE()",
            "  CODE",
            "  SELF.Add('copy', 'x', 0)",
            "ctQ_ActiveThreads:ThreadSafe.Add PROCEDURE(STRING Name, STRING Category, SIGNED ThreadNum)",
            "  CODE",
            "  PARENT.Add(Name, Category, ThreadNum)");
        Write(Path.Combine(lib, "ctThreadMonitor.inc"),
            "ctThreadMonitor     CLASS,TYPE,MODULE('ctThreadMonitor.clw')",
            "  Ping                PROCEDURE()",
            "                    END");
        Write(Path.Combine(lib, "ctThreadMonitor.clw"),
            "  MEMBER()",
            "ctThreadMonitor.Ping PROCEDURE()",
            "  CODE");

        // The global-data .inc: nested INCLUDEs, two global objects (one with ,THREAD), a scalar, a GROUP.
        Write(Path.Combine(app, "GlobalMirroredData.inc"),
            "       INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "       INCLUDE('MoreGlobals.inc'),ONCE",
            "ActiveThreads   ctQ_ActiveThreads:ThreadSafe",
            "ThreadMonitor   ctThreadMonitor,THREAD",
            "GLO:Counter     LONG",
            "GLO:Settings    GROUP,PRE(SET)",
            "Inner             LONG",
            "                END",
            "GMD:Max         EQUATE(10)",
            // A CLASS,TYPE whose GROUP member has its own END: the members after it are NOT globals.
            "GmdHolder       CLASS,TYPE",
            "HolderGrp         GROUP",
            "HolderX             LONG",
            "                  END",
            "HolderAfter       LONG",
            "                END",
            // A TYPE declares no data.
            "GmdQType        QUEUE,TYPE",
            "GmdQField         LONG",
            "                END",
            "AfterTypes      LONG");
        Write(Path.Combine(app, "MoreGlobals.inc"),
            "       INCLUDE('ctThreadMonitor.inc'),ONCE",
            "NestedGlobal    STRING(20)");
        // Included into a PROCEDURE's local data: its declarations are NOT global.
        Write(Path.Combine(app, "LocalBits.inc"),
            "LocalOnly       LONG");
        // Included inside a CLASS body in the PROGRAM: not global data either.
        Write(Path.Combine(app, "ClassBits.inc"),
            "MemberOnly        LONG");

        Write(Path.Combine(app, "b4.cwproj"),
            "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
            "  <PropertyGroup><ProjectGuid>{" + G1 + "}</ProjectGuid><OutputType>WinExe</OutputType><AssemblyName>b4</AssemblyName></PropertyGroup>\r\n" +
            "  <ItemGroup>\r\n    <Compile Include=\"b4.clw\" />\r\n" +
            string.Concat(Array.ConvertAll(CallingModules, m => "    <Compile Include=\"" + m + ".clw\" />\r\n")) +
            "    <Compile Include=\"Shadow.clw\" />\r\n    <Compile Include=\"Inherit.clw\" />\r\n" +
            "  </ItemGroup>\r\n</Project>\r\n");

        var map = new List<string>();
        foreach (string m in CallingModules)
        {
            map.Add("    MODULE('" + m + ".clw')");
            map.Add("      " + m + "Proc  PROCEDURE()");
            map.Add("    END");
        }
        var main = new List<string>
        {
            "  PROGRAM",
            " INCLUDE('GlobalMirroredData.inc'),ONCE",
            "! INCLUDE('Commented.inc'),ONCE",
            "Holder          CLASS",
            "  INCLUDE('ClassBits.inc')",
            "                END",
            "  MAP"
        };
        main.AddRange(map);
        main.AddRange(new[]
        {
            "    MODULE('Shadow.clw')",
            "      ShadowProc  PROCEDURE()",
            "    END",
            "    MODULE('Inherit.clw')",
            "      InheritProc  PROCEDURE()",
            "    END",
            "  END",
            "  CODE",
            "  ActiveThreads.Add('main', 'x', THREAD())"
        });
        Write(Path.Combine(app, "b4.clw"), main.ToArray());

        foreach (string m in CallingModules)
            Write(Path.Combine(app, m + ".clw"),
                "  MEMBER('b4.clw')",
                m + "Proc PROCEDURE()",
                "  INCLUDE('LocalBits.inc')",
                "  CODE",
                "  ActiveThreads.Add('" + m + "', 'Worker', THREAD())",
                "  ThreadMonitor.Ping()");
        // A LOCAL of the same name shadows the global: its call must not land on the global's class.
        Write(Path.Combine(app, "Shadow.clw"),
            "  MEMBER('b4.clw')",
            "ShadowProc PROCEDURE()",
            "ActiveThreads   ctThreadMonitor",
            "  CODE",
            "  ActiveThreads.Ping()",
            "  ActiveThreads.Add('shadow', 'x', 0)");
        // Count is declared only on the base class: the call through the derived-typed global walks up to it.
        Write(Path.Combine(app, "Inherit.clw"),
            "  MEMBER('b4.clw')",
            "InheritProc PROCEDURE()",
            "  CODE",
            "  IF ActiveThreads.Count() > 0 THEN GLO:Counter += 1.");

        // A second app with its OWN global of the same name and class: calls must stay in their own app.
        string app2 = Path.Combine(sln, "other");
        Write(Path.Combine(app2, "other.cwproj"),
            "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
            "  <PropertyGroup><ProjectGuid>{" + G2 + "}</ProjectGuid><OutputType>WinExe</OutputType><AssemblyName>other</AssemblyName></PropertyGroup>\r\n" +
            "  <ItemGroup>\r\n    <Compile Include=\"other.clw\" />\r\n    <Compile Include=\"OtherMod.clw\" />\r\n  </ItemGroup>\r\n</Project>\r\n");
        Write(Path.Combine(app2, "OtherGlobals.inc"),
            "       INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "ActiveThreads   ctQ_ActiveThreads:ThreadSafe");
        Write(Path.Combine(app2, "other.clw"),
            "  PROGRAM",
            "  INCLUDE('OtherGlobals.inc'),ONCE",
            "  MAP",
            "    MODULE('OtherMod.clw')",
            "      OtherProc  PROCEDURE()",
            "    END",
            "  END",
            "  CODE");
        Write(Path.Combine(app2, "OtherMod.clw"),
            "  MEMBER('other.clw')",
            "OtherProc PROCEDURE()",
            "  CODE",
            "  ActiveThreads.Add('other', 'x', 1)");

        Write(Path.Combine(sln, "Clarion110.red"),
            "[Common]",
            "*.inc = " + lib,
            "*.clw = " + lib,
            "");
        string slnPath = Path.Combine(sln, "b4.sln");
        File.WriteAllText(slnPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"b4\", \"b4\\b4.cwproj\", \"{" + G1 + "}\"\r\nEndProject\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"other\", \"other\\other.cwproj\", \"{" + G2 + "}\"\r\nEndProject\r\n",
            new UTF8Encoding(false));
        return slnPath;
    }

    sealed class Run
    {
        public IndexResult Result;
        public CodeGraphDatabase Db;
        public int Scalar(string sql)
        {
            var dt = Db.ExecuteQuery(sql);
            return dt.Rows.Count == 0 || dt.Rows[0][0] == DBNull.Value ? 0 : Convert.ToInt32(dt.Rows[0][0]);
        }
        public string Text(string sql)
        {
            var dt = Db.ExecuteQuery(sql);
            return dt.Rows.Count == 0 || dt.Rows[0][0] == DBNull.Value ? null : dt.Rows[0][0].ToString();
        }
    }

    static Run Index(string slnPath, string dbPath, bool globals)
    {
        var red = new ClarionAssistant.Services.RedFileService();
        var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["THISDIR"] = Path.GetDirectoryName(slnPath) };
        if (!red.Load(Path.Combine(Path.GetDirectoryName(slnPath), "Clarion110.red"), macros))
            throw new Exception("fixture .red did not load");
        var db = new CodeGraphDatabase();
        db.Open(dbPath);
        var indexer = new CodeGraphIndexer(db) { RedService = red, IndexGlobalDataIncludes = globals };
        // Library paths as every host passes them (the .red's *.inc folders): the class library's declarations
        // land in __Libraries__, which is where the inheritance walk finds ThreadSafe's parent.
        string lib = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(slnPath)), "shared", "libsrc");
        var result = indexer.IndexSolution(slnPath, false, new List<string> { lib });
        return new Run { Result = result, Db = db };
    }

    // Callers (from-symbol names) of the implementation NAME in project PROJ.
    static string CallersSql(string target, string proj)
    {
        return "SELECT COUNT(DISTINCT f.name) FROM relationships r JOIN symbols f ON f.id=r.from_id JOIN symbols t ON t.id=r.to_id " +
               "JOIN projects p ON p.id=f.project_id " +
               "WHERE r.type='calls' AND t.name='" + target + "' AND t.decl_kind='implementation' AND p.name='" + proj + "'";
    }

    static List<KeyValuePair<string, bool>> FeatureChecks(Run r)
    {
        var list = new List<KeyValuePair<string, bool>>();
        Action<string, bool> add = (n, b) => list.Add(new KeyValuePair<string, bool>(n, b));

        add("G1 ActiveThreads (declared in the INCLUDEd .inc) is a scope='global' variable typed ctQ_ActiveThreads:ThreadSafe",
            r.Scalar("SELECT COUNT(*) FROM symbols s JOIN projects p ON p.id=s.project_id WHERE s.name='ActiveThreads' AND s.type='variable' " +
                     "AND s.scope='global' AND UPPER(s.params)='CTQ_ACTIVETHREADS:THREADSAFE' AND s.file_path LIKE '%GlobalMirroredData.inc' AND p.name='b4'") == 1);
        add("G2 ThreadMonitor (,THREAD) is a global too",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ThreadMonitor' AND type='variable' AND scope='global'") == 1);
        add("G3 a scalar and a GROUP in the .inc are globals (GLO:Counter, GLO:Settings)",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name IN ('GLO:Counter','GLO:Settings') AND type='variable' AND scope='global'") == 2);
        add("G4 a NESTED global-data include is followed (NestedGlobal in MoreGlobals.inc)",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name='NestedGlobal' AND scope='global'") == 1);
        add("G5 every one of the 5 member modules' ActiveThreads.Add(...) is a caller of ctQ_ActiveThreads:ThreadSafe.Add",
            r.Scalar(CallersSql("ctQ_ActiveThreads:ThreadSafe.Add", "b4") +
                     " AND f.name IN ('Mod1Proc','Mod2Proc','Mod3Proc','Mod4Proc','Mod5Proc')") == 5);
        add("G6 the PROGRAM's own global CODE call is linked too",
            r.Scalar(CallersSql("ctQ_ActiveThreads:ThreadSafe.Add", "b4") + " AND f.type='program'") == 1);
        add("G7 ActiveThreads.Count() (declared only on the base class) lands on the inherited ctQ_ActiveThreads.Count",
            r.Scalar(CallersSql("ctQ_ActiveThreads.Count", "b4") + " AND f.name='InheritProc'") == 1);
        add("G8 a second global object's calls are linked (ThreadMonitor.Ping x5)",
            r.Scalar(CallersSql("ctThreadMonitor.Ping", "b4") + " AND f.name LIKE 'Mod%Proc'") == 5);
        add("G9 a reference edge runs from a member module to the .inc-declared global",
            r.Scalar("SELECT COUNT(*) FROM relationships r JOIN symbols f ON f.id=r.from_id JOIN symbols t ON t.id=r.to_id " +
                     "WHERE r.type='references' AND t.name='GLO:Counter' AND f.name='InheritProc'") >= 1);
        add("G10 indexed_files records the global-data reading (pass2d-global-data) of GlobalMirroredData.inc",
            r.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2d-global-data' AND file_name='GlobalMirroredData.inc' AND outcome='resolved_parsed'") == 1);
        return list;
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-globobj-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        var opened = new List<CodeGraphDatabase>();
        try
        {
            string slnPath = BuildFixture(root);

            Console.WriteLine("-- global-data includes on");
            var on = Index(slnPath, Path.Combine(root, "on.db"), true);
            opened.Add(on.Db);
            foreach (var c in FeatureChecks(on))
            {
                int sp = c.Key.IndexOf(' ');
                Check(c.Value, c.Key.Substring(0, sp), c.Key.Substring(sp + 1));
            }

            Console.WriteLine("-- control: global-data includes off (each check above must fail here)");
            var off = Index(slnPath, Path.Combine(root, "off.db"), false);
            opened.Add(off.Db);
            foreach (var c in FeatureChecks(off))
            {
                int sp = c.Key.IndexOf(' ');
                Check(!c.Value, "C" + c.Key.Substring(1, sp - 1), "without it, '" + c.Key.Substring(sp + 1) + "' does NOT hold");
            }
            Console.WriteLine("   before (off): callers of ctQ_ActiveThreads:ThreadSafe.Add in b4 = " +
                off.Scalar(CallersSql("ctQ_ActiveThreads:ThreadSafe.Add", "b4")) + "; after (on) = " +
                on.Scalar(CallersSql("ctQ_ActiveThreads:ThreadSafe.Add", "b4")));

            Console.WriteLine("-- precision");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='LocalOnly' AND scope='global'") == 0,
                  "P1", "an .inc INCLUDEd into a PROCEDURE's local data does not make globals");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='MemberOnly' AND scope='global'") == 0,
                  "P2", "an .inc INCLUDEd inside a CLASS body in the PROGRAM does not make globals");
            Check(on.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2d-global-data' AND file_name='Commented.inc'") == 0,
                  "P3", "a commented-out INCLUDE is not followed");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='GMD:Max'") == 1,
                  "P4", "an EQUATE in the global-data .inc appears once (ParseIncFile's row, not a second global copy)");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ActiveThreads' AND scope='global'") == 2,
                  "P5", "each app has its own ActiveThreads global (b4 + other), not one shared row");
            Check(on.Scalar(CallersSql("ctQ_ActiveThreads:ThreadSafe.Add", "b4") + " AND f.name='ShadowProc'") == 0 &&
                  on.Scalar(CallersSql("ctThreadMonitor.Ping", "b4") + " AND f.name='ShadowProc'") == 1,
                  "P6", "a LOCAL named ActiveThreads shadows the global: its calls resolve through the local's class");
            Check(on.Scalar("SELECT COUNT(*) FROM relationships r JOIN symbols f ON f.id=r.from_id JOIN symbols t ON t.id=r.to_id " +
                            "WHERE r.type='calls' AND f.name='Mod1Proc' AND t.name='ctQ_ActiveThreads:ThreadSafe.Add' AND r.ambiguous=0") == 1,
                  "P7", "the call through b4's global is not flagged ambiguous (the other app's same-named global is narrowed away)");
            Check(on.Scalar(CallersSql("ctQ_ActiveThreads:ThreadSafe.Add", "other") + " AND f.name='OtherProc'") == 1,
                  "P8", "the other app's call through ITS global is linked as well");
            Check(on.Scalar(CallersSql("ctQ_ActiveThreads.Add", "b4") + " AND f.name LIKE 'Mod%Proc'") == 0,
                  "P9", "Add is overridden on ThreadSafe, so the modules' calls do NOT walk up to the base Add");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE scope='global' AND name IN ('HolderGrp','HolderX','HolderAfter')") == 0,
                  "P10", "members of a CLASS in the .inc - after a GROUP member's own END too - are not globals");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE scope='global' AND name IN ('GmdQType','GmdQField')") == 0,
                  "P11", "a QUEUE,TYPE declares no global");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE scope='global' AND name='AfterTypes'") == 1,
                  "P12", "a declaration after the CLASS and the TYPE is a global again");
        }
        catch (Exception ex)
        {
            Check(false, "X", "harness threw: " + ex);
        }
        finally
        {
            foreach (var db in opened) { try { db.Close(); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "CodeGraphIndexer.GlobalObject: " + _assertions + " assertions, all passed"
            : "CodeGraphIndexer.GlobalObject: " + Failures.Count + " of " + _assertions + " FAILED");
        return Failures.Count == 0 ? 0 : 1;
    }
}
