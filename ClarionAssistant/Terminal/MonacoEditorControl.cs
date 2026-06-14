using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    // ── Monaco-default-editor converge (task cc8b092f), STEP 1 ──────────────────────────────────
    // Goal (John/Mark): ONE Monaco protocol host shared by BOTH the standalone file-mode embeditor
    // view (ModernEmbeditorViewContent) AND the dual-control source overlay (MonacoClarionEditor),
    // instead of three separate WebView2/Monaco paths. This file is the reusable surface those two
    // hosts will share.
    //
    // STEP 1 is deliberately ZERO-IMPACT: this is a NEW control that nothing references yet, so
    // ModernEmbeditorViewContent is byte-for-byte unchanged and the shipping embeditor cannot be
    // affected. The WebView2 init below is lifted (copied, not cut) from
    // ModernEmbeditorViewContent.OnHandleCreated so STEP 3's rewire is a faithful swap.
    //
    // STEP 2 ports the full outbound senders (SendSource/ApplyTheme/...) and the inbound action
    // dispatch into this control, routing the IDE-coupled leaf work through IMonacoEditorHost. For
    // now the raw protocol flows through OnEditorReady / OnEditorAction so the skeleton compiles and
    // can be smoke-tested on its own.

    /// <summary>
    /// Callbacks the reusable <see cref="MonacoEditorControl"/> needs from whatever is embedding it
    /// (the standalone ModernEmbeditor view, or the dual-control source overlay). All IDE-specific
    /// work — save round-trips, the structure designer, LSP completion/hover/diagnostics, settings /
    /// history / cursor / bookmark persistence, Data-pad refresh — stays in the host. The control
    /// owns ONLY the WebView2 + Monaco page + the JS&lt;-&gt;C# message transport.
    /// </summary>
    public interface IMonacoEditorHost
    {
        /// <summary>The page finished loading and sent {action:"ready"}. The host should now push
        /// source (the SendSource payload) plus any initial settings / history / bookmarks.</summary>
        void OnEditorReady(MonacoEditorControl editor);

        /// <summary>An inbound page-&gt;host message other than "ready". <paramref name="rawJson"/> is
        /// the full message; <paramref name="action"/> is the pre-parsed {action:...} value. STEP 2
        /// replaces this catch-all with the typed dispatch ported from
        /// ModernEmbeditorViewContent.OnWebMessageReceived.</summary>
        void OnEditorAction(MonacoEditorControl editor, string action, string rawJson);

        /// <summary>Navigation to the Monaco page completed. <paramref name="success"/> mirrors
        /// CoreWebView2NavigationCompletedEventArgs.IsSuccess. Optional hook for hosts that need to
        /// know the surface is live.</summary>
        void OnEditorNavigationCompleted(MonacoEditorControl editor, bool success);
    }

    /// <summary>
    /// Reusable Monaco-over-WebView2 surface. Owns the <see cref="Panel"/> + <see cref="WebView2"/>,
    /// CoreWebView2 initialization, a per-instance virtual-host temp folder (for large-buffer
    /// transfer via source.txt), navigation to the Monaco HTML (default monaco-embeditor.html) with
    /// the ?v= cache-bust, and the JS&lt;-&gt;C# WebMessage transport. Everything IDE-specific is
    /// delegated through <see cref="IMonacoEditorHost"/>.
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
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            // Init when the panel handle is realized (the WebView2 can only EnsureCoreWebView2 then).
            HandleCreated += OnHandleCreated;
        }

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
                settings.AreDevToolsEnabled = true;
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
            // Source push is triggered by the JS "ready" message (OnEditorReady), not here — avoids a double-send.
            try { if (_host != null) _host.OnEditorNavigationCompleted(this, e.IsSuccess); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] NavCompleted host error: " + ex.Message); }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                if (action == "ready")
                {
                    if (_host != null) _host.OnEditorReady(this);
                }
                else if (_host != null)
                {
                    // STEP 2 will replace this catch-all with the typed dispatch ported from the view.
                    _host.OnEditorAction(this, action, json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] Message error: " + ex.Message);
            }
        }

        /// <summary>Send a JSON message host-&gt;page (PostWebMessageAsJson). Note: WebView2 delivers
        /// it to JS as a parsed OBJECT, not a string — page handlers must not JSON.parse it.</summary>
        public void PostJson(string json)
        {
            try
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostJson error: " + ex.Message); }
        }

        /// <summary>Send a raw string message host-&gt;page (PostWebMessageAsString).</summary>
        public void PostString(string message)
        {
            try
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                    _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostString error: " + ex.Message); }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir, "Terminal", _htmlFileName);
        }

        // Lifted (copied) from ModernEmbeditorViewContent — a minimal forward-only JSON value reader.
        // STEP 2 consolidates so there is a single copy.
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
