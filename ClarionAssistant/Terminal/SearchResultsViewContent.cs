using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// "Open search results in editor" — a read-only TAB listing every match of one Find-All, with context
    /// lines around each hit and click-through back to the source (VS Code's search editor).
    ///
    /// A TAB, not a pad, on purpose: the IDE's docking layer (WeifenLuo DockPanel Suite v1, vendored into
    /// this SharpDevelop fork) misplaces its dock guides whenever a monitor sits above or left of the
    /// primary, so anything pad-shaped is unusable on those setups. An editor tab never touches that code.
    ///
    /// This view holds NO position state. Rows carry the engine's array index and nothing else; a click
    /// forwards {op:'reveal', idx} to the ORIGIN editor page, which resolves the index against its live
    /// Monaco decorations (syncFindMatches, #65). That is exactly how the CA Find pad works, and it is why
    /// buffer edits can't make this tab point at the wrong line: the numbers shown may age, but navigation
    /// always goes through the engine's tracked decorations.
    ///
    /// Lifecycle mirrors MonacoDiffViewContent: shared WebView2 env cache, per-instance virtual-host temp
    /// folder for the payload (it can be MBs), ready-handshake send, UI-thread shutdown disposal.
    /// </summary>
    public class SearchResultsViewContent : AbstractViewContent
    {
        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        private string _originKey;     // CaFindBroker session key of the editor this search ran against
        private string _originTitle;
        private string _originKind;    // "CA Editor" | "CA Embeditor"
        private string _payloadJson;   // the page's caFindOpenDoc message, verbatim
        private string _query;
        private bool _isDark = true;

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-search-results-data";

        private static readonly List<SearchResultsViewContent> _instances = new List<SearchResultsViewContent>();

        public override Control Control { get { return _panel; } }

        /// <summary>
        /// Open (or refresh) the results tab for a search. Reuses the tab already showing results for the
        /// same origin editor rather than stacking a new one per Find-All — re-running a search in the same
        /// buffer should update the view you're looking at. Deferred onto a clean turn: constructing a
        /// WebView2 on a reentrant/DoEvents-pumped stack hard-hangs the IDE (see ModernEmbeditorLauncher).
        /// </summary>
        public static void ShowFor(string originKey, string originTitle, string originKind, string payloadJson)
        {
            try
            {
                var ctx = System.Threading.SynchronizationContext.Current;
                Action open = () =>
                {
                    try
                    {
                        var existing = FindByOrigin(originKey);
                        if (existing != null) { existing.SetResults(payloadJson); existing.BringToFront(); return; }

                        var view = new SearchResultsViewContent(originKey, originTitle, originKind, payloadJson);
                        WorkbenchSingleton.Workbench.ShowView(view);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SearchResults] ShowFor(open): " + ex.Message); }
                };
                if (ctx != null) ctx.Post(_ => open(), null); else open();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SearchResults] ShowFor: " + ex.Message); }
        }

        private static SearchResultsViewContent FindByOrigin(string originKey)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    if (string.Equals(inst._originKey, originKey, StringComparison.OrdinalIgnoreCase)) return inst;
            }
            return null;
        }

        private SearchResultsViewContent(string originKey, string originTitle, string originKind, string payloadJson)
        {
            _originKey = originKey ?? "";
            _originTitle = originTitle ?? "";
            _originKind = originKind ?? "";
            _payloadJson = payloadJson ?? "{}";
            _query = ExtractJsonValue(_payloadJson, "query") ?? "";
            // Default dark like every other WebView2 view here; there is no static "is dark" query in this
            // codebase — theme arrives by broadcast (AssistantChatControl -> ApplyThemeToAll) and corrects us.
            TitleName = BuildTitle();
            // No Save()/SaveAs() here — without this, SharpDevelop treats it as a normal saveable
            // filename-less document (enables Save, then throws when invoked). Same as MonacoDiffViewContent.
            IsViewOnly = true;

            _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 46) };
            _webView = new WebView2 { Dock = DockStyle.Fill };
            _panel.Controls.Add(_webView);

            lock (_instances) { _instances.Add(this); }
            _panel.HandleCreated += OnHandleCreated;
        }

        private string BuildTitle()
        {
            string q = _query;
            if (string.IsNullOrEmpty(q)) q = "(no query)";
            if (q.Length > 24) q = q.Substring(0, 24) + "…";
            return "Search: " + q;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionSearchResults_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VIRTUAL_HOST, _tempDir, CoreWebView2HostResourceAccessKind.Allow);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                {
                    // Same ?v= mtime cache-bust + AbsoluteUri caveat as MonacoDiffViewContent: safe because
                    // this page resolves no relative resources (payload arrives over the virtual host).
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
                }
            }
            catch (Exception ex)
            {
                _isInitializing = false; // allow retry
                System.Diagnostics.Debug.WriteLine("[SearchResults] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // SendResults is driven by the page's "ready" message, not here (avoids the double-send).
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                if (json == null || json.Length > 1024 * 1024) return;   // cap inbound, same posture as CaFindPad
                string action = ExtractJsonValue(json, "action");

                if (action == "ready") { SendResults(); return; }

                if (action == "reveal") { HandleReveal(json); return; }

                if (action == "clipboard")
                {
                    // file:// pages can't reach the clipboard themselves — same host-side hop the other views use.
                    string text = ExtractJsonValue(json, "text") ?? "";
                    try { if (text.Length > 0) Clipboard.SetText(text); } catch { }
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SearchResults] Message error: " + ex.Message);
            }
        }

        // The origin is a PWEE embed session when its broker key is "embed::<proc>"; anything else is a file
        // path. Note this is NOT the same test as originKind — a ModernEmbeditorViewContent in FILE mode
        // registers as kind "CA Editor" with a path key, so the KEY is the honest discriminator.
        private bool IsEmbedOrigin
        {
            get { return _originKey != null && _originKey.StartsWith("embed::", StringComparison.OrdinalIgnoreCase); }
        }

        private string OriginProcName
        {
            get { return IsEmbedOrigin ? _originKey.Substring("embed::".Length) : null; }
        }

        /// <summary>
        /// A row was clicked. Three outcomes, in order of fidelity:
        ///
        /// 1. Origin editor is open  -> the engine reveals from its LIVE decorations (correct even after the
        ///    buffer has been edited), and we raise its tab, because the user is looking at THIS one.
        /// 2. Origin was a PWEE embeditor and it's closed -> nothing to reveal into. PWEE sessions are driven
        ///    through the app tree and a re-open regenerates the view, so we don't try to be clever: say so.
        /// 3. Origin was a source file and its tab is closed -> recoverable. SharpDevelop's OpenFile focuses an
        ///    open tab or opens a closed one, and we navigate by the line/col recorded when the search ran.
        ///    That's a snapshot rather than a tracked decoration — right unless the file changed on disk since.
        /// </summary>
        private void HandleReveal(string json)
        {
            // The page builds the complete caFind message (idx + the snapshot coords + the search identity
            // that produced this document) and we forward it verbatim — the pad's 'fwd' pattern. The engine
            // decides whether idx is still meaningful; only IT knows what search it is currently holding.
            string fwd = ExtractJsonValue(json, "fwd");
            if (string.IsNullOrEmpty(fwd)) return;

            // Routed by ORIGIN KEY, never to the active editor: this tab belongs to the buffer it was opened
            // from, so a click must not follow whatever the user happened to focus since.
            bool live = ClarionAssistant.Services.CaFindBroker.ForwardTo(_originKey, fwd);
            if (live) { FocusOrigin(); return; }

            if (IsEmbedOrigin)
            {
                PostToPage("{\"type\":\"originGone\",\"kind\":\"embed\",\"name\":"
                           + MonacoEditorControl.JsonString(OriginProcName ?? "") + "}");
                return;
            }

            int line, col;
            int.TryParse(ExtractJsonValue(json, "line"), out line);
            if (!int.TryParse(ExtractJsonValue(json, "col"), out col) || col < 1) col = 1;
            if (line > 0 && ClarionAssistant.Services.MonacoSourceNavigator.NavigateToFileAndLine(_originKey, line, col))
                return;

            PostToPage("{\"type\":\"originGone\",\"kind\":\"file\",\"name\":"
                       + MonacoEditorControl.JsonString(_originKey ?? "") + "}");
        }

        /// <summary>Raise the origin editor's tab. Clicking a result should take you to the code — leaving the
        /// user on the results tab while the reveal happens invisibly behind it reads as a dead click.</summary>
        private void FocusOrigin()
        {
            try
            {
                // TryFocusOwningTab, NOT TryFocusExisting: in overlay mode the CA Embeditor is a Monaco panel
                // docked over the NATIVE embeditor rather than a tab of its own, so the tab that needs raising
                // is the native gen editor's. TryFocusExisting would only re-assert z-order inside a host that
                // isn't the foreground document — reveal lands correctly but the user never sees it.
                if (IsEmbedOrigin) { ModernEmbeditorViewContent.TryFocusOwningTab(OriginProcName); return; }
                // File-backed: OpenFile on an already-open file focuses its tab (SharpDevelop semantics).
                // The engine's reveal is already in flight; this only brings the tab forward.
                ICSharpCode.SharpDevelop.FileService.OpenFile(_originKey);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SearchResults] FocusOrigin: " + ex.Message); }
        }

        /// <summary>Replace this tab's content with a fresh search (same origin editor).</summary>
        public void SetResults(string payloadJson)
        {
            _payloadJson = payloadJson ?? "{}";
            _query = ExtractJsonValue(_payloadJson, "query") ?? "";
            TitleName = BuildTitle();
            if (_isInitialized) SendResults();
        }

        private void SendResults()
        {
            if (_webView == null || _webView.CoreWebView2 == null) return;
            try
            {
                // The payload carries every match plus its context lines — easily MBs on a broad search, so
                // it goes over the virtual host as a file rather than through PostWebMessageAsJson.
                string file = Path.Combine(_tempDir, "results.json");
                File.WriteAllText(file, _payloadJson ?? "{}", new UTF8Encoding(false));

                string json = "{\"type\":\"setResults\"," +
                    "\"resultsUrl\":\"https://" + VIRTUAL_HOST + "/results.json?v=" + DateTime.UtcNow.Ticks + "\"," +
                    "\"originTitle\":" + MonacoEditorControl.JsonString(_originTitle) + "," +
                    "\"originKind\":" + MonacoEditorControl.JsonString(_originKind) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SearchResults] SendResults error: " + ex.Message);
            }
        }

        private void PostToPage(string json)
        {
            try { if (_webView != null && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(json); }
            catch { }
        }

        /// <summary>Raise this tab. Deferred: synchronously re-activating a WebView2 tab on a reentrant
        /// stack risks a focus deadlock (same reasoning as ModernEmbeditorViewContent.BringToFront).</summary>
        public void BringToFront()
        {
            try
            {
                var ctx = System.Threading.SynchronizationContext.Current;
                Action raise = () =>
                {
                    try
                    {
                        var ww = this.WorkbenchWindow;
                        if (ww == null) return;
                        var m = ww.GetType().GetMethod("SelectWindow");
                        if (m != null) m.Invoke(ww, null);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SearchResults] BringToFront(raise): " + ex.Message); }
                };
                if (ctx != null) ctx.Post(_ => raise(), null); else raise();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SearchResults] BringToFront: " + ex.Message); }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "search-results.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "search-results.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "search-results.html");
        }

        /// <summary>Apply light or dark theme to this results tab.</summary>
        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (_panel != null)
                _panel.BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized) PostToPage("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        /// <summary>Apply theme to every open results tab.</summary>
        public static void ApplyThemeToAll(bool isDark)
        {
            List<SearchResultsViewContent> snapshot;
            lock (_instances) { snapshot = new List<SearchResultsViewContent>(_instances); }
            foreach (var inst in snapshot) { try { inst.ApplyTheme(isDark); } catch { } }
        }

        public override void Dispose()
        {
            lock (_instances) { _instances.Remove(this); }
            if (_webView != null) { _webView.Dispose(); _webView = null; }
            if (_panel != null) { _panel.Dispose(); _panel = null; }
            CleanupTempDir();
            base.Dispose();
        }

        /// <summary>Shutdown hook: dispose every open results tab's WebView2 on the UI thread before native
        /// IDE teardown, to avoid the WebView2 &lt;-&gt; native focus deadlock. Idempotent + best-effort.</summary>
        public static void DisposeAllForShutdown()
        {
            List<SearchResultsViewContent> snapshot;
            lock (_instances) { snapshot = new List<SearchResultsViewContent>(_instances); }
            foreach (var inst in snapshot) { try { inst.Dispose(); } catch { } }
        }

        private void CleanupTempDir()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == 'n') return null;
            if (json[idx] == '{')
            {
                // Balanced-brace object scan (string-aware), same as CaFindPad's: lets the PAGE build the
                // complete editor-bound message and us forward it verbatim, instead of rebuilding nested
                // opts JSON by hand out here.
                int depth = 0; bool inStr = false;
                for (int i = idx; i < json.Length; i++)
                {
                    char c = json[i];
                    if (inStr)
                    {
                        if (c == '\\') { i++; continue; }
                        if (c == '"') inStr = false;
                        continue;
                    }
                    if (c == '"') { inStr = true; continue; }
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) return json.Substring(idx, i - idx + 1);
                    }
                }
                return null;
            }
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
    }
}
