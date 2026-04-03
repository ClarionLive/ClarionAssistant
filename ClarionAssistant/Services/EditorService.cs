using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    public class InsertResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public static InsertResult Succeeded() => new InsertResult { Success = true };
        public static InsertResult Failed(string message) => new InsertResult { Success = false, ErrorMessage = message };
    }

    /// <summary>
    /// Service to interact with the active text editor in the Clarion IDE.
    /// Uses reflection for version compatibility.
    /// </summary>
    public class EditorService
    {
        public bool HasActiveTextEditor()
        {
            try { return GetActiveTextArea() != null; }
            catch { return false; }
        }

        public string GetActiveDocumentContent()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                return (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
            }
            catch { return null; }
        }

        public string GetActiveDocumentPath()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow");
                if (activeWindow == null) return null;

                // Strategy 1: ToolTipText on the workspace window contains the full file path
                // e.g., "H:\Dev\Source\Classes\UltimateDebug.clw"
                var toolTip = GetProperty(activeWindow, "ToolTipText") as string;
                if (!string.IsNullOrEmpty(toolTip) && toolTip.Contains("\\") && toolTip.Contains("."))
                {
                    return toolTip;
                }

                // Strategy 2: Try FileName on ViewContent (may throw on ClarionEditor)
                var viewContent = GetProperty(activeWindow, "ViewContent")
                               ?? GetProperty(activeWindow, "ActiveViewContent");
                if (viewContent != null)
                {
                    try
                    {
                        var fileName = GetProperty(viewContent, "FileName") as string;
                        if (!string.IsNullOrEmpty(fileName)) return fileName;
                    }
                    catch { /* ClarionEditor throws on FileName - fall through */ }

                    try
                    {
                        var primaryFileName = GetProperty(viewContent, "PrimaryFileName") as string;
                        if (!string.IsNullOrEmpty(primaryFileName)) return primaryFileName;
                    }
                    catch { }
                }

                // Strategy 3: Title/TitleName as last resort (filename only, no path)
                var title = GetProperty(activeWindow, "Title") as string;
                if (!string.IsNullOrEmpty(title)) return title;

                return null;
            }
            catch { return null; }
        }

        public InsertResult InsertTextAtCaret(string text)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");

                var document = GetProperty(textArea, "Document");
                var caret = GetProperty(textArea, "Caret");
                if (document == null || caret == null) return InsertResult.Failed("Cannot access editor");

                var offset = (int)GetProperty(caret, "Offset");
                var insertMethod = document.GetType().GetMethod("Insert", new[] { typeof(int), typeof(string) });
                if (insertMethod == null) return InsertResult.Failed("Insert method not found");

                insertMethod.Invoke(document, new object[] { offset, text });
                SetProperty(caret, "Offset", offset + text.Length);

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Replace text in the active editor between two offsets.
        /// </summary>
        public InsertResult ReplaceRange(int startLine, int startCol, int endLine, int endCol, string newText)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");

                var document = GetProperty(textArea, "Document");
                var caret = GetProperty(textArea, "Caret");
                if (document == null) return InsertResult.Failed("Cannot access document");

                // Convert line/col to offsets (0-based lines internally)
                int startOffset = GetOffset(document, startLine - 1, startCol - 1);
                int endOffset = GetOffset(document, endLine - 1, endCol - 1);
                if (startOffset < 0 || endOffset < 0)
                    return InsertResult.Failed("Invalid line/column range");

                int length = endOffset - startOffset;

                // Use Document.Replace(offset, length, text)
                var replaceMethod = document.GetType().GetMethod("Replace",
                    new[] { typeof(int), typeof(int), typeof(string) });
                if (replaceMethod != null)
                {
                    replaceMethod.Invoke(document, new object[] { startOffset, length, newText });
                }
                else
                {
                    // Fallback: Remove then Insert
                    var removeMethod = document.GetType().GetMethod("Remove",
                        new[] { typeof(int), typeof(int) });
                    var insertMethod = document.GetType().GetMethod("Insert",
                        new[] { typeof(int), typeof(string) });
                    if (removeMethod == null || insertMethod == null)
                        return InsertResult.Failed("Replace/Remove method not found");

                    removeMethod.Invoke(document, new object[] { startOffset, length });
                    insertMethod.Invoke(document, new object[] { startOffset, newText });
                }

                // Move caret to end of replacement
                if (caret != null)
                    SetProperty(caret, "Offset", startOffset + (newText ?? "").Length);

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Replace all occurrences of a string in the active editor.
        /// </summary>
        public InsertResult ReplaceText(string oldText, string newText)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");

                var document = GetProperty(textArea, "Document");
                if (document == null) return InsertResult.Failed("Cannot access document");

                string content = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                if (content == null) return InsertResult.Failed("Cannot read document text");

                if (!content.Contains(oldText))
                    return InsertResult.Failed("Text not found in document");

                string updated = content.Replace(oldText, newText);

                // Use Document.TextContent setter or Replace full text
                var textContentProp = document.GetType().GetProperty("TextContent",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (textContentProp != null && textContentProp.CanWrite)
                {
                    textContentProp.SetValue(document, updated, null);
                }
                else
                {
                    var textProp = document.GetType().GetProperty("Text",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (textProp != null && textProp.CanWrite)
                        textProp.SetValue(document, updated, null);
                    else
                        return InsertResult.Failed("Cannot set document text");
                }

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }

                int count = (content.Length - content.Replace(oldText, "").Length) / oldText.Length;
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Select a range of text in the active editor.
        /// </summary>
        public InsertResult SelectRange(int startLine, int startCol, int endLine, int endCol)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");

                var document = GetProperty(textArea, "Document");
                var selMgr = GetProperty(textArea, "SelectionManager");
                if (document == null || selMgr == null) return InsertResult.Failed("Cannot access editor");

                // Clear existing selection
                var clearMethod = selMgr.GetType().GetMethod("ClearSelection");
                if (clearMethod != null) clearMethod.Invoke(selMgr, null);

                int startOffset = GetOffset(document, startLine - 1, startCol - 1);
                int endOffset = GetOffset(document, endLine - 1, endCol - 1);
                if (startOffset < 0 || endOffset < 0)
                    return InsertResult.Failed("Invalid line/column range");

                // Try SetSelection(startOffset, endOffset) or SetSelection(startPos, endPos)
                // SharpDevelop uses TextLocation-based selection
                var sharpDevelopAsm = System.Reflection.Assembly.Load("ICSharpCode.TextEditor");
                if (sharpDevelopAsm == null)
                    sharpDevelopAsm = System.Reflection.Assembly.Load("ICSharpCode.SharpDevelop");

                // Try the TextLocation approach
                var setSelMethod = selMgr.GetType().GetMethod("SetSelection",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (setSelMethod != null)
                {
                    // Find TextLocation type
                    var paramTypes = setSelMethod.GetParameters();
                    if (paramTypes.Length == 2)
                    {
                        var locType = paramTypes[0].ParameterType;
                        var startLoc = Activator.CreateInstance(locType, new object[] { startCol - 1, startLine - 1 });
                        var endLoc = Activator.CreateInstance(locType, new object[] { endCol - 1, endLine - 1 });
                        setSelMethod.Invoke(selMgr, new object[] { startLoc, endLoc });
                    }
                }

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Delete a range of text in the active editor (replace with empty string).
        /// </summary>
        public InsertResult DeleteRange(int startLine, int startCol, int endLine, int endCol)
        {
            return ReplaceRange(startLine, startCol, endLine, endCol, "");
        }

        /// <summary>
        /// Get offset from line/column in a document.
        /// </summary>
        private int GetOffset(object document, int line, int col)
        {
            try
            {
                // Try GetLineSegment(line).Offset + col
                var getLineMethod = document.GetType().GetMethod("GetLineSegment",
                    new[] { typeof(int) });
                if (getLineMethod != null)
                {
                    var segment = getLineMethod.Invoke(document, new object[] { line });
                    if (segment != null)
                    {
                        int lineOffset = (int)GetProperty(segment, "Offset");
                        int lineLength = (int)GetProperty(segment, "Length");
                        return lineOffset + Math.Min(col, lineLength);
                    }
                }

                // Fallback: calculate from text
                string text = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                if (text == null) return -1;

                int offset = 0;
                string[] lines = text.Split('\n');
                for (int i = 0; i < line && i < lines.Length; i++)
                    offset += lines[i].Length + 1; // +1 for \n
                return offset + Math.Min(col, lines.Length > line ? lines[line].Length : 0);
            }
            catch { return -1; }
        }

        /// <summary>
        /// Appends text to the end of a specific file. Opens the file if not already open.
        /// </summary>
        public InsertResult AppendTextToFile(string filePath, string text)
        {
            try
            {
                if (!File.Exists(filePath))
                    return InsertResult.Failed($"File not found: {filePath}");

                File.AppendAllText(filePath, "\r\n" + text);
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        public string GetSelectedText()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var selMgr = GetProperty(textArea, "SelectionManager");
                if (selMgr != null)
                {
                    var hasSelection = GetProperty(selMgr, "HasSomethingSelected");
                    if (hasSelection is bool && (bool)hasSelection)
                    {
                        var selectedText = GetProperty(selMgr, "SelectedText");
                        if (selectedText is string s && !string.IsNullOrEmpty(s))
                            return s.Trim();
                    }
                }
                return null;
            }
            catch { return null; }
        }

        public string GetWordUnderCursor()
        {
            try
            {
                string selected = GetSelectedText();
                if (!string.IsNullOrEmpty(selected)) return selected;

                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                var caret = GetProperty(textArea, "Caret");
                if (caret == null) return null;

                var offsetObj = GetProperty(caret, "Offset");
                if (offsetObj == null) return null;
                int offset = (int)offsetObj;

                var textObj = GetProperty(document, "TextContent") ?? GetProperty(document, "Text");
                if (textObj == null) return null;
                string fullText = textObj.ToString();

                if (offset < 0 || offset > fullText.Length) return null;

                int start = offset;
                while (start > 0 && IsWordChar(fullText[start - 1])) start--;
                int end = offset;
                while (end < fullText.Length && IsWordChar(fullText[end])) end++;

                return start < end ? fullText.Substring(start, end - start) : null;
            }
            catch { return null; }
        }

        public void NavigateToFileAndLine(string filePath, int lineNumber)
        {
            try
            {
                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return;

                var fileServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.FileService");
                if (fileServiceType == null) return;

                var openFileMethod = fileServiceType.GetMethod("OpenFile",
                    BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
                if (openFileMethod != null)
                {
                    openFileMethod.Invoke(null, new object[] { filePath });
                    if (lineNumber > 0)
                    {
                        var textArea = GetActiveTextArea();
                        if (textArea != null)
                        {
                            var caret = GetProperty(textArea, "Caret");
                            SetProperty(caret, "Line", lineNumber - 1);
                            SetProperty(caret, "Column", 0);
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Navigate to a line in the currently active document.
        /// </summary>
        public bool GoToLine(int lineNumber)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return false;

                var caret = GetProperty(textArea, "Caret");
                if (caret == null) return false;

                SetProperty(caret, "Line", lineNumber - 1); // 0-based internally
                SetProperty(caret, "Column", 0);

                // Scroll to make the line visible
                try
                {
                    var scrollToMethod = textArea.GetType().GetMethod("ScrollTo",
                        new[] { typeof(int) });
                    if (scrollToMethod != null)
                        scrollToMethod.Invoke(textArea, new object[] { lineNumber - 1 });
                }
                catch { }

                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Get the current cursor position (line and column, 1-based).
        /// </summary>
        public int[] GetCursorPosition()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var caret = GetProperty(textArea, "Caret");
                if (caret == null) return null;

                int line = (int)GetProperty(caret, "Line") + 1;   // convert to 1-based
                int col = (int)GetProperty(caret, "Column") + 1;
                return new[] { line, col };
            }
            catch { return null; }
        }

        /// <summary>
        /// Get total line count of the active document.
        /// </summary>
        public int GetLineCount()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return 0;

                var document = GetProperty(textArea, "Document");
                if (document == null) return 0;

                var totalLines = GetProperty(document, "TotalNumberOfLines");
                if (totalLines is int count) return count;

                // Fallback: count from text content
                string text = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                if (text != null) return text.Split('\n').Length;

                return 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Undo the last edit in the active editor.
        /// </summary>
        public bool Undo()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return false;
                var document = GetProperty(textArea, "Document");
                if (document == null) return false;

                var undoStack = GetProperty(document, "UndoStack");
                if (undoStack == null) return false;

                var canUndo = GetProperty(undoStack, "CanUndo");
                if (canUndo is bool && !(bool)canUndo) return false;

                var undoMethod = undoStack.GetType().GetMethod("Undo");
                if (undoMethod != null) { undoMethod.Invoke(undoStack, null); return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Redo the last undone edit in the active editor.
        /// </summary>
        public bool Redo()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return false;
                var document = GetProperty(textArea, "Document");
                if (document == null) return false;

                var undoStack = GetProperty(document, "UndoStack");
                if (undoStack == null) return false;

                var canRedo = GetProperty(undoStack, "CanRedo");
                if (canRedo is bool && !(bool)canRedo) return false;

                var redoMethod = undoStack.GetType().GetMethod("Redo");
                if (redoMethod != null) { redoMethod.Invoke(undoStack, null); return true; }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Save the active document.
        /// </summary>
        public bool SaveActiveDocument()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return false;
                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow");
                if (activeWindow == null) return false;
                var viewContent = GetProperty(activeWindow, "ViewContent")
                               ?? GetProperty(activeWindow, "ActiveViewContent");
                if (viewContent == null) return false;

                var saveMethod = viewContent.GetType().GetMethod("Save",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (saveMethod != null) { saveMethod.Invoke(viewContent, null); return true; }

                // Fallback: try SaveFile
                var saveFileMethod = viewContent.GetType().GetMethod("SaveFile",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (saveFileMethod != null) { saveFileMethod.Invoke(viewContent, null); return true; }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Close the active document tab.
        /// </summary>
        public bool CloseActiveDocument()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return false;
                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow");
                if (activeWindow == null) return false;

                var closeMethod = activeWindow.GetType().GetMethod("CloseWindow",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                if (closeMethod != null) { closeMethod.Invoke(activeWindow, new object[] { true }); return true; }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Get list of all open file paths in the editor.
        /// </summary>
        public List<string> GetOpenFiles()
        {
            var files = new List<string>();
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return files;

                var viewContents = GetProperty(workbench, "ViewContentCollection") as System.Collections.IEnumerable;
                if (viewContents == null) return files;

                foreach (var vc in viewContents)
                {
                    var fn = GetProperty(vc, "FileName") ?? GetProperty(vc, "PrimaryFileName");
                    if (fn != null)
                    {
                        string path = fn.ToString();
                        if (!string.IsNullOrEmpty(path)) files.Add(path);
                    }
                }
            }
            catch { }
            return files;
        }

        /// <summary>
        /// Get text of a specific line (1-based) from the active editor buffer (includes unsaved changes).
        /// </summary>
        public string GetLineText(int lineNumber)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;
                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                var getLineMethod = document.GetType().GetMethod("GetLineSegment", new[] { typeof(int) });
                if (getLineMethod != null)
                {
                    var segment = getLineMethod.Invoke(document, new object[] { lineNumber - 1 });
                    if (segment != null)
                    {
                        int offset = (int)GetProperty(segment, "Offset");
                        int length = (int)GetProperty(segment, "Length");

                        string fullText = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                        if (fullText != null && offset >= 0 && offset + length <= fullText.Length)
                            return fullText.Substring(offset, length);
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get text of a range of lines (1-based) from the active editor buffer.
        /// Returns lines with line numbers prefixed, one per line.
        /// </summary>
        public string GetLinesRange(int startLine, int endLine)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;
                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                var totalLinesProp = document.GetType().GetProperty("TotalNumberOfLines");
                int totalLines = totalLinesProp != null ? (int)totalLinesProp.GetValue(document, null) : 0;
                if (totalLines == 0) return null;

                if (startLine < 1) startLine = 1;
                if (endLine > totalLines) endLine = totalLines;
                if (startLine > endLine) return null;

                string fullText = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                if (fullText == null) return null;

                var getLineMethod = document.GetType().GetMethod("GetLineSegment", new[] { typeof(int) });
                if (getLineMethod == null) return null;

                var sb = new System.Text.StringBuilder();
                for (int i = startLine; i <= endLine; i++)
                {
                    var segment = getLineMethod.Invoke(document, new object[] { i - 1 });
                    if (segment != null)
                    {
                        int offset = (int)GetProperty(segment, "Offset");
                        int length = (int)GetProperty(segment, "Length");
                        string lineText = (offset >= 0 && offset + length <= fullText.Length)
                            ? fullText.Substring(offset, length) : "";
                        sb.AppendLine(i + "\t" + lineText);
                    }
                    else
                    {
                        sb.AppendLine(i + "\t");
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        /// <summary>
        /// Search for text in the active editor buffer. Returns list of (line, column, text) matches.
        /// </summary>
        public List<int[]> FindInFile(string searchText, bool caseSensitive = false)
        {
            var results = new List<int[]>();
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return results;
                var document = GetProperty(textArea, "Document");
                if (document == null) return results;

                string content = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                if (string.IsNullOrEmpty(content)) return results;

                var comparison = caseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                int pos = 0;
                while (pos < content.Length)
                {
                    int idx = content.IndexOf(searchText, pos, comparison);
                    if (idx < 0) break;

                    // Convert offset to line/col
                    int line = 1;
                    int lastLineStart = 0;
                    for (int i = 0; i < idx; i++)
                    {
                        if (content[i] == '\n') { line++; lastLineStart = i + 1; }
                    }
                    int col = idx - lastLineStart + 1;
                    results.Add(new[] { line, col });

                    pos = idx + 1;
                }
            }
            catch { }
            return results;
        }

        /// <summary>
        /// Check if the active document has unsaved changes.
        /// </summary>
        public bool IsModified()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return false;
                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow");
                if (activeWindow == null) return false;
                var viewContent = GetProperty(activeWindow, "ViewContent")
                               ?? GetProperty(activeWindow, "ActiveViewContent");
                if (viewContent == null) return false;

                var isDirty = GetProperty(viewContent, "IsDirty");
                if (isDirty is bool) return (bool)isDirty;

                var isModified = GetProperty(viewContent, "IsModified");
                if (isModified is bool) return (bool)isModified;

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Toggle line comment (!) on the specified lines.
        /// </summary>
        public InsertResult ToggleComment(int startLine, int endLine)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");
                var document = GetProperty(textArea, "Document");
                if (document == null) return InsertResult.Failed("Cannot access document");

                string content = (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
                if (content == null) return InsertResult.Failed("Cannot read document");

                var lines = content.Split('\n');
                if (startLine < 1 || endLine > lines.Length)
                    return InsertResult.Failed("Line range out of bounds");

                // Check if all lines in range are already commented
                bool allCommented = true;
                for (int i = startLine - 1; i < endLine; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("!"))
                    {
                        allCommented = false;
                        break;
                    }
                }

                // Toggle
                for (int i = startLine - 1; i < endLine; i++)
                {
                    if (allCommented)
                    {
                        // Uncomment: remove first !
                        int bangIdx = lines[i].IndexOf('!');
                        if (bangIdx >= 0)
                            lines[i] = lines[i].Substring(0, bangIdx) + lines[i].Substring(bangIdx + 1);
                    }
                    else
                    {
                        // Comment: add ! at start of content
                        int firstNonSpace = 0;
                        while (firstNonSpace < lines[i].Length && lines[i][firstNonSpace] == ' ')
                            firstNonSpace++;
                        lines[i] = lines[i].Substring(0, firstNonSpace) + "!" + lines[i].Substring(firstNonSpace);
                    }
                }

                string updated = string.Join("\n", lines);
                var textProp = document.GetType().GetProperty("TextContent", BindingFlags.Public | BindingFlags.Instance);
                if (textProp != null && textProp.CanWrite)
                    textProp.SetValue(document, updated, null);
                else
                {
                    textProp = document.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null && textProp.CanWrite)
                        textProp.SetValue(document, updated, null);
                    else
                        return InsertResult.Failed("Cannot set document text");
                }

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Gets the file path of the currently loaded solution in the IDE.
        /// </summary>
        public static string GetOpenSolutionPath()
        {
            try
            {
                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return null;

                var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
                if (projectServiceType == null) return null;

                // Try OpenSolution.FileName
                var openSolution = projectServiceType.GetProperty("OpenSolution",
                    BindingFlags.Public | BindingFlags.Static);
                if (openSolution != null)
                {
                    var solution = openSolution.GetValue(null, null);
                    if (solution != null)
                    {
                        var prop = solution.GetType().GetProperty("FileName", BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null)
                        {
                            var val = prop.GetValue(solution, null);
                            if (val != null) return val.ToString();
                        }
                        var dirProp = solution.GetType().GetProperty("Directory", BindingFlags.Public | BindingFlags.Instance);
                        if (dirProp != null)
                        {
                            var val = dirProp.GetValue(solution, null);
                            if (val != null) return val.ToString();
                        }
                    }
                }

                // Fallback: CurrentSolution
                var currentSolution = projectServiceType.GetProperty("CurrentSolution",
                    BindingFlags.Public | BindingFlags.Static);
                if (currentSolution != null)
                {
                    var solution = currentSolution.GetValue(null, null);
                    if (solution != null)
                    {
                        var prop = solution.GetType().GetProperty("FileName", BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null)
                        {
                            var val = prop.GetValue(solution, null);
                            if (val != null) return val.ToString();
                        }
                    }
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Opens a Clarion solution or project file in the IDE.
        /// Prompts to save the current solution first (BeforeLoadSolution handles this).
        /// </summary>
        public static string OpenSolution(string fileName)
        {
            try
            {
                if (!System.IO.File.Exists(fileName))
                    return "Error: file not found: " + fileName;

                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return "Error: could not load SharpDevelop assembly";

                var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
                if (projectServiceType == null) return "Error: ProjectService type not found";

                var beforeLoad = projectServiceType.GetMethod("BeforeLoadSolution",
                    BindingFlags.Public | BindingFlags.Static);
                if (beforeLoad != null)
                {
                    bool canLoad = (bool)beforeLoad.Invoke(null, null);
                    if (!canLoad) return "Cancelled: user chose not to close the current solution";
                }

                var loadMethod = projectServiceType.GetMethod("LoadSolutionOrProject",
                    BindingFlags.Public | BindingFlags.Static);
                if (loadMethod == null) return "Error: LoadSolutionOrProject method not found";

                loadMethod.Invoke(null, new object[] { fileName });
                return "Opened solution: " + fileName;
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        /// <summary>
        /// Close the currently open solution in the IDE.
        /// Uses ProjectService.CloseSolution() via reflection.
        /// </summary>
        public static string CloseSolution()
        {
            try
            {
                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return "Error: could not load SharpDevelop assembly";

                var projectServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
                if (projectServiceType == null) return "Error: ProjectService type not found";

                // Check if a solution is open
                var openSolutionProp = projectServiceType.GetProperty("OpenSolution",
                    BindingFlags.Public | BindingFlags.Static);
                if (openSolutionProp != null)
                {
                    var currentSolution = openSolutionProp.GetValue(null, null);
                    if (currentSolution == null) return "No solution is currently open";
                }

                // Try CloseSolution method
                var closeMethod = projectServiceType.GetMethod("CloseSolution",
                    BindingFlags.Public | BindingFlags.Static);
                if (closeMethod != null)
                {
                    closeMethod.Invoke(null, null);
                    return "Solution closed";
                }

                // Fallback: try CloseSolution(bool) overload
                closeMethod = projectServiceType.GetMethod("CloseSolution",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                if (closeMethod != null)
                {
                    closeMethod.Invoke(null, new object[] { true });
                    return "Solution closed";
                }

                return "Error: CloseSolution method not found on ProjectService";
            }
            catch (Exception ex) { return "Error: " + ex.Message; }
        }

        public static string GetClarionInstallPath()
        {
            try
            {
                var asm = typeof(WorkbenchSingleton).Assembly;
                string binPath = Path.GetDirectoryName(asm.Location);
                string clarionRoot = Path.GetDirectoryName(binPath);
                if (Directory.Exists(Path.Combine(clarionRoot, "accessory")))
                    return clarionRoot;

                string appBase = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                clarionRoot = Path.GetDirectoryName(appBase);
                if (Directory.Exists(Path.Combine(clarionRoot, "accessory")))
                    return clarionRoot;

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Walks the IDE object model and reports what it finds — for debugging
        /// when GetActiveDocumentPath() returns null.
        /// </summary>
        public string GetDiagnosticInfo()
        {
            var sb = new System.Text.StringBuilder();

            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                sb.AppendLine($"Workbench: {(workbench != null ? workbench.GetType().FullName : "NULL")}");

                if (workbench == null) return sb.ToString();

                // List all properties on the workbench
                sb.AppendLine("\nWorkbench properties:");
                foreach (var prop in workbench.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        var val = prop.GetValue(workbench, null);
                        sb.AppendLine($"  .{prop.Name} = {val?.GetType().Name ?? "null"} [{val}]");
                    }
                    catch { sb.AppendLine($"  .{prop.Name} = (error reading)"); }
                }

                // Try ActiveWorkbenchWindow
                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow");
                sb.AppendLine($"\nActiveWorkbenchWindow: {(activeWindow != null ? activeWindow.GetType().FullName : "NULL")}");

                if (activeWindow != null)
                {
                    sb.AppendLine("  Properties:");
                    foreach (var prop in activeWindow.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try
                        {
                            var val = prop.GetValue(activeWindow, null);
                            sb.AppendLine($"    .{prop.Name} = {val?.GetType().Name ?? "null"} [{val}]");
                        }
                        catch { sb.AppendLine($"    .{prop.Name} = (error reading)"); }
                    }

                    // Try ViewContent
                    var viewContent = GetProperty(activeWindow, "ViewContent")
                                   ?? GetProperty(activeWindow, "ActiveViewContent")
                                   ?? activeWindow;

                    sb.AppendLine($"\n  ViewContent: {viewContent?.GetType().FullName ?? "NULL"}");

                    if (viewContent != null)
                    {
                        sb.AppendLine("    Properties:");
                        foreach (var prop in viewContent.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            try
                            {
                                var val = prop.GetValue(viewContent, null);
                                string valStr = val?.ToString() ?? "null";
                                if (valStr.Length > 100) valStr = valStr.Substring(0, 100) + "...";
                                sb.AppendLine($"      .{prop.Name} = {val?.GetType().Name ?? "null"} [{valStr}]");
                            }
                            catch { sb.AppendLine($"      .{prop.Name} = (error reading)"); }
                        }
                    }
                }

                // Try ActiveContent as fallback
                var activeContent = GetProperty(workbench, "ActiveContent");
                if (activeContent != null && activeContent != activeWindow)
                {
                    sb.AppendLine($"\nActiveContent: {activeContent.GetType().FullName}");
                    foreach (var prop in activeContent.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try
                        {
                            var val = prop.GetValue(activeContent, null);
                            sb.AppendLine($"  .{prop.Name} = {val?.GetType().Name ?? "null"} [{val}]");
                        }
                        catch { sb.AppendLine($"  .{prop.Name} = (error reading)"); }
                    }
                }

                // List all open ViewContents
                var viewContents = GetProperty(workbench, "ViewContentCollection") as System.Collections.IEnumerable;
                if (viewContents != null)
                {
                    sb.AppendLine("\nOpen ViewContents:");
                    int i = 0;
                    foreach (var vc in viewContents)
                    {
                        var fn = GetProperty(vc, "FileName") ?? GetProperty(vc, "PrimaryFileName") ?? GetProperty(vc, "TitleName");
                        sb.AppendLine($"  [{i}] {vc.GetType().Name}: {fn}");
                        i++;
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"\nDiagnostic error: {ex.Message}");
            }

            return sb.ToString();
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == ':';
        }

        private object GetActiveTextArea()
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) return null;

            var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow") ?? GetProperty(workbench, "ActiveContent");
            if (activeWindow == null) return null;

            var viewContent = GetProperty(activeWindow, "ViewContent") ?? GetProperty(activeWindow, "ActiveViewContent") ?? activeWindow;

            var textEditor = GetProperty(viewContent, "TextEditorControl");
            if (textEditor != null)
            {
                var result = GetTextAreaFromEditor(textEditor);
                if (result != null) return result;
            }

            var control = GetProperty(viewContent, "Control");
            if (control != null)
            {
                var result = GetTextAreaFromEditor(control);
                if (result != null) return result;
                if (control is Control wc)
                {
                    result = FindTextAreaInControls(wc);
                    if (result != null) return result;
                }
            }

            var secondary = GetProperty(viewContent, "SecondaryViewContents") as System.Collections.IEnumerable;
            if (secondary != null)
            {
                foreach (var svc in secondary)
                {
                    if (GetProperty(svc, "Control") is Control wc)
                    {
                        var result = FindTextAreaInControls(wc);
                        if (result != null) return result;
                    }
                }
            }
            return null;
        }

        private object GetTextAreaFromEditor(object editor)
        {
            if (editor == null) return null;
            var tac = GetProperty(editor, "ActiveTextAreaControl");
            if (tac != null)
            {
                var ta = GetProperty(tac, "TextArea");
                if (ta != null && GetProperty(ta, "Document") != null && GetProperty(ta, "Caret") != null) return ta;
            }
            if (GetProperty(editor, "Document") != null && GetProperty(editor, "Caret") != null) return editor;
            return null;
        }

        private object FindTextAreaInControls(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                var result = GetTextAreaFromEditor(child) ?? FindTextAreaInControls(child);
                if (result != null) return result;
            }
            return null;
        }

        private object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj, null);
                var field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                return field?.GetValue(obj);
            }
            catch { return null; }
        }

        private void SetProperty(object obj, string name, object value)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop?.CanWrite == true) prop.SetValue(obj, value, null);
            }
            catch { }
        }
    }
}
