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

        public bool IsRunning { get { return _running && _process != null && !_process.HasExited; } }

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
                // Use bundled node.exe next to server.js, fall back to system PATH
                string lspDir = Path.GetDirectoryName(serverJsPath);
                string lspRoot = Path.GetFullPath(Path.Combine(lspDir, "..", "..", ".."));
                string nodeExe = Path.Combine(lspRoot, "node.exe");
                System.Diagnostics.Debug.WriteLine("[LSP] Looking for node.exe at: " + nodeExe);
                if (!File.Exists(nodeExe))
                {
                    System.Diagnostics.Debug.WriteLine("[LSP] Bundled node.exe not found, falling back to PATH");
                    nodeExe = "node";
                }

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

                // Capture stderr for diagnostics
                _process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        System.Diagnostics.Debug.WriteLine("[LSP stderr] " + e.Data);
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
            catch { }

            _process = null;
        }

        #region LSP Requests

        /// <summary>
        /// textDocument/definition - find where a symbol is defined.
        /// </summary>
        public Dictionary<string, object> GetDefinition(string filePath, int line, int character)
        {
            return SendTextDocumentPositionRequest("textDocument/definition", filePath, line, character);
        }

        /// <summary>
        /// textDocument/references - find all references to a symbol.
        /// </summary>
        public Dictionary<string, object> GetReferences(string filePath, int line, int character)
        {
            var parms = BuildTextDocumentPosition(filePath, line, character);
            parms["context"] = new Dictionary<string, object> { { "includeDeclaration", true } };
            return SendRequest("textDocument/references", parms);
        }

        /// <summary>
        /// textDocument/hover - get hover info (type, signature, docs).
        /// </summary>
        public Dictionary<string, object> GetHover(string filePath, int line, int character)
        {
            return SendTextDocumentPositionRequest("textDocument/hover", filePath, line, character);
        }

        /// <summary>
        /// textDocument/documentSymbol - get all symbols in a document.
        /// </summary>
        public Dictionary<string, object> GetDocumentSymbols(string filePath)
        {
            EnsureDocumentOpen(filePath);
            var parms = new Dictionary<string, object>
            {
                { "textDocument", new Dictionary<string, object> { { "uri", FilePathToUri(filePath) } } }
            };
            return SendRequest("textDocument/documentSymbol", parms);
        }

        /// <summary>
        /// workspace/symbol - search for symbols across the workspace.
        /// </summary>
        public Dictionary<string, object> FindWorkspaceSymbol(string query)
        {
            var parms = new Dictionary<string, object> { { "query", query } };
            return SendRequest("workspace/symbol", parms);
        }

        #endregion

        #region Document Management

        private readonly HashSet<string> _openDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void EnsureDocumentOpen(string filePath)
        {
            if (_openDocuments.Contains(filePath)) return;
            if (!File.Exists(filePath)) return;

            string uri = FilePathToUri(filePath);
            string content = File.ReadAllText(filePath);

            // Determine language ID
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
            _openDocuments.Add(filePath);
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
                lock (_responses)
                {
                    string response;
                    if (_responses.TryGetValue(id, out response))
                    {
                        _responses.Remove(id);
                        return _serializer.Deserialize<Dictionary<string, object>>(response);
                    }
                }
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
                catch { }
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
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
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
    }
}
