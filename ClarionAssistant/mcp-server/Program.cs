using System;
using System.Collections.Generic;

namespace ClarionAssistant.McpServer
{
    /// <summary>
    /// clarion-mcp-server — standalone host for the editor-agnostic half of Clarion Assistant's
    /// MCP tools, over stdio, with no Clarion IDE running (ticket d051fbd1).
    ///
    /// STATUS: SPIKE. This entry point exists to PROVE the agnostic service layer compiles and
    /// loads outside the addin. It does not serve MCP yet — hosting McpToolRegistry needs the
    /// IEditorService / IWorkspaceContext / IUiDispatcher seam first (see the ticket plan).
    ///
    /// `--selftest` touches one type from each linked service so the compiler cannot quietly
    /// drop an assembly reference, and so a regression shows up as a failed run rather than as
    /// a build that happens to succeed with the file excluded.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool selfTest = false, negativeControl = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
                if (string.Equals(a, "--selftest-negative", StringComparison.OrdinalIgnoreCase)) { selfTest = true; negativeControl = true; }
            }

            if (!selfTest)
            {
                Console.Error.WriteLine("clarion-mcp-server (spike). No MCP transport yet - see ticket d051fbd1.");
                Console.Error.WriteLine("Usage: clarion-mcp-server --selftest | --selftest-negative");
                return 64; // EX_USAGE
            }

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

            // THE ACTUAL POINT: construct the real McpToolRegistry - all 4,500 lines of it, the
            // SAME file the addin compiles - in a process with no IDE. Passing null for
            // IEditorService is correct rather than lazy: the tools that need it are IDE-only, and
            // with AppTreeFactory / IdeProbeFactory left unset they are not registered at all. A
            // stub returning plausible nulls would be worse, because the server would advertise
            // "get the active document" and silently answer nothing.
            int noIdeCount = 0, withIdeCount = 0;
            try
            {
                var registry = new ClarionAssistant.Services.McpToolRegistry(
                    null, new ClarionAssistant.Services.ClarionClassParser());
                noIdeCount = registry.GetToolCount();
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
    }
}
