using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClarionAssistant.Services;

// 1d8d1c49: TextLines.ExtractRanges must return exactly what the Split-based ExtractSlotTexts returned
// (the oracle below is that code, copied verbatim), while allocating a fraction of it.
// Edge cases, a seeded fuzz over mixed CRLF/CR/LF text and odd ranges, and a real generated module
// when one is on disk (arg 1, or CA_BIG_CLW). Proves the comparison can fail with a wrong walker.
//
// Run:  tests\Run-Tests.ps1
static class TextLinesTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // The pre-1d8d1c49 ModernEmbeditorSaver.ExtractSlotTexts + SplitLines, verbatim.
    static List<string> Oracle(string source, List<int[]> ranges)
    {
        var result = new List<string>();
        if (ranges == null) return result;
        var lines = (source ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        foreach (var r in ranges)
        {
            if (r == null || r.Length < 2) { result.Add(""); continue; }
            int s = Math.Max(1, r[0]), e = Math.Min(lines.Length, r[1]);
            if (e < s) { result.Add(""); continue; }
            var sb = new StringBuilder();
            for (int i = s; i <= e; i++)
            {
                if (i > s) sb.Append('\n');
                sb.Append(lines[i - 1]);
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    // A plausible-but-wrong walker (treats CR as ordinary text) — the comparison must catch it.
    static List<string> Wrong(string source, List<int[]> ranges)
    {
        return Oracle((source ?? "").Replace("\r\n", "\n").Replace('\r', 'X'), ranges);
    }

    static string Diff(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return "count " + a.Count + " vs " + b.Count;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return "range #" + i + ": \"" + Esc(a[i]) + "\" vs \"" + Esc(b[i]) + "\"";
        return null;
    }

    static string Esc(string s) { s = s ?? "null"; s = s.Replace("\r", "\\r").Replace("\n", "\\n"); return s.Length > 60 ? s.Substring(0, 60) + "..." : s; }

    static long Allocated() { AppDomain.MonitoringIsEnabled = true; return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize; }

    static int Main(string[] args)
    {
        Console.WriteLine("TextLines.Test");

        var texts = new[] { "", "a", "a\n", "\n", "\r\n", "\r", "a\rb", "a\r\nb\n\rc", "\n\n", "\r\r\n\n", "x\r\n", "only one line" };
        var rangeSets = new List<List<int[]>>
        {
            null, new List<int[]>(), new List<int[]> { new[] { 0, 0 } }, new List<int[]> { new[] { 1, 1 } },
            new List<int[]> { new[] { 2, 5 }, new[] { 3, 2 }, new[] { 5, 100 }, new[] { -3, 2 } },
            new List<int[]> { null, new[] { 1 }, new[] { 1, 3 } },
        };
        int edgeBad = 0; string edgeFirst = null;
        foreach (var t in texts)
            foreach (var rs in rangeSets)
            {
                string d = Diff(Oracle(t, rs), TextLines.ExtractRanges(t, rs));
                if (d != null) { edgeBad++; if (edgeFirst == null) edgeFirst = "\"" + Esc(t) + "\": " + d; }
            }
        Ok("edge cases (" + texts.Length * rangeSets.Count + ") match the Split version", edgeBad == 0, edgeFirst);

        var rnd = new Random(1234);
        string[] pieces = { "a", "bc", " ", "\r\n", "\n", "\r", "END", "" };
        int fuzzBad = 0; string fuzzFirst = null;
        for (int n = 0; n < 3000; n++)
        {
            var sb = new StringBuilder();
            int len = rnd.Next(0, 40);
            for (int i = 0; i < len; i++) sb.Append(pieces[rnd.Next(pieces.Length)]);
            string t = sb.ToString();
            var rs = new List<int[]>();
            int k = rnd.Next(0, 5);
            for (int i = 0; i < k; i++) rs.Add(new[] { rnd.Next(-2, 12), rnd.Next(-2, 14) });
            string d = Diff(Oracle(t, rs), TextLines.ExtractRanges(t, rs));
            if (d != null) { fuzzBad++; if (fuzzFirst == null) fuzzFirst = "\"" + Esc(t) + "\": " + d; }
        }
        Ok("3000 fuzz cases match the Split version", fuzzBad == 0, fuzzFirst);

        string crText = "one\rtwo\r\nthree";
        var crRanges = new List<int[]> { new[] { 1, 3 } };
        Ok("proof: a CR-blind walker is caught", Diff(Oracle(crText, crRanges), Wrong(crText, crRanges)) != null);

        string big = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("CA_BIG_CLW");
        string bigText;
        if (!string.IsNullOrEmpty(big) && File.Exists(big)) bigText = File.ReadAllText(big, Encoding.Default);
        else
        {
            // No real module on this machine: synthesize one of the same shape (~3.2M chars, ~87K CRLF lines).
            var sb = new StringBuilder(3300000);
            for (int i = 0; sb.Length < 3200000; i++) sb.Append("  LOC:Var").Append(i).Append("  STRING(20)  ! some comment text\r\n");
            bigText = sb.ToString();
            big = "(synthetic 3.2M chars)";
        }
        int[] st, en; TextLines.LineBounds(bigText, out st, out en);
        var bigRanges = new List<int[]>();
        for (int s = 1; s <= st.Length; s += 37) bigRanges.Add(new[] { s, Math.Min(st.Length, s + 11) });
        GC.Collect();
        long a0 = Allocated(); var oldR = Oracle(bigText, bigRanges); long a1 = Allocated();
        var newR = TextLines.ExtractRanges(bigText, bigRanges); long a2 = Allocated();
        double oldMb = (a1 - a0) / 1048576.0, newMb = (a2 - a1) / 1048576.0;
        Console.WriteLine("  big: " + big + " chars=" + bigText.Length + " lines=" + st.Length + " ranges=" + bigRanges.Count
            + "  alloc old=" + oldMb.ToString("0.0") + "MB new=" + newMb.ToString("0.0") + "MB");
        Ok("big module: every range matches the Split version", Diff(oldR, newR) == null, Diff(oldR, newR));
        Ok("big module: allocates under a quarter of the Split version", newMb < oldMb / 4, oldMb.ToString("0.0") + " vs " + newMb.ToString("0.0"));

        Console.WriteLine(fail == 0 ? "PASSED - " + pass + " assertions" : "FAILED - " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }
}
