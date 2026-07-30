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
        private readonly ClarionAssistant.Services.DiffFileContext _fileContext;
        private bool _origDirty;
        private bool _modDirty;
        // Live buffers mirrored from the page (debounced), so a close prompt can actually SAVE rather
        // than only warn — the page cannot be asked for its text from inside a cancellable close handler.
        private string _origText;
        private string _modText;
        // Version ids let us tell a CURRENT mirror from a stale one. *TextVersion is the version the
        // mirrored text was captured at; *Version is the latest the page has reported. When they
        // differ the mirror has fallen behind and must not be written to disk.
        private int _origTextVersion;
        private int _modTextVersion;
        private int _origVersion;
        private int _modVersion;
        // False until the page has reported its state at least once. Until then we cannot claim the
        // diff is clean, so a close is treated as potentially destructive.
        private bool _pageReportedState;

        // Tab-close hook. SharpDevelop closes a tab through the workbench window's OWN cancellable
        // ClosingEvent; the Form's FormClosing never fires here, and this view is IsViewOnly so the IDE
        // will never prompt for it by itself. Same mechanism MonacoClarionEditor uses (see its
        // EnsureCloseHook) — without it, closing the TAB discards unsaved compare edits silently, which
        // the page's own Close button already guards against.
        private object _wbWindow;
        private EventInfo _closingEvt;
        private System.ComponentModel.CancelEventHandler _closingHandler;
        private bool _closeHooked;

        private string _tempDir;
        private const string VIRTUAL_HOST = "clarion-monaco-diff-data";

        private static readonly List<MonacoDiffViewContent> _instances = new List<MonacoDiffViewContent>();

        /// <summary>Fires when the user clicks Approve (modified text is passed through).</summary>
        public event Action<string> Applied;

        /// <summary>Fires when the user clicks Cancel.</summary>
        public event Action Cancelled;

        /// <summary>Fires when the user saves one side of an editable file compare. The string is the SIDE
        /// TOKEN ("original"/"modified") — never a path; the handler resolves the real path through
        /// <see cref="FileContext"/>. The second argument is the pane's live text. The handler reports the
        /// outcome back via <see cref="PostSaveResult"/>.</summary>
        public event Action<string, string> SaveSideRequested;

        public override Control Control { get { return _panel; } }

        /// <summary>Non-null only for an editable whole-file compare — the authoritative record of which path
        /// each pane maps to. The page is never told a path it could send back; save requests name a SIDE and
        /// the host resolves it here, so the page cannot redirect a write at an arbitrary file.</summary>
        public ClarionAssistant.Services.DiffFileContext FileContext { get { return _fileContext; } }

        /// <summary>True when this diff is an editable file-vs-file compare rather than the read-only
        /// MCP show_diff viewer.</summary>
        public bool IsFileCompare { get { return _fileContext != null; } }

        /// <summary>True when either pane holds edits that have not been written to disk. Mirrored from the
        /// page, so it is only ever meaningful for a file compare.</summary>
        public bool HasUnsavedEdits { get { return _fileContext != null && (_origDirty || _modDirty); } }

        public MonacoDiffViewContent(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = true, bool isDark = true, ClarionAssistant.Services.DiffFileContext fileContext = null)
        {
            _title = title ?? "Diff";
            _originalText = originalText ?? "";
            _modifiedText = modifiedText ?? "";
            _language = language ?? "clarion";
            _ignoreWhitespace = ignoreWhitespace;
            _isDark = isDark;
            _fileContext = fileContext;
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
                {
                    SendDiffData();
                    // Attach HERE, not on the first dirtyState. A single keystroke followed by an
                    // immediate Ctrl+F4 can beat that message across the process boundary, and an
                    // unattached hook means the tab closes with no prompt at all.
                    EnsureCloseHook();
                }
                else if (action == "approve")
                {
                    // The page sends its LIVE modified buffer. Previously this passed _modifiedText — the
                    // text originally handed TO the page — so once the panes became editable, Approve would
                    // have silently returned the pre-edit text to the MCP caller. Falls back to the original
                    // field only if the payload has no text (an older page, or a malformed message).
                    string live = ExtractPayloadText(json);
                    Applied?.Invoke(live ?? _modifiedText);
                }
                else if (action == "cancel")
                {
                    Cancelled?.Invoke();
                }
                else if (action == "dirtyState")
                {
                    // Mirrored from the page so the host can guard destructive actions (close / replace)
                    // without having to ask the page synchronously. Sent on EVERY edit, so these version
                    // ids are the freshest the host can know about.
                    _origDirty = ExtractJsonValue(json, "origDirty") == "true";
                    _modDirty = ExtractJsonValue(json, "modDirty") == "true";
                    _origVersion = ParseIntOr(ExtractJsonValue(json, "origVersion"), _origVersion);
                    _modVersion = ParseIntOr(ExtractJsonValue(json, "modVersion"), _modVersion);
                    _pageReportedState = true;
                    EnsureCloseHook();
                }
                else if (action == "buffers")
                {
                    _origDirty = ExtractJsonValue(json, "origDirty") == "true";
                    _modDirty = ExtractJsonValue(json, "modDirty") == "true";
                    ReadBufferTexts(json);
                    _pageReportedState = true;
                    EnsureCloseHook();
                }
                else if (action == "saveSide")
                {
                    string side = ExtractJsonValue(json, "side");
                    string text = ExtractPayloadText(json);
                    if (side == null || text == null)
                        PostSaveResult(side ?? "", false, "Save request was malformed.");
                    else if (SaveSideRequested == null)
                        PostSaveResult(side, false, "Saving is not available for this diff.");
                    else
                        SaveSideRequested(side, text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] Message error: " + ex.Message);
            }
        }

        /// <summary>
        /// Pull the "text" field out of a page message. Uses a real JSON parser rather than the hand-rolled
        /// <see cref="ExtractJsonValue"/> because this field carries ARBITRARY FILE CONTENT: JSON.stringify
        /// escapes control characters as \uXXXX, which the hand-rolled reader passes through literally and
        /// would silently corrupt what we then write to disk. Returns null when absent.
        /// </summary>
        private static string ExtractPayloadText(string json)
        {
            try
            {
                var data = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
                if (data == null || !data.ContainsKey("text")) return null;
                return data["text"] as string;
            }
            catch { return null; }
        }

        /// <summary>Parse the mirrored buffers. A null/absent side means "not dirty" and deliberately
        /// CLEARS any stale text, so a side that was undone back to clean can't later be written from a
        /// buffer we no longer believe in.</summary>
        private void ReadBufferTexts(string json)
        {
            try
            {
                var data = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) throw new InvalidOperationException("buffers payload did not parse to an object");
                _origText = data.ContainsKey("original") ? data["original"] as string : null;
                _modText = data.ContainsKey("modified") ? data["modified"] as string : null;
                _origTextVersion = data.ContainsKey("origVersion") ? Convert.ToInt32(data["origVersion"]) : 0;
                _modTextVersion = data.ContainsKey("modVersion") ? Convert.ToInt32(data["modVersion"]) : 0;
                _origVersion = Math.Max(_origVersion, _origTextVersion);
                _modVersion = Math.Max(_modVersion, _modTextVersion);
            }
            catch (Exception ex)
            {
                // Do NOT leave the previous text in place with its old version looking current — that
                // is how a stale buffer gets written and reported as saved. Drop the mirror instead;
                // TryGetSideText then refuses and the close prompt cancels rather than writing.
                _origText = _modText = null;
                _origTextVersion = _modTextVersion = 0;
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] buffers parse failed: " + ex.Message);
            }
        }

        private static int ParseIntOr(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        /// <summary>
        /// The mirrored text for a side, ONLY if it is still current. The buffer arrives debounced, so
        /// it can lag the page; writing a lagging buffer would silently discard the newest keystrokes
        /// while reporting a successful save. False means "ask the page" — callers must refuse rather
        /// than invent content.
        /// </summary>
        public bool TryGetSideText(string side, out string text)
        {
            text = null;
            if (side == "original")
            {
                if (_origText == null || _origTextVersion != _origVersion) return false;
                text = _origText;
                return true;
            }
            if (side == "modified")
            {
                if (_modText == null || _modTextVersion != _modVersion) return false;
                text = _modText;
                return true;
            }
            return false;
        }

        /// <summary>Unsaved edits, OR a page that has not yet told us its state. Used to decide whether
        /// a CLOSE needs a prompt: a fast first keystroke can still be in flight, and silently
        /// discarding it is worse than an occasional prompt on a clean diff.</summary>
        public bool MayHaveUnsavedEdits
        {
            get { return HasUnsavedEdits || (_fileContext != null && !_pageReportedState); }
        }

        /// <summary>Which sides currently hold unsaved edits.</summary>
        public List<string> DirtySides()
        {
            var sides = new List<string>();
            if (_fileContext == null) return sides;
            if (_origDirty) sides.Add("original");
            if (_modDirty) sides.Add("modified");
            return sides;
        }

        /// <summary>Raised when the TAB is closing with unsaved edits. Handlers set Cancel to keep it
        /// open. Runs on the workbench window's cancellable ClosingEvent, which is the only close path
        /// a tab X / Ctrl+F4 actually goes through.</summary>
        public event Action<System.ComponentModel.CancelEventArgs> ClosingWithUnsavedEdits;

        /// <summary>
        /// Subscribe once to the workbench tab's cancellable ClosingEvent. The event is IDE-internal, so
        /// it is reflected off the window's runtime type (SdiWorkspaceWindow) or its interfaces — the
        /// same approach, and the same fallbacks, as MonacoClarionEditor.EnsureCloseHook.
        /// </summary>
        private void EnsureCloseHook()
        {
            if (_closeHooked || _fileContext == null) return;
            try
            {
                object wbw = null;
                try { wbw = WorkbenchWindow; } catch { }
                if (wbw == null) return;   // window not realized yet — try again on the next signal

                var evt = wbw.GetType().GetEvent("ClosingEvent");
                if (evt == null)
                    foreach (var itf in wbw.GetType().GetInterfaces()) { evt = itf.GetEvent("ClosingEvent"); if (evt != null) break; }

                _closeHooked = true;   // one shot either way — don't retry forever if the event is absent
                if (evt == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] ClosingEvent not found on " + wbw.GetType().FullName);
                    return;
                }

                _closingHandler = new System.ComponentModel.CancelEventHandler(OnWorkbenchClosing);
                evt.AddEventHandler(wbw, _closingHandler);
                _wbWindow = wbw;
                _closingEvt = evt;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] close-hook attach error: " + ex.Message);
            }
        }

        private void OnWorkbenchClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!MayHaveUnsavedEdits) return;
            var handler = ClosingWithUnsavedEdits;
            if (handler != null) handler(e);
        }

        /// <summary>Tell the page how a save went. On success the page marks that side clean; on failure it
        /// surfaces the message and LEAVES the side dirty, so a refused save never looks like a saved one.</summary>
        public void PostSaveResult(string side, bool ok, string message)
        {
            if (_webView == null || _webView.CoreWebView2 == null) return;
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(
                    "{\"type\":\"saveResult\"," +
                    "\"side\":" + JsonString(side ?? "") + "," +
                    "\"ok\":" + (ok ? "true" : "false") + "," +
                    "\"message\":" + JsonString(message ?? "") + "}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] PostSaveResult error: " + ex.Message);
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

                // fileCompare tells the page whether to unlock the panes and offer per-side saving at all. The
                // names/paths are for LABELS AND TOOLTIPS ONLY — the page never sends a path back (see FileContext).
                string fileCompareJson = "\"fileCompare\":false";
                if (_fileContext != null)
                {
                    fileCompareJson =
                        "\"fileCompare\":true," +
                        "\"originalName\":" + JsonString(SafeFileName(_fileContext.OriginalPath)) + "," +
                        "\"modifiedName\":" + JsonString(SafeFileName(_fileContext.ModifiedPath)) + "," +
                        "\"originalPath\":" + JsonString(_fileContext.OriginalPath) + "," +
                        "\"modifiedPath\":" + JsonString(_fileContext.ModifiedPath);
                }

                string json = "{\"type\":\"setDiff\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"ignoreWhitespace\":" + (_ignoreWhitespace ? "true" : "false") + "," +
                    fileCompareJson + "," +
                    "\"originalUrl\":\"https://" + VIRTUAL_HOST + "/original.txt\"," +
                    "\"modifiedUrl\":\"https://" + VIRTUAL_HOST + "/modified.txt\"}";
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoDiffViewContent] SendDiffData error: " + ex.Message);
            }
        }

        /// <summary>File name for a label, without throwing on a malformed path (Path.GetFileName can throw on
        /// invalid characters). A display label is never worth taking the whole diff down for.</summary>
        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path) ?? path; }
            catch { return path; }
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
            // Detach the close hook before teardown, so a queued close can't call back into a disposed view.
            if (_closingEvt != null && _wbWindow != null && _closingHandler != null)
            {
                try { _closingEvt.RemoveEventHandler(_wbWindow, _closingHandler); } catch { }
                _closingEvt = null; _wbWindow = null; _closingHandler = null;
            }
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
