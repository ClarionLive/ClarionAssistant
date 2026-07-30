using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ClarionAssistant.Terminal;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Where the two sides of an EDITABLE file-vs-file compare came from, and how to write them back.
    ///
    /// Its presence is what puts the Monaco diff into editable "file compare" mode; a null context means the
    /// classic read-only viewer contract (the MCP <c>show_diff</c> approve/cancel flow), which must not change.
    ///
    /// The encodings are captured at READ time from <see cref="EncodingHelper.DetectFileEncoding"/> so a save
    /// round-trips each file's ORIGINAL encoding instead of guessing one. That is deliberately per-side: the two
    /// files being compared need not share an encoding, and re-detecting at save time would be reading a file we
    /// are about to overwrite.
    ///
    /// Only ever constructed for a WHOLE-FILE compare — see <see cref="DiffService.ShowDiffFromFiles"/>.
    /// </summary>
    public class DiffFileContext
    {
        public string OriginalPath { get; private set; }
        public Encoding OriginalEncoding { get; private set; }
        public string ModifiedPath { get; private set; }
        public Encoding ModifiedEncoding { get; private set; }

        // Disk timestamps as they were when each side was READ into the diff. A save compares against these
        // to catch "something else changed this file while the compare was open" — writing then would
        // silently discard the other change, since the pane was built from the older content.
        private DateTime _originalStampUtc;
        private DateTime _modifiedStampUtc;

        public DiffFileContext(string originalPath, Encoding originalEncoding,
                               string modifiedPath, Encoding modifiedEncoding)
        {
            OriginalPath = originalPath;
            OriginalEncoding = originalEncoding;
            ModifiedPath = modifiedPath;
            ModifiedEncoding = modifiedEncoding;
            _originalStampUtc = SafeStamp(originalPath);
            _modifiedStampUtc = SafeStamp(modifiedPath);
        }

        private static DateTime SafeStamp(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Resolve a side name ("original"/"modified") coming FROM THE PAGE to a real path+encoding.
        /// The page sends only a side token and its buffer — never a path — so a compromised or buggy page
        /// cannot redirect a save at an arbitrary file. Returns false for anything else.</summary>
        public bool TryResolveSide(string side, out string path, out Encoding encoding)
        {
            if (side == "original") { path = OriginalPath; encoding = OriginalEncoding; return true; }
            if (side == "modified") { path = ModifiedPath; encoding = ModifiedEncoding; return true; }
            path = null; encoding = null; return false;
        }

        /// <summary>True if the file on disk still looks like the one this side was read from. A mismatch
        /// means someone else wrote it while the compare was open, so our pane is based on stale content and
        /// saving it would drop their change. Unknown stamps (MinValue) don't block — a guard that can't
        /// read the timestamp should not make the feature unusable.</summary>
        public bool IsSideUnchangedOnDisk(string side)
        {
            string path; DateTime captured;
            if (side == "original") { path = OriginalPath; captured = _originalStampUtc; }
            else if (side == "modified") { path = ModifiedPath; captured = _modifiedStampUtc; }
            else return false;

            if (captured == DateTime.MinValue) return true;
            DateTime now = SafeStamp(path);
            if (now == DateTime.MinValue) return true;
            return now == captured;
        }

        /// <summary>Adopt the on-disk state as the new baseline for a side we just wrote — otherwise our own
        /// write would make the next save of that side look like someone else's interference.</summary>
        public void NoteSideSaved(string side)
        {
            if (side == "original") _originalStampUtc = SafeStamp(OriginalPath);
            else if (side == "modified") _modifiedStampUtc = SafeStamp(ModifiedPath);
        }
    }

    /// <summary>
    /// Manages the diff viewer lifecycle. Creates DiffViewContent instances
    /// and opens them in the IDE's editor panel.
    /// NOTE: ShowDiff must be called on the UI thread (the MCP tool is RequiresUiThread=true).
    /// </summary>
    public class DiffService
    {
        // Tracked as the base type so either the classic (DiffViewContent) or the Monaco
        // (MonacoDiffViewContent) renderer can occupy the single "current diff" slot.
        private AbstractViewContent _currentDiff;
        private string _lastResult;
        private string _lastNotes;
        private string _lastAction; // "apply", "cancel", "notes", or null (pending)
        private bool _isDark = true;

        // Cache of the exact inputs to the most recent ShowDiff call, for get_diff_content.
        // Guarded by _cacheLock since ShowDiff runs on the UI thread while get_diff_content
        // (RequiresUiThread=false) can be dispatched concurrently on a thread-pool thread.
        private readonly object _cacheLock = new object();
        private string _lastOriginalText;
        private string _lastModifiedText;
        private string _lastDiffTitle;
        private bool _lastIgnoreWhitespace;

        public void SetTheme(bool isDark) { _isDark = isDark; }

        /// <summary>
        /// Show a diff in the IDE editor panel. Must be called on the UI thread.
        /// The result is available later via GetResult().
        ///
        /// <paramref name="fileContext"/> is the opt-in to EDITABLE file-compare mode: non-null puts the Monaco
        /// view into an editable diff that can write each side back. It is ignored for the classic renderer
        /// (which has no editing) and defaults to null, so every existing MCP <c>show_diff</c> caller keeps the
        /// read-only approve/cancel contract unchanged.
        /// </summary>
        public string ShowDiff(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = false, bool useMonaco = false, DiffFileContext fileContext = null)
        {
            // Reset state
            _lastResult = null;
            _lastNotes = null;
            _lastAction = null;

            lock (_cacheLock)
            {
                _lastOriginalText = originalText ?? "";
                _lastModifiedText = modifiedText ?? "";
                _lastDiffTitle = title;
                _lastIgnoreWhitespace = ignoreWhitespace;
            }

            try
            {
                // Close previous diff if still open. There is only ONE diff slot, so opening a second
                // compare discards the first — which is destructive once a compare can hold unsaved edits.
                if (_currentDiff != null)
                {
                    if (!ConfirmDiscardIfDirty(_currentDiff,
                            "The open compare has unsaved changes that have not been written to disk.\n\n" +
                            "Opening a new compare will discard them. Continue?"))
                        return "Cancelled: the open compare has unsaved changes.";

                    try
                    {
                        var ww = _currentDiff.WorkbenchWindow;
                        if (ww != null) ww.CloseWindow(true);
                    }
                    catch { }
                    _currentDiff = null;
                }

                if (useMonaco)
                {
                    // Monaco renderer: native side-by-side/inline + ignore-whitespace; ignore_whitespace
                    // defaults ON here (John's standing pref) when the caller didn't opt out.
                    var monaco = new MonacoDiffViewContent(title, originalText, modifiedText, language, ignoreWhitespace, _isDark, fileContext);
                    monaco.Applied += OnApplied;
                    monaco.Cancelled += OnCancelled;
                    if (fileContext != null)
                    {
                        // Captured per-view so the handler always writes through THIS diff's context, even if
                        // _currentDiff has since been replaced by another compare.
                        var target = monaco;
                        monaco.SaveSideRequested += (side, text) => OnSaveSideRequested(target, side, text);
                    }
                    // NOTE: the Monaco view has no notes workflow yet (deferred to a follow-up ticket).
                    _currentDiff = monaco;
                }
                else
                {
                    var classic = new DiffViewContent(title, originalText, modifiedText, language, ignoreWhitespace, _isDark);
                    classic.Applied += OnApplied;
                    classic.Cancelled += OnCancelled;
                    classic.NotesSubmitted += OnNotesSubmitted;
                    _currentDiff = classic;
                }

                WorkbenchSingleton.Workbench.ShowView(_currentDiff);
                return "Diff viewer opened: " + title;
            }
            catch (Exception ex)
            {
                return "Error opening diff viewer: " + ex.Message;
            }
        }

        /// <summary>
        /// Show a diff where the original is loaded from a file (with optional line range).
        /// </summary>
        public string ShowDiffFromFile(string title, string originalFile, int startLine, int endLine,
            string modifiedText, string language = "clarion", bool ignoreWhitespace = false, bool useMonaco = false)
        {
            try
            {
                if (!File.Exists(originalFile))
                    return "Error: File not found: " + originalFile;

                string originalText;
                try
                {
                    originalText = ReadOriginalText(originalFile, startLine, endLine, EncodingHelper.DetectFileEncoding(originalFile));
                }
                catch (ArgumentException ex)
                {
                    return "Error: " + ex.Message;
                }

                return ShowDiff(title, originalText, modifiedText, language, ignoreWhitespace, useMonaco);
            }
            catch (Exception ex)
            {
                return "Error reading file: " + ex.Message;
            }
        }

        /// <summary>
        /// Show a diff where both original and modified are loaded from files on disk.
        /// Avoids MCP text transport encoding issues for large files.
        ///
        /// When BOTH sides are whole-file (the Explorer "Compare" path) and the Monaco renderer is in use, this
        /// also builds the <see cref="DiffFileContext"/> that makes the diff editable and saveable. A SUB-RANGE
        /// compare is deliberately left read-only: the panes would hold only a slice of each file, so writing a
        /// pane back means splicing it into surrounding text it never showed — a different feature with its own
        /// failure modes. Better to not offer saving than to offer a save that silently truncates a file.
        /// </summary>
        public string ShowDiffFromFiles(string title, string originalFile, int origStartLine, int origEndLine,
            string modifiedFile, int modStartLine, int modEndLine, string language = "clarion", bool ignoreWhitespace = false,
            bool useMonaco = false)
        {
            try
            {
                if (!File.Exists(originalFile))
                    return "Error: Original file not found: " + originalFile;
                if (!File.Exists(modifiedFile))
                    return "Error: Modified file not found: " + modifiedFile;

                // Detected ONCE here and carried on the context — a save must round-trip the encoding the text
                // was read with, and re-detecting later would mean reading a file we're about to overwrite.
                Encoding originalEncoding = EncodingHelper.DetectFileEncoding(originalFile);
                Encoding modifiedEncoding = EncodingHelper.DetectFileEncoding(modifiedFile);

                string originalText = ReadOriginalText(originalFile, origStartLine, origEndLine, originalEncoding);
                string modifiedText = ReadOriginalText(modifiedFile, modStartLine, modEndLine, modifiedEncoding);

                // Same "whole file" test ReadOriginalText itself uses, so the editable/read-only decision can
                // never disagree with whether the panes actually hold the complete file.
                bool wholeFile = IsWholeFile(origStartLine, origEndLine) && IsWholeFile(modStartLine, modEndLine);
                DiffFileContext fileContext = (useMonaco && wholeFile)
                    ? new DiffFileContext(originalFile, originalEncoding, modifiedFile, modifiedEncoding)
                    : null;

                return ShowDiff(title, originalText, modifiedText, language, ignoreWhitespace, useMonaco, fileContext);
            }
            catch (Exception ex)
            {
                return "Error reading files: " + ex.Message;
            }
        }

        /// <summary>The "no sub-range requested" test, matching <see cref="ReadOriginalText"/>'s own condition.</summary>
        private static bool IsWholeFile(int startLine, int endLine)
        {
            return startLine <= 1 && endLine == -1;
        }

        /// <summary>
        /// Read a file for diffing, preserving its exact text (including any trailing newline)
        /// when no sub-range is requested (startLine &lt;= 1 and endLine == -1, the "whole file"
        /// default). A live editor buffer naturally includes the file's trailing EOL, so
        /// reconstructing the disk side via ReadAllLines+Join (which always drops it) produced a
        /// spurious "extra blank line" diff that was never a real edit. A genuine sub-range still
        /// goes through line splitting, since some reconstruction is unavoidable there anyway.
        /// </summary>
        private static string ReadOriginalText(string filePath, int startLine, int endLine, Encoding encoding)
        {
            if (startLine <= 1 && endLine == -1)
                return File.ReadAllText(filePath, encoding);

            string[] allLines = File.ReadAllLines(filePath, encoding);
            if (startLine < 1) startLine = 1;
            if (endLine < 1 || endLine > allLines.Length) endLine = allLines.Length;
            if (startLine > endLine)
                throw new ArgumentException("start_line (" + startLine + ") is greater than end_line (" + endLine + ")");

            var lines = new string[endLine - startLine + 1];
            Array.Copy(allLines, startLine - 1, lines, 0, lines.Length);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Write one side of an editable file compare back to disk. This is the "smart route" agreed with
        /// John (2026-07-30): SAVE ALWAYS MEANS ON DISK, but never at the cost of someone else's edit.
        ///
        /// Order matters — each guard exists because skipping it loses data silently:
        ///   1. Resolve the SIDE TOKEN to a path. The page never supplies a path, so it cannot redirect us.
        ///   2. Refuse if an open editor tab holds UNSAVED edits for that file. The diff panes were built
        ///      from DISK, so they do not contain those edits; writing would strand them, and pushing our
        ///      text into the tab would destroy them. Neither side can be preserved automatically, so the
        ///      only honest answer is to stop and say so.
        ///   3. Refuse if the file changed on disk since the compare opened — our pane is based on the older
        ///      content, so writing would drop whatever landed in between.
        ///   4. Write using the encoding captured at READ time, which round-trips the file's original BOM
        ///      state (DetectFileEncoding returns UTF8Encoding(true/false) accordingly — important because
        ///      Clarion rejects a BOM it did not start with).
        ///   5. Resync a CLEAN open tab so it shows the new content instead of a stale buffer.
        /// </summary>
        private void OnSaveSideRequested(MonacoDiffViewContent view, string side, string text)
        {
            if (view == null) return;
            try
            {
                var ctx = view.FileContext;
                if (ctx == null) { view.PostSaveResult(side, false, "This diff has no file to save to."); return; }

                string path; Encoding encoding;
                if (!ctx.TryResolveSide(side, out path, out encoding) || string.IsNullOrEmpty(path))
                {
                    view.PostSaveResult(side, false, "Unknown side — nothing was written.");
                    return;
                }

                string name = SafeName(path);

                // TWO dirty checks, because neither alone is sufficient:
                //   - The CA Monaco overlay keeps its buffer OUTSIDE the IDE's document model and leaves the
                //     native shell clean on purpose, so the IDE reports "not dirty" for a tab full of edits.
                //   - Conversely the overlay can be toggled off (ToggleMonacoSourceOverlayCommand), leaving a
                //     plain native editor whose unsaved state only the IDE knows about.
                bool tabDirty;
                if ((MonacoClarionEditor.TryGetLiveTabState(path, out tabDirty) && tabDirty) || IsOpenAndDirtyInIde(path))
                {
                    view.PostSaveResult(side, false,
                        name + " has unsaved changes in an editor. Save or close that tab, then re-run the compare.");
                    return;
                }

                if (!ctx.IsSideUnchangedOnDisk(side))
                {
                    view.PostSaveResult(side, false,
                        name + " changed on disk since this compare opened. Re-run the compare so you don't overwrite that change.");
                    return;
                }

                var attrs = SafeAttributes(path);
                if (attrs.HasValue && (attrs.Value & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    view.PostSaveResult(side, false, name + " is read-only on disk.");
                    return;
                }

                File.WriteAllText(path, text ?? "", encoding);
                ctx.NoteSideSaved(side);

                // Best-effort: an open CLEAN tab for this file now shows stale text. Refresh it so the editor
                // and disk agree. A failure here does not undo the save, so it must not be reported as one.
                try { MonacoClarionEditor.TryResyncFromDisk(path); } catch { }

                view.PostSaveResult(side, true, "Saved " + name + ".");
            }
            catch (Exception ex)
            {
                try { view.PostSaveResult(side, false, "Save failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// Does the IDE itself hold an open, modified document for this path? Complements the CA-overlay
        /// check — this one sees a plain native editor tab with unsaved edits.
        ///
        /// IsDirty lives on AbstractViewContent, NOT on the IViewContent that FileService hands back (verified
        /// against this fork's ICSharpCode.SharpDevelop.dll), hence the cast. A view content that is neither
        /// shape is treated as clean rather than blocking the save on something we cannot read.
        /// </summary>
        private static bool IsOpenAndDirtyInIde(string path)
        {
            try
            {
                if (!ICSharpCode.SharpDevelop.FileService.IsOpen(path)) return false;
                var vc = ICSharpCode.SharpDevelop.FileService.GetOpenFileViewContent(path) as AbstractViewContent;
                return vc != null && vc.IsDirty;
            }
            catch { return false; }
        }

        private static string SafeName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "The file";
            try { return Path.GetFileName(path) ?? path; }
            catch { return path; }
        }

        private static FileAttributes? SafeAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch { return null; }
        }

        private void OnApplied(string text)
        {
            _lastAction = "apply";
            _lastResult = text;
            CloseDiff();
        }

        private void OnCancelled()
        {
            var monaco = _currentDiff as MonacoDiffViewContent;
            if (monaco != null && monaco.HasUnsavedEdits)
            {
                // This runs on the WebView2 message-handler stack. A modal here pumps a nested message
                // loop inside that handler — the reentrancy trap this codebase has been bitten by before
                // (see the embeditor's "run the open OFF this stack" rule and the close-save MessageBox
                // note in MonacoClarionSourceEditor). Defer, then prompt.
                var ctrl = monaco.Control;
                if (ctrl != null && ctrl.IsHandleCreated)
                {
                    ctrl.BeginInvoke((MethodInvoker)(() =>
                    {
                        if (!ConfirmDiscardIfDirty(_currentDiff,
                                "This compare has unsaved changes that have not been written to disk.\n\n" +
                                "Close and discard them?"))
                            return;
                        _lastAction = "cancel";
                        _lastResult = null;
                        _lastNotes = null;
                        CloseDiff();
                    }));
                    return;
                }
            }

            _lastAction = "cancel";
            _lastResult = null;
            _lastNotes = null;
            CloseDiff();
        }

        /// <summary>Ask before discarding unsaved compare edits. Returns true when it is safe to proceed —
        /// including the common case of a diff that is read-only or clean, which is never prompted for.</summary>
        private static bool ConfirmDiscardIfDirty(AbstractViewContent diff, string message)
        {
            var monaco = diff as MonacoDiffViewContent;
            if (monaco == null || !monaco.HasUnsavedEdits) return true;
            try
            {
                return MessageBox.Show(message, "Unsaved compare changes",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            catch
            {
                // No UI available to ask — the safe default is to KEEP the edits, not silently bin them.
                return false;
            }
        }

        private void OnNotesSubmitted(string notesJson)
        {
            _lastAction = "notes";
            _lastNotes = notesJson;
            CloseDiff();
        }

        /// <summary>
        /// Get the result of the last diff interaction.
        /// Returns a dictionary with status and optionally text or notes.
        /// </summary>
        public Dictionary<string, string> GetResult()
        {
            var result = new Dictionary<string, string>();

            if (_lastAction == null)
            {
                result["status"] = "pending";
                result["message"] = "Diff viewer is still open. The developer hasn't acted yet.";
                return result;
            }

            if (_lastAction == "apply")
            {
                result["status"] = "approved";
                result["text"] = _lastResult ?? "";
                return result;
            }

            if (_lastAction == "notes")
            {
                result["status"] = "notes";
                result["notes"] = _lastNotes ?? "[]";
                return result;
            }

            result["status"] = "cancelled";
            return result;
        }

        /// <summary>Check if a diff viewer is currently open and pending user action.</summary>
        public bool IsPending { get { return _currentDiff != null && _lastAction == null; } }

        /// <summary>
        /// Get the unified diff text for the most recently shown diff, computed via
        /// UnifiedDiffGenerator from the exact text passed to the last ShowDiff call —
        /// the same generator DiffViewContent itself uses to render the classic view,
        /// so this can never disagree with what that view shows.
        /// Safe for very large files: response size scales with the size of the changes,
        /// not the file size.
        /// </summary>
        public Dictionary<string, object> GetContent()
        {
            string originalText, modifiedText, title;
            bool ignoreWhitespace;

            lock (_cacheLock)
            {
                if (_lastOriginalText == null && _lastModifiedText == null)
                    return new Dictionary<string, object> { { "error", "No diff has been shown yet." } };

                originalText = _lastOriginalText;
                modifiedText = _lastModifiedText;
                title = _lastDiffTitle;
                // Resolved value from that ShowDiff call (e.g. Monaco's ignore-whitespace-on-by-default
                // already applied) — not the raw parameter as the caller passed it.
                ignoreWhitespace = _lastIgnoreWhitespace;
            }

            string diff = UnifiedDiffGenerator.Generate(originalText, modifiedText, ignoreWhitespace);

            return new Dictionary<string, object>
            {
                { "title", title },
                { "diff", diff },
                { "isPending", IsPending }
            };
        }

        private void CloseDiff()
        {
            if (_currentDiff == null) return;
            try
            {
                var ww = _currentDiff.WorkbenchWindow;
                if (ww != null)
                    ww.CloseWindow(true);
            }
            catch { }
            _currentDiff = null;
        }
    }
}
