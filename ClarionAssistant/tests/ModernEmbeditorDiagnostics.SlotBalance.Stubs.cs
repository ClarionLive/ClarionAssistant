using System.Collections.Generic;

// ModernEmbeditorDiagnostics.cs's Pass 1 talks to the live Clarion language server through
// SharedLspBridge (and wraps the buffer via EmbedLspContext). The slot-balance pass under test
// (Passes 2 & 3) never touches either, so these stand-ins report "no LSP running" and let the real,
// unmodified ModernEmbeditorDiagnostics.cs compile standalone. ClarionAppDataReader (used for the
// routine set) is the REAL file, compiled with ClarionAppDataReader.StructureScan.Stubs.cs.
namespace ClarionAssistant.Services
{
    public static class SharedLspBridge
    {
        public static bool IsRunning { get { return false; } }
        public static void EnsureBufferSynced(string filePath, string bufferText) { }
        public static LspClient.DiagnosticWaitResult WaitForDiagnostics(string filePath, int timeoutMs, bool forceRefresh) { return null; }
        public static List<LspClient.DiagnosticEntry> GetCachedDiagnostics(string filePath) { return null; }
    }

    public class LspClient
    {
        public class DiagnosticEntry
        {
            public int Severity;
            public int Line;
            public int Character;
            public int EndLine;
            public int EndCharacter;
            public string Message;
            public string Source;
        }

        public class DiagnosticWaitResult
        {
            public List<DiagnosticEntry> Entries;
            public bool Pending;
        }
    }

    public sealed class EmbedLspContext
    {
        public int LineOffset { get { return 1; } }
        public string WrapBuffer(string buffer) { return buffer; }
    }
}
