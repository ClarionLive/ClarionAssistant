// Ticket 583abdec: every host-side index runs in clarion-indexer.exe, a 64-bit child process, instead of inside the
// 32-bit Clarion IDE (which ran out of memory on a large solution's relationship pass).
//
// This harness is the 32-bit HOST (compiled x86, like Clarion.exe). It:
//   P   round-trips the stderr progress protocol and the stdout result JSON (tricky text, every IndexResult field);
//   B   builds the REAL clarion-indexer.exe from the shared sources (x64, as the csproj does) into a folder laid out
//       like the deployed addin folder - with the 32-bit SQLite.Interop.dll at its ROOT, the trap the indexer's
//       preload exists for - and runs it through the REAL OutOfProcessIndexer;
//   E   indexes one fixture solution in-process and through the child, with the IDE's kind of inputs (an explicit
//       .red with host macros - spaces and '=' in a value - library paths, a build configuration), and requires the
//       same IndexResult, the same database rows and the same progress stream;
//   U   an incremental request on an older database runs full, and the host learns it (EffectiveIncremental);
//   C   cancel: graceful ("cancel" on stdin, exit 2) and forced (a child that ignores it is killed after the grace);
//   F   failures: a child error, a child that dies without a result, a missing x64 interop - each names the exe, the
//       exit code and the end of its stderr; a missing exe falls back in-process WITH a warning.
//
// Args: <csc.exe> <ClarionAssistant dir>.   Exit: 0 pass, 1 fail.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClarionAssistant.Services;
using ClarionCodeGraph.Graph;

static class OutOfProcessIndexerTest
{
    static int _assertions;
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string id, string message)
    {
        _assertions++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + id + "  " + message);
        if (!ok) Failures.Add(id + ": " + message);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

    static void Write(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }

    const string G1 = "33333333-0000-0000-0000-000000000001";
    const string G2 = "33333333-0000-0000-0000-000000000002";

    /// <summary>Two apps, a class reached through INCLUDE + MODULE() via the .red, several files each (so a slowed
    /// child emits enough events to cancel mid-run). The root folder name has a space. Returns the .sln path.</summary>
    static string BuildFixture(string root)
    {
        string lib = Path.Combine(root, "Shared Lib");
        Write(Path.Combine(lib, "Shared.inc"),
            "SharedThing         CLASS,TYPE,MODULE('Shared.clw'),LINK('Shared.clw')",
            "  Run                 PROCEDURE()",
            "                    END");
        Write(Path.Combine(lib, "Shared.clw"),
            "  MEMBER()",
            "  INCLUDE('Shared.inc'),ONCE",
            "SharedThing.Run PROCEDURE()",
            "  CODE");
        foreach (var app in new[] { new { Name = "App", Guid = G1 }, new { Name = "Other", Guid = G2 } })
        {
            string dir = Path.Combine(root, app.Name);
            var compile = new StringBuilder();
            var map = new StringBuilder();
            for (int i = 1; i <= 6; i++)
            {
                string proc = app.Name + "Proc" + i;
                Write(Path.Combine(dir, proc + ".clw"),
                    "  MEMBER('" + app.Name + ".clw')",
                    proc + " PROCEDURE()",
                    "  CODE",
                    "  " + (i < 6 ? app.Name + "Proc" + (i + 1) + "()" : "Thing.Run()"));
                compile.Append("    <Compile Include=\"" + proc + ".clw\" />\r\n");
                map.Append(proc + "  PROCEDURE()\r\n");
            }
            Write(Path.Combine(dir, app.Name + ".clw"),
                "  PROGRAM",
                "  INCLUDE('Shared.inc'),ONCE",
                "  MAP",
                map.ToString().TrimEnd(),
                "  END",
                "Thing   SharedThing",
                "  CODE",
                "  " + app.Name + "Proc1()");
            File.WriteAllText(Path.Combine(dir, app.Name + ".cwproj"),
                "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
                "  <PropertyGroup>\r\n    <ProjectGuid>{" + app.Guid + "}</ProjectGuid>\r\n    <OutputType>WinExe</OutputType>\r\n" +
                "    <AssemblyName>" + app.Name + "</AssemblyName>\r\n  </PropertyGroup>\r\n  <ItemGroup>\r\n" +
                "    <Compile Include=\"" + app.Name + ".clw\" />\r\n" + compile + "  </ItemGroup>\r\n</Project>\r\n", new UTF8Encoding(false));
        }
        string sln = Path.Combine(root, "Fix.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 11.00\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"App\", \"App\\App.cwproj\", \"{" + G1 + "}\"\r\nEndProject\r\n" +
            "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"Other\", \"Other\\Other.cwproj\", \"{" + G2 + "}\"\r\nEndProject\r\n",
            new UTF8Encoding(false));
        return sln;
    }

    /// <summary>The IDE's kind of redirection: a version-named .red in a "bin" folder, loaded with host macros whose
    /// values carry a space and an '=' (they must survive the command line), resolving the shared library only
    /// through a macro.</summary>
    static RedFileService BuildRed(string root)
    {
        string bin = Path.Combine(root, "Clarion Root", "bin");
        Write(Path.Combine(bin, "Clarion120.red"),
            "[Common]",
            "*.inc = %SHAREDLIB%",
            "*.clw = %SHAREDLIB%",
            "[Release32]",
            "*.inc = %SHAREDLIB%",
            "*.clw = %SHAREDLIB%");
        var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "SHAREDLIB", Path.Combine(root, "Shared Lib") },
            { "ROOT", Path.Combine(root, "Clarion Root") },
            { "ODD", "a=b c\"d\\" }
        };
        var red = new RedFileService();
        if (!red.Load(Path.Combine(bin, "Clarion120.red"), macros)) throw new Exception("fixture .red did not load");
        return red;
    }

    static string Csc;
    static string RepoDir;

    static readonly string[] IndexerSources =
    {
        "indexer\\Program.cs",
        "CodeGraph\\Graph\\CodeGraphDatabase.cs", "CodeGraph\\Graph\\CodeGraphIndexer.cs", "CodeGraph\\Graph\\IndexProgress.cs",
        "CodeGraph\\Graph\\IndexerProtocol.cs", "CodeGraph\\Graph\\CodeGraphQuery.cs",
        "CodeGraph\\Parsing\\ClarionParser.cs", "CodeGraph\\Parsing\\ClarionBuiltins.cs", "CodeGraph\\Parsing\\SolutionParser.cs",
        "CodeGraph\\Parsing\\ProjectParser.cs", "CodeGraph\\Parsing\\SourceResolver.cs",
        "CodeGraph\\Parsing\\Models\\ClarionSymbol.cs", "CodeGraph\\Parsing\\Models\\ClarionRelationship.cs",
        "CodeGraph\\Parsing\\Models\\SolutionProject.cs", "CodeGraph\\Parsing\\Models\\ParseResult.cs",
        "Services\\RedFileService.cs", "Services\\RedResolution.cs", "Services\\ClarionConfigDirectory.cs",
        "Services\\ClarionVersionService.cs", "Services\\EncodingHelper.cs"
    };

    static int RunCsc(string args)
    {
        var psi = new ProcessStartInfo(Csc, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using (var p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) Console.WriteLine(o);
            return p.ExitCode;
        }
    }

    /// <summary>The deployed addin folder's layout: exe, managed SQLite, the 32-bit interop at the ROOT (the IDE's),
    /// and x86\ + x64\ subfolders. withX64=false leaves x64\ out.</summary>
    static string StageIndexer(string stage, bool withX64)
    {
        Directory.CreateDirectory(Path.Combine(stage, "x86"));
        string lib = Path.Combine(RepoDir, "lib", "sqlite-fts5");
        File.Copy(Path.Combine(lib, "System.Data.SQLite.dll"), Path.Combine(stage, "System.Data.SQLite.dll"), true);
        File.Copy(Path.Combine(lib, "SQLite.Interop.dll"), Path.Combine(stage, "SQLite.Interop.dll"), true);
        File.Copy(Path.Combine(lib, "SQLite.Interop.dll"), Path.Combine(stage, "x86", "SQLite.Interop.dll"), true);
        if (withX64)
        {
            Directory.CreateDirectory(Path.Combine(stage, "x64"));
            File.Copy(Path.Combine(lib, "x64", "SQLite.Interop.dll"), Path.Combine(stage, "x64", "SQLite.Interop.dll"), true);
        }
        string exe = Path.Combine(stage, "clarion-indexer.exe");
        var src = string.Join(" ", IndexerSources.Select(s => "\"" + Path.Combine(RepoDir, s) + "\""));
        int rc = RunCsc("/nologo /warn:0 /platform:x64 /out:\"" + exe + "\" /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Data.dll " +
                        "/r:\"" + Path.Combine(lib, "System.Data.SQLite.dll") + "\" " + src);
        return rc == 0 ? exe : null;
    }

    // ------------------------------------------------------------------ P: protocol

    static void ProtocolRoundTrip()
    {
        string tricky = "C:\\Dir With Space\\a\"b\tc\r\nd\u00e9\u2028\u0001end\\";
        IndexerProtocol.Line l;
        Check(IndexerProtocol.TryDecode(IndexerProtocol.EncodeMessage(tricky), out l) && l.Kind == "msg" && l.Message == tricky,
            "P1", "a message with quotes, tabs, CR/LF, a backslash at the end, non-ASCII and control characters round-trips");
        Check(IndexerProtocol.EncodeMessage(tricky).IndexOfAny(new[] { '\r', '\n', '\u2028' }) < 0,
            "P2", "an encoded line holds no raw line break of any kind");
        var ev = new IndexProgressEvent { Phase = "resolving", ProjectName = null, CurrentFile = "x\ty.clw", Message = null,
                                          FilesDone = 12, FilesTotal = 345, SymbolCount = 6789, RelationshipCount = 2000000000 };
        Check(IndexerProtocol.TryDecode(IndexerProtocol.EncodeEvent(ev), out l) && l.Kind == "ev" && l.Event.Phase == ev.Phase
              && l.Event.ProjectName == null && l.Event.CurrentFile == ev.CurrentFile && l.Event.Message == null
              && l.Event.FilesDone == 12 && l.Event.FilesTotal == 345 && l.Event.SymbolCount == 6789 && l.Event.RelationshipCount == 2000000000,
            "P3", "a progress event round-trips every field, nulls included");
        Check(IndexerProtocol.TryDecode(IndexerProtocol.EncodeMode(false), out l) && l.Kind == "mode" && !l.Incremental
              && IndexerProtocol.TryDecode(IndexerProtocol.EncodeMode(true), out l) && l.Incremental,
            "P4", "the mode line round-trips both values");
        Check(!IndexerProtocol.TryDecode("  Parsing solution file...", out l) && !IndexerProtocol.TryDecode(IndexerProtocol.Prefix + "{\"t\":\"msg\",", out l)
              && !IndexerProtocol.TryDecode(null, out l),
            "P5", "console text and a truncated protocol line are not decoded");

        var r = new IndexResult
        {
            SlnPath = "C:\\x y\\S.sln", ProjectCount = 3, FileCount = 44, SymbolCount = 555, RelationshipCount = 6666, DurationMs = 7777,
            ProjectsResolved = 2, ProjectsUnresolved = 1, CwprojFilesListed = 40, CwprojFilesUnresolved = 3,
            FilesParsed = 41, FilesUnresolved = 2, FilesSkipped = 1, ModuleFilesIndexed = 5, ModuleTargetsUnresolved = 2,
            ModuleTargetsSkipped = 9, IncludeTargetsUnresolved = 4, GlobalDataFilesRead = 6, IndexerVersion = "9.8.7.6",
            FeatureLevel = 2, RequestedIncremental = true, Incremental = false, UpgradedFromFeatureLevel = 1, UpToDate = false
        };
        r.UnresolvedProjectNames.Add("Gone \"App\"");
        var o = IndexerProtocol.ParseOutcome("noise before\n" + IndexerProtocol.ResultToJson(r, "C:\\x y\\S.codegraph.db", 123) + "\n");
        var back = o != null ? o.Result : null;
        Check(back != null && o.Success && o.EffectiveIncremental == false, "P6", "the result JSON parses as a successful full run");
        if (back == null) return;
        bool same = back.SlnPath == r.SlnPath && back.ProjectCount == 3 && back.FileCount == 44 && back.SymbolCount == 555
            && back.RelationshipCount == 6666 && back.DurationMs == 7777 && back.ProjectsResolved == 2 && back.ProjectsUnresolved == 1
            && back.CwprojFilesListed == 40 && back.CwprojFilesUnresolved == 3 && back.FilesParsed == 41 && back.FilesUnresolved == 2
            && back.FilesSkipped == 1 && back.ModuleFilesIndexed == 5 && back.ModuleTargetsUnresolved == 2 && back.ModuleTargetsSkipped == 9
            && back.IncludeTargetsUnresolved == 4 && back.GlobalDataFilesRead == 6 && back.IndexerVersion == "9.8.7.6"
            && back.FeatureLevel == 2 && back.RequestedIncremental && !back.Incremental && back.UpgradedFromFeatureLevel == 1 && !back.UpToDate
            && back.UnresolvedProjectNames.Count == 1 && back.UnresolvedProjectNames[0] == "Gone \"App\"";
        Check(same, "P7", "every IndexResult field survives the round trip");
        Check(back.Headline == r.Headline && back.ModeText == r.ModeText && back.CoverageText() == r.CoverageText()
              && back.Status == r.Status && back.Warnings.SequenceEqual(r.Warnings),
            "P8", "Headline, ModeText, Status, Warnings and CoverageText are the original's, character for character");
        var fail = IndexerProtocol.ParseOutcome(IndexerProtocol.FailureToJson(new OperationCanceledException("stop"), true, false));
        Check(fail != null && !fail.Success && fail.Cancelled && fail.EffectiveIncremental == false && fail.Error == "stop",
            "P9", "a cancelled run's JSON says cancelled, and which mode it was running");
        Check(IndexerProtocol.ParseOutcome("") == null && IndexerProtocol.ParseOutcome("crashed {") == null,
            "P10", "no JSON on stdout reads as no outcome (not as a success)");

        string args = OutOfProcessIndexer.BuildArguments(new IndexRunRequest { SlnPath = "C:\\a b\\s.sln", Incremental = true, LibraryPaths = null });
        Check(args.Contains("--protocol") && args.Contains("--incremental") && args.Contains("--no-lib-paths") && args.Contains("\"C:\\a b\\s.sln\""),
            "P11", "the command line carries --protocol, the mode, the quoted solution and --no-lib-paths when the host has none");
        Check(OutOfProcessIndexer.QuoteArgument("C:\\dir\\") == "C:\\dir\\" && OutOfProcessIndexer.QuoteArgument("C:\\a b\\") == "\"C:\\a b\\\\\""
              && OutOfProcessIndexer.QuoteArgument("x\"y") == "\"x\\\"y\"" && OutOfProcessIndexer.QuoteArgument("") == "\"\"",
            "P12", "argument quoting doubles a trailing backslash inside quotes and escapes quotes");
    }

    // ------------------------------------------------------------------ helpers over a database

    static List<string> Rows(string dbPath, string sql)
    {
        var db = new CodeGraphDatabase();
        db.Open(dbPath);
        try
        {
            var dt = db.ExecuteQuery(sql);
            var list = new List<string>();
            foreach (System.Data.DataRow row in dt.Rows)
                list.Add(string.Join("|", row.ItemArray.Select(v => v == null || v == DBNull.Value ? "<null>" : v.ToString())));
            return list;
        }
        finally { db.Close(); }
    }

    static string DbShape(string dbPath)
    {
        // Ids are assigned in the same order by the same code, so whole rows compare.
        var sb = new StringBuilder();
        sb.Append("symbols=").Append(string.Join("\n", Rows(dbPath, "SELECT * FROM symbols ORDER BY id"))).Append("\n");
        sb.Append("relationships=").Append(string.Join("\n", Rows(dbPath, "SELECT * FROM relationships ORDER BY id"))).Append("\n");
        sb.Append("indexed_files=").Append(string.Join("\n", Rows(dbPath, "SELECT * FROM indexed_files ORDER BY id"))).Append("\n");
        sb.Append("projects=").Append(string.Join("\n", Rows(dbPath, "SELECT * FROM projects ORDER BY id")));
        return sb.ToString();
    }

    static int Count(string dbPath, string table)
    {
        var r = Rows(dbPath, "SELECT COUNT(*) FROM " + table);
        return r.Count == 0 ? -1 : int.Parse(r[0]);
    }

    static string EventKey(IndexProgressEvent e)
    {
        return e.Phase + "|" + e.ProjectName + "|" + e.CurrentFile + "|" + e.FilesDone + "/" + e.FilesTotal + "|" + e.SymbolCount + "|" + e.RelationshipCount;
    }

    // ------------------------------------------------------------------ main

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: <csc.exe> <ClarionAssistant dir>"); return 1; }
        Csc = args[0];
        RepoDir = args[1];
        string root = Path.Combine(Path.GetTempPath(), "ca-oopidx " + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            ProtocolRoundTrip();

            string exe = StageIndexer(Path.Combine(root, "addin"), true);
            Check(exe != null && File.Exists(exe), "B1", "the real clarion-indexer.exe builds from the shared sources (x64)");
            if (exe == null) return Finish();

            string sln = BuildFixture(Path.Combine(root, "sol"));
            var red = BuildRed(Path.Combine(root, "sol"));
            var libs = new List<string> { Path.Combine(root, "sol", "Shared Lib") + "\\" };
            const string config = "Release";

            // ---- E: in-process (the old path) vs the child (the new path), same inputs ----
            string dbIn = Path.Combine(root, "in.codegraph.db");
            string dbOut = Path.Combine(root, "out.codegraph.db");
            var inMsgs = new List<string>(); var inEvents = new List<string>();
            IndexResult inRes;
            {
                var db = new CodeGraphDatabase();
                db.Open(dbIn);
                try
                {
                    var ix = new CodeGraphIndexer(db) { RedService = red, BuildConfiguration = config };
                    ix.OnProgress += m => inMsgs.Add(m);
                    ix.OnProgressEvent += e => inEvents.Add(EventKey(e));
                    inRes = ix.IndexSolution(sln, false, libs);
                }
                finally { db.Close(); }
            }
            Check(inRes.SymbolCount > 0 && Count(dbIn, "relationships") > 0 && inRes.ModuleFilesIndexed > 0, "E0",
                "control: the fixture indexes in-process, with relationships and a MODULE() file reached through the .red macro");

            var runner = new OutOfProcessIndexer { ExePath = exe };
            var outMsgs = new List<string>(); var outEvents = new List<string>();
            int callerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            bool allOnCaller = true;
            bool? wow64 = null;
            runner.OnProgress += m => { outMsgs.Add(m); allOnCaller &= System.Threading.Thread.CurrentThread.ManagedThreadId == callerThread; };
            runner.OnProgressEvent += e => { outEvents.Add(EventKey(e)); allOnCaller &= System.Threading.Thread.CurrentThread.ManagedThreadId == callerThread; };
            runner.ChildStarted += p => { bool w; if (IsWow64Process(p.Handle, out w)) wow64 = w; };
            IndexResult outRes = null;
            try
            {
                outRes = runner.Run(new IndexRunRequest { SlnPath = sln, DbPath = dbOut, Incremental = false, Red = red, BuildConfiguration = config, LibraryPaths = libs });
            }
            catch (Exception ex) { Check(false, "E1", "the child run threw: " + ex.Message); }
            if (outRes != null)
            {
                Check(!runner.RanInProcess && runner.FallbackWarning == null, "E1", "the run went through the child process, no fallback");
                Check(wow64 == false && Environment.Is64BitOperatingSystem, "E2", "the child is a 64-bit process (IsWow64Process=" + (wow64.HasValue ? wow64.ToString() : "?") + "), from a folder whose root holds the 32-bit interop");
                Check(outRes.SymbolCount == inRes.SymbolCount && outRes.RelationshipCount == inRes.RelationshipCount && outRes.FileCount == inRes.FileCount
                      && outRes.ProjectCount == inRes.ProjectCount && outRes.ModuleFilesIndexed == inRes.ModuleFilesIndexed
                      && outRes.FilesParsed == inRes.FilesParsed && outRes.CwprojFilesListed == inRes.CwprojFilesListed,
                    "E3", "the child's counts are the in-process run's (symbols " + outRes.SymbolCount + "/" + inRes.SymbolCount +
                          ", relationships " + outRes.RelationshipCount + "/" + inRes.RelationshipCount + ")");
                Check(outRes.Headline == inRes.Headline && outRes.ModeText == inRes.ModeText && outRes.CoverageText() == inRes.CoverageText(),
                    "E4", "the formatted result (Headline, Mode, coverage block) is the in-process one");
                string a = DbShape(dbIn), b = DbShape(dbOut);
                Check(a == b, "E5", "the databases hold the same symbols, relationships, indexed_files and projects (" + Count(dbOut, "symbols") + " symbols, " + Count(dbOut, "relationships") + " relationships, " + Count(dbOut, "indexed_files") + " indexed_files)");
                Check(outEvents.SequenceEqual(inEvents) && outEvents.Count > 0, "E6", "the structured progress stream is the in-process one (" + outEvents.Count + " events)");
                Check(outMsgs.Count == inMsgs.Count && outMsgs.Select(StripDigits).SequenceEqual(inMsgs.Select(StripDigits)), "E7",
                    "the progress messages are the in-process ones (" + outMsgs.Count + " messages; timings aside)");
                Check(outMsgs.Exists(m => m.Contains("Clarion120.red") && m.Contains("host-supplied")) && outMsgs.Exists(m => m.Contains("Build configuration: Release")),
                    "E8", "the child used the host's .red and build configuration");
                Check(allOnCaller, "E9", "every callback ran on the thread that called Run, as the in-process indexer's did");
                Check(!runner.EffectiveIncremental, "E10", "EffectiveIncremental: a full request ran full");
            }

            // ---- U: an incremental request on an older-level database runs full; the host learns it ----
            {
                var db = new CodeGraphDatabase(); db.Open(dbOut);
                try { db.ExecuteQuery("DELETE FROM index_metadata WHERE key IN ('indexer_version','indexer_feature_level','indexer_features')"); db.SetMetadata(CodeGraphIndexer.MetaFeatureLevel, "1"); }
                finally { db.Close(); }
                var up = new OutOfProcessIndexer { ExePath = exe };
                IndexResult ur = null;
                try { ur = up.Run(new IndexRunRequest { SlnPath = sln, DbPath = dbOut, Incremental = true, Red = red, BuildConfiguration = config, LibraryPaths = libs }); }
                catch (Exception ex) { Check(false, "U1", "threw: " + ex.Message); }
                Check(ur != null && !ur.Incremental && ur.RequestedIncremental && ur.UpgradedFromFeatureLevel == 1 && !up.EffectiveIncremental
                      && ur.ModeText.StartsWith("full (upgraded from feature level 1"),
                    "U1", "incremental on a level-1 database ran full, upgraded, and EffectiveIncremental=false (" + (ur != null ? ur.ModeText : "no result") + ")");
                var inc = new OutOfProcessIndexer { ExePath = exe };
                IndexResult nr = null;
                try { nr = inc.Run(new IndexRunRequest { SlnPath = sln, DbPath = dbOut, Incremental = true, Red = red, BuildConfiguration = config, LibraryPaths = libs }); }
                catch (Exception ex) { Check(false, "U2", "threw: " + ex.Message); }
                Check(nr != null && nr.Incremental && nr.UpToDate && inc.EffectiveIncremental, "U2", "a current database stays incremental and up to date");
            }

            // ---- C: cancel ----
            {
                string dbC = Path.Combine(root, "cancel.codegraph.db");
                var c = new OutOfProcessIndexer { ExePath = exe, ExtraArgumentsForTest = "--test-slow-ms 400" };
                int events = 0; bool modeKnownAtFirstEvent = false;
                Process child = null;
                c.ChildStarted += p => child = p;
                c.OnProgressEvent += e => { if (events++ == 0) modeKnownAtFirstEvent = true; };
                c.CancelRequested = () => events > 0;
                var sw = Stopwatch.StartNew();
                bool cancelled = false;
                try { c.Run(new IndexRunRequest { SlnPath = sln, DbPath = dbC, Incremental = true, Red = red, LibraryPaths = libs }); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { Check(false, "C1", "threw " + ex.GetType().Name + ": " + ex.Message); }
                sw.Stop();
                int exitCode = c.ExitCode;
                Check(cancelled, "C1", "Cancel ends the run with OperationCanceledException, as the in-process cancel did");
                Check(exitCode == IndexerProtocol.ExitCancelled && sw.ElapsedMilliseconds < 10000, "C2",
                    "the child honoured the cancel itself (exit " + exitCode + ") in " + sw.ElapsedMilliseconds + " ms, before the grace ran out");
                Check(modeKnownAtFirstEvent && !c.EffectiveIncremental, "C3",
                    "an incremental request on a new database ran FULL, and the host knows it - so the caller deletes the partial database");
                bool deletable = false;
                try { File.Delete(dbC); deletable = !File.Exists(dbC); } catch { }
                Check(deletable, "C4", "after Run returns the child holds no handle: the cancelled full run's database can be deleted");

                var k = new OutOfProcessIndexer { ExePath = exe, ExtraArgumentsForTest = "--test-slow-ms 3000 --test-ignore-cancel", CancelGrace = TimeSpan.FromSeconds(1) };
                int kev = 0;
                Process kchild = null;
                k.ChildStarted += p => kchild = p;
                k.OnProgressEvent += e => kev++;
                k.CancelRequested = () => kev > 0;
                sw = Stopwatch.StartNew();
                cancelled = false;
                try { k.Run(new IndexRunRequest { SlnPath = sln, DbPath = Path.Combine(root, "kill.codegraph.db"), Incremental = true, Red = red, LibraryPaths = libs }); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { Check(false, "C5", "threw " + ex.GetType().Name + ": " + ex.Message); }
                sw.Stop();
                bool exited = false; try { exited = kchild.HasExited; } catch { exited = true; }
                Check(cancelled && exited && sw.ElapsedMilliseconds < 12000 && k.ExitCode != IndexerProtocol.ExitCancelled, "C5",
                    "a child that ignores the cancel is killed after the grace; Run still reports a cancel (" + sw.ElapsedMilliseconds + " ms, exit " + k.ExitCode + ")");
                Check(!k.EffectiveIncremental, "C6", "a KILLED child wrote no result, yet the host knows its incremental request ran FULL (the mode line comes first) - so the partial database is deleted");
            }

            // ---- F: failures ----
            {
                var f = new OutOfProcessIndexer { ExePath = exe };
                string msg = null;
                try { f.Run(new IndexRunRequest { SlnPath = Path.Combine(root, "nope.sln"), DbPath = Path.Combine(root, "nope.db") }); }
                catch (IndexerProcessException ex) { msg = ex.Message; }
                catch (Exception ex) { msg = "WRONG TYPE " + ex.GetType().Name + ": " + ex.Message; }
                Check(msg != null && msg.Contains("clarion-indexer.exe") && msg.Contains(exe) && msg.Contains("exit code 1") && msg.Contains("Solution file not found"),
                    "F1", "a child that fails reports the exe, its exit code and its error: " + FirstLine(msg));

                // A child that dies without writing a result.
                string crashSrc = Path.Combine(root, "crash.cs");
                File.WriteAllText(crashSrc, "class C { static int Main() { System.Console.Error.WriteLine(\"boom: out of address space\"); return 3; } }", new UTF8Encoding(false));
                string crashExe = Path.Combine(root, "crash", "clarion-indexer.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(crashExe));
                RunCsc("/nologo /out:\"" + crashExe + "\" \"" + crashSrc + "\"");
                var d = new OutOfProcessIndexer { ExePath = crashExe };
                msg = null; IndexerProcessException ipe = null;
                try { d.Run(new IndexRunRequest { SlnPath = sln }); }
                catch (IndexerProcessException ex) { msg = ex.Message; ipe = ex; }
                catch (Exception ex) { msg = "WRONG TYPE " + ex.GetType().Name + ": " + ex.Message; }
                Check(msg != null && msg.Contains(crashExe) && msg.Contains("exit code 3") && msg.Contains("wrote no result") && msg.Contains("boom: out of address space")
                      && ipe != null && ipe.ExitCode == 3,
                    "F2", "a child that dies without a result: exe, exit code 3 and the tail of its stderr - never a fallback");

                // The deployed folder without x64\: a clear error at start, not a BadImageFormatException later.
                string noX64 = StageIndexer(Path.Combine(root, "addin-no-x64"), false);
                var m = new OutOfProcessIndexer { ExePath = noX64 };
                msg = null;
                try { m.Run(new IndexRunRequest { SlnPath = sln, DbPath = Path.Combine(root, "nox64.db") }); }
                catch (IndexerProcessException ex) { msg = ex.Message; }
                catch (Exception ex) { msg = "WRONG TYPE " + ex.GetType().Name + ": " + ex.Message; }
                Check(msg != null && msg.Contains("the x64 SQLite interop is missing: " + Path.Combine(root, "addin-no-x64", "x64", "SQLite.Interop.dll")) && msg.Contains("exit code 1"),
                    "F3", "a missing x64 interop is named: " + FirstLine(msg));

                // No exe at all: in-process, with the warning on the progress channel and in FallbackWarning.
                var g = new OutOfProcessIndexer { ExePath = Path.Combine(root, "missing", "clarion-indexer.exe") };
                var gm = new List<string>();
                g.OnProgress += x => gm.Add(x);
                IndexResult gr = null;
                try { gr = g.Run(new IndexRunRequest { SlnPath = sln, DbPath = Path.Combine(root, "fallback.db"), Red = red, BuildConfiguration = config, LibraryPaths = libs }); }
                catch (Exception ex) { Check(false, "F4", "the fallback threw: " + ex.Message); }
                Check(gr != null && g.RanInProcess && g.FallbackWarning != null && g.FallbackWarning.Contains("clarion-indexer.exe was not found")
                      && gm.Count > 0 && gm[0] == g.FallbackWarning && gr.SymbolCount == inRes.SymbolCount,
                    "F4", "a missing exe runs in-process with a warning first on the progress channel, same result");
            }
        }
        catch (Exception ex)
        {
            Check(false, "X", "unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return Finish();
    }

    static string StripDigits(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) if (!char.IsDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static string FirstLine(string s)
    {
        if (s == null) return "(no exception)";
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s.Substring(0, i);
    }

    static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? "PASS  OutOfProcessIndexer.Test (" + _assertions + " assertions)"
            : "FAIL  OutOfProcessIndexer.Test: " + Failures.Count + " of " + _assertions + " failed");
        return Failures.Count == 0 ? 0 : 1;
    }
}
