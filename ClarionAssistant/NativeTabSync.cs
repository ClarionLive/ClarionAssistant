using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using ICSharpCode.SharpDevelop.DefaultEditor.Gui.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Services;
using ClarionAssistant.Terminal;

namespace ClarionAssistant
{
    /// <summary>
    /// GH #205: the IDE half of <see cref="OpenTabGuard"/>. Answers "is this file open in a NATIVE Clarion editor tab, and
    /// does that tab have unsaved edits?" and reloads a clean native tab from disk after write_file / append_to_file
    /// changed the file. Behind the IdeOnly tool editor_tab_sync (which the standalone clarion-mcp-server calls over the
    /// pane's endpoint), and wired straight into OpenTabGuard.Probe/Reload for an addin that serves write_file itself.
    /// Registered by LspAutostartCommand at addin start, so it works with no chat tab open.
    ///
    /// WHICH TAB IS NATIVE. A source tab is a TextEditorDisplayBindingWrapper (ClarionEditor : CommonClarionEditor :
    /// TextEditorDisplayBindingWrapper, reflected on C10 and C12). When it is a MonacoClarionEditor whose CA Editor is UP,
    /// the visible editor is the CA Editor, whose own disk watcher offers a reload: reported as ca-editor, never
    /// reloaded here. Every other wrapper on the path is native. Views that are not text editors (the CA Embeditor's
    /// file tab, a diff) are not this tool's business and are not reported.
    ///
    /// THE RELOAD is SharpDevelop's own: AbstractViewContent.Load(fileName), the call its FileChangeWatcher makes when
    /// the user accepts "file changed outside, reload?". It re-reads the file (encoding detected as at open), sets
    /// IsDirty false, re-arms the watcher and re-adds the bookmarks. The caret line and column are put back (clamped).
    /// It is REFUSED when the tab is dirty at reload time (it may have been typed into since the check), so a reload
    /// can never drop edits. The tab's undo history starts afresh, as with any reload.
    ///
    /// THREADING. Called on an MCP worker thread. Both operations need the IDE's object model, so they are POSTED to
    /// the UI thread and waited for at most a bound, never Invoke'd (the sync-over-async stall). A timed-out check is
    /// an unreachable answer (the write tool then says the tabs could not be checked); a timed-out reload is reported
    /// as not reloaded. A late-running posted call finishes and is discarded; the reload still re-checks IsDirty.
    /// </summary>
    internal static class NativeTabSync
    {
        internal const int CheckWaitMs = 2000;
        internal const int ReloadWaitMs = 8000;
        private static SynchronizationContext _ui;

        /// <summary>Call on the UI thread (addin start).</summary>
        internal static void Register()
        {
            _ui = SynchronizationContext.Current;
            OpenTabGuard.LocalCheck = Check;
            OpenTabGuard.LocalReload = Reload;
            // The addin serving write_file itself (no standalone exe): ask this IDE directly.
            OpenTabGuard.Probe = path => new List<TabProbe> { Check(path) };
            OpenTabGuard.Reload = (path, tab) => Reload(path);
        }

        internal static TabProbe Check(string path)
        {
            TabProbe answer = null;
            string failure = OnUi(() => { answer = CheckOnUi(path); }, CheckWaitMs);
            if (failure != null) answer = new TabProbe { Reachable = false, Reason = failure };
            answer.Ide = "IDE " + System.Diagnostics.Process.GetCurrentProcess().Id;
            MonacoSpikeLog.Write("[tab-sync] check " + path + " -> " + (answer.Reachable
                ? answer.Kind + (answer.Dirty ? " dirty" : " clean") : "unreachable: " + answer.Reason));
            return answer;
        }

        internal static TabReload Reload(string path)
        {
            TabReload answer = null;
            string failure = OnUi(() => { answer = ReloadOnUi(path); }, ReloadWaitMs);
            if (failure != null) answer = new TabReload { Reloaded = false, Reason = failure };
            MonacoSpikeLog.Write("[tab-sync] reload " + path + " -> " + (answer.Reloaded
                ? "reloaded, caret line " + answer.Line : "not reloaded: " + answer.Reason));
            return answer;
        }

        // Runs work on the UI thread with a bounded wait; null on success, else why it did not run in time.
        private static string OnUi(Action work, int waitMs)
        {
            var ui = _ui;
            if (ui == null) return "the IDE's editor check is not registered";
            Exception error = null;
            var done = new ManualResetEventSlim(false);
            SendOrPostCallback cb = _ =>
            {
                try { work(); }
                catch (Exception ex) { error = ex; }
                finally { try { done.Set(); } catch (ObjectDisposedException) { } }
            };
            if (SynchronizationContext.Current == ui) cb(null);
            else
            {
                ui.Post(cb, null);
                if (!done.Wait(waitMs))
                    return "the IDE's UI thread did not answer within " + (waitMs / 1000) + " s";
            }
            done.Dispose();
            return error != null ? "the editor lookup failed: " + error.Message : null;
        }

        private static TabProbe CheckOnUi(string path)
        {
            // The CA Editor first: its native shell stays clean by design, so IsDirty would lie about it.
            if (MonacoClarionEditor.IsOpenInOverlay(path))
            {
                bool caDirty;
                MonacoClarionEditor.TryGetLiveTabState(path, out caDirty);
                return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindCaEditor, Dirty = caDirty };
            }
            bool open = false, dirty = false;
            foreach (var w in NativeViewsOf(path))
            {
                open = true;
                if (w.IsDirty) dirty = true;
            }
            return new TabProbe { Reachable = true, Kind = open ? OpenTabGuard.KindNative : OpenTabGuard.KindNone, Dirty = dirty };
        }

        private static TabReload ReloadOnUi(string path)
        {
            if (MonacoClarionEditor.IsOpenInOverlay(path))
                return new TabReload { Reason = "it is now in the CA Editor, which offers its own reload" };
            var views = NativeViewsOf(path);
            if (views.Count == 0) return new TabReload { Reason = "the tab is no longer open" };
            foreach (var w in views)
                if (w.IsDirty) return new TabReload { Reason = "the tab has unsaved changes now, so reloading would lose them" };
            if (!File.Exists(path)) return new TabReload { Reason = "the file is not on disk" };

            int caretLine = 0;
            foreach (var w in views) caretLine = ReloadKeepingCaret(w, path);
            return new TabReload { Reloaded = true, Line = caretLine };
        }

        // SharpDevelop's own reload, with the caret put back. Returns the 1-based caret line it left (0 if unknown).
        private static int ReloadKeepingCaret(TextEditorDisplayBindingWrapper w, string path)
        {
            int line = -1, col = 0;
            ICSharpCode.TextEditor.TextEditorControl tec = null;
            try
            {
                tec = w.TextEditorControl;
                var caret = tec.ActiveTextAreaControl.Caret;
                line = caret.Line;
                col = caret.Column;
            }
            catch { }

            w.Load(path);

            if (tec == null || line < 0) return 0;
            try
            {
                var doc = tec.Document;
                int last = Math.Max(0, doc.TotalNumberOfLines - 1);
                int l = Math.Min(line, last);
                int len = doc.GetLineSegment(l).Length;
                var area = tec.ActiveTextAreaControl;
                area.Caret.Line = l;
                area.Caret.Column = Math.Min(col, len);
                area.ScrollToCaret();
                return l + 1;
            }
            catch { return 0; }
        }

        // The open text-editor views of path whose visible editor is the native one. UI thread.
        private static List<TextEditorDisplayBindingWrapper> NativeViewsOf(string path)
        {
            var found = new List<TextEditorDisplayBindingWrapper>();
            var seen = new HashSet<object>();
            Action<object> consider = vc =>
            {
                var w = vc as TextEditorDisplayBindingWrapper;
                if (w == null || seen.Contains(w)) return;
                seen.Add(w);
                if (!SamePath(FileNameOf(w), path)) return;
                var me = w as MonacoClarionEditor;
                if (me != null && MonacoClarionEditor.IsOpenInOverlay(path)) return;   // the CA Editor covers it
                found.Add(w);
            };

            try
            {
                if (ICSharpCode.SharpDevelop.FileService.IsOpen(path))
                {
                    var window = ICSharpCode.SharpDevelop.FileService.GetOpenFile(path);
                    if (window != null) { consider(window.ViewContent); consider(window.ActiveViewContent); }
                }
            }
            catch { }

            // And every view, in case FileService's own path match missed it (different spelling of the same file).
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                var all = wb == null ? null : wb.GetType().GetProperty("ViewContentCollection",
                    BindingFlags.Public | BindingFlags.Instance).GetValue(wb, null) as System.Collections.IEnumerable;
                if (all != null) foreach (var vc in all) consider(vc);
            }
            catch { }
            return found;
        }

        private static string FileNameOf(TextEditorDisplayBindingWrapper w)
        {
            try { var f = w.FileName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return w.TextEditorControl.FileName; } catch { return null; }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { a = Path.GetFullPath(a); b = Path.GetFullPath(b); } catch { }
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
