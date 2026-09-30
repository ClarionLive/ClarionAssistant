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
    ///       and a solution is open (idempotent),
    ///   (e) follows Build &gt; Set Clarion Version: reloads the .red and restarts the server on a switch
    ///       (905928c7; that too was chat-only).
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

        // 905928c7: follows Build > Set Clarion Version with or without a CA chat tab. Rooted with its
        // PropertyChanged handler (PropertyService.PropertyChanged is static).
        private static IdeVersionFollower _versionFollower;
        private static PropertyChangedEventHandler _versionHandler;
        private static System.Threading.SynchronizationContext _uiContext;

        public void Run()
        {
            // 1c685f2e item 8: a node crash or a dead reader loop gets a line in monaco-spike.log, beside
            // the [lsp-timing] / [diag-timing] lines it explains (the addin installs no LspTrace sink).
            LspClient.LifecycleLog = MonacoSpikeLog.Write;
            SymbolIndex.LogSink = MonacoSpikeLog.Write;   // noIndex / busy lines from the local layer's DB lookups
            // The local layer's databases (both Monaco surfaces): the solution's CodeGraph and the ClarionGraph library.
            LocalLayerHandlers.ProjectDbPath = () => { var p = SharedLspBridge.CodeGraphDbPathProvider; return p != null ? p() : null; };
            LocalLayerHandlers.LibraryDbPath = ClarionGraphService.ResolveDbPath;
            // f64ba833: where the walk-up starts when a surface has no real module path (a CA Embeditor whose
            // module context wasn't captured): the IDE's open solution folder.
            LocalLayerHandlers.SolutionDirPath = () =>
            {
                string sln = EditorService.GetOpenSolutionPath();
                return string.IsNullOrEmpty(sln) ? null : System.IO.Path.GetDirectoryName(sln);
            };

            // 1c685f2e L2 (pre-existing on master 2fcb940): the LSP never started unless a CA chat tab had opened.
            // Every start funnels through LspService.EnsureRunning, which takes the solution from
            // LspService.SolutionPathProvider, and that hook was set only by AssistantChatControl. With no chat,
            // every SolutionLoaded, immediate and 5 s fallback start returned NoSolution, silently. The IDE's open
            // solution is the same answer the chat control gives; set it here, at addin start, unless a host already did.
            if (LspService.SolutionPathProvider == null)
                LspService.SolutionPathProvider = () => EditorService.GetOpenSolutionPath();
            LspService.StartLog = MonacoSpikeLog.Write;   // [lsp-autostart] start|skip reason=

            try
            {
                // (e) Seed and subscribe BEFORE any start below (pipeline run 1): a seed taken after a start
                // could absorb a version change that start had missed, and nothing would restart the server.
                StartVersionFollower();
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] version follower failed: " + ex.Message); }

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
        /// 905928c7: follow the IDE's Build &gt; Set Clarion Version from addin start. It used to be followed
        /// only by AssistantChatControl.SyncVersionWithIde, so with no CA chat tab a switch left the language
        /// server on the old version's paths and the .red unchanged. Clarion's Versions.SetActiveVersion (the
        /// menu command) and SetActiveVersionFromSolution both end in PropertyService.Set("Clarion.Version"),
        /// which raises PropertyChanged; the 5 s fallback tick re-checks, so a missed event only delays it.
        /// The chat panel keeps its own header refresh and leaves the restart to this.
        /// </summary>
        private static void StartVersionFollower()
        {
            if (_versionFollower != null) return;
            _uiContext = System.Threading.SynchronizationContext.Current;
            _versionFollower = new IdeVersionFollower();
            _versionFollower.Changed += OnIdeVersionChanged;
            CheckIdeVersion();   // seeds

            _versionHandler = (s, e) =>
            {
                try
                {
                    if (e == null || e.Key != "Clarion.Version") return;
                    // Posted, even on the UI thread: run after the IDE has finished its own switch.
                    var ctx = _uiContext;
                    if (ctx != null) ctx.Post(_ => CheckIdeVersion(), null);
                    else CheckIdeVersion();
                }
                catch { }
            };
            PropertyService.PropertyChanged += _versionHandler;
        }

        /// <summary>One PropertyService read; raises OnIdeVersionChanged when the choice moved. Never throws.</summary>
        private static void CheckIdeVersion()
        {
            try
            {
                var f = _versionFollower;
                string live;
                if (f == null || !ClarionVersionService.TryGetLiveIdeVersionName(out live)) return;
                f.Observe(live);
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] version check failed: " + ex.Message); }
        }

        private static void OnIdeVersionChanged(string was, string now)
        {
            try
            {
                var sel = EffectiveClarionVersion.Resolve();
                string name = sel.Config != null ? sel.Config.Name : null;
                MonacoSpikeLog.Write("[version-follow] IDE Build > Set Clarion Version: " + was + " -> " + now
                    + "; " + sel.Describe());
                // Watchers keyed on the version (Data pad environment, library graph memo) — the chat panel
                // was the only thing bumping these.
                ClarionGraphService.InvalidateVersionCache();
                EffectiveClarionVersion.NotifyChanged();
                // The .red: keyed on version + .red path + solution, so this reloads only on a real change.
                ModernEmbeditorLauncher.EnsureRedirectionLoaded();
                // The server takes its .red and library paths once, at start. No-op when nothing is running,
                // when it already serves this version, or while the shared ClarionLsp addin owns the LSP.
                LspService.RestartIfVersionChanged(name);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[version-follow] failed: " + ex.Message); }
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
        // The stop runs on the pool (4d63b995): SolutionClosed fires on the UI thread, also while the IDE
        // closes, and Stop() sleeps ~400 ms. ShutdownService's KillForShutdown still reaps the process at exit.
        private static void OnSolutionClosed(object sender, EventArgs e)
        {
            ShutdownLog.Close("OnSolutionClosed begin");
            // Completion's held-open symbol DB connections belong to the closed solution (1c685f2e).
            try { SymbolIndex.ReleaseAll(); } catch { }
            try
            {
                if (!SharedLspBridge.IsSharedActive)
                {
                    var c = LspClient.Active;
                    if (c != null && c.IsRunning)
                    {
                        Debug.WriteLine("[LspAutostart] Solution closed — stopping the bundled LSP so the next solution re-roots it.");
                        c.StopInBackground(m => ShutdownLog.Close(m));
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[LspAutostart] OnSolutionClosed failed: " + ex.Message); }
            ShutdownLog.Close("OnSolutionClosed end");
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
                // Backstop for the Clarion.Version event, ahead of the shared-LSP return: the .red and the
                // version watchers follow the IDE even while the shared ClarionLsp addin owns the server.
                CheckIdeVersion();

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
