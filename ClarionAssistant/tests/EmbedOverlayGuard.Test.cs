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
                                     "toggle_comment", "undo", "redo", "save_file", "close_file" })
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
        r = EmbedOverlayGuard.Run("replace_range", Const(true), () => { throw new Exception("probe"); }, handler, null) as string;
        Ok("covered probe throws -> editor write refused (fail closed)", ran == 0 && r != null && r.StartsWith("Error"), r);

        // No CA Embeditor live -> nothing can be covered, so the active-view probe (a UI round-trip once
        // fc420c30 moves these tools off the UI thread) is not even asked.
        int coveredCalls = 0;
        mk("ok");
        r = EmbedOverlayGuard.Run("replace_range", Const(false), () => { coveredCalls++; return true; }, handler, null) as string;
        Ok("no CA Embeditor -> editor write runs without asking the active-view probe",
            ran == 1 && r == "ok" && coveredCalls == 0, coveredCalls + " probe call(s), " + r);

        // --- native embeditor save/cancel: refused while the CA Embeditor holds the embed ---
        foreach (var tool in new[] { "save_and_close_embeditor", "cancel_embeditor" })
        {
            mk("Embeditor closed.");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(false), handler, null) as string;
            Ok(tool + " + CA Embeditor live -> refused, handler not run",
                ran == 0 && r != null && r.StartsWith("Error") && r.Contains("CA Embeditor") && r.Contains("Nothing was done"), r);
            Ok(tool + " refusal tells Claude to ask the developer",
                r != null && r.Contains("Ask the developer to save or close the CA Embeditor"), r);

            mk("Embeditor closed.");
            r = EmbedOverlayGuard.Run(tool, Const(false), Const(false), handler, null) as string;
            Ok(tool + ", no CA Embeditor -> runs unchanged", ran == 1 && r == "Embeditor closed.", r);
        }
        mk("x");
        r = EmbedOverlayGuard.Run("save_and_close_embeditor", Const(true), Const(false), handler, null) as string;
        Ok("save refusal says the native save would lack the developer's edits", r != null && r.Contains("WITHOUT"), r);

        // --- editor reads: allowed, with a note only when they read the covered native document ---
        foreach (var tool in new[] { "get_active_file", "get_selected_text", "get_word_under_cursor", "get_cursor_position",
                                     "get_line_text", "get_lines_range", "find_in_file", "is_modified" })
        {
            mk("line text");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(true), handler, null) as string;
            Ok(tool + " on the covered native document -> runs, with the note first",
                ran == 1 && r != null && r.StartsWith("NOTE: the active editor is the CA Embeditor") && r.EndsWith("line text"), r);

            mk("line text");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(false), handler, null) as string;
            Ok(tool + " in another editor while a CA Embeditor is open -> no note", r == "line text", r);

            mk("line text");
            r = EmbedOverlayGuard.Run(tool, Const(false), Const(true), handler, null) as string;
            Ok(tool + ", no CA Embeditor -> no note", r == "line text", r);
        }

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

        // --- fix (2): routable (the CA Embeditor's page is ready) -> routing replaces the refusals ---
        mk("Wrote to embed at line 12.");
        r = EmbedOverlayGuard.Run("write_embed_content", Const(true), Const(false), Const(true), handler, null) as string;
        Ok("routable: write_embed_content passes to its (routed) handler", ran == 1 && r == "Wrote to embed at line 12.", r);
        mk("x");
        r = EmbedOverlayGuard.Run("write_embed_content", Const(true), Const(false), () => { throw new Exception("probe"); }, handler, null) as string;
        Ok("routable probe throws -> NOT routable: refused (fail closed)", ran == 0 && r != null && r.StartsWith("Error"), r);
        mk("x");
        r = EmbedOverlayGuard.Run("write_embed_content", Const(true), Const(false), Const(false), handler, null) as string;
        Ok("page not ready -> the fix (1) refusal stands", ran == 0 && r != null && r.Contains("Nothing was written"), r);
        mk("src");
        r = EmbedOverlayGuard.Run("get_embeditor_source", Const(true), Const(false), Const(true), handler, null) as string;
        Ok("routable: embed read gets no native-buffer note (the route adds its lineBase)", r == "src", r);
        foreach (var tool in new[] { "insert_text_at_cursor", "replace_text", "replace_range", "delete_range", "toggle_comment", "undo", "redo" })
        {
            mk("ok");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(true), Const(true), handler, null) as string;
            Ok("routable: " + tool + " on the covered view passes to its (routed) handler", ran == 1 && r == "ok", r);
        }
        foreach (var tool in new[] { "save_file", "save_and_close_embeditor" })
        {
            mk("saved");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(true), Const(true), handler, null) as string;
            Ok("routable: " + tool + " passes to its (routed) save", ran == 1 && r == "saved", r);
            mk("saved");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(true), Const(false), handler, null) as string;
            Ok("NOT routable: " + tool + " is still refused (fail closed)", ran == 0 && r != null && r.StartsWith("Error"), r);
        }
        foreach (var tool in new[] { "close_file", "cancel_embeditor" })
        {
            mk("ok");
            r = EmbedOverlayGuard.Run(tool, Const(true), Const(true), Const(true), handler, null) as string;
            Ok("routable: " + tool + " is STILL refused (discarding is the developer's call)", ran == 0 && r != null && r.StartsWith("Error"), r);
        }
        mk("line text");
        r = EmbedOverlayGuard.Run("get_lines_range", Const(true), Const(true), Const(true), handler, null) as string;
        Ok("routable: editor read on the covered view has no note (it reads Monaco)", r == "line text", r);
        int routableCalls = 0;
        mk("x");
        EmbedOverlayGuard.Run("write_embed_content", Const(false), Const(false), () => { routableCalls++; return true; }, handler, null);
        Ok("no CA Embeditor -> the routable probe is not asked", routableCalls == 0 && ran == 1, routableCalls.ToString());

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
                Regex.IsMatch(body, @"EmbedOverlayGuard\.Run\(\s*name,\s*CaEmbeditorLiveProbe,\s*ActiveEditorCoveredProbe,\s*EmbedRoutableProbe,\s*\(\)\s*=>\s*tool\.Handler\("),
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

            // fix (2) wiring
            foreach (var t in new[] { "get_embeditor_source", "search_embeditor_source", "get_embed_content", "write_embed_content" })
            {
                var tm = Regex.Match(reg, "Name = \"" + t + "\",(.*?)\\n            \\}\\);", RegexOptions.Singleline);
                string tb = tm.Success ? tm.Groups[1].Value : "";
                Ok(t + " routes through EmbedRouter.Run, off the UI thread",
                    tb.Contains("EmbedRouter.Run(\"" + t + "\"") && Regex.IsMatch(tb, @"RequiresUiThread\s*=\s*false"), tm.Success ? null : "tool not found");
            }
            string lsp = File.ReadAllText(Path.Combine(dir, "LspAutostartCommand.cs"));
            Ok("the editor router's resolver is COMPOSED: CA Editor first, then the covered CA Embeditor",
                Regex.IsMatch(lsp, @"ActiveOverlayResolver\s*=\s*\(\)\s*=>\s*MonacoClarionEditor\.ResolveActiveOverlay\(\)\s*\?\?\s*Terminal\.ModernEmbeditorViewContent\.ResolveCoveredEmbedOverlay\(\)"), null);
            Ok("a native write_embed_content result names its procedure (NativeEmbedProcedure wired, the handler uses Named)",
                lsp.Contains("EmbedToolRouter.NativeEmbedProcedure = ") && lsp.Contains("ModernEmbeditorLauncher.ProcNameFromSource(source, null)")
                && reg.Contains("EmbedToolRouter.Named(_appTree.WriteEmbedContentByLine(line, code)"), null);
            Ok("the embed router's resolver, column lookup and the routable probe are wired at addin start",
                lsp.Contains("EmbedToolRouter.LiveEmbedResolver = Terminal.ModernEmbeditorViewContent.ResolveLiveEmbedChannel") &&
                lsp.Contains("EmbedToolRouter.NativeEmbedColumn = ") &&
                lsp.Contains("McpToolRegistry.EmbedRoutableProbe = () => Terminal.ModernEmbeditorViewContent.EmbedRoutingReady"), null);
            // routed save: off the UI thread only when routable; the native path keeps its UI thread and token
            var sm = Regex.Match(reg, "Name = \"save_and_close_embeditor\",(.*?)\\n            \\}\\);", RegexOptions.Singleline);
            string sb2 = sm.Success ? sm.Groups[1].Value : "";
            Ok("save_and_close_embeditor: UI-bound, off it only when the CA Embeditor is routable",
                Regex.IsMatch(sb2, @"RequiresUiThread\s*=\s*true") && sb2.Contains("OffUiWhen = CaEmbeditorRoutable"), null);
            Ok("save_and_close_embeditor: routed via EmbedRouter (never the native save off the UI thread), native keeps TryCommit",
                sb2.Contains("EmbedRouter.Run(\"save_and_close_embeditor\"") && sb2.Contains("ov => ov.SaveAndClose()")
                && sb2.Contains("McpCallContext.TryCommit()") && sb2.Contains("nothing was saved"), null);
            Ok("RequiresUiThread(name) honours OffUiWhen per call",
                Regex.IsMatch(reg, @"if \(tool\.OffUiWhen == null\) return true;\s*try \{ return !tool\.OffUiWhen\(\); \}\s*catch \{ return true; \}"), null);
            Ok("the CA Embeditor channel's save waits on EmbedSaveFinished (the page cannot answer a successful save)",
                Regex.IsMatch(mevc, @"if \(action == ""save""\)") && mevc.Contains("EmbedSaveWait.Run(ProcedureName")
                && mevc.Contains("h => EmbedSaveFinished += h, h => EmbedSaveFinished -= h"), null);

            // The routed save recognises 1565ef7b's busy refusal by its text (no request id until dc4f7115): the gate's
            // message must keep starting with the prefix EmbedSaveWait matches.
            string flow = File.Exists(Path.Combine(dir, @"Services\EmbedSaveFlow.cs")) ? File.ReadAllText(Path.Combine(dir, @"Services\EmbedSaveFlow.cs")) : "";
            string router = File.ReadAllText(Path.Combine(dir, @"Services\EmbedToolRouter.cs"));
            var busyM = Regex.Match(flow, "public const string BusyMessage = \"([^\"]*)\"");
            var prefM = Regex.Match(router, "public const string InProgressPrefix = \"([^\"]*)\"");
            Ok("1565ef7b's EmbedSaveGate.BusyMessage starts with EmbedSaveWait.InProgressPrefix",
                busyM.Success && prefM.Success && busyM.Groups[1].Value.StartsWith(prefM.Groups[1].Value, StringComparison.OrdinalIgnoreCase),
                (busyM.Success ? busyM.Groups[1].Value : "BusyMessage not found") + " / " + (prefM.Success ? prefM.Groups[1].Value : "prefix not found"));

            string ats = File.ReadAllText(Path.Combine(dir, @"Services\AppTreeService.cs"));
            Ok("native search_embeditor_source shares EmbedSlotText.Search (one format for both editors)",
                ats.Contains("return EmbedSlotText.Search(source, pattern, contextLines);"), null);

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
