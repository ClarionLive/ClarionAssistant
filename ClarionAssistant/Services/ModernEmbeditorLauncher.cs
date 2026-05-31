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
            var appTree = new AppTreeService();

            // Only one Clarion embeditor at a time — make sure none is open before we trigger generation.
            if (!WaitForEmbedClosed(appTree, 2000))
                return "An embeditor is still open; close it and try again.";

            // Bring the app tree to the front so OpenProcedureEmbed's native automation works even
            // when a Modern Embeditor tab is currently active.
            appTree.ActivateAppView();

            // Trigger native generation + Clarion's (transient) embeditor for this procedure.
            string openLog = appTree.OpenProcedureEmbed(procName);

            // First-time open loads the ABC class libraries and can take many seconds — wait generously
            // so we don't falsely report failure (and leave a stray embeditor open) on the first procedure.
            if (!WaitForEmbedOpen(appTree, 45000))
            {
                // It may yet open late; try to leave things tidy rather than stranding an embeditor.
                try { appTree.CancelEmbeditor(); } catch { }
                return "Embeditor did not open for '" + procName + "' within 45s.\r\n" + openLog;
            }

            // Mirror the live buffer (full source + editable-region map).
            string title, source, error;
            List<int[]> ranges;
            bool ok = EmbeditorCompletionService.TryGetActiveEmbeditorSource(out title, out source, out ranges, out error);

            // Release the native lock no matter what — we made no edits, so discard/close.
            try { appTree.CancelEmbeditor(); } catch { }
            WaitForEmbedClosed(appTree, 3000);

            if (!ok) return "Could not read embed source for '" + procName + "': " + error;

            // Title the tab with the procedure name (nicer than the C7pweeN.appclw temp filename).
            // Passing procName also enables the save round-trip (mirror-mode views can't save).
            var view = new ModernEmbeditorViewContent(procName, source, ranges, "clarion", isDark, procName);
            WorkbenchSingleton.Workbench.ShowView(view);
            return null;
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
