using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Process-wide owner of the single Clarion Language Server client.
    ///
    /// Historically the LSP only ever started via AssistantChatControl (created lazily
    /// when the "Claude Chat" pad was first shown). If a user opened the Modern Embeditor
    /// WITHOUT first opening that pad, the LSP never started and there was no hover /
    /// completion. This static service makes the start pane-independent: the autostart
    /// command (LspAutostartCommand) and the embeditor self-heal hook both drive it, and
    /// McpToolRegistry delegates to it so there is exactly ONE LspClient in the process.
    ///
    /// Invariant: after EnsureRunning() returns, LspClient.Active == _client == the one
    /// running client (when a solution + server.js are resolvable).
    /// </summary>
    public static class LspService
    {
        /// <summary>
        /// Supplies the active solution path. Set by the host at startup: the addin points it
        /// at EditorService.GetOpenSolutionPath(); a standalone host points it at whatever it
        /// was told on the command line. Null means "no solution", and StartIfNeeded already
        /// treats that as "cannot start" - so an unset hook degrades to not starting the LSP
        /// rather than throwing.
        /// </summary>
        public static System.Func<string> SolutionPathProvider;

        /// <summary>
        /// Supplies the Clarion version this host has settled on, or null to let this service
        /// resolve one itself. Same host-hook pattern as <see cref="SolutionPathProvider"/>.
        ///
        /// Set by the standalone server, which has tiers the IDE does not — an explicit
        /// --clarion-version and the solution's committed clarion-assistant.json — and whose
        /// answer must reach the language server's redirection and library paths. Left unset by
        /// the addin, where the IDE's own version selection is already authoritative.
        /// </summary>
        public static System.Func<ClarionVersionConfig> VersionConfigProvider;

        private static readonly object _lock = new object();
        // Single-flight background start + a restart request that is never lost (16d140e9).
        private static readonly LspStartGate _startGate = new LspStartGate();
        private static LspClient _client;

        /// <summary>The single shared LSP client owned by this service (may be null).</summary>
        public static LspClient Client { get { return _client; } }

        /// <summary>
        /// The .sln the client currently running was started for, or null. Lets a caller that
        /// names a DIFFERENT solution be told the truth ("already running for X") instead of
        /// "started for Y" while X keeps serving.
        /// </summary>
        private static string _runningSolutionPath;

        /// <summary>
        /// The last solution named explicitly (lsp_start workspace_path). Used as a fallback when
        /// the host hook has nothing, so a later auto-start - lsp_diagnostics after the server
        /// died, say - restarts on the solution the user chose instead of failing "no solution".
        /// </summary>
        private static string _lastExplicitSolutionPath;

        /// <summary>The .sln the running client was started for, or null when none is running.</summary>
        public static string RunningSolutionPath
        {
            get
            {
                var c = LspClient.Active;
                return (c != null && c.IsRunning) ? _runningSolutionPath : null;
            }
        }

        /// <summary>The outcome of the most recent start attempt (null before the first call).</summary>
        public static LspStartResult LastResult { get; private set; }

        /// <summary>Starts on the host's solution. See <see cref="EnsureRunning(string)"/>.</summary>
        public static LspStartResult EnsureRunning()
        {
            return EnsureRunning(null);
        }

        /// <summary>
        /// Starts the LSP synchronously if it isn't already running. Resolves the solution,
        /// server.js, version config and redirection file UP FRONT and starts ONCE — there
        /// is no live post-start path update. Never throws to callers.
        ///
        /// <paramref name="explicitSolutionPath"/>, when given, is the .sln to start on, and wins
        /// over <see cref="SolutionPathProvider"/>. It used to be impossible to pass one: lsp_start
        /// resolved its workspace_path argument and then called a parameterless EnsureRunning that
        /// read only the host hook, so the argument was silently discarded (ticket 77aceec5).
        /// It does NOT restart a server already running on another solution - the addin shares one
        /// client with the embeditor, and swapping its workspace under it is not this call's to do.
        /// The result says which solution is actually being served.
        ///
        /// RETURNS WHAT HAPPENED rather than only tracing it, so callers can say why the server is
        /// not running instead of guessing (see <see cref="LspStartResult"/>).
        /// </summary>
        public static LspStartResult EnsureRunning(string explicitSolutionPath)
        {
            // EnsureRunningCore catches everything itself and returns Error, so no wrapper catch.
            var result = EnsureRunningCore(explicitSolutionPath);
            LastResult = result;
            return result;
        }

        /// <summary>
        /// A solution source that can CHANGE under a running server, and is followed: when the
        /// running server was started from it, every EnsureRunning re-asks it, restarts the server
        /// on a new answer and stops it on none. Consulted after <see cref="SolutionPathProvider"/>.
        ///
        /// Set by the standalone server launched by the IDE with no --solution: it returns the
        /// solution the IDE publishes (IdeSolutionRecord). Without following, a Chat tab's LSP kept
        /// serving solution A after the developer switched the IDE to B, or closed A (77aceec5,
        /// pipeline run 1). Must be cheap - it is called on every lsp_* auto-start while following.
        /// The addin leaves it unset.
        /// </summary>
        public static System.Func<string> FollowedSolutionProvider;

        /// <summary>True when the running server's solution came from <see cref="FollowedSolutionProvider"/>.</summary>
        private static bool _runningFromFollowed;

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            try { a = Path.GetFullPath(a); b = Path.GetFullPath(b); } catch { }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Stops the running client and clears what it was serving. Caller holds _lock.</summary>
        private static void StopClientLocked()
        {
            try { if (_client != null) _client.Dispose(); } catch { }
            _client = null;
            _runningSolutionPath = null;
            _runningFromFollowed = false;
            _runningVersionName = null;
        }

        /// <summary>The Clarion version name the running client was given in clarion/updatePaths, or null.</summary>
        private static string _runningVersionName;

        /// <summary>
        /// Restart the bundled server when the effective Clarion version moved under it (16d140e9). The
        /// server takes its redirection file, macros and libsrc paths once, at start — there is no live
        /// path update — so a Build &gt; Set Clarion Version change left it resolving through the old
        /// version's .red until the IDE was restarted. No-op when nothing is running, when it already
        /// serves <paramref name="versionName"/>, or while the shared ClarionLsp addin owns the LSP.
        /// Safe from the UI thread: the stop and restart run on the thread pool.
        /// </summary>
        public static void RestartIfVersionChanged(string versionName)
        {
            if (SharedLspBridge.IsSharedActive) return;
            // Remembered for ONE re-check after the next background start completes: a rapid A->B->C switch
            // whose C arrives while B's restart is still starting (nothing running yet to compare) would
            // otherwise leave the server on B.
            Volatile.Write(ref _wantedVersionName, versionName ?? "");
            System.Threading.Tasks.Task.Run(() => RestartCore(versionName));
        }

        /// <summary>The version most recently asked for by RestartIfVersionChanged, pending its post-start re-check.</summary>
        private static string _wantedVersionName;

        private static void RestartCore(string versionName)
        {
            {
                try
                {
                    bool restart = false;
                    lock (_lock)
                    {
                        if (_client != null && _client.IsRunning
                            && !string.Equals(_runningVersionName, versionName, StringComparison.OrdinalIgnoreCase))
                        {
                            LspTrace.Write("[LspService] Clarion version changed: " + (_runningVersionName ?? "(none)")
                                + " -> " + (versionName ?? "(none)") + "; restarting the language server.");
                            StopClientLocked();
                            restart = true;
                        }
                    }
                    // Not EnsureRunningInBackground: its plain guard dropped this request when the start
                    // that launched the client we just stopped had not yet released it (pipeline run 1).
                    if (restart && _startGate.RequestRestart()) StartOnPoolHoldingGate();
                }
                catch (Exception ex)
                {
                    LspTrace.Write("[LspService] version-change restart failed: " + ex.Message);
                }
            }
        }

        private static LspStartResult AlreadyRunning()
        {
            return new LspStartResult(LspStartOutcome.AlreadyRunning, "already running",
                _runningSolutionPath, null, null, null);
        }

        private static LspStartResult EnsureRunningCore(string explicitSolutionPath)
        {
            try
            {
                // Phase 4 (#17 — single process): when the shared ClarionLsp addin is the active,
                // capability-verified server, do NOT spawn our bundled node server. All consumers route
                // through SharedLspBridge, which talks to the shared client. This is the one chokepoint
                // every start path funnels through (autostart command, MCP EnsureLspRunning, embeditor
                // self-heal LspStarter), so gating here covers them all.
                if (SharedLspBridge.IsSharedActive)
                {
                    LspTrace.Write("[LspService] Shared ClarionLsp addin active — not starting the bundled LSP server.");
                    return LspStartResult.Of(LspStartOutcome.SharedActive,
                        "the shared ClarionLsp addin is active; the bundled server is not started while it is.");
                }

                // Fast pre-check against the process-wide Active client (set by LspClient.Start) -
                // but NOT while following a solution that may have changed, and not for an explicit
                // request: both must reach the comparison below.
                if (string.IsNullOrEmpty(explicitSolutionPath) && !_runningFromFollowed
                    && LspClient.Active != null && LspClient.Active.IsRunning) return AlreadyRunning();

                lock (_lock)
                {
                    string slnPath = null;
                    string slnSource = null;
                    bool fromFollowed = false;

                    if (_client != null && _client.IsRunning)
                    {
                        // FOLLOWING: resolve the desired solution BEFORE declaring "already running",
                        // and act on a change (77aceec5, pipeline run 1).
                        if (string.IsNullOrEmpty(explicitSolutionPath) && _runningFromFollowed
                            && FollowedSolutionProvider != null)
                        {
                            string now = FollowedSolutionProvider();
                            if (SamePath(now, _runningSolutionPath)) return AlreadyRunning();

                            string was = _runningSolutionPath;
                            LspTrace.Write("[LspService] followed solution changed: " + was + " -> "
                                + (now ?? "(none)") + "; stopping the server.");
                            StopClientLocked();
                            if (string.IsNullOrEmpty(now))
                                return LspStartResult.Of(LspStartOutcome.NoSolution,
                                    "The IDE no longer has a solution open (was " + was + "), so the language "
                                    + "server was stopped. " + LspStartResult.NoSolutionMessage);
                            slnPath = now;
                            slnSource = "the IDE's open solution";
                            fromFollowed = true;
                        }
                        else
                        {
                            // Re-check inside the lock — another thread may have started it.
                            return AlreadyRunning();
                        }
                    }

                    // Was EditorService.GetOpenSolutionPath() - a static call into an
                    // IDE-coupled class, and the one thing stopping this otherwise IDE-free
                    // file compiling in the standalone MCP server (ticket d051fbd1). Routed
                    // through a host-supplied hook, the same pattern SharedLspBridge already
                    // uses for CodeGraphDbPathProvider / SchemaGraphDbPathProvider.
                    //
                    // An EXPLICIT solution (lsp_start workspace_path) wins over the hook; the
                    // last explicit one is the fallback when the hook has nothing (77aceec5).
                    if (string.IsNullOrEmpty(slnPath) && !string.IsNullOrEmpty(explicitSolutionPath))
                    {
                        slnPath = explicitSolutionPath;
                        slnSource = "workspace_path";
                        _lastExplicitSolutionPath = explicitSolutionPath;
                    }
                    if (string.IsNullOrEmpty(slnPath) && SolutionPathProvider != null)
                    {
                        slnPath = SolutionPathProvider();
                        slnSource = "current solution";
                    }
                    if (string.IsNullOrEmpty(slnPath) && FollowedSolutionProvider != null)
                    {
                        slnPath = FollowedSolutionProvider();
                        slnSource = "the IDE's open solution";
                        fromFollowed = !string.IsNullOrEmpty(slnPath);
                    }
                    if (string.IsNullOrEmpty(slnPath) && !string.IsNullOrEmpty(_lastExplicitSolutionPath))
                    {
                        slnPath = _lastExplicitSolutionPath;
                        slnSource = "earlier workspace_path";
                    }
                    if (string.IsNullOrEmpty(slnPath))
                    {
                        // Traced, not silent. This is the standalone server's MOST LIKELY exit —
                        // launched without --solution, or in a directory holding no single .sln —
                        // and it used to return without a word. The caller then reported "LSP
                        // server failed to start" alongside a resolved server.js and node.exe and
                        // the line "the failure is in the client handshake", which is exactly
                        // wrong: no handshake was ever attempted. Measured against the real .exe
                        // (d051fbd1 item 0) — that misdiagnosis was the ENTIRE observable output.
                        LspTrace.Write("[LspService] no solution - nothing to start. "
                            + (SolutionPathProvider == null
                                ? "The host installed no SolutionPathProvider."
                                : "SolutionPathProvider returned nothing; pass --solution <path.sln> "
                                  + "or run where exactly one .sln is discoverable."));
                        return LspStartResult.Of(LspStartOutcome.NoSolution, LspStartResult.NoSolutionMessage);
                    }

                    string wsPath = Path.GetDirectoryName(slnPath);

                    // resolveSource carries the resolver's OWN account of where it looked, and is
                    // worth more on the failure branch than on the success one — so it is traced
                    // either way rather than discarded (it used to be named "ignoredSource").
                    string resolveSource;
                    string serverJs = ResolveServerPath(out resolveSource);
                    if (serverJs == null)
                    {
                        LspTrace.Write("[LspService] no server.js - nothing to start. "
                            + (string.IsNullOrEmpty(resolveSource) ? "(resolver gave no detail)" : resolveSource));
                        return new LspStartResult(LspStartOutcome.NoServer, resolveSource,
                            slnPath, slnSource, null, resolveSource);
                    }
                    LspTrace.Write("[LspService] server.js: " + serverJs + "  (source: " + resolveSource + ")");

                    // Resolve version config + redirection file ourselves (pane-independent).
                    // Either may be null — the LSP still starts; only cross-file features degrade.
                    ClarionVersionConfig versionConfig = null;
                    try
                    {
                        // ASK THE HOST FIRST, if it has an opinion. Without this hook the LSP ran
                        // its OWN ClarionVersionService.Detect() and reached its own conclusion, so
                        // a standalone server told which Clarion to use — by --clarion-version or by
                        // the solution's committed clarion-assistant.json — still handed the language
                        // server a DIFFERENT one's redirection file and library paths. Measured on a
                        // machine with 27 configured versions, where the two disagreed by two major
                        // releases (d051fbd1 item 5). The addin leaves this unset and keeps resolving
                        // for itself, which is right: there the IDE's own selection is authoritative.
                        if (VersionConfigProvider != null)
                        {
                            versionConfig = VersionConfigProvider();
                            LspTrace.Write("[LspService] version from host: "
                                + (versionConfig != null ? versionConfig.Name : "none - cross-file features will degrade"));
                        }
                        if (versionConfig == null)
                        {
                            // The SAME resolution the Assistant panel, CodeGraph indexer and library graph
                            // use: the IDE's Build > Set Clarion Version, unless CA's saved VERSION choice
                            // still applies (16d140e9). Traced with the tier that decided it.
                            var sel = EffectiveClarionVersion.Resolve();
                            versionConfig = sel.Config;
                            LspTrace.Write("[LspService] " + sel.Describe());
                        }
                    }
                    catch (Exception ex)
                    {
                        LspTrace.Write("[LspService] version/redfile resolution failed: " + ex.Message);
                    }

                    if (_client != null) _client.Dispose();
                    _client = new LspClient();

                    // Build clarion/updatePaths in the exact shape the Clarion language server
                    // expects (handshake contract from PR #37 — the redirection-file fix).
                    //
                    // Critical contract — the server resolves the effective .red with
                    // path.join(projectPath, redirectionFile) and path.join(redirectionPaths[0],
                    // redirectionFile). Therefore:
                    //   • redirectionFile MUST be a bare filename ("Clarion100.red"), NOT an
                    //     absolute path — an absolute path makes path.join produce a non-existent
                    //     target and the server floods "No valid redirection file found".
                    //   • redirectionPaths[0] MUST be the reddir DIRECTORY (global .red location).
                    //     The per-project .red is discovered via projectPaths[0] (the solution dir).
                    //
                    // If versionConfig is null we SKIP updatePaths — the LSP still starts;
                    // completion + in-buffer hover/diagnostics are context-free, only cross-file degrades.
                    try
                    {
                        if (versionConfig != null)
                        {
                            // redirectionFile: bare filename only (server path.join()s it).
                            string redirectionFileName = versionConfig.RedFileName ?? "";

                            // redirectionPaths[0]: the reddir directory. Prefer the `reddir` macro
                            // (matches the VS Code client); fall back to the install red's own dir.
                            string reddir = null;
                            if (versionConfig.Macros != null)
                                versionConfig.Macros.TryGetValue("reddir", out reddir);
                            if (string.IsNullOrEmpty(reddir) && !string.IsNullOrEmpty(versionConfig.RedFilePath))
                                reddir = Path.GetDirectoryName(versionConfig.RedFilePath);

                            var redirectionPaths = new List<string>();
                            if (!string.IsNullOrEmpty(reddir))
                                redirectionPaths.Add(reddir);

                            // libsrcPaths from ClarionProperties.xml <libsrc> (not the red file).
                            var libsrcPaths = versionConfig.LibSrcPaths ?? new List<string>();

                            // projectPaths[0] is the solution directory (project-local .red anchor).
                            var projectPaths = new List<string> { wsPath };

                            _client.SetUpdatePaths(new Dictionary<string, object>
                            {
                                { "solutionFilePath", slnPath ?? "" },
                                { "redirectionFile", redirectionFileName },
                                { "clarionVersion", versionConfig.Name ?? "" },
                                { "configuration", "Debug" },
                                { "macros", versionConfig.Macros ?? new Dictionary<string, string>() },
                                { "redirectionPaths", redirectionPaths },
                                { "libsrcPaths", libsrcPaths },
                                { "projectPaths", projectPaths },
                                { "defaultLookupExtensions", new[] { ".clw", ".inc", ".equ", ".int" } }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        LspTrace.Write("[LspService] Failed to build LSP updatePaths: " + ex.Message);
                    }

                    string wsUri = "file:///" + wsPath.Replace("\\", "/");
                    string wsName = Path.GetFileName(wsPath);
                    // Recorded BEFORE Start, which publishes LspClient.Active: a reader that sees the new
                    // Active must never pair it with the previous solution (pipeline run 1).
                    _runningSolutionPath = slnPath;
                    _runningFromFollowed = fromFollowed;
                    _runningVersionName = versionConfig != null ? versionConfig.Name : null;
                    bool started = _client.Start(serverJs, wsUri, wsName); // Start sets LspClient.Active itself
                    if (started && _client.IsRunning)
                    {
                        return new LspStartResult(LspStartOutcome.Started, "started",
                            slnPath, slnSource, serverJs, resolveSource);
                    }
                    _runningSolutionPath = null;
                    _runningFromFollowed = false;
                    if (_client.LastSpawnError != null)
                    {
                        // node.exe itself could not be launched - nothing to shake hands with.
                        return new LspStartResult(LspStartOutcome.SpawnFailed, _client.LastSpawnError,
                            slnPath, slnSource, serverJs, resolveSource);
                    }
                    return new LspStartResult(LspStartOutcome.StartFailed,
                        "LspClient.Start returned false (process launch or initialize handshake failed)",
                        slnPath, slnSource, serverJs, resolveSource);
                }
            }
            catch (Exception ex)
            {
                LspTrace.Write("[LspService] EnsureRunning failed: " + ex.Message);
                return LspStartResult.Of(LspStartOutcome.Error, ex.Message);
            }
        }

        /// <summary>
        /// Starts the LSP in the background if not already running. Safe to call from the
        /// UI thread — the start (process spawn + initialize, which can block several
        /// seconds) runs on a thread-pool thread. Idempotent: a single background start is
        /// allowed at a time, and it no-ops once the server is running.
        /// </summary>
        public static void EnsureRunningInBackground()
        {
            // Single-process: shared addin active → never start our bundled server (see EnsureRunning).
            if (SharedLspBridge.IsSharedActive) return;
            if (LspClient.Active != null && LspClient.Active.IsRunning) return;
            // Only one background start at a time — the self-heal path can call this on
            // every completion attempt, and EnsureRunning isn't safe to run concurrently.
            if (!_startGate.TryBegin()) return;
            StartOnPoolHoldingGate();
        }

        /// <summary>Run one start on the thread pool; the caller holds <see cref="_startGate"/>. A restart
        /// requested while it ran (the gate said so on release) is served by going round again.</summary>
        private static void StartOnPoolHoldingGate()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { EnsureRunning(); }
                catch (Exception ex)
                {
                    LspTrace.Write("[LspService] background EnsureRunning failed: " + ex.Message);
                }
                finally
                {
                    if (_startGate.End())
                    {
                        LspTrace.Write("[LspService] a restart was requested during this start; starting again.");
                        EnsureRunningInBackground();
                    }
                    else
                    {
                        // One re-check against the latest requested version (consumed, so a start that can
                        // never match it — e.g. no version resolvable — does not loop).
                        string wanted = Interlocked.Exchange(ref _wantedVersionName, null);
                        if (!string.IsNullOrEmpty(wanted)) RestartCore(wanted);
                    }
                }
            });
        }

        #region Server path resolution (moved here from McpToolRegistry — LspService owns the start)

        /// <summary>
        /// Resolves the LSP server.js path. Priority:
        /// 1. Settings key "Lsp.ServerPath" (manual override — always wins)
        /// 2. Bundled server relative to assembly: {assemblyDir}\lsp-server\out\server\src\server.js.
        ///    PREFERRED: it ships with the addin (deploy.ps1) and is version-locked to the
        ///    addin's features (e.g. textDocument/completion), so it must win over an
        ///    externally-installed VS Code extension that can lag behind and 404 newer methods.
        /// 3. Clarion VS Code extension install (Stable/Insiders/custom roots, excluding
        ///    tombstoned extensions, highest stable SemVer) — fallback when no bundled server.
        /// 4. Returns null ("LSP not available")
        ///
        /// The source parameter is populated with a short label describing which
        /// branch resolved the path ("manual", "bundled", "vscode-stable",
        /// "vscode-insiders", "vscode-custom") or an error message for lsp_start to surface.
        /// </summary>
        public static string ResolveServerPath(out string source)
        {
            source = null;

            // 1. Manual override — never superseded. Read from the shared settings store
            //    (pane-independent: no AssistantChatControl required).
            try
            {
                string configured = new SettingsService().Get("Lsp.ServerPath");
                if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                {
                    source = "manual (Lsp.ServerPath)";
                    return configured;
                }
            }
            catch { }

            // 2. Bundled server next to the addin — PREFERRED.
            string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string lspPath = Path.Combine(assemblyDir, "lsp-server", "out", "server", "src", "server.js");
            if (File.Exists(lspPath))
            {
                source = "bundled (lsp-server next to addin)";
                return lspPath;
            }

            // 3. VS Code extension scan — fallback only when no bundled server is present.
            string vsCodeError = null;
            string vsCodePath = DiscoverVsCodeLspServer(out source, out vsCodeError);
            if (vsCodePath != null)
                return vsCodePath;

            // 4. Nothing found. Prefer the VS Code error if the scan had one.
            if (vsCodeError != null)
                source = vsCodeError;
            return null;
        }

        /// <summary>
        /// Scans the Clarion VS Code extension install locations for the Clarion
        /// Language Server. Returns the path to server.js for the highest stable
        /// version found, or null if none resolved.
        /// </summary>
        private static string DiscoverVsCodeLspServer(out string source, out string layoutError)
        {
            source = null;
            layoutError = null;

            var candidateRoots = new List<KeyValuePair<string, string>>();
            string envOverride = Environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
            if (!string.IsNullOrEmpty(envOverride))
                candidateRoots.Add(new KeyValuePair<string, string>(envOverride, "vscode-custom"));

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidateRoots.Add(new KeyValuePair<string, string>(
                Path.Combine(userProfile, ".vscode", "extensions"), "vscode-stable"));
            candidateRoots.Add(new KeyValuePair<string, string>(
                Path.Combine(userProfile, ".vscode-insiders", "extensions"), "vscode-insiders"));

            foreach (var root in candidateRoots)
            {
                if (!Directory.Exists(root.Key)) continue;

                string serverJs = FindBestClarionExtensionInRoot(root.Key, out layoutError);
                if (serverJs != null)
                {
                    string version = ExtractVersionFromExtensionPath(serverJs);
                    source = root.Value + (version != null ? " v" + version : "");
                    return serverJs;
                }
                if (layoutError != null)
                    return null;
            }

            return null;
        }

        /// <summary>
        /// Scan a single VS Code extensions root for Clarion LSP. Honors the
        /// .obsolete tombstone file, picks the highest stable SemVer, and verifies
        /// the expected server.js layout exists.
        /// </summary>
        private static string FindBestClarionExtensionInRoot(string extensionsRoot, out string layoutError)
        {
            layoutError = null;

            HashSet<string> obsolete = LoadObsoleteSet(extensionsRoot);

            var candidates = new List<KeyValuePair<string, Version>>();
            string[] stablePrefixed;
            try
            {
                stablePrefixed = Directory.GetDirectories(extensionsRoot, "msarson.clarion-extensions-*");
            }
            catch
            {
                return null;
            }

            foreach (string dir in stablePrefixed)
            {
                string folderName = Path.GetFileName(dir);
                if (obsolete.Contains(folderName)) continue;

                Version v = ParseExtensionVersion(folderName);
                if (v == null) continue;
                candidates.Add(new KeyValuePair<string, Version>(dir, v));
            }

            if (candidates.Count == 0) return null;

            candidates.Sort((a, b) => b.Value.CompareTo(a.Value));

            foreach (var candidate in candidates)
            {
                string serverJs = Path.Combine(candidate.Key, "out", "server", "src", "server.js");
                if (File.Exists(serverJs)) return serverJs;
            }

            string highest = Path.GetFileName(candidates[0].Key);
            layoutError = "Clarion VS Code extension found (" + highest + ") but '"
                + Path.Combine("out", "server", "src", "server.js")
                + "' was not present. The extension layout may have changed in a newer version. "
                + "Set the 'Lsp.ServerPath' setting to the actual server.js location as a workaround.";
            return null;
        }

        /// <summary>
        /// Reads the .obsolete JSON file in a VS Code extensions directory (if any)
        /// and returns the set of tombstoned extension folder names.
        /// </summary>
        private static HashSet<string> LoadObsoleteSet(string extensionsRoot)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string obsoletePath = Path.Combine(extensionsRoot, ".obsolete");
                if (!File.Exists(obsoletePath)) return set;

                string json = File.ReadAllText(obsoletePath);
                var serializer = new JavaScriptSerializer();
                var map = serializer.Deserialize<Dictionary<string, object>>(json);
                if (map != null)
                {
                    foreach (var key in map.Keys)
                        set.Add(key);
                }
            }
            catch (Exception ex)
            {
                LspTrace.Write("[LspService] Failed to read .obsolete at " + extensionsRoot + ": " + ex.Message);
            }
            return set;
        }

        /// <summary>
        /// Parses the version suffix of a folder like "msarson.clarion-extensions-0.8.7"
        /// into a System.Version. Pre-release suffixes are downranked.
        /// </summary>
        private static Version ParseExtensionVersion(string folderName)
        {
            const string prefix = "msarson.clarion-extensions-";
            if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            string versionPart = folderName.Substring(prefix.Length);
            bool isPrerelease = false;
            int dashIdx = versionPart.IndexOf('-');
            if (dashIdx >= 0)
            {
                isPrerelease = true;
                versionPart = versionPart.Substring(0, dashIdx);
            }

            string[] parts = versionPart.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return null;

            try
            {
                int major = int.Parse(parts[0]);
                int minor = int.Parse(parts[1]);
                int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
                int revision = parts.Length > 3 ? int.Parse(parts[3]) : (isPrerelease ? 0 : 1);
                if (!isPrerelease && parts.Length <= 3) revision = 1;
                return new Version(major, minor, build, revision);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts the extension version from a resolved server.js path for display.
        /// </summary>
        private static string ExtractVersionFromExtensionPath(string serverJsPath)
        {
            try
            {
                string folder = Path.GetFileName(
                    Path.GetDirectoryName(
                        Path.GetDirectoryName(
                            Path.GetDirectoryName(
                                Path.GetDirectoryName(serverJsPath)))));
                const string prefix = "msarson.clarion-extensions-";
                if (folder != null && folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return folder.Substring(prefix.Length);
            }
            catch { }
            return null;
        }

        #endregion
    }
}
