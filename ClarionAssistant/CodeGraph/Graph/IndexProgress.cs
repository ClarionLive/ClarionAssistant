using System;

namespace ClarionCodeGraph.Graph
{
    /// <summary>
    /// One structured progress update from CodeGraphIndexer (ticket 0d788f8b). The string
    /// OnProgress channel stays for the console exe and the header log; this event exists so
    /// UI consumers (the in-IDE progress window, MCP progress streaming) get machine-readable
    /// state — phase, current file, and a real done/total — instead of parsing log lines.
    /// FilesDone/FilesTotal are per-PHASE counts: the parsing pass and the relationship pass
    /// each walk the file list once, so a consumer computing an overall percentage should
    /// weight the phases (relationship resolution dominates wall-clock).
    /// </summary>
    public class IndexProgressEvent
    {
        public const string PhaseParsing = "parsing";      // Pass 1/1b/2/2b — symbols
        public const string PhaseResolving = "resolving";  // relationship scan
        public const string PhaseFinishing = "finishing";  // inheritance/uses_type/includes tail

        public string Phase { get; set; }
        /// <summary>Project whose files are being processed; null when not project-scoped.</summary>
        public string ProjectName { get; set; }
        /// <summary>File name currently being processed; null for phase-transition events.</summary>
        public string CurrentFile { get; set; }
        public int FilesDone { get; set; }
        public int FilesTotal { get; set; }
        /// <summary>Symbols indexed for THIS event's ProjectName so far — a per-project
        /// delta, not the solution-wide running total (a UI column fed the cumulative
        /// figure would be silently wrong on every row). 0 when not project-scoped.</summary>
        public int SymbolCount { get; set; }
        public int RelationshipCount { get; set; }
    }
}
