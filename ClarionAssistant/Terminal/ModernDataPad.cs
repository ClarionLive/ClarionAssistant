using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant
{
    /// <summary>
    /// Modern Data pad (Path B). Lists the active Modern Embeditor tab's data symbols (locals/globals/
    /// structures, from the LSP document-symbol tree) and lets the developer double-click one to insert it
    /// at the cursor in that tab. Our managed replacement for Clarion's native Data/Tables field selector,
    /// which can't drive a non-ICSharpCode editor. Works with the snapshot model, so multi-open is preserved.
    /// </summary>
    public class ModernDataPad : AbstractPadContent
    {
        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        public override Control Control
        {
            get
            {
                if (_panel == null)
                {
                    _panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
                    _webView = new WebView2 { Dock = DockStyle.Fill };
                    _panel.Controls.Add(_webView);
                    _panel.HandleCreated += OnHandleCreated;
                }
                return _panel;
            }
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;
            try
            {
                var environment = await Terminal.WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Restore the ctrl-mousewheel font zoom (WebView2's built-in ZoomFactor) and keep it saved.
                _webView.ZoomFactor = Terminal.WebViewZoomHelper.GetZoom("modernDataPad");
                _webView.ZoomFactorChanged += (s2, e2) => Terminal.WebViewZoomHelper.SetZoom("modernDataPad", _webView.ZoomFactor);

                StartAutoRefreshTimer();

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                System.Diagnostics.Debug.WriteLine("[ModernDataPad] Init error: " + ex.Message);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // Trust boundary: only honor messages from our OWN bundled page. If the WebView is ever navigated
                // to other content (or a message is posted from an unexpected source), ignore it — this gates the
                // mutating actions (add/edit/deleteVariable) so a hijacked document can't drive live IDE edits.
                if (!IsTrustedSource(e.Source)) return;

                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                if (action == "ready")
                {
                    _isInitialized = true;
                    PostRestoreUiState(); // send saved collapse/expand state BEFORE the first data render
                    Refresh();
                }
                else if (action == "saveUiState")
                {
                    // Opaque JSON blob owned by the pad's JS (sectionCollapsed/localCollapsed/relExpanded/detailOpen).
                    Terminal.ModernDataPadState.Save(ExtractJsonValue(json, "state"));
                }
                else if (action == "openSettings")
                {
                    // ⚙ in the pad: open the floating native settings window (a real top-level window that can
                    // move anywhere over the IDE, unlike an in-webview overlay clipped to this dock pane).
                    if (_panel != null) _panel.BeginInvoke((Action)ShowSettingsWindow);
                    else ShowSettingsWindow();
                }
                else if (action == "refresh")
                {
                    // Explicit Refresh re-exports the whole-app .txa FIRST (UI thread, silent) so it picks up
                    // changes made outside the Modern Embeditor — e.g. dictionary/table edits — then re-parses
                    // the fresh cache. Deferred out of the WebView2 callback (like 'open') to avoid re-entrancy.
                    if (_panel != null)
                        _panel.BeginInvoke((Action)(() =>
                        {
                            Terminal.ModernEmbeditorViewContent.RefreshPadSources();
                            Refresh();
                        }));
                    else
                        Refresh();
                }
                else if (action == "insert")
                {
                    string name = ExtractJsonValue(json, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        // Act on the SAME context that rendered the displayed pad data (captured at the last
                        // Refresh), NOT a fresh Resolve(): by click time the WebView2 pad holds focus, so a
                        // re-resolve could fail the native-focus probe and fall back to an unfocused Modern tab,
                        // writing into the wrong editor. _renderCtx matches what the developer is looking at.
                        var ctx = _renderCtx;
                        if (ctx != null) ctx.Insert(name);
                        // [19] Also place the same compilable (prefixed) text on the Windows clipboard
                        // so it can be pasted elsewhere. We're on the UI/STA thread here; guard against
                        // the clipboard being transiently locked by another process.
                        try { System.Windows.Forms.Clipboard.SetText(name); }
                        catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] clipboard: " + cex.Message); }
                    }
                }
                else if (action == "open")
                {
                    string name = ExtractJsonValue(json, "name");
                    if (!string.IsNullOrEmpty(name) && _panel != null)
                    {
                        // Defer OUT of the WebView2 message callback — running the heavy open synchronously
                        // here re-enters the message pump. On the next UI turn: focus if already open, else open.
                        _panel.BeginInvoke((Action)(() =>
                        {
                            // Move focus off the pad's WebView2 to the main IDE window first — the native
                            // embeditor close (Discard) deadlocks if a WebView2 holds focus during it.
                            try
                            {
                                var mainForm = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench as Form;
                                if (mainForm != null) { mainForm.Activate(); Application.DoEvents(); }
                            }
                            catch { }
                            try
                            {
                                if (!Terminal.ModernEmbeditorViewContent.TryFocusExisting(name))
                                    Services.ModernEmbeditorLauncher.OpenProcedure(name, false);
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] open: " + ex.Message); }
                            // Do NOT pre-seed _lastShownProc/_lastShownNative or Refresh() here: OpenProcedure()
                            // defers ShowView to a later UI turn, so the target tab isn't the active document yet.
                            // Pre-seeding the change-detector would suppress the corrective tick-refresh once it
                            // IS active, leaving _renderCtx bound to the pre-open editor. Let OnAutoRefreshTick
                            // detect the newly-focused tab and refresh then.
                        }));
                    }
                }
                else if (action == "goto")
                {
                    string name = ExtractJsonValue(json, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        // Same rendered-context rule as 'insert': navigate via the context that produced the
                        // displayed routine list, so the line matches and we never re-resolve to a different
                        // editor after the pad took focus.
                        var ctx = _renderCtx;
                        if (ctx != null) ctx.GotoRoutine(name);
                    }
                }
                else if (action == "addVariable")
                {
                    // "＋ Add" on the Local/Global section: drive Clarion's OWN add-variable flow (managed
                    // FileSchemaTree → AddItemEventHandler → FieldForm). Scope = "local" (current procedure) |
                    // "global". 'procedure' is what the WebView had ON SCREEN at click time — a LOCAL op validates
                    // against THIS (not _renderCtx, which flips a tick before the WebView visibly renders the
                    // switch, leaving a sub-render-tick race); the inserter fails closed on a procedure mismatch.
                    string scope = ExtractJsonValue(json, "scope");
                    string clickedProc = ExtractJsonValue(json, "procedure");
                    RunVariableCrud("Add Variable", () => Services.FileSchemaVariableInserter.AddVariable(scope, clickedProc));
                }
                else if (action == "editVariable")
                {
                    // ✎ on a Local/Global row: open Clarion's FieldForm for the field at this structural path.
                    // Clarion picks editable (ChangeRecord) vs read-only (ViewRecord) by the field's
                    // DataStorageLocation. 'path' (Group/Member) resolves nested members unambiguously.
                    string scope = ExtractJsonValue(json, "scope");
                    string path = ExtractJsonValue(json, "path");
                    string clickedProc = ExtractJsonValue(json, "procedure");
                    if (!string.IsNullOrEmpty(path))
                        RunVariableCrud("Edit Variable", () => Services.FileSchemaVariableInserter.EditVariable(scope, path, clickedProc));
                }
                else if (action == "deleteVariable")
                {
                    // 🗑 on a Local/Global row: invoke Clarion's delete for the field at this structural path
                    // (Clarion pops its own ConfirmDeletionForm / Yes-No, so no extra JS confirm).
                    string scope = ExtractJsonValue(json, "scope");
                    string path = ExtractJsonValue(json, "path");
                    string clickedProc = ExtractJsonValue(json, "procedure");
                    if (!string.IsNullOrEmpty(path))
                        RunVariableCrud("Delete Variable", () => Services.FileSchemaVariableInserter.DeleteVariable(scope, path, clickedProc));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] Message error: " + ex.Message); }
        }

        /// <summary>
        /// Pull the focused procedure editor's data (locals + used tables) off the UI thread. The active editor
        /// is whichever the resolver picks — the native (PWEE) embeditor when focused, else the Modern view.
        /// Resolve() runs HERE on the UI thread (it snapshots the native buffer); GetPadData() runs on the task.
        /// </summary>
        public void Refresh()
        {
            ++_refreshGen;   // supersede any in-flight parse (incl. this no-editor transition)
            var ctx = Services.ActiveProcedureContext.Resolve();
            if (ctx == null)
            {
                _renderCtx = null;   // nothing focused — stale insert/goto can't fire
                _pendingCtx = null;
                Post(new Dictionary<string, object>
                {
                    { "title", "(no procedure editor active)" },
                    { "locals", new List<object>() }, { "tables", new List<object>() }
                });
                return;
            }
            // Latest-wins: record the desired context; a running pump picks it up when it finishes. We do NOT
            // touch _renderCtx here — it is committed together with the payload that actually renders (see
            // PumpRefresh), so the displayed data and the insert/goto target can never diverge under a refresh
            // race (proc A still parsing while the user switches to B).
            _pendingCtx = ctx;
            if (_refreshing) return;
            _refreshing = true;
            PumpRefresh();
        }

        /// <summary>
        /// Parse the latest pending context off the UI thread, then on the UI thread commit _renderCtx and post
        /// its payload TOGETHER — so insert/goto are always bound to the procedure currently displayed — and loop
        /// if a newer context arrived mid-parse. Guarantees the latest switch always renders (no dropped refresh).
        /// </summary>
        private void PumpRefresh()
        {
            var ctx = _pendingCtx;   // UI thread
            _pendingCtx = null;
            int gen = _refreshGen;   // the generation this parse belongs to
            Task.Run(() =>
            {
                Dictionary<string, object> data;
                try { data = ctx.GetPadData(); }
                catch { data = null; }
                if (data == null)
                    data = new Dictionary<string, object> { { "locals", new List<object>() }, { "tables", new List<object>() } };
                object proc; data.TryGetValue("procedure", out proc);
                data["title"] = string.IsNullOrEmpty(proc as string) ? "Data" : (string)proc;

                Action commit = () =>
                {
                    // Drop if a newer Refresh (including a no-editor transition that cleared _renderCtx) superseded
                    // this parse — never resurrect stale data or rebind _renderCtx behind a newer state.
                    if (gen != _refreshGen)
                    {
                        if (_pendingCtx != null) PumpRefresh(); else _refreshing = false;
                        return;
                    }
                    _renderCtx = ctx;          // bind actions to the context that produced THIS payload (UI thread)
                    Post(data);
                    if (_pendingCtx != null) PumpRefresh();   // a newer switch arrived during parse → render it next
                    else _refreshing = false;
                };
                try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(commit); else commit(); }
                catch { _refreshing = false; }
            });
        }

        // Outstanding deferred add-refresh timers, tracked so Dispose() can stop them and so repeated ＋Add
        // clicks coalesce (a new schedule drops the previous passes) instead of stacking whole-app exports.
        private readonly System.Collections.Generic.List<Timer> _addRefreshTimers = new System.Collections.Generic.List<Timer>();

        // Single-flight guard for variable add/edit/delete (UI thread only): true from the moment a CRUD op is
        // accepted until its flow (incl. the modal FieldForm / delete confirmation) completes. Blocks re-entrant
        // ops AND the 750ms auto-refresh tick so a background .txa export can't churn the tree the modal is editing.
        private volatile bool _varCrudInProgress;

        /// <summary>
        /// Run a Local/Global variable CRUD op (add / edit / delete) off the WebView2 message callback. All three
        /// share the same machinery: single-flight (also suppresses the auto-tick so a .txa export can't churn the
        /// tree mid-modal), defer onto the next UI turn (the FieldForm / delete-confirm is modal), move focus to the
        /// main IDE first (a WebView2 holding focus can deadlock native modals — same rule as 'open'/'refresh'),
        /// invoke the inserter, surface failures via MessageBox, and refresh our pad on success via ScheduleAddRefresh
        /// (the deferred whole-app .txa re-export — Clarion finalizes the mutation a turn after the modal unwinds).
        /// </summary>
        private void RunVariableCrud(string title, Func<Services.FileSchemaVariableInserter.Result> op)
        {
            if (_panel == null || _varCrudInProgress) return;
            _varCrudInProgress = true;
            _panel.BeginInvoke((Action)(() =>
            {
                Services.FileSchemaVariableInserter.Result r = null;
                try
                {
                    try
                    {
                        var mainForm = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench as Form;
                        if (mainForm != null) { mainForm.Activate(); Application.DoEvents(); }
                    }
                    catch { }

                    try { r = op(); }
                    catch (Exception ex) { r = Services.FileSchemaVariableInserter.Result.Fail(ex.Message); }

                    // On failure tell the developer why nothing happened (pad closed, read-only, wrong-procedure,
                    // not found, can't-delete). On success Clarion's own form/confirm was the feedback.
                    if (r != null && !r.Ok)
                    {
                        try { MessageBox.Show(r.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
                        catch { }
                    }
                }
                finally { _varCrudInProgress = false; }

                // Refresh only after an actual committed mutation. A cancelled delete returns Ok with Committed=false
                // so we skip the whole-app .txa export on a no-op. (Add/edit are Committed by design — a single
                // user-initiated op's refresh is cheap, and edit no-op detection isn't reliable.)
                if (r != null && r.Ok && r.Committed) ScheduleAddRefresh();
            }));
        }

        /// <summary>
        /// After a "＋ Add" commits a variable through Clarion's FieldForm, re-export the whole-app .txa and
        /// re-render — on LATER UI turns (Clarion finalizes the new field a turn after the modal closes, so an
        /// immediate export misses it). Two passes (short + longer) absorb settle time on larger apps. Coalesces:
        /// any pending passes from a prior add are cancelled first, so rapid adds don't stack exports.
        /// </summary>
        private void ScheduleAddRefresh()
        {
            ClearAddRefreshTimers();
            DeferRefresh(500);
            DeferRefresh(1500);
        }

        private void ClearAddRefreshTimers()
        {
            foreach (var t in _addRefreshTimers) { try { t.Stop(); t.Dispose(); } catch { } }
            _addRefreshTimers.Clear();
        }

        private void DeferRefresh(int ms)
        {
            var t = new Timer { Interval = ms };
            t.Tick += (s, e) =>
            {
                try { t.Stop(); _addRefreshTimers.Remove(t); t.Dispose(); } catch { }
                if (_panel == null || _panel.IsDisposed) return;   // pad torn down — don't touch the IDE
                try { Terminal.ModernEmbeditorViewContent.RefreshPadSources(); } catch { }
                Refresh();
            };
            _addRefreshTimers.Add(t);
            t.Start();
        }

        // The floating native settings window (null when closed). Reused/activated if already open.
        private Terminal.DataPadSettingsWindow _settingsWindow;

        /// <summary>Open (or re-focus) the floating Data-pad settings window. UI thread.</summary>
        private void ShowSettingsWindow()
        {
            try
            {
                if (_settingsWindow != null && !_settingsWindow.IsDisposed)
                {
                    _settingsWindow.Activate();
                    _settingsWindow.ReseedState();   // refresh the cards from the pad's latest persisted layout
                    return;
                }
                _settingsWindow = new Terminal.DataPadSettingsWindow(OnSettingsStateChanged);
                _settingsWindow.FormClosed += (s, e) => { _settingsWindow = null; };
                var owner = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench as Form;
                if (owner != null) _settingsWindow.Show(owner); else _settingsWindow.Show();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] settings window: " + ex.Message); }
        }

        /// <summary>
        /// The settings window changed the layout — push it to the live pad so it applies instantly. The pad
        /// applies ONLY the section-layout fields and persists its own current state (single-writer), so the
        /// window's stale snapshot of theme/zoom/tree-collapse/detail state can't clobber live pad state.
        /// </summary>
        private void OnSettingsStateChanged(string state)
        {
            Post(new Dictionary<string, object> { { "type", "applyLayout" }, { "state", state } });
        }

        /// <summary>Send the persisted collapse/expand UI state to the pad so it can restore before first render.</summary>
        private void PostRestoreUiState()
        {
            string state = Terminal.ModernDataPadState.Load();
            if (string.IsNullOrEmpty(state)) return;
            // Pass the saved blob through as a STRING value (the JS JSON.parses it). Keeping it opaque means
            // the host never has to understand the state shape.
            Post(new Dictionary<string, object> { { "type", "restoreUiState" }, { "state", state } });
        }

        private void Post(Dictionary<string, object> data)
        {
            Action post = () =>
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                try
                {
                    var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    if (!data.ContainsKey("type")) data["type"] = "setSymbols"; // default; restoreUiState sets its own
                    _webView.CoreWebView2.PostWebMessageAsJson(ser.Serialize(data));
                }
                catch { }
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        private volatile bool _refreshing;
        private Timer _autoTimer;

        // The procedure-editor context that produced the CURRENTLY displayed pad data. insert/goto act on THIS
        // (set on the UI thread in Refresh) instead of re-resolving at click time — by click time the WebView2
        // pad holds focus, and a fresh Resolve() could fail the native-focus probe and fall back to an unfocused
        // Modern tab, writing into the wrong editor. Acting on the rendered context = act on what's displayed.
        private Services.ActiveProcedureContext _renderCtx;

        // The most recently requested context awaiting a parse. Latest-wins: while a parse runs, a newer Refresh
        // overwrites this and the running pump picks it up on completion, so the displayed payload and _renderCtx
        // are always committed together for the SAME context (UI thread only).
        private Services.ActiveProcedureContext _pendingCtx;

        // Monotonic refresh generation, bumped on EVERY Refresh() — including the no-editor (Resolve()==null)
        // transition. A pump commit whose captured generation is stale is dropped, so an in-flight parse can
        // never resurrect _renderCtx / repost old data after focus has moved away (UI thread only).
        private int _refreshGen;

        // Re-entrancy guard for the native-activation whole-app .txa export (RefreshPadSources on the UI thread).
        private bool _nativeSourcesRefreshing;

        // Editor KIND of the last shown procedure. Change detection keys on (proc name + kind): a switch from
        // Modern "Foo" to native "Foo" (same name, different editor) must still refresh, or _renderCtx would stay
        // bound to the previous editor and insert/goto would target it.
        private bool _lastShownNative;
        private string _lastShownProc = " "; // sentinel so the first tick always refreshes

        /// <summary>
        /// Auto-refresh via a timer (NOT a workbench event subscription). The event approach re-entered
        /// during the native embeditor close and deadlocked; a timer tick runs in a clean message-loop
        /// context and skips while an open/save is busy.
        /// </summary>
        private void StartAutoRefreshTimer()
        {
            if (_autoTimer != null) return;
            _autoTimer = new Timer { Interval = 750 };
            _autoTimer.Tick += OnAutoRefreshTick;
            _autoTimer.Start();
        }

        private void OnAutoRefreshTick(object sender, EventArgs e)
        {
            if (Services.ModernEmbeditorLauncher.IsBusy) return;  // never touch the IDE mid open/save
            if (_varCrudInProgress) return;  // never run a .txa export while Clarion's add/edit/delete modal is open
            string proc;
            bool isNative = false;
            try
            {
                // Lightweight peek (native embeditor preferred, else Modern view) — no buffer snapshot per tick.
                // This is the pad's "active-document-changed" detector; we poll on a timer rather than subscribe
                // to workbench events because the event approach re-entered the native close and deadlocked.
                proc = Services.ActiveProcedureContext.PeekActiveProcedure(out isNative);
            }
            catch { return; }
            // Refresh when the active procedure OR its editor kind changed (or it went away). Keying on kind too
            // catches a Modern->native (or native->Modern) switch at the same procedure name.
            if (!string.Equals(proc, _lastShownProc, StringComparison.OrdinalIgnoreCase) || isNative != _lastShownNative)
            {
                _lastShownProc = proc;
                _lastShownNative = isNative;
                // Switching to a NATIVE procedure: refresh the whole-app .txa + live-dict snapshot first. The
                // native path has no open/save hook to populate them, so without this a native-only session would
                // show empty Local/Global/Tables data. (Modern proc-switches reuse the cache the open/save hooks
                // already maintain.) RefreshPadSources is UI-thread + best-effort (keeps the prior cache on error).
                if (isNative && !string.IsNullOrEmpty(proc) && !_nativeSourcesRefreshing)
                {
                    // Re-entrancy guard: RefreshPadSources runs a silent whole-app .txa export on the UI thread;
                    // if it pumps messages, a re-entrant timer tick must not stack a second export. (Only fires on
                    // proc CHANGE, so it's already debounced to native-activation events, not every 750ms tick.)
                    _nativeSourcesRefreshing = true;
                    try { Terminal.ModernEmbeditorViewContent.RefreshPadSources(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] native RefreshPadSources: " + ex.Message); }
                    finally { _nativeSourcesRefreshing = false; }
                }
                Refresh();
            }
        }

        public override void RedrawContent() { _lastShownProc = " "; Refresh(); }

        public override void Dispose()
        {
            ClearAddRefreshTimers();
            if (_settingsWindow != null && !_settingsWindow.IsDisposed) { try { _settingsWindow.Close(); } catch { } _settingsWindow = null; }
            if (_autoTimer != null) { _autoTimer.Stop(); _autoTimer.Dispose(); _autoTimer = null; }
            if (_webView != null) { _webView.Dispose(); _webView = null; }
            if (_panel != null) { _panel.Dispose(); _panel = null; }
            base.Dispose();
        }

        private static string GetHtmlPath()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(dir, "Terminal", "modern-data-pad.html");
            return File.Exists(path) ? path : Path.Combine(dir, "modern-data-pad.html");
        }

        // True only when a WebView2 message came from our OWN bundled page (the file URI of GetHtmlPath, ignoring
        // the ?v= cache-buster query). This is the trust gate for all host actions — especially the mutating
        // add/edit/deleteVariable — so a document hijack / unexpected navigation can't drive live IDE edits.
        private static bool IsTrustedSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            try
            {
                string expected = new Uri(GetHtmlPath()).AbsoluteUri;   // file:///.../modern-data-pad.html
                int q = source.IndexOf('?');
                string src = q >= 0 ? source.Substring(0, q) : source;
                return string.Equals(src, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            if (json == null) return null;
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            var sb = new System.Text.StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length)
                {
                    char n = json[idx + 1];
                    if (n == '"') { sb.Append('"'); idx += 2; continue; }
                    if (n == '\\') { sb.Append('\\'); idx += 2; continue; }
                    if (n == 'n') { sb.Append('\n'); idx += 2; continue; }
                    if (n == 't') { sb.Append('\t'); idx += 2; continue; }
                    sb.Append(c); idx++; continue;
                }
                if (c == '"') break;
                sb.Append(c); idx++;
            }
            return sb.ToString();
        }
    }
}
