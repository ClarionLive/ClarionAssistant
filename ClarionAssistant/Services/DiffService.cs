using System;
using System.Collections.Generic;
using System.IO;
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
        private DiffViewContent _currentDiff;
        private string _lastResult;
        private string _lastNotes;
        private string _lastAction; // "apply", "cancel", "notes", or null (pending)

        /// <summary>
        /// Show a diff in the IDE editor panel. Must be called on the UI thread.
        /// The result is available later via GetResult().
        /// </summary>
        public string ShowDiff(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = false)
        {
            // Reset state
            _lastResult = null;
            _lastNotes = null;
            _lastAction = null;

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

                _currentDiff = new DiffViewContent(title, originalText, modifiedText, language, ignoreWhitespace);

                _currentDiff.Applied += OnApplied;
                _currentDiff.Cancelled += OnCancelled;
                _currentDiff.NotesSubmitted += OnNotesSubmitted;

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
            string modifiedText, string language = "clarion", bool ignoreWhitespace = false)
        {
            try
            {
                if (!File.Exists(originalFile))
                    return "Error: File not found: " + originalFile;

                string[] allLines = File.ReadAllLines(originalFile);

                // Clamp line range (1-based input)
                if (startLine < 1) startLine = 1;
                if (endLine < 1 || endLine > allLines.Length) endLine = allLines.Length;
                if (startLine > endLine)
                    return "Error: start_line (" + startLine + ") is greater than end_line (" + endLine + ")";

                // Extract the line range (convert 1-based to 0-based)
                var lines = new string[endLine - startLine + 1];
                Array.Copy(allLines, startLine - 1, lines, 0, lines.Length);
                string originalText = string.Join("\n", lines);

                return ShowDiff(title, originalText, modifiedText, language, ignoreWhitespace);
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
            string modifiedFile, int modStartLine, int modEndLine, string language = "clarion", bool ignoreWhitespace = false)
        {
            try
            {
                if (!File.Exists(originalFile))
                    return "Error: Original file not found: " + originalFile;
                if (!File.Exists(modifiedFile))
                    return "Error: Modified file not found: " + modifiedFile;

                string originalText = ReadFileRange(originalFile, origStartLine, origEndLine);
                string modifiedText = ReadFileRange(modifiedFile, modStartLine, modEndLine);

                return ShowDiff(title, originalText, modifiedText, language, ignoreWhitespace);
            }
            catch (Exception ex)
            {
                return "Error reading files: " + ex.Message;
            }
        }

        private static string ReadFileRange(string filePath, int startLine, int endLine)
        {
            string[] allLines = File.ReadAllLines(filePath);
            if (startLine < 1) startLine = 1;
            if (endLine < 1 || endLine > allLines.Length) endLine = allLines.Length;
            if (startLine > endLine) startLine = 1;

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
