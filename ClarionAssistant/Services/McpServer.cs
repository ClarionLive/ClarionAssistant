using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// MCP server using HttpListener with SSE transport.
    /// Implements the MCP SSE protocol:
    ///   GET /sse     → Opens SSE stream, sends endpoint event
    ///   POST /messages?sessionId=X → JSON-RPC requests, responses sent via SSE
    /// </summary>
    public class McpServer : IDisposable
    {
        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly Control _uiControl;
        private McpToolRegistry _toolRegistry;
        private int _port;

        // Per-session auth token — regenerated on every Start(). Embedded as
        // `Authorization: Bearer <token>` in the MCP config file so the spawned
        // CLI clients (Claude Code, Copilot CLI) transparently authenticate.
        // A browser drive-by fetch from an unrelated site has no way to learn
        // this token, so even though the server binds to loopback the tools
        // surface is not reachable without reading settings.txt / mcp-config.json.
        private string _sessionToken;

        // SSE client connections keyed by session ID
        private readonly ConcurrentDictionary<string, SseClient> _sseClients =
            new ConcurrentDictionary<string, SseClient>();

        // Streamable HTTP sessions keyed by session ID
        private readonly ConcurrentDictionary<string, DateTime> _httpSessions =
            new ConcurrentDictionary<string, DateTime>();

        public event Action<string, string> OnToolCall;
        public event Action<bool, int> OnStatusChanged;
        public event Action<string> OnError;

        public int Port { get { return _port; } }
        public bool IsRunning { get { return _running; } }

        public McpServer(Control uiControl)
        {
            _uiControl = uiControl;
        }

        public void SetToolRegistry(McpToolRegistry registry)
        {
            _toolRegistry = registry;
        }

        public bool Start(int preferredPort = 19372)
        {
            if (_running) return true;
            if (_toolRegistry == null)
                throw new InvalidOperationException("Tool registry must be set before starting");

            _sessionToken = GenerateSessionToken();

            for (int port = preferredPort; port < preferredPort + 10; port++)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(string.Format("http://localhost:{0}/", port));
                    _listener.Start();
                    _port = port;
                    _running = true;

                    _listenerThread = new Thread(ListenLoop)
                    {
                        IsBackground = true,
                        Name = "McpServer-Listener"
                    };
                    _listenerThread.Start();

                    RaiseStatusChanged(true, port);
                    return true;
                }
                catch (HttpListenerException)
                {
                    try { _listener.Close(); } catch { }
                    continue;
                }
            }

            RaiseError("Could not find an available port in range " + preferredPort + "-" + (preferredPort + 9));
            return false;
        }

        private static string GenerateSessionToken()
        {
            // 32 bytes = 256 bits of entropy; hex-encode to 64 chars.
            byte[] bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Constant-time compare — avoid timing side-channels when
        /// comparing the presented token to the expected one.</summary>
        private static bool TokensEqual(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>
        /// Authenticate the request via <c>Authorization: Bearer &lt;token&gt;</c>.
        /// Returns true on success; on failure sends 401 and returns false so the
        /// caller should stop processing.
        /// </summary>
        private bool RequireAuth(HttpListenerContext context)
        {
            string header = context.Request.Headers["Authorization"];
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.Ordinal)
                || !TokensEqual(header.Substring(prefix.Length), _sessionToken ?? ""))
            {
                try
                {
                    context.Response.StatusCode = 401;
                    context.Response.Headers.Add("WWW-Authenticate", "Bearer");
                    byte[] buf = Encoding.UTF8.GetBytes("{\"error\":\"unauthorized\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = buf.Length;
                    context.Response.OutputStream.Write(buf, 0, buf.Length);
                    context.Response.Close();
                }
                catch { }
                return false;
            }
            return true;
        }

        /// <summary>
        /// Reject requests whose Host header isn't one of our expected loopback
        /// aliases — defends against DNS rebinding where an attacker-controlled
        /// hostname resolves to 127.0.0.1 after the browser has already committed
        /// to treating the origin as same-site.
        /// </summary>
        private bool ValidateHost(HttpListenerContext context)
        {
            string host = context.Request.Headers["Host"] ?? "";
            if (string.Equals(host, "localhost:" + _port, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(host, "127.0.0.1:" + _port, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
            }
            catch { }
            return false;
        }

        /// <summary>
        /// If an Origin header is present (indicating a browser request), only
        /// allow it if it matches one of our loopback aliases. CLI clients
        /// typically don't set Origin, so this is effectively
        /// "block browser drive-by; let CLI clients through".
        /// </summary>
        private bool ValidateOrigin(HttpListenerContext context)
        {
            string origin = context.Request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return true;
            string expected1 = "http://localhost:" + _port;
            string expected2 = "http://127.0.0.1:" + _port;
            if (string.Equals(origin, expected1, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(origin, expected2, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
            }
            catch { }
            return false;
        }

        public void Stop()
        {
            _running = false;
            _sessionToken = null;

            // Close all SSE connections
            foreach (var kvp in _sseClients)
            {
                try { kvp.Value.Close(); } catch { }
            }
            _sseClients.Clear();
            _httpSessions.Clear();

            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            RaiseStatusChanged(false, _port);
        }

        public bool IncludeMultiTerminal { get; set; }
        public string MultiTerminalMcpPath { get; set; }

        public enum McpConfigFormat
        {
            Claude,
            Copilot
        }

        /// <summary>
        /// True if the multiterminal-channel plugin .mjs file is present on disk.
        /// Set by GenerateMcpConfig when it successfully resolves the path.
        /// AssistantChatControl reads this to decide whether to grant the matching
        /// tool permissions in --allowedTools.
        /// </summary>
        public bool IncludeMultiTerminalChannel { get; private set; }

        public string GenerateMcpConfig(McpConfigFormat format = McpConfigFormat.Claude)
        {
            // Both Claude and Copilot MCP client configs accept a `headers` map
            // for HTTP transport; we use it to pass our per-session bearer token.
            // The server's RequireAuth middleware rejects any request without it.
            var authHeaders = new Dictionary<string, object>
            {
                { "Authorization", "Bearer " + (_sessionToken ?? "") }
            };

            var servers = new Dictionary<string, object>();
            if (format == McpConfigFormat.Copilot)
            {
                // Copilot MCP schema requires per-server tools allowlist.
                // Use ["*"] to avoid name/namespace mismatches with the mcp__clarion-assistant__ prefix.
                servers["clarion-assistant"] = new Dictionary<string, object>
                {
                    { "type", "http" },
                    { "url", string.Format("http://localhost:{0}/mcp", _port) },
                    { "headers", authHeaders },
                    { "tools", new string[] { "*" } }
                };
            }
            else
            {
                var toolNames = new List<string>();
                foreach (var tool in _toolRegistry.GetToolDefinitions())
                {
                    var name = tool.ContainsKey("name") ? tool["name"] as string : null;
                    if (!string.IsNullOrEmpty(name))
                        toolNames.Add("mcp__clarion-assistant__" + name);
                }
                servers["clarion-assistant"] = new Dictionary<string, object>
                {
                    { "type", "http" },
                    { "url", string.Format("http://localhost:{0}/mcp", _port) },
                    { "headers", authHeaders },
                    { "autoApprove", toolNames.ToArray() }
                };
            }

            // Conditionally add MultiTerminal (Claude-only for now)
            if (format == McpConfigFormat.Claude && IncludeMultiTerminal && !string.IsNullOrEmpty(MultiTerminalMcpPath)
                && File.Exists(MultiTerminalMcpPath))
            {
                var mt = new Dictionary<string, object>
                {
                    { "type", "stdio" },
                    { "command", "node" },
                    { "args", new string[] { MultiTerminalMcpPath } }
                };
                servers["multiterminal"] = mt;
            }

            // Add the multiterminal-channel MCP server so the embedded Claude receives
            // real <channel> notifications. Parent-process env vars MULTITERMINAL_NAME
            // and MULTITERMINAL_DOC_ID are exported from LaunchClaudeForTab per tab and
            // inherited by this stdio subprocess. --strict-mcp-config blocks user-level
            // mcpServers entries, so we must include the channel server here explicitly.
            string channelPath = (format == McpConfigFormat.Claude) ? ResolveMultiTerminalChannelPath() : null;
            if (channelPath != null)
            {
                servers["multiterminal-channel"] = new Dictionary<string, object>
                {
                    { "type", "stdio" },
                    { "command", "node" },
                    { "args", new string[] { channelPath } },
                    { "env", new Dictionary<string, object>
                        {
                            // Claude Code expands ${VAR} against its own environment at load time,
                            // then merges onto the inherited parent env (additive, not replacing).
                            // Belt + braces: rely on inheritance AND declare the keys explicitly.
                            { "MT_API_URL", "http://localhost:5050" },
                            { "MULTITERMINAL_NAME", "${MULTITERMINAL_NAME}" },
                            { "MULTITERMINAL_DOC_ID", "${MULTITERMINAL_DOC_ID}" }
                        }
                    }
                };
                IncludeMultiTerminalChannel = true;
            }
            else
            {
                IncludeMultiTerminalChannel = false;
            }

            return McpJsonRpc.Serialize(new Dictionary<string, object>
            {
                { "mcpServers", servers }
            });
        }

        /// <summary>
        /// Locate multiterminal-channel.mjs on this machine. Resolution order:
        /// 1. %USERPROFILE%\.claude\plugins\marketplaces\multiterminal-marketplace\plugins\multiterminal\server\multiterminal-channel.mjs
        /// 2. Return null if not present — caller falls back to channel-disabled mode.
        /// </summary>
        private static string ResolveMultiTerminalChannelPath()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(userProfile)) return null;

                string pluginRoot = Path.Combine(userProfile, ".claude", "plugins", "marketplaces",
                    "multiterminal-marketplace", "plugins", "multiterminal");
                string candidate = Path.Combine(pluginRoot, "server", "multiterminal-channel.mjs");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
            return null;
        }

        public string WriteMcpConfigFile()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClarionAssistant");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string configPath = Path.Combine(dir, "mcp-config.json");
            File.WriteAllText(configPath, GenerateMcpConfig(McpConfigFormat.Claude));
            return configPath;
        }

        public string WriteMcpConfigFile(string directory, McpConfigFormat format)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException("directory");
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            string configPath = Path.Combine(directory, "mcp-config.json");
            File.WriteAllText(configPath, GenerateMcpConfig(format));
            return configPath;
        }

        #region HTTP Listener Loop

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    RaiseError("Listener error: " + ex.Message);
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                string path = request.Url.AbsolutePath;

                // Host / Origin validation run before anything else — blocks DNS
                // rebinding and cross-origin browser drive-by. Token auth below
                // is the primary gate; these are defense in depth.
                if (!ValidateHost(context)) return;
                if (!ValidateOrigin(context)) return;

                // Deliberately no CORS headers. MCP CLIs (Claude Code, Copilot CLI)
                // are not browsers and don't need CORS; leaving CORS off means any
                // browser that somehow reaches us can't read our responses even if
                // it could send a request. A browser preflight (OPTIONS) will fail
                // its own CORS check when no Access-Control-Allow-Origin comes back.
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // Require a valid session token on every non-OPTIONS request.
                // The token is embedded in the MCP config file the clients read;
                // drive-by HTTP fetches from other processes / browsers don't have it.
                if (!RequireAuth(context)) return;

                // Streamable HTTP endpoint — JSON-RPC request/response over POST
                if (request.HttpMethod == "POST" && path == "/mcp")
                {
                    HandleStreamableHttpPost(context);
                    return;
                }

                // Streamable HTTP session teardown
                if (request.HttpMethod == "DELETE" && path == "/mcp")
                {
                    HandleStreamableHttpDelete(context);
                    return;
                }

                // SSE endpoint — long-lived event stream (legacy)
                if (request.HttpMethod == "GET" && path == "/sse")
                {
                    HandleSseConnection(context);
                    return;
                }

                // Messages endpoint — JSON-RPC over POST, response via SSE (legacy)
                if (request.HttpMethod == "POST" && path.StartsWith("/messages"))
                {
                    HandleMessagePost(context);
                    return;
                }

                // Health check
                if (request.HttpMethod == "GET" && (path == "/" || path == "/mcp"))
                {
                    string health = McpJsonRpc.Serialize(new Dictionary<string, object>
                    {
                        { "status", "ok" },
                        { "server", "ClarionAssistant MCP" },
                        { "port", _port },
                        { "tools", _toolRegistry.GetToolCount() }
                    });
                    byte[] buffer = Encoding.UTF8.GetBytes(health);
                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.Close();
                    return;
                }

                string notFound = "{\"error\":\"not_found\",\"path\":\"" + path.Replace("\"", "\\\"") + "\"}";
                byte[] notFoundBuf = System.Text.Encoding.UTF8.GetBytes(notFound);
                response.StatusCode = 404;
                response.ContentType = "application/json";
                response.ContentLength64 = notFoundBuf.Length;
                response.OutputStream.Write(notFoundBuf, 0, notFoundBuf.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                RaiseError("Request handling error: " + ex.Message);
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        #endregion

        #region SSE Transport

        private void HandleSseConnection(HttpListenerContext context)
        {
            var response = context.Response;
            string sessionId = Guid.NewGuid().ToString();

            response.ContentType = "text/event-stream";
            response.Headers.Add("Cache-Control", "no-cache");
            response.Headers.Add("Connection", "keep-alive");

            var client = new SseClient(response, sessionId);
            _sseClients[sessionId] = client;

            // Send the endpoint event — tells Claude where to POST messages
            string endpointUrl = string.Format("http://localhost:{0}/messages?sessionId={1}", _port, sessionId);
            client.SendEvent("endpoint", endpointUrl);

            RaiseError("SSE client connected: " + sessionId);

            // Keep the connection alive until client disconnects or server stops
            try
            {
                while (_running && !client.IsClosed)
                {
                    Thread.Sleep(15000);
                    // Send keepalive comment
                    if (!client.IsClosed)
                        client.SendComment("keepalive");
                }
            }
            catch { }
            finally
            {
                SseClient removed;
                _sseClients.TryRemove(sessionId, out removed);
                try { response.Close(); } catch { }
            }
        }

        private void HandleMessagePost(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Extract session ID from query string
            string sessionId = request.QueryString["sessionId"];
            if (string.IsNullOrEmpty(sessionId))
            {
                response.StatusCode = 400;
                byte[] err = Encoding.UTF8.GetBytes("Missing sessionId parameter");
                response.OutputStream.Write(err, 0, err.Length);
                response.Close();
                return;
            }

            SseClient client;
            if (!_sseClients.TryGetValue(sessionId, out client))
            {
                response.StatusCode = 400;
                byte[] err = Encoding.UTF8.GetBytes("Unknown session: " + sessionId);
                response.OutputStream.Write(err, 0, err.Length);
                response.Close();
                return;
            }

            // Read JSON-RPC request
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            // Respond to the POST with 202 Accepted immediately
            response.StatusCode = 202;
            response.Close();

            // Process the JSON-RPC request asynchronously and send result via SSE
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string responseJson = ProcessJsonRpc(body);
                    client.SendEvent("message", responseJson);
                }
                catch (Exception ex)
                {
                    RaiseError("Async tool call error: " + ex.Message);
                }
            });
        }

        #endregion

        #region Streamable HTTP Transport

        private void HandleStreamableHttpPost(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Read JSON-RPC request body
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            // Determine or create session
            string sessionId = request.Headers["Mcp-Session-Id"];
            bool isInitialize = body.Contains("\"method\":\"initialize\"");

            if (isInitialize)
            {
                // New session
                sessionId = Guid.NewGuid().ToString();
                _httpSessions[sessionId] = DateTime.UtcNow;
            }
            else if (string.IsNullOrEmpty(sessionId) || !_httpSessions.ContainsKey(sessionId))
            {
                // Unknown session — require initialize first
                response.StatusCode = 400;
                byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"missing or invalid Mcp-Session-Id\"}");
                response.ContentType = "application/json";
                response.ContentLength64 = err.Length;
                response.OutputStream.Write(err, 0, err.Length);
                response.Close();
                return;
            }

            // Update last-seen timestamp
            _httpSessions[sessionId] = DateTime.UtcNow;

            // Process the JSON-RPC request
            string responseJson = ProcessJsonRpc(body);

            // Check if this is a notification (no id → no response expected)
            bool isNotification = body.Contains("\"method\":\"notifications/");
            if (isNotification)
            {
                response.StatusCode = 204;
                response.Headers.Add("Mcp-Session-Id", sessionId);
                response.Close();
                return;
            }

            // Send JSON-RPC response directly
            byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            response.Headers.Add("Mcp-Session-Id", sessionId);
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }

        private void HandleStreamableHttpDelete(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            string sessionId = request.Headers["Mcp-Session-Id"];
            if (!string.IsNullOrEmpty(sessionId))
            {
                DateTime removed;
                _httpSessions.TryRemove(sessionId, out removed);
            }

            response.StatusCode = 204;
            response.Close();
        }

        #endregion

        #region JSON-RPC Dispatch

        private string ProcessJsonRpc(string body)
        {
            JsonRpcRequest request;
            try
            {
                request = McpJsonRpc.ParseRequest(body);
            }
            catch (Exception ex)
            {
                return McpJsonRpc.SerializeError(null, -32700, "Parse error: " + ex.Message);
            }

            if (string.IsNullOrEmpty(request.Method))
            {
                return McpJsonRpc.SerializeError(request.Id, -32600, "Invalid request: missing method");
            }

            try
            {
                switch (request.Method)
                {
                    case "initialize":
                        var initResult = McpJsonRpc.BuildInitializeResult("clarion-assistant", "1.0.0");
                        return McpJsonRpc.SerializeResponse(request.Id, initResult);

                    case "notifications/initialized":
                        return McpJsonRpc.SerializeResponse(request.Id, new Dictionary<string, object>());

                    case "ping":
                        return McpJsonRpc.SerializeResponse(request.Id, new Dictionary<string, object>());

                    case "tools/list":
                        var tools = _toolRegistry.GetToolDefinitions();
                        var listResult = new Dictionary<string, object> { { "tools", tools } };
                        return McpJsonRpc.SerializeResponse(request.Id, listResult);

                    case "tools/call":
                        return HandleToolCall(request);

                    default:
                        return McpJsonRpc.SerializeError(request.Id, -32601,
                            "Method not found: " + request.Method);
                }
            }
            catch (Exception ex)
            {
                return McpJsonRpc.SerializeError(request.Id, -32603,
                    "Internal error: " + ex.Message);
            }
        }

        private string HandleToolCall(JsonRpcRequest request)
        {
            var parms = request.Params;
            string toolName = McpJsonRpc.GetString(parms, "name");
            var arguments = parms.ContainsKey("arguments")
                ? parms["arguments"] as Dictionary<string, object>
                : new Dictionary<string, object>();

            if (string.IsNullOrEmpty(toolName))
            {
                return McpJsonRpc.SerializeError(request.Id, -32602,
                    "Missing tool name in tools/call");
            }

            object result;
            try
            {
                if (_toolRegistry.RequiresUiThread(toolName))
                {
                    object uiResult = null;
                    Exception uiException = null;

                    _uiControl.Invoke((Action)(() =>
                    {
                        try { uiResult = _toolRegistry.ExecuteTool(toolName, arguments); }
                        catch (Exception ex) { uiException = ex; }
                    }));

                    if (uiException != null) throw uiException;
                    result = uiResult;
                }
                else
                {
                    result = _toolRegistry.ExecuteTool(toolName, arguments);
                }
            }
            catch (Exception ex)
            {
                RaiseToolCall(toolName, "ERROR: " + ex.Message);
                var errorResult = McpJsonRpc.BuildToolResult(
                    "Error executing tool '" + toolName + "': " + ex.Message, true);
                return McpJsonRpc.SerializeResponse(request.Id, errorResult);
            }

            string resultText = result is string
                ? (string)result
                : McpJsonRpc.Serialize(result);

            RaiseToolCall(toolName, resultText.Length > 100
                ? resultText.Substring(0, 100) + "..."
                : resultText);

            var toolResult = McpJsonRpc.BuildToolResult(resultText);
            return McpJsonRpc.SerializeResponse(request.Id, toolResult);
        }

        #endregion

        #region Event Helpers

        private void RaiseToolCall(string name, string summary)
        {
            try
            {
                if (OnToolCall != null && _uiControl != null && !_uiControl.IsDisposed)
                    _uiControl.BeginInvoke((Action)(() => OnToolCall(name, summary)));
            }
            catch { }
        }

        private void RaiseStatusChanged(bool running, int port)
        {
            try
            {
                if (OnStatusChanged != null && _uiControl != null && !_uiControl.IsDisposed)
                    _uiControl.BeginInvoke((Action)(() => OnStatusChanged(running, port)));
            }
            catch { }
        }

        private void RaiseError(string message)
        {
            try
            {
                if (OnError != null && _uiControl != null && !_uiControl.IsDisposed)
                    _uiControl.BeginInvoke((Action)(() => OnError(message)));
            }
            catch { }
        }

        #endregion

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// Represents a connected SSE client with a writable response stream.
    /// </summary>
    internal class SseClient
    {
        private readonly HttpListenerResponse _response;
        private readonly StreamWriter _writer;
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        public string SessionId { get; private set; }
        public bool IsClosed { get { return _closed; } }

        public SseClient(HttpListenerResponse response, string sessionId)
        {
            _response = response;
            SessionId = sessionId;
            _writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        public void SendEvent(string eventType, string data)
        {
            if (_closed) return;
            lock (_writeLock)
            {
                try
                {
                    _writer.Write("event: " + eventType + "\n");
                    // Data can contain newlines — each line needs "data: " prefix
                    foreach (string line in data.Split('\n'))
                    {
                        _writer.Write("data: " + line + "\n");
                    }
                    _writer.Write("\n");
                    _writer.Flush();
                }
                catch
                {
                    _closed = true;
                }
            }
        }

        public void SendComment(string comment)
        {
            if (_closed) return;
            lock (_writeLock)
            {
                try
                {
                    _writer.Write(": " + comment + "\n\n");
                    _writer.Flush();
                }
                catch
                {
                    _closed = true;
                }
            }
        }

        public void Close()
        {
            _closed = true;
            try { _writer.Close(); } catch { }
        }
    }
}
