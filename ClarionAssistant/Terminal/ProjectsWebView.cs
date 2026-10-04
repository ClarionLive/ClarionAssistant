using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    public class ProjectsActionEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public string Data { get; private set; }
        public ProjectsActionEventArgs(string action, string data) { Action = action; Data = data; }
    }

    /// <summary>
    /// WebView2 "COM Controls" / "IDE Addins" tab (d4e941e3): the COM and Addin project list, moved off the
    /// Home dashboard so developers stop reading it as "one project per Clarion app". One page serves both
    /// kinds; <see cref="Kind"/> picks which. Follows the same pattern as CreateClassWebView.
    /// </summary>
    public class ProjectsWebView : UserControl
    {
        public const string KindCom = "COM Control";
        public const string KindAddin = "Addin";

        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDark = true;

        public event EventHandler<ProjectsActionEventArgs> ActionReceived;
        public event EventHandler Initialized;

        public bool IsReady { get { return _isInitialized; } }

        /// <summary>The project type this tab lists and creates: <see cref="KindCom"/> or <see cref="KindAddin"/>.</summary>
        public string Kind { get; private set; }

        public ProjectsWebView(string kind)
        {
            Kind = kind == KindAddin ? KindAddin : KindCom;

            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 46);
            Dock = DockStyle.Fill;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "projectsWebView" };
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
                _webView.ZoomFactorChanged += (s, ev) => WebViewZoomHelper.SetZoom("projects", _webView.ZoomFactor);

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                {
                    string url = new Uri(htmlPath).AbsoluteUri
                        + "?kind=" + (Kind == KindAddin ? "addin" : "com")
                        + "&theme=" + (_isDark ? "dark" : "light");
                    _webView.CoreWebView2.Navigate(url);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ProjectsWebView] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = true;
            _isInitializing = false;
            _webView.ZoomFactor = WebViewZoomHelper.GetZoom("projects");
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
                    ActionReceived?.Invoke(this, new ProjectsActionEventArgs(action, data));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ProjectsWebView] Message error: " + ex.Message);
            }
        }

        /// <summary>Send a JSON message to the projects page JavaScript.</summary>
        public void SendMessage(string json)
        {
            if (!_isInitialized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        /// <summary>Send ALL project entries as a pre-built JSON array; the page keeps its own kind (plus "Other").</summary>
        public void SetProjectsJson(string jsonArray)
        {
            SendMessage("{\"type\":\"setProjects\",\"items\":" + jsonArray + "}");
        }

        /// <summary>Send the source-control accounts list for the project modal.</summary>
        public void SetGitHubAccounts(string jsonArray)
        {
            SendMessage("{\"type\":\"setGitHubAccounts\",\"accounts\":" + jsonArray + "}");
        }

        /// <summary>Send the default project base folder (COM.ProjectsFolder) to pre-fill a new project's folder.</summary>
        public void SetDefaultProjectFolder(string folder)
        {
            SendMessage("{\"type\":\"setDefaultProjectFolder\",\"folder\":\"" + EscapeJson(folder ?? "") + "\"}");
        }

        /// <summary>Send folder browse result back to the page.</summary>
        public void SendBrowseResult(string folder, string editId)
        {
            SendMessage("{\"type\":\"browseResult\",\"folder\":\"" + EscapeJson(folder ?? "") + "\",\"editId\":\"" + EscapeJson(editId ?? "") + "\"}");
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
            string path = Path.Combine(assemblyDir, "Terminal", "projects.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "projects.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "projects.html");
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
