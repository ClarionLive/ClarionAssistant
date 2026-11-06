using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClarionAssistant.Services;

// Harness for ticket f9843525: the Home tab's "Resume last session" and Previous sessions drawer
// list a solution folder's past Claude Code sessions. Compiles the REAL Services\ClaudeSessionCatalog.cs
// (no IDE coupling) against fixture transcripts written to %TEMP%.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir, for the source check that
// BuildInitialPrompt takes its greetings from the catalog (otherwise a greeting added there would
// become every new session's summary).
static class ClaudeSessionCatalogTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    // A method's text, from its signature to its closing brace (the first "\n        }" after it), or null.
    static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return null;
        int end = src.IndexOf("\n        }", at, StringComparison.Ordinal);
        return end < 0 ? src.Substring(at) : src.Substring(at, end - at);
    }

    static string J(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

    // Transcript lines in Claude Code's compact JSON, as real CA transcripts have them.
    static string AgentName(string n) { return "{\"type\":\"agent-name\",\"agentName\":\"" + n + "\",\"sessionId\":\"x\"}"; }
    static string Title(string n) { return "{\"type\":\"custom-title\",\"customTitle\":\"" + n + "\",\"sessionId\":\"x\"}"; }
    static string User(string cwd, string text, string extra = "")
    {
        return "{\"parentUuid\":null,\"isSidechain\":false" + extra + ",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" +
               J(text) + "\"},\"cwd\":\"" + J(cwd) + "\"}";
    }
    static string UserBlocks(string cwd, string blocksJson)
    {
        return "{\"isSidechain\":false,\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + blocksJson + "},\"cwd\":\"" + J(cwd) + "\"}";
    }
    static string Asst(string cwd, string text)
    {
        return "{\"isSidechain\":false,\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"" +
               J(text) + "\"}]},\"cwd\":\"" + J(cwd) + "\"}";
    }
    static string LastPrompt(string text) { return "{\"type\":\"last-prompt\",\"lastPrompt\":\"" + J(text) + "\",\"sessionId\":\"x\"}"; }

    static string Write(string folder, string id, DateTime lastWriteUtc, params string[] lines)
    {
        string path = Path.Combine(folder, id + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    static ClaudeSessionInfo Find(ClaudeSessionList l, string id)
    {
        foreach (var s in l.Sessions) if (s.Id == id) return s;
        return null;
    }

    static int Main(string[] args)
    {
        Console.WriteLine("ClaudeSessionCatalog: resumable sessions for a solution folder (f9843525)");
        string tmp = Path.Combine(Path.GetTempPath(), "ca-session-catalog-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            // ---- Encoding and ids ----
            Ok("encode: H:\\Dev\\aPOSitive -> H--Dev-aPOSitive", ClaudeSessionCatalog.EncodeProjectPath(@"H:\Dev\aPOSitive") == "H--Dev-aPOSitive",
               ClaudeSessionCatalog.EncodeProjectPath(@"H:\Dev\aPOSitive"));
            Ok("encode: spaces, dots and underscores become '-'", ClaudeSessionCatalog.EncodeProjectPath(@"C:\My Apps\v1.2_x") == "C--My-Apps-v1-2-x",
               ClaudeSessionCatalog.EncodeProjectPath(@"C:\My Apps\v1.2_x"));
            Ok("id: GUID lowercased", ClaudeSessionCatalog.NormalizeId(" B7CCAD9B-FF5F-45EA-B6E4-F02F0823F632 ") == "b7ccad9b-ff5f-45ea-b6e4-f02f0823f632");
            Ok("id: not a GUID -> null", ClaudeSessionCatalog.NormalizeId("b7ccad9b; rm -rf") == null);
            Ok("id: empty -> null", ClaudeSessionCatalog.NormalizeId("") == null);

            // ---- Fixture folder ----
            string sol = Path.Combine(tmp, "sol", "v61POSitive");
            Directory.CreateDirectory(sol);
            string root = Path.Combine(tmp, "projects");
            string folder = Path.Combine(root, ClaudeSessionCatalog.EncodeProjectPath(sol));
            Directory.CreateDirectory(folder);
            DateTime now = DateTime.UtcNow;

            string idA = "aaaaaaaa-0000-4000-8000-000000000001"; // normal, 12 min old
            string idB = "bbbbbbbb-0000-4000-8000-000000000002"; // greeting only
            string idC = "cccccccc-0000-4000-8000-000000000003"; // live in a CA tab
            string idD = "dddddddd-0000-4000-8000-000000000004"; // other folder, same encoding
            string idE = "eeeeeeee-0000-4000-8000-000000000005"; // 1 min old -> possibly open
            string idF = "ffffffff-0000-4000-8000-000000000006"; // no assistant turn
            string idG = "99999999-0000-4000-8000-000000000007"; // custom-title only, prompt noise, last-prompt
            string idH = "88888888-0000-4000-8000-000000000008"; // prompt beyond the head: summary from last-prompt

            Write(folder, idA, now.AddMinutes(-12),
                Title("CA1"), AgentName("CA1"),
                User(sol, "Greetings, Clarion Developer!"),
                Asst(sol, "Welcome back!"),
                User(sol, "Add the FREIGHT category test to ReDoTaxes and FBVerify"),
                Asst(sol, "Done."),
                LastPrompt("now do SaveInvoice"));
            Write(folder, idB, now.AddMinutes(-20),
                AgentName("CA2"), User(sol, "Good morning!"), Asst(sol, "Ready to help"), LastPrompt("Good morning!"));
            Write(folder, idC, now.AddMinutes(-5),
                AgentName("CA3"), User(sol, "live one"), Asst(sol, "ok"));
            // <tmp>\sol\v61POSitive and <tmp>\sol-v61POSitive encode to the same folder name.
            string twin = Path.Combine(tmp, "sol-v61POSitive");
            Ok("fixture: the twin folder really encodes alike", ClaudeSessionCatalog.EncodeProjectPath(twin) == ClaudeSessionCatalog.EncodeProjectPath(sol));
            Write(folder, idD, now.AddMinutes(-30),
                AgentName("CA4"), User(twin, "a prompt from another folder"), Asst(twin, "ok"));
            Write(folder, idE, now.AddSeconds(-50),
                AgentName("CA1"), User(sol, "PriceItSource and Invoice2 by hand"), Asst(sol, "ok"));
            Write(folder, idF, now.AddMinutes(-40),
                AgentName("CA5"), User(sol, "a prompt that never got an answer"));
            Write(folder, idG, now.AddDays(-3),
                Title("CA6"),
                User(sol, "<system-reminder>hook output</system-reminder>", ",\"isMeta\":true"),
                UserBlocks(sol, "[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"ok\"}]"),
                User(sol, "[MultiTerminal message from X] {}"),
                User(sol, "<cross-session-message from=\\\"a\\\">hi</cross-session-message>"),
                UserBlocks(sol, "[{\"type\":\"text\",\"text\":\"<system-reminder>noise</system-reminder> Deploy   the two\\nSQL procs\"}]"),
                Asst(sol, "ok"),
                LastPrompt("second"), LastPrompt("Evening!"), LastPrompt("third and last"));
            // idH: a 300 KB head of tool output before any prompt; the real prompt only shows in last-prompt.
            var big = new List<string> { AgentName("CA7"), User(sol, "Reporting for duty!"), Asst(sol, "hi") };
            string filler = new string('x', 2000);
            for (int i = 0; i < 160; i++) big.Add(Asst(sol, filler));
            big.Add(User(sol, "a prompt past the head"));
            big.Add(LastPrompt("a prompt past the head"));
            Write(folder, idH, now.AddDays(-10), big.ToArray());
            File.WriteAllText(Path.Combine(folder, "not-a-guid.jsonl"), User(sol, "x") + "\n" + Asst(sol, "y"));
            Directory.CreateDirectory(Path.Combine(folder, idA, "subagents")); // subagent folders are not sessions

            var live = new List<string> { idC.ToUpperInvariant() };
            var list = ClaudeSessionCatalog.List(root, sol, live, now, 10);
            var ids = new List<string>(); foreach (var s in list.Sessions) ids.Add(s.Id);
            string got = string.Join(",", ids);

            Ok("list: exactly E, A, G, H, newest first", got == idE + "," + idA + "," + idG + "," + idH, got);
            Ok("list: greeting-only session left out", Find(list, idB) == null);
            Ok("list: session live in a CA tab left out (id case ignored)", Find(list, idC) == null);
            Ok("list: same folder name, other folder -> left out", Find(list, idD) == null);
            Ok("list: no assistant turn -> left out", Find(list, idF) == null);
            Ok("list: non-GUID transcript ignored", list.Sessions.TrueForAll(s => s.Id.Length == 36));

            var a = Find(list, idA);
            Ok("A: first prompt is the one after CA's greeting", a != null && a.FirstPrompt == "Add the FREIGHT category test to ReDoTaxes and FBVerify", a == null ? null : a.FirstPrompt);
            Ok("A: summary is the last prompt when there is one", a != null && a.Summary == "now do SaveInvoice", a == null ? null : a.Summary);
            Ok("A: agent name CA1", a != null && a.AgentName == "CA1");
            Ok("A: last prompt read from the tail", a != null && a.LastPrompt == "now do SaveInvoice", a == null ? null : a.LastPrompt);
            Ok("A: cwd is the solution folder", a != null && string.Equals(a.Cwd, sol, StringComparison.OrdinalIgnoreCase), a == null ? null : a.Cwd);
            Ok("A: 12 min old -> not possibly open", a != null && !a.PossiblyOpenElsewhere);

            var e = Find(list, idE);
            Ok("E: written 50 s ago -> possibly open elsewhere", e != null && e.PossiblyOpenElsewhere);

            var g = Find(list, idG);
            Ok("G: meta, tool_result, MT message and cross-session lines skipped; reminder stripped, spaces collapsed",
               g != null && g.FirstPrompt == "Deploy the two SQL procs", g == null ? null : g.FirstPrompt);
            Ok("G: agent name from custom-title when no agent-name", g != null && g.AgentName == "CA6", g == null ? null : g.AgentName);
            Ok("G: last genuine last-prompt wins, greeting skipped", g != null && g.LastPrompt == "third and last", g == null ? null : g.LastPrompt);

            var h = Find(list, idH);
            Ok("H: prompt past the 256 KB head -> summary from last-prompt", h != null && h.FirstPrompt == null && h.Summary == "a prompt past the head",
               h == null ? "missing" : h.FirstPrompt + " / " + h.Summary);

            // ---- max and HasMore ----
            var two = ClaudeSessionCatalog.List(root, sol, live, now, 2);
            Ok("max 2: the two newest", two.Sessions.Count == 2 && two.Sessions[0].Id == idE && two.Sessions[1].Id == idA, two.Sessions.Count.ToString());
            Ok("max 2: HasMore", two.HasMore);
            Ok("max 10: not HasMore", !list.HasMore);
            var one = ClaudeSessionCatalog.List(root, sol, live, now, 1);
            Ok("max 1 (budget 3): newest valid found", one.Sessions.Count == 1 && one.Sessions[0].Id == idE);

            // ---- Folder that does not exist / bad input ----
            Ok("no folder for the solution -> empty, no throw", ClaudeSessionCatalog.List(root, Path.Combine(tmp, "nowhere"), null, now, 5).Sessions.Count == 0);
            Ok("null workDir -> empty", ClaudeSessionCatalog.List(root, null, null, now, 5).Sessions.Count == 0);

            // ---- TryGet: the pre-launch recheck ----
            Ok("TryGet: valid session", ClaudeSessionCatalog.TryGet(root, sol, idA, live, now) != null);
            Ok("TryGet: live in a CA tab -> null", ClaudeSessionCatalog.TryGet(root, sol, idC, live, now) == null);
            Ok("TryGet: greeting only -> null", ClaudeSessionCatalog.TryGet(root, sol, idB, null, now) == null);
            Ok("TryGet: not a GUID -> null", ClaudeSessionCatalog.TryGet(root, sol, "..\\x", null, now) == null);
            File.Delete(Path.Combine(folder, idA + ".jsonl"));
            Ok("TryGet: transcript deleted -> null", ClaudeSessionCatalog.TryGet(root, sol, idA, live, now) == null);

            // ---- Long prompt cut ----
            string idL = "77777777-0000-4000-8000-000000000009";
            Write(folder, idL, now.AddMinutes(-1), User(sol, new string('a', 400)), Asst(sol, "ok"));
            var l = ClaudeSessionCatalog.TryGet(root, sol, idL, null, now);
            Ok("long prompt cut to 160 chars with an ellipsis", l != null && l.Summary.Length == 160 && l.Summary.EndsWith("\u2026"), l == null ? null : l.Summary.Length.ToString());
            Ok("no last-prompt record -> summary falls back to the first prompt", l != null && l.LastPrompt == null && l.Summary == l.FirstPrompt);

            // ---- Real-data cases (first run against H:\Dev\aPOSitive and the CA repo) ----
            string idP = "66666666-0000-4000-8000-00000000000a";
            Write(folder, idP, now.AddMinutes(-2),
                AgentName("CA2"),
                User(sol, @"@C:\Users\X\AppData\Local\CLARIO~4\PASTE-~1\PASTE-~1.TXT"),
                User(sol, "Another Claude session sent a message: <teammate-message>do X</teammate-message>"),
                User(sol, "the real question"),
                Asst(sol, "ok"),
                LastPrompt("Another Claude session sent a message: hi"));
            var p = ClaudeSessionCatalog.TryGet(root, sol, idP, null, now);
            Ok("a prompt that is only a pasted-file @reference is skipped", p != null && p.FirstPrompt == "the real question", p == null ? null : p.FirstPrompt);
            Ok("a peer message rendered as a prompt is not the last prompt", p != null && p.LastPrompt == null, p == null ? null : p.LastPrompt);

            string idM = "55555555-0000-4000-8000-00000000000b";
            Write(folder, idM, now.AddMinutes(-3), AgentName("Charlie"), User(sol, "push it"), Asst(sol, "ok"));
            var caOnly = ClaudeSessionCatalog.List(root, sol, null, now, 20, ClaudeSessionCatalog.IsCaTabName);
            var all = ClaudeSessionCatalog.List(root, sol, null, now, 20);
            Ok("CA filter: MultiTerminal agent's session left out", Find(caOnly, idM) == null && Find(all, idM) != null);
            Ok("CA filter: CA sessions kept", Find(caOnly, idP) != null && Find(caOnly, idE) != null);
            Ok("TryGet with CA filter: MT session -> null", ClaudeSessionCatalog.TryGet(root, sol, idM, null, now, ClaudeSessionCatalog.IsCaTabName) == null);
            Ok("CA names: CA1, CA12, CA-Terminal-2-CC yes",
               ClaudeSessionCatalog.IsCaTabName("CA1") && ClaudeSessionCatalog.IsCaTabName("CA12") && ClaudeSessionCatalog.IsCaTabName("CA-Terminal-2-CC"));
            Ok("CA names: Charlie, CAT, CP1, null no",
               !ClaudeSessionCatalog.IsCaTabName("Charlie") && !ClaudeSessionCatalog.IsCaTabName("CAT") &&
               !ClaudeSessionCatalog.IsCaTabName("CP1") && !ClaudeSessionCatalog.IsCaTabName(null));

            // ---- Pipeline run 1 fixes ----
            Ok("CA names (security): a smart-quote breakout is rejected",
               !ClaudeSessionCatalog.IsCaTabName("CA-x’;calc;‘y") && !ClaudeSessionCatalog.IsCaTabName("CA-x';calc;'y"));
            Ok("CA names (security): '$', backtick and spaces rejected",
               !ClaudeSessionCatalog.IsCaTabName("CA-$env:X") && !ClaudeSessionCatalog.IsCaTabName("CA-a`b") && !ClaudeSessionCatalog.IsCaTabName("CA-a b"));
            Ok("CA names (security): length capped", !ClaudeSessionCatalog.IsCaTabName("CA-" + new string('a', 65)) &&
               ClaudeSessionCatalog.IsCaTabName("CA-" + new string('a', 64)) && !ClaudeSessionCatalog.IsCaTabName("CA12345"));

            // The name in the tail wins: a session renamed, or resumed under another -n, shows its current name;
            // and a session whose head has no name record is still found by its tail.
            string idR = "44444444-0000-4000-8000-00000000000c";
            Write(folder, idR, now.AddMinutes(-4), AgentName("CA1"), User(sol, "q"), Asst(sol, "a"), AgentName("CA3"));
            var rn = ClaudeSessionCatalog.TryGet(root, sol, idR, null, now);
            Ok("name: the tail's agent-name wins over the head's", rn != null && rn.AgentName == "CA3", rn == null ? null : rn.AgentName);
            string idT = "33333333-0000-4000-8000-00000000000d";
            var tl = new List<string> { User(sol, "Morning!"), Asst(sol, "hi") };
            for (int i = 0; i < 210; i++) tl.Add(Asst(sol, "line " + i));
            tl.Add(User(sol, "real"));
            tl.Add(AgentName("CA2"));
            tl.Add(LastPrompt("real"));
            Write(folder, idT, now.AddMinutes(-6), tl.ToArray());
            var tn = ClaudeSessionCatalog.TryGet(root, sol, idT, null, now, ClaudeSessionCatalog.IsCaTabName);
            Ok("name: a name record only past the head (line 213) still makes it a CA session", tn != null && tn.AgentName == "CA2",
               tn == null ? "hidden" : tn.AgentName);

            // Long paths: Claude Code cuts a folder name over 200 chars and appends "-<hash>". Such a fixture
            // needs paths over 260 chars, which a runtime with legacy path handling (this harness, maybe the
            // Clarion host) cannot create. Then only the fail-closed half is checked: no throw, empty list.
            string longSol = Path.Combine(tmp, new string('L', 120), new string('M', 100));
            try { Directory.CreateDirectory(longSol); Directory.CreateDirectory(Path.Combine(root, new string('x', 207))); }
            catch (PathTooLongException)
            {
                Console.WriteLine("  [skip] long-path fixture: this runtime uses legacy 260-char paths");
                bool threw = false; ClaudeSessionList none = null;
                try { none = ClaudeSessionCatalog.List(root, longSol, null, now, 5); } catch { threw = true; }
                Ok("long path, legacy runtime: no throw, empty list", !threw && none != null && none.Sessions.Count == 0);
                goto afterLong;
            }
            string longEnc = ClaudeSessionCatalog.EncodeProjectPath(longSol);
            Ok("fixture: the long path encodes past 200", longEnc.Length > 200, longEnc.Length.ToString());
            string longFolder = Path.Combine(root, longEnc.Substring(0, 200) + "-7r53k4");
            Directory.CreateDirectory(longFolder);
            string idLong = "22222222-0000-4000-8000-00000000000e";
            Write(longFolder, idLong, now.AddMinutes(-7), AgentName("CA1"), User(longSol, "deep"), Asst(longSol, "ok"));
            var ll = ClaudeSessionCatalog.List(root, longSol, null, now, 5, ClaudeSessionCatalog.IsCaTabName);
            Ok("long path: the cut, hashed folder is found", Find(ll, idLong) != null, ll.Sessions.Count.ToString());
            Ok("long path: TryGet finds it too", ClaudeSessionCatalog.TryGet(root, longSol, idLong, null, now) != null);
            Ok("long path: a sibling with the same 200-char prefix but another cwd stays out",
               ClaudeSessionCatalog.List(root, longSol + "X", null, now, 5).Sessions.Count == 0);
            afterLong:

            Ok("resumed note: tells the model not to greet", ClaudeSessionCatalog.ResumedSessionNote.Contains("do not greet"));

            // ---- FormatAge ----
            DateTime t = new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Local).ToUniversalTime();
            Ok("age: 30 s -> just now", ClaudeSessionCatalog.FormatAge(t.AddSeconds(-30), t) == "just now");
            Ok("age: 12 min", ClaudeSessionCatalog.FormatAge(t.AddMinutes(-12), t) == "12 min ago", ClaudeSessionCatalog.FormatAge(t.AddMinutes(-12), t));
            Ok("age: 3 h today", ClaudeSessionCatalog.FormatAge(t.AddHours(-3), t) == "3 h ago", ClaudeSessionCatalog.FormatAge(t.AddHours(-3), t));
            Ok("age: yesterday", ClaudeSessionCatalog.FormatAge(t.AddDays(-1), t) == "yesterday", ClaudeSessionCatalog.FormatAge(t.AddDays(-1), t));
            Ok("age: 4 days", ClaudeSessionCatalog.FormatAge(t.AddDays(-4), t) == "4 days ago", ClaudeSessionCatalog.FormatAge(t.AddDays(-4), t));
            Ok("age: 8 days -> date", ClaudeSessionCatalog.FormatAge(t.AddDays(-8), t) == "Oct 28", ClaudeSessionCatalog.FormatAge(t.AddDays(-8), t));

            // ---- Source check: one greeting list ----
            if (args.Length > 0)
            {
                string src = File.ReadAllText(Path.Combine(args[0], "AssistantChatControl.cs"));
                Ok("BuildInitialPrompt takes its greetings from ClaudeSessionCatalog",
                   src.Contains("ClaudeSessionCatalog.FunGreetings") && !src.Contains("\"Greetings, Clarion Developer!\""));

                // Launch (item 2). AssistantChatControl is IDE-coupled, so these are source checks.
                string build = Body(src, "private BuiltBackendCommand BuildClaudeCommand(");
                Ok("launch: BuildClaudeCommand found", build != null);
                if (build != null)
                {
                    Ok("launch: --resume for a picked session, --session-id for a new one",
                       build.Contains("--resume '{resumeId}'") && build.Contains("--session-id '{tab.ClaudeSessionId}'"));
                    Ok("launch: the session flag is in the claude invocation",
                       build.Contains("{nameFlag}{sessionFlag}"));
                    Ok("launch: a resume sends no greeting prompt",
                       build.Contains("string initialPrompt = resumeId == null ? BuildInitialPrompt(ctx.WorkDir) : null;"));
                    Ok("launch: a resume gets no Last Session Recap",
                       build.Contains("BuildSystemPromptInjection(ctx.WorkDir, includeRecap: resumeId == null)"));
                    Ok("launch: the resume request is one-shot",
                       build.Contains("tab.ResumeSessionId = null;"));
                    int sess = build.IndexOf("tab.ClaudeSessionId = resumeId ??", StringComparison.Ordinal);
                    int name = build.IndexOf("ResolveUniqueAgentName(tab)", StringComparison.Ordinal);
                    Ok("launch: session id chosen before the name (the preferred name is read there)", sess > 0 && name > sess);
                }
                string inj = Body(src, "private string BuildSystemPromptInjection(");
                Ok("recap: skipped when includeRecap is false", inj != null && inj.Contains("if (includeRecap) try"));
                string rel = Body(src, "private void ReleaseAgentName(");
                Ok("exit: ReleaseAgentName clears ClaudeSessionId before its early return",
                   rel != null && rel.IndexOf("tab.ClaudeSessionId = null;", StringComparison.Ordinal) >= 0 &&
                   rel.IndexOf("tab.ClaudeSessionId = null;", StringComparison.Ordinal) < rel.IndexOf("if (tab.AgentName == null) return;", StringComparison.Ordinal));
                string wws = Body(src, "private void OnWorkWithSolution(");
                Ok("resume: Work With Open Solution re-checks the session (TryGet, CA names, live ids) before opening the tab",
                   wws != null && wws.Contains("ClaudeSessionCatalog.TryGet(") && wws.Contains("GetLiveClaudeSessionIds()") &&
                   wws.Contains("IsCaTabName") &&
                   wws.IndexOf("TryGet(", StringComparison.Ordinal) < wws.IndexOf("OpenSolutionInNewTab(", StringComparison.Ordinal));
                string res = Body(src, "private string ResolveUniqueAgentName(TerminalTab tab, string prefix)");
                Ok("name: a resumed session keeps its CA name only when it is free",
                   res != null && res.Contains("!taken.Contains(preferred.Trim())") && res.Contains("tab.PreferredAgentName = null;"));
                Ok("name (run 1): kept only when the MultiTerminal roster answered, if MultiTerminal is configured",
                   res != null && res.Contains("(rosterAnswered || !rosterNeeded)") && res.Contains("rosterAnswered = true;"));

                // Pipeline run 1 fixes.
                string liveSrc = Body(src, "private List<string> GetLiveClaudeSessionIds()");
                Ok("live (run 1): a pending, not-yet-launched resume counts as live (double-click)",
                   liveSrc != null && liveSrc.Contains("NormalizeId(t.ResumeSessionId)") && liveSrc.Contains("if (pending != null) ids.Add(pending);"));
                Ok("live (run 1): the statusline's current session id counts as live (/clear)",
                   liveSrc != null && liveSrc.Contains("ca-statusline-") && liveSrc.Contains("StatusLineSessionId.Match") && liveSrc.Contains("if (now != null) ids.Add(now);"));
                Ok("resume last (run 1): lists the drawer's size, not 1", wws != null && wws.Contains("Math.Max(5, _homeSessionMax)"));
                Ok("resume (run 1): a session active in the last 2 minutes asks before resuming",
                   wws != null && wws.Contains("resume.PossiblyOpenElsewhere") && wws.Contains("MessageBoxButtons.YesNo"));
                string abort = Body(src, "private void AbortLaunch(TerminalTab tab)");
                Ok("abort (run 1): the one-shot resume fields are cleared",
                   abort != null && abort.Contains("tab.ResumeSessionId = null;") && abort.Contains("tab.PreferredAgentName = null;"));
                Ok("launch (run 1): a resume gets the do-not-greet note",
                   build != null && build.Contains("ClaudeSessionCatalog.ResumedSessionNote"));
                string sl = File.ReadAllText(Path.Combine(args[0], "Terminal", "ca-statusline.js"));
                Ok("statusline (run 1): writes the current session id", sl.Contains("sessionId: typeof data.session_id === 'string'"));
            }
            else Ok("project dir argument given", false, "Run-Tests.ps1 passes it");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
