// GH #313 (bb480461): query_docs ranks a chunk into a heading tier on the query's FIRST word only, so
// "HttpSubmitRequest Parameters" put "HttpSubmitRequest Function > Parameters" last of the function's nine
// sections. Compiles the REAL Services\DocGraphService.cs over real x86 SQLite (FTS5 from lib\sqlite-fts5),
// builds a temp DocGraph with the issue's headings, and checks the order QueryDocs returns.
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using ClarionAssistant.Services;

static class DocGraphQueryDocsTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static SQLiteConnection Open(string db)
    {
        var c = new SQLiteConnection("Data Source=" + db + ";Version=3;");
        c.Open();
        c.EnableExtensions(true);
        c.LoadExtension("SQLite.Interop.dll", "sqlite3_fts5_init");
        return c;
    }

    // Headings from GH #313 (SocketTools 12 Markdown reference, library cshtpapi), with content shaped like the
    // real thing: "Parameters" appears all through every function's text, and the Parameters section is the longest.
    static readonly string[][] Chunks =
    {
        new[] { "HttpSubmitRequest Function > Prototype", "" },
        new[] { "HttpSubmitRequest Function > See Also", "HttpSubmitRequest HttpConnect HttpGetData parameters" },
        new[] { "HttpSubmitRequest Function", "HttpSubmitRequest submits a request to the server." },
        new[] { "HttpSubmitRequest Function > Requirements", "HttpSubmitRequest requires cshtpv12.dll; see the parameters for the Unicode build." },
        new[] { "HttpSubmitRequest Function > Remarks", Repeat("HttpSubmitRequest sends the request built from the parameters and waits for the response. ", 14) },
        new[] { "HttpSubmitRequest Function > Return Value", Repeat("HttpSubmitRequest returns non-zero on success; the parameters are unchanged. ", 5) },
        new[] { "HttpSubmitRequest Function > Example", "" },
        new[] { "HttpSubmitRequest Function > Options", Repeat("HttpSubmitRequest option flags combine with the other parameters. ", 18) },
        new[] { "HttpSubmitRequest Function > Parameters", Repeat("hClient handle. lpszMethod verb. lpszResource path. The parameters are described in this table. ", 30) },
        new[] { "HttpConnect Function > Return Value", "HttpConnect returns a client handle." },
        new[] { "HttpConnectUrl Function > Return Value", "HttpConnectUrl returns a client handle." },
        new[] { "HTTPCONNECTION Structure > Definition", "HTTPCONNECTION holds the HttpConnect settings." },
        new[] { "HttpGetData Function > Parameters", Repeat("hClient handle. lpszResource path. The parameters are described here. ", 10) },
        new[] { "Changelog > [1.2.0] > Fixed", "Fixed 100% CPU in HttpGetData." },
    };

    static string Repeat(string s, int n) { return string.Concat(Enumerable.Repeat(s, n)); }

    static void Build(string db)
    {
        var svc = new DocGraphService(db);
        svc.EnsureDatabase();
        using (var c = Open(db))
        {
            using (var cmd = new SQLiteCommand("INSERT INTO libraries(name, vendor, source_format) VALUES ('cshtpapi', 'SocketTools', 'md')", c))
                cmd.ExecuteNonQuery();
            foreach (var ch in Chunks)
            {
                using (var cmd = new SQLiteCommand(
                    "INSERT INTO doc_chunks(library_id, class_name, method_name, topic, heading, content) VALUES (1, 'cshtpapi', NULL, 'reference', @h, @c)", c))
                {
                    cmd.Parameters.AddWithValue("@h", ch[0]);
                    cmd.Parameters.AddWithValue("@c", ch[1]);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        svc.RebuildFtsIndex();
    }

    // QueryDocs prints "Topic: <topic> | <heading>" once per result, in rank order.
    static List<string> Headings(DocGraphService svc, string query, int limit)
    {
        string text = svc.QueryDocs(query, null, null, limit);
        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (!l.StartsWith("Topic: ")) continue;
            int bar = l.IndexOf(" | ", StringComparison.Ordinal);
            list.Add(bar < 0 ? "" : l.Substring(bar + 3));
        }
        return list;
    }

    static string Show(List<string> h) { return h.Count == 0 ? "(none)" : string.Join(" ; ", h); }

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-docgraph-qd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "docgraph.db");
        try
        {
            Build(db);
            var svc = new DocGraphService(db);

            // A. The issue: the named section comes first, even at limit 1.
            var a = Headings(svc, "HttpSubmitRequest Parameters", 1);
            Check(a.Count == 1 && a[0] == "HttpSubmitRequest Function > Parameters", "A",
                  "'HttpSubmitRequest Parameters' limit 1 -> the Parameters section (got: " + Show(a) + ")");

            // B. A section a query names, with a stop word in front: the stop word must not be the tier word.
            var b = Headings(svc, "the HttpSubmitRequest Return Value", 1);
            Check(b.Count == 1 && b[0] == "HttpSubmitRequest Function > Return Value", "B",
                  "'the HttpSubmitRequest Return Value' limit 1 -> Return Value (got: " + Show(b) + ")");

            // C. Two words, both in the heading, beat one: HttpConnect's own Return Value first.
            var c3 = Headings(svc, "HttpConnect Return Value", 3);
            Check(c3.Count > 0 && c3[0] == "HttpConnect Function > Return Value", "C",
                  "'HttpConnect Return Value' -> HttpConnect Function > Return Value first (got: " + Show(c3) + ")");

            // D. A single-word query ranks exactly as before (no extra words -> no extra sort key):
            //    the order is tier, then bm25, then content length, which here puts the bare function heading in
            //    the LIKE tier with its sections. Record it rather than assert a particular order, then assert the
            //    fix did not change it: compare against the order the pre-fix SQL gives (computed below).
            var d = Headings(svc, "HttpSubmitRequest", 20);
            var dOld = OldOrder(db, "HttpSubmitRequest", 20);
            Check(d.SequenceEqual(dOld), "D",
                  "single-word query keeps the pre-fix order (got: " + Show(d) + " | pre-fix: " + Show(dOld) + ")");

            // E. LIKE wildcards in the query are literal: '%' must not match every heading as a tier-3 hit.
            //    "100%" matches only the changelog chunk via FTS ('100'), and must still be found.
            var e = Headings(svc, "100%", 5);
            Check(e.Count >= 1 && e[0] == "Changelog > [1.2.0] > Fixed", "E",
                  "'100%' -> the changelog chunk first (got: " + Show(e) + ")");
            var e2 = Headings(svc, "HttpGetData %", 1);
            Check(e2.Count == 1 && e2[0] == "HttpGetData Function > Parameters", "E2",
                  "'HttpGetData %' limit 1 -> HttpGetData's own section, '%' adds nothing (got: " + Show(e2) + ")");

            // F. A stop-word-only query still searches (SanitizeFtsQuery falls back to the raw words).
            string f = svc.QueryDocs("the", null, null, 5);
            Check(f != null && !f.StartsWith("Error"), "F", "stop-word-only query does not error (got: " + (f ?? "null").Split('\n')[0] + ")");

            // G. After #312 a nested section heading is "Parent > Section": a bare section query ranks those
            //    sections as exact-heading matches (tier 2) again, ahead of chunks that only mention the word.
            var g = Headings(svc, "Parameters", 2);
            Check(g.Count == 2 && g.All(h => h.EndsWith("> Parameters")), "G",
                  "'Parameters' limit 2 -> two '> Parameters' sections (got: " + Show(g) + ")");

            // H. An oversized query (pipeline review, Run 1): every other word used to become a SQL parameter and a
            //    LIKE term, so thousands of words went past SQLite's limits and threw. It must answer normally.
            string huge = "HttpSubmitRequest Parameters " + string.Join(" ", Enumerable.Range(0, 3000).Select(i => "w" + i));
            string hOut = null; Exception hEx = null;
            try { hOut = svc.QueryDocs(huge, null, null, 1); } catch (Exception ex) { hEx = ex; }
            Check(hEx == null && hOut != null && !hOut.StartsWith("Error"), "H",
                  "3002-word query answers without an exception (got: " + (hEx != null ? hEx.GetType().Name + ": " + hEx.Message.Split('\n')[0] : (hOut ?? "null").Split('\n')[0]) + ")");

            // I. A repeated word counts once: 'Parameters' x3 must not outweigh a section naming two different words.
            var i1 = Headings(svc, "HttpSubmitRequest Return Value Parameters Parameters Parameters", 1);
            Check(i1.Count == 1 && i1[0] == "HttpSubmitRequest Function > Return Value", "I",
                  "repeated 'Parameters' counts once -> Return Value (2 words) first (got: " + Show(i1) + ")");
        }
        catch (Exception ex)
        {
            Check(false, "X", "unexpected exception: " + ex);
        }
        finally
        {
            try { SQLiteConnection.ClearAllPools(); Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASSED - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAILED - " + Failures.Count + " of " + _assertions);
        return 1;
    }

    // The ranking SQL as it was before GH #313, for check D: single-word queries must not move.
    static List<string> OldOrder(string db, string word, int limit)
    {
        var list = new List<string>();
        using (var c = Open(db))
        using (var cmd = new SQLiteCommand(@"
            SELECT dc.heading,
                   CASE WHEN dc.topic = 'index' THEN 9
                        WHEN dc.method_name = @exact THEN 0
                        WHEN dc.method_name LIKE '%' || @exact || '%' THEN 1
                        WHEN dc.heading = @exact THEN 2
                        WHEN dc.heading LIKE '%' || @exact || '%' THEN 3
                        WHEN dc.signature LIKE '%' || @exact || '%' THEN 4
                        ELSE 5 END as tier,
                   rank as bm25_score, LENGTH(dc.content) as content_len
            FROM doc_fts JOIN doc_chunks dc ON dc.id = CAST(doc_fts.chunk_id AS INTEGER)
            WHERE doc_fts MATCH @query
            ORDER BY tier, bm25_score, content_len DESC LIMIT @limit", c))
        {
            cmd.Parameters.AddWithValue("@exact", word);
            cmd.Parameters.AddWithValue("@query", "\"" + word + "\"");
            cmd.Parameters.AddWithValue("@limit", limit);
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }
}
