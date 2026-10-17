using System;
using System.Diagnostics;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// GH #205 pipeline run 1: the IDE's call-back endpoint for the standalone clarion-mcp-server, WITHOUT a chat pane.
    ///
    /// The standalone serves write_file / append_to_file / lsp_diagnostics and has no editors; it calls back into the IDE
    /// (editor_tab_sync: is this file open in a native tab; get_live_text: an open editor's text) through the endpoint
    /// record an IDE McpServer publishes (IdeEndpointRecord) and matches it to a solution through IdeSolutionRecord.
    /// Both used to be published only by the chat pane's McpServer (AssistantChatControl / ClassHelperControl), so with
    /// no chat pane every write said "open tabs could not be checked" - the chat-only-initialization trap.
    ///
    /// Started from LspAutostartCommand at addin start. It runs a small McpServer whose registry is cut down to those two
    /// tools (McpToolRegistry.RetainOnly): it is a call-back channel, not a tool surface for a model, and it accepts
    /// only its own per-session token (no external token), on its own port range (19390-19399) so it can never take
    /// the chat pane's 19372 that external clients are configured for. It does not retry a taken preferred port.
    ///
    /// ONE RECORD PER IDE: McpServer publishes the record for whichever of its servers started first and hands it to
    /// another running one when that stops, so this and a chat pane's server coexist without double-publishing, and
    /// closing the chat pane never leaves the IDE unreachable.
    ///
    /// It also publishes the IDE's open solution (IdeSolutionRecord.Publish, which writes only on change and removes
    /// on close) every 10 s, the same poll the chat pane runs; two publishers of the same value are harmless.
    /// </summary>
    internal static class IdeCallbackHost
    {
        internal const int PreferredPort = 19390;
        internal static readonly string[] CallbackTools = { "get_live_text", "editor_tab_sync" };

        private static McpServer _server;
        private static Timer _solutionTimer;

        internal static McpServer Server { get { return _server; } }

        /// <summary>UI thread, addin start. Never throws.</summary>
        internal static void Start()
        {
            if (_server != null) return;
            try
            {
                var registry = new McpToolRegistry(new EditorService(), new ClarionClassParser(), true);
                registry.RetainOnly(CallbackTools);
                Control ui = WorkbenchSingleton.MainForm;
                var server = new McpServer(ui, null) { ReclaimPreferredPort = false };
                server.SetToolRegistry(registry);
                if (!server.Start(PreferredPort))
                {
                    MonacoSpikeLog.Write("[ide-callback] could not start: no free port in " + PreferredPort + "-" + (PreferredPort + 9));
                    return;
                }
                _server = server;
                MonacoSpikeLog.Write("[ide-callback] serving " + string.Join(", ", CallbackTools) + " on port " + server.Port
                    + (McpServer.EndpointOwner == server ? " (endpoint record published)" : " (another server of this IDE holds the record)"));
                Application.ApplicationExit += (s, e) => { try { server.Stop(); } catch { } };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[IdeCallbackHost] start failed: " + ex.Message);
                MonacoSpikeLog.Write("[ide-callback] start failed: " + ex.Message);
            }

            try
            {
                PublishSolution();
                _solutionTimer = new Timer { Interval = 10000 };
                _solutionTimer.Tick += (s, e) => PublishSolution();
                _solutionTimer.Start();
            }
            catch (Exception ex) { Debug.WriteLine("[IdeCallbackHost] solution poll failed: " + ex.Message); }
        }

        private static void PublishSolution()
        {
            try { IdeSolutionRecord.Publish(EditorService.GetOpenSolutionPath()); }
            catch (Exception ex) { Debug.WriteLine("[IdeCallbackHost] solution publish failed: " + ex.Message); }
        }
    }
}
