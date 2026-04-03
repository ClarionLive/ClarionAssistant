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
    public class HomeActionEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public string Data { get; private set; }
        public HomeActionEventArgs(string action, string data) { Action = action; Data = data; }
    }

    /// <summary>
    /// WebView2-based Home page showing recent solutions, Open Solution, and Create COM.
    /// Follows the same pattern as HeaderWebView.
    /// </summary>
    public class HomeWebView : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        public event EventHandler<HomeActionEventArgs> ActionReceived;
        public event EventHandler HomeReady;

        public bool IsReady { get { return _isInitialized; } }

        public HomeWebView()
        {
            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 46);
            Dock = DockStyle.Fill;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "homeWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            HandleCreated += OnHandleCreated;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            System.Diagnostics.Debug.WriteLine("[HomeWebView] OnVisibleChanged: Visible=" + Visible
                + ", IsHandleCreated=" + IsHandleCreated
                + ", HasParent=" + (Parent != null));
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[HomeWebView] OnHandleCreated: Visible=" + Visible
                + ", IsHandleCreated=" + IsHandleCreated
                + ", HasParent=" + (Parent != null)
                + ", _isInitializing=" + _isInitializing
                + ", _isInitialized=" + _isInitialized);

            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Starting WebView2 init...");
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Got environment, calling EnsureCoreWebView2Async...");
                await _webView.EnsureCoreWebView2Async(environment);
                System.Diagnostics.Debug.WriteLine("[HomeWebView] EnsureCoreWebView2Async completed.");

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                System.Diagnostics.Debug.WriteLine("[HomeWebView] htmlPath=" + htmlPath + ", exists=" + File.Exists(htmlPath));
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Init error: " + ex.GetType().Name + ": " + ex.Message);
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Stack: " + ex.StackTrace);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = true;
            _isInitializing = false;
            HomeReady?.Invoke(this, EventArgs.Empty);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Message: " + json);
                string action = ExtractJsonValue(json, "action");
                string data = ExtractJsonValue(json, "data");
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Action=" + action + ", Data=" + data);
                if (!string.IsNullOrEmpty(action))
                    ActionReceived?.Invoke(this, new HomeActionEventArgs(action, data));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[HomeWebView] Message error: " + ex.Message);
            }
        }

        /// <summary>Send a JSON message to the home page JavaScript.</summary>
        public void SendMessage(string json)
        {
            if (!_isInitialized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        /// <summary>Send solution history entries to the home page.</summary>
        public void SetSolutionHistory(string[] names, string[] paths, string[] modified, long[] sizes, long[] modifiedTs)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append(",");
                long sz = (sizes != null && i < sizes.Length) ? sizes[i] : 0;
                long ts = (modifiedTs != null && i < modifiedTs.Length) ? modifiedTs[i] : 0;
                sb.AppendFormat("{{\"name\":\"{0}\",\"path\":\"{1}\",\"modified\":\"{2}\",\"size\":{3},\"modifiedTs\":{4}}}",
                    EscapeJson(names[i]), EscapeJson(paths[i]), EscapeJson(modified[i]), sz, ts);
            }
            sb.Append("]");
            SendMessage("{\"type\":\"setSolutions\",\"items\":" + sb + "}");
        }

        /// <summary>Switch the home page between light and dark theme.</summary>
        public void SetTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(220, 224, 232);
            SendMessage("{\"type\":\"setTheme\",\"theme\":\"" + (isDark ? "dark" : "light") + "\"}");
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "home.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "home.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "home.html");
        }

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
            if (json[idx] == 'n') return null;
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
