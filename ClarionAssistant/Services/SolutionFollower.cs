using System;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Where a workspace's solution came from, at one instant (ticket 9e1bd280). One object so a caller
    /// reads the path, its source and the switch together - never a path from before a switch and a
    /// source from after it.
    /// </summary>
    public sealed class SolutionSourceSnapshot
    {
        /// <summary>"ide-live": the solution the launching IDE has open now.</summary>
        public const string IdeLive = "ide-live";
        /// <summary>"launch-arg": --solution (or the one .sln in the working directory) - no IDE record to follow.</summary>
        public const string LaunchArg = "launch-arg";
        /// <summary>"none": no solution - the IDE closed it, or nothing named one.</summary>
        public const string None = "none";

        /// <summary>The solution, or null when <see cref="Source"/> is <see cref="None"/>.</summary>
        public string Path { get; internal set; }
        /// <summary><see cref="IdeLive"/>, <see cref="LaunchArg"/> or <see cref="None"/>.</summary>
        public string Source { get; internal set; }
        /// <summary>One sentence saying why this is the answer.</summary>
        public string Note { get; internal set; }
        /// <summary>The solution served before the last switch, or null when there has been none.</summary>
        public string SwitchedFrom { get; internal set; }
        /// <summary>When that switch was seen (local time), or null.</summary>
        public DateTime? SwitchedAt { get; internal set; }
        /// <summary>
        /// The IDE record this answer was read from (null when none), so everything derived from the snapshot - the
        /// IDE's version choice above all - comes from the SAME read, not a second one a switch can land between
        /// (9e1bd280 run 1).
        /// </summary>
        public IdeSolutionRecord.Details Record { get; internal set; }
    }

    /// <summary>
    /// Optional, for a workspace whose solution can move under it (the standalone MCP server): get_solution_info
    /// reports where the solution came from and, after a switch, which one it replaced (9e1bd280).
    /// </summary>
    public interface ISolutionSourceInfo
    {
        SolutionSourceSnapshot SolutionSource { get; }
    }

    /// <summary>
    /// "Which solution is this tab working on" for the standalone clarion-mcp-server: THE ONE THE IDE HAS OPEN
    /// NOW (ticket 9e1bd280).
    ///
    /// WHY. The addin launches each tab's standalone with --solution, a snapshot taken at launch. Only the LSP
    /// followed the IDE after that (77aceec5); every other tool kept the launch solution. Measured live: the
    /// Owner opened UltimateSQLTest in the IDE, the header followed (Clarion 10 Active And Updated,
    /// Clarion100.red), and the open tab "CA1 · v61POSitive" kept answering get_solution_info for
    /// v61POSitive.sln on "POSitive v61 C10 Updated" / Clarion10v61.red - so the agent resolved files through
    /// the wrong .red. Owner decision: FOLLOW THE IDE, every tool, so a tab answers for what the header shows.
    ///
    /// THE ORDER, re-asked on every read (one file stat while the record is unchanged - ReadDetailsCached):
    ///   1. the record the launching IDE publishes (IdeSolutionRecord) names a solution -> that one ("ide-live");
    ///   2. that IDE HAS published one before and no longer does -> NONE: it closed the solution, and silently
    ///      carrying on with the old one is the defect this class exists to remove;
    ///   3. no record yet (an IDE from before 77aceec5, or not launched by an IDE) -> --solution ("launch-arg");
    ///   4. otherwise none.
    /// Rule 2 needs the "has published before" latch because an absent record means both "this IDE closed its
    /// solution" and "this IDE never publishes": only having seen one tells them apart.
    ///
    /// The LSP reads the same follower (Program.cs), so the language server and the tools cannot disagree.
    /// Thread-safe: tool calls, the LSP and the instance heartbeat all read it.
    /// </summary>
    public sealed class SolutionFollower
    {
        private readonly object _lock = new object();
        private readonly string _launchSolution;
        private readonly Func<int?> _idePid;

        private bool _ideSeen;
        private bool _started;
        private string _current;
        private string _switchedFrom;
        private DateTime? _switchedAt;

        /// <summary>Called (outside the lock) with (was, now) each time the followed solution changes; either may be null.</summary>
        public Action<string, string> Changed { get; set; }

        /// <param name="launchSolution">--solution, resolved; null when none was named.</param>
        /// <param name="idePid">The launching IDE's process id (--ide-pid), asked on every read; null = no IDE.</param>
        public SolutionFollower(string launchSolution, Func<int?> idePid)
        {
            _launchSolution = string.IsNullOrEmpty(launchSolution) ? null : launchSolution;
            _idePid = idePid;
        }

        /// <summary>The solution to serve now; null when there is none.</summary>
        public string CurrentPath { get { return Current().Path; } }

        /// <summary>The solution to serve now, with its source and the last switch.</summary>
        public SolutionSourceSnapshot Current()
        {
            int? pid = null;
            try { pid = _idePid != null ? _idePid() : null; } catch { pid = null; }

            IdeSolutionRecord.Details rec = null;
            string recNote = null;
            if (pid.HasValue) rec = IdeSolutionRecord.ReadDetailsCached(pid.Value, out recNote);

            string was = null, now = null;
            bool changed = false;
            SolutionSourceSnapshot snap;
            lock (_lock)
            {
                string path, source, note;
                if (rec != null && !string.IsNullOrEmpty(rec.Solution))
                {
                    _ideSeen = true;
                    path = rec.Solution;
                    source = SolutionSourceSnapshot.IdeLive;
                    note = "the solution open in the IDE now (pid " + pid.Value + ")";
                }
                else if (pid.HasValue && _ideSeen)
                {
                    path = null;
                    source = SolutionSourceSnapshot.None;
                    note = "no solution is open in the IDE (pid " + pid.Value + "): " + recNote;
                }
                else if (_launchSolution != null)
                {
                    path = _launchSolution;
                    source = SolutionSourceSnapshot.LaunchArg;
                    note = pid.HasValue
                        ? "--solution, from launch: " + recNote
                        : "--solution, from launch (not launched by an IDE, so nothing to follow)";
                }
                else
                {
                    path = null;
                    source = SolutionSourceSnapshot.None;
                    note = pid.HasValue ? "no solution: " + recNote
                                        : "no solution: no --solution given and not launched by an IDE";
                }

                if (!_started)
                {
                    _started = true;
                    _current = path;
                }
                else if (!SamePath(path, _current))
                {
                    was = _current;
                    now = path;
                    changed = true;
                    // The solution it replaced; through a "none" the last REAL one, so A -> none -> B still
                    // says it switched from A.
                    if (_current != null) _switchedFrom = _current;
                    _switchedAt = DateTime.Now;
                    _current = path;
                }

                snap = new SolutionSourceSnapshot
                {
                    Path = path,
                    Source = source,
                    Note = note,
                    SwitchedFrom = _switchedFrom,
                    SwitchedAt = _switchedAt,
                    Record = rec
                };
            }

            if (changed)
            {
                var cb = Changed;
                if (cb != null) { try { cb(was, now); } catch { } }
            }
            return snap;
        }

        /// <summary>
        /// SolutionVersionResolver.SamePath's comparison (full path, trailing separators trimmed, case-insensitive), with
        /// ONE difference: null equals null here, because "no solution" -> "no solution" is not a switch. Not shared
        /// because this file compiles alone into its harness (with IdeSolutionRecord only).
        /// </summary>
        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            try { a = Path.GetFullPath(a); b = Path.GetFullPath(b); } catch { }
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
