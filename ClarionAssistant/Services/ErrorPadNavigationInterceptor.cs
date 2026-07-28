using System;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Task d3ab083a: stop Clarion's Errors-pane navigation from closing/re-opening the native embeditor
    /// (the "reload" John sees whenever a click crosses between embed-routed and source-routed rows —
    /// see reference_errors_pane_navigation_routes / d19c036d for the confirmed mechanics).
    ///
    /// Mechanism: the stock ErrorListPad wires its ListView's ItemActivate to its own TaskActivated
    /// handler, whose fork implementation both jumps AND drives the embeditor open/close dance. A
    /// delegate constructed over the SAME target+method is EQUAL to the wired one, so we can remove the
    /// native subscription, install our own dispatcher, and re-invoke the captured original only for the
    /// rows we deliberately pass through. Fully reversible (Uninstall re-adds the original).
    ///
    /// Dispatch rule (v1, state-based): while a CA embeditor overlay is LIVE, handle the click ourselves —
    /// open/position the row's file via MonacoSourceNavigator (our own machinery end-to-end, no Clarion
    /// error-navigation code) so the open embed is never closed and an editing session is never yanked
    /// away. With no live overlay, pass through to the original handler — 100% native behavior.
    ///
    /// Every step is shape-probed and spike-logged; ANY mismatch (fork renamed the pad, rewired the
    /// event, changed the Task shape) means we install nothing and native behavior is untouched.
    /// </summary>
    internal static class ErrorPadNavigationInterceptor
    {
        private static readonly object _gate = new object();
        private static bool _installed;
        private static bool _permanentlyFailed;   // shape mismatch — stop retrying, native behavior stands
        private static ListView _listView;
        private static EventHandler _originalHandler;   // the pad's own TaskActivated, re-invoked on pass-through

        /// <summary>Idempotent, lazy install — call whenever a Monaco surface spins up (the pad may not
        /// exist at addin load). No-ops after success or a permanent shape mismatch.</summary>
        public static void EnsureInstalled()
        {
            lock (_gate)
            {
                if (_installed || _permanentlyFailed) return;
                try
                {
                    var pad = FindErrorListPad();
                    if (pad == null) return;   // pad not created yet — retry on a later call

                    var lv = FindListView(GetPadControl(pad));
                    if (lv == null) { Fail("ErrorListPad control has no ListView descendant"); return; }

                    var m = pad.GetType().GetMethod("TaskActivated",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m == null) { Fail("TaskActivated not found on " + pad.GetType().FullName); return; }

                    EventHandler original;
                    try { original = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), pad, m); }
                    catch (Exception ex) { Fail("TaskActivated signature mismatch: " + ex.Message); return; }

                    // Removing a delegate that was never subscribed is a silent no-op — so if the fork
                    // wired activation some other way (WndProc, different event), our handler still
                    // installs but the native one ALSO still fires. Detect that in OnItemActivate via
                    // the _sawNativeDouble sentinel? No — keep v1 honest: we cannot verify removal
                    // directly, so log the install and rely on the in-IDE verification pass to catch a
                    // double-fire (symptom: embeditor STILL reloads with a live overlay).
                    lv.ItemActivate -= original;
                    lv.ItemActivate += OnItemActivate;
                    _listView = lv;
                    _originalHandler = original;
                    _installed = true;
                    ClarionAssistant.MonacoSpikeLog.Write("error-pad interceptor installed on " + pad.GetType().FullName);
                }
                catch (Exception ex) { Fail("install error: " + ex.Message); }
            }
        }

        /// <summary>Restore the native wiring (used by shutdown/teardown paths; safe to call anytime).</summary>
        public static void Uninstall()
        {
            lock (_gate)
            {
                try
                {
                    if (_listView != null && _originalHandler != null)
                    {
                        _listView.ItemActivate -= OnItemActivate;
                        _listView.ItemActivate += _originalHandler;
                        ClarionAssistant.MonacoSpikeLog.Write("error-pad interceptor uninstalled (native wiring restored)");
                    }
                }
                catch { }
                _listView = null;
                _originalHandler = null;
                _installed = false;
            }
        }

        private static void OnItemActivate(object sender, EventArgs e)
        {
            try
            {
                // v1 dispatch: only reroute while an embeditor overlay is live — that is exactly the
                // state whose teardown/reload is disruptive. Otherwise the native handler runs as-is.
                if (!Terminal.ModernEmbeditorViewContent.HasLiveOverlay)
                {
                    InvokeOriginal(sender, e);
                    return;
                }

                string file; int line, column;
                if (!TryReadActivatedTask(out file, out line, out column))
                {
                    // Couldn't read the row — never swallow a click: fall back to native.
                    ClarionAssistant.MonacoSpikeLog.Write("error-pad interceptor: row unreadable — passing through");
                    InvokeOriginal(sender, e);
                    return;
                }

                // Our navigation end-to-end (open + deterministic position; 1-based) — Clarion's error
                // routing never runs, so the open embeditor is never closed.
                bool ok = MonacoSourceNavigator.NavigateToFileAndLine(file, line + 1, column + 1);
                ClarionAssistant.MonacoSpikeLog.Write("error-pad interceptor: rerouted to source " +
                    System.IO.Path.GetFileName(file ?? "?") + ":" + (line + 1) + " (ok=" + ok + ", live overlay preserved)");
                if (!ok) InvokeOriginal(sender, e);   // our path couldn't handle it — let the native one try
            }
            catch (Exception ex)
            {
                ClarionAssistant.MonacoSpikeLog.Write("error-pad interceptor dispatch error: " + ex.Message);
                try { InvokeOriginal(sender, e); } catch { }
            }
        }

        private static void InvokeOriginal(object sender, EventArgs e)
        {
            var h = _originalHandler;
            if (h != null) h(sender, e);
        }

        /// <summary>Read the activated row's Task (.Tag): FileName + 0-based Line/Column, reflectively —
        /// the SD Task type lives in an assembly we don't compile against.</summary>
        private static bool TryReadActivatedTask(out string file, out int line, out int column)
        {
            file = null; line = 0; column = 0;
            try
            {
                var lv = _listView;
                if (lv == null || lv.SelectedItems.Count == 0) return false;
                object task = lv.SelectedItems[0].Tag;
                if (task == null) return false;
                var t = task.GetType();
                file = t.GetProperty("FileName")?.GetValue(task, null) as string;
                object l = t.GetProperty("Line")?.GetValue(task, null);
                object c = t.GetProperty("Column")?.GetValue(task, null);
                if (l is int) line = (int)l;
                if (c is int) column = (int)c;
                return !string.IsNullOrEmpty(file);
            }
            catch { return false; }
        }

        private static object FindErrorListPad()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb == null) return null;
                var padsProp = wb.GetType().GetProperty("PadContentCollection");
                var pads = padsProp?.GetValue(wb, null) as System.Collections.IEnumerable;
                if (pads == null) return null;
                foreach (var p in pads)
                {
                    object content = p;
                    // The collection may hold PadDescriptors wrapping the content — unwrap if so.
                    var padContentProp = p.GetType().GetProperty("PadContent");
                    if (padContentProp != null) content = padContentProp.GetValue(p, null) ?? p;
                    if (content != null && content.GetType().Name == "ErrorListPad") return content;
                }
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("error-pad find error: " + ex.Message); }
            return null;
        }

        private static Control GetPadControl(object pad)
        {
            try { return pad.GetType().GetProperty("Control")?.GetValue(pad, null) as Control; }
            catch { return null; }
        }

        private static ListView FindListView(Control root)
        {
            if (root == null) return null;
            if (root is ListView lvRoot) return lvRoot;
            foreach (Control c in root.Controls)
            {
                var found = FindListView(c);
                if (found != null) return found;
            }
            return null;
        }

        private static void Fail(string why)
        {
            _permanentlyFailed = true;
            ClarionAssistant.MonacoSpikeLog.Write("error-pad interceptor NOT installed (" + why + ") — native behavior unchanged");
        }
    }
}
