using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ClarionAssistant.Services;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Path B — Modern Embeditor (M1 spike, read-only render).
    /// Hosts a Monaco editor in WebView2 as a SharpDevelop view, showing the assembled
    /// embeditor source. Generation + parse-back + persistence remain Clarion-owned; this
    /// view is a parallel surface (mirror model — see docs/ModernEmbeditor-PathA.md, Path B).
    ///
    /// M1 scope: scaffold + render only. The editable-region map (read-only guard) and the
    /// save round-trip back through WriteEmbedContentByLine / SaveAndCloseEmbeditor are M2.
    ///
    /// Mirrors the proven WebView2-as-view pattern from DiffViewContent.cs: shared environment
    /// cache, virtual-host folder mapping for large-buffer transfer, and a JS to C# message bridge.
    /// </summary>
    public class ModernEmbeditorViewContent : AbstractViewContent
    {
        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        private string _title;
        private string _sourceText;
        private string _language;
        private bool _isDark = true;
        private List<int[]> _editableRanges; // 1-based inclusive [start,end] embed-slot ranges
        private readonly string _procedureName;     // set when opened from the picker (enables save)
        private List<string> _originalSlotTexts;     // baseline slot contents for change detection
        private readonly bool _saveEnabled;

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-embeditor-data";

        private static readonly List<ModernEmbeditorViewContent> _instances = new List<ModernEmbeditorViewContent>();

        public override Control Control { get { return _panel; } }

        public ModernEmbeditorViewContent(string title, string sourceText, List<int[]> editableRanges,
            string language = "clarion", bool isDark = true, string procedureName = null)
        {
            _title = title ?? "Embeditor";
            _sourceText = sourceText ?? "";
            _editableRanges = editableRanges ?? new List<int[]>();
            _language = language ?? "clarion";
            _isDark = isDark;
            _procedureName = procedureName;
            _saveEnabled = !string.IsNullOrWhiteSpace(procedureName);
            _originalSlotTexts = ModernEmbeditorSaver.ExtractSlotTexts(_sourceText, _editableRanges);
            TitleName = "Modern: " + _title;

            _panel = new Panel { Dock = DockStyle.Fill, BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245) };
            // Plain WebView2 — Monaco's native mouseWheelZoom handles Ctrl+wheel inside the
            // renderer (a WinForms WndProc override never sees WebView2's inner Chrome wheel msg).
            _webView = new WebView2 { Dock = DockStyle.Fill };
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

                _tempDir = Path.Combine(Path.GetTempPath(), "ClarionEmbeditor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
                settings.AreBrowserAcceleratorKeysEnabled = false; // let Monaco own Ctrl+S, not the browser

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
            }
            catch (Exception ex)
            {
                _isInitializing = false; // allow retry
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // SendSource is triggered by the JS "ready" message, not here — avoids double-send.
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                if (action == "ready")
                    SendSource();
                else if (action == "save")
                    HandleSave(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] Message error: " + ex.Message);
            }
        }

        /// <summary>Persist the user's edits: parse the per-slot payload and run the save round-trip.</summary>
        private void HandleSave(string json)
        {
            if (!_saveEnabled || string.IsNullOrWhiteSpace(_procedureName))
            {
                PostSaveResult(false, "Save isn't available — this tab was opened in mirror mode, not from the procedure picker.");
                return;
            }

            List<string> current;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                var arr = (data != null && data.ContainsKey("slots")) ? data["slots"] as object[] : null;
                if (arr == null) { PostSaveResult(false, "Save failed: malformed payload (no slots)."); return; }
                current = arr.Select(o => o == null ? "" : o.ToString()).ToList();
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Save failed parsing the editor payload: " + ex.Message);
                return;
            }

            bool ok;
            string msg = ModernEmbeditorSaver.Save(_procedureName, _editableRanges, _originalSlotTexts, current, out ok);
            // On success, the saved content is the new baseline so a follow-up save sees no changes.
            if (ok && current.Count == _originalSlotTexts.Count) _originalSlotTexts = current;
            // The save activated the app tree to drive the embeditor — bring this tab back to the front.
            BringToFront();
            PostSaveResult(ok, msg);
        }

        /// <summary>Re-select this view's tab (the save round-trip activates the app tree to drive the embeditor).</summary>
        private void BringToFront()
        {
            try
            {
                var w = WorkbenchWindow;
                if (w != null) w.GetType().GetMethod("SelectWindow", Type.EmptyTypes)?.Invoke(w, null);
            }
            catch { }
        }

        private void PostSaveResult(bool ok, string message)
        {
            PostSaveResultOnce(ok, message);
            // Backup re-post on the next message-loop turn — delivery of the first post can race with
            // the embeditor open/close churn that just happened during the save.
            try { _panel?.BeginInvoke((Action)(() => PostSaveResultOnce(ok, message))); }
            catch { }
        }

        private void PostSaveResultOnce(bool ok, string message)
        {
            if (_webView == null || _webView.CoreWebView2 == null) return;
            string json = "{\"type\":\"saveResult\",\"ok\":" + (ok ? "true" : "false") +
                          ",\"message\":" + JsonString(message) + "}";
            try { _webView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

        /// <summary>Update the displayed source. Sends immediately if ready, else waits for the JS "ready".</summary>
        public void SetSource(string title, string sourceText, string language = null)
        {
            _title = title ?? _title;
            _sourceText = sourceText ?? "";
            if (language != null) _language = language;
            TitleName = "Modern: " + _title;

            if (_isInitialized)
                SendSource();
        }

        private void SendSource()
        {
            if (_webView.CoreWebView2 == null) return;

            try
            {
                // Transfer source via the virtual host (temp file) to avoid huge postMessage payloads.
                string sourceFile = Path.Combine(_tempDir, "source.txt");
                File.WriteAllText(sourceFile, _sourceText ?? "", Encoding.UTF8);

                string json = "{\"type\":\"setSource\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"saveEnabled\":" + (_saveEnabled ? "true" : "false") + "," +
                    "\"editableRanges\":" + RangesJson() + "," +
                    "\"sourceUrl\":\"https://" + VIRTUAL_HOST + "/source.txt\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] SendSource error: " + ex.Message);
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            if (_panel != null)
                _panel.BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized && _webView?.CoreWebView2 != null)
                _webView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        public static void ApplyThemeToAll(bool isDark)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    inst.ApplyTheme(isDark);
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(assemblyDir, "Terminal", "monaco-embeditor.html");
            if (File.Exists(path)) return path;
            path = Path.Combine(assemblyDir, "monaco-embeditor.html");
            if (File.Exists(path)) return path;
            return Path.Combine(assemblyDir, "Terminal", "monaco-embeditor.html");
        }

        /// <summary>Serializes the editable ranges as a JSON array of [start,end] pairs (1-based, inclusive).</summary>
        private string RangesJson()
        {
            if (_editableRanges == null || _editableRanges.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < _editableRanges.Count; i++)
            {
                var r = _editableRanges[i];
                if (r == null || r.Length < 2) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append('[').Append(r[0]).Append(',').Append(r[1]).Append(']');
            }
            sb.Append(']');
            return sb.ToString();
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
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
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
            if (json == null) return null;
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
