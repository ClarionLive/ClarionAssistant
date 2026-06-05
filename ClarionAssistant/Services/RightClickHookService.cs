using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// PHASE 2 (ticket 4b82f1de) — production native-hook service that injects an
    /// "Open in Modern Embeditor" item into the Clarion app-tree procedure right-click popup.
    ///
    /// Root cause recap (phase-1 + phase-1.5, see docs/ModernEmbeditor-RightClickOpen.md):
    ///   - The proc tree, its popup, and the Views strip are all NATIVE Clarion UI on a NATIVE UI
    ///     thread; our addin runs on the MANAGED thread. SetWindowSubclass is thread-affine → it
    ///     silently no-ops cross-thread (F4). The ONLY install that works is a thread-specific
    ///     SetWindowsHookEx targeting the native tid: install is not thread-affine, the proc runs
    ///     ON the native thread, same-process targeting needs no DLL injection.
    ///   - The native UI thread is recreated per app-view (F5), and multiple .app tabs can yield
    ///     multiple concurrent native tids → a tid-keyed hook roster (a 1-entry roster == single pair).
    ///   - The popup is owner-drawn but a plain MF_STRING item renders + is clickable (F11); selecting
    ///     it POSTs WM_COMMAND (F6) which WH_GETMESSAGE catches.
    ///   - The discriminator is TIER 4 (correlation): GW_OWNER==0, GetParent==shared ClaWin, 3 ClaLists
    ///     exist — stateless lineage never reaches the ClaList. We arm a [ThreadStatic] flag when a
    ///     WM_CONTEXTMENU's clicked window IS the proc-tree ClaList, then inject on the very next
    ///     WM_INITMENUPOPUP.
    ///
    /// CARDINAL RULE (F4-family crash): the HOOKPROC is a managed delegate marshaled to a native
    /// function pointer. If GC collects it while a hook is installed, the next native message calls
    /// freed memory → AccessViolation → dead IDE. We hold THREE static delegates (s_cwp, s_cwpRet,
    /// s_getMsg) for the addin lifetime, reused across every tid. The roster stores HHOOK handles only.
    ///
    /// FIRST CUT: the launch is a LOGGED NO-OP — we prove the off-stack BeginInvoke round-trip onto the
    /// managed thread, but do NOT open WebView2 / ModernEmbeditorLauncher yet (phase 3). We OBSERVE the
    /// WM_COMMAND (do not rewrite it to WM_NULL): OUR_CMD is unknown to Clarion's ClaPopup wndproc, which
    /// ignores it, and a less-invasive hook is safer.
    /// </summary>
    internal static class RightClickHookService
    {
        #region P/Invoke
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr hMenu, int pos);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string item);

        // ---- MIM_BACKGROUND surface (white the popup's menu-background brush so our SYSTEM-drawn item fills
        // WHITE instead of COLOR_MENU gray; Clarion's owner-drawn items paint over it, unaffected). No native-thread GDI. ----
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO mi);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colorref);   // 0x00BBGGRR
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        // Owner-draw separator paint = FillRect(existing s_whiteMenuBrush) + DrawEdge(EDGE_ETCHED). ZERO new GDI
        // objects: FillRect reuses the one static white brush; DrawEdge is the system's own themed-separator
        // primitive (system colors, creates nothing). Nothing to balance → no leak surface beyond s_whiteMenuBrush.
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
        [DllImport("user32.dll")] private static extern bool DrawEdge(IntPtr hdc, ref RECT qrc, uint edge, uint grfFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct MENUINFO
        {
            public uint cbSize;
            public uint fMask;
            public uint dwStyle;
            public uint cyMax;
            public IntPtr hbrBack;
            public uint dwContextHelpID;
            public UIntPtr dwMenuData;
        }

        // Marshaled whole (PtrToStructure) so field alignment is bitness-correct automatically — we only touch
        // these on a confirmed OUR_SEP_ID match (rare), so the marshal cost is negligible vs the owner-draw firehose.
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEASUREITEMSTRUCT
        {
            public uint CtlType;
            public uint CtlID;
            public uint itemID;
            public uint itemWidth;
            public uint itemHeight;
            public UIntPtr itemData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DRAWITEMSTRUCT
        {
            public uint CtlType;
            public uint CtlID;
            public uint itemID;
            public uint itemAction;
            public uint itemState;
            public IntPtr hwndItem;
            public IntPtr hDC;
            public RECT rcItem;
            public UIntPtr itemData;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc cb, IntPtr hMod, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hHook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hHook, int code, IntPtr w, IntPtr l);

        private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
        private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        #endregion

        #region constants
        private const int WH_GETMESSAGE = 3, WH_CALLWNDPROC = 4, WH_CALLWNDPROCRET = 12;
        private const int PM_REMOVE = 1;
        private const uint WM_INITMENUPOPUP = 0x0117, WM_COMMAND = 0x0111, WM_CONTEXTMENU = 0x007B;
        private const uint MF_STRING = 0x0000;
        private const uint MF_OWNERDRAW = 0x0100;  // our separator is owner-drawn so we can indent + lighten it to match native
        private const uint MF_DISABLED = 0x0002;   // separator is non-selectable / never highlights
        private const uint OUR_CMD = 0xE001;       // our menu command id (high, collision-safe vs Clarion's 1..N)
        private const uint OUR_SEP_ID = 0xE002;    // our owner-draw separator id (recognizes our row in WM_MEASUREITEM/WM_DRAWITEM + dedup)
        private const uint EMBEDITOR_CMD = 9;      // secondary confirm: the native Embeditor command (F9) — orthogonal backstop
        private const uint MIM_BACKGROUND = 0x00000002;
        private const uint WHITE = 0x00FFFFFF;     // COLORREF 0x00BBGGRR — white menu background
        // owner-draw separator wiring
        private const uint WM_MEASUREITEM = 0x002C, WM_DRAWITEM = 0x002B;
        private const uint ODT_MENU = 1;           // DRAWITEMSTRUCT/MEASUREITEMSTRUCT CtlType for a menu item
        private const uint EDGE_ETCHED = 0x0006, BF_TOP = 0x0002;  // BF_BOTTOM=0x0008 if BF_TOP reads off
        private const int SEP_HEIGHT = 7;          // measured separator row height (eyeball — native ~6-8px)
        private const int SEP_TEXT_INDENT = 26;    // push the etched line in to the text column (matches the MF_STRING caption indent); eyeball-tunable
        #endregion

        // White background brush for the injected popup (MIM_BACKGROUND). Created ONCE, reused on every popup,
        // DeleteObject'd ONLY at Terminate — the menu holds this brush ref for as long as it is shown, so a
        // per-popup create/delete would free a brush still in use. GDI HBRUSH is process-wide → cross-thread safe.
        private static IntPtr s_whiteMenuBrush;

        // ---- GC-rooted, addin-lifetime delegates. NEVER per-install lambdas. Cardinal rule. ----
        // Three hooks per tid: PRE-CALLWNDPROC arms, CALLWNDPROCRET appends, GETMESSAGE catches the command.
        private static readonly HookProc s_cwp = CwpProc;        // WH_CALLWNDPROC  (PRE)  — ARM
        private static readonly HookProc s_cwpRet = CwpRetProc;  // WH_CALLWNDPROCRET (POST) — APPEND
        private static readonly HookProc s_getMsg = GetMsgProc;  // WH_GETMESSAGE          — COMMAND

        // ---- roster: tid -> (HHOOK cwp, HHOOK cwpRet, HHOOK getMsg). Handles only — never per-tid delegates. ----
        private static readonly Dictionary<uint, (IntPtr cwp, IntPtr cwpRet, IntPtr getMsg)> _hooked
            = new Dictionary<uint, (IntPtr, IntPtr, IntPtr)>();

        private static volatile bool _shuttingDown;
        private static bool _started;
        private static bool _torndown;        // idempotency guard — whichever teardown path fires first wins
        private static Timer _timer;

        // Rooted managed-event delegates (so none is GC'd while subscribed).
        private static Delegate _solutionLoadedHandler;
        private static Delegate _solutionClosedHandler;
        private static EventHandler _activeWindowChangedHandler;
        private static EventHandler _appExitHandler;

        // TIER-4 correlation slot — per native tid, lock-free, reentrant by construction.
        [ThreadStatic] private static bool s_armNextPopup;
        // [ThreadStatic] reused class-name buffer for the hook thread (no per-call heap alloc — flag D).
        [ThreadStatic] private static StringBuilder s_classBuf;

        // Observable — Charlie/inspect_ide can read these to confirm the off-stack round-trip.
        internal static volatile int LaunchRequestCount;
        internal static string LastLaunchInfo;

        // ---- phase-3 launch serialization (managed UI thread only) ----
        // E1/C1: set synchronously at the TOP of the marshalled launch delegate, BEFORE OpenCommittedSelection's
        // first internal DoEvents pump. A 2nd click that arrives (and is dispatched re-entrantly inside the
        // first open's pump) sees this set and bails — preventing two concurrent native opens fighting over
        // Clarion's single-instance embed lock. Volatile for publication across the reentrant dispatch.
        private static volatile bool _openInProgress;
        // E2/C2: pure backstop that clears a WEDGED _openInProgress. The finally in the delegate is the PRIMARY
        // clear and always runs once OpenCommittedSelection returns (its internal pumps are all timeout-bounded).
        // Sized ABOVE the launcher's worst-case wall time (WaitForEmbedOpen is 45s for a cold ABC open) so it can
        // NEVER fire during a legit-but-slow open and clear the flag mid-open (the C2 concurrent-conflict trap).
        private static Timer _openWatchdog;
        private const int OpenWatchdogMs = 60000;   // >> worst-case healthy open (~2.6s) AND > the 45s cold ceiling

        // §7.1 one-shot re-arm after ActiveWorkbenchWindowChanged (shrinks the post-reopen miss window).
        private static Timer _reArmOneShot;
        private const int ReArmOneShotMs = 300;

        // ============================ lifecycle (managed UI thread) ============================

        /// <summary>Start the service: one immediate ArmPass + a self-healing UI-thread timer + event nudges.
        /// Idempotent; must never throw at workbench load.</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            try { ArmPass(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] initial ArmPass: " + ex.Message); }
            try { StartTimer(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] timer: " + ex.Message); }
            try { SubscribeProjectService(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] ProjectService subscribe: " + ex.Message); }
            try { SubscribeActiveWindowChanged(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] ActiveWindowChanged subscribe: " + ex.Message); }
            try { SubscribeApplicationExit(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] ApplicationExit subscribe: " + ex.Message); }
        }

        /// <summary>Teardown: latch _shuttingDown FIRST (gates append + launch), stop the timer, unhook every
        /// roster pair, clear. IDEMPOTENT — invoked from BOTH /Workspace/Terminate AND the ApplicationExit backstop
        /// (the project has a known close-hang ticket); whichever fires first wins, a double-fire is a no-op.
        /// UnhookWindowsHookEx on a stale handle is harmless.</summary>
        public static void Terminate()
        {
            if (_torndown) return;
            _torndown = true;
            _shuttingDown = true;
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; } } catch { }
            try { StopReArmOneShot(); } catch { }
            try { StopOpenWatchdog(); } catch { }
            try
            {
                foreach (var kv in _hooked)
                {
                    if (kv.Value.cwp != IntPtr.Zero) UnhookWindowsHookEx(kv.Value.cwp);
                    if (kv.Value.cwpRet != IntPtr.Zero) UnhookWindowsHookEx(kv.Value.cwpRet);
                    if (kv.Value.getMsg != IntPtr.Zero) UnhookWindowsHookEx(kv.Value.getMsg);
                }
                _hooked.Clear();
            }
            catch (Exception ex) { Debug.WriteLine("[RightClickHook] Terminate: " + ex.Message); }

            // Free the MIM_BACKGROUND brush exactly once, after all hooks are down (no popup can still hold it).
            try { if (s_whiteMenuBrush != IntPtr.Zero) { DeleteObject(s_whiteMenuBrush); s_whiteMenuBrush = IntPtr.Zero; } } catch { }
        }

        private static void StartTimer()
        {
            if (_timer != null) return;
            _timer = new Timer { Interval = 2500 };   // same UI-thread timer type as LspAutostartCommand → ArmPass lands on the managed thread
            _timer.Tick += (s, e) => { try { ArmPass(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] tick: " + ex.Message); } };
            _timer.Start();
        }

        /// <summary>Discover live native tids hosting Cla* windows, install on new ones, prune dead ones.
        /// ALWAYS on the managed UI thread (timer + event nudges). Idempotent.</summary>
        public static void ArmPass()
        {
            if (_shuttingDown) return;

            var live = DiscoverNativeTids();

            // install on new tids — all 3 hooks together; if any fails, back out the ones that took (all-or-nothing)
            foreach (var tid in live)
            {
                if (_hooked.ContainsKey(tid)) continue;
                IntPtr a = SetWindowsHookEx(WH_CALLWNDPROC, s_cwp, IntPtr.Zero, tid);
                IntPtr b = SetWindowsHookEx(WH_CALLWNDPROCRET, s_cwpRet, IntPtr.Zero, tid);
                IntPtr c = SetWindowsHookEx(WH_GETMESSAGE, s_getMsg, IntPtr.Zero, tid);
                if (a != IntPtr.Zero && b != IntPtr.Zero && c != IntPtr.Zero)
                {
                    _hooked[tid] = (a, b, c);
                }
                else
                {
                    if (a != IntPtr.Zero) UnhookWindowsHookEx(a);
                    if (b != IntPtr.Zero) UnhookWindowsHookEx(b);
                    if (c != IntPtr.Zero) UnhookWindowsHookEx(c);
                }
            }

            // prune dead tids (native thread died on app close → its hooks are already invalidated; unhook is harmless)
            if (_hooked.Count > 0)
            {
                List<uint> dead = null;
                foreach (var kv in _hooked)
                    if (!live.Contains(kv.Key)) (dead ?? (dead = new List<uint>())).Add(kv.Key);
                if (dead != null)
                    foreach (var tid in dead)
                    {
                        var p = _hooked[tid];
                        UnhookWindowsHookEx(p.cwp);
                        UnhookWindowsHookEx(p.cwpRet);
                        UnhookWindowsHookEx(p.getMsg);
                        _hooked.Remove(tid);
                    }
            }
        }

        // Enumerate ClaWin*/ClaList* under the MANAGED workbench main handle (plain WinForms — zero Clarion
        // reflection) and collect their native tids. NO visibility filter: a hidden ClaWin still yields the tid
        // (so the hook installs even when the editor is foreground at app-open and the tree is hidden).
        private static HashSet<uint> DiscoverNativeTids()
        {
            var tids = new HashSet<uint>();
            IntPtr main = GetWorkbenchMainHandle();
            if (main == IntPtr.Zero) return tids;

            var sb = new StringBuilder(64);  // one buffer per pass (every ~2.5s — not the hot path), reused across the callback
            EnumChildWindows(main, (h, _) =>
            {
                sb.Length = 0;
                GetClassName(h, sb, sb.Capacity);
                if (SbStartsWith(sb, "ClaWin") || SbStartsWith(sb, "ClaList"))
                {
                    uint t = GetWindowThreadProcessId(h, out uint pid);
                    if (t != 0) tids.Add(t);
                }
                return true;
            }, IntPtr.Zero);
            return tids;
        }

        private static IntPtr GetWorkbenchMainHandle()
        {
            try { if (WorkbenchSingleton.Workbench is Control wb && wb.IsHandleCreated) return wb.Handle; } catch { }
            return IntPtr.Zero;
        }

        // ---- event nudges into ArmPass (eager; the timer is the source of truth) ----

        private static void SubscribeProjectService()
        {
            var asm = Assembly.Load("ICSharpCode.SharpDevelop");
            var projectServiceType = asm?.GetType("ICSharpCode.SharpDevelop.Project.ProjectService");
            if (projectServiceType == null) return;

            MethodInfo handler = typeof(RightClickHookService).GetMethod("OnSolutionEvent", BindingFlags.NonPublic | BindingFlags.Static);
            if (handler == null) return;

            var loaded = projectServiceType.GetEvent("SolutionLoaded", BindingFlags.Public | BindingFlags.Static);
            if (loaded != null)
            {
                _solutionLoadedHandler = Delegate.CreateDelegate(loaded.EventHandlerType, handler);
                loaded.AddEventHandler(null, _solutionLoadedHandler);
            }

            // SolutionClosed → the ArmPass prune removes dead tids. Do NOT stop the timer (next solution self-arms).
            var closed = projectServiceType.GetEvent("SolutionClosed", BindingFlags.Public | BindingFlags.Static);
            if (closed != null)
            {
                _solutionClosedHandler = Delegate.CreateDelegate(closed.EventHandlerType, handler);
                closed.AddEventHandler(null, _solutionClosedHandler);
            }
        }

        // Binds to EventHandler<SolutionEventArgs>(object, EventArgs) regardless of the args subtype.
        private static void OnSolutionEvent(object sender, EventArgs e)
        {
            try { ArmPass(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] OnSolutionEvent: " + ex.Message); }
        }

        private static void SubscribeActiveWindowChanged()
        {
            var wb = WorkbenchSingleton.Workbench;
            if (wb == null) return;
            _activeWindowChangedHandler = (s, e) =>
            {
                // Immediate pass (may enumerate nothing if the new native windows don't exist yet) +
                // §7.1 one-shot ~300ms re-arm (after the native windows have settled) to shrink the
                // post-reopen miss window from ~2.5s (perpetual timer) to ~300ms.
                try { ArmPass(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] activeWindow: " + ex.Message); }
                try { StartReArmOneShot(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] reArmOneShot: " + ex.Message); }
            };
            wb.ActiveWorkbenchWindowChanged += _activeWindowChangedHandler;
        }

        // §7.1 — ActiveWorkbenchWindowChanged fires BEFORE the new app-view's native windows/thread exist, so
        // the immediate ArmPass enumerates nothing and the perpetual 2.5s timer backstops (a one-time
        // post-reopen miss). This one-shot re-runs ArmPass once after the windows settle. UI-thread,
        // _shuttingDown-guarded, idempotent (the roster ContainsKey guard makes a redundant pass a no-op).
        private static void StartReArmOneShot()
        {
            if (_shuttingDown) return;
            if (_reArmOneShot != null) return;   // a pass is already pending — coalesce
            _reArmOneShot = new Timer { Interval = ReArmOneShotMs };
            _reArmOneShot.Tick += (s, e) =>
            {
                StopReArmOneShot();   // one-shot: stop+dispose first so the next event can schedule a fresh pass
                try { ArmPass(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] reArm tick: " + ex.Message); }
            };
            _reArmOneShot.Start();
        }

        private static void StopReArmOneShot()
        {
            try { if (_reArmOneShot != null) { _reArmOneShot.Stop(); _reArmOneShot.Dispose(); _reArmOneShot = null; } } catch { }
        }

        // BACKSTOP for teardown: even if /Workspace/Terminate isn't dispatched, ApplicationExit guarantees
        // _shuttingDown is latched and the ArmPass timer is stopped so it can't tick during shutdown (cheap
        // insurance against contributing to the project's known Clarion close-hang). Terminate() is idempotent.
        private static void SubscribeApplicationExit()
        {
            _appExitHandler = (s, e) => { try { Terminate(); } catch (Exception ex) { Debug.WriteLine("[RightClickHook] appExit: " + ex.Message); } };
            Application.ApplicationExit += _appExitHandler;
        }

        // ============================ hook procs (run on the NATIVE thread) ============================
        // HOT PATH: fires on EVERY message on each hooked native tid. code<0 → pass through immediately; peek the
        // message id FIRST (cheap field read) and bail on non-targets; only WM_CONTEXTMENU / WM_INITMENUPOPUP do
        // any class/menu work; ALWAYS CallNextHookEx; whole body try/catch (an escaping exception corrupts the
        // native stack). NEVER sync-call the managed thread (async BeginInvoke only).

        // ARM hook (WH_CALLWNDPROC, PRE — fires BEFORE the wndproc). The ORIGINAL WM_CONTEXTMENU is delivered to
        // the clicked window FIRST, before Clarion forwards it up the chain and before any modal TrackPopupMenu, so
        // arming here is timing- and topology-independent (the append/INITMENUPOPUP always sees the arm already set).
        private static IntPtr CwpProc(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return CallNextHookEx(IntPtr.Zero, code, w, l);
            try
            {
                // CWPSTRUCT { IntPtr lParam; IntPtr wParam; uint message; IntPtr hwnd; }  (no lResult — that's RET only)
                uint m = unchecked((uint)Marshal.ReadInt32(l, IntPtr.Size * 2));   // message
                if (m == WM_CONTEXTMENU)
                {
                    // FLAG A + Eve#2 (recv==wParam gate): arm off the ORIGINAL click, identified deterministically.
                    // Clarion re-SENDs WM_CONTEXTMENU up the chain with wParam = the forwarding child (custom, not
                    // vanilla DefWindowProc — phase-1.5 trace), so ONLY at the original dispatch does the recipient
                    // hwnd equal wParam (recv==wParam==clicked window); forwarded hops have recv=parent != wParam.
                    // Single write at the origin → no last-writer-wins reasoning. FLAG C: a non-proc click's origin
                    // assigns false, so a stale arm can't ride to a later popup.
                    IntPtr clicked = Marshal.ReadIntPtr(l, IntPtr.Size);          // wParam (clicked window)
                    IntPtr recv = Marshal.ReadIntPtr(l, IntPtr.Size * 3);         // hwnd (recipient)
                    if (recv == clicked)
                        s_armNextPopup = IsProcTreeClaList(clicked);
                }
            }
            catch { /* swallow — never let an exception escape a native callback */ }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        // APPEND hook (WH_CALLWNDPROCRET, POST — fires AFTER the wndproc, so our item survives a build-in-handler).
        private static IntPtr CwpRetProc(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return CallNextHookEx(IntPtr.Zero, code, w, l);
            try
            {
                // CWPRETSTRUCT { IntPtr lResult; IntPtr lParam; IntPtr wParam; uint message; IntPtr hwnd; }
                uint m = unchecked((uint)Marshal.ReadInt32(l, IntPtr.Size * 3));   // message

                if (m == WM_INITMENUPOPUP)
                {
                    IntPtr hMenu = Marshal.ReadIntPtr(l, IntPtr.Size * 2);         // wParam = HMENU
                    IntPtr lp = Marshal.ReadIntPtr(l, IntPtr.Size);               // lParam: HIWORD != 0 => window/system menu
                    bool systemMenu = ((lp.ToInt64() >> 16) & 0xFFFF) != 0;

                    bool armed = s_armNextPopup;
                    s_armNextPopup = false;   // FLAG C: consume+clear on the very next popup, armed or not

                    if (armed && !systemMenu && !_shuttingDown
                        && MenuHasCmd(hMenu, EMBEDITOR_CMD)     // FLAG B: orthogonal confirm — popup actually has the Embeditor command
                        && !MenuHasCmd(hMenu, OUR_CMD)          // dedup (HMENU rebuilt per show, but be safe on a reused handle)
                        && !MenuHasCmd(hMenu, OUR_SEP_ID))      // ...and don't re-stack our owner-draw separator either
                    {
                        // (1) White the popup's background brush so our SYSTEM-drawn MF_STRING item fills WHITE
                        // instead of COLOR_MENU gray (the native MFT_OWNERDRAW items paint white themselves, so a
                        // plain item visibly mismatched). MIM_BACKGROUND swaps the menu's bg brush; Clarion's
                        // owner-drawn rows overpaint it (unaffected). The white brush is process-lifetime (created
                        // once, freed at Terminate) because the menu retains the ref while shown. No native-thread
                        // GDI on the paint path — strictly cheaper/safer than owner-drawing our row.
                        EnsureWhiteMenuBrush();
                        if (s_whiteMenuBrush != IntPtr.Zero)
                        {
                            var mi = new MENUINFO
                            {
                                cbSize = (uint)Marshal.SizeOf(typeof(MENUINFO)),
                                fMask = MIM_BACKGROUND,
                                hbrBack = s_whiteMenuBrush,
                            };
                            SetMenuInfo(hMenu, ref mi);
                        }

                        // (2) Visually group our injected action like the native separator-delimited groups
                        // (Properties | Window..Formulas | ... | Rename), then append the item. BOTH appends
                        // live inside the OUR_CMD/OUR_SEP_ID dedup guard so a re-fire on a reused HMENU can't
                        // stack separators. The separator is OWNER-DRAW (MF_OWNERDRAW | MF_DISABLED) so we can
                        // draw it INDENTED to the text column and LIGHTER (etched) to match Clarion's native
                        // separators — a plain MF_SEPARATOR renders full-width + solid, visibly off. MF_DISABLED
                        // makes it non-selectable and never-highlighted; OUR_SEP_ID lets WM_MEASUREITEM/WM_DRAWITEM
                        // recognize our row. The item stays MF_STRING (system-drawn); only the separator is owner-draw.
                        // Leading spaces nudge the caption toward the native owner-drawn text column (wide icon
                        // gutter our system-drawn item lacks). 7 overshot (image #4) → 5 → 6 (John: a touch right).
                        AppendMenu(hMenu, MF_OWNERDRAW | MF_DISABLED, (UIntPtr)OUR_SEP_ID, null);
                        AppendMenu(hMenu, MF_STRING, (UIntPtr)OUR_CMD, "      Open in CA Embeditor");
                    }
                }
                // ---- owner-draw separator: measure + paint, RET-hook (paint-last wins, Eve's rule) ----
                // WM_MEASUREITEM/WM_DRAWITEM fire for EVERY owner-draw item on this native tid (Clarion menus +
                // lists are a firehose). Cheap pre-filter on CtlType@0 / itemID@8 (fixed offsets, both bitnesses)
                // before any whole-struct marshal — only OUR_SEP_ID does real work; everything else passes through.
                else if (m == WM_MEASUREITEM)
                {
                    IntPtr pMis = Marshal.ReadIntPtr(l, IntPtr.Size);   // lParam -> MEASUREITEMSTRUCT
                    if (pMis != IntPtr.Zero
                        && (uint)Marshal.ReadInt32(pMis) == ODT_MENU
                        && (uint)Marshal.ReadInt32(pMis, 8) == OUR_SEP_ID)
                    {
                        var mis = (MEASUREITEMSTRUCT)Marshal.PtrToStructure(pMis, typeof(MEASUREITEMSTRUCT));
                        mis.itemWidth = 1;                     // nominal — the menu system stretches the row to menu width
                        mis.itemHeight = (uint)SEP_HEIGHT;
                        Marshal.StructureToPtr(mis, pMis, false);
                    }
                }
                else if (m == WM_DRAWITEM)
                {
                    IntPtr pDis = Marshal.ReadIntPtr(l, IntPtr.Size);   // lParam -> DRAWITEMSTRUCT
                    if (pDis != IntPtr.Zero
                        && (uint)Marshal.ReadInt32(pDis) == ODT_MENU
                        && (uint)Marshal.ReadInt32(pDis, 8) == OUR_SEP_ID)
                    {
                        var dis = (DRAWITEMSTRUCT)Marshal.PtrToStructure(pDis, typeof(DRAWITEMSTRUCT));
                        if (dis.hDC != IntPtr.Zero)
                        {
                            // (1) Owner-draw rows are NOT covered by the MIM_BACKGROUND auto-fill (the system fills
                            // only system-drawn rows), so fill our rect WHITE first with the EXISTING brush — else
                            // it shows COLOR_MENU gray. ZERO new GDI: reuse s_whiteMenuBrush.
                            EnsureWhiteMenuBrush();
                            if (s_whiteMenuBrush != IntPtr.Zero)
                            {
                                RECT bg = dis.rcItem;
                                FillRect(dis.hDC, ref bg, s_whiteMenuBrush);
                            }
                            // (2) Themed etched line — exactly how the system draws menu separators (system colors,
                            // creates NOTHING). Push .left in to the text column so it's INDENTED like native, not
                            // full-width. NEVER release dis.hDC — the menu system owns it.
                            RECT edge = dis.rcItem;
                            edge.left += SEP_TEXT_INDENT;
                            DrawEdge(dis.hDC, ref edge, EDGE_ETCHED, BF_TOP);
                        }
                    }
                }
            }
            catch { /* swallow — never let an exception escape a native callback */ }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        private static IntPtr GetMsgProc(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return CallNextHookEx(IntPtr.Zero, code, w, l);
            try
            {
                if (w.ToInt64() == PM_REMOVE)   // PM_REMOVE only, so a NOREMOVE peek doesn't double-fire
                {
                    // MSG { IntPtr hwnd; uint message; IntPtr wParam; IntPtr lParam; uint time; POINT pt; }
                    uint m = unchecked((uint)Marshal.ReadInt32(l, IntPtr.Size));   // message
                    if (m == WM_COMMAND)
                    {
                        long wp = Marshal.ReadIntPtr(l, IntPtr.Size * 2).ToInt64(); // wParam: LOWORD=cmdId, HIWORD=notify
                        if ((wp & 0xFFFF) == OUR_CMD && ((wp >> 16) & 0xFFFF) == 0)
                        {
                            if (!_shuttingDown) PostLaunchToManaged();
                            // OBSERVE-ONLY: do NOT rewrite to WM_NULL — ClaPopup ignores the unknown id.
                        }
                    }
                }
            }
            catch { /* swallow */ }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        }

        // ============================ tier-4 arm gate + menu confirm ============================

        // The arm gate (Eve's edge): 3 ClaLists exist (1 visible proc tree + 2 hidden), so "class is ClaList" alone
        // would mis-arm. The confirmed gate is ALL THREE: class starts 'ClaList' AND parent chain ClaChildClient ->
        // ClaWin AND IsWindowVisible. Reads are gated behind the WM_CONTEXTMENU id-filter (flag D), off the firehose.
        private static bool IsProcTreeClaList(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!ClassStartsWith(hwnd, "ClaList")) return false;
            IntPtr p1 = GetParent(hwnd);
            if (p1 == IntPtr.Zero || !ClassStartsWith(p1, "ClaChildClient")) return false;
            IntPtr p2 = GetParent(p1);
            if (p2 == IntPtr.Zero || !ClassStartsWith(p2, "ClaWin")) return false;
            return IsWindowVisible(hwnd);
        }

        // Lazily create the process-lifetime white menu-background brush (once). Called on the native thread from
        // the popup hook — a benign double-create race (two tids first-popping simultaneously) at worst leaks one
        // HBRUSH for the addin lifetime, so no lock; the loser's brush is simply overwritten. Freed at Terminate.
        private static void EnsureWhiteMenuBrush()
        {
            if (s_whiteMenuBrush == IntPtr.Zero)
            {
                try { s_whiteMenuBrush = CreateSolidBrush(WHITE); }
                catch (Exception ex) { Debug.WriteLine("[RightClickHook] CreateSolidBrush: " + ex.Message); }
            }
        }

        private static bool MenuHasCmd(IntPtr hMenu, uint cmd)
        {
            int n = GetMenuItemCount(hMenu);
            for (int i = 0; i < n; i++)
                if (GetMenuItemID(hMenu, i) == cmd) return true;
            return false;
        }

        // Class-name read into the [ThreadStatic] reused buffer — no managed heap alloc per call (flag D).
        private static bool ClassStartsWith(IntPtr h, string prefix)
        {
            var sb = s_classBuf ?? (s_classBuf = new StringBuilder(64));
            sb.Length = 0;
            if (GetClassName(h, sb, sb.Capacity) == 0) return false;
            return SbStartsWith(sb, prefix);
        }

        // Alloc-free, case-insensitive prefix compare over a StringBuilder (no ToString()).
        private static bool SbStartsWith(StringBuilder sb, string prefix)
        {
            if (sb.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (char.ToUpperInvariant(sb[i]) != char.ToUpperInvariant(prefix[i])) return false;
            return true;
        }

        // ============================ launch (FIRST CUT = logged no-op) ============================
        // Off-stack deferral + STA affinity in one hop: the hook proc is on the NATIVE thread; marshal async to the
        // managed UI thread. First cut just logs "would launch (committed selection)" — proves the round-trip with
        // ZERO freeze. Phase 3 swaps the body for BM_CLICK-on-committed-selection -> source-regex name ->
        // ModernEmbeditorLauncher. Per F3, right-click commits the selection, so launch needs no proc name here.
        private static void PostLaunchToManaged()
        {
            try
            {
                var wb = WorkbenchSingleton.Workbench as Control;
                if (wb == null || !wb.IsHandleCreated) return;
                wb.BeginInvoke((MethodInvoker)LaunchDelegate);
            }
            catch (Exception ex) { Debug.WriteLine("[RightClickHook] PostLaunchToManaged: " + ex.Message); }
        }

        // Runs on the managed UI thread (off the native message stack). This is the off-stack hop that BOTH
        // defers the open past the menu/message stack AND lands on the STA/WebView2 thread (§2).
        private static void LaunchDelegate()
        {
            // E5 (CRITICAL): hook-originated with NO error boundary above it — an unhandled exception on the UI
            // thread = IDE CRASH. The ENTIRE body is wrapped: catch + log + swallow; finally clears the gate.
            try
            {
                // E1/C1: check→SET the in-progress gate with NO pump between the two (effectively atomic on the
                // single UI thread precisely because nothing pumps here). OpenCommittedSelection — which pumps
                // DoEvents internally — is only reached AFTER the gate is set, so a 2nd click dispatched
                // re-entrantly inside that pump sees the gate set and bails (no two concurrent native opens).
                if (_openInProgress) { Debug.WriteLine("[RightClickHook] launch ignored — open already in progress."); return; }
                _openInProgress = true;
                StartOpenWatchdog();   // E2/C2 backstop (only fires if the open wedges before finally runs)

                // E4: re-check teardown INSIDE the delegate — the app/solution may have switched/closed in the
                // async gap between the native command and this run.
                if (_shuttingDown) return;

                LaunchRequestCount++;
                LastLaunchInfo = "launch (committed selection) #" + LaunchRequestCount;
                Debug.WriteLine("[RightClickHook] " + LastLaunchInfo);
                try { LoggingService.Info("[RightClickHook] " + LastLaunchInfo); } catch { }

                // The REAL open — reuse the freeze-fixed launcher (its off-stack ShowView fixes the WebView2
                // reentrancy freeze; E7 BM_CLICK→caption→name stays tightly sequential inside it, no awaits).
                // isDark resolved the same way OpenModernEmbeditorPickerCommand does (false).
                string err = ModernEmbeditorLauncher.OpenCommittedSelection(isDark: false);
                if (err != null)
                {
                    LastLaunchInfo = "launch #" + LaunchRequestCount + " — " + err;
                    Debug.WriteLine("[RightClickHook] " + LastLaunchInfo);
                    try { LoggingService.Info("[RightClickHook] " + LastLaunchInfo); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[RightClickHook] LaunchDelegate: " + ex.Message);
                try { LoggingService.Error("[RightClickHook] LaunchDelegate failed: " + ex.Message); } catch { }
            }
            finally
            {
                // E2: PRIMARY clear — always runs once OpenCommittedSelection returns (its internal pumps are
                // all timeout-bounded, so this delegate always completes). By here the native single-instance
                // embed lock is already released (CancelEmbeditor + WaitForEmbedClosed ran inside the launcher);
                // only the deferred WebView2 ShowView is still pending, which doesn't touch the native lock — so
                // a subsequent launch may safely proceed.
                _openInProgress = false;
                StopOpenWatchdog();
            }
        }

        private static void StartOpenWatchdog()
        {
            try
            {
                StopOpenWatchdog();
                _openWatchdog = new Timer { Interval = OpenWatchdogMs };
                _openWatchdog.Tick += (s, e) =>
                {
                    Debug.WriteLine("[RightClickHook] open watchdog fired — clearing a wedged _openInProgress.");
                    _openInProgress = false;
                    StopOpenWatchdog();
                };
                _openWatchdog.Start();
            }
            catch (Exception ex) { Debug.WriteLine("[RightClickHook] StartOpenWatchdog: " + ex.Message); }
        }

        private static void StopOpenWatchdog()
        {
            try { if (_openWatchdog != null) { _openWatchdog.Stop(); _openWatchdog.Dispose(); _openWatchdog = null; } } catch { }
        }
    }
}
