using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// LSP client that communicates with the Clarion Language Server via stdio.
    /// Sends JSON-RPC requests with Content-Length framing, reads responses.
    /// </summary>
    public class LspClient : IDisposable
    {
        private Process _process;
        private readonly object _writeLock = new object();
        private readonly object _readLock = new object();
        private int _nextId = 1;

        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // Pending responses keyed by request ID
        private readonly Dictionary<int, string> _responses = new Dictionary<int, string>();
        private readonly AutoResetEvent _responseReceived = new AutoResetEvent(false);
        private Thread _readerThread;
        private volatile bool _running;
        private Dictionary<string, object> _pendingUpdatePaths;

        // Tracks the last file path any LSP tool operated on. Used by the header
        // diagnostics pill to know which file's diagnostics to display.
        private string _lastActiveFilePath;
        public string LastActiveFilePath { get { return _lastActiveFilePath; } }

        /// <summary>
        /// Fired on each LSP request with (toolName, targetDescription).
        /// Used by the header activity strip to show "hover: UpdateProducts" etc.
        /// Fires on the calling thread — UI consumers must marshal via BeginInvoke.
        /// </summary>
        public event Action<string, string> OnLspRequest;

        // Diagnostics cache — populated by textDocument/publishDiagnostics notifications.
        // Keyed by canonical file URI (always built via FilePathToUri to avoid encoding drift).
        // LRU-bounded at 50 entries; oldest-by-LastUpdateTicks is evicted on insert.
        private const int MaxCachedDiagnosticFiles = 50;
        private readonly Dictionary<string, DiagnosticSet> _diagnostics =
            new Dictionary<string, DiagnosticSet>(StringComparer.OrdinalIgnoreCase);
        private readonly object _diagnosticsLock = new object();

        // Debug telemetry — populated by ReadLoop/stderr handler. Used by the
        // lsp_debug_status tool to expose what the server is actually sending.
        private readonly Dictionary<string, int> _notificationCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly object _debugLock = new object();
        private const int MaxStderrBuffer = 100;
        private readonly Queue<string> _stderrBuffer = new Queue<string>();
        private string _lastRawNotificationPreview;

        public bool IsRunning { get { return _running && _process != null && !_process.HasExited; } }

        /// <summary>
        /// The most-recently-started LspClient. The app runs a single language
        /// server, so embeditor features that aren't owned by McpToolRegistry
        /// (e.g. the embeditor completion provider) can reach the live, initialized
        /// client through this. Set on a successful Start(); cleared on Stop().
        ///
        /// Backed by a volatile field so the UI-thread reader sees a consistent
        /// publication of the reference across Start()/Stop() on another thread.
        /// (Consumers still snapshot it into a local and re-check IsRunning.)
        /// </summary>
        private static volatile LspClient _active;
        public static LspClient Active { get { return _active; } private set { _active = value; } }

        /// <summary>
        /// Diagnostic string describing the outcome of the most recent GetCompletion call
        /// (sent / timeout / parsed N / etc.). Surfaced in the embeditor completion-test
        /// result file to diagnose why the LSP source contributed 0 items.
        /// </summary>
        public string LastCompletionDiagnostic { get; private set; }

        // Hard caps so a misbehaving/compromised local server or a corrupt CodeGraph DB
        // can't hang or exhaust the IDE through an unbounded completion payload.
        private const int MaxCompletionItems = 5000;
        private const int MaxLabelLen = 256;
        private const int MaxTextLen = 4096;

        private static string Cap(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max);
        }

        /// <summary>
        /// Set clarion/updatePaths data to be sent after LSP initialization.
        /// Must be called before Start().
        /// </summary>
        public void SetUpdatePaths(Dictionary<string, object> updatePaths)
        {
            _pendingUpdatePaths = updatePaths;
        }

        /// <summary>
        /// Start the LSP server and initialize the protocol.
        /// </summary>
        public bool Start(string serverJsPath, string workspaceUri, string workspaceName)
        {
            if (_running) return true;

            if (!File.Exists(serverJsPath))
                return false;

            try
            {
                // Resolve node.exe in order:
                // 1. Bundled next to our LSP distribution (when shipped in installer)
                // 2. VS Code's bundled node from the Stable install
                // 3. System PATH
                string nodeExe = ResolveNodeExe(serverJsPath);

                System.Diagnostics.Debug.WriteLine("[LSP] Starting: " + nodeExe + " \"" + serverJsPath + "\" --stdio");

                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = nodeExe,
                        Arguments = "\"" + serverJsPath + "\" --stdio",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };

                // Capture stderr for diagnostics — ring buffer + Debug output
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    System.Diagnostics.Debug.WriteLine("[LSP stderr] " + e.Data);
                    lock (_debugLock)
                    {
                        _stderrBuffer.Enqueue(e.Data);
                        while (_stderrBuffer.Count > MaxStderrBuffer)
                            _stderrBuffer.Dequeue();
                    }
                };

                _process.Start();
                _process.BeginErrorReadLine();
                _running = true;

                System.Diagnostics.Debug.WriteLine("[LSP] Process started, PID=" + _process.Id);

                // Start reader thread
                _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "LSP-Reader" };
                _readerThread.Start();

                // Send initialize
                var initParams = new Dictionary<string, object>
                {
                    { "processId", Process.GetCurrentProcess().Id },
                    { "capabilities", new Dictionary<string, object>() },
                    { "rootUri", workspaceUri },
                    { "workspaceFolders", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "uri", workspaceUri },
                                { "name", workspaceName }
                            }
                        }
                    }
                };

                System.Diagnostics.Debug.WriteLine("[LSP] Sending initialize request...");
                var initResult = SendRequest("initialize", initParams, 15000);
                if (initResult == null)
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] Initialize timed out or returned null");
                    // Check if process crashed
                    if (_process.HasExited)
                        System.Diagnostics.Debug.WriteLine("[LSP] Process exited with code: " + _process.ExitCode);
                    Stop();
                    return false;
                }

                System.Diagnostics.Debug.WriteLine("[LSP] Initialize succeeded");

                // Send initialized notification
                SendNotification("initialized", new Dictionary<string, object>());

                // Send clarion/updatePaths if provided — required for cross-file LSP features
                if (_pendingUpdatePaths != null)
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] Sending clarion/updatePaths...");
                    SendNotification("clarion/updatePaths", _pendingUpdatePaths);
                    _pendingUpdatePaths = null;
                }

                // Give the server a moment to finish initialization
                Thread.Sleep(1000);

                System.Diagnostics.Debug.WriteLine("[LSP] Ready");
                Active = this;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] Start failed: " + ex.GetType().Name + ": " + ex.Message);
                System.Diagnostics.Debug.WriteLine("[LSP] Stack: " + ex.StackTrace);
                Stop();
                return false;
            }
        }

        /// <summary>
        /// Resolves node.exe for spawning the LSP server. VS Code cannot be used as a
        /// fallback because it embeds node inside Electron (Code.exe) rather than
        /// shipping a standalone binary. The Clarion VS Code extension also does not
        /// bundle its own node. So if a user has only VS Code + the extension, they
        /// still need a real Node.js install somewhere.
        ///
        /// Tries in order:
        /// (1) node.exe bundled next to the LSP distribution (installer-shipped layout),
        /// (2) Node.js official installer default at C:\Program Files\nodejs\node.exe,
        /// (3) Node.js 32-bit installer default at C:\Program Files (x86)\nodejs\node.exe,
        /// (4) Claude Code standalone bundled node at %USERPROFILE%\.claude\local\node.exe,
        /// (5) "node" on the system PATH (last resort — Process.Start will fail with a
        ///     clear error if no node is installed at all).
        /// </summary>
        private static string ResolveNodeExe(string serverJsPath)
        {
            try
            {
                string lspDir = Path.GetDirectoryName(serverJsPath);
                string lspRoot = Path.GetFullPath(Path.Combine(lspDir, "..", "..", ".."));
                string bundled = Path.Combine(lspRoot, "node.exe");
                System.Diagnostics.Debug.WriteLine("[LSP] Looking for bundled node.exe at: " + bundled);
                if (File.Exists(bundled)) return bundled;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] Bundled node.exe lookup failed: " + ex.Message);
            }

            try
            {
                string[] candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "local", "node.exe"),
                };

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        System.Diagnostics.Debug.WriteLine("[LSP] Using node.exe at: " + candidate);
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] node.exe fallback search failed: " + ex.Message);
            }

            System.Diagnostics.Debug.WriteLine("[LSP] No bundled node.exe found, falling back to PATH");
            return "node";
        }

        /// <summary>
        /// Fast, no-handshake kill of the live LSP node.exe (and any child tree) for the IDE
        /// shutdown fast-path. Unlike Stop(), this skips the graceful shutdown/exit JSON-RPC
        /// notifications and their ~400ms of Thread.Sleeps — at shutdown we just need the node
        /// handle GONE so it can't keep the IDE alive. Best-effort; swallows all errors.
        ///
        /// Called (bounded) from ShutdownService.Terminate(). Operates on the static Active
        /// client; no-op if no server is running. SharedLspBridge spawns no process of its own
        /// (it only WebSocket-connects to the shared server), so this is the only node owner.
        /// </summary>
        public static void KillForShutdown()
        {
            var inst = Active;
            if (inst == null) return;
            inst._running = false;

            // Claim the Process atomically so a concurrent Stop() (graceful path, reachable during teardown)
            // can't also operate on the same handle — once we've taken it, Stop()'s _process null-guards make
            // it a no-op. Prevents racing .Kill()/.HasExited on one Process object.
            var p = Interlocked.Exchange(ref inst._process, null);
            if (p == null) { if (ReferenceEquals(Active, inst)) Active = null; return; }

            try
            {
                if (!p.HasExited)
                {
                    int pid = p.Id;
                    // Kill the whole tree first (node can spawn workers); taskkill /T reaps children. Resolve
                    // taskkill by its ABSOLUTE System32 path — never the bare name — so a rogue taskkill.exe on
                    // PATH or the current directory can't be executed at shutdown (CWE-426 untrusted search path).
                    try
                    {
                        string taskkillExe = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                        var psi = new ProcessStartInfo(taskkillExe, "/PID " + pid + " /T /F")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        var tk = Process.Start(psi);
                        if (tk != null) tk.WaitForExit(1500);
                    }
                    catch { }
                    // Fallback: if taskkill didn't take, kill the root directly.
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    // taskkill exiting != the tree is gone. Confirm the ROOT actually exited; if it didn't,
                    // log it so a real leak is visible in verify (the OS still reclaims at process exit).
                    try { p.WaitForExit(500); } catch { }
                    try
                    {
                        if (!p.HasExited)
                            System.Diagnostics.Debug.WriteLine("[Shutdown] LSP kill UNCONFIRMED — node pid " + pid + " may survive");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shutdown] LSP KillForShutdown: " + ex.Message);
            }

            // Deliberately skip the diagnostics ManualResetEvent cleanup that Stop() does — the process is
            // force-exiting and the OS reclaims those handles; the graceful Stop() path is where that matters.
            if (ReferenceEquals(Active, inst)) Active = null;
        }

        public void Stop()
        {
            _running = false;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    SendNotification("shutdown", null);
                    Thread.Sleep(200);
                    SendNotification("exit", null);
                    Thread.Sleep(200);
                    if (!_process.HasExited) _process.Kill();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] Stop failed: " + ex.Message);
            }

            _process = null;
            if (ReferenceEquals(Active, this)) Active = null;

            // Release diagnostic events.
            lock (_diagnosticsLock)
            {
                foreach (var set in _diagnostics.Values)
                {
                    try { set.Ready.Dispose(); } catch { }
                }
                _diagnostics.Clear();
            }
        }

        #region LSP Requests

        /// <summary>
        /// textDocument/definition - find where a symbol is defined.
        /// </summary>
        public Dictionary<string, object> GetDefinition(string filePath, int line, int character)
        {
            TrackRequest("definition", filePath);
            return SendTextDocumentPositionRequest("textDocument/definition", filePath, line, character);
        }

        /// <summary>
        /// textDocument/references - find all references to a symbol.
        /// </summary>
        public Dictionary<string, object> GetReferences(string filePath, int line, int character)
        {
            TrackRequest("references", filePath);
            var parms = BuildTextDocumentPosition(filePath, line, character);
            parms["context"] = new Dictionary<string, object> { { "includeDeclaration", true } };
            return SendRequest("textDocument/references", parms);
        }

        /// <summary>
        /// textDocument/hover - get hover info (type, signature, docs).
        /// </summary>
        public Dictionary<string, object> GetHover(string filePath, int line, int character, string bufferText = null)
        {
            TrackRequest("hover", filePath);
            // Buffer-aware like GetCompletion: sync the live embeditor text so hover resolves
            // against current content; else open from disk. Short timeout (UI-thread call).
            try
            {
                if (!string.IsNullOrEmpty(bufferText)) EnsureDocumentOpenWithText(filePath, bufferText);
                else EnsureDocumentOpen(filePath);
            }
            catch { }
            var parms = BuildTextDocumentPosition(filePath, line, character);
            return SendRequest("textDocument/hover", parms, 1500);
        }

        /// <summary>
        /// textDocument/signatureHelp - parameter hints for the call site at the position. Buffer-aware
        /// like GetHover so the hints resolve against live embeditor/editor content.
        /// </summary>
        public Dictionary<string, object> GetSignatureHelp(string filePath, int line, int character, string bufferText = null)
        {
            TrackRequest("signatureHelp", filePath);
            try
            {
                if (!string.IsNullOrEmpty(bufferText)) EnsureDocumentOpenWithText(filePath, bufferText);
                else EnsureDocumentOpen(filePath);
            }
            catch { }
            var parms = BuildTextDocumentPosition(filePath, line, character);
            return SendRequest("textDocument/signatureHelp", parms, 1500);
        }

        /// <summary>
        /// textDocument/documentSymbol - get all symbols in a document.
        /// </summary>
        public Dictionary<string, object> GetDocumentSymbols(string filePath) { return GetDocumentSymbols(filePath, null); }

        /// <summary>
        /// textDocument/documentSymbol. When <paramref name="bufferText"/> is supplied (e.g. the Modern
        /// Embeditor's in-memory buffer, which isn't on disk), it is synced to the server first so symbols
        /// reflect the live content.
        /// </summary>
        public Dictionary<string, object> GetDocumentSymbols(string filePath, string bufferText)
        {
            TrackRequest("symbols", filePath);
            try
            {
                if (!string.IsNullOrEmpty(bufferText)) EnsureDocumentOpenWithText(filePath, bufferText);
                else EnsureDocumentOpen(filePath);
            }
            catch { }
            var parms = new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object> { { "uri", FilePathToUri(filePath) } } }
            };
            return SendRequest("textDocument/documentSymbol", parms, 3000);
        }

        /// <summary>
        /// workspace/symbol - search for symbols across the workspace.
        /// </summary>
        public Dictionary<string, object> FindWorkspaceSymbol(string query)
        {
            TrackRequest("find-symbol", query);
            var parms = new Dictionary<string, object> { { "query", query } };
            return SendRequest("workspace/symbol", parms);
        }

        /// <summary>
        /// textDocument/rename - asks the server for a workspace edit that would
        /// rename the symbol at the given position. Returns the raw LSP WorkspaceEdit
        /// result — the caller is responsible for applying the edits (and MUST seek
        /// developer approval first per CLAUDE.md rule #9).
        /// </summary>
        public Dictionary<string, object> Rename(string filePath, int line, int character, string newName)
        {
            TrackRequest("rename", filePath);
            EnsureDocumentOpen(filePath);
            var parms = BuildTextDocumentPosition(filePath, line, character);
            parms["newName"] = newName;
            return SendRequest("textDocument/rename", parms, 8000);
        }

        /// <summary>
        /// textDocument/completion - get completion items at a position. Returns a
        /// parsed (possibly empty) list. The server tolerates a missing document and
        /// still returns context-free language items, so this works for the embeditor
        /// case where the live buffer is not identical to any file on disk.
        ///
        /// Uses a short timeout because it is called synchronously from the editor's
        /// completion code path (UI thread).
        /// </summary>
        public List<CompletionItemInfo> GetCompletion(string filePath, int line, int character, int timeoutMs = 2500, string bufferText = null)
        {
            var items = new List<CompletionItemInfo>();
            if (!IsRunning) { LastCompletionDiagnostic = "client not running"; return items; }
            TrackRequest("completion", filePath);

            // If the caller supplies the live buffer (the embeditor case — its generated
            // source isn't on disk), sync that text to the server so it can tokenize and
            // do scope-aware completion (in-scope locals/params). Otherwise fall back to
            // opening from disk. Server tolerates neither being present (context-free set).
            try
            {
                if (!string.IsNullOrEmpty(bufferText)) EnsureDocumentOpenWithText(filePath, bufferText);
                else EnsureDocumentOpen(filePath);
            }
            catch { }

            var parms = BuildTextDocumentPosition(filePath, line, character);
            var uri = ((Dictionary<string, object>)parms["textDocument"])["uri"] as string;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = SendRequest("textDocument/completion", parms, timeoutMs);
            sw.Stop();

            if (response == null)
            {
                LastCompletionDiagnostic = "no response after " + sw.ElapsedMilliseconds + "ms (timeout=" + timeoutMs + "ms); uri=" + uri;
                return items;
            }
            if (response.ContainsKey("error"))
            {
                LastCompletionDiagnostic = "server error: " + _serializer.Serialize(response["error"]);
                return items;
            }
            if (!response.ContainsKey("result") || response["result"] == null)
            {
                LastCompletionDiagnostic = "no 'result' (keys: " + string.Join(",", new List<string>(response.Keys).ToArray()) + ") in " + sw.ElapsedMilliseconds + "ms; uri=" + uri;
                return items;
            }

            // result is either CompletionItem[] or a CompletionList { items: [...] }.
            object result = response["result"];
            var rawItems = result as System.Collections.ArrayList;
            if (rawItems == null)
            {
                var asList = result as Dictionary<string, object>;
                if (asList != null && asList.ContainsKey("items"))
                    rawItems = asList["items"] as System.Collections.ArrayList;
            }
            if (rawItems == null)
            {
                LastCompletionDiagnostic = "result not list (type=" + result.GetType().FullName + ") in " + sw.ElapsedMilliseconds + "ms";
                return items;
            }
            LastCompletionDiagnostic = "raw=" + rawItems.Count + " in " + sw.ElapsedMilliseconds + "ms; uri=" + uri;

            foreach (var obj in rawItems)
            {
                // Hard item cap — bound the work done on the (UI) calling thread even if
                // the server returns an enormous list.
                if (items.Count >= MaxCompletionItems) break;

                var d = obj as Dictionary<string, object>;
                if (d == null) continue;

                var ci = new CompletionItemInfo();
                if (d.ContainsKey("label")) ci.Label = Cap(d["label"] as string, MaxLabelLen);
                if (d.ContainsKey("kind")) { try { ci.Kind = Convert.ToInt32(d["kind"]); } catch { } }
                if (d.ContainsKey("detail")) ci.Detail = Cap(d["detail"] as string, MaxTextLen);
                if (d.ContainsKey("insertText")) ci.InsertText = Cap(d["insertText"] as string, MaxTextLen);
                if (d.ContainsKey("documentation"))
                {
                    var docVal = d["documentation"];
                    var docStr = docVal as string;
                    if (docStr != null) ci.Documentation = Cap(docStr, MaxTextLen);
                    else
                    {
                        var md = docVal as Dictionary<string, object>;
                        if (md != null && md.ContainsKey("value")) ci.Documentation = Cap(md["value"] as string, MaxTextLen);
                    }
                }

                if (!string.IsNullOrEmpty(ci.Label)) items.Add(ci);
            }
            return items;
        }

        /// <summary>
        /// clarion/findFile — ask the server to resolve a filename to its absolute path using
        /// the server's full, config-aware redirection logic (active configuration section +
        /// Common, with libsrc fallback). This is the authoritative resolver: prefer it over any
        /// local Common-only redirection lookup, which misses config-specific paths like
        /// .\genfiles\src.
        ///
        /// Returns the inner { path, source } result dictionary, or null when the server is down,
        /// no solution is loaded server-side, or the file isn't found.
        /// </summary>
        public Dictionary<string, object> FindFile(string filename, string sourceUri = null)
        {
            if (!IsRunning || string.IsNullOrEmpty(filename)) return null;
            TrackRequest("findFile", filename);

            var parms = new Dictionary<string, object> { { "filename", filename } };
            if (!string.IsNullOrEmpty(sourceUri)) parms["sourceUri"] = sourceUri;

            var response = SendRequest("clarion/findFile", parms);
            var result = ExtractResultObject(response) as Dictionary<string, object>;
            if (result == null) return null;

            // The server returns { path: "", source: "" } on a miss — normalize that to null.
            object pathObj;
            if (!result.TryGetValue("path", out pathObj) || string.IsNullOrEmpty(pathObj as string))
                return null;

            return result;
        }

        /// <summary>
        /// clarion/getSearchPaths — config-aware search directories for a project + extension
        /// (merges the active configuration section with Common, the way the compiler does).
        /// Requires the server-side solution to be loaded and a valid projectName. Returns an
        /// empty list when the server is down or the project isn't found.
        /// </summary>
        public List<string> GetServerSearchPaths(string projectName, string extension)
        {
            var paths = new List<string>();
            if (!IsRunning || string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(extension))
                return paths;

            TrackRequest("getSearchPaths", projectName);
            var parms = new Dictionary<string, object>
            {
                { "projectName", projectName },
                { "extension", extension }
            };

            var response = SendRequest("clarion/getSearchPaths", parms);
            var resultObj = ExtractResultObject(response);

            // JavaScriptSerializer deserializes a JSON array to ArrayList/object[] — both are
            // IEnumerable. Guard against a string (also IEnumerable) just in case.
            if (resultObj is System.Collections.IEnumerable && !(resultObj is string))
            {
                foreach (var item in (System.Collections.IEnumerable)resultObj)
                {
                    string s = item as string;
                    if (!string.IsNullOrEmpty(s)) paths.Add(s);
                }
            }

            return paths;
        }

        /// <summary>
        /// Pulls the JSON-RPC "result" payload out of a SendRequest response. Returns null if the
        /// response is null, carried an "error", or had no "result".
        /// </summary>
        private static object ExtractResultObject(Dictionary<string, object> response)
        {
            if (response == null) return null;
            if (response.ContainsKey("error")) return null;
            object result;
            return response.TryGetValue("result", out result) ? result : null;
        }

        private void TrackRequest(string tool, string target)
        {
            if (!string.IsNullOrEmpty(target))
            {
                // Extract just the filename for display, keep full path for lookup
                _lastActiveFilePath = target;
            }
            try { OnLspRequest?.Invoke(tool, System.IO.Path.GetFileName(target ?? "")); }
            catch { }
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Triggers fresh analysis of the given file and waits for the server to
        /// publish diagnostics. If the file isn't open yet, sends didOpen; if it is,
        /// sends didChange with the current disk contents so stale cached diagnostics
        /// from a previous version don't satisfy the wait.
        ///
        /// Returns a DiagnosticWaitResult where Pending=false means the server
        /// authoritatively reported results (possibly an empty list = clean file),
        /// and Pending=true means the server didn't respond within timeoutMs.
        /// </summary>
        public DiagnosticWaitResult GetDiagnostics(string filePath, int timeoutMs = 3000)
        {
            var result = new DiagnosticWaitResult { Entries = new List<DiagnosticEntry>(), Pending = true };
            if (!IsRunning || string.IsNullOrEmpty(filePath)) return result;
            TrackRequest("diagnostics", filePath);

            // Trigger server analysis before waiting. We always force a new publish
            // so Claude sees the state of the file as of this call — stale cached
            // diagnostics from before the last edit are not good enough.
            try
            {
                if (_openDocuments.ContainsKey(filePath))
                    SendDidChangeFromDisk(filePath);
                else
                    EnsureDocumentOpen(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] GetDiagnostics trigger failed: " + ex.Message);
                return result;
            }

            return WaitForDiagnostics(filePath, timeoutMs, forceRefresh: true);
        }

        /// <summary>
        /// Returns a snapshot of debug telemetry for the lsp_debug_status tool:
        /// process state, notification method counts, diagnostics cache state, and
        /// the last N lines of server stderr. Used to debug why diagnostics aren't
        /// arriving on a live server without needing DebugView access.
        /// </summary>
        public Dictionary<string, object> GetDebugStatus()
        {
            var result = new Dictionary<string, object>();
            result["isRunning"] = IsRunning;
            result["processId"] = _process != null && !_process.HasExited ? _process.Id : -1;

            lock (_debugLock)
            {
                var counts = new Dictionary<string, object>();
                foreach (var kv in _notificationCounts)
                    counts[kv.Key] = kv.Value;
                result["notificationCounts"] = counts;
                result["lastNotificationPreview"] = _lastRawNotificationPreview ?? "(none yet)";
                result["stderrTail"] = new List<string>(_stderrBuffer);
            }

            lock (_diagnosticsLock)
            {
                var cache = new List<Dictionary<string, object>>();
                foreach (var kv in _diagnostics)
                {
                    cache.Add(new Dictionary<string, object>
                    {
                        { "uri", kv.Key },
                        { "wasPublished", kv.Value.WasPublished },
                        { "entryCount", kv.Value.Entries.Count }
                    });
                }
                result["diagnosticsCache"] = cache;
            }

            // Currently-open documents (tracked by EnsureDocumentOpen / didChange)
            var openDocs = new List<string>();
            foreach (var kv in _openDocuments)
                openDocs.Add(kv.Key);
            result["openDocuments"] = openDocs;

            return result;
        }

        /// <summary>
        /// Returns the current diagnostics cached for the given file. Does NOT
        /// wait or trigger re-analysis — intended for UI polling (Phase 3a) where
        /// we just want a fast read of whatever the server has told us so far.
        /// Returns null if no publishDiagnostics has arrived yet for this file.
        /// </summary>
        public List<DiagnosticEntry> GetCachedDiagnostics(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string key = FilePathToUri(filePath);

            lock (_diagnosticsLock)
            {
                DiagnosticSet set;
                if (!_diagnostics.TryGetValue(key, out set)) return null;
                if (!set.WasPublished) return null;
                // Return a snapshot to avoid cross-thread mutation of the caller's list.
                return new List<DiagnosticEntry>(set.Entries);
            }
        }

        /// <summary>
        /// Waits up to timeoutMs for a publishDiagnostics notification to arrive
        /// for the given file. If `forceRefresh` is true, the wait ignores any
        /// previously-cached publish and only returns when a NEW publish arrives
        /// after the call begins (use this when you know the file content has
        /// changed and want a fresh analysis).
        ///
        /// Returns a result with `Pending=false` on signal and `Pending=true` on
        /// timeout. Entries may be empty on a signal result — that means the
        /// server authoritatively reported zero diagnostics (a clean file).
        ///
        /// The caller is responsible for triggering analysis beforehand (didOpen
        /// or didChange) — this method only waits on the publish event.
        /// </summary>
        public DiagnosticWaitResult WaitForDiagnostics(string filePath, int timeoutMs, bool forceRefresh)
        {
            var result = new DiagnosticWaitResult { Entries = new List<DiagnosticEntry>(), Pending = true };
            if (string.IsNullOrEmpty(filePath)) return result;

            string key = FilePathToUri(filePath);

            DiagnosticSet set;
            lock (_diagnosticsLock)
            {
                if (!_diagnostics.TryGetValue(key, out set))
                {
                    set = new DiagnosticSet();
                    _diagnostics[key] = set;
                    EvictOldestIfFull_NoLock();
                }

                if (forceRefresh)
                {
                    // Clear the event so we wait strictly for a publish that happens
                    // AFTER this call — cached diagnostics from before the content
                    // changed must not satisfy the wait.
                    try { set.Ready.Reset(); } catch { }
                }
                else if (set.WasPublished)
                {
                    // Non-force path with an already-cached publish — return immediately.
                    result.Entries = new List<DiagnosticEntry>(set.Entries);
                    result.Pending = false;
                    return result;
                }
            }

            // Wait outside the lock so publish handlers aren't blocked.
            bool signaled;
            try
            {
                signaled = set.Ready.Wait(timeoutMs);
            }
            catch (ObjectDisposedException)
            {
                // Set was evicted between registration and wait — report as pending so the caller can retry.
                return result;
            }

            // Always check the cache — even on timeout. The publish may have arrived
            // before our forceRefresh Reset() cleared the event (race between the
            // initial didOpen publish and the re-trigger). Returning pending:true when
            // the cache has 44 valid entries is the bug this fixes.
            lock (_diagnosticsLock)
            {
                if (_diagnostics.TryGetValue(key, out set) && set.WasPublished)
                {
                    result.Entries = new List<DiagnosticEntry>(set.Entries);
                    result.Pending = false;
                }
            }

            return result;
        }

        #endregion

        #region Document Management

        // Tracks open documents and their current textDocument version number (LSP protocol
        // requires version to increase monotonically across didOpen → didChange for a URI).
        private readonly Dictionary<string, int> _openDocuments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Guards the document-sync state (_openDocuments + _lastSyncedHash) AND the matching
        // didOpen/didChange notification so version assignment and the send are atomic. Multiple
        // background threads sync the SAME synthetic .clw URI concurrently — Modern Embeditor hover
        // (HandleHover), completion, document-symbols, AND the 600ms-debounced diagnostics pass all
        // run on Task.Run threads. Without this, two threads computed the same nextVersion and sent
        // out-of-order didChanges, desyncing the server's copy so hover resolved against a stale
        // buffer and returned nothing. (Regression from the diagnostics feature; see ModernEmbeditor.)
        private readonly object _docSyncLock = new object();

        private void EnsureDocumentOpen(string filePath)
        {
            lock (_docSyncLock)
            {
                if (_openDocuments.ContainsKey(filePath)) return;
                if (!File.Exists(filePath)) return;

                string uri = FilePathToUri(filePath);
                string content = File.ReadAllText(filePath);

                string ext = Path.GetExtension(filePath).ToLower();
                string languageId = ext == ".inc" || ext == ".clw" || ext == ".equ" ? "clarion" : "plaintext";

                var parms = new Dictionary<string, object>
                {
                    { "textDocument", new Dictionary<string, object>
                        {
                            { "uri", uri },
                            { "languageId", languageId },
                            { "version", 1 },
                            { "text", content }
                        }
                    }
                };

                SendNotification("textDocument/didOpen", parms);
                _openDocuments[filePath] = 1;
            }
        }

        /// <summary>
        /// Opens (or, if already open, full-replaces) the document on the server using
        /// caller-supplied text rather than disk contents. Used for the embeditor, whose
        /// live generated buffer is not on disk — keeps the server's copy in sync with the
        /// editor so scope-aware completion sees the current locals.
        /// </summary>
        // Hash of the last text we synced per file, so repeated completion/hover calls on an
        // unchanged buffer skip the didChange (and the server-side re-tokenization it triggers).
        private readonly Dictionary<string, int> _lastSyncedHash =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private void EnsureDocumentOpenWithText(string filePath, string text)
        {
            lock (_docSyncLock)
            {
                string uri = FilePathToUri(filePath);
                int hash = (text != null ? text.Length : 0) ^ (text != null ? text.GetHashCode() : 0);
                int currentVersion;
                if (_openDocuments.TryGetValue(filePath, out currentVersion))
                {
                    // Unchanged since last sync → nothing to send (avoids needless re-tokenize).
                    int lastHash;
                    if (_lastSyncedHash.TryGetValue(filePath, out lastHash) && lastHash == hash)
                        return;

                    int nextVersion = currentVersion + 1;
                    var changes = new System.Collections.ArrayList
                    {
                        new Dictionary<string, object> { { "text", text } }
                    };
                    var changeParms = new Dictionary<string, object>
                    {
                        { "textDocument", new Dictionary<string, object> { { "uri", uri }, { "version", nextVersion } } },
                        { "contentChanges", changes }
                    };
                    SendNotification("textDocument/didChange", changeParms);
                    _openDocuments[filePath] = nextVersion;
                    _lastSyncedHash[filePath] = hash;
                    return;
                }

                var openParms = new Dictionary<string, object>
                {
                    { "textDocument", new Dictionary<string, object>
                        {
                            { "uri", uri },
                            { "languageId", "clarion" },
                            { "version", 1 },
                            { "text", text }
                        }
                    }
                };
                SendNotification("textDocument/didOpen", openParms);
                _openDocuments[filePath] = 1;
                _lastSyncedHash[filePath] = hash;
            }
        }

        /// <summary>
        /// Public: sync an in-memory buffer (e.g. the live embeditor) to the server. Triggers
        /// re-validation and a textDocument/publishDiagnostics for the URI. No-op if the text
        /// is unchanged since the last sync, so it's cheap to call on a UI timer.
        /// </summary>
        public void EnsureBufferSynced(string filePath, string bufferText)
        {
            if (!IsRunning || string.IsNullOrEmpty(filePath) || bufferText == null) return;
            try { EnsureDocumentOpenWithText(filePath, bufferText); } catch { }
        }

        /// <summary>
        /// Send a full-document textDocument/didChange with the current file contents
        /// from disk. Used to force the server to re-analyze a file that's already open
        /// after it may have changed (e.g., after write_embed_content or an external edit).
        /// If the file hasn't been opened yet, falls through to EnsureDocumentOpen instead.
        /// </summary>
        private void SendDidChangeFromDisk(string filePath)
        {
            if (!File.Exists(filePath)) return;

            lock (_docSyncLock) // reentrant: EnsureDocumentOpen also takes _docSyncLock
            {
                int currentVersion;
                if (!_openDocuments.TryGetValue(filePath, out currentVersion))
                {
                    EnsureDocumentOpen(filePath);
                    return;
                }

                string uri = FilePathToUri(filePath);
                string content = File.ReadAllText(filePath);
                int nextVersion = currentVersion + 1;

                // LSP TextDocumentContentChangeEvent without `range` = full document replacement.
                var changes = new System.Collections.ArrayList
                {
                    new Dictionary<string, object> { { "text", content } }
                };

                var parms = new Dictionary<string, object>
                {
                    { "textDocument", new Dictionary<string, object>
                        {
                            { "uri", uri },
                            { "version", nextVersion }
                        }
                    },
                    { "contentChanges", changes }
                };

                SendNotification("textDocument/didChange", parms);
                _openDocuments[filePath] = nextVersion;
            }
        }

        #endregion

        #region JSON-RPC Transport

        private Dictionary<string, object> SendTextDocumentPositionRequest(string method, string filePath, int line, int character)
        {
            EnsureDocumentOpen(filePath);
            var parms = BuildTextDocumentPosition(filePath, line, character);
            return SendRequest(method, parms);
        }

        private Dictionary<string, object> BuildTextDocumentPosition(string filePath, int line, int character)
        {
            return new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object> { { "uri", FilePathToUri(filePath) } } },
                { "position", new Dictionary<string, object> { { "line", line }, { "character", character } } }
            };
        }

        private Dictionary<string, object> SendRequest(string method, Dictionary<string, object> parms, int timeoutMs = 5000)
        {
            if (!_running || _process == null || _process.HasExited) return null;

            int id = Interlocked.Increment(ref _nextId);
            var request = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "method", method }
            };
            if (parms != null) request["params"] = parms;

            string json = _serializer.Serialize(request);
            WriteMessage(json);

            // Wait for response
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string response = null;
                lock (_responses)
                {
                    if (_responses.TryGetValue(id, out response))
                        _responses.Remove(id);
                    else
                        response = null;
                }

                // Deserialize OUTSIDE the lock. A large response (e.g. a completion
                // list with thousands of items) can take real time to parse; doing it
                // under _responses would block the reader thread from draining stdout
                // and back-pressure the server's pipe.
                if (response != null)
                    return _serializer.Deserialize<Dictionary<string, object>>(response);

                _responseReceived.WaitOne(100);
            }

            return null; // Timeout
        }

        private void SendNotification(string method, Dictionary<string, object> parms)
        {
            if (!_running || _process == null || _process.HasExited) return;

            var notification = new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "method", method }
            };
            if (parms != null) notification["params"] = parms;

            WriteMessage(_serializer.Serialize(notification));
        }

        private void WriteMessage(string json)
        {
            lock (_writeLock)
            {
                try
                {
                    byte[] content = Encoding.UTF8.GetBytes(json);
                    string header = "Content-Length: " + content.Length + "\r\n\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(header);

                    _process.StandardInput.BaseStream.Write(headerBytes, 0, headerBytes.Length);
                    _process.StandardInput.BaseStream.Write(content, 0, content.Length);
                    _process.StandardInput.BaseStream.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] WriteMessage failed: " + ex.Message);
                }
            }
        }

        private void ReadLoop()
        {
            try
            {
                var stream = _process.StandardOutput.BaseStream;
                while (_running && !_process.HasExited)
                {
                    string json = ReadMessage(stream);
                    if (json == null) break;

                    try
                    {
                        var msg = _serializer.Deserialize<Dictionary<string, object>>(json);

                        // Response: has `id` → correlate with a pending request
                        if (msg.ContainsKey("id") && msg["id"] != null)
                        {
                            int id;
                            if (int.TryParse(msg["id"].ToString(), out id))
                            {
                                lock (_responses)
                                {
                                    _responses[id] = json;
                                }
                                _responseReceived.Set();
                                continue;
                            }
                        }

                        // Notification: has `method` but no `id` → dispatch by method name
                        if (msg.ContainsKey("method") && msg["method"] != null)
                        {
                            HandleNotification(msg);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[LSP] ReadLoop message parse failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LSP] ReadLoop terminated: " + ex.Message);
            }
        }

        private void HandleNotification(Dictionary<string, object> msg)
        {
            string method = msg["method"] as string;
            if (string.IsNullOrEmpty(method)) return;

            // Telemetry: count every notification method we see, and keep a preview of
            // the most recent one so the debug tool can show the raw shape.
            lock (_debugLock)
            {
                int cur;
                _notificationCounts.TryGetValue(method, out cur);
                _notificationCounts[method] = cur + 1;
                try
                {
                    string preview = _serializer.Serialize(msg);
                    if (preview.Length > 2000) preview = preview.Substring(0, 2000) + "...";
                    _lastRawNotificationPreview = preview;
                }
                catch { }
            }

            switch (method)
            {
                case "textDocument/publishDiagnostics":
                    HandlePublishDiagnostics(msg["params"] as Dictionary<string, object>);
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine("[LSP] Ignored notification: " + method);
                    break;
            }
        }

        private void HandlePublishDiagnostics(Dictionary<string, object> parms)
        {
            if (parms == null) return;

            string uri = parms.ContainsKey("uri") ? parms["uri"] as string : null;
            if (string.IsNullOrEmpty(uri)) return;

            // Canonicalize via round-trip through FilePathToUri so cache keys are consistent
            // regardless of whether the URI came in with different casing/encoding than what
            // we sent on didOpen.
            string canonical = CanonicalizeUri(uri);

            var entries = new List<DiagnosticEntry>();
            var diagList = parms.ContainsKey("diagnostics") ? parms["diagnostics"] as System.Collections.ArrayList : null;
            if (diagList != null)
            {
                foreach (var obj in diagList)
                {
                    var d = obj as Dictionary<string, object>;
                    if (d == null) continue;

                    var entry = new DiagnosticEntry();
                    if (d.ContainsKey("severity")) entry.Severity = Convert.ToInt32(d["severity"]);
                    if (d.ContainsKey("message")) entry.Message = d["message"] as string;
                    if (d.ContainsKey("source")) entry.Source = d["source"] as string;

                    var range = d.ContainsKey("range") ? d["range"] as Dictionary<string, object> : null;
                    var start = range != null && range.ContainsKey("start") ? range["start"] as Dictionary<string, object> : null;
                    if (start != null)
                    {
                        if (start.ContainsKey("line")) entry.Line = Convert.ToInt32(start["line"]);
                        if (start.ContainsKey("character")) entry.Character = Convert.ToInt32(start["character"]);
                    }
                    var end = range != null && range.ContainsKey("end") ? range["end"] as Dictionary<string, object> : null;
                    if (end != null)
                    {
                        if (end.ContainsKey("line")) entry.EndLine = Convert.ToInt32(end["line"]);
                        if (end.ContainsKey("character")) entry.EndCharacter = Convert.ToInt32(end["character"]);
                    }

                    entries.Add(entry);
                }
            }

            lock (_diagnosticsLock)
            {
                DiagnosticSet set;
                if (!_diagnostics.TryGetValue(canonical, out set))
                {
                    set = new DiagnosticSet();
                    _diagnostics[canonical] = set;
                    EvictOldestIfFull_NoLock();
                }

                set.Entries = entries;
                set.WasPublished = true;
                set.LastUpdateTicks = DateTime.UtcNow.Ticks;
                // Signal any waiter that new diagnostics have arrived.
                set.Ready.Set();
            }

            System.Diagnostics.Debug.WriteLine(string.Format(
                "[LSP] publishDiagnostics: {0} entries for {1}", entries.Count, canonical));
        }

        private void EvictOldestIfFull_NoLock()
        {
            if (_diagnostics.Count <= MaxCachedDiagnosticFiles) return;

            string oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach (var kv in _diagnostics)
            {
                if (kv.Value.LastUpdateTicks < oldestTicks)
                {
                    oldestTicks = kv.Value.LastUpdateTicks;
                    oldestKey = kv.Key;
                }
            }

            if (oldestKey != null)
            {
                var victim = _diagnostics[oldestKey];
                _diagnostics.Remove(oldestKey);
                try { victim.Ready.Dispose(); } catch { }
                System.Diagnostics.Debug.WriteLine("[LSP] Evicted oldest diagnostics cache entry: " + oldestKey);
            }
        }

        private static string CanonicalizeUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return uri;
            // Strip file:// prefix, normalize to a real path, re-apply FilePathToUri so the
            // cached key matches what our own EnsureDocumentOpen would produce.
            try
            {
                string path = UriToFilePath(uri);
                return FilePathToUri(path);
            }
            catch
            {
                return uri;
            }
        }

        private string ReadMessage(Stream stream)
        {
            // Read headers
            int contentLength = -1;
            var headerLine = new StringBuilder();

            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) return null;

                headerLine.Append((char)b);
                string h = headerLine.ToString();

                if (h.EndsWith("\r\n\r\n"))
                {
                    foreach (string line in h.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int val;
                            if (int.TryParse(line.Substring(15).Trim(), out val))
                                contentLength = val;
                        }
                    }
                    break;
                }
            }

            if (contentLength <= 0) return null;

            byte[] buffer = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(buffer, read, contentLength - read);
                if (n <= 0) return null;
                read += n;
            }

            return Encoding.UTF8.GetString(buffer);
        }

        #endregion

        #region Helpers

        private static string FilePathToUri(string filePath)
        {
            return "file:///" + filePath.Replace("\\", "/").Replace(" ", "%20");
        }

        public static string UriToFilePath(string uri)
        {
            if (uri.StartsWith("file:///"))
                uri = uri.Substring(8).Replace("/", "\\").Replace("%20", " ");
            return uri;
        }

        #endregion

        public void Dispose()
        {
            Stop();
        }

        #region Diagnostic data types

        /// <summary>
        /// A single diagnostic entry from a textDocument/publishDiagnostics notification.
        /// Severity follows the LSP spec: 1=Error, 2=Warning, 3=Information, 4=Hint.
        /// </summary>
        public class DiagnosticEntry
        {
            public int Severity;
            public int Line;
            public int Character;
            public int EndLine;
            public int EndCharacter;
            public string Message;
            public string Source;
        }

        /// <summary>
        /// A single completion item parsed from a textDocument/completion response.
        /// Kind follows the LSP CompletionItemKind enum (1=Text, 2=Method, 3=Function,
        /// 6=Variable, 7=Class, 10=Property, 14=Keyword, 21=Constant, 25=TypeParameter, …);
        /// 0 = unspecified.
        /// </summary>
        public class CompletionItemInfo
        {
            public string Label;
            public int Kind;
            public string Detail;
            public string Documentation;
            public string InsertText;
        }

        /// <summary>
        /// Per-URI diagnostics cache entry. Ready is set by HandlePublishDiagnostics
        /// whenever a new publish arrives, allowing WaitForDiagnostics callers to block
        /// on an event-driven signal instead of polling.
        /// </summary>
        private class DiagnosticSet
        {
            public List<DiagnosticEntry> Entries = new List<DiagnosticEntry>();
            public ManualResetEventSlim Ready = new ManualResetEventSlim(false);
            public long LastUpdateTicks = DateTime.UtcNow.Ticks;
            // True once a publishDiagnostics has ever arrived for this URI — distinguishes
            // an authoritative "clean file" (Entries=[]) from "we haven't heard anything yet".
            public bool WasPublished;
        }

        /// <summary>
        /// Result returned by WaitForDiagnostics. Pending=true means the wait timed out;
        /// the server may still be analysing and callers should report that to the user
        /// rather than treating empty Entries as "file is clean".
        /// </summary>
        public class DiagnosticWaitResult
        {
            public List<DiagnosticEntry> Entries;
            public bool Pending;
        }

        #endregion
    }
}
