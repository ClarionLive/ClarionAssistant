namespace ClarionAssistant.Services
{
    /// <summary>
    /// Result of an editor mutation: did it work, and if not, why.
    ///
    /// MOVED OUT OF EditorService.cs (ticket d051fbd1). It is a plain POCO with no IDE
    /// dependency, but it lived in a file that imports ICSharpCode.SharpDevelop.Gui and
    /// ICSharpCode.TextEditor — so any build that wanted this type had to drag the whole
    /// IDE in with it. It sits in its own file so the standalone MCP server can share it.
    ///
    /// Keep it free of IDE types. It crosses the boundary between the addin and the
    /// standalone server, and that is only safe while it stays a value.
    /// </summary>
    public class InsertResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public static InsertResult Succeeded() => new InsertResult { Success = true };
        public static InsertResult Failed(string message) => new InsertResult { Success = false, ErrorMessage = message };
    }
}
