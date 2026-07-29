using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// THE single choke point for opening files into the IDE editor (Monaco by default for Clarion source,
    /// via MonacoClarionEditorDisplayBinding). Every open routes through
    /// <see cref="OpenFile"/> so recents + last-folder get recorded in <see cref="ExplorerRecentsStore"/>
    /// in exactly one place — callers (Tools menu, Explorer panel, etc.) never touch the store directly.
    /// <see cref="Compare"/> is the same choke point for compare PAIRS: every diff, whether it came from the
    /// split button, a class row, or a re-run of a recorded pair, records through here and nowhere else.
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
        /// Open a single file through the IDE's standard file-open pipeline, then record the open + its folder.
        /// We route via <c>FileService.OpenFile</c> rather than constructing a standalone CA Monaco tab so the
        /// registered DisplayBinding decides the editor: the Monaco source editor is now the DEFAULT for Clarion
        /// source (<c>MonacoClarionEditorDisplayBinding</c>), so the file still lands in Monaco — but inside the
        /// IDE's own document/editor lifecycle (Ctrl+D structure designer, save/close, focus-if-already-open),
        /// which is smoother and more Clarion-compatible than a bespoke tab. The IDE focuses the existing
        /// document if the file is already open, so we don't dedup here.
        ///
        /// A path that does not exist on disk is ignored (never opened, never recorded) — this also stops a
        /// WebView2 drop of a bare filename (no real path) from creating a broken tab + a junk recents entry.
        /// <paramref name="isDark"/> is retained for caller compatibility but no longer used at open time — the
        /// editor owns its own theme.
        /// </summary>
        /// <returns>true if the file was handed to the IDE to open; false if it was missing/invalid.</returns>
        public static bool OpenFile(string path, bool isDark)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            try
            {
                // IDE-standard open → routes to MonacoClarionEditorDisplayBinding for Clarion source (Monaco by
                // default); focuses the tab if the file is already open.
                ICSharpCode.SharpDevelop.FileService.OpenFile(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoFileOpener] OpenFile: " + ex.Message);
                return false;
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
        /// Show a Monaco side-by-side diff of two files on disk (full file vs full file).
        ///
        /// Records the PAIR in <see cref="ExplorerRecentsStore.RecordCompare"/> — but still never records a
        /// file recent for either side, because a comparison is a read-only look, not an editing open. The
        /// pair is recorded only AFTER the diff actually opens, the same "record after success" discipline
        /// as <see cref="OpenFile"/>, so a stale pair or a failed diff can't pollute the compare list.
        /// </summary>
        /// <returns>true if the diff was shown (and the pair recorded); false if either side was
        /// missing/invalid or the diff failed to open — the caller surfaces the feedback.</returns>
        public static bool Compare(string a, string b, bool isDark)
        {
            // Fail closed if either side is missing — same existence guard as OpenFile, so a stale/bogus path
            // (e.g. from persisted recents or a crafted compare message) can't drive DiffService to resolve a
            // non-file. UNC handling mirrors OpenFile: a path the user themselves opened is allowed; the
            // untrusted drag vector is the one that hard-blocks UNC (in the dropFiles host handler).
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (!File.Exists(a) || !File.Exists(b)) return false;

            try
            {
                var diff = new DiffService();
                diff.SetTheme(isDark);
                // endLine = -1 -> DiffService clamps to full file length for each side.
                diff.ShowDiffFromFiles(
                    Path.GetFileName(a) + " ↔ " + Path.GetFileName(b),
                    a, 1, -1,
                    b, 1, -1,
                    language: "clarion", ignoreWhitespace: false, useMonaco: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MonacoFileOpener] Compare: " + ex.Message);
                return false;
            }

            // Record AFTER the diff opened. Dedup/cap/pin bookkeeping all live in the store; the incoming
            // side order is preserved there, which is what makes a swapped re-run rewrite the one entry.
            ExplorerRecentsStore.RecordCompare(a, b);
            return true;
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
