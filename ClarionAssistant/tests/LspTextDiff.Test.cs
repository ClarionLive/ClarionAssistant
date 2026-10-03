using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using ClarionAssistant.Services;

// Harness for LspTextDiff — the ranged change LspClient sends instead of the whole buffer when the server supports
// incremental sync. Compiles the REAL LspTextDiff.cs on its own.
//
// WHY. CA re-sent the WHOLE buffer on every change (a full-text didChange); on a 2.5 MB generated module that is 2.5 MB
// of JSON per edit, which HoverBench measured as the bulk of the post-edit hover cost. The language server has always
// advertised TextDocumentSyncKind.Incremental, so one small ranged change does the same job.
//
// WHAT IS PINNED. A wrong range does not fail loudly: the server applies it and silently holds different text from the
// editor, so every later hover, completion and squiggle is computed against the wrong buffer. So:
//   * applying the change with the LSP's own rules (lines end at \r\n, \r or \n; characters are UTF-16 units) must
//     reproduce the new text exactly, over thousands of random edits on text full of CRLFs, lone CRs and surrogates;
//   * no range boundary may fall inside a \r\n or a surrogate pair (the server would place it differently);
//   * one small edit must give one small change, wherever it is in the document;
//   * a 2.5 MB document must diff fast.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class LspTextDiffTest
{
    static int pass = 0, fail = 0;
    static readonly List<string> firstFailures = new List<string>();

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    // ---- the LSP's side: apply a ranged change to the old text ----
    static List<int> LineStarts(string t)
    {
        var s = new List<int> { 0 };
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '\r') { if (i + 1 < t.Length && t[i + 1] == '\n') i++; s.Add(i + 1); }
            else if (t[i] == '\n') s.Add(i + 1);
        }
        return s;
    }

    // Offset of an LSP position; null when the position is not exactly representable (outside the text, past the
    // line's content, i.e. inside its line break, or splitting a surrogate pair).
    static int? OffsetOf(string t, List<int> starts, int line, int ch, out string why)
    {
        why = null;
        if (line < 0 || line >= starts.Count) { why = "line " + line + " out of range"; return null; }
        int start = starts[line];
        int next = line + 1 < starts.Count ? starts[line + 1] : t.Length;
        int contentEnd = next;
        if (contentEnd > start && t[contentEnd - 1] == '\n') contentEnd--;
        if (contentEnd > start && t[contentEnd - 1] == '\r') contentEnd--;
        if (ch < 0 || start + ch > contentEnd) { why = "character " + ch + " is past the content of line " + line; return null; }
        int off = start + ch;
        if (off > 0 && off < t.Length && char.IsHighSurrogate(t[off - 1]) && char.IsLowSurrogate(t[off])) { why = "splits a surrogate pair"; return null; }
        return off;
    }

    static string Apply(string oldText, LspTextChange c, out string why)
    {
        var starts = LineStarts(oldText);
        string w1, w2;
        int? s = OffsetOf(oldText, starts, c.StartLine, c.StartCharacter, out w1);
        int? e = OffsetOf(oldText, starts, c.EndLine, c.EndCharacter, out w2);
        why = w1 ?? w2;
        if (s == null || e == null) return null;
        if (e < s) { why = "end before start"; return null; }
        if (s != c.OldStart || e != c.OldEnd) { why = "offsets " + c.OldStart + ".." + c.OldEnd + " disagree with the position (" + s + ".." + e + ")"; return null; }
        return oldText.Substring(0, s.Value) + (c.Text ?? "") + oldText.Substring(e.Value);
    }

    static bool Check(string oldText, string newText, out string detail, int maxRemoved = -1, int maxInserted = -1)
    {
        detail = null;
        var c = LspTextDiff.Compute(oldText, newText);
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            if (c != null) detail = "a change for identical texts";
            return c == null;
        }
        if (c == null) { detail = "no change for different texts"; return false; }
        string why;
        string got = Apply(oldText, c, out why);
        if (got == null) { detail = why; return false; }
        if (!string.Equals(got, newText, StringComparison.Ordinal)) { detail = "applying the change does not give the new text"; return false; }
        if (maxRemoved >= 0 && c.OldEnd - c.OldStart > maxRemoved) { detail = "replaces " + (c.OldEnd - c.OldStart) + " chars, expected <= " + maxRemoved; return false; }
        if (maxInserted >= 0 && c.Text.Length > maxInserted) { detail = "inserts " + c.Text.Length + " chars, expected <= " + maxInserted; return false; }
        return true;
    }

    static void Case(string name, string a, string b, int maxRemoved = -1, int maxInserted = -1)
    {
        string d;
        Ok(name, Check(a, b, out d, maxRemoved, maxInserted), d);
    }

    static readonly string[] Atoms = { "a", "b", "X", " ", "\r\n", "\n", "\r", "\u00e9", "\U0001F600", "\t", "!" };

    static string RandomText(Random r, int atoms)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < atoms; i++) sb.Append(Atoms[r.Next(Atoms.Length)]);
        return sb.ToString();
    }

    static int Main()
    {
        Console.WriteLine("Fixed cases:");
        Case("identical texts -> no change", "abc\r\ndef", "abc\r\ndef");
        Case("empty -> text", "", "abc\r\n");
        Case("text -> empty", "abc\r\n", "");
        Case("insert one char mid-line", "IF x\r\n  y = 1\r\nEND\r\n", "IF x\r\n  y = 12\r\nEND\r\n", 0, 1);
        Case("delete one char mid-line", "IF x\r\n  y = 12\r\nEND\r\n", "IF x\r\n  y = 1\r\nEND\r\n", 1, 0);
        Case("append a line at the end", "a\r\nb\r\n", "a\r\nb\r\n! comment\r\n", 0, 11);
        Case("insert between \\r and \\n is widened, not split", "a\r\nb", "a\rX\nb", 2, 3);
        Case("CRLF -> LF", "a\r\nb", "a\nb", 2, 1);
        Case("LF -> CRLF", "a\nb", "a\r\nb", 1, 2);
        Case("lone CR line breaks count as lines", "a\rb\rc", "a\rb\rcd", 0, 1);
        Case("edit after an emoji keeps the pair whole", "x\U0001F600y", "x\U0001F600zy", 0, 1);
        Case("replace one emoji with another (same high surrogate)", "x\U0001F600y", "x\U0001F601y", 2, 2);
        Case("insert inside a run of identical chars", "aaaa\r\n", "aaaaa\r\n", 0, 1);

        Console.WriteLine("Random single edits on CRLF / CR / LF / surrogate text (5000):");
        var r = new Random(206);
        int bad = 0; string firstBad = null;
        for (int i = 0; i < 5000; i++)
        {
            string a = RandomText(r, r.Next(0, 60));
            int p = r.Next(0, a.Length + 1);
            if (p > 0 && p < a.Length && char.IsLowSurrogate(a[p])) p--;   // edit whole characters, as an editor does
            int del = r.Next(0, Math.Min(6, a.Length - p) + 1);
            if (p + del < a.Length && p + del > 0 && char.IsLowSurrogate(a[p + del])) del++;
            string ins = RandomText(r, r.Next(0, 4));
            string b = a.Substring(0, p) + ins + a.Substring(p + del);
            string d;
            // A boundary may widen by up to 2 units on each side for a \r\n or a surrogate pair.
            if (!Check(a, b, out d, del + 4, ins.Length + 4)) { bad++; if (firstBad == null) firstBad = Show(a) + " -> " + Show(b) + ": " + d; }
        }
        Ok("every random edit round-trips, splits nothing, and stays small", bad == 0, bad + " bad; first: " + firstBad);

        Console.WriteLine("Random unrelated pairs (1000):");
        bad = 0; firstBad = null;
        for (int i = 0; i < 1000; i++)
        {
            string a = RandomText(r, r.Next(0, 40)), b = RandomText(r, r.Next(0, 40)), d;
            if (!Check(a, b, out d)) { bad++; if (firstBad == null) firstBad = Show(a) + " -> " + Show(b) + ": " + d; }
        }
        Ok("any two texts round-trip", bad == 0, bad + " bad; first: " + firstBad);

        Console.WriteLine("A 2.5 MB generated-module-sized buffer:");
        var big = new StringBuilder();
        for (int i = 0; i < 61000; i++) big.Append("  LOC:Field").Append(i).Append(" = SomeProcedure(").Append(i).Append(")  ! generated\r\n");
        string before = big.ToString();
        string after = before.Substring(0, before.Length / 2) + "X" + before.Substring(before.Length / 2);
        var sw = Stopwatch.StartNew();
        var change = LspTextDiff.Compute(before, after);
        long ms = sw.ElapsedMilliseconds;
        string why;
        Ok("one char typed mid-way through " + before.Length / 1024 + " KB sends one char",
           change != null && change.Text == "X" && change.OldEnd == change.OldStart && Apply(before, change, out why) == after,
           change == null ? "null" : "sends " + change.Text.Length + " chars");
        Ok("... and diffs in under 100 ms", ms < 100, ms + " ms");

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail) + " assertions");
        return fail == 0 ? 0 : 1;
    }

    static string Show(string s)
    {
        return "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    }
}
