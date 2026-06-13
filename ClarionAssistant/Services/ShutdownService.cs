using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using ClarionAssistant.Terminal;
using ClarionAssistant.TaskLifecycleBoard;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// One ordered, idempotent addin teardown. Fixes Clarion hanging on close (it could not even be
    /// end-tasked — "Access is denied"). The disable-the-addin-and-close test confirmed the addin is the
    /// cause; the prime suspect is ConPtyTerminal teardown blocking on native ConPty closes.
    ///
    /// Ordered steps, each guarded + time-bounded:
    ///   1. Stop the MCP server (clean HttpListener close; its listener thread is already a background thread).
    ///   2. KILL every ConPty child-process tree (pwsh -> claude/node -> conhost) FAST — the teardown could
    ///      block on ClosePseudoConsole / pipe disposes / an INFINITE process-wait. Bounded on a worker so
    ///      even a stuck kill can't stall the IDE.
    ///   3. Dispose every WebView2 instance ON THE UI THREAD before native IDE teardown (the WebView2 &lt;-&gt;
    ///      native focus-deadlock precedent: the embeditor Discard hang).
    ///
    /// Invoked from BOTH /Workspace/Terminate (ShutdownTerminateCommand) AND an Application.ApplicationExit
    /// backstop — whichever fires first wins; a double-fire is a no-op.
    /// </summary>
    public static class ShutdownService
    {
        private static bool _done;
        private static McpServer _mcpServer;
        private static EventHandler _appExitHandler;

        /// <summary>Wire the ApplicationExit backstop ONCE, unconditionally. Called from addin autostart
        /// (ShutdownAutostartCommand) so the backstop exists even in sessions that never start the chat MCP
        /// server — the /Workspace/Terminate command is the primary hook, this is the fallback. Idempotent.</summary>
        public static void ArmBackstop()
        {
            if (_appExitHandler != null) return;
            _appExitHandler = (s, e) =>
            {
                try { Terminate(); }
                catch (Exception ex) { Debug.WriteLine("[Shutdown] appExit: " + ex.Message); }
            };
            try { Application.ApplicationExit += _appExitHandler; }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] appExit subscribe: " + ex.Message); }
        }

        /// <summary>Record the MCP server so Terminate() can stop it (independent of the chat pad's Dispose
        /// ordering). Also arms the backstop as belt-and-suspenders in case autostart didn't run.</summary>
        public static void RegisterMcpServer(McpServer server)
        {
            _mcpServer = server;
            ArmBackstop();
        }

        /// <summary>The ordered teardown. Idempotent (first call wins). Each step is guarded so one failure
        /// can't block the rest. Runs on the UI thread (both /Workspace/Terminate and ApplicationExit fire there),
        /// which is required for the WebView2 disposal step.</summary>
        public static void Terminate()
        {
            if (_done) return;
            _done = true;

            // 1. Stop the MCP server (fast, clean).
            try { if (_mcpServer != null) _mcpServer.Stop(); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] MCP stop: " + ex.Message); }

            // 2. Kill all ConPty child-process trees FAST, bounded so a stuck kill can't stall shutdown.
            try { RunBounded(ConPtyTerminal.KillAllForShutdown, 2500); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] ConPty kill: " + ex.Message); }

            // 3. Dispose all WebView2 instances on the UI thread (this call runs on the UI thread) BEFORE
            //    native teardown, to avoid the WebView2 <-> native focus deadlock.
            try { ModernEmbeditorViewContent.DisposeAllForShutdown(); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] embeditor dispose: " + ex.Message); }
            try { DiffViewContent.DisposeAllForShutdown(); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] diff dispose: " + ex.Message); }
            try { MonacoDiffViewContent.DisposeAllForShutdown(); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] monaco diff dispose: " + ex.Message); }
            try { TaskLifecycleBoardForm.DisposeAllForShutdown(); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] board dispose: " + ex.Message); }
        }

        /// <summary>Run an action on a background worker bounded by a Join timeout. If it overruns we move on
        /// — at shutdown the processes are already being killed; we won't let a stuck native call hang the IDE.</summary>
        private static void RunBounded(Action a, int ms)
        {
            var t = new Thread(() => { try { a(); } catch { } }) { IsBackground = true, Name = "Shutdown-Bounded" };
            t.Start();
            t.Join(ms);
        }
    }
}
