using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using ClarionAssistant.Services;

// PR #198 point 1: the UI-thread tool timeout is no longer a hardcoded 30s in McpDispatcher.
//
//   * McpUiTimeoutPolicy.Resolve - default 30, the "Mcp.UiToolTimeoutSeconds" setting, a tool's declared
//     minimum (the larger wins), clamped to [5, 600].
//   * The REAL McpDispatcher.cs actually waits that long: a tool on the (fake) UI thread that outlives a
//     configured 5s budget times out and says "5s"; the same sleep under a declared 10s budget finishes.
//     Against the old hardcoded 30s the first case returns success instead - that is the red.
//   * The real McpToolRegistry.cs still declares the embed round-trip budget on the four slow tools
//     (source scan - the registry itself cannot compile outside the IDE).
//
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
        Ok("setting raises the default", McpUiTimeoutPolicy.Resolve("90", 0) == 90, null);
        Ok("setting may lower the default", McpUiTimeoutPolicy.Resolve(" 10 ", 0) == 10, null);
        Ok("garbage setting = default", McpUiTimeoutPolicy.Resolve("abc", 0) == 30, null);
        Ok("zero setting = default (cannot disable the guard)", McpUiTimeoutPolicy.Resolve("0", 0) == 30, null);
        Ok("negative setting = default", McpUiTimeoutPolicy.Resolve("-5", 0) == 30, null);
        Ok("tiny setting clamps to 5", McpUiTimeoutPolicy.Resolve("1", 0) == 5, null);
        Ok("huge setting clamps to 600", McpUiTimeoutPolicy.Resolve("99999", 0) == 600, null);
        Ok("declared beats a smaller setting", McpUiTimeoutPolicy.Resolve("30", 180) == 180, null);
        Ok("setting beats a smaller declared", McpUiTimeoutPolicy.Resolve("300", 180) == 300, null);
        Ok("declared clamps to 600", McpUiTimeoutPolicy.Resolve(null, 900) == 600, null);

        // ---- the real dispatcher honours it ----
        var reg = new McpToolRegistry();
        reg.Add("fast_tool", 100, 0);
        reg.Add("slow_undeclared", 7000, 0);   // outlives a 5s setting
        reg.Add("slow_declared", 7000, 10);    // same sleep, declares 10s
        var d = new McpDispatcher(reg, new ThreadUiDispatcher(), null, "test", "1");
        d.UiTimeoutSettingReader = () => "5";

        string rUndeclared = null, rDeclared = null;
        var t1 = new Thread(() => rUndeclared = Call(d, "slow_undeclared"));
        var t2 = new Thread(() => rDeclared = Call(d, "slow_declared"));
        t1.Start(); t2.Start(); t1.Join(); t2.Join();

        Ok("configured 5s: a 7s UI tool times out", rUndeclared != null && rUndeclared.Contains("did not respond within 5s"), rUndeclared);
        Ok("timeout names the setting to raise", rUndeclared != null && rUndeclared.Contains("Mcp.UiToolTimeoutSeconds"), rUndeclared);
        Ok("declared 10s: the same 7s UI tool completes", rDeclared != null && rDeclared.Contains("done:slow_declared")
            && !rDeclared.Contains("did not respond"), rDeclared);

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
