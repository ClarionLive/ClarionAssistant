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

        /// <summary>
        /// The "Terminal N" name TabManager.CreateTerminalTab numbered this tab with, whatever name it was then
        /// given. A tab that follows the IDE's solution shows it while the IDE has no solution open, so its label
        /// is the agent name alone ("CA1") rather than a solution none of its tools serve.
        /// </summary>
        public string DefaultName { get; set; }

        /// <summary>
        /// True for a tab whose label names the IDE's open solution and follows every switch and close (F4,
        /// ticket 9e1bd280): New Chat and a tab opened on a solution - their tools answer for the IDE's solution.
        /// False for a tab opened on a project, a class or Evaluate Code: that context is not the solution.
        /// </summary>
        public bool FollowsIdeSolution { get; set; }

        /// <summary>
        /// The tab's short name, captured at launch: CA1/CA2/... for Claude - also the identity
        /// it is known by in MultiTerminal (its MULTITERMINAL_NAME and -n) - CP1/CP2/... for
        /// Copilot and CX1/CX2/... for Codex, which are labels only (ticket cfb63b58). Null until
        /// an assistant is launched, and again once it exits.
        ///
        /// Read by AssistantChatControl.ResolveUniqueAgentName: other tabs' launches treat it as
        /// taken. It has to be STORED rather than recomputed: the number depends on what else
        /// was open at launch, so asking again later could give a different one.
        /// </summary>
        public string AgentName { get; set; }

        /// <summary>
        /// The tab's name before any launch decorated it ("Terminal 2", or the solution/project/
        /// class it was opened on), captured on the first launch. Every relabel is built from
        /// this, so a relaunch never stacks "CA2 · CA2 · ..." or a second backend suffix.
        /// </summary>
        public string BaseName { get; set; }

        /// <summary>
        /// Tail of the previous terminal output chunk, kept so the assistant-exited marker is
        /// still recognised when a read splits it (CaAgentIdentity.SeesExitSignal). Touched only
        /// by the terminal's output handler.
        /// </summary>
        public string ExitSignalCarry { get; set; }

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

            ContentControl = null;
        }
    }
}
