// EmbedLspContext.cs captures itself from the live IDE (AppTreeService) and reverts its LSP shadow
// through SharedLspBridge. Neither is on the path under test (WrapBuffer + LineOffsetFor), so these
// stand-ins exist only to let the REAL, unmodified EmbedLspContext.cs compile standalone.
namespace ClarionAssistant.Services
{
    public class AppTreeService
    {
        public object GetOpenPweeDetails() { return null; }
    }

    public static class SharedLspBridge
    {
        public static bool IsRunning { get { return false; } }
        public static void EnsureBufferSynced(string filePath, string bufferText) { }
        public static void ClearDiagnostics(string filePath) { }
    }

    // RedFileService.cs (compiled in since PR #228: EmbedLspContext resolves the generated module through
    // the .red) names ClarionVersionConfig in its LoadForProject/Load(config) overloads, not used here.
    public class ClarionVersionConfig
    {
        public string Name { get; set; }
        public string BinPath { get; set; }
        public string RootPath { get; set; }
        public string RedFileName { get; set; }
        public string RedFilePath { get; set; }
        public System.Collections.Generic.Dictionary<string, string> Macros { get; set; }
    }
}
