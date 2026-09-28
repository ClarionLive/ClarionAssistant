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
    public class SchemaSourceActionEventArgs : EventArgs
    {
        public string Action { get; private set; }
        public string Data { get; private set; }
        public SchemaSourceActionEventArgs(string action, string data) { Action = action; Data = data; }
    }

    /// <summary>
    /// The CA header's Schema Sources / Source Control panes (82938fc7): ONE WebView2 panel for the whole
    /// chat pane, docked under the header and shown while one of those header tabs is active. It is sized
    /// to the header's fixed pane (PaneHeight) and grows to fit the Manage Sources modal while it is open.
    /// It shares the header's zoom (HeaderWebView.ZoomKey): its height is the header's pane, so content at a
    /// different zoom would not fit the pane it was sized for. Follows the same pattern as HomeWebView.
    /// </summary>
    public class SchemaSourcesView : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _modalOpen;
        private int _paneHeight = HeaderWebView.CssFullHeight - HeaderWebView.CssStripHeight;

        public event EventHandler<SchemaSourceActionEventArgs> ActionReceived;
        public event EventHandler Ready;

        public bool IsReady { get { return _isInitialized; } }

        // The Manage Sources modal is a fixed 580 CSS px design (schema-sources.html .modal); the panel grows
        // to fit it (at the zoom and DPI) while it is open and returns to the pane height when it closes.
        private const int MODAL_HEIGHT = 580;

        /// <summary>Raised when the user zooms this panel (Ctrl+wheel); the host carries it to the header.</summary>
        public event EventHandler ZoomChanged;

        /// <summary>True while the Manage Sources modal is open.</summary>
        public bool ModalOpen { get { return _modalOpen; } }

        /// <summary>The panel's zoom; the host keeps it equal to the header's.</summary>
        public double ZoomFactor
        {
            get { return _webView != null ? _webView.ZoomFactor : 1.0; }
            set { if (_webView != null && Math.Abs(_webView.ZoomFactor - value) > 0.001) _webView.ZoomFactor = value; }
        }

        private int ModalPixelHeight
        {
            get { return (int)Math.Ceiling(MODAL_HEIGHT * ZoomFactor * DeviceDpi / 96.0); }
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            if (_modalOpen) Height = ModalPixelHeight;   // the pane height arrives from the header's LayoutChanged
        }

        /// <summary>The pixel height of the header pane this view fills (set by the host from the header).</summary>
        public int PaneHeight
        {
            get { return _paneHeight; }
            set
            {
                _paneHeight = Math.Max(1, value);
                if (!_modalOpen) Height = _paneHeight;
            }
        }

        public SchemaSourcesView()
        {
            SuspendLayout();
            BackColor = Color.FromArgb(30, 30, 46);
            Dock = DockStyle.Top;
            Height = _paneHeight;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "schemaSourcesWebView" };
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
                    if (_modalOpen) Height = ModalPixelHeight;
                    ZoomChanged?.Invoke(this, EventArgs.Empty);
                };

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SchemaSourcesView] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = true;
            _isInitializing = false;
            _webView.ZoomFactor = WebViewZoomHelper.GetZoom(HeaderWebView.ZoomKey);
            Ready?.Invoke(this, EventArgs.Empty);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                string data = ExtractJsonValue(json, "data");

                // Handle modal open/close — expand height to fit form
                if (action == "modalOpened")
                {
                    _modalOpen = true;
                    Height = ModalPixelHeight;
                    return;
                }
                if (action == "modalClosed")
                {
                    _modalOpen = false;
                    Height = _paneHeight;
                    return;
                }

                if (!string.IsNullOrEmpty(action))
                    ActionReceived?.Invoke(this, new SchemaSourceActionEventArgs(action, data));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SchemaSourcesView] Message error: " + ex.Message);
            }
        }

        /// <summary>Send a JSON message to the schema-sources.html JavaScript.</summary>
        public void SendMessage(string json)
        {
            if (!_isInitialized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        /// <summary>Send the solution's linked sources to the UI.</summary>
        public void SetSources(string jsonArray)
        {
            SendMessage("{\"type\":\"setSources\",\"items\":" + jsonArray + "}");
        }

        /// <summary>
        /// Send all global sources (for the Manage Sources modal), stamped with the solution they were drawn
        /// for; the page echoes it on Select so the host can refuse a stale write.
        /// </summary>
        public void SetGlobalSources(string jsonArray, string linkedIdsJson, string slnPath)
        {
            SendMessage("{\"type\":\"setGlobalSources\",\"sln\":\"" + EscapeJson(slnPath ?? "") + "\",\"items\":" + jsonArray
                + ",\"linkedIds\":" + linkedIdsJson + "}");
        }

        /// <summary>Update index status for a single source.</summary>
        public void SetIndexStatus(string sourceId, string statusJson)
        {
            SendMessage("{\"type\":\"indexStatus\",\"sourceId\":\"" + EscapeJson(sourceId) + "\",\"status\":" + statusJson + "}");
        }

        /// <summary>Send folder/file browse result back to the JS.</summary>
        public void SendBrowseResult(string path, string editId)
        {
            SendMessage("{\"type\":\"browseResult\",\"path\":\"" + EscapeJson(path ?? "") + "\",\"editId\":\"" + EscapeJson(editId ?? "") + "\"}");
        }

        /// <summary>Switch between light and dark theme.</summary>
        public void SetTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);   // = the header page's light #eff1f5
            SendMessage("{\"type\":\"setTheme\",\"theme\":\"" + (isDark ? "dark" : "light") + "\"}");
        }

        /// <summary>Show the Schema Sources ("schema") or the Source Control ("repo") pane.</summary>
        public void SetMode(string mode)
        {
            if (!HeaderWebView.IsPanelTab(mode)) return;
            SendMessage("{\"type\":\"setMode\",\"mode\":\"" + mode + "\"}");
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "schema-sources.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "schema-sources.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "schema-sources.html");
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
                var sb = new StringBuilder();
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
