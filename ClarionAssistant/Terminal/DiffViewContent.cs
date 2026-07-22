using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ClarionAssistant.Services;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// ViewContent that hosts a unified diff viewer with inline code review notes.
    /// Generates unified diff via git or LCS fallback, renders in WebView2.
    /// </summary>
    public class DiffViewContent : AbstractViewContent
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

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-diff-data";

        private static readonly List<DiffViewContent> _instances = new List<DiffViewContent>();

        /// <summary>Fires when the user clicks Approve (modified text is passed through).</summary>
        public event Action<string> Applied;

        /// <summary>Fires when the user clicks Cancel.</summary>
        public event Action Cancelled;

        /// <summary>Fires when the user submits review notes (JSON array string).</summary>
        public event Action<string> NotesSubmitted;

        public override Control Control { get { return _panel; } }

        private bool _isDark = true;

        public DiffViewContent(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = false, bool isDark = true)
        {
            _title = title ?? "Diff";
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            _language = language ?? "clarion";
            _ignoreWhitespace = ignoreWhitespace;
            _isDark = isDark;
            TitleName = "Diff: " + _title;
            // No Save()/SaveAs() implementation exists for this view — without this, SharpDevelop
            // treats it as a normal, saveable, filename-less document (enables Save, then throws when invoked).
            IsViewOnly = true;

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

                // Set up temp directory and virtual host mapping for large file transfer
                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionDiff_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tempDir);
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VIRTUAL_HOST, _tempDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsZoomControlEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
            }
            catch (Exception ex)
            {
                _isInitializing = false; // Reset so retry is possible
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // Don't call SendDiffData here — the JS "ready" message is the trigger.
            // This avoids double-invocation (NavigationCompleted + "ready" both fire on every load).
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");

                if (action == "ready")
                {
                    SendDiffData();
                }
                else if (action == "approve")
                {
                    // Read-only viewer — return the stored modified text
                    Applied?.Invoke(_modifiedText);
                }
                else if (action == "notes")
                {
                    string notesJson = ExtractJsonValue(json, "notes");
                    NotesSubmitted?.Invoke(notesJson ?? "[]");
                }
                else if (action == "cancel")
                {
                    Cancelled?.Invoke();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] Message error: " + ex.Message);
            }
        }

        /// <summary>Update the diff content. If already initialized, sends immediately; otherwise waits for "ready" message.</summary>
        public void SetDiff(string title, string originalText, string modifiedText, string language = null)
        {
            _title = title ?? _title;
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            if (language != null) _language = language;
            TitleName = "Diff: " + _title;

            if (_isInitialized)
                SendDiffData();
            // Otherwise the JS "ready" message will trigger SendDiffData when the page loads
        }

        private void SendDiffData()
        {
            if (_webView.CoreWebView2 == null) return;

            try
            {
                // Generate unified diff (git diff --no-index, LCS fallback — shared with get_diff_content)
                string diffText = UnifiedDiffGenerator.Generate(_originalText, _modifiedText, _ignoreWhitespace);

                // Write diff to temp file for virtual host serving
                string diffFile = Path.Combine(_tempDir, "diff.txt");
                File.WriteAllText(diffFile, diffText, Encoding.UTF8);

                // Send metadata — JavaScript fetches the diff via virtual host URL
                string json = "{\"type\":\"setDiff\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"diffUrl\":\"https://" + VIRTUAL_HOST + "/diff.txt\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DiffViewContent] SendDiffData error: " + ex.Message);
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "diff.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "diff.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "diff.html");
        }

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
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    default:
                        if (c < ' ')
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
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

        /// <summary>Apply light or dark theme to this diff viewer.</summary>
        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (_panel != null)
                _panel.BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        /// <summary>Apply theme to all open diff viewers.</summary>
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

        /// <summary>Shutdown hook: dispose every open diff viewer's WebView2 on the UI thread, before native
        /// IDE teardown, to avoid the WebView2 &lt;-&gt; native focus deadlock. Idempotent + best-effort.</summary>
        public static void DisposeAllForShutdown()
        {
            List<DiffViewContent> snapshot;
            lock (_instances) { snapshot = new List<DiffViewContent>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// WebView2 subclass that intercepts Ctrl+MouseWheel at the Win32 message level
        /// and forwards it to JavaScript for font size changes.
        /// WebView2 swallows WM_MOUSEWHEEL+Ctrl internally even when IsZoomControlEnabled=false,
        /// so the wheel event never reaches JavaScript. This override catches it first.
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
    }
}
