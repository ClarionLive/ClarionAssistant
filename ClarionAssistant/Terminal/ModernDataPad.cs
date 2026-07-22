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
        // Live-instance registry so ShutdownService can dispose this pad's WebView2 ON THE UI THREAD
        // BEFORE native IDE teardown (the WebView2 <-> native focus-deadlock precedent). The SharpDevelop
        // pad lifecycle alone doesn't guarantee Dispose runs before native teardown, so we mirror the
        // ModernEmbeditorViewContent pattern. (Practically a singleton pad, but a List handles any count.)
        private static readonly List<ModernDataPad> _instances = new List<ModernDataPad>();

        private Panel _panel;
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;

        // ed2ccb84: a Copy/Ctrl+C on a Local/Global variable stashes its identity here (process-wide) so a
        // subsequent Paste/Ctrl+V can do a FULL-PROPERTY clone (source.Copy) instead of a name-text paste —
        // matching the native data pad. Validated at paste time against the live clipboard text (the copied name)
        // so an unrelated clipboard change falls back to the normal declaration-text paste.
        private sealed class PendingVarCopy { public string Scope; public string Name; public string Proc; }
        private static PendingVarCopy _pendingVarCopy;

        // ed2ccb84: a Copy/Ctrl+C on a dictionary COLUMN (a Declared Tables / Other Files row) stashes the column(s)
        // here (process-wide) so a subsequent Paste/Ctrl+V into a Local/Global section does a LOSSLESS field clone
        // (the same CopyColumns flow a column drag uses) instead of a name-text paste. ColsJson is the
        // [{table,column},...] payload; Ref is the clipboard text set at copy time (the prefixed reference, e.g.
        // aut:Phone), validated at paste so an unrelated clipboard change falls back to the normal text paste.
        // Mutually exclusive with _pendingVarCopy — copying one clears the other.
        private sealed class PendingColCopy { public string ColsJson; public string Ref; }
        private static PendingColCopy _pendingColCopy;

        public ModernDataPad()
        {
            lock (_instances) { _instances.Add(this); }
        }

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

                // Drag-drop: WebView2 hides a dropped file's real path from the page (File.path is empty), AND an
                // external OLE drop does NOT bubble through WebView2's Chromium child HWNDs to the parent panel —
                // so neither the page nor a panel-level handler can read dropped paths (a panel handler just yields
                // a no-drop cursor). The fix is a real WinForms control that owns its OWN region: _dropStrip, docked
                // at the bottom of the Files tab, ON TOP of the WebView2. It receives DataFormats.FileDrop with the
                // real full paths. Page external-drop stays ON for the Data tab's variable-declaration text-drop and
                // is turned OFF on the Files tab (see SetFilesDropMode) so the page area shows no-drop, steering the
                // user to the strip. Default = Data tab.
                _webView.AllowExternalDrop = true;
                _dropStrip = new Label
                {
                    Dock = DockStyle.Bottom,
                    Height = 56,
                    AllowDrop = true,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = "⬇  Drop files here to open them in Monaco",
                    Font = new Font("Segoe UI", 9F),
                    Visible = false
                };
                _dropStrip.DragEnter += OnDropStripDragEnter;
                _dropStrip.DragOver += OnDropStripDragEnter;
                _dropStrip.DragLeave += (s2, e2) => ApplyDropStripTheme(false);
                _dropStrip.DragDrop += OnDropStripDragDrop;
                _panel.Controls.Add(_dropStrip);
                _dropStrip.BringToFront();
                // Layout: strip is Dock=Bottom, WebView2 is Dock=Fill. BringToFront() puts the strip ahead of the
                // WebView2 in the z-order so it reserves its bottom edge first and the WebView2 fills the remainder —
                // the two never overlap. SetFilesDropMode re-asserts BringToFront() on EVERY Files-tab activation, so
                // the layout is deterministic across fresh / restored / floating dock states (re-applied each time the
                // strip is shown), not dependent on Controls.Add order. Validated live in C12.
                ApplyDropStripTheme(false);

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
                // Bound an oversized incoming message before any scanning — defense-in-depth against a huge dropped
                // payload (the page already caps drops at 256KB; this guards the host side too). Real pad messages
                // are small; the only large one is a paste, capped well under this.
                if (json != null && json.Length > 1024 * 1024) return;
                string action = ExtractJsonValue(json, "action");
                if (action == "ready")
                {
                    _isInitialized = true;
                    PostRestoreUiState(); // send saved collapse/expand state BEFORE the first data render
                    SniffTheme(Terminal.ModernDataPadState.Load()); // learn dark/light for files opened from the Files tab
                    PostVersionInfo(); // populate the version/solution banner under the title
                    Refresh();
                }
                else if (action == "saveUiState")
                {
                    // Opaque JSON blob owned by the pad's JS (sectionCollapsed/localCollapsed/relExpanded/detailOpen).
                    string state = ExtractJsonValue(json, "state");
                    Terminal.ModernDataPadState.Save(state);
                    SniffTheme(state); // keep _isDark in sync when the developer toggles the pad theme
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
                else if (action == "pasteVariables")
                {
                    // 📋 Paste / declaration-text drop on a Local/Global section: parse the Clarion declaration
                    // text and create the field(s) WITHOUT the native FieldForm. 'text' is present for a DROP
                    // (the dropped declaration text); ABSENT for the Paste button / Ctrl+V, in which case the
                    // HOST reads the Windows clipboard (we're on the STA UI thread inside RunVariableCrud).
                    // showSuccess: there's no native form here, so the added/skipped summary is the only feedback.
                    string scope = ExtractJsonValue(json, "scope");
                    string clickedProc = ExtractJsonValue(json, "procedure");
                    string droppedText = ExtractJsonValue(json, "text");
                    // FULL-PROPERTY clone path: a prior Copy/Ctrl+C stashed a source variable AND it's still the
                    // active clipboard (clip text == the copied name) → clone the whole field (every property),
                    // not a text declaration. Any other clipboard state falls through to the text paste below.
                    var pend = _pendingVarCopy;
                    var pendCol = _pendingColCopy;
                    bool fullClone = false, colClone = false;
                    if (string.IsNullOrEmpty(droppedText))
                    {
                        string clip = null;
                        try { clip = System.Windows.Forms.Clipboard.GetText(); }
                        catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] paste clip check: " + cex.Message); }
                        // VARIABLE full-property clone wins if its stash is still the active clipboard; else a COLUMN
                        // clone if ITS stash matches. Either falls through to the text paste when the clipboard moved on.
                        if (pend != null && !string.IsNullOrEmpty(pend.Name) && string.Equals(clip, pend.Name, StringComparison.Ordinal))
                            fullClone = true;
                        else if (pendCol != null && !string.IsNullOrEmpty(pendCol.ColsJson) && string.Equals(clip, pendCol.Ref, StringComparison.Ordinal))
                            colClone = true;
                    }
                    if (fullClone)
                    {
                        var p = pend;
                        RunVariableCrud("Copy Variable",
                            () => Services.FileSchemaVariableInserter.CopyVariableToScope(scope, p.Scope, p.Name, p.Proc, clickedProc),
                            showSuccessInfo: true);
                    }
                    else if (colClone)
                    {
                        var pc = pendCol;
                        RunVariableCrud("Copy Column",
                            () => CopyColumns(scope, pc.ColsJson, clickedProc),
                            showSuccessInfo: true);
                    }
                    else
                    {
                        RunVariableCrud("Paste Variables", () =>
                        {
                            string decl = droppedText;
                            if (string.IsNullOrEmpty(decl))
                            {
                                try { decl = System.Windows.Forms.Clipboard.GetText(); }
                                catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] clipboard read: " + cex.Message); decl = null; }
                            }
                            return Services.FileSchemaVariableInserter.PasteVariableDefinitions(scope, decl, clickedProc);
                        }, showSuccessInfo: true);
                    }
                }
                else if (action == "copyColumns")
                {
                    // In-pad column drag dropped on a Local/Global section: lossless copy of one or more existing
                    // dictionary columns into Local/Global data (true DDField copy via Clarion's native paste flow).
                    // 'cols' is a JSON array string [{table,column},...] (supports a multi-row column drag); the
                    // whole loop runs inside ONE RunVariableCrud op so single-flight doesn't drop later columns.
                    string scope = ExtractJsonValue(json, "scope");
                    string clickedProc = ExtractJsonValue(json, "procedure");
                    string colsJson = ExtractJsonValue(json, "cols");
                    if (!string.IsNullOrEmpty(colsJson))
                        RunVariableCrud("Copy Column", () => CopyColumns(scope, colsJson, clickedProc), showSuccessInfo: true);
                }
                else if (action == "copyText")
                {
                    // "Copy Details" — put plain "NAME   TYPE" text on the OS clipboard (for pasting a declaration
                    // into code/Monaco). WinForms Clipboard = the same clipboard Clarion uses. A pure-text copy
                    // CLEARS any pending full-property var stash so a later Paste doesn't wrongly full-clone.
                    string text = ExtractJsonValue(json, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        _pendingVarCopy = null; _pendingColCopy = null;
                        try { System.Windows.Forms.Clipboard.SetText(text); }
                        catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] copyText clipboard: " + cex.Message); }
                    }
                }
                else if (action == "copyColumn")
                {
                    // "Copy" / Ctrl+C on a dictionary COLUMN row: put the prefixed reference (e.g. aut:Phone) on the
                    // clipboard (so pasting into code gives the compilable reference) AND stash the column(s) so a
                    // subsequent Paste/Ctrl+V into a Local/Global section does a LOSSLESS field clone (CopyColumns) —
                    // the column analog of copyVar's full-property variable clone.
                    string cols = ExtractJsonValue(json, "cols");
                    string refName = ExtractJsonValue(json, "name");
                    if (!string.IsNullOrEmpty(cols))
                    {
                        _pendingVarCopy = null;
                        _pendingColCopy = new PendingColCopy { ColsJson = cols, Ref = refName };
                        if (!string.IsNullOrEmpty(refName))
                        {
                            try { System.Windows.Forms.Clipboard.SetText(refName); }
                            catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] copyColumn clipboard: " + cex.Message); }
                        }
                    }
                }
                else if (action == "copyVar")
                {
                    // "Copy" / Ctrl+C on a Local/Global variable: put the NAME on the clipboard (so pasting into
                    // code gives the reference) AND stash the source identity so a subsequent Paste into a data
                    // section does a FULL-PROPERTY clone (the native data pad's Ctrl+C/Ctrl+V behavior).
                    string scope = ExtractJsonValue(json, "scope");
                    string name = ExtractJsonValue(json, "name");
                    string proc = ExtractJsonValue(json, "procedure");
                    if (!string.IsNullOrEmpty(name))
                    {
                        _pendingColCopy = null;
                        _pendingVarCopy = new PendingVarCopy { Scope = scope, Name = name, Proc = proc };
                        try { System.Windows.Forms.Clipboard.SetText(name); }
                        catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] copyVar clipboard: " + cex.Message); }
                    }
                }
                else if (action == "beginFieldDrag")
                {
                    // Ticket ed2ccb84 / 0bada8de: a field/column row drag started (host-owned). The page suppressed
                    // its own HTML5/WebView2 OLE drag and posted this on a small pointer-move with the button still
                    // down; FieldDropService then tracks the cursor (WH_MOUSE_LL, NO OLE payload) until release and
                    // hit-tests the point. On release: DESIGNER creates the bound control natively; MONACO inserts
                    // the plain reference at the CA-embeditor's cursor; PAD is posted back to THIS page to hit-test
                    // the section + run the in-pad copy; EDITOR (native Clarion editor / standalone Monaco / any
                    // other active editor) is inserted here at the active editor's caret (the double-click path) so
                    // dropping a field into code yields ONLY the reference, never the whole field. We're on the UI
                    // thread (WebMessageReceived) with the button down — call synchronously inside the live gesture.
                    string fKind = ExtractJsonValue(json, "kind");
                    string fName = ExtractJsonValue(json, "name");
                    string fType = ExtractJsonValue(json, "ftype");
                    string fPic = ExtractJsonValue(json, "picture");
                    // table (column drags) + scope (var drags) + procedure let the drop resolve the LIVE,
                    // parented DDField (real identity) instead of a forged transient one — the live-field fix.
                    string fTable = ExtractJsonValue(json, "table");
                    string fScope = ExtractJsonValue(json, "scope");
                    string fProc = ExtractJsonValue(json, "procedure");
                    // text = the plain reference (parity with the old HTML5 text drop) inserted on a Monaco
                    // release; cols = the in-pad column JSON the page re-uses for a PAD release.
                    string fText = ExtractJsonValue(json, "text");
                    string fCols = ExtractJsonValue(json, "cols");
                    var drop = Services.FieldDropService.DoFieldDrag(fKind, fName, fType, fPic, fTable, fScope, fProc, _panel, _webView, fText);
                    // DESIGNER/MONACO/EDITOR are handled inside DoFieldDrag (EDITOR inserts the reference straight
                    // into the text area under the cursor — including a Monaco-default-editor the pad's _renderCtx
                    // resolver doesn't recognize). Only PAD needs the page round-trip to hit-test its own section.
                    if (drop.Target == Services.FieldDropTarget.Pad)
                        PostHostFieldDrop(drop.ScreenX, drop.ScreenY, fCols, fText);
                }
                // ===== Explorer "Files" tab — Monaco file loader (all routed through MonacoFileOpener) =====
                else if (action == "tabChanged")
                {
                    // Switch host-vs-page drop ownership with the active tab (see SetFilesDropMode). On the Files
                    // tab the host handles file drops to recover real paths; on Data the page keeps its text-drop.
                    string tab = ExtractJsonValue(json, "tab");
                    bool filesActive = string.Equals(tab, "files", StringComparison.OrdinalIgnoreCase);
                    if (_panel != null) _panel.BeginInvoke((Action)(() => SetFilesDropMode(filesActive)));
                    else SetFilesDropMode(filesActive);
                }
                else if (action == "requestExplorerData")
                {
                    // The Files tab asks for its data on first activation (and we re-post after each mutation).
                    PostExplorerData();
                }
                else if (action == "loadFile")
                {
                    string path = ExtractJsonValue(json, "path");
                    if (!string.IsNullOrEmpty(path))
                        DeferExplorer(() => { Services.MonacoFileOpener.OpenFile(path, _isDark); PostExplorerData(); });
                }
                else if (action == "loadFileDialog")
                {
                    // "Load File…" button: pick file(s) starting in the sticky last folder, open each.
                    DeferExplorer(() =>
                    {
                        var files = PickFiles(true, Services.ExplorerRecentsStore.GetLastFolder());
                        if (files != null) { foreach (var f in files) Services.MonacoFileOpener.OpenFile(f, _isDark); PostExplorerData(); }
                    });
                }
                else if (action == "openClassPair")
                {
                    // Open BOTH sides of a class (.inc + .clw) into two separate Monaco editors. If both paths
                    // are stale (moved/deleted) nothing opens — tell the developer instead of a silent no-op.
                    string inc = ExtractJsonValue(json, "inc");
                    string clw = ExtractJsonValue(json, "clw");
                    if (!string.IsNullOrEmpty(inc) || !string.IsNullOrEmpty(clw))
                        DeferExplorer(() =>
                        {
                            int opened = Services.MonacoFileOpener.OpenClassPair(inc, clw, _isDark);
                            if (opened == 0)
                            {
                                try { MessageBox.Show("Neither side of this class is on disk anymore (it may have been moved or deleted).",
                                    "Open Class", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                                catch { }
                            }
                            PostExplorerData();
                        });
                }
                else if (action == "compare")
                {
                    // Diff two files in Monaco. 'a'/'b' may be supplied (row Compare) or omitted (toolbar) —
                    // prompt for whichever side is missing, seeding the picker near the known file/last folder.
                    string a = ExtractJsonValue(json, "a");
                    string b = ExtractJsonValue(json, "b");
                    DeferExplorer(() =>
                    {
                        string fa = a, fb = b;
                        if (string.IsNullOrEmpty(fa)) fa = PickSingle("Compare — pick the first file", Services.ExplorerRecentsStore.GetLastFolder());
                        if (string.IsNullOrEmpty(fa)) return;
                        if (string.IsNullOrEmpty(fb)) fb = PickSingle("Compare — pick the second file", Path.GetDirectoryName(fa));
                        if (string.IsNullOrEmpty(fb)) return;
                        Services.MonacoFileOpener.Compare(fa, fb, _isDark);
                    });
                }
                else if (action == "pickFolderAndLoad")
                {
                    // Quick-location / pinned-folder chip: open the file picker rooted at that folder.
                    string dir = ExtractJsonValue(json, "dir");
                    DeferExplorer(() =>
                    {
                        var files = PickFiles(true, dir);
                        if (files != null) { foreach (var f in files) Services.MonacoFileOpener.OpenFile(f, _isDark); PostExplorerData(); }
                    });
                }
                else if (action == "setLastFolder")
                {
                    // "change" link on the last-folder strip: pick a new sticky default folder.
                    DeferExplorer(() =>
                    {
                        string dir = PickFolder("Set the Explorer's default folder", Services.ExplorerRecentsStore.GetLastFolder());
                        if (!string.IsNullOrEmpty(dir)) { Services.ExplorerRecentsStore.SetLastFolder(dir); PostExplorerData(); }
                    });
                }
                else if (action == "reveal")
                {
                    string path = ExtractJsonValue(json, "path");
                    if (!string.IsNullOrEmpty(path)) Services.MonacoFileOpener.RevealInExplorer(path);
                }
                else if (action == "copyPath")
                {
                    // On the STA UI thread here — same guarded clipboard set as 'insert'.
                    string path = ExtractJsonValue(json, "path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { System.Windows.Forms.Clipboard.SetText(path); }
                        catch (Exception cex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] copyPath clipboard: " + cex.Message); }
                    }
                }
                else if (action == "pin")
                {
                    string path = ExtractJsonValue(json, "path");
                    if (!string.IsNullOrEmpty(path)) { Services.ExplorerRecentsStore.Pin(path); PostExplorerData(); }
                }
                else if (action == "unpin")
                {
                    string path = ExtractJsonValue(json, "path");
                    if (!string.IsNullOrEmpty(path)) { Services.ExplorerRecentsStore.Unpin(path); PostExplorerData(); }
                }
                else if (action == "pinFolder")
                {
                    DeferExplorer(() =>
                    {
                        string dir = PickFolder("Pin a folder to Quick locations", Services.ExplorerRecentsStore.GetLastFolder());
                        if (!string.IsNullOrEmpty(dir)) { Services.ExplorerRecentsStore.PinFolder(dir); PostExplorerData(); }
                    });
                }
                else if (action == "unpinFolder")
                {
                    string dir = ExtractJsonValue(json, "dir");
                    if (!string.IsNullOrEmpty(dir)) { Services.ExplorerRecentsStore.UnpinFolder(dir); PostExplorerData(); }
                }
                else if (action == "removeRecent")
                {
                    // A class row removes BOTH sides (.inc + .clw): removing only one leaves the partner recent,
                    // which re-folds the class row back on the next render (remove would silently fail). path2 is
                    // the optional second side, sent by class rows.
                    string path = ExtractJsonValue(json, "path");
                    string path2 = ExtractJsonValue(json, "path2");
                    bool any = false;
                    if (!string.IsNullOrEmpty(path)) { Services.ExplorerRecentsStore.RemoveRecent(path); any = true; }
                    if (!string.IsNullOrEmpty(path2)) { Services.ExplorerRecentsStore.RemoveRecent(path2); any = true; }
                    if (any) PostExplorerData();
                }
                else if (action == "dropFiles")
                {
                    // Files dragged from Windows Explorer. The page sends whatever it can — in WebView2 a real
                    // path is often NOT exposed, so a bare filename or arbitrary text/plain can arrive here.
                    // Validate hard before opening: rooted (absolute), NOT a UNC share (a hostile \\host\share
                    // path would force outbound SMB auth), and an allowed source extension. OpenFile re-checks
                    // File.Exists, so a bare name / missing path is dropped rather than opened as a broken tab.
                    var paths = ExtractStringArray(json, "paths", 50);
                    var safe = new List<string>();
                    foreach (var f in paths)
                    {
                        if (string.IsNullOrEmpty(f)) continue;
                        bool rooted; try { rooted = Path.IsPathRooted(f); } catch { rooted = false; }
                        if (!rooted || IsUncPath(f)) continue;
                        if (!Services.MonacoFileOpener.IsAllowedDropExtension(f)) continue;
                        safe.Add(f);
                    }
                    if (safe.Count > 0)
                        DeferExplorer(() => { foreach (var f in safe) Services.MonacoFileOpener.OpenFile(f, _isDark); PostExplorerData(); });
                }
                else if (action == "saveExplorerUiState")
                {
                    // Collapsed-group state for the Files tab; kept so a re-post (after a pin/open) doesn't expand
                    // everything. Echoed back in PostExplorerData's uiState. (Session-scoped; persistence is polish.)
                    _explorerCollapsed = ParseCollapsed(json);
                }
                else if (action == "requestRedIndex")
                {
                    // "Open File via Redirection" type-ahead asks for its file index. We enumerate the active .red
                    // ONCE off the UI thread (below); the page then filters that index in-memory per keystroke with
                    // zero disk I/O — that's what eliminates the native dialog's fast-typing freeze.
                    RequestRedIndex();
                }
                else if (action == "traceRedFile")
                {
                    // "Trace" button: walk the .red sections/patterns for a complete filename and log each step.
                    // Cheap (a handful of File.Exists checks), so it runs synchronously on the UI thread.
                    string name = ExtractJsonValue(json, "name");
                    TraceRedFile(name);
                }
                // NOTE: opening a redirection match reuses the existing "loadFile" action above (it already opens via
                // MonacoFileOpener + records recents), so no separate openRedFile case is needed.
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
        private void RunVariableCrud(string title, Func<Services.FileSchemaVariableInserter.Result> op, bool showSuccessInfo = false)
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
                    // not found, can't-delete). On success Clarion's own form/confirm is normally the feedback —
                    // but the form-less paste/copy path has no native dialog, so showSuccessInfo surfaces its
                    // added/skipped summary (and Clarion's own form still appears for native Add/Edit/Delete).
                    if (r != null && !r.Ok)
                    {
                        try { MessageBox.Show(r.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
                        catch { }
                    }
                    else if (r != null && r.Ok && r.Committed && showSuccessInfo && !string.IsNullOrEmpty(r.Message))
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
        /// Parse the JSON array of {table,column} and copy them into the scope in ONE ordered inserter call
        /// (CopyColumnsToScope resolves the add target once and pastes in drag order). UI thread, inside one
        /// RunVariableCrud op.
        /// </summary>
        private static Services.FileSchemaVariableInserter.Result CopyColumns(string scope, string colsJson, string clickedProc)
        {
            // Bound the untrusted drag payload before deserializing (a hostile/oversized application/x-ca-column
            // could otherwise allocate hugely on the UI thread). The per-op column-count cap lives in the inserter.
            if (string.IsNullOrEmpty(colsJson) || colsJson.Length > 256 * 1024)
                return Services.FileSchemaVariableInserter.Result.Fail("No columns to copy (or the drop payload was too large).");

            System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> cols = null;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 };
                cols = ser.Deserialize<System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>>(colsJson);
            }
            catch { }
            if (cols == null || cols.Count == 0)
                return Services.FileSchemaVariableInserter.Result.Fail("No columns to copy.");

            var list = new System.Collections.Generic.List<string[]>();
            foreach (var c in cols)
            {
                object t, col;
                c.TryGetValue("table", out t);
                c.TryGetValue("column", out col);
                list.Add(new[] { t as string, col as string });
            }
            return Services.FileSchemaVariableInserter.CopyColumnsToScope(scope, list, clickedProc);
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

        // ===================== Explorer "Files" tab host support =====================

        // The pad's current theme, sniffed from the saved/just-saved UI-state blob ("dark":bool). Files opened
        // from the Files tab match the pad's light/dark mode. Defaults to light.
        private bool _isDark;

        // Collapsed Files-tab group keys, echoed back in setExplorerData.uiState so a re-post (after a pin/open)
        // doesn't expand everything. Session-scoped; cross-session persistence is a polish item.
        private List<string> _explorerCollapsed = new List<string>();

        // Single source of truth for the open-file dialog filter (shared with OpenSourceFileInCaEditorCommand).
        private static readonly string ExplorerOpenFilter = Services.MonacoFileOpener.OpenFileFilter;

        /// <summary>True for a UNC path (\\host\share\...). Used to reject UNC drops so a hostile share path
        /// can't force outbound SMB authentication.</summary>
        private static bool IsUncPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2) return false;
            char a = path[0], b = path[1];
            if ((a == '\\' || a == '/') && (b == '\\' || b == '/')) return true;
            try { return new Uri(path).IsUnc; } catch { return false; }
        }

        // True while the Files tab is showing — gates the native drop strip's visibility / page external-drop.
        private bool _filesTabActive;

        // The native WinForms drop target for the Files tab. Docked at the bottom of _panel, ON TOP of the WebView2
        // (in its own region), so it receives external OLE file drops with REAL full paths — which the page and a
        // panel-level handler cannot (WebView2's Chromium child HWNDs swallow the drop and it doesn't bubble up).
        private Label _dropStrip;

        /// <summary>
        /// Switch drop ownership with the active tab. On the FILES tab the native _dropStrip is shown (it reads the
        /// real DataFormats.FileDrop paths) and the page's external-drop is turned OFF so the WebView2 area shows a
        /// no-drop cursor, steering the user to the strip. On the DATA tab the strip is hidden and the page keeps
        /// AllowExternalDrop so its variable-declaration text-drop (which needs in-page element targeting) works. UI thread.
        /// </summary>
        private void SetFilesDropMode(bool filesActive)
        {
            _filesTabActive = filesActive;
            try
            {
                if (_webView != null) _webView.AllowExternalDrop = !filesActive; // page text-drop on Data; off on Files
                if (_dropStrip != null)
                {
                    _dropStrip.Visible = filesActive;
                    if (filesActive) _dropStrip.BringToFront();
                    ApplyDropStripTheme(false);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] SetFilesDropMode: " + ex.Message); }
        }

        /// <summary>Paint the drop strip for the current theme; <paramref name="active"/> = a valid drag is hovering.</summary>
        private void ApplyDropStripTheme(bool active)
        {
            if (_dropStrip == null) return;
            try
            {
                if (_isDark)
                {
                    _dropStrip.BackColor = active ? Color.FromArgb(40, 60, 90) : Color.FromArgb(37, 37, 53);
                    _dropStrip.ForeColor = active ? Color.FromArgb(137, 180, 250) : Color.FromArgb(150, 150, 170);
                }
                else
                {
                    _dropStrip.BackColor = active ? Color.FromArgb(225, 238, 252) : Color.FromArgb(244, 246, 248);
                    _dropStrip.ForeColor = active ? Color.FromArgb(10, 93, 194) : Color.FromArgb(90, 90, 100);
                }
            }
            catch { }
        }

        /// <summary>Allow the Copy drop effect for a file drop; highlight the strip. Else reject.</summary>
        private void OnDropStripDragEnter(object sender, DragEventArgs e)
        {
            bool ok = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            ApplyDropStripTheme(ok);
        }

        /// <summary>
        /// Native file drop (Files tab): read the REAL full paths from DataFormats.FileDrop, validate the same way as
        /// the JS drop path (rooted, non-UNC, allowed source extension), then open each via the choke point.
        /// </summary>
        private void OnDropStripDragDrop(object sender, DragEventArgs e)
        {
            ApplyDropStripTheme(false);
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files;
            try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; }
            catch { files = null; }
            if (files == null || files.Length == 0) return;

            var safe = new List<string>();
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f)) continue;
                bool rooted; try { rooted = Path.IsPathRooted(f); } catch { rooted = false; }
                if (!rooted || IsUncPath(f)) continue;
                if (!Services.MonacoFileOpener.IsAllowedDropExtension(f)) continue;
                safe.Add(f);
                if (safe.Count >= 50) break;
            }
            if (safe.Count == 0) return;
            DeferExplorer(() => { foreach (var f in safe) Services.MonacoFileOpener.OpenFile(f, _isDark); PostExplorerData(); });
        }

        /// <summary>Build the grouped Files-tab view-model and push it to the page. UI thread (called from the
        /// WebView2 message callback / deferred handlers); the classifier caches by mtime so re-posts are cheap.</summary>
        private void PostExplorerData()
        {
            try
            {
                string sol = null;
                try { sol = Services.EditorService.GetOpenSolutionPath(); } catch { }
                var vm = Services.ExplorerFileClassifier.BuildViewModel(true);
                Post(new Dictionary<string, object>
                {
                    { "type", "setExplorerData" },
                    // Carry the active bucket so the page's banner can self-correct: the pad may post once at IDE
                    // startup before the solution has loaded (NoSolution); a later post (Files activation / mutation /
                    // solution-change tick) lands the real solution and refreshes the banner.
                    { "versionTag", Services.ModernEmbeditorHistory.VersionTag() },
                    { "solutionTag", Services.ModernEmbeditorHistory.SolutionTag(sol) },
                    { "lastFolder", vm.lastFolder ?? "" },
                    { "quickLocations", vm.quickLocations },
                    { "pinnedFolders", vm.pinnedFolders },
                    { "pinned", vm.pinned },
                    { "groups", vm.groups },
                    { "uiState", new Dictionary<string, object> { { "collapsed", _explorerCollapsed } } }
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] PostExplorerData: " + ex.Message); }
        }

        /// <summary>
        /// Push the active Clarion-version + solution tags to the page's banner. Recents/pins are bucketed by
        /// (version, solution) in <see cref="Services.ExplorerRecentsStore"/>, so surfacing the active bucket makes
        /// it obvious why switching the selected Clarion version shows a different recents list. UI thread.
        /// </summary>
        private void PostVersionInfo()
        {
            try
            {
                string sol = null;
                try { sol = Services.EditorService.GetOpenSolutionPath(); } catch { }
                Post(new Dictionary<string, object>
                {
                    { "type", "setVersionInfo" },
                    { "versionTag", Services.ModernEmbeditorHistory.VersionTag() },
                    { "solutionTag", Services.ModernEmbeditorHistory.SolutionTag(sol) }
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] PostVersionInfo: " + ex.Message); }
        }

        // Supersedes an in-flight redirection enumeration when the index is re-requested (e.g. the solution or
        // selected Clarion version changed, so RedFileService.Active now points at a different .red). The stale
        // Task.Run sees the cancelled token and drops its result instead of posting an outdated index.
        private System.Threading.CancellationTokenSource _redIndexCts;

        // Monotonic generation for redirection-index requests (UI thread). Carried in each setRedIndex payload so
        // the page can reject a stale enumeration that completes after a newer one (the supersede race).
        private int _redIndexGen;

        // Our own .red, loaded on demand when RedFileService.Active isn't populated by the chat pad. Cached;
        // cleared on an environment change so a solution/version switch re-resolves it.
        private Services.RedFileService _ownRed;

        // Canonical section search order used by the resolver (ClarionAppDataReader): C12 names first, then the
        // legacy names, then the universal Common fallback. EnumerateFiles/ResolveTrace walk this in priority order.
        private static readonly string[] RedSectionOrder = { "Debug32", "Release32", "Debug", "Release", "Common" };

        /// <summary>
        /// Load the active Clarion redirection (.red) file ourselves — same resolution the chat pad's LoadRedFile
        /// uses (detect the current Clarion version, then LoadForProject against the open solution's dir). Used as a
        /// fallback when RedFileService.Active is null (chat pad hasn't loaded it yet / isn't up), so the Files-tab
        /// type-ahead doesn't depend on the chat pad's timing. Cached in _ownRed; LoadForProject also sets the
        /// global Active, so once this succeeds later requests read Active directly. UI thread.
        /// </summary>
        private Services.RedFileService EnsureOwnRedFile()
        {
            if (_ownRed != null) return _ownRed;
            try
            {
                var versionInfo = Services.ClarionVersionService.Detect();
                var cfg = versionInfo != null ? versionInfo.GetCurrentConfig() : null;
                if (cfg == null) return null;

                string sol = null;
                try { sol = Services.EditorService.GetOpenSolutionPath(); } catch { }
                string projDir = !string.IsNullOrEmpty(sol) ? Path.GetDirectoryName(sol) : null;

                var r = new Services.RedFileService();
                if (r.LoadForProject(projDir, cfg) && !string.IsNullOrEmpty(r.RedFilePath))
                {
                    _ownRed = r;   // LoadForProject set RedFileService.Active too
                    return r;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] EnsureOwnRedFile: " + ex.Message); }
            return null;
        }

        /// <summary>
        /// Build the "Open File via Redirection" file index ONCE, off the UI thread, and push it to the page.
        /// The page caches it and filters in-memory per keystroke — no disk I/O on the keypath, so fast typing
        /// cannot freeze the IDE (the bug this feature fixes). Re-requested on Files-tab activation, so a solution
        /// or Clarion-version switch (which repoints RedFileService.Active) naturally rebuilds against the new .red.
        /// </summary>
        private void RequestRedIndex()
        {
            // Supersede any enumeration still running for a previous .red: cancel AND dispose the old source
            // (each owns a wait handle — leaking one per Files-tab activation is a slow handle leak). Safe to
            // dispose here: the in-flight task only reads its already-captured token, never the source.
            var old = _redIndexCts;
            try { if (old != null) old.Cancel(); } catch { }
            var cts = new System.Threading.CancellationTokenSource();
            _redIndexCts = cts;
            var token = cts.Token;
            try { if (old != null) old.Dispose(); } catch { }

            // Monotonic request generation, stamped on the UI thread and carried in the payload. The page applies
            // a setRedIndex only if its gen is the newest it has seen — so a stale enumeration that finishes AFTER
            // a newer one (the supersede race) can't overwrite the current .red's file list. Mirrors _refreshGen.
            int gen = ++_redIndexGen;

            // RedFileService.Active is populated by the chat pad when it loads the .red. If that hasn't happened
            // yet (fresh IDE start, chat pad not up, or it raced our first request), load the .red ourselves so the
            // type-ahead works independently of the chat pad. EnsureOwnRedFile also sets Active, so once it succeeds
            // every later request just reads Active.
            var red = Services.RedFileService.Active ?? EnsureOwnRedFile();
            if (red == null)
            {
                // No active .red — tell the page to show the "open a Clarion solution" hint instead of an empty list.
                Post(new Dictionary<string, object>
                {
                    { "type", "setRedIndex" },
                    { "gen", gen },
                    { "indexing", false },
                    { "available", false },
                    { "files", new List<object>() },
                    { "truncated", false },
                    { "redPath", "" }
                });
                return;
            }

            string sol = null;
            try { sol = Services.EditorService.GetOpenSolutionPath(); } catch { }
            string baseDir = !string.IsNullOrEmpty(sol) ? Path.GetDirectoryName(sol) : null;
            string redPath = red.RedFilePath ?? "";

            // Tell the page we've started — it shows "Indexing…" so a slow first scan (large / UNC redirection
            // trees) isn't mistaken for "no matches" while the off-thread enumeration is still running.
            Post(new Dictionary<string, object>
            {
                { "type", "setRedIndex" },
                { "gen", gen },
                { "indexing", true },
                { "available", true },
                { "redPath", redPath }
            });

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool truncated;
                    var matches = red.EnumerateFiles(baseDir, token, out truncated, RedSectionOrder);
                    if (token.IsCancellationRequested) return;

                    var files = new List<object>(matches.Count);
                    foreach (var m in matches)
                        files.Add(new Dictionary<string, object>
                        {
                            { "name", m.Name },
                            { "path", m.FullPath },
                            { "section", m.Section }
                        });

                    if (token.IsCancellationRequested) return;
                    Post(new Dictionary<string, object>
                    {
                        { "type", "setRedIndex" },
                        { "gen", gen },
                        { "indexing", false },
                        { "available", true },
                        { "files", files },
                        { "truncated", truncated },
                        { "redPath", redPath }
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ModernDataPad] RequestRedIndex: " + ex.Message);
                    // Clear the page's "Indexing…" state on failure so it doesn't hang there. Only the newest
                    // request posts the recovery (stale ones are dropped by the gen guard on the page anyway).
                    if (!token.IsCancellationRequested)
                        Post(new Dictionary<string, object>
                        {
                            { "type", "setRedIndex" },
                            { "gen", gen },
                            { "indexing", false },
                            { "available", true },
                            { "files", new List<object>() },
                            { "truncated", false },
                            { "redPath", redPath }
                        });
                }
            });
        }

        /// <summary>
        /// Trace how the active .red resolves <paramref name="name"/> — section by section, pattern by pattern —
        /// and push the human-readable log to the page's Trace panel. Mirrors the native dialog's trace.
        /// </summary>
        private void TraceRedFile(string name)
        {
            // Cheap guards first (no filesystem access) — answer synchronously. Same self-load fallback as the index.
            var red = Services.RedFileService.Active ?? EnsureOwnRedFile();
            if (red == null)
            {
                Post(new Dictionary<string, object> { { "type", "setRedTrace" },
                    { "lines", new List<string> { "No active redirection (.red) file — open a Clarion solution first." } }, { "found", "" } });
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                Post(new Dictionary<string, object> { { "type", "setRedTrace" },
                    { "lines", new List<string> { "Type a complete filename to trace (e.g. Customer.clw)." } }, { "found", "" } });
                return;
            }

            string sol = null;
            try { sol = Services.EditorService.GetOpenSolutionPath(); } catch { }
            string baseDir = !string.IsNullOrEmpty(sol) ? Path.GetDirectoryName(sol) : null;

            // ResolveTrace probes the filesystem (File.Exists per matching .red entry). Run it OFF the UI thread so a
            // stale/offline network redirection entry can't freeze the IDE when the user clicks Trace — the whole
            // point of this feature is to be lock-up-free. Post marshals the result back to the UI thread itself.
            System.Threading.Tasks.Task.Run(() =>
            {
                var trace = new List<string>();
                string found = null;
                try { found = red.ResolveTrace(name, baseDir, trace, RedSectionOrder); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ModernDataPad] TraceRedFile: " + ex.Message);
                    trace.Add("Trace error: " + ex.Message);
                }
                Post(new Dictionary<string, object>
                {
                    { "type", "setRedTrace" },
                    { "lines", trace },
                    { "found", found ?? "" }
                });
            });
        }

        /// <summary>
        /// Defer an Explorer action OUT of the WebView2 message callback onto the next UI turn, moving focus to the
        /// main IDE window first — identical to the 'open' action's guard: a WebView2 holding focus can deadlock the
        /// native embeditor close, and modal file/folder pickers must not run re-entrantly inside the message pump.
        /// </summary>
        private void DeferExplorer(Action work)
        {
            if (_panel == null) { try { work(); } catch { } return; }
            _panel.BeginInvoke((Action)(() =>
            {
                try
                {
                    var mainForm = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench as Form;
                    if (mainForm != null) { mainForm.Activate(); Application.DoEvents(); }
                }
                catch { }
                try { work(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] explorer action: " + ex.Message); }
            }));
        }

        /// <summary>Sniff the pad's dark/light flag out of the opaque JS UI-state blob (a JSON string with "dark":bool).</summary>
        private void SniffTheme(string stateBlob)
        {
            if (string.IsNullOrEmpty(stateBlob)) return;
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(stateBlob) as Dictionary<string, object>;
                object dark;
                if (d != null && d.TryGetValue("dark", out dark) && dark is bool) _isDark = (bool)dark;
            }
            catch { }
        }

        // OpenFileDialog/FolderBrowserDialog helpers (UI thread, inside DeferExplorer). Return null on cancel.
        private static string[] PickFiles(bool multi, string initialDir)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Load File into CA Editor";
                dlg.Filter = ExplorerOpenFilter;
                dlg.Multiselect = multi;
                dlg.CheckFileExists = true;
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.FileNames : null;
            }
        }

        private static string PickSingle(string title, string initialDir)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = title;
                dlg.Filter = ExplorerOpenFilter;
                dlg.Multiselect = false;
                dlg.CheckFileExists = true;
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
            }
        }

        private static string PickFolder(string description, string initialDir)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = description;
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir)) dlg.SelectedPath = initialDir;
                return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        /// <summary>Deserialize a top-level JSON string-array field (e.g. dropFiles.paths) with a hard count cap.</summary>
        private static List<string> ExtractStringArray(string json, string key, int cap)
        {
            var outp = new List<string>();
            try
            {
                var d = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
                object arr;
                if (d != null && d.TryGetValue(key, out arr) && arr is object[])
                {
                    foreach (var it in (object[])arr)
                    {
                        if (it == null) continue;
                        string s = it.ToString();
                        if (!string.IsNullOrEmpty(s)) outp.Add(s);
                        if (outp.Count >= cap) break;
                    }
                }
            }
            catch { }
            return outp;
        }

        /// <summary>Parse saveExplorerUiState's nested state.collapsed string array.</summary>
        private static List<string> ParseCollapsed(string json)
        {
            var outp = new List<string>();
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                object st;
                if (d != null && d.TryGetValue("state", out st))
                {
                    var sd = st as Dictionary<string, object>;
                    object col;
                    if (sd != null && sd.TryGetValue("collapsed", out col) && col is object[])
                        foreach (var it in (object[])col) if (it != null) outp.Add(it.ToString());
                }
            }
            catch { }
            return outp;
        }

        // A host-owned field drag (beginFieldDrag) was released over THIS pad. Post the drop back to the page so
        // it can hit-test the Local/Global section under the point and run the in-pad column copy / declaration
        // insert (the page owns the DOM layout; the host owns the OLE drag). Coords are physical screen offsets
        // from the webview's client origin; the page divides by devicePixelRatio for elementFromPoint.
        private void PostHostFieldDrop(int screenX, int screenY, string cols, string text)
        {
            try
            {
                var origin = _webView.PointToScreen(System.Drawing.Point.Empty);
                Post(new Dictionary<string, object>
                {
                    { "type", "hostFieldDrop" },
                    { "x", screenX - origin.X },
                    { "y", screenY - origin.Y },
                    { "cols", cols ?? "" },
                    { "text", text ?? "" }
                });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] PostHostFieldDrop: " + ex.Message); }
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
        private bool _lastShownSelection;    // last shown proc came from app-tree selection, not a focused editor
        private string _lastEnvKey = "\0"; // last (solution | version | active .red path) key; sentinel so the first tick posts the banner
        private string _lastShownProc = "\0"; // sentinel so the first tick always refreshes

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

            // Environment-change watcher: the pad is a restored dock that can init (and post its version banner /
            // explorer recents) at IDE startup BEFORE a solution finishes loading — bucketing to "NoSolution" with an
            // empty recents list. Re-post the banner + (if the Files tab is showing) the recents and redirection index
            // whenever the EFFECTIVE environment changes. We key on solution tag + Clarion-version tag + active .red
            // path, not solution alone: a same-solution Clarion VERSION switch repoints RedFileService.Active to a
            // different version-level .red while the solution tag is unchanged — keying on solution only would leave
            // the type-ahead/trace resolving against the stale .red until the tab is reactivated. Cheap in-memory read.
            string envKey;
            try
            {
                string solTag = Services.ModernEmbeditorHistory.SolutionTag(Services.EditorService.GetOpenSolutionPath());
                string verTag = Services.ModernEmbeditorHistory.VersionTag();
                var activeRed = Services.RedFileService.Active;
                string redPath = activeRed != null ? activeRed.RedFilePath : null;
                envKey = (solTag ?? "") + "|" + (verTag ?? "") + "|" + (redPath ?? "");
            }
            catch { envKey = _lastEnvKey; } // on probe failure, don't thrash — leave the key unchanged
            if (!string.Equals(envKey, _lastEnvKey, StringComparison.OrdinalIgnoreCase))
            {
                _lastEnvKey = envKey;
                _ownRed = null; // env changed — drop our self-loaded .red so RequestRedIndex re-resolves
                PostVersionInfo();
                if (_filesTabActive)
                {
                    PostExplorerData();
                    // The solution/version switch repointed RedFileService.Active at a different .red. If the user is
                    // sitting on the Files tab (no reactivation to trigger it), rebuild the redirection index now —
                    // otherwise the type-ahead/trace would keep resolving against the previous environment's index.
                    RequestRedIndex();
                }
            }

            string proc;
            bool isNative = false, isSelection = false;
            try
            {
                // Lightweight peek (native embeditor preferred, else Modern view) — no buffer snapshot per tick.
                // This is the pad's "active-document-changed" detector; we poll on a timer rather than subscribe
                // to workbench events because the event approach re-entered the native close and deadlocked.
                proc = Services.ActiveProcedureContext.PeekActiveProcedure(out isNative, out isSelection);
            }
            catch { return; }
            // Refresh when the active procedure OR its editor kind changed (or it went away). Keying on kind too
            // catches a Modern->native (or native->Modern) switch at the same procedure name.
            if (!string.Equals(proc, _lastShownProc, StringComparison.OrdinalIgnoreCase) || isNative != _lastShownNative || isSelection != _lastShownSelection)
            {
                _lastShownProc = proc;
                _lastShownNative = isNative;
                _lastShownSelection = isSelection;
                // Switching to a NATIVE procedure: refresh the whole-app .txa + live-dict snapshot first. The
                // native path has no open/save hook to populate them, so without this a native-only session would
                // show empty Local/Global/Tables data. (Modern proc-switches reuse the cache the open/save hooks
                // already maintain.) RefreshPadSources is UI-thread + best-effort (keeps the prior cache on error).
                // App-tree SELECTION has the same gap (no open/save hook), so it also needs the caches loaded —
                // but only ONCE: the whole-app .txa carries every proc, so EnsurePadSourcesLoaded exports on the
                // first selection and reuses the cache on subsequent clicks (no per-click full export).
                if ((isNative || isSelection) && !string.IsNullOrEmpty(proc) && !_nativeSourcesRefreshing)
                {
                    // Re-entrancy guard: RefreshPadSources runs a silent whole-app .txa export on the UI thread;
                    // if it pumps messages, a re-entrant timer tick must not stack a second export. (Only fires on
                    // proc CHANGE, so it's already debounced to native-activation events, not every 750ms tick.)
                    _nativeSourcesRefreshing = true;
                    try { if (isNative) Terminal.ModernEmbeditorViewContent.RefreshPadSources(); else Terminal.ModernEmbeditorViewContent.EnsurePadSourcesLoaded(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernDataPad] pad sources refresh: " + ex.Message); }
                    finally { _nativeSourcesRefreshing = false; }
                }
                Refresh();
            }
        }

        public override void RedrawContent() { _lastShownProc = "\0"; Refresh(); }

        public override void Dispose()
        {
            lock (_instances) { _instances.Remove(this); }
            ClearAddRefreshTimers();
            // Cancel any in-flight redirection enumeration so its Task.Run drops its result instead of
            // posting to a torn-down WebView2 (and doesn't outlive the pad at shutdown).
            try { if (_redIndexCts != null) { _redIndexCts.Cancel(); _redIndexCts.Dispose(); _redIndexCts = null; } } catch { }
            if (_settingsWindow != null && !_settingsWindow.IsDisposed) { try { _settingsWindow.Close(); } catch { } _settingsWindow = null; }
            if (_autoTimer != null) { _autoTimer.Stop(); _autoTimer.Dispose(); _autoTimer = null; }
            if (_webView != null) { _webView.Dispose(); _webView = null; }
            if (_panel != null) { _panel.Dispose(); _panel = null; }
            base.Dispose();
        }

        /// <summary>Dispose every live ModernDataPad's WebView2 on the UI thread before native IDE teardown.
        /// Called from ShutdownService.Terminate(). Idempotent + exception-swallowing per instance.</summary>
        public static void DisposeAllForShutdown()
        {
            List<ModernDataPad> snapshot;
            lock (_instances) { snapshot = new List<ModernDataPad>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
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

        // Read a string value out of an inbound page message. Backed by JavaScriptSerializer (full JSON: unicode
        // escapes, numbers, nesting) rather than a hand-rolled scanner. The whole message is deserialized once
        // and cached by reference, so the many per-message lookups don't re-parse. Same (json,key)→string
        // signature the call sites use; missing/null keys return null, non-string scalars stringify invariantly.
        [ThreadStatic] private static string _lastJson;
        [ThreadStatic] private static Dictionary<string, object> _lastDict;
        private static string ExtractJsonValue(string json, string key)
        {
            if (json == null) return null;
            try
            {
                if (!ReferenceEquals(json, _lastJson))
                {
                    _lastDict = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                        .DeserializeObject(json) as Dictionary<string, object>;
                    _lastJson = json;
                }
                object v;
                if (_lastDict != null && _lastDict.TryGetValue(key, out v) && v != null)
                    return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }
    }
}
