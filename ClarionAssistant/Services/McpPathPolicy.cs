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
                 + Reason + ". " + Hint;
        }
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
    ///                   search folders, plus Mcp.AllowedRoots / CLARION_MCP_ALLOWED_ROOTS. Readable and writable.
    ///                   %APPDATA%\ClarionAssistant is a READ-ONLY root (the logs the assistant is told to read).
    ///                   Automatic roots never widen to a whole drive: a .red entry or project folder that IS a
    ///                   drive root is skipped. Only the developer's own setting can name a drive root.
    ///   Deny          - wins over every allow, and the built-in rules cannot be switched off: UNC and device paths,
    ///                   network drives, \.ssh\ \.aws\ (and a few other credential folders), the user's Claude
    ///                   config (%USERPROFILE%\.claude, %USERPROFILE%\.claude-*, CLAUDE_CONFIG_DIR) and any
    ///                   .credentials.json, other users' profiles, CA's own secrets (settings.txt holds the MCP token; mcp-*.json the tab tokens;
    ///                   copilot\ the Copilot login), and ANY write into %APPDATA%\ClarionAssistant - otherwise the
    ///                   model could rewrite Mcp.AllowedRoots and widen its own scope. Plus Mcp.DeniedPaths.
    ///   Write-only    - any OTHER .claude folder (a project's own, or the one a worktree lives under) stays
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

        /// <summary>File names denied (read and write) wherever they appear.</summary>
        public static readonly string[] DeniedFileNames = { ".credentials.json" };

        public sealed class Root
        {
            public string Lexical;   // GetFullPath form
            public string Resolved;  // link/8.3-resolved form (== Lexical when nothing to resolve)
            public bool Writable;
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
        }

        private readonly List<Root> _roots = new List<Root>();
        private readonly List<DenyRule> _deny = new List<DenyRule>();
        private readonly List<string> _ignored = new List<string>();
        private readonly Dictionary<char, bool> _networkDrive = new Dictionary<char, bool>();
        private string _usersDir, _userProfile, _publicProfile;

        public IList<Root> Roots { get { return _roots.AsReadOnly(); } }
        /// <summary>Setting entries that were not usable (relative, UNC, invalid), each as "entry (reason)".</summary>
        public IList<string> Ignored { get { return _ignored.AsReadOnly(); } }

        private McpPathPolicy() { }

        public static McpPathPolicy Build(PathPolicyInputs inputs)
        {
            var p = new McpPathPolicy();
            if (inputs == null) inputs = new PathPolicyInputs();

            // ---- deny first: roots that fall inside a denied area are dropped below
            string profile = Lexical(inputs.UserProfile);
            if (profile != null)
            {
                p._userProfile = profile;
                p._usersDir = Lexical(System.IO.Path.GetDirectoryName(profile));
                if (p._usersDir != null) p._publicProfile = p._usersDir + "\\Public";
                // The user's Claude Code config: credentials and every session transcript. Read AND write.
                p.AddBuiltInPrefix(profile + "\\.claude", profile + "\\.claude\\");
                p.AddBuiltInPattern(profile + "\\.claude-*", false);   // alternate profiles (.claude-work, ...)
                p.AddBuiltInPattern(profile + "\\.claude.json*", false); // Claude Code's user config file (+ backups)
            }
            string claudeDir = Lexical(inputs.ClaudeConfigDir);
            if (claudeDir != null && !IsDriveRoot(claudeDir)) p.AddBuiltInPrefix(claudeDir, claudeDir + "\\ (CLAUDE_CONFIG_DIR)");
            string ca = Lexical(inputs.CaDataDir);
            if (ca != null)
            {
                p.AddBuiltInPattern(ca + "\\settings.txt*", false);
                p.AddBuiltInPattern(ca + "\\mcp-*.json*", false);
                p._deny.Add(new DenyRule { Prefix = ca + "\\copilot", BuiltIn = true, Text = ca + "\\copilot\\" });
                p._deny.Add(new DenyRule { Prefix = ca, WriteOnly = true, BuiltIn = true, Text = ca + "\\ (writes)" });
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
                    string res = ResolveForRoot(full);
                    if (res != null && !Same(res, full)) p._deny.Add(new DenyRule { Prefix = res, Text = entry });
                }
                else
                    p._deny.Add(new DenyRule { Segment = entry.Trim('\\', '/'), Text = entry });
            }

            // ---- roots
            p.AddRoot(inputs.SolutionDir, "solution", true, false);
            if (inputs.ProjectDirs != null) foreach (string d in inputs.ProjectDirs) p.AddRoot(d, "project", true, false);
            if (inputs.ClarionRoots != null) foreach (string d in inputs.ClarionRoots) p.AddRoot(d, "clarion", true, false);
            if (inputs.RedPaths != null) foreach (string d in inputs.RedPaths) p.AddRoot(d, "red", true, false);
            foreach (string entry in SplitList(inputs.ExtraAllowedRoots)) p.AddRoot(entry, "setting", true, true);
            p.AddRoot(inputs.CaDataDir, "ca-data", false, false);
            return p;
        }

        private void AddBuiltInPrefix(string full, string text)
        {
            _deny.Add(new DenyRule { Prefix = full, BuiltIn = true, Text = text });
            string res = ResolveForRoot(full);   // the folder may itself be a junction (OneDrive, moved profile)
            if (res != null && !Same(res, full)) _deny.Add(new DenyRule { Prefix = res, BuiltIn = true, Text = text });
        }

        private void AddBuiltInPattern(string wildcard, bool writeOnly)
        {
            _deny.Add(new DenyRule { Pattern = WildcardRegex(wildcard), WriteOnly = writeOnly, BuiltIn = true, Text = wildcard });
        }

        private void AddRoot(string raw, string source, bool writable, bool explicitSetting)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            string why;
            string lex = Canonical(raw, out why);
            if (lex == null)
            {
                if (explicitSetting) _ignored.Add(raw + " (" + AllowedRootsSetting + ": " + why + ")");
                return;
            }
            if (IsNetworkDrive(lex)) { if (explicitSetting) _ignored.Add(raw + " (network drive)"); return; }
            string res = ResolveForRoot(lex) ?? lex;
            if (!IsLocalDrivePath(res)) { if (explicitSetting) _ignored.Add(raw + " (resolves to a network or device location)"); return; }
            if (!explicitSetting && IsDriveRoot(res)) return;          // automatic roots never widen to a whole drive
            foreach (var r in _roots)
                if (Same(r.Lexical, lex) && Same(r.Resolved, res))
                {
                    if (writable) r.Writable = true;
                    return;
                }
            _roots.Add(new Root { Lexical = lex, Resolved = res, Writable = writable, Source = source });
        }

        // ------------------------------------------------------------------ checks

        /// <summary>
        /// The full check for a file tool: lexical rules, then the link-resolved real path. Touches the file system
        /// only for drive type and the nearest existing ancestor's final path, and only after the lexical rules
        /// passed - so a UNC path is refused without any SMB traffic.
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

        private PathDecision Decide(string input, PathAccess access, bool resolve)
        {
            string why;
            string lex = Canonical(input, out why);
            if (lex == null) return Refuse(null, why, "This cannot be overridden: give a full local path such as C:\\MyApp\\Main.clw.");
            if (IsNetworkDrive(lex))
                return Refuse(lex, "it is on a network drive", "Network locations are always refused and cannot be allowed by a setting.");

            string real = lex;
            if (resolve)
            {
                real = ResolveNearest(lex);
                if (real == null)
                    return Refuse(lex, "its real location could not be determined (link or short name did not resolve)", "This fails closed and cannot be overridden.");
                if (!IsLocalDrivePath(real))
                    return Refuse(lex, "it resolves to '" + real + "', which is not a local drive path", "Network and device targets are always refused.");
            }

            DenyRule hit = FindDeny(lex, access) ?? (Same(real, lex) ? null : FindDeny(real, access));
            if (hit != null)
            {
                if (hit.BuiltIn)
                    return Refuse(real, "it matches the built-in deny rule '" + hit.Text + "'", "Built-in deny rules win over every allowed root and cannot be overridden.");
                return Refuse(real, "it matches your " + DeniedPathsSetting + " entry '" + hit.Text + "'",
                    "Remove that entry from " + DeniedPathsSetting + " (or " + DeniedPathsEnv + ") to allow it.");
            }
            string other = OtherProfileHit(lex) ?? (Same(real, lex) ? null : OtherProfileHit(real));
            if (other != null)
                return Refuse(real, "it is inside another user's profile (" + other + ")", "Other users' profiles are always refused.");

            bool inReadOnly = false;
            foreach (var r in _roots)
            {
                bool inside = resolve ? IsUnder(real, r.Resolved) || (Same(real, lex) && IsUnder(lex, r.Lexical))
                                      : IsUnder(lex, r.Lexical) || IsUnder(lex, r.Resolved);
                if (!inside) continue;
                if (access == PathAccess.Read || r.Writable) return new PathDecision { Allowed = true, Path = real };
                inReadOnly = true;
            }
            if (inReadOnly)
                return Refuse(real, "it is in a read-only root (Clarion Assistant's own data folder)", "Write tools never write there; this cannot be overridden.");
            return Refuse(real, "it is outside every allowed root (" + RootSummary() + ")",
                "To allow it, add its folder to the " + AllowedRootsSetting + " setting (semicolon-separated) in %APPDATA%\\ClarionAssistant\\settings.txt, or to the " + AllowedRootsEnv + " environment variable of the MCP server.");
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
            string leaf = System.IO.Path.GetFileName(full);
            foreach (string name in DeniedFileNames)
                if (Same(leaf, name))
                    return new DenyRule { BuiltIn = true, Text = "*\\" + name };
            if (access == PathAccess.Write)
                foreach (string seg in WriteDeniedSegments)
                    if (HasSegment(full, seg))
                        return new DenyRule { BuiltIn = true, WriteOnly = true, Text = "*\\" + seg + "\\* (writes)" };
            return null;
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

        /// <summary>The lexical canonical form, or null with the reason. Never touches the file system.</summary>
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
            if (s.StartsWith("\\\\") || s.StartsWith("\\??\\")) { why = "UNC and device paths (\\\\server\\share, \\\\?\\, \\\\.\\) are always refused"; return null; }
            if (!(s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\')) { why = "it is not a fully qualified local path (X:\\...); relative paths are refused"; return null; }
            if (s.IndexOf(':', 2) >= 0) { why = "it names an alternate data stream"; return null; }
            foreach (string seg in s.Substring(3).Split('\\'))
            {
                if (seg.Length == 0) continue;
                string stem = seg.TrimEnd(' ', '.');
                int dot = stem.IndexOf('.');
                if (dot >= 0) stem = stem.Substring(0, dot);
                if (DeviceName.IsMatch(stem.Trim())) { why = "it names a reserved device (" + seg + ")"; return null; }
            }
            string full;
            try { full = System.IO.Path.GetFullPath(s); }
            catch (Exception ex) { why = "it is not a valid path (" + ex.GetType().Name + ")"; return null; }
            if (!IsLocalDrivePath(full)) { why = "it does not stay a local drive path once normalised"; return null; }
            return TrimEnd(full);
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
}
