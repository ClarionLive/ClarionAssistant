using System;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Always-on, flush-per-line log of one CodeGraph index run (ticket 0d788f8b).
    /// %APPDATA%\ClarionAssistant\codegraph-index.log, previous run kept as
    /// codegraph-index.prev.log. Written unconditionally — not save-on-demand — so the
    /// transcript survives an IDE crash or a closed progress window (the same durable-log
    /// pattern as shutdown.log). All methods swallow IO errors: logging must never be the
    /// reason an index run fails.
    /// </summary>
    public sealed class IndexRunLog : IDisposable
    {
        private StreamWriter _writer;
        public string LogPath { get; private set; }

        public IndexRunLog(string solutionName)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ClarionAssistant");
                Directory.CreateDirectory(dir);

                // Per-SOLUTION file name: two IDE instances indexing different solutions must
                // not fight over one path (the loser's rotation throws a sharing violation and
                // it silently logs nothing — pipeline debugger finding).
                string safeName = solutionName ?? "solution";
                foreach (char bad in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(bad, '_');
                string logPath = Path.Combine(dir, "codegraph-index-" + safeName + ".log");
                string prev = Path.Combine(dir, "codegraph-index-" + safeName + ".prev.log");

                if (File.Exists(logPath))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(logPath, prev);
                }

                _writer = new StreamWriter(logPath, false) { AutoFlush = true };
                // LogPath is only published once the writer is live — on any failure above it
                // stays null, so the form's Open Log says "no log was written" instead of
                // opening a PREVIOUS run's file and presenting it as this run's.
                LogPath = logPath;
                _writer.WriteLine(string.Format("=== CodeGraph index run — {0} — started {1:yyyy-MM-dd HH:mm:ss} ===",
                    solutionName, DateTime.Now));
            }
            catch
            {
                _writer = null; // logging is best-effort
                LogPath = null;
            }
        }

        public void WriteLine(string message)
        {
            var w = _writer;
            if (w == null) return;
            try { w.WriteLine(string.Format("{0:HH:mm:ss}  {1}", DateTime.Now, message)); }
            catch { }
        }

        public void Dispose()
        {
            var w = _writer;
            _writer = null;
            if (w == null) return;
            try { w.Dispose(); } catch { }
        }
    }
}
