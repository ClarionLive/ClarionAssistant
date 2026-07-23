using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// One place that persists + broadcasts Monaco gear-panel settings across EVERY open Monaco surface,
    /// regardless of which host owns it:
    ///   • the Modern Embeditor (ModernEmbeditorViewContent), and
    ///   • the Monaco source / default editor (MonacoClarionEditor).
    /// Both surfaces load the SAME monaco-embeditor.html, so a gear change in either must persist once and
    /// push an `applySettings` message to all the others (live update + gear repopulate).
    ///
    /// WHY THIS EXISTS (deac3d16): cross-tab broadcast appeared "broken" because only the embeditor host
    /// implemented saveSettings — MonacoClarionEditor.OnSaveSettings was an empty no-op, so a setting changed
    /// in the default Monaco editor neither persisted nor synced (and silently dropped, no log). Routing both
    /// hosts through this bus makes the source editor a first-class participant and unifies the broadcast so
    /// it crosses editor types (embeditor↔source), not just same-type tabs.
    ///
    /// Each open surface Register()s a sink — a delegate that PostJson's a host→page message to its webview —
    /// and Dispose()s the registration when it tears down. Sinks are invoked on the caller's (UI) thread; each
    /// host's PostJson already marshals to its own webview.
    /// </summary>
    public static class MonacoSettingsBroadcaster
    {
        private static readonly object _gate = new object();
        private static readonly List<Action<string>> _sinks = new List<Action<string>>();
        private const int MaxBridgeJsonBytes = 65536;   // mirror ModernEmbeditorViewContent.MaxBridgeJsonBytes

        // GH #126: when the developer OKs Options → Text Editor, re-push the stored settings to every open
        // Monaco surface — ToDict() re-reads the IDE indentation pair live, so followers pick the new values
        // up immediately instead of on their next open. Static ctor = hooked once, on first Monaco use.
        static MonacoSettingsBroadcaster()
        {
            try
            {
                ICSharpCode.Core.PropertyService.PropertyChanged += (s, e) =>
                {
                    try
                    {
                        if (e != null && e.Key == "TextEditorSettings")
                            Broadcast(ModernEmbeditorSettings.Load());
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoSettingsBroadcaster] ide-options push: " + ex.Message); }
                };
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoSettingsBroadcaster] PropertyChanged hook: " + ex.Message); }
        }

        /// <summary>Register a sink (host→page JSON poster). Dispose the returned token to unregister.</summary>
        public static IDisposable Register(Action<string> postJson)
        {
            if (postJson == null) return new Token(null);
            lock (_gate) _sinks.Add(postJson);
            return new Token(postJson);
        }

        /// <summary>
        /// Parse a raw `{action:"saveSettings", settings:{...}}` bridge payload, persist the settings, then
        /// push `applySettings` to every registered surface. No-op on a malformed/oversized payload.
        /// </summary>
        public static void SaveAndBroadcastFromBridge(string rawJson)
        {
            var sd = ExtractSettingsDict(rawJson);
            if (sd == null) return;
            var settings = ModernEmbeditorSettings.FromDict(sd);
            try { settings.Save(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoSettingsBroadcaster] persist: " + ex.Message); }
            Broadcast(settings);
        }

        /// <summary>Serialize the settings to an `applySettings` message and push to every registered surface.</summary>
        public static void Broadcast(ModernEmbeditorSettings settings)
        {
            if (settings == null) return;
            string json;
            try { json = "{\"type\":\"applySettings\",\"settings\":" + new JavaScriptSerializer().Serialize(settings.ToDict()) + "}"; }
            catch { return; }
            Action<string>[] snapshot;
            lock (_gate) snapshot = _sinks.ToArray();
            foreach (var sink in snapshot)
            {
                try { sink(json); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoSettingsBroadcaster] sink: " + ex.Message); }
            }
        }

        private static Dictionary<string, object> ExtractSettingsDict(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxBridgeJsonBytes) return null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = MaxBridgeJsonBytes }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return null;
                object s;
                return data.TryGetValue("settings", out s) ? s as Dictionary<string, object> : null;
            }
            catch { return null; }
        }

        private sealed class Token : IDisposable
        {
            private Action<string> _sink;
            public Token(Action<string> sink) { _sink = sink; }
            public void Dispose()
            {
                if (_sink == null) return;
                lock (_gate) _sinks.Remove(_sink);
                _sink = null;
            }
        }
    }
}
