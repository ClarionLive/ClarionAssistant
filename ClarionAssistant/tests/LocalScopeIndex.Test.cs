// 1c685f2e item 1: LocalScopeIndex - instant buffer-local completion and hover (phase1-tests.md 1.1-1.29).
//
// Compiles the REAL Services\LocalScopeIndex.cs (with LspClient.cs for the item DTO and ClarionBuiltins.cs
// for the type keywords). Fixtures: tests\fixtures\local-scope\two-procs.clw (+ BOM and cp1252 variants);
// the 86k-line "big" module is generated here.
//
// Args: <fixture dir> [<LocalScopeIndex.cs path, for the source scan>]
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClarionAssistant.Services;

static class LocalScopeIndexTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static string _two;
    static string[] _lines;

    static int Line(string buf, string trimmed)
    {
        var ls = buf.Replace("\r\n", "\n").Split('\n');
        int i = Array.FindIndex(ls, l => l.Trim() == trimmed);
        if (i < 0) throw new Exception("fixture line not found: " + trimmed);
        return i;
    }

    static string LineText(string buf, int line) { return buf.Replace("\r\n", "\n").Split('\n')[line]; }

    // Completion with the caret at the END of the first line whose trimmed text is `trimmed`.
    static List<LspClient.CompletionItemInfo> At(string buf, string trimmed)
    {
        int l = Line(buf, trimmed);
        return LocalScopeIndex.Complete(buf, l, LineText(buf, l).Length, null);
    }

    static HashSet<string> Labels(IEnumerable<LspClient.CompletionItemInfo> items)
    {
        return new HashSet<string>(items.Select(i => i.Label), StringComparer.OrdinalIgnoreCase);
    }

    static string Show(IEnumerable<LspClient.CompletionItemInfo> items) { return "[" + string.Join(", ", items.Select(i => i.Label)) + "]"; }

    // Replace the first line whose trimmed text equals `trimmed` with `replacement` (a raw line).
    static string WithLine(string buf, string trimmed, string replacement)
    {
        var ls = buf.Replace("\r\n", "\n").Split('\n').ToList();
        int i = ls.FindIndex(l => l.Trim() == trimmed);
        ls[i] = replacement;
        return string.Join("\r\n", ls);
    }

    static string InsertAfter(string buf, string trimmed, string newLine)
    {
        var ls = buf.Replace("\r\n", "\n").Split('\n').ToList();
        int i = ls.FindIndex(l => l.Trim() == trimmed);
        ls.Insert(i + 1, newLine);
        return string.Join("\r\n", ls);
    }

    static LocalHoverResult HoverOn(string buf, string trimmedLine, string word)
    {
        int l = Line(buf, trimmedLine);
        int c = LineText(buf, l).IndexOf(word, StringComparison.Ordinal) + 1;
        return LocalScopeIndex.Hover(buf, l, c, "two-procs.clw");
    }

    static int Main(string[] args)
    {
        string dir = args[0];
        string sourcePath = args.Length > 1 ? args[1] : null;
        _two = File.ReadAllText(Path.Combine(dir, "two-procs.clw"));
        _lines = _two.Replace("\r\n", "\n").Split('\n');

        Completion();
        Hover();
        Encodings(dir);
        R3(sourcePath);
        Threads();

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }

    // ------------------------------------------------------------------------------ completion 1.1-1.14

    static void Completion()
    {
        Console.WriteLine("completion");
        string[] procAParams = { "pId", "pName", "pOpt", "pDef", "pAny", "pQ" };

        // 1.1 / 1.2: parameters of the ENCLOSING procedure only.
        string a = WithLine(_two, "pI", "  p");
        var l11 = Labels(At(a, "p"));
        Check(procAParams.All(l11.Contains) && !l11.Contains("pOther"), "1.1", "ProcA 'p' -> its six params, no pOther: " + string.Join(",", l11));
        string b = WithLine(_two, "Lo", "  p");
        var items12 = At(b, "p").Where(i => (i.Detail ?? "").Contains("(parameter)")).ToList();
        Check(items12.Count == 1 && items12[0].Label == "pOther", "1.2", "ProcB 'p' -> parameter pOther only: " + Show(items12));

        // 1.3: the MAP prototype "LocalHelper PROCEDURE(LONG)" declares no parameter names.
        string lbl;
        var mapParams = LocalScopeIndex.ParsePrototypeParams("LocalHelper PROCEDURE(LONG)", out lbl);
        Check(mapParams.Count == 1 && mapParams[0].Name == null && mapParams[0].Type == "LONG", "1.3",
              "the MAP prototype (LONG) declares a type and no parameter name");
        // ...and an implementation that lists names only takes its types from that MAP prototype.
        string c13 = InsertAfter(_two, "LocalHelper PROCEDURE(LONG)", "                       ProcC PROCEDURE(STRING, <*LONG>)");
        c13 = c13 + "\r\nProcC                PROCEDURE(pX, pY)\r\n  CODE\r\n  pX\r\n";
        var pc = At(c13, "pX");
        var px = pc.FirstOrDefault(i => i.Label == "pX");
        var py = pc.FirstOrDefault(i => i.Label == "pY");
        Check(px != null && px.Detail == "STRING  (parameter)", "1.3b", "names-only implementation: pX typed STRING from the MAP: " + (px == null ? "(missing)" : px.Detail));
        var c13y = At(c13.Replace("\r\n  pX\r\n", "\r\n  pY\r\n"), "pY").FirstOrDefault(i => i.Label == "pY");
        Check(c13y != null && c13y.Detail == "<*LONG>  (parameter)", "1.3c", "pY typed <*LONG> from the MAP: " + (c13y == null ? "(missing)" : c13y.Detail));

        // 1.4 / 1.5: inside ThisWindow.Init - its own parameter, and ProcA's locals.
        var l14 = Labels(At(_two, "pM"));
        Check(l14.Contains("pMode"), "1.4", "ThisWindow.Init 'pM' -> pMode: " + string.Join(",", l14));
        string c15 = WithLine(_two, "LO", "  LOC:");
        var l15 = Labels(At(c15, "LOC:"));
        Check(l15.Contains("LOC:Count"), "1.5", "ThisWindow.Init 'LOC:' -> ProcA's LOC:Count: " + string.Join(",", l15));

        // 1.6: detail + documentation.
        string c16 = WithLine(_two, "Loc", "  lo");
        var cnt = At(c16, "lo").FirstOrDefault(i => i.Label == "LOC:Count");
        Check(cnt != null && (cnt.Detail ?? "").Contains("LONG") && (cnt.Documentation ?? "").Contains("the row count"),
              "1.6", "'lo' -> LOC:Count with LONG / 'the row count': " + (cnt == null ? "(missing)" : cnt.Detail + " | " + cnt.Documentation));

        // 1.7: routine DATA only inside that routine.
        var inRtn = Labels(At(_two, "Rt"));
        string c17 = WithLine(_two, "Mo", "  Rt");
        var inProc = Labels(At(c17, "Rt"));
        Check(inRtn.Contains("RtnOnly") && !inProc.Contains("RtnOnly"), "1.7", "RtnOnly inside RtnA, absent in ProcA's CODE");

        // 1.8: no-PRE group fields bare; PRE'd fields not.
        var gr = Labels(At(_two, "Gr"));
        string c18 = WithLine(_two, "Gr", "  Pg");
        var pg = Labels(At(c18, "Pg"));
        Check(gr.Contains("GrpA") && gr.Contains("GrpB") && !pg.Contains("PgX"), "1.8", "'Gr' -> GrpA GrpB; 'Pg' has no bare PgX: " + string.Join(",", pg));

        // 1.9: DO -> routines of the current procedure only.
        var doB = Labels(At(_two, "DO R"));
        string c19 = WithLine(_two, "Mo", "  DO R");
        var doA = Labels(At(c19, "DO R"));
        Check(doB.SetEquals(new[] { "RtnB" }) && doA.SetEquals(new[] { "RtnA" }), "1.9",
              "DO R -> RtnB in ProcB [" + string.Join(",", doB) + "], RtnA in ProcA [" + string.Join(",", doA) + "]");

        // 1.10: a column-1 declaration being typed in DATA still resolves ProcA; past CODE it is module scope.
        string c110 = InsertAfter(_two, "WinFlag                BYTE", "NewVar LO");
        var sc = LocalScopeIndex.Complete(c110, Line(c110, "NewVar LO"), "NewVar LO".Length, null);
        string c110b = InsertAfter(_two, "LOC:Count = pId + ModCounter", "Test LO");
        var sc2 = LocalScopeIndex.Complete(c110b, Line(c110b, "Test LO"), "Test LO".Length, null);
        Check(Labels(sc).Contains("LOC:Count") && !Labels(sc2).Contains("LOC:Count"), "1.10",
              "declaration in progress: in DATA sees LOC:Count, past CODE does not: " + Show(sc2));

        // 1.11: comment / string.
        string c111 = WithLine(_two, "Loc", "  x = 1 ! Lo");
        string c111b = WithLine(_two, "Loc", "  x = 'Lo");
        Check(At(c111, "x = 1 ! Lo").Count == 0 && At(c111b, "x = 'Lo").Count == 0, "1.11", "inside a comment / a string -> empty");

        // 1.12: caret ON the ProcA header resolves ProcA; caret in module data -> module scope.
        int hdr = Line(_two, _lines.First(l => l.StartsWith("ProcA ")).Trim());
        var onHdr = LocalScopeIndex.GetScope(_two, hdr);
        var hdrItems = new List<LspClient.CompletionItemInfo>();
        onHdr.AddLocals("LOC", new HashSet<string>(StringComparer.OrdinalIgnoreCase), hdrItems, true);
        var modScope = LocalScopeIndex.GetScope(_two, Line(_two, "ModCounter           LONG                          ! module counter"));
        var modItems = new List<LspClient.CompletionItemInfo>();
        var seenM = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        modScope.AddLocals("", seenM, modItems, true);
        modScope.AddModuleVars("Mod", seenM, modItems);
        Check(Labels(hdrItems).Contains("LOC:Count") && Labels(modItems).Contains("ModCounter") && !Labels(modItems).Contains("LOC:Count"),
              "1.12", "on the header -> ProcA scope; in module data -> ModCounter, no LOC:Count");

        // 1.13 / 1.14: line-ending variants.
        string lf = _two.Replace("\r\n", "\n");
        string noTrail = _two.TrimEnd('\r', '\n');
        int last = noTrail.Replace("\r\n", "\n").Split('\n').Length - 1;
        List<LspClient.CompletionItemInfo> endItems = null;
        Exception ex13 = null;
        try { endItems = LocalScopeIndex.Complete(noTrail + "\r\n  LOC:O", last + 1, 7, null); } catch (Exception e) { ex13 = e; }
        Check(ex13 == null && endItems != null && Labels(endItems).Contains("LOC:Other"), "1.13", "end of a no-trailing-newline buffer resolves ProcB");
        bool same = true;
        foreach (var k in new[] { "Loc", "Mo", "Gr", "PG:", "MyGrp.", "pI", "Rt", "LO", "pM", "Lo", "DO RtnB" })
        {
            var x = At(_two, k).Select(i => i.Label + "|" + i.Detail).ToList();
            var y = At(lf, k).Select(i => i.Label + "|" + i.Detail).ToList();
            if (!x.SequenceEqual(y)) { same = false; Console.WriteLine("    CRLF/LF differ at " + k); }
        }
        Check(same, "1.14", "CRLF and LF buffers give identical items at every caret");
    }

    // ------------------------------------------------------------------------------ hover 1.17-1.23, 3.10

    static void Hover()
    {
        Console.WriteLine("hover");
        const string codeLine = "LOC:Count = pId + ModCounter";
        var h17 = HoverOn(_two, codeLine, "LOC:Count");
        Check(h17 != null && h17.Authoritative && h17.Markdown.Contains("LONG") && h17.Markdown.Contains("LOC:Count"), "1.17", "LOC:Count -> LONG, authoritative: " + Md(h17));

        string c18 = WithLine(_two, "Loc", "  x = pName");
        var h18 = HoverOn(c18, "x = pName", "pName");
        Check(h18 != null && h18.Authoritative && h18.Markdown.Contains("*STRING"), "1.18", "pName -> *STRING, authoritative: " + Md(h18));

        var h19 = HoverOn(_two, "DO RtnB", "RtnB");
        string c19 = WithLine(_two, "Loc", "  DO RtnA");
        var h19a = HoverOn(c19, "DO RtnA", "RtnA");
        Check(h19 != null && h19.Authoritative && h19a != null && h19a.Authoritative, "1.19", "routines RtnB / RtnA -> authoritative: " + Md(h19a));

        var h20 = HoverOn(_two, "ReturnValue = LocalHelper(pMode)", "LocalHelper");
        Check(h20 != null && h20.Authoritative, "1.20", "LocalHelper (MAP) -> authoritative: " + Md(h20));

        string c21 = WithLine(_two, "Loc", "  x = SomeGlobal");
        Check(HoverOn(c21, "x = SomeGlobal", "SomeGlobal") == null, "1.21", "SomeGlobal -> null (not declared here)");
        Check(HoverOn(_two, "RETURN ReturnValue", "RETURN") == null, "1.22", "RETURN -> null (keywords are item 3)");
        string c23 = WithLine(_two, "Loc", "  x = LOC:Other");
        Check(HoverOn(c23, "x = LOC:Other", "LOC:Other") == null, "1.23", "LOC:Other from ProcA -> null (ProcB's local)");

        // 3.10 (the local half): a DATA label that is also a built-in name is answered locally.
        string c310 = InsertAfter(_two, "LOC:Count            LONG                          ! the row count", "Clip                 LONG");
        c310 = WithLine(c310, "Loc", "  x = Clip");
        var h310 = HoverOn(c310, "x = Clip", "Clip");
        Check(h310 != null && h310.Authoritative && h310.Kind == "local", "3.10a", "a local named Clip wins, authoritative");

        // Local class member access (SELF.Init inside ThisWindow.Init): non-authoritative member hover.
        string cm = WithLine(_two, "SELF.", "  SELF.Kill()");
        var hm = HoverOn(cm, "SELF.Kill()", "Kill");
        Check(hm != null && !hm.Authoritative && hm.Markdown.Contains("PROCEDURE()"), "1.m1", "SELF.Kill -> the local CLASS member, non-authoritative: " + Md(hm));
        var selfItems = Labels(At(_two, "SELF."));
        Check(selfItems.SetEquals(new[] { "Init", "Kill", "WinFlag" }), "1.m2", "SELF. -> ThisWindow's members: " + string.Join(",", selfItems));
        var ma = LocalScopeIndex.GetMemberAccess(_two, Line(_two, "SELF."), LineText(_two, Line(_two, "SELF.")).Length);
        Check(ma != null && ma.LocalClass == "ThisWindow" && ma.BaseType == "WindowManager", "1.m3", "GetMemberAccess(SELF.) -> ThisWindow : WindowManager");
    }

    static string Md(LocalHoverResult r) { return r == null ? "(null)" : r.Markdown.Replace("\n", "\\n"); }

    // ------------------------------------------------------------------------------ encodings 1.15, 1.16

    static void Encodings(string dir)
    {
        Console.WriteLine("encodings");
        byte[] bomBytes = File.ReadAllBytes(Path.Combine(dir, "proc-bom.clw"));
        string bom = Encoding.UTF8.GetString(bomBytes);                 // keeps U+FEFF in the buffer
        string plain = bom.TrimStart('\uFEFF');
        Check(bom[0] == '\uFEFF', "1.15-pre", "the BOM fixture's buffer really starts with U+FEFF");
        int l = Line(plain, "pI");
        var x = LocalScopeIndex.Complete(bom, l, 3, null).Select(i => i.Label).OrderBy(s => s).ToList();
        var y = LocalScopeIndex.Complete(plain, l, 3, null).Select(i => i.Label).OrderBy(s => s).ToList();
        Check(x.Count > 0 && x.SequenceEqual(y) && x.Contains("pId"), "1.15", "BOM vs plain, ProcA 'p' -> identical: [" + string.Join(",", x) + "]");

        string ansi = Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(Path.Combine(dir, "proc-ansi.clw")));
        var items = At(ansi, "LOC:");
        var note = items.FirstOrDefault(i => i.Label == "LOC:Note");
        Check(Labels(items).Contains("LOC:After") && note != null && (note.Detail ?? "").Contains("'naïve ! not a comment'"),
              "1.16", "ANSI: LOC:Note keeps its string, LOC:After present: " + (note == null ? "(no LOC:Note)" : note.Detail));
        Check(!Labels(items).Any(s => s.StartsWith("Ü") || s.StartsWith("berschrift")), "1.16b", "nothing parsed from the comment line");
        string utf8Misread = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "proc-ansi.clw")));
        Check(Labels(At(utf8Misread, "LOC:")).SetEquals(Labels(items)), "1.16c", "a UTF-8 (U+FFFD) decode finds the same labels");
    }

    // ------------------------------------------------------------------------------ R3 1.24-1.28

    static string BuildModule(int procs, string callerProc, int callerAt)
    {
        var header = _two.Substring(0, _two.IndexOf("ProcA ", StringComparison.Ordinal));
        string procA = _two.Substring(_two.IndexOf("ProcA ", StringComparison.Ordinal));
        procA = procA.Substring(0, procA.IndexOf("ProcB ", StringComparison.Ordinal));
        var sb = new StringBuilder(header);
        for (int p = 0; p < procs; p++)
        {
            if (p == callerAt) { sb.Append(procA); continue; }
            sb.Append("Gen").Append(p).Append("                 PROCEDURE(LONG gId").Append(p).Append(")\r\n");
            for (int d = 0; d < 20; d++) sb.Append("G").Append(p).Append(":V").Append(d).Append("            LONG\r\n");
            sb.Append("ThisWindow           CLASS(WindowManager)\r\nInit                   PROCEDURE(),BYTE,PROC,DERIVED\r\n                     END\r\n");
            sb.Append("  CODE\r\n");
            for (int c = 0; c < 170; c++) sb.Append("  IF G").Append(p).Append(":V").Append(c % 20).Append(" = ").Append(c).Append(" THEN G").Append(p).Append(":V0 += 1.\r\n");
            sb.Append("  RETURN\r\n\r\nGenRtn").Append(p).Append("            ROUTINE\r\n  G").Append(p).Append(":V1 = 2\r\n\r\n");
            sb.Append("ThisWindow.Init      PROCEDURE\r\n  CODE\r\n  RETURN 0\r\n\r\n");
        }
        return sb.ToString();
    }

    static long Allocated() { return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize; }

    static double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }

    static void R3(string sourcePath)
    {
        Console.WriteLine("R3 (never the whole buffer)");
        AppDomain.MonitoringIsEnabled = true;
        string big = BuildModule(400, "ProcA", 200);
        string small = BuildModule(1, "ProcA", 0);
        int bigLines = big.Count(ch => ch == '\n');
        Console.WriteLine("    big: " + bigLines + " lines, " + (big.Length / 1024) + " KB chars; small: " + small.Count(ch => ch == '\n') + " lines");
        int bl = Line(big, "pI"), sl = Line(small, "pI");

        // Cold on the big buffer (new instance, cold header), then warm.
        LocalScopeIndex.ResetCaches();
        var sw = Stopwatch.StartNew();
        var coldItems = LocalScopeIndex.Complete(big, bl, 4, null);
        double coldMs = sw.Elapsed.TotalMilliseconds;
        Check(Labels(coldItems).Contains("pId"), "1.24-pre", "the caret procedure resolves in the big module");

        // 1.24: allocation of one Complete on a NEW instance of big (header warm).
        string big2 = new string(big.ToCharArray());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = Allocated();
        var it2 = LocalScopeIndex.Complete(big2, bl, 4, null);
        long bytes = Allocated() - before;
        Console.WriteLine("    one Complete on a new 3.2 MB instance allocated " + (bytes / 1024) + " KB");
        Check(bytes < 1024 * 1024 && Labels(it2).Contains("pId"), "1.24", "< 1 MB allocated per call on a new big instance (" + (bytes / 1024) + " KB)");

        // 1.25 / 1.27: warm medians, big vs small.
        var tb = new List<double>();
        var ts = new List<double>();
        for (int i = 0; i < 50; i++)
        {
            sw.Restart(); LocalScopeIndex.Complete(big, bl, 4, null); tb.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart(); LocalScopeIndex.Complete(small, sl, 4, null); ts.Add(sw.Elapsed.TotalMilliseconds);
        }
        double mb = Median(tb), ms = Median(ts);
        // A new instance each call (every edit is a new string): the realistic per-keystroke cost.
        var tn = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            string inst = new string(big.ToCharArray());
            sw.Restart(); LocalScopeIndex.Complete(inst, bl, 4, null); tn.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine(string.Format("    cold (first call, cold header) {0:F2} ms; warm median big {1:F3} ms, small {2:F3} ms; new-instance median {3:F2} ms",
                                        coldMs, mb, ms, Median(tn)));
        Check(mb <= 3 * Math.Max(ms, 0.02), "1.25", string.Format("warm big median {0:F3} ms <= 3x small {1:F3} ms", mb, ms));
        Check(mb < 10, "1.27", string.Format("warm Complete on big < 10 ms ({0:F3} ms)", mb));
        Check(Median(tn) < 20, "1.27b", string.Format("new-instance Complete on big < 20 ms ({0:F2} ms; every edit is a new instance: one linear newline+procedure walk)", Median(tn)));

        // 1.26: the header cache is by content, not by instance.
        LocalScopeIndex.ResetCaches();
        int c0 = LocalScopeIndex.HeaderParseCount;
        string e1 = WithLine(_two, "Loc", "  Lo");
        string e2 = WithLine(_two, "Loc", "  LOC");       // a different instance, edited inside ProcA
        LocalScopeIndex.Complete(e1, Line(e1, "Lo"), 4, null);
        LocalScopeIndex.Complete(e2, Line(e2, "LOC"), 5, null);
        int afterPair = LocalScopeIndex.HeaderParseCount - c0;
        string e3 = InsertAfter(e2, "ModCounter           LONG                          ! module counter", "ModNew               LONG");
        var modNew = LocalScopeIndex.Complete(e3, Line(e3, "LOC"), 3, null);
        var modNewItems = LocalScopeIndex.Complete(WithLine(e3, "LOC", "  Mo"), Line(e3, "LOC"), 4, null);
        int afterEdit = LocalScopeIndex.HeaderParseCount - c0;
        Check(afterPair == 1 && afterEdit == 2 && Labels(modNewItems).Contains("ModNew"), "1.26",
              "header parsed once for two instances (" + afterPair + "), again after a header edit (" + afterEdit + "), ModNew offered");

        // 1.28: source scan - no Split over the buffer, no ReadAllLines.
        if (sourcePath != null)
        {
            string src = File.ReadAllText(sourcePath);
            var splits = src.Split('\n').Select((t, i) => new { t, i }).Where(x => x.t.Contains(".Split(")).ToList();
            bool onlyHeader = splits.Count == 1 && splits[0].t.Contains("text.Replace(\"\\r\\n\", \"\\n\").Split('\\n')");
            Check(onlyHeader && !src.Contains("ReadAllLines"), "1.28",
                  "the only .Split( is the header-text split in ParseHeader (" + splits.Count + " found); no ReadAllLines");
        }
        else Check(false, "1.28", "no source path given for the scan");
    }

    // ------------------------------------------------------------------------------ 1.29 threads

    static void Threads()
    {
        Console.WriteLine("threads");
        string[] carets = { "Loc", "Gr", "PG:", "pI", "Rt", "LO", "pM", "Lo", "DO RtnB", "SELF." };
        var expected = carets.Select(k => string.Join(",", At(_two, k).Select(i => i.Label))).ToArray();
        int bad = 0, errors = 0;
        var threads = new List<Thread>();
        for (int t = 0; t < 8; t++)
        {
            int seed = t;
            var th = new Thread(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    int k = (i + seed) % carets.Length;
                    try
                    {
                        if (i % 7 == 0) LocalScopeIndex.ResetCaches();
                        string got = string.Join(",", At(_two, carets[k]).Select(x => x.Label));
                        if (got != expected[k]) Interlocked.Increment(ref bad);
                    }
                    catch { Interlocked.Increment(ref errors); }
                }
            });
            threads.Add(th);
            th.Start();
        }
        foreach (var th in threads) th.Join();
        Check(bad == 0 && errors == 0, "1.29", "8 threads x 200 calls: " + bad + " mismatches, " + errors + " exceptions");
    }
}
