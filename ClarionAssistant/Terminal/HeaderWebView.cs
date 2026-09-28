using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    public class HeaderActionEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public string Data { get; private set; }
        public HeaderActionEventArgs(string action, string data) { Action = action; Data = data; }
    }

    public class HeaderWebView : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private readonly System.Collections.Generic.List<string> _logLines = new System.Collections.Generic.List<string>();

        public event EventHandler<HeaderActionEventArgs> ActionReceived;
        public event EventHandler HeaderReady;

        public bool IsReady { get { return _isInitialized; } }

        // Fixed height (82938fc7): there is no splitter and no saved height. Values are CSS px, measured in
        // header.html with headless Edge at 320, 420 and 800 px wide, dark and light (all identical):
        // the title row + tab strip end at 72; the Solution pane, the tallest pane, ends the page at 188.
        // While Schema Sources or Source Control is active the page shrinks to the strip and the host's
        // SchemaSourcesView fills the pane below it (PanePixelHeight), so the header's total stays fixed.
        public const int CssStripHeight = 72;
        public const int CssFullHeight = 188;

        private string _activeTab = "solution";

        /// <summary>True for the tabs the host's SchemaSourcesView renders ("schema", "repo").</summary>
        public static bool IsPanelTab(string tab) { return tab == "schema" || tab == "repo"; }

        /// <summary>The active header tab: "solution", "schema" or "repo".</summary>
        public string ActiveTab { get { return _activeTab; } }

        /// <summary>Raised when the header's pixel height changed (a tab switch or a zoom).</summary>
        public event EventHandler LayoutChanged;

        /// <summary>Pixel height of the pane under the tab strip, at the header's zoom and DPI.</summary>
        public int PanePixelHeight { get { return ToPixels(CssFullHeight - CssStripHeight); } }

        public HeaderWebView()
        {
            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 46);
            Height = ToPixels(CssFullHeight);
            Dock = DockStyle.Top;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "headerWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.ZoomFactorChanged += (s, ev) =>
                {
                    WebViewZoomHelper.SetZoom("header", _webView.ZoomFactor);
                    ApplyHeight();
                };

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[HeaderWebView] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = true;
            _isInitializing = false;
            _webView.ZoomFactor = WebViewZoomHelper.GetZoom("header");
            ApplyHeight();
            HeaderReady?.Invoke(this, EventArgs.Empty);
        }

        private int ToPixels(int cssPixels)
        {
            double zoom = _webView != null ? _webView.ZoomFactor : 1.0;
            return (int)Math.Ceiling(cssPixels * zoom * DeviceDpi / 96.0);
        }

        private void ApplyHeight()
        {
            if (IsDisposed) return;
            Height = ToPixels(_activeTab == "solution" ? CssFullHeight : CssStripHeight);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                // Simple JSON parse — avoid dependency on JSON library
                string action = ExtractJsonValue(json, "action");
                string data = ExtractJsonValue(json, "data");
                if (action == "headerTab")
                {
                    if (data != "solution" && !IsPanelTab(data)) return;
                    _activeTab = data;
                    ApplyHeight();
                }
                if (!string.IsNullOrEmpty(action))
                    ActionReceived?.Invoke(this, new HeaderActionEventArgs(action, data));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[HeaderWebView] Message error: " + ex.Message);
            }
        }

        /// <summary>Send a JSON message to the header JavaScript.</summary>
        public void SendMessage(string json)
        {
            if (!_isInitialized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        /// <summary>Set the version dropdown items.</summary>
        public void SetVersions(string[] labels, string[] values, int selectedIndex)
        {
            var items = new System.Text.StringBuilder("[");
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0) items.Append(",");
                items.AppendFormat("{{\"label\":\"{0}\",\"value\":\"{1}\",\"selected\":{2}}}",
                    EscapeJson(labels[i]), EscapeJson(values[i]), i == selectedIndex ? "true" : "false");
            }
            items.Append("]");
            SendMessage("{\"type\":\"setVersions\",\"items\":" + items + "}");
        }

        /// <summary>Set the solution dropdown items.</summary>
        public void SetSolutions(string[] paths, int selectedIndex)
        {
            var items = new System.Text.StringBuilder("[");
            for (int i = 0; i < paths.Length; i++)
            {
                if (i > 0) items.Append(",");
                string label = paths[i].Length > 60
                    ? "..." + paths[i].Substring(paths[i].Length - 57)
                    : paths[i];
                items.AppendFormat("{{\"label\":\"{0}\",\"value\":\"{1}\",\"selected\":{2}}}",
                    EscapeJson(label), EscapeJson(paths[i]), i == selectedIndex ? "true" : "false");
            }
            items.Append("]");
            SendMessage("{\"type\":\"setSolutions\",\"items\":" + items + "}");
        }

        /// <summary>Update the MCP/status text in the header.</summary>
        public void SetStatus(string text, string cssClass = "")
        {
            SendMessage("{\"type\":\"setStatus\",\"text\":\"" + EscapeJson(text) + "\",\"css\":\"" + EscapeJson(cssClass) + "\"}");
        }

        /// <summary>Update the index status text.</summary>
        public void SetIndexStatus(string text, string cssClass = "")
        {
            SendMessage("{\"type\":\"setIndexStatus\",\"text\":\"" + EscapeJson(text) + "\",\"css\":\"" + EscapeJson(cssClass) + "\"}");
        }

        /// <summary>
        /// Update the redirection (.red) line. <paramref name="openable"/> makes it a link; a click posts only
        /// the intent (openRedFile) and the host opens its own resolved path.
        /// </summary>
        public void SetRedFile(string text, string cssClass, bool openable)
        {
            SendMessage("{\"type\":\"setRedFile\",\"text\":\"" + EscapeJson(text) + "\",\"css\":\"" + EscapeJson(cssClass)
                + "\",\"openable\":" + (openable ? "true" : "false") + "}");
        }

        /// <summary>The number of schema sources linked to the solution, for the Schema Sources tab badge.</summary>
        public void SetSchemaCount(int count)
        {
            SendMessage("{\"type\":\"setSchemaCount\",\"count\":" + Math.Max(0, count) + "}");
        }

        /// <summary>Tell the page whether the host's copy of the solution path worked (it shows ✓ or ✗).</summary>
        public void SendCopyResult(bool ok)
        {
            SendMessage("{\"type\":\"copyResult\",\"ok\":" + (ok ? "true" : "false") + "}");
        }

        /// <summary>Fired when a new log line is appended (for live updates).</summary>
        public event EventHandler<string> LogLineAppended;

        /// <summary>Append a line to the index progress log (accumulated in memory).</summary>
        public void AppendIndexLog(string text)
        {
            _logLines.Add(text);
            LogLineAppended?.Invoke(this, text);
        }

        /// <summary>Clear the index progress log.</summary>
        public void ClearIndexLog()
        {
            _logLines.Clear();
        }

        /// <summary>Get accumulated log lines and clear the buffer.</summary>
        public string[] GetLogLines()
        {
            return _logLines.ToArray();
        }

        /// <summary>Enable or disable the index buttons.</summary>
        public void SetIndexButtonsEnabled(bool enabled)
        {
            SendMessage("{\"type\":\"setIndexButtons\",\"enabled\":" + (enabled ? "true" : "false") + "}");
        }

        /// <summary>Switch the header between light and dark theme.</summary>
        public void SetTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(220, 224, 232);
            SendMessage("{\"type\":\"setTheme\",\"theme\":\"" + (isDark ? "dark" : "light") + "\"}");
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "header.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "header.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "header.html");
        }

        public static string EscapeJsonStatic(string s) { return EscapeJson(s); }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r")
                    .Replace("\t", "\\t").Replace("\b", "\\b").Replace("\f", "\\f");
        }

        private static string ExtractJsonValue(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length) return null;
            if (json[idx] == 'n') return null; // null
            if (json[idx] == '"')
            {
                idx++; // skip opening quote
                var sb = new System.Text.StringBuilder();
                while (idx < json.Length)
                {
                    char c = json[idx];
                    if (c == '\\' && idx + 1 < json.Length) { sb.Append(json[idx + 1]); idx += 2; continue; }
                    if (c == '"') break;
                    sb.Append(c);
                    idx++;
                }
                return sb.ToString();
            }
            // Number or boolean
            int start = idx;
            while (idx < json.Length && json[idx] != ',' && json[idx] != '}') idx++;
            return json.Substring(start, idx - start).Trim();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _webView != null)
            {
                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                }
                _webView.Dispose();
                _webView = null;
            }
            base.Dispose(disposing);
        }
    }
}
