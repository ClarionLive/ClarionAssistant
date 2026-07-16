using System;
using System.Diagnostics;
using System.Reflection;
using ICSharpCode.Core;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command — starts the Clarion Language Server EARLY and
    /// independent of any pad.
    ///
    /// Previously the LSP only ever started via AssistantChatControl (created lazily when
    /// the "Claude Chat" pad was first shown). A user who opened the Modern Embeditor
    /// WITHOUT opening that pad got no hover/completion because the start hook was never
    /// wired. This autostart runs at workbench load and:
    ///   (a) wires EmbeditorCompletionService.LspStarter pane-independently,
    ///   (b) starts immediately if a solution is already restored,
    ///   (c) subscribes to ProjectService.SolutionLoaded to start on later solution loads,
    ///   (d) keeps a low-frequency fallback timer that retries while no server is running
    ///       and a solution is open (idempotent).
    ///
    /// Everything is guarded — this MUST NOT throw at workbench load, and it must NOT
    /// construct AssistantChatControl or any pad.
    /// </summary>
    public class LspAutostartCommand : ICommand
    {
        private object _owner;
        public object Owner
        {
            get { return _owner; }
            set
            {
                _owner = value;
                var h = OwnerChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public event EventHandler OwnerChanged;

        // Rooted so neither the SolutionLoaded/SolutionClosed delegates nor the fallback timer are GC'd.
        private static Delegate _solutionLoadedHandler;
        private static Delegate _solutionClosedHandler;
        private static System.Windows.Forms.Timer _fallbackTimer;

        public void Run()
        {
            try
            {
                // (a) Wire the open-path / completion-time self-heal hook pane-independently.
                EmbeditorCompletionService.LspStarter = () => LspService.EnsureRunningInBackground();
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] wire LspStarter failed: " + ex.Message); }

            try
            {
                // (b) A solution is often already restored by the time the workbench loads.
                if (!string.IsNullOrEmpty(EditorService.GetOpenSolutionPath()))
                    LspService.EnsureRunningInBackground();
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] immediate start failed: " + ex.Message); }

            try
            {
                SubscribeSolutionLoaded();
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] SolutionLoaded subscribe failed: " + ex.Message); }

            try
            {
                SubscribeSolutionClosed();
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] SolutionClosed subscribe failed: " + ex.Message); }

            try
            {
                StartFallbackTimer();
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] fallback timer failed: " + ex.Message); }
        }

        /// <summary>
        /// Subscribes to ICSharpCode.SharpDevelop.Project.ProjectService.SolutionLoaded via
        /// reflection (same assembly/type EditorService uses). The event is a static
        /// EventHandler&lt;SolutionEventArgs&gt;; we build a matching delegate that ignores its
        /// args and kicks a background LSP start.
        /// </summary>
        private void SubscribeSolutionLoaded()
        {
            var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
            if (sharpDevelopAsm == null) return;

            var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
            if (projectServiceType == null) return;

            var evt = projectServiceType.GetEvent("SolutionLoaded",
                BindingFlags.Public | BindingFlags.Static);
            if (evt == null) return;

            // Build a delegate of the event's handler type that forwards to OnSolutionLoaded.
            MethodInfo handlerMethod = typeof(LspAutostartCommand).GetMethod(
                "OnSolutionLoaded", BindingFlags.NonPublic | BindingFlags.Static);
            if (handlerMethod == null) return;

            _solutionLoadedHandler = Delegate.CreateDelegate(evt.EventHandlerType, handlerMethod);
            evt.AddEventHandler(null, _solutionLoadedHandler);
        }

        // Matches EventHandler<SolutionEventArgs>(object sender, EventArgs e). Reflection
        // binds this to the event's concrete handler type regardless of the args subtype.
        private static void OnSolutionLoaded(object sender, EventArgs e)
        {
            try { LspService.EnsureRunningInBackground(); }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] OnSolutionLoaded failed: " + ex.Message); }
        }

        /// <summary>
        /// Subscribes to ProjectService.SolutionClosed (a static EventHandler) so we STOP the bundled
        /// server when the current solution closes. Without this the one LspClient.Active process
        /// survives a solution switch still rooted at the FIRST solution — EnsureRunning is idempotent and
        /// "starts ONCE" with no live post-start path update, so the next SolutionLoaded can't re-root it.
        /// Stopping here lets the SolutionLoaded -> EnsureRunning path start a fresh server for the new
        /// solution (a new process re-initializes cleanly with the new rootUri). (#106)
        /// </summary>
        private void SubscribeSolutionClosed()
        {
            var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
            if (sharpDevelopAsm == null) return;

            var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
            if (projectServiceType == null) return;

            var evt = projectServiceType.GetEvent("SolutionClosed",
                BindingFlags.Public | BindingFlags.Static);
            if (evt == null) return;

            MethodInfo handlerMethod = typeof(LspAutostartCommand).GetMethod(
                "OnSolutionClosed", BindingFlags.NonPublic | BindingFlags.Static);
            if (handlerMethod == null) return;

            _solutionClosedHandler = Delegate.CreateDelegate(evt.EventHandlerType, handlerMethod);
            evt.AddEventHandler(null, _solutionClosedHandler);
        }

        // ProjectService.SolutionClosed is a plain EventHandler(object, EventArgs). Stop the bundled
        // server (only if it's ours and running) so it re-roots on the next solution. Never touch the
        // shared ClarionLsp addin — it owns its own lifecycle.
        private static void OnSolutionClosed(object sender, EventArgs e)
        {
            try
            {
                if (SharedLspBridge.IsSharedActive) return;
                var c = LspClient.Active;
                if (c != null && c.IsRunning)
                {
                    Debug.WriteLine("[LspAutostart] Solution closed — stopping the bundled LSP so the next solution re-roots it.");
                    c.Stop();
                }
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] OnSolutionClosed failed: " + ex.Message); }
        }

        /// <summary>
        /// Low-frequency safety net: retries a background start while no server is running
        /// and a solution is open. Harmless once running (EnsureRunningInBackground no-ops).
        /// </summary>
        private void StartFallbackTimer()
        {
            if (_fallbackTimer != null) return;

            _fallbackTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _fallbackTimer.Tick += (s, e) =>
            {
                try
                {
                    // Single-process reconciliation: if the shared ClarionLsp server is now active
                    // (it may have loaded AFTER us and we started our bundled server in the start
                    // race — we no longer have a manifest load-order dependency), stop our redundant
                    // bundled server so only one LSP process runs. All bridge calls already route to
                    // the shared client, so ours is dead weight at this point.
                    if (SharedLspBridge.IsSharedActive)
                    {
                        var ours = LspClient.Active;
                        if (ours != null && ours.IsRunning)
                        {
                            Debug.WriteLine("[LspAutostart] Shared ClarionLsp active — stopping redundant bundled LSP server.");
                            try { ours.Stop(); } catch (Exception ex) { Debug.WriteLine("[LspAutostart] stop redundant server failed: " + ex.Message); }
                        }
                        return;
                    }

                    if (LspClient.Active != null && LspClient.Active.IsRunning) return; // idempotent no-op
                    if (string.IsNullOrEmpty(EditorService.GetOpenSolutionPath())) return;
                    LspService.EnsureRunningInBackground();
                }
                catch (Exception ex) { Debug.WriteLine("[LspAutostart] fallback tick failed: " + ex.Message); }
            };
            _fallbackTimer.Start();
        }
    }
}
