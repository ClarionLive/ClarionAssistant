using System.Collections.Generic;

// EmbedLspContext.cs's IDE-coupled dependencies (AppTreeService reads the live embeditor; SharedLspBridge
// drives the running language server; ClarionVersionConfig drags in ClarionVersionService's detection
// chain) are reachable only from TryCapture/RevertShadow, which this harness doesn't call. Minimal
// stand-ins let the real, unmodified EmbedLspContext.cs and RedFileService.cs compile standalone.
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

    public class ClarionVersionConfig
    {
        public string BinPath { get; set; }
        public string RootPath { get; set; }
        public string RedFilePath { get; set; }
        public Dictionary<string, string> Macros { get; set; }
    }
}
