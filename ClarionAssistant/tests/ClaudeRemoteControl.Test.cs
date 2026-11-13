using System;
using System.IO;
using ClarionAssistant.Services;

// Regression harness for ticket b17d528b: the "Remote Control for every Claude tab" setting, ported from
// MultiTerminal d096464f (MultiTerminal.Tests\ConPtyTerminalRemoteControlTests.cs).
// Compiles the REAL Services\ClaudeRemoteControl.cs and Services\PwshCommandQuoter.cs (zero IDE coupling).
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir. The launch checks read the real
// AssistantChatControl.cs (IDE-coupled, cannot be compiled here), so they are source checks: the flag sits
// straight after -n and before the session flag, which is what keeps a bare word from following it.
// Not covered: that the CLI accepts the result, and that the remote session appears. That is the live test.
static class ClaudeRemoteControlTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static void Eq(string name, string expected, string actual)
    {
        Ok(name, expected == actual, "expected [" + expected + "] got [" + actual + "]");
    }

    static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return null;
        int end = src.IndexOf("\n        }", at, StringComparison.Ordinal);
        return end < 0 ? src.Substring(at) : src.Substring(at, end - at);
    }

    static int Main(string[] args)
    {
        Console.WriteLine("ClaudeRemoteControl (b17d528b)");

        // ---- the setting: on only for a stored "true" ----
        Ok("setting: 'true' is on", ClaudeRemoteControl.IsOn("true"));
        Ok("setting: ' TRUE ' is on", ClaudeRemoteControl.IsOn(" TRUE "));
        Ok("setting: 'false' is off", !ClaudeRemoteControl.IsOn("false"));
        Ok("setting: '' is off", !ClaudeRemoteControl.IsOn(""));
        Ok("setting: missing is off", !ClaudeRemoteControl.IsOn(null));
        Ok("setting: 'yes' is off", !ClaudeRemoteControl.IsOn("yes"));
        Ok("setting: key is Claude.RemoteControl", ClaudeRemoteControl.SettingKey == "Claude.RemoteControl");

        // ---- the flag ----
        Eq("off: no flag", "", ClaudeRemoteControl.Flag(false, "claude"));
        Eq("on: a bare flag for plain claude", " --remote-control", ClaudeRemoteControl.Flag(true, "claude"));
        Eq("on: claude -c", " --remote-control", ClaudeRemoteControl.Flag(true, "claude -c"));
        Eq("on: claude --dangerously-skip-permissions", " --remote-control", ClaudeRemoteControl.Flag(true, "claude --dangerously-skip-permissions"));
        Eq("on: a full path", " --remote-control", ClaudeRemoteControl.Flag(true, "'C:\\Users\\x\\.local\\bin\\claude.exe' -c"));
        Eq("on: a bare word after claude still gets it (it goes after CA's -n, not after the word)",
           " --remote-control", ClaudeRemoteControl.Flag(true, "claude hello"));

        // Already asked for: never doubled.
        Eq("already: --remote-control", "", ClaudeRemoteControl.Flag(true, "claude --remote-control"));
        Eq("already: --remote-control 'Mine'", "", ClaudeRemoteControl.Flag(true, "claude --remote-control 'Mine' -c"));
        Eq("already: --rc", "", ClaudeRemoteControl.Flag(true, "claude --rc"));
        Eq("already: --rc=Mine", "", ClaudeRemoteControl.Flag(true, "claude --rc=Mine"));
        Eq("already: --remote-control=Mine", "", ClaudeRemoteControl.Flag(true, "claude --remote-control=Mine"));
        // Look-alikes are not a request.
        Eq("not already: --rcx", " --remote-control", ClaudeRemoteControl.Flag(true, "claude --rcx"));
        Eq("not already: --remote-controller", " --remote-control", ClaudeRemoteControl.Flag(true, "claude --remote-controller"));
        Eq("not already: a word containing --rc inside quotes is one token", " --remote-control",
           ClaudeRemoteControl.Flag(true, "claude 'say --rc'"));
        Eq("null command, on", " --remote-control", ClaudeRemoteControl.Flag(true, null));

        // ---- the launch (source checks) ----
        string projectDir = args.Length > 0 ? args[0] : null;
        string path = projectDir != null ? Path.Combine(projectDir, "AssistantChatControl.cs") : null;
        if (path == null || !File.Exists(path))
        {
            Ok("launch: AssistantChatControl.cs found", false, path ?? "(no project dir argument)");
        }
        else
        {
            string src = File.ReadAllText(path);
            string build = Body(src, "private BuiltBackendCommand BuildClaudeCommand(");
            Ok("launch: BuildClaudeCommand found", build != null);
            if (build != null)
            {
                Ok("launch: the flag reads the setting through the helper",
                   build.Contains("ClaudeRemoteControl.IsOn(_settings.Get(Services.ClaudeRemoteControl.SettingKey))"));
                Ok("launch: the developer's own command is checked for an existing flag",
                   build.Contains("ClaudeRemoteControl.Flag(") && build.Contains("claudeCmdRaw);"));
                // The order that makes a bare flag safe: -n first (the title), then the flag, then the
                // session flag (--resume/--session-id), which always starts with '-'.
                Ok("launch: flag sits after -n and before the session flag",
                   build.Contains("{claudeBase}{nameFlag}{remoteControlFlag}{sessionFlag}"));
                Ok("launch: the session flag always starts with a dash",
                   build.Contains("? $\" --resume '{resumeId}'\"") && build.Contains(": $\" --session-id '{tab.ClaudeSessionId}'\""));
            }
        }

        Console.WriteLine();
        Console.WriteLine(string.Format("{0} passed, {1} failed", pass, fail));
        return fail == 0 ? 0 : 1;
    }
}
