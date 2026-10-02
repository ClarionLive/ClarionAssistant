using System;
using System.IO;
using ClarionAssistant.Services;

// Regression harness for ticket c175492a: two Clarion IDEs, a CA chat in each, Alice messages both,
// and neither tab can reply with certainty because neither model knows its own MultiTerminal name.
// Compiles the REAL Services\CaAgentIdentity.cs (zero IDE coupling).
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir. The launch-order checks
// read the real AssistantChatControl.cs: the name must be resolved BEFORE the system-prompt file is
// composed, and the identity section must be appended to it. AssistantChatControl itself cannot be
// compiled here (IDE-coupled), so those two are source-order checks, not behaviour checks.
static class CaAgentIdentityTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static int Main(string[] args)
    {
        Console.WriteLine("CaAgentIdentity identity prompt (c175492a) and numbered names (7792e3e0)");

        // ---- BuildIdentityPrompt ----
        string p = CaAgentIdentity.BuildIdentityPrompt("CA2");
        Ok("prompt produced for a name", p != null);
        Ok("states the exact name in backticks", p != null && p.Contains("`CA2`"));
        Ok("names fromTerminalId", p != null && p.Contains("fromTerminalId"));
        Ok("warns against deriving it from list_terminals", p != null && p.Contains("list_terminals"));
        Ok("says it survives /clear", p != null && p.Contains("/clear"));
        Ok("has a heading", p != null && p.StartsWith("## Your MultiTerminal identity"));
        // Pipeline run 1 (adversary [high]): the name can be refused by the broker after launch, so
        // the prompt must say what to do then rather than assert the name unconditionally.
        Ok("says what to do if the name is held or unregistered", p != null && p.Contains("held by another terminal") && p.Contains("not registered"));
        // Pipeline run 1 (code-reviewer): the example must not build "<name>-2-2" from a suffixed name.
        Ok("no doubled suffix in the example", !CaAgentIdentity.BuildIdentityPrompt("CA2").Contains("CA2-2"));
        Ok("trims surrounding whitespace", (CaAgentIdentity.BuildIdentityPrompt("  CA9  ") ?? "").Contains("`CA9`")
            && !(CaAgentIdentity.BuildIdentityPrompt("  CA9  ") ?? "").Contains("`  CA9"));
        Ok("null name -> null", CaAgentIdentity.BuildIdentityPrompt(null) == null);
        Ok("empty name -> null", CaAgentIdentity.BuildIdentityPrompt("") == null);
        Ok("blank name -> null", CaAgentIdentity.BuildIdentityPrompt("   ") == null);

        // Two tabs, two IDEs: each prompt names only its own tab as "your name".
        string a = CaAgentIdentity.BuildIdentityPrompt("CA1");
        string b = CaAgentIdentity.BuildIdentityPrompt("CA2");
        Ok("tab A's prompt says its name is A", a.Contains("Your MultiTerminal name is `CA1`."));
        Ok("tab B's prompt says its name is B", b.Contains("Your MultiTerminal name is `CA2`."));
        Ok("tab A's prompt does not claim B's name", !a.Contains("Your MultiTerminal name is `CA2`"));

        // ---- NextFreeName (7792e3e0): CA1, CA2, ... lowest free, case-insensitive per caller ----
        var held = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Ok("nothing taken -> CA1", CaAgentIdentity.NextFreeName(held.Contains) == "CA1");
        Ok("null isTaken -> CA1", CaAgentIdentity.NextFreeName(null) == "CA1");
        held.Add("CA1");
        Ok("CA1 taken -> CA2 (second IDE gets its own number, no -2)", CaAgentIdentity.NextFreeName(held.Contains) == "CA2");
        held.Add("CA2"); held.Add("CA3"); held.Remove("CA2");
        Ok("gap is reused: CA1+CA3 held -> CA2", CaAgentIdentity.NextFreeName(held.Contains) == "CA2");
        held.Clear(); held.Add("ca1");
        Ok("taken check is the caller's (case-insensitive set) -> CA2", CaAgentIdentity.NextFreeName(held.Contains) == "CA2");
        held.Clear(); held.Add("Alice"); held.Add("CA-Terminal-1-CC");
        Ok("non-CA names and old-style names don't block CA1", CaAgentIdentity.NextFreeName(held.Contains) == "CA1");
        string exhausted = CaAgentIdentity.NextFreeName(n => true);
        Ok("all numbers taken -> random fallback, still CA-prefixed", exhausted.StartsWith("CA-") && exhausted.Length == 9, exhausted);
        Ok("name is short (fits tab and prompt label)", CaAgentIdentity.NextFreeName(null).Length <= 5);

        // ---- TabLabel / IsDefaultTabName ----
        Ok("plain Terminal tab -> name alone", CaAgentIdentity.TabLabel("CA2", "Terminal 2") == "CA2");
        Ok("Terminal 12 -> name alone", CaAgentIdentity.TabLabel("CA2", "Terminal 12") == "CA2");
        Ok("solution tab keeps its context, name first", CaAgentIdentity.TabLabel("CA2", "MySolution") == "CA2 · MySolution");
        Ok("Evaluate Code keeps its context", CaAgentIdentity.TabLabel("CA3", "Evaluate Code") == "CA3 · Evaluate Code");
        Ok("null base -> name alone", CaAgentIdentity.TabLabel("CA1", null) == "CA1");
        Ok("blank base -> name alone", CaAgentIdentity.TabLabel("CA1", "  ") == "CA1");
        Ok("no agent name -> base unchanged", CaAgentIdentity.TabLabel(null, "MySolution") == "MySolution");
        Ok("'Terminal' alone is not default", !CaAgentIdentity.IsDefaultTabName("Terminal "));
        Ok("'Terminal 2x' is not default", !CaAgentIdentity.IsDefaultTabName("Terminal 2x"));
        Ok("'terminal 2' (case) is not default", !CaAgentIdentity.IsDefaultTabName("terminal 2"));
        Ok("'Terminals 2' is not default", !CaAgentIdentity.IsDefaultTabName("Terminals 2"));
        Ok("null is not default", !CaAgentIdentity.IsDefaultTabName(null));

        // ---- Exit signal (7792e3e0 live test: pwsh -NoExit outlives Claude) ----
        string wrapped = CaAgentIdentity.WrapWithExitSignal("& 'claude' -n 'CA4'", "ab12cd34");
        Ok("invocation sits inside try", wrapped.StartsWith("try { & 'claude' -n 'CA4' } finally { "));
        Ok("finally drops MULTITERMINAL_NAME", wrapped.Contains("Remove-Item Env:MULTITERMINAL_NAME"));
        Ok("finally retitles the console", wrapped.Contains("$Host.UI.RawUI.WindowTitle = '"));
        Ok("finally's title concatenates to the tab's marker",
            wrapped.Replace("' + '", "").Contains("WindowTitle = 'ca-assistant-exited:ab12cd34'"));
        // Pipeline run 3 (debugger): the command line itself must not carry the marker verbatim.
        Ok("command line never contains the marker verbatim", !wrapped.Contains("ca-assistant-exited:ab12cd34"));
        Ok("no double quotes (command lives inside -Command \"...\")", !wrapped.Contains("\""));
        string c = null;
        Ok("plain output is not the signal", !CaAgentIdentity.SeesExitSignal("ab12cd34", "hello world", ref c));
        c = null;
        Ok("OSC title with the marker is the signal",
            CaAgentIdentity.SeesExitSignal("ab12cd34", "\x1b]0;ca-assistant-exited:ab12cd34\x07PS H:\\> ", ref c));
        c = null;
        Ok("OSC 2 title with the marker is the signal",
            CaAgentIdentity.SeesExitSignal("ab12cd34", "\x1b]2;ca-assistant-exited:ab12cd34\x1b\\", ref c));
        c = null;
        Ok("bare marker text (e.g. a listed command line) is NOT the signal",
            !CaAgentIdentity.SeesExitSignal("ab12cd34", "pwsh -Command \"... WindowTitle = 'ca-assistant-exited:ab12cd34' }\"", ref c));
        c = null;
        Ok("another tab's marker is not this tab's signal",
            !CaAgentIdentity.SeesExitSignal("ab12cd34", "\x1b]0;ca-assistant-exited:ffffffff\x07", ref c));
        c = null;
        bool first = CaAgentIdentity.SeesExitSignal("ab12cd34", "...\x1b]0;ca-assistant-ex", ref c);
        bool second = CaAgentIdentity.SeesExitSignal("ab12cd34", "ited:ab12cd34\x07", ref c);
        Ok("marker split across two reads is still seen", !first && second);
        c = null;
        CaAgentIdentity.SeesExitSignal("ab12cd34", new string('x', 5000), ref c);
        Ok("carry stays bounded", c != null && c.Length < "\x1b]0;ca-assistant-exited:ab12cd34".Length);
        c = null;
        bool a1 = CaAgentIdentity.SeesExitSignal("ab12cd34", "out\x1b", ref c);
        bool a2 = CaAgentIdentity.SeesExitSignal("ab12cd34", "]0;ca-assistant-exited:ab12cd34\x07", ref c);
        Ok("split right after ESC is still seen", !a1 && a2);

        // ---- AppendIdentityPrompt ----
        string extra = "## Last Session Recap\nwe did things\n";
        string both = CaAgentIdentity.AppendIdentityPrompt(extra, "CA1");
        Ok("extra kept, first", both.StartsWith("## Last Session Recap"));
        Ok("identity appended after extra", both.IndexOf("## Your MultiTerminal identity") > both.IndexOf("we did things"));
        Ok("blank line between them", both.Contains("we did things" + Environment.NewLine + Environment.NewLine + "## Your MultiTerminal identity"));
        Ok("empty extra -> identity alone", CaAgentIdentity.AppendIdentityPrompt("", "CA1") == CaAgentIdentity.BuildIdentityPrompt("CA1"));
        Ok("null extra -> identity alone", CaAgentIdentity.AppendIdentityPrompt(null, "CA1") == CaAgentIdentity.BuildIdentityPrompt("CA1"));
        Ok("no name -> extra unchanged", CaAgentIdentity.AppendIdentityPrompt(extra, null) == extra);
        Ok("no name, no extra -> null stays null", CaAgentIdentity.AppendIdentityPrompt(null, "") == null);

        // ---- Launch order in the real AssistantChatControl.cs ----
        if (args.Length > 0)
        {
            string path = Path.Combine(args[0], "AssistantChatControl.cs");
            if (!File.Exists(path)) { Ok("AssistantChatControl.cs found", false, path); }
            else
            {
                string src = File.ReadAllText(path);
                int resolve = src.IndexOf("string agentName = ResolveUniqueAgentName(tab);", StringComparison.Ordinal);
                int relabel = resolve < 0 ? -1 : src.IndexOf("CaAgentIdentity.TabLabel(agentName, tab.BaseName)", resolve, StringComparison.Ordinal);
                int baseCapture = src.IndexOf("if (tab.BaseName == null) tab.BaseName = tab.Name;", StringComparison.Ordinal);
                int launchClaude = src.IndexOf("LaunchClaudeForTab(tab);", baseCapture < 0 ? 0 : baseCapture, StringComparison.Ordinal);
                int compose = src.IndexOf("string systemPromptExtra = BuildSystemPromptInjection(", StringComparison.Ordinal);
                int append = src.IndexOf("CaAgentIdentity.AppendIdentityPrompt(systemPromptExtra, agentName)", StringComparison.Ordinal);
                int gate = src.IndexOf("_mcpServer.MultiTerminalConfigured)", compose < 0 ? 0 : compose, StringComparison.Ordinal);
                int abort = src.IndexOf("private void AbortLaunch(", StringComparison.Ordinal);
                int abortRelease = abort < 0 ? -1 : src.IndexOf("ReleaseAgentName(tab);", abort, StringComparison.Ordinal);
                int abortBackendNull = abort < 0 ? -1 : src.IndexOf("tab.AssistantBackend = null;", abort, StringComparison.Ordinal);
                int release = src.IndexOf("private void ReleaseAgentName(TerminalTab tab)", StringComparison.Ordinal);
                int releaseEnd = release < 0 ? -1 : src.IndexOf("\n        }", release, StringComparison.Ordinal);
                int releaseClear = release < 0 ? -1 : src.IndexOf("tab.AgentName = null;", release, StringComparison.Ordinal);
                int releaseRelabel = release < 0 ? -1 : src.IndexOf("ApplyBackendSuffix(tab.BaseName, tab.AssistantBackend)", release, StringComparison.Ordinal);
                int exited = src.IndexOf("private void OnTabTerminalProcessExited(TerminalTab tab)", StringComparison.Ordinal);
                int exitedEnd = exited < 0 ? -1 : src.IndexOf("\n        }", exited, StringComparison.Ordinal);
                int exitedRelease = exited < 0 ? -1 : src.IndexOf("ReleaseAgentName(tab)", exited, StringComparison.Ordinal);
                int wrap = src.IndexOf("CaAgentIdentity.WrapWithExitSignal(claudeInvocation, tab.Id)", StringComparison.Ordinal);
                int dataRecv = src.IndexOf("private void OnTabTerminalDataReceived(TerminalTab tab, byte[] data)", StringComparison.Ordinal);
                int dataEnd = dataRecv < 0 ? -1 : src.IndexOf("\n        }", dataRecv, StringComparison.Ordinal);
                int sees = dataRecv < 0 ? -1 : src.IndexOf("CaAgentIdentity.SeesExitSignal(tab.Id,", dataRecv, StringComparison.Ordinal);
                int seesRelease = sees < 0 ? -1 : src.IndexOf("ReleaseAgentName(tab)", sees, StringComparison.Ordinal);
                int abortEnd = abort < 0 ? -1 : src.IndexOf("\n        }", abort, StringComparison.Ordinal);
                int write = src.IndexOf("\"system-prompt-extra-\"", StringComparison.Ordinal);
                Ok("launch resolves the agent name", resolve >= 0);
                Ok("launch composes the system prompt", compose >= 0);
                // 7792e3e0: the tab shows the resolved name, built from the undecorated base name
                // captured before the first launch (so a relaunch can't stack labels).
                Ok("tab relabelled with the resolved name", relabel >= 0, "relabel@" + relabel);
                Ok("base name captured before Claude launches", baseCapture >= 0 && launchClaude > baseCapture,
                    "capture@" + baseCapture + " launch@" + launchClaude);
                Ok("name resolved BEFORE the prompt is composed", resolve >= 0 && compose >= 0 && resolve < compose,
                    "resolve@" + resolve + " compose@" + compose);
                Ok("identity appended to the prompt", append >= 0);
                Ok("identity appended before the prompt file is written", append >= 0 && write >= 0 && append < write,
                    "append@" + append + " write@" + write);
                // Pipeline run 1: gated on the same condition as the MCP config, not the bare setting.
                Ok("identity gated on MultiTerminalConfigured", gate >= 0 && append >= 0 && gate < append,
                    "gate@" + gate + " append@" + append);
                // Pipeline run 1: an aborted launch must not keep a name other tabs then avoid.
                // 7792e3e0: the name and its CA<n> label are released together, by one helper.
                Ok("AbortLaunch releases the name", abortRelease >= 0 && abortEnd >= 0 && abortRelease < abortEnd,
                    "release@" + abortRelease + " end@" + abortEnd);
                Ok("AbortLaunch releases before clearing the backend the label is built from",
                    abortRelease >= 0 && abortBackendNull > abortRelease, "release@" + abortRelease + " backendNull@" + abortBackendNull);
                Ok("ReleaseAgentName clears tab.AgentName", releaseClear >= 0 && releaseClear < releaseEnd,
                    "clear@" + releaseClear + " end@" + releaseEnd);
                Ok("ReleaseAgentName drops the CA<n> tab label", releaseRelabel >= 0 && releaseRelabel < releaseEnd,
                    "relabel@" + releaseRelabel + " end@" + releaseEnd);
                // Pipeline run 1 (debugger): an exited assistant must release its name too.
                Ok("process exit releases the name", exitedRelease >= 0 && exitedEnd >= 0 && exitedRelease < exitedEnd,
                    "release@" + exitedRelease + " end@" + exitedEnd);
                // Live test (John): /exit leaves the -NoExit shell running, so the process never exits.
                Ok("Claude command wrapped with the exit signal", wrap >= 0, "wrap@" + wrap);
                Ok("output handler watches for the exit signal and releases the name",
                    sees >= 0 && seesRelease > sees && seesRelease < dataEnd, "sees@" + sees + " release@" + seesRelease + " end@" + dataEnd);
            }
        }
        else Console.WriteLine("  (launch-order checks skipped: no project dir argument)");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL " + pass + " CHECKS PASSED" : fail + " FAILED, " + pass + " passed");
        return fail == 0 ? 0 : 1;
    }
}
