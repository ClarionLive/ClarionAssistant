using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #266: the glue between <see cref="McpPathPolicy"/> (pure rules) and the MCP tools. Builds the policy from
    /// the live workspace and settings on every call - the solution can switch under a running server (9e1bd280),
    /// and a policy cached across that switch would keep the OLD solution's folder allowed - gates the file tools,
    /// and runs the scoped Everything search. Every decision is written to <see cref="McpPathAudit"/>.
    /// </summary>
    public static class McpPathGate
    {
        /// <summary>Results scanned (in-scope or not) before a scoped search stops paging.</summary>
        public const int ScanCap = 10000;
        public const int MaxResultsCeiling = 1000;

        private static readonly object _slnLock = new object();
        private static string _slnCachePath;
        private static DateTime _slnCacheStamp;
        private static List<string> _slnCacheDirs;

        public static string CaDataDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
        }

        private static string Setting(string key)
        {
            try { return new SettingsService().Get(key); }
            catch { return null; }
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;
            return a + ";" + b;
        }

        /// <summary>search_content is OFF unless Mcp.EverythingContentSearch=true: it reads file CONTENTS across the index.</summary>
        public static bool ContentSearchEnabled()
        {
            return string.Equals((Setting(McpPathPolicy.ContentSearchSetting) ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        public static McpPathPolicy BuildPolicy(IWorkspaceContext ws)
        {
            var inputs = new PathPolicyInputs
            {
                CaDataDir = CaDataDir(),
                UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ClaudeConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                ExtraAllowedRoots = Join(Setting(McpPathPolicy.AllowedRootsSetting), Environment.GetEnvironmentVariable(McpPathPolicy.AllowedRootsEnv)),
                ExtraDeniedPaths = Join(Setting(McpPathPolicy.DeniedPathsSetting), Environment.GetEnvironmentVariable(McpPathPolicy.DeniedPathsEnv))
            };
            if (ws != null)
            {
                string sln = null;
                try { sln = ws.CurrentSolutionPath; } catch { }
                if (!string.IsNullOrEmpty(sln) && File.Exists(sln))
                {
                    inputs.SolutionDir = Path.GetDirectoryName(Path.GetFullPath(sln));
                    inputs.ProjectDirs.AddRange(ProjectDirs(sln));
                }
                try { var cfg = ws.CurrentVersionConfig; if (cfg != null) inputs.ClarionRoots.Add(cfg.RootPath); } catch { }
                try { inputs.ClarionRoots.Add(ws.GetClarionInstallPath()); } catch { }
                RedFileService red = null, active = null;
                try { red = ws.RedFile; } catch { }
                try { active = ws.ActiveRedFileService; } catch { }
                AddRedPaths(inputs, red);
                if (active != null && !ReferenceEquals(active, red)) AddRedPaths(inputs, active);
            }
            return McpPathPolicy.Build(inputs);
        }

        /// <summary>Every folder of every section of the .red. Relative entries hang off the solution folder; with no
        /// solution they are skipped rather than anchored on the process CWD (the Clarion bin, in the IDE).</summary>
        private static void AddRedPaths(PathPolicyInputs inputs, RedFileService red)
        {
            if (red == null || red.Sections == null) return;
            foreach (var section in red.Sections.Values)
            {
                if (section == null || section.Entries == null) continue;
                foreach (var entry in section.Entries)
                {
                    string p = entry != null ? entry.ResolvedPath : null;
                    if (string.IsNullOrEmpty(p) || RedFileService.HasUnexpandedMacro(p)) continue;
                    string full = RedFileService.IsFullyQualified(p)
                        ? RedFileService.AnchorAt(p, null)
                        : (inputs.SolutionDir != null ? RedFileService.AnchorAt(p, inputs.SolutionDir) : null);
                    if (full != null) inputs.RedPaths.Add(full);
                }
            }
        }

        /// <summary>The folders of the solution's projects (a project may live beside, not below, the .sln). Cached on
        /// the .sln's path and timestamp: every gated call builds a policy.</summary>
        private static List<string> ProjectDirs(string sln)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(sln); } catch { return new List<string>(); }
            lock (_slnLock)
            {
                if (_slnCacheDirs != null && string.Equals(_slnCachePath, sln, StringComparison.OrdinalIgnoreCase) && _slnCacheStamp == stamp)
                    return new List<string>(_slnCacheDirs);
            }
            var dirs = new List<string>();
            try
            {
                foreach (var proj in new ClarionCodeGraph.Parsing.SolutionParser().Parse(sln))
                {
                    if (proj == null || string.IsNullOrEmpty(proj.CwprojPath)) continue;
                    string d = Path.GetDirectoryName(proj.CwprojPath);
                    if (!string.IsNullOrEmpty(d)) dirs.Add(d);
                }
            }
            catch { }
            lock (_slnLock)
            {
                _slnCachePath = sln; _slnCacheStamp = stamp; _slnCacheDirs = dirs;
            }
            return new List<string>(dirs);
        }

        /// <summary>
        /// Gate one file-tool path. Returns the refusal text (and audits a DENY), or null with
        /// <paramref name="canonical"/> set to the path the tool must use from here on (the resolved one).
        /// </summary>
        public static string GatePath(string tool, string path, PathAccess access, IWorkspaceContext ws, out string canonical)
        {
            canonical = null;
            PathDecision d;
            try { d = BuildPolicy(ws).Check(path, access); }
            catch (Exception ex)
            {
                McpPathAudit.Write(tool, "DENY", path, "policy failed: " + ex.Message);
                return "Error: " + tool + " refused by the Clarion Assistant path policy (GH #266): the policy could not be evaluated (" + ex.Message + "). This fails closed.";
            }
            if (!d.Allowed)
            {
                McpPathAudit.Write(tool, "DENY", path, d.Reason);
                return d.Refusal(tool, path);
            }
            McpPathAudit.Write(tool, "ALLOW", d.Path, access.ToString().ToLowerInvariant());
            canonical = d.Path;
            return null;
        }

        public static int ClampMax(int requested, int defaultValue) { return McpPathPolicy.ClampMax(requested, defaultValue, MaxResultsCeiling); }

        public static bool IsBareWildcard(string query) { return McpPathPolicy.IsBareWildcard(query); }

        public sealed class ScopedResult
        {
            public List<SearchResultItem> Items = new List<SearchResultItem>();
            public string Error;
            public int Filtered;
            public bool ScanCapped;
        }

        private static string RootTerm(string folder)
        {
            // noww/nocase/noregex: the caller's match_whole_word / match_case apply to ITS terms, not to the scope.
            return "noww:nocase:noregex:\"" + folder.TrimEnd('\\') + "\\\"";
        }

        /// <summary>
        /// An Everything search confined to the policy's roots. Two layers, and only the second is the guarantee:
        ///   1. the query sent is "&lt;root1|root2|...&gt; [&lt;path&gt;] &lt;filters query&gt;". In Everything OR binds
        ///      tighter than AND (verified live: "&lt;root&gt; &lt;x&gt; | &lt;y&gt;" stays inside root), so a '|', '&gt;'
        ///      or 'path:' the model puts in the query cannot widen the scope - this just keeps the scan small;
        ///   2. EVERY result is checked against the policy before it is returned, whatever the query said.
        /// The model's own path argument is gated like a file-tool path first.
        /// </summary>
        public static ScopedResult Search(string tool, IWorkspaceContext ws, string userQuery, string filters,
                                          string pathArg, SearchOptions opts, int max)
        {
            var result = new ScopedResult();
            McpPathPolicy policy;
            try { policy = BuildPolicy(ws); }
            catch (Exception ex)
            {
                result.Error = tool + " refused: the path policy could not be evaluated (" + ex.Message + "). This fails closed.";
                McpPathAudit.Write(tool, "DENY", userQuery, result.Error);
                return result;
            }

            var roots = policy.SearchRoots();
            if (roots.Count == 0)
            {
                result.Error = tool + " refused by the Clarion Assistant path policy (GH #266): there are no allowed roots to search ("
                             + policy.RootSummary() + "). Open a solution, or add folders to the " + McpPathPolicy.AllowedRootsSetting + " setting.";
                McpPathAudit.Write(tool, "DENY", userQuery, "no roots");
                return result;
            }

            string pathTerm = "";
            if (!string.IsNullOrEmpty(pathArg))
            {
                PathDecision d = policy.Check(pathArg, PathAccess.Read);
                if (!d.Allowed)
                {
                    McpPathAudit.Write(tool, "DENY", "path=" + pathArg, d.Reason);
                    result.Error = d.Refusal(tool, pathArg).Substring("Error: ".Length);
                    return result;
                }
                pathTerm = " <" + RootTerm(d.Path) + ">";
            }

            string queryPart = userQuery ?? "";
            if (opts.Regex)
            {
                if (queryPart.IndexOf('"') >= 0)
                {
                    result.Error = tool + ": a regex query cannot contain a double quote (\"); the server wraps it as regex:\"...\" to keep it inside the allowed roots.";
                    McpPathAudit.Write(tool, "DENY", userQuery, "regex with quote");
                    return result;
                }
                queryPart = "regex:\"" + queryPart + "\"";
                opts.Regex = false;   // the global flag would make the ROOT terms regexes too
            }

            var sb = new StringBuilder("<");
            for (int i = 0; i < roots.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(RootTerm(roots[i]));
            }
            sb.Append('>').Append(pathTerm).Append(" <");
            if (!string.IsNullOrEmpty(filters)) sb.Append(filters.Trim()).Append(' ');
            sb.Append(queryPart).Append('>');
            string query = sb.ToString();

            int page = Math.Min(Math.Max(max, 100), MaxResultsCeiling);
            int offset = 0;
            while (result.Items.Count < max)
            {
                if (offset >= ScanCap) { result.ScanCapped = true; break; }
                opts.MaxResults = page;
                opts.Offset = offset;
                var raw = EverythingService.Search(query, opts);
                if (raw.HasError) { result.Error = raw.Error; break; }
                foreach (var item in raw.Items)
                {
                    if (policy.CheckLexical(item.FullPath, PathAccess.Read).Allowed)
                    {
                        if (result.Items.Count < max) result.Items.Add(item);
                    }
                    else result.Filtered++;
                }
                if (raw.Items.Count < page) break;
                offset += page;
            }

            McpPathAudit.Write(tool, result.Error == null ? "SEARCH" : "ERROR", query,
                result.Items.Count + " returned, " + result.Filtered + " filtered out" + (result.ScanCapped ? ", scan cap reached" : "")
                + (result.Error != null ? ", " + result.Error : ""));
            return result;
        }
    }

    /// <summary>
    /// GH #266: %APPDATA%\ClarionAssistant\mcp-path-audit.log - one line per gated file-tool path and per Everything
    /// query, with the outcome, so the developer can see what the assistant searched for, opened and was refused.
    /// UTF-8 without BOM, appended under a lock, rotated to mcp-path-audit.prev.log past 2 MB. Best-effort: logging
    /// never fails a tool call. The log sits in a READ-ONLY root, so the write tools cannot rewrite their own record.
    /// </summary>
    public static class McpPathAudit
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private static readonly object _lock = new object();

        public static string LogPath()
        {
            return Path.Combine(McpPathGate.CaDataDir(), "mcp-path-audit.log");
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        public static void Write(string tool, string outcome, string subject, string detail)
        {
            try
            {
                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff}\tpid={1}\t{2}\t{3}\t{4}\t{5}\r\n",
                    DateTime.Now, System.Diagnostics.Process.GetCurrentProcess().Id,
                    Clean(tool), Clean(outcome), Clean(subject), Clean(detail));
                string path = LogPath();
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string prev = Path.Combine(fi.DirectoryName, "mcp-path-audit.prev.log");
                            if (File.Exists(prev)) File.Delete(prev);
                            File.Move(path, prev);
                        }
                    }
                    catch { }   // another process rotated first, or holds it: append to whatever is there
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try { File.AppendAllText(path, line, EncodingHelper.Utf8NoBom); return; }
                        catch (IOException) { System.Threading.Thread.Sleep(15); }   // the other server process mid-append
                    }
                }
            }
            catch { }
        }
    }
}
