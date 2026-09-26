using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// THE cross-addin entry point for "open a Clarion source file and scroll/position to a 1-based line"
    /// that works in BOTH overlay states — so a CALLER never has to know whether the Monaco overlay is on.
    ///
    /// Why this exists: with the Monaco source overlay ON, the stock <c>FileService.JumpToFilePosition</c>
    /// moves the HIDDEN native caret behind the WebView2 — a visual no-op. Other addins (e.g. the standalone
    /// ClarionDebugger) have NO compile-time reference to ClarionAssistant, so they call
    /// <see cref="NavigateToFileAndLine"/> by reflection. The signature is deliberately primitive and frozen:
    /// <c>(string filePath, int line, int column) -&gt; bool</c>, line/column 1-based, true = handled.
    ///
    /// Routing: the file's <see cref="MonacoClarionEditor"/> view content (which exists in BOTH overlay
    /// states — the flag only controls whether Monaco attaches) self-registers here. A navigation request
    /// either drives an already-live editor or is parked in <see cref="_pending"/> for the editor to pick up
    /// when it finishes capturing/loading — so the very first click after opening a cold file still lands.
    /// </summary>
    public static class MonacoSourceNavigator
    {
        private static readonly object _gate = new object();

        // file -> live editor (registered on capture for native mode, on page-ready for overlay mode).
        private static readonly Dictionary<string, MonacoClarionEditor> _live =
            new Dictionary<string, MonacoClarionEditor>(StringComparer.OrdinalIgnoreCase);

        // file -> {line, column} requested before the editor was live; the editor consumes it on load.
        private static readonly Dictionary<string, int[]> _pending =
            new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        private static string Norm(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try { return Path.GetFullPath(p); } catch { return p; }
        }

        /// <summary>
        /// Open <paramref name="filePath"/> in the IDE editor and position the caret at <paramref name="line"/>
        /// (1-based), scrolling it into view. Transparently handles the Monaco overlay (revealLineInCenter +
        /// setPosition) and the stock native editor (caret + ScrollTo). UI-thread-marshalled internally, so it
        /// is safe to call from any thread.
        /// </summary>
        /// <returns>true if ClarionAssistant handled the request (file opened + navigated, or queued to apply
        /// on the editor's load); false ONLY if it could not (empty path / file missing) — the caller should
        /// then fall back to its own navigation (e.g. FileService.JumpToFilePosition).</returns>
        public static bool NavigateToFileAndLine(string filePath, int line, int column)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string full = Norm(filePath);
            if (!File.Exists(full)) return false;
            if (line < 1) line = 1;
            if (column < 1) column = 1;

            var form = WorkbenchSingleton.Workbench as Form;
            if (form != null && form.InvokeRequired)
            {
                bool ok = true;
                try { form.Invoke(new Action(() => { ok = DoNavigate(full, line, column); })); }
                catch (Exception ex) { MonacoSpikeLog.Write("Navigator marshal error: " + ex.Message); ok = false; }
                return ok;
            }
            return DoNavigate(full, line, column);
        }

        // Always on the UI thread. Park the desired position FIRST, THEN open (or focus) the file, then
        // drive the editor if it is already live — otherwise it self-applies via ApplyPendingNavigation
        // on capture/ready.
        //
        // Ordering fix (2026-07-30): this used to call FileService.OpenFile() BEFORE writing _pending.
        // For a COLD open, OpenFile() drives the entire view-creation sequence — CaptureTick →
        // AttachOverlay → the WebView2 page's own "ready" round-trip — and that sequence reads _pending
        // for this exact file (MonacoClarionSourceEditor's OnReady, via TryConsumePending) to seed the
        // initial cursor position into the very first setSource payload. If any part of that chain
        // completes before OpenFile() returns, _pending was still empty at read time — the file opened at
        // whatever position it last had, silently dropping the requested line (confirmed via
        // monaco-spike.log: a cold open logged with no ", nav->line N" suffix, which the log line only
        // appends when a pending nav was actually found). Writing _pending before OpenFile() closes that
        // window entirely. Same failure class as the historical "memento-restore race" (PR #111).
        private static bool DoNavigate(string full, int line, int column)
        {
            MonacoClarionEditor liveBefore;
            lock (_gate)
            {
                _pending[full] = new[] { line, column };
                _live.TryGetValue(full, out liveBefore);
            }

            try { ICSharpCode.SharpDevelop.FileService.OpenFile(full); }
            catch (Exception ex)
            {
                MonacoSpikeLog.Write("Navigator OpenFile error: " + ex.Message);
                lock (_gate) { _pending.Remove(full); }   // don't leave a stale entry for a later unrelated open
                return false;
            }

            // If it was ALREADY live before OpenFile (a no-op re-open/focus), drive it directly — the
            // cold-open path above only fires for a file that wasn't loaded yet.
            if (liveBefore != null) liveBefore.ApplyPendingNavigation();
            return true;
        }

        /// <summary>A <see cref="MonacoClarionEditor"/> announces it is live for <paramref name="filePath"/>.</summary>
        internal static void Register(string filePath, MonacoClarionEditor editor)
        {
            string full = Norm(filePath);
            if (string.IsNullOrEmpty(full) || editor == null) return;
            lock (_gate) { _live[full] = editor; }
        }

        /// <summary>A <see cref="MonacoClarionEditor"/> is disposing; drop it (only if it's still the one mapped).</summary>
        internal static void Unregister(string filePath, MonacoClarionEditor editor)
        {
            string full = Norm(filePath);
            if (string.IsNullOrEmpty(full)) return;
            lock (_gate)
            {
                MonacoClarionEditor cur;
                if (_live.TryGetValue(full, out cur) && ReferenceEquals(cur, editor)) _live.Remove(full);
            }
        }

        /// <summary>
        /// Cross-addin entry point (reflection, same pattern as <see cref="NavigateToFileAndLine"/>): the
        /// ACTIVE document's live Monaco cursor position — file path + 1-based line/column — for callers
        /// (e.g. the standalone ClarionDebugger's "Run to cursor") that need "wherever the developer's
        /// cursor actually is right now" rather than a specific known file/line. Frozen contract:
        ///   bool ClarionAssistant.Services.MonacoSourceNavigator.TryGetActiveCursor(out string filePath, out int line, out int column)
        /// Returns false if the active workbench window isn't a Monaco-hosted Clarion source editor, or
        /// Monaco hasn't reported a cursor position yet (overlay just attached, before the first move) —
        /// callers should treat false as "no usable cursor", not retry blindly.
        /// </summary>
        public static bool TryGetActiveCursor(out string filePath, out int line, out int column)
        {
            string fp = null; int ln = 0, col = 0; bool ok = false;
            try
            {
                var form = WorkbenchSingleton.Workbench as Form;
                if (form != null && form.InvokeRequired)
                    form.Invoke(new Action(() => ok = TryGetActiveCursorOnUiThread(out fp, out ln, out col)));
                else
                    ok = TryGetActiveCursorOnUiThread(out fp, out ln, out col);
            }
            catch (Exception ex) { MonacoSpikeLog.Write("TryGetActiveCursor error: " + ex.Message); ok = false; }
            filePath = fp; line = ln; column = col;
            return ok;
        }

        private static bool TryGetActiveCursorOnUiThread(out string filePath, out int line, out int column)
        {
            filePath = null; line = 0; column = 0;
            var window = WorkbenchSingleton.Workbench?.ActiveWorkbenchWindow;
            var editor = (window?.ActiveViewContent ?? window?.ViewContent) as MonacoClarionEditor;
            if (editor == null) return false;
            return editor.TryGetLiveCursor(out filePath, out line, out column);
        }

        // ── Debugger execution-line marker (CA-Debugger GitHub #26, ticket e6721573) ─────────────────────────
        // ONE global marker: the file + 1-based line the debugger is paused on. It is state, not a one-shot
        // request — unlike _pending it is never consumed, so an editor that loads (or reloads, or is closed and
        // reopened) while the marker is set paints it from here. _execFile == null means "no marker".
        private static string _execFile;
        private static int _execLine;

        /// <summary>
        /// FROZEN CROSS-ADDIN CONTRACT — the standalone ClarionDebugger binds this by reflection. Do NOT change
        /// the name, signature or semantics:
        ///   bool ClarionAssistant.Services.MonacoSourceNavigator.SetExecutionLine(string filePath, int line)
        ///
        /// SET (<paramref name="filePath"/> non-empty AND <paramref name="line"/> &gt;= 1, 1-based): paint the
        /// debugger's execution-line marker (white-on-green gutter line number) in that file's Monaco editor.
        /// There is ONE global marker — setting it removes any previous marker, in any file. It does NOT
        /// navigate, scroll, activate a tab or take focus (callers navigate separately via
        /// <see cref="NavigateToFileAndLine"/>). If the file's Monaco editor isn't open/ready yet, the marker is
        /// remembered and painted when that editor loads. Returns true when the Monaco overlay shows that file
        /// (the marker is or will be painted); false when the stock editor is the visible surface — the caller
        /// then paints the native SharpDevelop current-line marker instead. A false set still removes any
        /// previous Monaco marker (one global marker).
        ///
        /// CLEAR (<paramref name="filePath"/> null/empty OR <paramref name="line"/> &lt;= 0): remove the marker
        /// everywhere and drop any remembered one. Idempotent; always returns true.
        ///
        /// File paths match full-path, case-insensitive. Never throws. Safe from any thread (UI-marshalled).
        /// </summary>
        public static bool SetExecutionLine(string filePath, int line)
        {
            try
            {
                bool clear = string.IsNullOrEmpty(filePath) || line <= 0;
                string full = clear ? null : Norm(filePath);

                var form = WorkbenchSingleton.Workbench as Form;
                if (form != null && form.InvokeRequired)
                {
                    bool ok = clear;
                    form.Invoke(new Action(() => { ok = DoSetExecutionLine(full, clear ? 0 : line); }));
                    return clear || ok;
                }
                return DoSetExecutionLine(full, clear ? 0 : line);
            }
            catch (Exception ex)
            {
                try { MonacoSpikeLog.Write("SetExecutionLine error: " + ex.Message); } catch { }
                return string.IsNullOrEmpty(filePath) || line <= 0;
            }
        }

        // Always on the UI thread. full == null / line == 0 means clear.
        private static bool DoSetExecutionLine(string full, int line)
        {
            bool clear = full == null || line <= 0;
            string oldFile;
            MonacoClarionEditor oldEditor = null, newEditor = null;
            lock (_gate)
            {
                oldFile = _execFile;
                if (clear) { _execFile = null; _execLine = 0; }
                else { _execFile = full; _execLine = line; }
                if (oldFile != null) _live.TryGetValue(oldFile, out oldEditor);
                if (!clear) _live.TryGetValue(full, out newEditor);
            }

            // Remove the previous marker unless the same editor is about to be repainted anyway.
            if (oldEditor != null && !ReferenceEquals(oldEditor, newEditor))
            {
                try { oldEditor.ApplyExecutionLine(0); }
                catch (Exception ex) { MonacoSpikeLog.Write("SetExecutionLine clear-old error: " + ex.Message); }
            }
            if (clear) return true;

            bool monacoShown;
            if (newEditor != null)
            {
                // A live editor knows for sure: its overlay is attached or it isn't (the overlay switch only
                // applies to files opened AFTER a toggle, so the setting alone can be wrong for an open tab).
                monacoShown = newEditor.HasOverlay;
                try { if (monacoShown) newEditor.ApplyExecutionLine(line); }
                catch (Exception ex) { MonacoSpikeLog.Write("SetExecutionLine apply error: " + ex.Message); }
            }
            else
            {
                // Not open yet: it will get the overlay iff the switch is on and the file-type filter admits it —
                // the same gate CaptureTick uses. The marker stays remembered and paints on the editor's load.
                monacoShown = MonacoSourceOverlay.Enabled && CaEditorSettings.SourceAppliesTo(full);
            }
            return monacoShown;
        }

        /// <summary>The execution-line marker for <paramref name="filePath"/>: 1-based line, or 0 if the global
        /// marker is not set or is in another file. Editors read this whenever they (re)load content.</summary>
        internal static int GetExecutionLineFor(string filePath)
        {
            string full = Norm(filePath);
            if (string.IsNullOrEmpty(full)) return 0;
            lock (_gate)
            {
                return _execFile != null && string.Equals(_execFile, full, StringComparison.OrdinalIgnoreCase) ? _execLine : 0;
            }
        }

        /// <summary>Pop a parked navigation for <paramref name="filePath"/> (the editor applies it on load).</summary>
        internal static bool TryConsumePending(string filePath, out int line, out int column)
        {
            line = 0; column = 1;
            string full = Norm(filePath);
            if (string.IsNullOrEmpty(full)) return false;
            lock (_gate)
            {
                int[] v;
                if (_pending.TryGetValue(full, out v)) { _pending.Remove(full); line = v[0]; column = v[1]; return true; }
            }
            return false;
        }
    }
}
