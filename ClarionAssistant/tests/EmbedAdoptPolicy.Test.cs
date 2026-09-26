using System;
using ClarionAssistant.Services;

// PR #198 points 2 and 3: apply_embed_edits adopting an embeditor the developer already has open.
// Adoption ends in SaveAndCloseEmbeditor, which persists the WHOLE buffer, so it must be refused when
//   * the native buffer is dirty, or its dirty flag is unreadable (it would save the developer's edits), and
//   * the CA Embeditor (Monaco overlay or live tab) holds the embed (the write would land invisibly behind
//     Monaco and be overwritten by its save; Monaco's own unsaved edits never reach the native dirty flag,
//     so this must win even when the native flag reads clean).
// The live IDE facts are gathered by ModernEmbeditorLauncher.TryAdoptOpenEmbeditor; the decision is the
// pure EmbedAdoptPolicy.Decide exercised here.
//
// Run:  tests\Run-Tests.ps1
static class EmbedAdoptPolicyTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static EmbedAdoptDecision D(bool open, string readErr, string openProc, string target, bool caLive, bool? dirty,
        out string err)
    {
        return EmbedAdoptPolicy.Decide(open, readErr, openProc, target, caLive, dirty, out err);
    }

    static int Main()
    {
        string err;

        // --- nothing open: the pre-PR path, no error ---
        var r = D(false, null, null, "UpdateNalog", false, null, out err);
        Ok("nothing open -> OpenFresh", r == EmbedAdoptDecision.OpenFresh && err == null, r + " / " + err);

        // --- the happy adopt: same procedure, clean, no CA Embeditor ---
        r = D(true, null, "UpdateNalog", "updatenalog ", false, false, out err);
        Ok("same proc, clean, no overlay -> Adopt (name match case/space-insensitive)",
            r == EmbedAdoptDecision.Adopt && err == null, r + " / " + err);

        // --- point 2: never save the developer's unsaved edits ---
        r = D(true, null, "UpdateNalog", "UpdateNalog", false, true, out err);
        Ok("dirty buffer -> Refuse", r == EmbedAdoptDecision.Refuse, r.ToString());
        Ok("dirty refusal says the developer has unsaved changes",
            err != null && err.Contains("UNSAVED changes") && err.Contains("UpdateNalog") && err.Contains("nothing was written"), err);

        r = D(true, null, "UpdateNalog", "UpdateNalog", false, null, out err);
        Ok("unreadable dirty flag -> Refuse (fail closed)", r == EmbedAdoptDecision.Refuse, r.ToString());
        Ok("unreadable refusal says it could not confirm", err != null && err.Contains("Could not confirm"), err);

        // --- point 3: never write behind the CA Embeditor ---
        r = D(true, null, "UpdateNalog", "UpdateNalog", true, false, out err);
        Ok("CA Embeditor live over a CLEAN native buffer -> Refuse", r == EmbedAdoptDecision.Refuse, r.ToString());
        Ok("overlay refusal names the CA Embeditor and says nothing was written",
            err != null && err.Contains("CA Embeditor") && err.Contains("Nothing was written"), err);

        r = D(true, null, "UpdateNalog", "UpdateNalog", true, true, out err);
        Ok("CA Embeditor + dirty -> Refuse with the overlay reason (checked first)",
            r == EmbedAdoptDecision.Refuse && err != null && err.Contains("CA Embeditor"), err);

        // --- the pre-existing identity refusals still hold ---
        r = D(true, null, "BrowseAuthors", "UpdateNalog", false, false, out err);
        Ok("different procedure -> Refuse, names both",
            r == EmbedAdoptDecision.Refuse && err != null && err.Contains("BrowseAuthors") && err.Contains("UpdateNalog"), err);

        r = D(true, null, null, "UpdateNalog", false, false, out err);
        Ok("unidentified procedure -> Refuse", r == EmbedAdoptDecision.Refuse && err != null && err.Contains("unidentified"), err);

        r = D(true, "no text area", null, "UpdateNalog", false, false, out err);
        Ok("unreadable source -> Refuse", r == EmbedAdoptDecision.Refuse && err != null && err.Contains("no text area"), err);

        // A different procedure under the overlay is refused as a different procedure (still refused).
        r = D(true, null, "BrowseAuthors", "UpdateNalog", true, true, out err);
        Ok("different procedure under the overlay -> Refuse", r == EmbedAdoptDecision.Refuse, r.ToString());

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
