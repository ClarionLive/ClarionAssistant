using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

// 73bd1f03 fix (2): EmbedSlotText, the embed tools' text work over a buffer + slot ranges. The routed (CA Embeditor)
// answers must read exactly like the native ones, so every format below is AppTreeService's: «E:N» / «E:N/» tokens
// with noise stripping (GetEmbeditorSource), merged match windows (SearchEmbeditorSource, now this code),
// "(empty embed)" (GetEmbedContent), and the reindent + line-delta report (WriteEmbedContentByLine).
//
// Run:  tests\Run-Tests.ps1
static class EmbedSlotTextTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static string J(params string[] lines) { return string.Join("\r\n", lines); }
    static string Show(string s) { return s == null ? "(null)" : s.Replace("\r", "\\r").Replace("\n", "\\n"); }

    static int Main()
    {
        // 1 P PROCEDURE / 2 ! Start of / 3 (empty slot) / 4 blank / 5 !!! / 6-7 filled slot / 8 ! [Priority 5000]
        // 9-10 a slot of blanks / 11 CODE
        string buf = J("P PROCEDURE", "  ! Start of \"Init\"", "", "", "!!! generated", "    x = 1", "    y = 2",
                       "  ! [Priority 5000]", "   ", "", "  CODE");
        var ranges = new List<int[]> { new[] { 3, 3 }, new[] { 6, 7 }, new[] { 9, 10 } };

        // --- Annotate: get_embeditor_source's format ---
        string a = EmbedSlotText.Annotate(buf, ranges);
        string expected = J("1| P PROCEDURE", "\u00ABE:3/\u00BB", "\u00ABE:6\u00BB", "6|     x = 1", "7|     y = 2", "\u00AB/E:6\u00BB",
                            "\u00ABE:9/\u00BB", "11|   CODE") + "\r\n";
        Ok("annotate: empty slot «E:N/», filled «E:N»..«/E:N», noise and blank lines outside slots dropped, "
            + "every code line numbered with its BUFFER line, markers unnumbered",
            a == expected, Show(a));
        Ok("annotate: LF input gives the same answer", EmbedSlotText.Annotate(buf.Replace("\r\n", "\n"), ranges) == expected, null);
        Ok("annotate: no ranges = only the non-noise lines",
            EmbedSlotText.Annotate(buf, null) == J("1| P PROCEDURE", "6|     x = 1", "7|     y = 2", "11|   CODE") + "\r\n",
            Show(EmbedSlotText.Annotate(buf, null)));

        // --- Search: search_embeditor_source's format ---
        string s = EmbedSlotText.Search(buf, ranges, "x = 1", 1);
        Ok("search: header names the BUFFER lines the block spans; lines numbered; markers unnumbered",
            s == J("Matches for: x = 1", "--- buffer lines 6–7 ---", "\u00ABE:6\u00BB", "6|     x = 1", "7|     y = 2"), Show(s));
        Ok("search: no match", EmbedSlotText.Search(buf, ranges, "nothing-here", 2) == "No matches for: nothing-here", null);
        Ok("search: bad regex is an Error", EmbedSlotText.Search(buf, ranges, "(", 2).StartsWith("Error: invalid pattern"), null);
        string two = EmbedSlotText.Search(buf, ranges, "PROCEDURE|CODE", 0);
        Ok("search: separate windows stay separate", two.Contains("--- buffer lines 1–1 ---") && two.Contains("--- buffer lines 11–11 ---"), Show(two));
        Ok("search: the pattern sees the line's TEXT, not its number prefix (anchored patterns still work)",
            EmbedSlotText.Search(buf, ranges, "^\\s*CODE$", 0).Contains("11|   CODE") && EmbedSlotText.Search(buf, ranges, "^11", 0).StartsWith("No matches"), null);

        // --- Charlie's live finding (round 2): every printed number IS the buffer line of that text ---
        // The marker line made "! CLAUDE-R2" read as line 266 while the buffer (find_in_file, get_line_text) had it at 265.
        var big = new List<string>();
        for (int k = 1; k <= 300; k++) big.Add(k % 7 == 0 ? "  ! Start of \"x\"" : (k % 5 == 0 ? "" : "  L" + k));
        big[264] = "    ! CLAUDE-R2";   // buffer line 265
        string bigText = string.Join("\r\n", big);
        var bigRanges = new List<int[]> { new[] { 100, 100 }, new[] { 265, 266 }, new[] { 280, 282 } };
        bool allMatch = true; string firstBad = null;
        var bufLines = EmbedSlotText.Lines(bigText);
        foreach (var l in EmbedSlotText.AnnotateLines(bigText, bigRanges))
            if (l.Line > 0 && bufLines[l.Line - 1] != l.Text) { allMatch = false; firstBad = l.ToString(); break; }
        Ok("every numbered line's text equals the buffer at that line (300-line buffer, 3 slots, noise)", allMatch, firstBad);
        string r2 = EmbedSlotText.Search(bigText, bigRanges, "CLAUDE-R2", 1);
        Ok("the R2 line reads as 265, the line get_line_text/find_in_file use", r2.Contains("265|     ! CLAUDE-R2") && !r2.Contains("266|     ! CLAUDE-R2"), Show(r2));

        // --- SlotContent: get_embed_content ---
        Ok("content: a filled slot's lines", EmbedSlotText.SlotContent(buf, ranges, 6) == J("    x = 1", "    y = 2"),
            Show(EmbedSlotText.SlotContent(buf, ranges, 6)));
        Ok("content: an empty slot", EmbedSlotText.SlotContent(buf, ranges, 3) == "(empty embed)", null);
        Ok("content: a slot of blanks is empty", EmbedSlotText.SlotContent(buf, ranges, 9) == "(empty embed)", null);
        Ok("content: not a slot start -> the native tool's error",
            EmbedSlotText.SlotContent(buf, ranges, 7) == "Error: No embed point found at line 7. Use get_embeditor_source to get current line numbers.",
            EmbedSlotText.SlotContent(buf, ranges, 7));

        // --- PlanWrite: write_embed_content ---
        string err;
        var w = EmbedSlotText.PlanWrite(buf, ranges, 3, "IF a\r\n  b = 1\r\n\r\nEND\n", 5, true, out err);
        Ok("write: planned", w != null && err == null, err);
        if (w != null)
        {
            Ok("write: whole slot lines replaced (empty slot = line 3, col 1..1)",
                w.StartLine == 3 && w.EndLine == 3 && w.EndCol == 1 && w.SlotIndex == 0, w.StartLine + "-" + w.EndLine + " col " + w.EndCol);
            Ok("write: column 5 indents non-empty lines by 4, blank lines untouched, LF breaks, trailing newline kept",
                w.NewText == "    IF a\n      b = 1\n\n    END\n", Show(w.NewText));
            Ok("write: line delta = new lines - slot lines (5 - 1)", w.LineDelta == 4, w.LineDelta.ToString());
        }
        var w2 = EmbedSlotText.PlanWrite(buf, ranges, 6, "z = 3", 1, true, out err);
        Ok("write: a filled slot ends at its last line's end, column 1 = no indent, shrink -1",
            w2 != null && w2.StartLine == 6 && w2.EndLine == 7 && w2.EndCol == "    y = 2".Length + 1 && w2.NewText == "z = 3" && w2.LineDelta == -1,
            w2 == null ? err : w2.EndCol + " / " + Show(w2.NewText) + " / " + w2.LineDelta);
        var w3 = EmbedSlotText.PlanWrite(buf, ranges, 6, "  keep", 9, false, out err);
        Ok("write: reindent off = verbatim", w3 != null && w3.NewText == "  keep", w3 == null ? err : Show(w3.NewText));
        Ok("write: not a slot start -> the native tool's error, no plan",
            EmbedSlotText.PlanWrite(buf, ranges, 4, "x", 1, true, out err) == null && err.StartsWith("Error: No embed point found at line 4"), err);

        Ok("report: unchanged", EmbedSlotText.WriteReport(6, 0) ==
            "Wrote to embed at line 6.\r\nLine count unchanged — get_embeditor_source tokens remain valid.", null);
        Ok("report: changed", EmbedSlotText.WriteReport(3, 4) ==
            "Wrote to embed at line 3.\r\nLine count changed by +4 — call get_embeditor_source again before writing to embeds after line 3.", null);

        Console.WriteLine();
        Console.WriteLine("EmbedSlotText: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
