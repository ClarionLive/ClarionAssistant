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
//   * the server's LINE TABLE must stay right, not just its text. The Clarion server keeps documents as
//     vscode-languageserver-textdocument TextDocuments, whose update() PATCHES the line-offset table from the change
//     alone instead of rebuilding it. A change that completes a \r\n across its boundary ("a\n" + "\r" inserted at 0:1)
//     leaves the right text, "a\r\n", but lineCount 3 and a wrong positionAt. TextDocumentPort below is a faithful port
//     of that update (1.0.14); every change is applied with it and the result compared with a document built fresh from
//     the new text. Rebuilding the line table after each change (as this harness first did) hides exactly this, and
//     LspTextDiff's new-text boundary guards could then be deleted with every test green;
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
        string text = oldText.Substring(0, s.Value) + (c.Text ?? "") + oldText.Substring(e.Value);

        // ... and as the real server applies it: TextDocument.update, with its incremental line-table patch.
        var doc = new TextDocumentPort(oldText);
        doc.Update(c.StartLine, c.StartCharacter, c.EndLine, c.EndCharacter, c.Text ?? "");
        if (!string.Equals(doc.Text, text, StringComparison.Ordinal)) { why = "TextDocument.update gives different text"; return null; }
        why = doc.DriftFromFresh();
        return why == null ? text : null;
    }

    /// <summary>
    /// Port of vscode-languageserver-textdocument 1.0.14's FullTextDocument (lib/umd/main.js; MIT, Copyright (c)
    /// Microsoft Corporation): the document the Clarion language server keeps. Kept line for line with update(),
    /// offsetAt(), positionAt() and computeLineOffsets(), because the point is to fail exactly where the server would:
    /// update() patches the line-offset table from the change text alone. The same port is in
    /// fixtures\incremental-sync\fake-lsp.js (which uses the real package when node can resolve it).
    /// </summary>
    sealed class TextDocumentPort
    {
        string content;
        List<int> lineOffsets;

        public TextDocumentPort(string text) { content = text; }
        public string Text { get { return content; } }
        public int LineCount { get { return GetLineOffsets().Count; } }

        static bool IsEOL(char ch) { return ch == '\r' || ch == '\n'; }

        static List<int> ComputeLineOffsets(string text, bool isAtLineStart, int textOffset)
        {
            var result = isAtLineStart ? new List<int> { textOffset } : new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (IsEOL(ch))
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    result.Add(textOffset + i + 1);
                }
            }
            return result;
        }

        List<int> GetLineOffsets()
        {
            if (lineOffsets == null) lineOffsets = ComputeLineOffsets(content, true, 0);
            return lineOffsets;
        }

        public void Update(int sl, int sc, int el, int ec, string text)
        {
            if (sl > el || (sl == el && sc > ec)) { int t = sl; sl = el; el = t; t = sc; sc = ec; ec = t; }   // getWellformedRange
            int startOffset = OffsetAt(sl, sc), endOffset = OffsetAt(el, ec);
            content = content.Substring(0, startOffset) + text + content.Substring(endOffset);
            int startLine = Math.Max(sl, 0), endLine = Math.Max(el, 0);
            var offsets = GetLineOffsets();
            var added = ComputeLineOffsets(text, false, startOffset);
            if (endLine - startLine == added.Count)
            {
                for (int i = 0; i < added.Count; i++) offsets[i + startLine + 1] = added[i];
            }
            else
            {
                offsets.RemoveRange(startLine + 1, endLine - startLine);   // splice(startLine + 1, endLine - startLine, ...added)
                offsets.InsertRange(startLine + 1, added);
            }
            int diff = text.Length - (endOffset - startOffset);
            if (diff != 0)
                for (int i = startLine + 1 + added.Count; i < offsets.Count; i++) offsets[i] += diff;
        }

        public int OffsetAt(int line, int character)
        {
            var offsets = GetLineOffsets();
            if (line >= offsets.Count) return content.Length;
            if (line < 0) return 0;
            int lineOffset = offsets[line];
            if (character <= 0) return lineOffset;
            int nextLineOffset = line + 1 < offsets.Count ? offsets[line + 1] : content.Length;
            int offset = Math.Min(lineOffset + character, nextLineOffset);
            return EnsureBeforeEOL(offset, lineOffset);
        }

        public void PositionAt(int offset, out int line, out int character)
        {
            offset = Math.Max(Math.Min(offset, content.Length), 0);
            var offsets = GetLineOffsets();
            int low = 0, high = offsets.Count;
            if (high == 0) { line = 0; character = offset; return; }
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (offsets[mid] > offset) high = mid; else low = mid + 1;
            }
            line = low - 1;
            offset = EnsureBeforeEOL(offset, offsets[line]);
            character = offset - offsets[line];
        }

        int EnsureBeforeEOL(int offset, int lineOffset)
        {
            while (offset > lineOffset && IsEOL(content[offset - 1])) offset--;
            return offset;
        }

        /// <summary>Null when this (patched) document answers exactly as one created fresh from its text: same
        /// lineCount, same positionAt at every offset, same offsetAt at every position. Else what differs.</summary>
        public string DriftFromFresh()
        {
            var fresh = new TextDocumentPort(content);
            if (LineCount != fresh.LineCount) return "lineCount " + LineCount + ", fresh document " + fresh.LineCount;
            // Every offset on small texts; on big ones the tables decide it (positionAt/offsetAt are functions of them).
            if (content.Length > 20000)
            {
                var a = GetLineOffsets(); var b = fresh.GetLineOffsets();
                for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return "line " + i + " starts at " + a[i] + ", fresh " + b[i];
                return null;
            }
            for (int off = 0; off <= content.Length; off++)
            {
                int l1, c1, l2, c2;
                PositionAt(off, out l1, out c1); fresh.PositionAt(off, out l2, out c2);
                if (l1 != l2 || c1 != c2) return "positionAt(" + off + ") " + l1 + ":" + c1 + ", fresh " + l2 + ":" + c2;
                int o1 = OffsetAt(l2, c2), o2 = fresh.OffsetAt(l2, c2);
                if (o1 != o2) return "offsetAt(" + l2 + ":" + c2 + ") " + o1 + ", fresh " + o2;
            }
            return null;
        }
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
        // A \r inserted before an existing \n, or a \n after an existing \r, completes a \r\n ACROSS the change's
        // boundary. The text comes out right either way; the server's patched line table only does if the change
        // takes the whole new \r\n (LspTextDiff's new-text guards). Each case below is decided by one guard alone.
        Case("\\r before a trailing \\n (the end guard)", "a\n", "a\r\n", 1, 2);
        Case("\\n after a lone \\r (the start guard)", "a\rb", "a\r\nb", 1, 2);
        Case("\\n after a trailing lone \\r", "a\r", "a\r\n", 1, 2);
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
