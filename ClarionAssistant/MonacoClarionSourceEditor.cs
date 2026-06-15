using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ICSharpCode.Core;
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

                if (MonacoSourceOverlay.Enabled)
                {
                    AttachCover(host);     // hide the native editor FIRST (instant, opaque)
                    _hostEditor = tec;
                    AttachOverlay(host);   // then the WebView2/Monaco surface on top of the cover
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
                MonacoSpikeLog.Write("overlay MonacoEditorControl attached over host; awaiting ready");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("AttachOverlay error: " + ex.Message); }
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
                    + "\"readOnly\":true,"
                    + "\"filePath\":\"\","
                    + "\"saveEnabled\":false,"
                    + "\"editableRanges\":[],"
                    + "\"settings\":" + settingsJson + ","
                    + "\"findHistory\":[],\"replaceHistory\":[],\"procHistory\":[],"
                    + "\"cursorLine\":0,\"cursorColumn\":0,"
                    + "\"bookmarks\":[],"
                    + "\"sourceUrl\":\"https://clarion-embeditor-data/source.txt\"}";
                _editor.PostJson(json);
                MonacoSpikeLog.Write("overlay setSource sent (fileMode read-only, " + text.Length + " chars)");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("overlay OnReady error: " + ex.Message); }
        }

        // Read-only mirror: everything else is inert for this milestone.
        void IMonacoEditorHost.OnSave(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnClipboard(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnCompletion(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnHover(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnDiagnostics(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveSettings(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveHistory(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveCursor(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSaveBookmarks(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnSelectionChanged(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnFocusEditor(MonacoEditorControl editor) { }
        void IMonacoEditorHost.OnReload(MonacoEditorControl editor) { }
        void IMonacoEditorHost.OnFileState(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnOpenDesigner(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnOpenDesignerCreate(MonacoEditorControl editor, string rawJson) { }
        void IMonacoEditorHost.OnActivateDesigner(MonacoEditorControl editor) { }
        void IMonacoEditorHost.OnEditorNavigationCompleted(MonacoEditorControl editor, bool success) { MonacoSpikeLog.Write("overlay nav completed success=" + success); }
        void IMonacoEditorHost.OnUnknownAction(MonacoEditorControl editor, string action, string rawJson) { }

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

        public override void Dispose()
        {
            StopCaptureTimer();
            DisposeOverlay();
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
