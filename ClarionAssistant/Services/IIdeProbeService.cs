using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Asks the live Clarion IDE about itself (ticket d051fbd1).
    ///
    /// Backs the introspection and native-probe tools — inspect_ide, dump_object_api,
    /// dump_appmain_api, embeditor_get_selection and the popup/CLA-list diagnostics. All of them
    /// reflect over a running IDE's object graph or enumerate its native child windows, so every
    /// member here is permanently IDE-only in the same way IAppTreeService is: there is nothing to
    /// reflect over in a process with no IDE in it.
    ///
    /// Consolidates THREE previously-static call sites that the shared McpToolRegistry.cs named
    /// directly — IdeReflectionService (9 members), NativeProbeService (7) and one call into
    /// ClarionAssistant.Terminal.ModernEmbeditorViewContent. They are grouped rather than given
    /// three interfaces because they are one concern from the registry's point of view ("ask the
    /// IDE about itself") and because three interfaces with one implementation each would be
    /// ceremony, not design.
    ///
    /// The standalone MCP server does not implement this and does not register the tools that
    /// need it.
    /// </summary>
    public interface IIdeProbeService
    {
        // --- IDE object-graph reflection (was IdeReflectionService) ---
        string InspectActiveView();
        string ReadActiveEditorText();
        string ListAllWindows();
        string ListAllPads();
        string InspectPath(string dotPath);
        string DiscoverAutomationTypes();
        string InspectApplicationDetails();
        string InspectEmbedDetails();
        string ListLoadedAssemblies();

        // --- native window probes (was NativeProbeService) ---
        string DumpNativeChain();
        string ProbeClaListRead();
        string EnumClaLists();
        string PopupArm();
        string PopupArmInject();
        string PopupMark(string label);
        string PopupReport();

        /// <summary>
        /// HeaderTitle of the active workbench window, e.g.
        /// "ProcName - Embeditor - (module001.clw)". Null when there is no workbench, no active
        /// window, no view content, or the property is absent. Callers parse the module filename
        /// out of it; returning the raw title keeps that parsing in the registry where its regex
        /// and error messages already live.
        /// </summary>
        string GetActiveViewHeaderTitle();

        /// <summary>
        /// FileName of the active workbench window's view content - for an embeditor window that
        /// is the .app path. Null when unavailable. Paired with GetActiveViewHeaderTitle(): both
        /// are read inside one UI-thread handler, so they cannot observe different active views.
        /// </summary>
        string GetActiveViewFileName();

        /// <summary>
        /// The dictionary currently open in the IDE, as an opaque object (the DCT), or null.
        /// Deliberately object: the concrete type lives in Clarion's own assemblies, and naming
        /// it here would reintroduce exactly the dependency this interface removes. The registry
        /// already reflects over it.
        /// </summary>
        object FindOpenDictionary();

        /// <summary>
        /// Flush any dirty Monaco editors to disk before a build, so ClarionCL compiles what the
        /// developer can see rather than the last saved version. Was
        /// MonacoClarionEditor.SaveAllDirtyBeforeBuild().
        /// </summary>
        void SaveAllDirtyEditors();

        /// <summary>
        /// The selection in the focused CA Embeditor (Monaco/WebView2), with its 1-based
        /// line/column range. Was a direct call to
        /// ClarionAssistant.Terminal.ModernEmbeditorViewContent.GetFocusedSelection().
        /// Distinct from IEditorService.GetSelectedText(), which reads the NATIVE editor only.
        /// </summary>
        Dictionary<string, object> GetFocusedEmbeditorSelection();
    }
}
