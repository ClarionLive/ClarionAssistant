// H1-a (2026-10-23): EmbedLspContext.ToEmbeditorLinks - the CA Embeditor's hover links to its OWN module.
//
// The server analyses the embeditor's text, with the MEMBER header prepended, under the module's real path,
// so its card's link to that path is a line of the WRAPPED text: shifted back by the offset it is the
// embeditor line the page reveals in place. A card from the solution index (CodeGraph reads the module on
// disk) carries disk lines: those links get ?ondisk instead, so the page opens the .clw. Links to other
// files are never touched.
//
// Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using ClarionAssistant.Services;

static class EmbedLspContextSelfLinks
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(string name, string got, string want)
    {
        _assertions++;
        if (got != want) Failures.Add(name + "\n      got:  " + got + "\n      want: " + want);
    }

    static int Main()
    {
        const string Doc = @"H:\Demo\demoleg018.clw";
        const string Card = "```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:474](file:///H:/Demo/demoleg018.clw#L474)";

        Check("server card, own module: the line is shifted back by the header offset",
            EmbedLspContext.ToEmbeditorLinks(Card, Doc, 1, false),
            "```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:474](file:///H:/Demo/demoleg018.clw#L473)");
        Check("server card, offset 0 (buffer opens with MEMBER): unchanged line",
            EmbedLspContext.ToEmbeditorLinks(Card, Doc, 0, false), Card);
        Check("encoded drive colon, other case, a title: still the own module",
            EmbedLspContext.ToEmbeditorLinks("[x](file:///h%3A/demo/DEMOLEG018.clw#L10 \"t\")", Doc, 1, false),
            "[x](file:///h%3A/demo/DEMOLEG018.clw#L9 \"t\")");
        Check("the shifted line never goes below 1",
            EmbedLspContext.ToEmbeditorLinks("[x](file:///H:/Demo/demoleg018.clw#L1)", Doc, 1, false),
            "[x](file:///H:/Demo/demoleg018.clw#L1)");
        Check("index card, own module: marked ?ondisk, line kept (a disk line)",
            EmbedLspContext.ToEmbeditorLinks(Card, Doc, 1, true),
            "```clarion\nPrepareProcedure ROUTINE\n```\n\n[demoleg018.clw:474](file:///H:/Demo/demoleg018.clw?ondisk#L474)");
        const string Other = "[demoleg.inc:3](file:///H:/Demo/demoleg.inc#L3) → [demoleg019.clw:40](file:///H:/Demo/demoleg019.clw#L40)";
        Check("links to other files: untouched (server card)", EmbedLspContext.ToEmbeditorLinks(Other, Doc, 1, false), Other);
        Check("links to other files: untouched (index card)", EmbedLspContext.ToEmbeditorLinks(Other, Doc, 1, true), Other);
        Check("a mixed card: only the own-module link changes",
            EmbedLspContext.ToEmbeditorLinks("[a](file:///H:/Demo/demoleg.inc#L3) [b](file:///H:/Demo/demoleg018.clw#L20)", Doc, 1, false),
            "[a](file:///H:/Demo/demoleg.inc#L3) [b](file:///H:/Demo/demoleg018.clw#L19)");
        Check("a same-named file in another folder is not the own module",
            EmbedLspContext.ToEmbeditorLinks("[x](file:///H:/Other/demoleg018.clw#L20)", Doc, 1, false),
            "[x](file:///H:/Other/demoleg018.clw#L20)");
        Check("no docPath: untouched", EmbedLspContext.ToEmbeditorLinks(Card, "", 1, false), Card);
        Check("null markdown: null", EmbedLspContext.ToEmbeditorLinks(null, Doc, 1, false), null);
        Check("attribute chip links are not location links: untouched",
            EmbedLspContext.ToEmbeditorLinks("[HIDE](file:///clarion-attr/edit/abc \"Add HIDE\")", Doc, 1, false),
            "[HIDE](file:///clarion-attr/edit/abc \"Add HIDE\")");

        Console.WriteLine();
        if (Failures.Count == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + Failures.Count + " of " + _assertions + " assertions:");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
