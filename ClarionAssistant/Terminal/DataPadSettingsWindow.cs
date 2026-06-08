using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Floating, movable settings window for the Modern Data pad. A real top-level tool window (so it can be
    /// dragged anywhere over the IDE — unlike an in-webview overlay, which is clipped to the pad's dock pane).
    /// It hosts a WebView2 that loads the SAME modern-data-pad.html with <c>?mode=settings</c>, reusing the exact
    /// card UI with no duplication. Edits are persisted to <see cref="ModernDataPadState"/> and pushed live to
    /// the docked pad via the <c>onStateChanged</c> callback.
    /// </summary>
    public class DataPadSettingsWindow : Form
    {
        private WebView2 _web;
        private string _pageUri;          // file:/// URI of our settings page, used to build the navigate target
        private string _expectedLocalPath; // the page's local file path — the ONLY trusted nav target / message source (exact, query-independent)
        private readonly Action<string> _onStateChanged;

        public DataPadSettingsWindow(Action<string> onStateChanged)
        {
            _onStateChanged = onStateChanged;

            Text = "Data Pad Settings";
            FormBorderStyle = FormBorderStyle.SizableToolWindow; // native caption gives title + move + close + resize
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;                                       // float above the IDE so it's never hidden
            ClientSize = new Size(380, 540);
            MinimumSize = new Size(320, 320);
            BackColor = Color.White;

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            Load += OnLoadAsync;
        }

        private async void OnLoadAsync(object sender, EventArgs e)
        {
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _web.EnsureCoreWebView2Async(env);          // Show() (non-modal): WebView2 can't init under ShowDialog

                var s = _web.CoreWebView2.Settings;
                s.IsScriptEnabled = true;
                s.AreDefaultContextMenusEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;

                _web.CoreWebView2.WebMessageReceived += OnWebMessage;
                // Trust boundary: only ever allow our own local settings page. Block any attempt to navigate
                // the host webview elsewhere (defence-in-depth — the document is a local file we control, but
                // pinning the surface means a stray/injected navigation can't gain host-message capabilities).
                _web.CoreWebView2.NavigationStarting += (s2, a2) =>
                {
                    if (a2 == null) return;
                    if (!IsTrustedUri(a2.Uri)) a2.Cancel = true;
                };

                string html = GetHtmlPath();
                if (File.Exists(html))
                {
                    var pageUri = new Uri(html);
                    _pageUri = pageUri.AbsoluteUri;
                    _expectedLocalPath = pageUri.LocalPath;   // exact file path for the trust check (query-independent)
                    _web.CoreWebView2.Navigate(_pageUri + "?mode=settings&v=" + File.GetLastWriteTimeUtc(html).Ticks);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[DataPadSettingsWindow] init: " + ex.Message); }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // Trust boundary: ignore any message whose source isn't EXACTLY our own local settings file.
                if (!IsTrustedUri(e.Source)) return;

                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                if (action == "ready")
                {
                    ReseedState();   // seed the card UI with the current saved layout
                }
                else if (action == "saveUiState")
                {
                    string state = ExtractJsonValue(json, "state");
                    // Enforce the persistence size cap on this live path too (symmetric with Save), so a
                    // crafted/oversize payload can't be forwarded into the pad webview and frozen there.
                    if (string.IsNullOrEmpty(state)
                        || System.Text.Encoding.UTF8.GetByteCount(state) > ModernDataPadState.MaxBytes)
                        return;
                    // Do NOT persist from here — the pad is the single writer. We forward the change so the pad
                    // applies ONLY the layout fields and persists its own (current) full state. That avoids the
                    // last-writer-wins clobber where this window's stale open-time snapshot of theme / zoom /
                    // tree-collapse / detail state would overwrite live pad state.
                    if (_onStateChanged != null) _onStateChanged(state);
                }
                else if (action == "closeSettings")
                {
                    if (IsHandleCreated) BeginInvoke((Action)Close);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[DataPadSettingsWindow] msg: " + ex.Message); }
        }

        /// <summary>
        /// (Re)seed the card UI from the current persisted layout. Called on the page's initial 'ready' and
        /// again when the window is re-focused — so reopening reflects the pad's latest state rather than the
        /// snapshot from when the window was first opened.
        /// </summary>
        public void ReseedState()
        {
            string state = ModernDataPadState.Load();
            Post(new Dictionary<string, object>
            {
                { "type", "restoreUiState" }, { "state", string.IsNullOrEmpty(state) ? "{}" : state }
            });
        }

        // Exact resource pinning: trust ONLY our own local settings file, independent of the query string. A
        // prefix/substring test would admit sibling files such as '…modern-data-pad.html.evil' or remote
        // lookalikes; compare the parsed file path for exact equality instead.
        private bool IsTrustedUri(string uriString)
        {
            if (string.IsNullOrEmpty(uriString) || _expectedLocalPath == null) return false;
            Uri u;
            if (!Uri.TryCreate(uriString, UriKind.Absolute, out u)) return false;
            if (!u.IsFile) return false;
            // .NET Framework does NOT split the query off a file:// URI: Uri.Query is empty and the
            // "?mode=settings&v=..." stays glued onto LocalPath (e.g. "…\modern-data-pad.html?mode=settings&v=1").
            // A raw LocalPath compare against the bare page path therefore ALWAYS failed once we navigated with a
            // query — cancelling the navigation AND dropping every inbound web message, leaving the settings
            // window blank. Strip at the first '?' ('?' is illegal in Windows file paths, so this is unambiguous)
            // before the exact-path compare; the pinning is just as strict, only query-tolerant.
            string localPath = u.LocalPath;
            int q = localPath.IndexOf('?');
            if (q >= 0) localPath = localPath.Substring(0, q);
            return string.Equals(localPath, _expectedLocalPath, StringComparison.OrdinalIgnoreCase);
        }

        private void Post(Dictionary<string, object> data)
        {
            try
            {
                if (_web == null || _web.CoreWebView2 == null) return;
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                _web.CoreWebView2.PostWebMessageAsJson(ser.Serialize(data));
            }
            catch { }
        }

        private static string GetHtmlPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(dir, "Terminal", "modern-data-pad.html");
            return File.Exists(path) ? path : Path.Combine(dir, "modern-data-pad.html");
        }

        // Minimal string-value JSON extractor — same contract as ModernDataPad.ExtractJsonValue (no JSON lib
        // dependency for parsing inbound messages; we only need the "action" and "state" string fields).
        private static string ExtractJsonValue(string json, string key)
        {
            if (json == null) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            var sb = new System.Text.StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    char n = json[idx + 1];
                    if (n == '"') { sb.Append('"'); idx += 2; continue; }
                    if (n == '\\') { sb.Append('\\'); idx += 2; continue; }
                    if (n == 'n') { sb.Append('\n'); idx += 2; continue; }
                    if (n == 't') { sb.Append('\t'); idx += 2; continue; }
                    sb.Append(c); idx++; continue;
                }
                if (c == '"') break;
                sb.Append(c); idx++;
            }
            return sb.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _web != null) { _web.Dispose(); _web = null; }
            base.Dispose(disposing);
        }
    }
}
