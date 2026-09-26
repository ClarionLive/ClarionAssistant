using System.Reflection;

// An assembly named "ClarionDebugger" that defines ClarionDebugger.DebugSessionController, built as a
// library and loaded at RUN TIME by DebuggerBridgeCheck.exe — which is the only way to test what the
// bridge does about assemblies it did not start out with. Two scenarios use it (run-debugger-host-checks.ps1):
//   "ambiguous"  a second candidate beside the harness's own controller: Bind must refuse to resolve it.
//   "late"       the debugger addin appearing mid-session, which is the normal case in the IDE; then a
//                THIRD copy (the DUP2 build) appearing after the bridge has bound, which it must ignore.
//
// The versions are deliberately distinct (this one, DUP2's, and the harness exe's 0.0.0.0): that is what a
// stale copy loaded beside a fresh one actually looks like, and it makes the two copies tellable apart in
// a log or a debugger. The harness loads them from BYTES so that they are separate instances regardless —
// see the comment on Load() in DebuggerBridgeCheck.cs for why LoadFrom cannot do this.
#if DUP2
[assembly: AssemblyVersion("3.0.0.0")]
#else
[assembly: AssemblyVersion("2.0.0.0")]
#endif

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
