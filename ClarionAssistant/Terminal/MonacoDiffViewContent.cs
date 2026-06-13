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
    /// ViewContent that hosts a Monaco-based side-by-side / inline diff editor (monaco.editor.createDiffEditor)
    /// with Clarion syntax highlighting in both panes. Unlike <see cref="DiffViewContent"/> (which generates a
    /// unified diff via git/LCS and renders it as HTML), this view hands Monaco the two RAW texts and lets it
    /// compute the diff — so ignore-whitespace, side-by-side/inline, and navigation are native Monaco features.
    ///
    /// Mirrors DiffViewContent's WebView2-as-view lifecycle: shared env cache, virtual-host folder mapping for
    /// large text transfer, JS↔C# bridge, UI-thread shutdown disposal, and the Ctrl+MouseWheel font-zoom bridge.
    ///
    /// NOTE: the inline code-review NOTES workflow (BLOCKER/SUGGESTION/…) is intentionally NOT implemented here —
    /// it stays on the classic <see cref="DiffViewContent"/>. Porting notes onto Monaco view-zones is a separate
    /// follow-up ticket. This view supports the Approve/Cancel half of the show_diff result contract.
    /// </summary>
    public class MonacoDiffViewContent : AbstractViewContent
    {
        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        private string _title;
        private string _originalText;
        private string _modifiedText;
        private string _language;
        private bool _ignoreWhitespace;
        private bool _isDark = true;

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-monaco-diff-data";

        private static readonly List<MonacoDiffViewContent> _instances = new List<MonacoDiffViewContent>();

        /// <summary>Fires when the user clicks Approve (modified text is passed through).</summary>
        public event Action<string> Applied;

        /// <summary>Fires when the user clicks Cancel.</summary>
        public event Action Cancelled;

        public override Control Control { get { return _panel; } }

        public MonacoDiffViewContent(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = true, bool isDark = true)
        {
            _title = title ?? "Diff";
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            _language = language ?? "clarion";
            _ignoreWhitespace = ignoreWhitespace;
            _isDark = isDark;
            TitleName = "Diff: " + _title;

            _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 46) };
            _webView = new ZoomableWebView2 { Dock = DockStyle.Fill };
            _panel.Controls.Add(_webView);

            lock (_instances) { _instances.Add(this); }
            _panel.HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionMonacoDiff_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VIRTUAL_HOST, _tempDir,
                    CoreWebView2HostResourceAccessKind.Allow);

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
                    // Cache-bust on file mtime. NOTE: .NET Framework appends ?query onto file:// LocalPath which
                    // can break WebView2 IsTrustedUri path pins → blank window. We navigate via AbsoluteUri (not a
                    // raw file path) and the page never resolves relative resources off this URL (Monaco loads from
                    // CDN, diff data via the virtual host), so the ?query is harmless here. Same pattern as DiffViewContent.
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
                }
            }
            catch (Exception ex)
            {
                _isInitializing = false; // allow retry
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // SendDiffData is triggered by the JS "ready" message, not here (avoids double-send).
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");

                if (action == "ready")
                    SendDiffData();
                else if (action == "approve")
                    Applied?.Invoke(_modifiedText);
                else if (action == "cancel")
                    Cancelled?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] Message error: " + ex.Message);
            }
        }

        /// <summary>Update the diff content. If already initialized, sends immediately; otherwise the JS "ready"
        /// message will trigger the send when the page finishes loading.</summary>
        public void SetDiff(string title, string originalText, string modifiedText, string language = null)
        {
            _title = title ?? _title;
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            if (language != null) _language = language;
            TitleName = "Diff: " + _title;

            if (_isInitialized) SendDiffData();
        }

        private void SendDiffData()
        {
            if (_webView.CoreWebView2 == null) return;

            try
            {
                // Hand Monaco the RAW texts via virtual-host files — it computes the diff itself.
                string origFile = Path.Combine(_tempDir, "original.txt");
                string modFile = Path.Combine(_tempDir, "modified.txt");
                File.WriteAllText(origFile, _originalText ?? "", new UTF8Encoding(false));
                File.WriteAllText(modFile, _modifiedText ?? "", new UTF8Encoding(false));

                string json = "{\"type\":\"setDiff\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"ignoreWhitespace\":" + (_ignoreWhitespace ? "true" : "false") + "," +
                    "\"originalUrl\":\"https://" + VIRTUAL_HOST + "/original.txt\"," +
                    "\"modifiedUrl\":\"https://" + VIRTUAL_HOST + "/modified.txt\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] SendDiffData error: " + ex.Message);
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "monaco-diff.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "monaco-diff.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "monaco-diff.html");
        }

        /// <summary>Apply light or dark theme to this diff viewer.</summary>
        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (_panel != null)
                _panel.BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        /// <summary>Apply theme to all open Monaco diff viewers.</summary>
        public static void ApplyThemeToAll(bool isDark)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    inst.ApplyTheme(isDark);
            }
        }

        public override void Dispose()
        {
            lock (_instances) { _instances.Remove(this); }
            if (_webView != null)
            {
                _webView.Dispose();
                _webView = null;
            }
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }
            CleanupTempDir();
            base.Dispose();
        }

        /// <summary>Shutdown hook: dispose every open Monaco diff viewer's WebView2 on the UI thread, before native
        /// IDE teardown, to avoid the WebView2 &lt;-&gt; native focus deadlock. Idempotent + best-effort.</summary>
        public static void DisposeAllForShutdown()
        {
            List<MonacoDiffViewContent> snapshot;
            lock (_instances) { snapshot = new List<MonacoDiffViewContent>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// WebView2 subclass that intercepts Ctrl+MouseWheel at the Win32 message level and forwards it to
        /// JavaScript for font size changes. WebView2 swallows WM_MOUSEWHEEL+Ctrl internally even when
        /// IsZoomControlEnabled=false, so the wheel event never reaches JavaScript. This override catches it first.
        /// (Same bridge as DiffViewContent.ZoomableWebView2 — the page exposes applyFontSize + savedFontSize.)
        /// </summary>
        private class ZoomableWebView2 : WebView2
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private const int MK_CONTROL = 0x0008;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEWHEEL)
                {
                    int wParam = m.WParam.ToInt32();
                    int keys = wParam & 0xFFFF;
                    if ((keys & MK_CONTROL) != 0 && CoreWebView2 != null)
                    {
                        short delta = (short)(wParam >> 16);
                        int change = delta > 0 ? 1 : -1;
                        CoreWebView2.ExecuteScriptAsync(
                            "if(typeof applyFontSize==='function')applyFontSize(savedFontSize+(" + change + "))");
                        return; // Consume — don't let WebView2 swallow it
                    }
                }
                base.WndProc(ref m);
            }
        }

        private void CleanupTempDir()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
                _tempDir = null;
            }
        }

        #region JSON helpers (mirrors DiffViewContent)

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 20);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string ExtractJsonValue(string json, string key)
        {
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

        #endregion
    }
}
