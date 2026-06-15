using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Represents a single redirection entry: *.ext = path
    /// </summary>
    public class RedEntry
    {
        public string Pattern { get; set; }   // e.g. "*.clw", "*.inc", "*.*"
        public string RawPath { get; set; }    // original path with macros
        public string ResolvedPath { get; set; } // path with macros expanded
    }

    /// <summary>
    /// A parsed section from the .red file (e.g. [Common], [Debug32])
    /// </summary>
    public class RedSection
    {
        public string Name { get; set; }
        public List<RedEntry> Entries { get; set; }

        public RedSection()
        {
            Entries = new List<RedEntry>();
        }
    }

    /// <summary>
    /// A single file discovered by walking the redirection index (see <see cref="RedFileService.EnumerateFiles"/>).
    /// </summary>
    public class RedFileMatch
    {
        public string Name { get; set; }      // file name only (e.g. "Customer.clw")
        public string FullPath { get; set; }  // resolved absolute path on disk
        public string Section { get; set; }   // the .red section the match came from (e.g. "Common")
    }

    /// <summary>
    /// Parses Clarion .red (redirection) files and resolves file paths.
    /// The .red file tells the compiler/IDE where to find source files,
    /// includes, libraries, images, etc.
    /// </summary>
    public class RedFileService
    {
        /// <summary>Hard cap on how many files <see cref="EnumerateFiles"/> returns. Single source of truth —
        /// the host reports truncation from the out-param, so the UI hint and the flag never disagree.</summary>
        public const int MaxFiles = 20000;

        private readonly Dictionary<string, RedSection> _sections;
        private readonly Dictionary<string, string> _macros;
        private string _redFilePath;

        /// <summary>The most-recently-loaded instance, so static helpers can resolve generated files.</summary>
        public static RedFileService Active { get; private set; }

        public string RedFilePath => _redFilePath;
        public IReadOnlyDictionary<string, RedSection> Sections => _sections;
        public IReadOnlyDictionary<string, string> Macros => _macros;

        public RedFileService()
        {
            _sections = new Dictionary<string, RedSection>(StringComparer.OrdinalIgnoreCase);
            _macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Load and parse a .red file using macros from the version config.
        /// </summary>
        public bool Load(string redFilePath, Dictionary<string, string> macros)
        {
            if (string.IsNullOrEmpty(redFilePath) || !File.Exists(redFilePath))
                return false;

            _redFilePath = redFilePath;
            _sections.Clear();
            _macros.Clear();

            if (macros != null)
            {
                foreach (var kv in macros)
                    _macros[kv.Key] = kv.Value;
            }

            // Ensure standard macros exist
            if (!_macros.ContainsKey("BIN") && !string.IsNullOrEmpty(redFilePath))
                _macros["BIN"] = Path.GetDirectoryName(redFilePath);

            if (!_macros.ContainsKey("ROOT") && _macros.ContainsKey("root"))
                _macros["ROOT"] = _macros["root"];

            if (!_macros.ContainsKey("REDDIR") && _macros.ContainsKey("reddir"))
                _macros["REDDIR"] = _macros["reddir"];

            try
            {
                Parse(File.ReadAllLines(redFilePath));
                Active = this;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Load from a ClarionVersionConfig (convenience method).
        /// </summary>
        public bool Load(ClarionVersionConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.RedFilePath))
                return false;

            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (config.Macros != null)
            {
                foreach (var kv in config.Macros)
                    macros[kv.Key] = kv.Value;
            }

            if (!macros.ContainsKey("ROOT") && !string.IsNullOrEmpty(config.RootPath))
                macros["ROOT"] = config.RootPath;

            if (!macros.ContainsKey("BIN") && !string.IsNullOrEmpty(config.BinPath))
                macros["BIN"] = config.BinPath;

            return Load(config.RedFilePath, macros);
        }

        /// <summary>
        /// Load the effective .red file for a project directory.
        /// If a .red file exists in the project directory, it completely
        /// supersedes the version-level .red file.
        /// </summary>
        public bool LoadForProject(string projectDirectory, ClarionVersionConfig config)
        {
            if (config == null) return false;

            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (config.Macros != null)
            {
                foreach (var kv in config.Macros)
                    macros[kv.Key] = kv.Value;
            }

            if (!macros.ContainsKey("ROOT") && !string.IsNullOrEmpty(config.RootPath))
                macros["ROOT"] = config.RootPath;

            if (!macros.ContainsKey("BIN") && !string.IsNullOrEmpty(config.BinPath))
                macros["BIN"] = config.BinPath;

            // Check for a local .red file in the project directory
            if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory))
            {
                string localRed = FindLocalRedFile(projectDirectory);
                if (localRed != null)
                    return Load(localRed, macros);
            }

            // Fall back to the version-level .red
            if (!string.IsNullOrEmpty(config.RedFilePath))
                return Load(config.RedFilePath, macros);

            return false;
        }

        /// <summary>
        /// Look for a .red file in a project directory.
        /// </summary>
        private static string FindLocalRedFile(string directory)
        {
            try
            {
                string[] redFiles = Directory.GetFiles(directory, "*.red", SearchOption.TopDirectoryOnly);
                if (redFiles.Length > 0)
                    return redFiles[0];
            }
            catch { }
            return null;
        }

        private void Parse(string[] lines)
        {
            RedSection current = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(line) || line.StartsWith("--"))
                    continue;

                // Section header: [SectionName]
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    current = new RedSection { Name = name };
                    _sections[name] = current;
                    continue;
                }

                // Entry: pattern = path1;path2;...
                if (current != null && line.Contains("="))
                {
                    int eqIdx = line.IndexOf('=');
                    string pattern = line.Substring(0, eqIdx).Trim();
                    string pathsPart = line.Substring(eqIdx + 1).Trim().TrimEnd(';');

                    // A single entry can have multiple semicolon-separated paths,
                    // but typically each line is one path. Handle both.
                    string[] paths = pathsPart.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in paths)
                    {
                        string rawPath = p.Trim();
                        if (string.IsNullOrEmpty(rawPath)) continue;

                        current.Entries.Add(new RedEntry
                        {
                            Pattern = pattern,
                            RawPath = rawPath,
                            ResolvedPath = ExpandMacros(rawPath)
                        });
                    }
                }
            }
        }

        private string ExpandMacros(string path)
        {
            return Regex.Replace(path, @"%(\w+)%", m =>
            {
                string key = m.Groups[1].Value;
                string value;
                if (_macros.TryGetValue(key, out value))
                    return value;
                return m.Value; // leave unexpanded if unknown
            });
        }

        /// <summary>
        /// Resolve a filename to its full path by searching the redirection entries.
        /// Searches the specified sections in order (defaults to Common).
        /// Returns the first existing match, or null.
        /// </summary>
        public string Resolve(string fileName, params string[] sectionNames)
        {
            return ResolveFrom(fileName, null, sectionNames);
        }

        /// <summary>
        /// Resolve a filename to its full path, anchoring RELATIVE redirection paths (.\ , ..\)
        /// to <paramref name="baseDir"/> (the project/app directory) instead of the process CWD.
        /// Clarion redirection relative paths are relative to the project being built, so callers
        /// that know the app directory should pass it here. Absolute entries are unaffected.
        /// Searches the specified sections in order (defaults to Common). Returns the first existing match, or null.
        /// </summary>
        public string ResolveFrom(string fileName, string baseDir, params string[] sectionNames)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            if (sectionNames == null || sectionNames.Length == 0)
                sectionNames = new[] { "Common" };

            foreach (string sectionName in sectionNames)
            {
                RedSection section;
                if (!_sections.TryGetValue(sectionName, out section))
                    continue;

                foreach (var entry in section.Entries)
                {
                    if (MatchesPattern(fileName, entry.Pattern))
                    {
                        string candidate = Path.Combine(entry.ResolvedPath, fileName);
                        try
                        {
                            // Redirection relative paths are relative to the project dir, not the IDE's CWD.
                            if (!Path.IsPathRooted(candidate) && !string.IsNullOrEmpty(baseDir))
                                candidate = Path.Combine(baseDir, candidate);
                            candidate = Path.GetFullPath(candidate);
                            if (File.Exists(candidate))
                                return candidate;
                        }
                        catch { }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve a filename exactly like <see cref="ResolveFrom"/>, but append a human-readable line
        /// to <paramref name="trace"/> for each step taken — which sections were present/absent, which
        /// patterns matched, which directories were probed, and the final FOUND/NOT FOUND result.
        /// This drives the "Trace" button in the redirection UI (mirrors the native dialog's trace log).
        /// The search logic is identical to ResolveFrom so the trace faithfully explains a real resolve.
        /// </summary>
        public string ResolveTrace(string fileName, string baseDir, List<string> trace, params string[] sectionNames)
        {
            if (trace == null) trace = new List<string>();

            if (string.IsNullOrEmpty(fileName))
            {
                trace.Add("No filename specified.");
                return null;
            }

            if (sectionNames == null || sectionNames.Length == 0)
                sectionNames = new[] { "Common" };

            trace.Add("Looking for: " + fileName);

            foreach (string sectionName in sectionNames)
            {
                RedSection section;
                if (!_sections.TryGetValue(sectionName, out section))
                {
                    trace.Add("Section [" + sectionName + "] ... not present, skipped");
                    continue;
                }

                trace.Add("Section [" + sectionName + "]");

                foreach (var entry in section.Entries)
                {
                    if (!MatchesPattern(fileName, entry.Pattern))
                    {
                        trace.Add("  Pattern " + entry.Pattern + " does not match");
                        continue;
                    }

                    trace.Add("  Pattern " + entry.Pattern + " matches  (" + entry.RawPath + ")");

                    string candidate = Path.Combine(entry.ResolvedPath, fileName);
                    try
                    {
                        // Same relative-path anchoring as ResolveFrom.
                        if (!Path.IsPathRooted(candidate) && !string.IsNullOrEmpty(baseDir))
                            candidate = Path.Combine(baseDir, candidate);
                        candidate = Path.GetFullPath(candidate);
                    }
                    catch
                    {
                        trace.Add("    invalid path, skipped: " + entry.RawPath);
                        continue;
                    }

                    if (File.Exists(candidate))
                    {
                        trace.Add("FOUND: " + candidate);
                        return candidate;
                    }

                    trace.Add("    Not found in " + Path.GetDirectoryName(candidate));
                }
            }

            trace.Add("NOT FOUND: " + fileName);
            return null;
        }

        /// <summary>
        /// Get all search directories for a given file extension in the specified section.
        /// </summary>
        public List<string> GetSearchPaths(string extension, string sectionName = "Common")
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(extension)) return result;

            if (!extension.StartsWith("."))
                extension = "." + extension;

            string testName = "test" + extension;

            RedSection section;
            if (!_sections.TryGetValue(sectionName, out section))
                return result;

            foreach (var entry in section.Entries)
            {
                if (MatchesPattern(testName, entry.Pattern))
                {
                    string resolved = entry.ResolvedPath;
                    if (!string.IsNullOrEmpty(resolved) && !result.Contains(resolved))
                        result.Add(resolved);
                }
            }

            return result;
        }

        /// <summary>
        /// Enumerate every file reachable through the redirection index for the given sections,
        /// mirroring what the native "Open File via Redirection File" dialog lists.
        ///
        /// This walks the SAME section set + pattern matching that <see cref="ResolveFrom"/> uses, so
        /// every returned file is guaranteed openable via the resolver. Designed to be called ONCE
        /// (off the UI thread) to build an in-memory index; callers then filter that index per keystroke
        /// with zero disk I/O — which is how this feature avoids the native dialog's fast-typing freeze.
        ///
        /// Mirror-everything: no extension filtering beyond the .red patterns themselves, so .bak / .tpl /
        /// .sync-conflict-* etc. are included exactly as the native dialog shows them.
        /// </summary>
        /// <param name="baseDir">Directory used to anchor RELATIVE redirection paths (the project/solution dir),
        /// matching <see cref="ResolveFrom"/>. Absolute entries are unaffected.</param>
        /// <param name="token">Cancellation token, checked during directory traversal so a superseded or slow scan
        /// (e.g. a large local folder or a slow UNC share) exits promptly instead of running to completion.</param>
        /// <param name="sectionNames">Sections to walk, in priority order (defaults to Common). The canonical
        /// order used elsewhere is Debug32, Release32, Debug, Release, Common.</param>
        /// <param name="truncated">Set true when the <see cref="MaxFiles"/> cap was hit and more files existed —
        /// so the caller can warn the user the list is incomplete (the cap can't be inferred from Count alone).</param>
        /// <returns>Matches deduped by filename (OrdinalIgnoreCase), keeping the FIRST occurrence so the
        /// .red search-order winner survives — same precedence as ResolveFrom. Capped at <see cref="MaxFiles"/>.</returns>
        public List<RedFileMatch> EnumerateFiles(string baseDir, System.Threading.CancellationToken token, out bool truncated, params string[] sectionNames)
        {
            const int Cap = MaxFiles;
            truncated = false;
            var results = new List<RedFileMatch>();

            if (sectionNames == null || sectionNames.Length == 0)
                sectionNames = new[] { "Common" };

            // Dedupe by filename, keep the FIRST hit (= .red priority winner, matches ResolveFrom).
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // List each physical directory only ONCE per call (a dir referenced by *.clw and *.inc
            // must not be enumerated twice). Keyed by resolved full directory path; bounded per dir.
            var dirCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool capped = false;     // global result cap hit → stop the whole walk
            bool dirCapped = false;  // a single directory hit the cap → list MAY be incomplete (truncation signal only; does NOT stop the walk)

            foreach (string sectionName in sectionNames)
            {
                if (token.IsCancellationRequested) break;
                RedSection section;
                if (!_sections.TryGetValue(sectionName, out section))
                    continue;

                foreach (var entry in section.Entries)
                {
                    if (token.IsCancellationRequested) break;

                    // Resolve the entry's directory the same way ResolveFrom anchors candidates:
                    // relative paths hang off baseDir (the project dir), not the process CWD.
                    string dir = entry.ResolvedPath;
                    if (string.IsNullOrEmpty(dir)) continue;
                    try
                    {
                        if (!Path.IsPathRooted(dir) && !string.IsNullOrEmpty(baseDir))
                            dir = Path.Combine(baseDir, dir);
                        dir = Path.GetFullPath(dir);
                    }
                    catch { continue; }

                    List<string> filesInDir;
                    if (!dirCache.TryGetValue(dir, out filesInDir))
                    {
                        // Stream the listing (Directory.EnumerateFiles is lazy, unlike GetFiles which materializes
                        // the WHOLE directory up front) and bound it at the cap, checking the cancellation token as
                        // we go — so a pathologically huge or slow (e.g. UNC) directory can't balloon memory or run
                        // long after a superseding request has cancelled this one.
                        filesInDir = new List<string>();
                        try
                        {
                            foreach (string full in Directory.EnumerateFiles(dir))
                            {
                                if (token.IsCancellationRequested) break;
                                filesInDir.Add(full);
                                // This single directory exceeded the cap: stop listing IT (bounds memory) but keep
                                // walking other dirs/sections. Flag truncation so the UI honestly reports the list
                                // may be incomplete — matches happening past this point in THIS dir are dropped.
                                if (filesInDir.Count >= Cap) { dirCapped = true; break; }
                            }
                        }
                        catch { /* missing dir, access denied, slow share error — treat as empty */ }
                        dirCache[dir] = filesInDir;
                    }

                    foreach (string full in filesInDir)
                    {
                        string fileName = Path.GetFileName(full);
                        // Use the SAME matcher ResolveFrom uses (not Win32 glob, which has 8.3 quirks).
                        if (!MatchesPattern(fileName, entry.Pattern)) continue;
                        if (!seenNames.Add(fileName)) continue; // already claimed by a higher-priority entry

                        results.Add(new RedFileMatch { Name = fileName, FullPath = full, Section = sectionName });
                        if (results.Count >= Cap) { capped = true; break; }
                    }
                    if (capped) break;
                }
                if (capped) break;
            }

            truncated = capped || dirCapped;
            if (truncated)
                System.Diagnostics.Debug.WriteLine("[RedFileService] EnumerateFiles hit the " + Cap + "-file cap; list truncated.");

            return results;
        }

        /// <summary>
        /// Simple wildcard pattern matching for .red patterns like *.clw, *.inc, *.*
        /// </summary>
        private static bool MatchesPattern(string fileName, string pattern)
        {
            // Handle *.* (matches everything)
            if (pattern == "*.*") return true;

            // Handle *.ext
            if (pattern.StartsWith("*."))
            {
                string patExt = pattern.Substring(1); // ".clw"
                // Handle single-char wildcards like *.tp?
                if (patExt.Contains("?"))
                {
                    string fileExt = Path.GetExtension(fileName);
                    if (fileExt.Length != patExt.Length) return false;
                    for (int i = 0; i < patExt.Length; i++)
                    {
                        if (patExt[i] != '?' && char.ToLowerInvariant(patExt[i]) != char.ToLowerInvariant(fileExt[i]))
                            return false;
                    }
                    return true;
                }

                return fileName.EndsWith(patExt, StringComparison.OrdinalIgnoreCase);
            }

            // Exact match fallback
            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
