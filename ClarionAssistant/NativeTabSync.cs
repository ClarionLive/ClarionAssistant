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
    /// by FILE IDENTITY (FileIdentity), so a tab opened through a junction, a subst drive, a symlink, an 8.3 name or a
    /// hard link is found (pipeline run 1). Of those, a MonacoClarionEditor whose CA Editor is UP is the CA Editor (its
    /// own disk watcher offers a reload; its native shell is always clean, so IsDirty would lie): reported as ca-editor,
    /// never reloaded here. The rest are native.
    ///
    /// NO FILE I/O ON THE UI THREAD (pipeline run 2). Identity lookups open file handles, and a tab on a slow or
    /// disconnected share would freeze the IDE for the SMB timeout. So:
    ///   1. UI thread: SNAPSHOT every text-editor view (the view, its file name, IsDirty, CA Editor state). Property
    ///      reads only.
    ///   2. Worker thread: match the snapshot's file names against the target by identity (a network tab is not even
    ///      opened unless the target is on the network too, FileIdentity.MatchesAny).
    ///   3. Reload only: a second, minimal UI call reloads the matched views, each re-checked there first (still open,
    ///      same file name, CA Editor still down, still clean).
    ///
    /// THE RELOAD is SharpDevelop's own: AbstractViewContent.Load(fileName), the call its FileChangeWatcher makes when
    /// the user accepts "file changed outside, reload?", with the TAB'S OWN file name (so the tab keeps its title). It
    /// re-reads the file (encoding detected as at open), sets IsDirty false, re-arms the watcher and re-adds bookmarks.
    /// The caret line and column are put back (clamped). It is REFUSED when the tab is dirty at reload time, so a reload
    /// can never drop edits. The tab's undo history starts afresh.
    ///
    /// THREADING. UI work goes through UiThreadCall (posted, bounded wait, never Invoke). A timed-out check is reported
    /// as timed out (the write tool notes the tabs could not be checked); any other failure is a FAILED check, which
    /// refuses the write (OpenTabGuard, pipeline run 2).
    /// </summary>
    internal static class NativeTabSync
    {
        internal const int CheckWaitMs = 2000;
        // Pipeline run 3: a reload's two UI calls (snapshot, then reload) total 1.5 + 7.5 = 9 s, inside the standalone's
        // 10 s ReloadBudgetMs (IdeTabClient) with 1 s left for the HTTP round trip, so a slow reload is reported by the
        // IDE ("did not answer within 7 s") instead of being cut off by the client as a bare timeout.
        internal const int ReloadSnapshotWaitMs = 1500;
        internal const int ReloadWaitMs = 7500;
        private static SynchronizationContext _ui;

        /// <summary>One open text-editor view, as read on the UI thread.</summary>
        private sealed class TabSnap
        {
            public TextEditorDisplayBindingWrapper View;
            public string File;
            public bool Dirty;
            public bool OverlayUp;
            public bool OverlayDirty;
        }

        /// <summary>Call on the UI thread (addin start).</summary>
        internal static void Register()
        {
            _ui = SynchronizationContext.Current;
            OpenTabGuard.LocalCheck = Check;
            OpenTabGuard.LocalCheckLexical = CheckLexical;
            OpenTabGuard.LocalReload = Reload;
            // The addin serving write_file itself (no standalone exe): ask this IDE directly.
            OpenTabGuard.Probe = paths => new List<TabProbe> { Check(paths) };
            OpenTabGuard.Reload = (paths, tab) => Reload(paths);
        }

        internal static TabProbe Check(IList<string> paths)
        {
            return CheckWith(paths, snaps => Match(snaps, Probes(paths)), "check");
        }

        /// <summary>
        /// Pipeline run 3: the check for a path the #266 gate REFUSED. The tab lookup still runs - a refused path can
        /// still be open in a dirty tab, and writing under it is the #205 data loss - but by path TEXT only
        /// (FileIdentity.Lexical over the full paths): no file handle is opened and no I/O touches the refused path.
        /// Answers kind + dirty of a matching tab, or kind none.
        /// </summary>
        internal static TabProbe CheckLexical(IList<string> paths)
        {
            return CheckWith(paths, snaps => MatchLexical(snaps, paths), "check-lexical");
        }

        private static TabProbe CheckWith(IList<string> paths, Func<List<TabSnap>, List<TabSnap>> match, string label)
        {
            TabProbe answer;
            List<TabSnap> snaps = null;
            Exception error;
            var outcome = UiThreadCall.Run(_ui, () => { snaps = SnapshotOnUi(); }, CheckWaitMs, out error);
            if (outcome != UiThreadCall.Outcome.Ran)
                answer = new TabProbe
                {
                    Reachable = false,
                    TimedOut = outcome == UiThreadCall.Outcome.TimedOut,
                    Answered = outcome != UiThreadCall.Outcome.TimedOut,
                    Reason = Why(outcome, error, CheckWaitMs)
                };
            else
            {
                try { answer = Classify(match(snaps)); }
                catch (Exception ex) { answer = new TabProbe { Reachable = false, Answered = true, Reason = "the editor lookup failed: " + ex.Message }; }
            }
            answer.Ide = "IDE " + System.Diagnostics.Process.GetCurrentProcess().Id;
            MonacoSpikeLog.Write("[tab-sync] " + label + " " + Join(paths) + " -> " + (answer.Reachable
                ? answer.Kind + (answer.Dirty ? " dirty" : " clean") : "unreachable: " + answer.Reason));
            return answer;
        }

        internal static TabReload Reload(IList<string> paths)
        {
            TabReload answer = null;
            List<TabSnap> snaps = null;
            Exception error;
            var outcome = UiThreadCall.Run(_ui, () => { snaps = SnapshotOnUi(); }, ReloadSnapshotWaitMs, out error);
            if (outcome != UiThreadCall.Outcome.Ran)
                answer = new TabReload { Reason = Why(outcome, error, ReloadSnapshotWaitMs) };
            else
            {
                List<TabSnap> natives = null;
                try
                {
                    natives = Match(snaps, Probes(paths)).FindAll(s => !s.OverlayUp);   // the CA Editor offers its own reload
                }
                catch (Exception ex) { answer = new TabReload { Reason = "the editor lookup failed: " + ex.Message }; }
                if (answer == null)
                {
                    if (natives.Count == 0) answer = new TabReload { Reason = "the tab is no longer open in the native editor" };
                    else
                    {
                        outcome = UiThreadCall.Run(_ui, () => { answer = ReloadOnUi(natives); }, ReloadWaitMs, out error);
                        if (outcome != UiThreadCall.Outcome.Ran) answer = new TabReload { Reason = Why(outcome, error, ReloadWaitMs) };
                    }
                }
            }
            MonacoSpikeLog.Write("[tab-sync] reload " + Join(paths) + " -> " + (answer.Reloaded
                ? "reloaded, caret line " + answer.Line : "not reloaded: " + answer.Reason));
            return answer;
        }

        private static string Why(UiThreadCall.Outcome o, Exception error, int waitMs)
        {
            switch (o)
            {
                case UiThreadCall.Outcome.Ran: return null;
                case UiThreadCall.Outcome.TimedOut:
                    return "the IDE's UI thread did not answer within "
                        + (waitMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";
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

        // Worker thread: which snapshots are the target file. File handles are opened here, never on the UI thread.
        private static List<TabSnap> Match(List<TabSnap> snaps, List<FileIdentity.Probe> probes)
        {
            var matched = new List<TabSnap>();
            if (snaps != null) foreach (var s in snaps) if (FileIdentity.MatchesAny(s.File, probes)) matched.Add(s);
            return matched;
        }

        // Path text only, for a path the policy refused. Not even Path.GetFullPath: on .NET Framework it expands 8.3
        // segments with GetLongPathName, which touches the disk. Both sides are absolute already (a tab's FileName; the
        // standalone sends gated or caller-given absolute paths), so separators and case are all that need evening out.
        private static List<TabSnap> MatchLexical(List<TabSnap> snaps, IList<string> paths)
        {
            var wanted = new List<string>();
            if (paths != null) foreach (var p in paths) if (!string.IsNullOrEmpty(p)) wanted.Add(FullText(p));
            var matched = new List<TabSnap>();
            if (snaps != null)
                foreach (var s in snaps)
                {
                    if (string.IsNullOrEmpty(s.File)) continue;
                    string f = FullText(s.File);
                    foreach (var w in wanted) if (FileIdentity.Lexical(f, w)) { matched.Add(s); break; }
                }
            return matched;
        }

        private static string FullText(string p)
        {
            return p.Replace('/', '\\');
        }

        private static TabProbe Classify(List<TabSnap> matched)
        {
            bool caOpen = false, caDirty = false, nativeOpen = false, nativeDirty = false;
            foreach (var s in matched)
            {
                if (s.OverlayUp) { caOpen = true; if (s.OverlayDirty) caDirty = true; }
                else { nativeOpen = true; if (s.Dirty) nativeDirty = true; }
            }
            // A native tab is the one that loses data, so it decides when both kinds hold the file.
            if (nativeOpen) return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNative, Dirty = nativeDirty };
            if (caOpen) return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindCaEditor, Dirty = caDirty };
            return new TabProbe { Reachable = true, Kind = OpenTabGuard.KindNone };
        }

        // UI thread, property reads only: every open text-editor view.
        private static List<TabSnap> SnapshotOnUi()
        {
            var snaps = new List<TabSnap>();
            var seen = new HashSet<object>();
            foreach (var vc in OpenViews())
            {
                var w = vc as TextEditorDisplayBindingWrapper;
                if (w == null || !seen.Add(w)) continue;
                var me = w as MonacoClarionEditor;
                bool up = me != null && me.OverlayUp;
                snaps.Add(new TabSnap
                {
                    View = w,
                    File = FileNameOf(w),
                    Dirty = w.IsDirty,
                    OverlayUp = up,
                    OverlayDirty = up && me.OverlayDirty
                });
            }
            return snaps;
        }

        private static System.Collections.IEnumerable OpenViews()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                var all = wb == null ? null : wb.GetType().GetProperty("ViewContentCollection",
                    BindingFlags.Public | BindingFlags.Instance).GetValue(wb, null) as System.Collections.IEnumerable;
                if (all != null) return all;
            }
            catch { }
            return new object[0];
        }

        // UI thread: reload the matched views, each re-checked here first.
        private static TabReload ReloadOnUi(List<TabSnap> natives)
        {
            var open = new HashSet<object>();
            foreach (var vc in OpenViews()) open.Add(vc);
            foreach (var s in natives)
            {
                if (!open.Contains(s.View)) return new TabReload { Reason = "the tab was closed meanwhile" };
                var me = s.View as MonacoClarionEditor;
                if (me != null && me.OverlayUp) return new TabReload { Reason = "the tab is now in the CA Editor, which offers its own reload" };
                if (s.View.IsDirty) return new TabReload { Reason = "the tab has unsaved changes now, so reloading would lose them" };
                if (!string.Equals(FileNameOf(s.View), s.File, StringComparison.OrdinalIgnoreCase))
                    return new TabReload { Reason = "the tab now holds another file" };
            }

            int caretLine = 0;
            foreach (var s in natives)
            {
                string own = s.File;
                if (string.IsNullOrEmpty(own)) return new TabReload { Reason = "the tab has no file name" };
                caretLine = ReloadKeepingCaret(s.View, own);
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

        private static string FileNameOf(TextEditorDisplayBindingWrapper w)
        {
            try { var f = w.FileName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return w.TextEditorControl.FileName; } catch { return null; }
        }
    }
}
