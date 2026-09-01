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
    /// Of the 57, ELEVEN currently need an IWorkspaceContext that this host does not yet supply
    /// (the CodeGraph and solution tools: index_solution, query_codegraph, build_solution and
    /// friends). They register and answer an error until that lands. The remaining 46 — docs,
    /// knowledge, LSP, schema, file and search tools — are fully functional now.
    /// </summary>
    internal static class Program
    {
        private const string ServerName = "clarion-mcp-server";
        private const string ServerVersion = "1.0.0";

        private static int Main(string[] args)
        {
            bool selfTest = false, negativeControl = false, stdioSelfTest = false;
            bool stdio = false, stdioNoise = false, help = false;

            foreach (var a in args)
            {
                switch ((a ?? "").ToLowerInvariant())
                {
                    case "--selftest": selfTest = true; break;
                    case "--selftest-negative": selfTest = true; negativeControl = true; break;
                    case "--selftest-stdio": stdioSelfTest = true; break;
                    case "--stdio": stdio = true; break;
                    case "--stdio-noise": stdio = true; stdioNoise = true; break;
                    case "--help":
                    case "-h":
                    case "/?": help = true; break;
                }
            }

            if (help) { PrintUsage(Console.Error); return 0; }
            if (selfTest) return RunSelfTest(negativeControl);
            if (stdioSelfTest) return RunStdioSelfTest();

            // DEFAULT IS TO SERVE. An MCP client launches this with no arguments and immediately
            // starts talking JSON-RPC on the pipe; printing usage and exiting 64 (the old spike
            // behaviour) would look to every client like a server that crashes on startup.
            // --stdio stays accepted so a config file can be explicit.
            if (stdio || args.Length == 0)
                return StdioTransport.Run(BuildDispatcher(), stdioNoise);

            Console.Error.WriteLine(ServerName + ": unrecognised arguments.");
            PrintUsage(Console.Error);
            return 64; // EX_USAGE
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine(ServerName + " — Clarion Assistant MCP tools, without the IDE.");
            w.WriteLine();
            w.WriteLine("  (no args)            serve MCP over stdio — what an MCP client does");
            w.WriteLine("  --stdio              same, stated explicitly");
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
            var registry = new ClarionAssistant.Services.McpToolRegistry(
                null, new ClarionAssistant.Services.ClarionClassParser());

            var dispatcher = new ClarionAssistant.Services.McpDispatcher(
                registry,
                new StandaloneUiDispatcher(),
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
