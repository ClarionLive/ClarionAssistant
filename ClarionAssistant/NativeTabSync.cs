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
    /// THE IDE'S OWN FILE WATCHER (live test 2026-10-19, C12). Every text tab owns a
    /// TextEditorDisplayBindingWrapper.FileChangeWatcher (private field 'watcher'): a FileSystemWatcher whose Changed
    /// event sets its private 'wasChangedExternally' and, on the next MainForm.Activated (at once if the IDE is the
    /// active window), runs GotFocusEvent: clear the flag, then auto-reload (caret to line 1) or ask "file changed
    /// externally, reload?". Load re-arms the FileSystemWatcher but does NOT clear that flag - only GotFocusEvent does,
    /// before it reloads - so our reload alone left the prompt pending for every write. Same members in C10, C11, C11.1
    /// and C12 (reflected). So, as the IDE's own "Yes, reload" path does:
    ///   - CHECK, WRITE PATH ONLY: a clean native tab's watcher is HELD (FileChangeWatcher.Disable) before the write, so
    ///     the write raises no change event at all - which also covers the docked terminal, where the IDE is the active
    ///     window and would react within milliseconds, before our reload arrives. Only the write path's check holds
    ///     (hold: true from the standalone; the addin's own write_file), and only tabs of spellings that pass the #266
    ///     WRITE gate (review SEC MED). The bookkeeping is WatcherHoldBook: holders are COUNTED (concurrent writes share one
    ///     hold), the hold is CAPPED at HoldExpiryMs from its FIRST holder and never renewed (a capped tab then cools down
    ///     as long), and each hold has a GENERATION a release must still match.
    ///   - RELOAD: after Load, 'wasChangedExternally' is cleared (the GotFocusEvent order: clear, then Load) and one holder
    ///     dropped; while others remain the watcher is disabled again. A refused reload, or a write that was refused or
    ///     failed (editor_tab_sync action 'release', from OpenTabGuard.Run), drops its holder at once.
    ///   - RELEASE (last holder gone, or the cap): the watcher is re-armed (SetWatcher) and, WHENEVER the file changed
    ///     since the hold began - even if the IDE re-armed it meanwhile, by a save - the IDE's own change notification
    ///     (OnFileChangedEvent) is replayed, so a change made during the hold is never swallowed.
    /// The standalone exe cannot make the hold unforgeable: it reaches this IDE over the same endpoint (and token) any MCP
    /// client of this IDE pane uses, so the IDE cannot tell its write path from an agent calling editor_tab_sync itself.
    /// The write gate, the cap with cool-down, and the replay bound what such a call can do: the IDE learns of a change at
    /// most HoldExpiryMs late, never not at all.
    /// Every member is looked up by reflection and checked; a missing one is logged loudly once (WATCHER MEMBER MISSING)
    /// and the sync degrades to the reload alone (the IDE may then ask to reload).
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
            // editor_tab_sync (the standalone's call-back): a plain check never holds; the write path's check (hold: true)
            // holds only the tabs of the spellings that passed the WRITE gate (McpToolRegistry).
            OpenTabGuard.LocalCheck = CheckOnly;
            OpenTabGuard.LocalCheckHold = CheckAndHold;
            OpenTabGuard.LocalCheckLexical = CheckLexical;
            OpenTabGuard.LocalReload = Reload;
            OpenTabGuard.LocalRelease = Release;
            // The addin serving write_file itself (no standalone exe): ask this IDE directly. That check IS the write path
            // (write_file has write-gated the path already), so it holds; a refused or failed write releases at once.
            OpenTabGuard.Probe = paths => new List<TabProbe> { CheckAndHold(paths, paths) };
            OpenTabGuard.Reload = (paths, tab) => Reload(paths, null);
            OpenTabGuard.Release = (paths, tab) => Release(paths, null);
        }

        /// <summary>The tab lookup alone: no watcher is held.</summary>
        internal static TabProbe CheckOnly(IList<string> paths)
        {
            return CheckWith(paths, snaps => Match(snaps, Probes(paths)), "check", null);
        }

        /// <summary>
        /// The write path's check: the lookup over <paramref name="paths"/>, then a hold on the clean native tabs that
        /// match <paramref name="holdPaths"/> - only the spellings the WRITE gate allowed (SEC MED: a read-only spelling
        /// riding along as also_path can never get its tab's watcher switched off).
        /// </summary>
        internal static TabProbe CheckAndHold(IList<string> paths, IList<string> holdPaths)
        {
            return CheckWith(paths, snaps => Match(snaps, Probes(paths)), "check", holdPaths);
        }

        /// <summary>
        /// Pipeline run 3: the check for a path the #266 gate REFUSED. The tab lookup still runs - a refused path can
        /// still be open in a dirty tab, and writing under it is the #205 data loss - but by path TEXT only
        /// (FileIdentity.Lexical over the full paths): no file handle is opened and no I/O touches the refused path.
        /// Answers kind + dirty of a matching tab, or kind none.
        /// </summary>
        internal static TabProbe CheckLexical(IList<string> paths)
        {
            // No watcher hold here: holding needs a file stamp, and no I/O may touch a refused path.
            return CheckWith(paths, snaps => MatchLexical(snaps, paths), "check-lexical", null);
        }

        private static TabProbe CheckWith(IList<string> paths, Func<List<TabSnap>, List<TabSnap>> match, string label, IList<string> holdPaths)
        {
            bool holdWatchers = holdPaths != null && holdPaths.Count > 0;
            List<TabSnap> matched = null;
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
                try { matched = match(snaps); answer = Classify(matched); }
                catch (Exception ex) { answer = new TabProbe { Reachable = false, Answered = true, Reason = "the editor lookup failed: " + ex.Message }; }
            }
            answer.Ide = "IDE " + System.Diagnostics.Process.GetCurrentProcess().Id;
            MonacoSpikeLog.Write("[tab-sync] " + label + " " + Join(paths) + " -> " + (answer.Reachable
                ? answer.Kind + (answer.Dirty ? " dirty" : " clean") : "unreachable: " + answer.Reason));
            // A clean native tab is about to be written under: hold its IDE file watcher until our reload (see the class
            // comment). Best effort - a failed hold only means the IDE may ask to reload; the answer stands.
            if (holdWatchers && answer.Reachable && answer.Kind == OpenTabGuard.KindNative && !answer.Dirty && matched != null)
            {
                // Only the tabs of the write-allowed spellings (matched again, worker thread, by identity).
                try { HoldWatchers(Match(snaps, Probes(holdPaths)), holdPaths); }
                catch (Exception ex) { MonacoSpikeLog.Write("[tab-sync] watcher hold failed: " + ex.Message); }
            }
            return answer;
        }

        /// <summary>Reload the native tabs of the file. <paramref name="paths"/> are matched by file identity (the gate
        /// allowed them); <paramref name="byNameOnly"/> by path text only (refused spellings: no handle is opened on them).</summary>
        internal static TabReload Reload(IList<string> paths, IList<string> byNameOnly)
        {
            TabReload answer = null;
            List<TabSnap> snaps = null;
            Exception error;
            var outcome = UiThreadCall.Run(_ui, () => { snaps = SnapshotOnUi(); }, ReloadSnapshotWaitMs, out error);
            if (outcome != UiThreadCall.Outcome.Ran)
                answer = new TabReload { Reason = Why(outcome, error, ReloadSnapshotWaitMs) };
            else
            {
                List<TabSnap> natives = null, matched = null;
                try
                {
                    matched = Match(snaps, Probes(paths));
                    foreach (var s in MatchLexical(snaps, byNameOnly)) if (!matched.Contains(s)) matched.Add(s);
                    natives = matched.FindAll(s => !s.OverlayUp);   // the CA Editor offers its own reload
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
                // Not reloaded (refused, closed, now in the CA Editor): this holder is done - one fewer; the last one gives
                // the IDE its watcher back now (re-armed, its own change notice replayed when the file changed) instead of
                // at the cap. A reload that timed out but still runs late drops its holder itself (WatcherReset).
                if (!answer.Reloaded && matched != null && outcome == UiThreadCall.Outcome.Ran)
                    foreach (var s in matched) DropHolder(s.View, "the reload did not happen (" + answer.Reason + ")");
            }
            MonacoSpikeLog.Write("[tab-sync] reload " + Join(paths) + (byNameOnly != null && byNameOnly.Count > 0 ? " | by name: " + Join(byNameOnly) : "") + " -> " + (answer.Reloaded
                ? "reloaded, caret line " + answer.Line : "not reloaded: " + answer.Reason));
            return answer;
        }

        /// <summary>
        /// GH #205 review F1-1: the writer's check held this file's watcher, but its write was refused or failed, so no
        /// reload follows. Drop that holder now (the last one re-arms the watcher, replaying the IDE's change notice when
        /// the file changed) instead of leaving the watcher off until the cap. Matched like <see cref="Reload"/>.
        /// </summary>
        internal static TabReload Release(IList<string> paths, IList<string> byNameOnly)
        {
            List<TabSnap> snaps = null;
            Exception error;
            string result;
            var outcome = UiThreadCall.Run(_ui, () => { snaps = SnapshotOnUi(); }, ReloadSnapshotWaitMs, out error);
            if (outcome != UiThreadCall.Outcome.Ran) result = "not released: " + Why(outcome, error, ReloadSnapshotWaitMs) + " (the cap gives it back)";
            else
            {
                try
                {
                    var matched = Match(snaps, Probes(paths));
                    foreach (var s in MatchLexical(snaps, byNameOnly)) if (!matched.Contains(s)) matched.Add(s);
                    int dropped = 0;
                    foreach (var s in matched) if (DropHolder(s.View, "the writer released it (its write did not happen)")) dropped++;
                    result = dropped + " holder(s) dropped";
                }
                catch (Exception ex) { result = "not released: " + ex.Message; }
            }
            MonacoSpikeLog.Write("[tab-sync] release " + Join(paths) + (byNameOnly != null && byNameOnly.Count > 0 ? " | by name: " + Join(byNameOnly) : "") + " -> " + result);
            return new TabReload { Reloaded = false, Reason = result };
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

        // Path text only, for a path the policy refused (FileIdentity.LexicalMatch: pure-string normalisation, no
        // GetFullPath - on .NET Framework it expands 8.3 segments with GetLongPathName, which touches the disk).
        private static List<TabSnap> MatchLexical(List<TabSnap> snaps, IList<string> paths)
        {
            var matched = new List<TabSnap>();
            if (snaps != null)
                foreach (var s in snaps)
                    if (FileIdentity.LexicalMatch(s.File, paths)) matched.Add(s);
            return matched;
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
            // The IDE's own "Yes, reload" clears its pending-change flag before this same Load; do it after ours, or the
            // IDE reloads again (caret to line 1) or asks "changed externally, reload?" on its next activation.
            WatcherReset(w);

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

        // ---- the IDE's own file watcher: TextEditorDisplayBindingWrapper.FileChangeWatcher (see the class comment) ----

        /// <summary>How long a check's hold may wait for the UI thread; a hold that gets there later does nothing.</summary>
        internal const int HoldWaitMs = 500;
        /// <summary>The CAP: a hold ends this long after its FIRST holder took it, whoever joined since (never renewed), and
        /// a capped tab is not held again for as long (WatcherHoldBook).</summary>
        internal const int HoldExpiryMs = 30000;
        internal const int ReleaseWaitMs = 2000;

        /// <summary>Who holds which tab's watcher: counted holders, a fixed cap, a generation per hold (WatcherHoldBook).
        /// Keyed by the view; the entry's Tag is its cap timer.</summary>
        private static readonly WatcherHoldBook _book = new WatcherHoldBook(HoldExpiryMs);

        private static readonly object _membersLock = new object();
        private static bool _membersResolved;
        private static FieldInfo _viewWatcher, _watcherFsw, _watcherChanged, _watcherFile;
        private static MethodInfo _watcherDisable, _watcherSetWatcher, _watcherOnChanged;

        /// <summary>
        /// Look the watcher's members up once (all private or nested; the same in C10, C11, C11.1 and C12). Every missing
        /// one is logged loudly: probes into this fork otherwise fail silently as null.
        /// </summary>
        private static void ResolveWatcherMembers()
        {
            lock (_membersLock)
            {
                if (_membersResolved) return;
                _membersResolved = true;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var missing = new List<string>();
                try
                {
                    _viewWatcher = typeof(TextEditorDisplayBindingWrapper).GetField("watcher", inst);
                    Type wt = _viewWatcher != null ? _viewWatcher.FieldType : null;
                    if (wt == null || wt.Name != "FileChangeWatcher") { missing.Add("TextEditorDisplayBindingWrapper.watcher (FileChangeWatcher)"); _viewWatcher = null; }
                    else
                    {
                        _watcherFsw = wt.GetField("watcher", inst);
                        _watcherChanged = wt.GetField("wasChangedExternally", inst);
                        _watcherFile = wt.GetField("fileName", inst);
                        _watcherDisable = wt.GetMethod("Disable", inst, null, Type.EmptyTypes, null);
                        _watcherSetWatcher = wt.GetMethod("SetWatcher", inst, null, new[] { typeof(string) }, null);
                        _watcherOnChanged = wt.GetMethod("OnFileChangedEvent", inst, null, new[] { typeof(object), typeof(FileSystemEventArgs) }, null);
                        if (_watcherFsw == null || _watcherFsw.FieldType != typeof(FileSystemWatcher)) { missing.Add("FileChangeWatcher.watcher (FileSystemWatcher)"); _watcherFsw = null; }
                        if (_watcherChanged == null || _watcherChanged.FieldType != typeof(bool)) { missing.Add("FileChangeWatcher.wasChangedExternally (bool)"); _watcherChanged = null; }
                        if (_watcherFile == null || _watcherFile.FieldType != typeof(string)) { missing.Add("FileChangeWatcher.fileName (string)"); _watcherFile = null; }
                        if (_watcherDisable == null) missing.Add("FileChangeWatcher.Disable()");
                        if (_watcherSetWatcher == null) missing.Add("FileChangeWatcher.SetWatcher(string)");
                        if (_watcherOnChanged == null) missing.Add("FileChangeWatcher.OnFileChangedEvent(object, FileSystemEventArgs)");
                    }
                }
                catch (Exception ex) { missing.Add("the lookup failed: " + ex.Message); }
                if (missing.Count > 0)
                    MonacoSpikeLog.Write("[tab-sync] WATCHER MEMBER MISSING in this IDE: " + string.Join(", ", missing.ToArray())
                        + ". A write to a file open in a native tab may make the IDE ask 'changed externally, reload?' (hold: "
                        + (CanHold ? "available" : "OFF") + ", reset after reload: " + (CanReset ? "available" : "OFF") + ").");
            }
        }

        private static bool CanReset { get { return _viewWatcher != null && _watcherChanged != null; } }
        private static bool CanHold
        {
            get
            {
                return _viewWatcher != null && _watcherFsw != null && _watcherDisable != null && _watcherSetWatcher != null
                    && (_watcherOnChanged != null || _watcherChanged != null);
            }
        }

        /// <summary>The file's last-write time and length (length -1: absent). False when it cannot be read.</summary>
        private static bool Stamp(string path, out long ticks, out long length)
        {
            ticks = 0; length = -1;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) { ticks = fi.LastWriteTimeUtc.Ticks; length = fi.Length; }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Worker thread, from the WRITE PATH's check that found a CLEAN native tab: hold that tab's IDE watcher (Disable)
        /// before the write, so the write raises no "changed externally" event. <paramref name="paths"/> are the spellings
        /// the write gate allowed. The file is stamped here (I/O off the UI thread; a network path is never touched, so such
        /// a tab is not held). The UI call only flips the watcher.
        /// </summary>
        private static void HoldWatchers(List<TabSnap> matched, IList<string> paths)
        {
            var natives = matched.FindAll(s => !s.OverlayUp && !s.Dirty);
            if (natives.Count == 0) return;
            ResolveWatcherMembers();
            if (!CanHold) { MonacoSpikeLog.Write("[tab-sync] watcher NOT held (WATCHER MEMBER MISSING, see above)"); return; }
            string statPath = null;
            if (paths != null)
                foreach (var p in paths)
                    if (!string.IsNullOrEmpty(p) && !FileIdentity.IsNetworkPath(p)) { statPath = p; break; }
            long ticks, length;
            if (statPath == null || !Stamp(statPath, out ticks, out length))
            {
                MonacoSpikeLog.Write("[tab-sync] watcher not held: no local path to stamp (" + Join(paths) + ")");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int held = 0;
            string notHeld = "";
            Exception error;
            // A hold that reaches the UI thread after the wait gave up does nothing: it could land after the write and the
            // reload, and disable a watcher nobody re-arms until the cap.
            var outcome = UiThreadCall.Run(_ui, () => { if (sw.ElapsedMilliseconds <= HoldWaitMs) held = HoldOnUi(natives, statPath, ticks, length, out notHeld); },
                HoldWaitMs, out error);
            MonacoSpikeLog.Write("[tab-sync] watcher hold " + statPath + " -> " + (outcome == UiThreadCall.Outcome.Ran
                ? held + " tab(s) held" + notHeld : "not held: " + Why(outcome, error, HoldWaitMs)));
        }

        // UI thread: hold each re-checked tab. The book decides under its lock and the watcher flip runs inside it (F1-3):
        // a hold being released takes no new holder, and a capped tab cools down. A joined hold is COUNTED (F1-2) and keeps
        // its first stamp and its deadline: the cap timer is set once, by the first holder, and never renewed (SEC MED).
        // Property reads and the watcher flip only.
        private static int HoldOnUi(List<TabSnap> natives, string statPath, long ticks, long length, out string notHeld)
        {
            notHeld = "";
            var open = new HashSet<object>();
            foreach (var vc in OpenViews()) open.Add(vc);
            int held = 0;
            foreach (var s in natives)
            {
                var me = s.View as MonacoClarionEditor;
                if (!open.Contains(s.View) || (me != null && me.OverlayUp) || s.View.IsDirty) continue;
                object watcher = _viewWatcher.GetValue(s.View);
                var fsw = watcher != null ? _watcherFsw.GetValue(watcher) as FileSystemWatcher : null;
                bool watching = fsw != null && fsw.EnableRaisingEvents;
                WatcherHoldBook.Entry e;
                var take = _book.Hold(s.View, watching, statPath, ticks, length, () => _watcherDisable.Invoke(watcher, null), out e);
                if (take == WatcherHoldBook.Take.NewHold)
                {
                    object view = s.View;
                    long gen = e.Gen;
                    var cap = new System.Threading.Timer(_ => ExpireHold(view, gen), null, Timeout.Infinite, Timeout.Infinite);
                    e.Tag = cap;
                    cap.Change(_book.DueMs(e), Timeout.Infinite);   // once, from the FIRST holder: never renewed
                }
                if (take == WatcherHoldBook.Take.NewHold || take == WatcherHoldBook.Take.Joined) held++;
                else notHeld += "; one not held (" + take + ")";
            }
            return held;
        }

        /// <summary>UI thread, right after OUR Load: clear the IDE's pending-change flag (its "Yes, reload" order is clear,
        /// then Load) and drop one holder. Load has re-armed the watcher (SetWatcher): the last holder leaves it so; while
        /// other writes still hold the tab (F1-2) it is disabled again until their reloads.</summary>
        private static void WatcherReset(TextEditorDisplayBindingWrapper w)
        {
            bool stillHeld;
            WatcherHoldBook.Entry ended;
            bool wasHeld = _book.AfterReload(w, out stillHeld, out ended);
            if (ended != null) DisposeCap(ended);
            ResolveWatcherMembers();
            if (!CanReset) { MonacoSpikeLog.Write("[tab-sync] watcher NOT reset after reload (WATCHER MEMBER MISSING, see above): the IDE may ask to reload"); return; }
            try
            {
                object watcher = _viewWatcher.GetValue(w);
                if (watcher == null) { MonacoSpikeLog.Write("[tab-sync] watcher NOT reset after reload: the tab has no FileChangeWatcher"); return; }
                bool pending = (bool)_watcherChanged.GetValue(watcher);
                _watcherChanged.SetValue(watcher, false);
                if (stillHeld && CanHold) _watcherDisable.Invoke(watcher, null);   // other writers still hold it
                MonacoSpikeLog.Write("[tab-sync] watcher reset after reload (" + (!wasHeld ? "was not held"
                        : stillHeld ? "still held by " + _book.Holders(w) + " other write(s)" : "held since the check; hold ended")
                    + (pending ? "; cleared a pending external change" : "") + ")");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[tab-sync] watcher reset FAILED: " + ex.Message); }
        }

        /// <summary>One holder of <paramref name="view"/> is done without a reload (refused or failed write, a reload that
        /// did not happen, an explicit release). The LAST one gives the watcher back now instead of at the cap.
        /// True when the view was held.</summary>
        private static bool DropHolder(TextEditorDisplayBindingWrapper view, string why)
        {
            if (view == null) return false;
            bool held = _book.IsHeld(view);
            var e = _book.Drop(view, why);
            if (e != null)
            {
                DisposeCap(e);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { FinishRelease(e); }
                    catch (Exception ex) { MonacoSpikeLog.Write("[tab-sync] watcher release FAILED: " + ex.Message); }
                });
            }
            return held;
        }

        // Cap timer of one generation: release it whatever the count (the cap is absolute).
        private static void ExpireHold(object view, long gen)
        {
            try
            {
                var e = _book.Expire(view, gen);
                if (e != null) FinishRelease(e);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[tab-sync] watcher release FAILED: " + ex.Message); }
        }

        private static void DisposeCap(WatcherHoldBook.Entry e)
        {
            var t = e != null ? e.Tag as System.Threading.Timer : null;
            if (t != null) try { t.Dispose(); } catch { }
        }

        // Worker/timer thread: did the file change since the hold BEGAN (stamped here, off the UI thread), then give the
        // watcher back on the UI thread - for THIS generation only.
        private static void FinishRelease(WatcherHoldBook.Entry e)
        {
            DisposeCap(e);
            long gen = e.Gen;
            long t, l;
            bool stampOk = Stamp(e.StatPath, out t, out l);
            bool changed = WatcherHoldBook.Changed(e, stampOk, t, l);
            string result = null;
            Exception error;
            var outcome = UiThreadCall.Run(_ui, () => { result = ReleaseOnUi(e, gen, changed); }, ReleaseWaitMs, out error);
            // Never posted (no UI thread) or failed: forget it, so the tab is not stuck "releasing". A timed-out post still
            // runs late and finishes it there.
            if (outcome == UiThreadCall.Outcome.NoUiThread || outcome == UiThreadCall.Outcome.Failed) _book.Forget(e.Key, gen);
            MonacoSpikeLog.Write("[tab-sync] watcher released (" + e.Why + "): " + (outcome == UiThreadCall.Outcome.Ran
                ? result : Why(outcome, error, ReleaseWaitMs)));
        }

        // UI thread: only if generation <paramref name="gen"/> is still this tab's hold (F1-3), re-arm the watcher
        // (SetWatcher) unless the IDE did meanwhile, and - SEC LOW - WHENEVER the file changed since the hold began, even
        // when the IDE re-armed it (a save, say), replay the IDE's own change notice (OnFileChangedEvent: sets the flag; if
        // the IDE is the active window it schedules its own prompt or auto-reload, asynchronously), so a third party's
        // change made during the hold is never lost behind a save that gave no prompt.
        private static string ReleaseOnUi(WatcherHoldBook.Entry e, long gen, bool changed)
        {
            if (!_book.Finish(e.Key, gen)) return "generation " + gen + " is no longer this tab's hold (a reload ended it, or a newer hold took over); left alone";
            var view = e.Key as TextEditorDisplayBindingWrapper;
            bool open = false;
            foreach (var vc in OpenViews()) if (ReferenceEquals(vc, view)) { open = true; break; }
            if (view == null || !open) return "the tab was closed";
            object watcher = _viewWatcher.GetValue(view);
            if (watcher == null) return "the tab has no FileChangeWatcher";
            var fsw = _watcherFsw.GetValue(watcher) as FileSystemWatcher;
            bool rearmedByIde = fsw != null && fsw.EnableRaisingEvents;
            string file = (_watcherFile != null ? _watcherFile.GetValue(watcher) as string : null) ?? FileNameOf(view);
            if (string.IsNullOrEmpty(file)) return "the tab has no file name";
            if (!rearmedByIde)
            {
                _watcherSetWatcher.Invoke(watcher, new object[] { file });
                fsw = _watcherFsw.GetValue(watcher) as FileSystemWatcher;
            }
            string armed = rearmedByIde ? "re-armed by the IDE meanwhile (a save or a reload)" : "re-armed";
            if (!changed) return armed + " (the file did not change)";
            if (_watcherOnChanged != null)
            {
                _watcherOnChanged.Invoke(watcher, new object[] { fsw,
                    new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(file) ?? "", Path.GetFileName(file)) });
                return armed + "; the file changed during the hold, so the IDE's own change notice was replayed";
            }
            _watcherChanged.SetValue(watcher, true);
            return armed + "; the file changed during the hold, so the IDE offers its reload on its next activation";
        }

        private static string FileNameOf(TextEditorDisplayBindingWrapper w)
        {
            try { var f = w.FileName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return w.TextEditorControl.FileName; } catch { return null; }
        }
    }
}
