using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command — remembers which document tab was ACTIVE when a solution closed and
    /// re-selects it when the solution is opened again.
    ///
    /// The IDE persists only the LIST of open files (preferences\*.cwproj.*.xml, array "files", in tab
    /// order) — there is no active-document entry anywhere. On reopen it opens that list in order, so the
    /// last file in the list always ends up active, whatever the developer was looking at. This class
    /// supplies the missing piece:
    ///
    ///   record  — the file whose tab is DISPLAYED is written to active-documents.txt (one line per solution).
    ///             The IDE's own ActiveWorkbenchWindowChanged is not used for files that have a CA Editor: it only
    ///             fires once a document takes keyboard focus, so a plain tab click can leave it (and
    ///             ActiveWorkbenchWindow) on the previously focused tab for as long as focus stays elsewhere.
    ///             Each CA Editor reports its own tab through NotifyTabShown; the window event still covers
    ///             other document types (.app, dictionary, ...).
    ///   freeze  — every close path (Close Solution, opening another solution, IDE exit) calls
    ///             SaveSolutionPreferences FIRST and only then closes the tabs one by one, each close revealing
    ///             another tab, and raises SolutionClosing only after that. So recording is frozen at
    ///             SolutionPreferencesSaving — the same moment the IDE saves its own file list.
    ///             SolutionClosing/SolutionClosed freeze too, as a backstop. A cancelled close (Cancel on a
    ///             save prompt) leaves the solution open, and recording resumes.
    ///   restore — the IDE reopens the saved file list in ONE synchronous UI-thread call
    ///             (ProjectService.ParserServiceCreatedProjectContents, a plain FileService.OpenFile loop posted
    ///             by the parser's loadSolutionProjects thread after SolutionLoaded). It raises nothing when it
    ///             finishes, but a message posted to the UI thread from inside that loop only runs after the loop
    ///             returns. So a ViewOpened of the restore posts the selection, which lands right after
    ///             the last file has opened. The post waits until the saved file itself is open: a view that
    ///             opens BEFORE the loop (the .app tab when the solution comes from Recent Applications, a
    ///             start page) is not the loop. A verify pass 2 s later re-selects once if something selected
    ///             another tab; if the post is somehow delivered early, any later ViewOpened pushes that verify
    ///             back, and a quiet-window check (no view opened for 2.5 s) is the fallback if it never runs.
    ///             Recording resumes when the restore ends. Only the quiet window and the caps give up, and a
    ///             give-up HOLDS recording until the developer's own next tab change, so a reopen loop that
    ///             arrives late never records its last file over the saved choice (and if it opens the saved
    ///             file within the cap, that file is still selected).
    ///
    /// Only the file is stored: each editor tab already restores its own caret and scroll position.
    /// Everything is best-effort and logged to monaco-spike.log under "[active-doc]"; nothing here may
    /// throw at workbench load or disturb a normal tab switch.
    /// </summary>
    public class ActiveDocumentRestoreCommand : ICommand
    {
        private object _owner;
        public object Owner
        {
            get { return _owner; }
            set
            {
                _owner = value;
                var h = OwnerChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public event EventHandler OwnerChanged;

        // Timings. Quiet = no view opened for this long => the restore burst is over (fallback only).
        private const int TickMs = 500;
        private const int QuietMs = 2500;
        private const int NoViewsGiveUpMs = 20000;     // solution loaded but no tab ever appeared
        private const int HardCapMs = 60000;
        private const int ReassertAfterMs = 2000;      // verify once more after selecting, to beat late IDE selections
        private const int CancelCheckMs = 1500;        // a close still not done after two of these was cancelled

        // Rooted so the event delegates and the timers are never GC'd.
        private static Delegate _loadedHandler, _prefsSavingHandler, _closingHandler, _closedHandler, _viewOpenedHandler;
        private static Timer _timer, _cancelTimer;
        private static bool _wired;

        // State (UI thread only).
        private static bool _restoring;          // true between a solution load and the end of the restore
        private static bool _frozen;             // true once a close has begun: stop recording
        private static string _solutionKey;      // solution the current restore / freeze belongs to
        private static string _target;           // saved active file for _solutionKey, read BEFORE any recording
        private static int _restoreStartTick, _lastViewTick, _viewsSeen;
        private static bool _selected;           // SelectWindow issued for this restore
        private static bool _posted;             // selection queued behind the reopen loop for this restore
        private static bool _held;               // restore gave up: record only the developer's next tab change
        private static int _selectedTick;
        private static int _cancelChecks;        // consecutive "solution still open" checks after a freeze
        private static string _lastWritten;      // "solution\tfile" last written, to skip identical rewrites

        public void Run()
        {
            TryWireWorkbench();   // usually too early (Workbench is null at Autostart) — BeginRestore retries

            _loadedHandler = Subscribe("SolutionLoaded", "OnSolutionLoaded");
            _prefsSavingHandler = Subscribe("SolutionPreferencesSaving", "OnPreferencesSaving");
            _closingHandler = Subscribe("SolutionClosing", "OnSolutionClosing");
            _closedHandler = Subscribe("SolutionClosed", "OnSolutionClosed");
        }

        /// <summary>Autostart runs before the workbench exists, so this is retried from BeginRestore
        /// (SolutionLoaded is always late enough, and fires before the IDE reopens the files). Idempotent.</summary>
        private static void TryWireWorkbench()
        {
            if (_wired) return;
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                if (wb == null) { MonacoSpikeLog.Write("[active-doc] workbench hooks deferred: Workbench is null"); return; }
                wb.ActiveWorkbenchWindowChanged += OnActiveWindowChanged;
                _wired = true;
                MonacoSpikeLog.Write("[active-doc] WIRED: ActiveWorkbenchWindowChanged");

                // ViewOpened fires for every tab the IDE reopens, whatever the file type — the restore-burst clock.
                var evt = wb.GetType().GetEvent("ViewOpened", BindingFlags.Public | BindingFlags.Instance);
                var m = typeof(ActiveDocumentRestoreCommand).GetMethod("OnViewOpened", BindingFlags.NonPublic | BindingFlags.Static);
                if (evt == null) { MonacoSpikeLog.Write("[active-doc] NOT WIRED: Workbench.ViewOpened not found"); return; }
                _viewOpenedHandler = Delegate.CreateDelegate(evt.EventHandlerType, m);
                evt.AddEventHandler(wb, _viewOpenedHandler);
                MonacoSpikeLog.Write("[active-doc] WIRED: Workbench.ViewOpened");
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] workbench hook failed: " + ex.Message); }
        }

        private static Delegate Subscribe(string eventName, string handlerName)
        {
            try
            {
                var asm = Assembly.Load("ICSharpCode.SharpDevelop");
                var psType = asm == null ? null : asm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
                var evt = psType == null ? null : psType.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
                if (evt == null) { MonacoSpikeLog.Write("[active-doc] NOT WIRED: ProjectService." + eventName + " not found"); return null; }
                var m = typeof(ActiveDocumentRestoreCommand).GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Static);
                var d = Delegate.CreateDelegate(evt.EventHandlerType, m);
                evt.AddEventHandler(null, d);
                MonacoSpikeLog.Write("[active-doc] WIRED: ProjectService." + eventName);
                return d;
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] subscribe " + eventName + " failed: " + ex.Message); return null; }
        }

        // ── events ─────────────────────────────────────────────────────────────────────────────────

        private static void OnSolutionLoaded(object sender, EventArgs e)
        {
            try { BeginRestore(); }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] OnSolutionLoaded failed: " + ex.Message); }
        }

        // First step of every close path, BEFORE any tab closes: capture the active tab one last time, freeze.
        private static void OnPreferencesSaving(object sender, EventArgs e)
        {
            try
            {
                Freeze("preferences saving");
            }
            catch { }
        }

        // Raised only AFTER the tabs have been closed: freeze without capturing (backstop).
        private static void OnSolutionClosing(object sender, EventArgs e)
        {
            try { Freeze("solution closing"); } catch { }
        }

        private static void OnSolutionClosed(object sender, EventArgs e)
        {
            try { Freeze("solution closed"); StopCancelTimer(); } catch { }
        }

        private static void OnActiveWindowChanged(object sender, EventArgs e)
        {
            try
            {
                if (_restoring) { _lastViewTick = Environment.TickCount; return; }
                if (_frozen) return;
                // A file with a live CA Editor is recorded through NotifyTabShown; this event can lag behind it.
                string path = ActivePath();
                bool dirty;
                if (path != null && MonacoClarionEditor.TryGetLiveTabState(path, out dirty)) return;
                Record(path);
            }
            catch { }
        }

        /// <summary>Called by a CA Editor when its tab becomes visible (it is the front tab of its pane).</summary>
        internal static void NotifyTabShown(string path)
        {
            try { Record(path); }
            catch { }
        }

        private static void OnViewOpened(object sender, EventArgs e)
        {
            _lastViewTick = Environment.TickCount;
            if (_held && !_frozen && _lastViewTick - _restoreStartTick <= HardCapMs && IsOpen(_target))
            {
                // The reopen loop arrived after the restore gave up, and it has opened the saved file: restore after all.
                MonacoSpikeLog.Write("[active-doc] saved file opened after the restore gave up -> restoring");
                _held = false;
                _restoring = true;
                _selected = _posted = false;
                StartTimer();
            }
            if (!_restoring) return;
            _viewsSeen++;
            if (_selected) { _selectedTick = _lastViewTick; return; }   // still opening after we selected: verify later
            // Post only once the saved file itself is open. A view that opens before the reopen loop (the .app tab
            // from Recent Applications, a start page) would otherwise post before the loop has even started.
            if (!_posted && IsOpen(_target)) { _posted = true; PostAfterReopenLoop(); }
        }

        /// <summary>Queue the selection behind the IDE's synchronous reopen loop: it runs as soon as the loop
        /// has returned and the UI thread processes messages again.</summary>
        private static void PostAfterReopenLoop()
        {
            try
            {
                var form = WorkbenchSingleton.Workbench as Form;
                if (form == null || !form.IsHandleCreated) return;   // the quiet-window fallback covers it
                form.BeginInvoke((MethodInvoker)(() =>
                {
                    try { if (_restoring && !_selected) TrySelect("reopen loop finished", false); }
                    catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] post-reopen select failed: " + ex.Message); }
                }));
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] post after reopen failed: " + ex.Message); }
        }

        // ── freeze / cancelled close ───────────────────────────────────────────────────────────────

        private static void Freeze(string why)
        {
            if (!_frozen)
            {
                _solutionKey = Services.EditorService.GetOpenSolutionPath() ?? _solutionKey;
                MonacoSpikeLog.Write("[active-doc] " + why + " -> recording frozen (kept: " + Short(ReadSaved(_solutionKey)) + ")");
            }
            _frozen = true;
            _restoring = false;
            _held = false;
            StopTimer();
            if (_cancelTimer == null && why == "preferences saving") StartCancelTimer();
        }

        /// <summary>The close sequence is synchronous, so this only ticks while it waits on a modal save
        /// prompt (the main window is disabled then — keep waiting) or once it has finished. If the same
        /// solution is still open on two consecutive enabled ticks, the close was cancelled.</summary>
        private static void OnCancelTick(object sender, EventArgs e)
        {
            try
            {
                var form = WorkbenchSingleton.Workbench as Form;
                if (form != null && !form.Enabled) { _cancelChecks = 0; return; }
                string open = Services.EditorService.GetOpenSolutionPath();
                if (string.IsNullOrEmpty(open) || !SamePath(open, _solutionKey)) { StopCancelTimer(); return; }
                if (++_cancelChecks < 2) return;
                StopCancelTimer();
                _frozen = false;
                MonacoSpikeLog.Write("[active-doc] close was cancelled -> recording resumed");
            }
            catch { StopCancelTimer(); }
        }

        private static void StartCancelTimer()
        {
            _cancelChecks = 0;
            _cancelTimer = new Timer { Interval = CancelCheckMs };
            _cancelTimer.Tick += OnCancelTick;
            _cancelTimer.Start();
        }

        private static void StopCancelTimer()
        {
            try { if (_cancelTimer != null) { _cancelTimer.Stop(); _cancelTimer.Tick -= OnCancelTick; _cancelTimer.Dispose(); } } catch { }
            _cancelTimer = null;
        }

        // ── restore ────────────────────────────────────────────────────────────────────────────────

        private static void BeginRestore()
        {
            TryWireWorkbench();
            StopCancelTimer();
            _solutionKey = Services.EditorService.GetOpenSolutionPath();
            _frozen = false;
            _restoring = false;
            _target = ReadSaved(_solutionKey);
            _viewsSeen = 0;
            _selected = _posted = _held = false;
            _restoreStartTick = _lastViewTick = Environment.TickCount;
            MonacoSpikeLog.Write("[active-doc] BeginRestore solution=" + Short(_solutionKey) + " saved=" + Short(_target));
            if (string.IsNullOrEmpty(_solutionKey) || string.IsNullOrEmpty(_target)) return;   // nothing to restore: just record
            StartTimer();
            _restoring = true;
        }

        private static void StartTimer()
        {
            StopTimer();
            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private static void StopTimer()
        {
            try { if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer.Dispose(); } } catch { }
            _timer = null;
        }

        /// <summary>Ends the restore. A give-up holds recording until the developer's own next tab change: views may
        /// still be opening (the reopen loop can arrive after the quiet window), and they must not be recorded.</summary>
        private static void Finish(string why, bool giveUp)
        {
            _restoring = false;
            _held = giveUp;
            StopTimer();
            MonacoSpikeLog.Write("[active-doc] restore finished: " + why + " (active now: " + Short(ActivePath()) + ")"
                + (giveUp ? " -> recording held until the next tab change" : ""));
        }

        private static void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (!_restoring) { StopTimer(); return; }
                int now = Environment.TickCount;
                int sinceStart = now - _restoreStartTick;
                if (sinceStart > HardCapMs) { Finish("hard cap", true); return; }

                // Phase 2: we already selected the file — after a beat, verify and re-select once.
                if (_selected)
                {
                    if (now - _selectedTick < ReassertAfterMs) return;
                    if (!SamePath(ActivePath(), _target))
                    {
                        MonacoSpikeLog.Write("[active-doc] another tab was selected after us (" + Short(ActivePath()) + ") -> selecting again");
                        SelectTarget();
                    }
                    Finish("verified", false);
                    return;
                }

                // Phase 1: wait for the restore burst to end.
                if (_viewsSeen == 0)
                {
                    if (sinceStart > NoViewsGiveUpMs) Finish("no views were opened", true);
                    return;
                }
                if (now - _lastViewTick < QuietMs) return;
                TrySelect("no view opened for " + QuietMs + " ms", true);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] OnTick failed: " + ex.Message); Finish("error", true); }
        }

        /// <summary>Selects the saved file. Only the quiet-window check may give up (mayGiveUp) when it is not open:
        /// the posted path can run before the reopen loop has opened it, and then just waits for the next post.</summary>
        private static void TrySelect(string why, bool mayGiveUp)
        {
            if (!IsOpen(_target))
            {
                if (mayGiveUp) Finish("saved file is not open: " + Short(_target), true);
                else { _posted = false; MonacoSpikeLog.Write("[active-doc] " + why + " but " + Short(_target) + " is not open yet -> waiting"); }
                return;
            }
            _selected = true;          // set BEFORE selecting: SelectWindow can pump messages and re-enter
            _selectedTick = Environment.TickCount;
            if (SamePath(ActivePath(), _target)) { MonacoSpikeLog.Write("[active-doc] saved file already active"); return; }
            MonacoSpikeLog.Write("[active-doc] " + why + " (" + _viewsSeen + " views) -> selecting " + Short(_target) + " (was " + Short(ActivePath()) + ")");
            SelectTarget();
        }

        private static void SelectTarget()
        {
            try
            {
                var w = ICSharpCode.SharpDevelop.FileService.GetOpenFile(_target);
                if (w != null) w.SelectWindow();
                else MonacoSpikeLog.Write("[active-doc] GetOpenFile returned null for " + Short(_target));
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] SelectWindow failed: " + ex.Message); }
        }

        private static bool IsOpen(string path)
        {
            try { return ICSharpCode.SharpDevelop.FileService.IsOpen(path); } catch { return false; }
        }

        // ── active document path (reflection: view-content shape differs across IDE builds) ─────────

        /// <summary>Records the displayed tab, unless a close froze recording or a restore is running. While a
        /// give-up holds recording, only a tab change that is not part of a reopen burst counts: it is judged after
        /// the current synchronous work (a reopen loop shows each tab just before or after its ViewOpened), and
        /// only when no view has opened within the quiet window by then.</summary>
        private static void Record(string path)
        {
            if (_restoring || _frozen || string.IsNullOrEmpty(path)) return;
            string key = Services.EditorService.GetOpenSolutionPath();
            if (string.IsNullOrEmpty(key)) return;
            if (!_held) { WriteSaved(key, path); return; }
            try
            {
                var form = WorkbenchSingleton.Workbench as Form;
                if (form == null || !form.IsHandleCreated) return;   // stay held: the next tab change tries again
                form.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        if (!_held || _restoring || _frozen) return;
                        if (Environment.TickCount - _lastViewTick < QuietMs) return;
                        if (!SamePath(Services.EditorService.GetOpenSolutionPath(), key)) return;
                        _held = false;
                        MonacoSpikeLog.Write("[active-doc] tab changed after the restore gave up -> recording resumed");
                        WriteSaved(key, path);
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private static string ActivePath()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench;
                object aw = Prop(wb, "ActiveWorkbenchWindow");
                if (aw == null) return null;
                // ClarionEditor THROWS on ViewContent.FileName; the window's ToolTipText carries the full path
                // (same strategy order as EditorService.GetActiveDocumentPath, which get_active_file relies on).
                string tip = Prop(aw, "ToolTipText") as string;
                if (!string.IsNullOrEmpty(tip) && File.Exists(tip)) return tip;
                foreach (string vcName in new[] { "ViewContent", "ActiveViewContent" })
                {
                    object vc = Prop(aw, vcName);
                    if (vc == null) continue;
                    foreach (string fnName in new[] { "PrimaryFileName", "FileName", "TitleName" })
                    {
                        object fn = Prop(vc, fnName);
                        string s = fn == null ? null : fn.ToString();
                        if (!string.IsNullOrEmpty(s) && File.Exists(s)) return s;
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return (p != null && p.GetIndexParameters().Length == 0) ? p.GetValue(o, null) : null;
            }
            catch { return null; }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { a = Path.GetFullPath(a); b = Path.GetFullPath(b); } catch { }
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static string Short(string p)
        {
            if (string.IsNullOrEmpty(p)) return "(none)";
            try { return Path.GetFileName(p); } catch { return p; }
        }

        // ── persistence: one "solutionPath<TAB>activeFile" line per solution ────────────────────────

        private static string StorePath
        {
            get { return Path.Combine(MonacoSpikeLog.DataDir, "active-documents.txt"); }
        }

        private static string ReadSaved(string solution)
        {
            if (string.IsNullOrEmpty(solution)) return null;
            try
            {
                if (!File.Exists(StorePath)) return null;
                foreach (var line in File.ReadAllLines(StorePath))
                {
                    int t = line.IndexOf('\t');
                    if (t > 0 && SamePath(line.Substring(0, t), solution)) return line.Substring(t + 1);
                }
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] read failed: " + ex.Message); }
            return null;
        }

        private static void WriteSaved(string solution, string active)
        {
            string entry = solution + "\t" + active;
            if (string.Equals(entry, _lastWritten, StringComparison.OrdinalIgnoreCase)) return;   // tab switch back and forth: no rewrite
            string tmp = null;
            try
            {
                MonacoSpikeLog.EnsureDir();
                var lines = new List<string>();
                bool replaced = false;
                if (File.Exists(StorePath))
                {
                    foreach (var line in File.ReadAllLines(StorePath))
                    {
                        int t = line.IndexOf('\t');
                        if (t > 0 && SamePath(line.Substring(0, t), solution))
                        {
                            if (!replaced) { lines.Add(entry); replaced = true; }
                        }
                        else lines.Add(line);
                    }
                }
                if (!replaced) lines.Add(entry);

                // Write beside the store, then swap it in, so a failed write never loses the other solutions' lines.
                // The temp name is per process (two IDE instances share the store); a failed swap still removes it.
                tmp = StorePath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray());
                // Another process (a virus scanner, a search indexer, the other IDE instance reading it) can hold the
                // store for a moment, and the swap then fails with "Unable to remove the file to be replaced". Retry
                // briefly rather than drop the developer's choice; the last attempt's error still reaches the log.
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(StorePath)) File.Replace(tmp, StorePath, null);
                        else File.Move(tmp, StorePath);
                        break;
                    }
                    catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(25 * attempt); }
                    catch (UnauthorizedAccessException) when (attempt < 4) { System.Threading.Thread.Sleep(25 * attempt); }
                }
                _lastWritten = entry;
                MonacoSpikeLog.Write("[active-doc] recorded active: " + Short(active));
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[active-doc] write failed: " + ex.Message); }
            finally { try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }
}
