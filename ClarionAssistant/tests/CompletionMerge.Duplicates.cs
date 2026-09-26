// GH #187 (a): member completion listing the same method twice.
//
// Driven by CompletionMerge.DuplicateTest.ps1, which compiles this against the REAL
// clarion-mcp-server.exe (it compiles Services\SharedLspBridge.cs + Services\LspClient.cs from their
// one home) so the whole completion pipeline under test is the shipped one:
//
//   LspClient (real, over stdio)  ->  fake-lsp.js answers with a fixed item list
//   SharedLspBridge.GetCompletion ->  every host merge, including the CodeGraph member-access merge
//                                     against a synthetic .codegraph.db
//
// The fixed list is the shape of the reporter's screenshot: the server's "Name(params)" labels, with
// two members sent twice (once identical, once differing only by a missing detail - the shape a
// prototype + implementation pair would take) beside a pair of genuine OVERLOADS, which are different
// labels for the same insertText and must both survive. The CodeGraph db knows the same class's
// members, so a method the server already listed must not come back a second time from the db, and a
// member ONLY the db knows must still be added exactly once.
//
// Args: <fake-lsp.js> <work dir>   (LspClient finds node.exe itself, as it does in the IDE)
// Exit: 0 pass, 1 fail, 2 could-not-run.
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using ClarionAssistant.Services;

static class CompletionMergeDuplicates
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) Failures.Add(message);
    }

    static int Main(string[] args)
    {
        string serverJs = args[0];
        string work = args[1];

        // --- synthetic CodeGraph db: the same class the buffer's instance is declared as -----------
        string db = Path.Combine(work, "issue187.codegraph.db");
        SQLiteConnection.CreateFile(db);
        using (var cn = new SQLiteConnection("Data Source=" + db + ";Version=3;"))
        {
            cn.Open();
            // A real index is WAL; CodeGraphProvider opens read-only WITH "Journal Mode=WAL", which a
            // read-only connection cannot switch a rollback-journal db into - Open() would just fail.
            Exec(cn, "PRAGMA journal_mode=WAL");
            Exec(cn, "CREATE TABLE projects (id INTEGER PRIMARY KEY, name TEXT)");
            Exec(cn, "CREATE TABLE symbols (id INTEGER PRIMARY KEY, name TEXT, type TEXT, file_path TEXT, " +
                     "line_number INTEGER, params TEXT, return_type TEXT, parent_name TEXT, member_of TEXT, " +
                     "scope TEXT, project_id INTEGER)");
            Exec(cn, "INSERT INTO projects VALUES (1, 'issue187')");
            Exec(cn, "INSERT INTO symbols VALUES (1, 'Issue187Class', 'class', 'issue187.inc', 1, NULL, NULL, NULL, NULL, 'global', 1)");
            Exec(cn, "INSERT INTO symbols VALUES (2, 'Issue187Class._ColorToHex', 'procedure', 'issue187.inc', 2, '(long pAddHash=false)', NULL, 'Issue187Class', NULL, 'virtual', 1)");
            Exec(cn, "INSERT INTO symbols VALUES (3, 'Issue187Class._ColorFromCSL', 'procedure', 'issue187.inc', 3, '(string pColor)', 'LONG', 'Issue187Class', NULL, 'virtual', 1)");
            Exec(cn, "INSERT INTO symbols VALUES (4, 'Issue187Class.Trace', 'procedure', 'issue187.inc', 4, '(<string errMsg>)', NULL, 'Issue187Class', NULL, 'virtual', 1)");
            Exec(cn, "INSERT INTO symbols VALUES (5, 'Issue187Class._DataEnd', 'variable', 'issue187.inc', 5, 'LONG', NULL, 'Issue187Class', NULL, 'class', 1)");
            Exec(cn, "INSERT INTO symbols VALUES (6, 'Issue187Class.DbOnlyMethod', 'procedure', 'issue187.inc', 6, '()', NULL, 'Issue187Class', NULL, 'class', 1)");
        }
        SharedLspBridge.CodeGraphDbPathProvider = () => db;

        // --- the buffer: a procedure-local instance of that class, cursor after "obj." ------------
        string file = Path.Combine(work, "issue187.clw");
        string[] lines =
        {
            "  MEMBER()",
            "",
            "Issue187Proc PROCEDURE",
            "",
            "obj                  Issue187Class",
            "",
            "  CODE",
            "  obj.",
            "  RETURN",
        };
        string buffer = string.Join("\r\n", lines) + "\r\n";
        File.WriteAllText(file, buffer);
        int line = Array.IndexOf(lines, "  obj.");
        int character = lines[line].Length;

        // --- a real LspClient over stdio, talking to the stand-in server ---------------------------
        var client = new LspClient();
        bool started;
        try { started = client.Start(serverJs, new Uri(work + "\\").AbsoluteUri.TrimEnd('/'), "issue187"); }
        catch (Exception ex) { Console.WriteLine("COULD NOT RUN: LspClient.Start threw: " + ex.Message); return 2; }
        if (!started)
        {
            Console.WriteLine("COULD NOT RUN: LspClient did not start the stand-in server (" + (client.LastSpawnError ?? "handshake failed") + "). Needs node.exe.");
            return 2;
        }
        typeof(LspClient).GetField("_active", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, client);

        try
        {
            if (!SharedLspBridge.IsRunning) { Console.WriteLine("COULD NOT RUN: SharedLspBridge does not see the client as running."); return 2; }
            if (SharedLspBridge.IsSharedActive) { Console.WriteLine("COULD NOT RUN: the shared ClarionLsp addin path is active; this drives the bundled one."); return 2; }

            var items = SharedLspBridge.GetCompletion(file, line, character, 5000, buffer);
            if (items == null || items.Count == 0)
            {
                Console.WriteLine("COULD NOT RUN: no completion items came back (" + client.LastCompletionDiagnostic + ").");
                return 2;
            }
            foreach (var it in items)
                Console.WriteLine("  item: " + it.Label + "  | kind=" + it.Kind + " insert=" + it.InsertText + " detail=" + it.Detail);

            Func<string, int> countLabel = l => items.Count(it => string.Equals(it.Label, l, StringComparison.OrdinalIgnoreCase));

            // 1. THE REGRESSION GUARD: a member the server sent twice is listed once.
            Check(countLabel("_ColorToHex(long pAddHash=false)") == 1,
                "_ColorToHex(long pAddHash=false) listed " + countLabel("_ColorToHex(long pAddHash=false)") + " times, expected once");
            Check(countLabel("_ColorFromCSL(string pColor)") == 1,
                "_ColorFromCSL(string pColor) listed " + countLabel("_ColorFromCSL(string pColor)") + " times, expected once " +
                "(the second copy differs only by a missing detail - still the same row to the developer)");

            // ...and the copy that stays keeps the detail, whichever of the two arrived first.
            var csl = items.FirstOrDefault(it => it.Label == "_ColorFromCSL(string pColor)");
            Check(csl != null && csl.Detail == "Long, virtual",
                "the surviving _ColorFromCSL lost its detail: '" + (csl == null ? "(missing)" : csl.Detail) + "'");

            // 2. No row appears twice anywhere in the merged list (same label AND same detail).
            var dups = items.GroupBy(it => (it.Label ?? "") + " | " + (it.Detail ?? ""), StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1).Select(g => g.Key + " x" + g.Count()).ToList();
            Check(dups.Count == 0, "duplicate rows in the merged list: " + string.Join(", ", dups));

            // 3. OVERLOADS are not duplicates: same insertText, different signatures - both stay.
            Check(countLabel("Trace(<string errMsg>)") == 1 && countLabel("Trace(Queue pQueue)") == 1,
                "an overload of Trace was dropped - overloads are distinct items, not duplicates");

            // 3b. ...including when the server labels overloads BARE and puts the signature only in the
            // detail: two "Append" rows with different details are two overloads, not one repeat.
            var appends = items.Where(it => it.Label == "Append").Select(it => it.Detail ?? "").ToList();
            Check(appends.Count == 2 && appends.Contains("(STRING pStr)") && appends.Contains("(StringTheory pStr)"),
                "bare-label overloads of Append collapsed: [" + string.Join("; ", appends) + "] - expected both signatures");

            // 4. The CodeGraph merge adds nothing the server already listed, by its bare name either.
            foreach (var bare in new[] { "_ColorToHex", "_ColorFromCSL", "Trace", "_DataEnd" })
                Check(countLabel(bare) == 0, "the CodeGraph merge re-added '" + bare + "', which the server already listed");

            // 5. ...but still adds, once, the member only it knows about - the merge is not switched off.
            Check(countLabel("DbOnlyMethod") == 1,
                "DbOnlyMethod (CodeGraph-only member) listed " + countLabel("DbOnlyMethod") + " times, expected once");
            Check(countLabel("_DataEnd LONG") == 1, "the _DataEnd field was dropped or doubled");
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }

    static void Exec(SQLiteConnection cn, string sql)
    {
        using (var cmd = new SQLiteCommand(sql, cn)) cmd.ExecuteNonQuery();
    }
}
