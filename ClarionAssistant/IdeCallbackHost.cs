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
    ///
    /// b8f9431c: IT SERVES EVERY IdeOnly TOOL (McpToolRegistry.BuildIdeCallbackRegistry; exclusions, if any, are named
    /// with reasons in McpToolRegistry.IdeCallbackExcludedTools). The standalone server that MultiTerminal, VS Code and
    /// other plugin clients run lists those tools and forwards them here (IdeToolForwarder), so they work whenever
    /// Clarion is open on the client's solution, with no CA tab. The handlers are the CA tab's own, on this IDE's
    /// workspace (#266 path policy and its allowed roots) and UI dispatcher (McpDispatcher's UI timeout and call token).
    ///
    /// TRUST MODEL (b8f9431c). The listener is the same McpServer as the chat pane's 19372 (an http://localhost:&lt;port&gt;/
    /// HttpListener prefix; the standalone only ever connects to 127.0.0.1), every request must carry this server's per-start token (constant-time compare; NO external token: the server is built
    /// without settings), and Host/Origin are validated (McpServer.ValidateHost / ValidateOrigin: DNS rebinding and
    /// browser drive-by). The token is written to the endpoint record under %LOCALAPPDATA%, so ANY PROCESS RUNNING AS THIS
    /// USER can read it and now reach the IDE's write tools (write_embed_content, replace_text, import_txa, ...). That is
    /// not a new boundary crossed: (1) whenever a CA chat pane is open, its server on 19372 serves the same tools, and
    /// its per-session token sits in %APPDATA%\ClarionAssistant\mcp-config.json (McpServer.WriteMcpConfigFile), the
    /// external token (when enabled) in settings.txt, and the endpoint record itself carries the PANE's token whenever the
    /// pane's server holds it - all readable by the same user; (2) with no pane open, 19372 is not listening, so what
    /// this change adds is that state: Clarion open, no CA tab. A same-user process could already drive Clarion there
    /// by other means - inject into Clarion.exe (same user, same integrity level), rewrite the .app/.clw files or the
    /// add-in's settings, or replace the plugin's server exe. The record trust rules (IdeEndpointRecord: pid alive, is
    /// Clarion, start time matches) protect the CLIENT from talking to a non-Clarion process; they are not, and never
    /// were, a defence against the user's own processes (IdeSolutionRecord.VerifyWriter says the same).
    /// </summary>
    internal static class IdeCallbackHost
    {
        internal const int PreferredPort = 19390;
        internal static readonly string[] CallbackTools = McpToolRegistry.IdeCallbackTools;

        private static McpServer _server;
        private static Timer _solutionTimer;

        internal static McpServer Server { get { return _server; } }

        /// <summary>
        /// b8f9431c: the registry statics the IdeOnly handlers read, which used to be set ONLY by the chat pane
        /// (AssistantChatControl.StartMcpServer) - the chat-only-initialization trap. Without them, with no CA tab: the
        /// app-tree and embeditor tools had no AppTreeService (the registry reads AppTreeFactory in its constructor), the
        /// probe tools no IdeProbeService, and - the dangerous one - EmbedOverlayGuard had no CA Embeditor probes, so it
        /// read "no CA Embeditor" and would have let write_embed_content / save_and_close_embeditor write the native
        /// embeditor hidden behind one. The values are the chat pane's own, so whichever sets them first, they agree.
        /// </summary>
        internal static void InstallRegistryHooks()
        {
            McpToolRegistry.AppTreeFactory = () => new AppTreeService();
            McpToolRegistry.IdeProbeFactory = () => new IdeProbeService();
            McpToolRegistry.DiagnosticLog = msg => MonacoSpikeLog.Write(msg);
            McpToolRegistry.CaEmbeditorLiveProbe = () => Terminal.ModernEmbeditorViewContent.HasLiveOverlay;
            McpToolRegistry.ActiveEditorCoveredProbe = () => Terminal.ModernEmbeditorViewContent.ActiveEditorIsCoveredByOverlay();
        }

        /// <summary>UI thread, addin start. Never throws.</summary>
        internal static void Start()
        {
            if (_server != null) return;
            try
            {
                // Pipeline run 2 (HIGH): WITH the IDE's workspace. editor_tab_sync's check is read-gated by the #266 path
                // policy, built from the workspace; with none every solution file was refused, the check came back as an
                // error, and the write went ahead under a dirty native tab.
                Control ui = WorkbenchSingleton.MainForm;
                try { ModernEmbeditorLauncher.EnsureRedirectionLoaded(); } catch { }   // the .red the policy's roots come from
                InstallRegistryHooks();   // BEFORE the registry is built: its constructor reads AppTreeFactory/IdeProbeFactory
                var registry = McpToolRegistry.BuildIdeCallbackRegistry(new EditorService(), new ClarionClassParser(),
                    new IdeWorkspace(), new McpServer.ControlUiDispatcher(ui), new DiffService());
                var settings = new SettingsService();   // read here for the UI budget only; NOT given to the server (external token)
                var server = new McpServer(ui, null)
                {
                    ReclaimPreferredPort = false,
                    UiTimeoutSettingReader = () => settings.Get(McpUiTimeoutPolicy.SettingKey)
                };
                server.SetToolRegistry(registry);
                if (!server.Start(PreferredPort))
                {
                    MonacoSpikeLog.Write("[ide-callback] could not start: no free port in " + PreferredPort + "-" + (PreferredPort + 9));
                    return;
                }
                _server = server;
                MonacoSpikeLog.Write("[ide-callback] serving " + registry.GetToolCount() + " IDE tools (the call-backs "
                    + string.Join(", ", CallbackTools) + " and every IdeOnly tool, for plugin clients) on port " + server.Port
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
            // Keep the .red in force for the open solution and version (IdeWorkspace.RedFile reads it). Keyed inside:
            // a no-op unless the version, its .red or the solution folder moved.
            try { ModernEmbeditorLauncher.EnsureRedirectionLoaded(); } catch { }
        }
    }
}
