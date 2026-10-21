using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClarionCodeGraph.Graph;

namespace ClarionIndexer
{
    class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        /// <summary>
        /// Load the SQLite.Interop.dll that matches THIS process's bitness before System.Data.SQLite looks for one
        /// (ticket 583abdec: the indexer is 64-bit now). The exe shares the addin folder, whose root holds the
        /// 32-bit interop the IDE loads - and System.Data.SQLite's pre-loader stops searching when it finds an
        /// interop in the base directory itself, so left alone a 64-bit indexer would bind the 32-bit file and
        /// die with BadImageFormatException. Once a module named SQLite.Interop.dll is loaded, the runtime's
        /// DllImport("SQLite.Interop.dll") binds to it. Returns null when loaded, else the error - a missing file is a
        /// clear error at start, not a late crash.
        /// </summary>
        private static string PreloadSqliteInterop()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string path = Path.Combine(Path.Combine(dir, arch), "SQLite.Interop.dll");
            if (!File.Exists(path))
                return "the " + arch + " SQLite interop is missing: " + path + " - redeploy or reinstall ClarionAssistant";
            if (LoadLibraryW(path) == IntPtr.Zero)
                return "could not load " + path + " (Win32 error " + Marshal.GetLastWin32Error() + ")";
            return null;
        }

        /// <summary>True under --protocol: stderr is read by a host, and only protocol records may start a record.</summary>
        static bool _protocol;

        /// <summary>
        /// 583abdec review L2: every NON-protocol stderr line. Under --protocol its line breaks (CR, LF, NEL, the two
        /// Unicode separators) become spaces, and a line that would itself begin with the protocol prefix is indented,
        /// so no text - an exception message, a path - can ever start a protocol record on the host's side.
        /// Protocol records are written with <see cref="Record"/>, never through here.
        /// </summary>
        static void ErrLine(string text = "")
        {
            Console.Error.WriteLine(_protocol ? ProtocolSafe(text) : text);
        }

        internal static string ProtocolSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                sb.Append(c == '\r' || c == '\n' || c == '\u0085' || c == '\u2028' || c == '\u2029' || c == '\f' || c == '\v' ? ' ' : c);
            string s = sb.ToString();
            return s.StartsWith(IndexerProtocol.Prefix.TrimEnd(), StringComparison.Ordinal) ? "  " + s : s;
        }

        /// <summary>One protocol record (IndexerProtocol.Encode*) on stderr: the only raw write to it under --protocol.</summary>
        static void Record(string encoded)
        {
            Console.Error.WriteLine(encoded);
        }

        static int Main(string[] args)
        {
            // 583abdec review D4b: the UTF-8 (no BOM) pipes FIRST, before anything that can fail and report - the
            // interop preload's error names a path, and a host decodes the pipes as UTF-8.
            _protocol = Array.IndexOf(args, "--protocol") >= 0;
            if (_protocol)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            }

            string preloadError = args.Length > 0 && args[0] != "--help" && args[0] != "-h" ? PreloadSqliteInterop() : null;
            if (preloadError != null)
            {
                ErrLine("Error: " + preloadError);
                if (_protocol)
                    Console.WriteLine(IndexerProtocol.FailureToJson(new DllNotFoundException(preloadError), false, null));
                return 1;
            }

            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "index":
                    return RunIndex(args);
                case "query":
                    return RunQuery(args);
                case "stats":
                    return RunStats(args);
                default:
                    // Treat first arg as solution path for backwards compatibility
                    if (File.Exists(args[0]) && args[0].EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    {
                        return RunIndex(new[] { "index", args[0] });
                    }
                    ErrLine("Unknown command: " + command);
                    PrintUsage();
                    return 1;
            }
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("ClarionIndexer - Build and query Clarion code graph databases");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  clarion-indexer index <solution.sln> [--db <path>] [--incremental] [--lib-paths \"path1;path2\"] [--red <file.red>] [--configuration <name>]");
            Console.Error.WriteLine("  clarion-indexer query <db-path> <command> [args...]");
            Console.Error.WriteLine("  clarion-indexer stats <db-path>");
            Console.Error.WriteLine("  clarion-indexer <solution.sln>              (shorthand for index)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Index Commands:");
            Console.Error.WriteLine("  index          Parse a Clarion solution and build the code graph database");
            Console.Error.WriteLine("    --db <path>      Custom database output path (default: <sln-dir>/<sln-name>.codegraph.db)");
            Console.Error.WriteLine("    --incremental    Only re-index changed projects (a database built below the current feature level, or a new one, gets a full index instead; JSON \"mode\" says which ran)");
            Console.Error.WriteLine("    --lib-paths      Semicolon-delimited library .inc search directories");
            Console.Error.WriteLine("    --red <file>     Clarion redirection file for source resolution (e.g.");
            Console.Error.WriteLine("                     C:\\Clarion11\\bin\\Clarion110.red). %THISDIR% anchors to the");
            Console.Error.WriteLine("                     .red's own folder; without this flag, only a *.red found in");
            Console.Error.WriteLine("                     the solution directory is used.");
            Console.Error.WriteLine("    --configuration <name>  Build configuration (Debug, Release): resolve through");
            Console.Error.WriteLine("                     its .red sections ([Release32], [Release], [Common]) as the");
            Console.Error.WriteLine("                     compiler does. Default: Common, Debug, Release.");
            Console.Error.WriteLine("    --no-lib-paths   Do not derive library paths from the .red");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Host options (ClarionAssistant runs the index in this process):");
            Console.Error.WriteLine("    --protocol       Structured progress on stderr, result JSON on stdout, \"cancel\" on stdin;");
            Console.Error.WriteLine("                     paths are taken verbatim. Exit 0 indexed, 1 failed, 2 cancelled.");
            Console.Error.WriteLine("    --red-exact      Load --red with exactly the --red-macro NAME=VALUE pairs given");
            Console.Error.WriteLine("    --red-macro N=V  A redirection macro (repeatable)");
            Console.Error.WriteLine("    --red-version-name <file>  The version's .red file name (project-local .red rule)");
            Console.Error.WriteLine("    --red-version <name>       The Clarion version the .red was loaded for");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Query Commands:");
            Console.Error.WriteLine("  query <db> find-symbol <name>          Search symbols by name");
            Console.Error.WriteLine("  query <db> callers <symbol-id>         Find callers of a symbol");
            Console.Error.WriteLine("  query <db> callers-tree <symbol-id>    Caller tree (recursive)");
            Console.Error.WriteLine("  query <db> callees <symbol-id>         Find callees of a symbol");
            Console.Error.WriteLine("  query <db> callees-tree <symbol-id>    Callee tree (recursive)");
            Console.Error.WriteLine("  query <db> file-symbols <file-path>    Symbols in a file");
            Console.Error.WriteLine("  query <db> dead-code                   Find unreferenced procedures");
            Console.Error.WriteLine("  query <db> inheritance <class-id>      Class inheritance tree");
            Console.Error.WriteLine("  query <db> impact <symbol-id> [depth]  Impact analysis (transitive callers)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Stats Commands:");
            Console.Error.WriteLine("  stats <db>     Show index statistics");
        }

        static int RunIndex(string[] args)
        {
            if (args.Length < 2)
            {
                ErrLine("Error: Solution path required");
                return 1;
            }

            // --protocol (ticket 583abdec): run as a host's child - the addin in Clarion.exe, or the standalone
            // MCP server. Progress goes to stderr as IndexerProtocol lines, the pipes are UTF-8 without a BOM
            // (set up first thing in Main), "cancel" on stdin (or stdin closing: the host died) cancels, and paths
            // are taken VERBATIM - the host made them absolute (review L1), and a re-normalised library path would
            // change the stored library hash.
            bool protocol = _protocol;
            Func<string, string> fullPath = p => protocol ? p : Path.GetFullPath(p);

            string slnPath = fullPath(args[1]);
            string dbPath = null;
            bool incremental = false;
            List<string> libraryPaths = null;
            bool noLibraryPaths = false;
            string redPath = null;
            string configuration = null;
            bool redExact = false;
            string redVersionName = null;
            string redVersion = null;
            var redMacros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int testSlowMs = 0;
            bool testIgnoreCancel = false;
            string testThrow = null;

            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--configuration" && i + 1 < args.Length)
                {
                    configuration = args[++i];
                }
                else if (args[i] == "--db" && i + 1 < args.Length)
                {
                    dbPath = fullPath(args[++i]);
                }
                else if (args[i] == "--incremental")
                {
                    incremental = true;
                }
                else if (args[i] == "--lib-paths" && i + 1 < args.Length)
                {
                    string raw = args[++i];
                    libraryPaths = new List<string>();
                    foreach (string p in raw.Split(';'))
                    {
                        string trimmed = protocol ? p : p.Trim();
                        if (trimmed.Trim().Length > 0)
                            libraryPaths.Add(fullPath(trimmed));
                    }
                }
                else if (args[i] == "--no-lib-paths")
                {
                    noLibraryPaths = true;
                }
                else if (args[i] == "--red" && i + 1 < args.Length)
                {
                    redPath = fullPath(args[++i]);
                }
                else if (args[i] == "--red-exact")
                {
                    redExact = true;
                }
                else if (args[i] == "--red-version-name" && i + 1 < args.Length)
                {
                    redVersionName = args[++i];
                }
                else if (args[i] == "--red-version" && i + 1 < args.Length)
                {
                    redVersion = args[++i];
                }
                else if (args[i] == "--red-macro" && i + 1 < args.Length)
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq > 0) redMacros[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
                else if (args[i] == "--test-slow-ms" && i + 1 < args.Length)
                {
                    // Harness only: sleep this long after each structured progress event, so a cancel can land mid-run.
                    int.TryParse(args[++i], out testSlowMs);
                }
                else if (args[i] == "--test-ignore-cancel")
                {
                    // Harness only: never honour a cancel, so the host's kill-after-grace path is exercised.
                    testIgnoreCancel = true;
                }
                else if (args[i] == "--test-throw" && i + 1 < args.Length)
                {
                    // Harness only: fail the run with this exception message once the database is open (review L2:
                    // a message with line breaks must not reach the host as more than one stderr line).
                    testThrow = args[++i];
                }
            }

            if (!File.Exists(slnPath))
            {
                ErrLine("Error: Solution file not found: " + slnPath);
                if (protocol) Console.WriteLine(IndexerProtocol.FailureToJson(new FileNotFoundException("Solution file not found: " + slnPath), false, null));
                return 1;
            }

            if (dbPath == null)
            {
                dbPath = Path.Combine(
                    Path.GetDirectoryName(slnPath),
                    Path.GetFileNameWithoutExtension(slnPath) + ".codegraph.db");
            }

            // Load an explicit redirection file when given. %THISDIR%/%REDDIR% anchor to the
            // .red's own folder; %ROOT% is derived when the .red sits in a Clarion-style bin\
            // folder (C:\Clarion11\bin\Clarion110.red -> ROOT=C:\Clarion11). Without --red the
            // indexer falls back to probing the solution directory for a *.red, which misses
            // installs that keep the .red in the Clarion bin folder.
            ClarionAssistant.Services.RedFileService red = null;
            if (redPath != null)
            {
                if (!File.Exists(redPath))
                {
                    ErrLine("Error: redirection file not found: " + redPath);
                    if (protocol) Console.WriteLine(IndexerProtocol.FailureToJson(new FileNotFoundException("redirection file not found: " + redPath), false, null));
                    return 1;
                }
                red = new ClarionAssistant.Services.RedFileService();
                bool loaded;
                if (redExact)
                {
                    // The host's own loaded instance, rebuilt as-is: its final macros and version names (583abdec).
                    loaded = red.LoadAs(redPath, redMacros, redVersionName, redVersion);
                }
                else
                {
                    string redDir = Path.GetDirectoryName(redPath);
                    var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["THISDIR"] = redDir,
                        ["REDDIR"] = redDir
                    };
                    if (string.Equals(Path.GetFileName(redDir), "bin", StringComparison.OrdinalIgnoreCase))
                        macros["ROOT"] = Path.GetDirectoryName(redDir);
                    foreach (var kv in redMacros) macros[kv.Key] = kv.Value;
                    loaded = red.Load(redPath, macros);
                }
                if (!loaded)
                {
                    ErrLine("Error: failed to parse redirection file: " + redPath);
                    if (protocol) Console.WriteLine(IndexerProtocol.FailureToJson(new InvalidDataException("failed to parse redirection file: " + redPath), false, null));
                    return 1;
                }

                // Parity with the addin's RunIndex: when no explicit --lib-paths, derive the
                // library sweep dirs from the .red's .inc search paths. --no-lib-paths: the host
                // passed none on purpose.
                if (libraryPaths == null && !noLibraryPaths)
                {
                    var incPaths = red.GetSearchPaths(".inc");
                    if (incPaths.Count > 0)
                        libraryPaths = incPaths;
                }
            }

            ErrLine("Indexing: " + slnPath);
            ErrLine("Process: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + ", pid " + Process.GetCurrentProcess().Id);
            ErrLine("Database: " + dbPath);
            ErrLine("Mode requested: " + (incremental ? "incremental" : "full"));
            if (libraryPaths != null && libraryPaths.Count > 0)
                ErrLine("Library paths: " + string.Join("; ", libraryPaths));
            if (redPath != null)
                ErrLine("Redirection: " + redPath);
            ErrLine();

            // 583abdec review D1: THIS process's own exclusive writer lock (<db>.writer.lock) for the whole run - never
            // a handle inherited from the host. The host's lock (<db>.lock) dies with the host; if the host dies this
            // process keeps writing until its next cancel poll, and the host-side gate (IndexRunGate.TryEnter) treats
            // this lock as held, so a new run cannot ClearAll() under it. A second writer is refused here too.
            IDisposable writerLock;
            string writerHolder;
            if (ClarionAssistant.Services.IndexRunGate.TryAcquireWriterLock(dbPath, out writerLock, out writerHolder)
                == ClarionAssistant.Services.IndexRunGate.LockResult.Blocked)
            {
                string why = "another index run is writing " + dbPath + " - held by " + (writerHolder ?? "another process");
                ErrLine("Error: " + why);
                if (protocol) Console.WriteLine(IndexerProtocol.FailureToJson(new IOException(why), false, null));
                return IndexerProtocol.ExitFailed;
            }

            // Cooperative cancel from the host: "cancel" on stdin, or stdin closing (the host process died -
            // the pipe's write end goes with it), which must not leave an orphan writing the database.
            int cancelFlag = 0;
            int hostGone = 0;
            if (protocol)
            {
                var stdinWatch = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                        string line;
                        while ((line = input.ReadLine()) != null)
                        {
                            if (line.Trim().Trim('\uFEFF').Equals(IndexerProtocol.CancelCommand, StringComparison.OrdinalIgnoreCase))
                            {
                                System.Threading.Interlocked.Exchange(ref cancelFlag, 1);
                                return;
                            }
                        }
                        System.Threading.Interlocked.Exchange(ref hostGone, 1);
                        System.Threading.Interlocked.Exchange(ref cancelFlag, 1);
                    }
                    catch { }
                }) { IsBackground = true, Name = "host-cancel" };
                stdinWatch.Start();
            }

            CodeGraphIndexer indexer = null;
            CodeGraphDatabase db = null;
            try
            {
                db = new CodeGraphDatabase();
                db.Open(dbPath);

                indexer = new CodeGraphIndexer(db);
                indexer.RedService = red;
                indexer.BuildConfiguration = configuration;
                if (protocol)
                {
                    var self = indexer;
                    bool modeSent = false;
                    indexer.OnProgress += msg =>
                    {
                        // EffectiveIncremental is decided before the first progress message, and before anything is
                        // cleared: the host needs it to handle a cancel (a cancelled FULL run leaves a partial db).
                        if (!modeSent) { modeSent = true; Record(IndexerProtocol.EncodeMode(self.EffectiveIncremental)); }
                        Record(IndexerProtocol.EncodeMessage(msg));
                    };
                    indexer.OnProgressEvent += ev =>
                    {
                        Record(IndexerProtocol.EncodeEvent(ev));
                        if (testSlowMs > 0) System.Threading.Thread.Sleep(testSlowMs);
                    };
                    indexer.CancelRequested = () => !testIgnoreCancel
                        && System.Threading.Interlocked.CompareExchange(ref cancelFlag, 0, 0) == 1;
                }
                else
                {
                    indexer.OnProgress += msg => ErrLine("  " + msg);
                }

                if (testThrow != null) throw new InvalidOperationException(testThrow);

                var sw = Stopwatch.StartNew();
                var result = indexer.IndexSolution(slnPath, incremental, libraryPaths);
                sw.Stop();

                // 583abdec review D4a: the database is CLOSED (and the writer lock released) before the result is
                // printed - a host that reads the result may delete, reopen or re-index the file at once.
                db.Close();
                db = null;
                ReleaseWriter(ref writerLock);

                // The result as JSON on stdout: what a host (OutOfProcessIndexer) rebuilds its IndexResult from.
                Console.WriteLine(IndexerProtocol.ResultToJson(result, dbPath, sw.ElapsedMilliseconds));

                ErrLine();
                ErrLine("Done: " + result.SymbolCount + " symbols, " +
                    result.FileCount + " files, " + result.ProjectCount + " projects in " +
                    sw.ElapsedMilliseconds + "ms");
                ErrLine("Mode: " + result.ModeText);
                foreach (string coverageLine in result.CoverageText().Split('\n'))
                    ErrLine(coverageLine.TrimEnd('\r'));

                return IndexerProtocol.ExitOk;
            }
            catch (OperationCanceledException ex)
            {
                bool? mode = indexer != null ? indexer.EffectiveIncremental : (bool?)null;
                try { if (db != null) db.Close(); } catch { }
                // The host died mid-run: nobody is left to delete a cancelled FULL run's partial database, which
                // would otherwise pass for a complete one. (A host that is alive deletes it itself, verified.)
                // Deleted while this process still holds the writer lock, so no new run can start in between.
                if (System.Threading.Interlocked.CompareExchange(ref hostGone, 0, 0) == 1 && mode == false)
                {
                    try { File.Delete(dbPath); } catch { }
                    if (!File.Exists(dbPath))
                    {
                        try { File.Delete(dbPath + "-wal"); } catch { }
                        try { File.Delete(dbPath + "-shm"); } catch { }
                    }
                }
                ReleaseWriter(ref writerLock);
                Console.WriteLine(IndexerProtocol.FailureToJson(ex, true, mode));
                ErrLine("Cancelled: " + ex.Message);
                return IndexerProtocol.ExitCancelled;
            }
            catch (Exception ex)
            {
                try { if (db != null) db.Close(); } catch { }
                ReleaseWriter(ref writerLock);
                Console.WriteLine(IndexerProtocol.FailureToJson(ex, false, indexer != null ? indexer.EffectiveIncremental : (bool?)null));
                ErrLine("Error: " + ex.GetType().Name + ": " + ex.Message);
                return IndexerProtocol.ExitFailed;
            }
            finally
            {
                ReleaseWriter(ref writerLock);
            }
        }

        static void ReleaseWriter(ref IDisposable writerLock)
        {
            var h = writerLock;
            writerLock = null;
            if (h != null) { try { h.Dispose(); } catch { } }
        }

        static int RunQuery(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Error: Database path and query command required");
                return 1;
            }

            string dbPath = Path.GetFullPath(args[1]);
            string queryCmd = args[2].ToLower();

            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine("Error: Database not found: " + dbPath);
                return 1;
            }

            try
            {
                var db = new CodeGraphDatabase();
                db.Open(dbPath);
                var query = new CodeGraphQuery(db);

                DataTable result = null;

                switch (queryCmd)
                {
                    case "find-symbol":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol name required"); return 1; }
                        result = query.FindSymbol(args[3]);
                        break;

                    case "callers":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCallers(long.Parse(args[3]));
                        break;

                    case "callers-tree":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCallersTree(long.Parse(args[3]));
                        break;

                    case "callees":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCallees(long.Parse(args[3]));
                        break;

                    case "callees-tree":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        result = query.GetCalleesTree(long.Parse(args[3]));
                        break;

                    case "file-symbols":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: File path required"); return 1; }
                        result = query.GetFileSymbols(args[3]);
                        break;

                    case "dead-code":
                        result = query.GetDeadCode();
                        break;

                    case "inheritance":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Class ID required"); return 1; }
                        result = query.GetInheritanceTree(long.Parse(args[3]));
                        break;

                    case "impact":
                        if (args.Length < 4) { Console.Error.WriteLine("Error: Symbol ID required"); return 1; }
                        int depth = args.Length >= 5 ? int.Parse(args[4]) : 10;
                        result = query.GetImpact(long.Parse(args[3]), depth);
                        break;

                    default:
                        Console.Error.WriteLine("Unknown query command: " + queryCmd);
                        return 1;
                }

                if (result != null)
                {
                    PrintDataTableAsJson(result);
                }

                db.Close();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static int RunStats(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Error: Database path required");
                return 1;
            }

            string dbPath = Path.GetFullPath(args[1]);
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine("Error: Database not found: " + dbPath);
                return 1;
            }

            try
            {
                var db = new CodeGraphDatabase();
                db.Open(dbPath);
                var query = new CodeGraphQuery(db);
                var stats = query.GetStats();
                PrintDataTableAsJson(stats);
                db.Close();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static void PrintDataTableAsJson(DataTable dt)
        {
            Console.WriteLine("[");
            for (int r = 0; r < dt.Rows.Count; r++)
            {
                Console.Write("  {");
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) Console.Write(", ");
                    string colName = dt.Columns[c].ColumnName;
                    object val = dt.Rows[r][c];

                    Console.Write(JsonEscape(colName) + ": ");
                    if (val == null || val == DBNull.Value)
                        Console.Write("null");
                    else if (val is long || val is int || val is double || val is float || val is decimal)
                        Console.Write(val.ToString());
                    else
                        Console.Write(JsonEscape(val.ToString()));
                }
                Console.WriteLine(r < dt.Rows.Count - 1 ? "}," : "}");
            }
            Console.WriteLine("]");
        }

        static string JsonEscape(string s)
        {
            if (s == null) return "null";
            return "\"" + s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }
    }
}
