using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #279 follow-up (integ-1023 review D6/D7): what the chat header's CodeGraph line says during and after an
    /// index run. IDE-free, so a harness can drive it; AssistantChatControl.RunIndex does the painting.
    /// </summary>
    internal static class IndexHeaderText
    {
        /// <summary>An automatic incremental run before it knows whether there is anything to do: no spinner.</summary>
        public const string Checking = "Checking for changes...";
        /// <summary>An incremental run re-parsing changed projects.</summary>
        public const string Updating = "Updating...";
        /// <summary>An incremental request that runs FULL (a database below the current feature level).</summary>
        public const string Rebuilding = "Rebuilding index...";
        /// <summary>A full index that was asked for.</summary>
        public const string Indexing = "Indexing...";

        /// <summary>The spinner text for a run known to do real work. <paramref name="effectiveIncremental"/> is what the
        /// indexer said it runs (null before it said).</summary>
        public static string WorkText(bool requestedIncremental, bool? effectiveIncremental)
        {
            if (!requestedIncremental) return Indexing;
            return effectiveIncremental == false ? Rebuilding : Updating;
        }

        // CodeGraphIndexer's "{0} of {1} projects have changes", sent before any parsing.
        private static readonly Regex ChangedProjectsRegex = new Regex(@"^\s*(\d+) of (\d+) projects have changes\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>True when an indexer progress message says an incremental run has projects to re-parse: the first
        /// moment an incremental run is known to be real work (its first structured event comes only at Pass 2).</summary>
        public static bool MessageSignalsWork(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            var m = ChangedProjectsRegex.Match(message);
            int changed;
            return m.Success && int.TryParse(m.Groups[1].Value, out changed) && changed > 0;
        }
    }

    /// <summary>
    /// The header's remembered line for the last run in this IDE: per solution, with the database's stamp at the time.
    /// It holds a finished run's outcome ("Up to date", "Updated", ...) and a failed or cancelled run's text alike, so the
    /// error stays visible until the next run. Once the database has changed since (index_codegraph, the standalone
    /// server, another IDE wrote it), the line no longer describes it and is dropped.
    /// </summary>
    internal sealed class IndexOutcomeMemo
    {
        private string _slnPath;
        private string _text;
        private string _style;
        private DateTime _dbStampUtc;

        public void Record(string slnPath, string text, string style, DateTime dbStampUtc)
        {
            _slnPath = slnPath;
            _text = text;
            _style = style ?? "";
            _dbStampUtc = dbStampUtc;
        }

        public void Clear()
        {
            _slnPath = null;
            _text = null;
            _style = null;
        }

        /// <summary>The remembered line for <paramref name="currentSln"/>, when there is one and the database is as it was.</summary>
        public bool TryGet(string currentSln, DateTime currentDbStampUtc, out string text, out string style)
        {
            text = null;
            style = null;
            if (string.IsNullOrEmpty(_text) || string.IsNullOrEmpty(currentSln)
                || !string.Equals(_slnPath, currentSln, StringComparison.OrdinalIgnoreCase))
                return false;
            if (currentDbStampUtc != _dbStampUtc)
            {
                Clear();
                return false;
            }
            text = _text;
            style = _style;
            return true;
        }

        /// <summary>The database file's last write (UTC), or DateTime.MinValue when there is none.</summary>
        public static DateTime StampOf(string dbPath)
        {
            try
            {
                return !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath) ? File.GetLastWriteTimeUtc(dbPath) : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }
    }

    /// <summary>
    /// An automatic check asked for while another run is in flight (review D7e: Work With Solution on A, then quickly on
    /// B). The same solution is already in hand; a different one is remembered (the latest wins) and run when the
    /// current run ends, if it is still the solution shown.
    /// </summary>
    internal sealed class IndexFollowUp
    {
        private string _pendingSln;

        public string Pending { get { return _pendingSln; } }

        /// <summary>True when the request was queued (a different solution from the one running).</summary>
        public bool Queue(string runningSln, string requestedSln)
        {
            if (string.IsNullOrEmpty(requestedSln)) return false;
            if (string.Equals(runningSln, requestedSln, StringComparison.OrdinalIgnoreCase)) return false;
            _pendingSln = requestedSln;
            return true;
        }

        /// <summary>The run ended: the solution to check now (still the current one), or null. Clears the queue.</summary>
        public string Take(string currentSln)
        {
            string pending = _pendingSln;
            _pendingSln = null;
            return pending != null && string.Equals(pending, currentSln, StringComparison.OrdinalIgnoreCase) ? pending : null;
        }
    }
}
