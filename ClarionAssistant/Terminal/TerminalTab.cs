using System;
using System.Windows.Forms;
using ClarionAssistant.Services;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Represents a single tab in the Clarion Assistant tab system.
    /// Home tab has IsHome=true and no Renderer/Terminal.
    /// Terminal tabs each own their own WebViewTerminalRenderer + ConPtyTerminal.
    /// </summary>
    public class TerminalTab : IDisposable
    {
        public string Id { get; private set; }
        public string Name { get; set; }
        public bool IsHome { get; internal set; }
        public bool IsClosable { get; internal set; }

        /// <summary>The WebView2 terminal renderer (null for Home tab).</summary>
        public WebViewTerminalRenderer Renderer { get; set; }

        /// <summary>The ConPTY terminal process (null for Home tab).</summary>
        public ConPtyTerminal Terminal { get; set; }

        /// <summary>The control to show in the tab page (HomeWebView or WebViewTerminalRenderer).</summary>
        public Control ContentControl { get; set; }

        /// <summary>The TabPage that hosts this tab's content.</summary>
        public TabPage Page { get; set; }

        /// <summary>Solution loaded in this tab.</summary>
        public string SolutionPath { get; set; }

        /// <summary>Override working directory for this tab (e.g. solution folder).</summary>
        public string WorkingDirectory { get; set; }

        /// <summary>Clarion version config for this tab.</summary>
        public ClarionVersionConfig VersionConfig { get; set; }

        /// <summary>Knowledge service session ID.</summary>
        public int SessionId { get; set; }

        /// <summary>Whether an AI assistant has been launched in this tab.</summary>
        public bool AssistantLaunched { get; set; }

        /// <summary>
        /// Backend used for this tab's terminal session (e.g., "Claude" or "Copilot").
        /// Stored per-tab so existing tabs keep correct exit/status behavior even if settings change.
        /// </summary>
        public string AssistantBackend { get; set; }

        /// <summary>
        /// Optional pre-launch backend override requested by the dashboard backend dropdown.
        /// Consumed by <c>LaunchAssistantForTab</c>; null means "use saved default".
        /// Distinct from <see cref="AssistantBackend"/> which records the backend the tab
        /// actually launched with (set post-launch).
        /// </summary>
        public string RequestedBackend { get; set; }

        /// <summary>Skill command to auto-run after Claude starts (e.g. "/ClarionCOM").</summary>
        public string StartupCommand { get; set; }

        /// <summary>Schema sources panel for this tab (null for Home tab).</summary>
        public SchemaSourcesView SchemaSourcesView { get; set; }

        private bool _disposed;

        public TerminalTab()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (Terminal != null)
            {
                try { Terminal.Stop(); } catch { }
                try { Terminal.Dispose(); } catch { }
                Terminal = null;
            }

            if (Renderer != null)
            {
                try { Renderer.Dispose(); } catch { }
                Renderer = null;
            }

            if (SchemaSourcesView != null)
            {
                try { SchemaSourcesView.Dispose(); } catch { }
                SchemaSourcesView = null;
            }

            ContentControl = null;
        }
    }
}
