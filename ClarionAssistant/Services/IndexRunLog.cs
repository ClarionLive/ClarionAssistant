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
                LogPath = Path.Combine(dir, "codegraph-index.log");
                string prev = Path.Combine(dir, "codegraph-index.prev.log");

                if (File.Exists(LogPath))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(LogPath, prev);
                }

                _writer = new StreamWriter(LogPath, false) { AutoFlush = true };
                _writer.WriteLine(string.Format("=== CodeGraph index run — {0} — started {1:yyyy-MM-dd HH:mm:ss} ===",
                    solutionName, DateTime.Now));
            }
            catch
            {
                _writer = null; // logging is best-effort
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
