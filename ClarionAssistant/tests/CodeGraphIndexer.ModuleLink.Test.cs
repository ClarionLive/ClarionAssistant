// CodeGraph follows CLASS MODULE()/LINK() to the implementing .clw (GH #245), and says how much of the
// solution it covered (GH #244 item 3).
//
// Runs the REAL CodeGraphIndexer (+ parser, resolver, .red service, SQLite database) over a solution written to
// a temp dir, then queries the database it produced. The shape is Mark's: a shared class library outside the
// app folder, reached ONLY through INCLUDE('x.inc') + "X CLASS,TYPE,MODULE('x.clw'),LINK('x.clw')" - no .cwproj
// lists the library's .clw files, so before Pass 2c their method bodies (and every call made in them) were never
// indexed.
//
// Every check that depends on Pass 2c is run twice: once as shipped, once with FollowClassModules=false, where it
// MUST fail. A check that also passes without the feature proves nothing; the control run says so.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using ClarionCodeGraph.Graph;

static class CodeGraphIndexerModuleLinkTest
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

    static string Cwproj(string guid, string name, params string[] compile)
    {
        var sb = new StringBuilder();
        sb.Append("<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n");
        sb.Append("  <PropertyGroup>\r\n    <ProjectGuid>{" + guid + "}</ProjectGuid>\r\n    <OutputType>WinExe</OutputType>\r\n");
        sb.Append("    <AssemblyName>" + name + "</AssemblyName>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n");
        foreach (string c in compile) sb.Append("    <Compile Include=\"" + c + "\" />\r\n");
        sb.Append("  </ItemGroup>\r\n</Project>\r\n");
        return sb.ToString();
    }

    static string SlnProject(string name, string rel, string guid)
    {
        return "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"" + name + "\", \"" + rel + "\", \"{" + guid + "}\"\r\nEndProject\r\n";
    }

    const string G1 = "11111111-0000-0000-0000-000000000001";
    const string G2 = "11111111-0000-0000-0000-000000000002";

    /// <summary>The #245 fixture. Returns the .sln path.</summary>
    static string BuildFixture(string root)
    {
        string sln = Path.Combine(root, "sln");
        string lib = Path.Combine(root, "shared", "libsrc");          // the shared class library, outside the app folders
        string clarion = Path.Combine(root, "clarion");                // stands in for the Clarion install (%ROOT%)
        string clarionLib = Path.Combine(clarion, "libsrc");

        // ---- the shared library: reached only through INCLUDE + CLASS MODULE/LINK ----
        Write(Path.Combine(lib, "ctQ_ActiveThreads.inc"),
            "  INCLUDE('ctBase.inc'),ONCE",
            "ctQ_ActiveThreads   CLASS(ctBase),TYPE,MODULE('ctQ_ActiveThreads.clw'),LINK('ctQ_ActiveThreads.clw')",
            "  Add                 PROCEDURE(STRING Name, STRING Category, SIGNED ThreadNum),VIRTUAL",
            "  Helper              PROCEDURE()",
            "                    END",
            "ctQ_ActiveThreads:ThreadSafe CLASS(ctQ_ActiveThreads),TYPE,MODULE('ctQ_ActiveThreads_TS.clw'),LINK('ctQ_ActiveThreads_TS.clw') ! MODULE('NotThis.clw')",
            "  Lock                PROCEDURE()",
            "                    END");
        // Nested: reached only through ctQ_ActiveThreads.inc's own INCLUDE. LINK only, no MODULE. Plus a class whose
        // MODULE file does not exist anywhere, and a LINK naming a .lib (not source).
        Write(Path.Combine(lib, "ctBase.inc"),
            "ctBase              CLASS,TYPE,LINK('ctBase.clw')",
            "  Init                PROCEDURE()",
            "                    END",
            "Ghost               CLASS,TYPE,MODULE('Ghost.clw'),LINK('ghost.lib')",
            "  Boo                 PROCEDURE()",
            "                    END");
        Write(Path.Combine(lib, "ctQ_ActiveThreads.clw"),
            "  MEMBER()",
            "  INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "  MAP",
            "  END",
            "ctQ_ActiveThreads.Add PROCEDURE(STRING Name, STRING Category, SIGNED ThreadNum)",
            "  CODE",
            "  SELF.Helper()",
            "ctQ_ActiveThreads.Helper PROCEDURE()",
            "  CODE",
            "  SELF.Init()");
        Write(Path.Combine(lib, "ctQ_ActiveThreads_TS.clw"),
            "  MEMBER()",
            "  INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "ctQ_ActiveThreads:ThreadSafe.Lock PROCEDURE()",
            "  CODE");
        Write(Path.Combine(lib, "ctBase.clw"),
            "  MEMBER()",
            "  INCLUDE('ctBase.inc'),ONCE",
            "ctBase.Init PROCEDURE()",
            "  CODE");
        // A decoy beside the library: no class names it, so it must NOT be indexed.
        Write(Path.Combine(lib, "NotThis.clw"),
            "  MEMBER()",
            "NotThisProc PROCEDURE()",
            "  CODE");

        // ---- a class under the Clarion install: its MODULE file is NOT followed ----
        Write(Path.Combine(clarionLib, "ABFAKE.CLW"),
            "  MEMBER()",
            "FakeABC.Go PROCEDURE()",
            "  CODE");
        // An install .inc the walk must not even read: its own INCLUDE and MODULE are never followed.
        Write(Path.Combine(clarionLib, "ABFAKE.INC"),
            "  INCLUDE('ABDEEP.INC'),ONCE",
            "FakeABC             CLASS,TYPE,MODULE('ABFAKE.CLW'),LINK('ABFAKE.CLW')",
            "  Go                  PROCEDURE()",
            "                    END");
        Write(Path.Combine(clarionLib, "ABDEEP.INC"),
            "DeepABC             CLASS,TYPE,MODULE('ABDEEP.CLW')",
            "  Go                  PROCEDURE()",
            "                    END");
        // The app's OWN class naming a .clw under the install: not followed either.
        Write(Path.Combine(clarionLib, "ROOTIMPL.CLW"), "  MEMBER()", "AppRootThing.Go PROCEDURE()", "  CODE");

        // ---- configuration: CfgPick.clw exists for Debug and for Release ----
        Write(Path.Combine(root, "cfg", "CfgPick.inc"),
            "CfgPick             CLASS,TYPE,MODULE('CfgPick.clw')",
            "  Pick                PROCEDURE()",
            "                    END");
        Write(Path.Combine(root, "cfgDebug", "CfgPick.clw"), "  MEMBER()", "CfgPick.Pick PROCEDURE()", "  CODE", "  DebugOnlyCall()");
        Write(Path.Combine(root, "cfgRelease", "CfgPick.clw"), "  MEMBER()", "CfgPick.Pick PROCEDURE()", "  CODE");

        // ---- the solution's .red ----
        string red = string.Join("\r\n", new[]
        {
            "[Common]",
            "*.inc = " + lib + ";" + clarionLib + ";" + Path.Combine(root, "cfg"),
            "*.clw = " + lib + ";" + clarionLib,
            "[Debug]",
            "*.clw = " + Path.Combine(root, "cfgDebug"),
            "[Release]",
            "*.clw = " + Path.Combine(root, "cfgRelease"),
            ""
        });
        Write(Path.Combine(sln, "Clarion110.red"), red);

        // ---- App: PROGRAM + one member, both listed in its .cwproj ----
        Write(Path.Combine(sln, "App", "App.cwproj"), Cwproj(G1, "App", "App.clw", "AppMod.clw", "Missing.clw"));
        Write(Path.Combine(sln, "App", "App.clw"),
            "  PROGRAM",
            "  INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "  INCLUDE('ABFAKE.INC'),ONCE",
            "  INCLUDE('CfgPick.inc'),ONCE",
            "  MAP",
            "    MODULE('AppMod.clw')",
            "      DoWork  PROCEDURE()",
            "    END",
            "  END",
            "  CODE",
            "  DoWork()");
        Write(Path.Combine(sln, "App", "AppMod.clw"),
            "  MEMBER('App.clw')",
            "DoWork PROCEDURE()",
            "Q   ctQ_ActiveThreads",
            "  CODE",
            "  Q.Add('a', 'b', 1)");

        // ---- App2: its own version-named .red (supersedes the solution's), and the same shared library ----
        string app2 = Path.Combine(sln, "App2");
        Write(Path.Combine(app2, "App2.cwproj"), Cwproj(G2, "App2", "App2.clw"));
        Write(Path.Combine(app2, "App2.clw"),
            "  PROGRAM",
            "  INCLUDE('ctQ_ActiveThreads.inc'),ONCE",
            "  INCLUDE('App2Lib.inc'),ONCE",
            "  MAP",
            "  END",
            "  CODE");
        Write(Path.Combine(app2, "App2Lib.inc"),
            "  INCLUDE('ABFAKE.INC'),ONCE",
            "App2Thing           CLASS,TYPE,MODULE('App2Thing.clw')",
            "  Run                 PROCEDURE()",
            "                    END",
            "AppRootThing        CLASS,TYPE,MODULE('ROOTIMPL.CLW')",
            "  Go                  PROCEDURE()",
            "                    END");
        Write(Path.Combine(app2, "lib2", "App2Thing.clw"), "  MEMBER()", "App2Thing.Run PROCEDURE()", "  CODE");
        Write(Path.Combine(app2, "Clarion110.red"),
            "[Common]",
            "*.inc = " + lib + ";" + clarionLib,
            "*.clw = " + lib + ";.\\lib2;" + clarionLib,
            "");

        string slnPath = Path.Combine(sln, "ModuleLink.sln");
        File.WriteAllText(slnPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            SlnProject("App", "App\\App.cwproj", G1) +
            SlnProject("App2", "App2\\App2.cwproj", G2) +
            SlnProject("Gone", "Gone\\Gone.cwproj", "11111111-0000-0000-0000-000000000003"),
            new UTF8Encoding(false));
        return slnPath;
    }

    static ClarionAssistant.Services.RedFileService LoadRed(string slnPath, string clarionRoot)
    {
        var red = new ClarionAssistant.Services.RedFileService();
        var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["THISDIR"] = Path.GetDirectoryName(slnPath),
            ["ROOT"] = clarionRoot
        };
        if (!red.Load(Path.Combine(Path.GetDirectoryName(slnPath), "Clarion110.red"), macros))
            throw new Exception("fixture .red did not load");
        return red;
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

    static Run Index(string slnPath, string dbPath, string clarionRoot, bool follow, string configuration, bool incremental = false)
    {
        var db = new CodeGraphDatabase();
        db.Open(dbPath);
        var indexer = new CodeGraphIndexer(db)
        {
            RedService = LoadRed(slnPath, clarionRoot),
            FollowClassModules = follow,
            BuildConfiguration = configuration
        };
        var log = new List<string>();
        indexer.OnProgress += m => log.Add(m);
        var result = indexer.IndexSolution(slnPath, incremental, null);
        return new Run { Result = result, Db = db };
    }

    static string Q(string s) { return s.Replace("'", "''"); }

    /// <summary>The Pass 2c checks. Each returns true when it holds; the control run expects them false.</summary>
    static List<KeyValuePair<string, bool>> FeatureChecks(Run r, string lib)
    {
        string ts = Q(Path.Combine(lib, "ctQ_ActiveThreads.clw"));
        var list = new List<KeyValuePair<string, bool>>();
        Func<string, bool, KeyValuePair<string, bool>> kv = (n, b) => new KeyValuePair<string, bool>(n, b);

        list.Add(kv("A1 ctQ_ActiveThreads.Add has an implementation row in the library .clw",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctQ_ActiveThreads.Add' AND decl_kind='implementation' AND file_path='" + ts + "'") == 1));
        list.Add(kv("A2 the colon-labelled class's MODULE file is indexed (ctQ_ActiveThreads:ThreadSafe.Lock)",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctQ_ActiveThreads:ThreadSafe.Lock' AND decl_kind='implementation'") == 1));
        list.Add(kv("A3 a LINK-only target reached through a NESTED include is indexed (ctBase.Init)",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctBase.Init' AND decl_kind='implementation'") == 1));
        list.Add(kv("A4 calls made INSIDE the library .clw are edges (Add -> Helper)",
            r.Scalar("SELECT COUNT(*) FROM relationships r JOIN symbols f ON f.id=r.from_id JOIN symbols t ON t.id=r.to_id " +
                     "WHERE r.type='calls' AND f.name='ctQ_ActiveThreads.Add' AND t.name='ctQ_ActiveThreads.Helper' AND t.decl_kind='implementation'") == 1));
        list.Add(kv("A5 the app's call Q.Add() lands on the IMPLEMENTATION in the library .clw, not the .inc prototype",
            r.Scalar("SELECT COUNT(*) FROM relationships r JOIN symbols f ON f.id=r.from_id JOIN symbols t ON t.id=r.to_id " +
                     "WHERE r.type='calls' AND f.name='DoWork' AND t.name='ctQ_ActiveThreads.Add' AND t.file_path='" + ts + "'") == 1));
        list.Add(kv("A6 indexed_files records the library .clw as reached by pass2c-module",
            r.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2c-module' AND outcome='resolved_parsed' AND resolved_path='" + ts + "'") == 1));
        list.Add(kv("A7 App2's MODULE target resolves through App2's OWN .red (.\\lib2)",
            r.Scalar("SELECT COUNT(*) FROM symbols WHERE name='App2Thing.Run' AND decl_kind='implementation'") == 1));
        list.Add(kv("A8 an unresolvable MODULE target is recorded unresolved (Ghost.clw)",
            r.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2c-module' AND outcome='unresolved' AND file_name='Ghost.clw'") >= 1));
        list.Add(kv("A9 the walk stops at an .inc under the Clarion install, recorded ONCE per run though two apps include it",
            r.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2c-module' AND outcome='skipped_clarion_install' AND file_name='ABFAKE.INC'") == 1));
        return list;
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-modlink-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        var opened = new List<CodeGraphDatabase>();
        string legacyDbForStale = null;
        try
        {
            string slnPath = BuildFixture(root);
            string lib = Path.Combine(root, "shared", "libsrc");
            string clarionRoot = Path.Combine(root, "clarion");

            // ---------------- as shipped, Release configuration ----------------
            Console.WriteLine("-- Pass 2c on (configuration Release)");
            var on = Index(slnPath, Path.Combine(root, "on.db"), clarionRoot, true, "Release");
            opened.Add(on.Db);
            var onChecks = FeatureChecks(on, lib);
            foreach (var c in onChecks) Check(c.Value, c.Key.Substring(0, 2), c.Key.Substring(3));

            // ---------------- control: Pass 2c off - every feature check must FAIL ----------------
            Console.WriteLine("-- control: Pass 2c off (each feature check above must fail here)");
            var off = Index(slnPath, Path.Combine(root, "off.db"), clarionRoot, false, "Release");
            opened.Add(off.Db);
            foreach (var c in FeatureChecks(off, lib))
                Check(!c.Value, "C" + c.Key.Substring(1, 1), "without Pass 2c, '" + c.Key.Substring(3) + "' does NOT hold");

            // ---------------- precision: nothing extra, nothing twice ----------------
            Console.WriteLine("-- precision");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='NotThisProc'") == 0,
                  "P1", "a .clw beside the library that no class names (and a MODULE() inside a comment) is not indexed");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='FakeABC.Go'") == 0,
                  "P2", "the Clarion-install class's .clw is not parsed");
            Check(on.Scalar("SELECT COUNT(*) FROM indexed_files WHERE file_name LIKE 'ghost.lib'") == 0,
                  "P3", "a LINK naming a .lib is not treated as source");
            Check(on.Scalar("SELECT COUNT(*) FROM indexed_files WHERE file_name IN ('ABFAKE.CLW','ABDEEP.INC','ABDEEP.CLW')") == 0,
                  "P3b", "the install .inc is not read: its INCLUDE (ABDEEP.INC) and MODULE (ABFAKE.CLW) leave no trace");
            Check(on.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2c-module' AND outcome='skipped_clarion_install' AND file_name='ROOTIMPL.CLW'") == 1 &&
                  on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='AppRootThing.Go' AND decl_kind='implementation'") == 0,
                  "P3c", "an app class naming a .clw under the install is not followed, and says so once");

            // Which indexer built the database (pipeline follow-up, adversary LOW).
            Console.WriteLine("-- indexer version / feature level");
            Check(on.Text("SELECT value FROM index_metadata WHERE key='indexer_feature_level'") == CodeGraphIndexer.FeatureLevel.ToString() &&
                  !string.IsNullOrEmpty(on.Text("SELECT value FROM index_metadata WHERE key='indexer_version'")) &&
                  on.Result.FeatureLevel == CodeGraphIndexer.FeatureLevel && on.Result.CoverageText().Contains("Indexer: "),
                  "V1", "the database and the result carry the indexer version and feature level");
            string onDesc = CodeGraphIndexer.DescribeDatabase(Path.Combine(root, "on.db"));
            Check(onDesc.Contains("feature level " + CodeGraphIndexer.FeatureLevel) && !onDesc.Contains("STALE"),
                  "V2", "a current database is described without STALE: " + onDesc);
            legacyDbForStale = Path.Combine(root, "legacy.db");
            Check(on.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2c-module' AND outcome='resolved_parsed' AND resolved_path='" +
                            Q(Path.Combine(lib, "ctQ_ActiveThreads.clw")) + "'") == 1 &&
                  on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctQ_ActiveThreads.Helper' AND decl_kind='implementation'") == 1,
                  "P4", "the library .clw both apps reach is indexed ONCE (dedup by resolved path, solution-wide)");
            Check(on.Scalar("SELECT COUNT(*) FROM symbols WHERE name='DoWork' AND decl_kind='implementation'") == 1 &&
                  on.Scalar("SELECT COUNT(*) FROM indexed_files WHERE pass='pass2c-module' AND file_name IN ('App.clw','AppMod.clw','App2.clw')") == 0,
                  "P5", ".cwproj-listed files are not re-parsed by Pass 2c");

            // ---------------- configuration: Release picks cfgRelease, unknown keeps Common/Debug/Release ----------------
            Console.WriteLine("-- build configuration");
            string pickPath = on.Text("SELECT resolved_path FROM indexed_files WHERE pass='pass2c-module' AND file_name='CfgPick.clw'");
            Check(pickPath != null && pickPath.IndexOf("cfgRelease", StringComparison.OrdinalIgnoreCase) >= 0,
                  "K1", "BuildConfiguration=Release resolves CfgPick.clw from [Release] - got " + (pickPath ?? "(none)"));
            var legacy = Index(slnPath, Path.Combine(root, "legacy.db"), clarionRoot, true, null);
            opened.Add(legacy.Db);
            string legacyPick = legacy.Text("SELECT resolved_path FROM indexed_files WHERE pass='pass2c-module' AND file_name='CfgPick.clw'");
            Check(legacyPick != null && legacyPick.IndexOf("cfgDebug", StringComparison.OrdinalIgnoreCase) >= 0,
                  "K2", "no configuration keeps the old order (Common, Debug, Release) - got " + (legacyPick ?? "(none)"));
            // A database an older indexer wrote has no feature-level row: listings must call it stale.
            legacy.Db.ExecuteQuery("DELETE FROM index_metadata WHERE key IN ('indexer_feature_level','indexer_version')");
            legacy.Db.Close(); opened.Remove(legacy.Db);
            string staleDesc = CodeGraphIndexer.DescribeDatabase(legacyDbForStale);
            Check(staleDesc.Contains("feature level 1") && staleDesc.Contains("STALE"),
                  "V3", "a database without the feature-level row is described STALE: " + staleDesc);

            // ---------------- #244 item 3: honest coverage ----------------
            Console.WriteLine("-- coverage / status (#244 item 3)");
            var res = on.Result;
            Check(res.ProjectsResolved == 2 && res.ProjectsUnresolved == 1 && res.UnresolvedProjectNames.Contains("Gone"),
                  "S1", "projects: 2 resolved, 1 unresolved (Gone) - got " + res.ProjectsResolved + "/" + res.ProjectsUnresolved);
            Check(res.CwprojFilesListed == 4 && res.CwprojFilesUnresolved == 1,
                  "S2", ".cwproj-listed files: 4 listed, 1 unresolved (Missing.clw) - got " + res.CwprojFilesListed + "/" + res.CwprojFilesUnresolved);
            Check(res.ModuleFilesIndexed == 5,
                  "S3", "MODULE/LINK files indexed = 5 (ctQ_ActiveThreads, _TS, ctBase, CfgPick, App2Thing) - got " + res.ModuleFilesIndexed);
            Check(res.ModuleTargetsUnresolved == 2 && res.ModuleTargetsSkipped == 2,
                  "S4", "MODULE/LINK unresolved = 2 (Ghost.clw, once per project), skipped = 2 (ABFAKE.INC once for both apps, ROOTIMPL.CLW) - got " +
                        res.ModuleTargetsUnresolved + "/" + res.ModuleTargetsSkipped);
            Check(res.Status == IndexResult.StatusWarning && res.Headline.StartsWith("CodeGraph indexed with warnings"),
                  "S5", "1 of 3 projects missing is a WARNING, not success and not partial - got " + res.Status);
            string text = res.CoverageText();
            Check(text.Contains("Status: warning") && text.Contains("Gone") && text.Contains("1 of 4 .cwproj-listed") &&
                  text.Contains("2 of 7 CLASS MODULE()/LINK()"),
                  "S6", "the coverage text names the missing project and the counts:\n" + text);
            Check(off.Result.ModuleFilesIndexed == 0, "S7", "control run reports 0 MODULE/LINK files");

            // Decide() thresholds, directly.
            var d = new IndexResult { ProjectsResolved = 1, ProjectsUnresolved = 2 };
            d.Decide();
            Check(d.IsPartial && d.Headline.Contains("PARTIAL"), "S8", "2 of 3 .cwproj missing = partial");
            d = new IndexResult { ProjectsResolved = 3, CwprojFilesListed = 10, CwprojFilesUnresolved = 6 };
            d.Decide();
            Check(d.IsPartial, "S9", "6 of 10 cwproj-listed files unresolved = partial");
            d = new IndexResult { ProjectsResolved = 3, CwprojFilesListed = 10, CwprojFilesUnresolved = 5 };
            d.Decide();
            Check(d.Status == IndexResult.StatusWarning, "S10", "5 of 10 unresolved (not most) = warning");
            d = new IndexResult { ProjectsResolved = 3, CwprojFilesListed = 10, ModuleFilesIndexed = 1, ModuleTargetsUnresolved = 3 };
            d.Decide();
            Check(d.IsPartial, "S11", "3 of 4 MODULE/LINK targets unresolved = partial");
            d = new IndexResult { ProjectsResolved = 2, CwprojFilesListed = 3 };
            d.Decide();
            Check(d.Status == IndexResult.StatusOk && d.Headline.StartsWith("CodeGraph indexed successfully"), "S12", "everything resolved = ok");
            d = new IndexResult();
            d.Decide();
            Check(d.IsPartial, "S13", "no projects at all = partial, never success");

            // ---------------- incremental: MODULE-reached files stay in, and an edit to one is a change ----------------
            Console.WriteLine("-- incremental");
            on.Db.Close(); opened.Remove(on.Db);
            // Only App2 changes: App (which owns the library .clw) is NOT re-parsed, but Pass 3 rebuilds every edge,
            // so App's MODULE-reached files must still be scanned.
            System.Threading.Thread.Sleep(1100);
            File.AppendAllText(Path.Combine(root, "sln", "App2", "App2.clw"), "  ! touched\r\n", new UTF8Encoding(false));
            var inc1 = Index(slnPath, Path.Combine(root, "on.db"), clarionRoot, true, "Release", incremental: true);
            opened.Add(inc1.Db);
            Check(inc1.Scalar("SELECT COUNT(*) FROM relationships r JOIN symbols f ON f.id=r.from_id JOIN symbols t ON t.id=r.to_id " +
                              "JOIN projects p ON p.id=f.project_id " +
                              "WHERE r.type='calls' AND f.name='ctQ_ActiveThreads.Add' AND t.name='ctQ_ActiveThreads.Helper' AND p.name='App'") == 1,
                  "I1", "an incremental run that re-parses only App2 keeps the edges inside App's MODULE-reached .clw");
            Check(inc1.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctQ_ActiveThreads.Add' AND decl_kind='implementation'") == 1,
                  "I1b", "and App2 (changed) does not claim a second copy of the library App still owns");
            inc1.Db.Close(); opened.Remove(inc1.Db);
            System.Threading.Thread.Sleep(1100);
            File.AppendAllText(Path.Combine(lib, "ctQ_ActiveThreads.clw"),
                "ctQ_ActiveThreads.Added PROCEDURE()\r\n  CODE\r\n", new UTF8Encoding(false));
            var inc2 = Index(slnPath, Path.Combine(root, "on.db"), clarionRoot, true, "Release", incremental: true);
            opened.Add(inc2.Db);
            Check(inc2.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctQ_ActiveThreads.Added'") == 1,
                  "I2", "editing a MODULE-reached .clw makes its project re-index (the new method appears)");
            Check(inc2.Scalar("SELECT COUNT(*) FROM symbols WHERE name='ctQ_ActiveThreads.Add' AND decl_kind='implementation'") == 1,
                  "I3", "and the library is still indexed once");
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
            ? "CodeGraphIndexer.ModuleLink: " + _assertions + " assertions, all passed"
            : "CodeGraphIndexer.ModuleLink: " + Failures.Count + " of " + _assertions + " FAILED");
        return Failures.Count == 0 ? 0 : 1;
    }
}
