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
    /// EVERYTHING IS RESOLVED LAZILY AND CACHED. Detecting the Clarion install and parsing a .red
    /// costs real work, and a server that a client launches and then asks only query_docs must not
    /// pay for it. Resolution failures are recorded rather than thrown: "no solution" is a normal
    /// state that the tools already report properly, and killing the process over it would take
    /// the other 46 tools down with it.
    /// </summary>
    internal sealed class StandaloneWorkspace : IWorkspaceContext
    {
        private readonly object _lock = new object();
        private readonly string _solutionPath;

        private bool _versionResolved;
        private ClarionVersionConfig _versionConfig;

        private bool _redResolved;
        private RedFileService _redFile;

        /// <summary>
        /// How the solution was resolved, and why it wasn't when it wasn't. Written to stderr at
        /// startup: the client's log is the only place a stdio server can explain itself, and
        /// "index_solution says no solution is selected" is a mystery without it.
        /// </summary>
        public string ResolutionNote { get; private set; }

        private StandaloneWorkspace(string solutionPath, string note)
        {
            _solutionPath = solutionPath;
            ResolutionNote = note;
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

        public string CurrentSolutionPath { get { return _solutionPath; } }

        /// <summary>
        /// The solution the HOST believes is open — which here is simply the one this server was
        /// told to use. THIS PROCESS IS THE HOST, so the two cannot diverge: there is no separate
        /// IDE that might have closed a solution behind our back, which is the only thing the
        /// distinction ever existed to express.
        ///
        /// RETURNING NULL HERE WOULD BREAK TWO TOOLS, and I had written into IWorkspaceContext
        /// that null was correct because "callers fall back to CurrentSolutionPath". Reading the
        /// callers showed otherwise:
        ///
        ///   build_solution      uses this as its ONLY fallback and never consults
        ///                       CurrentSolutionPath — with null it fails "no solution is
        ///                       currently loaded in the IDE" even when --solution was given.
        ///   get_solution_info   treats "host says none, but a path is cached" as a STALE
        ///                       selection and returns an early stub, suppressing the version,
        ///                       .red and database fields that are the point of the tool.
        ///
        /// Both are correct in the addin, where a null genuinely means the IDE closed the
        /// solution. Neither is correct here, where a null would be describing a staleness that
        /// cannot happen.
        /// </summary>
        public string GetHostOpenSolutionPath() { return _solutionPath; }

        public ClarionVersionConfig CurrentVersionConfig
        {
            get
            {
                lock (_lock)
                {
                    if (!_versionResolved)
                    {
                        _versionResolved = true;
                        try
                        {
                            var info = ClarionVersionService.Detect();
                            if (info != null) _versionConfig = info.GetCurrentConfig();
                        }
                        catch { _versionConfig = null; }
                    }
                    return _versionConfig;
                }
            }
        }

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

        public RedFileService RedFile
        {
            get
            {
                lock (_lock)
                {
                    if (!_redResolved)
                    {
                        _redResolved = true;
                        _redFile = LoadRedFile();
                    }
                    return _redFile;
                }
            }
        }

        /// <summary>
        /// The addin distinguishes these because it resolves one per ACTIVE PROJECT, which can
        /// differ from the solution-level file. With no IDE there is no active project, so there
        /// is exactly one redirection context and both members answer it. Kept as two members
        /// rather than collapsed, because the interface's contract is what several tools resolve
        /// their search paths against.
        /// </summary>
        public RedFileService ActiveRedFileService { get { return RedFile; } }

        private RedFileService LoadRedFile()
        {
            var cfg = CurrentVersionConfig;
            if (cfg == null) return null;

            var svc = new RedFileService();
            string projectDir = null;
            if (!string.IsNullOrEmpty(_solutionPath))
            {
                try { projectDir = Path.GetDirectoryName(_solutionPath); }
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
                if (string.IsNullOrEmpty(_solutionPath)) return null;
                return Path.Combine(Path.GetDirectoryName(_solutionPath),
                    Path.GetFileNameWithoutExtension(_solutionPath) + ".codegraph.db");
            }
        }

        public List<string> BuildIndexLibraryPaths()
        {
            var red = RedFile;
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

            string slnPath = _solutionPath;
            if (string.IsNullOrEmpty(slnPath) || !File.Exists(slnPath))
            {
                complete("Error: no solution is selected. " + (ResolutionNote ?? "Pass --solution <path.sln>."));
                return;
            }

            string dbPath = CurrentDbPath;

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
                var worker = new System.Threading.Thread(() => IndexHeld(slnPath, dbPath, incremental, onProgress, complete));
                worker.IsBackground = true;
                worker.Name = "clarion-mcp-index";
                worker.Start();
                return;
            }

            IndexHeld(slnPath, dbPath, incremental, null, complete);
        }

        /// <summary>
        /// Do the indexing. The caller ALREADY HOLDS the IndexRunGate claim for dbPath and this
        /// method always releases it — including when it runs on a worker thread, which is why
        /// the release is in a finally here rather than at the call site.
        /// </summary>
        private void IndexHeld(string slnPath,
                               string dbPath,
                               bool incremental,
                               Action<ClarionCodeGraph.Graph.IndexProgressEvent> onProgress,
                               Action<string> complete)
        {
            IndexRunLog runLog = null;
            try
            {
                try { runLog = new IndexRunLog(Path.GetFileNameWithoutExtension(slnPath)); }
                catch { runLog = null; }

                var libPaths = BuildIndexLibraryPaths();
                var activeRed = ActiveRedFileService;

                var db = new ClarionCodeGraph.Graph.CodeGraphDatabase();
                db.Open(dbPath);
                try
                {
                    var indexer = new ClarionCodeGraph.Graph.CodeGraphIndexer(db);
                    indexer.RedService = activeRed;
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

                    var result = indexer.IndexSolution(slnPath, incremental, libPaths);

                    complete(string.Format(
                        "Indexed {0}: {1} projects, {2} files, {3} symbols, {4} relationships in {5}ms. Database: {6}",
                        Path.GetFileName(slnPath), result.ProjectCount, result.FileCount,
                        result.SymbolCount, result.RelationshipCount, result.DurationMs, dbPath));
                }
                finally
                {
                    try { db.Close(); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (runLog != null) { try { runLog.WriteLine("FAILED: " + ex.Message); } catch { } }
                complete("Error indexing solution: " + ex.Message);
            }
            finally
            {
                if (runLog != null) { try { runLog.Dispose(); } catch { } }
                IndexRunGate.Exit(dbPath);
            }
        }
    }
}
