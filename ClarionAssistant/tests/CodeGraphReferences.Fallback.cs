// Harness for CodeGraphProvider.GetReferences - the CodeGraph FALLBACK behind lsp_references
// (ticket 77aceec5 item 5). Built and run by CodeGraphReferences.FallbackTest.ps1, which supplies the
// vendored x86 System.Data.SQLite.
//
// The database is SYNTHETIC and shaped like a real index of a Clarion app, because the three defects
// are properties of that shape:
//   * a procedure has TWO rows - the MAP prototype (inserted first, so an unordered LIMIT 1 finds it)
//     and the implementation - and the call edges sit on the IMPLEMENTATION row only;
//   * file_path is stored LOWERCASED, while the files on disk are MixedCase\Source\...;
//   * nothing records a column, so the old fallback emitted a zero-width range at column 0.
// A same-named row of a different TYPE (a variable) is the negative control for the union: pulling in
// every same-named row would satisfy the caller check while reporting unrelated symbols.
//
// Character/Length are read by REFLECTION so this compiles against the pre-fix provider too, where
// those fields do not exist - that is a red result there, not a build break.
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

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-cgref-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string src = Path.Combine(root, "MixedCase", "Source");
        Directory.CreateDirectory(src);
        string mainClw = Path.Combine(src, "Main.clw");
        string implClw = Path.Combine(src, "Impl.clw");

        // 1-based lines: MAP entry on 4, call on 7; implementation on 3.
        File.WriteAllLines(mainClw, new[] {
            "  PROGRAM",
            "",
            "  MAP",
            "    SecondProc(LONG pX)",
            "  END",
            "  CODE",
            "  SecondProc(1)          ! call site",
            "  RETURN" });
        File.WriteAllLines(implClw, new[] {
            "  MEMBER('Main.clw')",
            "",
            "SecondProc PROCEDURE(LONG pX)",
            "  CODE",
            "  RETURN" });

        string db = Path.Combine(root, "t.codegraph.db");
        string lowerMain = mainClw.ToLowerInvariant();
        string lowerImpl = implClw.ToLowerInvariant();
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
                Exec(c, "INSERT INTO projects (name) VALUES ('Main')");
                // id 1: the MAP prototype - FIRST, so FindSymbolByName's LIMIT 1 lands on it.
                Exec(c, "INSERT INTO symbols (name,type,file_path,line_number,project_id,scope,decl_kind) VALUES "
                      + "('SecondProc','procedure','" + lowerMain + "',4,1,'global','prototype')");
                // id 2: the implementation - the row the call edge points at.
                Exec(c, "INSERT INTO symbols (name,type,file_path,line_number,project_id,scope,decl_kind) VALUES "
                      + "('SecondProc','procedure','" + lowerImpl + "',3,1,'module','implementation')");
                // id 3: the caller.
                Exec(c, "INSERT INTO symbols (name,type,file_path,line_number,project_id,scope,decl_kind) VALUES "
                      + "('Main','procedure','" + lowerMain + "',6,1,'global','implementation')");
                // id 4: NEGATIVE CONTROL - same name, different kind.
                Exec(c, "INSERT INTO symbols (name,type,file_path,line_number,project_id,scope) VALUES "
                      + "('SecondProc','variable','" + lowerImpl + "',1,1,'local')");
                Exec(c, "INSERT INTO relationships (from_id,to_id,type,file_path,line_number) VALUES "
                      + "(3,2,'calls','" + lowerMain + "',7)");
            }

            List<ReferenceLocation> refs;
            using (var p = new CodeGraphProvider())
            {
                Check(p.Open(db), "provider opens the synthetic db");
                refs = p.GetReferences("SecondProc");
            }
            SQLiteConnection.ClearAllPools();

            foreach (var r in refs)
                Console.WriteLine("       ref " + r.FilePath + ":" + r.LineNumber + (r.IsDefinition ? " (decl)" : ""));

            // Matched by TAIL: %TEMP% can be an 8.3 short path on one side and the provider returns the
            // on-disk long form, so a full-path compare would fail for a reason unrelated to the code.
            string runDir = Path.GetFileName(root);
            Func<string, int, ReferenceLocation> find = (file, line) =>
                refs.Find(r => r.FilePath != null && r.LineNumber == line
                    && r.FilePath.EndsWith(runDir + "\\MixedCase\\Source\\" + Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));

            var call = find(mainClw, 7);
            Check(call != null && !call.IsDefinition,
                "the CALL SITE is reported (edges live on the implementation row, not the prototype FindSymbolByName picks)");
            Check(find(mainClw, 4) != null, "the MAP prototype line is reported");
            Check(find(implClw, 3) != null, "the implementation line is reported");
            Check(find(implClw, 1) == null, "a same-named VARIABLE is not reported (union is limited to the same kind)");
            Check(refs.Count == 3, "exactly three locations (got " + refs.Count + ")");

            // Real width, via reflection (absent fields = the pre-fix provider = red).
            FieldInfo fChar = typeof(ReferenceLocation).GetField("Character");
            FieldInfo fLen = typeof(ReferenceLocation).GetField("Length");
            Check(fChar != null && fLen != null, "ReferenceLocation carries Character/Length");
            if (fChar != null && fLen != null && call != null)
            {
                int ch = (int)fChar.GetValue(call), len = (int)fLen.GetValue(call);
                Check(ch == 2 && len == "SecondProc".Length,
                    "call-site range covers the name: character 2, length 10 (got " + ch + ", " + len + ")");
            }
            var impl = find(implClw, 3);
            if (fChar != null && fLen != null && impl != null)
                Check((int)fChar.GetValue(impl) == 0 && (int)fLen.GetValue(impl) == 10,
                    "implementation range starts at column 0 with the name's width");

            // On-disk case, not the index's lowercased copy.
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
