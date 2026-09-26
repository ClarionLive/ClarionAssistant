using System;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>What <see cref="LspService.EnsureRunning(string)"/> actually did (ticket 77aceec5).</summary>
    public enum LspStartOutcome
    {
        /// <summary>A client was already running; nothing was started.</summary>
        AlreadyRunning,
        /// <summary>This call spawned the server and the handshake completed.</summary>
        Started,
        /// <summary>The shared ClarionLsp addin is active, so the bundled server is deliberately not started.</summary>
        SharedActive,
        /// <summary>No solution was named and the host supplied none. Nothing was spawned.</summary>
        NoSolution,
        /// <summary>No server.js could be resolved. Nothing was spawned.</summary>
        NoServer,
        /// <summary>server.js and a solution were in hand and the start was attempted, but it failed
        /// (process spawn or the initialize handshake). The ONLY outcome where blaming the
        /// handshake can be right.</summary>
        StartFailed,
        /// <summary>An unexpected exception escaped the start sequence.</summary>
        Error
    }

    /// <summary>
    /// The outcome of an LSP start, returned instead of only traced.
    ///
    /// WHY THIS EXISTS. EnsureRunning used to return void and explain itself only to LspTrace, which
    /// in the standalone server is stderr under --debug and in the addin is a log nobody is reading
    /// mid-conversation. Its callers therefore had to GUESS why the client was not running, and
    /// lsp_start guessed "the client handshake" for every case - including the commonest one, where
    /// no solution was known and nothing was ever spawned (ticket 77aceec5, measured live in Clarion
    /// 12: a plain Chat tab, lsp_start named the .sln directory, and the answer blamed a handshake
    /// that never happened). Now the caller reports what actually occurred.
    /// </summary>
    public sealed class LspStartResult
    {
        public LspStartOutcome Outcome { get; private set; }

        /// <summary>Human-readable reason. Always set for the not-running outcomes.</summary>
        public string Detail { get; private set; }

        /// <summary>The .sln the start used or would have used. Null when none was known.</summary>
        public string SolutionPath { get; private set; }

        /// <summary>Where <see cref="SolutionPath"/> came from ("workspace_path", "host", ...).</summary>
        public string SolutionSource { get; private set; }

        public string ServerJs { get; private set; }
        public string ServerSource { get; private set; }

        public bool IsRunning
        {
            get
            {
                return Outcome == LspStartOutcome.AlreadyRunning
                    || Outcome == LspStartOutcome.Started
                    || Outcome == LspStartOutcome.SharedActive;
            }
        }

        public LspStartResult(LspStartOutcome outcome, string detail,
            string solutionPath, string solutionSource, string serverJs, string serverSource)
        {
            Outcome = outcome;
            Detail = detail;
            SolutionPath = solutionPath;
            SolutionSource = solutionSource;
            ServerJs = serverJs;
            ServerSource = serverSource;
        }

        public static LspStartResult Of(LspStartOutcome outcome, string detail)
        {
            return new LspStartResult(outcome, detail, null, null, null, null);
        }

        /// <summary>
        /// The sentence a user-facing "not running" error should carry: WHY the server is not up.
        /// Used by every lsp_* tool, so a bare "LSP not running" no longer hides the reason.
        /// </summary>
        public string DescribeWhyNotRunning()
        {
            switch (Outcome)
            {
                case LspStartOutcome.NoSolution:
                    return NoSolutionMessage;
                case LspStartOutcome.NoServer:
                    return "no language server (server.js) could be found"
                        + (string.IsNullOrEmpty(Detail) ? "." : ": " + Detail)
                        + " Install the Clarion extension for VS Code, place server.js in the lsp-server "
                        + "subfolder next to the addin, or set the 'Lsp.ServerPath' setting.";
                case LspStartOutcome.StartFailed:
                    return "the language server was spawned for " + (SolutionPath ?? "(unknown solution)")
                        + " but did not complete its start (process launch or initialize handshake). "
                        + "Call lsp_start for the full diagnostic.";
                case LspStartOutcome.Error:
                    return "the start failed with an error: " + (Detail ?? "(no detail)");
                default:
                    return Detail ?? Outcome.ToString();
            }
        }

        /// <summary>
        /// The no-solution explanation, shared by lsp_start and the other lsp_* tools so they cannot
        /// drift apart. Names both ways out, because the user who hits this has usually done nothing
        /// wrong: a plain Chat tab launched before a solution was open has none to offer.
        /// </summary>
        public const string NoSolutionMessage =
            "No solution selected, so the language server has no workspace to start in. "
            + "Use 'Work With Open Solution' in Clarion Assistant, or call lsp_start with "
            + "workspace_path set to the .sln file (or the folder holding exactly one .sln).";

        /// <summary>
        /// Turns an lsp_start workspace_path argument into ONE .sln path, or explains why it cannot.
        ///
        /// Accepts a .sln file, or a directory holding exactly one .sln at its top level. A directory
        /// with several is REFUSED rather than guessed at - picking the first would start the server
        /// on a solution the user never named, and every answer after that would look authoritative
        /// (the same rule StandaloneWorkspace applies to --solution discovery). None is refused too.
        /// Pure file-system logic with no IDE or LSP dependency, so it is unit-tested directly.
        /// </summary>
        public static string ResolveSolutionArgument(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || path.Trim().Length == 0)
            {
                error = "workspace_path is empty.";
                return null;
            }

            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch (Exception ex)
            {
                error = "workspace_path '" + path + "' is not a usable path: " + ex.Message;
                return null;
            }

            if (File.Exists(full))
            {
                if (!string.Equals(Path.GetExtension(full), ".sln", StringComparison.OrdinalIgnoreCase))
                {
                    error = "workspace_path '" + full + "' is a file but not a .sln. Pass the .sln, "
                        + "or the folder that holds it.";
                    return null;
                }
                return full;
            }

            if (!Directory.Exists(full))
            {
                error = "workspace_path '" + full + "' does not exist.";
                return null;
            }

            string[] found;
            try { found = Directory.GetFiles(full, "*.sln", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                error = "could not scan '" + full + "' for a .sln: " + ex.Message;
                return null;
            }

            if (found.Length == 1) return found[0];

            if (found.Length == 0)
            {
                error = "no .sln file in '" + full + "'. Pass the .sln path itself, or the folder "
                    + "that holds it.";
                return null;
            }

            Array.Sort(found, StringComparer.OrdinalIgnoreCase);
            var names = new string[found.Length];
            for (int i = 0; i < found.Length; i++) names[i] = Path.GetFileName(found[i]);
            error = found.Length + " .sln files in '" + full + "' (" + string.Join(", ", names)
                + "). Pass the .sln path itself rather than have the server guess which you meant.";
            return null;
        }
    }
}
