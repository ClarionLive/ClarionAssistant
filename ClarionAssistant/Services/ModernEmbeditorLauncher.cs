using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;
using ClarionAssistant.Terminal;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Path B multi-editor: open a procedure's embed source in a Monaco view via the mirror+snapshot
    /// model. Clarion's native generator allows only ONE embeditor at a time, so for each procedure we:
    ///   1. OpenProcedureEmbed(name)  — native generation + Clarion's embeditor (transient)
    ///   2. mirror the live buffer (source + editable-region map)
    ///   3. CancelEmbeditor()         — discard/close to release the native single-embeditor lock
    ///   4. ShowView(new ModernEmbeditorViewContent)
    /// The snapshot lives in our own tab, so any number of procedures can be open at once.
    ///
    /// MUST run on the UI thread (OpenProcedureEmbed drives native focus + Application.DoEvents).
    /// Snapshots are read-only-of-truth for now; the save round-trip (re-open → write → save → close)
    /// is M2. If the .app is regenerated underneath, an open snapshot can go stale (reload to refresh).
    /// </summary>
    public static class ModernEmbeditorLauncher
    {
        /// <summary>Opens one procedure as a Monaco snapshot tab. Returns null on success, else an error message.</summary>
        public static string OpenProcedure(string procName, bool isDark)
        {
            if (string.IsNullOrWhiteSpace(procName)) return "No procedure specified.";
            EnterBusy();
            try
            {
                var appTree = new AppTreeService();

                string source, error;
                List<int[]> ranges;
                if (!OpenAndMirror(appTree, procName, out source, out ranges, out error))
                    return error;

                // ABC is loaded now (OpenAndMirror just opened the native embed, which triggers the lazy ABC
                // load). Warm the LSP HERE — deliberately not at picker-start — so its background solution parse
                // runs during the WebView2/Monaco load below rather than competing with the ABC load and
                // ~halving it. Idempotent, fire-and-forget.
                try { EmbeditorCompletionService.LspStarter?.Invoke(); } catch { }

                // OpenAndMirror leaves the embeditor open; we made no edits, so discard/close to free the lock.
                try { appTree.CancelEmbeditor(); } catch { }
                WaitForEmbedClosed(appTree, 3000);

                // CRITICAL — do NOT create the WebView2 view on THIS call stack. We are still unwinding the
                // nested Application.DoEvents() pumps that drove the native embeditor (SetFocus / AttachThreadInput
                // / WM_CHAR / BM_CLICK in OpenProcedureEmbed). WebView2's EnsureCoreWebView2Async — kicked off by
                // ShowView -> Panel.HandleCreated -> async OnHandleCreated — needs a SETTLED, non-reentrant
                // message-loop turn to complete; created on this reentrant/unsettled stack its await continuation
                // can't progress and the whole IDE hard-hangs. (This is the freeze: a manual idle GAP before
                // opening avoided it; ABC warmth was a red herring.) Post ShowView so this entire stack unwinds
                // and the message/input state drains first — deterministically reproducing that gap.
                var ctx = WindowsFormsSynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                string capProc = procName; string capSrc = source; List<int[]> capRanges = ranges; bool capDark = isDark;
                ctx.Post(_ =>
                {
                    try
                    {
                        var view = new ModernEmbeditorViewContent(capProc, capSrc, capRanges, "clarion", capDark, capProc);
                        WorkbenchSingleton.Workbench.ShowView(view);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[ModernEmbeditorLauncher] deferred ShowView failed: " + ex.Message);
                    }
                }, null);
                return null;
            }
            finally { LeaveBusy(); }
        }

        /// <summary>
        /// PHASE 3 (ticket 4b82f1de) — open the procedure CURRENTLY committed in the app tree (selected by
        /// the proc-tree right-click, F3) as a Monaco snapshot tab, with NO proc name supplied up front.
        /// Flow: open+mirror the committed selection (no locator typing → no type-miss → no retry) → derive
        /// the authoritative name via source-regex (cardinal rule #7 — NOT the temp pwee FileName/caption) →
        /// CancelEmbeditor → deferred ShowView (the SAME freeze-safe tail as <see cref="OpenProcedure"/>).
        /// Returns null on success, else an error string. MUST run on the managed UI thread.
        ///
        /// Guards: E3 (already-open Monaco tab for this proc → focus it, no duplicate); E6 (name
        /// unrecoverable from source → CancelEmbeditor + NO tab + return error; never a blank/wrong proc).
        /// </summary>
        public static string OpenCommittedSelection(bool isDark, bool live = false)
        {
            EnterBusy();
            try
            {
                var appTree = new AppTreeService();

                // LIVE mode (ticket a5bbf005): the previous foreground tab may STILL hold a native embed open
                // (only one embed can be open at a time). Release it synchronously BEFORE we open the new one,
                // or OpenAndMirrorCurrentSelection's WaitForEmbedClosed guard would error. Safe to pump here —
                // this runs on the launch delegate (off-stack), not inside an active-view-changed event.
                if (live) { try { ModernEmbeditorViewContent.ReleaseLiveInstanceSync(); } catch { } }

                string source, error;
                List<int[]> ranges;
                if (!OpenAndMirrorCurrentSelection(appTree, out source, out ranges, out error))
                    return error;

                // E6 — authoritative name from the mirrored SOURCE (cardinal rule #7), never the temp pwee
                // FileName/caption. Iterate the col-0 "Name PROCEDURE" declarations and take the first that is a
                // KNOWN top-level procedure (validates the regex pick against App.ProcedureNames).
                List<string> knownProcs = null;
                try { knownProcs = appTree.GetProcedureNames(); } catch { }
                string procName = ProcNameFromSource(source, knownProcs);
                if (string.IsNullOrWhiteSpace(procName))
                {
                    // E6: no confident identity → abort cleanly, no tab.
                    try { appTree.CancelEmbeditor(); } catch { }
                    WaitForEmbedClosed(appTree, 3000);
                    return "Could not determine the procedure name from the embed source — no tab opened.";
                }

                // Dup-name limitation (Eve #5): if the app declares the same proc name in more than one module,
                // source-regex has no module context to disambiguate → the tab may reflect the wrong module's
                // proc. Acceptable for v1; log it so it's diagnosable (future fix = embeditor window caption).
                if (CountIgnoreCase(knownProcs, procName) > 1)
                    System.Diagnostics.Debug.WriteLine(
                        "[ModernEmbeditorLauncher] '" + procName + "' is a duplicate proc name across modules — "
                        + "source-regex cannot disambiguate the module; may open the wrong module's tab.");

                // E3 — a Monaco tab for this proc is already open: focus it, discard the redundant native
                // open, and do NOT open a duplicate. (h2: the ~2.6s open is unavoidable here — no name to
                // dedup on before opening — so we eat it once and log nothing user-facing.)
                if (ModernEmbeditorViewContent.TryFocusExisting(procName))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    WaitForEmbedClosed(appTree, 3000);
                    System.Diagnostics.Debug.WriteLine(
                        "[ModernEmbeditorLauncher] '" + procName + "' already open — focused existing tab (redundant ~2.6s open).");
                    return null;
                }

                // ABC is loaded now (the native open triggered the lazy load). Warm the LSP HERE so its
                // background solution parse overlaps the WebView2/Monaco load below. Idempotent, fire-and-forget.
                try { EmbeditorCompletionService.LspStarter?.Invoke(); } catch { }

                // LIVE mode: do NOT cancel — leave the native embed OPEN + locked so save can write straight
                // back into it (no re-open). SNAPSHOT mode (default): discard/close to free the single-embeditor
                // lock so any number of Monaco tabs can be open at once.
                if (!live)
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    WaitForEmbedClosed(appTree, 3000);
                }

                // CRITICAL freeze-safe tail — identical to OpenProcedure: do NOT create the WebView2 view on
                // THIS call stack (still unwinding the native open's nested DoEvents pumps). Post ShowView so
                // the stack unwinds and the message/input state drains first, or WebView2's async init can't
                // progress and the IDE hard-hangs. (In live mode the embed stays open across this Post; the
                // freeze is about stack reentrancy, not the embed being open, so the deferral still covers it.)
                var ctx = WindowsFormsSynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                string capProc = procName; string capSrc = source; List<int[]> capRanges = ranges; bool capDark = isDark; bool capLive = live;
                ctx.Post(_ =>
                {
                    try
                    {
                        // LIVE mode (ticket a5bbf005): the native embed is still OPEN (the live path above skipped the
                        // cancel). Dock the Monaco surface as an in-place OVERLAY on top of the embeditor's host panel
                        // (ClaGenEditor.Control, per CC's probe) instead of opening a separate workbench tab — that's
                        // what keeps the native embeditor open with Monaco floating over it, one document, no
                        // switch-away flash. Fall back to the separate-tab path if the host can't be resolved.
                        if (capLive)
                        {
                            var at = new AppTreeService();
                            var host = at.GetClaGenEditorHost();
                            if (host != null)
                            {
                                var overlay = new ModernEmbeditorViewContent(capProc, capSrc, capRanges, "clarion", capDark, capProc, false);
                                overlay.ShowAsEmbedOverlay(host, at.GetOpenClaGenEditor());
                                return;
                            }
                            System.Diagnostics.Debug.WriteLine("[ModernEmbeditorLauncher] live overlay: no ClaGenEditor host resolved — falling back to a separate tab.");
                        }

                        var view = new ModernEmbeditorViewContent(capProc, capSrc, capRanges, "clarion", capDark, capProc, capLive);
                        WorkbenchSingleton.Workbench.ShowView(view);
                        // Separate-tab fallback: ShowView lands the tab in the BACKGROUND. Foreground it (also arms the
                        // switch-away watch, see the probe fix) via SelectWindow on a settled turn.
                        if (capLive) view.ActivateTab();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[ModernEmbeditorLauncher] deferred ShowView (committed) failed: " + ex.Message);
                    }
                }, null);
                return null;
            }
            finally { LeaveBusy(); }
        }

        /// <summary>
        /// Force the IDE's lazy ABC class load to happen NOW (in isolation) so the user's first Modern
        /// Embeditor open doesn't pay it concurrently with the WebView2 open — the conflict that freezes
        /// Clarion. Reuses the proven open/cancel pattern: open the first procedure's native embeditor
        /// (whose source generation loads ABC), then CancelEmbeditor + WaitForEmbedClosed (which pumps
        /// Application.DoEvents so the native view actually tears down — the step my earlier raw cancel
        /// missed). NO Monaco view, NO WebView2 — that's what keeps it freeze-free. UI thread only.
        /// Returns a short diagnostic. Safe to call once per app load (guarded by IsBusy).
        /// </summary>
        public static string WarmupAbc()
        {
            EnterBusy();
            try
            {
                var appTree = new AppTreeService();
                var procs = appTree.GetProcedureNames();
                if (procs == null || procs.Count == 0)
                    return "ABC warmup skipped: no procedures in the open app.";
                string proc = procs[0];

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string source, error;
                List<int[]> ranges;
                bool opened = OpenAndMirror(appTree, proc, out source, out ranges, out error);

                // Always close + WAIT (pumps DoEvents until GetEmbedInfo()==null) so the native embeditor
                // actually tears down — whether or not the mirror read succeeded, the open itself loads ABC.
                try { appTree.CancelEmbeditor(); } catch { }
                bool closed = WaitForEmbedClosed(appTree, 5000);
                appTree.ActivateAppView();
                sw.Stop();

                if (!opened)
                    return "ABC warmup: open of '" + proc + "' had trouble (" + error + ") after "
                           + sw.ElapsedMilliseconds + "ms; ABC may still have loaded. closed=" + closed;
                return "ABC warmup OK via '" + proc + "' — embed opened+closed in "
                       + sw.ElapsedMilliseconds + "ms (closed=" + closed + ").";
            }
            finally { LeaveBusy(); }
        }

        // Locator typing speed (ms/char): a quick first pass, then a slower, very reliable retry.
        // ClaList drops keystrokes typed too fast, so if the quick pass selects the wrong procedure
        // the verify step below catches it and we retry slower.
        private static readonly int[] CharDelaysMs = { 70, 130 };

        /// <summary>
        /// Reliably open the procedure's embeditor and mirror its source + editable-range map, leaving the
        /// embeditor OPEN on success (caller mirrors/edits then closes). Types the name into the locator at a
        /// quick speed first; if the WRONG procedure opened (keystrokes dropped), closes and retries slower.
        /// Verifies the opened source actually belongs to the procedure, so we never proceed on a mis-selected
        /// one. UI thread only.
        /// </summary>
        /// <summary>&gt;0 while an embeditor open/save is driving the IDE — pads should not auto-refresh then.</summary>
        private static int _busyCount;
        public static bool IsBusy { get { return _busyCount > 0; } }

        internal static void EnterBusy() { System.Threading.Interlocked.Increment(ref _busyCount); }
        internal static void LeaveBusy() { System.Threading.Interlocked.Decrement(ref _busyCount); }

        internal static bool OpenAndMirror(AppTreeService appTree, string procName,
            out string source, out List<int[]> ranges, out string error)
        {
            source = null; ranges = null; error = null;
            EnterBusy();
            try
            {
            for (int attempt = 0; attempt < CharDelaysMs.Length; attempt++)
            {
                if (!WaitForEmbedClosed(appTree, 3000))
                { error = "An embeditor is still open; close it and try again."; return false; }

                // Bring the app tree to the front so the native automation works even when a Modern
                // Embeditor tab is the active document.
                appTree.ActivateAppView();
                appTree.OpenProcedureEmbed(procName, CharDelaysMs[attempt]);

                // First open loads the ABC libraries and can take many seconds; wait generously.
                if (!WaitForEmbedOpen(appTree, 45000))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    error = "Embeditor did not open for '" + procName + "' within 45s.";
                    continue;
                }

                string title, ferr;
                if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out ferr))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    error = "Could not read embed source for '" + procName + "': " + ferr;
                    continue;
                }

                if (SourceMentionsProcedure(source, procName))
                    return true; // correct procedure — leave the embeditor open

                // Wrong procedure: keystrokes were dropped at this speed. Close and retry slower.
                try { appTree.CancelEmbeditor(); } catch { }
                error = "Opened a different procedure than '" + procName + "' — the locator search missed.";
                source = null; ranges = null;
            }
            return false;
            }
            finally { LeaveBusy(); }
        }

        /// <summary>
        /// PHASE 3 variant of <see cref="OpenAndMirror"/>: open the embeditor for the ALREADY-committed
        /// app-tree selection (the right-click selected the row, F3 — no name to type) and mirror its source
        /// + editable-range map, leaving the embeditor OPEN on success. Because nothing is typed there is no
        /// type-miss failure mode, so there is NO retry loop and NO SourceMentionsProcedure verify (there's
        /// no name to verify against — the committed selection is ground truth; the caller derives the name
        /// from the mirrored source). UI thread only.
        /// </summary>
        internal static bool OpenAndMirrorCurrentSelection(AppTreeService appTree,
            out string source, out List<int[]> ranges, out string error)
        {
            source = null; ranges = null; error = null;
            EnterBusy();
            try
            {
                if (!WaitForEmbedClosed(appTree, 3000))
                { error = "An embeditor is still open; close it and try again."; return false; }

                // Bring the app tree to the front so the native automation works even when a Modern
                // Embeditor tab is the active document.
                appTree.ActivateAppView();
                appTree.OpenProcedureEmbedCurrentSelection();

                // First open loads the ABC libraries and can take many seconds; wait generously.
                if (!WaitForEmbedOpen(appTree, 45000))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    error = "Embeditor did not open for the committed selection within 45s.";
                    return false;
                }

                string title, ferr;
                if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out ferr))
                {
                    try { appTree.CancelEmbeditor(); } catch { }
                    error = "Could not read embed source for the committed selection: " + ferr;
                    source = null; ranges = null;
                    return false;
                }
                return true; // leave the embeditor open — caller mirrors then closes
            }
            finally { LeaveBusy(); }
        }

        /// <summary>
        /// Extract the authoritative procedure name from generated embed source (cardinal rule #7 — name from
        /// source, NEVER the temp pwee FileName/caption). MIRROR SCOPE IS SINGLE-PROC: the embeditor buffer is
        /// one procedure's assembled source (verified live — BrowseAuthors' buffer contained only its own
        /// "BrowseAuthors PROCEDURE" col-0 declaration), so the proc's own entry is the relevant declaration.
        ///
        /// Hardening (Eve): the regex is COLUMN-0 anchored with a BARE name — Clarion proc DEFINITIONS sit at
        /// column 1, while MAP prototypes, local-MAP helper prototypes, and ABC dotted method labels
        /// (ThisWindow.Init PROCEDURE — <c>\w+</c> stops at the dot) are indented and/or dotted, so they're all
        /// excluded. We then ITERATE matches and return the first that is a KNOWN top-level proc
        /// (<paramref name="knownProcs"/> = App.ProcedureNames) — turning the fragile "first match" into a
        /// validated pick that rejects stray tokens / class names / local procs. If a known-proc list is
        /// supplied and NOTHING validates → returns null → caller treats as E6 (abort, no tab). With no list
        /// (degraded), falls back to the first col-0 match. Returns null when no declaration is found at all.
        ///
        /// KNOWN LIMITATION (v1): source-regex carries no module context, so it CANNOT disambiguate duplicate
        /// proc names across modules (two "Process" procs → possible wrong-module tab). The embeditor WINDOW
        /// caption 'Proc - Embeditor - (module.clw)' is the documented future fix. Caller logs dup-name hits.
        /// </summary>
        internal static string ProcNameFromSource(string source, ICollection<string> knownProcs)
        {
            if (string.IsNullOrEmpty(source)) return null;
            try
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    source, @"(?m)^([A-Za-z_][A-Za-z0-9_]*)[ \t]+PROCEDURE\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                string firstRaw = null;
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string name = m.Groups[1].Value;
                    if (firstRaw == null) firstRaw = name;
                    if (knownProcs != null && ContainsIgnoreCase(knownProcs, name))
                        return name;   // first match that IS a real top-level procedure — reliable pick
                }

                // No validated match: degrade to the first col-0 match ONLY when we had no list to validate
                // against; if we DID have the list and nothing matched, that's E6 (return null → abort).
                return (knownProcs == null || knownProcs.Count == 0) ? firstRaw : null;
            }
            catch { return null; }
        }

        private static bool ContainsIgnoreCase(ICollection<string> names, string name)
        {
            if (names == null) return false;
            foreach (var n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int CountIgnoreCase(ICollection<string> names, string name)
        {
            if (names == null) return 0;
            int c = 0;
            foreach (var n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) c++;
            return c;
        }

        /// <summary>
        /// Sanity check that the assembled embed source belongs to the procedure: its own name appears in
        /// its generated source (e.g. "Name PROCEDURE"), so if it's absent we almost certainly opened the
        /// wrong procedure.
        /// </summary>
        private static bool SourceMentionsProcedure(string source, string procName)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrWhiteSpace(procName)) return false;
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"\b" + System.Text.RegularExpressions.Regex.Escape(procName) + @"\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return source.IndexOf(procName, StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        // The native embed/ABC open + close are driven by the UI-thread MESSAGE LOOP. The old coarse
        // Sleep(50)-per-iteration starved that loop (we slept ~50ms of every tick), inflating a ~2s native
        // open to ~19s and leaving the embed half-settled for the close. These now pump Application.DoEvents()
        // at full speed (like the native click path) and only run the reflection-heavy GetEmbedInfo() poll
        // ~every 120ms, with a 1ms yield to avoid a 100% busy-spin.
        private const int EmbedPollIntervalMs = 120;

        // Shared pump: dispatch queued messages and block EFFICIENTLY until `condition` is true or timeout.
        // Replaces the old DoEvents + Thread.Sleep(1) spin: Sleep(1) actually sleeps ~15.6ms at the default
        // Windows timer resolution, so each native-generate step that posts-then-yields waited up to ~15ms for
        // our next pump — throttling the cold ABC generate vs the IDE's own GetMessage loop (the native ~2s
        // baseline). MsgWaitForMultipleObjectsEx wakes IMMEDIATELY when a message is queued (native-like latency)
        // and otherwise blocks/yields CPU (no busy-spin) until the next poll is due, so we still re-check
        // `condition` ~every EmbedPollIntervalMs. UI thread only.
        internal static bool PumpUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastPoll = -EmbedPollIntervalMs;
            bool met = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();                          // dispatch whatever woke us
                if (sw.ElapsedMilliseconds - lastPoll >= EmbedPollIntervalMs)
                {
                    lastPoll = sw.ElapsedMilliseconds;
                    if (condition()) { met = true; break; }      // GetEmbedInfo is reflection-heavy — keep it gated
                }
                long sincePoll = sw.ElapsedMilliseconds - lastPoll;
                uint wait = (uint)Math.Max(1, EmbedPollIntervalMs - sincePoll);
                // MWMO_INPUTAVAILABLE: return even for input already queued (can't miss a wake); QS_ALLINPUT: any msg.
                MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, wait, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            }
            if (!met) met = condition();
            return met;
        }

        internal static bool WaitForEmbedOpen(AppTreeService appTree, int timeoutMs)
        {
            return PumpUntil(() => appTree.GetEmbedInfo() != null, timeoutMs);
        }

        internal static bool WaitForEmbedClosed(AppTreeService appTree, int timeoutMs)
        {
            return PumpUntil(() => appTree.GetEmbedInfo() == null, timeoutMs);
        }

        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
        private const uint QS_ALLINPUT = 0x04FF;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;
    }
}
