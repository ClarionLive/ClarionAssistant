using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

namespace ClarionAssistant.McpServer
{
    /// <summary>
    /// IWorkspaceContext for a host with no IDE (ticket d051fbd1).
    ///
    /// This is the other half of the seam. In the addin these members hang off the WinForms chat
    /// control and answer "which solution has the developer selected"; here the client says which
    /// solution it means, via --solution or the working directory. Eleven of the registered tools
    /// depend on it — the CodeGraph and solution family, including index_solution, which is what
    /// builds the graph a non-IDE editor would then query.
    ///
    /// LAUNCHED BY AN IDE (--ide-pid), THE SOLUTION FOLLOWS THAT IDE (9e1bd280): whatever it has open now,
    /// published through IdeSolutionRecord, with --solution only the starting value. See SolutionFollower.
    ///
    /// EVERYTHING IS RESOLVED LAZILY AND CACHED. Detecting the Clarion install and parsing a .red
    /// costs real work, and a server that a client launches and then asks only query_docs must not
    /// pay for it. Resolution failures are recorded rather than thrown: "no solution" is a normal
    /// state that the tools already report properly, and killing the process over it would take
    /// the other 46 tools down with it.
    /// </summary>
    internal sealed class StandaloneWorkspace : IWorkspaceContext, IVersionNoteSource, ISolutionSourceInfo, ISolutionScopedWorkspace,
                                                IServerLaunchInfo
    {
        /// <summary>e1df6c2e: why the plugin launcher started this server, or null when it did not (set by Program).</summary>
        public string ServerChosenBy { get; set; }

        /// <summary>Pipeline run 3: the warning when this server is an older Clarion Assistant than it should be, or null (set by Program).</summary>
        public string DowngradeWarning { get; set; }

        /// <summary>The solution as resolved at launch (--solution or the working directory), not followed.</summary>
        public string LaunchSolutionPath { get { return _solutionPath; } }

        private readonly object _lock = new object();

        /// <summary>
        /// The solution as resolved at launch (--solution, or the working directory's one .sln). Since 9e1bd280
        /// only the STARTING value: <see cref="_follower"/> serves the IDE's open solution whenever the
        /// launching IDE publishes one, and this when it never has.
        /// </summary>
        private readonly string _solutionPath;

        /// <summary>
        /// What every member here asks "which solution" (9e1bd280). Built lazily, because the launching IDE's
        /// pid (McpToolRegistry.IdeProcessId) is parsed after this object is resolved.
        /// </summary>
        private SolutionFollower _follower;

        /// <summary>Version name from --clarion-version, or null. Outranks every other tier.</summary>
        private readonly string _versionOverride;

        private ClarionVersionConfig _versionConfig;
        private string _versionNote;

        private bool _redResolved;
        private RedFileService _redFile;

        /// <summary>
        /// How the solution was resolved, and why it wasn't when it wasn't. Written to stderr at
        /// startup: the client's log is the only place a stdio server can explain itself, and
        /// "index_solution says no solution is selected" is a mystery without it.
        /// </summary>
        public string ResolutionNote { get; private set; }

        private StandaloneWorkspace(string solutionPath, string note)
            : this(solutionPath, note, null) { }

        private StandaloneWorkspace(string solutionPath, string note, string versionOverride)
        {
            _solutionPath = solutionPath;
            ResolutionNote = note;
            _versionOverride = string.IsNullOrEmpty(versionOverride) ? null : versionOverride.Trim();
        }

        /// <summary>
        /// Which Clarion was chosen and WHICH TIER decided, in one line for stderr — the same
        /// "[chosen by: ...]" discipline the schema tools already use, and for the same reason:
        /// with several Clarions installed, an answer without its provenance cannot be checked.
        ///
        /// Null until <see cref="CurrentVersionConfig"/> has been asked for, because resolution is
        /// lazy on purpose; call <see cref="ResolveVersionNow"/> if you want it at startup.
        /// </summary>
        public string VersionNote
        {
            get
            {
                lock (_lock)
                {
                    // ONE LINE, always. This note can carry an exception message, and
                    // JavaScriptSerializer's parse errors are multi-line — which split the note
                    // across stderr lines and stripped the "clarion-mcp-server:" prefix off the
                    // remainder, so half the sentence looked like output from something else.
                    // Found by feeding the config file deliberately broken JSON.
                    if (_versionNote == null) return null;
                    return _versionNote.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
                }
            }
        }

        /// <summary>
        /// Force version resolution so <see cref="VersionNote"/> can be reported at startup
        /// rather than at whatever moment the first version-dependent tool happens to run.
        /// The cost is one ClarionProperties.xml parse; the benefit is that the user reads which
        /// Clarion they got BEFORE it has silently shaped an answer.
        /// </summary>
        public void ResolveVersionNow()
        {
            var ignored = CurrentVersionConfig;
        }

        /// <summary>
        /// Resolve the workspace from an explicit path or the working directory.
        ///
        /// AN AMBIGUOUS DIRECTORY RESOLVES TO NOTHING, DELIBERATELY. With several .sln files
        /// present, picking the first would silently index and answer questions about a solution
        /// the user never named — and every answer would look authoritative. Reporting "several,
        /// name one with --solution" costs one flag and cannot mislead.
        /// </summary>
        public static StandaloneWorkspace Resolve(string explicitPath, string workingDirectory)
        {
            return Resolve(explicitPath, workingDirectory, null);
        }

        /// <summary>
        /// As above, plus the --clarion-version name. Kept as a wrapper rather than threaded
        /// through the seven return sites below: solution resolution and version selection are
        /// independent questions, and interleaving them would obscure both.
        /// </summary>
        public static StandaloneWorkspace Resolve(
            string explicitPath, string workingDirectory, string versionOverride)
        {
            var ws = ResolveSolution(explicitPath, workingDirectory);
            if (string.IsNullOrEmpty(versionOverride)) return ws;
            return new StandaloneWorkspace(ws._solutionPath, ws.ResolutionNote, versionOverride);
        }

        private static StandaloneWorkspace ResolveSolution(string explicitPath, string workingDirectory)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                string full;
                try { full = Path.GetFullPath(explicitPath); }
                catch (Exception ex)
                {
                    return new StandaloneWorkspace(null, "--solution '" + explicitPath + "' is not a usable path: " + ex.Message);
                }
                if (!File.Exists(full))
                    return new StandaloneWorkspace(null, "--solution '" + full + "' does not exist.");
                return new StandaloneWorkspace(full, "solution from --solution: " + full);
            }

            string dir = workingDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return new StandaloneWorkspace(null, "no --solution given and the working directory is not readable.");

            string[] found;
            try { found = Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                return new StandaloneWorkspace(null, "could not scan '" + dir + "' for a solution: " + ex.Message);
            }

            if (found.Length == 1)
                return new StandaloneWorkspace(found[0], "solution discovered in the working directory: " + found[0]);

            if (found.Length == 0)
                return new StandaloneWorkspace(null,
                    "no solution: none found in '" + dir + "'. Pass --solution <path.sln> to name one. "
                    + "Tools that do not need a solution (docs, knowledge, LSP, schema, file, search) work regardless.");

            return new StandaloneWorkspace(null,
                "no solution: " + found.Length + " .sln files in '" + dir + "'. Pass --solution <path.sln> to name one, "
                + "rather than have this server guess which you meant.");
        }

        /// <summary>
        /// The followed solution (9e1bd280): the one the launching IDE has open NOW, else --solution while no
        /// IDE record exists, else null - including when the IDE closed its solution. Re-asked on every read
        /// (one file stat while the record is unchanged), so everything keyed on it - the version, the .red,
        /// the CodeGraph db, the index run, the LSP - re-resolves on a switch.
        /// </summary>
        public string CurrentSolutionPath { get { return Follower.CurrentPath; } }

        /// <summary>The followed solution with where it came from and the last switch (get_solution_info).</summary>
        public SolutionSourceSnapshot SolutionSource { get { return Follower.Current(); } }

        /// <summary>The follower itself, for the hosts that must agree with the tools (the LSP, the heartbeat).</summary>
        public SolutionFollower Follower
        {
            get
            {
                lock (_lock)
                {
                    if (_follower == null)
                    {
                        _follower = new SolutionFollower(_solutionPath, () => McpToolRegistry.IdeProcessId);
                        _follower.Changed = (was, now) =>
                        {
                            string line = "[StandaloneWorkspace] followed the IDE's solution: " + (was ?? "(none)")
                                + " -> " + (now ?? "(none - the IDE has no solution open)");
                            LspTrace.Write(line);
                            try { Console.Error.WriteLine("clarion-mcp-server: " + line); } catch { }
                            // Completion's held-open symbol DB connections belong to the old solution - released
                            // here as the addin's OnSolutionChanged does, so its index run is not locked out.
                            try { SymbolIndex.ReleaseAll(); } catch { }
                        };
                    }
                    return _follower;
                }
            }
        }

        /// <summary>
        /// The solution the HOST believes is open. Since 9e1bd280 that is the followed solution, the same value
        /// as <see cref="CurrentSolutionPath"/>: when this server follows an IDE, a null here really does mean
        /// the IDE closed its solution - the staleness the distinction exists to express - and the two members
        /// read one source, so they cannot diverge.
        ///
        /// NEVER NULL WHILE A SOLUTION IS SERVED: build_solution uses this as its ONLY fallback and
        /// never consults CurrentSolutionPath, and get_solution_info reads "host says none, but a
        /// path is cached" as a stale selection. Returning null beside a live --solution (as
        /// IWorkspaceContext once advised) broke both; the follower answers null only when there
        /// really is no solution, and then CurrentSolutionPath is null too.
        /// </summary>
        public string GetHostOpenSolutionPath() { return CurrentSolutionPath; }

        public ClarionVersionConfig CurrentVersionConfig
        {
            get { return ResolveVersionFor(CurrentSolutionPath); }
        }

        /// <summary>
        /// The version for <paramref name="solutionPath"/>, also recorded as the one <see cref="VersionNote"/> describes -
        /// the single place CurrentVersionConfig and RedFileFor resolve it (9e1bd280 run 1).
        /// </summary>
        private ClarionVersionConfig ResolveVersionFor(string solutionPath)
        {
            return ResolveVersionFor(solutionPath, ReadIdeRecord());
        }

        private ClarionVersionConfig ResolveVersionFor(string solutionPath, IdeSolutionRecord.Details rec)
        {
            string note, settings;
            var cfg = VersionConfigFor(solutionPath, rec, out note, out settings);
            lock (_lock) { _versionConfig = cfg; _versionNote = note; _settingsNote = settings; }
            return cfg;
        }

        /// <summary>The launching IDE's record (cached: one stat), or null when not launched by an IDE or none.</summary>
        private static IdeSolutionRecord.Details ReadIdeRecord()
        {
            int? idePid = McpToolRegistry.IdeProcessId;
            if (!idePid.HasValue) return null;
            string recNote;
            return IdeSolutionRecord.ReadDetailsCached(idePid.Value, out recNote);
        }

        /// <summary>
        /// ISolutionScopedWorkspace (9e1bd280 run 1): ONE read of the followed solution and everything keyed on it -
        /// the version from the SAME record read (so the IDE's choice for this solution survives a switch that lands
        /// afterwards), that version's .red for the solution's folder, its library paths and its database. An operation
        /// takes this once and uses nothing else, so a switch mid-operation cannot mix two solutions.
        /// </summary>
        public SolutionScope Snapshot()
        {
            var src = SolutionSource;
            return ScopeOf(src.Path, src.Record, src);
        }

        /// <summary>ISolutionScopedWorkspace: the scope of a solution the caller named (run 2) - not necessarily the followed one.</summary>
        public SolutionScope ScopeFor(string solutionPath)
        {
            // 583abdec review M1 (security): this is the standalone server's door for a CALLER-NAMED solution
            // (index_codegraph's sln_path), and building a scope reads that path (its folder's .red, its version). The
            // path policy judges it first, as index_codegraph itself does before it gets here - so no caller of this
            // method can make the server touch a UNC path (NTLM) or a folder outside the allowed roots.
            string canonical;
            string refusal = McpPathGate.GatePath("index_codegraph", solutionPath, PathAccess.Read, this, out canonical);
            if (refusal != null) throw new UnauthorizedAccessException(refusal);
            return ScopeOf(canonical, ReadIdeRecord(), null);
        }

        /// <summary>Everything keyed on <paramref name="sln"/>, the version decided from the record <paramref name="rec"/>.</summary>
        private SolutionScope ScopeOf(string sln, IdeSolutionRecord.Details rec, SolutionSourceSnapshot src)
        {
            string note, settings;
            var cfg = VersionConfigFor(sln, rec, out note, out settings);
            lock (_lock) { _versionConfig = cfg; _versionNote = note; _settingsNote = settings; }
            var red = RedFileFor(sln, cfg);
            return new SolutionScope
            {
                Source = src,
                SolutionPath = sln,
                Version = cfg,
                VersionNote = note == null ? null : note.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " "),
                SettingsNote = settings,
                Red = red,
                IndexLibraryPaths = LibraryPathsFrom(red),
                DbPath = DbPathFor(sln)
            };
        }

        /// <summary>
        /// The Clarion version for <paramref name="solutionPath"/> and the sentence saying which source chose it
        /// (0ce0b5e2): SolutionVersionResolver's order - --clarion-version, clarion-assistant.json, the IDE's own
        /// choice for this solution (live from the record the launching IDE publishes, else its saved
        /// preferences), then the Clarion tree this server is installed under.
        ///
        /// PER SOLUTION and RE-ASKED, not latched once: the language server can follow a solution other than
        /// --solution (the IDE's, 77aceec5), and the IDE's choice can change while this server runs (Build > Set
        /// Clarion Version, or a solution switch). The answer is cached per solution against the IDE record it was
        /// decided from, so an unchanged record costs one file stat (IdeSolutionRecord.ReadDetailsCached).
        /// </summary>
        public ClarionVersionConfig VersionConfigFor(string solutionPath, out string note)
        {
            string settings;
            return VersionConfigFor(solutionPath, ReadIdeRecord(), out note, out settings);
        }

        /// <summary>As above, decided from the IDE record <paramref name="rec"/> the caller already read (9e1bd280 run 1).</summary>
        private ClarionVersionConfig VersionConfigFor(string solutionPath, IdeSolutionRecord.Details rec, out string note,
                                                      out string settingsNote)
        {
            settingsNote = null;
            lock (_lock)
            {
                if (!_hostResolved)
                {
                    _hostResolved = true;
                    try
                    {
                        // Detects for the Clarion tree this server is installed under (GH #247). NULL when this
                        // server is not inside a Clarion tree: only a NAMED version then resolves.
                        _hostInfo = ClarionVersionService.Detect();
                        _hostRoot = ClarionVersionService.InstalledClarionRoot();
                    }
                    catch (Exception ex) { _hostError = ex.Message; }
                }
                if (_hostError != null)
                {
                    note = "no Clarion version: resolution failed - " + _hostError;
                    return null;
                }

                // e1df6c2e: WHICH SETTINGS FOLDER, decided first - it is what the version is looked up in. Live IDEs on
                // this solution: the launching one when its record is about it, else any (the plugin's server has no
                // --ide-pid). Read before the cache check, because the folder is part of what the answer depends on.
                var pin = SolutionClarionVersion.ReadPin(solutionPath);
                var live = LiveRecordsFor(solutionPath, rec);
                var dirChoice = StandaloneConfigDir.Resolve(live, _configDirArg, _configDirEnv, pin,
                    _baseDir, null, null);

                string recordKey = (rec == null ? "-" : (rec.Solution + "|" + rec.VersionChoice + "|" + rec.ConfigDir))
                    + "|" + dirChoice.Source + "|" + dirChoice.Path + "|" + dirChoice.Error;
                string cacheKey = (solutionPath ?? "").ToLowerInvariant();

                CachedVersion cached;
                if (_versionCache.TryGetValue(cacheKey, out cached) && cached.RecordKey == recordKey)
                {
                    note = cached.Note;
                    settingsNote = cached.SettingsNote;
                    return cached.Config;
                }

                if (dirChoice.Error != null)
                {
                    // Named but unusable: no version at all, said so - never the default folder in its place.
                    note = "no Clarion version: " + dirChoice.Error;
                    settingsNote = dirChoice.Error;
                    _versionCache[cacheKey] = new CachedVersion { RecordKey = recordKey, Config = null, Note = note, SettingsNote = settingsNote };
                    return null;
                }

                SolutionVersionChoice choice;
                try
                {
                    var info = _hostInfo;
                    Func<string, ClarionVersionConfig> findNamed;
                    string prefsDir;
                    if (dirChoice.Path != null)
                    {
                        // The relocated folder IS this Clarion's settings: detect from it, look names up in it and
                        // nowhere else (a name it does not list is "matches nothing", not another folder's copy).
                        info = ClarionVersionService.DetectInConfigDir(dirChoice.Path, _hostRoot);
                        var inDir = info;
                        findNamed = name => FindIn(inDir, name);
                        prefsDir = Path.Combine(dirChoice.Path, "preferences");
                    }
                    else
                    {
                        // Every settings folder is searched, since a NAMED version needs no host Clarion to pick
                        // the folder (#247); the copy in that entry's OWN folder beats this Clarion's (#244).
                        var host = info;
                        findNamed = name => ClarionVersionService.FindNamedVersion(host, name);
                        prefsDir = info != null ? IdeSolutionPreferences.PreferencesDirFor(info.PropertiesXmlPath) : null;
                    }
                    choice = SolutionVersionResolver.Resolve(new SolutionVersionInputs
                    {
                        SolutionPath = solutionPath,
                        CommandLineVersion = _versionOverride,
                        PinnedVersion = pin.Version,
                        PinNote = pin.Note,
                        IdeRecord = rec,
                        PreferencesDir = prefsDir,
                        HostInfo = info,
                        HostRoot = _hostRoot,
                        FindNamed = findNamed
                    });
                }
                catch (Exception ex)
                {
                    note = "no Clarion version: resolution failed - " + ex.Message;
                    settingsNote = dirChoice.Note;
                    _versionCache[cacheKey] = new CachedVersion { RecordKey = recordKey, Config = null, Note = note, SettingsNote = settingsNote };
                    return null;
                }

                note = OneLine(choice.Note);
                settingsNote = OneLine(dirChoice.Note);
                if (choice.Config != null && !string.IsNullOrEmpty(choice.Config.SourcePropertiesXml))
                    settingsNote = "read " + choice.Config.SourcePropertiesXml + " - " + settingsNote;
                string was = cached != null && cached.Config != null ? cached.Config.Name : null;
                string now = choice.Config != null ? choice.Config.Name : null;
                if (cached != null && !string.Equals(was, now, StringComparison.Ordinal))
                    LspTrace.Write("[StandaloneWorkspace] Clarion version for " + (solutionPath ?? "(no solution)")
                        + " changed: " + note);
                _versionCache[cacheKey] = new CachedVersion { RecordKey = recordKey, Config = choice.Config, Note = note, SettingsNote = settingsNote };
                return choice.Config;
            }
        }

        private static string OneLine(string s)
        {
            return (s ?? "").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>Exact, then case-insensitive, as ClarionVersionService.FindIn - over a folder's own entries.</summary>
        private static ClarionVersionConfig FindIn(ClarionVersionInfo info, string name)
        {
            if (info == null || info.Versions == null || string.IsNullOrEmpty(name)) return null;
            return info.Versions.Find(v => v != null && v.Name == name)
                ?? info.Versions.Find(v => v != null && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// e1df6c2e: the live IDE records for <paramref name="solutionPath"/> - the launching IDE's when it is about this
        /// solution, else every live IDE's on it (scanned at most every few seconds per solution; one directory
        /// listing). Empty when none.
        /// </summary>
        private List<IdeSolutionRecord.Details> LiveRecordsFor(string solutionPath, IdeSolutionRecord.Details rec)
        {
            var list = new List<IdeSolutionRecord.Details>();
            if (string.IsNullOrEmpty(solutionPath)) return list;
            if (rec != null && SolutionFollower.SamePath(rec.Solution, solutionPath))
            {
                list.Add(rec);
                return list;
            }
            string key = solutionPath.ToLowerInvariant();
            CachedLive c;
            if (_liveCache.TryGetValue(key, out c) && (DateTime.UtcNow - c.At).TotalSeconds < 3) return c.Records;
            var found = IdeSolutionRecord.FindLiveForSolution(solutionPath);
            _liveCache[key] = new CachedLive { At = DateTime.UtcNow, Records = found };
            return found;
        }

        private sealed class CachedLive
        {
            public DateTime At;
            public List<IdeSolutionRecord.Details> Records;
        }

        private readonly Dictionary<string, CachedLive> _liveCache = new Dictionary<string, CachedLive>();

        /// <summary>--clarion-config-dir, or null (e1df6c2e).</summary>
        private string _configDirArg;
        /// <summary>CLARION_CONFIG_DIR, or null (e1df6c2e).</summary>
        private string _configDirEnv;
        /// <summary>What a relative --clarion-config-dir / CLARION_CONFIG_DIR is relative to.</summary>
        private string _baseDir;

        /// <summary>
        /// e1df6c2e: the settings folder named on the command line (--clarion-config-dir) and in the environment
        /// (CLARION_CONFIG_DIR), relative ones anchored at <paramref name="baseDir"/>. Set before serving, by Program.
        /// </summary>
        public void SetConfigDirSources(string commandLine, string environment, string baseDir)
        {
            lock (_lock)
            {
                _configDirArg = string.IsNullOrWhiteSpace(commandLine) ? null : commandLine;
                _configDirEnv = string.IsNullOrWhiteSpace(environment) ? null : environment;
                _baseDir = baseDir;
                _versionCache.Clear();
            }
        }

        /// <summary>
        /// e1df6c2e: which ClarionProperties.xml the current version came from and why that settings folder - the
        /// line printed at startup and returned as get_solution_info's settingsChosenBy. Null until resolved.
        /// </summary>
        public string SettingsNote
        {
            get { lock (_lock) { return _settingsNote; } }
        }

        private string _settingsNote;

        private sealed class CachedVersion
        {
            public string RecordKey;
            public ClarionVersionConfig Config;
            public string Note;
            public string SettingsNote;
        }

        private bool _hostResolved;
        private ClarionVersionInfo _hostInfo;
        private string _hostRoot;
        private string _hostError;
        private readonly Dictionary<string, CachedVersion> _versionCache = new Dictionary<string, CachedVersion>();

        /// <summary>
        /// Root of the Clarion installation, from the detected version config.
        ///
        /// NOT AppDomain.BaseDirectory, which is what the interface doc offers as the portable
        /// fallback: that is this .exe's own folder, and this .exe does not live in the Clarion
        /// tree. Returning it would hand the redirection and library-path logic a confidently
        /// wrong root. Null is the honest answer when Clarion cannot be found.
        /// </summary>
        public string GetClarionInstallPath()
        {
            var cfg = CurrentVersionConfig;
            return cfg != null ? cfg.RootPath : null;
        }

        /// <summary>The followed solution's .red - <see cref="RedFileFor"/> of one read of it.</summary>
        public RedFileService RedFile
        {
            get { return RedFileFor(CurrentSolutionPath); }
        }

        /// <summary>
        /// The .red for <paramref name="solutionPath"/>: its folder's own version-named one, else its version's
        /// (9e1bd280 run 1). For a solution the caller snapshotted, so an operation derives its .red from the SAME
        /// solution as everything else it uses. Cached for the last solution asked; a different one replaces it.
        /// </summary>
        public RedFileService RedFileFor(string solutionPath)
        {
            return RedFileFor(solutionPath, ResolveVersionFor(solutionPath));
        }

        /// <summary>The .red of <paramref name="solutionPath"/> on version <paramref name="cfg"/> (already resolved).</summary>
        private RedFileService RedFileFor(string solutionPath, ClarionVersionConfig cfg)
        {
            // Re-loaded when the version moves (0ce0b5e2): the IDE's choice for the solution can change
            // while this server runs, and the .red is that version's.
            // 9e1bd280: and when the SOLUTION moves. Keyed on the version alone, a switch between two
            // solutions on one Clarion kept the first one's .red - a solution-local .red is per folder.
            string name = cfg != null ? cfg.Name : null;
            lock (_lock)
            {
                // GH #261: also when the .red (or a file it {include}s) changed on disk since it was read -
                // an edit to it used to change nothing until this server restarted.
                if (!_redResolved || !string.Equals(name, _redVersionName, StringComparison.Ordinal)
                    || !SolutionFollower.SamePath(solutionPath, _redSolution)
                    || (_redFile != null && _redFile.IsStale()))
                {
                    _redResolved = true;
                    _redVersionName = name;
                    _redSolution = solutionPath;
                    _redFile = LoadRedFile(cfg, solutionPath);
                }
                return _redFile;
            }
        }

        private string _redVersionName;
        private string _redSolution;

        /// <summary>
        /// The addin distinguishes these because it resolves one per ACTIVE PROJECT, which can
        /// differ from the solution-level file. With no IDE there is no active project, so there
        /// is exactly one redirection context and both members answer it. Kept as two members
        /// rather than collapsed, because the interface's contract is what several tools resolve
        /// their search paths against.
        /// </summary>
        public RedFileService ActiveRedFileService { get { return RedFile; } }

        private RedFileService LoadRedFile(ClarionVersionConfig cfg, string solutionPath)
        {
            if (cfg == null) return null;

            var svc = new RedFileService();
            string projectDir = null;
            if (!string.IsNullOrEmpty(solutionPath))
            {
                try { projectDir = Path.GetDirectoryName(solutionPath); }
                catch { projectDir = null; }
            }

            try
            {
                // Same call the addin makes (AssistantChatControl.LoadRedFile). A null projectDir
                // is fine and means "version-level .red only" — correct when no solution is named.
                svc.LoadForProject(projectDir, cfg);
            }
            catch { return null; }

            return svc;
        }

        /// <summary>
        /// Same formula as the addin: the .codegraph.db sits beside the .sln and is named after it.
        /// Duplicated as an expression rather than shared, because the addin's copy is a property
        /// on a WinForms control; if a third host ever appears this is the one to hoist.
        /// </summary>
        public string CurrentDbPath
        {
            get
            {
                // The followed solution's (9e1bd280), read once so the folder and the name agree.
                return DbPathFor(CurrentSolutionPath);
            }
        }

        /// <summary>The CodeGraph database beside <paramref name="solutionPath"/>, or null.</summary>
        private static string DbPathFor(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath)) return null;
            return Path.Combine(Path.GetDirectoryName(solutionPath),
                Path.GetFileNameWithoutExtension(solutionPath) + ".codegraph.db");
        }

        public List<string> BuildIndexLibraryPaths()
        {
            return LibraryPathsFrom(RedFile);
        }

        private static List<string> LibraryPathsFrom(RedFileService red)
        {
            if (red == null) return null;
            try
            {
                var incPaths = red.GetSearchPaths(".inc");
                return incPaths != null && incPaths.Count > 0 ? incPaths : null;
            }
            catch { return null; }
        }

        public void RunIndex(bool incremental)
        {
            RunIndex(incremental, null, null);
        }

        /// <summary>
        /// Index the solution into its CodeGraph database.
        ///
        /// THE STREAMING FORM MUST RUN ON A WORKER THREAD, and I had this wrong first time. I
        /// reasoned that the stdio transport is serial so a thread "would buy nothing", and ran
        /// everything inline. Measured on a 3.2-second index: ONE progress frame, delivered at
        /// 3450ms — after the run had already finished. The streaming caller
        /// (McpToolRegistry's index_solution StreamingHandler) does not merely receive events, it
        /// DRAINS A QUEUE CONCURRENTLY with the producer. Run the producer inline and it fills
        /// that bounded queue and completes before the loop starts, so a client watching a
        /// multi-minute index on a real solution sits silent throughout and then gets one frame.
        /// Events past the queue's 256 capacity are dropped outright.
        ///
        /// The fire-and-forget form stays synchronous, deliberately: this transport handles
        /// requests serially, so returning early would let the next request query a half-built
        /// database. NOTE the registry's message for that path says "index started", which
        /// understates what happened here — it has finished by the time the caller sees it. A
        /// client wanting live progress should send a progressToken and get the streaming path.
        ///
        /// onCompleted is invoked EXACTLY ONCE on every path — success, failure, refusal, and the
        /// no-solution case. The MCP streaming tool waits on it; missing one strands the caller
        /// until its watchdog fires.
        /// </summary>
        public void RunIndex(bool incremental,
                             Action<ClarionCodeGraph.Graph.IndexProgressEvent> onProgress,
                             Action<string> onCompleted)
        {
            Action<string> complete = summary =>
            {
                if (onCompleted != null) { try { onCompleted(summary); } catch { } }
            };

            // ONE read of the followed solution and everything keyed on it (9e1bd280; run 1: the .red, library
            // paths and version too): the run indexes the solution it started on, into that solution's database,
            // with that solution's .red, even if the IDE switches mid-run.
            var scope = Snapshot();
            var src = scope.Source;
            string slnPath = scope.SolutionPath;
            if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath))
            {
                complete("Error: no solution is selected. " + (src.Source == SolutionSourceSnapshot.None
                    ? src.Note : (ResolutionNote ?? "Pass --solution <path.sln>.")));
                return;
            }

            string dbPath = scope.DbPath;

            // The same gate the addin's entry points use. It now guards ACROSS PROCESSES as well
            // as within one, which this host is the reason for: the addin and this server can hold
            // the same .codegraph.db, and a full index clears it up front, so an overlapping pair
            // would destroy each other's work.
            //
            // Claimed HERE, on the calling thread, before any thread is started. Claiming it
            // inside the worker would let a second call slip past while the first was still
            // starting up — the gate would be doing nothing precisely when two runs are most
            // likely, which is back-to-back requests.
            string indexHolder;
            if (!IndexRunGate.TryEnter(dbPath, out indexHolder))
            {
                complete("Error: an index run is already in progress for this database, held by "
                         + indexHolder + ".");
                return;
            }

            // Streaming caller: it drains a queue concurrently, so the producer has to be
            // concurrent too. See the remarks above — running inline here delivers no live
            // progress at all, measured.
            if (onProgress != null)
            {
                var worker = new System.Threading.Thread(() => IndexHeld(scope, incremental, onProgress, complete));
                worker.IsBackground = true;
                worker.Name = "clarion-mcp-index";
                worker.Start();
                return;
            }

            IndexHeld(scope, incremental, null, complete);
        }

        /// <summary>
        /// Do the indexing. The caller ALREADY HOLDS the IndexRunGate claim for the scope's database and
        /// this method always releases it — including when it runs on a worker thread, which is why
        /// the release is in a finally here rather than at the call site.
        /// </summary>
        private void IndexHeld(SolutionScope scope,
                               bool incremental,
                               Action<ClarionCodeGraph.Graph.IndexProgressEvent> onProgress,
                               Action<string> complete)
        {
            string slnPath = scope.SolutionPath;
            string dbPath = scope.DbPath;
            IndexRunLog runLog = null;
            try
            {
                try { runLog = new IndexRunLog(Path.GetFileNameWithoutExtension(slnPath)); }
                catch { runLog = null; }

                // 9e1bd280 run 1: the .red and library paths of the scope this run SNAPSHOTTED, never the followed
                // solution re-read here - a switch between RunIndex and this line indexed A's db with B's .red.
                IndexRunGate.PauseBeforeSetUpForTest();
                var activeRed = scope.Red;
                var libPaths = scope.IndexLibraryPaths;
                if (runLog != null)
                {
                    try { runLog.WriteLine("Redirection file: " + (activeRed != null ? activeRed.RedFilePath : "(none)") + " (for " + slnPath + ")"); }
                    catch { }
                }

                SymbolIndex.Release(dbPath);   // completion's read-only handle (1c685f2e) - never across a write open
                {
                    // 583abdec: the same 64-bit child process the addin uses. This server is 32-bit too (it shares the
                    // addin's x86 SQLite), so a large solution had the same address-space ceiling here; and one
                    // indexing path for every host means one behaviour to test.
                    var indexer = new OutOfProcessIndexer();
                    if (runLog != null) { try { runLog.WriteLine("Indexer process: " + (indexer.ExePath ?? "(unknown)")); } catch { } }
                    indexer.OnProgress += msg =>
                    {
                        if (runLog != null) { try { runLog.WriteLine(msg); } catch { } }
                    };
                    if (onProgress != null)
                    {
                        indexer.OnProgressEvent += ev =>
                        {
                            try { onProgress(ev); } catch { }
                        };
                    }

                    var result = indexer.Run(new IndexRunRequest
                    {
                        SlnPath = slnPath,
                        DbPath = dbPath,
                        Incremental = incremental,
                        Red = activeRed,
                        BuildConfiguration = McpToolRegistry.BuildConfigurationForIndex(slnPath), // GH #245
                        LibraryPaths = libPaths
                    });

                    // #244 item 3: a run that resolved little of the solution says so (status + coverage).
                    complete(string.Format(
                        "{7} Indexed {0}: {1} projects, {2} files, {3} symbols, {4} relationships in {5}ms. Database: {6}",
                        Path.GetFileName(slnPath), result.ProjectCount, result.FileCount,
                        result.SymbolCount, result.RelationshipCount, result.DurationMs, dbPath,
                        result.Status == ClarionCodeGraph.Graph.IndexResult.StatusOk ? "" :
                            (result.IsPartial ? "PARTIAL INDEX." : "Indexed with warnings."))
                        .TrimStart()
                        // ef2fd2a9: an incremental request on an older database ran full - say so.
                        + "\n  Mode: " + result.ModeText
                        + "\n" + result.CoverageText()
                        + (indexer.FallbackWarning != null ? "\n  " + indexer.FallbackWarning : ""));
                }
            }
            catch (Exception ex)
            {
                // 583abdec review L3: the full message to the run log; the MCP caller gets a short reply naming it.
                if (runLog != null) { try { runLog.WriteLine("FAILED: " + ex.Message); } catch { } }
                complete(OutOfProcessIndexer.McpFailureReply(ex, runLog != null ? runLog.LogPath : null));
            }
            finally
            {
                if (runLog != null) { try { runLog.Dispose(); } catch { } }
                IndexRunGate.Exit(dbPath);
            }
        }
    }
}
