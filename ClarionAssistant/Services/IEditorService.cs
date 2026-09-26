using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The live Clarion IDE text editor, as McpToolRegistry needs it (ticket d051fbd1).
    ///
    /// WHY THIS EXISTS. The registry has no IDE imports of its own — its usings are pure
    /// System.* plus SQLite and Web.Script.Serialization. Its ONLY hard compile blocker
    /// outside the addin was the concrete EditorService, which imports
    /// ICSharpCode.SharpDevelop.Gui, ICSharpCode.TextEditor and System.Windows.Forms.
    /// Depending on this interface instead is what lets the whole 4,500-line registry
    /// compile in the standalone MCP server.
    ///
    /// THIS INTERFACE IS NOT A PORTABILITY PROMISE. Every member here drives a live editor
    /// window. The standalone server does NOT implement it and must not pretend to: the
    /// tools that need it are simply not registered there. An implementation that returned
    /// plausible nulls would be worse than none, because an MCP client would advertise
    /// "get the active document" and silently get nothing.
    ///
    /// Scope: exactly the members McpToolRegistry uses, and nothing more. EditorService has
    /// a wider surface (including overloads taking a textArea object) which stays off the
    /// interface deliberately — a seam that mirrors the whole class is not a seam.
    ///
    /// The two STATIC members the registry also used — GetOpenSolutionPath and
    /// GetClarionInstallPath — are NOT here. They answer "which solution / where is Clarion",
    /// which is workspace context rather than editor manipulation, and both have meaningful
    /// non-IDE answers. They live on IWorkspaceContext.
    /// </summary>
    public interface IEditorService
    {
        // --- reading the active document ---
        string GetActiveDocumentContent();
        string GetActiveDocumentPath();
        string GetSelectedText();
        string GetWordUnderCursor();
        int[] GetCursorPosition();
        int GetLineCount();
        string GetLineText(int lineNumber);
        string GetLinesRange(int startLine, int endLine);
        List<string> GetOpenFiles();
        List<int[]> FindInFile(string searchText, bool caseSensitive = false);
        bool IsModified();

        // --- mutating the active document ---
        InsertResult InsertTextAtCaret(string text);
        InsertResult ReplaceText(string oldText, string newText);
        InsertResult ReplaceRange(int startLine, int startCol, int endLine, int endCol, string newText);
        InsertResult DeleteRange(int startLine, int startCol, int endLine, int endCol);
        InsertResult SelectRange(int startLine, int startCol, int endLine, int endCol);
        InsertResult ToggleComment(int startLine, int endLine);
        InsertResult AppendTextToFile(string filePath, string text);
        bool Undo();
        bool Redo();

        // --- document lifecycle and navigation ---
        bool GoToLine(int lineNumber);
        void NavigateToFileAndLine(string filePath, int lineNumber);
        void OpenFileOnly(string filePath);
        bool SaveActiveDocument();
        bool CloseActiveDocument();
    }
}
