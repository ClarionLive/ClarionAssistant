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
    ///   optional and bind separately — see the seam in <see cref="Bind"/>:
    ///     public static bool BreakOnProcEntry(string filePath, int line, out string message);   // e61e4f92
    ///   frozen by the debugger's PM on 2026-09-25. Absent, or of another shape (the void (string, int) one
    ///   an earlier debugger build carried), it reads as "Break on entry is off" and nothing else changes.
    ///
    /// The type name alone does not identify the debugger: any assembly in the IDE's AppDomain can define
    /// ClarionDebugger.DebugSessionController. We bind only an assembly whose simple name is
    /// "ClarionDebugger", and refuse to guess when more than one of those defines the type.
    ///
    /// THE BINDING IS DECIDED ONCE AND NEVER RE-OPENED. The debugger addin usually loads after us, so an
    /// AppDomain.AssemblyLoad handler tells us the moment it appears, and the poll behind it backs off
    /// instead of sweeping every loaded assembly forever. But once a single candidate has been resolved,
    /// a SECOND ClarionDebugger assembly loading later does not unbind us: the ambiguity rule exists to
    /// stop us CHOOSING wrongly, and a latecomer is no evidence that the choice already made was wrong.
    /// Re-opening it would make the debugger context menu — and a Run to Cursor mid-session — appear and
    /// disappear under the user. It is logged once and otherwise ignored.
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
        // OPTIONAL (e61e4f92). Null = this debugger build has no usable BreakOnProcEntry: the item stays hidden.
        private static MethodInfo _breakOnProcEntry;
        // volatile: the AssemblyLoad handler reads this on whatever thread loaded the assembly.
        private static volatile bool _bound;
        // Candidate count last written to the log, so an ambiguity is reported once rather than every rescan.
        private static int _reportedCandidates = -1;

        // ── When to look again ──────────────────────────────────────────────────────────────────────
        // The debugger addin normally loads AFTER us, so the question is how long we take to notice. The
        // answer is not a faster poll: AppDomain.AssemblyLoad tells us the moment a ClarionDebugger assembly
        // appears, and the handler raises _rescanNow so the very next tick scans — one tick (400ms, the
        // editor's debugger-state poll), not up to a whole rescan interval.
        //
        // The poll behind it is only a safety net (a handler we failed to attach; an assembly that loaded
        // before our first call), so it BACKS OFF: each fruitless scan doubles the wait up to a cap, which
        // matters because the common case is an IDE where the debugger is not installed at all and a scan
        // walks every loaded assembly. A long wait is harmless precisely because the handler resets it.
        private static volatile bool _rescanNow;
        private static bool _assemblyLoadHooked;
        private static bool _lateDuplicateLogged;
        private static DateTime _nextScanUtc = DateTime.MinValue;
        private const int InitialRescanMs = 5000;
        private const int MaxRescanMs = 60000;
        private static int _rescanMs = InitialRescanMs;

        private static bool Bind()
        {
            if (_bound) return true;
            HookAssemblyLoad();

            bool forced = _rescanNow;
            if (forced) { _rescanNow = false; _rescanMs = InitialRescanMs; }
            else if (DateTime.UtcNow < _nextScanUtc) return false;

            bool ok = false;
            try { ok = TryBind(); }
            catch { }
            if (ok) { _bound = true; return true; }

            _nextScanUtc = DateTime.UtcNow.AddMilliseconds(_rescanMs);
            _rescanMs = _rescanMs < MaxRescanMs / 2 ? _rescanMs * 2 : MaxRescanMs;
            return false;
        }

        /// <summary>Hear about a debugger addin that loads later, instead of waiting for the next scan. Attached
        /// once, on the first Bind, and never detached: after we are bound it still reports a second
        /// ClarionDebugger assembly, which is a thing a developer wants in the log even though we ignore it.</summary>
        private static void HookAssemblyLoad()
        {
            if (_assemblyLoadHooked) return;
            _assemblyLoadHooked = true;   // set first: a hook we cannot attach must not be retried every tick
            try { AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad; }
            catch (Exception ex)
            {
                try { MonacoSpikeLog.Write("ClarionDebuggerBridge: AssemblyLoad hook not attached (" + ex.Message + ") - falling back to the poll"); } catch { }
            }
        }

        // Any thread. Does NO reflection and takes no decision: it only cancels the backoff, so the scan
        // itself still happens on the UI thread where every other reflection call here happens.
        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var asm = args != null ? args.LoadedAssembly : null;
                if (asm == null) return;
                string name;
                try { name = asm.GetName().Name; }
                catch { return; }
                if (!string.Equals(name, ControllerAssemblyName, StringComparison.OrdinalIgnoreCase)) return;

                if (_bound)
                {
                    // Deliberately NOT an unbind — see the class remarks. Said once, so a log is not a
                    // running commentary on an assembly load we are choosing to ignore.
                    if (!_lateDuplicateLogged)
                    {
                        _lateDuplicateLogged = true;
                        try { MonacoSpikeLog.Write("ClarionDebuggerBridge: another assembly named " + ControllerAssemblyName + " loaded after we bound; staying bound to the one already resolved"); } catch { }
                    }
                    return;
                }
                _rescanNow = true;
            }
            catch { }
        }

        /// <summary>One scan. Answers "is there exactly one debugger to bind, and does it carry the required
        /// members" — and on yes, leaves _state/_runToCursor resolved. Bind() owns the caching and the
        /// retry policy; this owns only the decision. Throwing is Bind()'s problem, not a caller's.</summary>
        private static bool TryBind()
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

            // Step 3 — SEAM for optional members. Resolve them HERE, after the required pair, and let a null
            // one disable just that feature — never the whole bridge, which would take the entire debugger
            // context menu down with it.
            //
            // e61e4f92 "Break on entry": bound by EXACT signature AND return type. An older debugger carried a
            // void (string, int) member of the same name that answered nothing, so a bool return is what says
            // this build can tell us whether anything was set.
            MethodInfo boe;
            try
            {
                boe = controller.GetMethod("BreakOnProcEntry", BindingFlags.Public | BindingFlags.Static, null,
                          new[] { typeof(string), typeof(int), typeof(string).MakeByRefType() }, null);
                if (boe != null && boe.ReturnType != typeof(bool)) boe = null;
            }
            catch { boe = null; }

            _state = state; _runToCursor = run; _breakOnProcEntry = boe;
            return true;
    }

        /// <summary>Is the CA Debugger loaded (with the RunToCursor entry point), and is its session paused?</summary>
        public static void GetState(out bool available, out bool paused)
        {
            bool breakOnEntry;
            GetState(out available, out paused, out breakOnEntry);
        }

        /// <summary><see cref="GetState(out bool, out bool)"/>, plus whether this debugger build has the optional
        /// Break on entry member (e61e4f92). That one does not depend on the session state: the debugger
        /// accepts it in every state, idle included.</summary>
        public static void GetState(out bool available, out bool paused, out bool breakOnEntry)
        {
            available = false; paused = false; breakOnEntry = false;
            try
            {
                if (!Bind()) return;
                available = true;
                breakOnEntry = _breakOnProcEntry != null;
                object s = _state.GetValue(null, null);
                paused = s != null && string.Equals(s.ToString(), "Paused", StringComparison.Ordinal);
            }
            catch { paused = false; }
        }

        /// <summary>CA's own words for a Break on entry that got no usable answer from the debugger.</summary>
        public const string BreakOnEntryNoAnswer = "The debugger did not answer.";

        /// <summary>Ask the debugger to break on the entry of the procedure containing
        /// <paramref name="filePath"/>:<paramref name="line"/> (1-based). True when it says a breakpoint was
        /// sent or staged; false when nothing was set, with <paramref name="message"/> the reason to show the
        /// user (never empty on false). Never throws. UI thread only — see the class remarks.</summary>
        public static bool BreakOnProcEntry(string filePath, int line, out string message)
        {
            message = null;
            bool ok = false;
            try
            {
                if (!Bind() || _breakOnProcEntry == null)
                    message = "Break on entry needs a CA Debugger that supports it.";
                else
                {
                    var args = new object[] { filePath, line, null };
                    ok = (bool)_breakOnProcEntry.Invoke(null, args);
                    message = args[2] as string;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                message = BreakOnEntryNoAnswer;
                try { MonacoSpikeLog.Write("ClarionDebuggerBridge.BreakOnProcEntry error: " + (ex.InnerException ?? ex).Message); } catch { }
            }
            if (!ok && string.IsNullOrEmpty(message)) message = BreakOnEntryNoAnswer;
            return ok;
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
