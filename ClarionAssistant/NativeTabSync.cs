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
    ///   - CHECK: a clean native tab's watcher is HELD (FileChangeWatcher.Disable) before the write, so the write raises
    ///     no change event at all - which also covers the docked terminal, where the IDE is the active window and would
    ///     react within milliseconds, before our reload arrives. A hold expires after HoldExpiryMs (a write that failed,
    ///     a writer that died): the watcher is re-armed (SetWatcher) and, if the file did change meanwhile, the IDE's own
    ///     change notification (OnFileChangedEvent) is replayed, so it prompts exactly as it would have.
    ///   - RELOAD: after Load, 'wasChangedExternally' is cleared (the GotFocusEvent order: clear, then Load) and the hold
    ///     dropped. A refused reload releases its hold at once (re-arm; replay when changed).
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
            OpenTabGuard.LocalCheck = Check;
            OpenTabGuard.LocalCheckLexical = CheckLexical;
            OpenTabGuard.LocalReload = Reload;
            // The addin serving write_file itself (no standalone exe): ask this IDE directly.
            OpenTabGuard.Probe = paths => new List<TabProbe> { Check(paths) };
            OpenTabGuard.Reload = (paths, tab) => Reload(paths, null);
        }

        internal static TabProbe Check(IList<string> paths)
        {
            return CheckWith(paths, snaps => Match(snaps, Probes(paths)), "check", true);
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
            return CheckWith(paths, snaps => MatchLexical(snaps, paths), "check-lexical", false);
        }

        private static TabProbe CheckWith(IList<string> paths, Func<List<TabSnap>, List<TabSnap>> match, string label, bool holdWatchers)
        {
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
                try { HoldWatchers(matched, paths); }
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
                // Not reloaded (refused, closed, now in the CA Editor): give the IDE its watcher back now - re-armed, and
                // its own change notification replayed when the file changed - instead of at the hold's expiry. A reload
                // that timed out but still runs late clears its hold itself.
                if (!answer.Reloaded && matched != null && outcome == UiThreadCall.Outcome.Ran)
                    foreach (var s in matched) ReleaseHoldSoon(s.View, "the reload did not happen (" + answer.Reason + ")");
            }
            MonacoSpikeLog.Write("[tab-sync] reload " + Join(paths) + (byNameOnly != null && byNameOnly.Count > 0 ? " | by name: " + Join(byNameOnly) : "") + " -> " + (answer.Reloaded
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
        /// <summary>A hold no reload released (the write failed, the writer died) is given back after this.</summary>
        internal const int HoldExpiryMs = 30000;
        internal const int ReleaseWaitMs = 2000;

        /// <summary>A tab whose IDE watcher we disabled for a write, and the file as it was before that write.</summary>
        private sealed class Hold
        {
            public TextEditorDisplayBindingWrapper View;
            public string StatPath;
            public long Ticks, Length;
            public string Why = "it expired";
            public System.Threading.Timer Expiry;
        }
        private static readonly object _holdLock = new object();
        private static readonly Dictionary<object, Hold> _holds = new Dictionary<object, Hold>();

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
        /// Worker thread, from a check that found a CLEAN native tab: disable that tab's IDE watcher (Disable) before the
        /// write, so the write raises no "changed externally" event. The file is stamped here (I/O off the UI thread; a
        /// network path is never touched, so such a tab is not held). The UI call only flips the watcher.
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
            Exception error;
            // A hold that reaches the UI thread after the wait gave up does nothing: it could land after the write and the
            // reload, and disable a watcher nobody re-arms until the expiry.
            var outcome = UiThreadCall.Run(_ui, () => { if (sw.ElapsedMilliseconds <= HoldWaitMs) held = HoldOnUi(natives, statPath, ticks, length); },
                HoldWaitMs, out error);
            MonacoSpikeLog.Write("[tab-sync] watcher hold " + statPath + " -> " + (outcome == UiThreadCall.Outcome.Ran
                ? held + " tab(s) held" : "not held: " + Why(outcome, error, HoldWaitMs)));
        }

        // UI thread: disable the watchers, each tab re-checked first. Property reads and the watcher flip only.
        private static int HoldOnUi(List<TabSnap> natives, string statPath, long ticks, long length)
        {
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
                lock (_holdLock)
                {
                    Hold h;
                    bool had = _holds.TryGetValue(s.View, out h);
                    if (had && !watching) { h.Expiry.Change(HoldExpiryMs, Timeout.Infinite); held++; continue; }   // still ours: keep its stamp
                    if (!watching) continue;   // detection off, or no watcher yet: nothing to hold
                    _watcherDisable.Invoke(watcher, null);
                    if (!had)
                    {
                        h = new Hold { View = s.View };
                        h.Expiry = new System.Threading.Timer(ExpireHold, h, Timeout.Infinite, Timeout.Infinite);
                        _holds[s.View] = h;
                    }
                    // New, or re-armed by the IDE since (a save): stamp it afresh.
                    h.StatPath = statPath; h.Ticks = ticks; h.Length = length;
                    h.Expiry.Change(HoldExpiryMs, Timeout.Infinite);
                    held++;
                }
            }
            return held;
        }

        /// <summary>UI thread, right after OUR Load: clear the IDE's pending-change flag (its "Yes, reload" order is clear,
        /// then Load) and drop the hold - Load has already re-armed the watcher (SetWatcher).</summary>
        private static void WatcherReset(TextEditorDisplayBindingWrapper w)
        {
            Hold h = null;
            lock (_holdLock) { if (_holds.TryGetValue(w, out h)) _holds.Remove(w); }
            if (h != null) try { h.Expiry.Dispose(); } catch { }
            ResolveWatcherMembers();
            if (!CanReset) { MonacoSpikeLog.Write("[tab-sync] watcher NOT reset after reload (WATCHER MEMBER MISSING, see above): the IDE may ask to reload"); return; }
            try
            {
                object watcher = _viewWatcher.GetValue(w);
                if (watcher == null) { MonacoSpikeLog.Write("[tab-sync] watcher NOT reset after reload: the tab has no FileChangeWatcher"); return; }
                bool pending = (bool)_watcherChanged.GetValue(watcher);
                _watcherChanged.SetValue(watcher, false);
                MonacoSpikeLog.Write("[tab-sync] watcher reset after reload (" + (h != null ? "held since the check" : "was not held")
                    + (pending ? "; cleared a pending external change" : "") + ")");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[tab-sync] watcher reset FAILED: " + ex.Message); }
        }

        /// <summary>A reload that did not happen gives its hold back now instead of at the expiry.</summary>
        private static void ReleaseHoldSoon(TextEditorDisplayBindingWrapper view, string why)
        {
            lock (_holdLock)
            {
                Hold h;
                if (view != null && _holds.TryGetValue(view, out h)) { h.Why = why; h.Expiry.Change(0, Timeout.Infinite); }
            }
        }

        private static void ExpireHold(object state)
        {
            var h = state as Hold;
            if (h == null) return;
            try { ReleaseHold(h); }
            catch (Exception ex) { MonacoSpikeLog.Write("[tab-sync] watcher release FAILED: " + ex.Message); }
        }

        // Timer thread: did the file change while held (stamped here, off the UI thread), then give the watcher back.
        private static void ReleaseHold(Hold h)
        {
            string why;
            lock (_holdLock)
            {
                Hold cur;
                if (!_holds.TryGetValue(h.View, out cur) || cur != h) return;   // a reload already took it
                _holds.Remove(h.View);
                why = h.Why;
            }
            try { h.Expiry.Dispose(); } catch { }
            long t, l;
            bool changed = !Stamp(h.StatPath, out t, out l) || t != h.Ticks || l != h.Length;
            string result = null;
            Exception error;
            var outcome = UiThreadCall.Run(_ui, () => { result = ReleaseOnUi(h, changed); }, ReleaseWaitMs, out error);
            MonacoSpikeLog.Write("[tab-sync] watcher released (" + why + "): " + (outcome == UiThreadCall.Outcome.Ran
                ? result : Why(outcome, error, ReleaseWaitMs)));
        }

        // UI thread: re-arm the watcher (SetWatcher) and, when the file changed meanwhile, replay the IDE's own change
        // notification (OnFileChangedEvent: sets the flag; if the IDE is the active window it schedules its own prompt or
        // auto-reload, asynchronously), so the IDE reacts exactly as it would have without the hold.
        private static string ReleaseOnUi(Hold h, bool changed)
        {
            lock (_holdLock) { if (_holds.ContainsKey(h.View)) return "held again meanwhile; left to that hold"; }
            bool open = false;
            foreach (var vc in OpenViews()) if (ReferenceEquals(vc, h.View)) { open = true; break; }
            if (!open) return "the tab was closed";
            object watcher = _viewWatcher.GetValue(h.View);
            if (watcher == null) return "the tab has no FileChangeWatcher";
            var fsw = _watcherFsw.GetValue(watcher) as FileSystemWatcher;
            if (fsw != null && fsw.EnableRaisingEvents) return "re-armed by the IDE meanwhile (a save or a reload)";
            string file = (_watcherFile != null ? _watcherFile.GetValue(watcher) as string : null) ?? FileNameOf(h.View);
            if (string.IsNullOrEmpty(file)) return "the tab has no file name";
            _watcherSetWatcher.Invoke(watcher, new object[] { file });
            if (!changed) return "re-armed (the file did not change)";
            if (_watcherOnChanged != null)
            {
                _watcherOnChanged.Invoke(watcher, new object[] { fsw,
                    new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(file) ?? "", Path.GetFileName(file)) });
                return "re-armed; the file changed meanwhile, so the IDE's own change notice was replayed";
            }
            _watcherChanged.SetValue(watcher, true);
            return "re-armed; the file changed meanwhile, so the IDE offers its reload on its next activation";
        }

        private static string FileNameOf(TextEditorDisplayBindingWrapper w)
        {
            try { var f = w.FileName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return w.TextEditorControl.FileName; } catch { return null; }
        }
    }
}
