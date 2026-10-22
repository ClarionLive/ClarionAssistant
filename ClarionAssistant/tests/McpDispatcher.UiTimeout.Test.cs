using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
//   * b8f9431c review M3: ONE UI-thread call at a time across EVERY dispatcher of the process (the chat pane's and the
//     call-back host's): a second client's call waits for the first instead of running inside it, is refused as busy
//     (nothing run) when the first takes longer than the gate wait, and the standalone's call-backs are exempt.
//   * b8f9431c review M2: a forwarded call (params._meta expected solution) is checked by ExpectedSolutionGuard ON THE
//     UI THREAD, for a UI tool and a worker tool alike, and a mismatch runs nothing; ForwardedCallFilter refuses before
//     anything; ForwardedCallObserver sees only forwarded calls that passed. A call without _meta is untouched.
//   The new statics are set by REFLECTION, so this same file compiles against c627adc's McpDispatcher.cs for the
//   fail-on-old proof (where those checks then fail instead of not compiling).
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

    const string MetaKey = "clarionAssistant/expectedSolution";

    static string CallForwarded(McpDispatcher d, string tool, string expectedSolution)
    {
        return d.ProcessJsonRpc(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{},"
            + "\"_meta\":{\"" + MetaKey + "\":\"" + expectedSolution.Replace("\\", "\\\\") + "\"}}}",
            null);
    }

    /// <summary>Set a public/internal static of McpDispatcher by name; false when it does not exist (old code).</summary>
    static bool SetStatic(string name, object value)
    {
        var f = typeof(McpDispatcher).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (f == null) return false;
        f.SetValue(null, value);
        return true;
    }

    static bool IsError(string response) { return response != null && response.Contains("\"isError\":true"); }

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
        // These cases run five fake "UI threads" at once on purpose (the real UI thread never does); M3's gate would
        // serialise them, so it is bypassed here and tested on its own below.
        SetStatic("UiGateBypassForTest", true);
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
        Ok("cancelled apply_embed_edits: discards at its save step, rollback not claimed as confirmed",
            mApply.Contains("discards the embed edits") && mApply.Contains("NOT confirmed") && !mApply.Contains("rolled back"), mApply);
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
        SetStatic("UiGateBypassForTest", false);

        // ---- b8f9431c review M3: one UI-thread call at a time across every dispatcher ----
        {
            var r3 = new McpToolRegistry();
            r3.Add("long_a", 1500, 0);
            r3.Add("short_b", 100, 0);
            r3.Add("long_c", 3000, 0);
            r3.Add("short_d", 100, 0);
            r3.Add("get_live_text", 100, 0);   // a standalone call-back: exempt
            var pane = new McpDispatcher(r3, new ThreadUiDispatcher(), null, "pane", "1");        // the chat pane's server
            var callback = new McpDispatcher(r3, new ThreadUiDispatcher(), null, "callback", "1"); // IdeCallbackHost's

            McpToolRegistry.ResetConcurrency();
            string ra = null, rb = null;
            var ta = new Thread(() => ra = Call(pane, "long_a"));
            ta.Start();
            Thread.Sleep(300);
            rb = Call(callback, "short_b");
            ta.Join();
            string[] order = McpToolRegistry.Finished.ToArray();
            Ok("M3: another client's UI call waits for the running one instead of running inside it (never 2 at once)",
                McpToolRegistry.MaxRunning == 1 && ra.Contains("done:long_a") && rb.Contains("done:short_b"),
                "max at once " + McpToolRegistry.MaxRunning + "; " + rb);
            Ok("M3: ...and runs after it", order.Length == 2 && order[0] == "long_a" && order[1] == "short_b", string.Join(",", order));

            bool waitSet = SetStatic("UiGateWaitSeconds", 1);
            McpToolRegistry.ResetConcurrency();
            int dBefore = McpToolRegistry.Executions.TryGetValue("short_d", out dBefore) ? dBefore : 0;
            string rc = null, rd = null, rSame = null, rLive = null;
            var tc = new Thread(() => rc = Call(pane, "long_c"));
            tc.Start();
            Thread.Sleep(300);
            rd = Call(callback, "short_d");
            rSame = Call(pane, "short_d");
            var swLive = System.Diagnostics.Stopwatch.StartNew();
            rLive = Call(callback, "get_live_text");
            swLive.Stop();
            tc.Join();
            int dAfter = McpToolRegistry.Executions.TryGetValue("short_d", out dAfter) ? dAfter : 0;
            Ok("M3: a call that cannot get the UI thread within the gate wait is refused as busy, naming the other client's tool",
                waitSet && IsError(rd) && rd.Contains("Clarion is busy with another client") && rd.Contains("s long_c;") && rd.Contains("was not run, try again"), rd);
            Ok("M3: ...the same client's second call says it is its own earlier call",
                IsError(rSame) && rSame.Contains("long_c (an earlier call of this client)"), rSame);
            Ok("M3: ...and neither refused call ran", dAfter == dBefore, dBefore + " -> " + dAfter);
            Ok("M3: the standalone's call-back get_live_text is exempt (runs at once, beside the running call)",
                rLive != null && rLive.Contains("done:get_live_text") && swLive.ElapsedMilliseconds < 1500,
                swLive.ElapsedMilliseconds + " ms; " + rLive);
            Ok("M3: the long call itself completed", rc != null && rc.Contains("done:long_c"), rc);
            SetStatic("UiGateWaitSeconds", 10);
        }

        // ---- b8f9431c review M2: the IDE re-checks the solution on the UI thread; filter and observer ----
        {
            var r2 = new McpToolRegistry();
            r2.Add("ui_tool", 50, 0);
            r2.AddWorker("worker_tool", 50);
            r2.Add("blocked_tool", 50, 0);
            var d2 = new McpDispatcher(r2, new ThreadUiDispatcher(), null, "callback", "1");
            var guardThreads = new List<string>();
            var observed = new List<string>();
            Func<string, string> guard = exp =>
            {
                lock (guardThreads) guardThreads.Add(Thread.CurrentThread.Name ?? "(unnamed)");
                return string.Equals(exp, @"C:\Work\A.sln", StringComparison.OrdinalIgnoreCase) ? null
                    : "Error: Clarion now has A.sln open, not " + Path.GetFileName(exp) + "; nothing was run.";
            };
            bool g = SetStatic("ExpectedSolutionGuard", guard);
            bool o = SetStatic("ForwardedCallObserver", (Action<string>)(t => { lock (observed) observed.Add(t); }));
            bool f = SetStatic("ForwardedCallFilter", (Func<string, Dictionary<string, object>, string>)((t, a) => t == "blocked_tool" ? "Error: blocked_tool is not available to clients outside the Clarion IDE; nothing was run." : null));

            int Ex(string n) { int v; return McpToolRegistry.Executions.TryGetValue(n, out v) ? v : 0; }

            int uiBefore = Ex("ui_tool"), wBefore = Ex("worker_tool");
            string sameUi = CallForwarded(d2, "ui_tool", @"C:\Work\A.sln");
            string otherUi = CallForwarded(d2, "ui_tool", @"C:\Work\B.sln");
            string sameW = CallForwarded(d2, "worker_tool", @"C:\Work\A.sln");
            string otherW = CallForwarded(d2, "worker_tool", @"C:\Work\B.sln");
            Ok("M2: a forwarded UI call on the solution this IDE has open runs", g && sameUi.Contains("done:ui_tool"), sameUi);
            Ok("M2: a forwarded UI call for ANOTHER solution is refused, saying which, and does not run",
                IsError(otherUi) && otherUi.Contains("Clarion now has A.sln open, not B.sln; nothing was run.") && Ex("ui_tool") == uiBefore + 1, otherUi);
            Ok("M2: the same for a worker-thread tool (open_file, the editor writes, build_app)",
                sameW.Contains("done:worker_tool") && IsError(otherW) && otherW.Contains("not B.sln") && Ex("worker_tool") == wBefore + 1, otherW);
            string[] gt; lock (guardThreads) gt = guardThreads.ToArray();
            Ok("M2: the check ran ON THE UI THREAD every time (4 checks)", gt.Length == 4 && gt.All(n => n == "fake-ui"), string.Join(",", gt));

            lock (guardThreads) guardThreads.Clear();
            string plain = Call(d2, "ui_tool");
            Ok("M2: a CA tab's own call (no _meta) is not checked", plain.Contains("done:ui_tool") && guardThreads.Count == 0, plain);

            int bBefore = Ex("blocked_tool");
            string blockedFwd = CallForwarded(d2, "blocked_tool", @"C:\Work\A.sln");
            string blockedTab = Call(d2, "blocked_tool");
            Ok("M1: ForwardedCallFilter refuses a forwarded call before anything runs",
                f && IsError(blockedFwd) && blockedFwd.Contains("not available to clients outside") && Ex("blocked_tool") == bBefore + 1, blockedFwd);
            Ok("M1: ...a CA tab's own call of the same tool still runs", blockedTab.Contains("done:blocked_tool"), blockedTab);

            string[] obs; lock (observed) obs = observed.ToArray();
            Ok("visibility: ForwardedCallObserver saw exactly the forwarded calls that ran (ui_tool, worker_tool)",
                o && obs.Length == 2 && obs.Contains("ui_tool") && obs.Contains("worker_tool"), string.Join(",", obs));

            SetStatic("ExpectedSolutionGuard", null);
            SetStatic("ForwardedCallObserver", null);
            SetStatic("ForwardedCallFilter", null);
        }

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
