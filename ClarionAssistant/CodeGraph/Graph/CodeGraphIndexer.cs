using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ClarionCodeGraph.Parsing;
using ClarionCodeGraph.Parsing.Models;

namespace ClarionCodeGraph.Graph
{
    /// <summary>
    /// Orchestrates the full indexing pipeline:
    /// Solution → Projects → Source files → Parse → Store in database.
    /// </summary>
    public class CodeGraphIndexer
    {
        private readonly CodeGraphDatabase _db;
        private readonly SolutionParser _slnParser;
        private readonly ProjectParser _projParser;
        private SourceResolver _resolver;
        private readonly ClarionParser _clarionParser;

        /// <summary>
        /// The ACTIVE redirection service to resolve source files with, supplied by the host
        /// (the IDE's loaded .red — version-level file plus any local project override). When
        /// null, IndexSolution falls back to probing the solution directory for a *.red, which
        /// only works for solutions that keep their .red beside the .sln — many keep it in the
        /// Clarion bin folder instead (e.g. C:\Clarion10v8\bin\Clarion10v61.red), where the
        /// probe never looks. Hosts that know the real .red MUST inject it here.
        /// </summary>
        public ClarionAssistant.Services.RedFileService RedService { get; set; }

        public event Action<string> OnProgress;

        public CodeGraphIndexer(CodeGraphDatabase db)
        {
            _db = db;
            _slnParser = new SolutionParser();
            _projParser = new ProjectParser();
            _resolver = new SourceResolver();
            _clarionParser = new ClarionParser();
        }

        /// <summary>
        /// Full re-index: wipes everything and re-parses all projects.
        /// </summary>
        public IndexResult IndexSolution(string slnPath)
        {
            return IndexSolution(slnPath, false);
        }

        /// <summary>
        /// Index a solution. If incremental=true, only re-parses projects with
        /// modified source files since the last index.
        /// </summary>
        public IndexResult IndexSolution(string slnPath, bool incremental, List<string> libraryPaths = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new IndexResult { SlnPath = slnPath };

            // Step 1: Parse .sln for projects
            ReportProgress("Parsing solution file...");
            var projects = _slnParser.Parse(slnPath);
            result.ProjectCount = projects.Count;

            // Redirection for SourceResolver (files in Compile\, Classes\, SharedLibsrc etc.):
            // prefer the host-injected ACTIVE .red; fall back to probing the solution dir.
            string slnDir = Path.GetDirectoryName(slnPath);
            var red = RedService;
            if (red != null)
                ReportProgress(string.Format("Using redirection file: {0} (host-supplied)",
                    string.IsNullOrEmpty(red.RedFilePath) ? "(in-memory)" : Path.GetFileName(red.RedFilePath)));
            else
                red = TryLoadRedFile(slnDir);
            if (red == null)
                ReportProgress("No redirection file in effect — resolution limited to .\\source, project root, and explicit search paths.");
            _resolver = new SourceResolver(red);

            // For full re-index, wipe everything and start fresh
            if (!incremental)
            {
                ReportProgress("Full re-index: clearing existing data...");
                _db.ClearAll();
            }

            ReportProgress(string.Format("Found {0} projects", projects.Count));

            // Step 2: Insert/update projects and build GUID → ID map
            var guidToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var projectIds = new Dictionary<string, int>(); // name → id

            using (var txn = _db.BeginTransaction())
            {
                foreach (var proj in projects)
                {
                    if (File.Exists(proj.CwprojPath))
                    {
                        var projResult = _projParser.Parse(proj.CwprojPath);
                        proj.OutputType = projResult.OutputType;
                    }

                    if (incremental)
                    {
                        // Reuse existing project row if it exists
                        int existingId = _db.FindProjectIdByName(proj.Name);
                        if (existingId >= 0)
                        {
                            proj.Id = existingId;
                        }
                        else
                        {
                            proj.Id = _db.InsertProject(proj);
                        }
                    }
                    else
                    {
                        proj.Id = _db.InsertProject(proj);
                    }

                    guidToId[proj.Guid] = proj.Id;
                    projectIds[proj.Name] = proj.Id;
                }

                // Insert project dependencies (skip if incremental — they don't change)
                if (!incremental)
                {
                    foreach (var proj in projects)
                    {
                        foreach (string depGuid in proj.DependencyGuids)
                        {
                            int depId;
                            if (guidToId.TryGetValue(depGuid, out depId))
                            {
                                _db.InsertProjectDependency(proj.Id, depId);
                            }
                        }
                    }
                }

                txn.Commit();
            }

            // Step 3: Resolve source files for each project
            var mainFiles = new Dictionary<int, string>();
            var memberFiles = new Dictionary<int, List<ResolvedFile>>();
            var incFiles = new Dictionary<int, List<ResolvedFile>>();
            var changedProjects = new HashSet<int>(); // projects that need re-parsing
            var projectDirs = new Dictionary<int, string>();
            var discoveredIncludeNames = new Dictionary<int, HashSet<string>>();
            var allResolved = new Dictionary<int, List<ResolvedFile>>(); // per-file outcome audit
            var mainMapSymCount = new Dictionary<int, int>(); // Pass 1 MAP symbol count per main file
            int unresolvedCount = 0;

            foreach (var proj in projects)
            {
                if (!File.Exists(proj.CwprojPath))
                {
                    ReportProgress(string.Format("Skipping {0} — .cwproj not found", proj.Name));
                    continue;
                }

                string projectDir = Path.GetDirectoryName(proj.CwprojPath);
                projectDirs[proj.Id] = projectDir;
                var projResult = _projParser.Parse(proj.CwprojPath);
                var resolved = _resolver.Resolve(projectDir, projResult.SourceFiles, libraryPaths);

                var members = new List<ResolvedFile>();
                var includes = new List<ResolvedFile>();

                foreach (var file in resolved)
                {
                    if (!file.Found) continue;
                    result.FileCount++;

                    if (file.FileName.EndsWith(".inc", StringComparison.OrdinalIgnoreCase))
                    {
                        includes.Add(file);
                        continue;
                    }

                    if (IsMainFile(file.FullPath))
                        mainFiles[proj.Id] = file.FullPath;
                    else
                        members.Add(file);
                }

                memberFiles[proj.Id] = members;
                incFiles[proj.Id] = includes;
                allResolved[proj.Id] = resolved;

                // Check if this project has changed since last index
                if (incremental)
                {
                    string lastIndexedStr = _db.GetMetadata("project_indexed:" + proj.Id);
                    if (string.IsNullOrEmpty(lastIndexedStr))
                    {
                        changedProjects.Add(proj.Id);
                    }
                    else
                    {
                        DateTime lastIndexed;
                        if (!DateTime.TryParse(lastIndexedStr, out lastIndexed))
                        {
                            changedProjects.Add(proj.Id);
                        }
                        else if (ProjectHasChanges(resolved, lastIndexed))
                        {
                            changedProjects.Add(proj.Id);
                        }
                    }
                }
                else
                {
                    changedProjects.Add(proj.Id);
                }
            }

            if (incremental)
            {
                ReportProgress(string.Format("{0} of {1} projects have changes",
                    changedProjects.Count, projects.Count));

                if (changedProjects.Count == 0)
                {
                    sw.Stop();
                    result.DurationMs = sw.ElapsedMilliseconds;
                    ReportProgress("No changes detected — index is up to date.");
                    return result;
                }

                // Clear symbols for changed projects only
                using (var txn = _db.BeginTransaction())
                {
                    foreach (int pid in changedProjects)
                    {
                        _db.ClearProject(pid);
                    }
                    txn.Commit();
                }
            }

            // Per-file outcome audit (ticket d1a0aea6): reset the changed projects' rows and
            // record every .cwproj-listed file that failed to resolve — previously those files
            // simply left no trace, indistinguishable from files that parsed to zero symbols.
            // Parsed outcomes are recorded at each parse site below. Unchanged incremental
            // projects keep their previous rows (their files were not re-examined).
            using (var txn = _db.BeginTransaction())
            {
                foreach (int pid in changedProjects)
                {
                    _db.ClearIndexedFiles(pid);
                    List<ResolvedFile> resolvedList;
                    if (!allResolved.TryGetValue(pid, out resolvedList)) continue;
                    foreach (var file in resolvedList)
                    {
                        if (file.Found) continue;
                        _db.InsertIndexedFile(pid, file.FileName, null, "unresolved", 0, "resolve");
                        unresolvedCount++;
                    }
                }
                txn.Commit();
            }

            // Pass 1: Parse main files and .inc files for changed projects
            ReportProgress("Pass 1: Parsing MAP declarations...");
            using (var txn = _db.BeginTransaction())
            {
                foreach (var kvp in mainFiles)
                {
                    int projectId = kvp.Key;
                    if (!changedProjects.Contains(projectId)) continue;

                    string mainFile = kvp.Value;
                    ReportProgress(string.Format("  Parsing MAP: {0}", Path.GetFileName(mainFile)));
                    var parseResult = _clarionParser.ParseMainFile(mainFile, projectId);

                    foreach (var sym in parseResult.Symbols)
                    {
                        long symId = _db.InsertSymbol(sym);
                        sym.Id = symId;

                        if (sym.Type == "include")
                        {
                            HashSet<string> names;
                            if (!discoveredIncludeNames.TryGetValue(projectId, out names))
                            {
                                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                discoveredIncludeNames[projectId] = names;
                            }
                            names.Add(sym.Name);
                        }
                    }
                    result.SymbolCount += parseResult.Symbols.Count;
                    mainMapSymCount[projectId] = parseResult.Symbols.Count;
                }

                foreach (var proj in projects)
                {
                    if (!changedProjects.Contains(proj.Id)) continue;

                    List<ResolvedFile> incs;
                    if (!incFiles.TryGetValue(proj.Id, out incs)) continue;

                    foreach (var file in incs)
                    {
                        var parseResult = _clarionParser.ParseIncFile(file.FullPath, proj.Id);
                        foreach (var sym in parseResult.Symbols)
                        {
                            long symId = _db.InsertSymbol(sym);
                            sym.Id = symId;
                        }
                        result.SymbolCount += parseResult.Symbols.Count;
                        _db.InsertIndexedFile(proj.Id, file.FileName, file.FullPath,
                            parseResult.Symbols.Count > 0 ? "resolved_parsed" : "resolved_no_symbols",
                            parseResult.Symbols.Count, "pass1-inc");
                    }
                }

                txn.Commit();
            }

            // Pass 1b: Index library .inc files from --lib-paths
            if (libraryPaths != null && libraryPaths.Count > 0)
            {
                // Collect already-indexed .inc paths for dedup
                var indexedIncPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in incFiles)
                {
                    foreach (var f in kvp.Value)
                    {
                        if (f.Found)
                            indexedIncPaths.Add(Path.GetFullPath(f.FullPath));
                    }
                }

                // Build a hash of library paths to detect changes for incremental
                string libPathHash = string.Join(";", libraryPaths).ToUpperInvariant();
                string storedLibHash = _db.GetMetadata("lib_paths_hash");
                bool libsChanged = !incremental || storedLibHash != libPathHash;

                // Create or reuse __Libraries__ pseudo-project
                int libProjectId;
                if (incremental)
                {
                    libProjectId = _db.FindProjectIdByName("__Libraries__");
                    if (libProjectId < 0)
                    {
                        libsChanged = true; // first time — must index
                        var libProj = new SolutionProject
                        {
                            Name = "__Libraries__",
                            Guid = "{00000000-0000-0000-0000-000000000000}",
                            OutputType = "Library",
                            SlnPath = slnPath
                        };
                        libProjectId = _db.InsertProject(libProj);
                    }
                    else if (libsChanged)
                    {
                        _db.ClearProject(libProjectId);
                    }
                }
                else
                {
                    var libProj = new SolutionProject
                    {
                        Name = "__Libraries__",
                        Guid = "{00000000-0000-0000-0000-000000000000}",
                        OutputType = "Library",
                        SlnPath = slnPath
                    };
                    libProjectId = _db.InsertProject(libProj);
                    libsChanged = true;
                }

                if (libsChanged)
                {
                    int libFileCount = 0;
                    int libSymCount = 0;

                    using (var txn = _db.BeginTransaction())
                    {
                        _db.ClearIndexedFiles(libProjectId);
                        foreach (string libDir in libraryPaths)
                        {
                            if (!Directory.Exists(libDir))
                            {
                                ReportProgress(string.Format("  Library path not found: {0}", libDir));
                                continue;
                            }

                            ReportProgress(string.Format("  Scanning library: {0}", libDir));
                            string[] libIncFiles;
                            try
                            {
                                libIncFiles = Directory.GetFiles(libDir, "*.inc", SearchOption.TopDirectoryOnly);
                            }
                            catch (Exception ex)
                            {
                                ReportProgress(string.Format("  Error scanning {0}: {1}", libDir, ex.Message));
                                continue;
                            }

                            foreach (string libIncPath in libIncFiles)
                            {
                                string fullPath = Path.GetFullPath(libIncPath);
                                if (!indexedIncPaths.Add(fullPath))
                                    continue; // already indexed (from project or duplicate lib path casing)

                                var parseResult = _clarionParser.ParseIncFile(fullPath, libProjectId);
                                if (parseResult.Symbols.Count > 0)
                                {
                                    foreach (var sym in parseResult.Symbols)
                                    {
                                        long symId = _db.InsertSymbol(sym);
                                        sym.Id = symId;
                                    }
                                    libSymCount += parseResult.Symbols.Count;
                                }
                                libFileCount++;
                                _db.InsertIndexedFile(libProjectId, Path.GetFileName(fullPath), fullPath,
                                    parseResult.Symbols.Count > 0 ? "resolved_parsed" : "resolved_no_symbols",
                                    parseResult.Symbols.Count, "pass1b-library");
                            }
                        }

                        txn.Commit();
                    }

                    _db.SetMetadata("lib_paths_hash", libPathHash);
                    result.FileCount += libFileCount;
                    result.SymbolCount += libSymCount;
                    ReportProgress(string.Format("  Library indexing: {0} files, {1} symbols", libFileCount, libSymCount));
                }
                else
                {
                    ReportProgress("  Library paths unchanged — skipping library re-index.");
                }
            }

            // Pass 2: Parse member files for changed projects
            ReportProgress("Pass 2: Parsing member files...");
            using (var txn = _db.BeginTransaction())
            {
                foreach (var proj in projects)
                {
                    if (!changedProjects.Contains(proj.Id)) continue;

                    List<ResolvedFile> members;
                    if (!memberFiles.TryGetValue(proj.Id, out members)) continue;

                    foreach (var file in members)
                    {
                        var parseResult = _clarionParser.ParseMemberFile(file.FullPath, proj.Id, null);
                        foreach (var sym in parseResult.Symbols)
                        {
                            long symId = _db.InsertSymbol(sym);
                            sym.Id = symId;

                            if (sym.Type == "include")
                            {
                                HashSet<string> names;
                                if (!discoveredIncludeNames.TryGetValue(proj.Id, out names))
                                {
                                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    discoveredIncludeNames[proj.Id] = names;
                                }
                                names.Add(sym.Name);
                            }
                        }
                        result.SymbolCount += parseResult.Symbols.Count;
                        _db.InsertIndexedFile(proj.Id, file.FileName, file.FullPath,
                            parseResult.Symbols.Count > 0 ? "resolved_parsed" : "resolved_no_symbols",
                            parseResult.Symbols.Count, "pass2-member");
                    }

                    // The main PROGRAM file: run the WHOLE file through the member-file parser
                    // (startLine 0), not just the post-CODE tail. The parser's PROGRAM handler
                    // opens the DATA machinery over the declaration section, capturing the app's
                    // GLOBAL data as scope='global' symbols — previously that entire section
                    // (~6,000 lines in a large app main) was never scanned and every global was
                    // invisible (ticket d1a0aea6). The MAP block is depth-skipped, so Pass 1's
                    // procedure declarations don't repeat; INCLUDE symbols DO repeat (Pass 1
                    // already captured them for this same file), so they're filtered here —
                    // they still feed discoveredIncludeNames, which is a set.
                    string tailMainPath;
                    if (mainFiles.TryGetValue(proj.Id, out tailMainPath))
                    {
                        var tailResult = _clarionParser.ParseMemberFile(tailMainPath, proj.Id, null, 0);
                        int inserted = 0;
                        foreach (var sym in tailResult.Symbols)
                        {
                            if (sym.Type == "include")
                            {
                                HashSet<string> names;
                                if (!discoveredIncludeNames.TryGetValue(proj.Id, out names))
                                {
                                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    discoveredIncludeNames[proj.Id] = names;
                                }
                                names.Add(sym.Name);
                                continue; // Pass 1 already inserted this file's include symbols
                            }

                            long symId = _db.InsertSymbol(sym);
                            sym.Id = symId;
                            inserted++;
                        }
                        result.SymbolCount += inserted;

                        // The main file's audit row combines Pass 1 (MAP declarations) and this
                        // full parse (global data + tail procedures).
                        int mapCount;
                        mainMapSymCount.TryGetValue(proj.Id, out mapCount);
                        int mainTotal = mapCount + inserted;
                        _db.InsertIndexedFile(proj.Id, Path.GetFileName(tailMainPath), tailMainPath,
                            mainTotal > 0 ? "resolved_parsed" : "resolved_no_symbols",
                            mainTotal, "main");
                    }
                }

                txn.Commit();
            }

            // Pass 2b: Resolve INCLUDE(...) targets discovered in Pass 1/2 that weren't already
            // known from the .cwproj (neither <Compile Include> nor <None Include>). Real Clarion
            // projects routinely reach their own class .inc files this way. Only sweep in files
            // that resolve to a path INSIDE the solution's own directory tree -- anything resolving
            // outside it (vendor/template folders, e.g. accessory\CapeSoft) is left to the explicit
            // --lib-paths mechanism (Pass 1b) rather than being auto-indexed as "project" symbols.
            ReportProgress("Pass 2b: Resolving INCLUDE() targets not listed in .cwproj...");
            using (var txn = _db.BeginTransaction())
            {
                string slnRootFull = Path.GetFullPath(slnDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                foreach (var kvp in discoveredIncludeNames)
                {
                    int projectId = kvp.Key;
                    if (!changedProjects.Contains(projectId)) continue;

                    string projectDir;
                    if (!projectDirs.TryGetValue(projectId, out projectDir)) continue;

                    // Files already known from the .cwproj for this project -- Pass 1 already parsed them.
                    var alreadyKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    List<ResolvedFile> knownIncs;
                    if (incFiles.TryGetValue(projectId, out knownIncs))
                        foreach (var f in knownIncs)
                            alreadyKnown.Add(f.FileName);

                    // RECURSIVE work-list (ticket d1a0aea6, Phase 3): an .inc swept in here is
                    // parsed, and the INCLUDE() targets IT declares are queued in turn — real
                    // class .inc files routinely include their base class's .inc. alreadyKnown
                    // is the cycle guard; the depth cap is belt-and-braces against pathological
                    // chains. INCLUDE of a .clw is a source FRAGMENT (can be arbitrary code out
                    // of any context, not MEMBER-shaped) — parsing one here would mis-attribute
                    // its content, so it is recorded and skipped rather than silently dropped.
                    const int MaxIncludeDepth = 5;
                    var includeWork = new Queue<KeyValuePair<string, int>>();
                    foreach (string seedName in kvp.Value)
                        includeWork.Enqueue(new KeyValuePair<string, int>(seedName, 0));

                    while (includeWork.Count > 0)
                    {
                        var workItem = includeWork.Dequeue();
                        string includeName = workItem.Key;
                        int depth = workItem.Value;

                        if (alreadyKnown.Contains(includeName)) continue;
                        if (!includeName.EndsWith(".inc", StringComparison.OrdinalIgnoreCase))
                        {
                            if (includeName.EndsWith(".clw", StringComparison.OrdinalIgnoreCase))
                            {
                                _db.InsertIndexedFile(projectId, includeName, null, "skipped_clw_include", 0, "pass2b-include");
                                alreadyKnown.Add(includeName);
                            }
                            continue; // .equ/.def/etc: equate soup, nothing symbol-shaped to gain
                        }

                        var resolvedList = _resolver.Resolve(projectDir, new List<string> { includeName }, libraryPaths);
                        var resolved = resolvedList.Count > 0 ? resolvedList[0] : null;
                        if (resolved == null || !resolved.Found)
                        {
                            // Unresolvable — leave alone, but leave a trace (audit, d1a0aea6)
                            _db.InsertIndexedFile(projectId, includeName, null, "unresolved", 0, "pass2b-include");
                            alreadyKnown.Add(includeName); // one audit row per name, not per referencing file
                            continue;
                        }

                        string fullPath = Path.GetFullPath(resolved.FullPath);
                        bool insideSolution =
                            fullPath.StartsWith(slnRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(slnRootFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                        if (!insideSolution)
                        {
                            // Vendor/template path — deliberately left to --lib-paths; record why
                            _db.InsertIndexedFile(projectId, includeName, fullPath, "skipped_outside_solution", 0, "pass2b-include");
                            alreadyKnown.Add(includeName);
                            continue;
                        }

                        var parseResult = _clarionParser.ParseIncFile(fullPath, projectId);
                        foreach (var sym in parseResult.Symbols)
                        {
                            long symId = _db.InsertSymbol(sym);
                            sym.Id = symId;
                        }
                        result.SymbolCount += parseResult.Symbols.Count;
                        _db.InsertIndexedFile(projectId, includeName, fullPath,
                            parseResult.Symbols.Count > 0 ? "resolved_parsed" : "resolved_no_symbols",
                            parseResult.Symbols.Count, "pass2b-include");
                        alreadyKnown.Add(includeName); // avoid double-parse if INCLUDE()'d from multiple files

                        // Follow THIS file's own INCLUDE() targets
                        if (depth < MaxIncludeDepth)
                        {
                            foreach (var sym in parseResult.Symbols)
                            {
                                if (sym.Type == "include" && !alreadyKnown.Contains(sym.Name))
                                    includeWork.Enqueue(new KeyValuePair<string, int>(sym.Name, depth + 1));
                            }
                        }
                    }
                }

                txn.Commit();
            }

            // Pass 3: Always rebuild ALL relationships (they cross project boundaries)
            ReportProgress("Rebuilding call relationships...");
            using (var txn = _db.BeginTransaction())
            {
                _db.ClearRelationships();
                ResolveRelationships(projects, memberFiles, mainFiles);
                txn.Commit();
            }

            // Store per-project timestamps for changed projects
            string now = DateTime.Now.ToString("o");
            foreach (int pid in changedProjects)
            {
                _db.SetMetadata("project_indexed:" + pid, now);
            }

            // Store global metadata
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;

            _db.SetMetadata("sln_path", slnPath);
            _db.SetMetadata("last_indexed", now);
            _db.SetMetadata("file_count", result.FileCount.ToString());
            _db.SetMetadata("symbol_count", result.SymbolCount.ToString());
            _db.SetMetadata("index_duration_ms", result.DurationMs.ToString());

            string mode = incremental ? "Incremental" : "Full";
            ReportProgress(string.Format("{0} indexing complete: {1} projects, {2} files, {3} symbols in {4}ms",
                mode, result.ProjectCount, result.FileCount, result.SymbolCount, result.DurationMs));
            // No silent gaps (ticket d1a0aea6): say when cwproj-listed files did not resolve,
            // and where the per-file audit lives either way.
            if (unresolvedCount > 0)
                ReportProgress(string.Format(
                    "WARNING: {0} cwproj-listed file(s) did not resolve — SELECT * FROM indexed_files WHERE outcome='unresolved'",
                    unresolvedCount));
            else
                ReportProgress("All cwproj-listed files resolved. Per-file audit: indexed_files table.");

            return result;
        }

        /// <summary>
        /// Check if any source file in the project has been modified since lastIndexed.
        /// </summary>
        private bool ProjectHasChanges(List<ResolvedFile> files, DateTime lastIndexed)
        {
            foreach (var file in files)
            {
                if (!file.Found) continue;
                try
                {
                    DateTime mtime = File.GetLastWriteTime(file.FullPath);
                    if (mtime > lastIndexed)
                        return true;
                }
                catch { }
            }
            return false;
        }

        private void ResolveRelationships(List<SolutionProject> projects, Dictionary<int, List<ResolvedFile>> memberFiles, Dictionary<int, string> mainFiles)
        {
            // Load ALL symbols into memory once — eliminates per-line DB queries
            var symbolNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var procNames = new List<string>(); // ordered list for matching
            // File-specific lookup: filePath → (name → id) — resolves ambiguous names
            var symbolByFile = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            // File-specific lookup: filePath → (definition line → id). Clarion allows several
            // procedures/methods to share the exact same name via parameter-type overloading
            // (e.g. a method declared once per parameter type) -- symbolByFile and
            // symbolNameToId can only ever hold ONE id per name, so they silently collapse every
            // overload onto whichever one happened to be inserted last (see the "Last wins"
            // comment below). A procedure's own definition line is always unique per file, so
            // it's used below to pick out the exact overload currentProcId/currentProcName
            // should track while scanning that overload's own body.
            var symbolLineByFile = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);

            // Scope-ordered call resolution (b7553893 #1): every callable name maps to ALL of
            // its candidates, each carrying its project, file, params, and whether it is a mere
            // prototype. The old flat symbolNameToId (kept below for non-call-target uses)
            // collapsed same-named procedures across the whole solution onto whichever loaded
            // last — measured on v61POSitive, EVERY StandardWarning call in 12+ apps resolved
            // to one arbitrary app's copy.
            var callTargetsByName = new Dictionary<string, List<CallTarget>>(StringComparer.OrdinalIgnoreCase);
            // Routines keyed (file, owning procedure, routine name) for DO resolution (#4).
            var routinesByFileProc = new Dictionary<string, Dictionary<string, Dictionary<string, long>>>(StringComparer.OrdinalIgnoreCase);
            // File-level routine fallback: name -> id, or -1 when the name is ambiguous in that file.
            var routinesByFileOnly = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

            var allSymDt = _db.ExecuteQuery(
                "SELECT id, name, type, file_path, line_number, project_id, params, parent_name, decl_kind FROM symbols WHERE type IN ('procedure','function','routine')");

            foreach (System.Data.DataRow row in allSymDt.Rows)
            {
                string name = row["name"].ToString();
                long id = Convert.ToInt64(row["id"]);
                string filePath = row["file_path"].ToString();
                int lineNumber = row["line_number"] != DBNull.Value ? Convert.ToInt32(row["line_number"]) : -1;
                int symProjectId = row["project_id"] != DBNull.Value ? Convert.ToInt32(row["project_id"]) : -1;
                string symParams = row["params"] != DBNull.Value ? row["params"].ToString() : null;
                string symParent = row["parent_name"] != DBNull.Value ? row["parent_name"].ToString() : null;
                string declKind = row["decl_kind"] != DBNull.Value ? row["decl_kind"].ToString() : null;
                bool isRoutine = row["type"].ToString() == "routine";

                if (isRoutine)
                {
                    // (file, proc, routine) exact map
                    Dictionary<string, Dictionary<string, long>> byProc;
                    if (!routinesByFileProc.TryGetValue(filePath, out byProc))
                    {
                        byProc = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
                        routinesByFileProc[filePath] = byProc;
                    }
                    string procKey = symParent ?? "";
                    Dictionary<string, long> byName;
                    if (!byProc.TryGetValue(procKey, out byName))
                    {
                        byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        byProc[procKey] = byName;
                    }
                    byName[name] = id;

                    // file-level fallback: unique -> id, duplicate -> -1 (refuse to guess)
                    Dictionary<string, long> fileRoutines;
                    if (!routinesByFileOnly.TryGetValue(filePath, out fileRoutines))
                    {
                        fileRoutines = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                        routinesByFileOnly[filePath] = fileRoutines;
                    }
                    fileRoutines[name] = fileRoutines.ContainsKey(name) ? -1 : id;
                }
                else
                {
                    List<CallTarget> targets;
                    if (!callTargetsByName.TryGetValue(name, out targets))
                    {
                        targets = new List<CallTarget>();
                        callTargetsByName[name] = targets;
                    }
                    targets.Add(new CallTarget
                    {
                        Id = id,
                        ProjectId = symProjectId,
                        FilePath = filePath,
                        Params = symParams,
                        IsPrototype = string.Equals(declKind, "prototype", StringComparison.OrdinalIgnoreCase)
                    });
                }

                // Last wins — implementation in member file overwrites MAP declaration. Also the
                // known limitation this fix works around: for genuine overloads sharing a name,
                // only the last-loaded one survives here. symbolLineByFile above is the
                // disambiguated path for anything that needs the correct per-overload id.
                // KEPT for the non-call-target lookups below; every call-target site now goes
                // through ResolveCallTarget instead.
                symbolNameToId[name] = id;

                // Build per-file symbol lookup
                Dictionary<string, long> fileSymbols;
                if (!symbolByFile.TryGetValue(filePath, out fileSymbols))
                {
                    fileSymbols = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    symbolByFile[filePath] = fileSymbols;
                }
                fileSymbols[name] = id;

                if (lineNumber > 0)
                {
                    Dictionary<int, long> fileSymbolsByLine;
                    if (!symbolLineByFile.TryGetValue(filePath, out fileSymbolsByLine))
                    {
                        fileSymbolsByLine = new Dictionary<int, long>();
                        symbolLineByFile[filePath] = fileSymbolsByLine;
                    }
                    fileSymbolsByLine[lineNumber] = id;
                }

                // Only add procedures/functions from .clw files to the match list.
                // Skip: routines, dotted names (class method implementations),
                // names that match class method declarations (Init, Kill, Event, etc.
                // appear as bare names from CLASS blocks but are always called with
                // a dot prefix and can't be called from other procedures).
                // Also skip Clarion built-in procedures/functions (ADD, CLOSE, etc.)
                if (row["type"].ToString() != "routine"
                    && !name.Contains(".")
                    && !procNames.Contains(name)
                    && !ClarionBuiltins.IsBuiltInOrKeyword(name)
                    && filePath.EndsWith(".clw", StringComparison.OrdinalIgnoreCase))
                    procNames.Add(name);
            }

            ReportProgress(string.Format("  Loaded {0} symbols into memory for matching ({1} callable procedures)", symbolNameToId.Count, procNames.Count));

            // Transitive project-dependency closure from the .sln-declared graph, for rank 3 of
            // scope-ordered resolution: a caller may legitimately call into projects it depends
            // on (DLL exports), but never into arbitrary sibling apps.
            var depDirect = new Dictionary<int, HashSet<int>>();
            foreach (var dep in _db.GetProjectDependencies())
            {
                HashSet<int> set;
                if (!depDirect.TryGetValue(dep.Key, out set))
                {
                    set = new HashSet<int>();
                    depDirect[dep.Key] = set;
                }
                set.Add(dep.Value);
            }
            var depClosure = new Dictionary<int, HashSet<int>>();
            foreach (var kv in depDirect)
            {
                var closure = new HashSet<int>();
                var work = new Queue<int>(kv.Value);
                while (work.Count > 0)
                {
                    int p = work.Dequeue();
                    if (!closure.Add(p)) continue;
                    HashSet<int> next;
                    if (depDirect.TryGetValue(p, out next))
                        foreach (int n in next) work.Enqueue(n);
                }
                depClosure[kv.Key] = closure;
            }

            // Count top-level parameters in a stored "(LONG A, STRING B)" params value.
            // -1 = unknown (null/unparseable), never a filter.
            Func<string, int> paramArity = delegate(string p)
            {
                if (string.IsNullOrEmpty(p)) return 0;
                string t = p.Trim();
                if (t.StartsWith("(")) t = t.Substring(1);
                if (t.EndsWith(")")) t = t.Substring(0, t.Length - 1);
                t = t.Trim();
                if (t.Length == 0) return 0;
                int depth = 0, count = 1;
                foreach (char c in t)
                {
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (c == ',' && depth == 0) count++;
                }
                return count;
            };

            // Argument count at a call site: find "name(" in the line and count top-level commas
            // to the matching ')'. -1 = unknown (no parens — legal bare call — or malformed).
            Func<string, string, int> callArity = delegate(string codeLine, string procName)
            {
                int idx = codeLine.IndexOf(procName, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    int after = idx + procName.Length;
                    int paren = after;
                    while (paren < codeLine.Length && codeLine[paren] == ' ') paren++;
                    if (paren < codeLine.Length && codeLine[paren] == '(')
                    {
                        int depth = 0, count = 0; bool any = false, inStr = false;
                        for (int c = paren; c < codeLine.Length; c++)
                        {
                            char ch = codeLine[c];
                            if (ch == '\'') inStr = !inStr;
                            if (inStr) continue;
                            if (ch == '(') { depth++; if (depth == 1) continue; }
                            else if (ch == ')') { depth--; if (depth == 0) return any ? count + 1 : 0; }
                            else if (depth >= 1 && !char.IsWhiteSpace(ch)) any = true;
                            if (ch == ',' && depth == 1) count++;
                        }
                        return -1; // unbalanced (continuation line) — unknown
                    }
                    idx = codeLine.IndexOf(procName, after, StringComparison.OrdinalIgnoreCase);
                }
                return -1;
            };

            // Scope-ordered call-target resolution (b7553893 #1/#2):
            //   implementations before prototypes; within that, same file -> same project ->
            //   dependency-closure projects -> anywhere; arity tie-break when the call site's
            //   argument count is known; still-tied -> lowest id, flagged ambiguous.
            // A prototype-only name (e.g. a DLL export whose body is outside the solution)
            // resolves to its prototype so the edge exists rather than dangling.
            ResolveCallDelegate resolveCallTarget = delegate(string name, int callerProjectId, string callerFile, string codeLine, out bool ambiguous)
            {
                ambiguous = false;
                List<CallTarget> cands;
                if (!callTargetsByName.TryGetValue(name, out cands) || cands.Count == 0) return -1;
                if (cands.Count == 1) return cands[0].Id;

                var pool = new List<CallTarget>();
                foreach (var c in cands) if (!c.IsPrototype) pool.Add(c);
                if (pool.Count == 0) pool.AddRange(cands); // prototype-only name

                if (pool.Count > 1)
                {
                    var narrowed = new List<CallTarget>();
                    foreach (var c in pool)
                        if (string.Equals(c.FilePath, callerFile, StringComparison.OrdinalIgnoreCase)) narrowed.Add(c);
                    if (narrowed.Count == 0 && callerProjectId >= 0)
                    {
                        foreach (var c in pool) if (c.ProjectId == callerProjectId) narrowed.Add(c);
                    }
                    if (narrowed.Count == 0 && callerProjectId >= 0)
                    {
                        HashSet<int> deps;
                        if (depClosure.TryGetValue(callerProjectId, out deps))
                            foreach (var c in pool) if (deps.Contains(c.ProjectId)) narrowed.Add(c);
                    }
                    if (narrowed.Count > 0) pool = narrowed;
                }

                if (pool.Count > 1 && codeLine != null)
                {
                    int arity = callArity(codeLine, name);
                    if (arity >= 0)
                    {
                        var arityMatch = new List<CallTarget>();
                        foreach (var c in pool) if (paramArity(c.Params) == arity) arityMatch.Add(c);
                        if (arityMatch.Count > 0) pool = arityMatch;
                    }
                }

                long best = long.MaxValue;
                foreach (var c in pool) if (c.Id < best) best = c.Id;
                ambiguous = pool.Count > 1;
                return best;
            };

            // Load variable symbols for reference tracking
            // Build per-file variable lookup: filePath → list of (name, id, parentName/scope)
            var variablesByFile = new Dictionary<string, List<VariableInfo>>(StringComparer.OrdinalIgnoreCase);
            // Also index by full stored name (e.g. "OwnerClass.MyWorker" for a CLASS data member
            // declared in an .inc) so dotted-call resolution can find a class's own member even
            // when it lives in a different file than the .clw doing the calling (issue: cross-file
            // class-member resolution gap). Last-wins on duplicate names, consistent with
            // symbolNameToId's existing behavior elsewhere in this method.
            var variablesByName = new Dictionary<string, VariableInfo>(StringComparer.OrdinalIgnoreCase);
            var allVarDt = _db.ExecuteQuery(
                "SELECT id, name, file_path, parent_name, scope, params FROM symbols WHERE type = 'variable'");

            foreach (System.Data.DataRow row in allVarDt.Rows)
            {
                string name = row["name"].ToString();
                long id = Convert.ToInt64(row["id"]);
                string fp = row["file_path"].ToString();
                string parentName = row["parent_name"] != DBNull.Value ? row["parent_name"].ToString() : null;
                string scope = row["scope"] != DBNull.Value ? row["scope"].ToString() : "local";
                string varParams = row["params"] != DBNull.Value ? row["params"].ToString() : null;

                var varInfo = new VariableInfo { Name = name, Id = id, ParentName = parentName, Scope = scope, Params = varParams };

                List<VariableInfo> fileVars;
                if (!variablesByFile.TryGetValue(fp, out fileVars))
                {
                    fileVars = new List<VariableInfo>();
                    variablesByFile[fp] = fileVars;
                }
                fileVars.Add(varInfo);

                variablesByName[name] = varInfo;
            }

            int totalVarCount = allVarDt.Rows.Count;
            ReportProgress(string.Format("  Loaded {0} variable symbols for reference tracking", totalVarCount));

            // Class name -> immediate parent class name (from parent_name on the class's own
            // symbol row), used below to walk the inheritance chain when a class data member
            // is declared on a BASE class but accessed via SELF.Member from a DERIVED class's
            // method (inherited-member gap: the member is only ever stored as
            // "<DeclaringClass>.<MemberName>" in variablesByName, never re-keyed per subclass).
            // Deliberately a separate query from the later "Insert inheritance relationships"
            // block below (which needs symbol ids, not names) -- not a duplicate to dedupe.
            var classParentByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var classParentDt = _db.ExecuteQuery(
                "SELECT name, parent_name FROM symbols WHERE type = 'class' AND parent_name IS NOT NULL");
            foreach (System.Data.DataRow row in classParentDt.Rows)
            {
                classParentByName[row["name"].ToString()] = row["parent_name"].ToString();
            }

            // Program symbols (one per PROGRAM file) own the calls made in the main file's
            // global CODE section. Deliberately kept OUT of symbolNameToId/procNames: the
            // program's name is the file name (e.g. "Worker"), and letting it act as a
            // bare-call target would turn every mention of a same-named variable into a
            // bogus call to the program.
            var programIdByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var programDt = _db.ExecuteQuery("SELECT id, file_path FROM symbols WHERE type = 'program'");
            foreach (System.Data.DataRow row in programDt.Rows)
                programIdByFile[row["file_path"].ToString()] = Convert.ToInt64(row["id"]);

            // Compiled regex patterns (reuse across all files)
            var procDefRegex = new System.Text.RegularExpressions.Regex(
                @"^([\w.]+)\s+(PROCEDURE|FUNCTION)\s*(\([^)]*\))?",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var codeRegex = new System.Text.RegularExpressions.Regex(
                @"^\s*CODE\s*([!].*)?$",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var routineRegex = new System.Text.RegularExpressions.Regex(
                @"^([\w:]+)\s+ROUTINE\s*([!].*)?$",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Routine labels routinely carry colons (BRW10::ProcessScroll) — \w+ alone missed
            // every template-generated routine name (b7553893 #4).
            var doRegex = new System.Text.RegularExpressions.Regex(
                @"\bDO\s+([\w:]+)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var startCallRegex = new System.Text.RegularExpressions.Regex(
                @"\bSTART\s*\(\s*(\w+)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var omitRegex = new System.Text.RegularExpressions.Regex(
                @"^\s*(OMIT|COMPILE)\s*\(\s*'([^']+)'\s*(?:,\s*([^)]+?)\s*)?\)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // SELF.Method and PARENT.Method call patterns
            var selfParentCallRegex = new System.Text.RegularExpressions.Regex(
                @"\b(SELF|PARENT)\s*\.\s*(\w+)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Dotted method calls: ObjectName.MethodName (excluding SELF/PARENT)
            var dottedCallRegex = new System.Text.RegularExpressions.Regex(
                @"\b(\w+)\s*\.\s*(\w+)\s*(\(|$)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // A CLASS declared inline in a .clw file's own line stream is always a procedure-local
            // derived class (e.g. "LocalDerived CLASS(DerivableClass)") -- a genuine top-level
            // CLASS,TYPE definition lives in an .inc file, never inline in a .clw's own text. Used
            // to skip such a declaration's body (see below) so its own overridden-method prototype
            // line doesn't get misread by procDefRegex as an unrelated procedure implementation.
            var classDefRegex = new System.Text.RegularExpressions.Regex(
                @"^(\w+)\s+CLASS\s*(\([^)]*\))?\s*(,.*)?$",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var endOrPeriodRegex = new System.Text.RegularExpressions.Regex(
                @"^\s*(END\s*([!].*)?|\.)\s*$",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            int fileCount = 0;
            int relCount = 0;
            // Track inserted relationships to avoid duplicates. For "calls"/"references" the key
            // includes the line number ("fromId|toId|type|line") so distinct call/reference sites
            // in the same procedure are each kept — dedup only collapses the same site being
            // matched twice (e.g. by more than one regex on the same line). For "inherits"/
            // "uses_type"/"includes" the key stays "fromId|toId|type": those relationships are
            // per-pair facts (does A inherit from B at all?), not per-occurrence.
            var insertedRels = new HashSet<string>();

            foreach (var proj in projects)
            {
                List<ResolvedFile> members;
                if (!memberFiles.TryGetValue(proj.Id, out members)) continue;

                // Scan list: member files (parent procedure auto-detected from the file),
                // plus the project's main PROGRAM file tail — everything from the global
                // CODE section onward. Top-level MAPs can only appear before the global
                // CODE, so the tail is MEMBER-shaped; its calls belong to the "program"
                // symbol until the first procedure implementation takes over.
                var scanTargets = new List<RelScanTarget>();
                foreach (var file in members)
                    scanTargets.Add(new RelScanTarget { Path = file.FullPath, StartLine = 0, ForcedParentId = -1, ProjectId = proj.Id });

                string mainPath;
                if (mainFiles != null && mainFiles.TryGetValue(proj.Id, out mainPath))
                {
                    long programId;
                    if (programIdByFile.TryGetValue(mainPath, out programId))
                    {
                        scanTargets.Add(new RelScanTarget
                        {
                            Path = mainPath,
                            StartLine = _clarionParser.FindMainTailStart(mainPath),
                            ForcedParentId = programId,
                            ProjectId = proj.Id
                        });
                    }
                }

                foreach (var target in scanTargets)
                {
                    if (!File.Exists(target.Path)) continue;
                    fileCount++;

                    if (fileCount % 50 == 0)
                        ReportProgress(string.Format("  Resolving calls: {0} files, {1} relationships...", fileCount, relCount));

                    var lines = ClarionAssistant.Services.EncodingHelper.ReadAllLines(target.Path, out _);
                    bool inCode = false;
                    // Tracks whether the scan is currently inside a procedure-local derived
                    // class's inline body (see classDefRegex above) -- skipped until its own
                    // closing END/period, without touching currentProcId.
                    bool inLocalClassBody = false;
                    int localClassEndDepth = 0;
                    // The parent (first) procedure in each member file owns all calls
                    long parentProcId = -1;
                    string parentProcName = null;
                    // Track local MAP procedure names — skip these as call targets
                    var localMapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Get file-specific (definition line → id) lookup. Used (instead of a
                    // name-keyed lookup) everywhere the exact overload matters, since
                    // symbolByFile/symbolNameToId can only hold one id per name and would
                    // collapse same-named overloads onto whichever loaded last (see
                    // symbolLineByFile's comment above).
                    Dictionary<int, long> currentFileSymbolsByLine;
                    if (!symbolLineByFile.TryGetValue(target.Path, out currentFileSymbolsByLine))
                        currentFileSymbolsByLine = new Dictionary<int, long>();

                    // Load variables for this file
                    List<VariableInfo> currentFileVars;
                    if (!variablesByFile.TryGetValue(target.Path, out currentFileVars))
                        currentFileVars = null;

                    // Pre-scan: find parent procedure and collect local MAP names
                    bool foundFirstProc = false;
                    bool inLocalMap = false;
                    for (int p = target.StartLine; p < lines.Length; p++)
                    {
                        string scanLine = lines[p].TrimStart();
                        if (!foundFirstProc)
                        {
                            var firstMatch = procDefRegex.Match(scanLine);
                            if (firstMatch.Success)
                            {
                                foundFirstProc = true;
                                string matchName = firstMatch.Groups[1].Value;
                                // Resolve by (file, definition line), not name alone -- see the
                                // overload note where currentProcId is updated the same way below.
                                long id;
                                if (currentFileSymbolsByLine.TryGetValue(p + 1, out id))
                                {
                                    parentProcId = id;
                                    parentProcName = matchName;
                                }
                            }
                            continue;
                        }
                        // After first PROCEDURE, look for MAP...END block
                        if (!inLocalMap)
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(scanLine, @"^MAP\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                inLocalMap = true;
                            else if (codeRegex.IsMatch(lines[p]))
                                break; // Hit CODE section, no more MAP blocks to find
                            continue;
                        }
                        // Inside local MAP — collect procedure/function names
                        if (System.Text.RegularExpressions.Regex.IsMatch(scanLine, @"^END\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            break; // End of local MAP
                        var localMatch = procDefRegex.Match(scanLine);
                        if (localMatch.Success)
                            localMapNames.Add(localMatch.Groups[1].Value);
                    }

                    // Main-file tail: the global CODE section's calls belong to the program
                    // symbol itself, not to whichever procedure implementation appears first.
                    if (target.ForcedParentId >= 0)
                        parentProcId = target.ForcedParentId;

                    // Skip files where we couldn't find the parent procedure
                    if (parentProcId < 0) continue;

                    long currentProcId = parentProcId;
                    string currentProcName = parentProcName;
                    bool seenFirstCode = false;

                    for (int i = target.StartLine; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        string trimmed = line.TrimStart();

                        // OMIT/COMPILE('terminator'[,expression]) — skip unconditional OMIT blocks
                        var omitMatch = omitRegex.Match(line);
                        if (omitMatch.Success)
                        {
                            string directive = omitMatch.Groups[1].Value.ToUpperInvariant();
                            string terminator = omitMatch.Groups[2].Value;
                            bool hasExpression = omitMatch.Groups[3].Success;
                            // COMPILE's code is always treated as included (no expression evaluation).
                            // A conditional OMIT('term', someEquate) is symmetric: whether it's really
                            // omitted depends on a project-specific EQUATE/Conditional Switch value
                            // CodeGraph can't know, so it's also always included. Only a bare,
                            // unconditional OMIT('term') is unambiguously dead code in every build.
                            if (directive == "OMIT" && !hasExpression)
                            {
                                i++;
                                while (i < lines.Length)
                                {
                                    // Per Clarion language reference: the block "ends with the line
                                    // that contains the same string constant as the terminator" — a
                                    // substring match anywhere in the line, not a prefix match. The
                                    // terminator is commonly written as a bare label, a "!label"
                                    // comment, or embedded in a longer decorative comment (e.g.
                                    // "!end- COMPILE ('*debug*',_debug_)") — all three are legal.
                                    if (lines[i].Contains(terminator))
                                        break;
                                    i++;
                                }
                            }
                            continue;
                        }

                        // CODE section toggles scanning on
                        if (codeRegex.IsMatch(line))
                        {
                            inCode = true;
                            seenFirstCode = true;
                            continue;
                        }

                        // Inside a procedure-local derived class's inline body (e.g.
                        // "LocalDerived CLASS(DerivableClass)", with one of its methods
                        // overridden and implemented later via the standard
                        // "ClassName.MethodName PROCEDURE(...)" syntax): skip until its own
                        // closing END/period, without touching currentProcId. Needed because
                        // this independent scan has no concept of ParseMemberFile's own
                        // inClassBody/dataGroupDepth tracking (a separate pass, over the same
                        // source, used only for symbol extraction) -- without this, the class
                        // body's own overridden-method PROTOTYPE line (shaped exactly like
                        // "MethodName PROCEDURE(...)") would fall straight into the procDefRegex
                        // match just below, which -- since no such symbol was ever created for a
                        // mere prototype -- resets currentProcId to parentProcId (the file's
                        // FIRST procedure), silently misattributing every call made for the rest
                        // of the REAL enclosing procedure to that unrelated first procedure
                        // instead (confirmed by direct verification against the repro: see the
                        // procedure-local derived-class-variable fix in ClarionParser.cs for the
                        // identical concern in the symbol-extraction pass).
                        if (inLocalClassBody)
                        {
                            if (endOrPeriodRegex.IsMatch(line))
                            {
                                localClassEndDepth--;
                                if (localClassEndDepth <= 0)
                                    inLocalClassBody = false;
                            }
                            continue;
                        }
                        var localClassMatch = classDefRegex.Match(trimmed);
                        if (localClassMatch.Success)
                        {
                            inLocalClassBody = true;
                            localClassEndDepth = 1;
                            continue;
                        }

                        // PROCEDURE/FUNCTION definitions: update current procedure and toggle scanning off
                        var procMatch = procDefRegex.Match(trimmed);
                        if (procMatch.Success)
                        {
                            inCode = false;
                            string matchedName = procMatch.Groups[1].Value;
                            // Update currentProcId for both top-level procedures AND class method
                            // implementations (ClassName.Method). Dotted method names resolve through
                            // currentFileSymbolsByLine (the same lookup SELF.Method uses), so calls made
                            // inside a class method are attributed to that method — not to whatever
                            // procedure was recognized before it (issue #54, Bug 2). The old
                            // `!matchedName.Contains(".")` guard skipped every class-method
                            // implementation, misattributing all their calls to the file's first
                            // method (typically Construct).
                            //
                            // Before the first CODE section, all PROCEDURE/FUNCTION matches
                            // are declarations (parent proc def, CLASS method declarations,
                            // local MAP forward declarations) — not implementations.
                            // Skip them to avoid prematurely updating currentProcId.
                            if (!seenFirstCode)
                            {
                                continue;
                            }
                            // Local MAP procedures are implementation details of the parent.
                            // Reset currentProcId to the parent so their calls are
                            // attributed to the parent procedure, not to whatever
                            // non-local proc happened to be defined before them.
                            if (localMapNames.Contains(matchedName))
                            {
                                currentProcId = parentProcId;
                                currentProcName = parentProcName;
                                continue;
                            }
                            // Resolve by (file, exact definition line) rather than name alone --
                            // Clarion allows multiple procedures/methods to share the same name via
                            // parameter-type overloading (e.g. several same-named overloads
                            // differing only by parameter type), which a name-keyed lookup can't
                            // distinguish: it silently collapses onto whichever overload happened
                            // to load last (see symbolLineByFile's comment above). procDefRegex only
                            // ever matches a procedure's own definition line, so (file, line) is
                            // unambiguous here.
                            long id;
                            if (currentFileSymbolsByLine.TryGetValue(i + 1, out id))
                            {
                                currentProcId = id;
                                currentProcName = matchedName;
                            }
                            else
                            {
                                currentProcId = parentProcId;
                                currentProcName = parentProcName;
                            }
                            continue;
                        }

                        // ROUTINE definitions toggle scanning off until next CODE
                        if (routineRegex.IsMatch(trimmed))
                        {
                            inCode = false;
                            continue;
                        }

                        if (!inCode) continue;
                        if (currentProcId < 0) continue;
                        if (trimmed.StartsWith("!")) continue;

                        // DO lines are routine calls. Resolve against THIS procedure's own
                        // routines — routines are procedure-local, so (file, owning procedure,
                        // name) is exact; fall back to a file-unique name for rows indexed
                        // before parent_name was recorded (b7553893 #4 — the 'do' relationship
                        // type was documented in the schema forever and had ZERO rows).
                        if (trimmed.StartsWith("DO ", StringComparison.OrdinalIgnoreCase)
                            || trimmed.StartsWith("DO\t", StringComparison.OrdinalIgnoreCase))
                        {
                            var doM = doRegex.Match(trimmed);
                            if (doM.Success)
                            {
                                string routineName = doM.Groups[1].Value;
                                long routineId = -1;
                                Dictionary<string, Dictionary<string, long>> byProc;
                                if (routinesByFileProc.TryGetValue(target.Path, out byProc))
                                {
                                    Dictionary<string, long> byName;
                                    if (currentProcName != null && byProc.TryGetValue(currentProcName, out byName))
                                        byName.TryGetValue(routineName, out routineId);
                                    if (routineId <= 0)
                                    {
                                        Dictionary<string, long> fileRoutines;
                                        long fallbackId;
                                        if (routinesByFileOnly.TryGetValue(target.Path, out fileRoutines) &&
                                            fileRoutines.TryGetValue(routineName, out fallbackId) &&
                                            fallbackId > 0) // -1 = duplicated in file, refuse to guess
                                            routineId = fallbackId;
                                    }
                                }
                                if (routineId > 0)
                                {
                                    string doKey = string.Format("{0}|{1}|do|{2}", currentProcId, routineId, i + 1);
                                    if (insertedRels.Add(doKey))
                                    {
                                        _db.InsertRelationship(new ClarionRelationship
                                        {
                                            FromId = currentProcId,
                                            ToId = routineId,
                                            Type = "do",
                                            FilePath = target.Path,
                                            LineNumber = i + 1
                                        });
                                        relCount++;
                                    }
                                }
                            }
                            continue;
                        }

                        // Detect START(ProcName, ...) — thread start is a call to the procedure
                        var startMatch = startCallRegex.Match(trimmed);
                        if (startMatch.Success)
                        {
                            string targetProc = startMatch.Groups[1].Value;
                            bool startAmbiguous = false;
                            long targetId = -1;
                            if (!localMapNames.Contains(targetProc))
                                targetId = resolveCallTarget(targetProc, target.ProjectId, target.Path, trimmed, out startAmbiguous);
                            if (targetId >= 0)
                            {
                                string relKey = string.Format("{0}|{1}|calls|{2}", currentProcId, targetId, i + 1);
                                if (insertedRels.Add(relKey))
                                {
                                    _db.InsertRelationship(new ClarionRelationship
                                    {
                                        FromId = currentProcId,
                                        ToId = targetId,
                                        Type = "calls",
                                        FilePath = target.Path,
                                        LineNumber = i + 1,
                                        Ambiguous = startAmbiguous
                                    });
                                    relCount++;
                                }
                            }
                        }

                        // Detect SELF.Method / PARENT.Method calls (class method dispatch)
                        var selfParentMatches = selfParentCallRegex.Matches(trimmed);
                        foreach (System.Text.RegularExpressions.Match spm in selfParentMatches)
                        {
                            string methodName = spm.Groups[2].Value;
                            // Bug N: deliberately do NOT skip built-in/keyword method names here. A
                            // genuine Clarion built-in statement (e.g. OPEN(SomeFile), ASK()) is always
                            // a plain, unqualified call -- Clarion has no "object.OPEN(...)" form for the
                            // language built-in -- so "SELF.Open(...)"/"PARENT.Ask(...)" unambiguously
                            // means a class method call, even when the method's name collides with a
                            // built-in keyword (Open/Close/Ask/Delete/Send/Get/... -- ABC itself defines
                            // methods with these exact names on WindowManager/FileManager/etc.). The
                            // symbolNameToId lookup below only ever succeeds against a real user-defined
                            // procedure symbol, so this can't manufacture a false "calls" edge -- it only
                            // stops erasing real ones.

                            // Try to resolve as ClassName.MethodName using the current procedure's
                            // class. currentProcName is tracked directly alongside currentProcId
                            // (overload-safe -- see the update above) rather than reverse-scanned
                            // from symbolNameToId: a reverse scan can't tell which of several
                            // same-named overloads currentProcId actually refers to.
                            string callerName = (currentProcName != null && currentProcName.Contains("."))
                                ? currentProcName
                                : null;
                            if (callerName != null)
                            {
                                string className = callerName.Substring(0, callerName.LastIndexOf('.'));
                                string fullMethodName = className + "." + methodName;
                                bool selfAmbiguous;
                                long targetId = resolveCallTarget(fullMethodName, target.ProjectId, target.Path, trimmed, out selfAmbiguous);
                                if (targetId >= 0)
                                {
                                    string relKey = string.Format("{0}|{1}|calls|{2}", currentProcId, targetId, i + 1);
                                    if (insertedRels.Add(relKey))
                                    {
                                        _db.InsertRelationship(new ClarionRelationship
                                        {
                                            FromId = currentProcId,
                                            ToId = targetId,
                                            Type = "calls",
                                            FilePath = target.Path,
                                            LineNumber = i + 1,
                                            Ambiguous = selfAmbiguous
                                        });
                                        relCount++;
                                    }
                                }
                            }
                        }

                        // Detect dotted method calls: ObjectName.MethodName(
                        var dottedMatches = dottedCallRegex.Matches(trimmed);
                        foreach (System.Text.RegularExpressions.Match dm in dottedMatches)
                        {
                            string objName = dm.Groups[1].Value;
                            string methodName = dm.Groups[2].Value;
                            // Skip SELF/PARENT (handled above -- see loop 1)
                            if (string.Equals(objName, "SELF", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(objName, "PARENT", StringComparison.OrdinalIgnoreCase))
                                continue;
                            // Bug N: deliberately do NOT skip built-in/keyword method names here either,
                            // for the same reason as loop 1 above -- "Object.Method(...)" dot notation is
                            // never how a real Clarion built-in statement is invoked, so this can't be
                            // confused with one; it only stops erasing real method calls whose name
                            // happens to collide with a built-in keyword.

                            // Resolve the object name through its declared class type when it is a
                            // typed variable (e.g. "Worker.Sign" where Worker is a WorkerClass →
                            // look up "WorkerClass.Sign", the real symbol name). Match by variable
                            // name anywhere in the file — Clarion overwhelmingly reuses the same var
                            // name for the same type across a file's procedures. Falls back to the
                            // literal name so genuine static-style ClassName.Method calls still
                            // resolve (issue #54, Bug 1).
                            string lookupOwner = objName;
                            bool foundLocalVar = false;
                            if (currentFileVars != null)
                            {
                                foreach (var varInfo in currentFileVars)
                                {
                                    if (!string.Equals(varInfo.Name, objName, StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    // Parameters must match the CURRENT procedure specifically -- the
                                    // same parameter name can carry a different type in a different
                                    // procedure in the same file, so the file-wide-by-name matching
                                    // that's safe for DATA locals (see Bug 1/#54) is not safe here.
                                    // currentProcName is tracked directly alongside currentProcId
                                    // (overload-safe -- see the update above), not reverse-scanned.
                                    if (string.Equals(varInfo.Scope, "parameter", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (currentProcName == null ||
                                            !string.Equals(varInfo.ParentName, currentProcName, StringComparison.OrdinalIgnoreCase))
                                            continue; // wrong procedure's parameter of the same name -- skip
                                    }

                                    foundLocalVar = true;
                                    string varTypeName;
                                    if (TryResolveVariableClassType(varInfo.Params, out varTypeName))
                                        lookupOwner = varTypeName;
                                    break;
                                }
                            }

                            // Fall back to a CLASS data member of the current procedure's own class
                            // (e.g. "SELF.MyWorker.Sign" where MyWorker is declared in the class's
                            // .inc file). Invisible to the file-scoped lookup above since the member's
                            // file_path is the .inc, not this .clw. Only tried when no local/module
                            // variable of this name exists in this file at all -- mirrors the same
                            // class-name derivation already used for SELF.Method resolution above.
                            if (!foundLocalVar)
                            {
                                // currentProcName is tracked directly alongside currentProcId
                                // (overload-safe -- see the update above), not reverse-scanned
                                // from symbolNameToId.
                                string ownerClassName = (currentProcName != null && currentProcName.Contains("."))
                                    ? currentProcName.Substring(0, currentProcName.LastIndexOf('.'))
                                    : null;
                                if (ownerClassName != null)
                                {
                                    // Walk the inheritance chain: try the current class first, then
                                    // each ancestor in turn, since the member may be declared on a
                                    // base class rather than the derived class doing the calling.
                                    // Hop limit guards against bad/cyclic parent_name data.
                                    VariableInfo memberVar = null;
                                    string searchClassName = ownerClassName;
                                    int hops = 0;
                                    while (searchClassName != null && hops < 25)
                                    {
                                        if (variablesByName.TryGetValue(searchClassName + "." + objName, out memberVar))
                                            break;
                                        memberVar = null;
                                        string parentClassName;
                                        if (!classParentByName.TryGetValue(searchClassName, out parentClassName) ||
                                            string.Equals(parentClassName, searchClassName, StringComparison.OrdinalIgnoreCase))
                                            break;
                                        searchClassName = parentClassName;
                                        hops++;
                                    }
                                    if (memberVar != null)
                                    {
                                        string varTypeName;
                                        if (TryResolveVariableClassType(memberVar.Params, out varTypeName))
                                            lookupOwner = varTypeName;
                                    }
                                }
                            }

                            string fullName = lookupOwner + "." + methodName;
                            bool dottedAmbiguous;
                            long targetId = resolveCallTarget(fullName, target.ProjectId, target.Path, trimmed, out dottedAmbiguous);
                            if (targetId >= 0)
                            {
                                string relKey = string.Format("{0}|{1}|calls|{2}", currentProcId, targetId, i + 1);
                                if (insertedRels.Add(relKey))
                                {
                                    _db.InsertRelationship(new ClarionRelationship
                                    {
                                        FromId = currentProcId,
                                        ToId = targetId,
                                        Type = "calls",
                                        FilePath = target.Path,
                                        LineNumber = i + 1,
                                        Ambiguous = dottedAmbiguous
                                    });
                                    relCount++;
                                }
                            }
                        }

                        // Procedure calls: attributed to the current procedure
                        foreach (string procName in procNames)
                        {
                            // Skip local MAP procedures
                            if (localMapNames.Contains(procName))
                                continue;

                            if (LineContainsCall(line, procName))
                            {
                                // The heart of b7553893 #1: previously symbolNameToId[procName]
                                // sent EVERY same-named call solution-wide to one arbitrary
                                // (last-inserted) target — measured: all StandardWarning calls
                                // in 12+ apps landed on one app's copy.
                                bool bareAmbiguous;
                                long bareTargetId = resolveCallTarget(procName, target.ProjectId, target.Path, trimmed, out bareAmbiguous);
                                if (bareTargetId < 0) continue;

                                string relKey = string.Format("{0}|{1}|calls|{2}", currentProcId, bareTargetId, i + 1);
                                if (!insertedRels.Add(relKey)) continue;

                                _db.InsertRelationship(new ClarionRelationship
                                {
                                    FromId = currentProcId,
                                    ToId = bareTargetId,
                                    Type = "calls",
                                    FilePath = target.Path,
                                    LineNumber = i + 1,
                                    Ambiguous = bareAmbiguous
                                });
                                relCount++;
                            }
                        }

                        // Variable references: scan for variable names in this code line
                        if (currentFileVars != null)
                        {
                            foreach (var varInfo in currentFileVars)
                            {
                                // Only match variables that are in scope:
                                // - module-level vars are visible to all procedures in this file
                                // - local vars and parameters are only visible to their owning procedure
                                if ((varInfo.Scope == "local" || varInfo.Scope == "parameter") && varInfo.ParentName != null)
                                {
                                    // Check if this var belongs to the current procedure.
                                    // currentProcName is tracked directly alongside currentProcId
                                    // (overload-safe -- see the update above), not reverse-scanned
                                    // from a name-keyed symbol lookup.
                                    if (currentProcName == null ||
                                        !string.Equals(varInfo.ParentName, currentProcName, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                }

                                if (LineContainsVariable(trimmed, varInfo.Name))
                                {
                                    string relKey = string.Format("{0}|{1}|references|{2}", currentProcId, varInfo.Id, i + 1);
                                    if (insertedRels.Add(relKey))
                                    {
                                        _db.InsertRelationship(new ClarionRelationship
                                        {
                                            FromId = currentProcId,
                                            ToId = varInfo.Id,
                                            Type = "references",
                                            FilePath = target.Path,
                                            LineNumber = i + 1
                                        });
                                        relCount++;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Build class/interface lookup dictionary for inheritance + uses_type
            var classNameToId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var classIfaceDt = _db.ExecuteQuery(
                "SELECT id, name FROM symbols WHERE type IN ('class','interface')");
            foreach (System.Data.DataRow row in classIfaceDt.Rows)
            {
                string name = row["name"].ToString();
                long id = Convert.ToInt64(row["id"]);
                classNameToId[name] = id; // last wins (shouldn't collide)
            }

            ReportProgress(string.Format("  Loaded {0} class/interface symbols for type resolution", classNameToId.Count));

            // Insert inheritance relationships for classes (fixed: uses classNameToId, not symbolNameToId)
            var classDt = _db.ExecuteQuery(
                "SELECT id, name, parent_name FROM symbols WHERE type = 'class' AND parent_name IS NOT NULL");
            foreach (System.Data.DataRow row in classDt.Rows)
            {
                long childId = Convert.ToInt64(row["id"]);
                string parentName = row["parent_name"].ToString();
                long parentId;
                if (classNameToId.TryGetValue(parentName, out parentId))
                {
                    string relKey = string.Format("{0}|{1}|inherits", childId, parentId);
                    if (insertedRels.Add(relKey))
                    {
                        _db.InsertRelationship(new ClarionRelationship
                        {
                            FromId = childId,
                            ToId = parentId,
                            Type = "inherits",
                            FilePath = "",
                            LineNumber = 0
                        });
                        relCount++;
                    }
                }
            }

            // Insert uses_type relationships: variable type → class/interface symbol
            var typedVarDt = _db.ExecuteQuery(
                "SELECT id, name, params, parent_name, file_path FROM symbols WHERE type = 'variable' AND params IS NOT NULL");
            int usesTypeCount = 0;
            foreach (System.Data.DataRow row in typedVarDt.Rows)
            {
                long varId = Convert.ToInt64(row["id"]);
                string varParams = row["params"].ToString();
                string ownerName = row["parent_name"] != DBNull.Value ? row["parent_name"].ToString() : null;
                string varFilePath = row["file_path"].ToString();

                // Extract type name from Params field:
                //   "CLASSNAME" — direct class instance
                //   "&CLASSNAME" — reference variable
                //   "LIKE(SOMETHING)" — skip (not a type usage)
                //   "GROUP", "QUEUE", "EQUATE" — skip built-in types
                string typeName = varParams;
                if (typeName.StartsWith("&"))
                    typeName = typeName.Substring(1);

                // Skip built-in types, EQUATE, GROUP, QUEUE, LIKE
                if (ClarionBuiltins.IsClarionType(typeName)) continue;
                if (ClarionBuiltins.IsBuiltInOrKeyword(typeName)) continue;
                if (typeName.StartsWith("LIKE(", StringComparison.OrdinalIgnoreCase)) continue;
                if (typeName.Contains(",")) continue; // GROUP/QUEUE with PRE attrs

                // Look up the type name in class/interface symbols
                long classId;
                if (!classNameToId.TryGetValue(typeName, out classId)) continue;

                // Find the owning procedure to create the edge from
                long fromId = -1;
                if (ownerName != null)
                {
                    // Try file-specific lookup first
                    Dictionary<string, long> ownerFileSymbols;
                    if (symbolByFile.TryGetValue(varFilePath, out ownerFileSymbols))
                        ownerFileSymbols.TryGetValue(ownerName, out fromId);

                    // Fall back to global lookup
                    if (fromId <= 0)
                        symbolNameToId.TryGetValue(ownerName, out fromId);
                }

                if (fromId <= 0)
                {
                    // Module-level variable — create edge from variable itself to the class
                    fromId = varId;
                }

                string relKey = string.Format("{0}|{1}|uses_type", fromId, classId);
                if (insertedRels.Add(relKey))
                {
                    _db.InsertRelationship(new ClarionRelationship
                    {
                        FromId = fromId,
                        ToId = classId,
                        Type = "uses_type",
                        FilePath = varFilePath,
                        LineNumber = 0
                    });
                    relCount++;
                    usesTypeCount++;
                }
            }

            ReportProgress(string.Format("  Created {0} uses_type relationships", usesTypeCount));

            // Insert INCLUDE relationships: module/program → include symbol
            // This enables "what depends on this file?" queries.
            // From = the module/program symbol of the file containing the INCLUDE statement
            // To = the include symbol itself (which records the included filename)
            int includesCount = 0;

            // Build file path → module/program symbol ID map
            var filePathToModuleId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var moduleSymDt = _db.ExecuteQuery(
                "SELECT id, file_path FROM symbols WHERE type IN ('module','program')");
            foreach (System.Data.DataRow row in moduleSymDt.Rows)
            {
                string fp = row["file_path"].ToString();
                if (!string.IsNullOrEmpty(fp))
                    filePathToModuleId[fp] = Convert.ToInt64(row["id"]);
            }

            // Also build filename → module ID for cross-referencing targets
            var fileNameToModuleId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in filePathToModuleId)
            {
                string fn = Path.GetFileName(kvp.Key);
                fileNameToModuleId[fn] = kvp.Value;
            }

            // Get all INCLUDE symbols
            var includeDt = _db.ExecuteQuery(
                "SELECT id, name, file_path, line_number FROM symbols WHERE type = 'include'");

            foreach (System.Data.DataRow row in includeDt.Rows)
            {
                long includeSymId = Convert.ToInt64(row["id"]);
                string includedFile = row["name"].ToString(); // e.g. "mo.Inc" or "oifunctionsmap.clw"
                string sourceFilePath = row["file_path"].ToString(); // file that contains the INCLUDE
                int lineNum = row["line_number"] != DBNull.Value ? Convert.ToInt32(row["line_number"]) : 0;

                if (string.IsNullOrEmpty(includedFile) || string.IsNullOrEmpty(sourceFilePath))
                    continue;

                // Find the source file's module/program symbol (the "from" side)
                long fromId;
                if (!filePathToModuleId.TryGetValue(sourceFilePath, out fromId))
                    continue;

                // The "to" side: prefer a module/program symbol matching the included filename,
                // fall back to the include symbol itself
                long toId;
                if (!fileNameToModuleId.TryGetValue(includedFile, out toId))
                    toId = includeSymId; // target is external — link to the include symbol

                if (fromId == toId) continue;

                string relKey = string.Format("{0}|{1}|includes", fromId, toId);
                if (insertedRels.Add(relKey))
                {
                    _db.InsertRelationship(new ClarionRelationship
                    {
                        FromId = fromId,
                        ToId = toId,
                        Type = "includes",
                        FilePath = sourceFilePath,
                        LineNumber = lineNum
                    });
                    relCount++;
                    includesCount++;
                }
            }

            ReportProgress(string.Format("  Created {0} includes relationships", includesCount));
            ReportProgress(string.Format("  Resolved {0} relationships across {1} files", relCount, fileCount));
        }

        // Resolve a variable's declared class type from its raw params string, mirroring the
        // uses_type extraction logic (see ResolveRelationships). Strips a leading '&' (reference
        // vars) and rejects Clarion built-in types, keywords, LIKE(...), and GROUP/QUEUE PRE-
        // attributed declarations. Returns false when params don't name a resolvable user class.
        private static bool TryResolveVariableClassType(string rawParams, out string typeName)
        {
            typeName = null;
            if (string.IsNullOrEmpty(rawParams)) return false;
            string t = rawParams.Trim();
            if (t.StartsWith("&")) t = t.Substring(1);
            if (t.Length == 0) return false;
            if (ClarionBuiltins.IsClarionType(t)) return false;
            if (ClarionBuiltins.IsBuiltInOrKeyword(t)) return false;
            if (t.StartsWith("LIKE(", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Contains(",")) return false; // GROUP/QUEUE with PRE attrs
            typeName = t;
            return true;
        }

        private bool LineContainsCall(string line, string procName)
        {
            int startSearch = 0;
            while (startSearch < line.Length)
            {
                int idx = line.IndexOf(procName, startSearch, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;

                // Check word boundaries (dot/colon before = method call or qualified name, skip it)
                if (idx > 0 && (char.IsLetterOrDigit(line[idx - 1]) || line[idx - 1] == '_' || line[idx - 1] == '.' || line[idx - 1] == ':' || line[idx - 1] == '?'))
                {
                    startSearch = idx + 1;
                    continue;
                }
                int afterIdx = idx + procName.Length;
                if (afterIdx < line.Length && (char.IsLetterOrDigit(line[afterIdx]) || line[afterIdx] == '_' || line[afterIdx] == ':' || line[afterIdx] == '.'))
                {
                    startSearch = idx + 1;
                    continue;
                }

                // Check if inside a single-quoted string literal
                if (IsInsideQuotedString(line, idx))
                {
                    startSearch = idx + 1;
                    continue;
                }

                // Check if this is an assignment target (name followed by optional whitespace then '=')
                // e.g. "Action = value" — Action is a variable, not a procedure call
                if (IsAssignmentTarget(line, afterIdx))
                {
                    startSearch = idx + 1;
                    continue;
                }

                // Check if used in concatenation context (& Name or Name &)
                // e.g. "'text' & Action & 'more'" — Action is a variable
                if (IsInConcatenation(line, idx, afterIdx))
                {
                    startSearch = idx + 1;
                    continue;
                }

                // Check if used as a value after comparison/assignment operators without parens
                // e.g. "IF Action = 5" or "CASE Action" — Action is a variable
                // A function returning a value MUST have parens: "IF MyFunc() = 5"
                if (IsValueContext(line, idx, afterIdx))
                {
                    startSearch = idx + 1;
                    continue;
                }

                return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a line of code contains a reference to a variable name.
        /// Uses word boundary matching, allowing colons (Loc:Name) as part of the name.
        /// Excludes matches inside string literals.
        /// </summary>
        private bool LineContainsVariable(string line, string varName)
        {
            int startSearch = 0;
            while (startSearch < line.Length)
            {
                int idx = line.IndexOf(varName, startSearch, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;

                // Word boundary before: allow colon as part of variable names
                if (idx > 0)
                {
                    char before = line[idx - 1];
                    if (char.IsLetterOrDigit(before) || before == '_')
                    {
                        // If the variable name contains a colon and the char before is
                        // a letter, this could be a different prefix — skip
                        startSearch = idx + 1;
                        continue;
                    }
                    // Dot before means it's a qualified name (object.property) — still a valid reference
                }

                // Word boundary after
                int afterIdx = idx + varName.Length;
                if (afterIdx < line.Length)
                {
                    char after = line[afterIdx];
                    if (char.IsLetterOrDigit(after) || after == '_' || after == ':')
                    {
                        startSearch = idx + 1;
                        continue;
                    }
                }

                // Skip if inside a string literal
                if (IsInsideQuotedString(line, idx))
                {
                    startSearch = idx + 1;
                    continue;
                }

                return true;
            }
            return false;
        }

        private static bool IsInsideQuotedString(string line, int position)
        {
            bool inString = false;
            for (int i = 0; i < position; i++)
            {
                if (line[i] == '\'')
                    inString = !inString;
            }
            return inString;
        }

        private static bool IsAssignmentTarget(string line, int afterNameIdx)
        {
            // Skip whitespace after the name
            int i = afterNameIdx;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                i++;
            // Check for '=' that isn't part of '=>' (Clarion doesn't use ==)
            return i < line.Length && line[i] == '=' && (i + 1 >= line.Length || line[i + 1] != '>');
        }

        private static bool IsInConcatenation(string line, int nameStart, int afterNameIdx)
        {
            // Check for '&' before the name (with optional whitespace)
            int b = nameStart - 1;
            while (b >= 0 && (line[b] == ' ' || line[b] == '\t'))
                b--;
            if (b >= 0 && line[b] == '&')
                return true;

            // Check for '&' after the name (with optional whitespace)
            int a = afterNameIdx;
            while (a < line.Length && (line[a] == ' ' || line[a] == '\t'))
                a++;
            if (a < line.Length && line[a] == '&')
                return true;

            return false;
        }

        private static bool IsValueContext(string line, int nameStart, int afterNameIdx)
        {
            // If the name is NOT followed by '(' (with optional whitespace), check if it's
            // in a context where only variables appear, not procedure calls.
            // Functions returning values MUST have parens in Clarion.
            int a = afterNameIdx;
            while (a < line.Length && (line[a] == ' ' || line[a] == '\t'))
                a++;
            bool hasParens = a < line.Length && line[a] == '(';
            if (hasParens) return false; // Has parens — could be a call

            // Check what precedes the name (skip whitespace)
            int b = nameStart - 1;
            while (b >= 0 && (line[b] == ' ' || line[b] == '\t'))
                b--;

            // After comparison operators: =, <, >, ~=, <=, >=, <> — it's a value
            if (b >= 0 && (line[b] == '=' || line[b] == '<' || line[b] == '>' || line[b] == '~'))
                return true;

            // After comma — it's a parameter value, not a standalone call
            if (b >= 0 && line[b] == ',')
                return true;

            // After open paren — it's a parameter: SomeProc(Action)
            if (b >= 0 && line[b] == '(')
                return true;

            return false;
        }

        private bool IsMainFile(string filePath)
        {
            try
            {
                using (var reader = new StreamReader(filePath))
                {
                    string line;
                    int lineCount = 0;
                    while ((line = reader.ReadLine()) != null && lineCount < 50)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*PROGRAM\s*([,!].*)?$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            return true;
                        }
                        lineCount++;
                    }
                }
            }
            catch { }
            return false;
        }

        private ClarionAssistant.Services.RedFileService TryLoadRedFile(string slnDir)
        {
            try
            {
                string[] redFiles = Directory.GetFiles(slnDir, "*.red", SearchOption.TopDirectoryOnly);
                if (redFiles.Length == 0) return null;

                var svc = new ClarionAssistant.Services.RedFileService();
                var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["THISDIR"] = slnDir
                };
                ReportProgress(string.Format("Using redirection file: {0}", Path.GetFileName(redFiles[0])));
                return svc.Load(redFiles[0], macros) ? svc : null;
            }
            catch { return null; }
        }

        private void ReportProgress(string message)
        {
            if (OnProgress != null)
                OnProgress(message);
        }
    }

    public class IndexResult
    {
        public string SlnPath { get; set; }
        public int ProjectCount { get; set; }
        public int FileCount { get; set; }
        public int SymbolCount { get; set; }
        public long DurationMs { get; set; }
    }

    internal class VariableInfo
    {
        public string Name { get; set; }
        public long Id { get; set; }
        public string ParentName { get; set; }
        public string Scope { get; set; }
        public string Params { get; set; }
    }

    /// <summary>
    /// One file (or file tail) to scan for relationships. Member files use StartLine 0 and
    /// ForcedParentId -1 (parent procedure auto-detected). The main PROGRAM file's tail uses
    /// the global CODE line as StartLine and the file's "program" symbol as ForcedParentId.
    /// </summary>
    /// <summary>Scope-ordered call-target resolution — Func can't carry an out param.</summary>
    internal delegate long ResolveCallDelegate(string name, int callerProjectId, string callerFile, string codeLine, out bool ambiguous);

    internal class RelScanTarget
    {
        public string Path { get; set; }
        public int StartLine { get; set; }
        public long ForcedParentId { get; set; }
        // The project this file is scanned FOR — the caller side of scope-ordered call
        // resolution (same file -> same project -> dependency projects -> global).
        public int ProjectId { get; set; }
    }

    /// <summary>A callable symbol as loaded for scope-ordered call resolution (b7553893 #1/#2).</summary>
    internal class CallTarget
    {
        public long Id;
        public int ProjectId;
        public string FilePath;
        public string Params;
        public bool IsPrototype;
    }
}
