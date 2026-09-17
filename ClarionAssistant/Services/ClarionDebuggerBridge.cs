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
    ///
    /// Everything here is best-effort and never throws. A missing type or member reads as "debugger not
    /// available", which hides the menu item. Call on the IDE UI thread: RunToCursor ends in the debugger pad's
    /// WinForms/WebView2 state, which it does not marshal itself.
    /// </summary>
    internal static class ClarionDebuggerBridge
    {
        private const string ControllerTypeName = "ClarionDebugger.DebugSessionController";

        private static PropertyInfo _state;
        private static MethodInfo _runToCursor;
        private static bool _bound;
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
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t;
                    try { t = asm.GetType(ControllerTypeName, false); }
                    catch { t = null; }
                    if (t == null) continue;
                    var state = t.GetProperty("State", BindingFlags.Public | BindingFlags.Static);
                    var run = t.GetMethod("RunToCursor", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    // Both or nothing: an older debugger build without RunToCursor must read as unavailable,
                    // not as a menu item that does nothing.
                    if (state == null || run == null) continue;
                    _state = state; _runToCursor = run; _bound = true;
                    return true;
                }
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
