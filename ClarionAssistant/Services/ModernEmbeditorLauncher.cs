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
        /// TICKET 4d16b53a — the poll-detect entry. Attach the live Monaco overlay to an embed that is
        /// ALREADY OPEN in the native embeditor (opened via Clarion's own "Embeditor Source" menu — we did
        /// NOT open it). Unlike <see cref="OpenCommittedSelection"/> there is NO native open here: the embed
        /// is live, so we only MIRROR it (TryGetActiveEmbeditorSource reads the ACTIVE embed) and dock the
        /// overlay onto the editor's host panel. Returns null on success, else an error string. MUST run on
        /// the managed UI thread.
        ///
        /// Structural parity with our BM_CLICK path proven live (CC probe, 4d16b53a): a Clarion-native-opened
        /// embed reads identically on all 3 axes (TryGetActiveEmbeditorSource, host resolution, SaveLive
        /// CustomLines target). Single-live-overlay: release any prior overlay first. E3: a Monaco surface
        /// already open for this proc → focus it, don't double-attach. E6: name unrecoverable → abort, no overlay.
        /// </summary>
        public static string AttachOverlayToOpenEmbed(bool isDark)
        {
            EnterBusy();
            System.Windows.Forms.Panel preCover = null;
            try
            {
                var appTree = new AppTreeService();

                // Nothing to attach to if no native embed is currently open.
                if (appTree.GetOpenClaGenEditor() == null)
                    return "No native embeditor is open.";

                // Single-live-overlay (a5bbf005): release the previous overlay's hold before attaching a new one.
                // cancelOpenEmbed:FALSE is load-bearing — the currently-open native embed is the one the developer
                // just opened via Clarion's own menu and is our DOCK TARGET; the default (true) would CancelEmbeditor
                // it and close the very embed we're attaching to. This only detaches a stale prior overlay.
                try { ModernEmbeditorViewContent.ReleaseLiveInstanceSync(cancelOpenEmbed: false); } catch { }

                // PRE-COVER FIRST (4d16b53a flicker): synchronously drop an opaque cover over the embed host NOW —
                // before the mirror + WebView2 work — so the native text area is hidden before it can paint. When
                // driven by the ActiveViewContentChanged event this runs in the posted turn that precedes WM_PAINT,
                // so the native text never shows. Cheap WinForms, no WebView2 → freeze-safe. Removed if we abort.
                var host = appTree.GetClaGenEditorHost();
                if (host != null) { try { preCover = ModernEmbeditorViewContent.AddInstantCover(host, isDark); } catch { } }

                // MIRROR the already-open embed — the KEY difference vs OpenCommittedSelection: no open, just read
                // the ACTIVE embed's assembled source + editable-range map (Document.CustomLineManager.CustomLines).
                string title, source, ferr;
                List<int[]> ranges;
                if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out ferr))
                { RemoveCover(preCover); return "Could not read the open embed source: " + ferr; }

                // Authoritative proc name from source (cardinal rule #7 — never the temp pwee FileName/caption).
                List<string> knownProcs = null;
                try { knownProcs = appTree.GetProcedureNames(); } catch { }
                string procName = ProcNameFromSource(source, knownProcs);
                if (string.IsNullOrWhiteSpace(procName))
                { RemoveCover(preCover); return "Could not determine the procedure name from the embed source — no overlay attached."; }

                // E3 — already overlaid/open for this proc: focus it, don't double-attach.
                if (ModernEmbeditorViewContent.TryFocusExisting(procName))
                { RemoveCover(preCover); return null; }

                // Warm the LSP now so its background parse overlaps the WebView2/Monaco load. Fire-and-forget.
                try { EmbeditorCompletionService.LspStarter?.Invoke(); } catch { }

                // Freeze-safe tail — dock on a settled turn. Keep WebView2 async init off any reentrant stack. The
                // pre-cover is already up over the host; ShowAsEmbedOverlay ADOPTS it (no second cover, no gap).
                var ctx = WindowsFormsSynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                string capProc = procName; string capSrc = source; List<int[]> capRanges = ranges; bool capDark = isDark;
                var capHost = host; var capCover = preCover; var capEditor = appTree.GetOpenClaGenEditor();
                ctx.Post(_ =>
                {
                    try
                    {
                        var h = capHost ?? new AppTreeService().GetClaGenEditorHost();
                        if (h == null)
                        {
                            RemoveCover(capCover);
                            System.Diagnostics.Debug.WriteLine("[ModernEmbeditorLauncher] AttachOverlayToOpenEmbed: no ClaGenEditor host — cannot overlay.");
                            return;
                        }
                        var overlay = new ModernEmbeditorViewContent(capProc, capSrc, capRanges, "clarion", capDark, capProc, false);
                        overlay.ShowAsEmbedOverlay(h, capEditor ?? new AppTreeService().GetOpenClaGenEditor(), capCover);
                    }
                    catch (Exception ex)
                    {
                        RemoveCover(capCover);
                        System.Diagnostics.Debug.WriteLine("[ModernEmbeditorLauncher] AttachOverlayToOpenEmbed deferred attach failed: " + ex.Message);
                    }
                }, null);
                return null;
            }
            finally { LeaveBusy(); }
        }

        /// <summary>Remove + dispose a pre-cover panel (ticket 4d16b53a) when the attach aborts before the overlay
        /// adopts it. Guarded — never throws into the attach path.</summary>
        private static void RemoveCover(System.Windows.Forms.Control cover)
        {
            if (cover == null) return;
            try { if (cover.Parent != null) cover.Parent.Controls.Remove(cover); cover.Dispose(); } catch { }
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
