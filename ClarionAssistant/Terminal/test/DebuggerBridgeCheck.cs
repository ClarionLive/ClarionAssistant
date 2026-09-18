using System;
using System.Reflection;

// Check for Services/ClarionDebuggerBridge.cs (tasks 2484592b, f022fb4e): the reflection binding into the CA
// Debugger's ClarionDebugger.DebugSessionController, compiled against the REAL bridge source with a fake
// controller.
//
// Run:  powershell -ExecutionPolicy Bypass -File Terminal\test\run-debugger-host-checks.ps1
// That script builds the four scenarios below and runs them; exit code 0 = all pass. The scenarios exist
// as SEPARATE PROCESSES on purpose — the bridge caches its binding in static fields, so each question can
// only be asked once per process.
//
//   bound      ClarionDebugger.exe          this assembly is named ClarionDebugger and defines the
//                                           controller: the normal installed-debugger case.
//   none       NoClarionDebugger.exe        built with /define:NO_DEBUGGER, so nothing anywhere defines the
//                                           controller: the debugger-not-installed case.
//   decoy      SomeOtherAddin.exe           same source as "bound", built under a DIFFERENT assembly name:
//                                           the controller type exists, but not in ClarionDebugger.
//   ambiguous  ClarionDebugger.exe + a      TWO loaded assemblies named ClarionDebugger define the
//              second ClarionDebugger.dll   controller: the bridge must refuse rather than pick one.
//
// The scenario is passed as argv[0] and cross-checked against this assembly's own name, so a harness built
// wrong fails loudly instead of passing for the wrong reason.

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
    const string ControllerTypeName = "ClarionDebugger.DebugSessionController";
    const string DebuggerAssemblyName = "ClarionDebugger";

    static int _fail;
    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
        if (!ok) _fail++;
    }

    static string MyName() { return typeof(Program).Assembly.GetName().Name; }

    // How many LOADED assemblies named ClarionDebugger define the controller — i.e. how many candidates the
    // bridge is choosing between. Pinning this is what makes the negative scenarios honest: without it, a
    // harness that failed to build its second assembly would "prove" the ambiguity guard while testing nothing.
    static int CandidateAssemblies()
    {
        int n = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name;
            try { name = asm.GetName().Name; }
            catch { continue; }
            if (!string.Equals(name, DebuggerAssemblyName, StringComparison.OrdinalIgnoreCase)) continue;
            Type t;
            try { t = asm.GetType(ControllerTypeName, false); }
            catch { t = null; }
            if (t != null) n++;
        }
        return n;
    }

    public static int Main(string[] argv)
    {
        string scenario = argv.Length > 0 ? argv[0] : "bound";
        Console.WriteLine("scenario \"" + scenario + "\" in assembly \"" + MyName() + "\"");

#if NO_DEBUGGER
        if (scenario != "none")
        {
            Console.WriteLine("  ABORT: this build defines no controller at all — it can only run the \"none\" scenario.");
            return 2;
        }
        NoDebuggerLoaded();
#else
        switch (scenario)
        {
            case "bound":
                Check("harness built as the debugger assembly (scenario precondition)", MyName() == DebuggerAssemblyName);
                Bound();
                break;
            case "decoy":
                Check("harness built under a name that is NOT " + DebuggerAssemblyName + " (scenario precondition)",
                      MyName() != DebuggerAssemblyName);
                Decoy();
                break;
            case "ambiguous":
                Check("harness built as the debugger assembly (scenario precondition)", MyName() == DebuggerAssemblyName);
                Ambiguous(argv.Length > 1 ? argv[1] : null);
                break;
            case "none":
                Console.WriteLine("  ABORT: this build defines the controller — the \"none\" scenario needs /define:NO_DEBUGGER.");
                return 2;
            default:
                Console.WriteLine("  ABORT: unknown scenario \"" + scenario + "\".");
                return 2;
        }
#endif
        Console.WriteLine(_fail == 0 ? "ALL PASS" : (_fail + " FAILED"));
        return _fail == 0 ? 0 : 1;
    }

#if NO_DEBUGGER
    static void NoDebuggerLoaded()
    {
        bool available, paused;
        Check("no assembly defines the controller (scenario precondition)", CandidateAssemblies() == 0);
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("no debugger loaded: not available", !available);
        Check("no debugger loaded: not paused", !paused);
        Check("no debugger loaded: RunToCursor returns false and does not throw", !ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
    }
#else
    static void Bound()
    {
        bool available, paused;
        Check("exactly one candidate assembly (scenario precondition)", CandidateAssemblies() == 1);

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
    }

    // The type name is not a credential (f022fb4e item 1). This scenario is byte-for-byte the "bound" one
    // except for the assembly it was built into, so what it pins is the identity check and nothing else.
    static void Decoy()
    {
        bool available, paused;
        Check("no candidate assembly, though the type IS loaded (scenario precondition)",
              CandidateAssemblies() == 0 && Type.GetType(ControllerTypeName) != null);

        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Paused;
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("controller defined by an assembly not named " + DebuggerAssemblyName + ": not available", !available);
        Check("...and not paused, though the decoy says Paused", !paused);
        Check("...and RunToCursor returns false", !ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
        Check("...and the decoy's RunToCursor was never invoked", ClarionDebugger.DebugSessionController.RunToCursorCalls == 0);
    }

    // Two assemblies named ClarionDebugger, both defining the controller. Nothing here may call into the
    // bridge before the second one is loaded: the bridge caches its first successful bind for the life of
    // the process, so an early GetState would bind to this assembly and the ambiguity would never be seen.
    static void Ambiguous(string duplicatePath)
    {
        bool available, paused;
        if (string.IsNullOrEmpty(duplicatePath))
        {
            Console.WriteLine("  ABORT: the \"ambiguous\" scenario needs the path of the duplicate ClarionDebugger.dll as argv[1].");
            _fail++;
            return;
        }

        Assembly dup = null;
        try { dup = Assembly.LoadFrom(duplicatePath); }
        catch (Exception ex) { Console.WriteLine("  load error: " + ex.Message); }

        Check("the duplicate loaded", dup != null);
        Check("the duplicate is a SECOND assembly, not this one",
              dup != null && !ReferenceEquals(dup, typeof(Program).Assembly));
        Check("the duplicate is also named " + DebuggerAssemblyName,
              dup != null && dup.GetName().Name == DebuggerAssemblyName);
        Check("the duplicate also defines the controller",
              dup != null && dup.GetType(ControllerTypeName, false) != null);
        Check("two candidate assemblies are loaded (scenario precondition)", CandidateAssemblies() == 2);

        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Paused;
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("two candidates: not available — the bridge refuses to guess", !available);
        Check("two candidates: not paused, though both controllers exist", !paused);
        Check("two candidates: RunToCursor returns false", !ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor());
        Check("two candidates: neither controller was invoked",
              ClarionDebugger.DebugSessionController.RunToCursorCalls == 0 && DupRunToCursorCalls(dup) == 0);
    }

    static int DupRunToCursorCalls(Assembly dup)
    {
        try
        {
            Type t = dup == null ? null : dup.GetType(ControllerTypeName, false);
            FieldInfo f = t == null ? null : t.GetField("RunToCursorCalls", BindingFlags.Public | BindingFlags.Static);
            return f == null ? -1 : (int)f.GetValue(null);
        }
        catch { return -1; }
    }
#endif
}
