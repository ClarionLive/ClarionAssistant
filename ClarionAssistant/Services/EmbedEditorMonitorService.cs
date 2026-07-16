using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// TICKET 4d16b53a — trigger for the live CA Embeditor overlay. Attaches our floating Monaco overlay onto a
    /// native PWEE embeditor that the developer opened via Clarion's OWN built-in "Embeditor Source" popup entry
    /// (or the Views-strip Embeditor button) — no injected menu, no native hook. Replacement for the retired
    /// RightClickHookService menu-injection (adapted from Mark Sarson's EmbedEditorMonitor, CA issue #55).
    ///
    /// TWO triggers, sharing one (editor, PweeEditorDetails) dedup so whichever fires first attaches and the
    /// other no-ops:
    ///   1. EVENT FAST-PATH — the ClaGenEditor host panel's Control.VisibleChanged. Switching the app window to
    ///      the embed (secondary) view makes that panel visible, firing VisibleChanged synchronously before the
    ///      WinForms loop pumps WM_PAINT. This is the ONLY pre-paint signal available: this SD fork has NO
    ///      workbench ActiveViewContentChanged, and the window's TitleChanged/SecondaryViewsUpdated do NOT fire on
    ///      the view-switch (title stays the .app name; the ClaGenEditor persists in the secondaries list) —
    ///      both verified live (4d16b53a log). The handler DEFERS the attach off the SwitchView stack
    ///      (freeze-safety) via the captured UI SynchronizationContext; the posted turn precedes WM_PAINT and the
    ///      attach's synchronous pre-cover then hides the native text before it paints → no flicker. The host
    ///      panel only exists after the first embed opens, so we (re)subscribe it during the poll's discovery
    ///      step; the very first open per session falls to the poll, subsequent opens are event-driven.
    ///   2. POLL FALLBACK — a 300ms UI-thread timer (also the discovery step that subscribes the host once it
    ///      exists). With the pre-cover this bounds the worst-case native-visible window even when the event misses.
    ///
    /// Single-embeditor model: only one embed open at a time. Gated on CaEditorSettings.MonacoEmbeditorEnabled
    /// (default ON). All work is on the UI thread; fully guarded. Writes a diagnostic to
    /// %APPDATA%\ClarionAssistant\embed-monitor.log so the event-wire/fire path is observable.
    /// </summary>
    public static class EmbedEditorMonitorService
    {
        private static Timer _pollTimer;
        private static bool _started;
        private static volatile bool _shuttingDown;
        private static System.Threading.SynchronizationContext _uiCtx;

        private static object _lastEditor;
        private static object _lastPwee;

        // ClaGenEditor host panels whose VisibleChanged we've subscribed (reference identity).
        private static readonly HashSet<Control> _watchedControls = new HashSet<Control>();

        private const int StartupDelayMs = 3000;   // workbench isn't ready at autostart
        private const int PollIntervalMs = 300;    // backstop + discovery; the VisibleChanged event is primary

        private static void Log(string msg) => Debug.WriteLine("[EmbedEditorMonitor] " + msg);

        // d4635694 — durable diagnostics for the attach/dedup decisions (Debug.WriteLine is invisible in a
        // deployed IDE; this made the tab-away/tab-back lost-edits repro undiagnosable). Transition-gated by
        // the callers, so the 300ms poll can't spam the file.
        private static void FileLog(string msg) => MonacoSpikeLog.Write("[embed-monitor] " + msg);

        private static string IdOf(object o) =>
            o == null ? "null" : "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString("x8");

        /// <summary>Start the monitor (called from /Workspace/Autostart). Idempotent; defers until the workbench is up.</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            _shuttingDown = false;

            var startup = new Timer { Interval = StartupDelayMs };
            startup.Tick += (s, e) =>
            {
                startup.Stop();
                startup.Dispose();
                BeginMonitoring();
            };
            startup.Start();
        }

        private static void BeginMonitoring()
        {
            if (_shuttingDown) return;
            if (WorkbenchSingleton.Workbench == null)
            {
                var retry = new Timer { Interval = StartupDelayMs };
                retry.Tick += (s, e) => { retry.Stop(); retry.Dispose(); BeginMonitoring(); };
                retry.Start();
                return;
            }

            // Capture the UI SynchronizationContext (we're on the UI thread here) so the VisibleChanged handler —
            // which fires synchronously inside SwitchView — can defer the attach onto a settled turn off that stack.
            _uiCtx = System.Threading.SynchronizationContext.Current;

            _pollTimer = new Timer { Interval = PollIntervalMs };
            _pollTimer.Tick += (s, e) => { EnsureHostWatched(); TryAttachIfNewEmbed("poll"); };
            _pollTimer.Start();

            EnsureHostWatched();
            Log("started — " + PollIntervalMs + "ms poll + ClaGenEditor VisibleChanged event");
            TryAttachIfNewEmbed("startup");
        }

        /// <summary>Subscribe the ClaGenEditor host panel's VisibleChanged once it exists (it's created on the first
        /// embed open and then persists). Deduped by control identity; cheap + idempotent per poll tick.</summary>
        private static void EnsureHostWatched()
        {
            try
            {
                var host = new AppTreeService().GetClaGenEditorHost();
                if (host == null || _watchedControls.Contains(host)) return;
                _watchedControls.Add(host);
                host.VisibleChanged += OnHostVisibleChanged;
                host.Disposed += OnHostDisposed;
                Log("watching ClaGenEditor host VisibleChanged (" + host.GetType().Name + ")");
            }
            catch (Exception ex) { Log("EnsureHostWatched: " + ex.Message); }
        }

        private static void OnHostDisposed(object sender, EventArgs e)
        {
            var c = sender as Control;
            if (c == null) return;
            try { c.VisibleChanged -= OnHostVisibleChanged; c.Disposed -= OnHostDisposed; } catch { }
            _watchedControls.Remove(c);
        }

        private static void OnHostVisibleChanged(object sender, EventArgs e)
        {
            if (_shuttingDown) return;
            try
            {
                var c = sender as Control;
                if (c != null && !c.Visible) return;   // attach only when the embed view is shown, not hidden
                // TryAttachIfNewEmbed no-ops for an embed we already overlaid (dedup) — that's the tab-back
                // case, where the overlay survives hidden but nothing re-focuses it (no SwitchedTo for an
                // overlay). OnEmbedHostShown hands it the keyboard + re-claims the CA Find pad (d4635694).
                if (_uiCtx != null) _uiCtx.Post(_ => { TryAttachIfNewEmbed("visible"); ClarionAssistant.Terminal.ModernEmbeditorViewContent.OnEmbedHostShown(c); }, null);
                else { TryAttachIfNewEmbed("visible"); ClarionAssistant.Terminal.ModernEmbeditorViewContent.OnEmbedHostShown(c); }
            }
            catch (Exception ex) { Log("OnHostVisibleChanged: " + ex.Message); }
        }

        /// <summary>Attach the overlay iff a NEW native PWEE embed (or a different proc in the reused editor) is
        /// open. Shared by the event fast-path and the poll fallback; (editor, pwee) identity dedup means the
        /// first caller attaches and the rest no-op. UI thread only.</summary>
        private static void TryAttachIfNewEmbed(string source)
        {
            if (_shuttingDown) return;
            try
            {
                if (!CaEditorSettings.MonacoEmbeditorEnabled) return;   // master CA-embeditor toggle (default ON)
                if (ModernEmbeditorLauncher.IsBusy) return;             // don't fight our own launch paths

                var at = new AppTreeService();
                var editor = at.GetOpenClaGenEditor();
                if (editor == null)
                {
                    // Embed closed → reset dedup. d4635694: log the transition (once, not per poll tick) —
                    // a reset while an overlay holds unsaved edits is the lost-edits smoking gun.
                    if (_lastEditor != null) FileLog("dedup reset — editor no longer found (" + source + ")");
                    _lastEditor = null; _lastPwee = null; return;
                }

                var pwee = at.GetOpenPweeDetails();
                if (pwee == null)
                {
                    if (_lastPwee != null) FileLog("dedup reset — pwee gone, editor alive (" + source + ")");
                    _lastEditor = null; _lastPwee = null; return;      // editor but no PWEE loaded
                }

                // Same embed + same proc we already handled → nothing to do.
                if (ReferenceEquals(editor, _lastEditor) && ReferenceEquals(pwee, _lastPwee)) return;

                FileLog("attach trigger (" + source + "): editor " + IdOf(_lastEditor) + "→" + IdOf(editor)
                        + " pwee " + IdOf(_lastPwee) + "→" + IdOf(pwee));

                // New embed / proc-change → (re)attach. Record BEFORE attaching so a re-entrant call can't double-fire.
                _lastEditor = editor; _lastPwee = pwee;

                string err = ModernEmbeditorLauncher.AttachOverlayToOpenEmbed(isDark: false);
                Log(err != null ? ("attach skipped (" + source + "): " + err) : ("attached via " + source));
                FileLog(err != null ? ("attach skipped (" + source + "): " + err) : ("attached via " + source));
            }
            catch (Exception ex)
            {
                Log("TryAttachIfNewEmbed(" + source + ") error: " + ex.Message);
            }
        }

        /// <summary>Tear down at /Workspace/Terminate. Guarded; harmless if Start() never ran.</summary>
        public static void Terminate()
        {
            _shuttingDown = true;
            try { if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); _pollTimer = null; } } catch { }
            foreach (var c in new List<Control>(_watchedControls))
            {
                try { c.VisibleChanged -= OnHostVisibleChanged; c.Disposed -= OnHostDisposed; } catch { }
            }
            _watchedControls.Clear();
            _lastEditor = null; _lastPwee = null;
            _started = false;
        }
    }
}
