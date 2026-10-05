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
        /// <summary>
        /// f64ba833: make sure a redirection file is loaded before EmbedLspContext.TryCapture looks for the
        /// generated module. RedFileService.Active was loaded only by the CA chat panel, so with no chat tab
        /// (or before it loaded) the lookup had no .red: on v61PRM004 the module lives in .\Source (from
        /// CLARION120.red's "*.clw = .\Source"), and capture failed with "module 'PRM002023.clw' not found
        /// beside ...PRM002.app or via the .red". The embeditor then had no real module path: no CodeGraph
        /// DB for its local layer and a synthetic document for the LSP. Loads the IDE's effective version's
        /// .red for the open solution, exactly as the chat panel does.
        ///
        /// Keyed on WHICH .red should be in force (effective version, its .red path, solution folder), not on
        /// "is one loaded": the chat panel was also the only thing that reloaded the .red after Build > Set
        /// Clarion Version, so without a chat tab a version or solution switch would keep the old file.
        /// Re-resolving the version is an XML parse, once per embed open. LspAutostartCommand also calls it
        /// when the IDE's version moves (905928c7), so the .red follows a switch before the next embed open.
        ///
        /// A failed load here leaves no stale .red in force (LoadForProject fails closed, f3b47441), and
        /// <see cref="RetryRedirectionIfDue"/> tries again from the 5 s tick until one loads here, or another
        /// caller (the chat panel, the Data pad) has put this version's .red in force.
        /// </summary>
        private static string _redKey;

        /// <summary>True while this launcher's last attempt loaded nothing and no .red for the effective
        /// version is in force; cleared by a successful load, here or by another caller.</summary>
        private static bool _redLoadFailed;
        private static int _redLastAttemptTick;
        private static string _redLastOutcome;
        private const int RedRetryIntervalMs = 30000;

        internal static void EnsureRedirectionLoaded()
        {
            ClarionVersionConfig cfg = null;
            try
            {
                cfg = EffectiveClarionVersion.CurrentConfig();
                string sln = EditorService.GetOpenSolutionPath();
                string slnDir = string.IsNullOrEmpty(sln) ? null : System.IO.Path.GetDirectoryName(sln);
                string key = (cfg != null ? cfg.Name + "|" + cfg.RedFilePath : "none") + "|" + slnDir;
                if (RedFileService.Active != null && string.Equals(key, _redKey, StringComparison.OrdinalIgnoreCase)) return;

                string was = RedFileService.Active != null ? RedFileService.Active.RedFilePath : "none";
                var red = new RedFileService();
                bool ok = red.LoadForProject(slnDir, cfg);   // makes it Active; a failure clears a stale Active
                _redKey = ok ? key : null;
                // Another caller may already hold THIS version's .red (LoadForProject kept it): then there is
                // nothing to recover, and retrying would only repeat this caller's failure.
                var active = RedFileService.Active;
                bool covered = !ok && active != null && cfg != null
                    && string.Equals(active.LoadedForVersion, cfg.Name, StringComparison.OrdinalIgnoreCase);
                _redLoadFailed = !ok && !covered;
                _redLastAttemptTick = Environment.TickCount;

                // One line per outcome, not per attempt: a retry that fails the same way stays quiet. A
                // solution switch that lands on the same version and the same .red is deliberately not logged.
                string outcome = (cfg != null ? cfg.Name : "not resolved") + "|"
                    + (ok ? red.RedFilePath : covered ? "kept " + active.RedFilePath : "nothing loaded");
                if (string.Equals(outcome, _redLastOutcome, StringComparison.OrdinalIgnoreCase)) return;
                _redLastOutcome = outcome;
                string from = cfg != null && !string.IsNullOrEmpty(cfg.RedFilePath) ? " from " + cfg.RedFilePath : "";
                MonacoSpikeLog.Write("[embed-ctx] .red for version '" + (cfg != null ? cfg.Name : "not resolved") + "': "
                    + (ok ? red.RedFilePath + " (was " + was + ")"
                       : covered ? "nothing loaded" + from + "; keeping " + active.RedFilePath + " (same version, loaded by another caller)"
                       : "nothing loaded" + from + "; no .red in force (was " + was + "), retrying every " + (RedRetryIntervalMs / 1000) + " s"));
            }
            catch (Exception ex)
            {
                // Fail closed here too (f3b47441 pipeline run 1): an exception is a failed load, and it must
                // leave recovery scheduled rather than the previous version's .red in force.
                _redKey = null;
                _redLoadFailed = true;
                _redLastAttemptTick = Environment.TickCount;
                try { RedFileService.ClearActiveUnlessFor(cfg != null ? cfg.Name : null); } catch { }
                MonacoSpikeLog.Write("[embed-ctx] .red load failed: " + ex.Message + "; retrying every " + (RedRetryIntervalMs / 1000) + " s");
            }
        }

        /// <summary>
        /// Called from LspAutostartCommand's 5 s tick: after a failed load (a .red missing, or held open by
        /// an editor), try again at most every 30 s so it recovers without waiting for the next embed open or
        /// version switch. A no-op until a load has failed. UI thread.
        /// </summary>
        internal static void RetryRedirectionIfDue()
        {
            if (!_redLoadFailed) return;
            if (unchecked(Environment.TickCount - _redLastAttemptTick) < RedRetryIntervalMs) return;
            EnsureRedirectionLoaded();
        }

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

                // #56: capture the real-module LSP context NOW — PweeEditorDetails only exists while the
                // native embeditor is open, and CancelEmbeditor below tears it down.
                EmbedLspContext lspCtx = null;
                EnsureRedirectionLoaded();   // f64ba833
                try { lspCtx = EmbedLspContext.TryCapture(appTree); } catch { }
                MonacoSpikeLog.Write("[embed-ctx] " + (EmbedLspContext.LastCaptureResult ?? "?"));   // f64ba833: a failed capture used to be silent

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
                EmbedLspContext capLspCtx = lspCtx;
                ctx.Post(_ =>
                {
                    try
                    {
                        var view = new ModernEmbeditorViewContent(capProc, capSrc, capRanges, "clarion", capDark, capProc,
                            false, 0, capLspCtx);
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

                // d4635694 idempotence guard — a DUPLICATE trigger for the SAME still-open embed (the monitor
                // re-firing on tab-away/tab-back) must no-op BEFORE the release below: ReleaseLiveInstanceSync
                // would discard the live overlay's unsaved Monaco buffer (John's lost-edits repro). Same pwee
                // identity + overlay still docked on this host = same embed, nothing to attach.
                var openPwee = appTree.GetOpenPweeDetails();
                if (ModernEmbeditorViewContent.IsLiveOverlayCurrent(appTree.GetClaGenEditorHost(), openPwee))
                    return "live overlay already current for this embed (duplicate trigger) — kept as-is";

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
                bool gotSource;
                using (MemoryHeadroom.Phase("M1 readSource"))   // 1d8d1c49
                    gotSource = EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out ferr);
                if (!gotSource)
                { RemoveCover(preCover); return "Could not read the open embed source: " + ferr; }
                MemoryHeadroom.MarkSettledSoon("embed chars=" + (source != null ? source.Length : 0));

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

                // #56: capture the real-module LSP context from the live embed (it stays open in overlay
                // mode, but capture eagerly for symmetry with the snapshot path).
                EmbedLspContext lspCtx = null;
                EnsureRedirectionLoaded();   // f64ba833
                try { lspCtx = EmbedLspContext.TryCapture(appTree); } catch { }
                MonacoSpikeLog.Write("[embed-ctx] " + (EmbedLspContext.LastCaptureResult ?? "?"));   // f64ba833: a failed capture used to be silent

                // Read the native caret position NOW (before the embed closes under us) so Monaco lands at the
                // embed point the developer had the cursor on — not the last-saved cursor for this procedure.
                int nativeLine = 0;
                try { nativeLine = EmbeditorCompletionService.GetNativeCaretLine(); } catch { }

                // Freeze-safe tail — dock on a settled turn. Keep WebView2 async init off any reentrant stack. The
                // pre-cover is already up over the host; ShowAsEmbedOverlay ADOPTS it (no second cover, no gap).
                var ctx = WindowsFormsSynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                string capProc = procName; string capSrc = source; List<int[]> capRanges = ranges; bool capDark = isDark;
                int capNativeLine = nativeLine;
                EmbedLspContext capLspCtx = lspCtx;
                var capHost = host; var capCover = preCover; var capEditor = appTree.GetOpenClaGenEditor();
                var capPwee = openPwee;
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
                        var overlay = new ModernEmbeditorViewContent(capProc, capSrc, capRanges, "clarion", capDark, capProc, false, capNativeLine, capLspCtx);
                        overlay.ShowAsEmbedOverlay(h, capEditor ?? new AppTreeService().GetOpenClaGenEditor(), capCover,
                            capPwee ?? new AppTreeService().GetOpenPweeDetails());
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
            if (!WaitForEmbedClosed(appTree, 3000))
            { error = "An embeditor is still open; close it and try again."; return false; }

            // a964cde3: the verified open. It brings the app tree forward (so it works while a Modern
            // Embeditor or CA Editor tab is the active document), types the name at a quick speed then a slower
            // one, checks the tree selection before clicking and the opened procedure's NAME after: a wrong
            // procedure is cancelled without saving. This replaced a "the source mentions the name" check that
            // a procedure merely CALLING the target passed.
            var opened = appTree.OpenProcedureEmbedChecked(procName, CharDelaysMs);
            if (!opened.Ok) { error = opened.Message; return false; }

            string title, ferr;
            if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out ferr))
            {
                try { appTree.CancelEmbeditor(); } catch { }
                WaitForEmbedClosed(appTree, 3000);
                error = "Could not read embed source for '" + procName + "': " + ferr;
                source = null; ranges = null;
                return false;
            }
            return true; // correct procedure — leave the embeditor open
            }
            finally { LeaveBusy(); }
        }

        /// <summary>
        /// Adopt an ALREADY-OPEN embeditor when it is showing <paramref name="procName"/>, mirroring its live
        /// buffer instead of demanding the caller close it first. A large procedure is exactly where a fresh
        /// open is unreliable, so the developer-opened editor is often the only handle that worked — refusing
        /// it makes the round-trip unusable in the case that needs it most.
        ///
        /// The identity check here is deliberately STRICTER than the post-open sanity check in
        /// <see cref="OpenAndMirror"/>. That one only asks whether the name appears anywhere in the source,
        /// which is sound after WE typed the name into the locator (a mis-select is the unlikely branch).
        /// Here the editor was opened by someone else, so a bare mention could just as well be a CALL to the
        /// target from an unrelated procedure. We therefore take the column-0 declaration via
        /// <see cref="ProcNameFromSource"/> and require an exact name match.
        ///
        /// It is also refused - see <see cref="EmbedAdoptPolicy"/>, which makes the decision - when the CA
        /// Embeditor (Monaco overlay or live tab) holds the embed, or when the native buffer has unsaved changes
        /// (or its dirty flag is unreadable): adoption ends in a save of the WHOLE buffer, so it must never
        /// persist edits that are not ours, nor write behind a Monaco buffer that would overwrite them.
        ///
        /// Returns true (with the mirror) only when adoption is safe. Returns false with <paramref name="error"/>
        /// set when an embeditor is open but may not be adopted — the caller should surface that rather than open
        /// anything. Returns false with <paramref name="error"/> null when NO embeditor is open, i.e. "carry on
        /// and open one". Never closes, cancels or writes: a refused editor is left exactly as the developer left
        /// it. UI thread only.
        /// </summary>
        internal static bool TryAdoptOpenEmbeditor(AppTreeService appTree, string procName,
            out string source, out List<int[]> ranges, out string error)
        {
            source = null; ranges = null; error = null;
            if (appTree == null || string.IsNullOrWhiteSpace(procName)) return false;

            // Nothing open → not an error, just nothing to adopt.
            bool open;
            try { open = appTree.GetEmbedInfo() != null; }
            catch { open = false; }

            string mirrored = null, openProc = null, readErr = null;
            List<int[]> mirroredRanges = null;
            bool caLive = false;
            bool? nativeDirty = null;
            if (open)
            {
                string title, ferr;
                if (!EmbeditorCompletionService.TryGetActiveEmbeditorSource(
                        out title, out mirrored, out mirroredRanges, out ferr))
                {
                    readErr = ferr ?? "unknown error";
                }
                else
                {
                    ICollection<string> knownProcs = null;
                    try { knownProcs = appTree.GetProcedureNames(); } catch { }
                    openProc = ProcNameFromSource(mirrored, knownProcs);
                }

                // Any CA Embeditor view holding the native embed (overlay OR live tab): its Monaco buffer,
                // not the native one, is what the developer edits and what its save writes.
                try { caLive = ModernEmbeditorViewContent.HasLiveOverlay; } catch { caLive = true; }
                nativeDirty = appTree.GetEmbeditorIsDirty();
            }

            switch (EmbedAdoptPolicy.Decide(open, readErr, openProc, procName, caLive, nativeDirty, out error))
            {
                case EmbedAdoptDecision.Adopt:
                    source = mirrored;
                    ranges = mirroredRanges;
                    return true;
                default:
                    return false;   // OpenFresh (error null) or Refuse (error set)
            }
        }

        /// <summary>
        /// Extract the authoritative procedure name from generated embed source (cardinal rule #7 — name from
        /// source, NEVER the temp pwee FileName/caption). MIRROR SCOPE IS SINGLE-PROC: the embeditor buffer is
        /// one procedure's assembled source (verified live — BrowseAuthors' buffer contained only its own
        /// "BrowseAuthors PROCEDURE" col-0 declaration), so the proc's own entry is the relevant declaration.
        ///
        /// Hardening (Eve): the regex is COLUMN-0 anchored with an UNDOTTED name — Clarion proc DEFINITIONS sit
        /// at column 1, while MAP prototypes, local-MAP helper prototypes, and ABC dotted method labels
        /// (ThisWindow.Init PROCEDURE — the class excludes '.', so it stops at the dot) are indented and/or
        /// dotted, so they're all excluded. We then ITERATE matches and return the first that is a KNOWN top-level proc
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
                // The ':' in the label class is load-bearing (GH #196): colon-separated labels
                // (reg:TENDER:GiftCard) are idiomatic Clarion and dominate template-generated app suites, and
                // without it the class stopped at the first colon, [ \t]+ never matched, and EVERY procedure in
                // such a suite failed name resolution → null → the caller's E6 abort, so the overlay never
                // attached. The dot stays OUT: that is what keeps ABC dotted method labels excluded (above).
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    source, @"(?m)^([A-Za-z_][A-Za-z0-9_:]*)[ \t]+PROCEDURE\b",
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
