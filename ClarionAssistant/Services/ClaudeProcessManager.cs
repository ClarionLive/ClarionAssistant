using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Parsed stream-json event from Claude Code CLI output.
    /// </summary>
    public class ClaudeStreamEvent
    {
        public string Type { get; set; }       // system, assistant, result, rate_limit_event
        public string Subtype { get; set; }    // for system: init, hook_started, hook_response
        public string Text { get; set; }       // extracted text content
        public string SessionId { get; set; }
        public bool IsError { get; set; }
        public string RawJson { get; set; }

        // Result-specific fields
        public int DurationMs { get; set; }
        public double CostUsd { get; set; }
    }

    /// <summary>
    /// Manages launching Claude Code as a subprocess per turn.
    /// Uses --output-format stream-json to capture structured output.
    /// Supports --resume for conversation continuity.
    /// </summary>
    public class ClaudeProcessManager : IDisposable
    {
        private readonly string _claudePath;
        private readonly string _mcpConfigPath;
        private string _sessionId;
        private Process _currentProcess;
        private volatile bool _isRunning;
        private string _model = "sonnet";

        private const string SystemPrompt =
            "You are running INSIDE the Clarion IDE as an embedded assistant. " +
            "The user is a Clarion developer working in the IDE right now. " +
            "You have MCP tools that directly interact with the IDE they are using — " +
            "get_active_file reads the file they have open, open_file opens files in their editor, " +
            "insert_text_at_cursor types into their editor, etc. " +
            "Do NOT suggest opening external programs or editors — you ARE in the editor. " +
            "Keep responses concise and action-oriented.";

        /// <summary>
        /// Fired for each parsed stream event (assistant text, system messages, etc.).
        /// </summary>
        public event Action<ClaudeStreamEvent> OnStreamEvent;

        /// <summary>
        /// Fired when Claude sends assistant text (the main response content).
        /// </summary>
        public event Action<string> OnAssistantText;

        /// <summary>
        /// Fired when the turn completes with the final result.
        /// </summary>
        public event Action<ClaudeStreamEvent> OnTurnComplete;

        /// <summary>
        /// Fired on errors (process crash, stderr output).
        /// </summary>
        public event Action<string> OnError;

        /// <summary>
        /// Whether a Claude process is currently running.
        /// </summary>
        public bool IsRunning { get { return _isRunning; } }

        /// <summary>
        /// Current session ID for --resume continuity.
        /// </summary>
        public string SessionId { get { return _sessionId; } }

        public ClaudeProcessManager(string mcpConfigPath)
        {
            _mcpConfigPath = mcpConfigPath;
            _claudePath = FindClaudePath();
        }

        /// <summary>
        /// Set the model to use (e.g. "sonnet", "opus", "haiku"). Default is "sonnet".
        /// </summary>
        public void SetModel(string model)
        {
            _model = model;
        }

        /// <summary>
        /// Send a message to Claude Code. Launches a subprocess, captures stream-json output.
        /// If a session exists, uses --resume for continuity.
        /// </summary>
        public void SendMessage(string message, string workingDirectory = null)
        {
            if (_isRunning)
            {
                RaiseError("A Claude process is already running");
                return;
            }

            if (string.IsNullOrEmpty(_claudePath))
            {
                RaiseError("Claude CLI not found. Install with: npm install -g @anthropic-ai/claude-code");
                return;
            }

            _isRunning = true;

            var thread = new Thread(() => RunClaudeProcess(message, workingDirectory))
            {
                IsBackground = true,
                Name = "Claude-Process"
            };
            thread.Start();
        }

        /// <summary>
        /// Cancel the current Claude process if running.
        /// </summary>
        public void Cancel()
        {
            if (_currentProcess != null && !_currentProcess.HasExited)
            {
                try { _currentProcess.Kill(); } catch { }
            }
            _isRunning = false;
        }

        /// <summary>
        /// Reset the session (start fresh conversation).
        /// </summary>
        public void ResetSession()
        {
            _sessionId = null;
        }

        private void RunClaudeProcess(string message, string workingDirectory)
        {
            try
            {
                var args = new StringBuilder();
                args.Append("-p ");
                args.Append(EscapeArgument(message));
                args.Append(" --output-format stream-json --verbose");
                args.Append(" --model ");
                args.Append(_model);
                args.Append(" --system-prompt ");
                args.Append(EscapeArgument(SystemPrompt));

                if (!string.IsNullOrEmpty(_sessionId))
                {
                    args.Append(" --resume ");
                    args.Append(EscapeArgument(_sessionId));
                }

                if (!string.IsNullOrEmpty(_mcpConfigPath) && File.Exists(_mcpConfigPath))
                {
                    args.Append(" --mcp-config ");
                    args.Append(EscapeArgument(_mcpConfigPath));
                }

                // Load the Clarion Assistant plugin (skills, hooks, CLAUDE.md)
                // Note: use simple quoting, not EscapeArgument which double-escapes backslashes
                string pluginDir = GetClarionAssistantPluginPath();
                if (pluginDir != null)
                {
                    args.Append(" --plugin-dir \"");
                    args.Append(pluginDir);
                    args.Append("\"");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _claudePath,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                    startInfo.WorkingDirectory = workingDirectory;

                _currentProcess = new Process { StartInfo = startInfo };
                _currentProcess.Start();

                // Read stderr on a separate thread
                var stderrThread = new Thread(() => ReadStderr(_currentProcess))
                {
                    IsBackground = true,
                    Name = "Claude-Stderr"
                };
                stderrThread.Start();

                // Read stdout line-by-line (stream-json is newline-delimited JSON)
                string line;
                while ((line = _currentProcess.StandardOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;

                    var evt = ParseStreamEvent(line);
                    if (evt == null) continue;

                    // Capture session ID from any event that has one
                    if (!string.IsNullOrEmpty(evt.SessionId))
                        _sessionId = evt.SessionId;

                    RaiseStreamEvent(evt);

                    if (evt.Type == "assistant" && !string.IsNullOrEmpty(evt.Text))
                        RaiseAssistantText(evt.Text);

                    if (evt.Type == "result")
                        RaiseTurnComplete(evt);
                }

                _currentProcess.WaitForExit();
                stderrThread.Join(2000);
            }
            catch (Exception ex)
            {
                RaiseError("Claude process error: " + ex.Message);
            }
            finally
            {
                _isRunning = false;
                _currentProcess = null;
            }
        }

        private void ReadStderr(Process process)
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    if (!string.IsNullOrEmpty(line))
                        RaiseError(line);
                }
            }
            catch { }
        }

        #region Stream-JSON Parsing

        private ClaudeStreamEvent ParseStreamEvent(string json)
        {
            try
            {
                var dict = McpJsonRpc.Deserialize(json);
                var evt = new ClaudeStreamEvent
                {
                    RawJson = json,
                    Type = GetString(dict, "type"),
                    Subtype = GetString(dict, "subtype"),
                    SessionId = GetString(dict, "session_id"),
                    IsError = GetBool(dict, "is_error")
                };

                switch (evt.Type)
                {
                    case "assistant":
                        evt.Text = ExtractAssistantText(dict);
                        break;

                    case "result":
                        evt.Text = GetString(dict, "result");
                        evt.DurationMs = GetInt(dict, "duration_ms");
                        evt.CostUsd = GetDouble(dict, "total_cost_usd");
                        break;

                    case "system":
                        // Hook responses can contain useful output
                        if (evt.Subtype == "hook_response")
                            evt.Text = GetString(dict, "output");
                        break;
                }

                return evt;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract text from assistant message: message.content[].text
        /// </summary>
        private string ExtractAssistantText(Dictionary<string, object> dict)
        {
            if (!dict.ContainsKey("message")) return null;

            var message = dict["message"] as Dictionary<string, object>;
            if (message == null || !message.ContainsKey("content")) return null;

            var content = message["content"] as object[];
            if (content == null)
            {
                // JavaScriptSerializer may return ArrayList
                var contentList = message["content"] as System.Collections.ArrayList;
                if (contentList == null) return null;
                content = contentList.ToArray();
            }

            var sb = new StringBuilder();
            foreach (var item in content)
            {
                var block = item as Dictionary<string, object>;
                if (block == null) continue;

                string type = GetString(block, "type");
                if (type == "text")
                {
                    string text = GetString(block, "text");
                    if (!string.IsNullOrEmpty(text))
                        sb.Append(text);
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        #endregion

        #region Helpers

        private static string FindClaudePath()
        {
            // Check common locations
            string npmGlobal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "claude.cmd");
            if (File.Exists(npmGlobal)) return npmGlobal;

            // Try PATH via where command
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "claude",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                string output = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(output) && File.Exists(output))
                    return output;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Finds the Clarion Assistant plugin directory.
        /// Checks marketplace source first, then cache.
        /// </summary>
        private static string GetClarionAssistantPluginPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Check marketplace source
            string marketplacePath = Path.Combine(userProfile, ".claude", "plugins", "marketplaces",
                "clarionassistant-marketplace", "plugins", "clarion-assistant");
            if (Directory.Exists(marketplacePath))
                return marketplacePath;

            // Check cache
            string cachePath = Path.Combine(userProfile, ".claude", "plugins", "cache",
                "clarionassistant-marketplace", "clarion-assistant", "1.0.0");
            if (Directory.Exists(cachePath))
                return cachePath;

            return null;
        }

        private static string EscapeArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            // Wrap in quotes, escape internal quotes
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] != null)
                return dict[key].ToString();
            return null;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] is bool)
                return (bool)dict[key];
            return false;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] != null)
            {
                int result;
                if (int.TryParse(dict[key].ToString(), out result))
                    return result;
            }
            return 0;
        }

        private static double GetDouble(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.ContainsKey(key) && dict[key] != null)
            {
                double result;
                if (double.TryParse(dict[key].ToString(), out result))
                    return result;
            }
            return 0.0;
        }

        #endregion

        #region Event Raisers

        private void RaiseStreamEvent(ClaudeStreamEvent evt)
        {
            try { if (OnStreamEvent != null) OnStreamEvent(evt); } catch { }
        }

        private void RaiseAssistantText(string text)
        {
            try { if (OnAssistantText != null) OnAssistantText(text); } catch { }
        }

        private void RaiseTurnComplete(ClaudeStreamEvent evt)
        {
            try { if (OnTurnComplete != null) OnTurnComplete(evt); } catch { }
        }

        private void RaiseError(string message)
        {
            try { if (OnError != null) OnError(message); } catch { }
        }

        #endregion

        public void Dispose()
        {
            Cancel();
        }
    }
}
