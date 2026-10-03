using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

// Harness for LspClient's incremental document sync — compiles the REAL LspClient.cs + LspTextDiff.cs and drives them
// against tests\fixtures\incremental-sync\fake-lsp.js, a stand-in server that applies every didChange by the LSP's
// rules and reports the text it ends up holding.
//
// WHY. LspClient used to send the whole buffer on every change. On a 61k-line generated module that full-text didChange
// took 1.4 s typically and up to 6 s to send (HoverBench, 2026-10-03), holding the document-sync lock the whole time;
// a ranged change takes ~21 ms. The ranged path is only as good as the server's resulting copy, so this pins:
//   * against a server advertising incremental sync, changes go as RANGES and the server's text equals the editor's
//     after many edits (CRLF, surrogates, inserts and deletes anywhere);
//   * against a server advertising FULL sync, or with LspClient.IncrementalSyncEnabled off, changes go as full text;
//   * a disk resync (SendDidChangeFromDisk, which GetDiagnostics runs for an open document) is recorded, so the next ranged change
//     applies to the text the server really holds;
//   * Stop forgets every document, so a reused client opens afresh instead of sending ranges against a text the new
//     server never received.
//
// Run:  tests\Run-Tests.ps1   (passes the fake server's path; needs node.exe where LspClient looks for it)
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class LspClientIncrementalSyncTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static string fake, dir;

    static LspClient Start(int syncKind)
    {
        Environment.SetEnvironmentVariable("FAKE_SYNC", syncKind.ToString());
        var c = new LspClient();
        if (!c.Start(fake, "file:///" + dir.Replace("\\", "/"), "incr")) return null;
        return c;
    }

    static string Uri(string path) { return "file:///" + path.Replace("\\", "/").Replace(" ", "%20"); }

    // The fake server's state for one document: text, ranged/full change counts, opens.
    static Dictionary<string, object> ServerState(LspClient c, string path)
    {
        return c.FindFile(Uri(path));
    }

    static int Int(Dictionary<string, object> d, string k) { return d != null && d.ContainsKey(k) ? Convert.ToInt32(d[k]) : -1; }

    static readonly string[] Inserts = { "X", " ", "\r\n", "\r\n  Loc LONG\r\n", "\u00e9", "\U0001F600", "", "IF a\r\nEND\r\n" };

    // Many random edits, each synced; returns the editor's final text.
    static string Edit(LspClient c, string path, string text, int edits, Random r)
    {
        for (int i = 0; i < edits; i++)
        {
            int p = r.Next(0, text.Length + 1);
            if (p > 0 && p < text.Length && (char.IsLowSurrogate(text[p]) || (text[p] == '\n' && text[p - 1] == '\r'))) p--;
            int e = Math.Min(text.Length, p + (r.Next(0, 3) == 0 ? r.Next(0, 8) : 0));
            if (e > 0 && e < text.Length && (char.IsLowSurrogate(text[e]) || (text[e] == '\n' && text[e - 1] == '\r'))) e++;
            text = text.Substring(0, p) + Inserts[r.Next(Inserts.Length)] + text.Substring(Math.Min(e, text.Length));
            c.EnsureBufferSynced(path, text);
        }
        return text;
    }

    static int Main(string[] args)
    {
        fake = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
        if (fake == null || !File.Exists(fake)) { Console.WriteLine("COULD NOT RUN: fake server not found: " + (fake ?? "(no argument)")); return 2; }
        dir = Path.Combine(Path.GetTempPath(), "ca-incr-sync-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string doc = Path.Combine(dir, "Module One.clw");   // a space in the name, as in real projects
        string seed = "  MEMBER('App')\r\nProc PROCEDURE\r\nx LONG\r\n  CODE\r\n  x = 1 ! \u00e9\U0001F600\r\n";
        var r = new Random(206);
        try
        {
            // ---- incremental server ----
            var c = Start(2);
            if (c == null) { Console.WriteLine("COULD NOT RUN: LspClient could not start the fake server (is node.exe installed?)"); return 2; }
            Ok("a server advertising incremental sync is used incrementally", c.UsesIncrementalSync);
            c.EnsureBufferSynced(doc, seed);
            string text = Edit(c, doc, seed, 400, r);
            var st = ServerState(c, doc);
            Ok("after 400 edits the server holds exactly the editor's text", st != null && (string)st["text"] == text,
               st == null ? "no state" : "server " + ((string)st["text"]).Length + " chars vs editor " + text.Length);
            Ok("... every change went as a range (none as full text)", Int(st, "ranged") > 0 && Int(st, "full") == 0,
               "ranged " + Int(st, "ranged") + ", full " + Int(st, "full"));
            Ok("... and the client's own counters agree", c.IncrementalChangesSent == Int(st, "ranged") && c.FullChangesSent == 0,
               "client incremental " + c.IncrementalChangesSent + ", full " + c.FullChangesSent);

            // A disk resync replaces the server's copy with the file; the next ranged change must apply to THAT text.
            string disk = "! from disk\r\nProc PROCEDURE\r\n  CODE\r\n";
            File.WriteAllText(doc, disk);
            c.GetDiagnostics(doc, 200);   // re-reads the open document from disk first (SendDidChangeFromDisk); the fake never publishes, so this just times out
            st = ServerState(c, doc);
            Ok("a disk resync leaves the server holding the file", st != null && (string)st["text"] == disk);
            string afterDisk = disk.Replace("CODE", "CODE\r\n  RETURN");
            c.EnsureBufferSynced(doc, afterDisk);
            st = ServerState(c, doc);
            Ok("... and the next ranged change applies to the disk text", st != null && (string)st["text"] == afterDisk,
               st == null ? "no state" : Show((string)st["text"]));
            // The resync also re-bases the 'unchanged' check: the editor's PRE-disk text differs from what the server
            // now holds, so syncing it again must send it (it used to be skipped as unchanged).
            c.EnsureBufferSynced(doc, disk);
            c.EnsureBufferSynced(doc, afterDisk);
            st = ServerState(c, doc);
            Ok("... and syncing back and forth stays exact", st != null && (string)st["text"] == afterDisk);

            // Stop forgets the documents; a reused instance opens afresh on the new server.
            c.Stop();
            Ok("a reused instance restarts", c.Start(fake, "file:///" + dir.Replace("\\", "/"), "incr"));
            c.EnsureBufferSynced(doc, afterDisk + "! more\r\n");
            st = ServerState(c, doc);
            Ok("after Stop + Start the document is opened again, not changed blind", st != null && Int(st, "opens") == 1
               && (string)st["text"] == afterDisk + "! more\r\n", st == null ? "the new server never received the document" : "opens " + Int(st, "opens"));
            c.Stop();

            // ---- full-sync server ----
            c = Start(1);
            Ok("a server advertising FULL sync is not sent ranges", c != null && !c.UsesIncrementalSync);
            c.EnsureBufferSynced(doc, seed);
            text = Edit(c, doc, seed, 50, r);
            st = ServerState(c, doc);
            Ok("... it gets full text and ends up exact", st != null && (string)st["text"] == text && Int(st, "ranged") == 0 && Int(st, "full") > 0,
               "ranged " + Int(st, "ranged") + ", full " + Int(st, "full"));
            c.Stop();

            // ---- the switch ----
            LspClient.IncrementalSyncEnabled = false;
            c = Start(2);
            Ok("with IncrementalSyncEnabled off, an incremental server gets full text", c != null && !c.UsesIncrementalSync);
            c.EnsureBufferSynced(doc, seed);
            text = Edit(c, doc, seed, 50, r);
            st = ServerState(c, doc);
            Ok("... and ends up exact", st != null && (string)st["text"] == text && Int(st, "ranged") == 0);
            c.Stop();
            LspClient.IncrementalSyncEnabled = true;
        }
        catch (Exception ex) { fail++; Console.WriteLine("  [FAIL] harness threw: " + ex); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }

    static string Show(string s)
    {
        string v = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return "\"" + (v.Length > 120 ? v.Substring(0, 120) + "...\" (" + s.Length + " chars)" : v + "\"");
    }
}
