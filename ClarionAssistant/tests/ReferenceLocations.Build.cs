// Find All References: the reply ReferenceLocations.Build gives the Monaco page.
//
// The hosts send it the server's textDocument/references result, a predicate for "this path is the
// editor's own document", and a mapping from a 0-based server line in that document to the editor's
// 1-based line (0 = not in the editor's text). Checked here:
//   * a hit in the editor's own document: path null, the mapped line (CA Editor: identity + 1; CA
//     Embeditor: shifted back by the wrapper header, dropped in the header or past the editor's end),
//     the end line moved with it, 1-based columns;
//   * a hit in another file: its path and 1-based server line, and a SPARSE preview of that file (only
//     the lines within 20 of a hit filled, the rest empty, the file's line count kept);
//   * a hit in the editor's own file that the host does not treat as its own document (the CodeGraph
//     fallback: lines of the file on disk) is a file location with a preview like any other file;
//   * duplicates (also by path case) collapse, the reply stops at 1000 locations, a missing file gives
//     a location but no preview, and a single-object or empty result is handled.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

static class ReferenceLocationsBuildTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) Failures.Add(message);
    }

    static string Uri(string path) { return "file:///" + path.Replace('\\', '/'); }

    static Dictionary<string, object> Loc(string path, int line, int col, int endLine, int endCol)
    {
        return new Dictionary<string, object>
        {
            { "uri", Uri(path) },
            { "range", new Dictionary<string, object>
                {
                    { "start", new Dictionary<string, object> { { "line", line }, { "character", col } } },
                    { "end", new Dictionary<string, object> { { "line", endLine }, { "character", endCol } } }
                } }
        };
    }

    static Dictionary<string, object> Result(params Dictionary<string, object>[] locs)
    {
        var list = new ArrayList();
        foreach (var l in locs) list.Add(l);
        return new Dictionary<string, object> { { "result", list } };
    }

    static List<Dictionary<string, object>> Locations(Dictionary<string, object> reply)
    {
        return (List<Dictionary<string, object>>)reply["locations"];
    }

    static Dictionary<string, object> Files(Dictionary<string, object> reply)
    {
        return (Dictionary<string, object>)reply["files"];
    }

    static string Describe(Dictionary<string, object> l)
    {
        return "{path=" + (l["path"] ?? "null") + " line=" + l["line"] + " col=" + l["column"]
            + " endLine=" + l["endLine"] + " endCol=" + l["endColumn"] + "}";
    }

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ca-reflocs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string own = Path.Combine(dir, "Module001.clw");
            string other = Path.Combine(dir, "Other.clw");
            string missing = Path.Combine(dir, "Missing.clw");
            var ownLines = new string[60];
            for (int i = 0; i < ownLines.Length; i++) ownLines[i] = "own line " + (i + 1);
            File.WriteAllText(own, string.Join("\r\n", ownLines));
            var otherLines = new string[100];
            for (int i = 0; i < otherLines.Length; i++) otherLines[i] = "other line " + (i + 1);
            File.WriteAllText(other, string.Join("\r\n", otherLines));

            Func<string, bool> isOwn = p => string.Equals(p, own, StringComparison.OrdinalIgnoreCase);

            // ---- CA Editor: own document, identity + 1 ----
            {
                var reply = ReferenceLocations.Build(Result(Loc(own, 9, 4, 9, 12)), isOwn, l0 => l0 + 1);
                var locs = Locations(reply);
                Check(locs.Count == 1, "CA Editor: one location, got " + locs.Count);
                if (locs.Count == 1)
                {
                    var l = locs[0];
                    Check(l["path"] == null, "CA Editor: own-document hit has path null: " + Describe(l));
                    Check((int)l["line"] == 10 && (int)l["column"] == 5, "CA Editor: 1-based line 10 col 5: " + Describe(l));
                    Check((int)l["endLine"] == 10 && (int)l["endColumn"] == 13, "CA Editor: end 10:13: " + Describe(l));
                }
                Check(Files(reply).Count == 0, "CA Editor: no preview sent for the own document");
            }

            // ---- CA Embeditor: own document behind a 3-line wrapper header, 10 editor lines ----
            {
                int offset = 3, lineCount = 10;
                Func<int, int> embedLine1 = l0 => { int l1 = l0 + 1 - offset; return (l1 >= 1 && l1 <= lineCount) ? l1 : 0; };
                var reply = ReferenceLocations.Build(Result(
                    Loc(own, 2, 0, 2, 5),      // server line 3 = the header's last line: dropped
                    Loc(own, 5, 2, 6, 1),      // server line 6 = editor line 3, spanning two lines
                    Loc(own, 20, 0, 20, 3)),   // past the editor's 10 lines: dropped
                    isOwn, embedLine1);
                var locs = Locations(reply);
                Check(locs.Count == 1, "Embeditor: header hit and past-the-end hit dropped, got " + locs.Count);
                if (locs.Count == 1)
                {
                    var l = locs[0];
                    Check(l["path"] == null && (int)l["line"] == 3 && (int)l["column"] == 3, "Embeditor: editor line 3 col 3: " + Describe(l));
                    Check((int)l["endLine"] == 4 && (int)l["endColumn"] == 2, "Embeditor: end moved with the start (4:2): " + Describe(l));
                }
            }

            // ---- Another file: path, server line + 1, sparse preview ----
            {
                var reply = ReferenceLocations.Build(Result(Loc(other, 49, 6, 49, 10)), isOwn, l0 => l0 + 1);
                var locs = Locations(reply);
                Check(locs.Count == 1, "Other file: one location, got " + locs.Count);
                if (locs.Count == 1)
                {
                    var l = locs[0];
                    Check(string.Equals((string)l["path"], other, StringComparison.OrdinalIgnoreCase), "Other file: path kept: " + Describe(l));
                    Check((int)l["line"] == 50 && (int)l["column"] == 7, "Other file: line 50 col 7: " + Describe(l));
                }
                object prev;
                Check(Files(reply).TryGetValue(other, out prev) && prev is string, "Other file: a preview is sent");
                var pl = ((prev as string) ?? "").Split('\n');
                Check(pl.Length == 100, "Preview keeps the file's 100 lines, got " + pl.Length);
                if (pl.Length == 100)
                {
                    Check(pl[49] == "other line 50", "Preview: the hit line is filled: '" + pl[49] + "'");
                    Check(pl[29] == "other line 30" && pl[69] == "other line 70", "Preview: 20 lines either side are filled");
                    Check(pl[28] == "" && pl[70] == "" && pl[0] == "" && pl[99] == "", "Preview: lines further away are empty");
                }
            }

            // ---- The editor's own file, NOT treated as its own document (CodeGraph fallback: disk lines) ----
            {
                var reply = ReferenceLocations.Build(Result(Loc(own, 40, 0, 40, 4)), p => false, l0 => l0 + 1);
                var locs = Locations(reply);
                Check(locs.Count == 1 && string.Equals((string)locs[0]["path"], own, StringComparison.OrdinalIgnoreCase)
                    && (int)locs[0]["line"] == 41, "Fallback: own file as a file location at its disk line 41"
                    + (locs.Count == 1 ? ": " + Describe(locs[0]) : ""));
                object prev;
                Check(Files(reply).TryGetValue(own, out prev) && ((string)prev).Split('\n')[40] == "own line 41",
                    "Fallback: the preview is the file on disk");
            }

            // ---- Duplicates (same place, path case differs) collapse ----
            {
                var reply = ReferenceLocations.Build(Result(
                    Loc(other, 4, 1, 4, 3), Loc(other.ToUpperInvariant(), 4, 1, 4, 3), Loc(other, 4, 8, 4, 9)),
                    isOwn, l0 => l0 + 1);
                Check(Locations(reply).Count == 2, "Duplicates collapse (case-insensitive path), got " + Locations(reply).Count);
                Check(Files(reply).Count == 1, "Duplicates: one preview for the file, got " + Files(reply).Count);
            }

            // ---- Cap at 1000 ----
            {
                var many = new List<Dictionary<string, object>>();
                for (int i = 0; i < 1500; i++) many.Add(Loc(other, i % 100, i / 100, i % 100, i / 100 + 1));
                var reply = ReferenceLocations.Build(Result(many.ToArray()), isOwn, l0 => l0 + 1);
                Check(Locations(reply).Count == 1000, "Capped at 1000 locations, got " + Locations(reply).Count);
            }

            // ---- A file that cannot be read: the location stays, no preview ----
            {
                var reply = ReferenceLocations.Build(Result(Loc(missing, 3, 0, 3, 2)), isOwn, l0 => l0 + 1);
                Check(Locations(reply).Count == 1, "Missing file: location kept");
                Check(Files(reply).Count == 0, "Missing file: no preview");
            }

            // ---- Result shapes ----
            {
                var single = new Dictionary<string, object> { { "result", Loc(other, 1, 0, 1, 1) } };
                Check(Locations(ReferenceLocations.Build(single, isOwn, l0 => l0 + 1)).Count == 1, "A single-object result gives one location");
                Check(Locations(ReferenceLocations.Build(null, isOwn, l0 => l0 + 1)).Count == 0, "A null result gives none");
                var empty = new Dictionary<string, object> { { "result", null } };
                Check(Locations(ReferenceLocations.Build(empty, isOwn, l0 => l0 + 1)).Count == 0, "An empty result gives none");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        if (Failures.Count == 0)
        {
            Console.WriteLine("PASS - ReferenceLocations.Build: " + _assertions + " assertions");
            return 0;
        }
        foreach (var f in Failures) Console.WriteLine("FAIL - " + f);
        Console.WriteLine(Failures.Count + " of " + _assertions + " assertions failed");
        return 1;
    }
}
