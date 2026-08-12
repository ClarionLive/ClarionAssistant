using System;
using System.Collections.Concurrent;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Cross-entry-point guard for CodeGraph index runs, keyed by database path
    /// (ticket 0d788f8b, pipeline adversary finding run 2). Two writers exist:
    /// AssistantChatControl.RunIndex (UI thread, used by the header buttons and MCP
    /// index_solution) and McpToolRegistry.ExecuteIndexCodeGraph (MCP worker thread,
    /// index_codegraph). The UI-side _indexRunInProgress bool cannot see the worker-side
    /// runs, so without this gate a client could start index_codegraph while an
    /// index_solution run is still writing the same .codegraph.db. Concurrent runs on
    /// DIFFERENT solutions remain allowed — the key is the db file, not the process.
    /// </summary>
    public static class IndexRunGate
    {
        private static readonly ConcurrentDictionary<string, byte> Active =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Try to claim an index run on this database. False = a run already holds it.</summary>
        public static bool TryEnter(string dbPath)
        {
            return Active.TryAdd(Normalize(dbPath), 1);
        }

        /// <summary>Release the claim. Safe to call for a path that isn't held.</summary>
        public static void Exit(string dbPath)
        {
            byte unused;
            Active.TryRemove(Normalize(dbPath), out unused);
        }

        private static string Normalize(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return "";
            try { return Path.GetFullPath(dbPath); }
            catch { return dbPath; }
        }
    }
}
