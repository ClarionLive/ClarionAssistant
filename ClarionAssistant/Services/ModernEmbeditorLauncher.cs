using System;
using System.Collections.Generic;
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

                // OpenAndMirror leaves the embeditor open; we made no edits, so discard/close to free the lock.
                try { appTree.CancelEmbeditor(); } catch { }
                WaitForEmbedClosed(appTree, 3000);

                // Title the tab with the procedure name; passing procName also enables the save round-trip.
                var view = new ModernEmbeditorViewContent(procName, source, ranges, "clarion", isDark, procName);
                WorkbenchSingleton.Workbench.ShowView(view);
                return null;
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

        internal static bool WaitForEmbedOpen(AppTreeService appTree, int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 50)
            {
                if (appTree.GetEmbedInfo() != null) return true;
                Application.DoEvents();
                Thread.Sleep(50);
            }
            return appTree.GetEmbedInfo() != null;
        }

        internal static bool WaitForEmbedClosed(AppTreeService appTree, int timeoutMs)
        {
            for (int waited = 0; waited < timeoutMs; waited += 50)
            {
                if (appTree.GetEmbedInfo() == null) return true;
                Application.DoEvents();
                Thread.Sleep(50);
            }
            return appTree.GetEmbedInfo() == null;
        }
    }
}
