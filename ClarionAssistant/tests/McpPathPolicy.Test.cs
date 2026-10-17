using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClarionAssistant.Services;

// Harness for McpPathPolicy (GH #266): the boundary read_file / write_file / append_to_file / list_directory and
// the Everything tools go through, because Claude Code's own permission rules never see an MCP tool.
//
// Every case is built on a REAL temp tree (solution, sibling, Clarion root, .red folder, CA data folder, fake
// profiles), and the link cases use a REAL junction (mklink /J) and the REAL 8.3 short name when the volume
// generates one - a policy that only reasons about strings would pass the string cases and still be walked
// out of by a junction.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class McpPathPolicyTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static void Allowed(McpPathPolicy p, string name, string path, PathAccess a = PathAccess.Read)
    {
        var d = p.Check(path, a);
        Ok(name, d.Allowed, d.Allowed ? null : d.Reason);
    }

    static PathDecision Denied(McpPathPolicy p, string name, string path, PathAccess a = PathAccess.Read)
    {
        var d = p.Check(path, a);
        Ok(name, !d.Allowed, d.Allowed ? "was allowed as " + d.Path : null);
        return d;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint len);

    static string Mk(params string[] parts)
    {
        string p = Path.Combine(parts);
        Directory.CreateDirectory(p);
        return p;
    }

    static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "x");
        return path;
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-pathpolicy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string junction = null;
        try
        {
            string sol = Mk(root, "Sol");
            string sibling = Mk(root, "Solution2");
            string proj = Mk(root, "SideProject");
            string clarion = Mk(root, "Clarion12");
            string redDir = Mk(root, "RedLibs");
            string ca = Mk(root, "AppData", "ClarionAssistant");
            string users = Mk(root, "Users");
            string me = Mk(users, "me");
            string bob = Mk(users, "bob");
            Mk(users, "Public");
            string outside = Mk(root, "Outside Long Folder Name");

            string solFile = Touch(Path.Combine(sol, "Main.clw"));
            string sibFile = Touch(Path.Combine(sibling, "Main.clw"));
            Touch(Path.Combine(outside, "secret.txt"));
            Touch(Path.Combine(ca, "settings.txt"));
            Touch(Path.Combine(ca, "embed-monitor.log"));
            Touch(Path.Combine(ca, "mcp-config.json"));
            Touch(Path.Combine(ca, "copilot", "config.json"));
            Touch(Path.Combine(bob, "Documents", "invoice.txt"));
            Touch(Path.Combine(me, "Documents", "notes.txt"));

            Func<PathPolicyInputs> full = () => new PathPolicyInputs
            {
                SolutionDir = sol,
                ProjectDirs = new List<string> { proj, sol },
                ClarionRoots = new List<string> { clarion },
                RedPaths = new List<string> { redDir, Path.GetPathRoot(root) },   // a drive-root .red entry must NOT widen the scope
                CaDataDir = ca,
                UserProfile = me,
                ExtraDeniedPaths = Path.Combine(sol, "Secrets") + "; Customers ;*\\*.pfx",
                ExtraAllowedRoots = users + ";relative\\folder;\\\\server\\share"
            };
            var p = McpPathPolicy.Build(full());

            Console.WriteLine("=== allowed roots ===");
            Allowed(p, "file in the solution folder (read)", solFile);
            Allowed(p, "file in the solution folder (write)", solFile, PathAccess.Write);
            Allowed(p, "new file in a not-yet-existing subfolder of the solution (write)", Path.Combine(sol, "new", "deeper", "x.clw"), PathAccess.Write);
            Allowed(p, "the solution folder itself", sol);
            Allowed(p, "project folder beside the solution", Path.Combine(proj, "a.cwproj"));
            Allowed(p, "Clarion root", Path.Combine(clarion, "libsrc", "win", "abfile.inc"));
            Allowed(p, ".red search folder", Path.Combine(redDir, "x.inc"), PathAccess.Write);
            Allowed(p, "own profile via an explicit Mcp.AllowedRoots entry", Path.Combine(me, "Documents", "notes.txt"));
            Allowed(p, "Public profile via an explicit Mcp.AllowedRoots entry", Path.Combine(users, "Public", "x.txt"));

            Console.WriteLine("=== containment is on a separator boundary ===");
            Denied(p, "sibling-prefix trick (Sol vs Solution2)", sibFile);
            Denied(p, "sibling-prefix trick, folder itself", sibling);
            Denied(p, "a drive-root .red entry is skipped, not trusted", Path.Combine(Path.GetPathRoot(root), "Windows", "win.ini"));

            Console.WriteLine("=== traversal ===");
            Denied(p, "'..' out of the solution", Path.Combine(sol, "..", "Solution2", "Main.clw"));
            Denied(p, "'..' chain out of the solution", sol + "\\sub\\..\\..\\Outside Long Folder Name\\secret.txt");
            Allowed(p, "'..' that stays inside the solution", sol + "\\sub\\..\\Main.clw");
            Denied(p, "forward-slash traversal", sol.Replace('\\', '/') + "/../Solution2/Main.clw");
            // The lexical check (Everything results) has no link resolution behind it: '..' must be gone BEFORE
            // containment, or "Sol\..\Solution2" would pass as "starts with Sol\".
            Ok("'..' out of the solution, lexical check", !p.CheckLexical(Path.Combine(sol, "..", "Solution2", "Main.clw"), PathAccess.Read).Allowed);
            Ok("'..' through a missing folder, lexical check", !p.CheckLexical(sol + "\\nope\\..\\..\\Solution2\\Main.clw", PathAccess.Read).Allowed);

            Console.WriteLine("=== UNC, device, network ===");
            var unc = Denied(p, "UNC path", "\\\\server\\share\\x.clw");
            // The drive-letter rule would refuse it too; this pins the explicit UNC rule and its message.
            Ok("UNC refusal says why (UNC), not just 'not fully qualified'", unc.Reason != null && unc.Reason.Contains("UNC"), unc.Reason);
            Denied(p, "UNC path with forward slashes", "//server/share/x.clw");
            Denied(p, "\\\\?\\ prefix (even onto an allowed root)", "\\\\?\\" + solFile);
            Denied(p, "\\\\.\\ device path", "\\\\.\\PhysicalDrive0");
            Denied(p, "\\??\\ NT path", "\\??\\" + solFile);
            Denied(p, "\\\\?\\UNC\\ path", "\\\\?\\UNC\\server\\share\\x");
            Denied(p, "reserved device name inside an allowed root", Path.Combine(sol, "nul.txt"));
            Denied(p, "reserved device name CON", Path.Combine(sol, "CON"));
            Denied(p, "alternate data stream", solFile + ":hidden");
            Denied(p, "wildcard character", Path.Combine(sol, "*.clw"));
            Denied(p, "control character", sol + "\\a\nb.clw");
            Ok("UNC setting entry is ignored, not trusted", ContainsLike(p.Ignored, "\\\\server\\share"));
            Ok("relative setting entry is ignored, not trusted", ContainsLike(p.Ignored, "relative\\folder"));

            Console.WriteLine("=== deny wins inside an allowed root ===");
            Denied(p, ".ssh inside the solution", Path.Combine(sol, ".ssh", "id_rsa"));
            Denied(p, ".aws inside the solution", Path.Combine(sol, ".aws", "credentials"));
            Denied(p, ".gnupg inside the solution", Path.Combine(sol, ".gnupg", "secring.gpg"));
            Denied(p, ".credentials.json anywhere (read)", Path.Combine(sol, "sub", ".credentials.json"));
            Denied(p, ".credentials.json anywhere (write)", Path.Combine(sol, ".credentials.json"), PathAccess.Write);

            Console.WriteLine("=== .claude: the project's is read-only, the user's is off limits ===");
            Allowed(p, "project .claude\\CLAUDE.md readable", Path.Combine(sol, ".claude", "CLAUDE.md"));
            Allowed(p, "project .claude folder readable", Path.Combine(sol, ".claude"));
            Denied(p, "project .claude\\settings.local.json not writable (self-granted permissions)", Path.Combine(sol, ".claude", "settings.local.json"), PathAccess.Write);
            Denied(p, "project .claude\\CLAUDE.md not writable", Path.Combine(sol, ".claude", "CLAUDE.md"), PathAccess.Write);
            Denied(p, "upper-cased .CLAUDE not writable", Path.Combine(sol, ".CLAUDE", "settings.json"), PathAccess.Write);
            Denied(p, "project .claude\\.credentials.json not readable", Path.Combine(sol, ".claude", ".credentials.json"));
            // A git worktree lives UNDER a .claude folder (repo\.claude\worktrees\name): the whole solution must not
            // become unreadable for it - McpStdio.EndToEndTest failed exactly so on integ-1017.
            string wtSol = Mk(root, "Repo", ".claude", "worktrees", "agent-x", "ClarionAssistant");
            string wtFile = Touch(Path.Combine(wtSol, "Services", "McpToolRegistry.cs"));
            var wt = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = wtSol, UserProfile = me, CaDataDir = ca });
            Allowed(wt, "solution under a worktree's .claude: file readable", wtFile);
            Allowed(wt, "solution under a worktree's .claude: folder listable", wtSol);
            Denied(wt, "solution under a worktree's .claude: file not writable", wtFile, PathAccess.Write);
            // The user's own Claude config: credentials and transcripts. Refused even though `users` is an explicit root.
            string meClaude = Mk(me, ".claude");
            Touch(Path.Combine(meClaude, ".credentials.json"));
            Touch(Path.Combine(meClaude, "projects", "H--x", "session.jsonl"));
            Denied(p, "profile .claude transcript not readable", Path.Combine(meClaude, "projects", "H--x", "session.jsonl"));
            Denied(p, "profile .claude folder not readable", meClaude);
            Denied(p, "profile .claude\\settings.json not writable", Path.Combine(meClaude, "settings.json"), PathAccess.Write);
            Ok("profile .claude refusal is a built-in that cannot be overridden",
                !p.Check(Path.Combine(meClaude, "settings.json"), PathAccess.Read).Allowed &&
                p.Check(Path.Combine(meClaude, "settings.json"), PathAccess.Read).Hint.Contains("cannot be overridden"));
            Denied(p, "profile .claude-work (alternate profile) not readable", Path.Combine(me, ".claude-work", "settings.json"));
            Denied(p, "profile .claude.json not readable", Path.Combine(me, ".claude.json"));
            Allowed(p, "a profile folder merely STARTING with .claude-like text is not the config", Path.Combine(me, "claude-notes", "a.txt"));
            string cfgDir = Mk(sol, "MovedClaudeConfig");
            var withCfg = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, UserProfile = me, ClaudeConfigDir = cfgDir });
            Denied(withCfg, "CLAUDE_CONFIG_DIR inside an allowed root not readable", Path.Combine(cfgDir, "history.jsonl"));
            Allowed(withCfg, "the rest of that root still readable", solFile);
            Denied(p, "Mcp.DeniedPaths folder entry", Path.Combine(sol, "Secrets", "keys.txt"));
            Denied(p, "Mcp.DeniedPaths bare-name entry", Path.Combine(sol, "Customers", "acme.txt"));
            Denied(p, "Mcp.DeniedPaths wildcard entry", Path.Combine(sol, "cert", "server.pfx"));
            Allowed(p, "a name that only STARTS like a denied one", Path.Combine(sol, "Secrets2", "ok.txt"));
            Denied(p, "another user's profile, even under an explicit root", Path.Combine(bob, "Documents", "invoice.txt"));
            Denied(p, "another user's profile folder itself", bob);

            Console.WriteLine("=== CA data folder: read-only, secrets denied ===");
            Allowed(p, "CA log readable", Path.Combine(ca, "embed-monitor.log"));
            Denied(p, "CA log not writable", Path.Combine(ca, "embed-monitor.log"), PathAccess.Write);
            Denied(p, "new file in CA folder not writable", Path.Combine(ca, "system-prompt-extra.md"), PathAccess.Write);
            Denied(p, "settings.txt not readable (MCP token)", Path.Combine(ca, "settings.txt"));
            Denied(p, "settings.txt not writable (would widen its own scope)", Path.Combine(ca, "settings.txt"), PathAccess.Write);
            Denied(p, "settings.txt backup not readable", Path.Combine(ca, "settings.txt.bak-123"));
            Denied(p, "mcp-config.json not readable", Path.Combine(ca, "mcp-config.json"));
            Denied(p, "copilot folder not readable", Path.Combine(ca, "copilot", "config.json"));
            // The real layout: CA's folder is INSIDE the profile. A developer who allows their whole profile must
            // still not make CA's own folder writable - else the model rewrites system-prompt-extra.md or its scope.
            string nestedCa = Mk(me, "AppData", "Roaming", "ClarionAssistant");
            var nested = McpPathPolicy.Build(new PathPolicyInputs { CaDataDir = nestedCa, UserProfile = me, ExtraAllowedRoots = me });
            Allowed(nested, "profile allowed explicitly: a profile file is writable", Path.Combine(me, "Documents", "notes.txt"), PathAccess.Write);
            Denied(nested, "profile allowed explicitly: CA's folder still not writable", Path.Combine(nestedCa, "system-prompt-extra.md"), PathAccess.Write);
            Denied(nested, "profile allowed explicitly: settings.txt still not readable", Path.Combine(nestedCa, "settings.txt"));

            Console.WriteLine("=== case and separators ===");
            Allowed(p, "upper-cased path", solFile.ToUpperInvariant());
            Allowed(p, "lower-cased path", solFile.ToLowerInvariant());
            Denied(p, "upper-cased denied segment", Path.Combine(sol, ".SSH", "id_rsa"));
            Denied(p, "upper-cased Mcp.DeniedPaths entry", Path.Combine(sol, "SECRETS", "keys.txt"));
            Allowed(p, "trailing separator", sol + "\\");
            Allowed(p, "doubled separators", sol + "\\\\Main.clw");
            Allowed(p, "forward slashes", solFile.Replace('\\', '/'));
            Allowed(p, "surrounding whitespace", "  " + solFile + "  ");

            Console.WriteLine("=== empty and relative ===");
            Denied(p, "null", null);
            Denied(p, "empty", "");
            Denied(p, "whitespace", "   ");
            Denied(p, "bare file name", "Main.clw");
            Denied(p, "dot-relative", ".\\Main.clw");
            Denied(p, "dot-dot-relative", "..\\Main.clw");
            Denied(p, "root-relative (current drive)", "\\Windows\\win.ini");
            Denied(p, "drive-relative (C:x)", sol.Substring(0, 2) + "Main.clw");

            Console.WriteLine("=== refusal text ===");
            var outsideD = p.Check(Path.Combine(outside, "secret.txt"), PathAccess.Read);
            Ok("outside-root refusal starts with 'Error:'", outsideD.Refusal("read_file", "x").StartsWith("Error: read_file refused"));
            Ok("outside-root refusal names Mcp.AllowedRoots", outsideD.Refusal("read_file", "x").Contains(McpPathPolicy.AllowedRootsSetting));
            var userDenyD = p.Check(Path.Combine(sol, "Secrets", "keys.txt"), PathAccess.Read);
            Ok("user-deny refusal names Mcp.DeniedPaths", userDenyD.Refusal("read_file", "x").Contains(McpPathPolicy.DeniedPathsSetting));
            var builtinD = p.Check(Path.Combine(sol, ".ssh", "id_rsa"), PathAccess.Read);
            Ok("built-in refusal says it cannot be overridden", builtinD.Refusal("read_file", "x").Contains("cannot be overridden"));

            Console.WriteLine("=== links: junctions and 8.3 short names ===");
            junction = Path.Combine(sol, "escape");
            if (MakeJunction(junction, outside))
            {
                Denied(p, "junction inside the solution pointing outside it", Path.Combine(junction, "secret.txt"));
                Denied(p, "write through that junction", Path.Combine(junction, "new.txt"), PathAccess.Write);
                Denied(p, "the junction itself", junction);
                Ok("lexical check (Everything results) judges the name as indexed",
                    p.CheckLexical(Path.Combine(junction, "secret.txt"), PathAccess.Read).Allowed);
            }
            else Ok("could create a junction for the link cases", false, "mklink /J failed");

            string shortOutside = ShortName(Path.Combine(outside, "secret.txt"));
            if (shortOutside != null && !string.Equals(shortOutside, Path.Combine(outside, "secret.txt"), StringComparison.OrdinalIgnoreCase))
            {
                Denied(p, "8.3 short name of a path outside the roots", shortOutside);
                // A short-named ROOT: the root is given in 8.3 form, the file in long form - and the reverse.
                var shortRoot = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = ShortName(outside) });
                Allowed(shortRoot, "long path under a root given as its 8.3 name", Path.Combine(outside, "secret.txt"));
                Allowed(McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = outside }), "8.3 path under a root given long", shortOutside);
            }
            else Console.WriteLine("  [skip] 8.3 short names are not generated on this volume");

            Console.WriteLine("=== no solution context ===");
            var noSln = McpPathPolicy.Build(new PathPolicyInputs
            {
                ClarionRoots = new List<string> { clarion },
                RedPaths = new List<string> { redDir },
                CaDataDir = ca,
                UserProfile = me
            });
            Denied(noSln, "old solution folder is no longer a root", solFile);
            Allowed(noSln, "Clarion root still applies", Path.Combine(clarion, "x.tpl"));
            Allowed(noSln, ".red folder still applies", Path.Combine(redDir, "x.inc"));
            Allowed(noSln, "CA log still readable", Path.Combine(ca, "embed-monitor.log"));
            Denied(noSln, "settings.txt still denied", Path.Combine(ca, "settings.txt"));
            var nothing = McpPathPolicy.Build(new PathPolicyInputs());
            Denied(nothing, "no roots at all: everything refused", solFile);
            Ok("no roots at all: summary says there are none", nothing.RootSummary().StartsWith("there are none"));
            Ok("no roots at all: Everything has nothing to search", nothing.SearchRoots().Count == 0);

            Console.WriteLine("=== Everything query rules ===");
            Ok("bare '*' refused", McpPathPolicy.IsBareWildcard("*"));
            Ok("bare '*.*' refused", McpPathPolicy.IsBareWildcard("*.*"));
            Ok("empty refused", McpPathPolicy.IsBareWildcard(" "));
            Ok("punctuation-only refused", McpPathPolicy.IsBareWildcard("! | <>"));
            Ok("'*.clw' accepted", !McpPathPolicy.IsBareWildcard("*.clw"));
            Ok("max_results negative -> default", McpPathPolicy.ClampMax(-5, 100, 1000) == 100);
            Ok("max_results 0 -> default", McpPathPolicy.ClampMax(0, 50, 1000) == 50);
            Ok("max_results huge -> ceiling", McpPathPolicy.ClampMax(int.MaxValue, 100, 1000) == 1000);
            Ok("max_results in range kept", McpPathPolicy.ClampMax(250, 100, 1000) == 250);
            Ok("search roots never include a drive root from .red", !ContainsExact(p.SearchRoots(), Path.GetPathRoot(root)));
        }
        finally
        {
            if (junction != null && Directory.Exists(junction)) { try { Directory.Delete(junction); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("McpPathPolicy: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static bool ContainsLike(IList<string> list, string needle)
    {
        foreach (string s in list) if (s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static bool ContainsExact(IList<string> list, string needle)
    {
        foreach (string s in list) if (string.Equals(s, needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool MakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var pr = Process.Start(psi))
            {
                pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd();
                pr.WaitForExit(15000);
                return pr.ExitCode == 0 && Directory.Exists(link);
            }
        }
        catch { return false; }
    }

    static string ShortName(string longPath)
    {
        var sb = new StringBuilder(1024);
        uint n = GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
        return n == 0 || n >= sb.Capacity ? null : sb.ToString();
    }
}
