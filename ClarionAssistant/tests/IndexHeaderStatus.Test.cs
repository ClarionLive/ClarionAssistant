// IndexHeaderStatus.Test.cs - the chat header's CodeGraph line (GH #279 follow-up, integ-1023 review D6/D7).
//
//   W   the spinner text: "Rebuilding index..." for an incremental request that runs full, "Updating..." otherwise.
//   S   "N of M projects have changes" with N > 0 is the early real-work signal; 0 of M is not.
//   O   the remembered outcome: per solution, with the database stamp; an error/cancel line stays; a database
//       written since (index_codegraph, the standalone server) drops it.
//   F   an automatic check for another solution during a run is queued, the same one is not, and it runs only if
//       that solution is still shown.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClarionAssistant.Services;

static class IndexHeaderStatusTest
{
    static int _n;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _n++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static int Main()
    {
        Console.WriteLine("-- W: spinner text");
        Check(IndexHeaderText.WorkText(true, false) == "Rebuilding index...", "W1", "incremental request that runs full: '" + IndexHeaderText.WorkText(true, false) + "'");
        Check(IndexHeaderText.WorkText(true, true) == "Updating...", "W2", "incremental that stays incremental: '" + IndexHeaderText.WorkText(true, true) + "'");
        Check(IndexHeaderText.WorkText(true, null) == "Updating...", "W3", "before the mode is known: '" + IndexHeaderText.WorkText(true, null) + "'");
        Check(IndexHeaderText.WorkText(false, false) == "Indexing...", "W4", "a full index that was asked for: '" + IndexHeaderText.WorkText(false, false) + "'");
        Check(IndexHeaderText.Checking == "Checking for changes...", "W5", "the quiet check's text is unchanged");

        Console.WriteLine("-- S: the changed-projects message");
        Check(IndexHeaderText.MessageSignalsWork("3 of 27 projects have changes"), "S1", "3 of 27 is real work");
        Check(!IndexHeaderText.MessageSignalsWork("0 of 27 projects have changes"), "S2", "0 of 27 is not");
        Check(!IndexHeaderText.MessageSignalsWork("No changes detected - index is up to date."), "S3", "the up-to-date line is not");
        Check(!IndexHeaderText.MessageSignalsWork(null) && !IndexHeaderText.MessageSignalsWork("Parsing solution file..."), "S4", "nor null or any other line");
        Check(!IndexHeaderText.MessageSignalsWork("  INCLUDE not resolved: 2 of 3 projects have changes.inc (x.clw:1)"), "S5", "anchored at the start of the line");

        Console.WriteLine("-- O: the remembered outcome");
        string root = Path.Combine(Path.GetTempPath(), "ca-hdr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            string db = Path.Combine(root, "A.codegraph.db");
            File.WriteAllText(db, "x", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(db, new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc));
            var memo = new IndexOutcomeMemo();
            string t, st;
            memo.Record(@"C:\s\A.sln", "Error: disk full", "error", IndexOutcomeMemo.StampOf(db));
            Check(memo.TryGet(@"c:\S\a.sln", IndexOutcomeMemo.StampOf(db), out t, out st) && t == "Error: disk full" && st == "error",
                  "O1", "an error line stays the header's line for its solution while the database is unchanged");
            Check(!memo.TryGet(@"C:\s\B.sln", IndexOutcomeMemo.StampOf(db), out t, out st), "O2", "another solution does not get it");
            File.SetLastWriteTimeUtc(db, new DateTime(2026, 10, 23, 10, 5, 0, DateTimeKind.Utc));
            Check(!memo.TryGet(@"C:\s\A.sln", IndexOutcomeMemo.StampOf(db), out t, out st), "O3", "the database written since (index_codegraph): dropped");
            File.SetLastWriteTimeUtc(db, new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc));
            Check(!memo.TryGet(@"C:\s\A.sln", IndexOutcomeMemo.StampOf(db), out t, out st), "O4", "and stays dropped");
            File.Delete(db);
            memo.Record(@"C:\s\A.sln", "Index cancelled", "error", IndexOutcomeMemo.StampOf(db));
            Check(IndexOutcomeMemo.StampOf(db) == DateTime.MinValue && memo.TryGet(@"C:\s\A.sln", IndexOutcomeMemo.StampOf(db), out t, out st) && t == "Index cancelled",
                  "O5", "a cancelled full run whose partial database was deleted keeps 'Index cancelled', not 'Not indexed'");
            File.WriteAllText(db, "y", new UTF8Encoding(false));
            Check(!memo.TryGet(@"C:\s\A.sln", IndexOutcomeMemo.StampOf(db), out t, out st), "O6", "a database written afterwards drops it");
            memo.Record(@"C:\s\A.sln", "Up to date (checked Oct 23 10:00)", "", IndexOutcomeMemo.StampOf(db));
            memo.Clear();
            Check(!memo.TryGet(@"C:\s\A.sln", IndexOutcomeMemo.StampOf(db), out t, out st), "O7", "Clear forgets it");
            Check(IndexOutcomeMemo.StampOf(null) == DateTime.MinValue && IndexOutcomeMemo.StampOf("") == DateTime.MinValue, "O8", "no path: no stamp, no throw");
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine("-- F: a check for another solution during a run");
        var f = new IndexFollowUp();
        Check(!f.Queue(@"C:\s\A.sln", @"c:\S\a.sln") && f.Pending == null, "F1", "the solution already running: in hand, nothing queued");
        Check(f.Queue(@"C:\s\A.sln", @"C:\s\B.sln") && f.Pending == @"C:\s\B.sln", "F2", "another solution is queued");
        Check(f.Queue(@"C:\s\A.sln", @"C:\s\C.sln") && f.Pending == @"C:\s\C.sln", "F3", "the latest request wins");
        Check(f.Take(@"C:\s\C.sln") == @"C:\s\C.sln" && f.Pending == null, "F4", "the run ended with C shown: C is checked, the queue is empty");
        f.Queue(@"C:\s\A.sln", @"C:\s\B.sln");
        Check(f.Take(@"C:\s\A.sln") == null && f.Pending == null, "F5", "the developer went back to A: B's check is dropped, not run behind their back");
        Check(!f.Queue(@"C:\s\A.sln", null) && f.Pending == null, "F6", "no solution: nothing queued");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "IndexHeaderStatus: " + _n + " assertions, all passed"
            : "IndexHeaderStatus: " + Failures.Count + " of " + _n + " FAILED");
        return Failures.Count == 0 ? 0 : 1;
    }
}
