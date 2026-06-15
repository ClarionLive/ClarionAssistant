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

        // Always on the UI thread. Open (or focus) the file, park the desired position, then drive the editor
        // if it is already live — otherwise it self-applies via ApplyPendingNavigation on capture/ready.
        private static bool DoNavigate(string full, int line, int column)
        {
            try { ICSharpCode.SharpDevelop.FileService.OpenFile(full); }
            catch (Exception ex) { MonacoSpikeLog.Write("Navigator OpenFile error: " + ex.Message); return false; }

            MonacoClarionEditor ed;
            lock (_gate)
            {
                _pending[full] = new[] { line, column };
                _live.TryGetValue(full, out ed);
            }
            if (ed != null) ed.ApplyPendingNavigation();
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
