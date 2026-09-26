using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using ClarionAssistant.Services;

// PR #198: the UI-thread tool timeout in McpDispatcher.
//
//   * McpUiTimeoutPolicy.Resolve - default 30, the "Mcp.UiToolTimeoutSeconds" setting overrides it (5-600s),
//     never below a tool's declared minimum.
//   * The REAL McpDispatcher.cs waits that long: a tool on the (fake) UI thread that outlives a configured
//     5s budget times out and says "5s"; the same sleep under a declared 10s budget finishes.
//   * Pipeline round: a timed-out call is ABANDONED. A tool that reaches its commit point (TryCommit)
//     afterwards is refused, so it cannot save behind the caller's back; a call the UI thread never started
//     never runs; one already committed is reported "may still complete". Each message says which.
//   * The real McpToolRegistry.cs still declares the embed round-trip budget on the four slow tools
//     (source scan - the registry itself cannot compile outside the IDE).
// Run:  tests\Run-Tests.ps1   (arg 0 = the ClarionAssistant project dir)
static class UiTimeoutTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static string Call(McpDispatcher d, string tool)
    {
        return d.ProcessJsonRpc(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}",
            null);
    }

    static int Main(string[] args)
    {
        // ---- the pure policy ----
        Ok("default is 30s", McpUiTimeoutPolicy.Resolve(null, 0) == 30, McpUiTimeoutPolicy.Resolve(null, 0).ToString());
        Ok("setting overrides the default upward", McpUiTimeoutPolicy.Resolve("90", 0) == 90, null);
        Ok("setting overrides the default downward", McpUiTimeoutPolicy.Resolve(" 10 ", 0) == 10, null);
        Ok("garbage setting = default", McpUiTimeoutPolicy.Resolve("abc", 0) == 30, null);
        Ok("zero setting = default (cannot disable the guard)", McpUiTimeoutPolicy.Resolve("0", 0) == 30, null);
        Ok("negative setting = default", McpUiTimeoutPolicy.Resolve("-5", 0) == 30, null);
        Ok("tiny setting clamps to 5", McpUiTimeoutPolicy.Resolve("1", 0) == 5, null);
        Ok("huge setting clamps to 600", McpUiTimeoutPolicy.Resolve("99999", 0) == 600, null);
        Ok("declared beats a smaller setting", McpUiTimeoutPolicy.Resolve("30", 180) == 180, null);
        Ok("setting beats a smaller declared", McpUiTimeoutPolicy.Resolve("300", 180) == 300, null);
        Ok("declared clamps to 600", McpUiTimeoutPolicy.Resolve(null, 900) == 600, null);
        Ok("setting 10 never undercuts a declared 180", McpUiTimeoutPolicy.Resolve("10", 180) == 180, null);

        // ---- the real dispatcher honours it, and abandons what it gives up on ----
        var reg = new McpToolRegistry();
        reg.Add("fast_tool", 100, 0);
        reg.Add("slow_undeclared", 7000, 0);                              // outlives a 5s setting
        reg.Add("slow_declared", 7000, 10);                               // same sleep, declares 10s
        reg.Add("commit_late", 7000, 0, FakeToolMode.CommitAtEnd);        // reaches its save after the timeout
        reg.Add("commit_early", 7000, 0, FakeToolMode.CommitAtStart);     // already saving when the wait ends
        reg.Add("never_started", 100, 0);                                 // the UI thread is busy for 7s first
        var d = new McpDispatcher(reg, new ThreadUiDispatcher(), null, "test", "1");
        d.UiTimeoutSettingReader = () => "5";
        var dBusy = new McpDispatcher(reg, new ThreadUiDispatcher { StartDelayMs = 7000 }, null, "test", "1");
        dBusy.UiTimeoutSettingReader = () => "5";

        string rUndeclared = null, rDeclared = null, rLate = null, rEarly = null, rNever = null;
        var threads = new[]
        {
            new Thread(() => rUndeclared = Call(d, "slow_undeclared")),
            new Thread(() => rDeclared = Call(d, "slow_declared")),
            new Thread(() => rLate = Call(d, "commit_late")),
            new Thread(() => rEarly = Call(d, "commit_early")),
            new Thread(() => rNever = Call(dBusy, "never_started")),
        };
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        Thread.Sleep(3000);   // let the abandoned UI work (7s sleeps, 7s busy start) run to its end

        Ok("configured 5s: a 7s UI tool times out", rUndeclared != null && rUndeclared.Contains("did not answer within 5s"), rUndeclared);
        Ok("timeout names the setting", rUndeclared != null && rUndeclared.Contains("Mcp.UiToolTimeoutSeconds"), rUndeclared);
        Ok("timeout says cancelled and to re-read before retrying", rUndeclared != null
            && rUndeclared.Contains("was cancelled") && rUndeclared.Contains("get_embeditor_source"), rUndeclared);
        Ok("declared 10s: the same 7s UI tool completes", rDeclared != null && rDeclared.Contains("done:slow_declared")
            && !rDeclared.Contains("did not answer"), rDeclared);

        bool lateCommit;
        Ok("abandoned call reaching its save AFTER the timeout: TryCommit refused",
            McpToolRegistry.CommitResults.TryGetValue("commit_late", out lateCommit) && !lateCommit,
            McpToolRegistry.CommitResults.ContainsKey("commit_late") ? lateCommit.ToString() : "never reached");
        Ok("  ...and the caller was told it was cancelled", rLate != null && rLate.Contains("was cancelled"), rLate);

        bool earlyCommit;
        Ok("call already committed before the timeout: TryCommit granted",
            McpToolRegistry.CommitResults.TryGetValue("commit_early", out earlyCommit) && earlyCommit, null);
        Ok("  ...and the caller was told it may still complete", rEarly != null && rEarly.Contains("may still complete"), rEarly);

        int neverRuns;
        Ok("UI thread busy past the timeout: caller told it never started",
            rNever != null && rNever.Contains("never started"), rNever);
        Ok("  ...and the tool never runs once the UI thread frees up",
            !McpToolRegistry.Executions.TryGetValue("never_started", out neverRuns) || neverRuns == 0, neverRuns.ToString());

        Ok("TryCommit outside a dispatched call is allowed", McpCallContext.TryCommit(), null);

        // ---- the cancelled message is tool-accurate ----
        string mSave = McpDispatcher.BuildTimeoutMessage("save_and_close_embeditor", 180, McpCallToken.Running);
        Ok("cancelled save_and_close_embeditor: nothing saved, edits remain unsaved, no rollback claimed",
            mSave.Contains("nothing was saved") && mSave.Contains("remain UNSAVED") && !mSave.Contains("rolled back"), mSave);
        string mApply = McpDispatcher.BuildTimeoutMessage("apply_embed_edits", 180, McpCallToken.Running);
        Ok("cancelled apply_embed_edits: rolled back", mApply.Contains("rolled back"), mApply);
        string mOther = McpDispatcher.BuildTimeoutMessage("open_procedure_embed", 180, McpCallToken.Running);
        Ok("cancelled other tool: may stand, no rollback claimed",
            mOther.Contains("may stand") && !mOther.Contains("rolled back"), mOther);

        var dDefault = new McpDispatcher(reg, new ThreadUiDispatcher(), null, "test", "1");
        string rFast = Call(dDefault, "fast_tool");
        Ok("no setting reader: a fast tool still completes (default path)", rFast.Contains("done:fast_tool"), rFast);

        var dThrow = new McpDispatcher(reg, new ThreadUiDispatcher(), null, "test", "1");
        dThrow.UiTimeoutSettingReader = () => { throw new InvalidOperationException("settings broken"); };
        string rThrow = Call(dThrow, "fast_tool");
        Ok("a throwing settings reader is treated as unset", rThrow.Contains("done:fast_tool"), rThrow);

        // ---- the real registry still declares the round-trip budget on the slow embed tools ----
        string projectDir = args.Length > 0 ? args[0] : null;
        string regPath = projectDir != null ? Path.Combine(projectDir, @"Services\McpToolRegistry.cs") : null;
        if (regPath == null || !File.Exists(regPath))
        {
            Ok("McpToolRegistry.cs found", false, regPath ?? "(no project dir argument)");
        }
        else
        {
            string src = File.ReadAllText(regPath);
            foreach (var tool in new[] { "open_procedure_embed", "save_and_close_embeditor", "apply_embed_edits", "warmup_abc" })
            {
                // The Register block from this tool's Name up to its Handler.
                var m = Regex.Match(src, "Name = \"" + tool + "\"(.*?)Handler =", RegexOptions.Singleline);
                Ok(tool + " declares UiTimeoutSeconds = EmbedRoundTripTimeoutSeconds",
                    m.Success && m.Groups[1].Value.Contains("UiTimeoutSeconds = EmbedRoundTripTimeoutSeconds"),
                    m.Success ? "block found, no declaration" : "tool not found");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
