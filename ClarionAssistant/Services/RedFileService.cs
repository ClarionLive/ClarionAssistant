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

    /// <summary>Why a .red {include} line was not followed (<see cref="RedFileService.SkippedIncludeKinds"/>).</summary>
    public enum RedIncludeSkip
    {
        /// <summary>The {...} line is not an {include path} command.</summary>
        NotAnInclude,
        /// <summary>The path (after macro expansion) is not a valid path.</summary>
        InvalidPath,
        /// <summary>The file was already read (it includes itself, or round a loop): its entries are already in, nothing is lost.</summary>
        AlreadyLoaded,
        /// <summary>Nested deeper than the include limit.</summary>
        TooDeep,
        /// <summary>No file at that path - often an unexpanded %MACRO%.</summary>
        FileNotFound,
        /// <summary>The file exists but could not be read.</summary>
        Unreadable
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

        /// <summary>
        /// The section order to search when resolving a file the BUILD produces or consumes — the C12 names
        /// (Debug32/Release32) first, then the legacy Debug/Release, then the universal Common fallback.
        /// A redirection for generated sources often lives ONLY under a build-specific section, so a
        /// Common-only lookup (the <see cref="ResolveFrom"/> default) misses it. Single source of truth:
        /// pass this rather than spelling the list out at each call site.
        ///
        /// The order is FIXED, not the build's active configuration: nothing in this codebase reads which
        /// configuration (Debug/Release) is current. It is the order ClarionAppDataReader has always used,
        /// so a .red that maps *.clw differently under [Debug32] and [Release32] resolves the [Debug32] one.
        ///
        /// Shared array: DO NOT MUTATE it — every caller passes this same instance.
        /// </summary>
        public static readonly string[] BuildSectionOrder = { "Debug32", "Release32", "Debug", "Release", "Common" };

        private readonly Dictionary<string, RedSection> _sections;
        private readonly Dictionary<string, string> _macros;
        private string _redFilePath;

        /// <summary>
        /// The .red in force, so static helpers can resolve generated files: the most recent successful
        /// load. NULL when the last <see cref="LoadForProject"/> failed while the file in force belonged to a
        /// DIFFERENT Clarion version (fails closed, f3b47441; see <see cref="ClearActiveUnlessFor"/>), so a
        /// reader must treat null as "no redirection". The Load(...) overloads only ever set it.
        /// </summary>
        public static RedFileService Active { get; private set; }

        /// <summary>The Clarion version name this instance was loaded for (from its config), or null.</summary>
        public string LoadedForVersion { get; private set; }

        /// <summary>Test seam (f3b47441): runs at the start of every LoadForProject, so the harness can make
        /// the load throw. Never set outside tests.</summary>
        internal static Action BeforeLoadForTest;

        public string RedFilePath => _redFilePath;

        /// <summary>The running Clarion version's redirection file NAME (e.g. "Clarion120.red"), when this
        /// instance was loaded from a <see cref="ClarionVersionConfig"/>; null otherwise. A local .red only
        /// takes effect under exactly this name (see <see cref="FindLocalRedFile"/>).</summary>
        public string VersionRedFileName { get; private set; }
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
            return Load(redFilePath, macros, makeActive: true);
        }

        /// <summary>
        /// Rebuild, in another process, an instance a host already loaded (ticket 583abdec: the IDE's index
        /// runs in clarion-indexer.exe). The host passes the file it loaded, its FINAL macro table
        /// (<see cref="Macros"/>, which already holds the BIN/ROOT/REDDIR it derived) and the version names,
        /// so resolution - the project-local .red rule of <see cref="ForProjectDirectory"/> included - is
        /// the host's own, not a re-derivation from the file's location.
        /// </summary>
        public bool LoadAs(string redFilePath, Dictionary<string, string> macros, string versionRedFileName, string loadedForVersion)
        {
            VersionRedFileName = string.IsNullOrEmpty(versionRedFileName) ? null : versionRedFileName;
            LoadedForVersion = string.IsNullOrEmpty(loadedForVersion) ? null : loadedForVersion;
            return Load(redFilePath, macros, makeActive: true);
        }

        private bool Load(string redFilePath, Dictionary<string, string> macros, bool makeActive)
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

            // %REDNAME% is predefined by Clarion, not stored with the version's Macros: it is the version's
            // redirection file NAME (RedirectionFile/Name, e.g. "Clarion110.red"). A project-local .red
            // typically chains to the default one with {include %REDDIR%\%REDNAME%}; without it that include
            // was skipped and every default search path lost. A local .red only loads under that same name,
            // so the loaded file's own name is the right fallback when no version name was recorded.
            if (!_macros.ContainsKey("REDNAME"))
            {
                string redName = !string.IsNullOrEmpty(VersionRedFileName) ? VersionRedFileName : redFilePath;
                _macros["REDNAME"] = Path.GetFileName(redName);
            }

            _loadedFiles.Clear();
            _watched.Clear();
            _skippedIncludes.Clear();
            _skippedIncludeKinds.Clear();
            try
            {
                ParseFile(redFilePath, 0);
                if (makeActive) Active = this;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Every .red file this instance read: the loaded file first, then each {include}d one, as full paths.
        /// </summary>
        public IReadOnlyList<string> LoadedFiles => _loadedFiles.AsReadOnly();

        /// <summary>{include} lines that could not be followed, with the reason: missing file, cycle,
        /// too deep. Clarion reports these as errors; we skip the line and carry on, so say so here.</summary>
        public IReadOnlyList<string> SkippedIncludes => _skippedIncludes;

        /// <summary>
        /// True when the redirection on disk may no longer be what this instance holds, so a host that caches
        /// it should load again (GH #261: an edit to the .red changed nothing until the server restarted):
        /// a file it read has changed or vanished, a file it could NOT read (locked mid-save) is readable now,
        /// or a file it looked for and did not find has appeared (an {include} target, a project-local .red).
        /// False for an instance that never loaded. Never throws.
        /// </summary>
        public bool IsStale()
        {
            foreach (var w in _watched)
            {
                try
                {
                    DateTime now = File.Exists(w.Key) ? File.GetLastWriteTimeUtc(w.Key) : AbsentOrUnread;
                    if (now != w.Value) return true;
                }
                catch { return true; }
            }
            return false;
        }

        /// <summary>The watch stamp for a file that was absent, or present but unreadable: any readable file
        /// then differs from it, so its appearance (or its unlock) makes the instance stale.</summary>
        private static readonly DateTime AbsentOrUnread = DateTime.MinValue;

        /// <summary>Record a path (absent, unread, or read with this stamp) for <see cref="IsStale"/>.</summary>
        private void Watch(string path, DateTime stamp)
        {
            if (!string.IsNullOrEmpty(path)) _watched.Add(new KeyValuePair<string, DateTime>(path, stamp));
        }

        private static string FullPathOrSelf(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        private readonly List<string> _loadedFiles = new List<string>();
        private readonly List<KeyValuePair<string, DateTime>> _watched = new List<KeyValuePair<string, DateTime>>();
        private readonly List<string> _skippedIncludes = new List<string>();
        private readonly List<RedIncludeSkip> _skippedIncludeKinds = new List<RedIncludeSkip>();

        /// <summary>Why each <see cref="SkippedIncludes"/> entry was skipped, same order, so a caller can word its
        /// report per reason: an {include} of a file already loaded loses nothing, a missing one loses its paths.</summary>
        public IReadOnlyList<RedIncludeSkip> SkippedIncludeKinds => _skippedIncludeKinds;

        private void Skip(string entry, RedIncludeSkip kind)
        {
            _skippedIncludes.Add(entry);
            _skippedIncludeKinds.Add(kind);
        }

        /// <summary>Nesting limit for {include}; Clarion has none, but a cycle it would hang on must not hang us.</summary>
        private const int MaxIncludeDepth = 8;

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

            VersionRedFileName = config.RedFileName;
            LoadedForVersion = config.Name;
            return Load(config.RedFilePath, macros);
        }

        /// <summary>
        /// Load the effective .red file for a project directory.
        /// If a .red file exists in the project directory, it completely
        /// supersedes the version-level .red file.
        ///
        /// FAILS CLOSED (f3b47441): when nothing can be loaded — no config, no RedFilePath, a missing or
        /// unreadable file, or an exception — a <see cref="Active"/> left over from a DIFFERENT Clarion version
        /// is cleared rather than kept; it resolved generated modules and Files-tab lookups through the wrong
        /// version's paths while looking healthy. An Active already loaded for THIS version is kept: three
        /// callers load with their own inputs (the chat panel with its own solution; the embeditor launcher and
        /// the Data pad with the IDE's), and one's failure must not undo another's current-version load
        /// (pipeline run 1). Never throws.
        /// </summary>
        public bool LoadForProject(string projectDirectory, ClarionVersionConfig config)
        {
            bool ok;
            try
            {
                var hook = BeforeLoadForTest;
                if (hook != null) hook();
                ok = LoadForProjectCore(projectDirectory, config);
            }
            catch { ok = false; }
            if (!ok) ClearActiveUnlessFor(config != null ? config.Name : null);
            return ok;
        }

        /// <summary>
        /// Clear <see cref="Active"/> unless it was loaded for <paramref name="versionName"/> (case-insensitive).
        /// A null name — no version resolved — clears any Active. The fail-closed rule of
        /// <see cref="LoadForProject"/>, for callers that fail before they can call it.
        /// </summary>
        public static void ClearActiveUnlessFor(string versionName)
        {
            var active = Active;
            if (active == null) return;
            if (!string.IsNullOrEmpty(versionName)
                && string.Equals(active.LoadedForVersion, versionName, StringComparison.OrdinalIgnoreCase))
                return;
            Active = null;
        }

        private bool LoadForProjectCore(string projectDirectory, ClarionVersionConfig config)
        {
            if (config == null) return false;
            LoadedForVersion = config.Name;

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

            VersionRedFileName = config.RedFileName;

            // Check for a local .red file in the project directory
            string localCandidate = null;
            if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory))
            {
                string localRed = FindLocalRedFile(projectDirectory, config.RedFileName);
                if (localRed != null)
                    return Load(localRed, macros);
                if (!string.IsNullOrEmpty(config.RedFileName))
                    try { localCandidate = Path.Combine(projectDirectory, Path.GetFileName(config.RedFileName)); } catch { }
            }

            // Fall back to the version-level .red
            if (!string.IsNullOrEmpty(config.RedFilePath))
            {
                bool ok = Load(config.RedFilePath, macros);
                // GH #261: a project-local .red created later supersedes this one; watch for it (after Load,
                // which resets the watch list).
                Watch(localCandidate, AbsentOrUnread);
                return ok;
            }

            return false;
        }

        /// <summary>
        /// The redirection file that governs a project living in <paramref name="projectDirectory"/>: the
        /// directory's own version-named .red when it has one (it completely supersedes the solution/version
        /// one; same rule as <see cref="LoadForProject"/>, see <see cref="FindLocalRedFile"/>), otherwise
        /// <paramref name="fallback"/> — normally
        /// <see cref="Active"/>, which is loaded for the SOLUTION's directory. Without this, an .app in its own
        /// project folder with its own .red resolves through another project's redirection.
        /// The local file is parsed with the fallback's macros (%ROOT%, %BIN%, ...) and is NOT made
        /// <see cref="Active"/>. Returns <paramref name="fallback"/> when the local .red is the file it already
        /// holds, or when the local one can't be read.
        /// </summary>
        public static RedFileService ForProjectDirectory(string projectDirectory, RedFileService fallback)
        {
            if (string.IsNullOrEmpty(projectDirectory) || fallback == null) return fallback;
            // The version's file name: recorded when the fallback came from a version config; otherwise the
            // fallback's own file name (a version-level .red is named for its version).
            string versionName = fallback.VersionRedFileName;
            if (string.IsNullOrEmpty(versionName) && !string.IsNullOrEmpty(fallback.RedFilePath))
                versionName = Path.GetFileName(fallback.RedFilePath);
            string localRed = FindLocalRedFile(projectDirectory, versionName);
            if (localRed == null) return fallback;
            if (fallback != null && !string.IsNullOrEmpty(fallback.RedFilePath))
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(localRed), Path.GetFullPath(fallback.RedFilePath),
                                      StringComparison.OrdinalIgnoreCase))
                        return fallback;
                }
                catch { }
            }

            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fallback != null)
                foreach (var kv in fallback.Macros) macros[kv.Key] = kv.Value;
            var local = new RedFileService { VersionRedFileName = versionName };
            return local.Load(localRed, macros, makeActive: false) ? local : fallback;
        }

        /// <summary>
        /// The local override .red in a project directory, or null. Clarion honours a local .red ONLY under
        /// the running version's own redirection file name (Clarion120.red for C12, Clarion110.red for C11 —
        /// ClarionVersionConfig.RedFileName); any other *.red there (MyApp.red, a backup, a second copy) is
        /// ignored by the IDE and ClarionCL alike (docs\ClarionCL-App-Generation.md, "A local .red overrides
        /// the global one only if version-named"). This used to take the FIRST *.red in the folder, which
        /// could be a file Clarion ignores, and with several present was whichever the file system listed
        /// first. Returns null when the version name is unknown.
        /// </summary>
        private static string FindLocalRedFile(string directory, string versionRedFileName)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(versionRedFileName)) return null;
            try
            {
                string candidate = Path.Combine(directory, Path.GetFileName(versionRedFileName));
                return File.Exists(candidate) ? candidate : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Parse one .red file into the sections, following its {include} lines. Mirrors Clarion's own
        /// parser (Clarion.Core RedirectionFile.Load, GH #261):
        /// - {include path} reads the other file IN PLACE, so its entries sit in the search order exactly
        ///   where the line is. The path's macros are expanded (%THISDIR% = the INCLUDING file's folder),
        ///   and a relative path is relative to the including file's folder.
        /// - A section named twice ([Common] in the included file and again here) ADDS to the search
        ///   order; it does not replace the earlier entries. Clarion keeps a list of sections and walks
        ///   them all; appending to one section per name gives the same order for each name.
        /// - Entries before any [section] header belong to [Common].
        /// - The included file's own lines start with no section (so its headerless lines are Common);
        ///   after it, this file carries on in the section it was in.
        /// An include that can't be followed (missing file, cycle, too deep) is skipped and recorded in
        /// <see cref="SkippedIncludes"/> - Clarion raises an error; a headless parser must not throw.
        /// </summary>
        private void ParseFile(string filePath, int depth)
        {
            // Full path, so the cycle check in FollowInclude compares like with like (".." segments).
            filePath = FullPathOrSelf(filePath);
            // Stamp BEFORE the read (an edit during it then shows as stale), record it only AFTER: a file
            // held by an editor mid-save throws here, and must stay stale so the next access retries
            // rather than keeping this empty load as if it were current.
            DateTime stamp = File.GetLastWriteTimeUtc(filePath);
            string[] lines;
            try { lines = EncodingHelper.ReadAllLines(filePath, out _); }
            catch { Watch(filePath, AbsentOrUnread); throw; }
            Watch(filePath, stamp);
            _loadedFiles.Add(filePath);
            string thisDir = Path.GetDirectoryName(filePath);
            RedSection current = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(line) || line.StartsWith("--"))
                    continue;

                // %THISDIR% is the folder of the file the line is IN, not of the top-level .red.
                if (line.IndexOf("%THISDIR%", StringComparison.OrdinalIgnoreCase) >= 0)
                    line = Regex.Replace(line, "%THISDIR%", (thisDir ?? "").Replace("$", "$$"), RegexOptions.IgnoreCase);

                // Section header: [SectionName]
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = GetOrAddSection(line.Substring(1, line.Length - 2).Trim());
                    continue;
                }

                // Command: {include path}
                if (line.StartsWith("{"))
                {
                    FollowInclude(line, thisDir, depth);
                    continue;
                }

                // Entry: pattern = path1;path2;...
                if (line.Contains("="))
                {
                    if (current == null) current = GetOrAddSection("Common");
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

        /// <summary>The section of that name, created on first use: a name met again ADDS to it.</summary>
        private RedSection GetOrAddSection(string name)
        {
            RedSection section;
            if (!_sections.TryGetValue(name, out section))
            {
                section = new RedSection { Name = name };
                _sections[name] = section;
            }
            return section;
        }

        private void FollowInclude(string line, string thisDir, int depth)
        {
            int close = line.IndexOf('}');
            Match m = Regex.Match(close > 0 ? line.Substring(1, close - 1) : line.Substring(1),
                                  @"^\s*include\s+(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                Skip(line + "  (not an {include} command)", RedIncludeSkip.NotAnInclude);
                return;
            }

            string target = ExpandMacros(m.Groups[1].Value.Trim().Trim('"'));
            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(target) || string.IsNullOrEmpty(thisDir)
                    ? target : Path.Combine(thisDir, target));
            }
            catch
            {
                Skip(line + "  (invalid path: " + target + ")", RedIncludeSkip.InvalidPath);
                return;
            }

            if (_loadedFiles.Exists(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
            {
                // Includes itself, directly or round a loop. A local Clarion110.red with
                // {include %BIN%\clarion110.red} lands here when %BIN% is the local folder.
                Skip(line + "  (already loaded: " + full + ")", RedIncludeSkip.AlreadyLoaded);
                return;
            }
            if (depth + 1 > MaxIncludeDepth)
            {
                Skip(line + "  (nested deeper than " + MaxIncludeDepth + ")", RedIncludeSkip.TooDeep);
                return;
            }
            if (!File.Exists(full))
            {
                Skip(line + "  (file not found: " + full + ")", RedIncludeSkip.FileNotFound);
                Watch(full, AbsentOrUnread);   // its creation later makes the instance stale
                return;
            }

            try { ParseFile(full, depth + 1); }
            catch (Exception ex) { Skip(line + "  (could not read: " + ex.Message + ")", RedIncludeSkip.Unreadable); }
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
                        try
                        {
                            // Redirection relative paths are relative to the project dir, not the IDE's CWD.
                            string candidate = AnchorAt(Path.Combine(entry.ResolvedPath, fileName), baseDir);
                            if (candidate != null && File.Exists(candidate))
                                return candidate;
                        }
                        catch { }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolve a build-related file (e.g. a generated module .clw) through every section in
        /// <see cref="BuildSectionOrder"/>, anchoring relative entries at <paramref name="baseDir"/>.
        /// Returns the first existing match, or null.
        /// </summary>
        public string ResolveForBuild(string fileName, string baseDir)
        {
            return ResolveFrom(fileName, baseDir, BuildSectionOrder);
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

                    // Same relative-path anchoring as ResolveFrom.
                    string candidate = null;
                    try { candidate = AnchorAt(Path.Combine(entry.ResolvedPath, fileName), baseDir); } catch { }
                    if (candidate == null)
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
        /// Like <see cref="GetSearchPaths(string, string)"/> for several extensions at once, in the .red's own entry
        /// order (= precedence): an entry joins when its pattern matches ANY of them. 5a5581e6: templates are
        /// <c>.tpl</c> and <c>.tpw</c>, and a .red may name them as <c>*.tp?</c> or one by one.
        /// </summary>
        public List<string> GetSearchPathsForAny(IEnumerable<string> extensions, string sectionName = "Common")
        {
            // No LINQ: the csc harnesses compile this file against System.dll alone.
            var result = new List<string>();
            if (extensions == null) return result;
            var testNames = new List<string>();
            foreach (string e in extensions)
                if (!string.IsNullOrEmpty(e)) testNames.Add("test" + (e.StartsWith(".") ? e : "." + e));

            RedSection section;
            if (testNames.Count == 0 || !_sections.TryGetValue(sectionName, out section))
                return result;

            foreach (var entry in section.Entries)
            {
                bool matches = false;
                foreach (string t in testNames)
                    if (MatchesPattern(t, entry.Pattern)) { matches = true; break; }
                if (!matches) continue;
                string resolved = entry.ResolvedPath;
                if (!string.IsNullOrEmpty(resolved) && !result.Contains(resolved))
                    result.Add(resolved);
            }
            return result;
        }

        /// <summary>
        /// Anchor a .red path the way <see cref="ResolveFrom"/> does: a relative entry (".", ".\Source", "..\x") hangs
        /// off <paramref name="baseDir"/> (the project dir), not the process CWD (the Clarion bin, in the IDE).
        /// Absolute entries are only normalised. Drive-relative entries are rooted to Path but not absolute: "\x"
        /// takes <paramref name="baseDir"/>'s drive, "C:x" is relative to <paramref name="baseDir"/> when that is on
        /// C: - and with <paramref name="baseDir"/> on another drive it cannot be anchored (null; see
        /// <see cref="UnanchorableReason"/>). Returns null for a path that cannot be made full. With no
        /// <paramref name="baseDir"/>, the process's own CWD and drive apply, as they always did.
        /// </summary>
        public static string AnchorAt(string path, string baseDir)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!string.IsNullOrEmpty(baseDir))
                {
                    if (!Path.IsPathRooted(path))
                        path = Path.Combine(baseDir, path);
                    else if (IsRootRelative(path))
                    {
                        string drive = Path.GetPathRoot(Path.GetFullPath(baseDir));   // "H:\"
                        path = drive.TrimEnd('\\', '/') + "\\" + path.TrimStart('\\', '/');
                    }
                    else if (IsDriveRelative(path))
                    {
                        if (!SameDrive(path, baseDir)) return null;
                        path = Path.Combine(baseDir, path.Substring(2));
                    }
                }
                string full = Path.GetFullPath(path);
                // "x\Source\" and "x\Source" are one folder; keep a drive root's own backslash.
                string root = Path.GetPathRoot(full) ?? "";
                return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
            }
            catch { return null; }
        }

        /// <summary>A path that names its folder on its own: "C:\x" or "\\server\share\x".</summary>
        public static bool IsFullyQualified(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return Path.IsPathRooted(path) && !IsRootRelative(path) && !IsDriveRelative(path); }
            catch { return false; }
        }

        /// <summary>"\x": rooted, but on whichever drive is current (not "\\server\share").</summary>
        private static bool IsRootRelative(string path)
        {
            return path.Length > 0 && (path[0] == '\\' || path[0] == '/')
                && !(path.Length > 1 && (path[1] == '\\' || path[1] == '/'));
        }

        /// <summary>"C:x": a drive letter without a root, relative to that drive's current folder.</summary>
        private static bool IsDriveRelative(string path)
        {
            return path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])
                && !(path.Length > 2 && (path[2] == '\\' || path[2] == '/'));
        }

        private static bool SameDrive(string driveRelative, string baseDir)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(baseDir)) ?? "";
                return root.Length >= 2 && root[1] == ':' && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(driveRelative[0]);
            }
            catch { return false; }
        }

        /// <summary>An entry whose %MACRO% was never expanded: not a real folder, whatever it looks like.</summary>
        public static bool HasUnexpandedMacro(string path)
        {
            return !string.IsNullOrEmpty(path) && Regex.IsMatch(path, @"%[^%\\/]+%");
        }

        /// <summary>
        /// 5a5581e6: why <paramref name="path"/> cannot be anchored at <paramref name="baseDir"/> (an unexpanded
        /// %MACRO%, a drive-relative "C:x" on a drive other than <paramref name="baseDir"/>'s, an invalid path), or
        /// null when <see cref="AnchorAt"/> can.
        /// </summary>
        public static string UnanchorableReason(string path, string baseDir)
        {
            if (string.IsNullOrEmpty(path)) return "empty entry";
            if (HasUnexpandedMacro(path)) return "unexpanded macro";
            if (!string.IsNullOrEmpty(baseDir) && IsDriveRelative(path) && !SameDrive(path, baseDir))
                return "drive-relative entry on another drive than " + baseDir;
            return AnchorAt(path, baseDir) == null ? "invalid path" : null;
        }

        /// <summary>
        /// 5a5581e6: <paramref name="paths"/> anchored at <paramref name="baseDir"/> (<see cref="AnchorAt"/>), order
        /// kept, duplicates dropped - what an agent can Grep without knowing the .red's relative-path rule. Entries
        /// that cannot be anchored (<see cref="UnanchorableReason"/>) are left out; list them with
        /// <see cref="Unanchorable"/>. Null when there is no <paramref name="baseDir"/>: a relative entry would
        /// otherwise silently land on the process CWD.
        /// </summary>
        public static List<string> AnchorAll(IEnumerable<string> paths, string baseDir)
        {
            if (paths == null || string.IsNullOrEmpty(baseDir)) return null;
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in paths)
            {
                if (UnanchorableReason(p, baseDir) != null) continue;
                string full = AnchorAt(p, baseDir);
                if (full != null && seen.Add(full))
                    result.Add(full);
            }
            return result;
        }

        /// <summary>The entries <see cref="AnchorAll"/> leaves out, each as "entry (reason)". Empty when none.</summary>
        public static List<string> Unanchorable(IEnumerable<string> paths, string baseDir)
        {
            var result = new List<string>();
            if (paths == null) return result;
            foreach (string p in paths)
            {
                string why = UnanchorableReason(p, baseDir);
                if (why != null && !result.Contains(p + " (" + why + ")")) result.Add(p + " (" + why + ")");
            }
            return result;
        }

        /// <summary>One existing match for a file: the section and the anchored path.</summary>
        public sealed class RedHit
        {
            public string Section { get; set; }
            public string Path { get; set; }
        }

        /// <summary>
        /// 5a5581e6: the first existing match for the LEAF name <paramref name="fileName"/> in EACH of
        /// <paramref name="sectionNames"/> that has one, in that order. Entries are anchored with
        /// <see cref="AnchorAt"/>; an entry that cannot be anchored is skipped, and a candidate that does not sit
        /// directly in its entry's folder (a name that climbs out with "..\" or is rooted) never matches.
        /// </summary>
        public List<RedHit> FindInSections(string fileName, string baseDir, IEnumerable<string> sectionNames)
        {
            var hits = new List<RedHit>();
            if (string.IsNullOrEmpty(fileName) || sectionNames == null) return hits;
            foreach (string sectionName in sectionNames)
            {
                RedSection section;
                if (string.IsNullOrEmpty(sectionName) || !_sections.TryGetValue(sectionName, out section)) continue;
                foreach (var entry in section.Entries)
                {
                    if (!MatchesPattern(fileName, entry.Pattern)) continue;
                    if (UnanchorableReason(entry.ResolvedPath, baseDir) != null) continue;
                    // No base folder (no solution): a relative entry has nothing to hang off - never the process CWD
                    // (run 2), as AnchorAll already refuses.
                    if (string.IsNullOrEmpty(baseDir) && !IsFullyQualified(entry.ResolvedPath)) continue;
                    string dir = AnchorAt(entry.ResolvedPath, baseDir);
                    if (dir == null) continue;
                    try
                    {
                        string candidate = Path.GetFullPath(Path.Combine(dir, fileName));
                        if (!string.Equals(Path.GetDirectoryName(candidate), dir, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(candidate)) continue;
                        hits.Add(new RedHit { Section = sectionName, Path = candidate });
                        break;
                    }
                    catch { }
                }
            }
            return hits;
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
                    string dir = AnchorAt(entry.ResolvedPath, baseDir);
                    if (dir == null) continue;

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
        /// Internal (GH #204): ClarionBuildTarget's output lookup must match .red entries exactly as the resolver does.
        /// </summary>
        internal static bool MatchesPattern(string fileName, string pattern)
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
