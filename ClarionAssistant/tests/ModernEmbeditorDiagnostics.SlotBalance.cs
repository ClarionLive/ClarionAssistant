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
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // Whole buffer is one editable slot.
    static List<Dictionary<string, object>> Diag(params string[] lines)
    {
        string buf = string.Join("\r\n", lines);
        var ranges = new List<int[]> { new[] { 1, lines.Length } };
        return ModernEmbeditorDiagnostics.ComputeAsync(null, buf, ranges, "TestProc").GetAwaiter().GetResult();
    }

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
                ModernEmbeditorDiagnostics.ComputeAsync(file, buf, ranges, "TestProc", timing: t).GetAwaiter().GetResult();
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

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed.");
        return fail == 0 ? 0 : 1;
    }
}
