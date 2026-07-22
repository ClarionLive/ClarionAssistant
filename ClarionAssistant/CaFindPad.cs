using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// CA Find/Replace as a dockable SharpDevelop pad (GitHub #66, ticket 91e6ecac). Hosts the find
    /// UI (Terminal\ca-find.html — tabs, history, options, all-matches results grid) in a WebView2;
    /// the match/replace engine stays in each Monaco editor page and is driven remotely through
    /// <see cref="CaFindBroker"/>. Same hosting pattern as <see cref="ModernDataPad"/> (CA Explorer):
    /// instance registry for ordered WebView2 shutdown, trust-gated messages, opaque JSON state blob.
    /// </summary>
    public class CaFindPad : AbstractPadContent
    {
        // Live-instance registry so ShutdownService can dispose this pad's WebView2 ON THE UI THREAD
        // BEFORE native IDE teardown (the WebView2 <-> native focus-deadlock precedent; see ModernDataPad).
        private static readonly List<CaFindPad> _instances = new List<CaFindPad>();

        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _disposed;

        public CaFindPad()
        {
            lock (_instances) { _instances.Add(this); }
        }

        public override Control Control
        {
            get
            {
                if (_panel == null)
                {
                    _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
                    _webView = new WebView2 { Dock = DockStyle.Fill };
                    _panel.Controls.Add(_webView);
                    _panel.HandleCreated += OnHandleCreated;
                }
                return _panel;
            }
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;
            try
            {
                var environment = await Terminal.WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Ctrl+mousewheel font zoom (WebView2's built-in ZoomFactor), persisted like the Explorer pad.
                _webView.ZoomFactor = Terminal.WebViewZoomHelper.GetZoom("caFindPad");
                _webView.ZoomFactorChanged += (s2, e2) => Terminal.WebViewZoomHelper.SetZoom("caFindPad", _webView.ZoomFactor);

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
                else
                    System.Diagnostics.Debug.WriteLine("[CaFindPad] HTML missing: " + htmlPath);
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                System.Diagnostics.Debug.WriteLine("[CaFindPad] Init error: " + ex.Message);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // Trust boundary: only honor messages from our OWN bundled page (same gate as ModernDataPad).
                if (!IsTrustedSource(e.Source)) return;

                string json = e.TryGetWebMessageAsString();
                if (json != null && json.Length > 1024 * 1024) return;   // defense-in-depth size cap
                string action = ExtractJsonValue(json, "action");

                if (action == "ready")
                {
                    _isInitialized = true;
                    // Saved state FIRST (sessions/history/options/theme), THEN attach to the broker —
                    // SetPadPoster immediately replays the current active editor, and the page needs its
                    // sessions restored before it can pick the right one.
                    string state = Terminal.CaFindPadState.Load();
                    if (!string.IsNullOrEmpty(state))
                        PostJson("{\"type\":\"applyState\",\"state\":" + state + "}");
                    // Shared history AFTER the state blob: the solution-wide find/replace lists live in
                    // ModernEmbeditorHistory (one store for pad + overlay + both editors, #66 phase 2),
                    // so they override the blob's legacy copies when present.
                    PushSharedHistory();
                    CaFindBroker.SetPadPoster(PostJson);
                    // Same WebView2-in-WinForms focus race as #66 round-2 (see
                    // MonacoEditorControl.FocusEditor/FocusAttempt, which this mirrors): on the VERY
                    // FIRST Ctrl+F this pad didn't exist yet, so CaFindBroker.ShowPad's
                    // BringPadToFront() ran before CoreWebView2/this page finished loading, and a
                    // lone Focus() isn't enough to pull real Win32 keyboard focus into the browser
                    // content afterward. See CaFindBroker.SuppressEditorFocusSteal for the other
                    // half of this fix — MonacoClarionSourceEditor's WindowSelected hook was
                    // actively fighting this same claim during first-time pad layout.
                    FocusAttempt(0);
                }
                else if (action == "saveHistory")
                {
                    // The pad mutated the shared find/replace history (new term, delete, clear): persist to
                    // the SAME per-solution store the editors use, then converge every surface via the bus.
                    HandleSaveHistory(json);
                }
                else if (action == "caFindFwd")
                {
                    // The page built the complete editor-bound message; route it to the active host verbatim.
                    string fwd = ExtractJsonValue(json, "fwd");
                    if (!string.IsNullOrEmpty(fwd)) CaFindBroker.FromPad(fwd);
                }
                else if (action == "saveState")
                {
                    string state = ExtractJsonValue(json, "state");
                    Terminal.CaFindPadState.Save(state);
                }
                else if (action == "clipboard")
                {
                    // navigator.clipboard.writeText silently fails on file:// under WebView2 — route through
                    // the host on the UI/STA thread (gotcha_webview2_fileurl_clipboard_writetext_fails).
                    string text = ExtractJsonValue(json, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        try { Clipboard.SetText(text); }
                        catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[CaFindPad] clipboard: " + cex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CaFindPad] Message error: " + ex.Message);
            }
        }

        /// <summary>
        /// Load the solution-wide find/replace lists from the shared per-solution store
        /// (<see cref="ModernEmbeditorHistory"/> — the same file the CA editors persist to) and push
        /// them to the pad page as {type:'applyHistory'}. Sent on ready, after the state blob, so the
        /// shared lists override the blob's legacy copies (#66 phase 2 unified history).
        /// </summary>
        private void PushSharedHistory()
        {
            try
            {
                string sol = null;
                try { sol = EditorService.GetOpenSolutionPath(); } catch { }
                List<string> find, replace, proc;
                ModernEmbeditorHistory.Load(sol, null, out find, out replace, out proc);
                if (find.Count == 0 && replace.Count == 0) return;   // nothing saved yet — keep the blob's lists
                PostJson("{\"type\":\"applyHistory\",\"find\":" + ModernEmbeditorHistory.ToJson(find)
                       + ",\"replace\":" + ModernEmbeditorHistory.ToJson(replace) + "}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindPad] PushSharedHistory: " + ex.Message); }
        }

        /// <summary>Persist a pad-side history mutation to the shared store and converge all surfaces.</summary>
        private void HandleSaveHistory(string json)
        {
            try
            {
                var data = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return;
                var find = JsonStringList(data, "find");
                var replace = JsonStringList(data, "replace");
                string sol = null;
                try { sol = EditorService.GetOpenSolutionPath(); } catch { }
                List<string> savedFind, savedReplace;
                // No procKey: the pad's per-procedure recents stay in its own state blob; only the
                // solution-wide lists are shared.
                ModernEmbeditorHistory.Save(sol, null, find, replace, null, out savedFind, out savedReplace);
                CaFindBroker.BroadcastHistory(savedFind, savedReplace);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindPad] HandleSaveHistory: " + ex.Message); }
        }

        private static List<string> JsonStringList(Dictionary<string, object> data, string key)
        {
            var outp = new List<string>();
            object o;
            if (data != null && data.TryGetValue(key, out o) && o is object[])
                foreach (var item in (object[])o) if (item != null) outp.Add(item.ToString());
            return outp;
        }

        // #66 round-2 (same bug as MonacoEditorControl.FocusAttempt): a single _webView.Focus() is
        // not enough right after this page finishes loading. The WinForms WebView2 only forwards
        // focus into Chromium from its GotFocus handler, so when the host HWND already holds Win32
        // focus, the render widget never gets the keyboard. MoveFocus() is the authoritative
        // hand-off. Root cause of the original flakiness was MonacoClarionSourceEditor's
        // WindowSelected hook fighting this same claim during first-time pad layout (now dampened
        // via CaFindBroker.SuppressEditorFocusSteal) — with that fixed, a normal stop-on-success
        // retry (matching MonacoEditorControl's own cadence) is enough; no need to keep re-asserting
        // after we've already won, which was only ever needed to survive that fight and produced a
        // visible focus-flicker of its own once the fight was gone.
        private void FocusAttempt(int attempt)
        {
            try
            {
                if (_webView == null || _webView.IsDisposed || !_webView.IsHandleCreated) return;
                if (_webView.Visible)
                {
                    _webView.Focus();
                    TryMoveFocusIntoChromium();
                }
                if (attempt < 3 && !_webView.ContainsFocus)
                {
                    var t = new Timer { Interval = 80 };
                    t.Tick += (s, e) =>
                    {
                        try { t.Stop(); t.Dispose(); } catch { }
                        FocusAttempt(attempt + 1);
                    };
                    t.Start();
                }
                else if (!_webView.ContainsFocus)
                {
                    MonacoSpikeLog.Write("[CaFindPad] FocusAttempt: focus never landed after retries (focused control elsewhere)");
                }
            }
            catch { }
        }

        // The WinForms wrapper doesn't expose CoreWebView2Controller publicly; reflect it and call
        // MoveFocus(Programmatic) — the only API that reliably puts the keyboard in the render widget.
        private void TryMoveFocusIntoChromium()
        {
            try
            {
                object ctl =
                    _webView.GetType().GetProperty("CoreWebView2Controller",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_webView, null)
                    ?? _webView.GetType().GetField("_coreWebView2Controller",
                        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_webView);
                var controller = ctl as CoreWebView2Controller;
                if (controller != null) controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            catch { }
        }

        /// <summary>Post a JSON message into the pad page, marshalled to the UI thread. This is the
        /// delegate handed to <see cref="CaFindBroker.SetPadPoster"/>.</summary>
        private void PostJson(string json)
        {
            Action post = () =>
            {
                try { if (_webView != null && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(json); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindPad] PostJson: " + ex.Message); }
            };
            try
            {
                if (_panel != null && _panel.IsHandleCreated && _panel.InvokeRequired) _panel.BeginInvoke(post);
                else post();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CaFindPad] PostJson marshal: " + ex.Message); }
        }

        public override void Dispose()
        {
            if (_disposed) { base.Dispose(); return; }
            _disposed = true;
            lock (_instances) { _instances.Remove(this); }
            try
            {
                CaFindBroker.SetPadPoster(null);
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.Dispose();
                    _webView = null;
                }
                if (_panel != null)
                {
                    _panel.HandleCreated -= OnHandleCreated;
                    _panel.Dispose();
                    _panel = null;
                }
            }
            catch { }
            base.Dispose();
        }

        /// <summary>Ordered WebView2 teardown before native IDE shutdown (called by ShutdownService).</summary>
        public static void DisposeAllForShutdown()
        {
            List<CaFindPad> snapshot;
            lock (_instances) { snapshot = new List<CaFindPad>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
        }

        private static string GetHtmlPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(dir, "Terminal", "ca-find.html");
            return File.Exists(path) ? path : Path.Combine(dir, "ca-find.html");
        }

        // True only when a WebView2 message came from our OWN bundled page (ignoring the ?v= cache-buster).
        private static bool IsTrustedSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            try
            {
                string expected = new Uri(GetHtmlPath()).AbsoluteUri;
                int q = source.IndexOf('?');
                string src = q >= 0 ? source.Substring(0, q) : source;
                return string.Equals(src, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Minimal string-value extractor for inbound messages. "fwd" and "state" arrive as NESTED JSON
        // OBJECTS (not strings), so those are extracted as balanced-brace substrings; plain string values
        // fall back to the simple scanner.
        private static string ExtractJsonValue(string json, string key)
        {
            if (json == null) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t')) idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == 'n') return null;   // null
            if (json[idx] == '{')
            {
                // Balanced-brace object scan (string-aware) — returns the raw object text.
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
                var sb = new System.Text.StringBuilder();
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

    /// <summary>Shows the CA Find/Replace pad (Tools menu).</summary>
    public class ShowCaFindPadCommand : ICSharpCode.Core.AbstractMenuCommand
    {
        public override void Run()
        {
            CaFindBroker.ShowPad();
        }
    }
}
