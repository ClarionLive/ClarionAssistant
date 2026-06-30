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
                    // Cache-bust keyed to the file's last-write time. WebView2 caches
                    // file:// pages aggressively, so after deploying an edited
                    // terminal.html a stale page can serve and make a correct fix look
                    // broken. The query changes only when the html actually changes, so
                    // launches are still served from cache when nothing moved. (Safe
                    // here — direct file:// navigation, no virtual-host trust pins.)
                    string uri = new Uri(htmlPath).AbsoluteUri
                        + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks;
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
                    case "copy":
                        OnCopyRequested(message.SelectedText);
                        break;
                    case "contextmenu":
                        OnContextMenuRequested(message.X, message.Y, message.SelectedText, message.SelectedTextRaw);
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

        private void OnCopyRequested(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // navigator.clipboard.writeText() silently fails on file:// origins under
            // current WebView2, so the page routes Ctrl+C and OSC 52 copies here. Runs
            // on the UI (STA) thread — required by Clipboard.SetText. Best-effort: the
            // clipboard can be transiently locked by another process. Mirrors OnPasteRequested.
            try { Clipboard.SetText(text); }
            catch { }
        }

        private void OnPasteRequested()
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;

            // Claude Code's prompt UI caps at ~5-6 visible rows; pasting a long
            // blob makes the prompt unreadable and unscrollable. For pastes over
            // the threshold, write to a temp file and inject "@<short-path> "
            // instead — Claude reads the file on submit and the prompt stays
            // visually compact.
            if (IsLargePaste(text))
            {
                string cached = WritePasteCacheFile(text);
                if (cached != null)
                {
                    InjectAtReference(cached);
                    return;
                }
                // Cache write failed — fall through to raw paste so the user
                // doesn't silently lose their clipboard.
            }
            DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        }

        private void OnContextMenuRequested(int clientX, int clientY, string selectedText, string selectedTextRaw)
        {
            try
            {
                if (_webView == null || _webView.IsDisposed) return;

                var menu = new ContextMenuStrip();

                var copyItem = new ToolStripMenuItem("Copy");
                copyItem.Enabled = !string.IsNullOrEmpty(selectedText);
                copyItem.Click += (s, e) =>
                {
                    try { if (!string.IsNullOrEmpty(selectedText)) Clipboard.SetText(selectedText); }
                    catch { }
                };
                menu.Items.Add(copyItem);

                // "Copy raw" — only when raw differs from flowed (otherwise it'd
                // be a confusing duplicate). Useful for code blocks, tables,
                // and any structured output that should NOT have continuation
                // rows joined into paragraphs.
                if (!string.IsNullOrEmpty(selectedTextRaw) && selectedTextRaw != selectedText)
                {
                    var copyRawItem = new ToolStripMenuItem("Copy raw (no paragraph join)");
                    copyRawItem.Click += (s, e) =>
                    {
                        try { Clipboard.SetText(selectedTextRaw); }
                        catch { }
                    };
                    menu.Items.Add(copyRawItem);
                }

                var pasteItem = new ToolStripMenuItem("Paste");
                pasteItem.Enabled = Clipboard.ContainsText();
                pasteItem.Click += (s, e) => OnPasteRequested();
                menu.Items.Add(pasteItem);

                menu.Items.Add(new ToolStripSeparator());

                var pasteFromFileItem = new ToolStripMenuItem("Paste from file…");
                pasteFromFileItem.Click += (s, e) => PasteFromFileDialog();
                menu.Items.Add(pasteFromFileItem);

                // Only surface the "large clipboard as file" item when it would
                // actually do something different from regular Paste.
                bool hasLargeClip = false;
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string clip = Clipboard.GetText();
                        hasLargeClip = !string.IsNullOrEmpty(clip) && IsLargePaste(clip);
                    }
                }
                catch { }
                if (hasLargeClip)
                {
                    var pasteLargeItem = new ToolStripMenuItem("Paste large clipboard as file");
                    pasteLargeItem.Click += (s, e) =>
                    {
                        try
                        {
                            string clip = Clipboard.GetText();
                            if (string.IsNullOrEmpty(clip)) return;
                            string cached = WritePasteCacheFile(clip);
                            if (cached != null) InjectAtReference(cached);
                        }
                        catch { }
                    };
                    menu.Items.Add(pasteLargeItem);
                }

                Point screen = _webView.PointToScreen(new Point(clientX, clientY));
                menu.Show(screen);
            }
            catch { }
        }

        private static bool IsLargePaste(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > 1000) return true;
            int newlines = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { newlines++; if (newlines > 4) return true; }
            }
            return false;
        }

        private void PasteFromFileDialog()
        {
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Paste file as @reference";
                    dlg.Filter = "Text files (*.txt;*.md;*.log;*.json;*.xml;*.clw;*.inc)|*.txt;*.md;*.log;*.json;*.xml;*.clw;*.inc|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(dlg.FileName))
                        InjectAtReference(dlg.FileName);
                }
            }
            catch { }
        }

        private string WritePasteCacheFile(string text)
        {
            try
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClarionAssistant", "paste-cache");
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

                // Prune entries older than 24h to keep the cache from accumulating.
                try
                {
                    DateTime cutoff = DateTime.UtcNow.AddHours(-24);
                    foreach (var f in Directory.GetFiles(baseDir, "paste-*.txt"))
                    {
                        try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
                    }
                }
                catch { }

                string name = "paste-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".txt";
                string full = Path.Combine(baseDir, name);
                File.WriteAllText(full, text, new UTF8Encoding(false));
                return full;
            }
            catch { return null; }
        }

        private void InjectAtReference(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            string injected = ToShortPathIfPossible(fullPath);
            // Trailing space terminates the @reference for Claude Code's parser
            // and leaves the cursor positioned for the user's question.
            DataReceived?.Invoke(Encoding.UTF8.GetBytes("@" + injected + " "));
        }

        private static string ToShortPathIfPossible(string longPath)
        {
            try
            {
                if (string.IsNullOrEmpty(longPath)) return longPath;
                if (longPath.IndexOf(' ') < 0) return longPath;
                var sb = new StringBuilder(longPath.Length + 260);
                uint n = NativeMethods.GetShortPathName(longPath, sb, (uint)sb.Capacity);
                if (n > 0 && n <= sb.Capacity) return sb.ToString();
            }
            catch { }
            return longPath;
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

        public void UpdateStatusLine(string json)
        {
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsString("statusLine:" + json);
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
            public int X { get; set; }
            public int Y { get; set; }
            public string SelectedText { get; set; }
            public string SelectedTextRaw { get; set; }
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
                    else if (key == "selectedText") msg.SelectedText = value;
                    else if (key == "selectedTextRaw") msg.SelectedTextRaw = value;
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
                            else if (key == "x") msg.X = num;
                            else if (key == "y") msg.Y = num;
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
