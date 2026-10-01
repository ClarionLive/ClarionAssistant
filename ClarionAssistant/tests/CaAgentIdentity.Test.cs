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
        Console.WriteLine("CaAgentIdentity identity prompt (c175492a)");

        // ---- BuildIdentityPrompt ----
        string p = CaAgentIdentity.BuildIdentityPrompt("CA-Terminal-1-CC-2");
        Ok("prompt produced for a name", p != null);
        Ok("states the exact name in backticks", p != null && p.Contains("`CA-Terminal-1-CC-2`"));
        Ok("names fromTerminalId", p != null && p.Contains("fromTerminalId"));
        Ok("warns against deriving it from list_terminals", p != null && p.Contains("list_terminals"));
        Ok("says it survives /clear", p != null && p.Contains("/clear"));
        Ok("has a heading", p != null && p.StartsWith("## Your MultiTerminal identity"));
        Ok("trims surrounding whitespace", (CaAgentIdentity.BuildIdentityPrompt("  CA-X  ") ?? "").Contains("`CA-X`")
            && !(CaAgentIdentity.BuildIdentityPrompt("  CA-X  ") ?? "").Contains("`  CA-X"));
        Ok("null name -> null", CaAgentIdentity.BuildIdentityPrompt(null) == null);
        Ok("empty name -> null", CaAgentIdentity.BuildIdentityPrompt("") == null);
        Ok("blank name -> null", CaAgentIdentity.BuildIdentityPrompt("   ") == null);

        // Two tabs, two IDEs: each prompt names only its own tab as "your name".
        string a = CaAgentIdentity.BuildIdentityPrompt("CA-Terminal-1-CC");
        string b = CaAgentIdentity.BuildIdentityPrompt("CA-Terminal-1-CC-2");
        Ok("tab A's prompt says its name is A", a.Contains("Your MultiTerminal name is `CA-Terminal-1-CC`."));
        Ok("tab B's prompt says its name is B", b.Contains("Your MultiTerminal name is `CA-Terminal-1-CC-2`."));
        Ok("tab A's prompt does not claim B's name", !a.Contains("Your MultiTerminal name is `CA-Terminal-1-CC-2`"));

        // ---- AppendIdentityPrompt ----
        string extra = "## Last Session Recap\nwe did things\n";
        string both = CaAgentIdentity.AppendIdentityPrompt(extra, "CA-Tab1");
        Ok("extra kept, first", both.StartsWith("## Last Session Recap"));
        Ok("identity appended after extra", both.IndexOf("## Your MultiTerminal identity") > both.IndexOf("we did things"));
        Ok("blank line between them", both.Contains("we did things" + Environment.NewLine + Environment.NewLine + "## Your MultiTerminal identity"));
        Ok("empty extra -> identity alone", CaAgentIdentity.AppendIdentityPrompt("", "CA-Tab1") == CaAgentIdentity.BuildIdentityPrompt("CA-Tab1"));
        Ok("null extra -> identity alone", CaAgentIdentity.AppendIdentityPrompt(null, "CA-Tab1") == CaAgentIdentity.BuildIdentityPrompt("CA-Tab1"));
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
                int resolve = src.IndexOf("string agentName = ResolveUniqueAgentName(tab,", StringComparison.Ordinal);
                int compose = src.IndexOf("string systemPromptExtra = BuildSystemPromptInjection(", StringComparison.Ordinal);
                int append = src.IndexOf("CaAgentIdentity.AppendIdentityPrompt(systemPromptExtra, agentName)", StringComparison.Ordinal);
                int write = src.IndexOf("\"system-prompt-extra-\"", StringComparison.Ordinal);
                Ok("launch resolves the agent name", resolve >= 0);
                Ok("launch composes the system prompt", compose >= 0);
                Ok("name resolved BEFORE the prompt is composed", resolve >= 0 && compose >= 0 && resolve < compose,
                    "resolve@" + resolve + " compose@" + compose);
                Ok("identity appended to the prompt", append >= 0);
                Ok("identity appended before the prompt file is written", append >= 0 && write >= 0 && append < write,
                    "append@" + append + " write@" + write);
            }
        }
        else Console.WriteLine("  (launch-order checks skipped: no project dir argument)");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL " + pass + " CHECKS PASSED" : fail + " FAILED, " + pass + " passed");
        return fail == 0 ? 0 : 1;
    }
}
