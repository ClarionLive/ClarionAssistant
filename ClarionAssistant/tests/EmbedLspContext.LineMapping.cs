// The CA Embeditor's line mapping between Monaco and the language server (EmbedLspContext).
//
// Every LSP request from the embeditor sends WrapBuffer(buffer) and a line mapped by the context's
// offset; every answer comes back through the same offset. The two must agree: the Monaco line L has
// to be line L-1+offset of the wrapped buffer, or completion, hover, definition and diagnostics all
// land on the wrong line. WrapBuffer passes a buffer whose FIRST line is MEMBER/PROGRAM through
// untouched, but the offset used to be a constant 1 - so for that buffer every position was one line
// LOW (a completion at "  st." asked the server about the line below it).
//
// Checked for every line of three buffers: the usual embed shape (blank lines, then MEMBER), a buffer
// opening with MEMBER on line 1, and one opening with PROGRAM. Round trip too: mapping to the LSP
// line and back returns the Monaco line.
//
// The offset is read through reflection so this same file also runs against the pre-fix class (a
// LineOffset property) - that is how it was shown to fail before the fix.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Reflection;
using ClarionAssistant.Services;

static class EmbedLspContextLineMapping
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string message)
    {
        _assertions++;
        if (!ok) Failures.Add(message);
    }

    static int OffsetFor(EmbedLspContext ctx, string buffer)
    {
        var m = typeof(EmbedLspContext).GetMethod("LineOffsetFor", new[] { typeof(string) });
        if (m != null) return (int)m.Invoke(ctx, new object[] { buffer });
        var p = typeof(EmbedLspContext).GetProperty("LineOffset");   // pre-fix shape
        return (int)p.GetValue(ctx, null);
    }

    static string[] Lines(string text) { return text.Replace("\r\n", "\n").Split('\n'); }

    static int Main()
    {
        var ctor = typeof(EmbedLspContext).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(string), typeof(string) }, null);
        var ctx = (EmbedLspContext)ctor.Invoke(new object[] { @"C:\app\app002.clw", "   MEMBER('app.clw')" });

        var buffers = new Dictionary<string, string>
        {
            { "blank lines, then MEMBER (usual embed shape)", string.Join("\r\n", new[] {
                "", "", "   MEMBER('app.clw')", "", "MyProc PROCEDURE", "st   StringTheory", "  CODE", "  st.", "  RETURN" }) },
            { "MEMBER on line 1", string.Join("\r\n", new[] {
                "   MEMBER('app.clw')", "", "MyProc PROCEDURE", "st   StringTheory", "  CODE", "  st.", "  RETURN" }) },
            { "PROGRAM on line 1", string.Join("\r\n", new[] {
                "  PROGRAM", "", "  MAP", "  END", "st   StringTheory", "  CODE", "  st.", "  RETURN" }) },
        };

        foreach (var kv in buffers)
        {
            string buffer = kv.Value;
            string[] mon = Lines(buffer);
            string[] lsp = Lines(ctx.WrapBuffer(buffer));
            int off = OffsetFor(ctx, buffer);

            // The request line for the completion at "  st." must BE "  st." in what the server sees.
            int caret1 = Array.IndexOf(mon, "  st.") + 1;
            int req0 = caret1 - 1 + off;
            Check(req0 >= 0 && req0 < lsp.Length && lsp[req0] == "  st.",
                kv.Key + ": completion at Monaco line " + caret1 + " asks the server about LSP line " + req0 +
                " = '" + (req0 >= 0 && req0 < lsp.Length ? lsp[req0] : "(past end)") + "', not '  st.'");

            // Every line maps onto itself, and back.
            bool allSame = true, roundTrip = true;
            for (int l1 = 1; l1 <= mon.Length; l1++)
            {
                int l0 = l1 - 1 + off;
                if (l0 >= lsp.Length || lsp[l0] != mon[l1 - 1]) allSame = false;
                if (l0 + 1 - off != l1) roundTrip = false;
            }
            Check(allSame, kv.Key + ": some Monaco line does not map to the same text in the wrapped buffer (offset " + off + ")");
            Check(roundTrip, kv.Key + ": Monaco -> LSP -> Monaco does not return the same line");

            // What was prepended is exactly the offset.
            Check(lsp.Length - mon.Length == off,
                kv.Key + ": WrapBuffer added " + (lsp.Length - mon.Length) + " line(s) but the offset says " + off);
        }

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
