using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    // ── Monaco-default-editor converge (task cc8b092f), STEPS 1–2 ───────────────────────────────
    // Goal (John/Mark): ONE Monaco protocol host shared by BOTH the standalone file-mode embeditor
    // view (ModernEmbeditorViewContent) AND the dual-control source overlay (MonacoClarionEditor),
    // instead of three separate WebView2/Monaco paths.
    //
    // STEP 1 (done): WebView2 lifecycle + nav + the JS<->C# transport skeleton.
    // STEP 2 (this revision): port the inbound action DISPATCH and the transport-pure outbound
    //   senders into the control; expand IMonacoEditorHost to the full typed callback surface.
    //   The state-assembling senders (SendSource / ApplyHistory / ApplySettings) stay host-side
    //   because they read host fields + domain types; the host builds their JSON and calls PostJson.
    //
    // STILL ZERO-IMPACT: nothing references this control yet, so ModernEmbeditorViewContent is
    // byte-for-byte unchanged. The dispatch + senders are COPIED (not cut) faithfully from the view
    // so STEP 3's rewire is a mechanical swap.

    /// <summary>
    /// Callbacks the reusable <see cref="MonacoEditorControl"/> needs from whatever embeds it (the
    /// standalone ModernEmbeditor view, or the dual-control source overlay). The control owns the
    /// WebView2 + Monaco page + JS&lt;-&gt;C# transport + the inbound action routing; every IDE-specific
    /// operation (source assembly, save round-trips, the structure designer, LSP completion / hover /
    /// diagnostics, settings / history / cursor / bookmark persistence, Data-pad refresh, file-mode
    /// reload) is delegated here. Each method maps 1:1 to an inbound page-&gt;host action so the routing
    /// is a thin switch. A host that doesn't care about an action implements it as a no-op.
    /// </summary>
    public interface IMonacoEditorHost
    {
        /// <summary>{action:"ready"} — the page is loaded. Host should push source (SendSource) plus
        /// any initial settings / history / bookmarks.</summary>
        void OnReady(MonacoEditorControl editor);

        /// <summary>{action:"save"} — embed mode: per-slot save; file mode: whole-buffer save.</summary>
        void OnSave(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"clipboard"} — Clarion-style cut: put text on the Windows clipboard.</summary>
        void OnClipboard(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"completion"} — LSP completion request; host replies via PostResponse.</summary>
        void OnCompletion(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"hover"} — LSP hover request; host replies via PostResponse.</summary>
        void OnHover(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"definition"} — F12 go-to-definition; host navigates (same- or cross-file)
        /// then replies via PostResponse with {navigated:bool}.</summary>
        void OnDefinition(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"diagnostics"} — hybrid LSP + slot diagnostics; host replies via PostResponse.</summary>
        void OnDiagnostics(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveSettings"} — persist gear-panel settings + broadcast to all tabs.</summary>
        void OnSaveSettings(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveHistory"} — persist Find/Replace history + broadcast to all tabs.</summary>
        void OnSaveHistory(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveCursor"} — persist cursor position per proc+solution.</summary>
        void OnSaveCursor(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveBookmarks"} — persist bookmark lines per proc+solution.</summary>
        void OnSaveBookmarks(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"selectionChanged"} — cache the Monaco selection snapshot.</summary>
        void OnSelectionChanged(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"focusEditor"} — drag-drop from the Data pad; host activates the tab.</summary>
        void OnFocusEditor(MonacoEditorControl editor);

        /// <summary>{action:"reload"} — file mode: re-read from disk, discard edits.</summary>
        void OnReload(MonacoEditorControl editor);

        /// <summary>{action:"fileState"} — file mode: page mirrors its live buffer + dirty flag.</summary>
        void OnFileState(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"openDesigner"} — Ctrl+D on an existing structure. The host enforces any
        /// mode guard (e.g. file mode refuses — the designer needs an embeditor-backed procedure).</summary>
        void OnOpenDesigner(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"openDesignerCreate"} — template-picker choice for a NEW structure.</summary>
        void OnOpenDesignerCreate(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"activateDesigner"} — 'Show designer' on the modal lock overlay.</summary>
        void OnActivateDesigner(MonacoEditorControl editor);

        /// <summary>Navigation to the Monaco page completed (IsSuccess). Optional liveness hook.</summary>
        void OnEditorNavigationCompleted(MonacoEditorControl editor, bool success);

        /// <summary>Any inbound action not matched above. Forward-compat / diagnostics.</summary>
        void OnUnknownAction(MonacoEditorControl editor, string action, string rawJson);
    }

    /// <summary>
    /// Reusable Monaco-over-WebView2 surface. Owns the <see cref="Panel"/> + <see cref="WebView2"/>,
    /// CoreWebView2 init, a per-instance virtual-host temp folder (large-buffer transfer via
    /// source.txt), navigation to the Monaco HTML (default monaco-embeditor.html) with the ?v=
    /// cache-bust, the inbound action dispatch, and the transport-pure outbound senders. Everything
    /// IDE-specific is delegated through <see cref="IMonacoEditorHost"/>.
    /// </summary>
    public class MonacoEditorControl : Panel
    {
        private readonly IMonacoEditorHost _host;
        private readonly string _htmlFileName;   // page under Terminal\ (the overlay may pick a different one)
        private readonly string _virtualHost;    // virtual host name mapped to this control's temp folder

        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _disposedControl;
        private string _tempDir;

        /// <summary>The underlying WebView2 (null until constructed / after dispose).</summary>
        public WebView2 WebView { get { return _webView; } }

        /// <summary>True once navigation to the Monaco page has completed successfully.</summary>
        public bool IsInitialized { get { return _isInitialized; } }

        /// <summary>Per-instance temp folder mapped to <see cref="VirtualHost"/>; hosts write the
        /// large source buffer here (source.txt) and hand Monaco a virtual-host URL. Null until init.</summary>
        public string TempDir { get { return _tempDir; } }

        /// <summary>Virtual host name the page resolves large buffers against (default
        /// "clarion-embeditor-data", matching monaco-embeditor.html).</summary>
        public string VirtualHost { get { return _virtualHost; } }

        public MonacoEditorControl(IMonacoEditorHost host, bool isDark = true,
                                   string htmlFileName = "monaco-embeditor.html",
                                   string virtualHost = "clarion-embeditor-data")
        {
            _host = host;
            _htmlFileName = string.IsNullOrEmpty(htmlFileName) ? "monaco-embeditor.html" : htmlFileName;
            _virtualHost = string.IsNullOrEmpty(virtualHost) ? "clarion-embeditor-data" : virtualHost;

            Dock = DockStyle.Fill;
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);

            // Plain WebView2 — Monaco's native mouseWheelZoom owns Ctrl+wheel inside the renderer.
            // DefaultBackgroundColor = the themed backdrop so the WebView2 surface shows the editor's colour
            // (not a black/white compositor flash) before Monaco's first paint.
            _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(_webView);

            // Init when the panel handle is realized (the WebView2 can only EnsureCoreWebView2 then).
            HandleCreated += OnHandleCreated;
        }

        // ── WebView2 lifecycle ──────────────────────────────────────────────────────────────────
        // Lifted (copied) from ModernEmbeditorViewContent.OnHandleCreated so STEP 3 is a faithful swap.
        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionEmbeditor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    _virtualHost, _tempDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                // DevTools OFF so F12 reaches the page's go-to-definition handler instead of being captured
                // natively by WebView2 for DevTools (AreBrowserAcceleratorKeysEnabled=false alone doesn't free
                // F12 while DevTools is enabled — that's why Ctrl+F/Ctrl+click work but F12 did nothing). Flip
                // to true temporarily if you need to debug the embeditor page. (ticket 6e8f2439)
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false; // let Monaco own Ctrl+S, not the browser

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
                else
                    System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] HTML missing: " + htmlPath);
            }
            catch (Exception ex)
            {
                _isInitializing = false; // allow retry
                System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // Source push is triggered by the JS "ready" message (OnReady), not here — avoids a double-send.
            try { if (_host != null) _host.OnEditorNavigationCompleted(this, e.IsSuccess); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] NavCompleted host error: " + ex.Message); }
        }

        // ── Inbound dispatch (page -> host) ─────────────────────────────────────────────────────
        // Ported (copied) from ModernEmbeditorViewContent.OnWebMessageReceived. The fileMode guards
        // that wrapped the designer cases there now live in the host's OnOpenDesigner* impls — the
        // control routes unconditionally; the host knows its own mode.
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                var h = _host;
                if (h == null) return;

                switch (action)
                {
                    case "ready":             h.OnReady(this); break;
                    case "save":              h.OnSave(this, json); break;
                    case "clipboard":         h.OnClipboard(this, json); break;
                    case "completion":        h.OnCompletion(this, json); break;
                    case "hover":             h.OnHover(this, json); break;
                    case "definition":        h.OnDefinition(this, json); break;
                    case "diagnostics":       h.OnDiagnostics(this, json); break;
                    case "saveSettings":      h.OnSaveSettings(this, json); break;
                    case "saveHistory":       h.OnSaveHistory(this, json); break;
                    case "saveCursor":        h.OnSaveCursor(this, json); break;
                    case "saveBookmarks":     h.OnSaveBookmarks(this, json); break;
                    case "selectionChanged":  h.OnSelectionChanged(this, json); break;
                    case "focusEditor":       h.OnFocusEditor(this); break;
                    case "reload":            h.OnReload(this); break;
                    case "fileState":         h.OnFileState(this, json); break;
                    case "openDesigner":      h.OnOpenDesigner(this, json); break;
                    case "openDesignerCreate":h.OnOpenDesignerCreate(this, json); break;
                    case "activateDesigner":  h.OnActivateDesigner(this); break;
                    default:                  h.OnUnknownAction(this, action, json); break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] Message error: " + ex.Message);
            }
        }

        // ── Outbound transport (host -> page) ───────────────────────────────────────────────────

        /// <summary>Send a JSON message host-&gt;page (PostWebMessageAsJson), marshalled to the UI
        /// thread. Note: WebView2 delivers it to JS as a parsed OBJECT, not a string — page handlers
        /// must not JSON.parse it. Used by the host for the state-assembled messages (SendSource etc.).</summary>
        public void PostJson(string json)
        {
            Action post = () =>
            {
                try { if (_webView != null && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(json); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostJson error: " + ex.Message); }
            };
            try { if (InvokeRequired) BeginInvoke(post); else post(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostJson marshal error: " + ex.Message); }
        }

        /// <summary>Send a raw string message host-&gt;page (PostWebMessageAsString), UI-thread marshalled.</summary>
        public void PostString(string message)
        {
            Action post = () =>
            {
                try { if (_webView != null && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsString(message); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostString error: " + ex.Message); }
            };
            try { if (InvokeRequired) BeginInvoke(post); else post(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostString marshal error: " + ex.Message); }
        }

        /// <summary>Jump the editor caret to a routine: {type:"gotoRoutine", name}.</summary>
        public void GotoRoutine(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            PostJson("{\"type\":\"gotoRoutine\",\"name\":" + JsonString(name) + "}");
        }

        /// <summary>Reveal + position the ALREADY-open editor at a 1-based line: {type:"revealLine", line, column}.
        /// The page centers the line (revealLineInCenter) and focuses the caret. Used for debugger / breakpoint-list
        /// navigation against the source overlay (where moving the hidden native caret would be invisible).</summary>
        public void RevealLine(int line, int column)
        {
            if (line < 1) return;
            PostJson("{\"type\":\"revealLine\",\"line\":" + line + ",\"column\":" + Math.Max(1, column) + "}");
        }

        /// <summary>Insert text at the editor's cursor: {type:"insertText", text}. (Tab focus, if
        /// wanted after an insert, is a host UI concern — the control only posts.)</summary>
        public void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            PostJson("{\"type\":\"insertText\",\"text\":" + JsonString(text) + "}");
        }

        /// <summary>Move the Monaco caret to the editor position under a SCREEN point (used during a Data-pad field
        /// drag so the caret tracks the mouse and the drop lands at the pointer). We send the PHYSICAL offset from
        /// the webview's client origin; the page divides by devicePixelRatio for Monaco's CSS-px hit-test.</summary>
        public void MoveCaretToScreenPoint(int screenX, int screenY)
        {
            try
            {
                if (_webView == null || !_webView.IsHandleCreated) return;
                var origin = _webView.PointToScreen(System.Drawing.Point.Empty);
                PostJson("{\"type\":\"moveCaretToPoint\",\"x\":" + (screenX - origin.X) + ",\"y\":" + (screenY - origin.Y) + "}");
            }
            catch { }
        }

        /// <summary>Insert text at the editor position under a SCREEN point — the ATOMIC Data-pad field DROP. The
        /// page resolves the position from these coordinates at insert time, so the drop lands exactly where it was
        /// released (no race with the drag's caret-follow). Falls back to a plain caret insert if coords are
        /// unavailable.</summary>
        public void InsertTextAtScreenPoint(string text, int screenX, int screenY)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (_webView == null || !_webView.IsHandleCreated) { InsertText(text); return; }
                var origin = _webView.PointToScreen(System.Drawing.Point.Empty);
                PostJson("{\"type\":\"insertTextAtPoint\",\"text\":" + JsonString(text)
                    + ",\"x\":" + (screenX - origin.X) + ",\"y\":" + (screenY - origin.Y) + "}");
            }
            catch { InsertText(text); }
        }

        /// <summary>Give the editor OS keyboard focus (e.g. right after a Data-pad field DROP so the developer can
        /// type immediately). The page also calls ed.focus() for the Monaco-internal caret; this hands the WebView2
        /// control the Windows focus the pad held during the drag. Deferred a turn so it lands after the drop edit.</summary>
        public void FocusEditor()
        {
            try
            {
                if (_webView == null || !_webView.IsHandleCreated) return;
                _webView.BeginInvoke((Action)(() => { try { _webView.Focus(); } catch { } }));
            }
            catch { }
        }

        /// <summary>Reply to an LSP/request message: {type:"response", reqId, data}.</summary>
        public void PostResponse(int reqId, IDictionary<string, object> data)
        {
            string json;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                json = ser.Serialize(new Dictionary<string, object>
                {
                    { "type", "response" }, { "reqId", reqId }, { "data", data }
                });
            }
            catch { return; }
            PostJson(json);
        }

        /// <summary>Confirm a save: {type:"saveResult", ok, savedSeq, message}.</summary>
        public void PostSaveResult(bool ok, string message) { PostSaveResult(ok, message, 0); }

        /// <summary>Confirm a save with a sequence number. NOTE: the view's embed-mode double-post
        /// (to survive open/close churn during a slot save) is a host concern — a host that needs it
        /// calls this twice; the control posts once.</summary>
        public void PostSaveResult(bool ok, string message, long savedSeq)
        {
            string json = "{\"type\":\"saveResult\",\"ok\":" + (ok ? "true" : "false") +
                          ",\"savedSeq\":" + savedSeq +
                          ",\"message\":" + JsonString(message) + "}";
            PostJson(json);
        }

        /// <summary>Push a designer-session event to Monaco: {type, text?, message?} (e.g.
        /// designerSplice / designerClosed).</summary>
        public void PostDesignerMessage(string type, string text, string message)
        {
            string json;
            try
            {
                var d = new Dictionary<string, object> { { "type", type } };
                if (text != null) d["text"] = text;
                if (message != null) d["message"] = message;
                json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d);
            }
            catch { return; }
            PostJson(json);
        }

        /// <summary>Apply a dark/light theme: recolors the panel backdrop and posts
        /// {type:"applyTheme", isDark} once the surface is live.</summary>
        public void ApplyTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized)
                PostJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir, "Terminal", _htmlFileName);
        }

        /// <summary>Minimal JSON string escaper (copied from the view + the overlay — they share the
        /// same encoder). Wraps the result in double quotes.</summary>
        public static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Lifted (copied) from ModernEmbeditorViewContent — a minimal forward-only JSON value reader.
        private static string ExtractJsonValue(string json, string key)
        {
            if (json == null) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == 'n') return null;
            if (json[idx] == '"')
            {
                idx++;
                var sb = new StringBuilder();
                while (idx < json.Length)
                {
                    char c = json[idx];
                    if (c == '\\' && idx + 1 < json.Length)
                    {
                        char next = json[idx + 1];
                        if (next == '"') { sb.Append('"'); idx += 2; continue; }
                        if (next == '\\') { sb.Append('\\'); idx += 2; continue; }
                        if (next == 'n') { sb.Append('\n'); idx += 2; continue; }
                        if (next == 'r') { sb.Append('\r'); idx += 2; continue; }
                        if (next == 't') { sb.Append('\t'); idx += 2; continue; }
                        sb.Append(c); idx++; continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                    idx++;
                }
                return sb.ToString();
            }
            int start = idx;
            while (idx < json.Length && json[idx] != ',' && json[idx] != '}') idx++;
            return json.Substring(start, idx - start).Trim();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposedControl)
            {
                _disposedControl = true;
                try
                {
                    HandleCreated -= OnHandleCreated;
                    if (_webView != null)
                    {
                        if (_webView.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                        }
                        _webView.Dispose();
                        _webView = null;
                    }
                    if (_tempDir != null && Directory.Exists(_tempDir))
                    {
                        try { Directory.Delete(_tempDir, true); } catch { }
                    }
                }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}
