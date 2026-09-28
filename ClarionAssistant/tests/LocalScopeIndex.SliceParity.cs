// 1c685f2e R11: the SLICE overloads of LocalScopeIndex answer exactly what the full-buffer overloads
// answer, at every caret. The slices are built the way the page builds them from the span map:
//   span  = the caret's procedure, Start..End (and, for the capped variant, its Start..DataEnd piece plus a
//           window of lines around the caret),
//   owner = for a Class.Method, the owning procedure's Start..DataEnd,
//   routines = the map's routine names for that procedure,
//   header = the text cached under the map's hash (TryGetHeaderText).
// Also measures BuildSpanMap on a real generated module when one is given.
//
// Args: <fixture dir> [<real generated .clw to measure/compare on>]
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ClarionAssistant.Services;

static class LocalScopeIndexSliceParity
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    static int Main(string[] args)
    {
        string dir = args[0];
        string real = args.Length > 1 ? args[1] : null;
        string two = File.ReadAllText(Path.Combine(dir, "two-procs.clw"));

        Console.WriteLine("span map");
        var map = LocalScopeIndex.BuildSpanMap(two);
        var names = map.Procs.Select(p => p.Name).ToList();
        var lines = two.Replace("\r\n", "\n").Split('\n');
        int procA = Array.FindIndex(lines, l => l.StartsWith("ProcA ")) + 1;
        var a = map.Procs.First(p => p.Name == "ProcA");
        var init = map.Procs.First(p => p.Name == "ThisWindow.Init");
        var b = map.Procs.First(p => p.Name == "ProcB");
        Check(names.SequenceEqual(new[] { "ProcA", "ThisWindow.Init", "ProcB" }), "R11.1",
              "procs = ProcA, ThisWindow.Init, ProcB - the CLASS/MAP prototypes are not procedures: [" + string.Join(", ", names) + "]");
        Check(a.Start == procA && lines[a.DataEnd - 1].Trim() == "CODE" && a.End == init.Start - 1 && b.End == lines.Length,
              "R11.2", "ProcA: start " + a.Start + ", dataEnd " + a.DataEnd + " (the CODE line), end " + a.End + " (before ThisWindow.Init at " + init.Start + ")");
        Check(init.Owner == 0 && b.Owner == null && a.Routines.SequenceEqual(new[] { "RtnA" }) && b.Routines.SequenceEqual(new[] { "RtnB" }),
              "R11.3", "ThisWindow.Init's owner is ProcA (index 0); routines ProcA [" + string.Join(",", a.Routines) + "], ProcB [" + string.Join(",", b.Routines) + "]");
        string text;
        Check(LocalScopeIndex.TryGetHeaderText(map.HeaderHash, out text) && text == map.HeaderText && two.StartsWith(text), "R11.4",
              "the header text is cached under the map's hash, and is the buffer's prefix up to ProcA");
        string planted;
        Check(!LocalScopeIndex.RegisterHeader("stale-hash", text + "ModX LONG\r\n") && !LocalScopeIndex.TryGetHeaderText("stale-hash", out planted),
              "R11.5", "headerSync FAILS CLOSED: a text that does not hash to the caller's key is refused and stored under no key (F9)");
        string good = text + "ModY LONG\r\n";
        string goodKey = LocalScopeIndex.ContentKey(good, 0, good.Length);
        Check(LocalScopeIndex.RegisterHeader(goodKey, good) && LocalScopeIndex.TryGetHeaderText(goodKey, out planted) && planted == good,
              "R11.5b", "headerSync with a matching key is cached");

        // F2: an edited header travels as text in the slice; the answer reflects it with no headerSync.
        string editedHeader = text + "ModZ                 LONG\r\n";
        var ma = map.Procs.First(p => p.Name == "ProcA");
        var editedLines = (editedHeader + two.Substring(text.Length)).Split('\n');
        int shift = 1;   // one line added to the header
        var slice = new List<SlicePiece> { new SlicePiece(ma.Start + shift, Lines(editedLines, ma.Start + shift, ma.End + shift)) };
        int caret = Array.FindIndex(editedLines, x => x.TrimEnd('\r') == "  Mo");
        var withText = LocalScopeIndex.Complete(map.HeaderHash, editedHeader, slice, ma.Routines, caret, 4, null).Select(i => i.Label).ToList();
        var byHash = LocalScopeIndex.Complete(map.HeaderHash, null, slice, ma.Routines, caret, 4, null).Select(i => i.Label).ToList();
        int caretPr = Array.FindIndex(editedLines, x => x.TrimEnd('\r') == "  Pr");
        var procsWithText = LocalScopeIndex.Complete(map.HeaderHash, editedHeader, slice, ma.Routines, caretPr, 4, null).Select(i => i.Label).ToList();
        Check(withText.Contains("ModZ") && !byHash.Contains("ModZ") && procsWithText.Contains("ProcB"), "F2.headerText",
              "a slice's headerText wins over the cached header (ModZ offered: [" + string.Join(",", withText) + "]) and keeps the map's procedure list");

        // F9: the header and piece caches are bounded by aggregate chars (8 MB each), oldest evicted first.
        var bigHeader = new string((char)120, 3 * 1024 * 1024);
        for (int i = 0; i < 5; i++) LocalScopeIndex.RegisterHeader(null, "  MEMBER()\r\n! " + i + bigHeader);
        var sizes = LocalScopeIndex.CacheSizes();
        Check(sizes[1] <= LocalScopeIndex.CacheCharBudget && sizes[1] > 0, "F9.header", "five 3 MB headers leave the header cache at " + sizes[1] / 1024 + " K chars (<= 8 M)");
        var sb = new StringBuilder("  MEMBER()\r\n");
        for (int p = 0; p < 40; p++)
        {
            sb.Append("P").Append(p).Append("                   PROCEDURE\r\n");
            sb.Append("D").Append(p).Append("                   STRING('").Append(new string('y', 400 * 1024)).Append("')\r\n  CODE\r\n");
        }
        var bigMap = LocalScopeIndex.BuildSpanMap(sb.ToString());
        sizes = LocalScopeIndex.CacheSizes();
        string newest, oldest;
        Check(sizes[3] <= LocalScopeIndex.CacheCharBudget && LocalScopeIndex.TryGetPieceText(bigMap.Procs.Last().DataHash, out newest)
              && !LocalScopeIndex.TryGetPieceText(bigMap.Procs.First().DataHash, out oldest), "F9.pieces",
              "40 pieces of 400 K chars: the piece cache holds " + sizes[3] / 1024 + " K chars (<= 8 M), newest kept, oldest evicted");

        // F10: a slice's allocation does not grow with the caret's line number (no padding per skipped line).
        {
            AppDomain.MonitoringIsEnabled = true;
            var lb = new StringBuilder("  MEMBER()\r\nBigProc              PROCEDURE\r\nLoc:A                LONG\r\n  CODE\r\n");
            for (int i = 0; i < 80000; i++) lb.Append("  Loc:A += ").Append(i).Append("\r\n");
            string longBuf = lb.ToString();
            var lm = LocalScopeIndex.BuildSpanMap(longBuf);
            var lp = lm.Procs[0];
            var lraw = longBuf.Split('\n');
            Func<int, long> allocAt = line1 =>
            {
                var pcs = new List<SlicePiece> { new SlicePiece { Start = lp.Start, Hash = lp.DataHash },
                                                 new SlicePiece(line1 - 200, Lines(lraw, line1 - 200, line1 + 200)) };
                LocalScopeIndex.Complete(lm.HeaderHash, null, pcs, lp.Routines, line1 - 1, 4, null);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long a0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                for (int i = 0; i < 5; i++) LocalScopeIndex.Complete(lm.HeaderHash, null, pcs, lp.Routines, line1 - 1, 4, null);
                return (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - a0) / 5;
            };
            long near = allocAt(1000), far = allocAt(79000);
            Check(far - near < 32 * 1024, "F10.alloc",
                  "slice Complete allocates " + near / 1024 + " KB at line 1000 and " + far / 1024 + " KB at line 79000 (must not grow with the line)");
        }

        Check(LocalScopeIndex.LineOf("a\r\nbb\ncc", 1) == "bb" && LocalScopeIndex.LineOf("a\r\nbb\ncc", 2) == "cc" &&
              LocalScopeIndex.LineOf("a\r\nbb\ncc", 3) == null && LocalScopeIndex.LineOf("a\r\n", 0) == "a", "F10.lineOf",
              "the shared LineOf helper: CR dropped, null past the end");

        Console.WriteLine("parity: full buffer == slice, every caret");
        Parity("two-procs", two, 1, capped: false);
        Parity("two-procs (LF)", two.Replace("\r\n", "\n"), 1, capped: false);
        string edited = two.Replace("  Loc\r\n", "  Loc\r\nNewRtn               ROUTINE\r\n  DO New\r\n");   // a routine typed since the map
        Parity("two-procs, capped windows", two, 1, capped: true);
        // A DO far above its routine: capped, the routine header is outside the window, so only the map's
        // routine list can answer it.
        Parity("two-procs, DO far from its routine, capped", two.Replace("  Loc\r\n", "  Loc\r\n  DO Rt\r\n"), 1, capped: true);
        // R11b: the DATA pieces travel as their map hash alone, resolved from the cache.
        Parity("two-procs, capped, hash-only DATA pieces", two, 1, capped: true, hashOnly: true);
        Parity("two-procs, DO far, capped, hash-only", two.Replace("  Loc\r\n", "  Loc\r\n  DO Rt\r\n"), 1, capped: true, hashOnly: true);
        var hm = LocalScopeIndex.BuildSpanMap(two);
        string t1;
        bool allResolve = hm.Procs.All(p => LocalScopeIndex.TryGetPieceText(p.DataHash, out t1) && t1.StartsWith(p.Name))
                       && hm.Procs.SelectMany(p => p.RoutineSpans).All(r => LocalScopeIndex.TryGetPieceText(r.DataHash, out t1) && t1.StartsWith(r.Name));
        Check(allResolve && hm.Procs.SelectMany(p => p.RoutineSpans).Any(), "R11b.cache",
              "BuildSpanMap cached every proc's and routine's DATA piece under its DataHash (TryGetPieceText resolves them)");
        Check(LocalScopeIndex.MissingPieces(new[] { new SlicePiece { Start = 1, Hash = "nope:1" } }).SequenceEqual(new[] { "nope:1" }),
              "R11b.missing", "an unknown hash is reported by MissingPieces (the needPieces reply)");
        ParityEdited(two, edited);

        if (real != null && File.Exists(real))
        {
            Console.WriteLine("real module: " + Path.GetFileName(real));
            string buf = EncodingHelper.ReadAllText(real, out _);
            LocalScopeIndex.BuildSpanMap(buf);   // warm the JIT
            var times = new List<double>();
            SpanMap rm = null;
            for (int i = 0; i < 5; i++)
            {
                string inst = new string(buf.ToCharArray());
                var sw = Stopwatch.StartNew();
                rm = LocalScopeIndex.BuildSpanMap(inst);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            int lineCount = buf.Count(c => c == '\n') + 1;
            Console.WriteLine(string.Format("    BuildSpanMap on {0} lines / {1:F1} MB chars: median {2:F1} ms, max {3:F1} ms; {4} procs, header {5} chars",
                lineCount, buf.Length / 1048576.0, times[2], times[4], rm.Procs.Count, rm.HeaderText.Length));
            Check(rm.Procs.Count > 0, "R11.real", "the real module maps to " + rm.Procs.Count + " procedures");

            // Per-keystroke cost of a slice call deep in the largest procedure, span capped the way the page
            // caps it: the procedure's header..DATA, the routine's header..DATA, and a 400-line window.
            var raw = buf.Split('\n');
            var big = rm.Procs.OrderByDescending(p => p.End - p.Start).First();
            int caret1 = big.Start + (big.End - big.Start) * 2 / 3;
            var pcs = new List<SlicePiece> { new SlicePiece(big.Start, Lines(raw, big.Start, big.DataEnd)) };
            int wlo = Math.Max(big.DataEnd + 1, caret1 - 200), whi = Math.Min(big.End, caret1 + 200);
            var brt = big.RoutineSpans.LastOrDefault(r => r.Start <= caret1);
            if (brt != null) pcs.Add(new SlicePiece(brt.Start, Lines(raw, brt.Start, brt.DataEnd)));
            pcs.Add(new SlicePiece(wlo, Lines(raw, wlo, whi)));
            Console.WriteLine("    pieces: " + string.Join(", ", pcs.Select(p => "line " + p.Start + ": " + (p.Text.Length / 1024) + " KB")));
            if (big.Owner.HasValue) { var o = rm.Procs[big.Owner.Value]; pcs.Insert(0, new SlicePiece(o.Start, Lines(raw, o.Start, o.DataEnd))); }
            string hdrText;
            LocalScopeIndex.TryGetHeaderText(rm.HeaderHash, out hdrText);
            string caretLine = raw[caret1 - 1].TrimEnd('\r');
            var st = new List<double>();
            for (int i = 0; i < 21; i++)
            {
                var sw = Stopwatch.StartNew();
                LocalScopeIndex.Complete(hdrText, pcs, big.Routines, caret1 - 1, caretLine.Length, null);
                LocalScopeIndex.Hover(hdrText, pcs, big.Routines, caret1 - 1, Math.Max(0, caretLine.Length - 2), "m.clw");
                st.Add(sw.Elapsed.TotalMilliseconds);
            }
            st.Sort();
            int sliceChars = pcs.Sum(p => p.Text.Length) + hdrText.Length;
            Console.WriteLine(string.Format("    slice Complete+Hover at line {0} of {1} ({2}, {3} lines): median {4:F2} ms, max {5:F2} ms; slice {6} KB chars",
                caret1, raw.Length, big.Name, big.End - big.Start + 1, st[10], st[20], sliceChars / 1024));
            Parity(Path.GetFileName(real), buf, Math.Max(1, lineCount / 60), capped: false);
            Parity(Path.GetFileName(real) + " capped, 3-line window", buf, Math.Max(1, lineCount / 60) + 7, capped: true);
            Parity(Path.GetFileName(real) + " capped, 400-line window", buf, Math.Max(1, lineCount / 60) + 3, capped: true, window: 200);
            Parity(Path.GetFileName(real) + " capped, 400-line window, hash-only DATA", buf, Math.Max(1, lineCount / 60) + 5, capped: true, window: 200, hashOnly: true);
        }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + ":");
        foreach (var f in Failures.Take(40)) Console.WriteLine("  - " + f);
        return 1;
    }

    static string Lines(string[] lines, int from1, int to1)
    {
        var sb = new StringBuilder();
        for (int i = from1; i <= to1 && i <= lines.Length; i++)
        {
            if (i > from1) sb.Append('\n');
            sb.Append(lines[i - 1]);
        }
        return sb.ToString();
    }

    /// <summary>The page's slice for 0-based line <paramref name="line0"/>, from the span map.</summary>
    static void Slice(SpanMap map, string[] rawLines, int line0, bool capped, int window, bool hashOnly,
                      out List<SlicePiece> pieces, out List<string> routines)
    {
        pieces = new List<SlicePiece>();
        routines = null;
        int line1 = line0 + 1;
        var proc = map.Procs.LastOrDefault(p => p.Start <= line1 && line1 <= p.End);
        if (proc == null) return;
        routines = proc.Routines.ToList();
        SpanProc owner = proc.Owner.HasValue ? map.Procs[proc.Owner.Value] : null;
        if (!capped || proc.End - proc.DataEnd < 8)
        {
            if (owner != null) pieces.Add(new SlicePiece(owner.Start, Lines(rawLines, owner.Start, owner.DataEnd)));
            pieces.Add(new SlicePiece(proc.Start, Lines(rawLines, proc.Start, proc.End)));
            return;
        }
        // Capped, in the R11b wire order: the procedure's DATA, the enclosing routine's DATA, the owner's
        // DATA, then the window around the caret. With hashOnly, the DATA pieces travel as their map hash.
        Func<int, int, string, SlicePiece> data = (start, dataEnd, hash) => hashOnly
            ? new SlicePiece { Start = start, Hash = hash }
            : new SlicePiece(start, Lines(rawLines, start, dataEnd));
        pieces.Add(data(proc.Start, proc.DataEnd, proc.DataHash));
        var rt = proc.RoutineSpans.LastOrDefault(r => r.Start <= line1);
        if (rt != null) pieces.Add(data(rt.Start, rt.DataEnd, rt.DataHash));
        if (owner != null) pieces.Add(data(owner.Start, owner.DataEnd, owner.DataHash));
        int lo = Math.Max(proc.DataEnd + 1, line1 - window), hi = Math.Min(proc.End, line1 + window);
        if (lo <= hi) pieces.Add(new SlicePiece(lo, Lines(rawLines, lo, hi)));
    }

    static string Items(List<LspClient.CompletionItemInfo> items)
    {
        return string.Join("\n", items.Select(i => i.Label + "|" + i.Kind + "|" + i.Detail + "|" + i.Documentation + "|" + i.InsertText));
    }

    static string Hov(LocalHoverResult h) { return h == null ? "(null)" : h.Authoritative + "|" + h.Kind + "|" + h.Markdown; }

    static string Mem(LocalMemberAccess m) { return m == null ? "(null)" : m.Instance + "|" + m.Partial + "|" + m.LocalClass + "|" + m.BaseType; }

    static void Parity(string name, string buf, int step, bool capped, int window = 3, bool hashOnly = false)
    {
        var map = LocalScopeIndex.BuildSpanMap(buf);
        string header;
        LocalScopeIndex.TryGetHeaderText(map.HeaderHash, out header);
        var raw = buf.Split('\n');   // keep '\r' - exactly what getValueInRange hands back per line
        int carets = 0, diffs = 0;
        string firstDiff = null;
        for (int l = 0; l < raw.Length; l += step)
        {
            string line = raw[l].TrimEnd('\r');
            List<SlicePiece> pieces;
            List<string> routines;
            Slice(map, raw, l, capped, window, hashOnly, out pieces, out routines);
            if (hashOnly && LocalScopeIndex.MissingPieces(pieces).Count > 0) { diffs++; firstDiff = firstDiff ?? "a map hash did not resolve"; }
            var cols = new SortedSet<int> { line.Length };
            for (int c = 1; c < line.Length; c++)
            {
                bool word = char.IsLetterOrDigit(line[c]) || line[c] == '_';
                bool prevWord = char.IsLetterOrDigit(line[c - 1]) || line[c - 1] == '_';
                if (word != prevWord || line[c - 1] == '.' || line[c - 1] == ':') cols.Add(c);
                if (cols.Count > 8) break;
            }
            foreach (int col in cols)
            {
                carets++;
                string fc = Items(LocalScopeIndex.Complete(buf, l, col, null));
                // The header by the map's hash (the wire form); the header-text overloads must agree.
                string sc = Items(LocalScopeIndex.Complete(map.HeaderHash, null, pieces, routines, l, col, null));
                string fh = Hov(LocalScopeIndex.Hover(buf, l, col, "m.clw"));
                string sh = Hov(LocalScopeIndex.Hover(map.HeaderHash, null, pieces, routines, l, col, "m.clw"));
                string fm = Mem(LocalScopeIndex.GetMemberAccess(buf, l, col));
                string sm = Mem(LocalScopeIndex.GetMemberAccess(map.HeaderHash, null, pieces, routines, l, col));
                if (!capped)
                {
                    string sc2 = Items(LocalScopeIndex.Complete(header, pieces, routines, l, col, null));
                    string sm2 = Mem(LocalScopeIndex.GetMemberAccess(header, pieces, routines, l, col));
                    if (sc2 != sc) sc = "(header-text overload differs) " + sc2;
                    if (sm2 != sm) sm = "(header-text overload differs) " + sm2;
                }
                if (fc != sc || fh != sh || fm != sm)
                {
                    diffs++;
                    if (firstDiff == null)
                        firstDiff = "line " + (l + 1) + " col " + col + " '" + line.Trim() + "'\n      full:  " +
                                    (fc != sc ? fc.Replace("\n", " ; ") : fh != sh ? fh : fm) + "\n      slice: " +
                                    (fc != sc ? sc.Replace("\n", " ; ") : fh != sh ? sh : sm);
                }
            }
        }
        Check(diffs == 0 && carets > 0, "R11.parity " + name, carets + " carets, " + diffs + " differ" + (firstDiff == null ? "" : "; first: " + firstDiff));
    }

    /// <summary>The buffer was edited after the map (a routine typed inside ProcA): the slice, built from
    /// the OLD map's spans over the NEW text, still sees the new routine - as the full buffer does.</summary>
    static void ParityEdited(string before, string after)
    {
        var map = LocalScopeIndex.BuildSpanMap(before);
        string header;
        LocalScopeIndex.TryGetHeaderText(map.HeaderHash, out header);
        var raw = after.Split('\n');
        int l = Array.FindIndex(raw, x => x.TrimEnd('\r') == "  DO New");
        var proc = map.Procs.First(p => p.Name == "ProcA");
        // The span grew by two lines; the page's decorations track that, so its end moves with the edit.
        var pieces = new List<SlicePiece> { new SlicePiece(proc.Start, Lines(raw, proc.Start, proc.End + 2)) };
        string full = Items(LocalScopeIndex.Complete(after, l, "  DO New".Length, null));
        string slice = Items(LocalScopeIndex.Complete(header, pieces, proc.Routines, l, "  DO New".Length, null));
        Check(full == slice && full.StartsWith("NewRtn|"), "R11.edited", "a routine typed since the map: full [" + full.Replace("\n", " ; ") + "] slice [" + slice.Replace("\n", " ; ") + "]");
    }
}
