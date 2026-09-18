using System.Reflection;

// A SECOND assembly named "ClarionDebugger" that also defines ClarionDebugger.DebugSessionController —
// the situation ClarionDebuggerBridge.Bind() must refuse to resolve (a stale copy still loaded beside a
// fresh one, or a side-by-side install). Built as a library named ClarionDebugger.dll and loaded at run
// time by DebuggerBridgeCheck.exe's "ambiguous" scenario; see run-debugger-host-checks.ps1.
//
// The version is deliberately NOT the harness exe's 0.0.0.0: two assemblies may share a simple name only
// if the rest of the identity differs, and Assembly.LoadFrom would otherwise hand back the already-loaded
// one instead of giving us a genuine second candidate.
[assembly: AssemblyVersion("2.0.0.0")]

namespace ClarionDebugger
{
    public enum DebugControllerState { Idle, Launching, Running, Paused }

    public static class DebugSessionController
    {
        public static DebugControllerState State { get; set; }
        public static int RunToCursorCalls;
        public static void RunToCursor() { RunToCursorCalls++; }
    }
}
