using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

// Regression coverage for FindStructureAtLine's structure-opener detection — a Clarion keyword
// (WINDOW/REPORT/GROUP/...) used as a plain label is a legal identifier, not a reserved word, and
// gets misclassified as opening that structure. Same bug family as PR #132/#136/#150/#156
// (ModernEmbeditorDiagnostics.cs's StructOpen/ToolbarOpen/NestedBandOpen/GroupOpen/RecordOpen), but
// in the Ctrl+D structure-designer scan (ClarionAppDataReader.cs) rather than the diagnostics pass —
// confirmed a genuinely separate code path, not previously covered by those fixes.
//
// A Clarion label always starts at column 0, at any nesting depth — every fixture below follows that
// rule for structure openers (labelled or not) and only indents body/continuation lines, matching
// real APP-generated source (e.g. a REPORT's own DETAIL bands sit at column 0, not indented under
// the REPORT that contains them).
//
// Run:  tests\Run-Tests.ps1
//
// Not in ClarionAssistant.csproj — it has its own Main(). See the note in
// VsCodeSettingsImporter.SmokeTest.cs's header for why these harnesses live outside the project.
static class StructureScan
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static string Src(params string[] lines) { return string.Join("\r\n", lines); }

    static ClarionAppDataReader.StructureHit Hit(string src, int caretLine)
    {
        return ClarionAppDataReader.FindStructureAtLine(src, caretLine);
    }

    static int Main()
    {
        // --- the reported bug: a keyword used as a plain label, in the label column ---

        // "report STRING(4096)" — a local variable literally named after the REPORT keyword — must
        // NOT open a REPORT. REPORT always requires a label, so it can only ever be the SECOND token
        // on its line; one sitting in the label column IS the label.
        {
            string src = Src(
                "MyProc PROCEDURE()",
                "report       STRING(4096)",
                "i            LONG",
                "  CODE",
                "Window WINDOW('Test'),AT(0,0,100,50)",
                "      BUTTON('Close'),AT(10,10,30,14),USE(?Button1)",
                "END");
            var hit = Hit(src, 6); // inside the window body
            Ok("report-as-label: caret inside real WINDOW finds WINDOW, not the phantom REPORT",
                hit.Found && hit.Type == "WINDOW", hit.Found ? hit.Type : "not found");
            Ok("report-as-label: WINDOW range starts at its own opener",
                hit.StartLine == 5, "StartLine=" + hit.StartLine);
            Ok("report-as-label: WINDOW range ends at its own END, not a runaway later one",
                hit.EndLine == 7, "EndLine=" + hit.EndLine);
        }

        // Same shape for the other "always needs a label" keywords: WINDOW, QUEUE, CLASS, VIEW,
        // APPLICATION, FILE, INTERFACE — a local named after any of them must not open a structure.
        foreach (var kw in new[] { "window", "queue", "class", "view", "application", "file", "interface" })
        {
            string src = Src(
                "MyProc PROCEDURE()",
                kw + "       STRING(64)",
                "  CODE",
                "RealWindow WINDOW('Test'),AT(0,0,100,50)",
                "      BUTTON('Close'),AT(10,10,30,14),USE(?Button1)",
                "END");
            var hit = Hit(src, 5);
            Ok(kw + "-as-label: caret inside real WINDOW finds WINDOW",
                hit.Found && hit.Type == "WINDOW" && hit.StartLine == 4,
                hit.Found ? (hit.Type + " Start=" + hit.StartLine) : "not found");
        }

        // --- second trigger found during verification: keyword inside a control's string attribute ---

        // A wrapped BUTTON(...),TIP('...report...' & | splits so "rest" (everything after the first
        // whitespace run) starts with the word from inside the string literal. The phantom REPORT this
        // opens is NESTED under the real WINDOW, so a naive Type/StartLine check alone doesn't catch it
        // (the outer WINDOW is still what's reported) — the tell is the phantom's END never getting
        // popped, so the window's own EndLine comes out wrong. A trailing line after the real END makes
        // that observable: if the phantom swallows the real END, the scan runs on and reports the
        // TRAILING line as EndLine instead — this fixture failed exactly that way before the fix
        // (EndLine 8, not 7) despite Type/StartLine already looking correct.
        {
            string src = Src(
                "MyProc PROCEDURE()",
                "  CODE",
                "Window WINDOW('Test'),AT(0,0,100,50)",
                "      BUTTON('&Report'),AT(10,10,30,14),USE(?Button2),TIP('Write report ' & |",
                "            'to file.'),LEFT",
                "      BUTTON('Close'),AT(50,10,30,14),USE(?Button1)",
                "END",
                "END");
            var hit = Hit(src, 4);
            Ok("keyword-in-string-attribute: caret on the TIP line still finds the enclosing WINDOW",
                hit.Found && hit.Type == "WINDOW" && hit.StartLine == 3 && hit.EndLine == 7,
                hit.Found ? (hit.Type + " Start=" + hit.StartLine + " End=" + hit.EndLine) : "not found");
        }

        // --- the biggest real-world trigger: ABC's own generated "Report:Save::<field>" local ---

        // Every ABC-template report procedure declares "Report:Save::<key> LIKE(<key>)". \b matches at
        // the colon, so REPORT was misdetected here in effectively every generated report module.
        {
            string src = Src(
                "MyReportProc2 PROCEDURE",
                "Report:Save::SomeKey LIKE(SomeKey)",
                "  CODE",
                "StatusWindow WINDOW('Status...'),AT(0,0,100,50)",
                "      BUTTON('Cancel'),AT(10,10,30,14),USE(?Button3)",
                "END");
            var hit = Hit(src, 4);
            Ok("Report:Save:: ABC local: caret inside StatusWindow finds WINDOW, not a phantom REPORT",
                hit.Found && hit.Type == "WINDOW" && hit.StartLine == 4,
                hit.Found ? (hit.Type + " Start=" + hit.StartLine) : "not found");
        }

        // --- real REPORT structures still detected correctly, including band nesting ---

        // Band openers (HEADER/DETAIL/FOOTER) sit at column 0 too — matching real APP-generated
        // report source, where a REPORT's own bands are never indented under the REPORT line that
        // contains them.
        {
            string src = Src(
                "MyReportProc PROCEDURE",
                "  CODE",
                "Report REPORT,AT(0,0,7698,10063),PRE(RPT)",
                "HEADER,AT(0,0,100,50),USE(?unnamed)",
                "        STRING('Title'),AT(0,0,100,20),USE(?Title)",
                "END",
                "DetailBand   DETAIL,AT(,,,100),USE(?DetailBand)",
                "        STRING(@s32),AT(0,0,100,20),USE(SOME:Field)",
                "END",
                "FOOTER,AT(0,0,100,50)",
                "END",
                "END");
            Ok("real REPORT: caret inside labelled anonymous HEADER band finds the enclosing REPORT",
                Hit(src, 5).Found && Hit(src, 5).Type == "REPORT" && Hit(src, 5).StartLine == 3,
                null);
            Ok("real REPORT: caret inside the named DETAIL band finds the enclosing REPORT",
                Hit(src, 8).Found && Hit(src, 8).Type == "REPORT" && Hit(src, 8).StartLine == 3,
                null);
            Ok("real REPORT: caret inside the anonymous FOOTER band finds the enclosing REPORT",
                Hit(src, 10).Found && Hit(src, 10).Type == "REPORT" && Hit(src, 10).StartLine == 3,
                null);
            var closed = Hit(src, 12);
            Ok("real REPORT: caret on the REPORT's own closing END still counts as inside",
                closed.Found && closed.Type == "REPORT" && closed.EndLine == 12,
                closed.Found ? ("EndLine=" + closed.EndLine) : "not found");
        }

        // --- non-regression: keywords that are legitimately written bare must still open ---

        // These are the confirmed regression cases from the prior fixes in this bug family
        // (MAP/MODULE from PR #132's correction; GROUP/RECORD from PR #156) — must keep working.
        {
            string src = Src(
                "MyProc PROCEDURE()",
                "  MAP",
                "    SomeProto  PROCEDURE()",
                "  END",
                "  CODE",
                "Window WINDOW('Test'),AT(0,0,100,50)",
                "END");
            var hit = Hit(src, 6);
            // A desynced MAP (mistaken for a bare label) would leave its END un-popped, which would
            // make the window's own END get swallowed by that leftover frame instead of closing the
            // window at its own line — so a correct EndLine here IS the regression check.
            Ok("MAP ahead of a window doesn't desync the window's own range",
                hit.Found && hit.Type == "WINDOW" && hit.StartLine == 6 && hit.EndLine == 7,
                hit.Found ? (hit.Type + " Start=" + hit.StartLine + " End=" + hit.EndLine) : "not found");
        }

        {
            string src = Src(
                "MyProc PROCEDURE()",
                "SomeGroup GROUP,PRE(SG)",
                "Field1     LONG",
                "         END",
                "AnonRec  RECORD,PRE()",
                "Field2     LONG",
                "         END",
                "  CODE",
                "Window WINDOW('Test'),AT(0,0,100,50)",
                "END");
            var hit = Hit(src, 9);
            Ok("GROUP/RECORD desync regression: a bare GROUP/RECORD ahead of the window doesn't break its range",
                hit.Found && hit.Type == "WINDOW" && hit.StartLine == 9 && hit.EndLine == 10,
                hit.Found ? (hit.Type + " Start=" + hit.StartLine + " End=" + hit.EndLine) : "not found");
        }

        // A bare option/toolbar inside a window body must still open (anonymous and labelled forms).
        {
            string src = Src(
                "MyProc PROCEDURE()",
                "  CODE",
                "Window WINDOW('Test'),AT(0,0,100,50)",
                "OPTION,AT(0,0,50,10),USE(?Opt1)",
                "        RADIO('A'),AT(0,0,20,10),USE(?Radio1)",
                "END",
                "Toolbar TOOLBAR,USE(?Toolbar1)",
                "END",
                "END");
            Ok("bare OPTION band inside a real window doesn't desync the window's own range",
                Hit(src, 5).Found && Hit(src, 5).Type == "WINDOW" && Hit(src, 5).EndLine == 9,
                null);
            Ok("labelled bare TOOLBAR inside a real window doesn't desync the window's own range",
                Hit(src, 7).Found && Hit(src, 7).Type == "WINDOW" && Hit(src, 7).EndLine == 9,
                null);
        }

        // --- non-regression: a plain local named after a bare-capable keyword is still just a local ---

        // "option LONG(0)" (no attributes, no trailing comma) — confirmed real false positive that
        // motivated PR #150 in the diagnostics pass; this reader must not repeat it.
        {
            string src = Src(
                "MyProc PROCEDURE()",
                "option       LONG(0)",
                "  CODE",
                "Window WINDOW('Test'),AT(0,0,100,50)",
                "END");
            var hit = Hit(src, 4);
            Ok("option-as-label: caret inside real WINDOW finds WINDOW, not a phantom OPTION",
                hit.Found && hit.Type == "WINDOW" && hit.StartLine == 4,
                hit.Found ? (hit.Type + " Start=" + hit.StartLine) : "not found");
        }

        // --- no structure at all: must refuse, not guess ---

        {
            string src = Src(
                "MyProc PROCEDURE()",
                "i LONG",
                "  CODE",
                "  i = 1",
                "  RETURN");
            var hit = Hit(src, 4);
            Ok("no enclosing structure: Found is false", !hit.Found, hit.Found ? hit.Type : null);
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
