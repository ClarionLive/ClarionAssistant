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
    /// IDE's endpoint, IdeCallbackHost), and wired straight into OpenTabGuard.Probe/Reload for an addin that serves
    /// write_file itself. Registered by LspAutostartCommand at addin start, so it works with no chat tab open.
    ///
    /// WHICH TAB. Every open text-editor view (TextEditorDisplayBindingWrapper: ClarionEditor : CommonClarionEditor :
    /// TextEditorDisplayBindingWrapper, reflected on C10 and C12) whose file is THE SAME FILE as any spelling asked about,
    /// by FILE IDENTITY (FileIdentity: volume serial + file index, then the resolved final path), so a tab opened through
    /// a junction, a subst drive, a symlink, an 8.3 name or a hard link is found (pipeline run 1). Of those, a
    /// MonacoClarionEditor whose CA Editor is UP is the CA Editor (its own disk watcher offers a reload; its native shell
    /// is always clean, so IsDirty would lie): reported as ca-editor, never reloaded here. The rest are native. Views
    /// that are not text editors (the CA Embeditor's file tab, a diff) are not this tool's business.
    ///
    /// THE RELOAD is SharpDevelop's own: AbstractViewContent.Load(fileName), the call its FileChangeWatcher makes when
    /// the user accepts "file changed outside, reload?", with the TAB'S OWN file name (so the tab keeps its title). It
    /// re-reads the file (encoding detected as at open), sets IsDirty false, re-arms the watcher and re-adds bookmarks.
    /// The caret line and column are put back (clamped). It is REFUSED when the tab is dirty at reload time (it may have
    /// been typed into since the check), so a reload can never drop edits. The tab's undo history starts afresh.
    ///
    /// THREADING. Called on an MCP worker thread; the IDE's object model is touched only on the UI thread through
    /// UiThreadCall (posted, bounded wait, never Invoke). A timed-out check is unreachable (the write tool then says the
    /// tabs could not be checked); a timed-out reload is reported as not reloaded.
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
            OpenTabGuard.Probe = paths => new List<TabProbe> { Check(paths) };
            OpenTabGuard.Reload = (paths, tab) => Reload(paths);
        }

        internal static TabProbe Check(IList<string> paths)
        {
            TabProbe answer = null;
            var probes = Probes(paths);   // file identities: off the UI thread
            string failure = Why(UiThreadCall.Run(_ui, () => { answer = CheckOnUi(probes); }, CheckWaitMs, out Exception error), error, CheckWaitMs);
            if (failure != null) answer = new TabProbe { Reachable = false, Reason = failure, TimedOut = failure.Contains("did not answer") };
            answer.Ide = "IDE " + System.Diagnostics.Process.GetCurrentProcess().Id;
            MonacoSpikeLog.Write("[tab-sync] check " + Join(paths) + " -> " + (answer.Reachable
                ? answer.Kind + (answer.Dirty ? " dirty" : " clean") : "unreachable: " + answer.Reason));
            return answer;
        }

        internal static TabReload Reload(IList<string> paths)
        {
            TabReload answer = null;
            var probes = Probes(paths);
            string failure = Why(UiThreadCall.Run(_ui, () => { answer = ReloadOnUi(probes); }, ReloadWaitMs, out Exception error), error, ReloadWaitMs);
            if (failure != null) answer = new TabReload { Reloaded = false, Reason = failure };
            MonacoSpikeLog.Write("[tab-sync] reload " + Join(paths) + " -> " + (answer.Reloaded
                ? "reloaded, caret line " + answer.Line : "not reloaded: " + answer.Reason));
            return answer;
        }

        private static string Why(UiThreadCall.Outcome o, Exception error, int waitMs)
        {
            switch (o)
            {
                case UiThreadCall.Outcome.Ran: return null;
                case UiThreadCall.Outcome.TimedOut: return "the IDE's UI thread did not answer within " + (waitMs / 1000) + " s";
                case UiThreadCall.Outcome.NoUiThread: return "the IDE's editor check is not registered";
                default: return "the editor lookup failed: " + (error != null ? error.Message : "unknown error");
            }
        }

        private static List<FileIdentity.Probe> Probes(IList<string> paths)
        {
            var list = new List<FileIdentity.Probe>();
            if (paths != null) foreach (var p in paths) if (!string.IsNullOrEmpty(p)) list.Add(FileIdentity.Of(p));
            return list;
        }

        private static string Join(IList<string> paths) { return paths == null ? "" : string.Join(" | ", new List<string>(paths).ToArray()); }

        private static TabProbe CheckOnUi(List<FileIdentity.Probe> probes)
        {
            bool caOpen = false, caDirty = false, nativeOpen = false, nativeDirty = false;
            foreach (var w in ViewsOf(probes))
            {
                var me = w as MonacoClarionEditor;
                if (me != null && me.OverlayUp) { caOpen = true; if (me.OverlayDirty) caDirty = true; }
                else { nativeOpen = true; if (w.IsDirty) nativeDirty = true; }
            }
            // A native tab is the one that loses data, so it decides when both kinds hold the file.
            if (nativeOpen) return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNative, Dirty = nativeDirty };
            if (caOpen) return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindCaEditor, Dirty = caDirty };
            return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNone };
        }

        private static TabReload ReloadOnUi(List<FileIdentity.Probe> probes)
        {
            var natives = new List<TextEditorDisplayBindingWrapper>();
            foreach (var w in ViewsOf(probes))
            {
                var me = w as MonacoClarionEditor;
                if (me != null && me.OverlayUp) continue;   // the CA Editor offers its own reload
                natives.Add(w);
            }
            if (natives.Count == 0) return new TabReload { Reason = "the tab is no longer open in the native editor" };
            foreach (var w in natives)
                if (w.IsDirty) return new TabReload { Reason = "the tab has unsaved changes now, so reloading would lose them" };

            int caretLine = 0;
            foreach (var w in natives)
            {
                string own = FileNameOf(w);
                if (string.IsNullOrEmpty(own) || !File.Exists(own)) return new TabReload { Reason = "the tab's file is not on disk" };
                caretLine = ReloadKeepingCaret(w, own);
            }
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

        // Every open text-editor view of the file (any spelling, by identity). UI thread.
        private static List<TextEditorDisplayBindingWrapper> ViewsOf(List<FileIdentity.Probe> probes)
        {
            var found = new List<TextEditorDisplayBindingWrapper>();
            var seen = new HashSet<object>();
            Action<object> consider = vc =>
            {
                var w = vc as TextEditorDisplayBindingWrapper;
                if (w == null || seen.Contains(w)) return;
                seen.Add(w);
                if (FileIdentity.MatchesAny(FileNameOf(w), probes)) found.Add(w);
            };

            foreach (var p in probes)
            {
                try
                {
                    if (!ICSharpCode.SharpDevelop.FileService.IsOpen(p.Full)) continue;
                    var window = ICSharpCode.SharpDevelop.FileService.GetOpenFile(p.Full);
                    if (window != null) { consider(window.ViewContent); consider(window.ActiveViewContent); }
                }
                catch { }
            }

            // And every view: FileService matches by path text, so an alias is only found here.
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
    }
}
