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
        Check(!LocalScopeIndex.RegisterHeader("stale-hash", text + "ModX LONG\r\n") && LocalScopeIndex.TryGetHeaderText("stale-hash", out text),
              "R11.5", "headerSync: a text that does not hash to the page's hash is still cached under it (and reports the mismatch)");

        Console.WriteLine("parity: full buffer == slice, every caret");
        Parity("two-procs", two, 1, capped: false);
        Parity("two-procs (LF)", two.Replace("\r\n", "\n"), 1, capped: false);
        string edited = two.Replace("  Loc\r\n", "  Loc\r\nNewRtn               ROUTINE\r\n  DO New\r\n");   // a routine typed since the map
        Parity("two-procs, capped windows", two, 1, capped: true);
        // A DO far above its routine: capped, the routine header is outside the window, so only the map's
        // routine list can answer it.
        Parity("two-procs, DO far from its routine, capped", two.Replace("  Loc\r\n", "  Loc\r\n  DO Rt\r\n"), 1, capped: true);
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
            // caps it (3000 lines: the procedure's header..DATA plus a window around the caret).
            var raw = buf.Split('\n');
            var big = rm.Procs.OrderByDescending(p => p.End - p.Start).First();
            int caret1 = big.Start + (big.End - big.Start) * 2 / 3;
            var pcs = new List<SlicePiece> { new SlicePiece(big.Start, Lines(raw, big.Start, big.DataEnd)) };
            int wlo = Math.Max(big.DataEnd + 1, caret1 - 1500), whi = Math.Min(big.End, caret1 + 1500);
            pcs.Add(new SlicePiece(wlo, Lines(raw, wlo, whi)));
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
            Parity(Path.GetFileName(real) + " capped", buf, Math.Max(1, lineCount / 60) + 7, capped: true);
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
    static void Slice(SpanMap map, string[] rawLines, int line0, bool capped,
                      out List<SlicePiece> pieces, out List<string> routines)
    {
        pieces = new List<SlicePiece>();
        routines = null;
        int line1 = line0 + 1;
        var proc = map.Procs.LastOrDefault(p => p.Start <= line1 && line1 <= p.End);
        if (proc == null) return;
        routines = proc.Routines.ToList();
        if (proc.Owner.HasValue)
        {
            var o = map.Procs[proc.Owner.Value];
            pieces.Add(new SlicePiece(o.Start, Lines(rawLines, o.Start, o.DataEnd)));
        }
        if (!capped || proc.End - proc.DataEnd < 8)
        {
            pieces.Add(new SlicePiece(proc.Start, Lines(rawLines, proc.Start, proc.End)));
            return;
        }
        // Capped: the procedure's own header..DATA, the enclosing routine's header..DATA, plus a window
        // around the caret.
        pieces.Add(new SlicePiece(proc.Start, Lines(rawLines, proc.Start, proc.DataEnd)));
        var rt = proc.RoutineSpans.LastOrDefault(r => r.Start <= line1);
        if (rt != null) pieces.Add(new SlicePiece(rt.Start, Lines(rawLines, rt.Start, rt.DataEnd)));
        int lo = Math.Max(proc.DataEnd + 1, line1 - 3), hi = Math.Min(proc.End, line1 + 3);   // a small window, so routines fall outside it
        if (lo <= hi) pieces.Add(new SlicePiece(lo, Lines(rawLines, lo, hi)));
    }

    static string Items(List<LspClient.CompletionItemInfo> items)
    {
        return string.Join("\n", items.Select(i => i.Label + "|" + i.Kind + "|" + i.Detail + "|" + i.Documentation + "|" + i.InsertText));
    }

    static string Hov(LocalHoverResult h) { return h == null ? "(null)" : h.Authoritative + "|" + h.Kind + "|" + h.Markdown; }

    static string Mem(LocalMemberAccess m) { return m == null ? "(null)" : m.Instance + "|" + m.Partial + "|" + m.LocalClass + "|" + m.BaseType; }

    static void Parity(string name, string buf, int step, bool capped)
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
            Slice(map, raw, l, capped, out pieces, out routines);
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
                string sc = Items(LocalScopeIndex.Complete(header, pieces, routines, l, col, null));
                string fh = Hov(LocalScopeIndex.Hover(buf, l, col, "m.clw"));
                string sh = Hov(LocalScopeIndex.Hover(header, pieces, routines, l, col, "m.clw"));
                SlicePiece owner = pieces.Count > 1 && !capped ? pieces[0] : null;
                SlicePiece span = pieces.Count > 0 ? pieces[pieces.Count - 1] : null;
                string fm = Mem(LocalScopeIndex.GetMemberAccess(buf, l, col));
                string sm = capped ? fm : Mem(LocalScopeIndex.GetMemberAccess(header, owner, span, routines, l, col));
                if (!capped && pieces.Count > 0)
                {
                    // The contract's two-piece overloads must agree with the list overloads.
                    string sc2 = Items(LocalScopeIndex.Complete(header, owner, span, routines, l, col, null));
                    if (sc2 != sc) sc = "(two-piece overload differs) " + sc2;
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
