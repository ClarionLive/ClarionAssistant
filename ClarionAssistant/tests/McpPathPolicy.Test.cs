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
                ExtraAllowedRoots = users + ";relative\\folder"
            };
            var p = McpPathPolicy.Build(full());

            Console.WriteLine("=== allowed roots ===");
            Allowed(p, "file in the solution folder (read)", solFile);
            Allowed(p, "file in the solution folder (write)", solFile, PathAccess.Write);
            Allowed(p, "new file in a not-yet-existing subfolder of the solution (write)", Path.Combine(sol, "new", "deeper", "x.clw"), PathAccess.Write);
            Allowed(p, "the solution folder itself", sol);
            Allowed(p, "project folder beside the solution", Path.Combine(proj, "a.cwproj"));
            Allowed(p, "Clarion root (read)", Path.Combine(clarion, "libsrc", "win", "abfile.inc"));
            Denied(p, "Clarion root is read-only (no overwriting the install or dropping an addin)", Path.Combine(clarion, "accessory", "addins", "ClarionAssistant", "x.dll"), PathAccess.Write);
            Allowed(p, ".red search folder outside the solution (read)", Path.Combine(redDir, "x.inc"));
            Denied(p, ".red search folder outside the solution is read-only", Path.Combine(redDir, "x.inc"), PathAccess.Write);
            Allowed(p, "project folder outside the solution (read)", Path.Combine(proj, "a.clw"));
            Denied(p, "project folder outside the solution is read-only", Path.Combine(proj, "a.clw"), PathAccess.Write);
            Allowed(p, "own profile via an explicit Mcp.AllowedRoots entry", Path.Combine(me, "Documents", "notes.txt"));
            Allowed(p, "Public profile via an explicit Mcp.AllowedRoots entry", Path.Combine(users, "Public", "x.txt"));

            Console.WriteLine("=== containment is on a separator boundary ===");
            Denied(p, "sibling-prefix trick (Sol vs Solution2)", sibFile);
            Denied(p, "sibling-prefix trick, folder itself", sibling);
            Denied(p, "a drive-root .red entry is skipped, not trusted", Path.Combine(Path.GetPathRoot(root), "Windows", "win.ini"));
            // On a drive that holds no protected folder the protected-folder rule cannot catch it: the drive-root rule must.
            Denied(McpPathPolicy.Build(new PathPolicyInputs { RedPaths = new List<string> { "Q:\\" }, UserProfile = me }),
                "a drive-root .red entry on a drive with no protected folder is skipped too", "Q:\\Libs\\x.inc");

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
            // Adversary HIGH: a VM shared folder / team share is a real place to keep a solution. Only the developer's
            // OWN setting may allow one; never an automatic root. (Lexical checks: no SMB traffic from the harness.)
            var share = McpPathPolicy.Build(new PathPolicyInputs { ExtraAllowedRoots = "\\\\server\\share\\Team", UserProfile = me });
            Ok("explicit Mcp.AllowedRoots UNC entry allows that share", share.CheckLexical("\\\\server\\share\\Team\\App\\Main.clw", PathAccess.Read).Allowed);
            Ok("...but not a sibling folder on the same share", !share.CheckLexical("\\\\server\\share\\Other\\x.clw", PathAccess.Read).Allowed);
            Ok("...nor another share", !share.CheckLexical("\\\\server\\other\\x.clw", PathAccess.Read).Allowed);
            Ok("...and '..' cannot climb out of it", !share.CheckLexical("\\\\server\\share\\Team\\..\\Other\\x.clw", PathAccess.Read).Allowed);
            var uncSln = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = "\\\\server\\share\\Sol", RedPaths = new List<string> { "\\\\server\\libs" }, UserProfile = me });
            Ok("a solution on a share is NOT an automatic root", uncSln.Roots.Count == 0);
            Ok("...and the policy says so (SolutionNote names the folder and the setting)",
                uncSln.SolutionNote != null && uncSln.SolutionNote.Contains("\\\\server\\share\\Sol") && uncSln.SolutionNote.Contains(McpPathPolicy.AllowedRootsSetting), uncSln.SolutionNote);
            var uncD = uncSln.Check("\\\\server\\share\\Sol\\Main.clw", PathAccess.Read);
            Ok("...and the refusal for its files carries that note", !uncD.Allowed && uncD.Refusal("read_file", "x").Contains("network drive"), uncD.Hint);
            var uncFixed = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = "\\\\server\\share\\Sol", ExtraAllowedRoots = "\\\\server\\share\\Sol", UserProfile = me });
            Ok("adding the share to Mcp.AllowedRoots clears the note", uncFixed.SolutionNote == null, uncFixed.SolutionNote);
            Ok("device path still refused even as an explicit root", McpPathPolicy.Build(new PathPolicyInputs { ExtraAllowedRoots = "\\\\?\\C:\\" }).Roots.Count == 0);
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
            // Debugger MEDIUM: a .claude segment immediately followed by "worktrees" is Claude Code's worktree container,
            // so it does not write-deny the checkout below it. Any other .claude segment still does, deeper ones too.
            Allowed(wt, "solution under .claude\\worktrees: file writable", wtFile, PathAccess.Write);
            Denied(wt, "...but a .claude INSIDE that worktree is still write-denied", Path.Combine(wtSol, ".claude", "settings.json"), PathAccess.Write);
            Allowed(wt, "...and still readable", Path.Combine(wtSol, ".claude", "settings.json"));
            Denied(wt, "...a .sln inside the worktree is still a boundary file", Path.Combine(wtSol, "x.sln"), PathAccess.Write);
            string notWt = Mk(root, "Repo2", ".claude", "agents", "Sol3");
            var nwt = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = notWt, UserProfile = me });
            Denied(nwt, ".claude followed by anything but 'worktrees' still write-denies", Path.Combine(notWt, "a.clw"), PathAccess.Write);
            Ok("HasWriteDeniedClaudeSegment: the container itself (nothing below) still denies", McpPathPolicy.HasWriteDeniedClaudeSegment("C:\\r\\.claude\\worktrees"));
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

            Console.WriteLine("=== security gate run 1: automatic roots cannot widen themselves ===");
            // A model that could write the .sln added a project at ..\..\..\Users\me\x.cwproj: the profile became a root.
            var widened = McpPathPolicy.Build(new PathPolicyInputs
            {
                SolutionDir = sol,
                ProjectDirs = new List<string> { me, users, Path.Combine(me, "AppData") },
                RedPaths = new List<string> { users },
                UserProfile = me,
                ProtectedDirs = new List<string> { Path.Combine(me, "AppData", "Roaming") }
            });
            Denied(widened, ".sln project pointing AT the profile: profile not a root", Path.Combine(me, "Documents", "notes.txt"));
            Denied(widened, ".red entry CONTAINING the profile: not a root", Path.Combine(me, "Documents", "notes.txt"));
            Ok("...and both are reported as skipped", ContainsLike(widened.SkippedRoots, me) && ContainsLike(widened.SkippedRoots, users));
            Ok("a project folder that contains a protected folder (AppData\\Roaming) is skipped", ContainsLike(widened.SkippedRoots, Path.Combine(me, "AppData")));
            Allowed(widened, "...the solution itself still works", solFile, PathAccess.Write);
            string inProfileSol = Mk(me, "source", "repos", "MyApp");
            var inside = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = inProfileSol, UserProfile = me });
            Allowed(inside, "a solution INSIDE the profile (%USERPROFILE%\\source\\...) is still a root", Path.Combine(inProfileSol, "Main.clw"), PathAccess.Write);
            Ok("...and is not reported as skipped", inside.SkippedRoots.Count == 0 && inside.SolutionNote == null);
            var profAsSol = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = me, UserProfile = me });
            Ok("a solution folder that IS the profile is not a root, with a note", profAsSol.Roots.Count == 0 && profAsSol.SolutionNote != null, profAsSol.SolutionNote);
            Allowed(McpPathPolicy.Build(new PathPolicyInputs { ExtraAllowedRoots = me, UserProfile = me }),
                "...but the developer's own Mcp.AllowedRoots may name the profile", Path.Combine(me, "Documents", "notes.txt"));
            string jProfile = Path.Combine(root, "JProfile");
            if (MakeJunction(jProfile, users))
            {
                var viaJ = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, RedPaths = new List<string> { jProfile }, UserProfile = me });
                Ok("a .red entry that reaches the profile through a junction is skipped", ContainsLike(viaJ.SkippedRoots, jProfile));
                Denied(viaJ, "...so the profile is not reachable through it", Path.Combine(jProfile, "me", "Documents", "notes.txt"));
                try { Directory.Delete(jProfile); } catch { }
            }
            else Ok("could create the profile junction", false, "mklink /J failed");

            Console.WriteLine("=== boundary files are never written by the MCP tools ===");
            foreach (string bf in new[] { "Sol.sln", "App.cwproj", "Clarion120.red", "clarion-assistant.json", "ClarionProperties.xml", "SOL.SLN" })
            {
                var w = p.Check(Path.Combine(sol, bf), PathAccess.Write);
                Ok(bf + " write refused", !w.Allowed, w.Allowed ? "was allowed" : null);
                Ok(bf + " refusal says it defines the scope and names Claude Code's Edit tool",
                    !w.Allowed && w.Hint != null && w.Hint.Contains("define which folders") && w.Hint.Contains("Edit tool"), w.Hint);
                Allowed(p, bf + " still readable", Path.Combine(sol, bf));
            }
            Denied(p, "boundary file under an EXPLICIT root still not writable", Path.Combine(me, "x.sln"), PathAccess.Write);
            Allowed(p, "a .slnx-like name is not a boundary file", Path.Combine(sol, "notes.sln.txt"), PathAccess.Write);

            Console.WriteLine("=== most specific root decides ===");
            string c12 = Mk(root, "C12");
            string exSol = Mk(c12, "Examples", "Sol");
            var nestedRoots = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = exSol, ClarionRoots = new List<string> { c12 }, UserProfile = me });
            Allowed(nestedRoots, "solution inside the Clarion root stays writable", Path.Combine(exSol, "Main.clw"), PathAccess.Write);
            Denied(nestedRoots, "...the rest of the Clarion root is not", Path.Combine(c12, "libsrc", "win", "abfile.inc"), PathAccess.Write);
            // ...and the other way round: a Clarion kept INSIDE the solution folder (a portable install in the repo). The
            // solution root is listed first, so "first containing root wins" would make the Clarion install writable.
            string portable = Mk(root, "Portable");
            string portableC12 = Mk(portable, "C12");
            var portableRoots = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = portable, ClarionRoots = new List<string> { portableC12 }, UserProfile = me });
            Denied(portableRoots, "Clarion root INSIDE the solution folder stays read-only", Path.Combine(portableC12, "bin", "x.dll"), PathAccess.Write);
            Allowed(portableRoots, "...while the solution around it is writable", Path.Combine(portable, "Main.clw"), PathAccess.Write);
            var explicitC12 = McpPathPolicy.Build(new PathPolicyInputs { ClarionRoots = new List<string> { c12 }, ExtraAllowedRoots = c12, UserProfile = me });
            Allowed(explicitC12, "Mcp.AllowedRoots naming the Clarion root makes it writable", Path.Combine(c12, "accessory", "template", "x.tpl"), PathAccess.Write);

            Console.WriteLine("=== token stores ===");
            string localCa = Mk(root, "Local", "ClarionAssistant");
            Touch(Path.Combine(localCa, "ide-endpoint", "1234-5000.json"));
            var tok = McpPathPolicy.Build(new PathPolicyInputs { LocalCaDir = localCa, UserProfile = me, ExtraAllowedRoots = Path.Combine(root, "Local") });
            Denied(tok, "%LOCALAPPDATA%\\ClarionAssistant\\ide-endpoint (bearer tokens) refused even under an explicit root", Path.Combine(localCa, "ide-endpoint", "1234-5000.json"));
            Denied(tok, "...the folder itself", Path.Combine(localCa, "ide-endpoint"));
            Denied(tok, "WebView2 profile (cookies) refused", Path.Combine(localCa, "WebView2Data", "EBWebView", "Default", "Cookies"));
            Allowed(tok, "the rest of that root still readable", Path.Combine(localCa, "monaco-spike.log"));
            Denied(p, "mcp-config.json anywhere", Path.Combine(sol, "mcp-config.json"));
            Denied(p, "mcp-config-vscode.json anywhere", Path.Combine(sol, "sub", "mcp-config-vscode.json"));
            Denied(p, ".git-credentials anywhere", Path.Combine(me, ".git-credentials"));
            Denied(p, ".npmrc anywhere", Path.Combine(sol, ".npmrc"));

            Console.WriteLine("=== pattern denies follow a junctioned base ===");
            string realCa = Mk(root, "RealRoaming", "ClarionAssistant");
            Touch(Path.Combine(realCa, "settings.txt"));
            Touch(Path.Combine(realCa, "embed.log"));
            string jRoaming = Path.Combine(root, "JRoaming");
            if (MakeJunction(jRoaming, Path.Combine(root, "RealRoaming")))
            {
                var jca = McpPathPolicy.Build(new PathPolicyInputs { CaDataDir = Path.Combine(jRoaming, "ClarionAssistant"), UserProfile = me });
                Denied(jca, "junctioned CA folder: settings.txt by its REAL path", Path.Combine(realCa, "settings.txt"));
                Denied(jca, "junctioned CA folder: settings.txt by the junction path", Path.Combine(jRoaming, "ClarionAssistant", "settings.txt"));
                Denied(jca, "junctioned CA folder: mcp-*.json by its REAL path", Path.Combine(realCa, "mcp-extra.json"));
                Ok("junctioned CA folder: settings.txt by its REAL path, lexical check (Everything)", !jca.CheckLexical(Path.Combine(realCa, "settings.txt"), PathAccess.Read).Allowed);
                Allowed(jca, "...its log still readable by the real path", Path.Combine(realCa, "embed.log"));
                Denied(jca, "...and not writable by the real path", Path.Combine(realCa, "embed.log"), PathAccess.Write);
                try { Directory.Delete(jRoaming); } catch { }
            }
            else Ok("could create the roaming junction", false, "mklink /J failed");

            Console.WriteLine("=== trailing dot / space segments ===");
            var dotD = Denied(p, "segment ending in a dot", sol + "\\x.\\Main.clw");
            Ok("...says why", dotD.Reason != null && dotD.Reason.Contains("dot"), dotD.Reason);
            Denied(p, "segment ending in a space", sol + "\\x \\Main.clw");
            Denied(p, "file name ending in a dot", sol + "\\Main.clw.");
            Denied(p, "'...' segment", sol + "\\...\\Main.clw");
            Allowed(p, "'.' and '..' segments still normalise", sol + "\\.\\sub\\..\\Main.clw");

            Console.WriteLine("=== security gate run 2 ===");
            // 1. A junction INSIDE the solution, named by the .red, pointing OUTSIDE it: readable root, never writable.
            string inLink = Path.Combine(sol, "LinkedLibs");
            if (MakeJunction(inLink, outside))
            {
                var linked = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, RedPaths = new List<string> { inLink }, UserProfile = me });
                Denied(linked, "junction in the solution to outside it: not writable through the .red root", Path.Combine(inLink, "new.clw"), PathAccess.Write);
                Denied(linked, "...nor by its real path", Path.Combine(outside, "new.clw"), PathAccess.Write);
                Allowed(linked, "...still readable (the .red names it)", Path.Combine(inLink, "secret.txt"));
                try { Directory.Delete(inLink); } catch { }
            }
            else Ok("could create the in-solution junction", false, "mklink /J failed");
            string inSub = Mk(sol, "Libs");
            Allowed(McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, RedPaths = new List<string> { inSub }, UserProfile = me }),
                "a plain .red folder inside the solution stays writable", Path.Combine(inSub, "x.inc"), PathAccess.Write);

            // 2. Automatic project / .red roots may not sit INSIDE %APPDATA% / %LOCALAPPDATA% / %PROGRAMDATA%.
            string fakeRoaming = Mk(me, "AppData", "Roaming");
            string fakeLocal = Mk(me, "AppData", "Local");
            string protect = Mk(fakeRoaming, "Microsoft", "Protect");
            string chrome = Mk(fakeLocal, "Google", "Chrome", "User Data");
            Touch(Path.Combine(protect, "masterkey"));
            var confined = McpPathPolicy.Build(new PathPolicyInputs
            {
                SolutionDir = sol, UserProfile = me,
                ProjectDirs = new List<string> { protect },
                RedPaths = new List<string> { chrome },
                ConfinedDirs = new List<string> { fakeRoaming, fakeLocal }
            });
            Denied(confined, ".sln project inside %APPDATA% (Microsoft\\Protect) is not a root", Path.Combine(protect, "masterkey"));
            Denied(confined, ".red entry inside %LOCALAPPDATA% (Chrome User Data) is not a root", Path.Combine(chrome, "Default", "Cookies"));
            Ok("...both reported as skipped", ContainsLike(confined.SkippedRoots, protect) && ContainsLike(confined.SkippedRoots, "User Data"));
            Allowed(McpPathPolicy.Build(new PathPolicyInputs { UserProfile = me, ConfinedDirs = new List<string> { fakeRoaming }, ExtraAllowedRoots = protect }),
                "...but Mcp.AllowedRoots may name a folder there", Path.Combine(protect, "masterkey"));
            string jConfined = Path.Combine(root, "JProtect");
            if (MakeJunction(jConfined, protect))
            {
                var viaJc = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, UserProfile = me, RedPaths = new List<string> { jConfined }, ConfinedDirs = new List<string> { fakeRoaming } });
                Ok("...and a junction into %APPDATA% is caught by its resolved form", ContainsLike(viaJc.SkippedRoots, jConfined));
                try { Directory.Delete(jConfined); } catch { }
            }
            else Ok("could create the confined junction", false, "mklink /J failed");

            // 3. Files that make git / an MCP client / an editor run commands: readable, never written.
            foreach (string rel in new[] { ".git\\config", ".git\\hooks\\pre-commit", ".mcp.json", ".vscode\\tasks.json", ".vscode\\settings.json",
                                           ".vscode\\launch.json", ".gitattributes", ".gitmodules", "App.code-workspace", "sub\\.GIT\\config" })
            {
                var w = p.Check(Path.Combine(sol, rel), PathAccess.Write);
                Ok(rel + " write refused", !w.Allowed && w.Hint != null && w.Hint.Contains("run commands"), w.Allowed ? "was allowed" : w.Hint);
                Allowed(p, rel + " readable", Path.Combine(sol, rel));
            }
            Allowed(p, ".gitignore stays writable", Path.Combine(sol, ".gitignore"), PathAccess.Write);
            Allowed(p, ".vscode\\extensions.json stays writable", Path.Combine(sol, ".vscode", "extensions.json"), PathAccess.Write);
            Allowed(p, "a settings.json outside .vscode stays writable", Path.Combine(sol, "cfg", "settings.json"), PathAccess.Write);

            // 4. Every refusal names the sanctioned ways round it, and forbids the rest.
            foreach (var d in new[] { p.Check(Path.Combine(outside, "secret.txt"), PathAccess.Read),
                                      p.Check(Path.Combine(clarion, "x.tpl"), PathAccess.Write),
                                      p.Check(Path.Combine(sol, ".ssh", "id_rsa"), PathAccess.Read),
                                      p.Check(Path.Combine(sol, "x.sln"), PathAccess.Write) })
            {
                string text = d.Refusal("write_file", "x");
                Ok("refusal names Claude Code's Edit/Write tools and forbids run_command: " + d.Reason,
                    text.Contains("Claude Code's own Edit/Write tools") && text.Contains("Do not use run_command"), text);
            }

            // 5. A solution at the root of a SUBST drive is a folder, not a whole drive.
            char free = FreeDriveLetter();
            if (free != '\0' && Subst(free + ":", sol))
            {
                try
                {
                    var substSol = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = free + ":\\", UserProfile = me });
                    Allowed(substSol, "solution at the root of a SUBST drive is a root", free + ":\\Main.clw", PathAccess.Write);
                    Ok("...and is not reported as a whole drive", substSol.SolutionNote == null, substSol.SolutionNote);
                }
                finally { Subst(free + ":", null); }
            }
            else Ok("could create a SUBST drive", false, "no free letter or subst failed");
            var realDrive = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = Path.GetPathRoot(root), UserProfile = me });
            Ok("a solution at a REAL drive root is still refused", realDrive.Roots.Count == 0 && realDrive.SolutionNote != null, realDrive.SolutionNote);

            // 6. Every file the .red parser loaded is a boundary file, whatever its extension ({include}).
            string inc = Touch(Path.Combine(sol, "redirect-extra.inc"));
            var withInc = McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, UserProfile = me, RedFiles = new List<string> { Path.Combine(sol, "Clarion120.red"), inc } });
            var incW = withInc.Check(inc, PathAccess.Write);
            Ok(".red {include} with a .inc name: write refused as a boundary file", !incW.Allowed && incW.Hint != null && incW.Hint.Contains("define which folders"), incW.Hint);
            Allowed(withInc, "...still readable", inc);
            Allowed(withInc, "...another .inc in the solution is writable", Path.Combine(sol, "other.inc"), PathAccess.Write);

            Console.WriteLine("=== security gate run 3 ===");
            // 1. A planted BARE repo has no .git segment, and git honours its config (core.fsmonitor) inside it.
            string bareRepo = Mk(sol, "vendor", "lib");
            Touch(Path.Combine(bareRepo, "HEAD"));
            Mk(bareRepo, "objects"); Mk(bareRepo, "refs");
            foreach (string rel in new[] { "config", "HEAD", "packed-refs", "FETCH_HEAD", "ORIG_HEAD", "hooks\\pre-commit", "hooks\\fsmonitor-watchman" })
            {
                var w = p.Check(Path.Combine(bareRepo, rel), PathAccess.Write);
                Ok("bare repo: " + rel + " write refused", !w.Allowed && w.Hint != null && w.Hint.Contains("run commands"), w.Allowed ? "was allowed" : w.Hint);
            }
            Allowed(p, "bare repo: config still readable", Path.Combine(bareRepo, "config"));
            string halfRepo = Mk(sol, "half");
            Mk(halfRepo, "objects"); Mk(halfRepo, "refs");
            Denied(p, "objects\\ + refs\\ but no HEAD yet: creating HEAD refused", Path.Combine(halfRepo, "HEAD"), PathAccess.Write);
            string headOnly = Mk(sol, "headonly");
            Touch(Path.Combine(headOnly, "HEAD"));
            Denied(p, "HEAD file only: creating objects\\ beside it refused", Path.Combine(headOnly, "objects", "ab", "cdef"), PathAccess.Write);
            Denied(p, "HEAD file only: creating refs\\ beside it refused", Path.Combine(headOnly, "refs", "heads", "main"), PathAccess.Write);
            Denied(p, "HEAD file only: config beside it refused", Path.Combine(headOnly, "config"), PathAccess.Write);
            Allowed(p, "a 'config' in an ordinary folder stays writable", Path.Combine(sol, "settings", "config"), PathAccess.Write);
            Allowed(p, "a 'hooks' folder outside a git dir stays writable", Path.Combine(sol, "docs", "hooks", "notes.txt"), PathAccess.Write);
            Allowed(p, "a 'refs' folder with no HEAD beside it stays writable", Path.Combine(sol, "manual", "refs", "a.txt"), PathAccess.Write);
            Allowed(p, "a file named HEAD in an ordinary folder stays writable", Path.Combine(sol, "notes", "HEAD"), PathAccess.Write);

            // 2. Instructions later sessions follow: readable, never written by the MCP tools.
            foreach (string rel in new[] { "CLAUDE.md", "CLAUDE.local.md", "AGENTS.md", "sub\\CLAUDE.md", "claude.md" })
            {
                var w = p.Check(Path.Combine(sol, rel), PathAccess.Write);
                string text = w.Refusal("write_file", "x");
                Ok(rel + " write refused, with the standard Edit/Write wording",
                    !w.Allowed && w.Hint != null && w.Hint.Contains("instructions") && text.Contains("Claude Code's own Edit/Write tools"), w.Allowed ? "was allowed" : w.Hint);
                Allowed(p, rel + " readable", Path.Combine(sol, rel));
            }
            Allowed(p, "README.md stays writable", Path.Combine(sol, "README.md"), PathAccess.Write);
            Allowed(p, "a name that only contains CLAUDE stays writable", Path.Combine(sol, "CLAUDE-notes.md"), PathAccess.Write);

            Console.WriteLine("=== policy cache ===");
            var cache = new PolicyCache();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Func<McpPathPolicy> mk = () => McpPathPolicy.Build(new PathPolicyInputs { SolutionDir = sol, UserProfile = me });
            var c1 = cache.Get("sig-A", mk, t0);
            var c2 = cache.Get("sig-A", mk, t0.AddSeconds(1));
            Ok("same signature: the built policy is reused", ReferenceEquals(c1, c2) && cache.Builds == 1, "builds=" + cache.Builds);
            var c3 = cache.Get("sig-B", mk, t0.AddSeconds(2));
            Ok("changed signature: rebuilt", !ReferenceEquals(c2, c3) && cache.Builds == 2, "builds=" + cache.Builds);
            var c4 = cache.Get("sig-B", mk, t0.AddSeconds(2) + cache.MaxAge);
            Ok("older than MaxAge: rebuilt even with the same signature", !ReferenceEquals(c3, c4) && cache.Builds == 3, "builds=" + cache.Builds);
            cache.Get(null, mk, t0.AddSeconds(40)); cache.Get(null, mk, t0.AddSeconds(40));
            Ok("no signature (an input unreadable): never cached", cache.Builds == 5, "builds=" + cache.Builds);
            bool threw = false;
            try { cache.Get("sig-C", () => { throw new IOException("boom"); }, t0.AddSeconds(41)); } catch (IOException) { threw = true; }
            var c6 = cache.Get("sig-C", mk, t0.AddSeconds(41));
            Ok("a build that throws caches nothing (fail closed)", threw && c6 != null && cache.Builds == 6, "builds=" + cache.Builds);
            string redFile = Touch(Path.Combine(sol, "Clarion120.red"));
            string s1 = PolicyCache.Signature(new List<string> { "sln", PolicyCache.FileStamp(redFile) });
            File.AppendAllText(redFile, "\r\n*.inc = C:\\Elsewhere");
            File.SetLastWriteTimeUtc(redFile, DateTime.UtcNow.AddMinutes(1));
            string s2 = PolicyCache.Signature(new List<string> { "sln", PolicyCache.FileStamp(redFile) });
            Ok("editing a .red changes the signature", s1 != null && s2 != null && s1 != s2);
            Ok("a null part makes the whole signature null", PolicyCache.Signature(new List<string> { "a", null }) == null);
            Ok("signature parts cannot run together ('ab'+'c' vs 'a'+'bc')",
                PolicyCache.Signature(new List<string> { "ab", "c" }) != PolicyCache.Signature(new List<string> { "a", "bc" }));

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

    static char FreeDriveLetter()
    {
        for (char c = 'Z'; c >= 'M'; c--)
            if (!Directory.Exists(c + ":\\") && Array.IndexOf(Environment.GetLogicalDrives(), c + ":\\") < 0) return c;
        return '\0';
    }

    /// <summary>subst X: target, or subst X: /d when target is null.</summary>
    static bool Subst(string drive, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("subst.exe", target == null ? drive + " /d" : drive + " \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var pr = Process.Start(psi))
            {
                pr.StandardOutput.ReadToEnd(); pr.StandardError.ReadToEnd();
                pr.WaitForExit(15000);
                return pr.ExitCode == 0 && (target == null || Directory.Exists(drive + "\\"));
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
