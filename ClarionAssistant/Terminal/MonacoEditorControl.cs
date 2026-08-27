using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClarionAssistant.Terminal
{
    // ── Monaco-default-editor converge (task cc8b092f), STEPS 1–2 ───────────────────────────────
    // Goal (John/Mark): ONE Monaco protocol host shared by BOTH the standalone file-mode embeditor
    // view (ModernEmbeditorViewContent) AND the dual-control source overlay (MonacoClarionEditor),
    // instead of three separate WebView2/Monaco paths.
    //
    // STEP 1 (done): WebView2 lifecycle + nav + the JS<->C# transport skeleton.
    // STEP 2 (this revision): port the inbound action DISPATCH and the transport-pure outbound
    //   senders into the control; expand IMonacoEditorHost to the full typed callback surface.
    //   The state-assembling senders (SendSource / ApplyHistory / ApplySettings) stay host-side
    //   because they read host fields + domain types; the host builds their JSON and calls PostJson.
    //
    // STILL ZERO-IMPACT: nothing references this control yet, so ModernEmbeditorViewContent is
    // byte-for-byte unchanged. The dispatch + senders are COPIED (not cut) faithfully from the view
    // so STEP 3's rewire is a mechanical swap.

    /// <summary>
    /// Callbacks the reusable <see cref="MonacoEditorControl"/> needs from whatever embeds it (the
    /// standalone ModernEmbeditor view, or the dual-control source overlay). The control owns the
    /// WebView2 + Monaco page + JS&lt;-&gt;C# transport + the inbound action routing; every IDE-specific
    /// operation (source assembly, save round-trips, the structure designer, LSP completion / hover /
    /// diagnostics, settings / history / cursor / bookmark persistence, Data-pad refresh, file-mode
    /// reload) is delegated here. Each method maps 1:1 to an inbound page-&gt;host action so the routing
    /// is a thin switch. A host that doesn't care about an action implements it as a no-op.
    /// </summary>
    public interface IMonacoEditorHost
    {
        /// <summary>{action:"ready"} — the page is loaded. Host should push source (SendSource) plus
        /// any initial settings / history / bookmarks.</summary>
        void OnReady(MonacoEditorControl editor);

        /// <summary>{action:"save"} — embed mode: per-slot save; file mode: whole-buffer save.</summary>
        void OnSave(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"cancel"} — discard edits and close: overlay mode releases the native embed; tab
        /// mode closes the workbench tab. (a5bbf005 — our toolbar's Cancel, replacing the hidden native red-X.)</summary>
        void OnCancel(MonacoEditorControl editor);

        /// <summary>{action:"confirmSaveExit"} — Ctrl+Q on a dirty buffer. The host raises a NATIVE
        /// Windows dialog and posts the answer back as {action:"confirmSaveExitResult", result:"yes"|
        /// "no"|"cancel"}; the page keeps ownership of what those answers DO.
        ///
        /// GH #193 (BoxSoft): this used to be an in-page dark overlay, which the native embeditor's
        /// stock MessageBox made look conspicuously foreign — "its strange appearance causes one to
        /// pause, which disrupts mental flow". The dialog is deliberately the host's job because only
        /// the host can produce real Windows chrome, theming, DPI and mnemonics.
        ///
        /// ON THE INTERFACE, not bolted to one host, ON PURPOSE: monaco-embeditor.html is shared by
        /// BOTH IMonacoEditorHost implementations (MonacoClarionEditor and ModernEmbeditorViewContent),
        /// so a handler added to only one would silently no-op in the other. Declaring it here makes
        /// the compiler insist. The two hosts SHOULD differ in wording — only the embeditor is the
        /// "Embed Editor" the native dialog names.</summary>
        void OnConfirmSaveExit(MonacoEditorControl editor);

        /// <summary>{action:"openSource"} — clicking our header strip opens the generated source (runs the native
        /// OpenSourceButton.OpenSourceCommand). Overlay mode only; other hosts no-op. (b1e05287)</summary>
        void OnOpenSource(MonacoEditorControl editor);

        /// <summary>{action:"clipboard"} — Clarion-style cut: put text on the Windows clipboard.</summary>
        void OnClipboard(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"completion"} — LSP completion request; host replies via PostResponse.</summary>
        void OnCompletion(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"hover"} — LSP hover request; host replies via PostResponse.</summary>
        void OnHover(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"definition"} — F12 go-to-definition; host navigates (same- or cross-file)
        /// then replies via PostResponse with {navigated:bool}.</summary>
        void OnDefinition(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"diagnostics"} — hybrid LSP + slot diagnostics; host replies via PostResponse.</summary>
        void OnDiagnostics(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"signatureHelp"} — LSP parameter hints at a call site (typing '(' or ',');
        /// host replies via PostResponse with {signatureHelp: {...}|null}.</summary>
        void OnSignatureHelp(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"implementation"} — Ctrl+F12 go-to-implementation (declaration → body);
        /// host navigates (same- or cross-file) then replies via PostResponse with {navigated:bool}.</summary>
        void OnImplementation(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"documentStructure"} — outline/document-symbol tree for the current buffer;
        /// host replies via PostResponse with {symbols:[{name,kind,detail,line,children}], fileMode}.</summary>
        void OnDocumentStructure(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveSettings"} — persist gear-panel settings + broadcast to all tabs.</summary>
        void OnSaveSettings(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"readVsCodeSettings", browse:bool?, path:string?} — READ-ONLY: locate and map
        /// the developer's VS Code editor settings for the gear panel's import preview. Replies via
        /// PostResponse with {found, path, source, error, values, skipped, cancelled}. Applying the result
        /// goes back through saveSettings, so this action never writes.</summary>
        void OnReadVsCodeSettings(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveHistory"} — persist Find/Replace history + broadcast to all tabs.</summary>
        void OnSaveHistory(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"snippetCommand", op:"add"|"edit"|"delete", data:{...}} — gear-panel Code
        /// Snippets CRUD; host persists via SnippetStore and broadcasts the updated list to all tabs.</summary>
        void OnSnippetCommand(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveCursor"} — persist cursor position per proc+solution.</summary>
        void OnSaveCursor(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"saveBookmarks"} — persist bookmark lines per proc+solution.</summary>
        void OnSaveBookmarks(MonacoEditorControl editor, string rawJson);

        /// <summary>
        /// {action:"saveFolds"} — persist the collapsed fold set per proc+solution, as {line,text} records.
        /// Deliberately a first-class interface member rather than an OnUnknownAction case: there are TWO
        /// IMonacoEditorHost implementations (this view and MonacoClarionEditor), and a state-persisting
        /// callback that only ONE of them handles fails silently on the other surface. Putting it on the
        /// interface makes the compiler refuse to build until both are wired.
        /// </summary>
        void OnSaveFolds(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"selectionChanged"} — cache the Monaco selection snapshot.</summary>
        void OnSelectionChanged(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"focusEditor"} — drag-drop from the Data pad; host activates the tab.</summary>
        void OnFocusEditor(MonacoEditorControl editor);

        /// <summary>{action:"reload"} — file mode: re-read from disk, discard edits.</summary>
        void OnReload(MonacoEditorControl editor);

        /// <summary>{action:"fileState"} — file mode: page mirrors its live buffer + dirty flag.</summary>
        void OnFileState(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"openDesigner"} — Ctrl+D on an existing structure. The host enforces any
        /// mode guard (e.g. file mode refuses — the designer needs an embeditor-backed procedure).</summary>
        void OnOpenDesigner(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"openDesignerCreate"} — template-picker choice for a NEW structure.</summary>
        void OnOpenDesignerCreate(MonacoEditorControl editor, string rawJson);

        /// <summary>{action:"activateDesigner"} — 'Show designer' on the modal lock overlay.</summary>
        void OnActivateDesigner(MonacoEditorControl editor);

        /// <summary>{action:"caFindUpdate"|"caFindOpen"|"caFindOpenDoc"|"caFindActivity"} — CA Find pad
        /// protocol (GitHub #66): results push / Ctrl+F open request / open-results-in-editor / editor-focus
        /// activity. Hosts forward to <c>CaFindBroker.FromEditor</c> with their own identity; the broker
        /// routes to the pad — except caFindOpenDoc, which it turns into a SearchResultsViewContent tab
        /// stamped with the sending host's key. Hosts stay identical for all four; no per-host logic.</summary>
        void OnCaFind(MonacoEditorControl editor, string action, string rawJson);

        /// <summary>Navigation to the Monaco page completed (IsSuccess). Optional liveness hook.</summary>
        void OnEditorNavigationCompleted(MonacoEditorControl editor, bool success);

        /// <summary>Any inbound action not matched above. Forward-compat / diagnostics.</summary>
        void OnUnknownAction(MonacoEditorControl editor, string action, string rawJson);
    }

    /// <summary>
    /// Reusable Monaco-over-WebView2 surface. Owns the <see cref="Panel"/> + <see cref="WebView2"/>,
    /// CoreWebView2 init, a per-instance virtual-host temp folder (large-buffer transfer via
    /// source.txt), navigation to the Monaco HTML (default monaco-embeditor.html) with the ?v=
    /// cache-bust, the inbound action dispatch, and the transport-pure outbound senders. Everything
    /// IDE-specific is delegated through <see cref="IMonacoEditorHost"/>.
    /// </summary>
    public class MonacoEditorControl : Panel
    {
        private readonly IMonacoEditorHost _host;
        private readonly string _htmlFileName;   // page under Terminal\ (the overlay may pick a different one)
        private readonly string _virtualHost;    // virtual host name mapped to this control's temp folder

        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _disposedControl;
        private string _tempDir;

        /// <summary>The underlying WebView2 (null until constructed / after dispose).</summary>
        public WebView2 WebView { get { return _webView; } }

        /// <summary>True once navigation to the Monaco page has completed successfully.</summary>
        public bool IsInitialized { get { return _isInitialized; } }

        /// <summary>Per-instance temp folder mapped to <see cref="VirtualHost"/>; hosts write the
        /// large source buffer here (source.txt) and hand Monaco a virtual-host URL. Null until init.</summary>
        public string TempDir { get { return _tempDir; } }

        /// <summary>Virtual host name the page resolves large buffers against (default
        /// "clarion-embeditor-data", matching monaco-embeditor.html).</summary>
        public string VirtualHost { get { return _virtualHost; } }

        /// <summary>
        /// THIS surface's current dark/light state, as opposed to the process-wide
        /// CaEditorSettings.MonacoThemeDark mirror — which records whichever page most recently
        /// booted or toggled, and so does NOT track the editor the user is currently looking at.
        /// Seeded from the ctor and re-synced on every themeChanged this page posts (boot and each
        /// toolbar toggle), which is the only way to know a given surface's theme: localStorage is
        /// shared per-origin, but an already-open page never re-reads it, so its live theme exists
        /// only in that page's memory until it tells us.
        /// </summary>
        public bool IsDark { get; private set; }

        public MonacoEditorControl(IMonacoEditorHost host, bool isDark = true,
                                   string htmlFileName = "monaco-embeditor.html",
                                   string virtualHost = "clarion-embeditor-data")
        {
            _host = host;
            _htmlFileName = string.IsNullOrEmpty(htmlFileName) ? "monaco-embeditor.html" : htmlFileName;
            _virtualHost = string.IsNullOrEmpty(virtualHost) ? "clarion-embeditor-data" : virtualHost;

            Dock = DockStyle.Fill;
            IsDark = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);

            // Plain WebView2 — Monaco's native mouseWheelZoom owns Ctrl+wheel inside the renderer.
            // DefaultBackgroundColor = the themed backdrop so the WebView2 surface shows the editor's colour
            // (not a black/white compositor flash) before Monaco's first paint.
            _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(_webView);

            // Init when the panel handle is realized (the WebView2 can only EnsureCoreWebView2 then).
            HandleCreated += OnHandleCreated;
        }

        // ── WebView2 lifecycle ──────────────────────────────────────────────────────────────────
        // Lifted (copied) from ModernEmbeditorViewContent.OnHandleCreated so STEP 3 is a faithful swap.
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
                    _virtualHost, _tempDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                // DevTools OFF so F12 reaches the page's go-to-definition handler instead of being captured
                // natively by WebView2 for DevTools (AreBrowserAcceleratorKeysEnabled=false alone doesn't free
                // F12 while DevTools is enabled — that's why Ctrl+F/Ctrl+click work but F12 did nothing). Flip
                // to true temporarily if you need to debug the embeditor page. (ticket 6e8f2439)
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false; // let Monaco own Ctrl+S, not the browser

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri + "?v=" + File.GetLastWriteTimeUtc(htmlPath).Ticks);
                else
                    System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] HTML missing: " + htmlPath);
            }
            catch (Exception ex)
            {
                _isInitializing = false; // allow retry
                System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] Init error: " + ex.Message);
            }
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _isInitialized = e.IsSuccess;
            _isInitializing = false;
            // Source push is triggered by the JS "ready" message (OnReady), not here — avoids a double-send.
            try { if (_host != null) _host.OnEditorNavigationCompleted(this, e.IsSuccess); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] NavCompleted host error: " + ex.Message); }
        }

        // ── Inbound dispatch (page -> host) ─────────────────────────────────────────────────────
        // Ported (copied) from ModernEmbeditorViewContent.OnWebMessageReceived. The fileMode guards
        // that wrapped the designer cases there now live in the host's OnOpenDesigner* impls — the
        // control routes unconditionally; the host knows its own mode.
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.TryGetWebMessageAsString();
                string action = ExtractJsonValue(json, "action");
                var h = _host;
                if (h == null) return;

                switch (action)
                {
                    case "themeChanged":
                        // Page → host mirror of the persisted light/dark pref (localStorage is authoritative;
                        // see CaEditorSettings.MonacoThemeDark). Handled here — not on IMonacoEditorHost — so
                        // every Monaco surface mirrors it without each host implementing anything. Payload is
                        // our own tiny fixed message, so a literal match beats a JSON round-trip.
                        // Record it PER SURFACE as well as in the global mirror: the mirror only ever
                        // says "whoever posted last", so it can't answer "what theme is the editor the
                        // user is looking at right now" once two surfaces disagree. See IsDark.
                        bool pageIsDark = json.IndexOf("\"dark\":true", StringComparison.Ordinal) >= 0;
                        IsDark = pageIsDark;
                        try { Services.CaEditorSettings.MonacoThemeDark = pageIsDark; }
                        catch { }
                        break;
                    case "openLocation":
                        // A hover-footer location link was clicked ("Foo.inc:12 → Foo.clw:340"). Handled
                        // HERE rather than on IMonacoEditorHost — like "themeChanged" above — so every
                        // Monaco surface gets it without each host implementing anything.
                        //
                        // Services.MonacoSourceNavigator, NOT Services.EditorService (2026-07-30, second
                        // pass) — EditorService.NavigateToFileAndLine (what open_file uses) drives
                        // FileService.JumpToFilePosition, which IS the historically-fixed, race-free way
                        // to position the NATIVE caret — but on a Monaco-overlay COLD open, the surface
                        // the developer actually sees is seeded from a SEPARATE place entirely:
                        // MonacoClarionSourceEditor.OnReady calls MonacoSourceNavigator.TryConsumePending
                        // to bake the target line into the very FIRST setSource payload (deliberately —
                        // see that method's own comment: a follow-up revealLine sent after setSource races
                        // the async content fetch on a cold open and loses). JumpTo's own Monaco mirror
                        // (_navPendingLine) is exactly that losing follow-up path when the file wasn't
                        // already open. EditorService never touches MonacoSourceNavigator's pending dict,
                        // so a cold open through it landed on the file's last-remembered position instead
                        // — reproduced live: first click on a not-yet-open target opened at the wrong
                        // line, a second click (file now already live) worked, matching this mechanism
                        // exactly. MonacoSourceNavigator.NavigateToFileAndLine already routes correctly to
                        // BOTH surfaces (NativeGoTo → EditorService.GoToLine when the overlay is off), so
                        // it alone is the right call here — the same one OnDefinition's F12/Ctrl+F12
                        // cross-file jump already uses for this identical scenario.
                        try
                        {
                            string locPath = ExtractJsonValue(json, "path");
                            int locLine;
                            if (!int.TryParse(ExtractJsonValue(json, "line"), out locLine)) locLine = 1;
                            if (!string.IsNullOrEmpty(locPath))
                                Services.MonacoSourceNavigator.NavigateToFileAndLine(locPath, locLine, 1);
                        }
                        catch (Exception lex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] openLocation error: " + lex.Message); }
                        break;
                    case "ready":             h.OnReady(this); break;
                    case "save":              h.OnSave(this, json); break;
                    case "cancel":            h.OnCancel(this); break;
                    case "confirmSaveExit":   h.OnConfirmSaveExit(this); break;
                    // GH #192 key probe (temporary diagnostic) — the page's half of the trace.
                    case "keyProbe":          MonacoSpikeLog.Write("[KEYPROBE] PAGE  " + ExtractKeyProbe(json)); break;
                    // GH #192: a key the page decided belongs to the IDE, not to Monaco.
                    case "ideKey":            HandleIdeKey(ExtractJsonString(json, "combo")); break;
                    // GH #192: Alt+<letter> — a menu MNEMONIC, which has no codon and no shortcut entry.
                    case "ideMenu":           HandleIdeMenu(ExtractJsonString(json, "mnemonic")); break;
                    case "openSource":        h.OnOpenSource(this); break;
                    case "clipboard":         h.OnClipboard(this, json); break;
                    case "completion":        h.OnCompletion(this, json); break;
                    case "hover":             h.OnHover(this, json); break;
                    case "definition":        h.OnDefinition(this, json); break;
                    case "diagnostics":       h.OnDiagnostics(this, json); break;
                    case "signatureHelp":     h.OnSignatureHelp(this, json); break;
                    case "implementation":    h.OnImplementation(this, json); break;
                    case "documentStructure": h.OnDocumentStructure(this, json); break;
                    case "saveSettings":      h.OnSaveSettings(this, json); break;
                    case "readVsCodeSettings": h.OnReadVsCodeSettings(this, json); break;
                    case "saveHistory":       h.OnSaveHistory(this, json); break;
                    case "snippetCommand":    h.OnSnippetCommand(this, json); break;
                    case "saveCursor":        h.OnSaveCursor(this, json); break;
                    case "saveBookmarks":     h.OnSaveBookmarks(this, json); break;
                    case "saveFolds":         h.OnSaveFolds(this, json); break;
                    case "selectionChanged":  h.OnSelectionChanged(this, json); break;
                    case "focusEditor":       h.OnFocusEditor(this); break;
                    case "reload":            h.OnReload(this); break;
                    case "fileState":         h.OnFileState(this, json); break;
                    case "openDesigner":      h.OnOpenDesigner(this, json); break;
                    case "openDesignerCreate":h.OnOpenDesignerCreate(this, json); break;
                    case "activateDesigner":  h.OnActivateDesigner(this); break;
                    case "caFindUpdate":
                    case "caFindOpen":
                    case "caFindOpenDoc":
                    case "caFindActivity":    h.OnCaFind(this, action, json); break;
                    default:                  h.OnUnknownAction(this, action, json); break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] Message error: " + ex.Message);
            }
        }

        // ── Outbound transport (host -> page) ───────────────────────────────────────────────────

        /// <summary>Send a JSON message host-&gt;page (PostWebMessageAsJson), marshalled to the UI
        /// thread. Note: WebView2 delivers it to JS as a parsed OBJECT, not a string — page handlers
        /// must not JSON.parse it. Used by the host for the state-assembled messages (SendSource etc.).
        ///
        /// GPF-on-close guard: a background continuation (e.g. the diagnostics settle-loop in
        /// ModernEmbeditorDiagnostics.ComputeAsync, which can now run for several seconds) can still be
        /// in flight when the embed is closed and DetachOverlay()/Dispose() tears this control down.
        /// `_webView != null` alone does NOT detect that — Dispose() never nulls the field, only IsDisposed
        /// flips. Worse, `InvokeRequired` on a control whose handle was just destroyed (mid-teardown, or
        /// its parent chain was just severed by Controls.Remove) returns FALSE — not because we're on the
        /// UI thread, but because it has no handle left to compare against — which used to fall through to
        /// calling the WebView2/CoreWebView2 COM object (STA) directly from a thread-pool thread instead of
        /// marshalling. That's undefined behaviour for an STA COM object mid-teardown and can produce a
        /// native access violation no C# try/catch can intercept. IsDisposed is checked before trusting
        /// InvokeRequired at all, and again inside the marshalled action (the two checks can't be merged
        /// into one atomic test, but this closes the window down to the marshalling call itself).</summary>
        public void PostJson(string json)
        {
            if (IsDisposed || _webView == null || _webView.IsDisposed) return;
            Action post = () =>
            {
                try { if (_webView != null && !_webView.IsDisposed && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsJson(json); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostJson error: " + ex.Message); }
            };
            try { if (InvokeRequired) BeginInvoke(post); else post(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostJson marshal error: " + ex.Message); }
        }

        /// <summary>Send a raw string message host-&gt;page (PostWebMessageAsString), UI-thread marshalled.
        /// Same GPF-on-close guard as <see cref="PostJson"/> — see its remarks.</summary>
        public void PostString(string message)
        {
            if (IsDisposed || _webView == null || _webView.IsDisposed) return;
            Action post = () =>
            {
                try { if (_webView != null && !_webView.IsDisposed && _webView.CoreWebView2 != null) _webView.CoreWebView2.PostWebMessageAsString(message); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostString error: " + ex.Message); }
            };
            try { if (InvokeRequired) BeginInvoke(post); else post(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[MonacoEditorControl] PostString marshal error: " + ex.Message); }
        }

        /// <summary>Jump the editor caret to a routine: {type:"gotoRoutine", name}.</summary>
        public void GotoRoutine(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            PostJson("{\"type\":\"gotoRoutine\",\"name\":" + JsonString(name) + "}");
        }

        /// <summary>Reveal + position the ALREADY-open editor at a 1-based line: {type:"revealLine", line, column}.
        /// The page centers the line (revealLineInCenter) and focuses the caret. Used for debugger / breakpoint-list
        /// navigation against the source overlay (where moving the hidden native caret would be invisible).</summary>
        public void RevealLine(int line, int column)
        {
            if (line < 1) return;
            PostJson("{\"type\":\"revealLine\",\"line\":" + line + ",\"column\":" + Math.Max(1, column) + "}");
        }

        /// <summary>Insert text at the editor's cursor: {type:"insertText", text}. (Tab focus, if
        /// wanted after an insert, is a host UI concern — the control only posts.)</summary>
        public void InsertText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            PostJson("{\"type\":\"insertText\",\"text\":" + JsonString(text) + "}");
        }

        /// <summary>Move the Monaco caret to the editor position under a SCREEN point (used during a Data-pad field
        /// drag so the caret tracks the mouse and the drop lands at the pointer). We send the PHYSICAL offset from
        /// the webview's client origin; the page divides by devicePixelRatio for Monaco's CSS-px hit-test.</summary>
        public void MoveCaretToScreenPoint(int screenX, int screenY)
        {
            try
            {
                if (_webView == null || !_webView.IsHandleCreated) return;
                var origin = _webView.PointToScreen(System.Drawing.Point.Empty);
                PostJson("{\"type\":\"moveCaretToPoint\",\"x\":" + (screenX - origin.X) + ",\"y\":" + (screenY - origin.Y) + "}");
            }
            catch { }
        }

        /// <summary>Insert text at the editor position under a SCREEN point — the ATOMIC Data-pad field DROP. The
        /// page resolves the position from these coordinates at insert time, so the drop lands exactly where it was
        /// released (no race with the drag's caret-follow). Falls back to a plain caret insert if coords are
        /// unavailable.</summary>
        public void InsertTextAtScreenPoint(string text, int screenX, int screenY)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (_webView == null || !_webView.IsHandleCreated) { InsertText(text); return; }
                var origin = _webView.PointToScreen(System.Drawing.Point.Empty);
                PostJson("{\"type\":\"insertTextAtPoint\",\"text\":" + JsonString(text)
                    + ",\"x\":" + (screenX - origin.X) + ",\"y\":" + (screenY - origin.Y) + "}");
            }
            catch { InsertText(text); }
        }

        /// <summary>Give the editor OS keyboard focus (e.g. right after a Data-pad field DROP so the developer can
        /// type immediately). The page also calls ed.focus() for the Monaco-internal caret; this hands the WebView2
        /// control the Windows focus the pad held during the drag. Deferred a turn so it lands after the drop edit.</summary>
        public void FocusEditor()
        {
            try
            {
                if (_webView == null || !_webView.IsHandleCreated) return;
                _webView.BeginInvoke((Action)(() => FocusAttempt(0)));
            }
            catch { }
        }

        // #66 round-2: a single _webView.Focus() is not enough on an IDE tab switch. The WinForms
        // WebView2 only forwards focus into Chromium from its GotFocus handler, so when the host HWND
        // already holds Win32 focus (or the workbench re-focuses its own control a beat AFTER us),
        // the render widget never gets the keyboard. MoveFocus() is the authoritative hand-off, and
        // the verify+retry beats whatever late focus routing the workbench does on tab activation.
        private void FocusAttempt(int attempt)
        {
            try
            {
                if (_webView == null || _webView.IsDisposed || !_webView.IsHandleCreated) return;
                if (_webView.Visible)
                {
                    _webView.Focus();
                    TryMoveFocusIntoChromium();
                }
                if (attempt < 3 && !_webView.ContainsFocus)
                {
                    var t = new Timer { Interval = 80 };
                    t.Tick += (s, e) =>
                    {
                        try { t.Stop(); t.Dispose(); } catch { }
                        // GH #140 defense-in-depth: if the user actively focused a pad since the
                        // previous attempt, retrying would yank the keyboard back out of it — the
                        // verify+retry exists to beat the WORKBENCH's late focus routing, not the user.
                        if (Services.EditorFocusGuard.FocusInForeignPad(_webView)) return;
                        FocusAttempt(attempt + 1);
                    };
                    t.Start();
                }
                else if (!_webView.ContainsFocus)
                {
                    ClarionAssistant.MonacoSpikeLog.Write("FocusEditor: focus never landed after retries (focused control elsewhere)");
                }
            }
            catch { }
        }

        // The WinForms wrapper doesn't expose CoreWebView2Controller publicly; reflect it and call
        // MoveFocus(Programmatic) — the only API that reliably puts the keyboard in the render widget.
        private void TryMoveFocusIntoChromium()
        {
            try
            {
                object ctl =
                    _webView.GetType().GetProperty("CoreWebView2Controller",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(_webView, null)
                    ?? _webView.GetType().GetField("_coreWebView2Controller",
                        BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_webView);
                var controller = ctl as CoreWebView2Controller;
                if (controller != null) controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            catch { }
        }

        // ── Keep editor accelerators inside Monaco (issue #66 / native-find leak) ────────────────
        // OVERLAY REALITY: this Monaco WebView2 is docked FILL on top of Clarion's still-open native
        // embeditor (ClaGenEditor) — its chrome is only hidden (ModernEmbeditorViewContent.HideNativeChrome),
        // the native text editor is alive underneath as the write-back target. With
        // AreBrowserAcceleratorKeysEnabled=false the WebView still delivers Ctrl+F to the DOM (our page
        // opens Monaco's own find), but it ALSO forwards the accelerator up to the host. SharpDevelop's
        // form-level ProcessCmdKey (DefaultWorkbench) then runs its Find menu-shortcut against the ACTIVE
        // view content — the hidden native editor — so its search bottom-bar leaks up on top of Monaco's.
        // This control sits BELOW the workbench form in the parent chain, so returning true here consumes
        // the forwarded key before the form can dispatch. The DOM keydown already reached the page, so we
        // only swallow the host-side leak; we don't re-trigger the find here.
        /// <summary>Pull a top-level string field out of a small page message without paying for a full
        /// JSON deserialise on every keystroke.</summary>
        private static string ExtractJsonString(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string tag = "\"" + field + "\":\"";
            int i = json.IndexOf(tag, StringComparison.Ordinal);
            if (i < 0) return null;
            i += tag.Length;
            int j = json.IndexOf('"', i);
            return j > i ? json.Substring(i, j - i) : null;
        }

        /// <summary>GH #192 key probe (temporary).</summary>
        private static string ExtractKeyProbe(string json)
        {
            return ExtractJsonString(json, "combo") ?? (json ?? "(empty)");
        }

        /// <summary>Turn "Ctrl+Shift+F4" into a WinForms <see cref="Keys"/>. Returns Keys.None if any
        /// token is unrecognised — callers must treat None as "could not parse", never as a real key.</summary>
        private static Keys ParseCombo(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return Keys.None;
            Keys mods = Keys.None;
            string keyName = null;
            foreach (var raw in combo.Split('+'))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                if (string.Equals(p, "Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= Keys.Control;
                else if (string.Equals(p, "Alt", StringComparison.OrdinalIgnoreCase)) mods |= Keys.Alt;
                else if (string.Equals(p, "Shift", StringComparison.OrdinalIgnoreCase)) mods |= Keys.Shift;
                else keyName = p;
            }
            if (string.IsNullOrEmpty(keyName)) return Keys.None;
            // Single letters arrive lowercase from the DOM ("o"); the Keys enum is uppercase.
            if (keyName.Length == 1) keyName = keyName.ToUpperInvariant();
            try { return (Keys)Enum.Parse(typeof(Keys), keyName, true) | mods; }
            catch { return Keys.None; }
        }

        /// <summary>GH #192 SPIKE: run an IDE command for a key the page decided Monaco does not own.
        ///
        /// Established by measurement first (see the ticket): keys typed into the editor NEVER reach the
        /// host's WinForms key pipeline — WebView2 handles them in its own child window — so ProcessCmdKey
        /// cannot be the route. The page->host channel is the one that demonstrably works.
        ///
        /// Dispatch goes through the IDE's OWN main MenuStrip: find the item whose ShortcutKeys match and
        /// PerformClick() it, so we invoke exactly the object the menu invokes, with its enablement and
        /// its handlers. That also means user-customised shortcuts work for free — SharpDevelop already
        /// applied them to these items via MenuShortcutService.
        ///
        /// EVERY STEP LOGS. This is an SD fork whose object surface has burned us before by returning
        /// null SILENTLY rather than throwing, so a failure must say WHICH step failed rather than
        /// producing the same "nothing happened" the bug already produces.</summary>
        private void HandleIdeKey(string combo)
        {
            Action work = () =>
            {
                try
                {
                    Keys k = ParseCombo(combo);
                    MonacoSpikeLog.Write("[IDEKEY] combo='" + combo + "' -> Keys=" + k);
                    if (k == Keys.None) { MonacoSpikeLog.Write("[IDEKEY] FAIL: could not parse combo"); return; }

                    // Cross-check against the IDE's own shortcut table. NOT used for dispatch — dispatch
                    // matches on ShortcutKeys, which is why a combo with no known codon still works.
                    //
                    // This lookup used to be hardcoded to "CloseFile" regardless of the combo, which was
                    // fine while Ctrl+F4 was the only forwarded key and actively misleading the moment it
                    // was not: pressing Ctrl+O would have logged a confident line about CloseFile. Keyed
                    // off the combo now, and silent about combos whose codon we do not know rather than
                    // guessing one — a wrong codon in the log is worse than no codon, because the next
                    // person debugging this will believe it.
                    string codon = CodonForCombo(combo);
                    if (codon != null)
                    {
                        try
                        {
                            var owner = ICSharpCode.Core.MenuShortcutService.GetShortcutKey(codon);
                            MonacoSpikeLog.Write("[IDEKEY] MenuShortcutService.GetShortcutKey(\"" + codon + "\") = " + owner);
                        }
                        catch (Exception ex) { MonacoSpikeLog.Write("[IDEKEY] MenuShortcutService threw: " + ex.Message); }
                    }
                    else
                    {
                        MonacoSpikeLog.Write("[IDEKEY] no codon id known for '" + combo
                            + "'; dispatch does not need one (matches on ShortcutKeys)");
                    }

                    Form host;
                    var strip = FindWorkbenchMenuStrip("[IDEKEY]", out host);
                    if (strip == null) return;

                    var item = FindItemByShortcut(strip.Items, k);
                    if (item == null) { MonacoSpikeLog.Write("[IDEKEY] FAIL: no menu item carries ShortcutKeys=" + k); return; }

                    MonacoSpikeLog.Write("[IDEKEY] MATCH '" + item.Text + "' enabled=" + item.Enabled + " -> PerformClick()");
                    item.PerformClick();
                    MonacoSpikeLog.Write("[IDEKEY] PerformClick returned");
                }
                catch (Exception ex) { MonacoSpikeLog.Write("[IDEKEY] EXCEPTION: " + ex); }
            };
            if (InvokeRequired) BeginInvoke(work); else work();
        }

        /// <summary>SharpDevelop CodonId for a forwarded combo, or null if we do not know one.
        ///
        /// Diagnostic only — dispatch matches on ShortcutKeys and never consults this. The point of
        /// returning null rather than a best guess is that this string goes straight into the log, and a
        /// plausible-but-wrong codon there would send the next reader hunting the wrong command.
        ///
        /// Ctrl+O is deliberately absent: the Clarion fork's codon for Open has not been confirmed in a
        /// live IDE, and the key works without it. Add it here once the [IDEKEY] MATCH line names the
        /// item it actually resolved to.</summary>
        private static string CodonForCombo(string combo)
        {
            if (string.Equals(combo, "Ctrl+F4", StringComparison.OrdinalIgnoreCase)) return "CloseFile";
            return null;
        }

        /// <summary>The IDE main menu, found across ALL open forms.
        ///
        /// FindForm() is not enough and this was measured, not guessed: from inside the editor it returns
        /// SdiWorkspaceWindow — the per-DOCUMENT window, whose Text is the filename — and the main menu is
        /// not under it. DefaultWorkbench OWNS that window rather than parenting it, so the menu lives in
        /// a different tree entirely.
        ///
        /// Logs every form and whether it carries a MenuStrip, so a failure here reports the inventory
        /// instead of a bare "not found". In this fork the hit is DefaultWorkbench, 20 top-level items.</summary>
        private static MenuStrip FindWorkbenchMenuStrip(string tag, out Form host)
        {
            MenuStrip strip = null;
            host = null;
            foreach (Form f in Application.OpenForms)
            {
                var candidate = FindMenuStrip(f);
                MonacoSpikeLog.Write(tag + "   form " + f.GetType().Name + " text='" + f.Text
                    + "' menuStrip=" + (candidate != null));
                if (candidate != null && strip == null) { strip = candidate; host = f; }
            }
            if (strip == null) { MonacoSpikeLog.Write(tag + " FAIL: no MenuStrip on any open form"); return null; }
            MonacoSpikeLog.Write(tag + " using menustrip on " + host.GetType().Name
                + " topLevelItems=" + strip.Items.Count);
            return strip;
        }

        /// <summary>GH #192: open a top-level IDE menu by its mnemonic letter (Alt+F -> File).
        ///
        /// A mnemonic is NOT a shortcut. It has no CodonId and never appears in MenuShortcutService, so
        /// the codon route that serves Ctrl+F4 cannot serve this — it comes from the '&' in the menu
        /// label. Once the workbench MenuStrip is reachable, though, the item is right there to open.
        ///
        /// Focus has to move off the WebView2 first, or the menu opens without keyboard ownership and
        /// arrow keys keep going to Monaco — a menu you cannot drive is barely better than no menu.</summary>
        private void HandleIdeMenu(string mnemonic)
        {
            Action work = () =>
            {
                try
                {
                    MonacoSpikeLog.Write("[IDEMENU] mnemonic='" + mnemonic + "'");
                    if (string.IsNullOrEmpty(mnemonic) || mnemonic.Length != 1) { MonacoSpikeLog.Write("[IDEMENU] FAIL: bad mnemonic"); return; }
                    char want = char.ToUpperInvariant(mnemonic[0]);

                    Form host;
                    var strip = FindWorkbenchMenuStrip("[IDEMENU]", out host);
                    if (strip == null) return;

                    ToolStripMenuItem target = null;
                    foreach (ToolStripItem it in strip.Items)
                    {
                        var mi = it as ToolStripMenuItem;
                        if (mi == null || string.IsNullOrEmpty(mi.Text)) continue;
                        int amp = mi.Text.IndexOf('&');
                        if (amp >= 0 && amp + 1 < mi.Text.Length && char.ToUpperInvariant(mi.Text[amp + 1]) == want)
                        { target = mi; break; }
                    }
                    if (target == null) { MonacoSpikeLog.Write("[IDEMENU] FAIL: no top-level menu with mnemonic '" + want + "'"); return; }

                    MonacoSpikeLog.Write("[IDEMENU] MATCH '" + target.Text + "' enabled=" + target.Enabled);
                    host.Activate();                 // take the foreground off the editor
                    strip.Focus();                   // give the strip keyboard ownership
                    strip.Select();
                    target.Select();
                    target.ShowDropDown();
                    MonacoSpikeLog.Write("[IDEMENU] ShowDropDown returned; dropDownVisible=" + target.DropDown.Visible);
                }
                catch (Exception ex) { MonacoSpikeLog.Write("[IDEMENU] EXCEPTION: " + ex); }
            };
            if (InvokeRequired) BeginInvoke(work); else work();
        }

        /// <summary>First MenuStrip anywhere under <paramref name="c"/>. The workbench's main menu is not
        /// necessarily a direct child, so this walks the whole control tree.</summary>
        private static MenuStrip FindMenuStrip(Control c)
        {
            var ms = c as MenuStrip;
            if (ms != null) return ms;
            foreach (Control child in c.Controls)
            {
                var found = FindMenuStrip(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Depth-first search for a menu item bound to <paramref name="k"/>. Recurses into
        /// submenus, because almost nothing with a shortcut sits at the top level.</summary>
        private static ToolStripMenuItem FindItemByShortcut(ToolStripItemCollection items, Keys k)
        {
            foreach (ToolStripItem it in items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                if (mi.ShortcutKeys == k) return mi;
                if (mi.HasDropDownItems)
                {
                    var found = FindItemByShortcut(mi.DropDownItems, k);
                    if (found != null) return found;
                }
            }
            return null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // GH #192 KEY PROBE (temporary — pairs with the PAGE line the page posts).
            // Logs EVERY key reaching the host, before the swallow-list below decides anything, so the
            // log distinguishes "never arrived" from "arrived and we ate it".
            //
            // UNCONDITIONAL, and it must stay that way: round 1 gated this on ContainsFocus (mirroring
            // the swallow-list below) and produced 24 PAGE lines with zero CMDKEY lines. That is
            // ambiguous — "ProcessCmdKey was never called" and "it was called while ContainsFocus read
            // false" are the same silence, and they need completely different fixes. Logging the focus
            // flag as DATA rather than using it as a filter is what tells them apart.
            MonacoSpikeLog.Write("[KEYPROBE] CMDKEY " + keyData
                + " wv=" + (_webView != null)
                + " focus=" + (_webView != null && _webView.ContainsFocus));

            if (_webView != null && _webView.ContainsFocus)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.F:                 // Find
                    case Keys.Control | Keys.Shift | Keys.F:    // Find All (Ctrl+Shift+F) — else it leaks to Find-in-Files
                    case Keys.Control | Keys.H:                 // Replace
                    case Keys.Control | Keys.Shift | Keys.H:    // Find All + Replace (Ctrl+Shift+H) — else leaks to Replace-in-Files
                    case Keys.F3:                               // Find next
                    case Keys.Shift | Keys.F3:                  // Find previous
                        return true;               // handled — don't let the workbench run its native Find
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>Reply to an LSP/request message: {type:"response", reqId, data}.</summary>
        public void PostResponse(int reqId, IDictionary<string, object> data)
        {
            string json;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                json = ser.Serialize(new Dictionary<string, object>
                {
                    { "type", "response" }, { "reqId", reqId }, { "data", data }
                });
            }
            catch { return; }
            PostJson(json);
        }

        /// <summary>Confirm a save: {type:"saveResult", ok, savedSeq, message}.</summary>
        public void PostSaveResult(bool ok, string message) { PostSaveResult(ok, message, 0); }

        /// <summary>Confirm a save with a sequence number. NOTE: the view's embed-mode double-post
        /// (to survive open/close churn during a slot save) is a host concern — a host that needs it
        /// calls this twice; the control posts once.</summary>
        public void PostSaveResult(bool ok, string message, long savedSeq)
        {
            string json = "{\"type\":\"saveResult\",\"ok\":" + (ok ? "true" : "false") +
                          ",\"savedSeq\":" + savedSeq +
                          ",\"message\":" + JsonString(message) + "}";
            PostJson(json);
        }

        /// <summary>Push a designer-session event to Monaco: {type, text?, message?} (e.g.
        /// designerSplice / designerClosed).</summary>
        public void PostDesignerMessage(string type, string text, string message)
        {
            string json;
            try
            {
                var d = new Dictionary<string, object> { { "type", type } };
                if (text != null) d["text"] = text;
                if (message != null) d["message"] = message;
                json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(d);
            }
            catch { return; }
            PostJson(json);
        }

        /// <summary>Apply a dark/light theme: recolors the panel backdrop and posts
        /// {type:"applyTheme", isDark} once the surface is live.</summary>
        public void ApplyTheme(bool isDark)
        {
            IsDark = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 46) : Color.FromArgb(239, 241, 245);
            if (_isInitialized)
                PostJson("{\"type\":\"applyTheme\",\"isDark\":" + (isDark ? "true" : "false") + "}");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir, "Terminal", _htmlFileName);
        }

        /// <summary>Minimal JSON string escaper (copied from the view + the overlay — they share the
        /// same encoder). Wraps the result in double quotes.</summary>
        public static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Lifted (copied) from ModernEmbeditorViewContent — a minimal forward-only JSON value reader.
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

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposedControl)
            {
                _disposedControl = true;
                try
                {
                    HandleCreated -= OnHandleCreated;
                    if (_webView != null)
                    {
                        if (_webView.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                        }
                        _webView.Dispose();
                        _webView = null;
                    }
                    if (_tempDir != null && Directory.Exists(_tempDir))
                    {
                        try { Directory.Delete(_tempDir, true); } catch { }
                    }
                }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}
