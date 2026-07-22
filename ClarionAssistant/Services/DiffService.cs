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
        /// </summary>
        public string ShowDiff(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = false, bool useMonaco = false)
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
                // Close previous diff if still open
                if (_currentDiff != null)
                {
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
                    var monaco = new MonacoDiffViewContent(title, originalText, modifiedText, language, ignoreWhitespace, _isDark);
                    monaco.Applied += OnApplied;
                    monaco.Cancelled += OnCancelled;
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

                string originalText = ReadOriginalText(originalFile, origStartLine, origEndLine, EncodingHelper.DetectFileEncoding(originalFile));
                string modifiedText = ReadOriginalText(modifiedFile, modStartLine, modEndLine, EncodingHelper.DetectFileEncoding(modifiedFile));

                return ShowDiff(title, originalText, modifiedText, language, ignoreWhitespace, useMonaco);
            }
            catch (Exception ex)
            {
                return "Error reading files: " + ex.Message;
            }
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

        private void OnApplied(string text)
        {
            _lastAction = "apply";
            _lastResult = text;
            CloseDiff();
        }

        private void OnCancelled()
        {
            _lastAction = "cancel";
            _lastResult = null;
            _lastNotes = null;
            CloseDiff();
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
