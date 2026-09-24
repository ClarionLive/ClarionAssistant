using System;
using System.IO;
using ClarionAssistant.Services;

// Regression harness for GH #227: "Clarion Assistant completely replaced my Claude.md".
// Compiles the REAL Services\ClaudeMdDeployer.cs (zero IDE coupling) and drives it against a
// fake user profile in %TEMP% - it never touches the real ~\.claude.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (optional but passed by Run-Tests.ps1): the ClarionAssistant project dir, so the real
// shipped prompt can be checked against ClaudeMdDeployer.Signature.
//
// PROVEN ABLE TO FAIL: this harness was run against a copy of Deploy() reduced to the pre-fix body
// (create .claude, File.Copy overwrite:true) - 12 of 15 red - and against Deploy() with each guard
// removed in turn; every mutation turned at least one check red. See ticket 79ef5f10.
static class ClaudeMdDeployerTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-claudemd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string profile = Path.Combine(root, "Users", "Someone");
            Directory.CreateDirectory(profile);

            string source = Path.Combine(root, "clarion-assistant-prompt.md");
            // Starts like the real shipped prompt so the legacy-signature path is exercised honestly.
            File.WriteAllText(source,
                "# Clarion IDE Assistant\r\n\r\nYou are running INSIDE the Clarion IDE as an embedded assistant. (v-new)\r\n",
                EncodingHelper.Utf8NoBom);

            const string userGlobal = "# Global Preferences\r\n\r\n- Keep responses concise.\r\n";
            const string userProject = "# My project rules\r\n\r\nUse tabs.\r\n";

            // --- 1. The reported bug: New Chat, no working directory -> workDir = %USERPROFILE%.
            string globalMd = Path.Combine(profile, ".claude", "CLAUDE.md");
            Directory.CreateDirectory(Path.GetDirectoryName(globalMd));
            File.WriteAllText(globalMd, userGlobal, EncodingHelper.Utf8NoBom);

            var o1 = ClaudeMdDeployer.Deploy(source, profile, profile, null);
            Ok("New Chat (workDir = profile): global CLAUDE.md untouched",
               File.ReadAllText(globalMd) == userGlobal, "content now: " + Head(globalMd));
            Ok("New Chat: outcome is SkippedUserConfig", o1 == ClaudeMdDeployer.Outcome.SkippedUserConfig, o1.ToString());
            Ok("New Chat: prompt reported NOT delivered (caller must append it)", !ClaudeMdDeployer.Delivered(o1));

            // --- 1b. Same, with no global CLAUDE.md yet: still must not create one.
            string profile2 = Path.Combine(root, "Users", "Fresh");
            Directory.CreateDirectory(profile2);
            ClaudeMdDeployer.Deploy(source, profile2, profile2, null);
            Ok("Profile with no CLAUDE.md: none created",
               !File.Exists(Path.Combine(profile2, ".claude", "CLAUDE.md")));

            // --- 1c. Trailing slash / different case on the workDir must not slip past the check.
            ClaudeMdDeployer.Deploy(source, profile.ToUpperInvariant() + "\\", profile, null);
            Ok("Profile path with trailing slash + other case: untouched", File.ReadAllText(globalMd) == userGlobal);

            // --- 2. CLAUDE_CONFIG_DIR relocates the global dir; that one is protected too.
            string cfgHome = Path.Combine(root, "cfg");
            string cfgDir = Path.Combine(cfgHome, ".claude");
            Directory.CreateDirectory(cfgDir);
            string cfgMd = Path.Combine(cfgDir, "CLAUDE.md");
            File.WriteAllText(cfgMd, userGlobal, EncodingHelper.Utf8NoBom);
            var o2 = ClaudeMdDeployer.Deploy(source, cfgHome, profile, cfgDir);
            Ok("CLAUDE_CONFIG_DIR: global CLAUDE.md untouched", File.ReadAllText(cfgMd) == userGlobal, Head(cfgMd));
            Ok("CLAUDE_CONFIG_DIR: outcome is SkippedUserConfig", o2 == ClaudeMdDeployer.Outcome.SkippedUserConfig, o2.ToString());

            // --- 3. A user-authored project CLAUDE.md is never overwritten.
            string proj = Path.Combine(root, "Projects", "Invoices");
            string projMd = Path.Combine(proj, ".claude", "CLAUDE.md");
            Directory.CreateDirectory(Path.GetDirectoryName(projMd));
            File.WriteAllText(projMd, userProject, EncodingHelper.Utf8NoBom);
            var o3 = ClaudeMdDeployer.Deploy(source, proj, profile, null);
            Ok("User-authored project CLAUDE.md untouched", File.ReadAllText(projMd) == userProject, Head(projMd));
            Ok("User-authored: outcome is SkippedUserAuthored", o3 == ClaudeMdDeployer.Outcome.SkippedUserAuthored, o3.ToString());

            // --- 4. Project with no CLAUDE.md: created, byte-identical to the prompt, no BOM.
            string proj2 = Path.Combine(root, "Projects", "Payroll");
            Directory.CreateDirectory(proj2);
            var o4 = ClaudeMdDeployer.Deploy(source, proj2, profile, null);
            string proj2Md = Path.Combine(proj2, ".claude", "CLAUDE.md");
            Ok("Fresh project: CLAUDE.md created", o4 == ClaudeMdDeployer.Outcome.Created && File.Exists(proj2Md), o4.ToString());
            Ok("Fresh project: byte-identical to the shipped prompt (Check-PromptSync relies on it)",
               File.Exists(proj2Md) && SameBytes(proj2Md, source));
            Ok("Fresh project: no UTF-8 BOM", File.Exists(proj2Md) && !HasBom(proj2Md));

            // --- 5. CA's own file from an older build is refreshed with the current prompt.
            File.WriteAllText(proj2Md,
                "# Clarion IDE Assistant\r\n\r\nYou are running INSIDE the Clarion IDE as an embedded assistant.\r\n(v-old)\r\n",
                EncodingHelper.Utf8NoBom);
            var o5 = ClaudeMdDeployer.Deploy(source, proj2, profile, null);
            Ok("CA-owned older copy: refreshed",
               o5 == ClaudeMdDeployer.Outcome.Refreshed && File.ReadAllText(proj2Md).Contains("(v-new)"), o5.ToString());

            // --- 6. Same H1, but the user rewrote the body: that is theirs now.
            const string userFork = "# Clarion IDE Assistant\r\n\r\nMy own rules for this project.\r\n";
            File.WriteAllText(proj2Md, userFork, EncodingHelper.Utf8NoBom);
            var o6 = ClaudeMdDeployer.Deploy(source, proj2, profile, null);
            Ok("User file sharing only the H1: untouched",
               o6 == ClaudeMdDeployer.Outcome.SkippedUserAuthored && File.ReadAllText(proj2Md) == userFork, o6.ToString());

            // --- 6b. The REAL shipped prompt must still open with the signature, or every CA copy
            //         would be mistaken for the user's and stop refreshing. Needs the repo dir.
            if (args.Length > 0)
            {
                string shipped = Path.Combine(args[0], "Terminal", "clarion-assistant-prompt.md");
                Ok("Shipped prompt opens with ClaudeMdDeployer.Signature",
                   File.Exists(shipped) && ClaudeMdDeployer.IsCaOwned(File.ReadAllText(shipped)), shipped);
            }
            else
            {
                Ok("Shipped prompt check needs the repo dir as argument 1 (Run-Tests.ps1 passes it)", false);
            }

            // --- 8. settings.local.json: only CA's own statusLine-only file may be replaced.
            string caSettings = "{\"statusLine\":{\"type\":\"command\",\"command\":\"\\\"C:/Program Files/nodejs/node.exe\\\" \\\"C:/Clarion12/Accessory/AddIns/ClarionAssistant/Terminal/ca-statusline.js\\\"\"}}";
            Ok("settings.local.json: CA's own statusLine file is CA-owned", ClaudeMdDeployer.IsCaOwnedSettingsLocal(caSettings));
            Ok("settings.local.json: user permissions alongside our statusLine are NOT CA-owned",
               !ClaudeMdDeployer.IsCaOwnedSettingsLocal("{\n  \"statusLine\": {\"type\":\"command\",\"command\":\"node ca-statusline.js\"},\n  \"permissions\": {\"allow\": [\"Bash(git status)\"]}\n}"));
            Ok("settings.local.json: our statusLine plus an extra key on one line is NOT CA-owned",
               !ClaudeMdDeployer.IsCaOwnedSettingsLocal("{\"statusLine\":{\"type\":\"command\",\"command\":\"x ca-statusline.js\"},\"permissions\":{\"allow\":[]}}"));
            Ok("settings.local.json: someone else's statusLine is NOT CA-owned",
               !ClaudeMdDeployer.IsCaOwnedSettingsLocal("{\"statusLine\":{\"type\":\"command\",\"command\":\"my-own-line.sh\"}}"));

            // --- 7. Missing source: nothing written, reported as not delivered.
            string proj3 = Path.Combine(root, "Projects", "Empty");
            Directory.CreateDirectory(proj3);
            var o7 = ClaudeMdDeployer.Deploy(Path.Combine(root, "nope.md"), proj3, profile, null);
            Ok("Missing source: SourceMissing, no .claude created",
               o7 == ClaudeMdDeployer.Outcome.SourceMissing && !Directory.Exists(Path.Combine(proj3, ".claude")), o7.ToString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("  " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static string Head(string path)
    {
        string s = File.Exists(path) ? File.ReadAllText(path) : "<missing>";
        s = s.Replace("\r", "").Replace("\n", " | ");
        return s.Length > 90 ? s.Substring(0, 90) + "..." : s;
    }

    static bool SameBytes(string a, string b)
    {
        byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
        return true;
    }

    static bool HasBom(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        return b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    }
}
