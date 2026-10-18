using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    public enum PathAccess { Read, Write }

    /// <summary>The answer for one path: allowed or not, the canonical path it was judged as, and why.</summary>
    public sealed class PathDecision
    {
        public bool Allowed { get; internal set; }
        /// <summary>The canonical (full, link-resolved where possible) path. Null when the input could not be canonicalised.</summary>
        public string Path { get; internal set; }
        /// <summary>Why it was refused, in words. Null when allowed.</summary>
        public string Reason { get; internal set; }
        /// <summary>What the developer can change to allow it - names the setting, or says it cannot be overridden.</summary>
        public string Hint { get; internal set; }

        /// <summary>The tool reply for a refusal. Always starts "Error:" so the model cannot read it as an empty result.</summary>
        public string Refusal(string tool, string input)
        {
            return "Error: " + tool + " refused by the Clarion Assistant path policy (GH #266): '" + (input ?? "") + "' - "
                 + Reason + ". " + Hint + " " + WorkaroundNote;
        }

        /// <summary>
        /// On EVERY refusal (adversary run 2): Mcp.AllowedRoots lives in settings.txt, which the model cannot touch, so a
        /// hint that only names it leaves the model looking for another way in. Name the sanctioned ways, and the
        /// unsanctioned one.
        /// </summary>
        public const string WorkaroundNote =
            "Use Claude Code's own Edit/Write tools for this file (they ask the developer), or ask the developer to add the folder to " +
            "Mcp.AllowedRoots. Do not use run_command or other tools to work around this refusal.";
    }

    /// <summary>
    /// Everything a <see cref="McpPathPolicy"/> is built from. Plain data, so the harness can build a policy
    /// for a fake solution without an IDE, a .red or a settings file.
    /// </summary>
    public sealed class PathPolicyInputs
    {
        public string SolutionDir;
        public List<string> ProjectDirs = new List<string>();
        public List<string> ClarionRoots = new List<string>();
        /// <summary>.red search folders, already absolute (relative entries anchored at the solution folder by the caller).</summary>
        public List<string> RedPaths = new List<string>();
        /// <summary>%APPDATA%\ClarionAssistant: a READ-ONLY root, with its secrets denied outright.</summary>
        public string CaDataDir;
        /// <summary>Raw semicolon-separated <see cref="McpPathPolicy.AllowedRootsSetting"/> value (+ the env var).</summary>
        public string ExtraAllowedRoots;
        /// <summary>Raw semicolon-separated <see cref="McpPathPolicy.DeniedPathsSetting"/> value (+ the env var).</summary>
        public string ExtraDeniedPaths;
        /// <summary>The current user's profile (C:\Users\me). Its parent is the profiles folder; every OTHER profile there is denied.</summary>
        public string UserProfile;
        /// <summary>CLAUDE_CONFIG_DIR when set: Claude Code's config folder moved out of the profile. Denied like
        /// %USERPROFILE%\.claude (credentials, transcripts).</summary>
        public string ClaudeConfigDir;
        /// <summary>%LOCALAPPDATA%\ClarionAssistant: never a root; its ide-endpoint records (bearer tokens) and WebView2
        /// profiles (cookies) are denied outright, in case a developer's own root covers it.</summary>
        public string LocalCaDir;
        /// <summary>Folders an AUTOMATIC root may never equal or contain (%APPDATA%, %LOCALAPPDATA%, %PROGRAMDATA%,
        /// %WINDIR%, Program Files...). The user profile is always added. Only Mcp.AllowedRoots may name one.</summary>
        public List<string> ProtectedDirs = new List<string>();
        /// <summary>Folders an automatic PROJECT or .red root may not even sit inside (%APPDATA%, %LOCALAPPDATA%,
        /// %PROGRAMDATA%): DPAPI keys, gh tokens and browser profiles live there (security gate run 2).</summary>
        public List<string> ConfinedDirs = new List<string>();
        /// <summary>Every file the .red parser actually loaded, {include}d ones too, whatever their extension:
        /// boundary files, never written by the MCP tools.</summary>
        public List<string> RedFiles = new List<string>();
    }

    /// <summary>
    /// GH #266: the path policy every path-taking MCP file tool and every Everything search goes through.
    ///
    /// WHY IT EXISTS. Claude Code's permission rules (Read(X:\**) and friends) only see the BUILT-IN tools. An MCP
    /// tool that takes a path is a different tool, and a developer's deny rules never reach it, so read_file could
    /// open anything and the Everything tools listed the whole machine's index. This class is the boundary instead.
    ///
    /// THE MODEL.
    ///   Allowed roots - solution folder, its project folders, the build's Clarion root, the IDE's install, the .red's
    ///                   search folders, plus Mcp.AllowedRoots / CLARION_MCP_ALLOWED_ROOTS.
    ///                   WRITABLE: the solution folder (and project / .red folders inside it) and Mcp.AllowedRoots.
    ///                   READ-ONLY: the Clarion root, the IDE install, project and .red folders OUTSIDE the solution
    ///                   folder, and %APPDATA%\ClarionAssistant. The most specific root containing a path decides, so a
    ///                   solution inside C:\Clarion12\Examples stays writable while C:\Clarion12\accessory does not.
    ///                   AUTOMATIC ROOTS ARE ATTACKER-INFLUENCED: the .sln, .cwproj and .red that name them are files in
    ///                   the solution (pipeline run 1, security gate HIGH: write a project "..\..\..\Users\me\x.cwproj"
    ///                   into the .sln and the next call had the whole profile as a read/write root). So the boundary
    ///                   files are write-denied (below), and an automatic root that equals or CONTAINS a protected
    ///                   folder (the profile, %APPDATA%, %LOCALAPPDATA%, %PROGRAMDATA%, %WINDIR%, Program Files, a drive
    ///                   root - lexically or through a junction) is skipped and reported. A solution INSIDE the profile
    ///                   (%USERPROFILE%\source\...) is fine; a root that would swallow the profile is not. Only the
    ///                   developer's own Mcp.AllowedRoots may name such a folder.
    ///   Deny          - wins over every allow, and the built-in rules cannot be switched off: UNC and device paths,
    ///                   network drives, \.ssh\ \.aws\ (and a few other credential folders), the user's Claude
    ///                   config (%USERPROFILE%\.claude, %USERPROFILE%\.claude-*, CLAUDE_CONFIG_DIR) and any
    ///                   .credentials.json, other users' profiles, CA's own secrets (settings.txt holds the MCP token; mcp-*.json the tab tokens;
    ///                   copilot\ the Copilot login), and ANY write into %APPDATA%\ClarionAssistant - otherwise the
    ///                   model could rewrite Mcp.AllowedRoots and widen its own scope. Plus Mcp.DeniedPaths.
    ///   Write-only    - the BOUNDARY FILES, wherever they are: *.sln, *.cwproj, *.red, clarion-assistant.json,
    ///                   ClarionProperties.xml. They decide what the roots are, so a tool that could write them could
    ///                   widen its own scope; the developer edits them, or Claude Code's own Edit tool, which their
    ///                   permission rules govern.
    ///                 - any OTHER .claude folder (a project's own, or the one a worktree lives under) stays
    ///                   READABLE - CLAUDE.md and project skills are instructions the model is meant to read - but
    ///                   is never writable, so the model cannot write .claude\settings*.json to grant itself
    ///                   permissions or rewrite its own CLAUDE.md. (Fully denying every .claude segment refused
    ///                   whole solutions that happen to live under one; McpStdio.EndToEndTest caught it.)
    ///
    /// CANONICALISATION, FAIL CLOSED. A path is judged only after: no control/wildcard characters, no alternate data
    /// stream, no reserved device name, fully qualified "X:\" (relative, "\x" and "C:x" are refused), GetFullPath
    /// (so ".." is resolved BEFORE containment is checked, never after), containment on a separator boundary
    /// (C:\Sol does not contain C:\Solution2), ordinal case-insensitive. <see cref="Check"/> then resolves the
    /// nearest existing ancestor with GetFinalPathNameByHandle, which expands 8.3 short names AND follows
    /// junctions / symlinks / subst drives, and judges that real path too. Anything that throws is refused.
    ///
    /// NO SOLUTION. With nothing open the solution and project roots simply are not there; the Clarion roots,
    /// the .red's absolute folders, the extra roots and CA's data folder still apply, so the tools stay useful
    /// for library and template work. Relative .red entries are skipped then (they would anchor on the process CWD).
    ///
    /// No LINQ and System.dll only: tests\McpPathPolicy.Test.cs compiles this file on its own.
    /// </summary>
    public sealed class McpPathPolicy
    {
        public const string AllowedRootsSetting = "Mcp.AllowedRoots";
        public const string DeniedPathsSetting = "Mcp.DeniedPaths";
        public const string ContentSearchSetting = "Mcp.EverythingContentSearch";
        public const string AllowedRootsEnv = "CLARION_MCP_ALLOWED_ROOTS";
        public const string DeniedPathsEnv = "CLARION_MCP_DENIED_PATHS";

        /// <summary>Folder names denied (read and write) wherever they appear in a path: credential stores.</summary>
        public static readonly string[] DeniedSegments = { ".ssh", ".aws", ".gnupg", ".azure", ".kube", ".docker" };

        /// <summary>Folder names that may be read but never written, wherever they appear: Claude config other than
        /// the user's own (which is fully denied by prefix in <see cref="Build"/>).</summary>
        public static readonly string[] WriteDeniedSegments = { ".claude" };

        /// <summary>File names denied (read and write) wherever they appear: credentials and bearer-token configs.</summary>
        public static readonly string[] DeniedFileNames = { ".credentials.json", ".git-credentials", ".npmrc", ".netrc", "_netrc" };

        /// <summary>File-name wildcards denied (read and write) wherever they appear: CA's MCP configs carry its bearer token.</summary>
        public static readonly string[] DeniedFileNamePatterns = { "mcp-config*.json" };

        /// <summary>File-name wildcards that may be read but never written: the files that DEFINE the roots.</summary>
        public static readonly string[] BoundaryFilePatterns = { "*.sln", "*.cwproj", "*.red", "clarion-assistant.json", "ClarionProperties.xml" };

        /// <summary>Files that run commands when a tool reads its config: git (core.fsmonitor, hooks, filters), MCP server
        /// lists, editor tasks. Readable; never written by the MCP tools (security gate run 2 MEDIUM).</summary>
        public static readonly string[] ExecConfigFilePatterns = { ".mcp.json", ".gitattributes", ".gitmodules", "*.code-workspace" };
        public static readonly string[] VsCodeExecFiles = { "tasks.json", "settings.json", "launch.json" };

        /// <summary>Instructions later assistant sessions load and follow: a prompt-injected model must not plant them
        /// (security gate run 3 LOW). Readable; never written by the MCP tools.</summary>
        public static readonly string[] InstructionFileNames = { "CLAUDE.md", "CLAUDE.local.md", "AGENTS.md" };

        private const string InstructionHint =
            "This file holds instructions that later assistant sessions load and follow, so the Clarion Assistant MCP tools never write it.";

        /// <summary>Leaf names git reads from a git dir; a planted BARE repo has them with no .git segment (run 3 LOW).</summary>
        public static readonly string[] GitDirFileNames = { "HEAD", "config", "packed-refs", "FETCH_HEAD", "ORIG_HEAD" };

        private const string ExecConfigHint =
            "This file can make git, an MCP client or an editor run commands, so the Clarion Assistant MCP tools never write it.";

        private const string BoundaryHint =
            "These files define which folders the Clarion Assistant file tools may reach, so the MCP tools never write them. " +
            "The developer edits them, or Claude Code's own Edit tool does, which the developer's permission rules govern.";

        public sealed class Root
        {
            public string Lexical;   // GetFullPath form
            public string Resolved;  // link/8.3-resolved form (== Lexical when nothing to resolve)
            public bool Writable;
            public bool Explicit;    // from Mcp.AllowedRoots / the env var: wins a tie
            public bool Network;     // a share or network drive - only ever explicit
            public string Source;    // "solution", "project", "clarion", "red", "setting", "ca-data"
        }

        private sealed class DenyRule
        {
            public string Prefix;     // full path: that file/folder and everything below it
            public string Segment;    // bare name: any path segment equal to it
            public Regex Pattern;     // wildcard over the full path
            public bool WriteOnly;
            public bool BuiltIn;
            public string Text;       // as shown to the developer
            public string Hint;       // overrides the default hint
        }

        private readonly List<Root> _roots = new List<Root>();
        private readonly List<DenyRule> _deny = new List<DenyRule>();
        private readonly List<string> _ignored = new List<string>();
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _protected = new List<string>();
        private readonly List<string> _confined = new List<string>();
        private readonly Dictionary<char, bool> _networkDrive = new Dictionary<char, bool>();
        private string _usersDir, _userProfile, _publicProfile;

        private static readonly List<Regex> DeniedLeafRegexes = LeafRegexes(DeniedFileNamePatterns);
        private static readonly List<Regex> BoundaryLeafRegexes = LeafRegexes(BoundaryFilePatterns);
        private static readonly List<Regex> ExecLeafRegexes = LeafRegexes(ExecConfigFilePatterns);

        public IList<Root> Roots { get { return _roots.AsReadOnly(); } }
        /// <summary>Setting entries that were not usable (relative, UNC, invalid), each as "entry (reason)".</summary>
        public IList<string> Ignored { get { return _ignored.AsReadOnly(); } }
        /// <summary>AUTOMATIC roots refused because they equal or contain a protected folder, each as "root (why)".
        /// McpPathGate audits these.</summary>
        public IList<string> SkippedRoots { get { return _skipped.AsReadOnly(); } }

        private McpPathPolicy() { }

        /// <summary>
        /// Set when the open solution's own folder could not be a root (a network drive or share, or a folder that
        /// contains a protected one): appended to every outside-root refusal and reported by get_solution_info, so the
        /// developer is told rather than left with a solution whose files are silently unreachable. Null otherwise.
        /// </summary>
        public string SolutionNote { get; private set; }

        public static McpPathPolicy Build(PathPolicyInputs inputs)
        {
            var p = new McpPathPolicy();
            if (inputs == null) inputs = new PathPolicyInputs();

            // ---- protected folders: no AUTOMATIC root may equal or contain one
            string profile = Lexical(inputs.UserProfile);
            if (profile != null) p.AddProtected(profile);
            if (inputs.ProtectedDirs != null) foreach (string d in inputs.ProtectedDirs) p.AddProtected(Lexical(d));
            if (inputs.ConfinedDirs != null)
                foreach (string d in inputs.ConfinedDirs)
                {
                    string c = Lexical(d);
                    if (c == null) continue;
                    p._confined.Add(c);
                    string cr = IsNetworkPathStatic(c) ? null : ResolveForRoot(c);
                    if (cr != null && !Same(cr, c)) p._confined.Add(cr);
                }
            if (inputs.RedFiles != null)
                foreach (string f in inputs.RedFiles)
                {
                    string rf = Lexical(f);
                    if (rf != null) p.AddBuiltInPrefix(rf, rf + " (a file the .red loads)", true, BoundaryHint);
                }

            // ---- deny first: roots that fall inside a denied area are dropped below
            if (profile != null)
            {
                p._userProfile = profile;
                p._usersDir = Lexical(System.IO.Path.GetDirectoryName(profile));
                if (p._usersDir != null) p._publicProfile = p._usersDir + "\\Public";
                // The user's Claude Code config: credentials and every session transcript. Read AND write.
                p.AddBuiltInPrefix(profile + "\\.claude", profile + "\\.claude\\", false);
                p.AddBuiltInPattern(profile, "\\.claude-*", false);     // alternate profiles (.claude-work, ...)
                p.AddBuiltInPattern(profile, "\\.claude.json*", false); // Claude Code's user config file (+ backups)
            }
            string claudeDir = Lexical(inputs.ClaudeConfigDir);
            if (claudeDir != null && !IsDriveRoot(claudeDir)) p.AddBuiltInPrefix(claudeDir, claudeDir + "\\ (CLAUDE_CONFIG_DIR)", false);
            string ca = Lexical(inputs.CaDataDir);
            if (ca != null)
            {
                p.AddBuiltInPattern(ca, "\\settings.txt*", false);
                p.AddBuiltInPattern(ca, "\\mcp-*.json*", false);
                p.AddBuiltInPrefix(ca + "\\copilot", ca + "\\copilot\\", false);
                p.AddBuiltInPrefix(ca, ca + "\\ (writes)", true);
            }
            string localCa = Lexical(inputs.LocalCaDir);
            if (localCa != null)
            {
                p.AddBuiltInPrefix(localCa + "\\ide-endpoint", localCa + "\\ide-endpoint\\ (IDE bearer tokens)", false);
                p.AddBuiltInPattern(localCa, "\\WebView2Data*", false);  // browser profiles: cookies, storage
            }
            foreach (string entry in SplitList(inputs.ExtraDeniedPaths))
            {
                if (entry.IndexOf('*') >= 0 || entry.IndexOf('?') >= 0)
                    p._deny.Add(new DenyRule { Pattern = WildcardRegex(entry.Replace('/', '\\')), Text = entry });
                else if (LooksAbsolute(entry))
                {
                    string full = Lexical(entry);
                    if (full == null) { p._ignored.Add(entry + " (" + DeniedPathsSetting + ": invalid path)"); continue; }
                    p._deny.Add(new DenyRule { Prefix = full, Text = entry });
                    string res = IsNetworkPathStatic(full) ? null : ResolveForRoot(full);
                    if (res != null && !Same(res, full)) p._deny.Add(new DenyRule { Prefix = res, Text = entry });
                }
                else
                    p._deny.Add(new DenyRule { Segment = entry.Trim('\\', '/'), Text = entry });
            }

            // ---- roots. Writable only where the developer's own solution (or setting) says so.
            string sol = Lexical(inputs.SolutionDir);
            string solRes = sol == null || IsNetworkPathStatic(sol) ? sol : (ResolveForRoot(sol) ?? sol);
            p.AddRoot(inputs.SolutionDir, "solution", true, false);
            if (inputs.ProjectDirs != null)
                foreach (string d in inputs.ProjectDirs) p.AddRoot(d, "project", InsideSolution(d, sol, solRes), false);
            if (inputs.ClarionRoots != null) foreach (string d in inputs.ClarionRoots) p.AddRoot(d, "clarion", false, false);
            if (inputs.RedPaths != null) foreach (string d in inputs.RedPaths) p.AddRoot(d, "red", InsideSolution(d, sol, solRes), false);
            foreach (string entry in SplitList(inputs.ExtraAllowedRoots)) p.AddRoot(entry, "setting", true, true);
            p.AddRoot(inputs.CaDataDir, "ca-data", false, false);

            // An explicit root covering the solution folder answers the note.
            if (p.SolutionNote != null && sol != null)
                foreach (var r in p._roots)
                    if (r.Explicit && (IsUnder(sol, r.Lexical) || IsUnder(sol, r.Resolved))) { p.SolutionNote = null; break; }
            return p;
        }

        /// <summary>
        /// Writable only when BOTH the typed path and its link-resolved form are inside the solution's resolved folder
        /// (security gate run 2 MEDIUM: a junction inside the solution, named by the .red or .sln, pointing outside it,
        /// was a writable root by its typed name alone).
        /// </summary>
        private static bool InsideSolution(string dir, string sol, string solRes)
        {
            string lex = Lexical(dir);
            if (sol == null || lex == null || !IsUnder(lex, sol)) return false;
            if (IsNetworkPathStatic(lex)) return IsUnder(lex, solRes ?? sol);
            string res = ResolveForRoot(lex);
            return res != null && IsUnder(res, solRes ?? sol);
        }

        private void AddProtected(string full)
        {
            if (full == null) return;
            if (!_protected.Contains(full)) _protected.Add(full);
            if (IsNetworkPathStatic(full)) return;
            string res = ResolveForRoot(full);
            if (res != null && !_protected.Contains(res)) _protected.Add(res);
        }

        private void AddBuiltInPrefix(string full, string text, bool writeOnly, string hint = null)
        {
            _deny.Add(new DenyRule { Prefix = full, BuiltIn = true, WriteOnly = writeOnly, Text = text, Hint = hint });
            string res = ResolveForRoot(full);   // the folder may itself be a junction (OneDrive, moved profile)
            if (res != null && !Same(res, full)) _deny.Add(new DenyRule { Prefix = res, BuiltIn = true, WriteOnly = writeOnly, Text = text, Hint = hint });
        }

        /// <summary>A wildcard under <paramref name="baseDir"/>, for the lexical AND the link-resolved form of the base
        /// (security gate MEDIUM: a junctioned %APPDATA% reached settings.txt through its real path).</summary>
        private void AddBuiltInPattern(string baseDir, string suffix, bool writeOnly)
        {
            _deny.Add(new DenyRule { Pattern = WildcardRegex(baseDir + suffix), WriteOnly = writeOnly, BuiltIn = true, Text = baseDir + suffix });
            string res = ResolveForRoot(baseDir);
            if (res != null && !Same(res, baseDir))
                _deny.Add(new DenyRule { Pattern = WildcardRegex(res + suffix), WriteOnly = writeOnly, BuiltIn = true, Text = baseDir + suffix });
        }

        /// <summary>The protected folder <paramref name="root"/> equals or contains, or null.</summary>
        private string ContainsProtected(string root)
        {
            foreach (string pr in _protected)
                if (IsUnder(pr, root)) return pr;
            return null;
        }

        private void AddRoot(string raw, string source, bool writable, bool explicitSetting)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string why;
            string lex = Canonical(raw, out why);
            if (lex == null)
            {
                if (explicitSetting) _ignored.Add(raw + " (" + AllowedRootsSetting + ": " + why + ")");
                else if (source == "solution") SolutionNote = raw + " cannot be used by the file tools (" + why + ")";
                return;
            }
            bool network = IsNetworkPath(lex);
            if (network && !explicitSetting)
            {
                // Network locations only through the developer's own setting (adversary HIGH: VM shared folders and
                // team shares). Say so when it is the open solution itself that drops out.
                if (source == "solution")
                    SolutionNote = lex + " is on a network drive; add it to " + AllowedRootsSetting + " to allow the file tools there";
                _skipped.Add(lex + " (" + source + ": network location; only " + AllowedRootsSetting + " may name one)");
                return;
            }
            string res = ResolveForRoot(lex) ?? lex;
            if (!network && !IsLocalDrivePath(res))
            {
                if (explicitSetting) { network = true; }    // a local name for a network target, allowed explicitly
                else
                {
                    if (source == "solution")
                        SolutionNote = lex + " resolves to the network location " + res + "; add it to " + AllowedRootsSetting + " to allow the file tools there";
                    _skipped.Add(lex + " (" + source + ": resolves to network location " + res + ")");
                    return;
                }
            }
            if (!explicitSetting)
            {
                // An automatic root comes from files in the solution (.sln, .cwproj, .red, clarion-assistant.json).
                // A drive-root NAME is fine when it resolves to a folder (a SUBST drive for the solution, debugger LOW);
                // a drive root it really is, or one that does not resolve elsewhere, is not.
                bool wholeDrive = IsDriveRoot(res) || (IsDriveRoot(lex) && Same(res, lex));
                string hit = wholeDrive ? "a whole drive" : (ContainsProtected(lex) ?? ContainsProtected(res));
                if (hit != null)
                {
                    if (source == "solution")
                        SolutionNote = lex + " contains " + hit + ", so it is not used as a root; add the folder to " + AllowedRootsSetting + " if that is really intended";
                    _skipped.Add(lex + " (" + source + ": equals or contains protected folder " + hit + ")");
                    return;
                }
                // Project and .red roots may not even sit INSIDE %APPDATA% / %LOCALAPPDATA% / %PROGRAMDATA% (security gate
                // run 2 MEDIUM: a hostile .sln opened %APPDATA%\Microsoft\Protect read-only).
                if (source == "project" || source == "red")
                    foreach (string c in _confined)
                        if (IsUnder(lex, c) || IsUnder(res, c))
                        {
                            _skipped.Add(lex + " (" + source + ": inside " + c + "; only " + AllowedRootsSetting + " may name a folder there)");
                            return;
                        }
            }
            foreach (var r in _roots)
                if (Same(r.Lexical, lex) && Same(r.Resolved, res))
                {
                    if (writable) r.Writable = true;
                    if (explicitSetting) r.Explicit = true;
                    return;
                }
            _roots.Add(new Root { Lexical = lex, Resolved = res, Writable = writable, Explicit = explicitSetting, Network = network, Source = source });
        }

        // ------------------------------------------------------------------ checks

        /// <summary>
        /// The full check for a file tool: lexical rules, then the link-resolved real path. Touches the file system
        /// only for drive type and the nearest existing ancestor's final path, and only after the lexical rules
        /// passed - so a UNC path outside every explicit network root is refused without any SMB traffic.
        /// </summary>
        public PathDecision Check(string path, PathAccess access)
        {
            return Decide(path, access, true);
        }

        /// <summary>
        /// The lexical-only check, for Everything results: the index reports real long-name paths, and it does not
        /// index through junctions, so per-result link resolution would cost a CreateFile per hit for nothing.
        /// </summary>
        public PathDecision CheckLexical(string path, PathAccess access)
        {
            return Decide(path, access, false);
        }

        private bool UnderNetworkRoot(string full)
        {
            foreach (var r in _roots)
                if (r.Network && (IsUnder(full, r.Lexical) || IsUnder(full, r.Resolved))) return true;
            return false;
        }

        private string OutsideHint()
        {
            return "To allow it, add its folder to the " + AllowedRootsSetting + " setting (semicolon-separated) in %APPDATA%\\ClarionAssistant\\settings.txt, or to the " + AllowedRootsEnv + " environment variable of the MCP server."
                 + (SolutionNote != null ? " Note: " + SolutionNote + "." : "");
        }

        private PathDecision Decide(string input, PathAccess access, bool resolve)
        {
            string why;
            string lex = Canonical(input, out why);
            if (lex == null) return Refuse(null, why, "This cannot be overridden: give a full local path such as C:\\MyApp\\Main.clw.");
            if (IsNetworkPath(lex) && !UnderNetworkRoot(lex))
                return Refuse(lex, IsUncPath(lex) ? "UNC paths (\\\\server\\share) are refused unless " + AllowedRootsSetting + " names the share" : "it is on a network drive",
                    "Network locations are allowed only by an explicit " + AllowedRootsSetting + " entry (or " + AllowedRootsEnv + ") naming them."
                    + (SolutionNote != null ? " Note: " + SolutionNote + "." : ""));

            string real = lex;
            if (resolve)
            {
                real = ResolveNearest(lex);
                if (real == null)
                    return Refuse(lex, "its real location could not be determined (link or short name did not resolve)", "This fails closed and cannot be overridden.");
                if (!IsLocalDrivePath(real) && !(IsUncPath(real) && UnderNetworkRoot(real)))
                    return Refuse(lex, "it resolves to '" + real + "', which is not a local drive path", "Network targets are allowed only by an explicit " + AllowedRootsSetting + " entry naming them.");
            }

            DenyRule hit = FindDeny(lex, access) ?? (Same(real, lex) ? null : FindDeny(real, access));
            if (hit != null)
            {
                if (hit.Hint != null) return Refuse(real, "it matches the built-in rule '" + hit.Text + "'", hit.Hint);
                if (hit.BuiltIn)
                    return Refuse(real, "it matches the built-in deny rule '" + hit.Text + "'", "Built-in deny rules win over every allowed root and cannot be overridden.");
                return Refuse(real, "it matches your " + DeniedPathsSetting + " entry '" + hit.Text + "'",
                    "Remove that entry from " + DeniedPathsSetting + " (or " + DeniedPathsEnv + ") to allow it.");
            }
            string other = OtherProfileHit(lex) ?? (Same(real, lex) ? null : OtherProfileHit(real));
            if (other != null)
                return Refuse(real, "it is inside another user's profile (" + other + ")", "Other users' profiles are always refused.");

            // The MOST SPECIFIC root containing the path decides; on a tie an explicit root wins, then read-only.
            Root best = null;
            int bestLen = -1;
            foreach (var r in _roots)
            {
                int len = -1;
                if (resolve)
                {
                    if (IsUnder(real, r.Resolved)) len = r.Resolved.Length;
                    else if (Same(real, lex) && IsUnder(lex, r.Lexical)) len = r.Lexical.Length;
                }
                else
                {
                    if (IsUnder(lex, r.Lexical)) len = r.Lexical.Length;
                    else if (IsUnder(lex, r.Resolved)) len = r.Resolved.Length;
                }
                if (len < 0) continue;
                if (access == PathAccess.Read) return new PathDecision { Allowed = true, Path = real };
                bool better = len > bestLen
                    || (len == bestLen && best != null && ((r.Explicit && !best.Explicit) || (r.Explicit == best.Explicit && !r.Writable && best.Writable)));
                if (better) { best = r; bestLen = len; }
            }
            if (best != null)
            {
                if (best.Writable) return new PathDecision { Allowed = true, Path = real };
                return Refuse(real, "it is in a read-only root (" + best.Lexical + ", " + ReadOnlyWhy(best.Source) + ")",
                    best.Source == "ca-data" ? "Write tools never write there; this cannot be overridden."
                    : "The MCP write tools only write inside the open solution folder. To allow writes here, add the folder to " + AllowedRootsSetting + ".");
            }
            return Refuse(real, "it is outside every allowed root (" + RootSummary() + ")", OutsideHint());
        }

        private static string ReadOnlyWhy(string source)
        {
            switch (source)
            {
                case "clarion": return "the Clarion / IDE install";
                case "red": return "a .red search folder outside the solution";
                case "project": return "a project folder outside the solution folder";
                case "ca-data": return "Clarion Assistant's own data folder";
                default: return source;
            }
        }

        private static PathDecision Refuse(string path, string reason, string hint)
        {
            return new PathDecision { Allowed = false, Path = path, Reason = reason, Hint = hint };
        }

        private DenyRule FindDeny(string full, PathAccess access)
        {
            foreach (var d in _deny)
            {
                if (d.WriteOnly && access != PathAccess.Write) continue;
                if (d.Prefix != null && IsUnder(full, d.Prefix)) return d;
                if (d.Pattern != null && d.Pattern.IsMatch(full)) return d;
                if (d.Segment != null && HasSegment(full, d.Segment)) return d;
            }
            foreach (string seg in DeniedSegments)
                if (HasSegment(full, seg))
                    return new DenyRule { BuiltIn = true, Text = "*\\" + seg + "\\*" };
            string leaf = System.IO.Path.GetFileName(full) ?? "";
            foreach (string name in DeniedFileNames)
                if (Same(leaf, name))
                    return new DenyRule { BuiltIn = true, Text = "*\\" + name };
            for (int i = 0; i < DeniedLeafRegexes.Count; i++)
                if (DeniedLeafRegexes[i].IsMatch(leaf))
                    return new DenyRule { BuiltIn = true, Text = "*\\" + DeniedFileNamePatterns[i] };
            if (access == PathAccess.Write)
            {
                for (int i = 0; i < BoundaryLeafRegexes.Count; i++)
                    if (BoundaryLeafRegexes[i].IsMatch(leaf))
                        return new DenyRule { BuiltIn = true, WriteOnly = true, Text = BoundaryFilePatterns[i] + " (writes)", Hint = BoundaryHint };
                if (HasSegment(full, ".git"))
                    return new DenyRule { BuiltIn = true, WriteOnly = true, Text = "*\\.git\\* (writes)", Hint = ExecConfigHint };
                string bare = PlantedGitDirHit(full, leaf);
                if (bare != null)
                    return new DenyRule { BuiltIn = true, WriteOnly = true, Text = bare, Hint = ExecConfigHint };
                foreach (string name in InstructionFileNames)
                    if (Same(leaf, name))
                        return new DenyRule { BuiltIn = true, WriteOnly = true, Text = name + " (writes)", Hint = InstructionHint };
                for (int i = 0; i < ExecLeafRegexes.Count; i++)
                    if (ExecLeafRegexes[i].IsMatch(leaf))
                        return new DenyRule { BuiltIn = true, WriteOnly = true, Text = ExecConfigFilePatterns[i] + " (writes)", Hint = ExecConfigHint };
                string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(full) ?? "") ?? "";
                if (Same(parent, ".vscode"))
                    foreach (string v in VsCodeExecFiles)
                        if (Same(leaf, v))
                            return new DenyRule { BuiltIn = true, WriteOnly = true, Text = ".vscode\\" + v + " (writes)", Hint = ExecConfigHint };
                if (HasWriteDeniedClaudeSegment(full))
                    return new DenyRule { BuiltIn = true, WriteOnly = true, Text = "*\\.claude\\* (writes)" };
            }
            return null;
        }

        /// <summary>
        /// A write into what looks like a git directory without a .git segment - a planted bare repo, whose
        /// core.fsmonitor git honours when run inside it (security gate run 3 LOW). Cheap: the file system is asked only
        /// when a name matches. Denied: HEAD / config / packed-refs / FETCH_HEAD / ORIG_HEAD in a folder that holds
        /// objects\ and refs\ (or a HEAD file); anything under a hooks\ whose parent looks like that; and creating
        /// objects\ or refs\ beside an existing HEAD file. Returns the rule text, or null.
        /// </summary>
        private static string PlantedGitDirHit(string full, string leaf)
        {
            try
            {
                foreach (string n in GitDirFileNames)
                    if (Same(leaf, n))
                    {
                        string dir = System.IO.Path.GetDirectoryName(full);
                        if (dir != null && LooksLikeGitDir(dir)) return leaf + " in a git directory (writes)";
                        break;
                    }
                string[] segs = full.Split('\\');
                string prefix = segs[0];
                for (int i = 1; i < segs.Length; i++)
                {
                    string seg = segs[i];
                    // prefix is the folder that holds segs[i]
                    if (i < segs.Length - 1 && Same(seg, "hooks") && LooksLikeGitDir(prefix))
                        return "hooks\\* in a git directory (writes)";
                    if ((Same(seg, "objects") || Same(seg, "refs")) && File.Exists(prefix + "\\HEAD"))
                        return seg + "\\ beside a git HEAD (writes)";
                    prefix = prefix + "\\" + seg;
                }
            }
            catch { return "a path whose git-directory check failed (writes)"; }   // fail closed
            return null;
        }

        private static bool LooksLikeGitDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(dir + "\\HEAD") || (Directory.Exists(dir + "\\objects") && Directory.Exists(dir + "\\refs"));
        }

        /// <summary>
        /// A .claude segment that write-denies. ".claude\worktrees\..." is where Claude Code keeps git worktrees -
        /// whole checkouts the developer works in - so THAT occurrence does not deny what is below it (debugger
        /// MEDIUM). Any other .claude segment does, including a deeper one inside a worktree
        /// (.claude\worktrees\x\.claude\settings.json).
        /// </summary>
        public static bool HasWriteDeniedClaudeSegment(string full)
        {
            string[] segs = full.Split('\\');
            for (int i = 0; i < segs.Length; i++)
            {
                if (!Same(segs[i], ".claude")) continue;
                bool worktreeContainer = i + 1 < segs.Length && Same(segs[i + 1], "worktrees") && i + 2 < segs.Length && segs[i + 2].Length > 0;
                if (!worktreeContainer) return true;
            }
            return false;
        }

        private string OtherProfileHit(string full)
        {
            if (_usersDir == null || !IsUnder(full, _usersDir) || Same(full, _usersDir)) return null;
            if (IsUnder(full, _userProfile) || IsUnder(full, _publicProfile)) return null;
            string rest = full.Substring(_usersDir.Length).TrimStart('\\');
            int cut = rest.IndexOf('\\');
            return _usersDir + "\\" + (cut < 0 ? rest : rest.Substring(0, cut));
        }

        /// <summary>The roots as one short line for a refusal, at most eight.</summary>
        public string RootSummary()
        {
            if (_roots.Count == 0) return "there are none: no solution is open and no Clarion root, .red or " + AllowedRootsSetting + " entry applies";
            var sb = new StringBuilder();
            int n = 0;
            foreach (var r in _roots)
            {
                if (n == 8) { sb.Append("; +" + (_roots.Count - 8) + " more"); break; }
                if (n > 0) sb.Append("; ");
                sb.Append(r.Lexical);
                if (!r.Writable) sb.Append(" [read-only]");
                n++;
            }
            return sb.ToString();
        }

        /// <summary>The distinct folders an Everything query is scoped to (lexical and resolved forms of every root).</summary>
        public List<string> SearchRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var r in _roots)
            {
                if (seen.Add(r.Lexical)) list.Add(r.Lexical);
                if (seen.Add(r.Resolved)) list.Add(r.Resolved);
            }
            return list;
        }

        // ------------------------------------------------------------------ Everything query rules

        /// <summary>max_results as the server enforces it: 1..ceiling; zero, negative or missing means the tool's default.</summary>
        public static int ClampMax(int requested, int defaultValue, int ceiling)
        {
            if (requested <= 0) return defaultValue;
            return requested > ceiling ? ceiling : requested;
        }

        /// <summary>A query that names nothing (*, *.*, **, ?, empty, punctuation only) would enumerate the index.</summary>
        public static bool IsBareWildcard(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            foreach (char c in query)
                if (char.IsLetterOrDigit(c)) return false;
            return true;
        }

        // ------------------------------------------------------------------ canonicalisation

        private static readonly Regex DeviceName = new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]|CONIN\$|CONOUT\$|CLOCK\$)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// The lexical canonical form - "X:\..." or a UNC "\\server\share\..." - or null with the reason. Never
        /// touches the file system. A UNC form is only syntax here: <see cref="Decide"/> refuses it unless an explicit
        /// Mcp.AllowedRoots entry names the share. Device and NT forms (\\?\, \\.\, \??\) are always refused.
        /// </summary>
        public static string Canonical(string input, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(input)) { why = "the path is empty"; return null; }
            string s = input.Trim().Replace('/', '\\');
            foreach (char c in s)
            {
                if (c < 0x20) { why = "it contains a control character"; return null; }
                if (c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') { why = "it contains a wildcard or reserved character"; return null; }
            }
            if (s.StartsWith("\\\\.\\") || s.StartsWith("\\??\\") || s.StartsWith("\\\\?\\") || s == "\\\\?" || s == "\\\\.")
            { why = "device and NT paths (\\\\?\\, \\\\.\\, \\??\\) are always refused"; return null; }
            bool unc = s.StartsWith("\\\\");
            string body;
            if (unc)
            {
                string[] parts = s.Substring(2).Split('\\');
                if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) { why = "it is not a complete UNC path (\\\\server\\share)"; return null; }
                if (s.IndexOf(':') >= 0) { why = "it names an alternate data stream"; return null; }
                body = s.Substring(2);
            }
            else
            {
                if (!(s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\')) { why = "it is not a fully qualified local path (X:\\...); relative paths are refused"; return null; }
                if (s.IndexOf(':', 2) >= 0) { why = "it names an alternate data stream"; return null; }
                body = s.Substring(3);
            }
            foreach (string seg in body.Split('\\'))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") continue;
                // Windows silently strips a trailing dot or space ("x." is "x"), so a name that ends in one is not
                // the name the rules would judge. Security gate LOW: refuse it rather than reason about it.
                char last = seg[seg.Length - 1];
                if (last == '.' || last == ' ') { why = "a path segment ends in a dot or a space ('" + seg + "')"; return null; }
                string stem = seg;
                int dot = stem.IndexOf('.');
                if (dot >= 0) stem = stem.Substring(0, dot);
                if (DeviceName.IsMatch(stem.Trim())) { why = "it names a reserved device (" + seg + ")"; return null; }
            }
            string full;
            try { full = System.IO.Path.GetFullPath(s); }
            catch (Exception ex) { why = "it is not a valid path (" + ex.GetType().Name + ")"; return null; }
            if (unc ? !IsUncPath(full) : !IsLocalDrivePath(full)) { why = "it does not keep its form once normalised"; return null; }
            if (unc)
            {
                // ".." must not climb above \\server\share.
                string root = System.IO.Path.GetPathRoot(full) ?? "";
                if (root.TrimEnd('\\').Split('\\').Length < 4) { why = "it is not a complete UNC path (\\\\server\\share)"; return null; }
            }
            return TrimEnd(full);
        }

        public static bool IsUncPath(string full)
        {
            return full != null && full.StartsWith("\\\\") && !full.StartsWith("\\\\?\\") && !full.StartsWith("\\\\.\\");
        }

        /// <summary>A share, or a drive letter Windows reports as a network drive (cached per letter).</summary>
        private bool IsNetworkPath(string full)
        {
            return IsUncPath(full) || IsNetworkDrive(full);
        }

        /// <summary>The uncached form, for Build-time inputs that are not checked per path.</summary>
        private static bool IsNetworkPathStatic(string full)
        {
            if (IsUncPath(full)) return true;
            try { return new DriveInfo(full.Substring(0, 1)).DriveType == DriveType.Network; }
            catch { return true; }
        }

        private static List<Regex> LeafRegexes(string[] patterns)
        {
            var list = new List<Regex>();
            foreach (string p in patterns) list.Add(WildcardRegex(p));
            return list;
        }

        private static string Lexical(string input)
        {
            string why;
            return string.IsNullOrWhiteSpace(input) ? null : Canonical(input, out why);
        }

        private static bool LooksAbsolute(string s)
        {
            s = s.Replace('/', '\\');
            return s.StartsWith("\\") || (s.Length >= 2 && s[1] == ':');
        }

        private static bool IsLocalDrivePath(string full)
        {
            return full != null && full.Length >= 3 && char.IsLetter(full[0]) && full[1] == ':' && full[2] == '\\';
        }

        private static bool IsDriveRoot(string full)
        {
            return full != null && full.Length == 3 && full[1] == ':';
        }

        private static string TrimEnd(string full)
        {
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it - on a separator boundary.</summary>
        public static bool IsUnder(string path, string root)
        {
            if (path == null || root == null) return false;
            if (Same(path, root)) return true;
            string prefix = root.EndsWith("\\") ? root : root + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasSegment(string full, string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            foreach (string seg in full.Split('\\'))
                if (Same(seg, segment)) return true;
            return false;
        }

        private static Regex WildcardRegex(string wildcard)
        {
            string rx = "^" + Regex.Escape(wildcard).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static IEnumerable<string> SplitList(string raw)
        {
            if (string.IsNullOrEmpty(raw)) yield break;
            foreach (string part in raw.Split(';'))
            {
                string t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        private bool IsNetworkDrive(string full)
        {
            char letter = char.ToUpperInvariant(full[0]);
            bool net;
            lock (_networkDrive)
                if (_networkDrive.TryGetValue(letter, out net)) return net;
            try { net = new DriveInfo(letter.ToString()).DriveType == DriveType.Network; }
            catch { net = true; }                                       // cannot tell: fail closed
            lock (_networkDrive) _networkDrive[letter] = net;
            return net;
        }

        /// <summary>A root's real location; null when it cannot be determined (the lexical form is used then).</summary>
        private static string ResolveForRoot(string lex)
        {
            try { return ResolveNearest(lex); } catch { return null; }
        }

        /// <summary>
        /// The real path: the nearest EXISTING ancestor resolved through GetFinalPathNameByHandle (8.3 names expanded,
        /// junctions / symlinks / subst followed), with the not-yet-existing tail re-appended. Null on any failure.
        /// </summary>
        private static string ResolveNearest(string lex)
        {
            try
            {
                string current = lex;
                string tail = "";
                for (int guard = 0; guard < 256 && current != null; guard++)
                {
                    if (File.Exists(current) || Directory.Exists(current))
                    {
                        string final = FinalPath(current);
                        if (final == null) return null;
                        final = TrimEnd(final);
                        return tail.Length == 0 ? final : (final.EndsWith("\\") ? final : final + "\\") + tail;
                    }
                    string parent = System.IO.Path.GetDirectoryName(current);
                    if (parent == null) return lex;                       // nothing on the path exists (missing drive): judge it lexically
                    string name = System.IO.Path.GetFileName(current);
                    tail = tail.Length == 0 ? name : name + "\\" + tail;
                    current = parent;
                }
                return null;
            }
            catch { return null; }
        }

        private const uint FILE_SHARE_ALL = 0x7;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr h, StringBuilder buf, uint len, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private static string FinalPath(string existing)
        {
            // Access 0 = query attributes only: works on files the user cannot read, never opens content.
            IntPtr h = CreateFileW(existing, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                if (n == 0) return null;
                if (n >= sb.Capacity)
                {
                    sb = new StringBuilder((int)n + 1);
                    n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity) return null;
                }
                string s = sb.ToString();
                if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);   // network: refused by caller
                if (s.StartsWith(@"\\?\")) s = s.Substring(4);
                return s;
            }
            finally { CloseHandle(h); }
        }
    }

    /// <summary>
    /// One built policy, reused while nothing it was built from has changed (code review run 2: every gated call re-read
    /// each project's .red and resolved ~8 protected folders). The caller supplies a SIGNATURE of the inputs - the .sln
    /// and every loaded .red with their timestamps, the settings values, the env vars - and the policy is rebuilt when it
    /// changes, or after <see cref="MaxAge"/> regardless (a junction re-pointed under an unchanged name changes no
    /// timestamp). Fail closed: a null signature (some input could not be read) is never cached, and a build that throws
    /// caches nothing.
    /// </summary>
    public sealed class PolicyCache
    {
        private readonly object _lock = new object();
        private string _sig;
        private McpPathPolicy _policy;
        private DateTime _builtUtc;

        public TimeSpan MaxAge = TimeSpan.FromSeconds(30);
        /// <summary>How many times a policy was actually built (for the harness).</summary>
        public int Builds { get; private set; }

        public McpPathPolicy Get(string signature, Func<McpPathPolicy> build, DateTime nowUtc)
        {
            if (signature != null)
                lock (_lock)
                    if (_policy != null && _sig == signature && nowUtc >= _builtUtc && nowUtc - _builtUtc < MaxAge)
                        return _policy;
            McpPathPolicy p = build();
            lock (_lock)
            {
                Builds++;
                if (signature != null) { _sig = signature; _policy = p; _builtUtc = nowUtc; }
                else { _sig = null; _policy = null; }
            }
            return p;
        }

        /// <summary>The parts joined unambiguously; null when any part is null (an input that could not be read).</summary>
        public static string Signature(IList<string> parts)
        {
            var sb = new StringBuilder();
            foreach (string s in parts)
            {
                if (s == null) return null;
                sb.Append(s.Length).Append(':').Append(s).Append('|');
            }
            return sb.ToString();
        }

        /// <summary>"path@ticks/length", "path@dir" or "path@absent"; null when the file system could not be asked.</summary>
        public static string FileStamp(string path)
        {
            if (string.IsNullOrEmpty(path)) return "@none";
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) return path + "@" + fi.LastWriteTimeUtc.Ticks + "/" + fi.Length;
                return path + (Directory.Exists(path) ? "@dir" : "@absent");
            }
            catch { return null; }
        }
    }
}
