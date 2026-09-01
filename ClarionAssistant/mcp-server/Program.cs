using System;
using System.Collections.Generic;
using System.IO;

namespace ClarionAssistant.McpServer
{
    /// <summary>
    /// clarion-mcp-server — standalone host for the editor-agnostic half of Clarion Assistant's
    /// MCP tools, over stdio, with no Clarion IDE running (ticket d051fbd1).
    ///
    /// Serves 57 of the addin's 115 tools. The other 58 drive the IDE itself and are withheld by
    /// McpTool.IdeOnly — an MCP client reads the tool list as a contract, so a tool that can only
    /// throw is worse than an absent one.
    ///
    /// All 57 function. The eleven that need to know which solution they are on — the CodeGraph
    /// and solution family, index_solution and query_codegraph among them — get it from
    /// StandaloneWorkspace, resolved from --solution or the working directory. The other 46
    /// (docs, knowledge, LSP, schema, file, search) never needed one.
    /// </summary>
    internal static class Program
    {
        private const string ServerName = "clarion-mcp-server";
        private const string ServerVersion = "1.0.0";

        private static int Main(string[] args)
        {
            bool selfTest = false, negativeControl = false, stdioSelfTest = false;
            bool stdioNoise = false, help = false;
            string solution = null;
            bool unknownArg = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                switch (a.ToLowerInvariant())
                {
                    case "--selftest": selfTest = true; break;
                    case "--selftest-negative": selfTest = true; negativeControl = true; break;
                    case "--selftest-stdio": stdioSelfTest = true; break;
                    // Accepted and intentionally inert: serving stdio is the DEFAULT, so this
                    // flag exists only so an MCP client config can state it explicitly.
                    case "--stdio": break;
                    case "--stdio-noise": stdioNoise = true; break;
                    case "--help":
                    case "-h":
                    case "/?": help = true; break;
                    case "--debug":
                        // Route System.Diagnostics.Debug output to stderr.
                        //
                        // Whole subsystems here report themselves ONLY through Debug.WriteLine —
                        // LspClient's entire start sequence does, including every reason it can
                        // fail. Inside the IDE that lands in the Debug Output window; a standalone
                        // process has no such window, so those failures were unobservable and
                        // "LSP not running" was the only thing a user could ever learn.
                        //
                        // NOTE the ceiling: Debug.WriteLine is [Conditional("DEBUG")], so this
                        // flag shows nothing in a Release build. Any diagnostic that must survive
                        // shipping has to move off Debug.* — worth knowing before relying on this
                        // in the field.
                        System.Diagnostics.Debug.Listeners.Add(
                            new System.Diagnostics.TextWriterTraceListener(Console.Error));
                        System.Diagnostics.Debug.AutoFlush = true;
                        break;

                    case "--lockprobe":
                        // TEST ONLY. Claims the cross-process index lock for a database path and
                        // holds it until stdin closes, so a harness can drive TWO processes
                        // deterministically instead of racing a real index run. See RunLockProbe.
                        if (i + 1 < args.Length) return RunLockProbe(args[++i]);
                        Console.Error.WriteLine(ServerName + ": --lockprobe needs a database path.");
                        return 64;

                    case "--solution":
                        // Consumes the next argument. Checked rather than assumed: a trailing
                        // "--solution" with nothing after it would otherwise silently resolve to
                        // "no solution", and the user would be left wondering why the flag they
                        // passed did nothing.
                        if (i + 1 < args.Length) { solution = args[++i]; }
                        else
                        {
                            Console.Error.WriteLine(ServerName + ": --solution needs a path.");
                            return 64;
                        }
                        break;
                    default:
                        if (a.StartsWith("--solution=", StringComparison.OrdinalIgnoreCase))
                            solution = a.Substring("--solution=".Length);
                        else
                            unknownArg = true;
                        break;
                }
            }

            if (help) { PrintUsage(Console.Error); return 0; }
            if (selfTest) return RunSelfTest(negativeControl);
            if (stdioSelfTest) return RunStdioSelfTest();

            if (unknownArg)
            {
                Console.Error.WriteLine(ServerName + ": unrecognised arguments.");
                PrintUsage(Console.Error);
                return 64; // EX_USAGE
            }

            // DEFAULT IS TO SERVE. An MCP client launches this with no arguments and immediately
            // starts talking JSON-RPC on the pipe; printing usage and exiting 64 (the old spike
            // behaviour) would look to every client like a server that crashes on startup.
            // --stdio stays accepted so a config file can be explicit.
            var workspace = StandaloneWorkspace.Resolve(solution, Environment.CurrentDirectory);

            // stderr is the ONLY place a stdio server can explain itself — stdout is the protocol
            // stream. Without this line, "index_solution says no solution is selected" is a
            // mystery the user has no way to diagnose from the client side.
            Console.Error.WriteLine(ServerName + ": " + workspace.ResolutionNote);

            var bundle = BuildDispatcher(workspace);
            StartLspInBackground(bundle, workspace);

            return StdioTransport.Run(bundle, stdioNoise);
        }

        /// <summary>
        /// Start the language server at launch when a solution is known.
        ///
        /// WHY THIS IS NEEDED AT ALL: in the addin the LSP "auto-starts when a solution is
        /// selected", because selecting one is an event. A standalone server is handed its
        /// solution on the command line and that event never happens, so every lsp_* tool was a
        /// guaranteed miss on first use — "LSP client has never been started" — for a user who
        /// had done nothing wrong. Found by CC testing the live server.
        ///
        /// IT CALLS THE lsp_start TOOL rather than reimplementing its sequence. That path already
        /// handles the shared-ClarionLsp case, resolves server.js across three install layouts,
        /// and produces a precise diagnostic when it cannot. A second start site would drift from
        /// it, and drift in the half nobody looks at.
        ///
        /// ON A BACKGROUND THREAD, so serving is not delayed. Spawning node and handshaking costs
        /// far more than this server's ~170ms startup, and most sessions never call an lsp_* tool
        /// at all — making every client wait for a feature most will not use is the wrong trade.
        /// The race is benign: an lsp_* call landing before the server is up gets the same
        /// "not running" error it would have got anyway, and succeeds on retry.
        /// </summary>
        private static void StartLspInBackground(McpDispatcherBundle bundle, StandaloneWorkspace workspace)
        {
            if (bundle == null || workspace == null) return;
            if (string.IsNullOrEmpty(workspace.CurrentSolutionPath)) return;   // nothing to serve

            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    // The tool reports its own outcome as text; relay it to stderr, which is the
                    // only channel available (stdout is the protocol stream).
                    object result = bundle.Registry.ExecuteTool(
                        "lsp_start", new Dictionary<string, object>());
                    string text = result as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Collapse to one line: stderr here is a log, and the failure diagnostic
                        // is deliberately multi-line.
                        Console.Error.WriteLine(ServerName + ": lsp_start: "
                            + text.Replace("\r", " ").Replace("\n", " "));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ServerName + ": lsp autostart failed - " + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Name = "clarion-mcp-lsp-autostart";
            t.Start();
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine(ServerName + " — Clarion Assistant MCP tools, without the IDE.");
            w.WriteLine();
            w.WriteLine("  (no args)            serve MCP over stdio — what an MCP client does");
            w.WriteLine("  --stdio              same, stated explicitly");
            w.WriteLine("  --solution <path>    the .sln the CodeGraph/solution tools work on.");
            w.WriteLine("                       Without it, a single .sln in the working directory");
            w.WriteLine("                       is used; several means none, rather than a guess.");
            w.WriteLine("  --selftest           service layer loads, registry gates correctly");
            w.WriteLine("  --selftest-negative  prove the no-IDE-assembly guard can actually fail");
            w.WriteLine("  --selftest-stdio     drive the real read/dispatch/write loop in-process");
        }

        /// <summary>
        /// Build the tool registry and wrap it in a transport-agnostic dispatcher.
        ///
        /// Passing null for IEditorService is correct rather than lazy: the tools that need one
        /// are IdeOnly, and with AppTreeFactory / IdeProbeFactory left unset they never register.
        /// A stub returning plausible nulls would be worse — the server would advertise "get the
        /// active document" and silently answer nothing.
        /// </summary>
        private static McpDispatcherBundle BuildDispatcher()
        {
            return BuildDispatcher(null);
        }

        /// <param name="workspace">
        /// Supplies "which solution am I on" to the eleven tools that need it. Null leaves them
        /// registered but answering their own no-solution error, which is what the self-tests use
        /// — they assert the tool SET, and building a workspace would make the count depend on
        /// whatever solution happens to sit in the working directory.
        /// </param>
        private static McpDispatcherBundle BuildDispatcher(StandaloneWorkspace workspace)
        {
            var registry = new ClarionAssistant.Services.McpToolRegistry(
                null, new ClarionAssistant.Services.ClarionClassParser());

            var ui = new StandaloneUiDispatcher();

            // Services the ADDIN injects and this host previously did not. Without them
            // add_knowledge / query_knowledge / save_session_summary and the four instance
            // coordination tools registered and then answered "<x> not initialized" on every
            // call — seven tools that could only fail, which is the exact thing McpTool.IdeOnly
            // exists to prevent. The gate missed them because it scanned for IDE-service fields
            // (_editorService, _appTree, _ideProbe, _diffService) and these are neither IDE
            // services nor gated: they are plain SQLite-backed services that simply had no
            // constructor call outside the addin. Found by CC testing the live server, not by
            // the gate.
            //
            // Both use the SAME %APPDATA%\ClarionAssistant databases as the addin, deliberately.
            // Knowledge is meant to be one memory across every host — a standalone session that
            // learned something the IDE session cannot recall would be worse than no memory at
            // all. And instance coordination only means anything if both hosts register in the
            // same place: a developer in the IDE and a colleague in Sublime on one solution is
            // precisely the conflict this is for.
            //
            // Constructed defensively: each opens a database, and a locked or corrupt file must
            // degrade to "that tool reports it is unavailable" rather than take down a server
            // whose other 50-odd tools are fine.
            try
            {
                registry.SetKnowledgeService(new ClarionAssistant.Services.KnowledgeService());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ServerName + ": knowledge service unavailable - " + ex.Message);
            }

            try
            {
                registry.SetInstanceCoordination(new ClarionAssistant.Services.InstanceCoordinationService());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ServerName + ": instance coordination unavailable - " + ex.Message);
            }

            if (workspace != null)
            {
                // MUST happen before serving. The registry reads _workspace inside the handlers,
                // not at registration, so a late call would leave already-answered calls having
                // reported "no solution" while later ones succeed — the same request giving two
                // answers depending on timing.
                registry.SetWorkspace(workspace, ui);

                // LspService.EnsureRunning() reads the solution through this hook and RETURNS
                // SILENTLY when it is unset — so without this line every lsp_* tool fails with a
                // generic "not running" and no clue why. The addin sets the same hook from
                // AssistantChatControl.StartMcpServer.
                ClarionAssistant.Services.LspService.SolutionPathProvider =
                    () => workspace.CurrentSolutionPath;
            }

            var dispatcher = new ClarionAssistant.Services.McpDispatcher(
                registry,
                ui,
                null,               // no activity sink: stdout is the protocol stream
                ServerName,
                ServerVersion);

            return new McpDispatcherBundle(registry, dispatcher);
        }

        /// <summary>Registry + dispatcher, so callers that need the tool count don't rebuild.</summary>
        private sealed class McpDispatcherBundle
        {
            public readonly ClarionAssistant.Services.McpToolRegistry Registry;
            public readonly ClarionAssistant.Services.McpDispatcher Dispatcher;

            public McpDispatcherBundle(
                ClarionAssistant.Services.McpToolRegistry registry,
                ClarionAssistant.Services.McpDispatcher dispatcher)
            {
                Registry = registry;
                Dispatcher = dispatcher;
            }

            public static implicit operator ClarionAssistant.Services.McpDispatcher(McpDispatcherBundle b)
            {
                return b.Dispatcher;
            }
        }

        #region cross-process lock probe

        /// <summary>
        /// TEST ONLY (--lockprobe &lt;dbPath&gt;). Claim the cross-process index lock, report the
        /// outcome on stdout, and — if acquired — hold it until stdin closes.
        ///
        /// This exists because the property worth testing cannot be tested inside one process.
        /// The lock stops two SEPARATE PROCESSES from indexing one .codegraph.db, and the
        /// in-process dictionary would mask that: a single-process test passes whether or not the
        /// file lock works at all. Driving a real index from two servers would test it, but the
        /// run takes seconds and the overlap would be a race — a test that fails intermittently
        /// teaches people to re-run it.
        ///
        /// Holding until stdin closes also makes the KILL case testable, which is the one that
        /// matters most: the harness kills this process and requires the next claim to succeed,
        /// proving a hard kill leaves no stale lock behind.
        ///
        /// Output is one line, deliberately parseable:
        ///     ACQUIRED pid=&lt;n&gt;
        ///     BLOCKED &lt;holder description&gt;
        /// </summary>
        private static int RunLockProbe(string dbPath)
        {
            string holder;
            if (!ClarionAssistant.Services.IndexRunGate.TryEnter(dbPath, out holder))
            {
                Console.WriteLine("BLOCKED " + (holder ?? "(no holder reported)"));
                return 0;
            }

            try
            {
                int pid;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
                catch { pid = -1; }
                Console.WriteLine("ACQUIRED pid=" + pid);
                Console.Out.Flush();

                // Block until the harness closes stdin (or kills us).
                Console.In.ReadLine();
                return 0;
            }
            finally
            {
                // Skipped entirely on a kill — which is the point of the kill test.
                ClarionAssistant.Services.IndexRunGate.Exit(dbPath);
            }
        }

        #endregion

        #region stdio self-test

        /// <summary>
        /// Drive the REAL transport loop over an in-memory pipe. Covers the properties that make
        /// stdio work or not: one JSON object per line, a response for every request, NO response
        /// for a notification, and a parse error that is itself a valid frame.
        ///
        /// What this deliberately does NOT cover is the stdout hijack and the UTF-8 encoding,
        /// both of which are properties of owning the process's real console. Those are proven by
        /// driving the built .exe as a subprocess with --stdio-noise.
        /// </summary>
        private static int RunStdioSelfTest()
        {
            var failures = new List<string>();
            var bundle = BuildDispatcher();
            int toolCount = bundle.Registry.GetToolCount();

            var input = string.Join("\n", new[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",   // no id ⇒ no reply
                "",                                                                  // blank ⇒ skipped
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
                "not json at all",                                                   // ⇒ -32700 frame
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\",\"params\":{}}"
            }) + "\n";

            var output = new StringWriter();
            StdioTransport.RunOn(bundle.Dispatcher, new StringReader(input), output);

            var lines = output.ToString().Split('\n');
            var frames = new List<string>();
            foreach (var l in lines) if (l.Length > 0) frames.Add(l);

            // Four requests carried an id; the notification and the blank line must produce
            // nothing. Getting 5 here is the classic bug: replying to a notification.
            if (frames.Count != 4)
                failures.Add("expected 4 frames (3 requests + 1 parse error), got " + frames.Count);

            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].IndexOf('\n') >= 0 || frames[i].IndexOf('\r') >= 0)
                    failures.Add("frame " + i + " contains an embedded newline — breaks framing");
                try { ClarionAssistant.Services.McpJsonRpc.Deserialize(frames[i]); }
                catch (Exception ex) { failures.Add("frame " + i + " is not valid JSON: " + ex.Message); }
            }

            if (frames.Count > 0 && frames[0].IndexOf(ServerName, StringComparison.Ordinal) < 0)
                failures.Add("initialize did not report serverInfo.name=" + ServerName
                             + " — a client cannot tell this host from the addin");

            if (frames.Count > 1)
            {
                // tools/list must advertise exactly what the registry holds. A mismatch means the
                // gate and the wire disagree, which is the whole failure this ticket avoids.
                int listed = CountOccurrences(frames[1], "\"inputSchema\"");
                if (listed != toolCount)
                    failures.Add("tools/list advertised " + listed + " tools, registry holds " + toolCount);
                Console.WriteLine("  ok  tools/list advertised " + listed + " tools");
            }

            if (frames.Count > 2 && frames[2].IndexOf("-32700", StringComparison.Ordinal) < 0)
                failures.Add("garbage input did not produce a -32700 parse error frame");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("STDIO SELFTEST FAILED (" + failures.Count + "):");
                foreach (var f in failures) Console.Error.WriteLine("  " + f);
                return 1;
            }

            Console.WriteLine("  ok  notification produced no reply; blank line skipped");
            Console.WriteLine("  ok  malformed input answered with a well-formed -32700 frame");
            Console.WriteLine("STDIO SELFTEST PASSED — " + frames.Count + " frames, all single-line JSON.");
            return 0;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        #endregion

        #region service-layer self-test

        private static int RunSelfTest(bool negativeControl)
        {
            // NEGATIVE CONTROL. The IDE-assembly scan below passes by finding NOTHING, and a check
            // that has only ever passed is indistinguishable from a check that cannot fail. This
            // flag deliberately loads System.Windows.Forms so the scan has something to catch: run
            // it and the scan MUST report a failure. If --selftest-negative ever exits 0, the guard
            // is broken and the clean run above proves nothing.
            if (negativeControl)
            {
                var forced = System.Reflection.Assembly.Load("System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                Console.WriteLine("negative control: force-loaded " + forced.GetName().Name);
            }

            var failures = new List<string>();

            // Each check names a type from a linked file. Referencing the TYPE is what forces the
            // file to have compiled; calling into it would require databases and config that a
            // compile check has no business needing.
            Check(failures, "CodeGraphDatabase", () => typeof(ClarionCodeGraph.Graph.CodeGraphDatabase));
            Check(failures, "CodeGraphQuery", () => typeof(ClarionCodeGraph.Graph.CodeGraphQuery));
            Check(failures, "CodeGraphIndexer", () => typeof(ClarionCodeGraph.Graph.CodeGraphIndexer));
            Check(failures, "ClarionParser", () => typeof(ClarionCodeGraph.Parsing.ClarionParser));
            Check(failures, "SolutionParser", () => typeof(ClarionCodeGraph.Parsing.SolutionParser));
            Check(failures, "RedFileService", () => typeof(ClarionAssistant.Services.RedFileService));
            Check(failures, "EncodingHelper", () => typeof(ClarionAssistant.Services.EncodingHelper));
            Check(failures, "ClarionClassParser", () => typeof(ClarionAssistant.Services.ClarionClassParser));
            Check(failures, "ClarionTraceService", () => typeof(ClarionAssistant.Services.ClarionTraceService));
            Check(failures, "DocGraphService", () => typeof(ClarionAssistant.Services.DocGraphService));
            Check(failures, "EverythingService", () => typeof(ClarionAssistant.Services.EverythingService));
            Check(failures, "KnowledgeService", () => typeof(ClarionAssistant.Services.KnowledgeService));
            Check(failures, "SchemaGraphService", () => typeof(ClarionAssistant.Services.SchemaGraphService));
            Check(failures, "McpDispatcher", () => typeof(ClarionAssistant.Services.McpDispatcher));

            // THE ACTUAL POINT: construct the real McpToolRegistry - all 4,500 lines of it, the
            // SAME file the addin compiles - in a process with no IDE.
            int noIdeCount = 0, withIdeCount = 0;
            try
            {
                var bundle = BuildDispatcher();
                noIdeCount = bundle.Registry.GetToolCount();
                Console.WriteLine("  ok  McpToolRegistry constructed, " + noIdeCount + " tools registered (no IDE)");
                if (noIdeCount == 0) failures.Add("McpToolRegistry registered 0 tools");
            }
            catch (Exception ex)
            {
                failures.Add("McpToolRegistry: " + ex.GetType().Name + " - " + ex.Message);
            }

            // NEGATIVE CONTROL FOR THE GATE. The count above is only meaningful if the gate also
            // STOPS firing when a host does have an editor - otherwise "57" could equally mean the
            // gate is stuck on and the addin is quietly losing tools too. Constructing with a stub
            // IEditorService must yield the FULL set.
            //
            // This stub is a TEST fixture and nothing else. It is the shape I argued against
            // shipping - plausible nulls - which is exactly why it lives here in the self-test and
            // is never handed to a real client.
            try
            {
                var withIde = new ClarionAssistant.Services.McpToolRegistry(
                    new GateProbeEditorService(), new ClarionAssistant.Services.ClarionClassParser());
                withIdeCount = withIde.GetToolCount();
                Console.WriteLine("  ok  with a stub IEditorService, " + withIdeCount + " tools registered");
                if (withIdeCount <= noIdeCount)
                    failures.Add("gate did not open with an editor present: " + withIdeCount + " <= " + noIdeCount);
            }
            catch (Exception ex)
            {
                failures.Add("gate negative control: " + ex.GetType().Name + " - " + ex.Message);
            }

            Console.WriteLine("  ok  gate withholds " + (withIdeCount - noIdeCount) + " IDE-only tools from a host with no IDE");

            // THE SPLIT IS A PARTITION, and a count alone would not show it. Once the addin serves
            // only its IDE tools and this server serves the rest, the two sets must be DISJOINT
            // (no tool offered twice under different prefixes, where the copies could disagree
            // about which solution they are looking at) and COMPLETE (no tool lost between them).
            // Both halves are checked here because this is the one process that can construct
            // either registry.
            try
            {
                var ideOnly = new ClarionAssistant.Services.McpToolRegistry(
                    new GateProbeEditorService(), new ClarionAssistant.Services.ClarionClassParser(),
                    ideToolsOnly: true);

                var agnosticNames = new List<string>();
                foreach (var t in new ClarionAssistant.Services.McpToolRegistry(
                             null, new ClarionAssistant.Services.ClarionClassParser()).GetToolDefinitions())
                    agnosticNames.Add((string)t["name"]);

                var ideNames = new List<string>();
                foreach (var t in ideOnly.GetToolDefinitions())
                    ideNames.Add((string)t["name"]);

                var overlap = ideNames.FindAll(n => agnosticNames.Contains(n));
                Console.WriteLine("  ok  split: addin " + ideNames.Count + " + standalone "
                                  + agnosticNames.Count + " = " + (ideNames.Count + agnosticNames.Count)
                                  + ", overlap " + overlap.Count);

                if (overlap.Count != 0)
                    failures.Add("split is not disjoint: " + overlap.Count + " tool(s) served by BOTH, e.g. " + overlap[0]);
                if (ideNames.Count + agnosticNames.Count != withIdeCount)
                    failures.Add("split loses tools: " + ideNames.Count + " + " + agnosticNames.Count
                                 + " != " + withIdeCount + " (the full set)");
            }
            catch (Exception ex)
            {
                failures.Add("split check: " + ex.GetType().Name + " - " + ex.Message);
            }

            // The process must NOT have dragged the IDE in.
            // A transitive reference would be invisible at compile time and fatal at run time on
            // a machine with no Clarion IDE, which is exactly the machine this is built for.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = asm.GetName().Name;
                if (n.StartsWith("ICSharpCode", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("CWBinding", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("System.Windows.Forms", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add("IDE assembly loaded: " + n);
                }
            }

            // Under the negative control the expected outcome is INVERTED: the scan must have
            // caught the assembly we deliberately loaded.
            if (negativeControl)
            {
                bool caught = failures.Exists(f => f.StartsWith("IDE assembly loaded:", StringComparison.Ordinal));
                if (caught)
                {
                    Console.WriteLine("NEGATIVE CONTROL PASSED - the guard detected the forced IDE assembly, so a clean run means something.");
                    return 0;
                }
                Console.Error.WriteLine("NEGATIVE CONTROL FAILED - the guard did NOT detect a force-loaded IDE assembly.");
                Console.Error.WriteLine("The plain --selftest result is therefore worthless; fix the scan before trusting it.");
                return 1;
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("SELFTEST FAILED (" + failures.Count + "):");
                foreach (var f in failures) Console.Error.WriteLine("  " + f);
                return 1;
            }

            Console.WriteLine("SELFTEST PASSED - agnostic service layer loads with no IDE assemblies.");
            return 0;
        }

        /// <summary>
        /// TEST FIXTURE ONLY - never registered, never handed to a client.
        ///
        /// Its sole job is to make McpToolRegistry.HasIde true so the self-test can prove the
        /// IdeOnly gate OPENS as well as closes. Every member throws: if the gate were ever wrong
        /// in the other direction and something actually invoked one of these during registration,
        /// the self-test should fail loudly rather than paper over it.
        /// </summary>
        private sealed class GateProbeEditorService : ClarionAssistant.Services.IEditorService
        {
            private static T Nope<T>() { throw new InvalidOperationException("GateProbeEditorService is a test fixture; it must never be invoked."); }

            public string GetActiveDocumentContent() { return Nope<string>(); }
            public string GetActiveDocumentPath() { return Nope<string>(); }
            public string GetSelectedText() { return Nope<string>(); }
            public string GetWordUnderCursor() { return Nope<string>(); }
            public int[] GetCursorPosition() { return Nope<int[]>(); }
            public int GetLineCount() { return Nope<int>(); }
            public string GetLineText(int lineNumber) { return Nope<string>(); }
            public string GetLinesRange(int startLine, int endLine) { return Nope<string>(); }
            public List<string> GetOpenFiles() { return Nope<List<string>>(); }
            public List<int[]> FindInFile(string searchText, bool caseSensitive = false) { return Nope<List<int[]>>(); }
            public bool IsModified() { return Nope<bool>(); }
            public ClarionAssistant.Services.InsertResult InsertTextAtCaret(string text) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public ClarionAssistant.Services.InsertResult ReplaceText(string oldText, string newText) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public ClarionAssistant.Services.InsertResult ReplaceRange(int a, int b, int c, int d, string newText) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public ClarionAssistant.Services.InsertResult DeleteRange(int a, int b, int c, int d) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public ClarionAssistant.Services.InsertResult SelectRange(int a, int b, int c, int d) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public ClarionAssistant.Services.InsertResult ToggleComment(int startLine, int endLine) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public ClarionAssistant.Services.InsertResult AppendTextToFile(string filePath, string text) { return Nope<ClarionAssistant.Services.InsertResult>(); }
            public bool Undo() { return Nope<bool>(); }
            public bool Redo() { return Nope<bool>(); }
            public bool GoToLine(int lineNumber) { return Nope<bool>(); }
            public void NavigateToFileAndLine(string filePath, int lineNumber) { Nope<bool>(); }
            public void OpenFileOnly(string filePath) { Nope<bool>(); }
            public bool SaveActiveDocument() { return Nope<bool>(); }
            public bool CloseActiveDocument() { return Nope<bool>(); }
        }

        private static void Check(List<string> failures, string label, Func<Type> get)
        {
            try
            {
                var t = get();
                if (t == null) failures.Add(label + ": type resolved to null");
                else Console.WriteLine("  ok  " + label + "  (" + t.FullName + ")");
            }
            catch (Exception ex)
            {
                failures.Add(label + ": " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        #endregion
    }
}
