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
    }
}
