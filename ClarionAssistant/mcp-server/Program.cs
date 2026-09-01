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

            // The point of the whole exercise: this process must NOT have dragged the IDE in.
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
