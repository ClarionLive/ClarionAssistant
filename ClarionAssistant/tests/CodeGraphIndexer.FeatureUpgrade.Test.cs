// An incremental index of a database built by an OLDER indexer (feature level below CodeGraphIndexer.FeatureLevel,
// or no feature level recorded) runs a FULL index instead, and says so (tickets ef2fd2a9 / cea47717, GH #245/#246).
//
// The live failure (2026-10-19): demoleg.sln's database, built at feature level 1, re-indexed with index_codegraph's
// defaults (incremental) -> "indexed successfully ... Symbols: 0, Mode: incremental, Status: ok", and the database
// stayed level 1: no project had changed on disk, so the incremental run returned before any of the new passes ran.
// The release notes tell users to "re-index after updating", so the new data never arrived for most of them.
//
// Runs the REAL CodeGraphIndexer over a temp solution whose shared class is reached only through
// INCLUDE + CLASS,MODULE()/LINK() (Pass 2c). The "old" database is a full index with the Pass 2c/2d switches OFF and
// the feature-level metadata removed - exactly what the pre-feature-level indexer left on disk.
//
// The new IndexResult members (Incremental, UpgradedFromFeatureLevel, ModeText, UpToDate) are read by reflection so
// this file also compiles against the code from before the fix, where it must fail (the fail-proof).
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClarionCodeGraph.Graph;

static class CodeGraphIndexerFeatureUpgradeTest
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

    /// <summary>One app; its PROGRAM INCLUDEs a class whose implementing .clw no .cwproj lists. Returns the .sln path.</summary>
    static string BuildFixture(string root)
    {
        string app = Path.Combine(root, "App");
        Write(Path.Combine(app, "Shared.inc"),
            "SharedThing         CLASS,TYPE,MODULE('Shared.clw'),LINK('Shared.clw')",
            "  Run                 PROCEDURE()",
            "                    END");
        Write(Path.Combine(app, "Shared.clw"),
            "  MEMBER()",
            "  INCLUDE('Shared.inc'),ONCE",
            "SharedThing.Run PROCEDURE()",
            "  CODE");
        Write(Path.Combine(app, "App.clw"),
            "  PROGRAM",
            "  INCLUDE('Shared.inc'),ONCE",
            "  MAP",
            "DoWork  PROCEDURE()",
            "  END",
            "Thing   SharedThing",
            "  CODE",
            "  DoWork()",
            "DoWork  PROCEDURE()",
            "  CODE",
            "  Thing.Run()");
        File.WriteAllText(Path.Combine(app, "App.cwproj"),
            "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
            "  <PropertyGroup>\r\n    <ProjectGuid>{" + G1 + "}</ProjectGuid>\r\n    <OutputType>WinExe</OutputType>\r\n" +
            "    <AssemblyName>App</AssemblyName>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n" +
            "    <Compile Include=\"App.clw\" />\r\n  </ItemGroup>\r\n</Project>\r\n", new UTF8Encoding(false));
        string sln = Path.Combine(root, "Fix.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 11.00\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"App\", \"App\\App.cwproj\", \"{" + G1 + "}\"\r\nEndProject\r\n",
            new UTF8Encoding(false));
        return sln;
    }

    static int Scalar(CodeGraphDatabase db, string sql)
    {
        var dt = db.ExecuteQuery(sql);
        return dt.Rows.Count == 0 || dt.Rows[0][0] == DBNull.Value ? 0 : Convert.ToInt32(dt.Rows[0][0]);
    }

    static string Text(CodeGraphDatabase db, string sql)
    {
        var dt = db.ExecuteQuery(sql);
        return dt.Rows.Count == 0 || dt.Rows[0][0] == DBNull.Value ? null : dt.Rows[0][0].ToString();
    }

    /// <summary>A member added by the fix, or null when the build under test does not have it.</summary>
    static object Member(object o, string name)
    {
        var p = o.GetType().GetProperty(name);
        return p == null ? null : p.GetValue(o, null);
    }

    static int ModuleRows(CodeGraphDatabase db)
    {
        return Scalar(db, "SELECT COUNT(*) FROM symbols WHERE name='SharedThing.Run' AND decl_kind='implementation'");
    }

    static IndexResult Index(string sln, string dbPath, bool incremental, bool features, Action<CodeGraphDatabase> after, List<string> log = null)
    {
        var db = new CodeGraphDatabase();
        db.Open(dbPath);
        try
        {
            var indexer = new CodeGraphIndexer(db) { FollowClassModules = features, IndexGlobalDataIncludes = features };
            if (log != null) indexer.OnProgress += m => log.Add(m);
            var r = indexer.IndexSolution(sln, incremental, null);
            if (after != null) after(db);
            return r;
        }
        finally { db.Close(); }
    }

    /// <summary>What the indexer from before feature levels left: Pass 2c/2d never ran, no feature-level rows.</summary>
    static void MakeOldDatabase(string sln, string dbPath, string levelRow)
    {
        Index(sln, dbPath, false, false, db =>
        {
            db.ExecuteQuery("DELETE FROM index_metadata WHERE key IN ('indexer_version','indexer_feature_level','indexer_features')");
            if (levelRow != null) db.SetMetadata(CodeGraphIndexer.MetaFeatureLevel, levelRow);
        });
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-featup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            string sln = BuildFixture(root);
            string level = CodeGraphIndexer.FeatureLevel.ToString();

            // ---- control: the fixture only shows the new data when Pass 2c runs ----
            string ctl = Path.Combine(root, "control.db");
            int offRows = -1, onRows = -1;
            Index(sln, ctl, false, false, db => offRows = ModuleRows(db));
            File.Delete(ctl);
            Index(sln, ctl, false, true, db => onRows = ModuleRows(db));
            Check(offRows == 0 && onRows == 1, "C1",
                "control: SharedThing.Run is indexed only with Pass 2c on (off=" + offRows + ", on=" + onRows + ")");

            // ---- U: an old-level database (no feature-level row) + incremental -> full, upgraded, reported ----
            string oldDb = Path.Combine(root, "old.db");
            MakeOldDatabase(sln, oldDb, null);
            var log = new List<string>();
            int rows = -1; string stored = null;
            var r = Index(sln, oldDb, true, true, db => { rows = ModuleRows(db); stored = Text(db, "SELECT value FROM index_metadata WHERE key='indexer_feature_level'"); }, log);
            Check(rows == 1, "U1", "the incremental request on a level-1 database indexed the Pass 2c code (rows=" + rows + ")");
            Check(stored == level, "U2", "the database now records feature level " + level + " (was: " + (stored ?? "null") + ")");
            Check(Equals(Member(r, "Incremental"), false), "U3", "IndexResult.Incremental is false: the run was FULL (got " + (Member(r, "Incremental") ?? "no member") + ")");
            Check(Equals(Member(r, "RequestedIncremental"), true), "U4", "IndexResult.RequestedIncremental keeps what was asked (got " + (Member(r, "RequestedIncremental") ?? "no member") + ")");
            Check(Equals(Member(r, "UpgradedFromFeatureLevel"), 1), "U5", "IndexResult.UpgradedFromFeatureLevel = 1 (got " + (Member(r, "UpgradedFromFeatureLevel") ?? "no member") + ")");
            string mode = Member(r, "ModeText") as string;
            Check(mode != null && mode.StartsWith("full (upgraded from feature level 1 to " + level + ";") && mode.Contains("incremental would have missed"),
                "U6", "ModeText says full + upgraded + why (got: " + (mode ?? "no member") + ")");
            Check(log.Exists(m => m.Contains("feature level 1") && m.Contains("full")), "U7", "the run log says why it ran full");
            Check(r.FeatureLevel == CodeGraphIndexer.FeatureLevel, "U8", "IndexResult.FeatureLevel = " + level + " (got " + r.FeatureLevel + ")");

            // ---- N: the same database, now current, + incremental -> stays incremental, honest up-to-date wording ----
            rows = -1;
            r = Index(sln, oldDb, true, true, db => rows = ModuleRows(db));
            Check(Equals(Member(r, "Incremental"), true), "N1", "a current-level database + incremental stays incremental (got " + (Member(r, "Incremental") ?? "no member") + ")");
            Check(Equals(Member(r, "UpgradedFromFeatureLevel"), -1), "N2", "UpgradedFromFeatureLevel = -1 (no upgrade) (got " + (Member(r, "UpgradedFromFeatureLevel") ?? "no member") + ")");
            Check(rows == 1, "N3", "the no-change incremental left the Pass 2c rows in place (rows=" + rows + ")");
            Check(Equals(Member(r, "UpToDate"), true), "N4", "a no-change incremental reports UpToDate (got " + (Member(r, "UpToDate") ?? "no member") + ")");
            mode = Member(r, "ModeText") as string;
            Check(mode != null && mode.StartsWith("incremental") && mode.Contains("up to date"), "N5",
                "ModeText of a no-change incremental says up to date, not a bare 0-symbol success (got: " + (mode ?? "no member") + ")");
            Check(r.FeatureLevel == CodeGraphIndexer.FeatureLevel, "N6", "a no-change incremental still reports the database's feature level (got " + r.FeatureLevel + ")");
            Check(r.SymbolCount == 0, "N7", "a no-change incremental parsed nothing (symbols this run = " + r.SymbolCount + ")");

            // ---- N8: a change on a current db -> incremental, not up to date ----
            System.Threading.Thread.Sleep(1100);
            File.AppendAllText(Path.Combine(root, "App", "App.clw"), "! touched\r\n");
            File.SetLastWriteTime(Path.Combine(root, "App", "App.clw"), DateTime.Now.AddMinutes(1));
            r = Index(sln, oldDb, true, true, null);
            Check(Equals(Member(r, "Incremental"), true) && Equals(Member(r, "UpToDate"), false) && r.SymbolCount > 0, "N8",
                "a changed project on a current database: incremental, re-parsed, not 'up to date' (symbols=" + r.SymbolCount + ")");
            // Back in the past, so the databases below see an UNCHANGED project: an incremental that re-parses a
            // changed project runs Pass 2c anyway and would hide the bug.
            File.SetLastWriteTime(Path.Combine(root, "App", "App.clw"), DateTime.Now.AddHours(-1));

            // ---- L: an unreadable feature-level row is not current -> full ----
            string badDb = Path.Combine(root, "bad.db");
            MakeOldDatabase(sln, badDb, "two");
            rows = -1;
            r = Index(sln, badDb, true, true, db => rows = ModuleRows(db));
            Check(rows == 1 && Equals(Member(r, "Incremental"), false), "L1", "an unreadable feature-level row runs full (rows=" + rows + ")");

            // ---- E: a brand-new database (no metadata at all) + incremental -> full, worded as new ----
            string newDb = Path.Combine(root, "new.db");
            rows = -1;
            r = Index(sln, newDb, true, true, db => rows = ModuleRows(db));
            mode = Member(r, "ModeText") as string;
            Check(rows == 1 && Equals(Member(r, "Incremental"), false) && Equals(Member(r, "UpgradedFromFeatureLevel"), 0)
                  && mode != null && mode.StartsWith("full (new database"), "E1",
                "a new database + incremental runs full and says so (rows=" + rows + ", mode=" + (mode ?? "no member") + ")");

            // ---- F: a FULL request on an old database is just full (no upgrade note needed) ----
            string fullDb = Path.Combine(root, "full.db");
            MakeOldDatabase(sln, fullDb, "1");
            r = Index(sln, fullDb, false, true, null);
            Check(Equals(Member(r, "ModeText"), "full") && Equals(Member(r, "UpgradedFromFeatureLevel"), -1), "F1",
                "a requested full index reads plain 'full' (got: " + (Member(r, "ModeText") ?? "no member") + ")");
        }
        catch (Exception ex)
        {
            Check(false, "X", "unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "PASS  CodeGraphIndexer.FeatureUpgrade.Test (" + _assertions + " assertions)"
            : "FAIL  CodeGraphIndexer.FeatureUpgrade.Test: " + Failures.Count + " of " + _assertions + " failed");
        return Failures.Count == 0 ? 0 : 1;
    }
}
