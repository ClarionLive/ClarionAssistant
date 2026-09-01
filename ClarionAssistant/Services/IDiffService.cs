using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The diff viewer, as McpToolRegistry needs it (ticket d051fbd1).
    ///
    /// IDE-ONLY, for a different reason than IAppTreeService. Nothing here touches Clarion's
    /// binary formats — it is the PRESENTATION that cannot move. show_diff opens a WebView2
    /// panel inside the IDE and then waits for a human to approve, reject or annotate; GetResult
    /// polls that outcome. A standalone MCP server has no window to show and nobody sitting in
    /// front of it, so "show a diff" has no meaning there.
    ///
    /// Worth stating because it is tempting to assume otherwise: computing a diff IS portable,
    /// and GetContent() is nearly so. What is not portable is the interactive review loop the
    /// other members exist to serve. If a caller ever wants only the computed text, that should
    /// become a separate, genuinely agnostic member rather than a "headless" implementation of
    /// this one — a viewer that shows nothing and always returns "pending" would be worse than
    /// an absent tool, because a client would sit waiting for an approval that cannot arrive.
    ///
    /// Exists so the single shared McpToolRegistry.cs compiles in both builds. The standalone
    /// server does not implement it and does not register the tools that need it.
    /// </summary>
    public interface IDiffService
    {
        string ShowDiff(string title, string originalText, string modifiedText, string language = "clarion",
            bool ignoreWhitespace = false, bool useMonaco = false, DiffFileContext fileContext = null);

        string ShowDiffFromFile(string title, string originalFile, int startLine, int endLine,
            string modifiedText, string language = "clarion", bool ignoreWhitespace = false, bool useMonaco = false);

        string ShowDiffFromFiles(string title, string originalFile, int origStartLine, int origEndLine,
            string modifiedFile, int modStartLine, int modEndLine, string language = "clarion",
            bool ignoreWhitespace = false, bool useMonaco = false);

        /// <summary>Outcome of the review: pending / approved / notes / cancelled.</summary>
        Dictionary<string, string> GetResult();

        /// <summary>The unified diff for the current or most recent ShowDiff.</summary>
        Dictionary<string, object> GetContent();
    }
}
