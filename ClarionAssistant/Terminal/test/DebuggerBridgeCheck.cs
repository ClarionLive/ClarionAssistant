using System;
using System.Diagnostics;
using System.IO;
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
//   late       NoClarionDebugger.exe + two  the debugger addin LOADS MID-SESSION, which is the normal case
//              ClarionDebugger.dlls         in the IDE: the bridge must notice within the rescan interval,
//                                           not after it. Then a THIRD copy loads once it is bound, which
//                                           it must ignore rather than un-bind over.
//   boevoid    ClarionDebugger.exe built    the controller has the OLD void BreakOnProcEntry(string, int)
//              with /define:BOE_VOID        (e61e4f92): Break on entry must read as off, Run to Cursor as on.
//   boenone    ClarionDebugger.exe built    the controller has no BreakOnProcEntry at all: the same.
//              with /define:BOE_NONE
//   boewrongret  ClarionDebugger.exe built  the right parameters (string, int, out string) but a VOID return:
//              with /define:BOE_WRONGRET    only the bridge's return-type check can turn this one off.
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

        // e61e4f92. The frozen shape (bool, with an out message) by default; the shapes the bridge must
        // refuse under BOE_VOID, BOE_NONE and BOE_WRONGRET.
        public static int BoeCalls;
        public static string BoeFile; public static int BoeLine;
#if BOE_VOID
        public static void BreakOnProcEntry(string filePath, int line) { BoeCalls++; BoeFile = filePath; BoeLine = line; }
#elif BOE_WRONGRET
        public static void BreakOnProcEntry(string filePath, int line, out string message) { BoeCalls++; message = "set"; }
#elif !BOE_NONE
        public static bool BoeResult; public static string BoeMessage; public static bool BoeThrows;
        public static bool BreakOnProcEntry(string filePath, int line, out string message)
        {
            BoeCalls++; BoeFile = filePath; BoeLine = line;
            if (BoeThrows) throw new InvalidOperationException("debugger blew up");
            message = BoeMessage;
            return BoeResult;
        }
#endif
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
        switch (scenario)
        {
            case "none": NoDebuggerLoaded(); break;
            case "late":
                Check("harness built under a name that is NOT " + DebuggerAssemblyName + " (scenario precondition)",
                      MyName() != DebuggerAssemblyName);
                LateLoad(argv);
                break;
            default:
                Console.WriteLine("  ABORT: this build defines no controller at all — it can only run \"none\" or \"late\".");
                return 2;
        }
#else
        switch (scenario)
        {
            case "bound":
                Check("harness built as the debugger assembly (scenario precondition)", MyName() == DebuggerAssemblyName);
#if BOE_VOID || BOE_NONE || BOE_WRONGRET
                Console.WriteLine("  ABORT: \"bound\" needs the current BreakOnProcEntry; this build defines another shape.");
                return 2;
#else
                Bound();
                BreakOnEntryBound();
                break;
#endif
            case "boevoid":
            case "boenone":
            case "boewrongret":
                Check("harness built as the debugger assembly (scenario precondition)", MyName() == DebuggerAssemblyName);
                BreakOnEntryOff(scenario);
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
        bool boe;
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused, out boe);
        Check("no debugger loaded: Break on entry is off", !boe);
        string message;
        bool ok = ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnProcEntry(@"C:\src\app.clw", 12, out message);
        Check("no debugger loaded: BreakOnProcEntry returns false with a message, and does not throw", !ok && !string.IsNullOrEmpty(message));
    }

    // f022fb4e item 3. In the IDE the debugger addin almost always loads AFTER us, so what matters is how
    // long the menu stays missing. The bridge's poll backs off, so "we'll catch it on the next scan" is not
    // an answer any more — it hears the load directly (AppDomain.AssemblyLoad) and cancels the wait. This
    // runs in a build with NO controller of its own, so everything it binds to arrived at run time.
    static void LateLoad(string[] argv)
    {
        const int RescanIntervalMs = 5000;   // the bridge's FIRST backoff step; it only grows from there
        string firstPath = argv.Length > 1 ? argv[1] : null;
        string secondPath = argv.Length > 2 ? argv[2] : null;
        if (string.IsNullOrEmpty(firstPath) || string.IsNullOrEmpty(secondPath))
        {
            Console.WriteLine("  ABORT: the \"late\" scenario needs two ClarionDebugger.dll paths as argv[1] and argv[2].");
            _fail++;
            return;
        }

        bool available, paused;
        Check("nothing defines the controller yet (scenario precondition)", CandidateAssemblies() == 0);

        var sinceFirstScan = Stopwatch.StartNew();
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("before the debugger loads: not available", !available);

        Assembly late = Load(firstPath);
        Check("the debugger assembly loaded mid-session (scenario precondition)",
              late != null && CandidateAssemblies() == 1);

        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        long ms = sinceFirstScan.ElapsedMilliseconds;
        Check("an assembly that loads AFTER a failed scan is bound", available);
        // Availability is part of THIS claim too: without it the check passes by measuring a clock while
        // nothing was bound at all, which is exactly how it read against the pre-hook bridge.
        Check("...without waiting out the rescan interval (bound " + ms + "ms after the failed scan, interval "
              + RescanIntervalMs + "ms)", available && ms < RescanIntervalMs - 1000);

        SetState(late, "Paused");
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("state is read from the late-loaded controller", available && paused);
        Check("RunToCursor reaches the late-loaded controller",
              ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor() && RunToCursorCalls(late) == 1);

        // The deliberate half: a duplicate turning up after we bound is NOT evidence the binding was wrong,
        // and un-binding would make the menu vanish mid-session. Stay bound; the bridge logs it once.
        Assembly third = Load(secondPath);
        Check("a SECOND ClarionDebugger assembly loaded after the bind (scenario precondition)",
              third != null && !ReferenceEquals(third, late) && CandidateAssemblies() == 2);

        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused);
        Check("a late duplicate does not un-bind: still available", available);
        Check("...still reading the state it was reading", paused);
        Check("...and RunToCursor still reaches the FIRST controller, not the newcomer",
              ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor()
              && RunToCursorCalls(late) == 2 && RunToCursorCalls(third) == 0);
    }

    // The controller in a run-time-loaded assembly, reached the way the bridge reaches it: by reflection,
    // with no compile-time knowledge of its enum type.
    static void SetState(Assembly asm, string stateName)
    {
        try
        {
            Type t = asm.GetType(ControllerTypeName, false);
            PropertyInfo p = t.GetProperty("State", BindingFlags.Public | BindingFlags.Static);
            p.SetValue(null, Enum.Parse(p.PropertyType, stateName), null);
        }
        catch (Exception ex) { Console.WriteLine("  set-state error: " + ex.Message); }
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

#if !BOE_VOID && !BOE_NONE && !BOE_WRONGRET
    // e61e4f92: the debugger's current BreakOnProcEntry(string, int, out string) : bool, bound optionally.
    static void BreakOnEntryBound()
    {
        var C = typeof(ClarionDebugger.DebugSessionController);
        bool available, paused, boe;
        foreach (var st in new[] { ClarionDebugger.DebugControllerState.Idle, ClarionDebugger.DebugControllerState.Running,
                                   ClarionDebugger.DebugControllerState.Paused })
        {
            ClarionDebugger.DebugSessionController.State = st;
            ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused, out boe);
            Check("Break on entry is on in state " + st + " (it does not depend on the session)", available && boe);
        }

        string message;
        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Idle;
        ClarionDebugger.DebugSessionController.BoeResult = true;
        ClarionDebugger.DebugSessionController.BoeMessage = "Break on entry: MAIN  app.clw:40 (staged for the next Start)";
        bool ok = ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnProcEntry(@"C:\src\app.clw", 44, out message);
        Check("a hit: true, with the debugger's message", ok && message == ClarionDebugger.DebugSessionController.BoeMessage);
        Check("...and the file and line reached the debugger unchanged",
              ClarionDebugger.DebugSessionController.BoeCalls == 1
              && ClarionDebugger.DebugSessionController.BoeFile == @"C:\src\app.clw" && ClarionDebugger.DebugSessionController.BoeLine == 44);

        ClarionDebugger.DebugSessionController.BoeResult = false;
        ClarionDebugger.DebugSessionController.BoeMessage = "Open the CA Debugger pad first.";
        ok = ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnProcEntry(@"C:\src\app.clw", 44, out message);
        Check("a miss: false, with the debugger's reason verbatim", !ok && message == "Open the CA Debugger pad first.");

        ClarionDebugger.DebugSessionController.BoeMessage = null;
        ok = ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnProcEntry(@"C:\src\app.clw", 44, out message);
        Check("a miss with no message: false, with CA's own", !ok && message == ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnEntryNoAnswer);

        ClarionDebugger.DebugSessionController.BoeThrows = true;
        try
        {
            ok = ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnProcEntry(@"C:\src\app.clw", 44, out message);
            Check("a debugger that throws: false, \"" + ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnEntryNoAnswer + "\", and nothing escapes",
                  !ok && message == ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnEntryNoAnswer);
        }
        catch (Exception ex) { Check("a debugger that throws: nothing escapes (" + ex.GetType().Name + " did)", false); }
        ClarionDebugger.DebugSessionController.BoeThrows = false;
        Check("the required pair still works beside it", C.GetMethod("RunToCursor") != null && ClarionDebugger.DebugSessionController.RunToCursorCalls == 1);
    }
#endif

    // e61e4f92: a debugger whose BreakOnProcEntry is the old void (string, int) one, missing, or of the right
    // parameters with no bool to return. The bridge must read each as "Break on entry is off" and change
    // nothing else.
    static void BreakOnEntryOff(string scenario)
    {
        var C = typeof(ClarionDebugger.DebugSessionController);
        var flags = BindingFlags.Public | BindingFlags.Static;
        bool isVoid = C.GetMethod("BreakOnProcEntry", flags, null, new[] { typeof(string), typeof(int) }, null) != null;
        var withOut = C.GetMethod("BreakOnProcEntry", flags, null, new[] { typeof(string), typeof(int), typeof(string).MakeByRefType() }, null);
        bool any = C.GetMethod("BreakOnProcEntry", flags) != null;
        if (scenario == "boevoid")
            Check("the controller has ONLY the old void (string, int) member (scenario precondition)", isVoid && any && withOut == null);
        else if (scenario == "boewrongret")
            Check("the controller has (string, int, out string) returning VOID (scenario precondition)",
                  withOut != null && withOut.ReturnType == typeof(void) && !isVoid);
        else
            Check("the controller has no BreakOnProcEntry at all (scenario precondition)", !any);

        bool available, paused, boe;
        ClarionDebugger.DebugSessionController.State = ClarionDebugger.DebugControllerState.Paused;
        ClarionAssistant.Services.ClarionDebuggerBridge.GetState(out available, out paused, out boe);
        Check("the debugger is still available and paused (the required pair is unaffected)", available && paused);
        Check("Break on entry is OFF", !boe);
        Check("Run to Cursor still reaches the controller",
              ClarionAssistant.Services.ClarionDebuggerBridge.RunToCursor() && ClarionDebugger.DebugSessionController.RunToCursorCalls == 1);
        string message;
        bool ok = ClarionAssistant.Services.ClarionDebuggerBridge.BreakOnProcEntry(@"C:\src\app.clw", 44, out message);
        Check("BreakOnProcEntry returns false with a message", !ok && !string.IsNullOrEmpty(message));
        Check("...and the unusable member was never invoked", ClarionDebugger.DebugSessionController.BoeCalls == 0);
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

        Assembly dup = Load(duplicatePath);

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
              ClarionDebugger.DebugSessionController.RunToCursorCalls == 0 && RunToCursorCalls(dup) == 0);
    }

#endif

    // Load a fixture assembly from BYTES, not with LoadFrom: on .NET Framework the LoadFrom context hands
    // back an assembly with the same simple name that is already loaded, so a second ClarionDebugger.dll
    // would silently BE the first one again — the scenario would test nothing and still look green (it did,
    // until the preconditions below caught it). A byte-loaded assembly is always a distinct instance.
    static Assembly Load(string path)
    {
        try { return Assembly.Load(File.ReadAllBytes(path)); }
        catch (Exception ex) { Console.WriteLine("  load error: " + ex.Message); return null; }
    }

    // How many times the controller in a RUN-TIME-LOADED assembly was invoked. -1 means "could not ask",
    // which no check may read as 0.
    static int RunToCursorCalls(Assembly asm)
    {
        try
        {
            Type t = asm == null ? null : asm.GetType(ControllerTypeName, false);
            FieldInfo f = t == null ? null : t.GetField("RunToCursorCalls", BindingFlags.Public | BindingFlags.Static);
            return f == null ? -1 : (int)f.GetValue(null);
        }
        catch { return -1; }
    }
}
