using System;
using System.Collections.Generic;
using System.Linq;
using ClarionAssistant.Services;

// Regression coverage for the Modern Embeditor's per-embed-slot structure balance pass
// (ModernEmbeditorDiagnostics.ComputeAsync, Passes 2 & 3), with the LSP pass stubbed out.
//
// First case family: the POST-CONDITION LOOP (GH #222 follow-up). Clarion lets a trailing
// 'UNTIL expr' or 'WHILE expr' line close a LOOP in place of END — SoftVelocity's own
// libsrc\win\abbrowse.clw uses it. The balance pass only closed on END / '.', so every such LOOP
// was reported "LOOP is not terminated with END or '.' in this embed slot." The pre-condition form
// ('LOOP WHILE x' / 'LOOP UNTIL x') starts with LOOP, is an opener, and still needs END.
//
// Run:  tests\Run-Tests.ps1
//
// Not in ClarionAssistant.csproj — it has its own Main(). See VsCodeSettingsImporter.SmokeTest.cs's
// header for why these harnesses live outside the project.
static class SlotBalance
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // Whole buffer is one editable slot.
    static List<Dictionary<string, object>> Diag(params string[] lines)
    {
        string buf = string.Join("\r\n", lines);
        var ranges = new List<int[]> { new[] { 1, lines.Length } };
        return ModernEmbeditorDiagnostics.ComputeSlotChecks(buf, ranges, "TestProc");
    }

    // 1c685f2e item 7 fixture: a procedure with one embed slot (lines 4-7) holding an unterminated LOOP and a
    // DO of an undefined routine, and a DO of a routine that is defined OUTSIDE the slot, further down.
    static readonly string[] SplitFixture = {
        "TestProc PROCEDURE",                 // 1
        "  CODE",                             // 2
        "  ! für the slot below",        // 3
        "  LOOP",                             // 4  slot start: never terminated
        "    DO NoSuchRoutine",               // 5
        "    DO RealRtn",                     // 6
        "    x# += 1",                        // 7  slot end
        "  RETURN",                           // 8
        "! für: a comment with a cp1252 byte before the ROUTINE", // 9
        "RealRtn ROUTINE",                    // 10
        "  x# = 0"                            // 11
    };
    static readonly List<int[]> SplitSlot = new List<int[]> { new[] { 4, 7 } };

    static string Show(List<Dictionary<string, object>> ms)
    {
        if (ms.Count == 0) return "(no markers)";
        return string.Join(" | ", ms.Select(m => "L" + m["line"] + " sev" + m["severity"] + ": " + m["message"]));
    }

    static int Main()
    {
        // --- the reported snippet: two post-condition LOOPs, one UNTIL, one WHILE ---
        {
            var ms = Diag(
                "LOOP",
                "  x# += 1",
                "UNTIL x# > 10",
                "LOOP",
                "  x# -= 1",
                "WHILE x# > 0");
            Ok("LOOP..UNTIL and LOOP..WHILE: no markers", ms.Count == 0, Show(ms));
        }

        // --- a block IF nested inside a LOOP..UNTIL: END closes the IF, UNTIL closes the LOOP ---
        {
            var ms = Diag(
                "LOOP",
                "  IF a = 1",
                "    b# += 1",
                "  END",
                "UNTIL b# > 5",
                "c# = 1");
            Ok("IF nested in LOOP..UNTIL: no markers", ms.Count == 0, Show(ms));
        }

        // --- a LOOP..WHILE nested inside an IF: WHILE closes the LOOP, END closes the IF ---
        {
            var ms = Diag(
                "IF a = 1",
                "  LOOP",
                "    x# -= 1",
                "  WHILE x# > 0",
                "END");
            Ok("LOOP..WHILE nested in IF: no markers", ms.Count == 0, Show(ms));
        }

        // --- lowercase keywords ---
        {
            var ms = Diag(
                "loop",
                "  x# += 1",
                "until x# > 10",
                "loop",
                "  x# -= 1",
                "while x# > 0");
            Ok("lowercase loop..until / loop..while: no markers", ms.Count == 0, Show(ms));
        }

        // --- pre-condition forms: LOOP WHILE / LOOP UNTIL are openers that still need END ---
        {
            var ms = Diag(
                "LOOP WHILE x# > 0",
                "  x# -= 1",
                "END",
                "LOOP UNTIL x# > 10",
                "  x# += 1",
                "END");
            Ok("LOOP WHILE..END / LOOP UNTIL..END: no markers", ms.Count == 0, Show(ms));
        }
        {
            var ms = Diag(
                "LOOP WHILE x# > 0",
                "  x# -= 1");
            Ok("unterminated LOOP WHILE is still flagged",
                ms.Count == 1 && (int)ms[0]["severity"] == 8 && (int)ms[0]["line"] == 1 &&
                ((string)ms[0]["message"]).StartsWith("LOOP is not terminated"), Show(ms));
        }
        {
            // The pre-condition LOOP's own WHILE is on the opener line — it must not close anything,
            // and a later UNTIL line does close it (post-condition closer on a pre-condition opener is
            // not something this check polices; it only balances).
            var ms = Diag(
                "LOOP UNTIL x# > 10",
                "  x# += 1");
            Ok("unterminated LOOP UNTIL is still flagged",
                ms.Count == 1 && (int)ms[0]["line"] == 1 &&
                ((string)ms[0]["message"]).StartsWith("LOOP is not terminated"), Show(ms));
        }

        // --- UNTIL/WHILE with an IF (not a LOOP) on top must NOT close the IF ---
        {
            // The UNTIL belongs to nothing (IF is innermost), so END closes the IF and the LOOP is
            // left unterminated. A naive "UNTIL always closes" would pop the IF, let END close the
            // LOOP, and report nothing.
            var ms = Diag(
                "LOOP",
                "  IF a = 1",
                "    b# += 1",
                "  UNTIL b# > 5",
                "END");
            Ok("UNTIL with IF on top does not close the IF (LOOP left unterminated)",
                ms.Count == 1 && (int)ms[0]["line"] == 1 &&
                ((string)ms[0]["message"]).StartsWith("LOOP is not terminated"), Show(ms));
        }
        {
            var ms = Diag(
                "IF a = 1",
                "  b# += 1",
                "WHILE b# > 5");
            Ok("WHILE with only an IF open does not close the IF",
                ms.Count == 1 && (int)ms[0]["line"] == 1 &&
                ((string)ms[0]["message"]).StartsWith("IF is not terminated"), Show(ms));
        }

        // --- UNTIL/WHILE with nothing open: silent (no new warning class) ---
        {
            var ms = Diag(
                "x# = 1",
                "UNTIL x# > 10",
                "WHILE x# > 0");
            Ok("stray UNTIL/WHILE with nothing open: silent", ms.Count == 0, Show(ms));
        }

        // --- existing behaviour kept: a stray END still warns ---
        {
            var ms = Diag("x# = 1", "END");
            Ok("stray END still warns", ms.Count == 1 && (int)ms[0]["severity"] == 4, Show(ms));
        }

        // --- a prefixed name spelled like the keyword is not a closer ---
        {
            // Closed by END, so a While:Count misread as the WHILE closer would pop the LOOP early and
            // leave that END stray ("END has no matching structure").
            var ms = Diag(
                "LOOP",
                "  While:Count += 1",
                "END");
            Ok("While:Count (prefixed name) is a statement, not a closer", ms.Count == 0, Show(ms));
        }

        // --- 1c685f2e 8.6: [diag-timing] names WHY the LSP pass did not run (skip=) ---
        {
            var one = new List<int[]> { new[] { 1, 1 } };
            Func<string, string, List<int[]>, string> skip = (file, buf, ranges) =>
            {
                var t = new ModernEmbeditorDiagnostics.Timing();
                ModernEmbeditorDiagnostics.ComputeAsync(file, buf, ranges, timing: t).GetAwaiter().GetResult();
                return t.Skip ?? "(null)";
            };
            SharedLspBridge.Reset();
            SharedLspBridge.Running = true;
            SharedLspBridge.FixedEntries.Add(new LspClient.DiagnosticEntry { Line = 0, Character = 0, EndLine = 0, EndCharacter = 3, Severity = 1, Message = "lsp says" });
            Ok("8.6 empty buffer -> skip=emptyBuffer", skip("x.clw", "", one) == "emptyBuffer", skip("x.clw", "", one));
            Ok("8.6 empty ranges -> skip=emptyRanges", skip("x.clw", "x = 1", new List<int[]>()) == "emptyRanges", skip("x.clw", "x = 1", new List<int[]>()));
            Ok("8.6 null ranges -> skip=emptyRanges", skip("x.clw", "x = 1", null) == "emptyRanges", skip("x.clw", "x = 1", null));
            Ok("8.6 no LSP file name -> skip=noFile", skip("", "x = 1", one) == "noFile", skip("", "x = 1", one));
            Ok("8.6 LSP ran -> skip is null", skip("x.clw", "x = 1", one) == "(null)", skip("x.clw", "x = 1", one));
            SharedLspBridge.Running = false;
            Ok("8.6 server not running -> skip=lspDown", skip("x.clw", "x = 1", one) == "lspDown", skip("x.clw", "x = 1", one));
            SharedLspBridge.Reset();
        }

        // --- 1c685f2e item 7: the slot checks are their own pure function; the LSP pass carries LSP markers only ---
        {
            string buf = string.Join("\r\n", SplitFixture);

            SharedLspBridge.Reset();
            SharedLspBridge.Running = true;   // even with a server "running", the slot pass must not touch it
            var slot = ModernEmbeditorDiagnostics.ComputeSlotChecks(buf, SplitSlot, "TestProc");
            Ok("7.3 ComputeSlotChecks never touches SharedLspBridge (every stub counter 0)",
                SharedLspBridge.TotalCalls == 0,
                "IsRunning=" + SharedLspBridge.IsRunningCalls + " sync=" + SharedLspBridge.SyncCalls + " wait=" + SharedLspBridge.WaitCalls + " cached=" + SharedLspBridge.CachedCalls);
            Ok("7.4 DO NoSuchRoutine is flagged",
                slot.Any(m => (int)m["line"] == 5 && ((string)m["message"]).Contains("'NoSuchRoutine' is not defined")), Show(slot));
            Ok("7.4 DO RealRtn (defined outside the slot, later in the buffer) is not",
                !slot.Any(m => ((string)m["message"]).Contains("RealRtn")), Show(slot));
            Ok("7.4 the unterminated LOOP in the slot is flagged",
                slot.Any(m => (int)m["line"] == 4 && ((string)m["message"]).StartsWith("LOOP is not terminated")), Show(slot));

            // 7.5: the same fixture decoded from a UTF-8-with-BOM file and from a cp1252 file.
            var cp1252 = System.Text.Encoding.GetEncoding(1252);
            byte[] ansiBytes = cp1252.GetBytes(buf);
            byte[] utf8Bytes = new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(buf)).ToArray();
            string fromAnsi = cp1252.GetString(ansiBytes);
            string fromUtf8;
            using (var r = new System.IO.StreamReader(new System.IO.MemoryStream(utf8Bytes), System.Text.Encoding.UTF8, true)) fromUtf8 = r.ReadToEnd();
            Ok("7.5 seeded: the cp1252 bytes really hold 0xFC (the check could tell the encodings apart)",
                ansiBytes.Contains((byte)0xFC) && !utf8Bytes.Skip(3).Contains((byte)0xFC));
            Ok("7.5 the same markers from the BOM file and the cp1252 file",
                Show(ModernEmbeditorDiagnostics.ComputeSlotChecks(fromUtf8, SplitSlot, "TestProc")) ==
                Show(ModernEmbeditorDiagnostics.ComputeSlotChecks(fromAnsi, SplitSlot, "TestProc")) && Show(slot) ==
                Show(ModernEmbeditorDiagnostics.ComputeSlotChecks(fromAnsi, SplitSlot, "TestProc")));

            // 7.2: the LSP pass over the same buffer returns ONLY the server's entry, no slot markers.
            SharedLspBridge.Reset();
            SharedLspBridge.Running = true;
            SharedLspBridge.FixedEntries.Add(new LspClient.DiagnosticEntry { Line = 5, Character = 4, EndLine = 5, EndCharacter = 9, Severity = 2, Message = "from the server" });
            var lsp = ModernEmbeditorDiagnostics.ComputeAsync("x.clw", buf, SplitSlot).GetAwaiter().GetResult();
            Ok("7.2 ComputeAsync carries only the LSP entry (no slot markers)",
                lsp.Count == 1 && (string)lsp[0]["message"] == "from the server" && (int)lsp[0]["line"] == 6, Show(lsp));
            SharedLspBridge.Reset();

            // 7.4b: a real ABC shape. The procedure name also appears as a MAP prototype, and the local
            // ThisWindow CLASS holds column-1 method prototypes (Coder-A found master's scope scan taking
            // those for the procedure header). The routine set must still hold the real routine and only it.
            string abc = string.Join("\r\n", new[] {
                "  MEMBER('app.clw')",                          // 1
                "  MAP",                                        // 2
                "Browse PROCEDURE",                             // 3  the MAP prototype of the same name
                "  END",                                        // 4
                "Browse PROCEDURE",                             // 5  the real header
                "ThisWindow           CLASS(WindowManager)",    // 6
                "Init PROCEDURE(),BYTE,PROC,DERIVED",           // 7  column-1 method prototype
                "Kill PROCEDURE(),BYTE,PROC,DERIVED",           // 8
                "                     END",                     // 9
                "  CODE",                                       // 10
                "  GlobalResponse = ThisWindow.Run()",          // 11
                "  DO RefreshTotals",                           // 12 slot
                "  DO NotThere",                                // 13 slot
                "RefreshTotals ROUTINE",                        // 14
                "  x# = 1",                                     // 15
                "ThisWindow.Init PROCEDURE",                    // 16
                "  CODE",                                       // 17
                "  RETURN ReturnValue" });                      // 18
            var abcMarkers = ModernEmbeditorDiagnostics.ComputeSlotChecks(abc, new List<int[]> { new[] { 12, 13 } }, "Browse");
            Ok("7.4b ABC shape: DO NotThere flagged, DO RefreshTotals not",
                abcMarkers.Count == 1 && (int)abcMarkers[0]["line"] == 13 && ((string)abcMarkers[0]["message"]).Contains("NotThere"), Show(abcMarkers));

            // 7.6: budget on a module-sized buffer (86k lines) with one 30-line slot.
            var big = new System.Text.StringBuilder();
            big.Append("BigProc PROCEDURE\r\n  CODE\r\n");
            for (int i = 0; i < 30; i++) big.Append(i == 5 ? "  DO NoSuchRoutine\r\n" : "  x# += 1\r\n");
            int n = 32;
            while (n < 86000) { big.Append(n % 500 == 0 ? "Rtn" + n + " ROUTINE\r\n" : "    IF LOC:Count > 0 THEN DO Rtn500. ! padding\r\n"); n++; }
            string bigBuf = big.ToString();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bigMarkers = ModernEmbeditorDiagnostics.ComputeSlotChecks(bigBuf, new List<int[]> { new[] { 3, 32 } }, "BigProc");
            long ms = sw.ElapsedMilliseconds;
            Console.WriteLine("  (7.6: 86,000-line buffer, one 30-line slot: " + ms + " ms, " + bigMarkers.Count + " marker(s))");
            Ok("7.6 86k-line buffer, one slot: under 1000 ms and the DO is flagged",
                ms < 1000 && bigMarkers.Any(m => ((string)m["message"]).Contains("NoSuchRoutine")), ms + " ms; " + Show(bigMarkers));
        }

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed.");
        return fail == 0 ? 0 : 1;
    }
}
