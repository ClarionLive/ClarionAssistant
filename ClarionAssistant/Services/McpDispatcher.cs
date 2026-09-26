using System;
using System.Collections.Generic;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Transport-agnostic MCP/JSON-RPC dispatch (ticket d051fbd1).
    ///
    /// Lifted verbatim out of McpServer so the stdio server can reuse it rather than grow a
    /// second copy. McpServer.cs imports System.Windows.Forms and HttpListener, so it cannot be
    /// linked into the standalone build; without this split the only way to serve MCP over stdio
    /// would have been to duplicate initialize/tools/list/tools/call, and two copies of a wire
    /// protocol drift silently — one transport gains a fix and the other quietly does not.
    ///
    /// What stays in McpServer: HTTP, SSE, bearer auth, Host/Origin validation, port scanning,
    /// session tracking. All of that exists because it is a network listener inside a long-lived
    /// IDE. None of it means anything to a client-launched stdio process.
    /// </summary>
    public class McpDispatcher
    {
        // Max time a UI-thread MCP tool may run before the request is abandoned with a timeout
        // error, so a busy/wedged UI thread can't hold a worker (and leak the connection as
        // CLOSE_WAIT) indefinitely.
        private const int UiToolTimeoutSeconds = 30;

        // Minimum interval between notifications/progress frames. The indexer emits at file
        // boundaries (up to ~30/s during parsing) — relaying every one would flood the client
        // for zero information gain.
        private const int ProgressThrottleMs = 1000;

        private readonly McpToolRegistry _toolRegistry;
        private readonly IUiDispatcher _ui;
        private readonly Action<string, string> _onToolCall;
        private readonly string _serverName;
        private readonly string _serverVersion;

        /// <summary>
        /// </summary>
        /// <param name="toolRegistry">Required. The shared tool set.</param>
        /// <param name="ui">
        /// The host's UI thread, or null when there isn't one. See ExecuteWithUiPolicy for what
        /// null actually changes — it is not merely "skip the marshalling".
        /// </param>
        /// <param name="onToolCall">
        /// Optional observation sink (name, result-summary) for the addin's activity log. The
        /// stdio server passes null: it has no UI to report into, and writing to stdout would
        /// corrupt the protocol stream.
        /// </param>
        /// <param name="serverName">
        /// Reported in initialize. DELIBERATELY DIFFERENT per host ("clarion-assistant" vs
        /// "clarion-mcp-server") — the two advertise different tool counts, so a client that
        /// cannot tell them apart cannot explain to a user why a tool it saw yesterday is gone.
        /// </param>
        public McpDispatcher(
            McpToolRegistry toolRegistry,
            IUiDispatcher ui,
            Action<string, string> onToolCall,
            string serverName,
            string serverVersion)
        {
            if (toolRegistry == null) throw new ArgumentNullException("toolRegistry");
            _toolRegistry = toolRegistry;
            _ui = ui;
            _onToolCall = onToolCall;
            _serverName = serverName ?? "clarion-assistant";
            _serverVersion = serverVersion ?? "1.0.0";
        }

        /// <summary>
        /// Process a JSON-RPC message. sendNotification, when non-null, is a transport sink for
        /// server→client notifications emitted DURING a tools/call (progress streaming, ticket
        /// 0d788f8b): the legacy SSE transport passes its session stream, the Streamable HTTP
        /// transport an SSE response writer. Null = the transport can't carry mid-call
        /// notifications; tools run buffered as before.
        /// </summary>
        public string ProcessJsonRpc(string body, Action<string> sendNotification)
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
                        var initResult = McpJsonRpc.BuildInitializeResult(_serverName, _serverVersion);
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
                        return HandleToolCall(request, sendNotification);

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

        /// <summary>
        /// Would this request be answered as a progress stream? A transport asks BEFORE
        /// dispatching, because it may have to commit to a streaming response shape first — the
        /// Streamable HTTP path has to switch the response to text/event-stream and chunked
        /// before any handler runs.
        ///
        /// It lives here, rather than in the transport, so the rules for what counts as a valid
        /// progressToken exist ONCE. A transport-side copy could accept a token this class
        /// rejects, and then stream a response nothing ever populates.
        /// </summary>
        public bool WouldStream(JsonRpcRequest request)
        {
            if (request == null || request.Method != "tools/call") return false;
            string toolName = McpJsonRpc.GetString(request.Params, "name");
            if (string.IsNullOrEmpty(toolName)) return false;
            return ExtractProgressToken(request.Params) != null
                && _toolRegistry.SupportsStreaming(toolName);
        }

        /// <summary>
        /// Extract the MCP progress token (params._meta.progressToken) from a tools/call request.
        /// Null when the client didn't ask for progress. The spec allows only string or number
        /// tokens; anything else (or an oversized string) is treated as absent rather than echoed
        /// into every progress frame — the token is reflected back once per notification, so it
        /// must stay a cheap scalar (Codex security finding, run 1).
        /// </summary>
        private static object ExtractProgressToken(Dictionary<string, object> parms)
        {
            if (parms == null) return null;
            object metaObj;
            if (!parms.TryGetValue("_meta", out metaObj)) return null;
            var meta = metaObj as Dictionary<string, object>;
            if (meta == null) return null;
            object token;
            meta.TryGetValue("progressToken", out token);

            string s = token as string;
            if (s != null) return s.Length <= 256 ? token : null;
            // JavaScriptSerializer materializes JSON numbers as int, long, decimal or double.
            if (token is int || token is long || token is decimal || token is double) return token;
            return null;
        }

        private string HandleToolCall(JsonRpcRequest request, Action<string> sendNotification)
        {
            var parms = request.Params;
            string toolName = McpJsonRpc.GetString(parms, "name");
            var arguments = parms != null && parms.ContainsKey("arguments")
                ? parms["arguments"] as Dictionary<string, object>
                : new Dictionary<string, object>();

            if (string.IsNullOrEmpty(toolName))
            {
                return McpJsonRpc.SerializeError(request.Id, -32602,
                    "Missing tool name in tools/call");
            }

            object progressToken = sendNotification != null ? ExtractProgressToken(parms) : null;
            bool streaming = progressToken != null && _toolRegistry.SupportsStreaming(toolName);

            object result;
            try
            {
                if (streaming)
                {
                    result = _toolRegistry.ExecuteToolStreaming(
                        toolName, arguments, BuildProgressRelay(progressToken, sendNotification));
                }
                else
                {
                    string timeoutError;
                    if (!ExecuteWithUiPolicy(request, toolName, arguments, out result, out timeoutError))
                        return timeoutError;
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

        /// <summary>
        /// Run a tool under the host's threading policy. Returns false, and sets
        /// <paramref name="timeoutResponse"/> to a complete JSON-RPC response, when a UI-thread
        /// tool timed out.
        ///
        /// THE STANDALONE CASE IS NOT "NO UI THREAD, SO SKIP THE MARSHALLING" — it is that the
        /// marshalling was never about the tool. THREE tools flagged RequiresUiThread survive the
        /// IdeOnly gate and DO register standalone: execute_command, get_solution_info and
        /// index_solution. Measured, not assumed. They are flagged because in the addin they read
        /// _workspace, which there IS the WinForms chat control and so is thread-affine. In a
        /// standalone host IWorkspaceContext is a plain object with no affinity, so running them
        /// inline on the calling thread is correct — not a degraded fallback.
        ///
        /// Those three are also, awkwardly, among the tools this whole ticket exists to deliver:
        /// index_solution is what builds the CodeGraph a non-IDE client would query. Had this
        /// path thrown or refused instead, the standalone server would have registered them and
        /// then failed every call.
        /// </summary>
        private bool ExecuteWithUiPolicy(
            JsonRpcRequest request,
            string toolName,
            Dictionary<string, object> arguments,
            out object result,
            out string timeoutResponse)
        {
            result = null;
            timeoutResponse = null;

            bool marshal = _toolRegistry.RequiresUiThread(toolName) && _ui != null && _ui.HasUiThread;
            if (!marshal)
            {
                result = _toolRegistry.ExecuteTool(toolName, arguments);
                return true;
            }

            object uiResult = null;
            Exception uiException = null;

            // Marshal onto the UI thread WITHOUT blocking the worker forever. A synchronous
            // Control.Invoke here deadlocks (and leaks the connection as CLOSE_WAIT) whenever the
            // UI thread is busy/wedged.
            using (var done = new ManualResetEventSlim(false))
            {
                _ui.BeginInvokeOnUi(() =>
                {
                    try { uiResult = _toolRegistry.ExecuteTool(toolName, arguments); }
                    catch (Exception ex) { uiException = ex; }
                    finally { try { done.Set(); } catch { } }
                });

                // Give the UI thread a bounded window to run the tool. On timeout we abandon the
                // delegate (it will complete harmlessly later) and return an error instead of
                // holding the worker + connection open.
                if (!done.Wait(TimeSpan.FromSeconds(UiToolTimeoutSeconds)))
                {
                    RaiseToolCall(toolName, "TIMEOUT (UI thread busy)");
                    timeoutResponse = McpJsonRpc.SerializeResponse(request.Id, McpJsonRpc.BuildToolResult(
                        "Error executing tool '" + toolName + "': UI thread did not respond within "
                        + UiToolTimeoutSeconds + "s (busy or blocked).", true));
                    return false;
                }
            }

            if (uiException != null) throw uiException;
            result = uiResult;
            return true;
        }

        /// <summary>
        /// Build the throttled progress relay handed to a streaming tool.
        ///
        /// Streaming handlers run on the CALLING thread regardless of RequiresUiThread — streaming
        /// must not hold the UI thread; the handler marshals its own UI work. Notification
        /// failures are swallowed after the first: a disconnected client must not abort a long
        /// index run and leave a partial database behind.
        /// </summary>
        private static Action<double, string> BuildProgressRelay(
            object progressToken, Action<string> sendNotification)
        {
            // Throttle math uses unchecked int subtraction so the TickCount wrap (~24.9 days
            // uptime) can't permanently silence the stream.
            int lastSentTick = 0;
            bool sentAny = false;
            bool sinkBroken = false;
            double lastSentPercent = 0.0;

            return (percent, message) =>
            {
                if (sinkBroken) return;
                int now = Environment.TickCount;
                if (sentAny && unchecked(now - lastSentTick) < ProgressThrottleMs) return;
                sentAny = true;
                lastSentTick = now;
                // MCP requires progress to increase with each notification. Phases that pin their
                // percentage (finishing-tail heartbeats sit at 98) get a minimal synthetic
                // increment, capped short of 100 so only a real completion can claim it.
                if (percent <= lastSentPercent)
                    percent = Math.Min(99.9, lastSentPercent + 0.1);
                lastSentPercent = percent;
                try
                {
                    sendNotification(McpJsonRpc.Serialize(new Dictionary<string, object>
                    {
                        { "jsonrpc", "2.0" },
                        { "method", "notifications/progress" },
                        { "params", new Dictionary<string, object>
                            {
                                { "progressToken", progressToken },
                                { "progress", Math.Round(percent, 1) },
                                { "total", 100 },
                                { "message", message }
                            }
                        }
                    }));
                }
                catch
                {
                    sinkBroken = true;
                }
            };
        }

        private void RaiseToolCall(string name, string summary)
        {
            try
            {
                if (_onToolCall != null) _onToolCall(name, summary);
            }
            catch { }
        }
    }
}
