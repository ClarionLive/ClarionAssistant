using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    public class CreateClassActionEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public string Data { get; private set; }
        public CreateClassActionEventArgs(string action, string data) { Action = action; Data = data; }
    }

    /// <summary>
    /// WebView2-based Create Class page for generating new Clarion classes from model templates.
    /// Follows the same pattern as HomeWebView.
    /// </summary>
    public class CreateClassWebView : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDark = true;

        public event EventHandler<CreateClassActionEventArgs> ActionReceived;
        public event EventHandler Initialized;

        public bool IsReady { get { return _isInitialized; } }

        public CreateClassWebView()
        {
            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 46);
            Dock = DockStyle.Fill;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "createClassWebView" };
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
                _webView.ZoomFactorChanged += (s, ev) => WebViewZoomHelper.SetZoom("createClass", _webView.ZoomFactor);

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                {
                    string url = new Uri(htmlPath).AbsoluteUri + "?theme=" + (_isDark ? "dark" : "light");
                    _webView.CoreWebView2.Navigate(url);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CreateClassWebView] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = true;
            _isInitializing = false;
            _webView.ZoomFactor = WebViewZoomHelper.GetZoom("createClass");
            Initialized?.Invoke(this, EventArgs.Empty);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                string data = ExtractJsonValue(json, "data");
                if (!string.IsNullOrEmpty(action))
                    ActionReceived?.Invoke(this, new CreateClassActionEventArgs(action, data));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CreateClassWebView] Message error: " + ex.Message);
            }
        }

        /// <summary>Send a JSON message to the create-class page JavaScript.</summary>
        public void SendMessage(string json)
        {
            if (!_isInitialized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        /// <summary>Send the list of available class models and default output folder.</summary>
        public void SetModels(string modelsJsonArray, string outputFolder)
        {
            SendMessage("{\"type\":\"setModels\",\"models\":" + modelsJsonArray
                + ",\"outputFolder\":\"" + EscapeJson(outputFolder ?? "") + "\"}");
        }

        /// <summary>Send preview content for the selected model.</summary>
        public void SendPreviewResult(string incContent, string clwContent)
        {
            SendMessage("{\"type\":\"previewResult\",\"inc\":\"" + EscapeJson(incContent ?? "")
                + "\",\"clw\":\"" + EscapeJson(clwContent ?? "") + "\"}");
        }

        /// <summary>Send the result of a class creation attempt.</summary>
        public void SendCreateResult(bool success, string message, string className)
        {
            SendMessage("{\"type\":\"createResult\",\"success\":" + (success ? "true" : "false")
                + ",\"message\":\"" + EscapeJson(message ?? "")
                + "\",\"className\":\"" + EscapeJson(className ?? "") + "\"}");
        }

        /// <summary>Send folder browse result back to the page.</summary>
        public void SendBrowseResult(string folder)
        {
            SendMessage("{\"type\":\"browseResult\",\"folder\":\"" + EscapeJson(folder ?? "") + "\"}");
        }

        /// <summary>Switch the page between light and dark theme.</summary>
        public void SetTheme(bool isDark)
        {
            _isDark = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(220, 224, 232);
            SendMessage("{\"type\":\"setTheme\",\"theme\":\"" + (isDark ? "dark" : "light") + "\"}");
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "create-class.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "create-class.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "create-class.html");
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
                idx++;
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
