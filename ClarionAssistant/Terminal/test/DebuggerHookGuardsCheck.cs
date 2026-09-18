using System;
using ClarionAssistant.Services;

// Check for the two host-side guards on the debugger hooks (task f022fb4e), compiled against the REAL
// sources: Services/DocumentLineGuard.cs (a page-supplied line must exist in the document) and
// Services/ExecutionLineGate.cs (a marker clear is worth sending only to a page that shows a marker).
//
// Run:  powershell -ExecutionPolicy Bypass -File Terminal\test\run-debugger-host-checks.ps1
//
// Both guards live in MonacoClarionSourceEditor's call paths, which need the entire SharpDevelop IDE to
// load; they are separate classes precisely so the decisions can be executed here instead of being pinned
// by reading the source. Exit code 0 = all pass.

public static class Program
{
    static int _fail;
    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
        if (!ok) _fail++;
    }
    static void Section(string t) { Console.WriteLine("\n" + t); }

    public static int Main()
    {
        LineCounting();
        RunToCursorLine();
        ExecutionLineMarker();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : ("\n" + _fail + " FAILED"));
        return _fail == 0 ? 0 : 1;
    }

    // Monaco's own counting is the contract: the host must agree with the editor about where the document
    // ends, or the guard refuses the last line of every file that ends in a newline.
    static void LineCounting()
    {
        Section("DocumentLineGuard.CountLines (Monaco's counting)");
        Check("one line has no newline in it", DocumentLineGuard.CountLines("PROGRAM") == 1);
        Check("empty text is one empty line", DocumentLineGuard.CountLines("") == 1);
        Check("three lines, two separators", DocumentLineGuard.CountLines("a\nb\nc") == 3);
        Check("a trailing newline opens a last, empty line", DocumentLineGuard.CountLines("a\nb\n") == 3);
        Check("CRLF counts once per line, not twice", DocumentLineGuard.CountLines("a\r\nb\r\nc") == 3);
        Check("null text counts nothing", DocumentLineGuard.CountLines(null) == 0);
    }

    static void RunToCursorLine()
    {
        string mirrored = "line1\nline2\nline3";          // 3 lines, as the page mirrors them
        const int native = 10;                            // the native document, if there were no mirror

        Section("DocumentLineGuard.Contains — the page's mirror is the truth");
        Check("a line inside the mirrored buffer is accepted", DocumentLineGuard.Contains(2, mirrored, native));
        Check("the LAST mirrored line is accepted (off-by-one)", DocumentLineGuard.Contains(3, mirrored, native));
        Check("one line PAST the end of the mirrored buffer is refused", !DocumentLineGuard.Contains(4, mirrored, native));
        Check("a wildly out-of-range line is refused", !DocumentLineGuard.Contains(2000000000, mirrored, native));
        Check("the stale native count does not widen the mirror", !DocumentLineGuard.Contains(9, mirrored, native));
        Check("line 0 is refused", !DocumentLineGuard.Contains(0, mirrored, native));
        Check("a negative line is refused", !DocumentLineGuard.Contains(-5, mirrored, native));
        Check("a mirrored buffer GROWN past the native count still accepts its own last line",
              DocumentLineGuard.Contains(12, "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12", native));

        Section("DocumentLineGuard.Contains — no mirror yet (the native document)");
        Check("a line inside the native document is accepted", DocumentLineGuard.Contains(10, null, native));
        Check("one line past the native document is refused", !DocumentLineGuard.Contains(11, null, native));

        Section("DocumentLineGuard.Contains — nothing known (unknown is not empty)");
        Check("with no mirror and no native document, any positive line is accepted",
              DocumentLineGuard.Contains(500, null, 0));
        Check("...but line 0 is still refused", !DocumentLineGuard.Contains(0, null, 0));
    }

    static void ExecutionLineMarker()
    {
        Section("ExecutionLineGate — a tab activation with no execution line sends nothing");
        {
            var gate = new ExecutionLineGate();
            Check("a fresh page is not marked", !gate.PageIsMarked);
            Check("activation at line 0 on a never-marked page: nothing to send", !gate.WorthSending(0));
            gate.PageNowShows(0);   // the seed a setSource with executionLine 0 records
            Check("a second activation after a 0-seed still sends nothing", !gate.WorthSending(0));
            Check("a negative line is a clear too, and is equally not worth sending", !gate.WorthSending(-1));
        }

        Section("ExecutionLineGate — a marker is always worth sending");
        {
            var gate = new ExecutionLineGate();
            Check("setting a marker on an unmarked page is worth sending", gate.WorthSending(7));
            gate.PageNowShows(7);
            Check("the page is now marked", gate.PageIsMarked);
            Check("re-asserting the same line is still worth sending (it may have been lost)", gate.WorthSending(7));
            Check("moving the marker is worth sending", gate.WorthSending(8));
        }

        Section("ExecutionLineGate — clearing a real marker IS sent, once");
        {
            var gate = new ExecutionLineGate();
            gate.PageNowShows(7);
            Check("the clear that removes a painted marker is worth sending", gate.WorthSending(0));
            gate.PageNowShows(0);
            Check("the page is no longer marked", !gate.PageIsMarked);
            Check("a repeat clear is not worth sending", !gate.WorthSending(0));
        }

        Section("ExecutionLineGate — a reload that wiped the marker is not remembered as marked");
        {
            var gate = new ExecutionLineGate();
            gate.PageNowShows(12);                      // marker painted
            gate.PageNowShows(0);                       // reload: setSource carried executionLine 0
            Check("after a reload with no marker, activation sends nothing", !gate.WorthSending(0));
            gate.PageNowShows(12);                      // reload while the debugger is paused here
            Check("after a reload that DID carry a marker, a later clear is sent", gate.WorthSending(0));
        }
    }
}
