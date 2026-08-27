using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
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
    public class ModernEmbeditorViewContent : AbstractViewContent, IMonacoEditorHost
    {
        // Converge step 3: _panel is now the reusable MonacoEditorControl (which IS a Panel), so every
        // designer/marshal/Control site that treated it as a Panel still compiles. The control owns the
        // WebView2 + page nav + JS<->C# transport + inbound dispatch; this view implements IMonacoEditorHost.
        private MonacoEditorControl _panel;
        private bool _isInitialized;   // mirrored from the control via OnEditorNavigationCompleted

        private string _title;
        private string _sourceText;
        private string _language;
        private bool _isDark = true;
        private List<int[]> _editableRanges; // 1-based inclusive [start,end] embed-slot ranges
        private readonly string _procedureName;     // set when opened from the picker (enables save)
        private List<string> _originalSlotTexts;     // baseline slot contents for change detection
        private readonly bool _saveEnabled;
        private readonly string _lspFileName;        // synthetic .clw URI for LSP completion/hover requests
        // Real-module LSP context (#56): when captured, _lspFileName is the generated module's REAL path and
        // every LSP-bound buffer is wrapped with its MEMBER header (+1 line) — Monaco itself is untouched.
        private readonly Services.EmbedLspContext _lspContext;

        // LIVE-LINKED mode (ticket a5bbf005): this tab holds Clarion's native embeditor OPEN underneath the
        // floating Monaco, so save writes straight back into it (no re-open / no locator re-type). At most ONE
        // tab is live at a time (native single-embeditor lock); _liveInstance is that tab. Switching away, cancel,
        // or save RELEASES the native embed and demotes the tab to a passive snapshot (which still saves via the
        // proven re-open Save path). We never re-link on activate — a re-focused tab stays a snapshot.
        private bool _liveLinked;
        // The switch-away release ARMS only after this live tab has been foreground at least once. ShowView lands
        // the new tab in the BACKGROUND, so the open-time active-window churn (app tree still active, then we
        // activate the tab) would otherwise fire OnActiveWindowChangedForLive with active!=live and release the
        // native embed the instant it opened — demoting the very first save off the live fast path (no save-and-
        // exit). Gate the release on this flag so open-time churn can't drop the embed. (a5bbf005 probe fix)
        private bool _liveActivatedOnce;
        private static ModernEmbeditorViewContent _liveInstance;   // the ONE tab currently holding an open native embed, or null
        /// <summary>True while any CA embeditor overlay/tab holds the native embed open — the state whose
        /// teardown by Clarion's error navigation is disruptive. Read by ErrorPadNavigationInterceptor's
        /// dispatch rule (d3ab083a).</summary>
        internal static bool HasLiveOverlay { get { return _liveInstance != null; } }

        /// <summary>
        /// The CA Embeditor OVERLAY instance currently covering the open native embeditor, or null when
        /// none (no live embed, or the live instance is a plain tab, not the overlay). Read by the Data
        /// pad's insert/goto routing (a9aa19ba): with the overlay up, the native ICSharpCode buffer is
        /// INVISIBLE under Monaco — and Monaco's save would discard anything written into it — so pad
        /// actions must target the overlay's Monaco editor, never the covered native text area.
        /// </summary>
        internal static ModernEmbeditorViewContent LiveOverlayInstance
        {
            get
            {
                var live = _liveInstance;
                return (live != null && live._embedOverlay) ? live : null;
            }
        }

        /// <summary>
        /// d3ab083a dispatch target: show an Errors-pane row INSIDE the live overlay instead of letting
        /// the native route re-host the embeditor (the reload). Mapping is SELF-ANCHORED: locate a
        /// distinctive run of the pwee document's opening lines inside the on-disk generated module —
        /// one mechanism gives module identity (run found = this IS our module), the module→pwee line
        /// offset, and validation (the mapped line must byte-match). No dependence on the fork's
        /// background CC thread (CommonGenEditor.BackgroundLineNumOffset proved unpopulated in the
        /// overlay context — first live test fell through silently). Every gate LOGS its reason; any
        /// failure returns false = native fallback. line0/col0 are 0-based (Task convention).
        /// Known v1 limit: the mapped line is in the OPEN-TIME baseline's line space — unsaved embed
        /// edits ABOVE the target shift the live doc below them, so a reveal after heavy editing can be
        /// off by the inserted/removed line count. Acceptable for the verify round; noted in the log.
        /// </summary>
        internal static bool TryRevealErrorInLiveOverlay(string fileName, int line0, int col0)
        {
            try
            {
                var live = _liveInstance;
                if (live == null || !live._embedOverlay || live._panel == null)
                { ClarionAssistant.MonacoSpikeLog.Write("error reveal: no live overlay (live=" + (live != null) + ")"); return false; }

                // The native embed must still be OPEN UNDERNEATH — same test Save uses to pick its path
                // (_liveLinked && IsStillLive()), deliberately, so the two can't disagree about liveness.
                //
                // Why this is load-bearing: when an Errors row lands in generated code this overlay can't map,
                // the native fallback opens the module .clw, and THAT closes the native embed — the log says
                // "[embed-monitor] dedup reset — pwee gone, editor alive". The overlay is then ORPHANED: it
                // still holds the procedure's text and its pwee baseline, so FindPweeLineByContext still finds
                // a unique match and the reveal looks completely healthy. Revealing into it is wrong twice:
                // the developer gets a surface with no embed behind it, where Save logs
                // liveCheck(live=False,overlay=True), takes the re-open path and reports "nothing to save",
                // and Cancel blanks the buffer (John, 2026-08-07 — reachable only once the reveal started
                // raising the tab, which is what made the orphan visible instead of hidden behind the .clw).
                //
                // Returning false hands the row to the native path, which re-opens the embeditor properly and
                // re-attaches a healthy overlay. Costs one GetEmbedInfo round-trip per Errors click, checked
                // BEFORE the disk read below so the orphaned path is the cheap one.
                if (!live._liveLinked || !live.IsStillLive())
                {
                    ClarionAssistant.MonacoSpikeLog.Write("error reveal: overlay orphaned — native embed gone (liveLinked="
                        + live._liveLinked + ") — native fallback re-opens it");
                    return false;
                }
                if (string.IsNullOrEmpty(fileName) || !System.IO.File.Exists(fileName))
                { ClarionAssistant.MonacoSpikeLog.Write("error reveal: row file missing (" + (fileName ?? "null") + ")"); return false; }
                var pwee = live._pweeBaselineLines;
                if (pwee == null || pwee.Length < 4)
                { ClarionAssistant.MonacoSpikeLog.Write("error reveal: no pwee baseline captured"); return false; }

                var disk = EncodingHelper.ReadAllLines(fileName, out _);
                if (line0 < 0 || line0 >= disk.Length)
                { ClarionAssistant.MonacoSpikeLog.Write("error reveal: line0 " + line0 + " outside module (" + disk.Length + " lines)"); return false; }

                // Local-context match (the global-offset attempt proved the pwee doc is NOT a contiguous
                // module slice — it shares the header then diverges structurally, so per-click context is
                // the only mapping that holds). A found match IS the location, the validation, and the
                // module-identity proof in one.
                int pweeLine0 = FindPweeLineByContext(pwee, disk, line0);
                if (pweeLine0 < 0)
                { ClarionAssistant.MonacoSpikeLog.Write("error reveal: context for module line0 " + line0 + " not located uniquely in pwee doc — native fallback"); return false; }

                live._panel.RevealLine(pweeLine0 + 1, col0 + 1);  // 0-based → Monaco 1-based
                // FocusOwningTab, NOT BringToFront: in overlay mode BringToFront only re-asserts the Monaco
                // panel's z-order INSIDE the embeditor's host, which is invisible when some other document is
                // the foreground tab. That is reachable from the Errors pane in two clicks (John's repro,
                // 2026-08-07, errors in two procedures): click a row this overlay can't map (generated code
                // belonging to the other procedure) and the native
                // fallback opens the generated .clw as the active tab; every later row that DOES map then
                // revealed correctly — the log showed the right pwee line each time — behind the .clw, so it
                // read as "the Errors pane stopped navigating". FocusOwningTab raises the gen editor we are
                // docked over, which is the tab that actually owns this session.
                live.FocusOwningTab();
                ClarionAssistant.MonacoSpikeLog.Write("error revealed in live overlay: module line0 " + line0 +
                    " -> pwee line " + (pweeLine0 + 1) + " (context match, " + System.IO.Path.GetFileName(fileName) +
                    ") + raised owning tab");
                return true;
            }
            catch (Exception ex)
            {
                ClarionAssistant.MonacoSpikeLog.Write("TryRevealErrorInLiveOverlay error: " + ex.Message);
                return false;
            }
        }

        /// <summary>Locate the pwee-document line showing the same code as generated-module line
        /// <paramref name="line0"/>, by matching the module's local context run inside the pwee lines.
        /// Strategy: a 3-line window centered on the target must match EXACTLY ONCE; if it matches
        /// several places (boilerplate), widen to 5 then 7 lines to disambiguate; if the 3-line window
        /// matches nowhere (divergence/edits inside the window), fall back to a UNIQUE single-line
        /// match. Windows are clamped at buffer edges. -1 = no unique location (ambiguous boilerplate
        /// like a bare END, a different module, or drifted source) — caller goes native.</summary>
        private static int FindPweeLineByContext(string[] pwee, string[] disk, int line0)
        {
            for (int radius = 1; radius <= 3; radius++)
            {
                int result = MatchWindow(pwee, disk, line0, radius, out bool ambiguous);
                if (result >= 0) return result;
                if (!ambiguous)                        // window not found at all — widening can't help
                    return radius == 1 ? MatchWindow(pwee, disk, line0, 0, out _) : -1;
            }
            return -1;                                 // still ambiguous at 7 lines — give up honestly
        }

        /// <summary>Match disk[line0-radius .. line0+radius] (edge-clamped) against every position in
        /// the pwee lines. Returns the pwee index aligned to line0 when the run occurs exactly once;
        /// -1 otherwise, with <paramref name="ambiguous"/> telling multiple-hits apart from none.</summary>
        private static int MatchWindow(string[] pwee, string[] disk, int line0, int radius, out bool ambiguous)
        {
            ambiguous = false;
            int lo = Math.Max(0, line0 - radius);
            int hi = Math.Min(disk.Length - 1, line0 + radius);
            int len = hi - lo + 1;
            int found = -1;
            for (int m = 0; m + len <= pwee.Length; m++)
            {
                bool match = true;
                for (int d = 0; d < len; d++)
                    if (!string.Equals(pwee[m + d], disk[lo + d], StringComparison.Ordinal)) { match = false; break; }
                if (!match) continue;
                if (found >= 0) { ambiguous = true; return -1; }
                found = m + (line0 - lo);              // align back to the target line inside the window
            }
            return found;
        }
        private static bool _liveWatchWired;                       // one-time ActiveWorkbenchWindowChanged subscription guard
        // Generation counter bumped at every live ACQUISITION (start of a live open, in ReleaseLiveInstanceSync).
        // A deferred switch-away release captures the gen when QUEUED and no-ops if a newer live open has since
        // happened — so a stale release can never cancel a NEWER tab's embed (CC static-review finding, a5bbf005).
        private static int _liveGen;

        // EMBED OVERLAY mode (ticket a5bbf005): instead of a separate workbench tab, this view's Monaco surface
        // (_panel) is docked FILL on top of the open native embeditor's host panel (ClaGenEditor.Control, per CC's
        // probe), with the native embed alive underneath as the write-back target. There is no ShowView tab and no
        // switch-away watch — the overlay auto-hides with the Source/Design tab and tears down when the embed
        // closes. Save uses the same live fast path (SaveLive) as the tab mode.
        private bool _embedOverlay;
        private Control _overlayHost;          // the ClaGenEditor.Control panel we docked into
        private Panel _overlayCover;           // opaque shim hiding the native text area until Monaco paints (anti-flash)
        private Timer _overlayCoverSafety;     // backstop: drop the cover even if navigation-completed never arrives
        private object _overlayGenEditor;      // the ClaGenEditor view content, for the Disposed teardown backstop
        private object _overlayPwee;           // the PweeEditorDetails we attached FOR — duplicate-trigger identity (d4635694)
        private EventHandler _overlayDisposedHandler; // our subscription to ClaGenEditor.Disposed (removed on detach)
        // The CANCELLABLE close hook. Separate from _overlayDisposedHandler above on purpose: Disposed is past
        // tense and cannot be vetoed, so it can only stash. This one can ask first. (bcba6efb)
        private System.ComponentModel.CancelEventHandler _embedClosingHandler;
        private object _embedClosingWindow;    // the IWorkbenchWindow we subscribed on — needed to unsubscribe
        private System.Reflection.EventInfo _embedClosingEvt;
        // The hook is retried from HandleEmbedState, which fires on every buffer change. Without this, a
        // persistent miss would write a line per keystroke and drown the log it is meant to be diagnosed from.
        private bool _embedClosingMissLogged;
        private bool _overlayDetached;         // idempotent guard so teardown runs exactly once
        // The native embeditor chrome (its ~24px Dock=Top toolbar strip: green-check save / red-X cancel /
        // embed-nav + header) we hide while the overlay is up, so only OUR Monaco toolbar shows. Restored on
        // detach. It's a real WinForms child of SdiWorkspaceWindow, above ClaGenEditor.Control (CC probe a5bbf005).
        private readonly List<Control> _hiddenChrome = new List<Control>();
        private Control _chromeHost;           // SdiWorkspaceWindow (the chrome strips' parent) — for PerformLayout on restore
        private string _nativeHeaderText;      // captured from the hidden AppHeaderLabel ("Proc - Embeditor - (module.clw)"):
                                               // rendered as OUR clickable header, whose click opens the generated source (b1e05287)
        private ToolStrip _nativeToolStrip;    // the hidden native embeditor toolbar — we PerformClick its "Open Source"
                                               // item from our header (hidden != disabled), instead of guessing a command class (b1e05287)
        // Chrome colors captured from the native toolbar so our overlay header/toolbar follow the active Clarion
        // theme (its ToolStripProfessionalRenderer gradient + text color). CSS hex strings, null if unavailable. (b1e05287)
        private string _chromeBg1, _chromeBg2, _chromeFg;
        // The REAL native embeditor icons (save/cancel/prev+next embed/filled), extracted from the hidden ToolStrip's
        // items as PNG data-URIs → rendered pixel-perfect in our WebView2 toolbar. role → data-uri. (b1e05287)
        private readonly Dictionary<string, string> _nativeIcons = new Dictionary<string, string>();

        // File mode (ticket 564aa142): the tab edits a plain source file on disk (.clw/.inc/...) instead of
        // an embeditor snapshot. Save = encoding-preserving file write; no slot machinery, no Data pad refresh,
        // no designer. _lspFileName is the REAL path so the LSP resolves includes/symbols against the file.
        private readonly string _filePath;
        private string _fileIdentity;                // true file-ID (vol serial + file index) for tab DEDUP; resolves all aliases incl. hard links (item 3)
        private readonly bool _fileMode;
        private Encoding _fileEncoding;              // detected at open, RE-DETECTED on reload (pipeline item 4)
        private string _fileEol = "\r\n";            // detected dominant EOL at open; non-Clarion files keep their style (item 5)
        private string _fileDiskSig;                 // disk fingerprint (mtimeTicks:length) for changed-on-disk detection (item 2)
        private string _fileOverwriteArmedSig;       // the EXACT disk version the user was warned about; null = not armed (item 2)
        // Host mirror of the page's live file-mode buffer + dirty flag, so a tab close can save WITHOUT an async
        // round-trip into the WebView2 (pipeline CRITICAL — silent data loss on close).
        private string _fileLiveText;
        private bool _fileDirty;
        private bool _disposed;
        private bool _sessionTornDown;               // idempotent guard for TeardownSession (#119)

        private const string VIRTUAL_HOST = "clarion-embeditor-data";

        // Find/Replace history scope: per-version (storage layer) + per-solution (folder) + per-procedure
        // (the "This procedure" group). Resolved once from the IDE when the page first asks for source.
        private string _histSolutionPath;
        private string _histProcKey;
        private bool _histScopeResolved;

        // CA Embeditor selection snapshot — pushed by Monaco (onDidChangeCursorSelection), read by the
        // embeditor_get_selection MCP tool. Follows the saveCursor/saveBookmarks push model (no async
        // round-trip → no WebView2 re-entrancy). Written + read on the UI thread for the instance path.
        private string _selText = "";
        private int _selStartLine, _selStartCol, _selEndLine, _selEndCol;
        private bool _selHasSelection;
        private bool _selTruncated;   // JS clipped the text at the cap — surface it so a consumer never treats a partial selection as whole

        // Last selection reported by whichever tab pushed most recently. Lets the read survive a moment of
        // ambiguous focus resolution. Guarded because GetFocusedSelection() may run before focus settles.
        private static readonly object _selSnapLock = new object();
        private static Dictionary<string, object> _lastFocusedSelection;

        private static readonly List<ModernEmbeditorViewContent> _instances = new List<ModernEmbeditorViewContent>();
        private IDisposable _settingsReg;   // registration in MonacoSettingsBroadcaster (cross-surface gear-settings sync, deac3d16)
        // Set during IDE shutdown (DisposeAllForShutdown) so per-tab Dispose takes a NONINTERACTIVE recovery path —
        // no modal prompt per dirty tab (avoids a shutdown modal storm). (pipeline Run-3 adversary)
        private static volatile bool _shuttingDown;

        public override Control Control { get { return _panel; } }

        /// <summary>The procedure this tab represents (null/empty in mirror mode).</summary>
        public string ProcedureName { get { return _procedureName; } }

        /// <summary>The Modern Embeditor tab that's currently the active document, or null.</summary>
        public static ModernEmbeditorViewContent ActiveModernView()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb != null)
                {
                    // Reflect ActiveWorkbenchWindow -> ViewContent (the property is explicit-interface on the
                    // workbench itself, so GetProperty by name there returns null — go via the window).
                    var aw = GetProp(wb, "ActiveWorkbenchWindow");
                    if (aw != null)
                    {
                        var vc = GetProp(aw, "ActiveViewContent") ?? GetProp(aw, "ViewContent");
                        var m = vc as ModernEmbeditorViewContent;
                        if (m != null) return m;
                    }
                }
                // Fallback: if exactly one Modern Embeditor is open, it's unambiguous.
                lock (_instances) { if (_instances.Count == 1) return _instances[0]; }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The Modern Embeditor view that is the FOCUSED active document, or null. Unlike ActiveModernView() this
        /// does NOT fall back to the lone open tab: the Data pad routes by focus, so a Modern tab sitting unfocused
        /// in the background must resolve to null (no editor) rather than silently becoming the action target.
        /// </summary>
        public static ModernEmbeditorViewContent FocusedModernView()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb == null) return null;
                var aw = GetProp(wb, "ActiveWorkbenchWindow");
                if (aw == null) return null;
                var vc = GetProp(aw, "ActiveViewContent") ?? GetProp(aw, "ViewContent");
                return vc as ModernEmbeditorViewContent;
            }
            catch { return null; }
        }

        private static object GetProp(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var p = obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Data symbols (locals/globals/structures) for this procedure, from the LSP document-symbol tree
        /// over the opened source. Each entry: { name, kind, detail }. Empty if the LSP isn't running.
        /// </summary>
        public List<Dictionary<string, object>> GetDataSymbols()
        {
            var result = new List<Dictionary<string, object>>();
            try
            {
                var lsp = LspClient.Active;
                if (lsp == null) return result;
                var resp = lsp.GetDocumentSymbols(_lspFileName, LspBuffer(_sourceText));
                object res = (resp != null && resp.ContainsKey("result")) ? resp["result"] : null;
                CollectSymbols(res, result);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetDataSymbols: " + ex.Message); }
            return result;
        }

        // {action:"documentStructure"} — outline tree for the current buffer, for the structure fly-out.
        // In file mode this is the whole module (reliable). In embed/slot mode the buffer is a procedure
        // slice with a MEMBER header, so the outline is partial — the page decides what to show via fileMode.
        private void HandleDocumentStructure(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                var symbols = new List<Dictionary<string, object>>();
                try
                {
                    var resp = SharedLspBridge.GetDocumentSymbols(_lspFileName, LspBuffer(buffer));
                    object res = (resp != null && resp.ContainsKey("result")) ? resp["result"] : null;
                    symbols = DocumentOutlineBuilder.Build(res, MonacoLine1);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] HandleDocumentStructure: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "symbols", symbols }, { "fileMode", _fileMode } });
            });
        }

        // LSP documentSymbol returns either DocumentSymbol[] (hierarchical, has children) or
        // SymbolInformation[] (flat). Collect leaf names + kinds from either shape.
        private static void CollectSymbols(object node, List<Dictionary<string, object>> into)
        {
            var list = node as System.Collections.IEnumerable;
            if (list == null) return;
            foreach (var item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                string name = d.ContainsKey("name") ? d["name"] as string : null;
                int kind = 0;
                if (d.ContainsKey("kind")) { try { kind = Convert.ToInt32(d["kind"]); } catch { } }
                string detail = d.ContainsKey("detail") ? d["detail"] as string : null;
                if (!string.IsNullOrEmpty(name))
                    into.Add(new Dictionary<string, object> { { "name", name }, { "kind", kind }, { "detail", detail } });
                if (d.ContainsKey("children")) CollectSymbols(d["children"], into);
            }
        }

        /// <summary>Recursively flatten a parsed FieldDef (with nested QUEUE/GROUP members) into the
        /// JSON-ready dictionary the Data pad consumes. Children are only emitted when present.</summary>
        private static Dictionary<string, object> FieldToDict(ClarionAppDataReader.FieldDef d)
        {
            var dict = new Dictionary<string, object> { { "name", d.Name }, { "type", d.Type } };
            // Display metadata — only present when sourced from the .txa (ParseTxaProcedureData).
            if (!string.IsNullOrEmpty(d.Picture)) dict["picture"] = d.Picture;
            if (!string.IsNullOrEmpty(d.Prompt)) dict["prompt"] = d.Prompt;
            if (!string.IsNullOrEmpty(d.Header)) dict["header"] = d.Header;
            // Detail-panel extras (a9aa19ba) — emitted only when present so the payload stays lean.
            if (!string.IsNullOrEmpty(d.Tooltip)) dict["tooltip"] = d.Tooltip;
            if (!string.IsNullOrEmpty(d.Message)) dict["message"] = d.Message;
            if (!string.IsNullOrEmpty(d.TypeMode)) dict["typemode"] = d.TypeMode;
            if (!string.IsNullOrEmpty(d.Justify)) dict["justify"] = d.Justify;
            if (!string.IsNullOrEmpty(d.Description)) dict["desc"] = d.Description;
            if (!string.IsNullOrEmpty(d.DerivedFrom)) dict["derived"] = d.DerivedFrom;
            if (d.Children != null && d.Children.Count > 0)
            {
                var kids = new List<object>();
                foreach (var c in d.Children) kids.Add(FieldToDict(c));
                dict["children"] = kids;
            }
            return dict;
        }

        // Whole-app .txa text, exported on the UI thread (open + save) and parsed per-proc on the pad's
        // background refresh. Static so it's shared across all Modern Embeditor tabs for the same app.
        private static readonly object _txaLock = new object();
        private static string _wholeAppTxa;

        // Live dictionary snapshot (master, proc-independent): table name -> TableDef (cols w/ pictures +
        // GROUP nesting, keys). Read from the IDE object model on the UI thread; the Other Files schema
        // source (replaces the .dcv). See reference_clarion_dict_object_model.
        private static readonly object _liveLock = new object();
        private static Dictionary<string, ClarionAppDataReader.TableDef> _liveTables;

        /// <summary>
        /// Refresh the Modern Data pad's IDE-sourced caches: (1) the whole-app .txa text (Local/Global Data),
        /// and (2) a snapshot of the live dictionary tables (Other Files schema). BOTH require the UI thread
        /// (they touch the IDE / drive a silent whole-app export) — never call from GetPadData, which runs on
        /// a background thread (a background export/IDE-poke is the re-entrancy that locks the IDE). Each
        /// source is independent and best-effort: on failure the prior cache is kept and GetPadData falls
        /// back (embeditor-source for locals, .dcv for Other Files).
        /// </summary>
        public static void RefreshPadSources()
        {
            // (1) Whole-app .txa — silent Export(path, all=TRUE), validated. Source for Local/Global Data.
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "ClarionModernData_wholeapp.txa");
                string res = new AppTreeService().ExportTxa(tmp);
                if (!string.IsNullOrEmpty(res) && !res.StartsWith("Error") && File.Exists(tmp))
                {
                    // Clarion writes the .txa as ANSI, so the no-encoding overload turned every
                    // high-bit character in exported Local/Global Data into mojibake in the pad.
                    string text = EncodingHelper.ReadAllText(tmp, out _);
                    if (!string.IsNullOrEmpty(text)) lock (_txaLock) { _wholeAppTxa = text; }
                }
            }
            catch { /* keep prior .txa cache */ }

            // (2) Live dictionary snapshot — the master Tables read from App.FileSchema.DataDictionary.
            try
            {
                var tables = new AppTreeService().ReadLiveDictionaryTables();
                if (tables != null && tables.Count > 0)
                {
                    var map = new Dictionary<string, ClarionAppDataReader.TableDef>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in tables)
                        if (!string.IsNullOrEmpty(t.Name)) map[t.Name] = t;
                    lock (_liveLock) { _liveTables = map; }
                }
            }
            catch { /* keep prior dict cache; GetOtherFiles falls back to the .dcv */ }
        }

        // Identity (.app file path) of the app the pad-source caches were last loaded FOR, via the SELECTION
        // path. The caches (_wholeAppTxa/_liveTables) are process-wide static and BuildPadData consumes them by
        // procedure name only — so switching .app must force a re-export, otherwise a same-named proc in the new
        // app would render the previous app's Local/Global/Tables data. Guarded by _txaLock.
        private static string _padSourcesAppKey;

        /// <summary>
        /// Ensure the pad's IDE-sourced caches (whole-app .txa + live dict snapshot) are loaded for the CURRENT
        /// app, re-exporting only when (a) nothing is cached yet, or (b) the active .app changed since the last
        /// selection load. The whole-app .txa carries EVERY procedure, so within one app a selection switch to
        /// any proc reads straight from cache — no per-click export. Used by the app-tree SELECTION path, which
        /// has no open/save hook to populate the caches. UI thread (delegates to RefreshPadSources).
        ///
        /// "Loaded" is keyed on the .txa being present + the app identity — NOT on dictionary non-emptiness: a
        /// dictionary-less app legitimately yields an empty _liveTables, and gating on that would re-export the
        /// whole app on every single click (a multi-second UI stall). The dictionary, when present, is loaded as
        /// a side effect of the same RefreshPadSources call.
        /// </summary>
        public static void EnsurePadSourcesLoaded()
        {
            string appKey = TryGetCurrentAppKey();
            bool appChanged, needLoad;
            lock (_txaLock)
            {
                appChanged = !string.Equals(_padSourcesAppKey, appKey, StringComparison.OrdinalIgnoreCase);
                needLoad = string.IsNullOrEmpty(_wholeAppTxa) || appChanged;
            }
            if (!needLoad) return;

            // APP SWITCH: drop the prior app's caches BEFORE refreshing so a failed OR empty refresh can never
            // serve the previous app's .txa or dictionary tables under the new app's procedure names (cross-app
            // isolation). RefreshPadSources is best-effort (keeps prior cache on error) and only overwrites
            // _liveTables when the dict read returns >0 rows — so without this clear, a failed export or a
            // dictionary-less new app would leave stale data behind. Within the SAME app we keep the prior cache
            // (no clear) so a transient export hiccup falls back gracefully.
            if (appChanged)
            {
                lock (_txaLock) { _wholeAppTxa = null; }
                lock (_liveLock) { _liveTables = null; }
            }

            RefreshPadSources();

            // Commit the app key ONLY when the .txa actually loaded for this app. If the export failed (txa still
            // empty), leave the key stale so the next tick retries — and since we cleared the caches above on an
            // app change, BuildPadData has no prior-app data to fall back on (it shows empty, not wrong-app data).
            //
            // FRESHNESS CONTRACT (selection/populate-only mode): the whole-app .txa + dict snapshot are exported
            // ONCE per app and reused across selection clicks. They are kept fresh by the existing open/save hooks,
            // native proc-change refresh, and the pad's own variable add/edit/delete (ScheduleAddRefresh). Edits
            // made through OTHER IDE surfaces (e.g. Clarion's native dictionary editor) while ONLY browsing tree
            // selections are not reflected until one of those events fires — an accepted trade-off for a read-only
            // quick-view that avoids a multi-second whole-app export on every click.
            lock (_txaLock) { _padSourcesAppKey = string.IsNullOrEmpty(_wholeAppTxa) ? null : appKey; }
        }

        // Current open .app identity (file path, else name) via pure managed reflection; null when no app open.
        private static string TryGetCurrentAppKey()
        {
            try
            {
                var info = new AppTreeService().GetAppInfo();
                if (info != null)
                {
                    object fn;
                    if (info.TryGetValue("fileName", out fn) && fn != null && !string.IsNullOrEmpty(fn.ToString()))
                        return fn.ToString();
                    object nm;
                    if (info.TryGetValue("name", out nm) && nm != null && !string.IsNullOrEmpty(nm.ToString()))
                        return nm.ToString();
                }
            }
            catch { }
            return null;
        }

        // Parsed dictionary (.dcv) tables, cached by path + mtime so we re-parse only when Clarion's
        // Auto Export/Import rewrites the .dcv. Parsing is pure file I/O + XML (safe on the bg thread).
        private static readonly object _dcvLock = new object();
        private static string _dcvPathCached;
        private static DateTime _dcvMtimeCached;
        private static List<ClarionAppDataReader.TableDef> _dcvTablesCached;

        private static List<ClarionAppDataReader.TableDef> GetDcvTablesCached(string dcvPath)
        {
            if (string.IsNullOrEmpty(dcvPath) || !File.Exists(dcvPath)) return null;
            var mtime = File.GetLastWriteTimeUtc(dcvPath);
            lock (_dcvLock)
            {
                if (_dcvTablesCached != null && _dcvPathCached == dcvPath && _dcvMtimeCached == mtime)
                    return _dcvTablesCached;
            }
            var parsed = ClarionAppDataReader.ParseDcvTables(dcvPath);
            lock (_dcvLock) { _dcvPathCached = dcvPath; _dcvMtimeCached = mtime; _dcvTablesCached = parsed; }
            return parsed;
        }

        // The dictionary .dcv text export beside the .dct (Clarion Auto Export/Import). Default ext .dcv;
        // fall back to any *.dcv in the dict folder matching the dict base name (then any) for ext variance.
        private static string ResolveDcvPath(string dctPath)
        {
            if (string.IsNullOrEmpty(dctPath)) return null;
            try
            {
                string dcv = Path.ChangeExtension(dctPath, ".dcv");
                if (File.Exists(dcv)) return dcv;
                string dir = Path.GetDirectoryName(dctPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string baseName = Path.GetFileNameWithoutExtension(dctPath);
                    var found = Directory.GetFiles(dir, "*.dcv");
                    foreach (var x in found)
                        if (string.Equals(Path.GetFileNameWithoutExtension(x), baseName, StringComparison.OrdinalIgnoreCase))
                            return x;
                    if (found.Length > 0) return found[0];
                }
            }
            catch { }
            return null;
        }

        // Field → JSON dict for an Other Files column. Name is UNPREFIXED (the prefix is shown once at the
        // file header; the frontend prepends it for double-click insert). Carries detail for the "+" panel.
        private static Dictionary<string, object> ColToDict(ClarionAppDataReader.FieldDef f)
        {
            var d = new Dictionary<string, object> { { "name", f.Name }, { "type", f.Type ?? "" } };
            if (!string.IsNullOrEmpty(f.Picture)) d["picture"] = f.Picture;
            // Show a description only when it adds info — the dict often defaults it to the field name.
            if (!string.IsNullOrEmpty(f.Description) && !string.Equals(f.Description, f.Name, StringComparison.OrdinalIgnoreCase))
                d["description"] = f.Description;
            if (!string.IsNullOrEmpty(f.DerivedFrom)) d["derivedFrom"] = f.DerivedFrom;
            if (!string.IsNullOrEmpty(f.Prompt)) d["prompt"] = f.Prompt;
            if (!string.IsNullOrEmpty(f.Header)) d["header"] = f.Header;
            // Column-panel extras (a9aa19ba round 2) — live-dictionary detail, emitted only when present.
            if (!string.IsNullOrEmpty(f.ExternalName)) d["extname"] = f.ExternalName;
            if (!string.IsNullOrEmpty(f.InitialValue)) d["initial"] = f.InitialValue;
            if (!string.IsNullOrEmpty(f.Dimensions)) d["dims"] = f.Dimensions;
            if (!string.IsNullOrEmpty(f.CaseText)) d["casetext"] = f.CaseText;
            if (!string.IsNullOrEmpty(f.Justify)) d["justify"] = f.Justify;
            if (!string.IsNullOrEmpty(f.TypeMode)) d["typemode"] = f.TypeMode;
            if (!string.IsNullOrEmpty(f.HelpId)) d["helpid"] = f.HelpId;
            if (!string.IsNullOrEmpty(f.Tooltip)) d["tooltip"] = f.Tooltip;
            if (!string.IsNullOrEmpty(f.Message)) d["message"] = f.Message;
            if (!string.IsNullOrEmpty(f.RowPicture)) d["rowpic"] = f.RowPicture;
            if (!string.IsNullOrEmpty(f.Validity)) d["validity"] = f.Validity;
            if (f.Flags != null && f.Flags.Count > 0) d["flags"] = f.Flags;
            if (f.Children != null && f.Children.Count > 0)
            {
                var kids = new List<object>();
                foreach (var c in f.Children) kids.Add(ColToDict(c));
                d["children"] = kids;
            }
            return d;
        }

        // Assemble the FILE attribute line for the table-detail panel, e.g.
        // DRIVER('MSSQL','/TRUSTEDCONNECTION=TRUE'),OWNER(Glo:Connection),NAME('Person.Address'),PRE(Add),BINDABLE,THREAD
        private static string BuildTableAttributes(ClarionAppDataReader.TableDef t)
        {
            var sb = new StringBuilder();
            Action<string> add = s => { if (sb.Length > 0) sb.Append(","); sb.Append(s); };
            if (!string.IsNullOrEmpty(t.Driver))
                add("DRIVER('" + t.Driver + "'" +
                    (!string.IsNullOrEmpty(t.DriverOptions) ? ",'" + t.DriverOptions + "'" : "") + ")");
            if (!string.IsNullOrEmpty(t.Owner)) add("OWNER(" + t.Owner + ")");
            if (!string.IsNullOrEmpty(t.FullName)) add("NAME('" + t.FullName + "')");
            if (!string.IsNullOrEmpty(t.Prefix)) add("PRE(" + t.Prefix + ")");
            if (t.Bindable) add("BINDABLE");
            if (t.Threaded) add("THREAD");
            return sb.ToString();
        }

        // Discrete table properties for the master/detail table panel (a9aa19ba) — the fused `attributes`
        // string stays for the panel's declaration line; these feed the label/value rows and chips.
        private static void AddTableDetailFields(Dictionary<string, object> d, ClarionAppDataReader.TableDef t)
        {
            if (!string.IsNullOrEmpty(t.Driver)) d["driver"] = t.Driver;
            if (!string.IsNullOrEmpty(t.DriverOptions)) d["driverOpts"] = t.DriverOptions;
            if (!string.IsNullOrEmpty(t.Owner)) d["owner"] = t.Owner;
            if (!string.IsNullOrEmpty(t.FullName)) d["fullName"] = t.FullName;
            d["threaded"] = t.Threaded;
            d["bindable"] = t.Bindable;
        }

        // KeyDef list → JSON dicts for the Other Files key rows (rich form). Falls back to legacy name-only.
        private static List<object> KeysToDicts(ClarionAppDataReader.TableDef t)
        {
            var keys = new List<object>();
            if (t.KeyDefs.Count > 0)
                foreach (var k in t.KeyDefs)
                {
                    var comps = new List<object>();
                    foreach (var c in k.Components) comps.Add(ColToDict(c));
                    keys.Add(new Dictionary<string, object>
                    {
                        { "name", k.Name }, { "components", comps }, { "keyType", k.KeyType },
                        { "primary", k.Primary }, { "unique", k.Unique },
                        { "caseSensitive", k.CaseSensitive }, { "autoNumber", k.AutoNumber },
                        { "excludeEmpty", k.ExcludeEmpty }, { "description", k.Description ?? "" }
                    });
                }
            else
                foreach (var kn in t.Keys) keys.Add(new Dictionary<string, object> { { "name", kn } });
            return keys;
        }

        // RelationDef list → JSON dicts for the Relations sub-folder. Each row is named by the related table;
        // the "+" detail carries the relation type, primary/foreign keys, and the column mappings.
        private static List<object> RelationsToDicts(ClarionAppDataReader.TableDef t)
        {
            var rels = new List<object>();
            foreach (var r in t.Relations)
            {
                var maps = new List<object>();
                foreach (var m in r.Mappings)
                    maps.Add(new Dictionary<string, object> { { "from", m.From ?? "" }, { "to", m.To ?? "" } });
                rels.Add(new Dictionary<string, object>
                {
                    { "name", r.Name ?? "" }, { "type", r.Type ?? "" },
                    { "primaryKey", r.PrimaryKey ?? "" }, { "foreignKey", r.ForeignKey ?? "" },
                    { "mappings", maps }
                });
            }
            return rels;
        }

        /// <summary>
        /// The procedure's "Other Files": the [FILES][OTHERS] names from the cached whole-app .txa, paired
        /// with their schema (columns w/ pictures + GROUP nesting, keys) from the dictionary .dcv export.
        /// If the .dcv isn't available, the files are still listed by name so the section appears.
        /// </summary>
        private static List<Dictionary<string, object>> GetOtherFiles(string txa, string procedureName)
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                if (string.IsNullOrEmpty(txa) || string.IsNullOrEmpty(procedureName)) return outp;
                var names = ClarionAppDataReader.ParseTxaOtherFiles(txa, procedureName);
                if (names.Count == 0) return outp;

                // Schema source: prefer the LIVE dictionary snapshot (always current, no file dependency);
                // fall back to the dictionary .dcv text export only if the live snapshot isn't available.
                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }
                List<ClarionAppDataReader.TableDef> dcvTables = null; // lazily loaded fallback

                foreach (var n in names)
                {
                    ClarionAppDataReader.TableDef t = null;
                    if (live != null) live.TryGetValue(n, out t);
                    if (t == null)
                    {
                        if (dcvTables == null)
                            dcvTables = GetDcvTablesCached(ResolveDcvPath(ClarionAppDataReader.ParseTxaDictionaryPath(txa)))
                                        ?? new List<ClarionAppDataReader.TableDef>();
                        t = dcvTables.Find(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                    }
                    if (t == null)
                    {
                        // Listed but no schema (no live dict / .dcv yet) — still show the file name.
                        outp.Add(new Dictionary<string, object>
                        {
                            { "name", n }, { "prefix", "" }, { "attributes", "" }, { "description", "" },
                            { "columns", new List<object>() }, { "keys", new List<object>() }
                        });
                        continue;
                    }
                    var cols = new List<object>();
                    foreach (var f in t.Fields) cols.Add(ColToDict(f));
                    var td = new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "prefix", t.Prefix },
                        { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                        { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) }
                    };
                    AddTableDetailFields(td, t);
                    outp.Add(td);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetOtherFiles: " + ex.Message); }
            return outp;
        }

        /// <summary>
        /// The procedure's PRIMARY browse file (Clarion's "File-Browsing List Box") enriched from the live
        /// dictionary, carrying the browse KEY. Returns 0 or 1 entries (a list keeps the frontend renderer
        /// uniform with Other Files / Declared Tables).
        /// </summary>
        private static List<Dictionary<string, object>> GetBrowseFiles(string txa, string procedureName)
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                if (string.IsNullOrEmpty(txa) || string.IsNullOrEmpty(procedureName)) return outp;
                var pf = ClarionAppDataReader.ParseTxaPrimaryFile(txa, procedureName);
                if (pf == null || string.IsNullOrEmpty(pf.File)) return outp;

                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }
                ClarionAppDataReader.TableDef t = null;
                if (live != null) live.TryGetValue(pf.File, out t);

                Dictionary<string, object> d;
                if (t != null)
                {
                    var cols = new List<object>();
                    foreach (var f in t.Fields) cols.Add(ColToDict(f));
                    d = new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "prefix", t.Prefix },
                        { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                        { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) }
                    };
                    AddTableDetailFields(d, t);
                }
                else
                {
                    // Listed but no live-dict schema yet — still show the file + key.
                    d = new Dictionary<string, object>
                    {
                        { "name", pf.File }, { "prefix", "" }, { "attributes", "" }, { "description", "" },
                        { "columns", new List<object>() }, { "keys", new List<object>() }
                    };
                }
                d["browseKey"] = pf.Key ?? "";
                outp.Add(d);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetBrowseFiles: " + ex.Message); }
            return outp;
        }

        /// <summary>
        /// The procedure's per-template-instance FILE SCOPES exactly as Clarion's native Data/Tables pad groups
        /// them ("File-Browsing List Box", "Update Record on Disk", "Relation Tree Viewing List Box", ...), read
        /// LIVE from the FileSchemaTree (see <see cref="FileSchemaScopeReader"/>). Each scope's attached file(s)
        /// are enriched with full dictionary schema (columns w/ pictures + GROUP nesting, keys, relations) from
        /// the live snapshot, so each renders identically to Other Files / Declared Tables.
        ///
        /// Returns an empty list when the live tree isn't reachable OR is showing a different procedure than
        /// <paramref name="procedureName"/> (the reader fails closed) — the caller then falls back to the flat
        /// .txa browse/other parsing. Only files that resolve in the live dictionary are emitted (a scope whose
        /// files all fail to resolve is dropped), so a stray non-file node never produces an empty table card.
        /// </summary>
        private static List<Dictionary<string, object>> GetFileScopes(string procedureName)
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                var scopes = FileSchemaScopeReader.ReadFileScopes(procedureName);
                if (scopes == null || scopes.Count == 0) return outp;

                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }
                if (live == null) return outp;   // no schema to enrich with → let the txa fallback render instead

                foreach (var sc in scopes)
                {
                    var files = new List<object>();
                    foreach (var fr in sc.Files)
                    {
                        ClarionAppDataReader.TableDef t;
                        if (fr == null || string.IsNullOrEmpty(fr.Name) || !live.TryGetValue(fr.Name, out t) || t == null) continue;
                        var cols = new List<object>();
                        foreach (var f in t.Fields) cols.Add(ColToDict(f));
                        var fd = new Dictionary<string, object>
                        {
                            { "name", t.Name }, { "prefix", t.Prefix },
                            { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                            { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) },
                            { "depth", fr.Depth }   // relation-tree nesting depth → indented rendering in the File Schematic
                        };
                        AddTableDetailFields(fd, t);
                        files.Add(fd);
                    }
                    if (files.Count == 0) continue;   // no resolvable file → drop the scope (don't show an empty card)
                    outp.Add(new Dictionary<string, object>
                    {
                        { "label", sc.Label }, { "instance", sc.Instance }, { "files", files }
                    });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetFileScopes: " + ex.Message); }
            return outp;
        }

        /// <summary>
        /// Combined data payload for the Modern Data pad: the procedure's local symbols (LSP) plus the
        /// dictionary tables it references (parsed from the generated &lt;app&gt;.clw, filtered to used ones).
        /// </summary>
        public Dictionary<string, object> GetPadData()
        {
            return BuildPadData(_procedureName, _sourceText);
        }

        /// <summary>
        /// Procedure-name-keyed Data pad payload: builds the same data as the instance GetPadData()
        /// from a procedure name + source text, with NO Modern view instance — this is what lets the pad
        /// serve the native (PWEE) embeditor too. Sources: the static whole-app .txa cache (RefreshPadSources)
        /// + the live dictionary snapshot + AppTreeService. <paramref name="sourceText"/> feeds the Routines
        /// list and the Local Data fallback (used only when the .txa isn't cached yet); pass the focused
        /// editor's buffer (the Modern mirror, or the native embeditor source).
        /// </summary>
        public static Dictionary<string, object> BuildPadData(string procedureName, string sourceText)
        {
            var locals = new List<Dictionary<string, object>>();
            var routines = new List<Dictionary<string, object>>();
            var localProcedures = new List<Dictionary<string, object>>();
            var globals = new List<Dictionary<string, object>>();
            var otherFiles = new List<Dictionary<string, object>>();
            var browseFiles = new List<Dictionary<string, object>>();
            var fileScopes = new List<Dictionary<string, object>>();
            try
            {
                // Prefer the AUTHORITATIVE .txa source (declaration order + pictures + exact Clarion item
                // set). Falls back to the embeditor-source parse when the whole-app .txa isn't cached yet.
                List<ClarionAppDataReader.FieldDef> localDefs = null;
                string txa; lock (_txaLock) { txa = _wholeAppTxa; }
                if (!string.IsNullOrEmpty(txa) && !string.IsNullOrEmpty(procedureName))
                {
                    var fromTxa = ClarionAppDataReader.ParseTxaProcedureData(txa, procedureName);
                    if (fromTxa.Count > 0) localDefs = fromTxa;
                }
                if (localDefs == null)
                    localDefs = ClarionAppDataReader.ParseLocalData(sourceText, procedureName);

                foreach (var d in localDefs)
                    locals.Add(FieldToDict(d));

                foreach (var r in ClarionAppDataReader.ParseRoutines(sourceText, procedureName))
                    routines.Add(new Dictionary<string, object> { { "name", r.Name }, { "line", r.Line } });

                foreach (var p in ClarionAppDataReader.ParseLocalProcedures(sourceText, procedureName))
                    localProcedures.Add(new Dictionary<string, object> { { "name", p.Name }, { "line", p.Line } });

                // Global Data: prefer the .txa [PROGRAM][DATA] — the developer-registered globals ONLY
                // (nested + pictures, matching Clarion's pad). When the .txa is cached it's authoritative
                // even if empty (an app with no dev globals shows none). Fall back to the generated
                // <app>.clw globals only when no .txa is available yet.
                List<ClarionAppDataReader.FieldDef> globalDefs;
                if (!string.IsNullOrEmpty(txa))
                {
                    globalDefs = ClarionAppDataReader.ParseTxaGlobalData(txa);
                }
                else
                {
                    string appClw = ClarionAppDataReader.FindAppClwPath();
                    globalDefs = appClw != null
                        ? ClarionAppDataReader.ParseGlobalData(appClw)
                        : new List<ClarionAppDataReader.FieldDef>();
                }
                foreach (var g in globalDefs)
                    globals.Add(FieldToDict(g));

                // Other Files: the proc's [FILES][OTHERS] names paired with dictionary (.dcv) schema.
                otherFiles = GetOtherFiles(txa, procedureName);

                // File scopes: ALL per-template-instance file groups the native Data/Tables pad shows ("File-
                // Browsing List Box", "Update Record on Disk", "Relation Tree...", ...), read LIVE from the
                // FileSchemaTree. When this resolves it SUPERSEDES the flat .txa browse parse below — the browse
                // is itself one of these instance scopes, so emitting both would double-list it. The reader fails
                // closed (empty) when the docked tree isn't reachable or is showing a different procedure, in
                // which case we keep the .txa browse fallback so the section still renders something.
                fileScopes = GetFileScopes(procedureName);
                if (fileScopes.Count == 0)
                {
                    // File-Browsing List Box: the proc's [FILES][PRIMARY] file + [KEY], dict-enriched.
                    browseFiles = GetBrowseFiles(txa, procedureName);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetPadData parse: " + ex.Message); }

            var moduleData = new List<Dictionary<string, object>>();
            try
            {
                string modClw = ClarionAppDataReader.FindModuleClwForProcedure(procedureName);
                foreach (var d in ClarionAppDataReader.ParseModuleData(modClw))
                    moduleData.Add(new Dictionary<string, object> { { "name", d.Name }, { "type", d.Type } });
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] ParseModuleData: " + ex.Message); }

            var procedures = new List<Dictionary<string, object>>();
            try
            {
                foreach (var p in new AppTreeService().GetProcedureDetails())
                {
                    string n = (p != null && p.ContainsKey("name")) ? p["name"]?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    string proto = p.ContainsKey("prototype") ? p["prototype"]?.ToString() : null;
                    procedures.Add(new Dictionary<string, object> { { "name", n }, { "params", ExtractParamList(proto) } });
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] procedures: " + ex.Message); }

            var data = new Dictionary<string, object>
            {
                { "procedure", procedureName ?? "" },
                { "locals", locals },
                { "routines", routines },
                { "localProcedures", localProcedures },
                { "moduleData", moduleData },
                { "globals", globals },
                { "otherFiles", otherFiles },
                { "browseFiles", browseFiles },
                { "fileScopes", fileScopes },
                { "tables", GetDeclaredTables() },
                { "procedures", procedures }
            };
            return data;
        }

        /// <summary>
        /// Pull the parameter list "(...)" out of a Clarion prototype so the Procedures pad can show it
        /// (e.g. "PROCEDURE(LONG id),LONG" -> "(LONG id)"). Returns "" when the prototype has no parens.
        /// </summary>
        private static string ExtractParamList(string prototype)
        {
            if (string.IsNullOrEmpty(prototype)) return "";
            int open = prototype.IndexOf('(');
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < prototype.Length; i++)
            {
                if (prototype[i] == '(') depth++;
                else if (prototype[i] == ')') { depth--; if (depth == 0) return prototype.Substring(open, i - open + 1); }
            }
            return prototype.Substring(open); // unbalanced — take the rest
        }

        /// <summary>Navigate this editor to a ROUTINE's declaration (Modern Data pad "go to routine" button).</summary>
        public void GotoRoutine(string name)
        {
            _panel?.GotoRoutine(name);
        }

        /// <summary>
        /// Raise the workbench TAB this embed session actually lives in, for this procedure. Returns true if a
        /// session was found.
        ///
        /// Distinct from <see cref="TryFocusExisting"/> on purpose. That one ends in BringToFront, which in
        /// OVERLAY mode only re-asserts Monaco's z-order inside the native embeditor's host panel — correct when
        /// the embed document is already the foreground tab (its original caller is the save path), useless to a
        /// caller sitting on a DIFFERENT document, who sees nothing happen. Search results are exactly that
        /// caller: the user is on the results tab and expects the click to take them to the code.
        /// </summary>
        public static bool TryFocusOwningTab(string procName)
        {
            if (string.IsNullOrWhiteSpace(procName)) return false;
            ModernEmbeditorViewContent found = null;
            lock (_instances)
            {
                foreach (var inst in _instances)
                    if (string.Equals(inst._procedureName, procName, StringComparison.OrdinalIgnoreCase)) { found = inst; break; }
            }
            if (found == null) return false;
            found.FocusOwningTab();
            return true;
        }

        /// <summary>Raise whichever tab owns this session: our own view content in tab mode, or the NATIVE gen
        /// editor we are docked over in overlay mode (we aren't a tab at all there).</summary>
        private void FocusOwningTab()
        {
            // Tab mode: our own view IS the tab, and BringToFront already defers the SelectWindow safely.
            if (!_embedOverlay) { BringToFront(); return; }

            var ed = _overlayGenEditor;
            if (ed == null) { BringToFront(); return; }   // nothing better available — z-order at least
            Action raise = () =>
            {
                try
                {
                    var ww = ed.GetType().GetProperty("WorkbenchWindow")?.GetValue(ed, null);
                    if (ww != null)
                    {
                        ww.GetType().GetMethod("SelectWindow", Type.EmptyTypes)?.Invoke(ww, null);
                        // SelectWindow only raises the DOCUMENT. The gen editor is not that document — it is a
                        // SECONDARY view inside it (the app window's own view tabs), so the window comes forward
                        // still showing whichever view was last active. That is the app tree, which is exactly
                        // what John saw after this path started raising anything at all: the Errors row moved
                        // focus to the app window but not onto the embeditor. Switch the inner view too.
                        SwitchToView(ww, ed);
                    }
                    try { if (_panel != null) _panel.BringToFront(); } catch { }   // Monaco back on top inside that host
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] FocusOwningTab: " + ex.Message); }
            };
            // Same deferral posture as BringToFront: re-activating a WebView2-bearing document synchronously on a
            // reentrant stack is the deadlock we guard against everywhere else here.
            var ctx = System.Threading.SynchronizationContext.Current;
            if (ctx != null) ctx.Post(_ => raise(), null); else raise();
        }

        /// <summary>
        /// Make <paramref name="target"/> the VISIBLE view inside its workbench window.
        ///
        /// SwitchView(int) is the only lever this fork gives us: ActiveViewContent is GET-ONLY on
        /// IWorkbenchWindow (verified by reflecting ICSharpCode.SharpDevelop.dll — it exposes
        /// get_ActiveViewContent with no setter, so assigning it was never an option). Index convention is
        /// SharpDevelop's: 0 = the window's primary ViewContent, 1..n = ViewContent.SecondaryViewContents in
        /// order. The ClaGenEditor lives in that secondary collection.
        ///
        /// Returns without touching anything when the target is ALREADY the active view — which is the
        /// overwhelmingly common case (reveal into an embeditor that is already in front), so the switch only
        /// ever runs for the cross-document case it exists to fix. That matters: switching views is the one
        /// operation here with the potential to disturb the native surface our overlay is docked onto, and
        /// this keeps it off the working path entirely.
        /// </summary>
        private static void SwitchToView(object ww, object target)
        {
            try
            {
                if (ww == null || target == null) return;
                var active = ww.GetType().GetProperty("ActiveViewContent")?.GetValue(ww, null);
                if (ReferenceEquals(active, target)) return;      // already showing — don't churn the tab

                var primary = ww.GetType().GetProperty("ViewContent")?.GetValue(ww, null);
                int index = -1;
                if (ReferenceEquals(primary, target)) index = 0;
                else if (primary != null)
                {
                    var sec = primary.GetType().GetProperty("SecondaryViewContents")?.GetValue(primary, null)
                              as System.Collections.IEnumerable;
                    if (sec != null)
                    {
                        int i = 1;
                        foreach (var v in sec) { if (ReferenceEquals(v, target)) { index = i; break; } i++; }
                    }
                }
                if (index < 0)
                {
                    ClarionAssistant.MonacoSpikeLog.Write("SwitchToView: target view not found among the window's views — left as-is");
                    return;
                }
                ww.GetType().GetMethod("SwitchView", new[] { typeof(int) })?.Invoke(ww, new object[] { index });
                ClarionAssistant.MonacoSpikeLog.Write("SwitchToView: switched owning window to view index " + index);
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("SwitchToView error: " + ex.Message); }
        }

        /// <summary>If a Modern Embeditor tab for this procedure is already open, focus it. Returns true if found.</summary>
        public static bool TryFocusExisting(string procName)
        {
            if (string.IsNullOrWhiteSpace(procName)) return false;
            lock (_instances)
            {
                foreach (var inst in _instances)
                {
                    if (string.Equals(inst._procedureName, procName, StringComparison.OrdinalIgnoreCase))
                    {
                        inst.BringToFront();
                        return true;
                    }
                }
            }
            return false;
        }

        // ── Field-drag support (ticket 0bada8de) ───────────────────────────────────────────────────────
        // The Data pad's host-owned DoDragDrop must (a) belt every live Monaco webview's external-drop OFF for
        // the drag duration so the native dictionary payload can't land — and insert the wrong window-control
        // string — inside a code editor, and (b) on a release over a Monaco editor, insert the plain reference
        // at THAT editor's cursor. These expose the live instances to FieldDropService without it taking a
        // WebView2 type dependency (it belts via the Control base + reflection).

        /// <summary>The live Monaco editor webviews (as Controls), for the field-drag external-drop belt.</summary>
        public static List<System.Windows.Forms.Control> LiveMonacoWebViews()
        {
            var list = new List<System.Windows.Forms.Control>();
            lock (_instances)
                foreach (var inst in _instances)
                {
                    var wv = inst._panel != null ? inst._panel.WebView : null;
                    if (wv != null) list.Add(wv);
                }
            return list;
        }

        /// <summary>If a VISIBLE Monaco editor webview covers the given screen point, move its caret to the editor
        /// position under that point and return true; otherwise false. Used during a Data-pad field DRAG so the
        /// caret tracks the mouse (the drop then lands where the pointer is).</summary>
        public static bool TryMoveMonacoCaretAt(int screenX, int screenY)
        {
            lock (_instances)
                foreach (var inst in _instances)
                {
                    var wv = inst._panel != null ? inst._panel.WebView : null;
                    if (wv == null || !wv.IsHandleCreated || !wv.Visible) continue;
                    try
                    {
                        if (wv.RectangleToScreen(wv.ClientRectangle).Contains(screenX, screenY))
                        {
                            inst._panel.MoveCaretToScreenPoint(screenX, screenY);
                            return true;
                        }
                    }
                    catch { }
                }
            return false;
        }

        /// <summary>If a VISIBLE Monaco editor webview covers the given screen point, insert <paramref name="text"/>
        /// at that editor's cursor and return true; otherwise false (the release wasn't over an editor). Mirrors
        /// the designer/pad rect hit-test so z-order/DPI behave consistently.</summary>
        public static bool TryInsertAtMonacoCursor(int screenX, int screenY, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            lock (_instances)
                foreach (var inst in _instances)
                {
                    var wv = inst._panel != null ? inst._panel.WebView : null;
                    if (wv == null || !wv.IsHandleCreated || !wv.Visible) continue;
                    try
                    {
                        if (wv.RectangleToScreen(wv.ClientRectangle).Contains(screenX, screenY))
                        {
                            // Atomic point-based insert: lands exactly where released (the page resolves the
                            // position from these coords), not at a separately-tracked caret that can go stale.
                            inst._panel.InsertTextAtScreenPoint(text, screenX, screenY);
                            inst.BringToFront();
                            inst._panel.FocusEditor();   // keyboard focus so the dev can type right after the drop
                            return true;
                        }
                    }
                    catch { }
                }
            return false;
        }

        // "Declared Tables": the tables DECLARED in the generated <app>.clw File Declaration (the program's
        // global file set). The SET comes from the <app>.clw (authoritative, stable, explainable — not a
        // fuzzy text scan); the SCHEMA is enriched from the LIVE dictionary snapshot (pictures, GROUP
        // nesting, full keys), matched by name, falling back to the <app>.clw-parsed schema when the live
        // snapshot lacks an entry. A standalone whole-dictionary browser is a separate, future addin.
        private static List<Dictionary<string, object>> GetDeclaredTables()
        {
            var outp = new List<Dictionary<string, object>>();
            try
            {
                string appClw = ClarionAppDataReader.FindAppClwPath();
                var declared = appClw != null
                    ? ClarionAppDataReader.ParseTables(appClw)
                    : new List<ClarionAppDataReader.TableDef>();
                if (declared.Count == 0) return outp;

                Dictionary<string, ClarionAppDataReader.TableDef> live;
                lock (_liveLock) { live = _liveTables; }

                foreach (var d in declared)
                {
                    ClarionAppDataReader.TableDef t = null;
                    if (live != null && !string.IsNullOrEmpty(d.Name)) live.TryGetValue(d.Name, out t);
                    if (t == null) t = d; // live snapshot not ready / no match — use the clw-parsed schema
                    var cols = new List<object>();
                    foreach (var f in t.Fields) cols.Add(ColToDict(f));
                    var dd = new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "prefix", t.Prefix },
                        { "attributes", BuildTableAttributes(t) }, { "description", t.Description ?? "" },
                        { "columns", cols }, { "keys", KeysToDicts(t) }, { "relations", RelationsToDicts(t) }
                    };
                    AddTableDetailFields(dd, t);
                    outp.Add(dd);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] GetDeclaredTables: " + ex.Message); }
            return outp;
        }

        /// <summary>Insert text at the editor's cursor (used by the Modern Data pad's double-click-insert).</summary>
        public void InsertAtCursor(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Marshal the insert + tab-activation together (the call may arrive off the UI thread from the
            // Data pad). The control's InsertText also marshals internally, which is harmless here.
            Action post = () =>
            {
                _panel?.InsertText(text);
                // Bring THIS editor tab to the front so the developer can start typing immediately after a
                // Data-pad double-click insert (the editor JS already does ed.focus() to place the caret).
                BringToFront();
            };
            try { if (_panel != null && _panel.InvokeRequired) _panel.BeginInvoke(post); else post(); }
            catch { }
        }

        // When set (> 0), overrides the saved cursor position on first open — used by AttachOverlayToOpenEmbed
        // to land Monaco at the embed point the developer had the native caret on when the overlay fired.
        private int _initialLine;

        // Open-time pwee document lines (from the ctor's sourceText) — the error-reveal self-anchor
        // (d3ab083a) locates these inside the generated module to map module lines → pwee lines.
        private string[] _pweeBaselineLines;

        // Native embeditor caret mirror (task d19c036d, sibling of PR #144's source-editor mirror): while the
        // overlay covers the live native embed, Clarion's own error→embed navigation keeps moving the HIDDEN
        // native caret — both when it positions LATE on the open that triggered our attach (the one-shot
        // GetNativeCaretLine read in AttachOverlayToOpenEmbed fires pre-paint, often before Clarion navigates)
        // and on every later error click routed into the already-open embed. Subscribe to the caret and reveal
        // each line change in Monaco so those jumps are visible instead of landing behind the overlay.
        private ICSharpCode.TextEditor.Caret _nativeEmbedCaret;
        private int _lastNativeEmbedLine = -1;   // 0-based; coalesces the Line-then-Column double-fire of one jump

        // Data-loss guard (task d19c036d part 3): Clarion's own error→embed navigation can close the native
        // embed under a DIRTY overlay. Our edits live only in Monaco until save — the native buffer is clean,
        // so Clarion never prompts, and the overlay teardown silently discarded the work (John's repro:
        // edit, click an Errors-pane row that re-opens the embeditor, edits gone). The page mirrors its
        // edited slot texts here on every legal edit (embedState, sibling of file mode's fileState); an
        // EXTERNAL teardown (not our own Save/Cancel) stashes them, and the next attach for the same
        // procedure restores them — validated against the same slot baseline so a stale stash or a
        // regenerated source can never corrupt anything.
        private List<string> _mirroredSlots;     // last embedState push from the page (live edited slot texts)
        private bool _mirroredDirty;             // page's dirty flag at that push
        private bool _teardownIntentional;       // set by our own Save/Cancel paths — suppresses the stash
        // True between a close-gesture sync and the close actually happening. Distinguishes "we set
        // _teardownIntentional for a close that is pending" from our own Save/Cancel, which never come back.
        // Cleared on the next buffer push, which only arrives if the user answered Cancel. (bcba6efb)
        private bool _closeSyncArmed;
        private sealed class EmbedEditStash
        {
            public string Proc;
            public List<string> Original;        // the torn-down overlay's slot BASELINE (validation key)
            public List<string> Edited;          // its unsaved edited slot texts
        }
        private static EmbedEditStash _editStash;   // single-slot: at most one live overlay exists (a5bbf005)

        public ModernEmbeditorViewContent(string title, string sourceText, List<int[]> editableRanges,
            string language = "clarion", bool isDark = true, string procedureName = null, bool liveLinked = false,
            int initialLine = 0, Services.EmbedLspContext lspContext = null)
        {
            _title = title ?? "Embeditor";
            _sourceText = sourceText ?? "";
            // Open-time pwee baseline, line-split once — the self-anchored error-reveal mapping
            // (TryRevealErrorInLiveOverlay, d3ab083a) matches these lines against the generated module.
            _pweeBaselineLines = _sourceText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            _editableRanges = editableRanges ?? new List<int[]>();
            _language = language ?? "clarion";
            _isDark = isDark;
            _procedureName = procedureName;
            _saveEnabled = !string.IsNullOrWhiteSpace(procedureName);
            _originalSlotTexts = ModernEmbeditorSaver.ExtractSlotTexts(_sourceText, _editableRanges);
            // #56: prefer the real generated-module path (captured by the launcher while the native embed
            // was open) so the LSP resolves the buffer inside the real project dir with PROGRAM scope via
            // the prepended MEMBER header. Falls back to the classic synthetic name when not captured.
            _lspContext = lspContext;
            _lspFileName = (_lspContext != null) ? _lspContext.RealPath : MakeLspFileName(procedureName);
            _initialLine = initialLine;
            TitleName = "CA: " + _title;

            // Reusable Monaco surface; we are its host (IMonacoEditorHost). It self-inits on HandleCreated.
            _panel = new MonacoEditorControl(this, isDark, "monaco-embeditor.html", VIRTUAL_HOST);

            lock (_instances) { _instances.Add(this); }
            // Cross-surface gear-settings sync: receive applySettings from any other Monaco surface (another
            // embeditor or a source/default editor). HandleSaveSettings publishes through the same bus. (deac3d16)
            _settingsReg = Services.MonacoSettingsBroadcaster.Register(json => { try { _panel?.PostJson(json); } catch { } });

            // Live-linked (ticket a5bbf005): become THE live tab and start watching for deactivation so we
            // release the native single-embeditor lock when the user switches away.
            _liveLinked = liveLinked;
            if (liveLinked)
            {
                _liveInstance = this;
                WireLiveWatch();
            }
        }

        /// <summary>
        /// File mode — open a plain source file (.clw/.inc/.equ/...) for whole-buffer editing.
        /// Same Monaco page and language services as the embeditor, but save writes the file
        /// (encoding-preserving) and all embed/slot machinery is bypassed.
        /// </summary>
        public ModernEmbeditorViewContent(string filePath, bool isDark)
        {
            _filePath = Path.GetFullPath(filePath);
            _fileMode = true;
            _title = Path.GetFileName(_filePath);
            _fileIdentity = CanonicalFileId(_filePath);   // stable identity for dedup/state, survives path aliasing (item 3)
            _sourceText = EncodingHelper.ReadAllText(_filePath, out _fileEncoding);
            _fileEol = DetectEol(_sourceText);
            _fileDiskSig = ReadFileSignature(_filePath);
            _fileLiveText = _sourceText;
            _editableRanges = new List<int[]>();
            _language = LanguageForFile(_filePath);
            _isDark = isDark;
            _procedureName = null;
            _saveEnabled = true;
            _originalSlotTexts = new List<string>();
            _lspFileName = _filePath;     // real path → LSP sees the actual file
            TitleName = "CA: " + _title;

            // Reusable Monaco surface; we are its host (IMonacoEditorHost). It self-inits on HandleCreated.
            _panel = new MonacoEditorControl(this, isDark, "monaco-embeditor.html", VIRTUAL_HOST);

            lock (_instances) { _instances.Add(this); }
            // Cross-surface gear-settings sync: receive applySettings from any other Monaco surface (another
            // embeditor or a source/default editor). HandleSaveSettings publishes through the same bus. (deac3d16)
            _settingsReg = Services.MonacoSettingsBroadcaster.Register(json => { try { _panel?.PostJson(json); } catch { } });
        }

        /// <summary>The file this tab edits (file mode), else null.</summary>
        public string FilePath { get { return _filePath; } }

        /// <summary>
        /// The key this tab's diagnostics are cached under, or null when that cache isn't safe to
        /// surface raw. FILE MODE ONLY, deliberately: there _lspFileName is the real file and the
        /// squiggle pass runs with embedSlotChecks=false, so the cache is exactly what Monaco marked
        /// up. In embed mode the same cache holds WHOLE-generated-buffer diagnostics that
        /// ModernEmbeditorDiagnostics.ComputeAsync clamps to the editable slots before rendering —
        /// reporting those unclamped would count generated-line noise the editor never squiggled.
        /// </summary>
        public string DiagnosticsCacheKey { get { return _fileMode ? _lspFileName : null; } }

        /// <summary>
        /// Dark/light state of the ACTIVE Modern tab's Monaco surface, or null when the active
        /// document isn't one (or its panel isn't up yet). Preferred over
        /// CaEditorSettings.MonacoThemeDark, which only records whichever page posted last and so
        /// drifts from the active editor once two surfaces disagree.
        /// </summary>
        public static bool? ActiveViewIsDark()
        {
            try
            {
                var view = ActiveModernView();
                return (view != null && view._panel != null) ? view._panel.IsDark : (bool?)null;
            }
            catch { return null; }
        }

        /// <summary>Find the open file-mode tab for a path, or null. Used by the open command to dedup so the same
        /// file doesn't open in two tabs (→ last-save-wins). Matches on the UNION of two identities:
        ///  • the canonical file ID (vol serial + file index) — collapses path aliases AND hard links;
        ///  • the normalized full path — collapses same-path reopens even when the file ID CHURNS between opens
        ///    (external delete/recreate, atomic replace, branch switch, or an id:↔path: fallback transition).
        /// Either match reuses the existing tab. This covers the realistic cases, NOT every possible one: a reopen
        /// that changes BOTH the path alias AND the file ID at once (external replace + reopen via a different
        /// alias) still escapes dedup — tracked as follow-up 8348435a. (pipeline item 3 + Run-6/7 adversary)</summary>
        public static ModernEmbeditorViewContent FindByFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string id = CanonicalFileId(path);
            string full = null;
            try { full = Path.GetFullPath(path); } catch { }
            lock (_instances)
            {
                foreach (var inst in _instances)
                {
                    if (!inst._fileMode) continue;
                    if (string.Equals(inst._fileIdentity, id, StringComparison.OrdinalIgnoreCase)
                        || (full != null && string.Equals(inst._filePath, full, StringComparison.OrdinalIgnoreCase)))
                        return inst;
                }
            }
            return null;
        }

        /// <summary>Resolve a path to a STABLE physical-file identity so the same file opened via ANY alias —
        /// 8.3 short name, junction/symlink, subst/mapped-drive vs UNC, OR an NTFS HARD LINK — dedups to one tab
        /// (→ no last-save-wins). PRIMARY: the handle's volume serial + file index (a true file ID; the only thing
        /// that collapses hard links, which are distinct directory entries for one file record with no common
        /// pathname). FALLBACK when the file ID is unavailable: the normalized final path, then the plain full path.
        /// The "id:"/"path:" prefixes keep a file-ID identity and a path fallback from ever colliding. (item 3)</summary>
        private static string CanonicalFileId(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    IntPtr h = fs.SafeFileHandle.DangerousGetHandle();
                    NativeMethods.BY_HANDLE_FILE_INFORMATION info;
                    // A 0 file index means the filesystem didn't supply a real per-file ID (some network/virtual FS) —
                    // do NOT use it, or every such file would collapse to one "id:…:0" identity → wrong-file dedup.
                    // Fall through to the path identity instead.
                    if (NativeMethods.GetFileInformationByHandle(h, out info) && (info.FileIndexHigh != 0 || info.FileIndexLow != 0))
                        return "id:" + info.VolumeSerialNumber.ToString("x8") + ":" +
                               info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8");
                    // File ID unavailable — fall back to the normalized final path (resolves path aliases, not hard links).
                    var sb = new StringBuilder(512);
                    uint len = NativeMethods.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);   // 0 = VOLUME_NAME_DOS | FILE_NAME_NORMALIZED
                    if (len > sb.Capacity) { sb.EnsureCapacity((int)len + 1); len = NativeMethods.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0); }
                    if (len > 0)
                    {
                        string p = sb.ToString();
                        if (p.StartsWith(@"\\?\UNC\")) p = @"\\" + p.Substring(8);   // \\?\UNC\server\share → \\server\share
                        else if (p.StartsWith(@"\\?\")) p = p.Substring(4);          // \\?\C:\... → C:\...
                        return "path:" + p.ToLowerInvariant();
                    }
                }
            }
            catch { }
            try { return "path:" + Path.GetFullPath(path).ToLowerInvariant(); } catch { return "path:" + path.ToLowerInvariant(); }
        }

        /// <summary>Activate this tab (public wrapper over the deferred SelectWindow used elsewhere).</summary>
        public void ActivateTab() { BringToFront(); }

        private static string LanguageForFile(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".clw": case ".inc": case ".equ": case ".int":
                case ".tpl": case ".tpw": case ".trn": case ".pr":
                    return "clarion";
                default:
                    return "plaintext";
            }
        }

        // ── IMonacoEditorHost (converge step 3) ─────────────────────────────────────────────────
        // The MonacoEditorControl owns the WebView2 lifecycle + nav + inbound action routing; it
        // calls these as page->host messages arrive. Each delegates to this view's existing handler,
        // unchanged. The fileMode designer guards that used to wrap the dispatch cases live here now.
        void IMonacoEditorHost.OnReady(MonacoEditorControl editor)
        {
            // On open: refresh the pad's IDE-sourced caches (whole-app .txa for Local/Global Data; live
            // dictionary snapshot for Other Files). Silent. File mode has no app context, so skip it.
            // (Was in the old OnHandleCreated; the "ready" message is the equivalent open moment.)
            if (!_fileMode) RefreshPadSources();
            SendSource();
            // CA Find pad (GitHub #66): this editor becomes findable. Key = stable session identity
            // (file path in file mode; procedure name otherwise — matches the cursor-persist scoping).
            Services.CaFindBroker.RegisterHost(this, _panel,
                () => _fileMode ? (_filePath ?? "") : ("embed::" + (_procedureName ?? "")),
                () => _fileMode ? System.IO.Path.GetFileName(_filePath ?? "") : (_procedureName ?? "embeditor"),
                _fileMode ? "CA Editor" : "CA Embeditor");
        }

        void IMonacoEditorHost.OnSave(MonacoEditorControl editor, string rawJson) { HandleSave(rawJson); }
        void IMonacoEditorHost.OnCancel(MonacoEditorControl editor) { HandleCancel(); }
        void IMonacoEditorHost.OnConfirmSaveExit(MonacoEditorControl editor) { HandleConfirmSaveExit(editor); }
        void IMonacoEditorHost.OnSyncNativeForClose(MonacoEditorControl editor) { HandleSyncNativeForClose(); }
        void IMonacoEditorHost.OnConfirmCancel(MonacoEditorControl editor) { HandleConfirmCancel(editor); }
        void IMonacoEditorHost.OnOpenSource(MonacoEditorControl editor) { HandleOpenSource(); }
        void IMonacoEditorHost.OnClipboard(MonacoEditorControl editor, string rawJson) { HandleClipboard(rawJson); }
        void IMonacoEditorHost.OnCompletion(MonacoEditorControl editor, string rawJson) { HandleCompletion(rawJson); }
        void IMonacoEditorHost.OnHover(MonacoEditorControl editor, string rawJson) { HandleHover(rawJson); }
        void IMonacoEditorHost.OnDefinition(MonacoEditorControl editor, string rawJson) { HandleDefinition(rawJson); }
        void IMonacoEditorHost.OnDiagnostics(MonacoEditorControl editor, string rawJson) { HandleDiagnostics(rawJson); }
        void IMonacoEditorHost.OnSignatureHelp(MonacoEditorControl editor, string rawJson) { HandleSignatureHelp(rawJson); }
        void IMonacoEditorHost.OnImplementation(MonacoEditorControl editor, string rawJson) { HandleImplementation(rawJson); }
        void IMonacoEditorHost.OnDocumentStructure(MonacoEditorControl editor, string rawJson) { HandleDocumentStructure(rawJson); }
        void IMonacoEditorHost.OnSaveSettings(MonacoEditorControl editor, string rawJson) { HandleSaveSettings(rawJson); }
        // Read-only preview feed for the gear panel's VS Code import; applying goes back through
        // OnSaveSettings above, so there is still exactly one write path.
        void IMonacoEditorHost.OnReadVsCodeSettings(MonacoEditorControl editor, string rawJson) { VsCodeImportBridge.Handle(editor, rawJson); }
        void IMonacoEditorHost.OnSaveHistory(MonacoEditorControl editor, string rawJson) { HandleSaveHistory(rawJson); }
        void IMonacoEditorHost.OnSnippetCommand(MonacoEditorControl editor, string rawJson)
        {
            // Gear-panel Code Snippets CRUD — persist via the shared store, then live-broadcast the
            // updated list to every open tab (refreshes both the snippet picker and the gear list).
            var updated = SnippetStore.ApplyCommand(rawJson);
            if (updated != null) ApplySnippetsToAll(updated);
        }
        void IMonacoEditorHost.OnSaveCursor(MonacoEditorControl editor, string rawJson) { HandleSaveCursor(rawJson); }
        void IMonacoEditorHost.OnSaveBookmarks(MonacoEditorControl editor, string rawJson) { HandleSaveBookmarks(rawJson); }
        void IMonacoEditorHost.OnSaveFolds(MonacoEditorControl editor, string rawJson) { HandleSaveFolds(rawJson); }
        void IMonacoEditorHost.OnSelectionChanged(MonacoEditorControl editor, string rawJson) { HandleSelectionChanged(rawJson); }
        void IMonacoEditorHost.OnFocusEditor(MonacoEditorControl editor) { BringToFront(); }   // Data-pad drag-drop
        void IMonacoEditorHost.OnReload(MonacoEditorControl editor) { HandleReload(); }        // file mode
        void IMonacoEditorHost.OnFileState(MonacoEditorControl editor, string rawJson) { HandleFileState(rawJson); }  // file mode

        // Designer needs an embeditor-backed procedure → file mode refuses. Guard lives here now.
        void IMonacoEditorHost.OnOpenDesigner(MonacoEditorControl editor, string rawJson) { if (!_fileMode) HandleOpenDesigner(rawJson); }
        void IMonacoEditorHost.OnOpenDesignerCreate(MonacoEditorControl editor, string rawJson) { if (!_fileMode) HandleOpenDesignerCreate(rawJson); }
        void IMonacoEditorHost.OnActivateDesigner(MonacoEditorControl editor) { if (!_fileMode) StructureDesignerService.ActivateCurrent(_panel); }

        // CA Find pad protocol (GitHub #66) — the broker routes to/from the dockable pad.
        void IMonacoEditorHost.OnCaFind(MonacoEditorControl editor, string action, string rawJson) { Services.CaFindBroker.FromEditor(this, action, rawJson); }

        /// <summary>Tab switched to this view: claim the CA Find pad and hand the Monaco surface real
        /// focus. The IDE does NOT focus a WebView2-hosted view on a tab switch, so the page's
        /// onDidFocusEditorText never fired and the pad kept targeting the previous editor (John,
        /// #66 validation). Focus goes both levels: WebView2 (Windows) + editor.focus() (Monaco).</summary>
        public override void SwitchedTo()
        {
            base.SwitchedTo();
            try
            {
                Services.CaFindBroker.NotifyActivity(this);
                if (_panel != null)
                {
                    _panel.FocusEditor();
                    _panel.PostJson("{\"type\":\"focusEditor\"}");
                }
            }
            catch { }
        }

        void IMonacoEditorHost.OnEditorNavigationCompleted(MonacoEditorControl editor, bool success)
        {
            _isInitialized = success;
            if (_embedOverlay && success) RemoveOverlayCover();
            if (_embedOverlay && success) TryRestoreStashedEdits();   // re-apply edits an external teardown stashed (d19c036d)
            FocusIfActiveTab();   // #66 round-4: the INITIAL open never fires SwitchedTo (the tab is born selected)
        }

        /// <summary>If an externally torn-down overlay stashed unsaved edits for THIS procedure, push them to
        /// the page (which re-applies them slot-by-slot once its source has loaded). Restores ONLY when the
        /// fresh baseline is identical to the stashed one — a regenerated/changed source invalidates the
        /// stash, and the page is told so the loss is at least surfaced instead of silent. (d19c036d)</summary>
        private void TryRestoreStashedEdits()
        {
            try
            {
                var stash = _editStash;
                if (stash == null || _panel == null) return;
                if (!string.Equals(stash.Proc, _procedureName, StringComparison.OrdinalIgnoreCase)) return;   // another proc's stash — leave it for its own re-open
                _editStash = null;   // single-shot: consumed (or invalidated) by this attach
                bool baselineMatches = _originalSlotTexts != null && stash.Original.Count == _originalSlotTexts.Count;
                if (baselineMatches)
                    for (int i = 0; i < stash.Original.Count; i++)
                        if (!string.Equals(stash.Original[i], _originalSlotTexts[i], StringComparison.Ordinal)) { baselineMatches = false; break; }
                if (!baselineMatches)
                {
                    try { _panel.PostJson("{\"type\":\"restoreSlotsFailed\"}"); } catch { }
                    ClarionAssistant.MonacoSpikeLog.Write("stashed unsaved edits NOT restored — baseline changed (" + _procedureName + ")");
                    return;
                }
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                _panel.PostJson("{\"type\":\"restoreSlots\",\"slots\":" + ser.Serialize(stash.Edited) + "}");
                ClarionAssistant.MonacoSpikeLog.Write("restored stashed unsaved edits into re-opened embed (" + _procedureName + ")");
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("TryRestoreStashedEdits error: " + ex.Message); }
        }

        /// <summary>Hand the freshly loaded Monaco page keyboard focus + claim the CA Find pad — but only
        /// if OUR tab is the active document (same initial-open gap the CA Editor had: a new tab is born
        /// selected, so SwitchedTo never fires for it and nothing focused the page).</summary>
        private void FocusIfActiveTab()
        {
            try
            {
                if (_panel == null) return;
                // Overlay mode floats over the just-opened NATIVE embeditor (not a workbench tab), so the
                // FocusedModernView identity check can't apply — the native window is foreground by
                // definition on open. Tab mode guards against background opens stealing focus.
                if (!_embedOverlay && FocusedModernView() != this) return;
                Services.CaFindBroker.NotifyActivity(this);
                _panel.FocusEditor();
                _panel.PostJson("{\"type\":\"focusEditor\"}");
            }
            catch { }
        }
        void IMonacoEditorHost.OnUnknownAction(MonacoEditorControl editor, string action, string rawJson)
        {
            if (action == "embedState") HandleEmbedState(rawJson);
        }

        /// <summary>{action:"embedState"} — the page's per-edit mirror of its edited slot texts (see the
        /// _mirroredSlots field comment). Routed via OnUnknownAction so the shared IMonacoEditorHost
        /// interface (and the CA Editor host) stays untouched.</summary>
        private void HandleEmbedState(string rawJson)
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(rawJson) as Dictionary<string, object>;
                if (data == null) return;
                object slotsObj, dirtyObj;
                var arr = data.TryGetValue("slots", out slotsObj) ? slotsObj as object[] : null;
                if (arr == null) return;
                _mirroredSlots = arr.Select(o => o == null ? "" : o.ToString()).ToList();
                _mirroredDirty = data.TryGetValue("dirty", out dirtyObj) && dirtyObj is bool && (bool)dirtyObj;

                // Late retry for the cancellable close hook (bcba6efb). ShowAsEmbedOverlay tries first, but
                // WorkbenchWindow can still be unassigned that early — MonacoClarionSourceEditor.EnsureCloseHook
                // hit exactly this and retries "next file-state" for the same reason. This is the embeditor's
                // equivalent tick, and it is the ONLY thing standing between "we ask before discarding edits"
                // and the silent close this ticket exists to fix, so it retries rather than trusting one attempt.
                // Idempotent: HookEmbedClosing returns immediately once subscribed.
                if (_embedOverlay) HookEmbedClosing(_overlayGenEditor);

                // PAIRED RESET for the close-sync stash suppression. Receiving a buffer push means the embed is
                // still alive and being edited, so the close we armed for did not happen — the user answered
                // Cancel. Re-arm the stash, or an interruption after that Cancel would discard their work with
                // the guard switched off. Only the Cancel path can reach here; Yes and No both tear the overlay
                // down and no further pushes arrive.
                if (_closeSyncArmed)
                {
                    _closeSyncArmed = false;
                    _teardownIntentional = false;
                    MonacoSpikeLog.Write("[native-dirty] close cancelled (still editing) — stash guard re-armed");
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] embedState: " + ex.Message); }
        }

        /// <summary>Persist the user's edits: parse the per-slot payload and run the save round-trip.</summary>
        private void HandleSave(string json)
        {
            if (_fileMode) { HandleFileSave(json); return; }

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

            // CRITICAL — do NOT run the save round-trip on THIS stack. We're inside OnWebMessageReceived,
            // the WebView2 web-message handler (a reentrant message-loop context). ModernEmbeditorSaver.Save
            // re-opens the native embeditor and drives it with nested Application.DoEvents() pumps; on this
            // reentrant stack that deadlocks the IDE — the same failure mode the deferred ShowView fixed on
            // open. Post it so this handler returns and the round-trip runs on a settled UI turn.
            var captured = current;
            // DIAGNOSTIC (e1162adf): log the handoff, so the gap between THIS line and "[save-timing] enter"
            // in the log measures how long the BeginInvoke sat queued. A large gap here means the UI thread
            // was already blocked before the save even started — a completely different fault than a slow
            // save round-trip, and worth being able to tell apart.
            try { MonacoSpikeLog.Write("[save-timing] posted to UI thread (slots=" + captured.Count + ")"); } catch { }
            if (_panel != null && _panel.IsHandleCreated)
                _panel.BeginInvoke((Action)(() => RunSaveRoundTrip(captured)));
            else
                RunSaveRoundTrip(captured);
        }

        /// <summary>Cancel/Discard from our toolbar (replaces the hidden native red-X). Overlay mode: detach the
        /// overlay then discard the native embed (CancelEmbeditor); tab/file mode: close the workbench tab (its
        /// Dispose does the discard). Deferred off the web-message stack — cancelling the native embed pumps
        /// DoEvents and disposing the WebView2 on that reentrant stack would deadlock the IDE.</summary>
        /// <summary>GH #193 (BoxSoft): Ctrl+Q's confirm, as a NATIVE Windows dialog.
        ///
        /// Matched character-for-character to the dialog Clarion's own embeditor raises — title,
        /// message, button set and order, default button and Question icon — because that is exactly
        /// what was asked for: "It should use the same visible style as the regular window." What we
        /// had was an in-page dark panel, and the mismatch is what "causes one to pause".
        ///
        /// The host owns the DIALOG only; the page still owns what each answer DOES (save-and-exit
        /// versus discard-and-close differ by live-linked/snapshot/file mode, and that logic is
        /// proven). So this posts the answer back and gets out of the way.
        ///
        /// Note the page has a key-swallowing shield up across this whole round trip. Ctrl+Q then
        /// Enter is muscle memory from the native editor, and without the shield that Enter would
        /// land in the buffer as a newline before this dialog ever appeared.</summary>
        private void HandleConfirmSaveExit(MonacoEditorControl editor)
        {
            if (editor == null) return;
            Action work = () =>
            {
                string result = "cancel";
                try
                {
                    // Own the dialog to the form actually hosting the WebView2. In overlay mode that is
                    // the window the native embeditor is docked into, which is exactly what the dialog
                    // should be modal to. FindForm() can return null mid-teardown, hence the fallback.
                    IWin32Window owner = null;
                    try { owner = editor.FindForm(); } catch { }
                    var r = owner != null
                        ? MessageBox.Show(owner, "Do you want to save the current changes?",
                            "Save Changes in Embed Editor?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question)
                        : MessageBox.Show("Do you want to save the current changes?",
                            "Save Changes in Embed Editor?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    result = r == DialogResult.Yes ? "yes" : (r == DialogResult.No ? "no" : "cancel");
                }
                catch (Exception ex)
                {
                    // Never strand the page behind its shield: any failure here answers "cancel", which
                    // keeps the user editing with their changes intact. Silence would leave the editor
                    // permanently unresponsive to keys.
                    MonacoSpikeLog.Write("HandleConfirmSaveExit dialog error: " + ex.Message);
                }
                // "type", not "action": page->host messages are keyed on action, host->page on type.
                try { editor.PostJson("{\"type\":\"confirmSaveExitResult\",\"result\":\"" + result + "\"}"); }
                catch (Exception ex) { MonacoSpikeLog.Write("HandleConfirmSaveExit post error: " + ex.Message); }
            };
            if (editor.InvokeRequired) editor.BeginInvoke(work); else work();
        }

        /// <summary>Page is about to hand a close gesture (Ctrl+F4) to the IDE. Make the NATIVE embed tell the
        /// truth about itself first, so Clarion's own prompt fires and its Yes saves the right content.
        ///
        /// WHY THIS IS THE FIX AND THE ClosingEvent HOOK WAS NOT (measured, bcba6efb):
        /// CommonGenEditor.WorkbenchWindow_ClosingEvent subscribes when the embed opens — long before our
        /// overlay attaches — so it always runs FIRST. It sets e.Cancel = true (it does not want the WINDOW
        /// closed) and then closes the embed itself via TryClose(), which consults the NATIVE IsDirty and
        /// raises "Save Changes in Embed Editor?". By the time our own handler ran, that decision was already
        /// made: we logged `ClosingEvent FIRED (cancel=True ... dirty=True)` and the embed closed anyway.
        /// A veto on the workspace window has no authority over the embed teardown.
        ///
        /// So we do not fight the close. We make the native editor dirty BEFORE the key is dispatched, and
        /// Clarion prompts natively — exact parity by construction, nothing reimplemented, which is what the
        /// report asked for.
        ///
        /// ORDER IS THE WHOLE MECHANISM: the page posts this, then posts the key. Host messages are processed
        /// in order on the UI thread, so the sync and the dirty flag are both in place before File>Close>File
        /// ever runs. Do not make this async, and do not move it onto a timer — see SyncLive's remarks on
        /// driving the live PWEE repeatedly.</summary>
        private void HandleSyncNativeForClose()
        {
            try
            {
                if (!_embedOverlay || _fileMode) return;
                if (!_mirroredDirty || _mirroredSlots == null || _originalSlotTexts == null)
                {
                    MonacoSpikeLog.Write("[native-dirty] nothing to sync (dirty=" + _mirroredDirty + ")");
                    return;
                }

                bool ok;
                string msg = ModernEmbeditorSaver.SyncLive(_procedureName, _editableRanges,
                    _originalSlotTexts, _mirroredSlots, out ok);
                MonacoSpikeLog.Write("[native-dirty] SyncLive ok=" + ok + " — " + msg);

                // Set the flag even if the sync failed: a prompt on stale content is bad, but closing with NO
                // prompt loses the edits outright. The user still gets asked, and the log names the failure.
                SetNativeDirty(true, ok);

                if (ok)
                {
                    // SUPPRESS THE STASH. From here the edits live in the NATIVE buffer and Clarion owns what
                    // happens to them — Yes persists them, No discards them deliberately. The stash exists for
                    // teardowns that interrupt unsaved work, and this is not one.
                    //
                    // Without this the user SEES the bug as its own opposite (measured 11:57): the save
                    // succeeded, DetachOverlay stashed 120 slots anyway because _teardownIntentional was false
                    // (Clarion closed us, not our own save path), and the re-open then refused the stash
                    // because the generated source had changed — which it had, BECAUSE THE SAVE WORKED. The
                    // toast then announces that unsaved edits could not be restored, about edits that were
                    // saved. Alarming, and exactly backwards.
                    _teardownIntentional = true;
                    _closeSyncArmed = true;   // paired reset below, for the Cancel case
                }
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[native-dirty] EXCEPTION: " + ex.Message); }
        }

        /// <summary>Set the native ClaGenEditor's IsDirty. This is CommonGenEditor's override (confirmed by
        /// reflection: declared canWrite=True, alongside TryClose/SaveAndExit/ExitNotSave), and it is the flag
        /// Clarion's own close prompt reads.
        ///
        /// NOT to be confused with TrySetHostDirty, which is a deliberate no-op on OUR view and must stay one:
        /// this view has no file binding, so setting ITS IsDirty pops a bogus Save As into ...\libsrc\win and
        /// then throws from AbstractViewContent.Save(fileName). The ClaGenEditor is a real view with real
        /// Clarion save machinery — a different object with a different contract.</summary>
        private void SetNativeDirty(bool dirty, bool contentSynced)
        {
            try
            {
                if (_overlayGenEditor == null) { MonacoSpikeLog.Write("[native-dirty] no genEditor"); return; }
                var p = _overlayGenEditor.GetType().GetProperty("IsDirty");
                if (p == null || !p.CanWrite) { MonacoSpikeLog.Write("[native-dirty] IsDirty not writable"); return; }
                p.SetValue(_overlayGenEditor, dirty, null);
                MonacoSpikeLog.Write("[native-dirty] native IsDirty=" + dirty + " (contentSynced=" + contentSynced
                    + ") — Clarion's TryClose() should now prompt");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[native-dirty] set failed: " + ex.Message); }
        }

        /// <summary>Clarion's own red-X confirmation, reproduced character-for-character from the dialog John
        /// screenshotted (2026-08-27): title "Exit from embed editor?", message "Are you sure you want to
        /// cancel?", Yes/No, Question icon, Yes default. It is a STOCK MessageBox, which is what makes exact
        /// parity achievable here rather than approximate — the same reason the GH #193 Ctrl+Q dialog matches.
        ///
        /// Unlike Ctrl+F4 we cannot delegate to Clarion: cancelling never reaches Clarion's own close path,
        /// because our toolbar replaced the native red-X and the host tears the embed down itself. So this one
        /// we do have to raise.
        ///
        /// Any failure answers "no" — the page then keeps editing. A dialog that fails must never be the thing
        /// that discards someone's work.</summary>
        private void HandleConfirmCancel(MonacoEditorControl editor)
        {
            if (editor == null) return;
            Action work = () =>
            {
                string result = "no";
                try
                {
                    IWin32Window owner = null;
                    try { owner = editor.FindForm(); } catch { }
                    var r = owner != null
                        ? MessageBox.Show(owner, "Are you sure you want to cancel?",
                            "Exit from embed editor?", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                        : MessageBox.Show("Are you sure you want to cancel?",
                            "Exit from embed editor?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    result = r == DialogResult.Yes ? "yes" : "no";
                }
                catch (Exception ex) { MonacoSpikeLog.Write("HandleConfirmCancel dialog error: " + ex.Message); }
                MonacoSpikeLog.Write("[confirm-cancel] answered " + result);
                // Always reply, or the page stays shielded and deaf to keys behind an invisible overlay.
                try { editor.PostJson("{\"type\":\"confirmCancelResult\",\"result\":\"" + result + "\"}"); }
                catch (Exception ex) { MonacoSpikeLog.Write("HandleConfirmCancel post error: " + ex.Message); }
            };
            if (editor.InvokeRequired) editor.BeginInvoke(work); else work();
        }

        private void HandleCancel()
        {
            Action work = () =>
            {
                if (_embedOverlay)
                {
                    // Controlled discard: detach the overlay (dispose the WebView2 on THIS settled turn) BEFORE
                    // cancelling the native embed, so the host disposal can't cascade-dispose the WebView2 on the
                    // native close stack (the freeze). Then release the native embed (nothing is written back).
                    _teardownIntentional = true;   // user chose Cancel — discarding is the point, don't stash (d19c036d)
                    DetachOverlay();
                    try
                    {
                        var appTree = new AppTreeService();
                        if (appTree.GetEmbedInfo() != null)
                        {
                            appTree.CancelEmbeditor();
                            ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000);
                        }
                    }
                    catch { }
                    return;
                }
                PostCloseTab();   // tab / snapshot / file mode: discard by closing the tab
            };
            try
            {
                if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke(work);
                else work();
            }
            catch { try { work(); } catch { } }
        }

        /// <summary>Clicking our header strip opens the generated source — same as the native embeditor's "Open Source"
        /// toolbar button. We drive that native ToolStripItem's Click directly (it's hidden with the toolbar, but
        /// hidden != disabled, so PerformClick still fires its handler) — robust, reuses the exact native behaviour,
        /// no guessing at a command class. Deferred onto a settled UI turn off the web-message stack. (b1e05287)</summary>
        private void HandleOpenSource()
        {
            Action work = () =>
            {
                try
                {
                    var item = FindToolStripItem(_nativeToolStrip, "source");   // "Open Source"
                    if (item != null) { item.PerformClick(); return; }
                    PostSaveResult(false, "Open Source: couldn't find the native toolbar's Open Source button.");
                }
                catch (Exception ex)
                {
                    try { PostSaveResult(false, "Open Source failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); } catch { }
                }
            };
            try
            {
                if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke(work);
                else work();
            }
            catch { try { work(); } catch { } }
        }

        /// <summary>Find a ToolStripItem whose Text/ToolTipText contains <paramref name="needle"/> (case-insensitive),
        /// excluding our own "CA Embeditor" item. Recurses dropdowns. Null if none. (b1e05287)</summary>
        private static ToolStripItem FindToolStripItem(ToolStrip strip, string needle)
        {
            if (strip == null || string.IsNullOrEmpty(needle)) return null;
            foreach (ToolStripItem it in strip.Items)
            {
                var found = MatchToolStripItem(it, needle);
                if (found != null) return found;
            }
            return null;
        }

        private static ToolStripItem MatchToolStripItem(ToolStripItem it, string needle)
        {
            if (it == null) return null;
            string t = ((it.Text ?? "") + " " + (it.ToolTipText ?? ""));
            bool hit = t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                       && t.IndexOf("CA Embeditor", StringComparison.OrdinalIgnoreCase) < 0;
            if (hit) return it;
            var dd = it as ToolStripDropDownItem;
            if (dd != null)
                foreach (ToolStripItem sub in dd.DropDownItems)
                {
                    var f = MatchToolStripItem(sub, needle);
                    if (f != null) return f;
                }
            return null;
        }

        /// <summary>
        /// File-mode save: write the whole buffer back to disk, preserving the encoding detected at open and
        /// normalizing line endings per language (CRLF for Clarion, else the file's own detected style). Overwrite
        /// consent is bound to the specific on-disk version the user was warned about: if the file changed on disk
        /// it reports the conflict, and a retry overwrites only that same version — if it changed AGAIN since the
        /// warning, it re-warns rather than clobbering a version the user never saw.
        /// Plain file I/O — safe on this stack, no native embeditor round-trip involved.
        /// </summary>
        private void HandleFileSave(string json)
        {
            string text; long seq = 0;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                text = (data != null && data.ContainsKey("text")) ? data["text"] as string : null;
                if (data != null && data.ContainsKey("seq")) long.TryParse(Convert.ToString(data["seq"]), out seq);
                if (text == null) { PostSaveResult(false, "Save failed: malformed payload (no text)."); return; }
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Save failed parsing the editor payload: " + ex.Message);
                return;
            }

            try
            {
                // Changed-on-disk guard. The signature (mtime+length) fingerprints the EXACT on-disk version.
                // Overwrite consent is bound to one specific version: a prior arm is honored only if the disk is
                // STILL at the version we warned about — if it changed AGAIN, re-warn (never clobber a version the
                // user never saw). (pipeline item 2 — debugger + adversary + security)
                string diskSig = ReadFileSignature(_filePath);
                if (diskSig != _fileDiskSig)
                {
                    if (_fileOverwriteArmedSig == null || _fileOverwriteArmedSig != diskSig)
                    {
                        _fileOverwriteArmedSig = diskSig;
                        PostSaveResult(false, _title + " changed on disk since it was opened/last saved. Save again to overwrite THIS disk version, or use Reload to discard your edits and pick up the disk version.");
                        return;
                    }
                    // armed for exactly this version — fall through and overwrite.
                }
                _fileOverwriteArmedSig = null;

                // Normalize EOL (Clarion=CRLF, else the file's detected style) + atomic write, bound to the disk
                // version validated above. Shared with the close-save path so the policy lives in one place. (items 2, 5)
                string outText = WriteFileMode(text, diskSig);

                _fileDiskSig = ReadFileSignature(_filePath);
                _sourceText = outText;
                _fileLiveText = outText;
                _fileDirty = false;
                TrySetHostDirty(false);
                PostSaveResult(true, "Saved " + _title, seq);
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Save failed: " + ex.Message);
            }
        }

        /// <summary>File mode: the page mirrors its live buffer + dirty flag here on each legal edit (and on blur),
        /// so a tab close can offer to save WITHOUT an async round-trip into the WebView2. (pipeline CRITICAL)</summary>
        private void HandleFileState(string json)
        {
            if (!_fileMode) return;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return;
                if (data.ContainsKey("text") && data["text"] is string) _fileLiveText = (string)data["text"];
                if (data.ContainsKey("dirty")) _fileDirty = Convert.ToBoolean(data["dirty"]);
                TrySetHostDirty(_fileDirty);
            }
            catch { }
        }

        /// <summary>Intentionally a NO-OP. We do NOT reflect dirty state onto AbstractViewContent.IsDirty.
        /// LIVE-IDE FINDING (John's test, 2026-06-13): setting IsDirty=true makes SharpDevelop's workbench run its
        /// OWN save-on-close path on tab close — but this WebView2 view has no real file binding, so the framework
        /// treats it as an untitled doc, pops a bogus "Save As" dialog (defaulting to ...\libsrc\win), then calls
        /// AbstractViewContent.Save(fileName) which we don't implement → System.NotImplementedException.
        /// Unsaved-edits-on-close is handled entirely by OUR Dispose() confirm (+ host buffer mirror), which writes
        /// the file directly and never touches the framework save machinery. Kept as a no-op so call sites are stable.</summary>
        private void TrySetHostDirty(bool dirty)
        {
            // deliberately empty — see summary (framework Save(fileName) is NotImplemented for this view)
        }

        /// <summary>A cheap disk-version fingerprint (last-write UTC ticks + byte length) that distinguishes the
        /// exact on-disk version. Returns null when the file is missing — so a delete/recreate reads as a change.</summary>
        private static string ReadFileSignature(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : null; }
            catch { return null; }
        }

        /// <summary>Detect the file's dominant EOL so non-Clarion files round-trip their own style instead of being
        /// force-converted to CRLF (which would mark every line changed). (pipeline item 5)</summary>
        private static string DetectEol(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\r\n";
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') crlf++; else lf++; }
            return lf > crlf ? "\n" : "\r\n";
        }

        /// <summary>Normalize every EOL to <paramref name="eol"/>. Collapse CRLF and lone CR to LF first (so a
        /// classic-Mac \r isn't left dangling), then expand to the target.</summary>
        private static string NormalizeEol(string text, string eol)
        {
            if (text == null) return "";
            string s = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return eol == "\n" ? s : s.Replace("\n", "\r\n");
        }

        /// <summary>Write atomically: encode to a temp file in the same directory, then replace the target, so a
        /// crash/lock mid-write can't truncate the real file. Re-checks the disk signature immediately before the
        /// replace and throws if the file changed AGAIN since the caller validated it (shrinks TOCTOU; fails closed).
        /// <paramref name="expectedSig"/> is the signature the caller approved (null = brand-new file).</summary>
        private static void WriteFileAtomic(string path, string text, Encoding enc, string expectedSig)
        {
            string dir = Path.GetDirectoryName(path);
            string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, text, enc);
            try
            {
                if (ReadFileSignature(path) != expectedSig)
                    throw new IOException("File changed on disk again during save.");
                if (File.Exists(path)) File.Replace(tmp, path, null);   // atomic on the same volume; preserves the original's ACLs
                else File.Move(tmp, path);                              // brand-new file
                tmp = null;
            }
            finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
        }

        /// <summary>The EOL this file is written with: CRLF for Clarion (tooling expects it), else the file's own
        /// detected style. Single home for the language→EOL policy used by every write path.</summary>
        private string TargetEol { get { return _language == "clarion" ? "\r\n" : _fileEol; } }

        /// <summary>Shared file-mode write: normalize EOL per language (Clarion = CRLF, else the file's detected
        /// style) then atomically write, requiring the on-disk version to still match <paramref name="expectedSig"/>.
        /// Returns the normalized text actually written. Single home for the write policy used by BOTH the
        /// interactive save and the close-save path. (pipeline Run-2 dedup)</summary>
        private string WriteFileMode(string text, string expectedSig)
        {
            string outText = NormalizeEol(text, TargetEol);
            WriteFileAtomic(_filePath, outText, _fileEncoding, expectedSig);
            return outText;
        }

        /// <summary>Write the unsaved buffer to a UNIQUE recovery file next to the original, created EXCLUSIVELY
        /// (CreateNew) so it can neither overwrite a real sibling nor stomp an earlier recovery copy. Returns the
        /// path written. (pipeline Run-3: security + adversary flagged the old fixed ".unsaved.bak" name.)</summary>
        private string WriteRecoveryBackup(string text)
        {
            string baseName = _filePath + ".unsaved";
            for (int i = 0; ; i++)
            {
                string candidate = baseName + (i == 0 ? "" : "." + i) + ".bak";
                try
                {
                    using (var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var sw = new StreamWriter(fs, _fileEncoding))
                        sw.Write(text);
                    return candidate;
                }
                catch (IOException) when (i < 1000 && File.Exists(candidate))
                {
                    // name already taken — try the next index
                }
            }
        }

        /// <summary>Close-time save (best-effort, no async round-trip) of the host-mirrored buffer. Uses the SAME
        /// changed-on-disk consent as the interactive save — <see cref="_fileDiskSig"/>, the version the user last
        /// saw — so it never silently clobbers an externally-changed file. On conflict OR write failure it does NOT
        /// drop the edits: it writes a sidecar backup next to the file and tells the user where it went.
        /// (pipeline Run-2: debugger + both Codex gates converged on the old silent-clobber/swallow path.)</summary>
        private void SaveOnClose()
        {
            try
            {
                WriteFileMode(_fileLiveText, _fileDiskSig);   // guarded: throws if the disk moved since the user last saw it
            }
            catch
            {
                // Conflict or write failure — preserve the edits WITHOUT overwriting the external change, to a
                // UNIQUE recovery file (never stomps a sibling or an earlier recovery copy).
                try
                {
                    string backup = WriteRecoveryBackup(NormalizeEol(_fileLiveText, TargetEol));
                    MessageBox.Show(_title + " changed on disk (or could not be written), so your unsaved edits were NOT applied to it.\n\nThey were saved to:\n" + backup,
                        "CA Editor — saved to backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { /* last-ditch at teardown; nothing more we can safely do */ }
            }
        }

        /// <summary>File-mode reload: re-read the file from disk and push it to the page (discards edits).</summary>
        private void HandleReload()
        {
            if (!_fileMode) return;
            try
            {
                // Re-detect encoding + EOL: the file may have been externally rewritten in a different encoding
                // since open. Reusing the stale open-time encoding would misdecode and a later save would write the
                // corrupted text back. (pipeline item 4 — adversary)
                _sourceText = EncodingHelper.ReadAllText(_filePath, out _fileEncoding);
                _fileEol = DetectEol(_sourceText);
                _fileDiskSig = ReadFileSignature(_filePath);
                _fileOverwriteArmedSig = null;
                _fileLiveText = _sourceText;
                _fileDirty = false;
                TrySetHostDirty(false);
                SendSource();
            }
            catch (Exception ex)
            {
                PostSaveResult(false, "Reload failed: " + ex.Message);
            }
        }

        // The actual save round-trip — re-open native embed, write slots, save+close. Runs deferred (off the
        // WebView2 web-message handler) on a settled UI turn so its nested DoEvents pumps don't reenter the
        // WebView2 message loop and deadlock the IDE.
        private void RunSaveRoundTrip(List<string> current)
        {
            // DIAGNOSTIC (ticket e1162adf): the FIRST embed save after a fresh Clarion start blocks ~60s
            // before the embeditor closes; later saves in the same session take seconds. The page posts its
            // "Saving…" toast and hands off immediately, so the stall is somewhere below this line — but
            // "somewhere" is not good enough to fix it. These marks pin the phase to the millisecond.
            // Pure logging: no behaviour change, no control flow depends on it.
            var swSave = System.Diagnostics.Stopwatch.StartNew();
            long lastMark = 0;
            Action<string> mark = phase =>
            {
                try
                {
                    long now = swSave.ElapsedMilliseconds;
                    MonacoSpikeLog.Write("[save-timing] " + phase + " +" + (now - lastMark) + "ms (total " + now + "ms)");
                    lastMark = now;
                }
                catch { }
            };

            mark("enter");
            bool ok;
            string msg;
            // LIVE fast-path (ticket a5bbf005): if THIS tab still holds its native embed open, write straight back
            // into it (no re-open, no locator re-type). Otherwise — a demoted/background tab — fall back to the
            // proven re-open Save. Both share the same per-slot write + SaveAndClose tail.
            bool live = _liveLinked && IsStillLive();
            // IsStillLive() calls AppTreeService().GetEmbedInfo() — an IDE round-trip, and a candidate in its
            // own right for a first-call cost, so it gets its own mark rather than being folded into "enter".
            mark("liveCheck(live=" + live + ",overlay=" + _embedOverlay + ",slots=" + current.Count + ")");

            // OVERLAY save-and-exit (a5bbf005): tear the Monaco surface OFF the embed host BEFORE SaveLive closes the
            // native embed. Closing it disposes the ClaGenEditor host panel, which would otherwise cascade-dispose
            // our WebView2 child on the native close stack (the documented freeze). We already captured `current`, so
            // the surface isn't needed for the write; and SaveLive discards-on-failure (cancels the embed either
            // way), so detaching first is consistent with both outcomes. DetachOverlay disposes the WebView2 on THIS
            // settled turn — well before SaveAndCloseEmbeditor's native-close DoEvents pump.
            if (_embedOverlay && live)
            {
                _teardownIntentional = true;   // save-and-exit — the buffer is being persisted, don't stash (d19c036d)
                DetachOverlay();
                mark("detachOverlay");
                msg = ModernEmbeditorSaver.SaveLive(_procedureName, _editableRanges, _originalSlotTexts, current, out ok);
                mark("SaveLive(overlay) ok=" + ok);
                if (ok) _editStash = null;     // saved — any stash for this proc is now stale
                try
                {
                    if (ok) RefreshPadSources();
                    else MessageBox.Show(msg, "CA Embeditor — save", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
                mark("refreshPadSources(overlay) — DONE");
                return;
            }

            if (live)
                msg = ModernEmbeditorSaver.SaveLive(_procedureName, _editableRanges, _originalSlotTexts, current, out ok);
            else
                msg = ModernEmbeditorSaver.Save(_procedureName, _editableRanges, _originalSlotTexts, current, out ok);
            // The prime suspect: Save() RE-OPENS the native embeditor and pumps DoEvents, which is where a
            // one-time-per-session lazy ABC class load would be paid (see the warmup_abc tool).
            mark((live ? "SaveLive" : "Save(re-open)") + " ok=" + ok);

            // A successful live save closed the native embed (SAVE-AND-EXIT) — drop the live link so Dispose won't
            // try to cancel an already-closed embed, and clear the global live pointer if it's us.
            if (live && ok)
            {
                _liveLinked = false;
                if (ReferenceEquals(_liveInstance, this)) _liveInstance = null;
            }

            // On success, the saved content is the new baseline so a follow-up save sees no changes.
            if (ok && current.Count == _originalSlotTexts.Count) _originalSlotTexts = current;
            if (ok) { _mirroredDirty = false; _editStash = null; }   // persisted — mirror clean, stash stale (d19c036d)
            // The save activated the app tree to drive the embeditor — bring this tab back to the front.
            BringToFront();
            mark("bringToFront");
            // Refresh the pad's IDE-sourced caches (UI thread) so Local/Global Data + Other Files reflect the save.
            if (ok) RefreshPadSources();
            mark("refreshPadSources");
            PostSaveResult(ok, msg);
            mark("postSaveResult — DONE");

            // SAVE-AND-EXIT (live mode only): once the round-trip has settled and the result posted, close this
            // tab — mirroring native Clarion embed editing. Deferred so it runs after the WebView2 gets its save
            // result and the close stack is clean.
            if (live && ok) PostCloseTab();
        }

        /// <summary>True if THIS tab is still the live one AND its native embed is still open (GetEmbedInfo). A tab
        /// demoted by a switch-away, or whose embed already closed, returns false → save uses the fallback path.</summary>
        private bool IsStillLive()
        {
            try
            {
                if (!ReferenceEquals(_liveInstance, this)) return false;
                return new AppTreeService().GetEmbedInfo() != null;
            }
            catch { return false; }
        }

        // ── Live-linked lock lifecycle (ticket a5bbf005) ─────────────────────────────────────────────────────
        // At most ONE tab holds Clarion's native embed open (single-embeditor lock). Opening a new live tab,
        // switching away from the live tab, cancel-closing it, or saving it all RELEASE that embed. We never
        // re-link on activate; a re-focused tab stays a passive snapshot that saves via the re-open path.

        /// <summary>One-time subscription to the workbench's active-document-changed event, so we can release the
        /// live embed the moment the live tab loses foreground. Wired lazily the first time a live tab opens.</summary>
        private static void WireLiveWatch()
        {
            if (_liveWatchWired) return;
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb == null) return;
                wb.ActiveWorkbenchWindowChanged += OnActiveWindowChangedForLive;
                _liveWatchWired = true;
            }
            catch { }
        }

        /// <summary>When the live tab loses foreground, demote it and release its native embed. CC's hard rule:
        /// NEVER pump DoEvents inside this active-view-changed handler (the switch may be mid WebView2 focus
        /// handshake → the original freeze). Mark demoted SYNCHRONOUSLY and DEFER the actual
        /// CancelEmbeditor+WaitForEmbedClosed off the event stack (mirrors the deferred-ShowView fix).</summary>
        private static void OnActiveWindowChangedForLive(object sender, EventArgs e)
        {
            var live = _liveInstance;
            if (live == null) return;
            ModernEmbeditorViewContent active = null;
            try { active = FocusedModernView(); } catch { }
            if (ReferenceEquals(active, live)) { live._liveActivatedOnce = true; return; }   // foreground → arm + stay live

            // Not the foreground doc. But if the live tab has NOT yet been brought to the foreground even once,
            // this is the open-time activation churn (ShowView placed the tab in the background before we could
            // activate it), NOT a genuine switch-away. Releasing now would drop the native embed before the first
            // save and demote save-and-exit to the re-open path. Wait until it's been foreground once. (a5bbf005)
            if (!live._liveActivatedOnce) return;

            _liveInstance = null;            // demote synchronously (cheap) …
            live._liveLinked = false;
            live.PostReleaseNativeEmbed();   // … release the embed on a clean, non-reentrant turn
        }

        /// <summary>Defer CancelEmbeditor+WaitForEmbedClosed off the current (event) stack, then run it on a settled
        /// turn. The brief window where the lock is still held is covered by ReleaseLiveInstanceSync at the next
        /// live open (and by OpenAndMirror's own WaitForEmbedClosed guard).</summary>
        private void PostReleaseNativeEmbed()
        {
            int gen = _liveGen;   // capture at QUEUE time; a newer live open bumps _liveGen and invalidates this
            Action release = () =>
            {
                try
                {
                    // If a newer live tab was acquired after we queued, the currently-open embed is ITS embed —
                    // not the one we meant to release (which ReleaseLiveInstanceSync already closed synchronously).
                    // No-op so we never cancel a newer tab's embed. (CC static-review race fix, a5bbf005.)
                    if (gen != _liveGen) return;
                    var appTree = new AppTreeService();
                    if (appTree.GetEmbedInfo() != null)
                    {
                        appTree.CancelEmbeditor();
                        ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000);
                    }
                }
                catch { }
            };
            try
            {
                if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke(release);
                else
                {
                    var ctx = System.Threading.SynchronizationContext.Current;
                    if (ctx != null) ctx.Post(_ => release(), null); else release();
                }
            }
            catch { try { release(); } catch { } }
        }

        /// <summary>Synchronously release whatever tab currently holds the live embed (if any). Called from the
        /// live-OPEN path, which runs on the launch delegate (off any event stack), so pumping DoEvents here is
        /// safe — it guarantees no stale embed is held before we open the next procedure's embed.
        ///
        /// <paramref name="cancelOpenEmbed"/> (default true, for the live-OPEN callers that are ABOUT to open a
        /// NEW embed): also CancelEmbeditor the currently-open native embed. The poll-detect ATTACH path
        /// (ticket 4d16b53a, AttachOverlayToOpenEmbed) passes FALSE — the currently-open embed is the one the
        /// developer just opened via Clarion's own menu and is our DOCK TARGET, so cancelling it would close the
        /// very embed we're attaching to (symptom: native embed flashes open for ~1s then exits). In the
        /// single-embeditor model any prior live overlay's embed has already been replaced by this one, so
        /// detaching the stale overlay (below) without a cancel leaks nothing.</summary>
        internal static void ReleaseLiveInstanceSync(bool cancelOpenEmbed = true)
        {
            // Bump the acquisition generation FIRST (before the new embed opens): any switch-away release still
            // queued for a previous live tab captured the old gen and will now no-op instead of cancelling the
            // embed we're about to open. We still cancel the currently-open embed by GetEmbedInfo() below — that
            // closes the outgoing embed synchronously here, so the no-op'd deferred release leaks nothing.
            _liveGen++;
            var live = _liveInstance;
            _liveInstance = null;
            if (live != null)
            {
                live._liveLinked = false;
                // OVERLAY (a5bbf005): the outgoing live surface is docked on the embed we're about to cancel below.
                // Detach it NOW (this runs on the launch delegate — a settled turn) so CancelEmbeditor's disposal of
                // the ClaGenEditor host panel can't cascade-dispose the WebView2 on that stack. DetachOverlay is
                // idempotent + a no-op for the tab-mode (non-overlay) live path.
                if (live._embedOverlay) { try { live.DetachOverlay(); } catch { } }
            }
            // ATTACH path (4d16b53a): leave the currently-open native embed OPEN — it is our dock target.
            if (!cancelOpenEmbed) return;
            try
            {
                var appTree = new AppTreeService();
                if (appTree.GetEmbedInfo() != null)
                {
                    appTree.CancelEmbeditor();
                    ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000);
                }
            }
            catch { }
        }

        /// <summary>Close this tab after a successful live save-and-exit. Deferred onto a clean turn (never the save
        /// round-trip's DoEvents-pumped stack) — re-entrant teardown of a WebView2 view risks the native↔WebView2
        /// focus deadlock. Uses IWorkbenchWindow.CloseWindow(force:true); the content is already persisted.</summary>
        private void PostCloseTab()
        {
            Action close = () =>
            {
                try
                {
                    var w = WorkbenchWindow;
                    if (w != null)
                    {
                        var m = w.GetType().GetMethod("CloseWindow", new[] { typeof(bool) });
                        if (m != null) m.Invoke(w, new object[] { true });   // force — already persisted
                    }
                }
                catch { }
            };
            try
            {
                if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke(close);
                else close();
            }
            catch { }
        }

        /// <summary>Re-select this view's tab (the save round-trip activates the app tree to drive the embeditor).</summary>
        private void BringToFront()
        {
            // Overlay mode has no workbench tab to SelectWindow — "front" means the Monaco surface on top of the
            // embeditor's host panel. Just re-assert z-order over the native text area. (a5bbf005)
            if (_embedOverlay) { try { _panel?.BringToFront(); } catch { } return; }
            // DEFER the re-select onto a clean, non-reentrant turn. The save round-trip just pumped DoEvents inside
            // the native TryClose; re-activating this (WebView2) tab synchronously on that same stack risks the very
            // focus deadlock we're fixing on the close side. Post it (same primitive HandleSave uses) so it runs
            // after the close stack fully unwinds. Re-ACTIVATING an existing WebView2 tab on a settled turn is safe
            // — only CREATING / manual SetFocus on a reentrant stack deadlocks. Use SelectWindow (the SharpDevelop
            // view activation), NOT a WebView2-specific focus call.
            try
            {
                Action select = () =>
                {
                    try
                    {
                        var w = WorkbenchWindow;
                        if (w != null)
                        {
                            w.GetType().GetMethod("SelectWindow", Type.EmptyTypes)?.Invoke(w, null);
                            // This tab is now the foreground doc. If it's the live one, ARM the switch-away release
                            // (see _liveActivatedOnce): from here on, losing foreground genuinely means "switched
                            // away" and should release the native embed. Set deterministically here rather than
                            // relying only on the active-window event, which may not re-fire if we were already
                            // the active window. (a5bbf005 probe fix)
                            if (_liveLinked && ReferenceEquals(_liveInstance, this)) _liveActivatedOnce = true;
                        }
                    }
                    catch { }
                };
                if (_panel != null && _panel.IsHandleCreated)
                    _panel.BeginInvoke(select);
                else
                    select();
            }
            catch { }
        }

        // ── Embed OVERLAY hosting (ticket a5bbf005) ─────────────────────────────────────────────────────
        // Dock the Monaco surface FILL on top of the open native embeditor's host panel (ClaGenEditor.Control),
        // native embed alive underneath. Runs on the launcher's settled Post turn (same freeze-safe turn ShowView
        // used), so the WebView2 async init that follows AddControl has a non-reentrant message loop to complete on.

        /// <summary>Attach this view's Monaco surface as an in-place overlay over the open native embeditor's host
        /// panel (<paramref name="host"/> = ClaGenEditor.Control). The native embed stays open as the write-back
        /// target; <paramref name="genEditor"/> is the ClaGenEditor view content whose Disposed we hook so an
        /// uncontrolled embed close (native cancel / Source-tab close / regen) tears the overlay down before WinForms
        /// cascades disposal into our WebView2.</summary>
        /// <summary>Ticket 4d16b53a flicker: synchronously drop an opaque cover panel over the embed host BEFORE the
        /// mirror/WebView2 work, so the native text area is hidden before it paints. The returned panel is later
        /// ADOPTED by <see cref="ShowAsEmbedOverlay"/> as its cover (kept on top until Monaco's first paint). Cheap
        /// WinForms only — no WebView2 — so it's safe on any settled turn. Caller removes it if the attach aborts.</summary>
        /// <summary>d4635694 follow-up — the embed host panel became visible again (tab-back to the app
        /// window's embed view). An overlay is NOT a workbench tab, so no SwitchedTo fires for it, and with
        /// the duplicate re-attach now correctly suppressed nothing else hands the keyboard over (John:
        /// arrows dead after tab-back, edits intact). If OUR live overlay is docked on this host, re-claim
        /// the CA Find pad and focus Monaco — exactly what a tab switch does for the tab-mode editors.</summary>
        internal static void OnEmbedHostShown(Control host)
        {
            try
            {
                var live = _liveInstance;
                if (live == null || !live._embedOverlay || live._overlayDetached || host == null) return;
                if (!ReferenceEquals(live._overlayHost, host)) return;
                Services.CaFindBroker.NotifyActivity(live);
                if (live._panel != null)
                {
                    live._panel.FocusEditor();
                    live._panel.PostJson("{\"type\":\"focusEditor\"}");
                }
            }
            catch { }
        }

        /// <summary>d4635694 — TRUE when the live overlay is STILL docked on <paramref name="host"/> for the
        /// same open PWEE: the caller's attach request is a duplicate trigger (e.g. the embed monitor re-firing
        /// after a tab-away/tab-back), not a new embed. A rebuild on a duplicate would discard the overlay's
        /// unsaved Monaco buffer — the caller must no-op instead.</summary>
        internal static bool IsLiveOverlayCurrent(Control host, object pwee)
        {
            try
            {
                var live = _liveInstance;
                if (live == null || !live._embedOverlay || live._overlayDetached) return false;
                if (host == null || pwee == null) return false;
                if (!ReferenceEquals(live._overlayHost, host)) return false;
                if (!ReferenceEquals(live._overlayPwee, pwee)) return false;
                // Belt: the Monaco surface must still actually sit in the host's control tree.
                return live._panel != null && !live._panel.IsDisposed && ReferenceEquals(live._panel.Parent, host);
            }
            catch { return false; }
        }

        internal static Panel AddInstantCover(Control host, bool isDark)
        {
            var cover = new Panel { Dock = DockStyle.Fill, BackColor = isDark ? Color.FromArgb(0x1E, 0x1E, 0x1E) : Color.White };
            host.Controls.Add(cover);
            cover.BringToFront();
            return cover;
        }

        internal void ShowAsEmbedOverlay(Control host, object genEditor, Panel preCover = null, object pwee = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            _embedOverlay = true;
            _overlayHost = host;
            _overlayGenEditor = genEditor;
            _overlayPwee = pwee;

            // Route saves through the live fast path (SaveLive writes into the still-open embed) WITHOUT the tab
            // switch-away watch: the overlay lives inside the embed's own document, so there is no "switch away" to
            // release on. Set live AFTER the ctor (which we called with liveLinked:false, so WireLiveWatch never ran).
            _liveLinked = true;
            _liveInstance = this;

            // Opaque cover first (hide the native text area before Monaco paints — no flash), then the WebView2 on
            // top, then the cover above it until the page signals its first paint. Mirrors MonacoClarionSourceEditor.
            // ADOPT the instant pre-cover if the caller already docked one (4d16b53a flicker — it went up before the
            // native paint); otherwise create it now.
            if (preCover != null)
            {
                _overlayCover = preCover;   // already added to host + themed by AddInstantCover
            }
            else
            {
                _overlayCover = new Panel { Dock = DockStyle.Fill, BackColor = _isDark ? Color.FromArgb(0x1E, 0x1E, 0x1E) : Color.White };
                host.Controls.Add(_overlayCover);
            }
            _overlayCover.BringToFront();

            _panel.Dock = DockStyle.Fill;
            host.Controls.Add(_panel);
            _panel.BringToFront();
            _overlayCover.BringToFront();   // keep the cover ABOVE the WebView2 until Monaco has painted

            _overlayCoverSafety = new Timer { Interval = 6000 };
            _overlayCoverSafety.Tick += (s, e) => RemoveOverlayCover();
            _overlayCoverSafety.Start();

            HideNativeChrome(host);
            CaptureNavIcons();
            HookOverlayTeardown(genEditor);
            HookEmbedClosing(genEditor);
            WireNativeEmbedCaretMirror();
            Services.ErrorPadNavigationInterceptor.EnsureInstalled();   // reroute error clicks while an overlay is live (d3ab083a)
        }

        /// <summary>Subscribe to the live native embeditor's caret while the overlay covers it (see the
        /// _nativeEmbedCaret field comment). Idempotent: unwires any prior subscription first.</summary>
        private void WireNativeEmbedCaretMirror()
        {
            try
            {
                UnwireNativeEmbedCaretMirror();
                _nativeEmbedCaret = Services.EmbeditorCompletionService.GetNativeCaret();
                if (_nativeEmbedCaret == null) return;
                _lastNativeEmbedLine = _nativeEmbedCaret.Line;
                _nativeEmbedCaret.PositionChanged += OnNativeEmbedCaretMoved;
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("WireNativeEmbedCaretMirror error: " + ex.Message); }
        }

        private void UnwireNativeEmbedCaretMirror()
        {
            try { if (_nativeEmbedCaret != null) _nativeEmbedCaret.PositionChanged -= OnNativeEmbedCaretMoved; }
            catch { }
            finally { _nativeEmbedCaret = null; }
        }

        private void OnNativeEmbedCaretMoved(object sender, EventArgs e)
        {
            try
            {
                if (_nativeEmbedCaret == null || _panel == null) return;
                int line = _nativeEmbedCaret.Line;
                if (line == _lastNativeEmbedLine) return;   // coalesce the Line/Column double-fire of one jump
                _lastNativeEmbedLine = line;
                int column = _nativeEmbedCaret.Column;
                Action reveal = () =>
                {
                    try { _panel?.RevealLine(line + 1, column + 1); }   // native 0-based → Monaco 1-based
                    catch { }
                };
                if (_panel.IsHandleCreated && _panel.InvokeRequired) _panel.BeginInvoke(reveal);
                else reveal();
            }
            catch (Exception ex) { ClarionAssistant.MonacoSpikeLog.Write("OnNativeEmbedCaretMoved error: " + ex.Message); }
        }

        /// <summary>Hide the native embeditor chrome (its ~24px Dock=Top toolbar strip with green-check/red-X/
        /// embed-nav + header) so only OUR Monaco toolbar shows. Per CC's probe (a5bbf005) it's a real WinForms
        /// child of SdiWorkspaceWindow sitting ABOVE the content panel (ClaGenEditor.Control); Visible=false hides
        /// it and the Dock=Fill content reflows up to fill the gap. Logs every sibling to a file so we can confirm
        /// exactly which control(s) matched. Best-effort — never throws into the open path.</summary>
        private void HideNativeChrome(Control content)
        {
            try
            {
                if (content == null) return;
                var sb = new StringBuilder();

                // (a) The header strip (AppHeaderLabel) is a Dock=Top sibling of the content panel in
                // SdiWorkspaceWindow — a small strip positioned ABOVE the content.
                var wb = content.Parent;
                if (wb != null)
                {
                    _chromeHost = wb;
                    sb.AppendLine("[overlay chrome] parent=" + wb.GetType().FullName + " children=" + wb.Controls.Count +
                                  " contentTop=" + content.Top);
                    foreach (Control c in wb.Controls)
                    {
                        bool isChrome = !ReferenceEquals(c, content) && c.Visible && c.Top < content.Top && c.Height > 0 && c.Height <= 40;
                        sb.AppendLine("  [parent] " + (isChrome ? "HIDE " : "keep ") + c.GetType().FullName +
                                      " dock=" + c.Dock + " bounds=" + c.Bounds + " vis=" + c.Visible);
                        if (isChrome)
                        {
                            // Capture the header text (AppHeaderLabel = "Proc - Embeditor - (module.clw)") before we
                            // hide it, so OUR clickable header can reproduce it and route a click to Open Source. (b1e05287)
                            try { if (string.IsNullOrWhiteSpace(_nativeHeaderText) && !string.IsNullOrWhiteSpace(c.Text)) _nativeHeaderText = c.Text; } catch { }
                            _hiddenChrome.Add(c); c.Visible = false;
                        }
                    }
                }

                // (b) The native embeditor TOOLBAR (green-check save / red-X cancel / embed-nav + our injected
                // buttons) is a Dock=Top child INSIDE the content panel itself, above the Dock=Fill text area. Our
                // overlay (_panel + _overlayCover) is also inside content but Dock=Fill, so filtering Dock=Top hides
                // the native toolbar without touching our surface or the text area. (a5bbf005, confirmed via log)
                sb.AppendLine("[overlay chrome] content=" + content.GetType().FullName + " children=" + content.Controls.Count);
                foreach (Control c in content.Controls)
                {
                    bool ours = ReferenceEquals(c, _panel) || ReferenceEquals(c, _overlayCover);
                    bool isToolbar = !ours && c.Visible && c.Dock == DockStyle.Top && c.Height > 0 && c.Height <= 60;
                    sb.AppendLine("  [content] " + (isToolbar ? "HIDE " : "keep ") + c.GetType().FullName +
                                  " dock=" + c.Dock + " bounds=" + c.Bounds + " vis=" + c.Visible);
                    if (isToolbar)
                    {
                        // Keep a handle to the native toolbar so our header can PerformClick its "Open Source" item. (b1e05287)
                        if (_nativeToolStrip == null) { _nativeToolStrip = c as ToolStrip; CaptureChromeColors(_nativeToolStrip); CaptureNativeIcons(_nativeToolStrip); }
                        _hiddenChrome.Add(c); c.Visible = false;
                    }
                }

                try { content.PerformLayout(); } catch { }
                try { wb?.PerformLayout(); } catch { }
                OverlayChromeLog(sb.ToString());
            }
            catch (Exception ex) { OverlayChromeLog("[overlay chrome] HideNativeChrome error: " + ex.Message); }
        }

        /// <summary>Capture the native toolbar's colors so our overlay chrome follows the active Clarion theme. Prefer
        /// the ToolStripProfessionalRenderer's gradient (that's the blue bar you see); fall back to flat BackColor.
        /// ForeColor drives our header/button text. All best-effort → null leaves the page on its own theme. (b1e05287)</summary>
        private void CaptureChromeColors(ToolStrip strip)
        {
            if (strip == null) return;
            try
            {
                var rend = strip.Renderer as ToolStripProfessionalRenderer;
                if (rend != null && rend.ColorTable != null)
                {
                    _chromeBg1 = ToCssHex(rend.ColorTable.ToolStripGradientBegin);
                    _chromeBg2 = ToCssHex(rend.ColorTable.ToolStripGradientEnd);
                }
                if (_chromeBg1 == null) { _chromeBg1 = ToCssHex(strip.BackColor); _chromeBg2 = _chromeBg1; }
                _chromeFg = ToCssHex(strip.ForeColor);
            }
            catch { }
        }

        private static string ToCssHex(Color c)
        {
            try { if (c.A == 0) return null; return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
            catch { return null; }
        }

        /// <summary>Extract the REAL native embeditor icons from the hidden ToolStrip's items (save/cancel/prev+next
        /// embed/filled) as PNG data-URIs, keyed by role, so our WebView2 toolbar renders pixel-perfect Clarion icons
        /// instead of hand-drawn SVGs. Role is inferred from each item's Text/ToolTipText. Logs every item so the
        /// mapping is verifiable. Best-effort. (b1e05287)</summary>
        private void CaptureNativeIcons(ToolStrip strip)
        {
            if (strip == null) return;
            try
            {
                var log = new StringBuilder();
                log.AppendLine("[overlay icons] toolstrip items=" + strip.Items.Count + " scalingSize=" + strip.ImageScalingSize);
                foreach (ToolStripItem it in strip.Items)
                {
                    string text = it.Text ?? "", tip = it.ToolTipText ?? "";
                    bool hasImg = it.Image != null;
                    string role = RoleForItem(text, tip);
                    log.AppendLine("  item text='" + text + "' tip='" + tip + "' img=" + hasImg + " role=" + (role ?? "-"));
                    if (hasImg && role != null && !_nativeIcons.ContainsKey(role))
                    {
                        var uri = ImageToDataUri(it.Image);
                        if (uri != null) _nativeIcons[role] = uri;
                    }
                }
                log.AppendLine("[overlay icons] captured roles=" + string.Join(",", _nativeIcons.Keys));
                OverlayChromeLog(log.ToString());
            }
            catch (Exception ex) { OverlayChromeLog("[overlay icons] error: " + ex.Message); }
        }

        /// <summary>Map a native toolbar item to one of our button roles by its text/tooltip (filled checked before
        /// plain embed; excludes CA Embeditor / Open Source). Null = not one of ours.</summary>
        private static string RoleForItem(string text, string tip)
        {
            string s = ((text ?? "") + " " + (tip ?? "")).ToLowerInvariant();
            if (s.Contains("filled") && s.Contains("prev")) return "prevFilled";
            if (s.Contains("filled") && s.Contains("next")) return "nextFilled";
            if (s.Contains("embed") && s.Contains("prev")) return "prevEmbed";
            if (s.Contains("embed") && s.Contains("next")) return "nextEmbed";
            if (s.Contains("cancel")) return "cancel";
            if (s.Contains("save") && s.IndexOf("source", StringComparison.Ordinal) < 0) return "save";
            return null;
        }

        private static string ImageToDataUri(System.Drawing.Image img)
        {
            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return null; }
        }

        // Navigate Back/Forward icons live in the IDE's main navigation toolbar (NOT the embeditor's). Scanned once
        // across the workbench form's ToolStrips and cached, then merged into _nativeIcons per instance. (b1e05287)
        private static bool _navIconsScanned;
        private static string _navBackUri, _navFwdUri;

        private void CaptureNavIcons()
        {
            try
            {
                if (!_navIconsScanned)
                {
                    _navIconsScanned = true;
                    var log = new StringBuilder("[overlay navicons] scanning workbench toolstrips\n");
                    Control root = null;
                    try { root = GetProp(WorkbenchSingleton.Workbench, "MainForm") as Control; } catch { }
                    if (root == null) { try { root = Form.ActiveForm; } catch { } }
                    if (root != null) ScanForNav(root, log, 0);
                    log.AppendLine("[overlay navicons] back=" + (_navBackUri != null) + " fwd=" + (_navFwdUri != null));
                    OverlayChromeLog(log.ToString());
                }
                if (_navBackUri != null && !_nativeIcons.ContainsKey("navBack")) _nativeIcons["navBack"] = _navBackUri;
                if (_navFwdUri != null && !_nativeIcons.ContainsKey("navFwd")) _nativeIcons["navFwd"] = _navFwdUri;
            }
            catch { }
        }

        private static void ScanForNav(Control c, StringBuilder log, int depth)
        {
            if (c == null || depth > 8 || (_navBackUri != null && _navFwdUri != null)) return;
            var strip = c as ToolStrip;
            if (strip != null)
            {
                foreach (ToolStripItem it in strip.Items)
                {
                    if (it.Image == null) continue;
                    string s = ((it.Text ?? "") + " " + (it.ToolTipText ?? "")).ToLowerInvariant();
                    if (s.IndexOf("back", StringComparison.Ordinal) >= 0 || s.IndexOf("forward", StringComparison.Ordinal) >= 0 || s.IndexOf("navigate", StringComparison.Ordinal) >= 0)
                        log.AppendLine("  candidate tip='" + it.ToolTipText + "' text='" + it.Text + "'");
                    if (_navBackUri == null && s.Contains("navigate back")) _navBackUri = ImageToDataUri(it.Image);
                    else if (_navFwdUri == null && s.Contains("navigate forward")) _navFwdUri = ImageToDataUri(it.Image);
                }
            }
            try { foreach (Control child in c.Controls) ScanForNav(child, log, depth + 1); } catch { }
        }

        /// <summary>JSON object of role→data-URI for the captured native icons (empty {} if none).</summary>
        private string NativeIconsJson()
        {
            if (_nativeIcons.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in _nativeIcons)
            {
                if (!first) sb.Append(",");
                sb.Append(JsonString(kv.Key)).Append(":").Append(JsonString(kv.Value));
                first = false;
            }
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>Restore the native chrome we hid (so a later native embed open shows its toolbar again).</summary>
        private void RestoreNativeChrome()
        {
            try
            {
                foreach (var c in _hiddenChrome) { try { if (c != null && !c.IsDisposed) c.Visible = true; } catch { } }
                _hiddenChrome.Clear();
                try { if (_chromeHost != null && !_chromeHost.IsDisposed) _chromeHost.PerformLayout(); } catch { }
                _chromeHost = null;
            }
            catch { }
        }

        /// <summary>One-shot diagnostic log for the chrome-hide probe → %APPDATA%\ClarionAssistant\overlay-chrome.log
        /// (readable by CC/John to confirm exactly which strip matched). Best-effort.</summary>
        private static void OverlayChromeLog(string text)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClarionAssistant");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "overlay-chrome.log"), text + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Drop the anti-flash cover once Monaco has painted (or the safety timer fires).</summary>
        private void RemoveOverlayCover()
        {
            try { if (_overlayCoverSafety != null) { _overlayCoverSafety.Stop(); _overlayCoverSafety.Dispose(); _overlayCoverSafety = null; } } catch { }
            try
            {
                if (_overlayCover != null)
                {
                    _overlayCover.Parent?.Controls.Remove(_overlayCover);
                    _overlayCover.Dispose();
                    _overlayCover = null;
                }
            }
            catch { }
        }

        /// <summary>Subscribe to the ClaGenEditor's Disposed so an embed close we did NOT initiate (native cancel,
        /// Source-tab close, app-gen regen) detaches the overlay. Best-effort/reflection — the event name matches
        /// SharpDevelop's IViewContent.Disposed.</summary>
        private void HookOverlayTeardown(object genEditor)
        {
            try
            {
                var evt = genEditor?.GetType().GetEvent("Disposed");
                if (evt == null) return;
                _overlayDisposedHandler = (s, e) => PostDetachOverlay();
                evt.AddEventHandler(genEditor, _overlayDisposedHandler);
            }
            catch { }
        }

        private void UnhookOverlayTeardown()
        {
            try
            {
                if (_overlayGenEditor != null && _overlayDisposedHandler != null)
                {
                    var evt = _overlayGenEditor.GetType().GetEvent("Disposed");
                    evt?.RemoveEventHandler(_overlayGenEditor, _overlayDisposedHandler);
                }
            }
            catch { }
            _overlayDisposedHandler = null;
            UnhookEmbedClosing();
        }

        /// <summary>Subscribe to the embed's workbench window ClosingEvent so a close we did NOT initiate —
        /// Ctrl+F4, the tab's [x], File&gt;Close — can ASK about unsaved edits instead of swallowing the question.
        ///
        /// WHY THIS EXISTS SEPARATELY FROM HookOverlayTeardown: that one hooks Disposed, which is PAST TENSE.
        /// By the time it fires the embed is already gone and an EventHandler carries no CancelEventArgs, so
        /// there is structurally nothing to cancel — the dirty path could only stash and close silently, which
        /// is exactly the bug (John, 2026-08-27: native Clarion asks, we did not).
        ///
        /// ClosingEvent is a CancelEventHandler and is the same hook MonacoClarionSourceEditor already uses for
        /// the CA Editor, which is why that surface has always prompted and this one has not. Confirmed against
        /// CWBinding 12.0.0.14000 by reflection rather than assumed: ClaGenEditor inherits a WorkbenchWindow
        /// property (declared on CommonClarionEditor) typed IWorkbenchWindow, which declares
        /// ClosingEvent : CancelEventHandler.
        ///
        /// Reflection, not a direct cast, to match the surrounding code's stance on this fork: its object
        /// surface returns null silently rather than throwing, so every step logs and a miss says WHICH step
        /// missed. A silent no-op here would reproduce the very bug it fixes.</summary>
        private void HookEmbedClosing(object genEditor)
        {
            try
            {
                if (_embedClosingHandler != null) return;   // already subscribed — this is called again on retry
                if (genEditor == null) { LogHookMissOnce("no genEditor"); return; }
                var wbw = genEditor.GetType().GetProperty("WorkbenchWindow")?.GetValue(genEditor, null);
                // Not an error on the FIRST pass: the window is often unassigned at attach time, which is why
                // HandleEmbedState retries. Logged once so a PERMANENT null is still visible.
                if (wbw == null) { LogHookMissOnce("WorkbenchWindow null (retrying on embed-state)"); return; }

                var evt = wbw.GetType().GetEvent("ClosingEvent");
                if (evt == null)
                    foreach (var itf in wbw.GetType().GetInterfaces()) { evt = itf.GetEvent("ClosingEvent"); if (evt != null) break; }
                if (evt == null)
                {
                    LogHookMissOnce("ClosingEvent NOT FOUND on " + wbw.GetType().FullName
                        + " — dirty closes fall back to the silent stash");
                    return;
                }

                _embedClosingHandler = new System.ComponentModel.CancelEventHandler(OnEmbedWorkbenchClosing);
                evt.AddEventHandler(wbw, _embedClosingHandler);
                _embedClosingWindow = wbw;
                _embedClosingEvt = evt;
                MonacoSpikeLog.Write("[embed-closing] hooked ClosingEvent on " + wbw.GetType().FullName);
            }
            catch (Exception ex) { LogHookMissOnce("hook error: " + ex.Message); }
        }

        /// <summary>One line per overlay session for a hook miss, not one per keystroke. HandleEmbedState
        /// retries the hook on every buffer change, so an unguarded log here would bury the very evidence
        /// someone would come to this log to find.</summary>
        private void LogHookMissOnce(string why)
        {
            if (_embedClosingMissLogged) return;
            _embedClosingMissLogged = true;
            MonacoSpikeLog.Write("[embed-closing] NOT hooked: " + why);
        }

        private void UnhookEmbedClosing()
        {
            try
            {
                if (_embedClosingWindow != null && _embedClosingEvt != null && _embedClosingHandler != null)
                    _embedClosingEvt.RemoveEventHandler(_embedClosingWindow, _embedClosingHandler);
            }
            catch { }
            _embedClosingHandler = null;
            _embedClosingWindow = null;
            _embedClosingEvt = null;
            _embedClosingMissLogged = false;   // next overlay session gets to report its own miss
        }

        /// <summary>Unsaved edits + a close we did not initiate → veto the close and hand the gesture to the
        /// page's own Ctrl+Q flow, so Ctrl+F4 and Ctrl+Q become the SAME code after this first step.
        ///
        /// WHY VETO-AND-DELEGATE RATHER THAN SAVE INLINE. The save path calls DetachOverlay, which disposes the
        /// WebView2. This file warns repeatedly that disposing it on the native embed-close stack risks the
        /// native&lt;-&gt;WebView2 focus deadlock — PostDetachOverlay exists purely to get off that stack. Saving
        /// synchronously inside ClosingEvent would do exactly the forbidden thing. Cancelling instead costs
        /// nothing: every branch of the page's flow closes the surface itself, so the close still happens, just
        /// on a settled turn and through paths GH #193 already proved.
        ///
        /// It also keeps ONE set of yes/no semantics. The page owns what each answer does (save-and-exit vs
        /// discard differs by live-linked/snapshot/file mode); duplicating that here is how the two paths drift.
        ///
        /// NEVER VETO A CLOSE WE CANNOT FOLLOW THROUGH ON. If the page is not there to delegate to, let the
        /// close proceed — the stash in DetachOverlay still protects the edits. A veto with no follow-through
        /// would make Ctrl+F4 look broken, which is worse than the bug being fixed.</summary>
        private void OnEmbedWorkbenchClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // DIAGNOSTIC (bcba6efb): the first cut logged ONLY on intercept, so when the prompt failed to
                // appear the log could not say whether this ran at all — and "never fired" and "fired, guard
                // bailed" want completely different fixes. Log entry and the reason for every early return.
                MonacoSpikeLog.Write("[embed-closing] ClosingEvent FIRED (cancel=" + e.Cancel
                    + " overlay=" + _embedOverlay + " intentional=" + _teardownIntentional
                    + " dirty=" + _mirroredDirty + " fileMode=" + _fileMode + ")");

                if (e.Cancel) { MonacoSpikeLog.Write("[embed-closing] skip: already vetoed"); return; }
                if (!_embedOverlay) { MonacoSpikeLog.Write("[embed-closing] skip: not overlay mode"); return; }
                if (_teardownIntentional) { MonacoSpikeLog.Write("[embed-closing] skip: our own save/cancel"); return; }
                if (!_mirroredDirty) { MonacoSpikeLog.Write("[embed-closing] skip: buffer clean"); return; }

                // The page's cmdSaveAndExit REFUSES in fileMode and returns without closing anything. An
                // overlay is never constructed in file mode (_fileMode is set only by the file-editing ctor),
                // so this cannot fire today — it is here because the follow-through rule below has to be
                // enforceable rather than merely believed. If that ever changes, this declines to veto and
                // Ctrl+F4 keeps working, instead of the embed silently refusing to close.
                if (_fileMode) { MonacoSpikeLog.Write("[embed-closing] skip: fileMode"); return; }

                // OBSERVE ONLY — DO NOT SET e.Cancel HERE. It was tried and it does not work (bcba6efb):
                // CommonGenEditor.WorkbenchWindow_ClosingEvent subscribes at embed open, long before our
                // overlay attaches, so it always runs FIRST. It sets e.Cancel = true itself (it does not want
                // the WINDOW closed) and then closes the embed via TryClose(). We measured our handler
                // entering with cancel=True and dirty=True, and the embed closed regardless — a veto on the
                // workspace window has NO authority over the embed teardown.
                //
                // The working fix is upstream of all this: HandleSyncNativeForClose sets the NATIVE editor's
                // IsDirty before the key is ever dispatched, so Clarion's own TryClose() prompts. This line
                // stays only so the log still shows the close arriving, and so the next reader sees the dead
                // end already explored rather than re-deriving it. Remove with the #192 probe.
                MonacoSpikeLog.Write("[embed-closing] observed dirty close (no veto — Clarion owns this decision)");
            }
            catch (Exception ex)
            {
                // Never let a fault here block the close — that would strand the user in an embed they asked to
                // shut. Falling through leaves e.Cancel as it was, and the stash still holds the edits.
                MonacoSpikeLog.Write("[embed-closing] EXCEPTION (close allowed to proceed): " + ex);
            }
        }

        /// <summary>Defer overlay teardown onto a clean, non-reentrant turn (never the native embed-close stack —
        /// disposing a WebView2 there risks the native&lt;-&gt;WebView2 focus deadlock).</summary>
        private void PostDetachOverlay()
        {
            try
            {
                if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke((Action)(() => DetachOverlay()));
                else DetachOverlay();
            }
            catch { try { DetachOverlay(); } catch { } }
        }

        /// <summary>Teardown responsibilities shared by BOTH close paths — tab mode (Dispose, called by the
        /// workbench) and overlay mode (DetachOverlay — an overlay session is never ShowView'd, so the workbench
        /// never calls Dispose and DetachOverlay IS the entire teardown). Idempotent: whichever path runs first
        /// does the work; a later double-call is a no-op.
        ///
        /// ADD NEW both-modes teardown responsibilities HERE, not in Dispose()/DetachOverlay() directly — the
        /// two lists were kept in sync by hand and drifted twice in one day (#114: dead CaFindBroker entry,
        /// #115: LSP module shadow never reverted). Mode-specific work (native chrome restore, WebView2
        /// detach ordering per the a5bbf005 freeze-safe rules, live-linked cancel, save prompt) stays in the
        /// respective caller. (#119)</summary>
        private void TeardownSession()
        {
            if (_sessionTornDown) return;
            _sessionTornDown = true;
            // DIAGNOSTIC (e1162adf) — see DetachOverlay. RevertShadow makes a SYNCHRONOUS LSP call, so it is
            // a prime suspect for the ~65s stall; these marks say so rather than leaving it inferred.
            var swT = System.Diagnostics.Stopwatch.StartNew();
            long lastT = 0;
            Action<string> markT = phase =>
            {
                try
                {
                    long now = swT.ElapsedMilliseconds;
                    MonacoSpikeLog.Write("[teardown-timing] " + phase + " +" + (now - lastT) + "ms (total " + now + "ms)");
                    lastT = now;
                }
                catch { }
            };

            // CA Find pad (#66): stop routing find traffic to this editor. Without this the broker keeps a
            // DEAD entry under this session's key (embed::<proc> / file path) forever; re-opening the same
            // procedure registers a second, live entry under the SAME key and any key-based lookup (the
            // search results tab) can pick the corpse and post into a disposed control — click does nothing.
            try { Services.CaFindBroker.UnregisterHost(this); } catch { }
            markT("caFindUnregister");

            // #56: while this session was up, every LSP request pushed the wrapped embed buffer to the server
            // under the generated module's REAL path, overriding the on-disk file in the server's view. There
            // is no didClose in the transport, so RevertShadow (pushing the on-disk content back) is the only
            // thing that un-shadows it — without it the server keeps answering hover/definition/references
            // for that .clw from a closed buffer, silently, for the rest of the session.
            try { if (_lspContext != null) _lspContext.RevertShadow(); } catch { }
            markT("lspRevertShadow(ctx=" + (_lspContext != null) + ")");

            lock (_instances) { _instances.Remove(this); }
            markT("instancesRemove");
            try { _settingsReg?.Dispose(); } catch { }
            _settingsReg = null;
            markT("settingsRegDispose — DONE");
        }

        /// <summary>Remove the Monaco surface (and cover) from the embeditor host and dispose it. Idempotent. The
        /// native embed itself is closed by whoever triggered teardown (SaveLive's save-and-close, native cancel, or
        /// the host's own disposal) — we only own the overlay controls.</summary>
        private void DetachOverlay()
        {
            if (_overlayDetached) return;
            _overlayDetached = true;

            // DIAGNOSTIC (bcba6efb): WHO tore this down? Ctrl+F4 makes the embed vanish without any close
            // event firing, so the teardown is reached by some path we have not identified — and the caller
            // is the whole question. Frames only, no args, and only the CA/SharpDevelop ones: enough to name
            // the route without dumping an unreadable wall. Remove with the rest of the #192 probe.
            try
            {
                var frames = new System.Diagnostics.StackTrace(false).GetFrames();
                var interesting = new List<string>();
                if (frames != null)
                    foreach (var fr in frames)
                    {
                        var m = fr.GetMethod();
                        var t = m?.DeclaringType;
                        if (t == null) continue;
                        var n = t.FullName ?? "";
                        if (n.IndexOf("ClarionAssistant", StringComparison.Ordinal) >= 0
                            || n.IndexOf("ICSharpCode", StringComparison.Ordinal) >= 0
                            || n.IndexOf("CWBinding", StringComparison.Ordinal) >= 0
                            || n.IndexOf("SoftVelocity", StringComparison.Ordinal) >= 0)
                            interesting.Add(t.Name + "." + m.Name);
                        if (interesting.Count >= 12) break;
                    }
                MonacoSpikeLog.Write("[detach-who] intentional=" + _teardownIntentional + " dirty=" + _mirroredDirty
                    + " via: " + (interesting.Count > 0 ? string.Join(" <- ", interesting) : "(no recognised frames)"));
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[detach-who] trace failed: " + ex.Message); }
            // DIAGNOSTIC (e1162adf): the save-timing marks proved the ~65s stall lives HERE, not in the save
            // (SaveLive itself takes ~700ms). These bisect DetachOverlay's steps so the next reproduction
            // names the culprit outright instead of narrowing it again.
            var swD = System.Diagnostics.Stopwatch.StartNew();
            long lastD = 0;
            Action<string> markD = phase =>
            {
                try
                {
                    long now = swD.ElapsedMilliseconds;
                    MonacoSpikeLog.Write("[detach-timing] " + phase + " +" + (now - lastD) + "ms (total " + now + "ms)");
                    lastD = now;
                }
                catch { }
            };
            UnhookOverlayTeardown();
            markD("unhookOverlayTeardown");
            UnwireNativeEmbedCaretMirror();   // stop mirroring BEFORE the native embed (and its caret) is torn down
            markD("unwireCaretMirror");

            // Data-loss guard (d19c036d): an EXTERNAL teardown (Clarion's error→embed navigation closing the
            // native embed under us — anything but our own Save/Cancel) with unsaved Monaco edits stashes the
            // page's last mirrored slot texts, so the next attach for this procedure can restore them.
            if (!_teardownIntentional && _mirroredDirty && _mirroredSlots != null &&
                _originalSlotTexts != null && _mirroredSlots.Count == _originalSlotTexts.Count &&
                !string.IsNullOrEmpty(_procedureName))
            {
                _editStash = new EmbedEditStash
                {
                    Proc = _procedureName,
                    Original = new List<string>(_originalSlotTexts),
                    Edited = new List<string>(_mirroredSlots)
                };
                ClarionAssistant.MonacoSpikeLog.Write("embed overlay torn down DIRTY — stashed " + _mirroredSlots.Count +
                    " slot(s) of unsaved edits for " + _procedureName);
            }
            // Overlay mode is never ShowView'd, so the workbench never calls our Dispose() — this IS the
            // teardown for the shared session-scoped state too (broker entry, LSP shadow, instance list). (#119)
            TeardownSession();
            markD("teardownSession");

            RestoreNativeChrome();
            markD("restoreNativeChrome");
            RemoveOverlayCover();
            markD("removeOverlayCover");
            try
            {
                // Reparent the WebView2 out of the host FIRST (so a host disposal in flight can't cascade into it),
                // then dispose it ourselves on this settled turn.
                if (_panel != null)
                {
                    _panel.Parent?.Controls.Remove(_panel);
                    markD("panelReparent");
                    _panel.Dispose();
                    markD("panelDispose(WebView2)");
                    _panel = null;
                }
            }
            catch { }
            markD("DONE");
            if (ReferenceEquals(_liveInstance, this)) _liveInstance = null;
            _liveLinked = false;
            _embedOverlay = false;
            _overlayHost = null;
            _overlayGenEditor = null;
        }

        /// <summary>
        /// LSP completion request from Monaco. Uses the context-free language set (keywords, builtins,
        /// datatypes, attributes, controls) — no per-keystroke buffer sync needed. Runs off the UI thread
        /// and posts the result back keyed by reqId.
        /// </summary>
        /// <summary>
        /// Kick the shared LSP self-heal (idempotent, fire-and-forget) when no client is running yet.
        /// Mirrors the native embeditor's completion-time self-heal (EmbeditorCompletionService.LspStarter,
        /// wired to EnsureLspRunningInBackground) so the Modern editor can also recover the language server —
        /// completion, hover, AND the LSP diagnostics pass all depend on it. The first request after a cold
        /// start still returns empty (server warming); the next one succeeds.
        /// </summary>
        private static void EnsureLspStarted()
        {
            try
            {
                // Only kick the bundled-server self-heal when NO LSP is available at all. When the shared
                // ClarionLsp addin is active, SharedLspBridge.IsRunning is already true and the bundled
                // starter is a deliberate no-op — checking LspClient.Active alone would loop forever
                // ("server starting…") because we intentionally never start the bundled server in that case.
                if (!SharedLspBridge.IsRunning)
                    EmbeditorCompletionService.LspStarter?.Invoke();
            }
            catch { }
        }

        // #56 helpers — the LSP-facing view of the buffer/positions. With a real-module context the
        // LSP buffer carries a prepended MEMBER header, so LSP lines run one AHEAD of Monaco lines;
        // without one these degrade to the classic identity mapping (±1 base conversion only).
        private string LspBuffer(string buffer)
        {
            return (_lspContext != null) ? _lspContext.WrapBuffer(buffer) : buffer;
        }
        private int LspLine0(int monacoLine1)
        {
            return Math.Max(0, monacoLine1 - 1) + ((_lspContext != null) ? _lspContext.LineOffset : 0);
        }
        private int MonacoLine1(int lspLine0)
        {
            return Math.Max(1, lspLine0 + 1 - ((_lspContext != null) ? _lspContext.LineOffset : 0));
        }

        private void HandleCompletion(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                var items = new List<Dictionary<string, object>>();
                string lspStatus;
                try
                {
                    EnsureLspStarted();
                    // Route through SharedLspBridge: shared ClarionLsp when active, else bundled LspClient.
                    if (!SharedLspBridge.IsRunning) lspStatus = "starting";
                    else
                    {
                        // Pass the LIVE buffer (mirror HandleHover). Passing null made the shared server complete
                        // against an empty document → always "no suggestions" (John's test; root-caused with Bob).
                        var comps = SharedLspBridge.GetCompletion(_lspFileName, LspLine0(line), Math.Max(0, column - 1), 2500, LspBuffer(buffer));
                        if (comps != null)
                            foreach (var c in comps)
                                items.Add(new Dictionary<string, object>
                                {
                                    { "label", c.Label },
                                    { "kind", c.Kind },
                                    { "detail", c.Detail },
                                    { "documentation", c.Documentation },
                                    { "insertText", c.InsertText }
                                });
                        // LastCompletionDiagnostic is bundled-only; surface it on the local path, else "ok".
                        var local = LspClient.Active;
                        lspStatus = (local != null && !string.IsNullOrEmpty(local.LastCompletionDiagnostic))
                            ? local.LastCompletionDiagnostic : "ok";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] completion: " + ex.Message);
                    lspStatus = "error: " + ex.Message;
                }
                PostResponse(reqId, new Dictionary<string, object> { { "items", items }, { "lsp", lspStatus } });
            });
        }

        /// <summary>F12 go-to-definition from the embed editor — resolve via the LSP (+ the C# CodeGraph
        /// fallback for cross-project) against the generated-source file, then open/position the target
        /// with MonacoSourceNavigator (same- or cross-file). (#40 / 2ba0ee17)</summary>
        private void HandleDefinition(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                bool navigated = false;
                try
                {
                    EnsureLspStarted();
                    if (SharedLspBridge.IsRunning)
                    {
                        var def = SharedLspBridge.GetDefinition(_lspFileName, LspLine0(line), Math.Max(0, column - 1), LspBuffer(buffer));
                        string targetPath; int targetLine0, targetChar0;
                        bool got = SharedLspBridge.TryGetFirstLocation(def, out targetPath, out targetLine0, out targetChar0);
                        // The embeditor's LSP document is a SYNTHETIC file (_lspFileName has no file on disk),
                        // so a SAME-FILE definition resolves to that synthetic path — NavigateToFileAndLine can't
                        // open it and returns false, which is why F12 did nothing. When the LSP resolves in-buffer
                        // (e.g. a local var) its line numbers ARE this buffer's, so reveal in-place. (task 37e2079f)
                        // With the real-module context (#56) the same-file path is the REAL module path our buffer
                        // shadows: LSP results are in wrapped-buffer coords (MonacoLine1 unwraps them), but the
                        // CodeGraph fallback returns ON-DISK module lines that mean nothing in this buffer — for
                        // routines TryResolveLocalRoutine is authoritative in-buffer, so it wins when it matches.
                        bool sameFile = got && IsSameLspFile(targetPath);
                        if (sameFile)
                        {
                            int localLine;
                            if (_lspContext != null && TryResolveLocalRoutine(buffer, line, column, out localLine))
                            {
                                if (_panel != null) _panel.RevealLine(localLine, 1);
                            }
                            else if (_panel != null)
                            {
                                _panel.RevealLine(MonacoLine1(targetLine0), targetChar0 + 1);
                            }
                            navigated = _panel != null;
                        }
                        else
                        {
                            // Not resolved in-buffer by the LSP. If the symbol is a ROUTINE declared in THIS
                            // embeditor buffer (a DO/GOTO target — the LSP doesn't resolve DO/ROUTINE, so CodeGraph
                            // pointed at the on-disk generated module clbrws001.clw), reveal it in-place instead of
                            // opening that file. Otherwise open the cross-file target. (task 37e2079f — RefreshWindow)
                            int localTargetLine;
                            if (TryResolveLocalRoutine(buffer, line, column, out localTargetLine))
                            {
                                if (_panel != null) _panel.RevealLine(localTargetLine, 1);
                                navigated = _panel != null;
                            }
                            else if (got)
                            {
                                navigated = MonacoSourceNavigator.NavigateToFileAndLine(targetPath, targetLine0 + 1, 1);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] definition: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "navigated", navigated } });
            });
        }

        /// <summary>If the identifier at (line1,col1) in <paramref name="buffer"/> is a ROUTINE declared in
        /// the SAME buffer (a DO/GOTO target), return its 1-based declaration line. The embeditor buffer is one
        /// procedure's view, so any ROUTINE in it belongs here — reveal in-place instead of opening the on-disk
        /// generated module the LSP/CodeGraph resolves it to. Matches "&lt;label&gt; ROUTINE". (task 37e2079f)</summary>
        private bool TryResolveLocalRoutine(string buffer, int line1, int col1, out int targetLine1)
        {
            targetLine1 = 0;
            try
            {
                if (string.IsNullOrEmpty(buffer)) return false;
                string[] lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                int li = line1 - 1;
                if (li < 0 || li >= lines.Length) return false;

                // Identifier under the cursor (Clarion names allow ':' and '.').
                string word = null;
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(lines[li], @"[A-Za-z_][A-Za-z0-9_:.]*"))
                {
                    if (col1 - 1 >= m.Index && col1 - 1 <= m.Index + m.Length) { word = m.Value; break; }
                }
                if (string.IsNullOrEmpty(word)) return false;

                // "<label> ROUTINE" — the routine's declaration (label at line start, optional leading whitespace).
                var re = new System.Text.RegularExpressions.Regex(
                    @"^\s*" + System.Text.RegularExpressions.Regex.Escape(word) + @"\s+ROUTINE\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                for (int i = 0; i < lines.Length; i++)
                    if (re.IsMatch(lines[i])) { targetLine1 = i + 1; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>True when a definition target refers to THIS editor's own LSP document (so a same-file
        /// jump reveals in-place rather than trying to open a file). Exact match, or — for the embeditor's
        /// synthetic bare-name doc — a directory-less target whose file name matches ours.</summary>
        private bool IsSameLspFile(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(_lspFileName)) return false;
            if (string.Equals(targetPath, _lspFileName, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (string.IsNullOrEmpty(System.IO.Path.GetDirectoryName(targetPath)))
                    return string.Equals(System.IO.Path.GetFileName(targetPath),
                                         System.IO.Path.GetFileName(_lspFileName), StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        /// <summary>Ctrl+F12 go-to-implementation from the embed editor — declaration → implementation
        /// body (e.g. a method prototype in a CLASS to its .clw body). Mirrors HandleDefinition's
        /// navigation: a same-file target reveals in-place (unwrapping the #56 header offset), a
        /// cross-file target opens via MonacoSourceNavigator.</summary>
        private void HandleImplementation(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                bool navigated = false;
                try
                {
                    EnsureLspStarted();
                    if (SharedLspBridge.IsRunning)
                    {
                        var impl = SharedLspBridge.GetImplementation(_lspFileName, LspLine0(line), Math.Max(0, column - 1), LspBuffer(buffer));
                        string targetPath; int targetLine0, targetChar0;
                        if (SharedLspBridge.TryGetFirstLocation(impl, out targetPath, out targetLine0, out targetChar0))
                        {
                            if (IsSameLspFile(targetPath))
                            {
                                if (_panel != null) _panel.RevealLine(MonacoLine1(targetLine0), targetChar0 + 1);
                                navigated = _panel != null;
                            }
                            else
                            {
                                navigated = MonacoSourceNavigator.NavigateToFileAndLine(targetPath, targetLine0 + 1, 1);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] implementation: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "navigated", navigated } });
            });
        }

        /// <summary>LSP signature-help request from Monaco (typing '(' or ',' at a call site). Same
        /// position/buffer contract as hover; replies {signatureHelp: {...}|null}.</summary>
        private void HandleSignatureHelp(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                Dictionary<string, object> help = null;
                try
                {
                    EnsureLspStarted();
                    if (SharedLspBridge.IsRunning)
                        help = SharedLspBridge.GetSignatureHelp(_lspFileName, LspLine0(line), Math.Max(0, column - 1), LspBuffer(buffer));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] signatureHelp: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "signatureHelp", help } });
            });
        }

        /// <summary>LSP hover request from Monaco. Syncs the current buffer (needed to resolve the symbol).</summary>
        private void HandleHover(string json)
        {
            int reqId, line, column; string buffer;
            if (!ParseRequest(json, out reqId, out line, out column, out buffer)) return;
            Task.Run(() =>
            {
                string contents = null;
                try
                {
                    EnsureLspStarted();
                    if (SharedLspBridge.IsRunning)
                    {
                        var resp = SharedLspBridge.GetHover(_lspFileName, LspLine0(line), Math.Max(0, column - 1), LspBuffer(buffer));
                        contents = ExtractHoverString(resp);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] hover: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "contents", contents } });
            });
        }

        /// <summary>
        /// Diagnostics request from Monaco (debounced after edits + once after load). Runs the hybrid
        /// ModernEmbeditorDiagnostics over the LIVE buffer + LIVE editable ranges — Monaco passes its
        /// decoration-tracked ranges because slots grow as the user types, so the load-time
        /// _editableRanges snapshot would be stale. Runs off the UI thread (the LSP sub-pass blocks),
        /// then posts back a unified marker list for setModelMarkers.
        /// </summary>
        private void HandleDiagnostics(string json)
        {
            int reqId; string buffer; List<int[]> ranges;
            if (!ParseDiagnosticsRequest(json, out reqId, out buffer, out ranges)) return;
            Task.Run(async () =>
            {
                var markers = new List<Dictionary<string, object>>();
                try
                {
                    markers = await ModernEmbeditorDiagnostics.ComputeAsync(
                        _lspFileName,
                        buffer ?? _sourceText,
                        (ranges != null && ranges.Count > 0) ? ranges : _editableRanges,
                        _procedureName,
                        embedSlotChecks: !_fileMode,    // file mode: LSP only, skip embed-slot heuristics
                        lspContext: _lspContext)        // #56: wrap the LSP pass with the MEMBER header
                        .ConfigureAwait(false);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] diagnostics: " + ex.Message); }
                PostResponse(reqId, new Dictionary<string, object> { { "markers", markers } });
            });
        }

        // Parses a diagnostics request: reqId, the live buffer text, and the live editable ranges
        // (an array of [start,end] line pairs from Monaco's tracked decorations).
        private bool ParseDiagnosticsRequest(string json, out int reqId, out string buffer, out List<int[]> ranges)
        {
            reqId = 0; buffer = null; ranges = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                if (data.ContainsKey("ranges"))
                {
                    var arr = data["ranges"] as object[];
                    if (arr != null)
                    {
                        ranges = new List<int[]>();
                        foreach (var item in arr)
                        {
                            var pair = item as object[];
                            if (pair != null && pair.Length >= 2)
                                ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) });
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Ctrl+D from Monaco — open the NATIVE structure designer for the WINDOW/REPORT at the caret
        /// (task 0a2ac0cb, literal-source mode). Validates here (structure detection + editable-slot
        /// guard + edit-vs-create mode), responds immediately so Monaco can arm its tracked splice
        /// target, then defers the designer open off this reentrant WebView2 stack (same rule as save).
        /// The designer's merges stream back as 'designerSplice' messages; tab close ends the session
        /// with 'designerClosed'.
        /// </summary>
        private void HandleOpenDesigner(string json)
        {
            int reqId, line; string buffer, templateTitle; List<int[]> ranges;
            if (!ParseDesignerRequest(json, out reqId, out line, out buffer, out ranges, out templateTitle)) return;
            if (buffer == null || ranges == null) { PostDesignerRefusal(reqId, "Designer request was malformed."); return; }

            if (StructureDesignerService.IsActive)
            {
                StructureDesignerService.ActivateCurrent(_panel);
                PostDesignerRefusal(reqId, "A structure designer is already open — close its tab first.");
                return;
            }

            var hit = ClarionAppDataReader.FindStructureAtLine(buffer, line);
            var lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            if (hit.Found)
            {
                if (!RangeEditable(ranges, hit.StartLine, hit.EndLine))
                {
                    PostDesignerRefusal(reqId, "This " + hit.Type + " is in generated code — the designer only works on editable embed code.");
                    return;
                }
                string structureText = string.Join("\n", lines.Skip(hit.StartLine - 1).Take(hit.EndLine - hit.StartLine + 1));
                string label = string.IsNullOrEmpty(hit.Name) ? "CAWindow" : hit.Name;
                bool isWindow = hit.Type == "WINDOW";
                PostResponse(reqId, new Dictionary<string, object>
                {
                    { "ok", true }, { "mode", "edit" },
                    { "startLine", hit.StartLine }, { "endLine", hit.EndLine }, { "type", hit.Type }
                });
                OpenDesignerDeferred(structureText, label, isWindow, isWindow);
                return;
            }

            // Create-new mode: a BLANK editable line becomes a fresh structure.
            string refusal = ValidateCreateLine(ranges, lines, line);
            if (refusal != null) { PostDesignerRefusal(reqId, refusal); return; }

            // Native parity (task 1f10aa51): offer the New Structure templates from DEFAULTS.CLW —
            // the same file the native Ctrl+D picker reads. Monaco shows the picker and comes back via
            // 'openDesignerCreate'. No templates (file missing) -> legacy hardcoded seed, no picker.
            var templates = DefaultStructuresReader.Load();
            if (templates.Count > 0)
            {
                var list = templates.Select(t => (object)new Dictionary<string, object> { { "title", t.Title }, { "type", t.Kind } }).ToList();
                PostResponse(reqId, new Dictionary<string, object> { { "ok", true }, { "mode", "pickTemplate" }, { "templates", list } });
                return;
            }

            PostResponse(reqId, new Dictionary<string, object>
            {
                { "ok", true }, { "mode", "insert" },
                { "startLine", line }, { "endLine", line }, { "type", "WINDOW" }
            });
            OpenDesignerDeferred(FallbackSeed, "NewWindow", true, true);
        }

        /// <summary>
        /// Second leg of create-new: Monaco's template picker chose an entry — re-validate the line
        /// (the user may have typed while the picker was up), seed from the chosen DEFAULTS.CLW block,
        /// and open with the designer flags the block's kind dictates (WINDOW / APPLICATION / REPORT).
        /// </summary>
        private void HandleOpenDesignerCreate(string json)
        {
            int reqId, line; string buffer, templateTitle; List<int[]> ranges;
            if (!ParseDesignerRequest(json, out reqId, out line, out buffer, out ranges, out templateTitle)) return;
            if (buffer == null || ranges == null || string.IsNullOrEmpty(templateTitle))
            {
                PostDesignerRefusal(reqId, "Designer request was malformed.");
                return;
            }
            if (StructureDesignerService.IsActive)
            {
                StructureDesignerService.ActivateCurrent(_panel);
                PostDesignerRefusal(reqId, "A structure designer is already open — close its tab first.");
                return;
            }

            var lines = buffer.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            string refusal = ValidateCreateLine(ranges, lines, line);
            if (refusal != null) { PostDesignerRefusal(reqId, refusal); return; }

            var template = DefaultStructuresReader.Load()
                .FirstOrDefault(t => string.Equals(t.Title, templateTitle, StringComparison.Ordinal));
            string structureText = template != null ? template.Source : FallbackSeed;
            string kind = template != null ? template.Kind : "WINDOW";

            // Scratch tab name = the template block's own label (e.g. Window / ProgressWindow / Report).
            string label = "NewStructure";
            var m = System.Text.RegularExpressions.Regex.Match(structureText, @"^\s*(\w+)");
            if (m.Success) label = m.Groups[1].Value;

            bool isWindowDesigner = kind != "REPORT";
            bool isWindowWindow = kind == "WINDOW";

            PostResponse(reqId, new Dictionary<string, object>
            {
                { "ok", true }, { "mode", "insert" },
                { "startLine", line }, { "endLine", line }, { "type", kind }
            });
            OpenDesignerDeferred(structureText, label, isWindowDesigner, isWindowWindow);
        }

        private const string FallbackSeed =
            "NewWindow WINDOW('New Window'),AT(,,200,120),GRAY,SYSTEM\n" +
            "         \n" +
            "       END";

        private static bool RangeEditable(List<int[]> ranges, int start, int end)
        {
            foreach (var r in ranges) if (start >= r[0] && end <= r[1]) return true;
            return false;
        }

        // null = OK; else the refusal message.
        private static string ValidateCreateLine(List<int[]> ranges, string[] lines, int line)
        {
            bool lineEditable = RangeEditable(ranges, line, line);
            bool lineBlank = line >= 1 && line <= lines.Length && lines[line - 1].Trim().Length == 0;
            if (lineEditable && lineBlank) return null;
            return lineEditable
                ? "Put the caret inside a WINDOW/REPORT, or on a blank line to create a new structure."
                : "The designer only works in editable embed code.";
        }

        /// <summary>Run the designer open off this reentrant WebView2 message-handler stack (save's rule).</summary>
        private void OpenDesignerDeferred(string structureText, string label, bool isWindowDesigner, bool isWindowWindow)
        {
            Action open = () =>
            {
                string err = StructureDesignerService.Open(structureText, label, isWindowDesigner, isWindowWindow, _panel,
                    onBufferChanged: text => PostDesignerMessage("designerSplice", text, null),
                    onClosed: finalText =>
                    {
                        PostDesignerMessage("designerClosed", finalText, null);
                        BringToFront();   // the scratch tab auto-closed after the merge — hand focus back here
                    });
                if (err != null) PostDesignerMessage("designerClosed", null, err);
            };
            if (_panel != null && _panel.IsHandleCreated) _panel.BeginInvoke(open);
            else open();
        }

        private bool ParseDesignerRequest(string json, out int reqId, out int line, out string buffer,
            out List<int[]> ranges, out string templateTitle)
        {
            reqId = 0; line = 0; buffer = null; ranges = null; templateTitle = null;
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return false;
                if (data.ContainsKey("reqId")) reqId = Convert.ToInt32(data["reqId"]);
                if (data.ContainsKey("line")) line = Convert.ToInt32(data["line"]);
                if (data.ContainsKey("buffer")) buffer = data["buffer"] as string;
                if (data.ContainsKey("templateTitle")) templateTitle = data["templateTitle"] as string;
                if (data.ContainsKey("ranges"))
                {
                    var arr = data["ranges"] as object[];
                    if (arr != null)
                    {
                        ranges = new List<int[]>();
                        foreach (var item in arr)
                        {
                            var pair = item as object[];
                            if (pair != null && pair.Length >= 2)
                                ranges.Add(new[] { Convert.ToInt32(pair[0]), Convert.ToInt32(pair[1]) });
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private void PostDesignerRefusal(int reqId, string message)
        {
            PostResponse(reqId, new Dictionary<string, object> { { "ok", false }, { "message", message } });
        }

        /// <summary>Push a designer-session event to Monaco (UI-thread marshalled by the control).</summary>
        private void PostDesignerMessage(string type, string text, string message)
        {
            _panel?.PostDesignerMessage(type, text, message);
        }

        /// <summary>
        /// Persist the dev's editor settings (from the gear panel) and broadcast them to every open
        /// Modern Embeditor tab so the change is consistent across tabs. Persist failures are logged but
        /// don't block the broadcast — the live editors still reflect the new options for this session.
        /// </summary>
        // Small fixed-cap parse for the tiny save* bridge payloads (cursor / bookmarks / settings). These
        // are page-supplied (untrusted) and bounded by design — refuse to materialize an oversized payload
        // BEFORE deserializing rather than trimming after. (Security gate finding.)
        private const int MaxBridgeJsonBytes = 65536;   // 64 KB — far above any legit save* message
        private static Dictionary<string, object> ParseBoundedBridgeJson(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxBridgeJsonBytes) return null;
            try { return new JavaScriptSerializer { MaxJsonLength = MaxBridgeJsonBytes }.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        private void HandleSaveSettings(string json)
        {
            try
            {
                // Persist + broadcast through the shared bus so the change reaches EVERY Monaco surface — other
                // embeditors AND source/default editors — not just ModernEmbeditorViewContent tabs. (deac3d16)
                Services.MonacoSettingsBroadcaster.SaveAndBroadcastFromBridge(json);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveSettings: " + ex.Message); }
        }

        // ApplySettings/ApplySettingsToAll were replaced by Services.MonacoSettingsBroadcaster (deac3d16): this
        // tab now receives applySettings as a registered bus sink (see ctor), so the broadcast reaches source
        // editors too — not just Modern Embeditor tabs.

        /// <summary>
        /// Broadcast the current snippet list to every open Modern Embeditor tab, so an
        /// add/edit/delete in the gear panel's Code Snippets tab is picked up live without reopening
        /// the tab (mirrors ApplySettingsToAll — called by OnSnippetCommand after each CRUD op).
        /// </summary>
        public static void ApplySnippetsToAll(List<Snippet> snippets)
        {
            string json = "{\"type\":\"applySnippets\",\"snippets\":" + SnippetStore.ToJson(snippets) + "}";
            lock (_instances) { foreach (var inst in _instances) inst._panel?.PostJson(json); }
        }

        /// <summary>
        /// Persist the Find/Replace dropdown history (sent by JS as full arrays) and broadcast the saved
        /// lists to every open tab so all tabs converge. The incoming list is authoritative, so per-entry
        /// delete and "clear history" stick. Persist failures are logged but never block the broadcast.
        /// </summary>
        private void HandleSaveHistory(string json)
        {
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null) return;
                var find = ToStringList(data, "find");
                var replace = ToStringList(data, "replace");
                var proc = ToStringList(data, "proc");
                EnsureHistoryScope();
                List<string> savedFind, savedReplace;
                ModernEmbeditorHistory.Save(_histSolutionPath, _histProcKey, find, replace, proc, out savedFind, out savedReplace);
                // Broadcast solution-wide lists only — each tab keeps its own procedure's recent terms.
                // Via the broker bus so CA Editor tabs AND the CA Find pad converge too (#66 phase 2).
                Services.CaFindBroker.BroadcastHistory(savedFind, savedReplace);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveHistory: " + ex.Message); }
        }

        /// <summary>Persist the cursor position (sent on Ctrl+S) per solution+procedure for restore-on-open.</summary>
        private void HandleSaveCursor(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                int line = data.ContainsKey("line") ? Convert.ToInt32(data["line"]) : 0;
                int column = data.ContainsKey("column") ? Convert.ToInt32(data["column"]) : 0;
                if (line < 1) return;
                EnsureHistoryScope();
                ModernEmbeditorState.SaveCursor(_histSolutionPath, _histProcKey, line, column);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveCursor: " + ex.Message); }
        }

        /// <summary>Persist the bookmark line set (sent whenever it changes) per solution+procedure.</summary>
        private void HandleSaveBookmarks(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                var lines = new List<int>();
                object o;
                if (data.TryGetValue("bookmarks", out o) && o is object[])
                {
                    // Bound ingestion: stop collecting once we have comfortably more than the persist cap
                    // (200) so a hostile/oversized array from the page can't force a huge allocation before
                    // CleanLines trims it. (Security gate finding.)
                    var arr = (object[])o;
                    for (int i = 0; i < arr.Length && lines.Count < 1000; i++)
                        if (arr[i] != null) { try { lines.Add(Convert.ToInt32(arr[i])); } catch { } }
                }
                EnsureHistoryScope();
                ModernEmbeditorState.SaveBookmarks(_histSolutionPath, _histProcKey, lines);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveBookmarks: " + ex.Message); }
        }

        /// <summary>Persist the collapsed fold set (sent whenever it changes) per solution+procedure.</summary>
        private void HandleSaveFolds(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                var folds = ReadFoldRecords(data);
                EnsureHistoryScope();
                ModernEmbeditorState.SaveFolds(_histSolutionPath, _histProcKey, folds);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] saveFolds: " + ex.Message); }
        }

        /// <summary>
        /// Pull the {line,text} fold records out of a bridge payload. Bounded ingestion for the same reason
        /// HandleSaveBookmarks bounds its array: the page is untrusted input, so stop collecting well before
        /// anything huge is allocated and let ModernEmbeditorState's CleanFolds do the final clamping.
        /// Shared by both IMonacoEditorHost implementations so the two surfaces can't drift apart.
        /// </summary>
        internal static List<Services.FoldRecord> ReadFoldRecords(IDictionary<string, object> data)
        {
            var folds = new List<Services.FoldRecord>();
            object o;
            if (data == null || !data.TryGetValue("folds", out o) || !(o is object[])) return folds;
            var arr = (object[])o;
            for (int i = 0; i < arr.Length && folds.Count < 1000; i++)
            {
                var d = arr[i] as Dictionary<string, object>;
                if (d == null) continue;
                int line;
                object lv, tv;
                if (!d.TryGetValue("line", out lv) || lv == null) continue;
                try { line = Convert.ToInt32(lv); } catch { continue; }
                if (line < 1) continue;
                string text = (d.TryGetValue("text", out tv) && tv != null) ? tv.ToString() : "";
                folds.Add(new Services.FoldRecord { Line = line, Text = text });
            }
            return folds;
        }

        /// <summary>
        /// Cache the Monaco selection pushed by the page (onDidChangeCursorSelection). Mirrors the
        /// saveCursor/saveBookmarks push model — read by the embeditor_get_selection MCP tool, no round-trip.
        /// The JS side caps the text at 10 KB chars, so even an all-escaped selection stays under
        /// MaxBridgeJsonBytes (64 KB) and the message is never dropped; `truncated` flags any clipping.
        /// </summary>
        private void HandleSelectionChanged(string json)
        {
            try
            {
                var data = ParseBoundedBridgeJson(json);
                if (data == null) return;
                string text = data.ContainsKey("text") ? (data["text"] as string ?? "") : "";
                bool truncated = data.ContainsKey("truncated") && data["truncated"] is bool && (bool)data["truncated"];
                int sl = data.ContainsKey("startLine") ? Convert.ToInt32(data["startLine"]) : 0;
                int sc = data.ContainsKey("startColumn") ? Convert.ToInt32(data["startColumn"]) : 0;
                int el = data.ContainsKey("endLine") ? Convert.ToInt32(data["endLine"]) : 0;
                int ec = data.ContainsKey("endColumn") ? Convert.ToInt32(data["endColumn"]) : 0;
                // A real selection has a non-empty range. An empty range (click/caret move) reports
                // hasSelection=false so the tool can say "nothing highlighted" on click-away.
                bool has = sl > 0 && (sl != el || sc != ec);

                _selText = has ? text : "";
                _selStartLine = sl; _selStartCol = sc; _selEndLine = el; _selEndCol = ec;
                _selHasSelection = has;
                _selTruncated = has && truncated;   // no selection ⇒ nothing to truncate

                var snap = BuildSelectionDict();
                lock (_selSnapLock) { _lastFocusedSelection = snap; }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ModernEmbeditor] selectionChanged: " + ex.Message); }
        }

        private Dictionary<string, object> BuildSelectionDict()
        {
            return new Dictionary<string, object>
            {
                { "procedure", _procedureName ?? "" },
                { "hasSelection", _selHasSelection },
                { "truncated", _selTruncated },
                { "text", _selText ?? "" },
                { "startLine", _selStartLine },
                { "startColumn", _selStartCol },
                { "endLine", _selEndLine },
                { "endColumn", _selEndCol }
            };
        }

        /// <summary>This tab's current Monaco selection snapshot (procedure, text, range, hasSelection).</summary>
        public Dictionary<string, object> GetSelectionSnapshot()
        {
            return BuildSelectionDict();
        }

        /// <summary>
        /// The selection from the FOCUSED Modern Embeditor; if focus is momentarily ambiguous, the last
        /// snapshot any tab reported; null only when no Modern Embeditor has ever reported one. Read by the
        /// embeditor_get_selection MCP tool on the UI thread.
        /// </summary>
        public static Dictionary<string, object> GetFocusedSelection()
        {
            var view = FocusedModernView();
            if (view != null) return view.GetSelectionSnapshot();
            // Fall back to the last snapshot ONLY while a Monaco CA Embeditor is genuinely still open but
            // unfocused (ambiguous-focus case). If no Modern tab is open, there is no CA Embeditor — return
            // null so the tool says so, instead of serving a stale snapshot left behind by a closed tab.
            lock (_instances) { if (_instances.Count == 0) return null; }
            lock (_selSnapLock) { return _lastFocusedSelection; }
        }

        /// <summary>
        /// Resolve (once) the history scope from the IDE: the open solution (folder) and an app::procedure
        /// key (the "This procedure" group). Cached for this tab's lifetime.
        /// </summary>
        private void EnsureHistoryScope()
        {
            if (_histScopeResolved) return;
            _histScopeResolved = true;
            try { _histSolutionPath = EditorService.GetOpenSolutionPath(); } catch { _histSolutionPath = null; }
            if (_fileMode)
            {
                // File tabs scope history/cursor/bookmarks by path — no app::procedure identity exists.
                // State (cursor/bookmarks/history) stays keyed on the PATH, not the file-ID dedup identity: a path
                // key is stable across external delete/recreate (a file-ID changes then, orphaning state) AND it
                // matches the key used before item 3, so existing users' saved state is NOT stranded on upgrade.
                _histProcKey = "file::" + _filePath.ToLowerInvariant();
                return;
            }
            string appName = null;
            try
            {
                var info = new AppTreeService().GetAppInfo();
                if (info != null && info.ContainsKey("name") && info["name"] != null) appName = info["name"].ToString();
            }
            catch { }
            string key = ((appName ?? "") + "::" + (_procedureName ?? "")).Trim(':');
            _histProcKey = string.IsNullOrEmpty(key) ? "" : key;
        }

        /// <summary>Coerce a JSON array field (object[] from DeserializeObject) into a string list.</summary>
        private static List<string> ToStringList(Dictionary<string, object> d, string key)
        {
            var res = new List<string>();
            object o;
            if (d != null && d.TryGetValue(key, out o) && o is object[])
            {
                foreach (var item in (object[])o)
                    if (item != null) res.Add(item.ToString());
            }
            return res;
        }

        private bool ParseRequest(string json, out int reqId, out int line, out int column, out string buffer)
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

        /// <summary>Posts a {type:"response", reqId, data} message back to Monaco (marshaled by the control).</summary>
        private void PostResponse(int reqId, Dictionary<string, object> data)
        {
            _panel?.PostResponse(reqId, data);
        }

        /// <summary>Pulls a plain string out of an LSP textDocument/hover response (MarkupContent/string/array).</summary>
        private static string ExtractHoverString(Dictionary<string, object> resp)
        {
            if (resp == null) return null;
            object result = resp.ContainsKey("result") ? resp["result"] : null;
            var rd = result as Dictionary<string, object>;
            object contents = rd != null && rd.ContainsKey("contents") ? rd["contents"] : result;
            return HoverPartToString(contents);
        }

        private static string HoverPartToString(object contents)
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
                    string p = HoverPartToString(part);
                    if (!string.IsNullOrEmpty(p)) { if (sb.Length > 0) sb.Append("\n\n"); sb.Append(p); }
                }
                return sb.Length > 0 ? sb.ToString() : null;
            }
            return null;
        }

        private static string MakeLspFileName(string procName)
        {
            string baseName = string.IsNullOrWhiteSpace(procName) ? "modern_embeditor" : procName;
            var sb = new StringBuilder();
            foreach (char c in baseName) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString() + ".clw";
        }

        /// <summary>Put text on the Windows clipboard (Clarion-style Ctrl+X cut from the editor).</summary>
        private void HandleClipboard(string json)
        {
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var data = ser.DeserializeObject(json) as Dictionary<string, object>;
                string text = (data != null && data.ContainsKey("text")) ? (data["text"]?.ToString() ?? "") : null;
                if (text != null) Clipboard.SetText(text.Length == 0 ? " " : text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] Clipboard error: " + ex.Message);
            }
        }

        private void PostSaveResult(bool ok, string message) { PostSaveResult(ok, message, 0); }

        private void PostSaveResult(bool ok, string message, long savedSeq)
        {
            PostSaveResultOnce(ok, message, savedSeq);
            // Backup re-post ONLY in embed mode — the embeditor open/close churn during a slot save can drop the
            // first post. A file save has no such churn, so the double-post is suppressed here: it widened the
            // dirty-clear race (a clean saveResult could land after an edit and wrongly clear the ●). (pipeline item 6)
            if (!_fileMode)
            {
                try { _panel?.BeginInvoke((Action)(() => PostSaveResultOnce(ok, message, savedSeq))); }
                catch { }
            }
        }

        private void PostSaveResultOnce(bool ok, string message, long savedSeq)
        {
            _panel?.PostSaveResult(ok, message, savedSeq);
        }

        /// <summary>Update the displayed source. Sends immediately if ready, else waits for the JS "ready".</summary>
        public void SetSource(string title, string sourceText, string language = null)
        {
            _title = title ?? _title;
            _sourceText = sourceText ?? "";
            if (language != null) _language = language;
            TitleName = "CA: " + _title;

            if (_isInitialized)
                SendSource();
        }

        private void SendSource()
        {
            if (_panel == null || _panel.TempDir == null) return;

            // Warm the language server as soon as the editor opens, so completion/hover/LSP-diagnostics
            // are ready by the time the dev uses them (self-heal if eager-start never fired).
            EnsureLspStarted();

            try
            {
                // Transfer source via the virtual host (temp file) to avoid huge postMessage payloads.
                string sourceFile = Path.Combine(_panel.TempDir, "source.txt");
                File.WriteAllText(sourceFile, _sourceText ?? "", Encoding.UTF8);

                string settingsJson;
                try { settingsJson = new JavaScriptSerializer().Serialize(ModernEmbeditorSettings.Load().ToDict()); }
                catch { settingsJson = "null"; }

                string findHistJson = "[]", replHistJson = "[]", procHistJson = "[]";
                int cursorLine = 0, cursorColumn = 0;
                string bookmarksJson = "[]";
                string foldsJson = "[]";
                try
                {
                    EnsureHistoryScope();
                    List<string> hf, hr, hp;
                    ModernEmbeditorHistory.Load(_histSolutionPath, _histProcKey, out hf, out hr, out hp);
                    findHistJson = ModernEmbeditorHistory.ToJson(hf);
                    replHistJson = ModernEmbeditorHistory.ToJson(hr);
                    procHistJson = ModernEmbeditorHistory.ToJson(hp);
                    List<int> bms;
                    ModernEmbeditorState.Load(_histSolutionPath, _histProcKey, out cursorLine, out cursorColumn, out bms);
                    bookmarksJson = ModernEmbeditorState.BookmarksJson(bms);
                    foldsJson = ModernEmbeditorState.FoldsJson(
                        ModernEmbeditorState.LoadFolds(_histSolutionPath, _histProcKey));
                }
                catch { }

                // Native-caret override: when the overlay attached to an already-open native embed, land Monaco
                // at the embed point the developer was on — not the last-saved cursor for this procedure.
                //
                // The test is > 1, NOT > 0 (ticket 7d807826). GetNativeCaretLine() returns
                // Caret.Line + 1 to convert ICSharpCode's 0-based caret, so a freshly-opened native embed
                // sitting at its default position reports 1 — indistinguishable from a developer who
                // deliberately parked on line 1. Treating that as a real position meant the override fired
                // on EVERY overlay open and silently replaced the saved cursor with 1:1, which is exactly
                // why the column was always 1 while the stored value on disk was always correct.
                //
                // Line 1 is therefore read as "no meaningful native position, use the saved cursor". The
                // cost is that genuinely parking the native caret on line 1 now restores the saved cursor
                // instead — far rarer, and far less annoying, than losing the position on every open.
                if (_initialLine > 1) { cursorLine = _initialLine; cursorColumn = 1; }
                _initialLine = 0;   // one-shot regardless, so a later reload can't re-apply a stale native line

                string snippetsJson;
                try { snippetsJson = SnippetStore.ToJson(SnippetStore.Load()); }
                catch { snippetsJson = "[]"; }

                string json = "{\"type\":\"setSource\"," +
                    "\"title\":" + JsonString(_title) + "," +
                    "\"language\":" + JsonString(_language) + "," +
                    "\"isDark\":" + (_isDark ? "true" : "false") + "," +
                    "\"fileMode\":" + (_fileMode ? "true" : "false") + "," +
                    "\"filePath\":" + JsonString(_filePath ?? "") + "," +
                    "\"saveEnabled\":" + (_saveEnabled ? "true" : "false") + "," +
                    "\"findUiMode\":\"" + Services.CaFindSettings.FindUiModeForPage + "\"," +   // Pad vs in-editor Overlay (#66 phase 2)
                    "\"liveLinked\":" + (_liveLinked ? "true" : "false") + "," +   // live mode: relabel Save → "Save and Exit" (a5bbf005)
                    "\"embedOverlay\":" + (_embedOverlay ? "true" : "false") + "," +   // overlay: Clarion-faithful toolbar + clickable header (b1e05287)
                    "\"headerText\":" + JsonString(_nativeHeaderText ?? "") + "," +    // native "Proc - Embeditor - (clw)" → our clickable header
                    "\"chromeBg1\":" + JsonString(_chromeBg1 ?? "") + "," +            // native theme colors → our overlay chrome (b1e05287)
                    "\"chromeBg2\":" + JsonString(_chromeBg2 ?? "") + "," +
                    "\"chromeFg\":" + JsonString(_chromeFg ?? "") + "," +
                    "\"nativeIcons\":" + NativeIconsJson() + "," +          // real Clarion icons → pixel-perfect toolbar (b1e05287)
                    "\"editableRanges\":" + RangesJson() + "," +
                    "\"settings\":" + settingsJson + "," +
                    "\"findHistory\":" + findHistJson + "," +
                    "\"replaceHistory\":" + replHistJson + "," +
                    "\"procHistory\":" + procHistJson + "," +
                    "\"cursorLine\":" + cursorLine + "," +
                    "\"cursorColumn\":" + cursorColumn + "," +
                    "\"bookmarks\":" + bookmarksJson + "," +
                    "\"folds\":" + foldsJson + "," +
                    "\"snippets\":" + snippetsJson + "," +
                    "\"sourceUrl\":\"https://" + VIRTUAL_HOST + "/source.txt\"}";
                _panel.PostJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ModernEmbeditorViewContent] SendSource error: " + ex.Message);
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDark = isDark;
            // The control recolors its own backdrop and posts {applyTheme} once it's live.
            _panel?.ApplyTheme(isDark);
        }

        public static void ApplyThemeToAll(bool isDark)
        {
            lock (_instances)
            {
                foreach (var inst in _instances)
                    inst.ApplyTheme(isDark);
            }
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
            // CRITICAL (pipeline): file mode edits a REAL file on disk. If the tab is closing with unsaved edits,
            // offer to save before teardown — otherwise the buffer is silently lost (the IDE tab close does not
            // prompt for our WebView2-hosted view). We hold the live buffer (_fileLiveText, mirrored from the page),
            // so the save is a synchronous file write — no async round-trip. Dispose the WebView2 FIRST so the
            // confirm MessageBox can't get stuck behind the live WebView2 (the documented native<->WebView2 deadlock).
            bool promptSave = _fileMode && _fileDirty && _fileLiveText != null && !_disposed;
            _disposed = true;

            // Shared session teardown (broker unregister, LSP shadow revert, instance list) — one path
            // with DetachOverlay so the two lists can't drift again. (#119)
            TeardownSession();
            UnwireNativeEmbedCaretMirror();   // a disposed tab's delegate must not keep firing off the native caret

            // Dispose the editor control (its WebView2 + temp dir) FIRST so the confirm MessageBox below
            // can't get stuck behind a live WebView2 (the documented native<->WebView2 focus deadlock).
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }

            // Live-linked cancel-close (ticket a5bbf005): the tab is closing while STILL holding the native embed
            // open (i.e. NOT via save-and-exit, which clears _liveLinked first) — so this is a discard/cancel.
            // Release the single-embeditor lock or the next open fails. Flow A leaves the native buffer untouched
            // until save, so we lose only the unsaved Monaco edits (which closing discards by intent). Safe to pump
            // here — Dispose is on the UI thread and NOT an active-view-changed event stack, and _panel is already
            // gone so there's no WebView2 reentrancy. Skipped during IDE shutdown (the lock is moot then).
            if (_liveLinked && ReferenceEquals(_liveInstance, this) && !_shuttingDown)
            {
                _liveInstance = null;
                _liveLinked = false;
                try
                {
                    var appTree = new AppTreeService();
                    if (appTree.GetEmbedInfo() != null)
                    {
                        appTree.CancelEmbeditor();
                        ModernEmbeditorLauncher.WaitForEmbedClosed(appTree, 3000);
                    }
                }
                catch { }
            }

            if (promptSave)
            {
                try
                {
                    if (_shuttingDown)
                    {
                        // IDE shutdown: NO modal prompt (a Yes/No per dirty tab = modal storm). Preserve the edits to
                        // a unique recovery file without overwriting the real file unprompted; the user recovers on
                        // next open. This also backstops the residual close-race on the shutdown path. (Run-3 adversary)
                        WriteRecoveryBackup(NormalizeEol(_fileLiveText, TargetEol));
                    }
                    else
                    {
                        var r = MessageBox.Show("Save changes to " + _title + " before closing?",
                            "CA Editor — unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (r == DialogResult.Yes)
                            SaveOnClose();   // guarded by _fileDiskSig; recovery-preserves on conflict (never silent-clobbers)
                    }
                }
                catch { /* best-effort save-on-close; the tab is closing regardless */ }
            }

            base.Dispose();
        }

        /// <summary>Shutdown hook: dispose every open Modern Embeditor's WebView2 on the UI thread, before
        /// native IDE teardown, to avoid the WebView2 &lt;-&gt; native focus deadlock. Idempotent + best-effort.</summary>
        public static void DisposeAllForShutdown()
        {
            _shuttingDown = true;   // per-tab Dispose takes the noninteractive recovery path (no modal storm)
            List<ModernEmbeditorViewContent> snapshot;
            lock (_instances) { snapshot = new List<ModernEmbeditorViewContent>(_instances); }
            foreach (var inst in snapshot)
            {
                try { inst.Dispose(); } catch { }
            }
        }

    }
}
