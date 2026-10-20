using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ClarionCodeGraph.Graph;

namespace ClarionAssistant.Services
{
    /// <summary>What one index run needs - everything the in-process run was given.</summary>
    public sealed class IndexRunRequest
    {
        public string SlnPath;
        /// <summary>Null: the indexer's default, &lt;sln dir&gt;\&lt;sln name&gt;.codegraph.db.</summary>
        public string DbPath;
        public bool Incremental;
        /// <summary>The host's ACTIVE redirection (version .red or the project-local override), or null to let the
        /// indexer probe the solution folder - the same rule as CodeGraphIndexer.RedService.</summary>
        public RedFileService Red;
        /// <summary>GH #245: resolve through this build configuration's .red sections; null for the default order.</summary>
        public string BuildConfiguration;
        public List<string> LibraryPaths;
    }

    /// <summary>The index child died, could not start, or wrote no result. The message names the exe, the exit
    /// code and the end of its stderr, so the progress window and the MCP reply say what happened.</summary>
    public sealed class IndexerProcessException : Exception
    {
        public string ExePath { get; private set; }
        public int ExitCode { get; private set; }
        public string StderrTail { get; private set; }

        /// <summary>The message without the stderr tail: what failed, in one line.</summary>
        public string Headline { get; private set; }

        public IndexerProcessException(string message, string exePath, int exitCode, string stderrTail, Exception inner = null)
            : this(message, message, exePath, exitCode, stderrTail, inner)
        {
        }

        public IndexerProcessException(string headline, string message, string exePath, int exitCode, string stderrTail, Exception inner = null)
            : base(message, inner)
        {
            Headline = headline;
            ExePath = exePath;
            ExitCode = exitCode;
            StderrTail = stderrTail;
        }
    }

    /// <summary>
    /// Runs a CodeGraph index in clarion-indexer.exe, a separate 64-bit process, instead of inside the host
    /// (ticket 583abdec). Clarion.exe is 32-bit and already ~1.5 GB at idle; the relationship pass of a large
    /// solution needs ~550 MB more and the in-process run died with OutOfMemoryException.
    ///
    /// The surface is CodeGraphIndexer's - OnProgress, OnProgressEvent, CancelRequested, EffectiveIncremental,
    /// and Run returns the IndexResult or throws OperationCanceledException on cancel - so a host swaps one for
    /// the other without changing what it shows. Every callback fires on the thread that called Run, as the
    /// in-process indexer's did.
    ///
    /// Protocol: <see cref="IndexerProtocol"/>. Cancel: "cancel" on the child's stdin (it stops at the next file
    /// boundary, as before), then a kill after <see cref="CancelGrace"/>. The exe is found beside THIS assembly
    /// (the deploy and the installer put it in the addin folder), never on PATH.
    ///
    /// Missing exe: the run falls back to in-process, WITH a warning on the progress channel and in
    /// <see cref="FallbackWarning"/> - see <see cref="RunInProcessFallback"/> for why that one case is allowed.
    /// A child that starts and then fails is an error, never a fallback.
    /// </summary>
    public sealed class OutOfProcessIndexer
    {
        public const string ExeName = "clarion-indexer.exe";

        /// <summary>How long a cancelled child gets to stop on its own before it is killed. The indexer polls
        /// cancel between files; the finishing tail does not poll, so this is generous.</summary>
        public TimeSpan CancelGrace = TimeSpan.FromSeconds(15);

        /// <summary>Lines of non-protocol stderr (and recent progress messages) kept for an error report.</summary>
        public int StderrTailLines = 25;

        public string ExePath { get; set; }

        public event Action<string> OnProgress;
        public event Action<IndexProgressEvent> OnProgressEvent;
        /// <summary>Raised on the calling thread once the child is running, for a host or a test that watches it.</summary>
        public event Action<Process> ChildStarted;
        public Func<bool> CancelRequested { get; set; }

        /// <summary>What actually ran: false when an incremental request ran full (an older database), as
        /// CodeGraphIndexer.EffectiveIncremental. Before the child reports it, the request's value.</summary>
        public bool EffectiveIncremental { get; private set; }

        /// <summary>True when the exe was missing and the run happened inside this process.</summary>
        public bool RanInProcess { get; private set; }

        /// <summary>The child's exit code from the last run (0 indexed, 1 failed, 2 cancelled; -1 when unknown or none ran).</summary>
        public int ExitCode { get; private set; }

        /// <summary>The warning a host appends to its summary when <see cref="RanInProcess"/>; null otherwise.</summary>
        public string FallbackWarning { get; private set; }

        /// <summary>Test seam: extra arguments for the child (the indexer's --test-* switches).</summary>
        public string ExtraArgumentsForTest { get; set; }

        public OutOfProcessIndexer()
        {
            ExePath = DefaultExePath();
        }

        /// <summary>clarion-indexer.exe beside the assembly this class is compiled into: ClarionAssistant.dll in
        /// the addin folder, or clarion-mcp-server.exe, which the deploy puts in the same folder.</summary>
        public static string DefaultExePath()
        {
            try
            {
                string here = typeof(OutOfProcessIndexer).Assembly.Location;
                if (string.IsNullOrEmpty(here)) return null;
                return Path.Combine(Path.GetDirectoryName(here), ExeName);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ arguments

        /// <summary>
        /// 583abdec review L1: a path the child is given is ABSOLUTE, resolved against THIS process's current directory
        /// - what a relative path meant to the in-process run. The child takes its paths verbatim and its working
        /// directory is not this process's in every case, so a relative path must never reach it. A fully qualified
        /// path is passed unchanged (character for character: a library path's spelling is part of the stored
        /// library hash).
        /// </summary>
        public static string AbsolutePath(string path)
        {
            if (string.IsNullOrEmpty(path) || IsFullyQualified(path)) return path;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static bool IsFullyQualified(string p)
        {
            if (p.Length >= 2 && (p[0] == '\\' || p[0] == '/') && (p[1] == '\\' || p[1] == '/')) return true;   // UNC, \\?\
            return p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
        }

        /// <summary>The child's command line for a request. Public for the protocol harness.</summary>
        public static string BuildArguments(IndexRunRequest r)
        {
            var a = new List<string> { "index", AbsolutePath(r.SlnPath), "--protocol" };
            if (!string.IsNullOrEmpty(r.DbPath)) { a.Add("--db"); a.Add(AbsolutePath(r.DbPath)); }
            if (r.Incremental) a.Add("--incremental");
            if (!string.IsNullOrEmpty(r.BuildConfiguration)) { a.Add("--configuration"); a.Add(r.BuildConfiguration); }
            if (r.Red != null && !string.IsNullOrEmpty(r.Red.RedFilePath))
            {
                // --red-exact: the host's loaded instance, rebuilt as-is (RedFileService.LoadAs) - its final macro
                // table and version names - instead of re-deriving macros from where the file sits.
                a.Add("--red"); a.Add(AbsolutePath(r.Red.RedFilePath));
                a.Add("--red-exact");
                if (!string.IsNullOrEmpty(r.Red.VersionRedFileName)) { a.Add("--red-version-name"); a.Add(r.Red.VersionRedFileName); }
                if (!string.IsNullOrEmpty(r.Red.LoadedForVersion)) { a.Add("--red-version"); a.Add(r.Red.LoadedForVersion); }
                foreach (var kv in r.Red.Macros)
                {
                    a.Add("--red-macro");
                    a.Add(kv.Key + "=" + (kv.Value ?? ""));
                }
            }
            if (r.LibraryPaths != null && r.LibraryPaths.Count > 0)
            {
                var libs = new List<string>(r.LibraryPaths.Count);
                foreach (string lib in r.LibraryPaths) libs.Add(AbsolutePath(lib));
                a.Add("--lib-paths");
                a.Add(string.Join(";", libs));
            }
            else
            {
                // The host passed none: the child must not derive them from the .red behind its back.
                a.Add("--no-lib-paths");
            }
            var sb = new StringBuilder();
            foreach (string s in a)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteArgument(s ?? ""));
            }
            return sb.ToString();
        }

        /// <summary>Quote one argument so CommandLineToArgvW / the CLR's argv parser gives it back unchanged
        /// (spaces, quotes, and the trailing backslash of a folder path).</summary>
        public static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ run

        public IndexResult Run(IndexRunRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            EffectiveIncremental = request.Incremental;
            RanInProcess = false;
            FallbackWarning = null;
            ExitCode = -1;

            string exe = ExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return RunInProcessFallback(request, exe, "was not found");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = BuildArguments(request) + (string.IsNullOrEmpty(ExtraArgumentsForTest) ? "" : " " + ExtraArgumentsForTest),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                // Every path argument is already absolute (BuildArguments, review L1); this only keeps anything else
                // the child resolves (a relative .red entry) where the in-process run resolved it.
                WorkingDirectory = WorkingDirectoryFor(exe)
            };

            var tail = new Queue<string>();
            Action<string> keep = line =>
            {
                tail.Enqueue(line);
                while (tail.Count > StderrTailLines) tail.Dequeue();
            };

            Process child;
            try
            {
                child = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 583abdec review D2: the exe is there but Windows will not START it - error 193 (not a valid Win32
                // application: a truncated or wrong-platform file), access denied, a blocked download. Nothing ran,
                // so this is the missing-exe case, with the reason in the warning.
                return RunInProcessFallback(request, exe, "could not be started (Win32 error " + ex.NativeErrorCode + ": " + ex.Message + ")");
            }
            catch (Exception ex)
            {
                throw new IndexerProcessException(string.Format("Could not start the indexer {0}: {1}", exe, ex.Message),
                    exe, -1, "", ex);
            }
            if (child == null)
                throw new IndexerProcessException("Could not start the indexer " + exe + ".", exe, -1, "");

            using (child)
            {
                var lines = new BlockingCollection<string>(new ConcurrentQueue<string>());
                var stdout = new StringBuilder();
                var stderrDone = new ManualResetEventSlim(false);
                var stdoutDone = new ManualResetEventSlim(false);

                var errReader = new Thread(() =>
                {
                    try
                    {
                        string l;
                        while ((l = child.StandardError.ReadLine()) != null) lines.Add(l);
                    }
                    catch { }
                    finally { stderrDone.Set(); }
                }) { IsBackground = true, Name = "clarion-indexer stderr" };
                var outReader = new Thread(() =>
                {
                    try { stdout.Append(child.StandardOutput.ReadToEnd()); }
                    catch { }
                    finally { stdoutDone.Set(); }
                }) { IsBackground = true, Name = "clarion-indexer stdout" };
                errReader.Start();
                outReader.Start();

                var started = ChildStarted;
                if (started != null) { try { started(child); } catch { } }

                bool cancelSent = false;
                // 583abdec review D3: true only once Kill() SUCCEEDED. A child that had already exited, or a Kill that
                // threw, is not "killed" - its own exit code and result decide what happened.
                bool killed = false;
                DateTime cancelAt = DateTime.MinValue;
                DateTime nextKillAt = DateTime.MaxValue;
                bool? childMode = null;

                while (true)
                {
                    string line;
                    if (lines.TryTake(out line, 100))
                    {
                        HandleLine(line, keep, ref childMode);
                        continue;   // drain everything queued before looking at cancel or exit
                    }

                    if (!cancelSent && IsCancelRequested())
                    {
                        cancelSent = true;
                        cancelAt = DateTime.UtcNow;
                        nextKillAt = cancelAt + CancelGrace;
                        SendCancel(child);
                    }
                    else if (cancelSent && !killed && DateTime.UtcNow > nextKillAt)
                    {
                        nextKillAt = DateTime.UtcNow.AddSeconds(1);   // a failed Kill is retried, never assumed
                        if (!HasExited(child) && TryKill(child))
                        {
                            killed = true;
                            keep("(cancel not honoured within " + (int)CancelGrace.TotalSeconds + " s - the indexer process was killed)");
                        }
                    }

                    if (stderrDone.IsSet && lines.Count == 0 && HasExited(child))
                        break;
                }

                stdoutDone.Wait(TimeSpan.FromSeconds(10));
                try { child.WaitForExit(); } catch { }
                int exitCode;
                try { exitCode = child.ExitCode; } catch { exitCode = -1; }
                ExitCode = exitCode;

                var outcome = IndexerProtocol.ParseOutcome(stdout.ToString());
                if (outcome != null && outcome.EffectiveIncremental.HasValue) childMode = outcome.EffectiveIncremental;
                if (childMode.HasValue) EffectiveIncremental = childMode.Value;

                // 583abdec review D3: a FINISHED index is checked FIRST. A cancel that arrived as the child was writing
                // its result does not turn a complete database into a "cancelled" one (which a caller deletes).
                if (outcome != null && outcome.Success && outcome.Result != null && exitCode == IndexerProtocol.ExitOk)
                    return outcome.Result;

                if (cancelSent && (killed || exitCode == IndexerProtocol.ExitCancelled || (outcome != null && outcome.Cancelled)))
                    throw new OperationCanceledException("Index cancelled by user.");

                string tailText = string.Join(Environment.NewLine, tail.ToArray());
                string why = outcome == null
                    ? "it wrote no result"
                    : (outcome.Error ?? "it reported failure") + (string.IsNullOrEmpty(outcome.ErrorType) ? "" : " (" + outcome.ErrorType + ")");
                string headline = string.Format("The indexer process {0} failed with exit code {1}: {2}.",
                    Path.GetFileName(exe) + " (" + exe + ")", exitCode, why);
                throw new IndexerProcessException(headline,
                    headline + (string.IsNullOrEmpty(tailText) ? "" : Environment.NewLine + "Last indexer output:" + Environment.NewLine + tailText),
                    exe, exitCode, tailText);
            }
        }

        private void HandleLine(string line, Action<string> keep, ref bool? childMode)
        {
            IndexerProtocol.Line decoded;
            if (!IndexerProtocol.TryDecode(line, out decoded))
            {
                if (!string.IsNullOrWhiteSpace(line)) keep(line);
                return;
            }
            switch (decoded.Kind)
            {
                case IndexerProtocol.KindMessage:
                    keep(decoded.Message);
                    var p = OnProgress;
                    if (p != null) p(decoded.Message);
                    break;
                case IndexerProtocol.KindEvent:
                    var e = OnProgressEvent;
                    if (e != null) e(decoded.Event);
                    break;
                case IndexerProtocol.KindMode:
                    childMode = decoded.Incremental;
                    EffectiveIncremental = decoded.Incremental;
                    break;
            }
        }

        /// <summary>This process's current directory, or the exe's folder when that is gone (Process.Start refuses a
        /// working directory that does not exist).</summary>
        private static string WorkingDirectoryFor(string exe)
        {
            try
            {
                string cwd = Environment.CurrentDirectory;
                if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) return cwd;
            }
            catch { }
            return Path.GetDirectoryName(exe);
        }

        private bool IsCancelRequested()
        {
            var probe = CancelRequested;
            if (probe == null) return false;
            try { return probe(); } catch { return false; }
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        /// <summary>True only when the kill went through (it throws when the process already exited or is not ours).</summary>
        private static bool TryKill(Process p)
        {
            try { p.Kill(); return true; }
            catch { return false; }
        }

        // ------------------------------------------------------------------ the MCP failure reply (review L3)

        public const int McpTailLines = 5;
        public const int McpLineChars = 200;
        public const int McpHeadlineChars = 300;

        /// <summary>
        /// 583abdec review L3: what an MCP caller is told when an index fails - the headline, at most
        /// <see cref="McpTailLines"/> lines of the child's last output, each cut at <see cref="McpLineChars"/>, and
        /// where the full output is (the run log, which gets the whole message). The whole tail is for a human, and
        /// a stderr dump in a tool result costs the model context and can carry text the model would read as input.
        /// </summary>
        public static string McpFailureReply(Exception ex, string runLogPath)
        {
            string headline;
            string tail = null;
            var ipe = ex as IndexerProcessException;
            if (ipe != null)
            {
                headline = ipe.Headline ?? ipe.Message;
                tail = ipe.StderrTail;
            }
            else
            {
                headline = ex != null ? ex.Message : "unknown error";
            }
            headline = Clip(FirstLine(headline), McpHeadlineChars);

            var sb = new StringBuilder("Error indexing solution: ").Append(headline);
            if (!string.IsNullOrEmpty(tail))
            {
                var lines = new List<string>();
                foreach (string l in tail.Split('\n'))
                {
                    string t = l.TrimEnd('\r');
                    if (t.Trim().Length > 0) lines.Add(t);
                }
                int from = Math.Max(0, lines.Count - McpTailLines);
                if (lines.Count > 0) sb.Append("\nLast indexer output" + (from > 0 ? " (last " + McpTailLines + " lines)" : "") + ":");
                for (int i = from; i < lines.Count; i++) sb.Append("\n  ").Append(Clip(lines[i], McpLineChars));
            }
            sb.Append("\nFull output: ").Append(string.IsNullOrEmpty(runLogPath) ? "(no run log was written)" : runLogPath);
            return sb.ToString();
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            return nl >= 0 ? s.Substring(0, nl) : s;
        }

        private static string Clip(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        /// <summary>Raw ASCII bytes: the StandardInput writer's encoding is the host console's, which in a GUI
        /// process can carry a preamble.</summary>
        private static void SendCancel(Process child)
        {
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(IndexerProtocol.CancelCommand + "\n");
                var s = child.StandardInput.BaseStream;
                s.Write(bytes, 0, bytes.Length);
                s.Flush();
            }
            catch { /* the child already exited, or its stdin is gone: the kill timer covers it */ }
        }

        /// <summary>
        /// THE ONLY in-process index left on a host path (the source scan pins this). Taken ONLY when
        /// clarion-indexer.exe is missing beside the addin - a broken or hand-made install, since the deploy and
        /// the installer both ship it - or is there but Windows will not START it (review D2: a Win32Exception such
        /// as error 193, before anything ran). Refusing to index there would turn a small solution's working index
        /// into an error over a file nobody can see; running it in-process keeps those working, and the warning says
        /// exactly what is wrong and that a large solution can still run out of memory. A child that STARTS and
        /// fails never comes here: that is a real error and is reported as one.
        /// </summary>
        private IndexResult RunInProcessFallback(IndexRunRequest request, string exe, string why)
        {
            RanInProcess = true;
            FallbackWarning = string.Format(
                "WARNING: {0} {1} ({2}), so this index ran inside this process - the old way, which can run " +
                "the 32-bit Clarion IDE out of memory on a large solution. Redeploy or reinstall ClarionAssistant to restore it.",
                ExeName, why, string.IsNullOrEmpty(exe) ? "no addin folder" : exe);
            var p = OnProgress;
            if (p != null) p(FallbackWarning);

            string dbPath = request.DbPath;
            if (string.IsNullOrEmpty(dbPath))
                dbPath = Path.Combine(Path.GetDirectoryName(request.SlnPath),
                    Path.GetFileNameWithoutExtension(request.SlnPath) + ".codegraph.db");

            // Review D1: the writer lock covers whoever writes - this process, here - as the child takes it for its run.
            IDisposable writerLock;
            string writerHolder;
            if (IndexRunGate.TryAcquireWriterLock(dbPath, out writerLock, out writerHolder) == IndexRunGate.LockResult.Blocked)
                throw new IOException("another index run is writing " + dbPath + " - held by " + (writerHolder ?? "another process"));

            var db = new CodeGraphDatabase();
            CodeGraphIndexer indexer = null;
            try
            {
                db.Open(dbPath);
                indexer = new CodeGraphIndexer(db);
                indexer.RedService = request.Red;
                indexer.BuildConfiguration = request.BuildConfiguration;
                indexer.CancelRequested = () => IsCancelRequested();
                indexer.OnProgress += msg => { var h = OnProgress; if (h != null) h(msg); };
                indexer.OnProgressEvent += ev => { var h = OnProgressEvent; if (h != null) h(ev); };
                return indexer.IndexSolution(request.SlnPath, request.Incremental, request.LibraryPaths);
            }
            finally
            {
                if (indexer != null) EffectiveIncremental = indexer.EffectiveIncremental;
                try { db.Close(); }
                finally { if (writerLock != null) { try { writerLock.Dispose(); } catch { } } }
            }
        }
    }
}
