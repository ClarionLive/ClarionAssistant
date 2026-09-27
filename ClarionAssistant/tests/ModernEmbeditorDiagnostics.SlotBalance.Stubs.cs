using System.Collections.Generic;

// ModernEmbeditorDiagnostics.cs's LSP pass talks to the live Clarion language server through
// SharedLspBridge (and wraps the buffer via EmbedLspContext). These stand-ins let the real, unmodified
// ModernEmbeditorDiagnostics.cs compile standalone. By default they report "no LSP running", which is
// what the slot-balance cases need. IsRunning is settable and every member counts its calls, so the
// harness can also drive the LSP pass (one fixed entry) and prove the slot pass never touches it
// (1c685f2e items 7 and 8). ClarionAppDataReader (used for the routine set) is the REAL file, compiled
// with ClarionAppDataReader.StructureScan.Stubs.cs.
namespace ClarionAssistant.Services
{
    public static class SharedLspBridge
    {
        public static bool Running;
        public static int IsRunningCalls, SyncCalls, WaitCalls, CachedCalls;
        /// <summary>What WaitForDiagnostics returns when the LSP pass runs (0-based line).</summary>
        public static List<LspClient.DiagnosticEntry> FixedEntries = new List<LspClient.DiagnosticEntry>();

        public static void Reset()
        {
            Running = false;
            IsRunningCalls = SyncCalls = WaitCalls = CachedCalls = 0;
            FixedEntries = new List<LspClient.DiagnosticEntry>();
        }

        public static int TotalCalls { get { return IsRunningCalls + SyncCalls + WaitCalls + CachedCalls; } }

        public static bool IsRunning { get { IsRunningCalls++; return Running; } }
        public static void EnsureBufferSynced(string filePath, string bufferText) { SyncCalls++; }
        public static LspClient.DiagnosticWaitResult WaitForDiagnostics(string filePath, int timeoutMs, bool forceRefresh)
        {
            WaitCalls++;
            return new LspClient.DiagnosticWaitResult { Entries = new List<LspClient.DiagnosticEntry>(FixedEntries), Pending = false };
        }
        public static List<LspClient.DiagnosticEntry> GetCachedDiagnostics(string filePath) { CachedCalls++; return null; }
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
        public int LineOffsetFor(string buffer) { return 1; }
        public string WrapBuffer(string buffer) { return buffer; }
    }
}
