using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ClarionAssistant.Terminal;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// THE single choke point for opening files into the CA (Monaco) editor. Every open routes through
    /// <see cref="OpenFile"/> so recents + last-folder get recorded in <see cref="ExplorerRecentsStore"/>
    /// in exactly one place — callers (Tools menu, Explorer panel, etc.) never touch the store directly.
    ///
    /// These methods assume they are already on the UI thread (the host defers Explorer actions before
    /// calling in), so they do NOT marshal with BeginInvoke — keep them straightforward.
    /// </summary>
    public static class MonacoFileOpener
    {
        /// <summary>Shared OpenFileDialog filter for "load a file into the CA editor" — one definition so the
        /// Tools-menu command and the Explorer Files tab can't drift.</summary>
        public const string OpenFileFilter =
            "Clarion source (*.clw;*.inc;*.equ;*.int;*.tpl;*.tpw;*.trn;*.app)|*.clw;*.inc;*.equ;*.int;*.tpl;*.tpw;*.trn;*.app|All files (*.*)|*.*";

        /// <summary>Extensions the Explorer will open from an UNTRUSTED drag-drop (matches the dialog filter's
        /// source set). A drop of anything else (e.g. .exe/.dll) is ignored rather than loaded as text.</summary>
        private static readonly HashSet<string> AllowedDropExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".clw", ".inc", ".equ", ".int", ".tpl", ".tpw", ".trn", ".app"
        };

        /// <summary>True if <paramref name="path"/> is one of the source extensions the Explorer loads.</summary>
        public static bool IsAllowedDropExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return AllowedDropExtensions.Contains(Path.GetExtension(path) ?? ""); }
            catch { return false; }
        }

        /// <summary>
        /// Open a single file into a Monaco tab (focusing the existing tab if already open), then record
        /// the open + its folder. Re-opening an already-open file just activates its tab. A path that does
        /// not exist on disk is ignored (never opened, never recorded) — this also stops a WebView2 drop of a
        /// bare filename (no real path) from creating a broken tab + a junk recents entry.
        /// </summary>
        /// <returns>true if the file was opened or focused; false if it was missing/invalid.</returns>
        public static bool OpenFile(string path, bool isDark)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            var existing = ModernEmbeditorViewContent.FindByFilePath(path);
            if (existing != null)
            {
                existing.ActivateTab();
            }
            else
            {
                WorkbenchSingleton.Workbench.ShowView(new ModernEmbeditorViewContent(path, isDark));
            }

            // Record AFTER a successful open so a failed/ignored open doesn't pollute recents.
            // One load+save cycle records both the recent and its folder.
            ExplorerRecentsStore.RecordOpen(path, Path.GetDirectoryName(path));
            return true;
        }

        /// <summary>
        /// Open both sides of an .inc/.clw class pair, each in its own Monaco tab (only the side(s)
        /// that exist on disk). Each open is recorded via <see cref="OpenFile"/>.
        /// </summary>
        /// <returns>The number of sides actually opened (0, 1, or 2) so the caller can surface feedback
        /// when a stale pair opens nothing or only half.</returns>
        public static int OpenClassPair(string incPath, string clwPath, bool isDark)
        {
            int opened = 0;
            if (OpenFile(incPath, isDark)) opened++;
            if (OpenFile(clwPath, isDark)) opened++;
            return opened;
        }

        /// <summary>
        /// Show a Monaco side-by-side diff of two files on disk (full file vs full file). This does NOT
        /// record recents — it's a read-only comparison, not an editing open.
        /// </summary>
        public static void Compare(string a, string b, bool isDark)
        {
            // Fail closed if either side is missing — same existence guard as OpenFile, so a stale/bogus path
            // (e.g. from persisted recents or a crafted compare message) can't drive DiffService to resolve a
            // non-file. UNC handling mirrors OpenFile: a path the user themselves opened is allowed; the
            // untrusted drag vector is the one that hard-blocks UNC (in the dropFiles host handler).
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return;
            if (!File.Exists(a) || !File.Exists(b)) return;

            var diff = new DiffService();
            diff.SetTheme(isDark);
            // endLine = -1 -> DiffService clamps to full file length for each side.
            diff.ShowDiffFromFiles(
                Path.GetFileName(a) + " ↔ " + Path.GetFileName(b),
                a, 1, -1,
                b, 1, -1,
                language: "clarion", ignoreWhitespace: false, useMonaco: true);
        }

        /// <summary>
        /// Open Windows Explorer with the file selected. Hardened against command-line injection: the path is
        /// normalized, must be a real existing file, and is rejected if it contains a double-quote (a real
        /// Windows path can't — so this closes the only break-out vector on the "/select,&quot;...&quot;" argument).
        /// </summary>
        public static void RevealInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }

            if (full.IndexOf('"') >= 0 || !File.Exists(full)) return;

            try { Process.Start("explorer.exe", "/select,\"" + full + "\""); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoFileOpener] RevealInExplorer: " + ex.Message);
            }
        }
    }
}
