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
            Lifecycle(proj);
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

        Check(Plan(proj, SymbolIndex.PrefixSql, cmd => { cmd.Parameters.AddWithValue("@lo", "Glo"); cmd.Parameters.AddWithValue("@hi", "Glo\uFFFF"); cmd.Parameters.AddWithValue("@limit", 10); })
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
        var sw = Stopwatch.StartNew();
        using (var db = new CodeGraphDatabase()) { db.Open(copy); }   // the indexer's next write open
        Console.WriteLine(string.Format("    CodeGraphDatabase.Open (creates the NOCASE indexes): {0} ms, file now {1:F1} MB",
                                        sw.ElapsedMilliseconds, new FileInfo(copy).Length / 1048576.0));
        mem0 = PrivateBytes();
        var after = Time(copy, prefixes);
        mem1 = PrivateBytes();
        var m = SymbolIndex.MembersOf("BrowseClass", true, copy, null).Count;
        Console.WriteLine(string.Format("    indexed (noIndex={0}): p50 {1:F2} ms, p95 {2:F2} ms, private bytes +{3} KB; MembersOf(BrowseClass) {4} rows",
                                        SymbolIndex.For(copy).NoIndex, after[0], after[1], (mem1 - mem0) / 1024, m));
        SymbolIndex.Release(copy);
    }
}
