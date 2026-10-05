using System;
using System.IO;
using System.Text.RegularExpressions;
using ClarionAssistant.Services;

// 73bd1f03: the embed and editor tools must never write the native embeditor document hidden behind the
// CA Embeditor. Such a write is invisible to the developer and is lost on the CA Embeditor's save or close,
// and a write that changes a slot's line count makes that save cancel the embed, taking the developer's
// unsaved Monaco edits with it.
//
// Part 1 drives EmbedOverlayGuard.Run, the whole decision McpToolRegistry.ExecuteTool delegates to.
// Part 2 checks that the decision is actually wired: ExecuteTool goes through Run, both addin hosts supply
// the probes, and both projects compile the guard. It takes the ClarionAssistant project dir as args[0],
// so pointing it at a tree without the fix shows it failing.
//
// Run:  tests\Run-Tests.ps1
static class EmbedOverlayGuardTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static Func<bool> Const(bool v) { return () => v; }

    static int Main(string[] args)
    {
        int ran = 0;
        Func<object> handler = null;
        Func<string, object> mk = res => { ran = 0; handler = () => { ran++; return res; }; return null; };

        // --- write_embed_content: refused while the CA Embeditor holds the embed ---
        mk("Wrote to embed at line 12.");
        var r = EmbedOverlayGuard.Run("write_embed_content", Const(true), Const(false), handler, null) as string;
        Ok("write_embed_content + CA Embeditor live -> refused", r != null && r.StartsWith("Error") && r.Contains("CA Embeditor"), r);
        Ok("refused write never reaches the handler", ran == 0, "handler ran " + ran + "x");
        Ok("refusal says nothing was written and what to do instead",
            r != null && r.Contains("Nothing was written") && r.Contains("apply_embed_edits"), r);

        mk("Wrote to embed at line 12.");
        r = EmbedOverlayGuard.Run("write_embed_content", Const(false), Const(false), handler, null) as string;
        Ok("write_embed_content, no CA Embeditor -> runs unchanged", ran == 1 && r == "Wrote to embed at line 12.", r);

        mk("Wrote to embed at line 12.");
        r = EmbedOverlayGuard.Run("write_embed_content", () => { throw new InvalidOperationException("probe"); },
            Const(false), handler, null) as string;
        Ok("CA Embeditor probe throws -> refused (fail closed)", ran == 0 && r != null && r.StartsWith("Error"), r);

        mk("Wrote to embed at line 12.");
        r = EmbedOverlayGuard.Run("write_embed_content", null, null, handler, null) as string;
        Ok("no probes (standalone host) -> runs", ran == 1 && r == "Wrote to embed at line 12.", r);

        string logged = null;
        mk("x");
        EmbedOverlayGuard.Run("write_embed_content", Const(true), Const(false), handler, m => logged = m);
        Ok("a refusal is logged", logged != null && logged.Contains("write_embed_content"), logged);

        // --- editor writes: refused only when the active editor IS the covered native document ---
        foreach (var tool in new[] { "insert_text_at_cursor", "replace_text", "replace_range", "delete_range",
                                     "toggle_comment", "undo", "redo", "save_file" })
        {
            mk("ok");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(true), handler, null) as string;
            Ok(tool + " on the covered native document -> refused", ran == 0 && r != null && r.StartsWith("Error"), r);

            // A CA Embeditor is open somewhere, but the developer is in another editor: that is a real
            // target, and refusing it would break the editor tools for no reason.
            mk("ok");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(false), handler, null) as string;
            Ok(tool + " in another editor while a CA Embeditor is open -> runs", ran == 1 && r == "ok", r);
        }

        mk("ok");
        r = EmbedOverlayGuard.Run("replace_range", Const(false), () => { throw new Exception("probe"); }, handler, null) as string;
        Ok("covered probe throws -> editor write refused (fail closed)", ran == 0 && r != null && r.StartsWith("Error"), r);

        // --- embed reads: allowed, but say they lack the developer's unsaved Monaco edits ---
        foreach (var tool in new[] { "get_embeditor_source", "search_embeditor_source", "get_embed_content" })
        {
            mk("«E:12/» some source");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(false), handler, null) as string;
            Ok(tool + " + CA Embeditor live -> runs, with the native-buffer note first",
                ran == 1 && r != null && r.StartsWith("NOTE:") && r.Contains("unsaved") && r.EndsWith("«E:12/» some source"), r);

            mk("«E:12/» some source");
            r = EmbedOverlayGuard.Run(tool, Const(false), Const(false), handler, null) as string;
            Ok(tool + ", no CA Embeditor -> no note", r == "«E:12/» some source", r);

            mk("Error: No PWEE embeditor is currently open.");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(false), handler, null) as string;
            Ok(tool + " error result -> passed through without a note", r == "Error: No PWEE embeditor is currently open.", r);
        }

        // --- everything else: untouched, and the IDE is not even asked ---
        int probes = 0;
        Func<bool> counting = () => { probes++; return true; };
        mk("app info");
        r = EmbedOverlayGuard.Run("get_app_info", counting, counting, handler, null) as string;
        Ok("unguarded tool runs unchanged", ran == 1 && r == "app info", r);
        Ok("unguarded tool never probes the IDE", probes == 0, probes + " probe call(s)");

        mk("x");
        EmbedOverlayGuard.Run("go_to_line", counting, counting, handler, null);
        Ok("navigation (go_to_line) is not refused", ran == 1, null);

        // --- wiring (source scan) ---
        string dir = args.Length > 0 ? args[0] : null;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Ok("project dir passed as args[0]", false, dir);
        }
        else
        {
            string reg = File.ReadAllText(Path.Combine(dir, @"Services\McpToolRegistry.cs"));
            var m = Regex.Match(reg, @"public object ExecuteTool\(string name, Dictionary<string, object> arguments\)\s*\{(.*?)\n        \}",
                RegexOptions.Singleline);
            string body = m.Success ? m.Groups[1].Value : "";
            Ok("ExecuteTool found", m.Success, null);
            Ok("ExecuteTool routes the handler through EmbedOverlayGuard.Run",
                Regex.IsMatch(body, @"EmbedOverlayGuard\.Run\(\s*name,\s*CaEmbeditorLiveProbe,\s*ActiveEditorCoveredProbe,\s*\(\)\s*=>\s*tool\.Handler\("),
                body.Trim());
            Ok("ExecuteTool calls tool.Handler nowhere else",
                Regex.Matches(body, @"tool\.Handler\(").Count == 1, body.Trim());

            foreach (var host in new[] { "AssistantChatControl.cs", "ClassHelperControl.cs" })
            {
                string src = File.ReadAllText(Path.Combine(dir, host));
                Ok(host + " supplies CaEmbeditorLiveProbe",
                    src.Contains("McpToolRegistry.CaEmbeditorLiveProbe = () =>") && src.Contains("ModernEmbeditorViewContent.HasLiveOverlay"), null);
                Ok(host + " supplies ActiveEditorCoveredProbe",
                    src.Contains("McpToolRegistry.ActiveEditorCoveredProbe = () =>") &&
                    src.Contains("ModernEmbeditorViewContent.ActiveEditorIsCoveredByOverlay()"), null);
            }

            // fc420c30 moves the routed editor tools off the UI thread, so the covered probe must marshal
            // itself, with a BOUNDED wait (an unbounded Invoke on a busy UI thread hangs the tool call).
            string mevc = File.ReadAllText(Path.Combine(dir, @"Terminal\ModernEmbeditorViewContent.cs"));
            var pm = Regex.Match(mevc, @"internal static bool ActiveEditorIsCoveredByOverlay\(\)\s*\{(.*?)\n        \}",
                RegexOptions.Singleline);
            string probeBody = pm.Success ? pm.Groups[1].Value : "";
            Ok("covered probe marshals to the UI thread when called off it",
                probeBody.Contains("InvokeRequired") && probeBody.Contains("BeginInvoke("), probeBody.Trim());
            Ok("covered probe waits with a timeout and throws on expiry (fail closed)",
                Regex.IsMatch(probeBody, @"WaitOne\(\s*CoveredProbeTimeoutMs\s*\)") && probeBody.Contains("throw new TimeoutException"),
                probeBody.Trim());
            Ok("covered probe never uses an unbounded Invoke",
                !Regex.IsMatch(probeBody, @"(?<!Begin)Invoke\("), probeBody.Trim());
            Ok("_liveInstance is volatile (HasLiveOverlay is read off the UI thread)",
                Regex.IsMatch(mevc, @"private static volatile ModernEmbeditorViewContent _liveInstance;"), null);

            Ok("addin project compiles the guard",
                File.ReadAllText(Path.Combine(dir, "ClarionAssistant.csproj")).Contains(@"Services\EmbedOverlayGuard.cs"), null);
            Ok("standalone server compiles the guard (it shares McpToolRegistry.cs)",
                File.ReadAllText(Path.Combine(dir, @"mcp-server\ClarionMcpServer.csproj")).Contains(@"Services\EmbedOverlayGuard.cs"), null);
        }

        Console.WriteLine();
        Console.WriteLine("EmbedOverlayGuard: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
