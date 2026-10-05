// EditorToolRouter.Test.cs - ticket fc420c30: with a CA Editor (Monaco overlay) up, the MCP editor tools must read and
// write ITS text and never the native document hidden under it; with no overlay they must behave exactly as before.
//
// Compiles the REAL Services\EditorToolRouter.cs (router + OverlayEditorOps), HostRequestBroker.cs and EditorTextOps.cs.
// The page is a fake channel with Monaco's rules where they matter (versionId, one undo step per batch, ranges clamped
// to the line, line breaks normalized to the model's CRLF, a save that writes the text to disk). The native editor is
// a stub that counts calls. A real "UI thread" runs the router's marshalled work, and the channel FAILS if it is ever
// asked from that thread (the deadlock rule).
//
// Run: tests\Run-Tests.ps1 (or EditorToolRouterTest.ps1). Exit 0 pass, 1 fail.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClarionAssistant.Services;

static class EditorToolRouterTest
{
    static int _pass, _fail;
    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name + (detail != null ? "  -- " + detail : "")); }
    }

    // ---- a real UI thread ----
    sealed class FakeUi : IUiDispatcher
    {
        readonly BlockingCollection<Action> _q = new BlockingCollection<Action>();
        public readonly Thread Thread;
        public volatile bool Blocked;
        public FakeUi()
        {
            Thread = new Thread(() => { foreach (var a in _q.GetConsumingEnumerable()) { while (Blocked) Thread.Sleep(20); a(); } }) { IsBackground = true };
            Thread.Start();
        }
        public bool HasUiThread { get { return true; } }
        public void BeginInvokeOnUi(Action action) { _q.Add(action); }
    }

    // ---- the page (Monaco's rules, in memory) ----
    sealed class FakePage : IEditorOverlayChannel
    {
        public string Path;
        public string Text;
        public long Version = 1;
        public bool Dirty;
        public int CurLine = 1, CurCol = 1;
        public int[] Sel;   // sl, sc, el, ec
        public bool Ready = true;
        public int StaleNext;            // refuse this many applyEdits as "stale" (simulates the developer typing)
        public bool ReadOnly;
        public bool Hang;                // simulates a page that does not answer
        public int UiThreadId;
        public int Requests, OnUiViolations;
        readonly Stack<string> _undo = new Stack<string>(), _redo = new Stack<string>();

        public string FilePath { get { return Path; } }
        public bool PageReady { get { return Ready; } }

        static string Crlf(string s) { return (s ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); }
        string[] Lines() { return Text.Split(new[] { "\r\n" }, StringSplitOptions.None); }
        int Off(int line, int col)   // Monaco validateRange + getOffsetAt
        {
            var ls = Lines();
            line = Math.Max(1, Math.Min(line, ls.Length));
            col = Math.Max(1, Math.Min(col, ls[line - 1].Length + 1));
            int off = 0;
            for (int i = 0; i < line - 1; i++) off += ls[i].Length + 2;
            return off + col - 1;
        }
        int[] Pos(int off)
        {
            int line = 1, start = 0;
            for (int i = 0; i < off; i++) if (Text[i] == '\n') { line++; start = i + 1; }
            return new[] { line, off - start + 1 };
        }

        public Dictionary<string, object> Request(string action, Dictionary<string, object> args, int timeoutMs)
        {
            Requests++;
            if (Thread.CurrentThread.ManagedThreadId == UiThreadId) { OnUiViolations++; throw new InvalidOperationException("asked on the UI thread"); }
            if (Hang) throw new TimeoutException("the CA Editor did not answer '" + action + "' within " + timeoutMs + " ms");
            args = args ?? new Dictionary<string, object>();
            switch (action)
            {
                case "getState":
                {
                    var d = new Dictionary<string, object>
                    {
                        { "versionId", Version }, { "dirty", Dirty }, { "lineCount", Lines().Length },
                        { "cursor", new Dictionary<string, object> { { "line", CurLine }, { "column", CurCol } } }
                    };
                    if (Sel != null)
                    {
                        int a = Off(Sel[0], Sel[1]), b = Off(Sel[2], Sel[3]);
                        d["selection"] = new Dictionary<string, object> { { "startLine", Sel[0] }, { "startCol", Sel[1] }, { "endLine", Sel[2] }, { "endCol", Sel[3] }, { "text", Text.Substring(a, b - a) } };
                    }
                    if (args.ContainsKey("withText") && (bool)args["withText"]) d["text"] = Text;
                    return d;
                }
                case "applyEdits":
                {
                    if (StaleNext > 0) { StaleNext--; Version++; throw new HostRequestBroker.RefusedException("stale"); }
                    if (Convert.ToInt64(args["expectedVersionId"]) != Version) throw new HostRequestBroker.RefusedException("stale");
                    if (ReadOnly) throw new HostRequestBroker.RefusedException("readOnly");
                    var edits = ((IEnumerable<Dictionary<string, object>>)args["edits"]).Select(e => new
                    {
                        A = Off(Convert.ToInt32(e["startLine"]), Convert.ToInt32(e["startCol"])),
                        B = Off(Convert.ToInt32(e["endLine"]), Convert.ToInt32(e["endCol"])),
                        T = Crlf((string)e["text"])
                    }).OrderByDescending(e => e.A).ToList();
                    _undo.Push(Text); _redo.Clear();
                    foreach (var e in edits) Text = Text.Substring(0, e.A) + e.T + Text.Substring(e.B);
                    if (args.ContainsKey("caretAtEnd") && (bool)args["caretAtEnd"] && edits.Count == 1)
                    { var p = Pos(edits[0].A + edits[0].T.Length); CurLine = p[0]; CurCol = p[1]; }
                    Version++; Dirty = true;
                    return new Dictionary<string, object>();
                }
                case "setSelection":
                    Sel = new[] { Convert.ToInt32(args["startLine"]), Convert.ToInt32(args["startCol"]), Convert.ToInt32(args["endLine"]), Convert.ToInt32(args["endCol"]) };
                    return new Dictionary<string, object>();
                case "reveal": CurLine = Convert.ToInt32(args["line"]); CurCol = 1; return new Dictionary<string, object>();
                case "undo":
                    if (_undo.Count == 0) return new Dictionary<string, object> { { "done", false } };
                    _redo.Push(Text); Text = _undo.Pop(); Version++; Dirty = true;
                    return new Dictionary<string, object> { { "done", true } };
                case "redo":
                    if (_redo.Count == 0) return new Dictionary<string, object> { { "done", false } };
                    _undo.Push(Text); Text = _redo.Pop(); Version++; Dirty = true;
                    return new Dictionary<string, object> { { "done", true } };
                case "save":
                    File.WriteAllText(Path, Text); Dirty = false;
                    return new Dictionary<string, object> { { "saved", true }, { "message", "Saved" } };
            }
            throw new HostRequestBroker.RefusedException("unknownAction:" + action);
        }
    }

    // ---- the native editor: counts every call ----
    static int NativeCalls;
    static object Native(string what) { NativeCalls++; return "NATIVE:" + what; }

    static object Run(EditorToolRouter r, string tool, Func<OverlayEditorOps, object> ov)
    {
        return r.Run(tool, () => Native(tool), ov);
    }

    static int Main()
    {
        var ui = new FakeUi();
        EditorToolRouter.UiThreadId = ui.Thread.ManagedThreadId;
        var router = new EditorToolRouter(() => ui);
        string tmp = Path.Combine(Path.GetTempPath(), "ca-router-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".clw");
        string seed = "  MEMBER('app')\r\nP PROCEDURE\r\n  CODE\r\n  x = 1\r\n  y = 2\r\n";
        File.WriteAllText(tmp, seed);
        var page = new FakePage { Path = tmp, Text = seed, UiThreadId = ui.Thread.ManagedThreadId, CurLine = 4, CurCol = 3 };
        EditorToolRouter.ActiveOverlayResolver = () => page;
        try
        {
            Console.WriteLine("\nWith a CA Editor up: writes land in Monaco's text, reads come from it, native untouched");
            NativeCalls = 0;
            var r = Run(router, "insert_text_at_cursor", ov => ov.InsertTextAtCursor("IF x = "));
            Check("insert_text_at_cursor inserts at the Monaco caret", r as string == "Text inserted successfully" && page.Text.Contains("  IF x = x = 1"), r + " | " + page.Text);
            Check("...and moves the caret to the end of the inserted text", page.CurLine == 4 && page.CurCol == 10, page.CurLine + ":" + page.CurCol);
            Check("...and marks the CA Editor dirty", page.Dirty);

            r = Run(router, "get_line_text", ov => ov.GetLineText(4));
            Check("get_line_text reads the edit back (Monaco's text, not the native document)", r as string == "  IF x = x = 1", r as string);

            var af = Run(router, "get_active_file", ov => ov.GetActiveFile()) as Dictionary<string, object>;
            Check("get_active_file gives the overlay's path and text", af != null && (string)af["path"] == tmp && ((string)af["content"]).Contains("IF x ="));

            r = Run(router, "replace_text", ov => ov.ReplaceText("y = 2", "y = 3"));
            Check("replace_text replaces in Monaco's text", r as string == "Text replaced successfully" && page.Text.Contains("y = 3"), r as string);
            r = Run(router, "replace_text", ov => ov.ReplaceText("nope", "x"));
            Check("replace_text on a miss gives the native error", r as string == "Error: Text not found in document", r as string);

            r = Run(router, "replace_range", ov => ov.ReplaceRange(5, 3, 5, 999, "z = 9"));
            Check("replace_range (end_col 999 clamped to the line, as natively)", r as string == "Range replaced successfully" && EditorTextOps.LineText(page.Text, 5) == "  z = 9", EditorTextOps.LineText(page.Text, 5));
            r = Run(router, "replace_range", ov => ov.ReplaceRange(99, 1, 99, 1, "x"));
            Check("replace_range past the last line gives the native error", r as string == "Error: Invalid line/column range", r as string);

            r = Run(router, "delete_range", ov => ov.DeleteRange(5, 1, 5, 3));
            Check("delete_range deletes in Monaco's text", r as string == "Text deleted" && EditorTextOps.LineText(page.Text, 5) == "z = 9", EditorTextOps.LineText(page.Text, 5));

            r = Run(router, "toggle_comment", ov => ov.ToggleComment(4, 5));
            Check("toggle_comment comments the lines (the native rule)", r as string == "Comment toggled on lines 4-5"
                && EditorTextOps.LineText(page.Text, 4) == "  !IF x = x = 1" && EditorTextOps.LineText(page.Text, 5) == "!z = 9", page.Text.Replace("\r\n", "|"));
            Run(router, "toggle_comment", ov => ov.ToggleComment(4, 5));
            Check("...and a second toggle uncomments them, line breaks intact", EditorTextOps.LineText(page.Text, 4) == "  IF x = x = 1"
                && EditorTextOps.LineText(page.Text, 5) == "z = 9" && !page.Text.Replace("\r\n", "").Contains("\r"), page.Text.Replace("\r\n", "|"));

            r = Run(router, "select_range", ov => ov.SelectRange(4, 3, 4, 7));
            var sel = Run(router, "get_selected_text", ov => ov.GetSelectedText());
            Check("select_range + get_selected_text use Monaco's selection", r as string == "Text selected" && sel as string == "IF x", sel as string);
            var word = Run(router, "get_word_under_cursor", ov => ov.GetWordUnderCursor());
            Check("get_word_under_cursor prefers the selection (native rule)", word as string == "IF x", word as string);
            page.Sel = null; page.CurLine = 2; page.CurCol = 2;
            word = Run(router, "get_word_under_cursor", ov => ov.GetWordUnderCursor());
            Check("get_word_under_cursor reads the word at Monaco's caret", word as string == "P", word as string);

            r = Run(router, "go_to_line", ov => ov.GoToLine(3));
            var cur = Run(router, "get_cursor_position", ov => ov.GetCursorPosition()) as Dictionary<string, object>;
            Check("go_to_line moves Monaco's caret; get_cursor_position reads it", r as string == "Moved to line 3" && cur != null && (int)cur["line"] == 3 && (int)cur["totalLines"] == 6);

            var found = Run(router, "find_in_file", ov => ov.FindInFile("x", false)) as List<int[]>;
            Check("find_in_file searches Monaco's text (native match rules)", found != null && found.Count == 2 && found[0][0] == 4 && found[0][1] == 6 && found[1][1] == 10, found == null ? "null" : string.Join(";", found.Select(f => f[0] + ":" + f[1])));
            r = Run(router, "get_lines_range", ov => ov.GetLinesRange(4, 5));
            Check("get_lines_range has the native format", r as string == "4\t  IF x = x = 1" + Environment.NewLine + "5\tz = 9" + Environment.NewLine, (r as string ?? "").Replace("\r\n", "|").Replace("\t", "<TAB>"));

            string before = page.Text;
            r = Run(router, "undo", ov => ov.Undo());
            Check("undo uses Monaco's stack", r as string == "Undo successful" && page.Text != before);
            r = Run(router, "redo", ov => ov.Redo());
            Check("redo uses Monaco's stack", r as string == "Redo successful" && page.Text == before);

            r = Run(router, "is_modified", ov => ov.IsModified());
            Check("is_modified reports the CA Editor's unsaved edits", r as string == "Yes - file has unsaved changes", r as string);

            Console.WriteLine("\nsave_file / close_file (the two pre-existing unsafe paths)");
            r = Run(router, "close_file", ov => ov.Close());
            Check("close_file on a DIRTY CA Editor is refused (no silent save, no discard)", (r as string ?? "").StartsWith("Error: unsaved changes in the CA Editor"), r as string);
            string edited = page.Text;
            r = Run(router, "save_file", ov => ov.Save());
            Check("save_file saves the CA Editor's text to disk (not the clean native shell)", r as string == "File saved" && File.ReadAllText(tmp) == edited, r as string);
            r = Run(router, "get_line_text", ov => ov.GetLineText(4));
            Check("...and the edit survives the save", r as string == "  IF x = x = 1", r as string);
            r = Run(router, "is_modified", ov => ov.IsModified());
            Check("...and is_modified then says saved", r as string == "No - file is saved", r as string);
            int nativeBeforeClose = NativeCalls;
            r = Run(router, "close_file", ov => ov.Close());
            Check("close_file on a CLEAN CA Editor closes through the native path", r as string == "NATIVE:close_file" && NativeCalls == nativeBeforeClose + 1, r as string);

            Check("the native document was never touched for an overlay operation (only the clean close)", NativeCalls == 1, "native calls: " + NativeCalls);
            Check("the page was never asked on the UI thread", page.OnUiViolations == 0, page.OnUiViolations + " violations");

            Console.WriteLine("\nFail closed: an overlay that cannot answer gives an error, never the native document");
            NativeCalls = 0;
            page.StaleNext = 1;
            r = Run(router, "insert_text_at_cursor", ov => ov.InsertTextAtCursor("A"));
            Check("one 'stale' (the developer typed) is recomputed and applied", r as string == "Text inserted successfully", r as string);
            page.StaleNext = 2;
            r = Run(router, "insert_text_at_cursor", ov => ov.InsertTextAtCursor("B"));
            Check("two in a row give an error", (r as string ?? "").StartsWith("Error: the CA Editor's text changed"), r as string);
            page.ReadOnly = true;
            r = Run(router, "insert_text_at_cursor", ov => ov.InsertTextAtCursor("C"));
            Check("a read-only file gives an error", (r as string ?? "").StartsWith("Error: the file is read-only"), r as string);
            page.ReadOnly = false;
            page.Ready = false;
            r = Run(router, "insert_text_at_cursor", ov => ov.InsertTextAtCursor("D"));
            Check("a page still loading gives an error", (r as string ?? "").StartsWith("Error: the CA Editor for"), r as string);
            page.Ready = true; page.Hang = true;
            r = Run(router, "get_line_text", ov => ov.GetLineText(1));
            Check("a page that does not answer gives an error", (r as string ?? "").Contains("did not answer"), r as string);
            page.Hang = false;
            Check("...and none of these fell back to the native document", NativeCalls == 0, "native calls: " + NativeCalls);

            Console.WriteLine("\nNo overlay: today's native behaviour, on the UI thread");
            EditorToolRouter.ActiveOverlayResolver = () => null;
            NativeCalls = 0;
            int ranOn = -1;
            r = router.Run("insert_text_at_cursor", () => { ranOn = Thread.CurrentThread.ManagedThreadId; return Native("insert"); }, ov => "OVERLAY");
            Check("no overlay → the native call, run ON the UI thread", r as string == "NATIVE:insert" && ranOn == ui.Thread.ManagedThreadId, "ranOn=" + ranOn);
            EditorToolRouter.ActiveOverlayResolver = null;
            r = Run(router, "get_line_text", ov => "OVERLAY");
            Check("no resolver (the standalone server) → native", r as string == "NATIVE:get_line_text", r as string);

            Console.WriteLine("\nA busy UI thread: bounded, never a hang");
            EditorToolRouter.ActiveOverlayResolver = () => page;
            ui.Blocked = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            r = Run(router, "insert_text_at_cursor", ov => ov.InsertTextAtCursor("E"));
            long ms = sw.ElapsedMilliseconds;
            ui.Blocked = false;
            Check("an unanswered UI thread gives an error after the resolve timeout", (r as string ?? "").StartsWith("Error: the IDE's UI thread did not answer")
                && ms >= EditorToolRouter.ResolveTimeoutMs - 200 && ms < EditorToolRouter.ResolveTimeoutMs + 2000, r + " in " + ms + " ms");

            Console.WriteLine("\nHostRequestBroker");
            int brokerUi = -1;
            var broker = new HostRequestBroker(json => { }, () => Thread.CurrentThread.ManagedThreadId == brokerUi);
            brokerUi = Thread.CurrentThread.ManagedThreadId;
            bool refused = false;
            try { broker.Request("getState", null, 100); } catch (InvalidOperationException) { refused = true; }
            Check("a request on the UI thread is refused (it would deadlock)", refused);
            brokerUi = -1;
            string posted = null;
            var b2 = new HostRequestBroker(json => posted = json, () => false);
            var t = new Thread(() => { Thread.Sleep(100); b2.Complete("{\"action\":\"hostReply\",\"reqId\":1,\"ok\":true,\"data\":{\"done\":true}}"); });
            t.Start();
            var reply = b2.Request("undo", null, 2000);
            Check("a reply completes the matching request", reply.ContainsKey("done") && (bool)reply["done"] && posted.Contains("\"hostRequest\"") && posted.Contains("\"reqId\":1"), posted);
            bool timedOut = false;
            try { b2.Request("getState", null, 150); } catch (TimeoutException) { timedOut = true; }
            Check("no reply → TimeoutException; nothing left pending", timedOut && b2.PendingCount == 0);
            Check("a late reply is dropped", !b2.Complete("{\"action\":\"hostReply\",\"reqId\":2,\"ok\":true,\"data\":{}}"));
            bool refusedCode = false;
            var t3 = new Thread(() => { Thread.Sleep(50); b2.Complete("{\"action\":\"hostReply\",\"reqId\":3,\"ok\":false,\"error\":\"stale\"}"); });
            t3.Start();
            try { b2.Request("applyEdits", null, 2000); } catch (HostRequestBroker.RefusedException ex) { refusedCode = ex.Code == "stale"; }
            Check("ok:false → RefusedException with the page's code", refusedCode);
        }
        finally { try { File.Delete(tmp); } catch { } }

        Console.WriteLine();
        Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + " - " + _pass + " passed, " + _fail + " failed");
        return _fail == 0 ? 0 : 1;
    }
}
