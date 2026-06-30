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
    /// Ordered steps, each guarded:
    ///   0. Arm a HARD-EXIT WATCHDOG on a background thread (the never-hang guarantee). It waits on a
    ///      completion signal and force-kills the process ONLY if managed teardown never finishes (a dispose
    ///      deadlocked) — a clean-but-slow shutdown signals done and is NOT killed. So the IDE can never become
    ///      an unkillable DLL-holding zombie, yet we don't truncate a healthy slow exit.
    ///   1. Stop the MCP server (clean HttpListener close; its listener thread is already a background thread).
    ///   2. KILL every ConPty child-process tree (pwsh -> claude/node -> conhost) FAST — the teardown could
    ///      block on ClosePseudoConsole / pipe disposes / an INFINITE process-wait. Bounded on a worker so
    ///      even a stuck kill can't stall the IDE.
    ///   3. KILL the LSP node.exe tree FAST (skip the graceful handshake) — a leaked node handle can keep the
    ///      IDE alive after its windows are gone. Bounded.
    ///   4. Dispose every WebView2 instance ON THE UI THREAD before native IDE teardown (the WebView2 &lt;-&gt;
    ///      native focus-deadlock precedent: the embeditor Discard hang). Covers ALL live WebView2 owners:
    ///      embeditor, diff, monaco-diff, lifecycle board, the Modern Data/Explorer pad, and the chat pad
    ///      (which owns the HUD header + home view). The step-0 watchdog backstops a UI-thread deadlock here.
    ///
    /// Invoked from BOTH /Workspace/Terminate (ShutdownTerminateCommand) AND an Application.ApplicationExit
    /// backstop — whichever fires first wins; a double-fire is a no-op.
    /// </summary>
    public static class ShutdownService
    {
        private static bool _done;
        private static McpServer _mcpServer;
        private static EventHandler _appExitHandler;

        // Signaled the instant Terminate()'s managed teardown returns. The watchdog waits on THIS rather
        // than a blind sleep, so a clean shutdown — even a slow one — is never force-killed; the hard exit
        // fires ONLY if managed teardown never completes (the one case we can't unblock: a WebView2 dispose
        // deadlocking on the UI thread). Once managed teardown finishes, all addin handles (WebView2/ConPty/
        // node) are released, so native IDE teardown is no longer blocked by us.
        private static readonly ManualResetEventSlim _teardownDone = new ManualResetEventSlim(false);

        /// <summary>Wire the ApplicationExit backstop ONCE, unconditionally. Called from addin autostart
        /// (ShutdownAutostartCommand) so the backstop exists even in sessions that never start the chat MCP
        /// server — the /Workspace/Terminate command is the primary hook, this is the fallback. Idempotent.</summary>
        public static void ArmBackstop()
        {
            if (_appExitHandler != null) return;
            _appExitHandler = (s, e) =>
            {
                ShutdownLog.Log("ApplicationExit fired -> Terminate()");
                try { Terminate(); }
                catch (Exception ex) { Debug.WriteLine("[Shutdown] appExit: " + ex.Message); ShutdownLog.Log("appExit Terminate() threw: " + ex.Message); }
            };
            try
            {
                Application.ApplicationExit += _appExitHandler;
                // Session-start delimiter on disk: confirms the backstop armed at addin load, and bounds this
                // IDE run as a block so a post-mortem can find the last session's trace. (intermittent shutdown hang)
                ShutdownLog.LogSessionStart("backstop armed");
            }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] appExit subscribe: " + ex.Message); ShutdownLog.Log("appExit subscribe failed: " + ex.Message); }
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
            if (_done) { ShutdownLog.Log("Terminate() re-entry ignored (already done)"); return; }
            _done = true;
            ShutdownLog.Log("Terminate() begin");

            // 0. HARD-EXIT WATCHDOG — the never-hang guarantee. WebView2 disposal must run on THIS (the UI)
            //    thread, so a synchronous same-thread dispose that truly deadlocks cannot be unblocked from
            //    here. This background watchdog is the backstop: it waits on _teardownDone and force-kills the
            //    process ONLY if managed teardown never signals completion within the budget — i.e. a dispose
            //    deadlocked. A clean shutdown (even a slow one) signals done and is NOT killed, so we don't
            //    truncate host finalization on a healthy-but-slow exit. The budget is generous (it just bounds
            //    the genuinely-stuck case); the event makes a clean exit return immediately regardless of it.
            ArmHardExitWatchdog(15000);
            Debug.WriteLine("[Shutdown] begin");
            ShutdownLog.Log("watchdog armed (15000ms)");

            // 1. Stop the MCP server (fast, clean).
            ShutdownLog.Log("step 1: MCP stop ...");
            try { if (_mcpServer != null) _mcpServer.Stop(); ShutdownLog.Log("step 1: MCP stop done"); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] MCP stop: " + ex.Message); ShutdownLog.Log("step 1: MCP stop failed: " + ex.Message); }

            // 2. Kill all ConPty child-process trees FAST, bounded so a stuck kill can't stall shutdown.
            ShutdownLog.Log("step 2: ConPty kill ...");
            try { RunBounded(ConPtyTerminal.KillAllForShutdown, 2500); ShutdownLog.Log("step 2: ConPty kill returned"); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] ConPty kill: " + ex.Message); ShutdownLog.Log("step 2: ConPty kill failed: " + ex.Message); }

            // 3. Kill the LSP node.exe tree FAST (skip the graceful handshake Stop() does), bounded — a leaked
            //    node handle is a prime suspect for keeping the IDE alive after the windows are gone.
            ShutdownLog.Log("step 3: LSP kill ...");
            try { RunBounded(Services.LspClient.KillForShutdown, 2000); ShutdownLog.Log("step 3: LSP kill returned"); }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] LSP kill: " + ex.Message); ShutdownLog.Log("step 3: LSP kill failed: " + ex.Message); }

            // 4. Dispose all WebView2 instances on the UI thread (this call runs on it) BEFORE native teardown,
            //    to avoid the WebView2 <-> native focus deadlock. Each is independently guarded + marked; the
            //    watchdog in step 0 is the hard backstop if any one of them deadlocks on the UI thread.
            DisposeWebView2("embeditor", ModernEmbeditorViewContent.DisposeAllForShutdown);
            DisposeWebView2("diff", DiffViewContent.DisposeAllForShutdown);
            DisposeWebView2("monaco diff", MonacoDiffViewContent.DisposeAllForShutdown);
            DisposeWebView2("board", TaskLifecycleBoardForm.DisposeAllForShutdown);
            DisposeWebView2("data pad", ModernDataPad.DisposeAllForShutdown);   // Explorer pad (GAP 2)
            DisposeWebView2("chat pad", AssistantChatControl.DisposeAllForShutdown); // owns HUD header + home view (GAP 2)

            // Also release the WebView2 inside any MODELESS top-level dialog open at shutdown (settings, cheat
            // sheet, datapad settings, create-class, etc.) — these aren't in the pad registries. Leaving one
            // live would make _teardownDone.Set() below dishonest AND, since the watchdog disarms on that
            // signal, its native teardown could hang with no backstop. We dispose the WebView2 CONTROLS, not
            // the Forms: disposing/closing a Form can fire FormClosed handlers that re-enter pads already
            // disposed above (e.g. the chat pad's home view) — touching a dead WebView2 on the very UI-thread
            // path we're trying to keep clean. Disposing just the WebView2 child releases the native browser
            // handle (the actual hang source) and leaves an inert form shell for native teardown.
            DisposeWebView2("open dialogs", DisposeOpenFormWebViews);

            // Managed teardown finished — all addin handles released. Signal the watchdog to stand down so a
            // slow-but-clean shutdown is never force-killed. (If a dispose above had deadlocked, we'd never
            // reach here and the watchdog would fire — exactly the never-hang case it exists for.)
            _teardownDone.Set();
            Debug.WriteLine("[Shutdown] teardown complete");
            ShutdownLog.Log("teardown complete — _teardownDone set, watchdog stands down (native IDE teardown now proceeds)");
        }

        /// <summary>Run one WebView2 disposer on the current (UI) thread, guarded and Debug-marked so verify
        /// can see exactly how far teardown got. A dispose that deadlocks here is caught by the step-0 watchdog,
        /// NOT by this method (a synchronous same-thread call can't be time-bounded in-place).</summary>
        private static void DisposeWebView2(string label, Action dispose)
        {
            try
            {
                Debug.WriteLine("[Shutdown] dispose " + label + " ...");
                ShutdownLog.Log("dispose " + label + " ...");   // if the matching 'done' never follows, THIS owner's dispose deadlocked
                dispose();
                Debug.WriteLine("[Shutdown] dispose " + label + " done");
                ShutdownLog.Log("dispose " + label + " done");
            }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] dispose " + label + " failed: " + ex.Message); ShutdownLog.Log("dispose " + label + " failed: " + ex.Message); }
        }

        /// <summary>Dispose the WebView2 control(s) inside every OPEN top-level WinForms Form (modeless dialogs
        /// like settings / cheat sheet / datapad-settings / create-class). Runs on the UI thread before native
        /// teardown. We dispose the WebView2 CONTROLS, never the Forms — disposing/closing a Form can raise
        /// FormClosed handlers that re-enter pads already disposed earlier in step 4 (poking a dead WebView2).
        /// Snapshots Application.OpenForms, and snapshots each Control collection before recursing (disposing a
        /// WebView2 mutates its parent's Controls). Only forms actually hosting a WebView2 are touched, so
        /// native IDE forms are left alone. Idempotent (skips already-disposed forms/controls).</summary>
        private static void DisposeOpenFormWebViews()
        {
            Form[] snapshot;
            try
            {
                var open = Application.OpenForms;
                snapshot = new Form[open.Count];
                for (int i = 0; i < snapshot.Length; i++) snapshot[i] = open[i];
            }
            catch (Exception ex) { Debug.WriteLine("[Shutdown] open-forms snapshot failed: " + ex.Message); return; }

            foreach (var f in snapshot)
            {
                try { if (f != null && !f.IsDisposed) DisposeWebView2ChildrenOf(f, f.GetType().Name); }
                catch (Exception ex) { Debug.WriteLine("[Shutdown] open-form WebView2 dispose failed: " + ex.Message); }
            }
        }

        /// <summary>Recursively dispose any WebView2 control in <paramref name="c"/>'s subtree. Snapshots each
        /// Controls collection before iterating because WebView2.Dispose() removes the control from its parent.</summary>
        private static void DisposeWebView2ChildrenOf(Control c, string formName)
        {
            var wv = c as Microsoft.Web.WebView2.WinForms.WebView2;
            if (wv != null)
            {
                try
                {
                    if (!wv.IsDisposed)
                    {
                        Debug.WriteLine("[Shutdown] dispose WebView2 in form: " + formName);
                        wv.Dispose();
                    }
                }
                catch (Exception ex) { Debug.WriteLine("[Shutdown] dispose form WebView2 failed: " + ex.Message); }
                return; // a WebView2 hosts no nested WebView2
            }

            int n = c.Controls.Count;
            var children = new Control[n];
            for (int i = 0; i < n; i++) children[i] = c.Controls[i];
            foreach (var child in children) DisposeWebView2ChildrenOf(child, formName);
        }

        /// <summary>Arm a background watchdog that force-terminates this process ONLY if managed teardown
        /// fails to signal <see cref="_teardownDone"/> within <paramref name="ms"/> — i.e. a dispose deadlocked.
        /// A clean shutdown (even a slow one) signals done and returns here immediately, so a healthy-but-slow
        /// exit is never killed. Process.Kill is preferred (immediate, runs no managed cleanup that could itself
        /// hang); Environment.Exit is the fallback.</summary>
        private static void ArmHardExitWatchdog(int ms)
        {
            var t = new Thread(() =>
            {
                bool completed;
                try { completed = _teardownDone.Wait(ms); } catch { completed = false; }
                if (completed) return;   // managed teardown finished cleanly — stand down, do NOT kill
                try { Debug.WriteLine("[Shutdown] WATCHDOG fired after " + ms + "ms (teardown stuck) — forcing process exit"); } catch { }
                // GOLD LINE for the intermittent hang: if this is the LAST entry in the log but the process is
                // still alive, Process.Kill couldn't reap it (a thread stuck in an uninterruptible kernel-mode
                // wait) — NOT a missed managed owner. Compare against the last "dispose X ..." to see what stuck.
                ShutdownLog.Log("WATCHDOG FIRED after " + ms + "ms — teardown never signaled done; forcing Process.Kill now");
                try { Process.GetCurrentProcess().Kill(); }
                catch (Exception ex) { ShutdownLog.Log("Process.Kill threw: " + ex.Message + " — falling back to Environment.Exit"); try { Environment.Exit(0); } catch { } }
            }) { IsBackground = true, Name = "Shutdown-Watchdog" };
            t.Start();
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
