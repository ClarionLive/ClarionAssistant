using System;

// Check for Services/ClarionDebuggerBridge.cs (task 2484592b): the reflection binding into the CA Debugger's
// ClarionDebugger.DebugSessionController, compiled against the REAL bridge source with a fake controller.
//
// Run (from ClarionAssistant\, Roslyn csc from VS2022 MSBuild):
//   csc /nologo /out:%TEMP%\bridge.exe Terminal\test\DebuggerBridgeCheck.cs Services\ClarionDebuggerBridge.cs && %TEMP%\bridge.exe
//   csc /nologo /define:NO_DEBUGGER /out:%TEMP%\bridge-none.exe Terminal\test\DebuggerBridgeCheck.cs Services\ClarionDebuggerBridge.cs && %TEMP%\bridge-none.exe
// Exit code 0 = all pass.

namespace ClarionAssistant
{
    // Stub for the addin's logger (the real one lives in MonacoClarionSourceEditor.cs, which needs the IDE).
    public static class MonacoSpikeLog { public static void Write(string message) { Console.WriteLine("  log: " + message); } }
}

#if !NO_DEBUGGER
namespace ClarionDebugger
{
    // Same shape as the debugger's frozen contract: an enum State and a parameterless static RunToCursor.
    public enum DebugControllerState { Idle, Launching, Running, Paused }

    public static class DebugSessionController
    {
        public static DebugControllerState State { get; set; }
        public static int RunToCursorCalls;
        public static void RunToCursor() { RunToCursorCalls++; }
    }
}
#endif

public static class Program
{
    static int _fail;
    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
        if (!ok) _fail++;
    }

    public static int Main()
    {
        bool available, paused;
#if NO_DEBUGGER
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("no debugger loaded: not available", !available);
        Check("no debugger loaded: not paused", !paused);
        Check("no debugger loaded: RunToCursor returns false and does not throw", !ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
#else
        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Running;
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("debugger loaded: available", available);
        Check("Running: not paused", !paused);
        Check("Running: RunToCursor not sent", !ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
        Check("Running: controller not invoked", ClarionDebugger.DebugSessionController.RunToCursorCalls == 0);

        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Paused;
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("Paused: paused", available && paused);
        Check("Paused: RunToCursor sent", ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
        Check("Paused: controller invoked exactly once", ClarionDebugger.DebugSessionController.RunToCursorCalls == 1);

        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Idle;
        Check("Idle: RunToCursor not sent", !ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
        Check("Idle: controller still invoked once", ClarionDebugger.DebugSessionController.RunToCursorCalls == 1);
#endif
        Console.WriteLine(_fail == 0 ? "ALL PASS" : (_fail + " FAILED"));
        return _fail == 0 ? 0 : 1;
    }
}
