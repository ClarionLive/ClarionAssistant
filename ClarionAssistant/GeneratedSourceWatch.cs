using System;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// GH #263 Q2 follow-up: keeps each CA Editor page's generatedSource flag current. setSource computes it once,
    /// so a tab opened (or restored at startup) BEFORE its app loaded would keep generatedSource=false, and with it
    /// the hover card's attribute chips on a generated module, whose edits the next generate wipes.
    ///
    /// Re-checks every live CA Editor page when the workbench opens or closes a view (an .app view among them) or
    /// changes the active window (an app finishing its load, then the developer coming back to the tab). The events
    /// come in bursts at startup, so the re-check is debounced; each page is posted to only when its answer changed
    /// (Services.GeneratedSourceFlag). Wired lazily, on the first setSource of a CA Editor page. UI thread only.
    /// </summary>
    internal static class GeneratedSourceWatch
    {
        private const int DebounceMs = 750;
        private static readonly string[] ViewEvents = { "ViewOpened", "ViewClosed" };
        // Idempotent wiring (GH #263 run-2 review): each subscription is marked attempted BEFORE it runs, and
        // is never retried, so no event can get the handler twice. Only a missing workbench is retried (the
        // next setSource calls again).
        private static bool _activeAttempted;
        private static readonly Services.EventWiringOnce _viewWiring = new Services.EventWiringOnce();
        private static Timer _debounce;

        internal static void EnsureWired()
        {
            if (_activeAttempted && _viewWiring.AllAttempted(ViewEvents)) return;
            object wbObj;
            try { wbObj = WorkbenchSingleton.Workbench; } catch { return; }
            var wb = wbObj as IWorkbench;
            if (wb == null) return;   // retried on the next setSource

            if (!_activeAttempted)
            {
                _activeAttempted = true;
                try
                {
                    wb.ActiveWorkbenchWindowChanged += OnWorkbenchChanged;
                    MonacoSpikeLog.Write("[generated-source] WIRED: ActiveWorkbenchWindowChanged");
                }
                catch (Exception ex) { MonacoSpikeLog.Write("[generated-source] ActiveWorkbenchWindowChanged wiring failed (not retried): " + ex.Message); }
            }
            var handler = typeof(GeneratedSourceWatch).GetMethod("OnWorkbenchChanged", BindingFlags.NonPublic | BindingFlags.Static);
            _viewWiring.WireAll(wb, ViewEvents, handler, s => MonacoSpikeLog.Write("[generated-source] Workbench." + s));
        }

        private static void OnWorkbenchChanged(object sender, EventArgs e)
        {
            try
            {
                if (_debounce == null)
                {
                    _debounce = new Timer { Interval = DebounceMs };
                    _debounce.Tick += (s, a) => { _debounce.Stop(); RefreshAll(); };
                }
                _debounce.Stop();
                _debounce.Start();
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[generated-source] debounce failed: " + ex.Message); }
        }

        internal static void RefreshAll()
        {
            try { MonacoClarionEditor.RefreshGeneratedSourceFlags(); }
            catch (Exception ex) { MonacoSpikeLog.Write("[generated-source] CA Editor refresh failed: " + ex.Message); }
            try { Terminal.ModernEmbeditorViewContent.RefreshGeneratedSourceFlags(); }
            catch (Exception ex) { MonacoSpikeLog.Write("[generated-source] CA Editor(tab) refresh failed: " + ex.Message); }
        }
    }
}
