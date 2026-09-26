// Harness for CodeGraphProvider.GetReferences - the CodeGraph FALLBACK behind lsp_references
// (ticket 77aceec5 item 5, and pipeline run 1 B1). Built and run by CodeGraphReferences.FallbackTest.ps1,
// which supplies the vendored x86 System.Data.SQLite.
//
// The database is SYNTHETIC and shaped like a real index of a multi-app Clarion solution, because the
// defects are properties of that shape:
//   * a procedure has TWO rows - the MAP prototype (inserted first, so an unordered LIMIT 1 finds it)
//     and the implementation - and the call edges sit on the IMPLEMENTATION row only;
//   * the SAME procedure name exists in TWO projects (template copies per app), each with its own
//     callers - a request from app A must see A's, never B's (B1);
//   * the SAME local name exists in TWO procedures - a request inside ProcOne must see ProcOne's local
//     only (B1; a real db had 3,278 rows for one such name);
//   * file_path is stored LOWERCASED, while the files on disk are MixedCase\Source\...;
//   * nothing records a column, so the old fallback emitted a zero-width range at column 0.
// Pipeline run 2 (precision over recall - unprovable means EMPTY): a local used inside its procedure's
// local-class method (R1); a usage-only file, a file in two projects, and rows with no project_id (R2);
// the same MODULE variable in two files of one project (R3).
//
// The position-aware overload and Character/Length are reached by REFLECTION so this compiles against
// older providers too; a missing member is a red result there, not a build break.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using ClarionCodeGraph.Graph;

static class CodeGraphReferencesFallback
{
    static int _failures, _assertions;

    static void Check(bool ok, string what)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what); }
        else Console.WriteLine("  ok   " + what);
    }

    static void Exec(SQLiteConnection c, string sql)
    {
        using (var cmd = new SQLiteCommand(sql, c)) cmd.ExecuteNonQuery();
    }

    static long Sym(SQLiteConnection c, string name, string type, string file, int line, int project, string scope, string declKind, string parent)
    {
        Exec(c, "INSERT INTO symbols (name,type,file_path,line_number,project_id,scope,decl_kind,parent_name) VALUES ('"
            + name + "','" + type + "','" + file.ToLowerInvariant() + "'," + line + "," + (project == 0 ? "NULL" : project.ToString()) + ",'" + scope + "',"
            + (declKind == null ? "NULL" : "'" + declKind + "'") + "," + (parent == null ? "NULL" : "'" + parent + "'") + ")");
        using (var cmd = new SQLiteCommand("SELECT last_insert_rowid()", c)) return (long)cmd.ExecuteScalar();
    }

    static List<ReferenceLocation> Refs(CodeGraphProvider p, string name, string file, int line1)
    {
        MethodInfo m = typeof(CodeGraphProvider).GetMethod("GetReferences", new[] { typeof(string), typeof(string), typeof(int) });
        if (m == null) return p.GetReferences(name);   // pre-B1 provider: no position awareness
        return (List<ReferenceLocation>)m.Invoke(p, new object[] { name, file, line1 });
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-cgref-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string srcA = Path.Combine(root, "MixedCase", "Source");
        string srcB = Path.Combine(root, "OtherApp");
        Directory.CreateDirectory(srcA);
        Directory.CreateDirectory(srcB);
        string mainA = Path.Combine(srcA, "Main.clw");
        string implA = Path.Combine(srcA, "Impl.clw");
        string mainB = Path.Combine(srcB, "Main.clw");
        string implB = Path.Combine(srcB, "Impl.clw");
        string usageA = Path.Combine(srcA, "Usage.clw");
        string impl2A = Path.Combine(srcA, "Impl2.clw");
        string sharedInc = Path.Combine(root, "Shared.inc");
        string looseA = Path.Combine(root, "Loose.clw");
        string looseB = Path.Combine(root, "Loose2.clw");

        // App A. 1-based lines: MAP entry 4, Main 6, call 7; SecondProc impl 3; ProcOne 10 (local 12);
        // ProcTwo 20 (local 22).
        string[] mainText = {
            "  PROGRAM",
            "",
            "  MAP",
            "    SecondProc(LONG pX)",
            "  END",
            "  CODE",
            "  SecondProc(1)          ! call site",
            "  RETURN" };
        File.WriteAllLines(mainA, mainText);
        var impl = new string[25];
        for (int i = 0; i < impl.Length; i++) impl[i] = "";
        impl[0]  = "  MEMBER('Main.clw')";
        impl[2]  = "SecondProc PROCEDURE(LONG pX)";
        impl[3]  = "  CODE";
        impl[9]  = "ProcOne PROCEDURE";
        impl[11] = "LocalRequest         LONG";
        impl[12] = "  CODE";
        impl[13] = "  LocalRequest = 1";
        impl[19] = "ProcTwo PROCEDURE";
        impl[21] = "LocalRequest         LONG";
        impl[22] = "  CODE";
        impl[23] = "  LocalRequest = 2";
        File.WriteAllLines(implA, impl);
        // App B: the same shapes, its own copy.
        File.WriteAllLines(mainB, mainText);
        File.WriteAllLines(implB, new[] { "  MEMBER('Main.clw')", "", "SecondProc PROCEDURE(LONG pX)", "  CODE", "  RETURN" });

        string db = Path.Combine(root, "t.codegraph.db");
        try
        {
            using (var c = new SQLiteConnection("Data Source=" + db + ";Version=3;"))
            {
                c.Open();
                // A real index is WAL (the indexer sets it), and the provider opens read-only WITH
                // Journal Mode=WAL - which a read-only connection cannot switch a rollback-journal db to.
                Exec(c, "PRAGMA journal_mode=WAL");
                Exec(c, "CREATE TABLE projects (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)");
                Exec(c, "CREATE TABLE symbols (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, type TEXT NOT NULL, "
                      + "file_path TEXT NOT NULL, line_number INTEGER, project_id INTEGER, params TEXT, return_type TEXT, "
                      + "parent_name TEXT, member_of TEXT, scope TEXT, source_preview TEXT, decl_kind TEXT)");
                Exec(c, "CREATE TABLE relationships (id INTEGER PRIMARY KEY AUTOINCREMENT, from_id INTEGER, to_id INTEGER, "
                      + "type TEXT NOT NULL, file_path TEXT, line_number INTEGER, ambiguous INTEGER NOT NULL DEFAULT 0)");
                Exec(c, "INSERT INTO projects (name) VALUES ('AppA')");
                Exec(c, "INSERT INTO projects (name) VALUES ('AppB')");

                // App A - the MAP prototype FIRST, so FindSymbolByName's LIMIT 1 lands on it.
                Sym(c, "SecondProc", "procedure", mainA, 4, 1, "global", "prototype", null);
                long implAId = Sym(c, "SecondProc", "procedure", implA, 3, 1, "module", "implementation", null);
                long mainAId = Sym(c, "Main", "procedure", mainA, 6, 1, "global", "implementation", null);
                Sym(c, "ProcOne", "procedure", implA, 10, 1, "module", "implementation", null);
                Sym(c, "LocalRequest", "variable", implA, 12, 1, "local", null, "ProcOne");
                Sym(c, "ProcTwo", "procedure", implA, 20, 1, "module", "implementation", null);
                Sym(c, "LocalRequest", "variable", implA, 22, 1, "local", null, "ProcTwo");
                // Same name, other KIND, in A: must not ride along with the procedure.
                Sym(c, "SecondProc", "variable", implA, 1, 1, "local", null, "SomethingElse");
                Exec(c, "INSERT INTO relationships (from_id,to_id,type,file_path,line_number) VALUES ("
                      + mainAId + "," + implAId + ",'calls','" + mainA.ToLowerInvariant() + "',7)");

                // App B - its own copy of everything, its own caller.
                Sym(c, "SecondProc", "procedure", mainB, 4, 2, "global", "prototype", null);
                long implBId = Sym(c, "SecondProc", "procedure", implB, 3, 2, "module", "implementation", null);
                long mainBId = Sym(c, "Main", "procedure", mainB, 6, 2, "global", "implementation", null);
                Exec(c, "INSERT INTO relationships (from_id,to_id,type,file_path,line_number) VALUES ("
                      + mainBId + "," + implBId + ",'calls','" + mainB.ToLowerInvariant() + "',7)");

                // --- pipeline run 2 fixtures ---------------------------------------------------
                // R1: a procedure's local used inside its LOCAL CLASS METHOD, which the index stores as
                //     its own dotted procedure with no parent_name.
                Sym(c, "ProcThree", "procedure", implA, 30, 1, "module", "implementation", null);
                Sym(c, "Counter", "variable", implA, 31, 1, "local", null, "ProcThree");
                Sym(c, "ThreeClass.Bump", "function", implA, 35, 1, "module", "implementation", null);
                // R2a: a usage-only file of app A; the name is declared only in app B.
                Sym(c, "UsageProc", "procedure", usageA, 1, 1, "module", "implementation", null);
                Sym(c, "OnlyInB", "procedure", implB, 10, 2, "module", "implementation", null);
                // R2b: a file in TWO projects; one name declared in both apps, one in a single app.
                Sym(c, "SharedA", "variable", sharedInc, 1, 1, "global", null, null);
                Sym(c, "SharedB", "variable", sharedInc, 2, 2, "global", null, null);
                Sym(c, "Twice", "procedure", implA, 40, 1, "module", "implementation", null);
                Sym(c, "Twice", "procedure", implB, 40, 2, "module", "implementation", null);
                Sym(c, "OnceOnly", "procedure", implA, 45, 1, "module", "implementation", null);
                // R2c: rows with NO project_id at all.
                Sym(c, "LooseOnce", "procedure", looseA, 2, 0, "global", null, null);
                Sym(c, "LooseTwice", "procedure", looseA, 3, 0, "global", null, null);
                Sym(c, "LooseTwice", "procedure", looseB, 3, 0, "global", null, null);
                // R3: the same MODULE variable name in two files of one project.
                Sym(c, "ModVar", "variable", implA, 2, 1, "module", null, null);
                Sym(c, "ModVar", "variable", impl2A, 2, 1, "module", null, null);
            }

            string runDir = Path.GetFileName(root);
            // Matched by TAIL: %TEMP% can be an 8.3 short path on one side and the provider returns the
            // on-disk long form, so a full-path compare would fail for a reason unrelated to the code.
            Func<List<ReferenceLocation>, string, int, ReferenceLocation> find = (refs, rel, line) =>
                refs.Find(r => r.FilePath != null && r.LineNumber == line
                    && r.FilePath.EndsWith(runDir + "\\" + rel, StringComparison.OrdinalIgnoreCase));
            Action<string, List<ReferenceLocation>> dump = (label, refs) =>
            {
                Console.WriteLine("     " + label + ":");
                foreach (var r in refs) Console.WriteLine("       " + r.FilePath + ":" + r.LineNumber + (r.IsDefinition ? " (decl)" : ""));
            };

            List<ReferenceLocation> fromA, fromB, localOne, localTwo, noFile,
                inMethod, usageOnly, sharedTwo, sharedOne, looseTwo, looseOne, modVar;
            using (var p = new CodeGraphProvider())
            {
                Check(p.Open(db), "provider opens the synthetic db");
                fromA    = Refs(p, "SecondProc", mainA, 7);      // at app A's call site
                fromB    = Refs(p, "SecondProc", mainB, 7);      // at app B's call site
                localOne = Refs(p, "LocalRequest", implA, 14);   // inside ProcOne
                localTwo = Refs(p, "LocalRequest", implA, 24);   // inside ProcTwo
                noFile   = Refs(p, "SecondProc", null, 0);          // position unknown
                inMethod   = Refs(p, "Counter", implA, 37);         // R1: inside ThreeClass.Bump
                usageOnly  = Refs(p, "OnlyInB", usageA, 3);         // R2a
                sharedTwo  = Refs(p, "Twice", sharedInc, 5);        // R2b: ambiguous project, 2 candidates
                sharedOne  = Refs(p, "OnceOnly", sharedInc, 5);     // R2b: ambiguous project, 1 candidate
                looseTwo   = Refs(p, "LooseTwice", looseA, 10);     // R2c: no project, 2 candidates
                looseOne   = Refs(p, "LooseOnce", looseA, 10);      // R2c: no project, 1 candidate
                modVar     = Refs(p, "ModVar", implA, 5);           // R3
            }
            SQLiteConnection.ClearAllPools();
            dump("SecondProc from app A", fromA);
            dump("SecondProc from app B", fromB);
            dump("LocalRequest in ProcOne", localOne);
            dump("LocalRequest in ProcTwo", localTwo);

            string A = "MixedCase\\Source\\", B = "OtherApp\\";

            // --- the procedure, from app A: A's prototype + implementation + call, nothing of B's
            var call = find(fromA, A + "Main.clw", 7);
            Check(call != null && !call.IsDefinition,
                "A: the CALL SITE is reported (edges live on the implementation row, not the prototype)");
            Check(find(fromA, A + "Main.clw", 4) != null, "A: the MAP prototype line is reported");
            Check(find(fromA, A + "Impl.clw", 3) != null, "A: the implementation line is reported");
            Check(find(fromA, A + "Impl.clw", 1) == null, "A: a same-named VARIABLE is not reported");
            Check(!fromA.Exists(r => r.FilePath.IndexOf("\\OtherApp\\", StringComparison.OrdinalIgnoreCase) >= 0),
                "A: nothing from app B (same procedure name, other project) - B1");
            Check(fromA.Count == 3, "A: exactly three locations (got " + fromA.Count + ")");

            // --- the same name, from app B: B's own three
            Check(find(fromB, B + "Main.clw", 7) != null && find(fromB, B + "Impl.clw", 3) != null
                && fromB.Count == 3 && !fromB.Exists(r => r.FilePath.IndexOf("MixedCase", StringComparison.OrdinalIgnoreCase) >= 0),
                "B: exactly its own prototype, implementation and call (got " + fromB.Count + ")");

            // --- locals: the requester's procedure only
            Check(localOne.Count == 1 && find(localOne, A + "Impl.clw", 12) != null,
                "local in ProcOne resolves to ProcOne's declaration only (got " + localOne.Count + ") - B1");
            Check(localTwo.Count == 1 && find(localTwo, A + "Impl.clw", 22) != null,
                "local in ProcTwo resolves to ProcTwo's declaration only (got " + localTwo.Count + ") - B1");

            // --- no request position: the project cannot be proven, so ONE row - never the union
            // PRECISION OVER RECALL (pipeline run 2): unprovable = empty, never an arbitrary row.
            Check(noFile.Count == 0, "no request file: EMPTY, not an arbitrary row (got " + noFile.Count + ")");

            // --- R1: a procedure's local, used inside that procedure's local-class method
            Check(inMethod.Count == 1 && find(inMethod, A + "Impl.clw", 31) != null,
                "R1: local used in a class method resolves to its procedure's declaration (got " + inMethod.Count + ")");

            // --- R2: never another app's declaration
            Check(usageOnly.Count == 0,
                "R2a: name declared only in ANOTHER project -> empty (got " + usageOnly.Count + ")");
            Check(sharedTwo.Count == 0,
                "R2b: file in two projects, name in both -> empty (got " + sharedTwo.Count + ")");
            Check(sharedOne.Count == 1 && find(sharedOne, A + "Impl.clw", 45) != null,
                "R2b: file in two projects, exactly one candidate -> that one (got " + sharedOne.Count + ")");
            Check(looseTwo.Count == 0,
                "R2c: no project_id, two candidates -> empty (got " + looseTwo.Count + ")");
            Check(looseOne.Count == 1,
                "R2c: no project_id, exactly one candidate -> that one (got " + looseOne.Count + ")");

            // --- R3: module data is visible only in its own file
            Check(modVar.Count == 1 && find(modVar, A + "Impl.clw", 2) != null,
                "R3: module variable resolves to the requesting file's own only (got " + modVar.Count + ")");

            // --- real width, via reflection (absent fields = the pre-fix provider = red)
            FieldInfo fChar = typeof(ReferenceLocation).GetField("Character");
            FieldInfo fLen = typeof(ReferenceLocation).GetField("Length");
            Check(fChar != null && fLen != null, "ReferenceLocation carries Character/Length");
            if (fChar != null && fLen != null && call != null)
            {
                int ch = (int)fChar.GetValue(call), len = (int)fLen.GetValue(call);
                Check(ch == 2 && len == "SecondProc".Length,
                    "call-site range covers the name: character 2, length 10 (got " + ch + ", " + len + ")");
            }

            // --- on-disk case, not the index's lowercased copy
            Check(call != null && call.FilePath.Contains(Path.Combine("MixedCase", "Source", "Main.clw")),
                "paths come back in their on-disk case (got " + (call != null ? call.FilePath : "null") + ")");
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(_failures == 0
            ? "PASS - " + _assertions + " assertions"
            : "FAIL - " + _failures + " of " + _assertions + " assertions");
        return _failures == 0 ? 0 : 1;
    }
}
