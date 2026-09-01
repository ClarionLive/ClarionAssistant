using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// "Which solution am I working on, and where is Clarion." (ticket d051fbd1)
    ///
    /// THIS IS THE SEAM THAT MOVES TOOLS, and it is worth being precise about why.
    ///
    /// Classifying all 115 MCP tools showed 61 are not UI-bound, but 11 of those still
    /// reached into IDE-held state and so looked unmovable. Reading what they actually did
    /// showed the dependency is almost never IDE BEHAVIOUR — it is ambient session state
    /// that happens to hang off a WinForms control:
    ///
    ///     CurrentSolutionPath, RedFile, CurrentDbPath, CurrentVersionConfig,
    ///     ActiveRedFileService, BuildIndexLibraryPaths(), RunIndex()
    ///
    /// The build tools are the clearest case. build_solution / build_app / generate_source
    /// shell straight out to ClarionCL and MSBuild — genuinely editor-agnostic work — and
    /// their ENTIRE IDE dependency was one call, EditorService.GetOpenSolutionPath(), used
    /// as a FALLBACK when no path was supplied (McpToolRegistry.cs:1811, :2853).
    ///
    /// So this is not coupling to the IDE. It is coupling to "someone tells me the
    /// workspace". Ten of those eleven tools move behind this interface. Only
    /// get_ca_project_info is genuinely IDE-coupled (DataDictionary / ProjectService /
    /// SharpDevelop) and stays behind.
    ///
    /// IMPLEMENTATIONS
    ///   addin       — AssistantChatControl, backed by the IDE's open solution. Unchanged
    ///                 behaviour; this interface only names what it already exposed.
    ///   standalone  — resolved from CLI arguments, the working directory, or config. The
    ///                 client says which solution it means, which is the honest answer when
    ///                 there is no IDE to ask.
    ///
    /// Keep every member here answerable WITHOUT a running IDE. A member that can only be
    /// satisfied by the IDE belongs on IEditorService, not on this.
    /// </summary>
    public interface IWorkspaceContext
    {
        /// <summary>Full path of the active .sln, or null when none is selected.</summary>
        string CurrentSolutionPath { get; }

        /// <summary>Clarion version/build configuration for the active solution.</summary>
        ClarionVersionConfig CurrentVersionConfig { get; }

        /// <summary>Parsed redirection (.red) file for the active solution.</summary>
        RedFileService RedFile { get; }

        /// <summary>
        /// Redirection service for the active context. Distinct from <see cref="RedFile"/>:
        /// the addin resolves this per active project, so it can differ from the solution-level
        /// one. Preserved rather than merged, because collapsing them would silently change
        /// which search paths several tools resolve against.
        /// </summary>
        RedFileService ActiveRedFileService { get; }

        /// <summary>Path of the CodeGraph database for the active solution.</summary>
        string CurrentDbPath { get; }

        /// <summary>Library/search paths used when indexing the active solution.</summary>
        List<string> BuildIndexLibraryPaths();

        /// <summary>Index or re-index the active solution into its CodeGraph database.</summary>
        void RunIndex(bool incremental);

        /// <summary>
        /// The solution the HOST believes is open, which may differ from CurrentSolutionPath.
        /// Was the static EditorService.GetOpenSolutionPath(). In the addin it asks the IDE;
        /// standalone it returns null and callers fall back to CurrentSolutionPath — which is
        /// exactly the precedence the build tools already used.
        /// </summary>
        string GetHostOpenSolutionPath();

        /// <summary>
        /// Root of the Clarion installation. Was the static
        /// EditorService.GetClarionInstallPath(). Mostly portable already — it prefers the
        /// loaded SharpDevelop assembly's location but falls back to
        /// AppDomain.CurrentDomain.BaseDirectory, which is what the standalone build uses.
        /// </summary>
        string GetClarionInstallPath();
    }
}
