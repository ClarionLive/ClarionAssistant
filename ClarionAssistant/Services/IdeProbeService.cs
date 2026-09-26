using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Addin-side implementation of <see cref="IIdeProbeService"/> (ticket d051fbd1).
    ///
    /// Pure forwarding. Every member delegates to the static it replaced, so behaviour is
    /// identical to the direct calls McpToolRegistry used to make. The only reason this class
    /// exists is that the registry is now SHARED with the standalone MCP server, and a static
    /// call into an IDE-coupled type cannot be compiled there — an interface can.
    ///
    /// This file is deliberately NOT linked into mcp-server\ClarionMcpServer.csproj. It names
    /// IdeReflectionService, NativeProbeService and ModernEmbeditorViewContent, all of which are
    /// IDE-coupled; linking it would defeat the whole exercise.
    /// </summary>
    public class IdeProbeService : IIdeProbeService
    {
        public string InspectActiveView() { return IdeReflectionService.InspectActiveView(); }
        public string ReadActiveEditorText() { return IdeReflectionService.ReadActiveEditorText(); }
        public string ListAllWindows() { return IdeReflectionService.ListAllWindows(); }
        public string ListAllPads() { return IdeReflectionService.ListAllPads(); }
        public string InspectPath(string dotPath) { return IdeReflectionService.InspectPath(dotPath); }
        public string DiscoverAutomationTypes() { return IdeReflectionService.DiscoverAutomationTypes(); }
        public string InspectApplicationDetails() { return IdeReflectionService.InspectApplicationDetails(); }
        public string InspectEmbedDetails() { return IdeReflectionService.InspectEmbedDetails(); }
        public string ListLoadedAssemblies() { return IdeReflectionService.ListLoadedAssemblies(); }

        public string DumpNativeChain() { return NativeProbeService.DumpNativeChain(); }
        public string ProbeClaListRead() { return NativeProbeService.ProbeClaListRead(); }
        public string EnumClaLists() { return NativeProbeService.EnumClaLists(); }
        public string PopupArm() { return NativeProbeService.PopupArm(); }
        public string PopupArmInject() { return NativeProbeService.PopupArmInject(); }
        public string PopupMark(string label) { return NativeProbeService.PopupMark(label); }
        public string PopupReport() { return NativeProbeService.PopupReport(); }

        public string GetActiveViewHeaderTitle()
        {
            try
            {
                var workbench = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench;
                if (workbench == null) return null;
                var activeWindow = workbench.ActiveWorkbenchWindow;
                if (activeWindow == null) return null;
                var viewContent = activeWindow.ViewContent;
                if (viewContent == null) return null;
                var headerProp = viewContent.GetType().GetProperty("HeaderTitle",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                return headerProp == null ? null : headerProp.GetValue(viewContent, null) as string;
            }
            catch { return null; }
        }

        public string GetActiveViewFileName()
        {
            try
            {
                var workbench = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench;
                if (workbench == null) return null;
                var activeWindow = workbench.ActiveWorkbenchWindow;
                if (activeWindow == null) return null;
                var viewContent = activeWindow.ViewContent;
                return viewContent == null ? null : viewContent.FileName;
            }
            catch { return null; }
        }

        public object FindOpenDictionary()
        {
            try
            {
                var workbench = ICSharpCode.SharpDevelop.Gui.WorkbenchSingleton.Workbench;
                if (workbench == null) return null;
                var vcProp = workbench.GetType().GetProperty("ViewContentCollection",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (vcProp == null) return null;
                var viewContents = vcProp.GetValue(workbench, null) as System.Collections.IEnumerable;
                if (viewContents == null) return null;
                foreach (var vc in viewContents)
                {
                    if (vc.GetType().Name == "DataDictionaryViewContent")
                    {
                        // NonPublic, NOT Public - DCT is a private property on
                        // DataDictionaryViewContent. Getting this wrong returns null silently and
                        // the dictionary tools simply stop finding the open .dct.
                        var dctProp = vc.GetType().GetProperty("DCT",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (dctProp != null) return dctProp.GetValue(vc, null);
                    }
                }
                return null;
            }
            catch { return null; }
        }

        public void SaveAllDirtyEditors()
        {
            try { MonacoClarionEditor.SaveAllDirtyBeforeBuild(); } catch { }
        }

        public Dictionary<string, object> GetFocusedEmbeditorSelection()
        {
            return ClarionAssistant.Terminal.ModernEmbeditorViewContent.GetFocusedSelection();
        }
    }
}
