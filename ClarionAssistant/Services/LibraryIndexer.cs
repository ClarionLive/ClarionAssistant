using System;
using System.IO;

namespace ClarionAssistant.Services
{
    public class LibraryIndexResult
    {
        public int SymbolCount { get; set; }
        public string DbPath { get; set; }
        public string Error { get; set; }
        public bool Success { get { return string.IsNullOrEmpty(Error); } }
    }

    /// <summary>
    /// DEPRECATED standalone library-equate indexer. Retained as a thin COMPATIBILITY SHIM
    /// over <see cref="ClarionGraphService"/> (ticket 6e8f2439). The build engine, schema, and
    /// equate ingestion now live in ClarionGraphService, which keys the cache by Clarion version
    /// (%APPDATA%\ClarionAssistant\clariongraph\ClarionGraph_&lt;version&gt;.db) instead of the old
    /// non-versioned assembly-dir ClarionLib.codegraph.db.
    ///
    /// Existing callers — the settings "Build Library" button (<c>HandleBuildLib</c>), its status
    /// label, and <c>list_codegraph_databases</c> — keep calling these methods unchanged; they now
    /// transparently target the versioned ClarionGraph cache. New code should call
    /// ClarionGraphService directly.
    /// </summary>
    public static class LibraryIndexer
    {
        /// <summary>
        /// Legacy assembly-dir db path (ClarionLib.codegraph.db). Used ONLY as a fallback when the
        /// Clarion version can't be resolved (e.g. running outside the IDE).
        /// </summary>
        public static string GetLegacyDbPath()
        {
            string asmDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(asmDir, "ClarionLib.codegraph.db");
        }

        /// <summary>
        /// The versioned ClarionGraph db path for the current Clarion version if resolvable,
        /// else the legacy assembly-dir path. (Repointed to the versioned cache — ticket 6e8f2439.)
        /// </summary>
        public static string GetDefaultDbPath()
        {
            return ClarionGraphService.ResolveDbPath() ?? GetLegacyDbPath();
        }

        /// <summary>
        /// Build the library symbol graph for the given Clarion root. Now ROUTES THROUGH
        /// <see cref="ClarionGraphService"/> — builds the version-keyed db from
        /// <c>clarionRoot\libsrc\win</c>. Preserves the original signature/return shape so the
        /// settings BackgroundWorker is unaffected.
        /// </summary>
        public static LibraryIndexResult Build(string clarionRoot)
        {
            string version = ClarionGraphService.ResolveVersionKey();
            if (string.IsNullOrEmpty(version))
                return new LibraryIndexResult { Error = "Clarion version not detected" };

            string dbPath = ClarionGraphService.ResolveDbPath(version);
            if (string.IsNullOrEmpty(dbPath))
                return new LibraryIndexResult { Error = "Could not resolve ClarionGraph cache path" };

            string libSrcRoot = string.IsNullOrEmpty(clarionRoot)
                ? ClarionGraphService.ResolveLibSrcRoot()
                : Path.Combine(clarionRoot, "libsrc", "win");

            return ClarionGraphService.Build(version, dbPath, libSrcRoot);
        }

        /// <summary>
        /// One-line status of the library graph for the settings dialog. Delegates to
        /// <see cref="ClarionGraphService.GetStatusText"/>.
        /// </summary>
        public static string GetStatus()
        {
            return ClarionGraphService.GetStatusText();
        }
    }
}
