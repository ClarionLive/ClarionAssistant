using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    public class TerminalSizeEventArgs : EventArgs
    {
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public TerminalSizeEventArgs(int cols, int rows) { Columns = cols; Rows = rows; }
    }

    public class WebViewTerminalRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private float _fontSize = 10f;
        private string _fontFamily = "Cascadia Mono";

        public event EventHandler<float> FontSizeChangedByUser;
        private int _cols = 80;
        private int _rows = 24;

        private readonly Queue<byte[]> _pendingData = new Queue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _pendingWrites = new ConcurrentQueue<byte[]>();
        private volatile bool _writeScheduled;
        private readonly object _writeLock = new object();

        public event Action<byte[]> DataReceived;
        public event EventHandler<TerminalSizeEventArgs> TerminalResized;
        public event EventHandler Initialized;

        public bool IsInitialized { get { return _isInitialized; } }
        public int VisibleCols { get { return _cols; } }
        public int VisibleRows { get { return _rows; } }

        public WebViewTerminalRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();
            BackColor = Color.FromArgb(12, 12, 12);
            Name = "WebViewTerminalRenderer";
            Size = new Size(640, 400);

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "webView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            // Lazy init: only initialize WebView2 when the control becomes visible.
            // This avoids WebView2 issues with hidden controls and matches
            // the MultiTerminal pattern (BrowserTabPage.OnVisibleChanged).
            VisibleChanged += OnVisibleChanged;
        }

        private async void OnVisibleChanged(object sender, EventArgs e)
        {
            if (!Visible) return; // Only init when becoming visible
            System.Diagnostics.Debug.WriteLine("[WVRenderer] VisibleChanged(true). _isInitializing=" + _isInitializing + ", _isInitialized=" + _isInitialized + ", Parent=" + (Parent?.Name ?? "null"));
            if (_isInitializing || _isInitialized)
            {
                System.Diagnostics.Debug.WriteLine("[WVRenderer] Init BLOCKED by guard");
                return;
            }
            _isInitializing = true;

            try
            {
                System.Diagnostics.Debug.WriteLine("[WVRenderer] Getting WebView2 environment...");
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                System.Diagnostics.Debug.WriteLine("[WVRenderer] Got environment. Calling EnsureCoreWebView2Async...");
                await _webView.EnsureCoreWebView2Async(environment);
                System.Diagnostics.Debug.WriteLine("[WVRenderer] EnsureCoreWebView2Async completed.");

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = true;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetTerminalHtmlPath();
                System.Diagnostics.Debug.WriteLine("[WVRenderer] HTML path: " + htmlPath + ", exists: " + File.Exists(htmlPath));
                if (File.Exists(htmlPath))
                {
                    string uri = new Uri(htmlPath).AbsoluteUri;
                    System.Diagnostics.Debug.WriteLine("[WVRenderer] Navigating to: " + uri);
                    _webView.CoreWebView2.Navigate(uri);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[WVRenderer] HTML NOT FOUND!");
                    ShowError("Terminal HTML not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WVRenderer] EXCEPTION: " + ex);
                ShowError("Failed to initialize WebView2: " + ex.Message);
            }
        }

        private string GetTerminalHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "Terminal", "terminal.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "terminal.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "Terminal", "terminal.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = Color.Red,
                BackColor = Color.Black,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowError("Failed to load terminal: " + e.WebErrorStatus);
                _isInitializing = false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var message = ParseJsonMessage(json);

                switch (message.Type)
                {
                    case "ready":
                        OnTerminalReady(message);
                        break;
                    case "input":
                        OnTerminalInput(message.Data);
                        break;
                    case "resize":
                        OnTerminalResize(message.Cols, message.Rows);
                        break;
                    case "paste":
                        OnPasteRequested();
                        break;
                    case "fontSizeChanged":
                        float newSize;
                        if (float.TryParse(message.Data, out newSize))
                        {
                            _fontSize = Math.Max(6f, Math.Min(32f, newSize));
                            FontSizeChangedByUser?.Invoke(this, _fontSize);
                        }
                        break;
                }
            }
            catch { }
        }

        private void OnTerminalReady(TerminalMessage message)
        {
            _isInitialized = true;
            _isInitializing = false;
            _cols = message.Cols;
            _rows = message.Rows;

            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString("fontSize:" + _fontSize.ToString());
                _webView.CoreWebView2.PostWebMessageAsString("fontFamily:" + _fontFamily);
            }

            while (_pendingData.Count > 0)
                WriteToTerminalInternal(_pendingData.Dequeue());

            Initialized?.Invoke(this, EventArgs.Empty);
            TerminalResized?.Invoke(this, new TerminalSizeEventArgs(_cols, _rows));
        }

        private void OnTerminalInput(string base64Data)
        {
            if (string.IsNullOrEmpty(base64Data)) return;
            try
            {
                byte[] data = Convert.FromBase64String(base64Data);
                DataReceived?.Invoke(data);
            }
            catch { }
        }

        private void OnTerminalResize(int cols, int rows)
        {
            if (cols > 0 && rows > 0)
            {
                _cols = cols;
                _rows = rows;
                TerminalResized?.Invoke(this, new TerminalSizeEventArgs(cols, rows));
            }
        }

        private void OnPasteRequested()
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                    DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
            }
        }

        public void WriteToTerminal(byte[] data)
        {
            if (!_isInitialized)
            {
                _pendingData.Enqueue(data);
                return;
            }

            _pendingWrites.Enqueue(data);
            ScheduleWrite();
        }

        private void ScheduleWrite()
        {
            lock (_writeLock)
            {
                if (_writeScheduled) return;
                _writeScheduled = true;
            }

            if (InvokeRequired)
                BeginInvoke(new Action(FlushWrites));
            else
                FlushWrites();
        }

        private void FlushWrites()
        {
            lock (_writeLock) { _writeScheduled = false; }

            var allData = new List<byte>();
            byte[] data;
            while (_pendingWrites.TryDequeue(out data))
                allData.AddRange(data);

            if (allData.Count > 0)
                WriteToTerminalInternal(allData.ToArray());
        }

        private void WriteToTerminalInternal(byte[] data)
        {
            if (_webView?.CoreWebView2 == null) return;
            try
            {
                string base64 = Convert.ToBase64String(data);
                _webView.CoreWebView2.PostWebMessageAsString("data:" + base64);
            }
            catch { }
        }

        public void SetFontSize(float size)
        {
            size = Math.Max(6f, Math.Min(32f, size));
            if (Math.Abs(_fontSize - size) < 0.1f) return;
            _fontSize = size;

            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString("fontSize:" + size.ToString());
        }

        public void SetFontFamily(string family)
        {
            if (string.IsNullOrEmpty(family)) return;
            _fontFamily = family;
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString("fontFamily:" + family);
        }

        public void SetTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(12, 12, 12) : Color.White;
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString("theme:" + (isDark ? "dark" : "light"));
        }

        public void Clear()
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString("clear:");
        }

        public new void Focus()
        {
            base.Focus();
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString("focus:");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }

        #region JSON Parsing

        private class TerminalMessage
        {
            public string Type { get; set; }
            public string Data { get; set; }
            public int Cols { get; set; }
            public int Rows { get; set; }
        }

        private TerminalMessage ParseJsonMessage(string json)
        {
            var msg = new TerminalMessage();
            json = json.Trim();
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);

            int pos = 0;
            while (pos < json.Length)
            {
                while (pos < json.Length && (json[pos] == ' ' || json[pos] == ',' || json[pos] == '\n' || json[pos] == '\r'))
                    pos++;
                if (pos >= json.Length) break;

                string key = ParseJsonString(json, ref pos);
                if (string.IsNullOrEmpty(key)) break;

                while (pos < json.Length && (json[pos] == ' ' || json[pos] == ':'))
                    pos++;

                if (pos < json.Length && json[pos] == '"')
                {
                    string value = ParseJsonString(json, ref pos);
                    if (key == "type") msg.Type = value;
                    else if (key == "data") msg.Data = value;
                }
                else
                {
                    int start = pos;
                    while (pos < json.Length && char.IsDigit(json[pos])) pos++;
                    if (pos > start)
                    {
                        string numStr = json.Substring(start, pos - start);
                        int num;
                        if (int.TryParse(numStr, out num))
                        {
                            if (key == "cols") msg.Cols = num;
                            else if (key == "rows") msg.Rows = num;
                            else if (key == "fontSize") msg.Data = numStr;
                        }
                    }
                }
            }
            return msg;
        }

        private string ParseJsonString(string json, ref int pos)
        {
            while (pos < json.Length && json[pos] != '"') pos++;
            if (pos >= json.Length) return null;
            pos++;

            var sb = new StringBuilder();
            while (pos < json.Length && json[pos] != '"')
            {
                if (json[pos] == '\\' && pos + 1 < json.Length)
                {
                    pos++;
                    switch (json[pos])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(json[pos]); break;
                    }
                }
                else sb.Append(json[pos]);
                pos++;
            }
            if (pos < json.Length) pos++;
            return sb.ToString();
        }

        #endregion
    }
}
