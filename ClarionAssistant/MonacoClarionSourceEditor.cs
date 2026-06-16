using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Debugging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using CWBinding.ClarionEditor;
using SoftVelocity.Common.ClarionEditor;
using ClarionAssistant.Terminal;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    // ── Monaco-default-editor spike (task cc8b092f) ────────────────────────────────────────
    // PHASE 0 (DONE, validated live in C12): our DisplayBinding subclass wins the source-file
    //   editor race ahead of stock ClarionWinEditor, and the Structure Designer (Ctrl+D) still
    //   works through the subclass (both designer gates are is-a checks ClarionEditor satisfies).
    // PHASE 1 (this file, in progress): host a Monaco/WebView2 overlay over the live editor.
    //   FOUNDATION landed here = capture the live text area from our subclass + prove we can read
    //   its document + a persisted toggle so the overlay can be flipped on per-file without a
    //   rebuild. The actual WebView2 attach + caret/document sync is wired in the live test loop
    //   (it's the freeze-prone part — native ops during WebView2 focus — and needs in-IDE
    //   iteration; see [[project_modern_embeditor]] FREEZE fix). Overlay is OFF by default.
    // See memory project_monaco_default_editor and MONACO_INTEGRATION_WRITEUP.md.

    /// <summary>
    /// Subclass of the stock Clarion source editor. Phase 0 was identity-only; Phase 1 adds a
    /// (toggle-gated) Monaco overlay over the live SharpDevelopTextAreaControl. The base remains a
    /// fully-working ClarionEditor (TextEditorDisplayBindingWrapper + IStructureDesignerCompatible),
    /// so the designer/app-gen keep functioning through us.
    /// </summary>
    public class MonacoClarionEditor : ClarionEditor, IMonacoEditorHost
    {
        private Timer _captureTimer;     // polls until the view's Control (text area) is realized
        private int _captureTries;
        private bool _captured;

        // Phase-1 overlay (inert, read-only): a Monaco/WebView2 surface docked over the live text
        // area. Caret/document two-way sync is Phase 2 — for now we push the buffer once on load.
        private MonacoEditorControl _editor;   // reusable Monaco surface (converge step 4), docked over the native editor
        private Panel _cover;            // opaque shim that hides the native editor until Monaco paints
        private ICSharpCode.TextEditor.TextEditorControl _hostEditor;   // the live editor we mirror
        private string _overlayTitle = "Clarion Source";
        private string _filePath;        // the source file we edit (from the native editor), saved to disk by Monaco
        private bool _overlayDirty;      // mirrored from the page (fileState) — drives save-on-close
        private string _overlayLiveText; // last live buffer the page mirrored, for the close-save write

        // Host-driven navigation (debugger / breakpoint-list click via MonacoSourceNavigator). The page can't
        // be positioned until it signals ready, so a nav that arrives earlier is parked here and flushed in
        // OnReady. 1-based; _navPendingLine == 0 means "nothing queued".
        private bool _pageReady;
        private int _navPendingLine;
        private int _navPendingCol = 1;

        // Save-on-close: our workbench tab is a SdiWorkspaceWindow (WeifenLuo DockContent : Form), so we host
        // the prompt on its CANCELLABLE FormClosing — which runs BEFORE teardown. (A MessageBox in Dispose
        // pumps a nested message loop that interrupts the in-flight tab close → the "have to click X twice" bug.)
        private Form _wbForm;
        private bool _closeHooked;

        // Monaco's loading background (#eff1f5) — the cover matches it so the swap to Monaco is seamless.
        private static readonly Color CoverColor = Color.FromArgb(0xEF, 0xF1, 0xF5);

        public MonacoClarionEditor()
        {
            // The text area isn't necessarily realized at ctor time (the wrapper builds it as the
            // view loads). Poll on the UI thread until Control exists + has a handle, then capture.
            try
            {
                _captureTimer = new Timer { Interval = 15 };
                _captureTimer.Tick += CaptureTick;
                _captureTimer.Start();
            }
            catch (Exception ex) { MonacoSpikeLog.Write("MonacoClarionEditor ctor timer error: " + ex.Message); }
        }

        private void CaptureTick(object sender, EventArgs e)
        {
            try
            {
                _captureTries++;
                Control host = null;
                try { host = this.Control; } catch { /* not ready */ }

                // Capture as soon as the control object exists — do NOT wait for the window handle.
                // Covering it before its first paint is what eliminates the native-editor flash.
                if (host == null)
                {
                    if (_captureTries >= 400) StopCaptureTimer();   // ~6s safety cap (15ms * 400)
                    return;
                }

                StopCaptureTimer();
                if (_captured) return;
                _captured = true;

                // Prove we can reach the live editor + read its buffer from our subclass — this is
                // the foundation the overlay + caret/document sync are built on.
                var tec = host as ICSharpCode.TextEditor.TextEditorControl;
                int len = -1, lines = -1;
                try
                {
                    if (tec != null && tec.Document != null)
                    {
                        len = tec.Document.TextContent != null ? tec.Document.TextContent.Length : 0;
                        lines = tec.Document.TotalNumberOfLines;
                    }
                }
                catch (Exception rex) { MonacoSpikeLog.Write("read document error: " + rex.Message); }

                MonacoSpikeLog.Write(string.Format(
                    "captured text area: viewType={0} controlType={1} size={2}x{3} docChars={4} docLines={5} overlayEnabled={6}",
                    GetType().Name, host.GetType().FullName, host.Width, host.Height, len, lines,
                    MonacoSourceOverlay.Enabled));

                // Keep the native editor ref + file path in BOTH modes: overlay-on uses it for save/disk +
                // breakpoints; overlay-off uses it as the visible editor for native-caret navigation. Register
                // with the navigator now so a debugger nav can find us (overlay-on re-registers in OnReady once
                // the page-resolved path is firm).
                _hostEditor = tec;
                try { _filePath = (tec != null ? tec.FileName : null) ?? _filePath; } catch (Exception fex) { MonacoSpikeLog.Write("capture filename error: " + fex.Message); }
                MonacoSourceNavigator.Register(_filePath, this);

                if (MonacoSourceOverlay.Enabled)
                {
                    AttachCover(host);     // hide the native editor FIRST (instant, opaque)
                    AttachOverlay(host);   // then the WebView2/Monaco surface on top of the cover
                }
                else
                {
                    // Overlay off: the native editor is the visible surface — apply any queued nav immediately.
                    ApplyPendingNavigation();
                }
            }
            catch (Exception ex) { MonacoSpikeLog.Write("CaptureTick error: " + ex.Message); }
        }

        /// <summary>Opaque shim docked over the native editor, added the instant we capture the host
        /// (before its first paint) so the Clarion editor is never visibly shown. The WebView2 sits on
        /// top of this; the cover matches Monaco's loading background so there's no flash on the swap.</summary>
        private void AttachCover(Control host)
        {
            try
            {
                if (_cover != null) return;
                _cover = new Panel { Dock = DockStyle.Fill, BackColor = CoverColor };
                host.Controls.Add(_cover);
                _cover.BringToFront();
            }
            catch (Exception ex) { MonacoSpikeLog.Write("AttachCover error: " + ex.Message); }
        }

        /// <summary>Dock a WebView2/Monaco surface over the live text area and cover it. The real
        /// editor stays alive underneath (so the designer/app-gen keep working through us); Monaco
        /// is purely the visible surface. Read-only mirror for the inert Phase-1 milestone.</summary>
        private void AttachOverlay(Control host)
        {
            try
            {
                if (_editor != null) return;
                // Reuse the rich embeditor Monaco surface (colorize/minimap/find/keymap come free) instead of
                // the bespoke monaco-source.html page. We are its host; it renders a read-only fileMode mirror.
                _editor = new MonacoEditorControl(this, true, "monaco-embeditor.html", "clarion-embeditor-data");
                host.Controls.Add(_editor);
                _editor.BringToFront();
                WireBreakpoints();   // keep Monaco's gutter in sync with IDE breakpoints
                MonacoSpikeLog.Write("overlay MonacoEditorControl attached over host; awaiting ready");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("AttachOverlay error: " + ex.Message); }
        }

        // ── Host-driven navigation (debugger / breakpoint-list click) ───────────────────────────────
        // Reached via MonacoSourceNavigator. Works in BOTH overlay states: overlay-on reveals the live Monaco
        // editor; overlay-off moves the native caret. Caller passes 1-based line/column; conversions are owned
        // here.

        /// <summary>Position this editor at a 1-based line (and column), scrolling it into view. Queues until
        /// the page is ready if the overlay is still loading. Returns true once handled or queued.</summary>
        public bool NavigateToLine(int line, int column)
        {
            if (line < 1) return false;
            if (column < 1) column = 1;
            try
            {
                if (_editor != null)            // overlay attached → reveal the Monaco surface
                {
                    if (_pageReady) _editor.RevealLine(line, column);
                    else { _navPendingLine = line; _navPendingCol = column; }   // flushed in OnReady
                    return true;
                }
                if (_hostEditor != null)        // overlay off → native editor is what's visible
                {
                    NativeGoTo(line);
                    return true;
                }
                // Host not captured yet — park it; CaptureTick/OnReady will apply.
                _navPendingLine = line; _navPendingCol = column;
                return true;
            }
            catch (Exception ex) { MonacoSpikeLog.Write("NavigateToLine error: " + ex.Message); return false; }
        }

        /// <summary>Pull and apply a navigation that the navigator parked for this file (on capture / ready).</summary>
        internal void ApplyPendingNavigation()
        {
            try
            {
                int line, col;
                if (MonacoSourceNavigator.TryConsumePending(_filePath, out line, out col))
                    NavigateToLine(line, col);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("ApplyPendingNavigation error: " + ex.Message); }
        }

        // Overlay-off path: move the native ICSharpCode caret + scroll. Reuses the proven EditorService.GoToLine
        // (1-based in, 0-based caret out) against the captured text area, reached reflectively to avoid hard
        // ICSharpCode.TextEditor type coupling here.
        private void NativeGoTo(int line)
        {
            try
            {
                object textArea = null;
                try
                {
                    var atc = _hostEditor.GetType().GetProperty("ActiveTextAreaControl")?.GetValue(_hostEditor, null);
                    textArea = atc != null ? atc.GetType().GetProperty("TextArea")?.GetValue(atc, null) : null;
                }
                catch { }
                if (textArea != null) new Services.EditorService().GoToLine(textArea, line);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("NativeGoTo error: " + ex.Message); }
        }

        // ── IMonacoEditorHost (converge step 4) ─────────────────────────────────────────────────
        // The MonacoEditorControl drives these as the page sends messages. The overlay is a READ-ONLY
        // mirror for this milestone, so only OnReady does work (push the native editor's buffer as a
        // fileMode, read-only setSource). LSP / save / designer are intentionally inert here — Step 5+
        // wires the two-way caret/document sync and re-fires Ctrl+D.
        void IMonacoEditorHost.OnReady(MonacoEditorControl editor)
        {
            try
            {
                if (_editor == null || _editor.TempDir == null) return;

                // Resolve the file path from the captured native editor — Monaco owns load/save to disk.
                try { _filePath = (_hostEditor != null ? _hostEditor.FileName : null) ?? _filePath; }
                catch (Exception fex) { MonacoSpikeLog.Write("overlay filename error: " + fex.Message); }
                if (!string.IsNullOrEmpty(_filePath)) _overlayTitle = Path.GetFileName(_filePath);

                string text = "";
                try { if (_hostEditor != null && _hostEditor.Document != null) text = _hostEditor.Document.TextContent ?? ""; }
                catch (Exception rex) { MonacoSpikeLog.Write("overlay read document error: " + rex.Message); }

                // Large-buffer transfer via the virtual host (same mechanism the embeditor uses).
                File.WriteAllText(Path.Combine(_editor.TempDir, "source.txt"), text, Encoding.UTF8);

                string settingsJson;
                try { settingsJson = new JavaScriptSerializer().Serialize(ModernEmbeditorSettings.Load().ToDict()); }
                catch { settingsJson = "null"; }

                string json = "{\"type\":\"setSource\","
                    + "\"title\":" + MonacoEditorControl.JsonString(_overlayTitle) + ","
                    + "\"language\":\"clarion\","
                    + "\"isDark\":true,"
                    + "\"fileMode\":true,"
                    + "\"readOnly\":false,"
                    + "\"breakpointsEnabled\":true,"
                    + "\"designerEnabled\":true,"
                    + "\"filePath\":" + MonacoEditorControl.JsonString(_filePath ?? "") + ","
                    + "\"saveEnabled\":true,"
                    + "\"editableRanges\":[],"
                    + "\"settings\":" + settingsJson + ","
                    + "\"findHistory\":[],\"replaceHistory\":[],\"procHistory\":[],"
                    + "\"cursorLine\":0,\"cursorColumn\":0,"
                    + "\"bookmarks\":[],"
                    + "\"sourceUrl\":\"https://clarion-embeditor-data/source.txt\"}";
                _editor.PostJson(json);
                MonacoSpikeLog.Write("overlay setSource sent (fileMode editable, " + text.Length + " chars, file=" + (_filePath ?? "?") + ")");
                PushBreakpoints();   // paint any existing IDE breakpoints for this file

                // The page can now be positioned. Re-register under the firm page-resolved path, flush any nav
                // that was parked while loading, then drain a navigator request that arrived between capture
                // and ready (debugger click on a cold file).
                _pageReady = true;
                MonacoSourceNavigator.Register(_filePath, this);
                if (_navPendingLine >= 1) { _editor.RevealLine(_navPendingLine, _navPendingCol); _navPendingLine = 0; }
                ApplyPendingNavigation();
            }
            catch (Exception ex) { MonacoSpikeLog.Write("overlay OnReady error: " + ex.Message); }
        }

        // Monaco owns the buffer and saves straight to disk — the native editor underneath stays a clean,
        // untouched shell (never edited → never dirty → no dueling save). It's just a file on disk.
        void IMonacoEditorHost.OnSave(MonacoEditorControl editor, string rawJson)
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath)) { editor.PostSaveResult(false, "No file path for this editor."); return; }

                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(rawJson) as System.Collections.Generic.Dictionary<string, object>;
                string text = (data != null && data.ContainsKey("text")) ? (data["text"] as string ?? "") : "";
                long seq = 0;
                try { if (data != null && data.ContainsKey("seq")) seq = Convert.ToInt64(data["seq"]); } catch { }

                int written = WriteToDisk(text);
                _overlayDirty = false;
                editor.PostSaveResult(true, "Saved", seq);
                MonacoSpikeLog.Write("overlay saved to disk: " + _filePath + " (" + written + " chars, seq " + seq + ")");
            }
            catch (Exception ex)
            {
                MonacoSpikeLog.Write("overlay save error: " + ex.Message);
                try { editor.PostSaveResult(false, "Save failed: " + ex.Message); } catch { }
            }
        }

        // LSP completion — route to the shared bridge against the REAL file path so includes/symbols resolve.
        void IMonacoEditorHost.OnCompletion(MonacoEditorControl editor, string rawJson)
        {
            int reqId, line, col; string buffer;
            if (!ParseLspRequest(rawJson, out reqId, out line, out col, out buffer)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                var items = new List<Dictionary<string, object>>();
                string lspStatus = "ok";
                try
                {
                    EnsureLsp();
                    if (!SharedLspBridge.IsRunning) lspStatus = "starting";
                    else
                    {
                        var comps = SharedLspBridge.GetCompletion(_filePath, Math.Max(0, line - 1), Math.Max(0, col - 1), 2500, buffer);
                        if (comps != null)
                            foreach (var c in comps)
                                items.Add(new Dictionary<string, object>
                                {
                                    { "label", c.Label }, { "kind", c.Kind }, { "detail", c.Detail },
                                    { "documentation", c.Documentation }, { "insertText", c.InsertText }
                                });
                    }
                }
                catch (Exception ex) { lspStatus = "error: " + ex.Message; MonacoSpikeLog.Write("overlay completion error: " + ex.Message); }
                editor.PostResponse(reqId, new Dictionary<string, object> { { "items", items }, { "lsp", lspStatus } });
            });
        }

        // LSP hover — the page shows "Loading…" until we PostResponse, so we must always answer.
        void IMonacoEditorHost.OnHover(MonacoEditorControl editor, string rawJson)
        {
            int reqId, line, col; string buffer;
            if (!ParseLspRequest(rawJson, out reqId, out line, out col, out buffer)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                string contents = null;
                try
                {
                    EnsureLsp();
                    if (SharedLspBridge.IsRunning)
                        contents = ExtractHover(SharedLspBridge.GetHover(_filePath, Math.Max(0, line - 1), Math.Max(0, col - 1), buffer));
                }
                catch (Exception ex) { MonacoSpikeLog.Write("overlay hover error: " + ex.Message); }
                editor.PostResponse(reqId, new Dictionary<string, object> { { "contents", contents } });
            });
        }

        // LSP/hybrid diagnostics — the page sends fileMode ranges [[1,lineCount]] (whole file editable).
        // embedSlotChecks:true ALSO runs the structure-balance heuristic over the whole file (unmatched
        // IF/LOOP/CASE/structure → squiggle), which file mode normally skips. John wants it for source
        // editing; caveat = it can false-positive on declaration files (FILE/GROUP as param types).
        void IMonacoEditorHost.OnDiagnostics(MonacoEditorControl editor, string rawJson)
        {
            int reqId; string buffer; List<int[]> ranges;
            if (!ParseDiagRequest(rawJson, out reqId, out buffer, out ranges)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                var markers = new List<Dictionary<string, object>>();
                try { markers = ModernEmbeditorDiagnostics.Compute(_filePath, buffer ?? "", ranges, null, embedSlotChecks: true); }
                catch (Exception ex) { MonacoSpikeLog.Write("overlay diagnostics error: " + ex.Message); }
                editor.PostResponse(reqId, new Dictionary<string, object> { { "markers", markers } });
            });
        }

        // The rest stay inert for now (Step 6 wires Ctrl+D through the embeditor's openDesigner path).
        void IMonacoEditorHost.OnClipboard(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveSettings(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveHistory(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveCursor(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveBookmarks(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSelectionChanged(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnFocusEditor(MonacoEditorControl editor) { }
        void IMonacoEditorHost.OnReload(MonacoEditorControl editor) { }

        // Track the page's live buffer + dirty flag so we can save-on-close (the native shell stays clean,
        // so the IDE never prompts — without this, closing a dirty tab would silently lose edits).
        void IMonacoEditorHost.OnFileState(MonacoEditorControl editor, string rawJson)
        {
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(rawJson) as Dictionary<string, object>;
                if (data == null) return;
                if (data.ContainsKey("text") && data["text"] is string) _overlayLiveText = (string)data["text"];
                if (data.ContainsKey("dirty")) _overlayDirty = Convert.ToBoolean(data["dirty"]);
                EnsureCloseHook();   // the page is live now → the workbench window is realized; safe to subscribe
            }
            catch { }
        }

        // Ctrl+D — open the native structure designer on the WINDOW/REPORT at the caret, in ANY source
        // file (.clw/.inc/.tpl/.tpw). Reuses the embeditor's proven path: parse the structure, hand the
        // text to StructureDesignerService (it spins its own scratch editor), splice merges back into
        // Monaco. The user then saves to disk (Monaco owns the file). The native editor is NOT involved.
        void IMonacoEditorHost.OnOpenDesigner(MonacoEditorControl editor, string rawJson)
        {
            int reqId = 0;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(rawJson) as Dictionary<string, object>;
                if (data == null) return;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                int line = data.ContainsKey("line") ? Convert.ToInt32(data["line"]) : 0;
                string buffer = data.ContainsKey("buffer") ? data["buffer"] as string : null;
                if (string.IsNullOrEmpty(buffer)) { editor.PostResponse(reqId, Refusal("Designer request was malformed.")); return; }

                if (StructureDesignerService.IsActive)
                {
                    StructureDesignerService.ActivateCurrent(_editor);
                    editor.PostResponse(reqId, Refusal("A structure designer is already open — close its tab first."));
                    return;
                }

                var hit = ClarionAppDataReader.FindStructureAtLine(buffer, line);
                if (!hit.Found)
                {
                    // No structure at the caret → CREATE-NEW path (native Ctrl+D parity). On a blank line,
                    // hand Monaco the DEFAULTS.CLW template list so it shows the picker; the chosen entry
                    // comes back via OnOpenDesignerCreate. File mode = the whole buffer is editable, so there
                    // is no embed-slot guard to satisfy — only the "blank line" gesture gates create-new.
                    var blines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                    bool lineBlank = line >= 1 && line <= blines.Length && blines[line - 1].Trim().Length == 0;
                    if (!lineBlank)
                    {
                        editor.PostResponse(reqId, Refusal("Put the caret on a WINDOW/REPORT structure, or on a blank line to create a new one."));
                        return;
                    }
                    var templates = DefaultStructuresReader.Load();
                    if (templates.Count > 0)
                    {
                        var list = new List<object>();
                        foreach (var t in templates)
                            list.Add(new Dictionary<string, object> { { "title", t.Title }, { "type", t.Kind } });
                        editor.PostResponse(reqId, new Dictionary<string, object> { { "ok", true }, { "mode", "pickTemplate" }, { "templates", list } });
                        return;
                    }
                    // No DEFAULTS.CLW found → seed a plain window directly (no picker), mirroring the embeditor.
                    // 'seed' lets the page lay the structure into the buffer immediately (so OK-with-no-edits writes it).
                    editor.PostResponse(reqId, new Dictionary<string, object>
                    {
                        { "ok", true }, { "mode", "insert" }, { "startLine", line }, { "endLine", line }, { "type", "WINDOW" }, { "seed", NewWindowSeed }
                    });
                    OpenDesignerOverlay(NewWindowSeed, "NewWindow", true, true);
                    return;
                }

                var lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                int s = Math.Max(1, hit.StartLine), e = Math.Min(lines.Length, hit.EndLine);
                var sb = new StringBuilder();
                for (int i = s; i <= e; i++) { if (i > s) sb.Append('\n'); sb.Append(lines[i - 1]); }
                string structureText = sb.ToString();
                string label = string.IsNullOrEmpty(hit.Name) ? "CAWindow" : hit.Name;
                bool isWindow = hit.Type == "WINDOW";

                editor.PostResponse(reqId, new Dictionary<string, object>
                {
                    { "ok", true }, { "mode", "edit" },
                    { "startLine", hit.StartLine }, { "endLine", hit.EndLine }, { "type", hit.Type }
                });

                // Run the open OFF this WebView2 message-handler stack (the embeditor's reentrancy rule).
                OpenDesignerOverlay(structureText, label, isWindow, isWindow);
            }
            catch (Exception ex)
            {
                MonacoSpikeLog.Write("overlay openDesigner error: " + ex.Message);
                try { editor.PostResponse(reqId, Refusal("Designer failed: " + ex.Message)); } catch { }
            }
        }

        private static Dictionary<string, object> Refusal(string message)
        {
            return new Dictionary<string, object> { { "ok", false }, { "message", message } };
        }

        // Seed used when DEFAULTS.CLW is missing (no picker possible) — mirrors the embeditor's FallbackSeed.
        private const string NewWindowSeed =
            "NewWindow WINDOW('New Window'),AT(,,200,120),GRAY,SYSTEM\n" +
            "         \n" +
            "       END";

        // Run the designer open OFF this WebView2 message-handler stack (the embeditor's reentrancy rule).
        // Shared by the edit path (OnOpenDesigner) and the create-new path (OnOpenDesignerCreate).
        private void OpenDesignerOverlay(string structureText, string label, bool isWindowDesigner, bool isWindowWindow)
        {
            Action open = () =>
            {
                string err = StructureDesignerService.Open(structureText, label, isWindowDesigner, isWindowWindow, _editor,
                    onBufferChanged: text => _editor.PostDesignerMessage("designerSplice", text, null),
                    onClosed: finalText => _editor.PostDesignerMessage("designerClosed", finalText, null));
                if (err != null) _editor.PostDesignerMessage("designerClosed", null, err);
            };
            try { if (_editor != null && _editor.IsHandleCreated) _editor.BeginInvoke(open); else open(); }
            catch (Exception oex) { MonacoSpikeLog.Write("overlay designer open marshal error: " + oex.Message); }
        }

        // Second leg of create-new (blank-line Ctrl+D): Monaco's picker chose a DEFAULTS.CLW entry. Seed from
        // the chosen block and open the designer with the flags its kind dictates (WINDOW/APPLICATION/REPORT).
        // File mode = whole buffer editable, so no slot re-validation is needed (unlike the embeditor).
        void IMonacoEditorHost.OnOpenDesignerCreate(MonacoEditorControl editor, string rawJson)
        {
            int reqId = 0;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(rawJson) as Dictionary<string, object>;
                if (data == null) return;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                int line = data.ContainsKey("line") ? Convert.ToInt32(data["line"]) : 0;
                string templateTitle = data.ContainsKey("templateTitle") ? data["templateTitle"] as string : null;
                if (string.IsNullOrEmpty(templateTitle)) { editor.PostResponse(reqId, Refusal("Designer request was malformed.")); return; }

                if (StructureDesignerService.IsActive)
                {
                    StructureDesignerService.ActivateCurrent(_editor);
                    editor.PostResponse(reqId, Refusal("A structure designer is already open — close its tab first."));
                    return;
                }

                DefaultStructuresReader.StructureTemplate template = null;
                foreach (var t in DefaultStructuresReader.Load())
                    if (string.Equals(t.Title, templateTitle, StringComparison.Ordinal)) { template = t; break; }
                string structureText = template != null ? template.Source : NewWindowSeed;
                string kind = template != null ? template.Kind : "WINDOW";

                // Scratch tab name = the template block's own label (e.g. Window / ProgressWindow / Report).
                string label = "NewStructure";
                var m = System.Text.RegularExpressions.Regex.Match(structureText, @"^\s*(\w+)");
                if (m.Success) label = m.Groups[1].Value;

                bool isWindowDesigner = kind != "REPORT";
                bool isWindowWindow = kind == "WINDOW";

                // 'seed' = the chosen structure; the page inserts it immediately so OK-with-no-edits still writes it.
                editor.PostResponse(reqId, new Dictionary<string, object>
                {
                    { "ok", true }, { "mode", "insert" }, { "startLine", line }, { "endLine", line }, { "type", kind }, { "seed", structureText }
                });
                OpenDesignerOverlay(structureText, label, isWindowDesigner, isWindowWindow);
            }
            catch (Exception ex)
            {
                MonacoSpikeLog.Write("overlay openDesignerCreate error: " + ex.Message);
                try { editor.PostResponse(reqId, Refusal("Designer failed: " + ex.Message)); } catch { }
            }
        }
        // 'Show designer' on the lock overlay → bring the scratch designer tab back to front.
        void IMonacoEditorHost.OnActivateDesigner(MonacoEditorControl editor) { StructureDesignerService.ActivateCurrent(_editor); }
        void IMonacoEditorHost.OnEditorNavigationCompleted(MonacoEditorControl editor, bool success) { MonacoSpikeLog.Write("overlay nav completed success=" + success); }
        void IMonacoEditorHost.OnUnknownAction(MonacoEditorControl editor, string action, string rawJson)
        {
            if (action != "toggleBreakpoint") return;
            // Gutter click in Monaco → toggle the IDE breakpoint on the native document. The native + CA
            // debuggers both listen to DebuggerService; the BreakPointAdded/Removed event re-pushes the set.
            try
            {
                if (_hostEditor == null || _hostEditor.Document == null || string.IsNullOrEmpty(_filePath)) return;
                var data = new JavaScriptSerializer().DeserializeObject(rawJson) as Dictionary<string, object>;
                int line = (data != null && data.ContainsKey("line")) ? Convert.ToInt32(data["line"]) : 0;
                if (line < 1) return;
                DebuggerService.ToggleBreakpointAt(_hostEditor.Document, _filePath, line - 1);   // ToggleBreakpointAt is 0-based
                MonacoSpikeLog.Write("overlay toggleBreakpoint line=" + line + " file=" + _filePath);
                PushBreakpoints();   // belt-and-suspenders; the event also re-pushes
            }
            catch (Exception ex) { MonacoSpikeLog.Write("overlay toggleBreakpoint error: " + ex.Message); }
        }

        // ── Breakpoints (IDE DebuggerService is the single source of truth) ─────────────────────────
        private EventHandler<BreakpointBookmarkEventArgs> _bpAdded, _bpRemoved;

        private void WireBreakpoints()
        {
            try
            {
                if (_bpAdded != null) return;
                _bpAdded = (s, e) => OnBreakpointChanged(e);
                _bpRemoved = (s, e) => OnBreakpointChanged(e);
                DebuggerService.BreakPointAdded += _bpAdded;
                DebuggerService.BreakPointRemoved += _bpRemoved;
            }
            catch (Exception ex) { MonacoSpikeLog.Write("overlay WireBreakpoints error: " + ex.Message); }
        }

        private void UnwireBreakpoints()
        {
            try
            {
                if (_bpAdded != null) DebuggerService.BreakPointAdded -= _bpAdded;
                if (_bpRemoved != null) DebuggerService.BreakPointRemoved -= _bpRemoved;
            }
            catch { }
            _bpAdded = null; _bpRemoved = null;
        }

        private void OnBreakpointChanged(BreakpointBookmarkEventArgs e)
        {
            try
            {
                var bb = e != null ? e.BreakpointBookmark : null;
                if (bb != null && !string.IsNullOrEmpty(bb.FileName) &&
                    string.Equals(bb.FileName, _filePath, StringComparison.OrdinalIgnoreCase))
                    PushBreakpoints();
            }
            catch { }
        }

        /// <summary>Push the IDE's current breakpoint lines for THIS file to Monaco (gutter red dots).</summary>
        private void PushBreakpoints()
        {
            try
            {
                if (_editor == null || string.IsNullOrEmpty(_filePath)) return;
                var sb = new StringBuilder();
                int n = 0;
                foreach (var bb in DebuggerService.Breakpoints)
                {
                    if (bb == null || string.IsNullOrEmpty(bb.FileName)) continue;
                    if (!string.Equals(bb.FileName, _filePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append(bb.LineNumber + 1);   // bookmark 0-based → Monaco 1-based
                }
                _editor.PostJson("{\"type\":\"setBreakpoints\",\"lines\":[" + sb + "]}");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("overlay PushBreakpoints error: " + ex.Message); }
        }

        // CRLF-normalize + write the buffer to disk with the file's load encoding. Shared by the
        // interactive save (OnSave) and the close-save prompt. Returns the char count written.
        private int WriteToDisk(string text)
        {
            string normalized = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            Encoding enc = null;
            try { enc = _hostEditor != null ? _hostEditor.Encoding : null; } catch { }
            File.WriteAllText(_filePath, normalized, enc ?? Encoding.UTF8);
            return normalized.Length;
        }

        private static void EnsureLsp()
        {
            try { if (!SharedLspBridge.IsRunning) EmbeditorCompletionService.LspStarter?.Invoke(); }
            catch { }
        }

        private static bool ParseLspRequest(string json, out int reqId, out int line, out int column, out string buffer)
        {
            reqId = 0; line = 0; column = 0; buffer = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("line")) line = Convert.ToInt32(data["line"]);
                if (data.ContainsKey("column")) column = Convert.ToInt32(data["column"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                return true;
            }
            catch { return false; }
        }

        private static bool ParseDiagRequest(string json, out int reqId, out string buffer, out List<int[]> ranges)
        {
            reqId = 0; buffer = null; ranges = new List<int[]>();
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                var arr = data.ContainsKey("ranges") ? data["ranges"] as object[] : null;
                if (arr != null)
                    foreach (var item in arr)
                    {
                        var pair = item as object[];
                        if (pair != null && pair.Length >= 2)
                            ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) });
                    }
                return true;
            }
            catch { return false; }
        }

        private static string ExtractHover(Dictionary<string, object> resp)
        {
            if (resp == null) return null;
            object result = resp.ContainsKey("result") ? resp["result"] : null;
            var rd = result as Dictionary<string, object>;
            object contents = (rd != null && rd.ContainsKey("contents")) ? rd["contents"] : result;
            return HoverPart(contents);
        }

        private static string HoverPart(object contents)
        {
            if (contents == null) return null;
            var s = contents as string;
            if (s != null) return s;
            var d = contents as Dictionary<string, object>;
            if (d != null && d.ContainsKey("value")) return d["value"] as string;
            var list = contents as System.Collections.IEnumerable;
            if (list != null)
            {
                var sb = new StringBuilder();
                foreach (var part in list)
                {
                    string p = HoverPart(part);
                    if (!string.IsNullOrEmpty(p)) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(p); }
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            return null;
        }

        private void DisposeOverlay()
        {
            try
            {
                if (_editor != null)
                {
                    var parent = _editor.Parent;
                    if (parent != null) parent.Controls.Remove(_editor);
                    _editor.Dispose();
                    _editor = null;
                }
                if (_cover != null)
                {
                    var cp = _cover.Parent;
                    if (cp != null) cp.Controls.Remove(_cover);
                    _cover.Dispose();
                    _cover = null;
                }
            }
            catch { }
        }

        private void StopCaptureTimer()
        {
            try
            {
                if (_captureTimer != null)
                {
                    _captureTimer.Stop();
                    _captureTimer.Tick -= CaptureTick;
                    _captureTimer.Dispose();
                    _captureTimer = null;
                }
            }
            catch { }
        }

        // Subscribe ONCE to the workbench tab's cancellable FormClosing so the save prompt runs before the
        // close commits (single-click close). WorkbenchWindow's runtime type is SdiWorkspaceWindow : Form;
        // reflect it (not worth a hard compile dependency on the IDE's view-content base) and cast to Form.
        // Self-healing: if the window isn't realized yet, we stay unhooked and retry on the next file-state.
        private void EnsureCloseHook()
        {
            if (_closeHooked) return;
            try
            {
                object wbw = null;
                try { wbw = GetType().GetProperty("WorkbenchWindow")?.GetValue(this, null); } catch { }
                _wbForm = wbw as Form;
                if (_wbForm != null)
                {
                    _wbForm.FormClosing += OnWorkbenchFormClosing;
                    _closeHooked = true;
                    MonacoSpikeLog.Write("close-hook attached to " + _wbForm.GetType().FullName);
                }
            }
            catch (Exception ex) { MonacoSpikeLog.Write("close-hook attach error: " + ex.Message); }
        }

        // Cancellable save-on-close: Yes = save + close, No = close without saving, Cancel = stay open. Runs
        // BEFORE Dispose, so the tab closes in one click. A decision clears _overlayDirty so the Dispose
        // safety-net won't re-write. During IDE/Windows shutdown we must NOT pop a modal (it can hang the
        // shutdown — see project_shutdown_hang); leave the buffer dirty so Dispose saves it silently instead.
        private void OnWorkbenchFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                if (e.Cancel) return;
                if (!(_overlayDirty && _overlayLiveText != null && !string.IsNullOrEmpty(_filePath))) return;
                if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall
                    || e.CloseReason == CloseReason.TaskManagerClosing)
                    return;   // shutdown → no modal; Dispose's silent fallback persists the edits

                var r = MessageBox.Show(_wbForm,
                    "Save changes to " + Path.GetFileName(_filePath) + " before closing?",
                    "CA Editor — unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes)
                {
                    int n = WriteToDisk(_overlayLiveText);
                    MonacoSpikeLog.Write("overlay close-save wrote: " + _filePath + " (" + n + " chars)");
                }
                _overlayDirty = false;   // decided (saved or discarded) — Dispose must not re-save
            }
            catch (Exception ex) { MonacoSpikeLog.Write("overlay FormClosing save error: " + ex.Message); }
        }

        public override void Dispose()
        {
            StopCaptureTimer();
            UnwireBreakpoints();
            try { MonacoSourceNavigator.Unregister(_filePath, this); } catch { }
            try { if (_wbForm != null) { _wbForm.FormClosing -= OnWorkbenchFormClosing; _wbForm = null; } } catch { }

            // The interactive save prompt lives in OnWorkbenchFormClosing (cancellable, pre-teardown). This is
            // only a SILENT safety net for disposal paths that bypass FormClosing (solution close / shutdown):
            // if edits are still pending, write them rather than lose them — NO modal here (a MessageBox in
            // Dispose pumps a nested loop that interrupts the close, which is the double-close bug we fixed).
            bool saveFallback = _overlayDirty && _overlayLiveText != null && !string.IsNullOrEmpty(_filePath);
            string toSave = _overlayLiveText;

            DisposeOverlay();

            if (saveFallback)
            {
                try { int n = WriteToDisk(toSave); MonacoSpikeLog.Write("overlay close-save (fallback) wrote: " + _filePath + " (" + n + " chars)"); }
                catch (Exception ex) { MonacoSpikeLog.Write("overlay close-save fallback error: " + ex.Message); }
            }

            base.Dispose();
        }
    }

    /// <summary>
    /// DisplayBinding that constructs <see cref="MonacoClarionEditor"/> in place of the stock
    /// ClarionEditor. Inherits the entire <see cref="ClarionEditorDisplayBinding"/> behavior and
    /// overrides ONLY the editor factory. Registered with insertbefore="ClarionWinEditor".
    /// </summary>
    public class MonacoClarionEditorDisplayBinding : ClarionEditorDisplayBinding
    {
        protected override CommonClarionEditor CreateClarionEditor()
        {
            MonacoSpikeLog.Write("CreateClarionEditor -> MonacoClarionEditor (our DisplayBinding won)");
            return new MonacoClarionEditor();
        }
    }

    /// <summary>
    /// Persisted on/off switch for the Monaco source-editor overlay. Backed by a tiny flag file so
    /// it survives restarts and can be toggled at runtime (next file-open picks it up) without a
    /// rebuild/redeploy. OFF by default — Phase 0/foundation ship safely as the stock editor.
    /// </summary>
    public static class MonacoSourceOverlay
    {
        private static string FlagPath
        {
            get { return Path.Combine(MonacoSpikeLog.DataDir, "monaco-overlay.enabled"); }
        }

        /// <summary>Read fresh each call so a toggle takes effect on the next opened file.</summary>
        public static bool Enabled
        {
            get
            {
                try { return File.Exists(FlagPath) && File.ReadAllText(FlagPath).Trim() == "1"; }
                catch { return false; }
            }
        }

        public static bool Toggle()
        {
            bool next = !Enabled;
            try
            {
                MonacoSpikeLog.EnsureDir();
                File.WriteAllText(FlagPath, next ? "1" : "0");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("toggle write error: " + ex.Message); }
            return next;
        }
    }

    /// <summary>Tiny append logger to our own file — ICSharpCode LoggingService is NOT routed to a
    /// discoverable file on this install (see gotcha memory), so spike markers go here.</summary>
    public static class MonacoSpikeLog
    {
        public static string DataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClarionAssistant");
            }
        }

        public static void EnsureDir()
        {
            try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); } catch { }
        }

        public static void Write(string message)
        {
            try
            {
                EnsureDir();
                File.AppendAllText(
                    Path.Combine(DataDir, "monaco-spike.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// Tools-menu toggle for the experimental Monaco source-editor overlay (Phase 1). Flips the
    /// persisted flag; the change applies to the NEXT source file opened.
    /// </summary>
    public class ToggleMonacoSourceOverlayCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            bool nowOn = MonacoSourceOverlay.Toggle();
            MonacoSpikeLog.Write("overlay toggled -> " + (nowOn ? "ON" : "OFF"));
            MessageBox.Show(
                "Monaco source-editor overlay is now " + (nowOn ? "ON" : "OFF") + ".\n\n" +
                "Close and reopen a Clarion source file (.clw/.inc/...) for the change to take effect.",
                "CA Monaco Overlay (experimental)",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
