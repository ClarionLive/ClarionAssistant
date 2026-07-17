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
        private readonly SourceResolver _resolver;
        private readonly ClarionParser _clarionParser;

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
                    }

                    // The main PROGRAM file's tail (global CODE + hand-written procedure
                    // implementations after it) is MEMBER-shaped source that Pass 1 never
                    // scans — run it through the member-file parser from the boundary.
                    string tailMainPath;
                    if (mainFiles.TryGetValue(proj.Id, out tailMainPath))
                    {
                        int tailStart = _clarionParser.FindMainTailStart(tailMainPath);
                        var tailResult = _clarionParser.ParseMemberFile(tailMainPath, proj.Id, null, tailStart);
                        foreach (var sym in tailResult.Symbols)
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
                        result.SymbolCount += tailResult.Symbols.Count;
                    }
                }

                txn.Commit();
            }

            // Pass 2b: Resolve INCLUDE(...) targets discovered in Pass 1/2 that weren't already
            // known from the .cwproj (neither <Compile Include> nor <None Include>). Real Clarion
            // projects routinely reach their own class .inc files this way. Only sweep in files
            // that resolve to a path INSIDE the solution's own directory tree -- anything resolving
            // outside it (vendor/template folders) is left to the explicit --lib-paths mechanism
            // rather than being auto-indexed as "project" symbols.
            ReportProgress("Pass 2b: Resolving INCLUDE() targets not listed in .cwproj...");
            using (var txn = _db.BeginTransaction())
            {
                string slnRootFull = Path.GetFullPath(Path.GetDirectoryName(slnPath))
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

                    foreach (string includeName in kvp.Value)
                    {
                        if (!includeName.EndsWith(".inc", StringComparison.OrdinalIgnoreCase)) continue;
                        if (alreadyKnown.Contains(includeName)) continue;

                        var resolvedList = _resolver.Resolve(projectDir, new List<string> { includeName }, null);
                        var resolved = resolvedList.Count > 0 ? resolvedList[0] : null;
                        if (resolved == null || !resolved.Found) continue; // unresolvable -- leave alone

                        string fullPath = Path.GetFullPath(resolved.FullPath);
                        bool insideSolution =
                            fullPath.StartsWith(slnRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            fullPath.StartsWith(slnRootFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                        if (!insideSolution) continue; // vendor/template path -- leave to --lib-paths

                        var parseResult = _clarionParser.ParseIncFile(fullPath, projectId);
                        foreach (var sym in parseResult.Symbols)
                        {
                            long symId = _db.InsertSymbol(sym);
                            sym.Id = symId;
                        }
                        result.SymbolCount += parseResult.Symbols.Count;
                        alreadyKnown.Add(includeName); // avoid double-parse if INCLUDE()'d from multiple files
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

            var allSymDt = _db.ExecuteQuery(
                "SELECT id, name, type, file_path FROM symbols WHERE type IN ('procedure','function','routine')");

            foreach (System.Data.DataRow row in allSymDt.Rows)
            {
                string name = row["name"].ToString();
                long id = Convert.ToInt64(row["id"]);
                string filePath = row["file_path"].ToString();
                // Last wins — implementation in member file overwrites MAP declaration
                symbolNameToId[name] = id;

                // Build per-file symbol lookup
                Dictionary<string, long> fileSymbols;
                if (!symbolByFile.TryGetValue(filePath, out fileSymbols))
                {
                    fileSymbols = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    symbolByFile[filePath] = fileSymbols;
                }
                fileSymbols[name] = id;

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
            var doRegex = new System.Text.RegularExpressions.Regex(
                @"\bDO\s+(\w+)",
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
                    scanTargets.Add(new RelScanTarget { Path = file.FullPath, StartLine = 0, ForcedParentId = -1 });

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
                            ForcedParentId = programId
                        });
                    }
                }

                foreach (var target in scanTargets)
                {
                    if (!File.Exists(target.Path)) continue;
                    fileCount++;

                    if (fileCount % 50 == 0)
                        ReportProgress(string.Format("  Resolving calls: {0} files, {1} relationships...", fileCount, relCount));

                    var lines = File.ReadAllLines(target.Path);
                    bool inCode = false;
                    // Tracks whether the scan is currently inside a procedure-local derived
                    // class's inline body (see classDefRegex above) -- skipped until its own
                    // closing END/period, without touching currentProcId.
                    bool inLocalClassBody = false;
                    int localClassEndDepth = 0;
                    // The parent (first) procedure in each member file owns all calls
                    long parentProcId = -1;
                    // Track local MAP procedure names — skip these as call targets
                    var localMapNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Get file-specific symbol lookup for this file
                    Dictionary<string, long> currentFileSymbols;
                    if (!symbolByFile.TryGetValue(target.Path, out currentFileSymbols))
                        currentFileSymbols = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

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
                                // Use ONLY file-specific lookup to avoid cross-file name collisions.
                                // symbolNameToId may return a symbol from a different file if names collide.
                                long id;
                                if (currentFileSymbols.TryGetValue(matchName, out id))
                                    parentProcId = id;
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
                            // currentFileSymbols (the same lookup SELF.Method uses), so calls made
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
                                continue;
                            }
                            // Use ONLY file-specific lookup to avoid cross-file name collisions.
                            // If the symbol isn't in this file's symbols, reset to parentProcId
                            // rather than risking a match to a same-named symbol in another file.
                            long id;
                            if (currentFileSymbols.TryGetValue(matchedName, out id))
                                currentProcId = id;
                            else
                                currentProcId = parentProcId;
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

                        // Skip DO lines — these are routine calls, not procedure calls
                        if (trimmed.StartsWith("DO ", StringComparison.OrdinalIgnoreCase)
                            || trimmed.StartsWith("DO\t", StringComparison.OrdinalIgnoreCase)) continue;

                        // Detect START(ProcName, ...) — thread start is a call to the procedure
                        var startMatch = startCallRegex.Match(trimmed);
                        if (startMatch.Success)
                        {
                            string targetProc = startMatch.Groups[1].Value;
                            long targetId;
                            if (!localMapNames.Contains(targetProc) && symbolNameToId.TryGetValue(targetProc, out targetId))
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
                                        LineNumber = i + 1
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
                            if (ClarionBuiltins.IsBuiltInOrKeyword(methodName)) continue;

                            // Try to resolve as ClassName.MethodName using the current procedure's class
                            string callerName = null;
                            // Look up current proc name to find its class prefix
                            foreach (var kvp2 in symbolNameToId)
                            {
                                if (kvp2.Value == currentProcId && kvp2.Key.Contains("."))
                                {
                                    callerName = kvp2.Key;
                                    break;
                                }
                            }
                            if (callerName != null)
                            {
                                string className = callerName.Substring(0, callerName.LastIndexOf('.'));
                                string fullMethodName = className + "." + methodName;
                                long targetId;
                                if (symbolNameToId.TryGetValue(fullMethodName, out targetId))
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
                                            LineNumber = i + 1
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
                            // Skip SELF/PARENT (handled above) and built-ins
                            if (string.Equals(objName, "SELF", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(objName, "PARENT", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (ClarionBuiltins.IsBuiltInOrKeyword(methodName)) continue;

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
                                string currentProcNameForParamScope = null;
                                bool triedCurrentProcNameForParamScope = false;
                                foreach (var varInfo in currentFileVars)
                                {
                                    if (!string.Equals(varInfo.Name, objName, StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    // Parameters must match the CURRENT procedure specifically -- the
                                    // same parameter name can carry a different type in a different
                                    // procedure in the same file, so the file-wide-by-name matching
                                    // that's safe for DATA locals (see Bug 1/#54) is not safe here.
                                    if (string.Equals(varInfo.Scope, "parameter", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!triedCurrentProcNameForParamScope)
                                        {
                                            triedCurrentProcNameForParamScope = true;
                                            foreach (var kvp4 in currentFileSymbols)
                                            {
                                                if (kvp4.Value == currentProcId) { currentProcNameForParamScope = kvp4.Key; break; }
                                            }
                                        }
                                        if (currentProcNameForParamScope == null ||
                                            !string.Equals(varInfo.ParentName, currentProcNameForParamScope, StringComparison.OrdinalIgnoreCase))
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
                                string ownerClassName = null;
                                foreach (var kvp3 in symbolNameToId)
                                {
                                    if (kvp3.Value == currentProcId && kvp3.Key.Contains("."))
                                    {
                                        ownerClassName = kvp3.Key.Substring(0, kvp3.Key.LastIndexOf('.'));
                                        break;
                                    }
                                }
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
                            long targetId;
                            if (symbolNameToId.TryGetValue(fullName, out targetId))
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
                                        LineNumber = i + 1
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
                                string relKey = string.Format("{0}|{1}|calls|{2}", currentProcId, symbolNameToId[procName], i + 1);
                                if (!insertedRels.Add(relKey)) continue;

                                _db.InsertRelationship(new ClarionRelationship
                                {
                                    FromId = currentProcId,
                                    ToId = symbolNameToId[procName],
                                    Type = "calls",
                                    FilePath = target.Path,
                                    LineNumber = i + 1
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
                                    // Check if this var belongs to the current procedure
                                    // currentProcId must match the procedure that owns this variable
                                    string currentProcName = null;
                                    foreach (var kvp2 in currentFileSymbols)
                                    {
                                        if (kvp2.Value == currentProcId)
                                        {
                                            currentProcName = kvp2.Key;
                                            break;
                                        }
                                    }
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
    internal class RelScanTarget
    {
        public string Path { get; set; }
        public int StartLine { get; set; }
        public long ForcedParentId { get; set; }
    }
}
