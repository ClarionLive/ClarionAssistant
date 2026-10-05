using System;
using System.IO;

// 1565ef7b: pins the two ORDERING facts EmbedSaveFlow.Test cannot see, because they live in IDE-coupled code:
//   1. RunSaveRoundTrip's overlay branch must not detach the CA Embeditor before the save has decided. The only
//      DetachOverlay() in that branch is the one inside the beforeClose callback handed to SaveLive.
//   2. ModernEmbeditorSaver.SaveLive must never CancelEmbeditor (a cancel there discarded the developer's text).
// Both were violated on master 3904549 (the live repro of 73bd1f03 Case A); this scan is red there.
//
// Run:  tests\Run-Tests.ps1   (arg 0 = the ClarionAssistant project dir)
static class EmbedSaveOrderSourceScan
{
    static int fail = 0;
    static void Ok(string name, bool cond, string detail = null)
    {
        Console.WriteLine((cond ? "  [ok]   " : "  [FAIL] ") + name + (cond || detail == null ? "" : "  -> " + detail));
        if (!cond) fail++;
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : ".";
        string view = File.ReadAllText(Path.Combine(root, "Terminal", "ModernEmbeditorViewContent.cs"));
        string saver = File.ReadAllText(Path.Combine(root, "Services", "ModernEmbeditorSaver.cs"));

        // ---- 1. the overlay branch ----
        int branch = view.IndexOf("if (_embedOverlay && live)", StringComparison.Ordinal);
        Ok("overlay save branch found", branch >= 0);
        if (branch >= 0)
        {
            int end = view.IndexOf("return;", branch, StringComparison.Ordinal);
            string body = view.Substring(branch, (end > branch ? end : view.Length) - branch);
            int save = body.IndexOf("ModernEmbeditorSaver.SaveLive(", StringComparison.Ordinal);
            int detach = body.IndexOf("DetachOverlay()", StringComparison.Ordinal);
            Ok("overlay branch calls SaveLive", save >= 0);
            Ok("no DetachOverlay() before SaveLive has run (detach only via beforeClose)",
                save >= 0 && (detach < 0 || detach > save),
                "DetachOverlay at +" + detach + ", SaveLive at +" + save);
            Ok("detach is handed to SaveLive as its beforeClose callback",
                save >= 0 && body.IndexOf("=>", save, StringComparison.Ordinal) > save && detach > save);
        }

        // ---- 2. SaveLive never cancels ----
        int sl = saver.IndexOf("SaveLive(string procName", StringComparison.Ordinal);
        Ok("SaveLive found", sl >= 0);
        if (sl >= 0)
        {
            int next = saver.IndexOf("public static", sl + 1, StringComparison.Ordinal);
            string body = saver.Substring(sl, (next > sl ? next : saver.Length) - sl);
            Ok("SaveLive never calls CancelEmbeditor", body.IndexOf("CancelEmbeditor", StringComparison.Ordinal) < 0);
        }

        Console.WriteLine(fail == 0 ? "all passed" : fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
