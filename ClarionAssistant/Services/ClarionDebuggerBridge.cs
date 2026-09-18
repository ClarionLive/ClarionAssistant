using System;
using System.Reflection;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// ClarionAssistant → CA Debugger direction of the cross-addin hook (task 2484592b). The debugger already
    /// reaches INTO us by reflection (MonacoSourceNavigator.NavigateToFileAndLine / TryGetActiveCursor /
    /// SetExecutionLine); this is the mirror image, so the Monaco editor's "Run to Cursor" can push a command
    /// into the debugger. There is no compile-time reference either way, and the debugger may not be installed.
    ///
    /// Bound contract (debugger side, assembly ClarionDebugger — frozen there):
    ///   namespace ClarionDebugger, public static class DebugSessionController
    ///     public static DebugControllerState State { get; }   // compared by name: "Paused"
    ///     public static void RunToCursor();                   // silent no-op unless Paused with a ready pad
    ///   Both are REQUIRED: either one missing reads as "debugger not available". Members added later are
    ///   optional and bind separately — see the seam in <see cref="Bind"/>.
    ///
    /// The type name alone does not identify the debugger: any assembly in the IDE's AppDomain can define
    /// ClarionDebugger.DebugSessionController. We bind only an assembly whose simple name is
    /// "ClarionDebugger", and refuse to guess when more than one of those defines the type.
    ///
    /// Everything here is best-effort and never throws. A missing type or member reads as "debugger not
    /// available", which hides the menu item. Call on the IDE UI thread: RunToCursor ends in the debugger pad's
    /// WinForms/WebView2 state, which it does not marshal itself.
    /// </summary>
    internal static class ClarionDebuggerBridge
    {
        private const string ControllerTypeName = "ClarionDebugger.DebugSessionController";
        // The assembly we contracted with, by simple name. Version/culture/key are deliberately NOT pinned:
        // the debugger ships and versions on its own schedule, and this hook only needs to know it is talking
        // to THE debugger addin rather than to some other assembly that happens to define the same type name.
        private const string ControllerAssemblyName = "ClarionDebugger";

        private static PropertyInfo _state;
        private static MethodInfo _runToCursor;
        private static bool _bound;
        // Candidate count last written to the log, so an ambiguity is reported once rather than every rescan.
        private static int _reportedCandidates = -1;
        // Not found yet: re-scan at most this often. The debugger addin may load after us, but scanning every
        // assembly on every poll tick for an addin that is not installed at all would be pure waste.
        private static DateTime _nextScanUtc = DateTime.MinValue;
        private const int RescanIntervalMs = 5000;

        private static bool Bind()
        {
            if (_bound) return true;
            if (DateTime.UtcNow < _nextScanUtc) return false;
            _nextScanUtc = DateTime.UtcNow.AddMilliseconds(RescanIntervalMs);
            try
            {
                // Step 1 — WHICH assembly. Identity first: the type name is not a credential, so only an
                // assembly actually called "ClarionDebugger" is a candidate. Two of those defining the type
                // (a stale copy still loaded beside a fresh one, a side-by-side install) is a situation we
                // cannot resolve correctly, and picking whichever loaded first is how you end up driving the
                // wrong debugger. Refuse, and say so.
                Type controller = null;
                int candidates = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName;
                    try { asmName = asm.GetName().Name; }
                    catch { continue; }
                    if (!string.Equals(asmName, ControllerAssemblyName, StringComparison.OrdinalIgnoreCase)) continue;
                    Type t;
                    try { t = asm.GetType(ControllerTypeName, false); }
                    catch { t = null; }
                    if (t == null) continue;
                    candidates++;
                    controller = t;
                }
                if (candidates != _reportedCandidates)
                {
                    _reportedCandidates = candidates;
                    if (candidates > 1)
                        try { MonacoSpikeLog.Write("ClarionDebuggerBridge: NOT bound - " + candidates + " loaded assemblies named " + ControllerAssemblyName + " define " + ControllerTypeName + "; refusing to guess"); } catch { }
                }
                if (candidates != 1) return false;

                // Step 2 — REQUIRED members. Both or nothing: an older debugger build without RunToCursor must
                // read as unavailable, not as a menu item that does nothing. (Unchanged by the identity work
                // above: the required set is exactly State + RunToCursor, as it has always been.)
                var state = controller.GetProperty("State", BindingFlags.Public | BindingFlags.Static);
                var run = controller.GetMethod("RunToCursor", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (state == null || run == null) return false;

                // Step 3 — SEAM for optional members (e61e4f92 "Break on entry" is the next one). Resolve them
                // HERE, after the required pair, and let a null one disable just that feature — never the whole
                // bridge, which would take the entire debugger context menu down with it. For example:
                //     _breakOnEntry = controller.GetMethod("BreakOnEntry", BindingFlags.Public | BindingFlags.Static,
                //                         null, new[] { typeof(string), typeof(int) }, null);   // null = feature off
                // Nothing optional is bound yet.

                _state = state; _runToCursor = run; _bound = true;
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>Is the CA Debugger loaded (with the RunToCursor entry point), and is its session paused?</summary>
        public static void GetState(out bool available, out bool paused)
        {
            available = false; paused = false;
            try
            {
                if (!Bind()) return;
                available = true;
                object s = _state.GetValue(null, null);
                paused = s != null && string.Equals(s.ToString(), "Paused", StringComparison.Ordinal);
            }
            catch { paused = false; }
        }

        /// <summary>Ask the debugger to run to the active Monaco editor's cursor. Returns false if the debugger
        /// isn't available or isn't paused (nothing was sent). UI thread only — see the class remarks.</summary>
        public static bool RunToCursor()
        {
            try
            {
                bool available, paused;
                GetState(out available, out paused);
                if (!available || !paused) return false;
                _runToCursor.Invoke(null, null);
                return true;
            }
            catch (Exception ex)
            {
                try { MonacoSpikeLog.Write("ClarionDebuggerBridge.RunToCursor error: " + (ex.InnerException ?? ex).Message); } catch { }
                return false;
            }
        }
    }
}
