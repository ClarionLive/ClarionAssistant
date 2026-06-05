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
        private static readonly object _lock = new object();
        private static int _lspStarting; // 0 = idle, 1 = a background start is in flight
        private static LspClient _client;

        /// <summary>The single shared LSP client owned by this service (may be null).</summary>
        public static LspClient Client { get { return _client; } }

        /// <summary>
        /// Starts the LSP synchronously if it isn't already running. Resolves the solution,
        /// server.js, version config and redirection file UP FRONT and starts ONCE — there
        /// is no live post-start path update. Never throws to callers.
        /// </summary>
        public static void EnsureRunning()
        {
            try
            {
                // Fast pre-check against the process-wide Active client (set by LspClient.Start).
                if (LspClient.Active != null && LspClient.Active.IsRunning) return;

                lock (_lock)
                {
                    // Re-check inside the lock — another thread may have started it.
                    if (_client != null && _client.IsRunning) return;

                    string slnPath = EditorService.GetOpenSolutionPath();
                    if (string.IsNullOrEmpty(slnPath)) return; // can't start without a solution

                    string wsPath = Path.GetDirectoryName(slnPath);

                    string ignoredSource;
                    string serverJs = ResolveServerPath(out ignoredSource);
                    if (serverJs == null) return;

                    // Resolve version config + redirection file ourselves (pane-independent).
                    // Either may be null — the LSP still starts; only cross-file features degrade.
                    ClarionVersionConfig versionConfig = null;
                    try
                    {
                        var versionInfo = ClarionVersionService.Detect();
                        if (versionInfo != null)
                        {
                            versionConfig = versionInfo.GetCurrentConfig();

                            // Honour a saved user version override (same key AssistantChatControl uses).
                            try
                            {
                                string overrideName = new SettingsService().Get("Clarion.Version.Override");
                                if (!string.IsNullOrEmpty(overrideName) && versionInfo.Versions != null)
                                {
                                    var ov = versionInfo.Versions.Find(v => v.Name == overrideName);
                                    if (ov != null) versionConfig = ov;
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[LspService] version/redfile resolution failed: " + ex.Message);
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
                        Debug.WriteLine("[LspService] Failed to build LSP updatePaths: " + ex.Message);
                    }

                    string wsUri = "file:///" + wsPath.Replace("\\", "/");
                    string wsName = Path.GetFileName(wsPath);
                    _client.Start(serverJs, wsUri, wsName); // Start sets LspClient.Active itself
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LspService] EnsureRunning failed: " + ex.Message);
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
            if (LspClient.Active != null && LspClient.Active.IsRunning) return;
            // Only one background start at a time — the self-heal path can call this on
            // every completion attempt, and EnsureRunning isn't safe to run concurrently.
            if (Interlocked.CompareExchange(ref _lspStarting, 1, 0) != 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { EnsureRunning(); }
                catch (Exception ex)
                {
                    Debug.WriteLine("[LspService] background EnsureRunning failed: " + ex.Message);
                }
                finally { Interlocked.Exchange(ref _lspStarting, 0); }
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
                Debug.WriteLine("[LspService] Failed to read .obsolete at " + extensionsRoot + ": " + ex.Message);
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
