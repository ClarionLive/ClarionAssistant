// 1c685f2e item 2: SymbolIndex - held-open, NOCASE-indexed symbol lookups (phase1-tests.md 2.1-2.20).
//
// Compiles the REAL Services\SymbolIndex.cs with CodeGraph\Graph\CodeGraphProvider.cs (the row mapper)
// and CodeGraphDatabase.cs (the indexer's write-open path, which creates the NOCASE indexes), against
// the vendored x86 System.Data.SQLite. Every database is synthetic, built in a temp dir, WAL mode.
//
// Args: <repo ClarionAssistant dir> [<real .codegraph.db COPY for the measurement pass>]
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ClarionAssistant.Services;
using ClarionCodeGraph.Graph;

static class SymbolIndexTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();
    static readonly List<string> Log = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static string _work;

    static int Main(string[] args)
    {
        string repo = args[0];
        string realCopy = args.Length > 1 ? args[1] : null;
        _work = Path.Combine(Path.GetTempPath(), "ca-symidx-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_work);
        SymbolIndex.LogSink = line => { lock (Log) Log.Add(line); };
        SymbolIndex.AutoIndex = false;   // the old-schema fallback tests need a DB that stays old; H2 turns it on
        try
        {
            string proj = Path.Combine(_work, "proj.codegraph.db");
            string lib = Path.Combine(_work, "ClarionGraph_test.db");
            string old = Path.Combine(_work, "old.codegraph.db");
            BuildIndexed(proj, ProjectRows());
            BuildIndexed(lib, LibraryRows());
            BuildOldSchema(old, ProjectRows());

            Queries(proj, lib);
            Fallback(old);
            IndexerCreatesIndexes();
            AutoIndexBuild();
            Lifecycle(proj);
            EquateIncludes();
            Sources(repo);
            if (Environment.GetEnvironmentVariable("SYMIDX_SKIP_LARGE") != "1") Large();
            if (realCopy != null) Real(realCopy);
        }
        finally
        {
            SymbolIndex.ReleaseAll();
            try { Directory.Delete(_work, true); } catch { }
        }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }

    // ------------------------------------------------------------------------------ fixtures

    // name, type, scope, parent_name, params
    static string[][] ProjectRows()
    {
        return new[]
        {
            new[] { "GloVar", "variable", "global", null, "LONG" },
            new[] { "glovar2", "variable", "global", null, "LONG" },
            new[] { "GLO_x", "variable", "global", null, "LONG" },
            new[] { "GloModule", "variable", "module", null, "LONG" },
            new[] { "GloLocal", "variable", "local", "SomeProc", "LONG" },
            new[] { "GloParam", "variable", "parameter", "SomeProc", "LONG" },
            new[] { "GloClass.Method", "procedure", "global", "GloClass", "()" },
            new[] { "Glp", "variable", "global", null, "LONG" },
            new[] { "Gl", "variable", "global", null, "LONG" },
            new[] { "MyBrowse", "class", "global", "BrowseClass", null },
            new[] { "MyBrowse.Custom", "procedure", "global", "MyBrowse", "()" },
            new[] { "MyBrowse.ResetSort", "procedure", "global", "MyBrowse", "(BYTE Force)" },
            new[] { "CycA", "class", "global", "CycB", null },
            new[] { "CycB", "class", "global", "CycA", null },
            new[] { "CycA.One", "procedure", "global", "CycA", "()" },
        };
    }

    static string[][] LibraryRows()
    {
        return new[]
        {
            new[] { "BrowseClass", "class", "global", "ViewManager", null },
            new[] { "BrowseClass.ResetSort", "procedure", "global", "BrowseClass", "(BYTE Force)" },
            new[] { "BrowseClass.TakeKey", "procedure", "global", "BrowseClass", "()" },
            new[] { "ViewManager", "class", "global", null, null },
            new[] { "ViewManager.Open", "procedure", "global", "ViewManager", "()" },
        };
    }

    static void Exec(SQLiteConnection cn, string sql)
    {
        using (var cmd = new SQLiteCommand(sql, cn)) cmd.ExecuteNonQuery();
    }

    static void Insert(SQLiteConnection cn, IEnumerable<string[]> rows)
    {
        using (var tx = cn.BeginTransaction())
        using (var cmd = new SQLiteCommand("INSERT INTO symbols (name, type, file_path, line_number, project_id, params, parent_name, scope) VALUES (@n, @t, 'x.clw', 1, 1, @p, @par, @s)", cn, tx))
        {
            var n = cmd.Parameters.Add("@n", System.Data.DbType.String);
            var t = cmd.Parameters.Add("@t", System.Data.DbType.String);
            var s = cmd.Parameters.Add("@s", System.Data.DbType.String);
            var par = cmd.Parameters.Add("@par", System.Data.DbType.String);
            var p = cmd.Parameters.Add("@p", System.Data.DbType.String);
            foreach (var r in rows)
            {
                n.Value = r[0]; t.Value = r[1]; s.Value = r[2]; par.Value = (object)r[3] ?? DBNull.Value; p.Value = (object)r[4] ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>A DB written through the indexer's own write-open path (CodeGraphDatabase.Open).</summary>
    static void BuildIndexed(string path, IEnumerable<string[]> rows)
    {
        using (var db = new CodeGraphDatabase()) { db.Open(path); }
        using (var cn = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
        {
            cn.Open();
            Exec(cn, "INSERT INTO projects (id, name) VALUES (1, 'proj')");
            Insert(cn, rows);
        }
    }

    /// <summary>The same rows in the pre-1c685f2e schema: no NOCASE indexes.</summary>
    static void BuildOldSchema(string path, IEnumerable<string[]> rows)
    {
        SQLiteConnection.CreateFile(path);
        using (var cn = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
        {
            cn.Open();
            Exec(cn, "PRAGMA journal_mode=WAL");
            Exec(cn, "CREATE TABLE projects (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, guid TEXT, cwproj_path TEXT, output_type TEXT, sln_path TEXT)");
            Exec(cn, "CREATE TABLE symbols (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, type TEXT NOT NULL, file_path TEXT NOT NULL, line_number INTEGER, project_id INTEGER, params TEXT, return_type TEXT, parent_name TEXT, member_of TEXT, scope TEXT, source_preview TEXT, decl_kind TEXT)");
            Exec(cn, "CREATE INDEX idx_sym_name ON symbols(name)");
            Exec(cn, "INSERT INTO projects (id, name) VALUES (1, 'proj')");
            Insert(cn, rows);
        }
    }

    // ------------------------------------------------------------------------------ file-level equates

    static void Sym(SQLiteConnection cn, string name, string type, string file, string scope, string prms, int project)
    {
        using (var cmd = new SQLiteCommand("INSERT INTO symbols (name, type, file_path, line_number, project_id, params, scope) VALUES (@n, @t, @f, 1, @pr, @p, @s)", cn))
        {
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@f", file);
            cmd.Parameters.AddWithValue("@pr", project);
            cmd.Parameters.AddWithValue("@p", (object)prms ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", scope);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A file-level EQUATE in a .inc is offered only to a file that includes that .inc - directly,
    /// through another include, or (for a MEMBER file) through its PROGRAM. Fixture, two projects:
    ///   Prog.clw (PROGRAM)  includes Common.inc            Mem.clw (MEMBER)  includes A.inc;  A.inc includes B.inc
    ///   Prog2.clw (PROGRAM) includes Other.inc             C.inc is included by nobody
    /// plus 600 equates in C.inc that sort BETWEEN the visible ones, to force the filtered query to page.</summary>
    static void EquateIncludes()
    {
        Console.WriteLine("file-level equates and the include closure");
        string db = Path.Combine(_work, "equ.codegraph.db");
        string dir = Path.Combine(_work, "src") + Path.DirectorySeparatorChar;
        BuildIndexed(db, new string[0][]);
        using (var cn = new SQLiteConnection("Data Source=" + db + ";Version=3;"))
        {
            cn.Open();
            Exec(cn, "INSERT INTO projects (id, name) VALUES (2, 'proj2')");
            Sym(cn, "ProgMain", "program", dir + "Prog.clw", "global", null, 1);
            Sym(cn, "Common.inc", "include", dir + "Prog.clw", "global", null, 1);
            Sym(cn, "A.inc", "include", dir + "Mem.clw", "global", null, 1);
            Sym(cn, "bad<name.inc", "include", dir + "Mem.clw", "global", null, 1);     // malformed: skipped, never aborts the load
            Sym(cn, "B.inc", "include", dir + "A.inc", "global", null, 1);
            Sym(cn, "ProgMain2", "program", dir + "Prog2.clw", "global", null, 2);
            Sym(cn, "Other.inc", "include", dir + "Prog2.clw", "global", null, 2);
            Sym(cn, "EQ_COMMON", "variable", dir + "Common.inc", "global", "EQUATE", 1);
            Sym(cn, "EQ_A", "variable", dir + "A.inc", "global", "EQUATE", 1);
            Sym(cn, "EQ_B", "variable", dir + "B.inc", "global", "EQUATE", 1);
            Sym(cn, "EQ_C", "variable", dir + "C.inc", "global", "EQUATE", 1);
            Sym(cn, "EQ_OTHER", "variable", dir + "Other.inc", "global", "EQUATE", 2);
            Sym(cn, "EQ_INCLW", "variable", dir + "Mem.clw", "global", "EQUATE", 1);     // not a .inc: never filtered
            Sym(cn, "EQ_PROC", "procedure", dir + "C.inc", "global", null, 1);           // not an equate: never filtered
            Sym(cn, "EQ_CLASSEQ", "variable", dir + "C.inc", "class", "EQUATE", 1);      // not file-level: never filtered
            using (var tx = cn.BeginTransaction())
            {
                for (int i = 0; i < 600; i++) Sym(cn, "EQ_BULK_" + i.ToString("000"), "variable", dir + "C.inc", "global", "EQUATE", 1);
                tx.Commit();
            }
        }

        var mem = SymbolIndex.IncludeClosure(dir + "Mem.clw", new[] { db });
        Check(mem != null && mem.SetEquals(new[] { "Mem.clw", "A.inc", "B.inc", "Prog.clw", "Common.inc" }),
              "E.1", "MEMBER file: itself + its includes (transitive) + its project's PROGRAM and the PROGRAM's includes, not another project's - got " + (mem == null ? "null" : "{" + string.Join(", ", mem) + "}"));
        var other = SymbolIndex.IncludeClosure(dir + "Prog2.clw", new[] { db });
        Check(other != null && other.SetEquals(new[] { "Prog2.clw", "Other.inc" }), "E.2", "a file of the other project sees only that project's PROGRAM chain");

        var idx = SymbolIndex.For(db);
        var all = Names(idx.ByPrefix("EQ_", 1000));
        Check(all.Contains("EQ_C") && all.Contains("EQ_BULK_000"), "E.3", "no filter: every equate is offered (unchanged behaviour)");

        var seen = Names(idx.ByPrefix("EQ_", 100, equateFiles: mem));
        Check(seen.SetEquals(new[] { "EQ_A", "EQ_B", "EQ_COMMON", "EQ_INCLW", "EQ_PROC", "EQ_CLASSEQ" }),
              "E.4", "filtered: equates from included .inc files + non-.inc/non-equate rows survive; EQ_C, EQ_OTHER and the 600 bulk rows are dropped - got " + Show(idx.ByPrefix("EQ_", 100, equateFiles: mem)));
        Check(Names(idx.ByPrefix("EQ_", 3, equateFiles: mem)).Count == 3, "E.5", "the limit still applies after filtering");

        var upper = SymbolIndex.IncludeClosure((dir + "Mem.clw").ToUpperInvariant(), new[] { db });
        Check(ReferenceEquals(upper, mem) && !upper.Contains("Other.inc"),
              "E.6", "paths are compared case-insensitively (Windows): an upper-case spelling of the same file is the SAME closure, still project-specific");

        var unknown = SymbolIndex.IncludeClosure(dir + "Nowhere.clw", new[] { db });
        Check(unknown != null && unknown.Contains("Common.inc") && unknown.Contains("Other.inc") && !unknown.Contains("A.inc"),
              "E.7", "a file with no indexed symbols falls back to every PROGRAM's chain rather than hiding everything");

        Check(SymbolIndex.IncludeClosure(null, new[] { db }) == null && SymbolIndex.IncludeClosure("", new[] { db }) == null, "E.8", "no context file: null = do not filter");
        Check(SymbolIndex.IncludeClosure(dir + "Mem.clw", new[] { Path.Combine(_work, "missing.codegraph.db") }) == null, "E.9", "unreadable project DB: null = do not filter (never an empty set)");
        Check(ReferenceEquals(mem, SymbolIndex.IncludeClosure(dir + "Mem.clw", new[] { db })), "E.10", "the closure is cached per file");

        // No project DB path: the library DB must NOT be promoted to project DB (it holds no PROGRAM and no
        // include edges from user files, so every equate the file does include would be hidden).
        Check(SymbolIndex.IncludeClosure(dir + "Mem.clw", new string[] { null, db }) == null &&
              SymbolIndex.IncludeClosure(dir + "Mem.clw", new string[] { "", db }) == null,
              "E.11", "no project DB path -> null (do not filter), even with a library DB present");

        // The keystroke lane (fastOnly) must not touch a DB it cannot query quickly.
        string oldDb = Path.Combine(_work, "equ-old.codegraph.db");
        BuildOldSchema(oldDb, ProjectRows());
        Check(SymbolIndex.IncludeClosure(dir + "Mem.clw", new[] { oldDb }, fastOnly: true) == null, "E.12", "fastOnly on a DB without the NOCASE indexes -> null");
        Check(SymbolIndex.IncludeClosure(dir + "Mem.clw", new[] { oldDb }) != null, "E.13", "the same DB is still usable off the keystroke lane");

        // ScopeEquateToIncludes: an unfiltered exact-name hit (a provider's FindByName-style first row), scoped to
        // what the context file can see. Own name prefixes so the E.3-E.5 counts above stay as they are.
        using (var cn = new SQLiteConnection("Data Source=" + db + ";Version=3;"))
        {
            cn.Open();
            Sym(cn, "DUP_X", "variable", dir + "C.inc", "global", "EQUATE", 1);       // not included, declared first
            Sym(cn, "DUP_X", "variable", dir + "A.inc", "global", "EQUATE", 1);       // included by Mem.clw
            Sym(cn, "ONLYC_Y", "variable", dir + "C.inc", "global", "EQUATE", 1);     // only in a not-included .inc
            Sym(cn, "ONLYEQU_Z", "variable", dir + "Std.equ", "global", "EQUATE", 1); // only in a not-included .equ
            Sym(cn, "SEEN_W", "variable", dir + "B.inc", "global", "EQUATE", 1);      // included (transitively)
        }
        SymbolIndex.Release(db);
        var idx2 = SymbolIndex.For(db);
        var memClosureDbs = new[] { db };
        Func<string, CodeGraphSymbol> scoped = n =>
            SymbolIndex.ScopeEquateToIncludes(idx2.FindByName(n), n, db, dir + "Mem.clw", memClosureDbs);

        var dup = scoped("DUP_X");
        Check(dup != null && string.Equals(Path.GetFileName(dup.FilePath), "A.inc", StringComparison.OrdinalIgnoreCase), "E.14",
              "two same-named equates, whichever the unfiltered lookup returns first -> the one from the INCLUDED file: " + (dup == null ? "null" : dup.FilePath));
        Check(scoped("ONLYC_Y") == null, "E.15", "an equate only in a .inc this file never includes -> null (no hover)");
        Check(scoped("ONLYEQU_Z") == null, "E.16", "an equate only in a .equ this file never includes -> null: .equ files are filtered like .inc");
        var seenW = scoped("SEEN_W");
        Check(seenW != null && seenW.Name == "SEEN_W", "E.17", "an equate in a transitively included .inc is kept");

        var proc = idx2.FindByName("EQ_PROC");
        Check(ReferenceEquals(SymbolIndex.ScopeEquateToIncludes(proc, "EQ_PROC", db, dir + "Mem.clw", memClosureDbs), proc), "E.18",
              "a non-equate row is returned unchanged");
        var onlyC = idx2.FindByName("ONLYC_Y");
        Check(ReferenceEquals(SymbolIndex.ScopeEquateToIncludes(onlyC, "ONLYC_Y", db, null, memClosureDbs), onlyC) &&
              ReferenceEquals(SymbolIndex.ScopeEquateToIncludes(onlyC, "ONLYC_Y", db, dir + "Mem.clw", new string[] { null, db }), onlyC),
              "E.19", "no context file, or no project DB to build a closure from -> unchanged (do not filter)");
        Check(SymbolIndex.ScopeEquateToIncludes(null, "X", db, dir + "Mem.clw", memClosureDbs) == null, "E.20", "no symbol -> null");

        Check(mem.Contains("A.inc") && !mem.Contains("bad<name.inc"), "E.14", "a malformed include row is skipped, the rest of the closure is intact");
    }

    static HashSet<string> Names(IEnumerable<CodeGraphSymbol> syms) { return new HashSet<string>(syms.Select(s => s.Name), StringComparer.Ordinal); }

    static string Show(IEnumerable<CodeGraphSymbol> syms) { return "[" + string.Join(", ", syms.Select(s => s.Name)) + "]"; }

    // ------------------------------------------------------------------------------ 2.1-2.7, 2.11-2.14

    static void Queries(string proj, string lib)
    {
        Console.WriteLine("queries (indexed)");
        var idx = SymbolIndex.For(proj);
        var glo = idx.ByPrefix("glo", 100);
        var GLO = idx.ByPrefix("GLO", 100);
        var expect = new[] { "GloVar", "glovar2", "GLO_x", "GloModule" };
        Check(Names(glo).SetEquals(expect) && Names(GLO).SetEquals(expect) && !idx.NoIndex, "2.1", "ByPrefix glo/GLO -> " + Show(glo));
        Check(!Names(glo).Contains("GloParam"), "2.2", "the parameter row is not offered");
        Check(!Names(glo).Contains("GloLocal"), "2.3", "the local row is not offered");
        Check(!Names(glo).Contains("GloClass.Method"), "2.4", "the dotted row is not offered");
        Check(idx.ByPrefix("Glo", 2).Count == 2, "2.5", "LIMIT 2 -> 2 rows");

        Check(Plan(proj, SymbolIndex.PrefixSql, cmd => { cmd.Parameters.AddWithValue("@lo", "Glo"); cmd.Parameters.AddWithValue("@hi", "Glo\uFFFF"); cmd.Parameters.AddWithValue("@limit", 10); cmd.Parameters.AddWithValue("@offset", 0); })
                  .Contains("USING INDEX " + SymbolIndex.NameIndex), "2.6", "the prefix query plan searches " + SymbolIndex.NameIndex + ": " + _lastPlan);
        Check(Plan(proj, SymbolIndex.MembersSql, cmd => { cmd.Parameters.AddWithValue("@parent", "MyBrowse"); cmd.Parameters.AddWithValue("@parentDot", "MyBrowse.%"); cmd.Parameters.AddWithValue("@limit", 10); })
                  .Contains("USING INDEX " + SymbolIndex.ParentIndex), "2.7", "the members query plan searches " + SymbolIndex.ParentIndex + ": " + _lastPlan);

        var inh = SymbolIndex.MembersOf("MyBrowse", true, proj, lib).Select(s => SymbolIndex.MemberName(s.Name)).ToList();
        Check(inh.SequenceEqual(new[] { "Custom", "ResetSort", "TakeKey", "Open" }), "2.11",
              "MembersOf(MyBrowse, inherited) across both DBs -> [" + string.Join(", ", inh) + "]");
        var resetOwner = SymbolIndex.MembersOf("MyBrowse", true, proj, lib).First(s => SymbolIndex.MemberName(s.Name) == "ResetSort").Name;
        Check(resetOwner == "MyBrowse.ResetSort", "2.11b", "ResetSort listed once, the derived row wins (" + resetOwner + ")");
        var direct = SymbolIndex.MembersOf("MyBrowse", false, proj, lib).Select(s => SymbolIndex.MemberName(s.Name)).ToList();
        Check(direct.SequenceEqual(new[] { "Custom", "ResetSort" }), "2.12", "MembersOf(MyBrowse, direct) -> [" + string.Join(", ", direct) + "]");

        List<CodeGraphSymbol> cyc = null;
        var th = new Thread(() => cyc = SymbolIndex.MembersOf("CycA", true, proj, lib));
        th.Start();
        bool done = th.Join(1000);
        Check(done && cyc != null && Names(cyc).SetEquals(new[] { "CycA.One" }), "2.13", "a parent cycle finishes within 1 s");
        if (!done) th.Abort();

        int before = SymbolIndex.OpenCountFor(proj);
        for (int i = 0; i < 200; i++) idx.ByPrefix(i % 2 == 0 ? "Glo" : "My", 50);
        Check(SymbolIndex.OpenCountFor(proj) == 1 && before == 1, "2.14", "200 queries, OpenCount " + SymbolIndex.OpenCountFor(proj));
    }

    static string _lastPlan;

    static string Plan(string db, string sql, Action<SQLiteCommand> bind)
    {
        var sb = new System.Text.StringBuilder();
        using (var cn = new SQLiteConnection("Data Source=" + db + ";Version=3;Read Only=True;"))
        {
            cn.Open();
            using (var cmd = new SQLiteCommand("EXPLAIN QUERY PLAN " + sql, cn))
            {
                bind(cmd);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) sb.Append(r["detail"]).Append(" | ");
            }
        }
        return _lastPlan = sb.ToString();
    }

    // ------------------------------------------------------------------------------ 2.9, 2.10

    static void Fallback(string old)
    {
        Console.WriteLine("old schema (no NOCASE indexes)");
        int logBefore = Log.Count(l => l.Contains("noIndex"));
        var idx = SymbolIndex.For(old);
        var glo = idx.ByPrefix("glo", 100);
        for (int i = 0; i < 100; i++) idx.ByPrefix("Glo", 100);
        int noIndexLines = Log.Count(l => l.Contains("noIndex db=old.codegraph.db")) - logBefore;
        Check(idx.NoIndex && Names(glo).SetEquals(new[] { "GloVar", "glovar2", "GLO_x", "GloModule" }) && idx.ByPrefix("Glo", 2).Count == 2,
              "2.10", "fallback answers like 2.1-2.5: " + Show(glo));
        Check(noIndexLines == 1, "2.10b", "exactly one noIndex line per open across 101 queries (" + noIndexLines + ")");
        Check(SymbolIndex.MembersOf("MyBrowse", false, old, null).Count == 2, "2.10c", "fallback members query");

        // 2.21 (Coder-B-Host): a local lane's FIRST lookup on an old-schema DB runs no fallback scan.
        string old3 = Path.Combine(_work, "old3.codegraph.db");
        BuildOldSchema(old3, ProjectRows());
        var sw = Stopwatch.StartNew();
        var i3 = SymbolIndex.For(old3);
        var fast = i3.ByPrefix("Glo", 100, fastOnly: true);
        long ms = sw.ElapsedMilliseconds;
        Check(fast.Count == 0 && SymbolIndex.QueryCountFor(old3) == 0 && ms < 100, "2.21",
              "first fastOnly ByPrefix on an old DB: " + fast.Count + " rows, " + SymbolIndex.QueryCountFor(old3) + " queries run, " + ms + " ms");
        var u3 = DateTime.UtcNow.AddSeconds(2);
        while (!i3.IsOpen && DateTime.UtcNow < u3) Thread.Sleep(5);
        Check(i3.IsOpen && i3.NoIndex && SymbolIndex.MembersOf("MyBrowse", false, old3, null, fastOnly: true).Count == 0 &&
              SymbolIndex.QueryCountFor(old3) == 0 && i3.ByPrefix("Glo", 100).Count == 4 && SymbolIndex.QueryCountFor(old3) == 1,
              "2.21b", "NoIndex is known before any query; fastOnly members also skip; the late path (fastOnly=false) still gets the fallback");
        SymbolIndex.Release(old3);

        // 2.22: For() opens and probes in the background - the first keystroke does not pay the open.
        string warmDb = Path.Combine(_work, "warm.codegraph.db");
        BuildIndexed(warmDb, ProjectRows());
        var wi = SymbolIndex.For(warmDb);
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!wi.IsOpen && DateTime.UtcNow < until) Thread.Sleep(5);
        Check(wi.IsOpen && !wi.NoIndex && SymbolIndex.QueryCountFor(warmDb) == 0 && SymbolIndex.OpenCountFor(warmDb) == 1, "2.22",
              "For() alone opened the connection and probed the indexes on a pool thread (no query run)");
        SymbolIndex.Release(warmDb);

        // F10: the test hooks never create an index (nor queue its background open).
        string never = Path.Combine(_work, "never-used.codegraph.db");
        BuildIndexed(never, ProjectRows());
        int o = SymbolIndex.OpenCountFor(never), q = SymbolIndex.QueryCountFor(never);
        Check(o == 0 && q == 0 && !SymbolIndex.IsRegistered(never), "2.23", "asking a never-used path's counters registers nothing");
        SymbolIndex.Release(old);
        Check(!IndexNames(old).Contains(SymbolIndex.NameIndex) && !IndexNames(old).Contains(SymbolIndex.ParentIndex), "2.9",
              "the read-only side created no index: [" + string.Join(", ", IndexNames(old)) + "]");
    }

    static List<string> IndexNames(string db)
    {
        var l = new List<string>();
        using (var cn = new SQLiteConnection("Data Source=" + db + ";Version=3;Read Only=True;"))
        {
            cn.Open();
            using (var cmd = new SQLiteCommand("PRAGMA index_list(symbols)", cn))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) l.Add(r["name"].ToString());
        }
        return l;
    }

    // ------------------------------------------------------------------------------ 2.8

    static void IndexerCreatesIndexes()
    {
        Console.WriteLine("indexer write-open");
        string old2 = Path.Combine(_work, "old2.codegraph.db");
        BuildOldSchema(old2, ProjectRows());
        using (var db = new CodeGraphDatabase()) { db.Open(old2); }   // the indexer's (and ClarionGraph's) write open
        var names = IndexNames(old2);
        Check(names.Contains(SymbolIndex.NameIndex) && names.Contains(SymbolIndex.ParentIndex), "2.8",
              "CodeGraphDatabase.Open adds both NOCASE indexes to an old DB: [" + string.Join(", ", names) + "]");
        var idx = SymbolIndex.For(old2);
        Check(idx.ByPrefix("glo", 10).Count == 4 && !idx.NoIndex, "2.8b", "and SymbolIndex then uses them");
        SymbolIndex.Release(old2);
    }

    // ------------------------------------------------------------------------------ H2: background index build

    static bool WaitFor(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    static int LogCount(string contains) { lock (Log) return Log.Count(l => l.Contains(contains)); }

    static void AutoIndexBuild()
    {
        Console.WriteLine("H2: background NOCASE index build on an old-schema DB");
        SymbolIndex.AutoIndex = true;
        try
        {
            // H2.1: For() alone -> the build runs in the background, NoIndex clears, fastOnly answers.
            string a = Path.Combine(_work, "h2-old.codegraph.db");
            BuildOldSchema(a, ProjectRows());
            int built = SymbolIndex.IndexBuildCount;
            var ia = SymbolIndex.For(a);
            bool done = WaitFor(() => SymbolIndex.IndexBuildCount > built, 10000);
            var rows = done ? ia.ByPrefix("glo", 100, fastOnly: true) : new List<CodeGraphSymbol>();
            Check(done && !ia.NoIndex && rows.Count == 4 && IndexNames(a).Contains(SymbolIndex.NameIndex) && LogCount("indexCreate db=h2-old.codegraph.db") == 1
                  && LogCount("result=ok") >= 1,
                  "H2.1", "old DB: indexes built in the background, NoIndex cleared, fastOnly ByPrefix -> " + rows.Count + " rows");
            SymbolIndex.Release(a);

            // H2.2: a reindex holds the gate -> no write; after the backoff, the next For() builds.
            string b = Path.Combine(_work, "h2-gated.codegraph.db");
            BuildOldSchema(b, ProjectRows());
            SymbolIndex.BuildBackoffMs = 300;
            IndexRunGate.TryEnter(b);
            var ib = SymbolIndex.For(b);
            WaitFor(() => LogCount("indexCreate db=h2-gated.codegraph.db") > 0, 5000);
            bool noWrite = !IndexNames(b).Contains(SymbolIndex.NameIndex) && LogCount("h2-gated.codegraph.db ms=") == 1 && LogCount("result=busy") >= 1;
            IndexRunGate.Exit(b);
            SymbolIndex.For(b);                                           // inside the backoff: no second attempt
            Thread.Sleep(150);
            bool heldOff = LogCount("indexCreate db=h2-gated.codegraph.db") == 1;
            Thread.Sleep(300);
            SymbolIndex.For(b);                                           // past the backoff: retried
            bool retried = WaitFor(() => IndexNames(b).Contains(SymbolIndex.NameIndex), 5000);
            Check(noWrite && heldOff && retried, "H2.2",
                  "gate held elsewhere: no index written (" + noWrite + "), no retry inside the backoff (" + heldOff + "), built after it (" + retried + ")");
            SymbolIndex.Release(b);

            // H2.3: a read-only file -> logged and given up, nothing thrown, the fallback still answers.
            string c = Path.Combine(_work, "h2-readonly.codegraph.db");
            BuildOldSchema(c, ProjectRows());
            File.SetAttributes(c, FileAttributes.ReadOnly);
            Exception ex = null;
            List<CodeGraphSymbol> slow = null;
            try
            {
                var ic = SymbolIndex.For(c);
                WaitFor(() => LogCount("indexCreate db=h2-readonly.codegraph.db") > 0, 5000);
                slow = ic.ByPrefix("glo", 100);
            }
            catch (Exception e) { ex = e; }
            Check(ex == null && LogCount("h2-readonly.codegraph.db ms=") == 1 && LogCount("result=readonly") == 1 && slow != null && slow.Count == 4
                  && !IndexNames(c).Contains(SymbolIndex.NameIndex),
                  "H2.3", "read-only DB: logged result=readonly, nothing thrown, fallback answers " + (slow == null ? 0 : slow.Count) + " rows");
            SymbolIndex.Release(c);
            File.SetAttributes(c, FileAttributes.Normal);
        }
        finally { SymbolIndex.AutoIndex = false; SymbolIndex.BuildBackoffMs = 60000; }
    }

    // ------------------------------------------------------------------------------ 2.16, 2.17, 2.20

    static void Lifecycle(string proj)
    {
        Console.WriteLine("lifecycle");
        // 2.16 control: a held connection blocks the delete on this machine.
        string a = Path.Combine(_work, "del-a.codegraph.db");
        string b = Path.Combine(_work, "del-b.codegraph.db");
        BuildIndexed(a, ProjectRows());
        BuildIndexed(b, ProjectRows());
        SymbolIndex.For(a).ByPrefix("Glo", 5);
        SymbolIndex.For(b).ByPrefix("Glo", 5);
        bool controlBlocked;
        try { File.Delete(a); controlBlocked = File.Exists(a); } catch { controlBlocked = true; }
        Check(controlBlocked, "2.16-control", "without Release, deleting a DB SymbolIndex holds fails on this machine");
        SymbolIndex.Release(b);
        try { File.Delete(b); } catch { }
        Check(!File.Exists(b), "2.16", "Release(path) then File.Delete -> the file is gone");
        SymbolIndex.Release(a);

        // 2.17: the DB file changes -> the next query reopens and serves the new rows.
        string c = Path.Combine(_work, "chg.codegraph.db");
        BuildIndexed(c, ProjectRows());
        var idx = SymbolIndex.For(c);
        idx.ByPrefix("Glo", 50);
        int opens = SymbolIndex.OpenCountFor(c);
        Thread.Sleep(20);
        using (var w = new SQLiteConnection("Data Source=" + c + ";Version=3;"))
        {
            w.Open();
            Insert(w, new[] { new[] { "GloNew", "variable", "global", null, "LONG" } });
            Exec(w, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        File.SetLastWriteTimeUtc(c, DateTime.UtcNow.AddSeconds(5));   // a replaced/rewritten file
        var after = idx.ByPrefix("Glo", 50);
        Check(Names(after).Contains("GloNew") && SymbolIndex.OpenCountFor(c) == opens + 1, "2.17",
              "after the file changed: GloNew served, OpenCount " + opens + " -> " + SymbolIndex.OpenCountFor(c));
        SymbolIndex.Release(c);

        // 2.20: a writer holding an exclusive lock never stalls a query.
        foreach (bool wal in new[] { true, false })
        {
            string d = Path.Combine(_work, (wal ? "lock-wal" : "lock-rollback") + ".codegraph.db");
            BuildIndexed(d, ProjectRows());
            if (!wal)
                using (var w = new SQLiteConnection("Data Source=" + d + ";Version=3;")) { w.Open(); Exec(w, "PRAGMA journal_mode=DELETE"); }
            var di = SymbolIndex.For(d);
            di.ByPrefix("Glo", 5);   // open first
            using (var w = new SQLiteConnection("Data Source=" + d + ";Version=3;"))
            {
                w.Open();
                Exec(w, "BEGIN EXCLUSIVE");
                Exec(w, "INSERT INTO symbols (name, type, file_path) VALUES ('GloLocked', 'variable', 'x')");
                var sw = Stopwatch.StartNew();
                Exception ex = null;
                List<CodeGraphSymbol> got = null;
                try { got = di.ByPrefix("Glo", 5); } catch (Exception e) { ex = e; }
                long ms = sw.ElapsedMilliseconds;
                Check(ex == null && got != null && ms < 100, "2.20" + (wal ? "" : "b"),
                      (wal ? "WAL" : "rollback-journal") + " DB under BEGIN EXCLUSIVE: returned " + (got == null ? "null" : got.Count + " rows") + " in " + ms + " ms, no throw");
                Exec(w, "ROLLBACK");
            }
            SymbolIndex.Release(d);
        }
    }

    // ------------------------------------------------------------------------------ 2.18, 2.19 (source scans)

    static string Slice(string src, string startMarker, string endMarker)
    {
        int a = src.IndexOf(startMarker, StringComparison.Ordinal);
        if (a < 0) return "";
        int b = src.IndexOf(endMarker, a + startMarker.Length, StringComparison.Ordinal);
        return b < 0 ? src.Substring(a) : src.Substring(a, b - a);
    }

    static bool Before(string text, string first, string second)
    {
        int a = text.IndexOf(first, StringComparison.Ordinal), b = text.IndexOf(second, StringComparison.Ordinal);
        return a >= 0 && b >= 0 && a < b;
    }

    static void Sources(string repo)
    {
        Console.WriteLine("release wiring (source scans)");
        string chat = File.ReadAllText(Path.Combine(repo, "AssistantChatControl.cs"));
        string work = Slice(chat, "worker.DoWork += (s, e) =>", "worker.ProgressChanged");
        string cancelled = Slice(work, "if (wasCancelled && !incremental)", "}");
        Check(Before(work, "SymbolIndex.Release(dbPath)", "db.Open(dbPath)") && Before(cancelled, "SymbolIndex.Release(dbPath)", "File.Delete(dbPath)"),
              "2.18a", "RunIndex releases before the write open and before the cancelled-run delete");
        string reg = File.ReadAllText(Path.Combine(repo, "Services", "McpToolRegistry.cs"));
        string exec = Slice(reg, "private object ExecuteIndexCodeGraph(", "indexer.IndexSolution");
        Check(Before(exec, "SymbolIndex.Release(dbPath)", "db.Open(dbPath)"), "2.18b", "ExecuteIndexCodeGraph releases before the write open");
        string cg = File.ReadAllText(Path.Combine(repo, "Services", "ClarionGraphService.cs"));
        Check(Before(Slice(cg, "private static void DeleteDbFiles(", "catch"), "SymbolIndex.Release(dbPath)", "File.Delete"), "2.18c",
              "the ClarionGraph rebuild releases before deleting");
        Check(Slice(chat, "private void OnSolutionChanged(", "private ").Contains("SymbolIndex.ReleaseAll()"), "2.19a", "OnSolutionChanged calls ReleaseAll");
        string auto = File.ReadAllText(Path.Combine(repo, "LspAutostartCommand.cs"));
        Check(Slice(auto, "private static void OnSolutionClosed(", "catch (Exception ex)").Contains("SymbolIndex.ReleaseAll()"), "2.19b", "the IDE's SolutionClosed calls ReleaseAll");
    }

    // ------------------------------------------------------------------------------ 2.15 (synthetic 422k)

    static long PrivateBytes() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64; }

    static void Large()
    {
        Console.WriteLine("large (422k synthetic rows)");
        string big = Path.Combine(_work, "big.codegraph.db");
        using (var db = new CodeGraphDatabase()) { db.Open(big); }
        var rnd = new Random(42);
        string[] scopes = { "local", "local", "local", "local", "local", "parameter", "parameter", "module", "module", "global" };
        using (var cn = new SQLiteConnection("Data Source=" + big + ";Version=3;"))
        {
            cn.Open();
            Exec(cn, "INSERT INTO projects (id, name) VALUES (1, 'big')");
            var rows = new List<string[]>(422000);
            for (int i = 0; i < 422000; i++)
            {
                string name = RandomName(rnd) + i;
                rows.Add(new[] { name, "variable", scopes[i % scopes.Length], null, "LONG" });
            }
            Insert(cn, rows);
        }
        string bigOld = Path.Combine(_work, "big-old.codegraph.db");
        File.Copy(big, bigOld);
        using (var cn = new SQLiteConnection("Data Source=" + bigOld + ";Version=3;"))
        {
            cn.Open();
            Exec(cn, "DROP INDEX " + SymbolIndex.NameIndex);
            Exec(cn, "DROP INDEX " + SymbolIndex.ParentIndex);
        }
        var prefixes = Enumerable.Range(0, 1000).Select(i => RandomName(rnd).Substring(0, 2 + (i % 2))).ToList();
        long mem0 = PrivateBytes();
        var t = Time(big, prefixes);
        long mem1 = PrivateBytes();
        var tOld = Time(bigOld, prefixes.Take(200).ToList());
        Console.WriteLine(string.Format("    indexed: p50 {0:F2} ms, p95 {1:F2} ms; old-schema fallback: p50 {2:F2} ms, p95 {3:F2} ms; private bytes +{4} KB",
                                        t[0], t[1], tOld[0], tOld[1], (mem1 - mem0) / 1024));
        Check(t[1] < 20, "2.15", string.Format("p95 over 1000 random 2-3 char prefixes < 20 ms ({0:F2} ms)", t[1]));
        SymbolIndex.Release(big);
        SymbolIndex.Release(bigOld);
    }

    static string RandomName(Random r)
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var c = new char[6];
        for (int i = 0; i < c.Length; i++) c[i] = letters[r.Next(letters.Length)];
        return new string(c);
    }

    static double[] Time(string db, List<string> prefixes)
    {
        var idx = SymbolIndex.For(db);
        idx.ByPrefix("warm", 100);
        var ms = new List<double>();
        var sw = new Stopwatch();
        foreach (var p in prefixes)
        {
            sw.Restart();
            idx.ByPrefix(p, 100);
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
        ms.Sort();
        return new[] { ms[ms.Count / 2], ms[(int)(ms.Count * 0.95)] };
    }

    // ------------------------------------------------------------------------------ measurement on a real DB copy

    static void Real(string copy)
    {
        Console.WriteLine("real DB copy: " + copy);
        var rnd = new Random(7);
        var prefixes = Enumerable.Range(0, 300).Select(i => RandomName(rnd).Substring(0, 2 + (i % 2))).ToList();
        prefixes.AddRange(new[] { "Glo", "Loc", "Brw", "Thi", "Acc", "Rel", "Inv", "Cus", "Pro", "Win" });
        long mem0 = PrivateBytes();
        var before = Time(copy, prefixes);
        long mem1 = PrivateBytes();
        Console.WriteLine(string.Format("    as found (noIndex={0}): p50 {1:F2} ms, p95 {2:F2} ms, private bytes +{3} KB",
                                        SymbolIndex.For(copy).NoIndex, before[0], before[1], (mem1 - mem0) / 1024));
        SymbolIndex.Release(copy);
        // H2: the background build, as the Owner's first open of an old DB triggers it.
        SymbolIndex.AutoIndex = true;
        int built = SymbolIndex.IndexBuildCount;
        var sw = Stopwatch.StartNew();
        var ri = SymbolIndex.For(copy);
        ri.Warm();
        bool ok = WaitFor(() => SymbolIndex.IndexBuildCount > built, 60000);
        string line;
        lock (Log) line = Log.LastOrDefault(l => l.Contains("indexCreate db=" + Path.GetFileName(copy)));
        Console.WriteLine(string.Format("    background build: {0} (waited {1} ms), file now {2:F1} MB", line ?? "(no log line)",
                                        sw.ElapsedMilliseconds, new FileInfo(copy).Length / 1048576.0));
        SymbolIndex.AutoIndex = false;
        Check(ok, "H2.real", "the background build indexed the real DB copy");
        mem0 = PrivateBytes();
        var after = Time(copy, prefixes);
        mem1 = PrivateBytes();
        var m = SymbolIndex.MembersOf("BrowseClass", true, copy, null).Count;
        Console.WriteLine(string.Format("    indexed (noIndex={0}): p50 {1:F2} ms, p95 {2:F2} ms, private bytes +{3} KB; MembersOf(BrowseClass) {4} rows",
                                        SymbolIndex.For(copy).NoIndex, after[0], after[1], (mem1 - mem0) / 1024, m));
        SymbolIndex.Release(copy);
    }
}
