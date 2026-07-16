using System;
using System.Collections.Generic;
using ClarionAssistant.Terminal;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Routes CA Find/Replace traffic between the dockable CaFindPad and whichever Monaco editor host
    /// (CA source editor / CA Embeditor) is ACTIVE (GitHub #66, ticket 91e6ecac).
    ///
    /// Topology: the find UI lives in the pad (ca-find.html); the match/replace/decoration ENGINE stays
    /// in each editor page (monaco-embeditor.html) because the match decorations are the live position
    /// source across buffer edits (#65). The pad is a remote control:
    ///   pad page -> CaFindPad -> FromPad(json) -> active host's MonacoEditorControl.PostJson
    ///   editor page -> host (caFindUpdate/caFindOpen/caFindActivity) -> FromEditor -> pad page
    ///
    /// "Active" = the editor whose Monaco text area most recently gained focus (the page posts
    /// caFindActivity on onDidFocusEditorText). No SharpDevelop workbench events are involved — this
    /// SD fork has no ActiveViewContentChanged (see reference_embed_open_visiblechanged_trigger).
    ///
    /// Everything is defensive: a dead pad or dead host silently drops traffic, never throws.
    /// </summary>
    public static class CaFindBroker
    {
        private sealed class HostEntry
        {
            public object Host;
            public MonacoEditorControl Control;
            public Func<string> Key;     // session identity: file path (source editor) / app::proc (embeditor)
            public Func<string> Title;   // short display name for the pad's target strip
            public string Kind;          // "CA Editor" | "CA Embeditor"
        }

        private static readonly object _lock = new object();
        private static readonly List<HostEntry> _hosts = new List<HostEntry>();
        private static HostEntry _active;
        private static Action<string> _padPoster;      // posts JSON into the pad page (null = pad not open)
        private static string _pendingForPad;          // a caFindOpen that arrived while the pad page was still loading

        // ── Host lifecycle ─────────────────────────────────────────────────────────────────────

        /// <summary>Register a Monaco host when its page is ready. Re-registering the same host
        /// updates its entry (the embeditor view can re-init its control).</summary>
        public static void RegisterHost(object host, MonacoEditorControl control,
                                        Func<string> key, Func<string> title, string kind)
        {
            if (host == null || control == null) return;
            lock (_lock)
            {
                UnregisterLocked(host);
                _hosts.Add(new HostEntry { Host = host, Control = control, Key = key, Title = title, Kind = kind });
            }
        }

        /// <summary>Unregister on host dispose. If it was the active target, the pad drops to idle.</summary>
        public static void UnregisterHost(object host)
        {
            bool wasActive;
            lock (_lock)
            {
                wasActive = _active != null && ReferenceEquals(_active.Host, host);
                UnregisterLocked(host);
                if (wasActive) _active = null;
            }
            if (wasActive) NotifyPadActiveChanged();
        }

        private static void UnregisterLocked(object host)
        {
            for (int i = _hosts.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_hosts[i].Host, host)) _hosts.RemoveAt(i);
        }

        /// <summary>The editor's Monaco surface gained focus — it becomes the pad's target.
        /// No-op (no pad notification) when it already is the target.</summary>
        public static void NotifyActivity(object host)
        {
            bool changed = false;
            lock (_lock)
            {
                var e = FindLocked(host);
                if (e != null && !ReferenceEquals(_active, e)) { _active = e; changed = true; }
            }
            if (changed) NotifyPadActiveChanged();
        }

        private static HostEntry FindLocked(object host)
        {
            for (int i = 0; i < _hosts.Count; i++)
                if (ReferenceEquals(_hosts[i].Host, host)) return _hosts[i];
            return null;
        }

        // ── Pad lifecycle ──────────────────────────────────────────────────────────────────────

        /// <summary>The pad page is ready (or closing, pass null). On attach the pad immediately
        /// receives the current active-editor identity so it can restore that session.</summary>
        public static void SetPadPoster(Action<string> poster)
        {
            string pending;
            lock (_lock) { _padPoster = poster; pending = _pendingForPad; _pendingForPad = null; }
            if (poster == null) return;
            NotifyPadActiveChanged();
            // The very FIRST Ctrl+F creates the pad; its page wasn't ready when the open request came
            // through, so it was parked here — deliver it now (after activeEditor, so the session's set).
            if (pending != null) { try { poster(pending); } catch { } }
        }

        // ── Routing ────────────────────────────────────────────────────────────────────────────

        /// <summary>Editor page -> pad. rawJson is the page's own message ({action:"caFindUpdate"...}
        /// or {action:"caFindOpen"...}), embedded verbatim as .msg with the sender's identity around
        /// it. caFindOpen also makes the sender active and raises the pad.</summary>
        public static void FromEditor(object host, string action, string rawJson)
        {
            try
            {
                if (action == "caFindActivity") { NotifyActivity(host); return; }
                if (action == "caFindOpen")
                {
                    NotifyActivity(host);
                    ShowPad();   // create/raise the pad; it restores this editor's session on activeEditor
                }
                HostEntry e; Action<string> pad;
                lock (_lock) { e = FindLocked(host); pad = _padPoster; }
                if (e == null) return;
                string wrapped = "{\"type\":\"caFindFromEditor\",\"key\":" + MonacoEditorControl.JsonString(SafeKey(e))
                    + ",\"msg\":" + (string.IsNullOrEmpty(rawJson) ? "{}" : rawJson) + "}";
                if (pad == null)
                {
                    // Pad still loading (ShowPad above just created it): park a caFindOpen for replay on
                    // attach so the first-ever Ctrl+F still lands with its seed. Other traffic just drops.
                    if (action == "caFindOpen") lock (_lock) { _pendingForPad = wrapped; }
                    return;
                }
                pad(wrapped);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindBroker] FromEditor: " + ex.Message); }
        }

        /// <summary>Pad -> active editor page. fwdJson is the complete page-bound message the pad
        /// built ({type:"caFind",op:...}); posted verbatim. Returns false when there is no target
        /// (pad shows its idle state from activeEditor, so this is belt-and-braces).</summary>
        public static bool FromPad(string fwdJson)
        {
            try
            {
                HostEntry e;
                lock (_lock) { e = _active; }
                if (e == null || e.Control == null || string.IsNullOrEmpty(fwdJson)) return false;
                e.Control.PostJson(fwdJson);
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindBroker] FromPad: " + ex.Message); return false; }
        }

        /// <summary>Tell the pad which editor it is targeting now (key=null -> idle). Sent on activity
        /// change, active-host dispose, and pad attach.</summary>
        private static void NotifyPadActiveChanged()
        {
            try
            {
                HostEntry e; Action<string> pad;
                lock (_lock) { e = _active; pad = _padPoster; }
                if (pad == null) return;
                if (e == null) { pad("{\"type\":\"activeEditor\",\"key\":null}"); return; }
                pad("{\"type\":\"activeEditor\",\"key\":" + MonacoEditorControl.JsonString(SafeKey(e))
                    + ",\"title\":" + MonacoEditorControl.JsonString(SafeCall(e.Title))
                    + ",\"kind\":" + MonacoEditorControl.JsonString(e.Kind ?? "") + "}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindBroker] NotifyPad: " + ex.Message); }
        }

        private static string SafeKey(HostEntry e)
        {
            string k = SafeCall(e.Key);
            return string.IsNullOrEmpty(k) ? ("host-" + e.Host.GetHashCode()) : k;
        }

        private static string SafeCall(Func<string> f)
        {
            try { return f != null ? (f() ?? "") : ""; } catch { return ""; }
        }

        // ── Pad activation (Ctrl+F in an editor) ──────────────────────────────────────────────

        /// <summary>Create/raise the CA Find pad — same reflection shape as ShowModernDataPadCommand.
        /// Marshalled to the UI thread by the workbench itself (BringPadToFront is UI-safe here since
        /// WebMessageReceived already runs on the UI thread).</summary>
        public static void ShowPad()
        {
            try
            {
                var workbench = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench;
                if (workbench == null) return;
                var getPad = workbench.GetType().GetMethod("GetPad", new Type[] { typeof(Type) });
                var pad = getPad != null ? getPad.Invoke(workbench, new object[] { typeof(CaFindPad) }) : null;
                if (pad != null)
                {
                    var bring = pad.GetType().GetMethod("BringPadToFront");
                    if (bring != null) bring.Invoke(pad, null);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindBroker] ShowPad: " + ex.Message); }
        }
    }
}
